using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Mainguard.Server.Auth;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Runtime;

/// <summary>The outcome of a stop: whether a session was actually removed, its adapter kind, and
/// the CLI login-state files harvested from the jail before teardown (empty when none) — the
/// client's cue to update the host OS keychain, the only durable credential store.</summary>
/// <param name="RepoHandle">The repository the session belonged to — the same opaque handle the client
/// supplied on spawn. Returned because settings are stored PER REPO and the client's harvest sweep
/// walks every agent on the daemon: without it the client could only file them under whichever repo
/// happened to be open, which is how one repository's approved-command list ends up in another's.</param>
public sealed record AgentStopResult(
    bool Stopped, string AgentKind,
    IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile> CliCredentials,
    IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxSettingsFile> CliSettings,
    string RepoHandle = "");

/// <summary>
/// May the settings a jail holds — the CLI's approved-command list — flow back OUT to the host store
/// that seeds every later agent in this repository?
///
/// <para>This is the escalation direction and it is deliberately narrower than the restore direction.
/// The settings file is agent-writable by construction (the CLI must be able to record a new
/// approval), so Mainguard cannot tell "the human answered yes" from "the agent wrote the file". What
/// it CAN tell is whether a human was in a position to answer at all:</para>
/// <list type="bullet">
///   <item>a <see cref="AgentRoles.Managed"/> worker's terminal is daemon-locked read-only (P2-14), so
///   nobody typed an approval into it — anything found there was written by the agent, and the
///   external-PR intake's untrusted worker is Managed by construction;</item>
///   <item>every other session (manual, coordinator) is a terminal the user drives, which is exactly
///   where the owner's approvals are made and therefore the only place worth harvesting.</item>
/// </list>
/// <para>Restore stays wider on purpose: a Managed worker SHOULD inherit the repo's approvals so it
/// does not stall on prompts nobody can answer. Grants flow in from a human-managed source and never
/// back out of an unattended one.</para>
/// </summary>
public static class CliSettingsHarvestPolicy
{
    /// <summary>True when a session of this role is human-attended, so its settings may be persisted.</summary>
    public static bool MayHarvest(string? role) =>
        !string.Equals(role, AgentRoles.Managed, StringComparison.Ordinal);
}

/// <summary>A spawn the daemon refuses on policy (kill switch engaged, no repo, …) — not a fault.</summary>
public sealed class AgentSpawnRefusedException : Exception
{
    public AgentSpawnRefusedException(string message) : base(message)
    {
    }
}

/// <summary>
/// The ONE spawn/stop workflow behind both entry points — <c>AgentService.SpawnAgent</c> (the
/// operator/UI path) and the coordinator's in-jail <c>mainguard-agent</c> shim (the
/// <see cref="AgentIpcServer"/> path) — so a coordinator-spawned worker takes exactly the
/// same chain as an RPC spawn: session record → IPC endpoint (spawn shim for a coordinator, plan shim
/// for a worker) → worktree + hardened jail (<see cref="SandboxAgentLauncher"/>) → CLI under a real TTY
/// (<see cref="AgentCliBinder"/>) → (managed only: terminal input lock, P2-14). Kept out of the
/// gRPC class per the P2-02 rejection trigger (no business logic in transports).
///
/// <para><b>Phase 2.</b> A coordinator's shim spawn no longer carries the worker's task into the jail: the
/// prompt is handed to <see cref="Mainguard.Agents.Agents.Orchestrator.WorkerPlanGate"/>, which withholds
/// it until the worker's own plan is approved. The worker's endpoint serves the plan-gate ops
/// (<c>brief</c> / <c>present_plan</c> / <c>revise_plan</c> / <c>await_decision</c>) and nothing else.</para>
/// </summary>
public sealed class AgentSpawnService
{
    private readonly AgentSessionStore _store;
    private readonly SandboxAgentLauncher _launcher;
    private readonly AgentCliBinder _binder;
    private readonly AgentIpcServer _ipc;
    private readonly SessionKeyCache _keys;
    private readonly TerminalLockRegistry _locks;
    private readonly KillSwitchGate _killGate;
    private readonly AdmissionController _admission;
    private readonly Mainguard.Agents.Agents.Orchestrator.CoordinatorLimits _limits;
    private readonly Mainguard.Agents.Agents.Orchestrator.MergeQueueProvisioner _mergeQueues;
    private readonly Mainguard.Agents.Agents.Orchestrator.PlanApprovalService _plans;
    private readonly Mainguard.Agents.Agents.Orchestrator.WorkerPlanGate _planGate;
    private readonly Mainguard.Agents.Agents.Orchestrator.PlanModeSwitch _planMode;
    private readonly IAuditLog _audit;
    private readonly Gateway.AgentGatewayCredentials _gatewayCredentials;
    private readonly ILogger _spawnLog;
    private readonly ILogger _coordLog;

    public AgentSpawnService(
        AgentSessionStore store,
        SandboxAgentLauncher launcher,
        AgentCliBinder binder,
        AgentIpcServer ipc,
        SessionKeyCache keys,
        TerminalLockRegistry locks,
        KillSwitchGate killGate,
        AdmissionController admission,
        Mainguard.Agents.Agents.Orchestrator.CoordinatorLimits limits,
        Mainguard.Agents.Agents.Orchestrator.MergeQueueProvisioner mergeQueues,
        Mainguard.Agents.Agents.Orchestrator.PlanApprovalService plans,
        Mainguard.Agents.Agents.Orchestrator.WorkerPlanGate planGate,
        Mainguard.Agents.Agents.Orchestrator.PlanModeSwitch planMode,
        IAuditLog audit,
        Gateway.AgentGatewayCredentials gatewayCredentials,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _launcher = launcher;
        _binder = binder;
        _ipc = ipc;
        _keys = keys;
        _locks = locks;
        _killGate = killGate;
        _admission = admission;
        _limits = limits;
        _mergeQueues = mergeQueues;
        _plans = plans;
        _planGate = planGate;
        _planMode = planMode
            ?? throw new ArgumentNullException(nameof(planMode));
        _audit = audit;
        // MG-4. Required, not optional: this is the only thing that revokes a stopped agent's gateway
        // token and drops the daemon's custody of its provider key. Registered unconditionally by
        // GatewayServiceRegistration (gateway on or off), so a container that cannot supply it is a
        // wiring regression — and it must fail at startup rather than degrade into the defect this
        // parameter exists to close, where StopAsync silently left both alive for the daemon's lifetime.
        _gatewayCredentials = gatewayCredentials ?? throw new ArgumentNullException(nameof(gatewayCredentials));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _spawnLog = loggerFactory.CreateLogger(DaemonLogCategories.Spawn);
        _coordLog = loggerFactory.CreateLogger(DaemonLogCategories.Coordinator);

        // Built EAGERLY, because the check inside is the role lock and a role lock that runs on the first
        // IPC request runs after the daemon has already told the operator it is up. A handler registered
        // for an op the contract does not list is a programming error — a coordinator capability added
        // without the contract change §3 requires — so it takes the daemon down here rather than becoming
        // a surface nobody reviewed.
        _coordinatorHandlers = BuildCoordinatorHandlers();
        _workerHandlers = BuildWorkerHandlers();
    }

    /// <summary>
    /// Spawns one agent. Throws <see cref="ArgumentException"/> on a missing kind,
    /// <see cref="AgentSpawnRefusedException"/> on a policy refusal (kill switch), and lets the
    /// launcher's typed provisioning failures propagate (the callers map them). Returns the agent id.
    /// </summary>
    /// <param name="agentId">An explicit session id, or null to mint one. Only the external-PR intake
    /// names its own (<c>pr-&lt;n&gt;</c>) — see <see cref="AgentSessionStore.Spawn"/>.</param>
    /// <param name="queueOrigin">How the merge-queue entry this spawn creates is badged. The default is
    /// <see cref="Mainguard.Agents.Agents.MergeEntryOrigin.Local"/>; an intake-driven spawn must stamp
    /// <c>External</c>, because the origin is what routes the merge back through the host's pull-request
    /// API instead of fast-forwarding the mirrored branch behind the pull request's back — and
    /// <c>EnsureEntry</c> overwrites the origin on every call, so a Local stamp here would silently undo
    /// the intake's.</param>
    /// <param name="cliSettings">The CLI's saved settings for THIS repository — the approved-command
    /// list the user built in earlier agents — restored into the jail so they are not re-prompted for
    /// everything they already allowed. Per-repo by construction: the client keys its store on the repo
    /// handle, and the daemon-side fallback cache is scoped the same way.</param>
    /// <param name="withoutHostCredentials">
    /// TRUST BOUNDARY. Set for a jail running code from outside the user's own machine (an external pull
    /// request head). A normal spawn falls back to the per-(repo, kind) memory cache for extra env and CLI
    /// login files so a coordinator-spawned worker inherits the login the user just performed; an
    /// untrusted head must inherit <b>nothing</b>, and must not seed that cache either.
    /// <para>It gates the SETTINGS the same way, and there the reasoning is stronger than for a login:
    /// a permission allowlist is a standing grant of execution, so a jail holding a pull request's code
    /// must start with an EMPTY one. Inheriting the user's approvals would hand somebody else's branch
    /// pre-approved execution — a real security regression introduced by a convenience fix.</para>
    /// </param>
    /// <param name="adoptExistingBranch">
    /// <b>Resume.</b> Start this jail on the <c>agent/&lt;id&gt;</c> branch <paramref name="agentId"/>
    /// already has, instead of creating one. Only <see cref="AgentResumeService"/> passes it, and only
    /// after establishing daemon-side that <c>(repo, agentId)</c> names a live, non-terminal merge-queue
    /// entry with no session of its own — the authorization question this flag does NOT ask, because a
    /// spawn that could name any id would let one agent adopt another agent's branch.
    /// </param>
    public async Task<string> SpawnAsync(
        string repoHandle, string agentKind, string? modelApiKey, string role, CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraEnv = null,
        IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile>? cliCredentials = null,
        string? parentAgentId = null,
        string? agentId = null,
        Mainguard.Agents.Agents.MergeEntryOrigin queueOrigin = Mainguard.Agents.Agents.MergeEntryOrigin.Local,
        bool withoutHostCredentials = false,
        string? heldTaskTitle = null,
        string? heldTaskPrompt = null,
        decimal heldBudgetUsd = 0m,
        bool adoptExistingBranch = false,
        IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxSettingsFile>? cliSettings = null)
    {
        // Custom env entries travel to the same 0400 tmpfs env-file as the model key; a malformed
        // name would corrupt it for every entry, so reject the whole spawn up front (typed →
        // InvalidArgument at the transport).
        if (extraEnv is not null)
        {
            foreach (var name in extraEnv.Keys)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$"))
                {
                    throw new ArgumentException($"'{name}' is not a valid environment variable name.");
                }
            }
        }

        // SA-1/F4: spawns are refused while the kill switch holds everything frozen — the IPC path
        // included (a frozen coordinator must not be able to fan out workers).
        if (_killGate.IsFrozen)
        {
            _spawnLog.LogWarning("spawn refused: kill switch engaged (kind={Kind})", agentKind);
            throw new AgentSpawnRefusedException(
                "Everything is frozen (kill switch engaged) — spawns are refused. Resume first.");
        }

        if (string.IsNullOrWhiteSpace(agentKind))
        {
            _spawnLog.LogWarning("spawn refused: agent_kind is required");
            throw new ArgumentException("agent_kind is required.");
        }

        // A plan-gated spawn is refused UP FRONT when its brief is not a brief — before _store.Spawn
        // mints a session, so a refusal leaves nothing behind. WorkerPlanGate.Hold enforces the same
        // rule (it is the authority; this is the same function, called early enough to fail cleanly),
        // but reaching it after the session record exists would leak that record and, worse, would put
        // the throw inside a path whose rollback is written for jail teardown rather than for a request
        // that never should have been admitted.
        if ((heldTaskTitle is not null || heldTaskPrompt is not null)
            && WorkerPlanGate.RefuseBrief(heldTaskTitle, heldTaskPrompt) is { } briefRefusal)
        {
            _spawnLog.LogWarning("spawn refused: {Reason}", briefRefusal);
            throw new ArgumentException(briefRefusal);
        }

        // Memory-only, per (repo, kind): lets a coordinator-initiated worker of the same kind IN THE
        // SAME REPO reuse the key the client last supplied (the daemon has no keystore of its own —
        // P2-01 is host-side). MG-6: scoping by repo keeps one repo's credentials out of another's
        // spawns; a miss simply yields no key rather than substituting a stranger's.
        if (!withoutHostCredentials)
        {
            _keys.Remember(repoHandle, agentKind, modelApiKey);
            _keys.RememberExtraEnv(repoHandle, extraEnv);
            _keys.RememberCliCredentials(repoHandle, agentKind, cliCredentials);
            _keys.RememberCliSettings(repoHandle, agentKind, cliSettings);
        }

        // Record the session first (its id names the worktree + container), then run the real
        // P2-06/P2-07 spawn chain. A provisioned repo takes the real-jail path; an unprovisioned
        // handle degrades to a session-only record (no fabricated jail).
        //
        // The repo goes in at SPAWN, not at AttachSandbox: it is half of the session's identity. An
        // intake-named `pr-<n>` id is unique only inside a repo, so keying by id alone made two
        // subscribed repositories with a pull request of the same number contend for one session — the
        // second was refused by name and never got a jail at all.
        var session = _store.Spawn(agentKind, role, parentAgentId, agentId, repoHandle);
        var key = session.Key;

        // Phase 2 — withhold the task BEFORE the jail exists. Holding it after the spawn returned would
        // leave a window in which the worker's own plan shim could ask for its brief and be told the
        // session was unknown; more importantly, the gate must be armed before anything inside the jail
        // can run, or "the daemon holds the task" is only true most of the time.
        var delegated = heldTaskTitle is not null || heldTaskPrompt is not null;

        // The operator's plan-mode toggle, read ONCE per spawn and then owned by this worker for its
        // whole life (WorkerPlanMode). Re-reading the switch later would let a toggle retroactively
        // authorise a worker still blocked at the gate, or strand one that had already been told to
        // start — two different lies, in opposite directions.
        //
        // A coordinator gets the same value even though it is not itself held: its operating
        // instructions describe the gate its workers will meet, and that text is rendered here.
        // Everything else — a manual-mode agent, an external-PR head — is not governed by the toggle at
        // all and keeps the Gated default, so its jail text is byte-identical to before.
        var planMode = delegated || role == AgentRoles.Coordinator
            ? _planMode.ModeForNewWorker
            : Mainguard.Agents.Agents.Orchestrator.WorkerPlanMode.Gated;

        if (delegated)
        {
            // The repo is half the key (see WorkerPlanGate._held): a held task belongs to (repo, agent id),
            // never to the bare id.
            // No `?? "Untitled task"` here any more. That fallback is what made the brief the task: with
            // the shim sending no title, `Title ?? TaskPrompt` upstream never reached the default, and
            // every worker's brief WAS its task. Both values are now required and validated above.
            // Held in BOTH modes, and that is the design rather than an oversight. "Plan mode off" is
            // not "skip the gate": an unheld worker is invisible to MayAutoVerify, carries the
            // manual-agent wording on its merge record, and has no recorded authorisation at all — the
            // shape SpawnWorkerAsync already calls "strictly worse than the defect being fixed". What
            // the mode changes is the one thing the toggle names: whether the task is withheld.
            _planGate.Hold(
                session.Id, parentAgentId ?? "", heldTaskTitle!, heldTaskPrompt!,
                heldBudgetUsd, session.RepoHash ?? string.Empty, planMode);
        }

        // Correlation: every Spawn/Egress/Terminal line for this agent shares its id — the scope
        // renders as (agentId) in the file format, so one grep follows the whole chain.
        using var scope = _spawnLog.BeginScope(session.Id);
        _spawnLog.LogInformation("spawn: session created role={Role} kind={Kind}", role, agentKind);
        // A CLI bind is expected (container start + docker-exec-under-PTY takes a few seconds). Mark it
        // NOW — before the slow launch — so an attach that races in while the agent is still "Starting"
        // waits for the bind instead of latching into echo. Cleared below if no CLI actually binds.
        _binder.MarkBindPending(key);
        string? ipcDir = null;
        try
        {
            if (role == AgentRoles.Coordinator || delegated)
            {
                // The endpoint is a container mount source, so it must exist before the jail does.
                // Best-effort: a box where the Unix socket cannot bind still gets a working agent
                // (terminal + jail), just without its in-jail tool — audited.
                //
                // A coordinator gets the spawn shim; a PLAN-GATED worker gets the plan shim (phase 2).
                // The role is fixed on the endpoint, so a worker cannot reach a spawn op and a
                // coordinator cannot reach a plan op, whatever either sends.
                //
                // Least privilege, deliberately: a Managed session the plan gate is NOT holding gets no
                // channel at all. That is every external-PR head (untrusted code from outside this
                // machine) and every manually spawned worker — neither is governed by the plan gate, and
                // handing an untrusted head a socket that queues cards in front of the human would be a
                // capability granted for no reason.
                var endpointRole = role == AgentRoles.Coordinator
                    ? Mainguard.Agents.Agents.Ipc.AgentIpcEndpointRole.Coordinator
                    : Mainguard.Agents.Agents.Ipc.AgentIpcEndpointRole.Worker;
                try
                {
                    // The instructions are RENDERED BY THE LAUNCHER and delivered here — the same string
                    // that goes on the jail's launch line moments later. This endpoint writes the file
                    // copy beside the shim; before G2 it rendered its own, from no catalog, and the two
                    // copies of one jail's briefing disagreed about what was installed.
                    ipcDir = _ipc.CreateEndpoint(
                        session.Id,
                        endpointRole == Mainguard.Agents.Agents.Ipc.AgentIpcEndpointRole.Coordinator
                            ? HandleShimRequestAsync
                            : HandleWorkerPlanRequestAsync,
                        endpointRole,
                        _launcher.InstructionsFor(endpointRole, planMode));
                    _coordLog.LogInformation(
                        "{Role} IPC endpoint created: {Dir}", endpointRole, ipcDir);
                }
                catch (Exception ex)
                {
                    _coordLog.LogWarning(ex, "{Role} IPC endpoint failed — degrading to no shim", endpointRole);
                    _audit.Append(new AuditEvent("ipc_endpoint_failed", new Dictionary<string, string>
                    {
                        ["agent_id"] = session.Id,
                        ["role"] = endpointRole.ToString(),
                        ["reason"] = ex.Message,
                    }));
                }
            }

            // The per-(repo, kind) memory cache is the fallback for a spawn with no client in the loop —
            // EXCEPT for an untrusted head, which inherits neither the user's llm_env_* entries nor any
            // harvested CLI login. Handing those to code that arrived in a pull request would put the
            // user's provider credentials inside a jail whose contents an outside author chose.
            var launchEnv = withoutHostCredentials ? null : extraEnv ?? _keys.TryGetExtraEnv(repoHandle);
            var launchCredentials = withoutHostCredentials
                ? null
                : cliCredentials ?? _keys.TryGetCliCredentials(repoHandle, agentKind);
            // Coordinator contract §8 step 1 — the role lock. A coordinator's jail carries none of the
            // repository capabilities a worker's does. This is the ONE place the role decides it, and the
            // spec builder re-asserts it fail-closed, so the two cannot drift into the shape where a
            // control looks present and is not (MG-12).
            var isCoordinator = role == AgentRoles.Coordinator;

            // THE UNTRUSTED-AGENT BOUNDARY, applied to the permission allowlist. An external pull
            // request's jail gets NO inherited grants — not the caller's, not the repo's cached ones —
            // so it starts asking about every command exactly as a first-ever agent would. Deleting
            // this ternary is the whole regression: the jail would boot pre-approved to run whatever
            // the user had ever allowed in this repository, on code an outside author chose.
            var launchSettings = withoutHostCredentials
                ? null
                : cliSettings ?? _keys.TryGetCliSettings(repoHandle, agentKind);
            // Launch progress → a state delta on THIS session, so the several minutes a first-run
            // toolchain image build takes read as progress in the client instead of as a hang. The
            // session stays in "Starting"; only the reason moves, which is exactly the update
            // AgentSessionStore.MarkState used to swallow. Reported synchronously (not through
            // System.Progress<T>, which hops to the thread pool) so the deltas reach the stream in the
            // order the launcher produced them.
            var launch = await _launcher.TryLaunchAsync(
                repoHandle, session.Id, agentKind, modelApiKey, ipcDir, ct,
                extraEnv: launchEnv,
                cliCredentials: launchCredentials,
                withoutRepositoryAccess: isCoordinator,
                progress: new InlineProgress(m => _store.MarkState(key, session.State, m)),
                adoptExistingBranch: adoptExistingBranch,
                cliSettings: launchSettings,
                // The role goes onto the jail's own labels. The session record that carries it here is
                // in-memory and dies with the daemon; the container outlives both, so the label is what
                // lets the next daemon adopt a surviving coordinator back AS a coordinator.
                agentRole: role,
                // Same value the endpoint copy above was rendered from — the two deliveries of one
                // jail's briefing disagreeing about the machine is defect G2, and a mode is exactly the
                // sort of argument one of two call sites forgets.
                planMode: planMode).ConfigureAwait(false);
            var bound = false;
            if (launch is not null)
            {
                _store.AttachSandbox(key, launch.ContainerId);

                // MG-10: a jailed agent has a worktree + agent/<id> branch in a provisioned repo, so it is
                // a merge-queue member from this moment. Registering here (as well as on CreateWorktree)
                // covers the real spawn chain, which provisions the worktree inside the launcher rather
                // than through the RepoSync RPC — a coordinator-spawned worker takes only this path.
                //
                // Phase 3: NOT for a coordinator. It has no worktree and no agent/<id> branch, so a queue
                // entry for it would name a branch that does not exist — and contract §4 denies it
                // "declaring its own work merge-ready". A coordinator with a queue row is precisely the
                // surface that denial is about, so it never gets one.
                if (!isCoordinator)
                {
                    _mergeQueues.EnsureEntry(repoHandle, session.Id, queueOrigin);
                }
                if (launch.LaunchCommand is { Count: > 0 })
                {
                    // The core P2-47→P2-03/09 wiring: the CLI starts inside the jail on a real TTY
                    // and TerminalService.Attach streams it (no more echo fallback for real agents).
                    bound = _binder.TryBind(new AgentCliLaunchSpec(
                        session.Id, repoHandle, launch.ContainerId, launch.LaunchCommand));
                }
            }

            if (!bound)
            {
                // Session-only (unprovisioned repo), a jail with no CLI, or a failed bind — no CLI will
                // bind, so release the pending-bind flag: a terminal attach should echo now, not wait.
                _binder.ClearBindPending(key);
            }

            if (role == AgentRoles.Managed)
            {
                // P2-14: a managed worker's terminal is read-only — daemon-enforced, never UI-only.
                _locks.Lock(session.Id);
            }

            _spawnLog.LogInformation("spawn complete: jailed={Jailed}", launch is not null);
            return session.Id;
        }
        catch (Exception ex)
        {
            // Leave no residue on a failed spawn: endpoint, lock, and session record all go. Previously
            // a silent rethrow — now the failure is recorded before cleanup so the outage is diagnosable.
            _spawnLog.LogError(ex, "spawn failed — tearing down session/endpoint/lock");

            // Broadcast the why BEFORE the record goes: the client keeps the last state's reason, so a
            // spawn that died without ever drawing a terminal still names its cause on the dead card.
            // A user-cancelled launch stays quiet — the client's Stop path owns that messaging.
            if (ex is not OperationCanceledException)
            {
                _store.MarkState(key, "Dead", ex.Message);
            }

            _store.Stop(key);

            // Repo-scoped, so it runs unconditionally: this spawn failed, so THIS session's attaches must
            // stop waiting — and doing so can no longer reach another repo's `pr-7`.
            _binder.ClearBindPending(key); // spawn failed → no bind coming; don't leave attaches waiting

            // The coordinator IPC endpoint and the terminal input lock are still keyed by agent id ALONE.
            // Now that two repos can hold one id, tearing them down unconditionally would sever the other
            // repo's live session — so they go only once this stop has removed the LAST holder of the id.
            if (_store.FindAll(session.Id).Count == 0)
            {
                if (ipcDir is not null)
                {
                    _ipc.CloseEndpoint(session.Id);
                }

                _locks.Unlock(session.Id);
                // A spawn that failed leaves no held task behind: the id names nothing now, and a stale
                // hold would keep counting toward the backpressure the operator is asked to act on.
                _planGate.Forget(session.Id);
            }

            throw;
        }
    }

    /// <summary>
    /// Harvests a LIVE agent's CLI login-state without stopping it.
    ///
    /// <para>The jail's <c>$HOME</c> is tmpfs and the durable store is the host OS keychain, but
    /// harvest previously ran ONLY inside <see cref="StopAsync"/>. So a daemon shutdown, VM stop, or
    /// crash never harvested at all — the tmpfs home died with the container and the user had to sign
    /// in again on every launch. This lets the client pull the current login while the agent keeps
    /// running, then persist it exactly as it does for a stop.</para>
    ///
    /// <para>Read-only: nothing is torn down, nothing is wiped, and the agent is untouched. An agent
    /// with no jail (session-only), no declared credentialPaths, or one that has not logged in yet
    /// yields an EMPTY result — which is normal and must never clobber a good keychain entry.</para>
    /// </summary>
    public async Task<AgentStopResult> HarvestCredentialsAsync(string agentId, CancellationToken ct)
    {
        var session = _store.Find(agentId);
        if (session?.ContainerId is not { Length: > 0 } containerId)
        {
            return new AgentStopResult(
                false, session?.Kind ?? string.Empty,
                Array.Empty<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile>(),
                Array.Empty<Mainguard.Agents.Agents.Sandbox.SandboxSettingsFile>(),
                session?.RepoHash ?? string.Empty);
        }

        var credentials = await _launcher.HarvestCliCredentialsAsync(
            containerId, session.Kind, ct).ConfigureAwait(false);
        var settings = await HarvestSettingsIfAttendedAsync(session, containerId, ct).ConfigureAwait(false);
        if (settings.Count > 0)
        {
            _keys.RememberCliSettings(session.RepoHash ?? string.Empty, session.Kind, settings);
        }

        // Same in-memory cache StopAsync refreshes, so a worker spawned by a coordinator later in this
        // daemon session (no client in the loop) boots with the login the user just performed (MG-6
        // scoping: per repo + kind). Only remember a NON-empty harvest — caching an empty result would
        // erase a good cached login for every later worker of this kind.
        if (credentials.Count > 0)
        {
            _keys.RememberCliCredentials(session.RepoHash ?? string.Empty, session.Kind, credentials);
        }

        _spawnLog.LogInformation(
            "harvest: agent={Agent} credentialFiles={Files} settingsFiles={Settings} (agent left running)",
            agentId, credentials.Count, settings.Count);
        return new AgentStopResult(
            false, session.Kind, credentials, settings, session.RepoHash ?? string.Empty);
    }

    /// <summary>
    /// Harvests a session's CLI settings — or refuses to, and says why. The gate is
    /// <see cref="CliSettingsHarvestPolicy"/>: only a human-attended session's approvals may flow back
    /// out to the store that seeds every later agent in the repository. A Managed worker (the
    /// external-PR intake's untrusted jail included) has a daemon-locked read-only terminal, so nothing
    /// in its settings file was approved by a person — persisting it would let an agent write its own
    /// future permissions.
    /// </summary>
    private async Task<IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxSettingsFile>>
        HarvestSettingsIfAttendedAsync(AgentSession session, string containerId, CancellationToken ct)
    {
        if (!CliSettingsHarvestPolicy.MayHarvest(session.Role))
        {
            _spawnLog.LogInformation(
                "cli settings harvest skipped: agent={Agent} role={Role} — an unattended jail's "
                + "approved-command list is agent-authored and never flows back to the host store.",
                session.Id, session.Role);
            return Array.Empty<Mainguard.Agents.Agents.Sandbox.SandboxSettingsFile>();
        }

        return await _launcher.HarvestCliSettingsAsync(containerId, session.Kind, ct).ConfigureAwait(false);
    }

    /// <summary>Stops one agent: session record, CLI PTY, IPC endpoint, input lock, jail + worktree.
    /// The jail's tmpfs $HOME dies with the teardown, so the CLI's login-state files (the adapter's
    /// declared <c>credentialPaths</c>) are harvested FIRST and handed back in the result — the
    /// client persists them into the host OS keychain (the daemon stores nothing).</summary>
    public Task<AgentStopResult> StopAsync(string agentId, CancellationToken ct)
    {
        // The daemon-global entry point (the StopAgent RPC): the operator names an agent by id alone.
        // Resolve it to ONE (repo, id) session rather than guessing — an id held by two repos stops
        // nothing and reports Stopped=false, because tearing down the wrong repo's jail is unrecoverable.
        var session = _store.Find(agentId);
        return StopAsync(session?.Key ?? new AgentSessionKey(string.Empty, agentId), ct);
    }

    /// <summary>The repo-scoped stop. Every caller that knows which repository the agent belongs to —
    /// the external-PR intake's release, the spawn chain's own rollback — comes through here, so one
    /// repo's <c>pr-&lt;n&gt;</c> can never tear down another's.</summary>
    public async Task<AgentStopResult> StopAsync(AgentSessionKey key, CancellationToken ct)
    {
        var agentId = key.AgentId;

        // Capture the session (with its container id/repo hash) BEFORE removing it, so a real jail +
        // worktree can be torn down after the record is gone.
        var session = _store.Find(key);
        var stopped = _store.Stop(key);

        // The terminal session is repo-keyed, so it is released unconditionally — and must be. Under the
        // old last-holder-of-the-id guard, stopping repo A's `pr-7` while repo B ran one released NOTHING:
        // repo A's CLI PTY, its replay ring and its pending-bind flag all leaked until repo B stopped too.
        _binder.Release(key);

        // Still-id-keyed subsystems (the SessionLeader PTY registry, the coordinator IPC endpoint, the
        // terminal input lock): released only when no session anywhere on the daemon still answers to this
        // id, so stopping repo A's `pr-7` cannot unlock, unregister or un-endpoint repo B's.
        if (_store.FindAll(agentId).Count == 0)
        {
            _binder.ReleaseLeader(agentId);
            _ipc.CloseEndpoint(agentId);
            _locks.Unlock(agentId);
            // The withheld task dies with the worker: nothing should be releasable to an id that no
            // longer names a session (and the gate must not grow for the daemon's lifetime).
            _planGate.Forget(agentId);

            // MG-4: drop this agent's gateway token AND the daemon's custody of its provider key.
            // `Revoke` had no production callers at all — the spawn path minted a credential on every
            // BYOK spawn and nothing ever released it, so a stopped agent left a LIVE token (replayable
            // by anything that had read it out of the jail) and a resident provider key for the rest of
            // the daemon's lifetime. The gateway is on by default, so that was the normal case, not an
            // edge one.
            //
            // Id-keyed, so it belongs in THIS block rather than beside the container teardown below:
            // AgentGatewayCredentials is keyed by agent id alone, so revoking while another repo's
            // session still answers to the same id would tear down a LIVE agent's confinement — its
            // next model call would arrive with an unknown token and be refused. Same reasoning, and
            // the same guard, as the leader/IPC/lock releases above.
            _gatewayCredentials.Revoke(agentId);
        }

        IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile> credentials =
            Array.Empty<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile>();
        IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxSettingsFile> settings =
            Array.Empty<Mainguard.Agents.Agents.Sandbox.SandboxSettingsFile>();
        if (stopped && session?.ContainerId is { Length: > 0 } containerId)
        {
            credentials = await _launcher.HarvestCliCredentialsAsync(
                containerId, session.Kind, ct).ConfigureAwait(false);
            // The approvals the user gave in this jail, on their way to the per-repo host store — but
            // only from a session a human could actually approve in (see the policy type).
            settings = await HarvestSettingsIfAttendedAsync(session, containerId, ct).ConfigureAwait(false);
            if (settings.Count > 0)
            {
                _keys.RememberCliSettings(session.RepoHash ?? string.Empty, session.Kind, settings);
            }

            // Keep the memory-only per (repo, kind) cache current too, so a worker of this kind spawned
            // later in THIS daemon session against THE SAME REPO (coordinator IPC — no client in the
            // loop) boots with the login the user just performed in the stopped jail. A session with no
            // repo hash caches nothing rather than leaking into a repo-less bucket (MG-6).
            _keys.RememberCliCredentials(session.RepoHash ?? string.Empty, session.Kind, credentials);
            await _launcher.TeardownAsync(session.RepoHash, agentId, containerId, ct).ConfigureAwait(false);
        }

        // MG-10 corollary: a stopped session's jail is gone, but nothing about that touches MergeQueue
        // state (Stop isn't a queue transition), so the queue's own `Changed` event — the only thing
        // that makes StreamQueue re-push — never fires here. An already-open rail keeps rendering this
        // agent's LAST-KNOWN HasLiveSandbox (true, from while it was running) until some unrelated queue
        // event elsewhere happens to force a fresh snapshot — which means the "Resume" affordance for a
        // now-genuinely-stranded entry can silently fail to appear for the rest of the session. Republish
        // (no state moves) so the rail's next paint reflects the sandbox that just went away.
        if (session?.RepoHash is { Length: > 0 } repoHashForQueue)
        {
            _mergeQueues.EnsureQueue(repoHashForQueue)?.Queue.NotifyGateChanged();
        }

        _spawnLog.LogInformation(
            "stop: agent={Agent} stopped={Stopped} credentialFiles={Files} settingsFiles={Settings}",
            agentId, stopped, credentials.Count, settings.Count);
        return new AgentStopResult(
            stopped, session?.Kind ?? string.Empty, credentials, settings, session?.RepoHash ?? string.Empty);
    }

    /// <summary>
    /// The coordinator shim's request handler. Identity is positional (only that coordinator's jail
    /// has the socket mount); the worker inherits the coordinator's repo and spawns MANAGED — same
    /// chain, locked terminal, visible in the activity bar as a subagent.
    /// </summary>
    internal async Task<AgentIpcResponse> HandleShimRequestAsync(
        AgentIpcRequest request, string coordinatorAgentId, CancellationToken ct)
    {
        _coordLog.LogInformation(
            "spawn-shim request: op={Op} kind={Kind} from coordinator={Coordinator}",
            request.Op, request.AgentKind, coordinatorAgentId);

        // Contract §3 is EXHAUSTIVE: "Anything not on this list is denied." The allow-list is consulted
        // FIRST and it is the real gate — an op is served because CoordinatorOps contains it, never
        // because some case label happened to name it.
        //
        // It used to be the other way round. Dispatch was a bare `switch` whose `default:` carried a
        // comment claiming the set was the allow-list, while CoordinatorOps was referenced by nothing but
        // a test: adding `case "read_worker_scrollback": return Ok(true);` served a fifth, unlisted
        // coordinator tool with the whole 95-test suite green. Deny-by-default was true of that switch,
        // but "and the served surface is exactly these five" was a property of control flow that no
        // reader and no test could check — which is the failure mode §5 names (MG-12: two copies of a
        // rule, one of them decorative).
        if (!_coordinatorHandlers.TryGetValue(request.Op, out var handler))
        {
            return new AgentIpcResponse(Ok: false, Error: $"unknown op '{request.Op}'");
        }

        return await handler(request, coordinatorAgentId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The coordinator's served surface, <b>as an object</b>: contract §3's op names bound to the methods
    /// that serve them, and nothing else can be dialled.
    ///
    /// <para>Checked against <see cref="AgentIpcRequest.CoordinatorOps"/> at construction, so the two
    /// cannot drift: a handler registered for an op the contract does not list <b>refuses to build</b>
    /// rather than quietly becoming a fifth coordinator tool. Adding one is therefore exactly what §3 says
    /// it is — a deliberate contract change — and it is the set, not a case label, that decides. Exposed
    /// to tests as <see cref="ServedCoordinatorOps"/>, because "the list is exhaustive" is then one
    /// assertable object rather than a claim about which case labels a reader managed to find.</para>
    /// </summary>
    private readonly ImmutableDictionary<string, Func<AgentIpcRequest, string, CancellationToken, Task<AgentIpcResponse>>>
        _coordinatorHandlers;

    private ImmutableDictionary<string, Func<AgentIpcRequest, string, CancellationToken, Task<AgentIpcResponse>>>
        BuildCoordinatorHandlers()
    {
        var table = new Dictionary<string, Func<AgentIpcRequest, string, CancellationToken, Task<AgentIpcResponse>>>(
            StringComparer.Ordinal)
        {
            [AgentIpcRequest.SpawnOp] = SpawnWorkerAsync,
            // One tool, two forms: `get_worker_status` scoped to the whole fan-out or to one worker.
            [AgentIpcRequest.ListOp] = (r, c, _) => Task.FromResult(Status(r, c)),
            [AgentIpcRequest.StatusOp] = (r, c, _) => Task.FromResult(Status(r, c)),
            [AgentIpcRequest.PromptOp] = PromptAsync,
            [AgentIpcRequest.VerifyOp] = VerifyAsync,
        };

        return LockToContract(table, AgentIpcRequest.CoordinatorOps, "coordinator");
    }

    /// <summary>
    /// The role lock itself: a handler table may serve <b>only</b> ops the contract lists, and it must
    /// serve all of them.
    ///
    /// <para>Both directions are refusals rather than silent corrections, and the asymmetry is the point.
    /// Dropping an unlisted handler quietly would restore exactly the failure this replaced — the set
    /// looking authoritative while the real surface lived somewhere else — only inverted. A missing
    /// handler is refused too: a listed op with nothing behind it answers "unknown op", which reads to a
    /// coordinator as "the contract lied" and to a reviewer as nothing at all.</para>
    /// </summary>
    private static ImmutableDictionary<string, Func<AgentIpcRequest, string, CancellationToken, Task<AgentIpcResponse>>>
        LockToContract(
            Dictionary<string, Func<AgentIpcRequest, string, CancellationToken, Task<AgentIpcResponse>>> table,
            IReadOnlySet<string> contractOps,
            string role)
    {
        var unlisted = table.Keys.Where(op => !contractOps.Contains(op)).OrderBy(op => op, StringComparer.Ordinal).ToArray();
        if (unlisted.Length > 0)
        {
            throw new InvalidOperationException(
                $"the {role} IPC surface would serve {string.Join(", ", unlisted.Select(o => $"'{o}'"))}, " +
                "which coordinator contract §3 does not list. Adding an operation is a deliberate contract " +
                "change: add it to the contract's op set (and its §3 table) or do not register a handler.");
        }

        var missing = contractOps.Where(op => !table.ContainsKey(op)).OrderBy(op => op, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"the {role} IPC surface lists {string.Join(", ", missing.Select(o => $"'{o}'"))} " +
                "but has no handler for it — a contract operation that answers \"unknown op\".");
        }

        return table.ToImmutableDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Exactly what a coordinator endpoint will serve, for the role-lock test. This is the observable form
    /// of contract §3's exhaustiveness claim: it must set-equal <see cref="AgentIpcRequest.CoordinatorOps"/>.
    /// </summary>
    internal IReadOnlySet<string> ServedCoordinatorOps =>
        _coordinatorHandlers.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The one way a refused shim spawn becomes visible: a warning naming the coordinator, and a
    /// <c>shim_spawn_refused</c> audit entry.
    ///
    /// <para><b>Why it is a named method (G3).</b> Two of the three refusal branches in
    /// <see cref="SpawnWorkerAsync"/> logged and audited; the third — a spawn carrying no agent kind at
    /// all, which is precisely the shape a shim reports when it could not parse its own arguments —
    /// answered the jail and told the daemon's operator nothing. Three copies of one report is how one of
    /// them comes to be missing, and one of them was.</para>
    /// </summary>
    private void RefuseShimSpawn(string coordinatorAgentId, string agentKind, string reason)
    {
        _coordLog.LogWarning(
            "shim spawn refused (coordinator={Coordinator}): {Reason}", coordinatorAgentId, reason);
        _audit.Append(new AuditEvent("shim_spawn_refused", new Dictionary<string, string>
        {
            ["coordinator_id"] = coordinatorAgentId,
            ["agent_kind"] = agentKind,
            ["reason"] = reason,
        }));
    }

    /// <summary>
    /// <c>spawn_worker</c> (contract §3) — start a Managed worker on the described task, under the caps.
    /// The described task itself goes to the plan gate, not into the jail.
    /// </summary>
    private async Task<AgentIpcResponse> SpawnWorkerAsync(
        AgentIpcRequest request, string coordinatorAgentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.AgentKind))
        {
            var installed = _launcher.InstalledAgentKinds;
            var noKind = "an agent kind is required (mainguard-agent spawn <agent-kind>): "
                       + Mainguard.Agents.Agents.Ipc.AgentOperatingInstructions.SpellKinds(installed);

            // G3: reported like the other two refusals below, and it was not. This is the shape a
            // shim-side refusal arrives in — the shim reports an attempt it could not build rather
            // than swallowing it (`report_refused_spawn`) — so a branch that answered silently was the
            // one place a spawn could still fail with nothing anywhere.
            RefuseShimSpawn(coordinatorAgentId, request.AgentKind ?? string.Empty, noKind);
            return new AgentIpcResponse(Ok: false, Error: noKind);
        }

        // D1: a kind that maps to no installed CLI is refused HERE, before anything is minted. Left to
        // run, it produced a jail with no CLI in it — the launcher deliberately allows that for the
        // operator's bare-shell path — and the coordinator was told it had a worker. Checked before the
        // caps so a typo is never reported as "the worker cap is full", and before SpawnAsync so it costs
        // no session record, no jail and no worker slot.
        if (CoordinatorSpawnGate.RefuseUnknownKind(request.AgentKind, _launcher.InstalledAgentKinds) is { } unknownKind)
        {
            RefuseShimSpawn(coordinatorAgentId, request.AgentKind, unknownKind);
            return new AgentIpcResponse(Ok: false, Error: unknownKind);
        }

        // The brief/task separation, refused at the channel — and this check is REQUIRED here rather
        // than only inside the gate. `SpawnAsync` treats "neither a title nor a task" as "this spawn is
        // not plan-gated" (that is how an operator's own spawn is spelled), so a shim request carrying
        // neither would not be refused downstream — it would silently produce an UNGATED managed worker,
        // which is strictly worse than the defect being fixed. A coordinator spawn is plan-gated by
        // definition, so the wire must carry both, and it is checked before anything is minted.
        if (WorkerPlanGate.RefuseBrief(request.Title, request.TaskPrompt) is { } briefRefusal)
        {
            RefuseShimSpawn(coordinatorAgentId, request.AgentKind, briefRefusal);
            return new AgentIpcResponse(Ok: false, Error: briefRefusal);
        }

        // Id-only by necessity: the shim's identity is positional (only that coordinator's jail
        // has the socket) and the socket carries no repo. Safe — a coordinator id is always a
        // minted GUID; the only ids that repeat across repos are the intake's `pr-<n>`, and an
        // external-PR worker is Managed, so it never gets an IPC endpoint to ask through.
        var coordinator = _store.Find(coordinatorAgentId);
        if (coordinator is null)
        {
            return new AgentIpcResponse(Ok: false, Error: "this coordinator session is no longer live");
        }

        if (coordinator.RepoHash is not { Length: > 0 } repoHandle)
        {
            return new AgentIpcResponse(Ok: false, Error: "the coordinator has no provisioned repo to spawn against");
        }

        // MG-2: the wired shim spawn is the agent-reachable path, so the hard caps that live in
        // the (un-wired) CoordinatorTools must be re-applied here server-side — a coordinator
        // must not be able to fan out unlimited Managed workers or spawn under memory pressure.
        //
        // MaxActiveWorkers is a BOX-wide allowance (as is the AdmissionController's agent count),
        // so the counted population is every Managed session on the daemon, unchanged by the
        // (repo, id) key — List() still returns one entry per session. It is now strictly more
        // accurate: a second repo's `pr-7` is a real session that consumes a slot, where before it
        // could not be recorded at all and so was invisible to the cap.
        //
        // Phase 2: a worker blocked on plan approval is a live Managed session, so it is already
        // inside this count — which is exactly the decided behaviour (the cap is a RESOURCE cap and
        // a blocked worker still holds its jail). What was missing is legibility: a coordinator
        // refused here used to be told only "let one finish", which is wrong and unactionable when
        // the truth is "six plans are sitting in front of you".
        var activeManaged = _store.List().Count(s => s.Role == AgentRoles.Managed);
        var refusal = CoordinatorSpawnGate.Evaluate(
            activeManaged, _limits.MaxActiveWorkers, _admission, _planGate);
        if (refusal is not null)
        {
            _coordLog.LogWarning(
                "shim spawn refused (coordinator={Coordinator}): {Reason}", coordinatorAgentId, refusal);
            _audit.Append(new AuditEvent("shim_spawn_refused", new Dictionary<string, string>
            {
                ["coordinator_id"] = coordinatorAgentId,
                ["active_managed"] = activeManaged.ToString(),
                ["max_active_workers"] = _limits.MaxActiveWorkers.ToString(),
                ["blocked_on_plan_approval"] = _planGate.BlockedWorkerCount.ToString(),
                ["reason"] = refusal,
            }));
            return new AgentIpcResponse(Ok: false, Error: refusal);
        }

        try
        {
            // The task the coordinator described goes to the plan gate, NOT into the jail. Before
            // phase 2 this field was parsed off the wire and then silently dropped — the worker
            // never received the task at all. It now has a home, and a gate: the daemon hands it
            // over only once a human has approved the worker's own plan.
            var agentId = await SpawnAsync(
                repoHandle, request.AgentKind, _keys.TryGet(repoHandle, request.AgentKind),
                AgentRoles.Managed, ct, _keys.TryGetExtraEnv(repoHandle),
                parentAgentId: coordinatorAgentId,
                heldTaskTitle: request.Title,
                heldTaskPrompt: request.TaskPrompt).ConfigureAwait(false);

            // Read back from the GATE, not from the switch. The two can differ by a toggle flipped in
            // the milliseconds since this spawn started, and what the coordinator must be told is what
            // happened to the worker it now owns. A literal "AwaitingPlan" here — the shipped wording —
            // would tell a coordinator to wait for an approval that is never coming, which is the
            // §12.2/F2 failure ("the coordinator could never be told the job finished") arriving at the
            // other end of the loop.
            return new AgentIpcResponse(
                Ok: true,
                AgentId: agentId,
                Status: _planGate.IsUngated(agentId) ? "Working" : "AwaitingPlan",
                Error: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentIpcResponse(Ok: false, Error: ex.Message);
        }
    }

    /// <summary>
    /// Resolves a worker a coordinator op named, <b>scoped to that coordinator's own fan-out</b>
    /// (coordinator contract §7). Returns null when the id names nothing the caller owns.
    ///
    /// <para><b>Ownership is (RepoHash, AgentId), never a bare id.</b> A bare agent id is unique only
    /// within a repo — the external-PR intake names its sessions <c>pr-&lt;n&gt;</c>, and two subscribed
    /// repositories that each have a pull request #7 both want <c>pr-7</c>. That collision has been fixed
    /// three times in this codebase already (#281 <c>AgentSessionStore</c>, #284 <c>SwarmReconciler</c>,
    /// #286 <c>TerminalSessionManager</c>), so this check does not re-introduce it: the target must be a
    /// child of this coordinator <i>and</i> live in this coordinator's repo. Matching on
    /// <c>ParentAgentId</c> alone would let a coordinator in repo A steer repo B's identically-named
    /// worker.</para>
    ///
    /// <para><b>A stranger's worker is answered as a missing one.</b> Same message either way, so the
    /// channel cannot be used to probe which agent ids exist elsewhere on the daemon — an ownership check
    /// that distinguishes "not yours" from "no such worker" is an existence oracle for every other
    /// coordinator's fan-out.</para>
    /// </summary>
    private AgentSession? OwnedWorker(string? namedAgentId, string coordinatorAgentId)
    {
        if (string.IsNullOrWhiteSpace(namedAgentId))
        {
            return null;
        }

        var coordinator = _store.Find(coordinatorAgentId);
        if (coordinator is null)
        {
            return null;
        }

        var scope = coordinator.RepoHash ?? string.Empty;
        return _store.List().FirstOrDefault(s =>
            string.Equals(s.Id, namedAgentId, StringComparison.Ordinal)
            && string.Equals(s.RepoHash ?? string.Empty, scope, StringComparison.Ordinal)
            && string.Equals(s.ParentAgentId, coordinatorAgentId, StringComparison.Ordinal));
    }

    /// <summary>Every worker this coordinator owns, in its own repo (contract §7).</summary>
    private List<AgentSession> OwnedWorkers(string coordinatorAgentId)
    {
        var coordinator = _store.Find(coordinatorAgentId);
        if (coordinator is null)
        {
            return new List<AgentSession>();
        }

        var scope = coordinator.RepoHash ?? string.Empty;
        return _store.List()
            .Where(s => string.Equals(s.RepoHash ?? string.Empty, scope, StringComparison.Ordinal)
                        && string.Equals(s.ParentAgentId, coordinatorAgentId, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// <c>get_worker_status</c> (contract §3). All owned workers, or one when the request names it.
    /// Each row carries the plan-gate reason, so "why is this worker not doing anything" is answered in
    /// the same call rather than tempting the coordinator toward a capability it does not have.
    /// </summary>
    private AgentIpcResponse Status(AgentIpcRequest request, string coordinatorAgentId)
    {
        if (!string.IsNullOrWhiteSpace(request.AgentId))
        {
            var owned = OwnedWorker(request.AgentId, coordinatorAgentId);
            if (owned is null)
            {
                return new AgentIpcResponse(Ok: false, Error: $"no worker '{request.AgentId}'");
            }

            return new AgentIpcResponse(Ok: true, Agents: new[] { Row(owned) });
        }

        var workers = OwnedWorkers(coordinatorAgentId);
        var rows = workers.Select(Row).ToArray();
        var backpressure = _planGate.BackpressureSignal(workers.Count, _limits.MaxActiveWorkers);
        return new AgentIpcResponse(Ok: true, Agents: rows, Status: backpressure);
    }

    /// <summary>One status row: id, kind, state, role, and the plan-gate reason when it is blocked.</summary>
    private string Row(AgentSession s)
    {
        var gate = _planGate.MayWork(s.Id, out var reason) ? string.Empty : "\t" + reason;
        return $"{s.Id}\t{s.Kind}\t{s.State}\t{s.Role}{gate}";
    }

    /// <summary>
    /// <c>send_worker_prompt</c> (contract §3) — steer a worker this coordinator owns. Refused while the
    /// worker sits at the plan gate: otherwise the coordinator could simply hand over the task the daemon
    /// is deliberately withholding (phase-2 decision 2.2, point 3).
    /// </summary>
    private async Task<AgentIpcResponse> PromptAsync(
        AgentIpcRequest request, string coordinatorAgentId, CancellationToken ct)
    {
        if (_killGate.IsFrozen)
        {
            return new AgentIpcResponse(Ok: false, Error: "Everything is frozen (kill switch engaged) — resume first.");
        }

        var owned = OwnedWorker(request.AgentId, coordinatorAgentId);
        if (owned is null)
        {
            return new AgentIpcResponse(Ok: false, Error: $"no worker '{request.AgentId}'");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return new AgentIpcResponse(Ok: false, Error: "a prompt is required (mainguard-agent prompt <agent-id> <text>)");
        }

        if (!_planGate.MayReceivePrompt(owned.Id, out var planReason))
        {
            return new AgentIpcResponse(Ok: false, Error: planReason);
        }

        var delivery = await _binder.TrySendPromptAsync(owned.Key, request.Prompt, ct).ConfigureAwait(false);
        if (!delivery.Submitted)
        {
            return new AgentIpcResponse(
                Ok: false, Error: delivery.Refusal ?? $"{owned.Id} has no live CLI to steer.");
        }

        _audit.Append(new AuditEvent("coordinator_worker_prompt", new Dictionary<string, string>
        {
            ["coordinator_id"] = coordinatorAgentId,
            ["worker_agent_id"] = owned.Id,
            ["repo_hash"] = owned.RepoHash ?? string.Empty,
            // What was actually pressed, and what the CLI did about it. The record used to say only that
            // a prompt happened — which is exactly what it said for three prompts that were never
            // submitted at all.
            ["terminator"] = "CR",
            // Separate from the body, which is the J2 fix; recorded so a regression to one coalesced
            // write is visible in the audit trail and not only in a live run's stranded input box.
            ["terminator_write"] = "separate",
            ["cli_echoed"] = delivery.Echoed ? "true" : "false",
            ["cli_reacted"] = delivery.Reacted ? "true" : "false",
        }));

        // No agentId on purpose: the shim prints an id when one is present and the status only when one
        // is not, so echoing the target back is what kept every observation below invisible to the
        // caller. A prompt's useful answer is what happened to it, not who it was for.
        return new AgentIpcResponse(
            Ok: true, Status: PromptStatus(owned.Id, delivery.Echoed, delivery.Reacted));
    }

    /// <summary>
    /// What the coordinator is told about its own steer — <b>an account of what the daemon did and saw,
    /// never a verdict on what the worker accepted</b> (defect J3).
    ///
    /// <para>This used to end "Enter was pressed and its CLI redrew in response." A redraw cannot carry
    /// that meaning: it fires on the CLI's own echo of the keystrokes, and a CLI already mid-turn
    /// repaints continuously without having read anything. But the wording read as confirmation, and the
    /// identical sentence came back for six prompts in a live run — where the coordinator had to work out
    /// for itself that "prompt confirms keystrokes landed, not that the worker accepted anything." An
    /// agent should not have to distrust its own tools' success messages, so the tool stops inviting it
    /// to: the limit is now stated in the message rather than left for the reader to discover.</para>
    ///
    /// <para>Every reading states that same limit and points at the one place the answer actually exists
    /// — the worker itself. Only the observation differs. None says "retry": a second prompt is a second
    /// turn, not a redelivery, and a coordinator told to retry would double-steer a worker that was
    /// merely busy.</para>
    /// </summary>
    internal static string PromptStatus(string workerId, bool echoed, bool reacted)
    {
        // What was DONE. The same in every reading, because it is the only part the daemon knows rather
        // than infers — and it names the two-write shape, so a J2 regression is visible in the message.
        var did = $"typed the prompt into {workerId}'s CLI, then pressed Enter as a separate keystroke";

        var saw = (echoed, reacted) switch
        {
            // The strongest reading available: the CLI repainted the text BEFORE Enter, so it had already
            // read the body and the CR went as its own keystroke rather than as the tail of a paste.
            // That rules out the J2 failure mode. It does not confirm a turn.
            (true, true) =>
                "it redrew both while the text was arriving and after Enter, so it is reading its terminal",
            (true, false) =>
                "it redrew while the text was arriving, but produced nothing for "
                + $"{AgentCliBinder.PromptReactionWindow.TotalSeconds:0}s after Enter",
            (false, true) =>
                "it produced nothing while the text was arriving, then redrew after Enter",
            (false, false) =>
                "it produced no output at all, so it may be mid-turn or wedged",
        };

        return $"{did}. Observed: {saw}. That is NOT confirmation the prompt became a turn — a redraw "
            + "only shows the CLI is reading its terminal, and one already mid-turn redraws regardless. "
            + $"Only {workerId} itself can confirm it acted: check `mainguard-agent status {workerId}` or "
            + "its terminal before sending another. A second prompt is a second turn, not a retry.";
    }

    /// <summary>
    /// <c>request_verification</c> (contract §3) — <b>propose</b> an owned worker's branch. The daemon
    /// verifies and decides; the coordinator cannot enqueue and cannot merge (§4, "Declaring its own work
    /// merge-ready"). Refused for a worker with no approved plan: there is no authorised work to verify.
    /// </summary>
    private async Task<AgentIpcResponse> VerifyAsync(
        AgentIpcRequest request, string coordinatorAgentId, CancellationToken ct)
    {
        var owned = OwnedWorker(request.AgentId, coordinatorAgentId);
        if (owned is null)
        {
            return new AgentIpcResponse(Ok: false, Error: $"no worker '{request.AgentId}'");
        }

        if (!_planGate.MayRequestVerification(owned.Id, out var planReason))
        {
            return new AgentIpcResponse(Ok: false, Error: planReason);
        }

        if (owned.RepoHash is not { Length: > 0 } repoHandle)
        {
            return new AgentIpcResponse(Ok: false, Error: $"{owned.Id} has no provisioned repo to verify against.");
        }

        var ctx = _mergeQueues.EnsureQueue(repoHandle);
        if (ctx is null)
        {
            return new AgentIpcResponse(Ok: false, Error: $"{owned.Id} has no merge queue to verify against.");
        }

        _audit.Append(new AuditEvent("coordinator_verification_requested", new Dictionary<string, string>
        {
            ["coordinator_id"] = coordinatorAgentId,
            ["worker_agent_id"] = owned.Id,
            ["repo_hash"] = repoHandle,
        }));

        try
        {
            var record = await ctx.Queue.RunVerificationAsync(owned.Id, ct).ConfigureAwait(false);

            // The daemon decides. The coordinator is told the outcome and nothing else moves because it
            // asked — a green record is what lets the item into the queue, and a human still merges it.
            return new AgentIpcResponse(
                Ok: true, AgentId: owned.Id, Status: record.Passed ? "Verified" : "VerificationFailed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentIpcResponse(Ok: false, Error: ex.Message);
        }
    }

    /// <summary>
    /// The <b>worker</b> plan shim's request handler — the daemon side of the phase-2 plan gate
    /// (coordinator contract §2).
    ///
    /// <para>Identity is positional (only that worker's jail has this socket), so a worker can only ever
    /// present, revise or await <i>its own</i> plan; there is no agent-id field on the wire to forge. The
    /// endpoint's role is fixed at creation, so the coordinator ops are not reachable here at all — a
    /// worker that sends <c>{"op":"spawn"}</c> gets "unknown op", not a worker.</para>
    ///
    /// <para><b>The block lives here.</b> <c>present_plan</c> and <c>revise_plan</c> do not return when the
    /// plan is queued — they return when a human decides. On approval the response carries the task prompt
    /// the daemon has been withholding since the spawn. That is what makes "the worker does not start work
    /// until its plan is approved" a property of the system rather than a request in a prompt.</para>
    /// </summary>
    internal async Task<AgentIpcResponse> HandleWorkerPlanRequestAsync(
        AgentIpcRequest request, string workerAgentId, CancellationToken ct)
    {
        _coordLog.LogInformation(
            "plan-shim request: op={Op} from worker={Worker}", request.Op, workerAgentId);

        // Same shape as the coordinator's, and for the same reason: WorkerOps is the allow-list, consulted
        // before anything is routed, so the worker's served surface is an object a test can hold rather
        // than a set of case labels a reader has to trust.
        if (!_workerHandlers.TryGetValue(request.Op, out var handler))
        {
            return new AgentIpcResponse(Ok: false, Error: $"unknown op '{request.Op}'");
        }

        return await handler(request, workerAgentId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The worker endpoint's served surface. One method per op — each plan-gate op has real branching, so
    /// the table stays a routing table. Intersected with <see cref="AgentIpcRequest.WorkerOps"/> for the
    /// same reason the coordinator's is: an unlisted handler is unreachable rather than quietly served.
    /// </summary>
    private readonly ImmutableDictionary<string, Func<AgentIpcRequest, string, CancellationToken, Task<AgentIpcResponse>>>
        _workerHandlers;

    private ImmutableDictionary<string, Func<AgentIpcRequest, string, CancellationToken, Task<AgentIpcResponse>>>
        BuildWorkerHandlers()
    {
        var table = new Dictionary<string, Func<AgentIpcRequest, string, CancellationToken, Task<AgentIpcResponse>>>(
            StringComparer.Ordinal)
        {
            [AgentIpcRequest.BriefOp] = (_, w, _) => Task.FromResult(Brief(w)),
            [AgentIpcRequest.TaskOp] = (_, w, _) => Task.FromResult(TaskFor(w)),
            [AgentIpcRequest.PresentPlanOp] = PresentPlanAsync,
            [AgentIpcRequest.RevisePlanOp] = RevisePlanAsync,
            [AgentIpcRequest.RescopePlanOp] = RescopePlanAsync,
            [AgentIpcRequest.AwaitDecisionOp] = AwaitDecisionAsync,
            [AgentIpcRequest.CommitWorkOp] = (r, w, _) => Task.FromResult(CommitWork(r, w)),
        };

        return LockToContract(table, AgentIpcRequest.WorkerOps, "worker");
    }

    /// <summary>Exactly what a worker endpoint will serve; must set-equal <see cref="AgentIpcRequest.WorkerOps"/>.</summary>
    internal IReadOnlySet<string> ServedWorkerOps => _workerHandlers.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// <c>commit_work</c> — record the approved work on this worker's own branch. The rung the loop was
    /// missing: without it a finished worker stopped on an uncommitted diff, its worktree was deleted at
    /// teardown, and the branch the merge queue reads never carried a single commit.
    ///
    /// <para><b>The gate is asked first, and it is the same gate.</b> <c>MayWork</c> is what answers
    /// whether a worker has an approved plan; steering and verification already route through it, and a
    /// commit is the act those two are about. A worker still at the gate has no authorised work, so it has
    /// nothing legitimate to record — and the refusal reads verbatim to a human because that string is
    /// written for one.</para>
    ///
    /// <para><b>This handler is transport.</b> Everything about WHAT is committed, WHERE and onto WHICH
    /// branch lives in <c>WorktreeManager.CommitAgentWork</c>, which computes all three from the agent id.
    /// Re-deriving any of it here would be the "one policy, two places" shape the plan gate's own release
    /// logic was already caught in.</para>
    /// </summary>
    private AgentIpcResponse CommitWork(AgentIpcRequest request, string workerAgentId)
    {
        if (!_planGate.MayWork(workerAgentId, out var refusal))
        {
            _audit.Append(new AuditEvent("worker_commit_denied", new Dictionary<string, string>
            {
                ["agent_id"] = workerAgentId,
                ["reason"] = refusal,
            }));
            return new AgentIpcResponse(Ok: false, AgentId: workerAgentId, Error: refusal);
        }

        var session = _store.Find(workerAgentId);
        if (session?.RepoHash is not { Length: > 0 } repoHash)
        {
            return new AgentIpcResponse(
                Ok: false, AgentId: workerAgentId, Error: "this worker session is no longer live");
        }

        // The deviation declaration is settled BEFORE anything is committed, so a refusal costs the worker
        // one re-run and nothing else — the worktree is untouched by it, which is the only reason this is
        // safe to make mandatory at all. Refusing after the commit would be a failed call over work that
        // had already landed.
        if (DeviationRefusal(request, workerAgentId) is { } declarationRefusal)
        {
            _audit.Append(new AuditEvent("worker_commit_denied", new Dictionary<string, string>
            {
                ["agent_id"] = workerAgentId,
                ["reason"] = declarationRefusal,
                ["cause"] = "deviation-declaration",
            }));
            return new AgentIpcResponse(Ok: false, AgentId: workerAgentId, Error: declarationRefusal);
        }

        var result = _launcher.Worktrees.CommitAgentWork(repoHash, workerAgentId, request.Message);
        _audit.Append(new AuditEvent("worker_commit", new Dictionary<string, string>
        {
            ["agent_id"] = workerAgentId,
            ["repo"] = repoHash,
            ["branch"] = result.Branch,
            ["outcome"] = result.Outcome.ToString(),
            ["sha"] = result.Sha ?? string.Empty,
        }));

        var declarationNote = result.Outcome is AgentWorkCommitOutcome.Committed
            or AgentWorkCommitOutcome.NothingToCommit
            ? RecordDeviations(request, workerAgentId)
            : null;

        return result.Outcome switch
        {
            AgentWorkCommitOutcome.Committed => new AgentIpcResponse(
                Ok: true, AgentId: workerAgentId, Status: result.Branch,
                CommitSha: result.Sha, Committed: true, Feedback: declarationNote),

            // Not an error — the worker asked correctly and there was nothing to record. Answered
            // truthfully rather than as a commit, because "committed" would tell a worker its work is
            // safe while the branch sits exactly where it was.
            AgentWorkCommitOutcome.NothingToCommit => new AgentIpcResponse(
                Ok: true, AgentId: workerAgentId, Status: result.Branch,
                CommitSha: result.Sha, Committed: false, Feedback: result.Detail),

            _ => new AgentIpcResponse(
                Ok: false, AgentId: workerAgentId, Committed: false,
                Error: result.Detail ?? $"could not commit on {result.Branch} ({result.Outcome})"),
        };
    }

    /// <summary>
    /// Why this <c>commit_work</c> may not proceed on its deviation declaration, or null when it may.
    ///
    /// <para><b>Required only where there is something to deviate FROM.</b> The question is asked of a
    /// worker that holds an approved plan, because the thing being departed from is that plan's
    /// <c>approach</c>. A worker spawned with plan mode off has no approved approach, so demanding a
    /// declaration from it would be a ritual — and it is <c>ApprovedForWorker</c> that answers this, the
    /// same single authority the F6 scope comparison and the flagged rows resolve through, rather than a
    /// second reading of the mode switch.</para>
    ///
    /// <para><b>Silence is refused, and that is the whole point.</b> The defect this closes had a worker
    /// ship the opposite of its approved approach with the file scope honoured and a green verification it
    /// had written the tests for. An optional declaration would be empty on precisely those runs. So a
    /// gated commit must carry exactly one of the two answers, and "no deviations" is a claim the worker
    /// makes rather than the absence of one.</para>
    /// </summary>
    private string? DeviationRefusal(AgentIpcRequest request, string workerAgentId)
    {
        var declared = DeclaredDeviations(request);
        var assertsNone = request.NoDeviations == true;

        if (assertsNone && declared.Count > 0)
        {
            return "--no-deviations and --deviated say opposite things about the same work. Send one: "
                   + "mainguard-plan " + WorkerPlanShim.CommitUsage;
        }

        if (_plans.ApprovedForWorker(workerAgentId) is null || assertsNone || declared.Count > 0)
        {
            return null;
        }

        return "this commit needs your deviation declaration. Your plan's approved `approach` is what the "
               + "human agreed you would do; say whether this work follows it. Nothing checks that for "
               + "you — your own tests pass either way — so the declaration is the only thing that tells "
               + "them otherwise. Run: mainguard-plan " + WorkerPlanShim.CommitUsage;
    }

    /// <summary>
    /// Records the worker's declaration on its approved plan and returns the sentence the shim prints
    /// back. Null when there was nothing to say either way.
    ///
    /// <para>An ungated worker that volunteered a declaration is <b>told</b> it was not recorded rather
    /// than having it silently dropped or its commit refused: the commit is the thing that must not be
    /// lost, and "we ignored what you said" is exactly the kind of quiet that this subsystem keeps
    /// finding at the bottom of its defects.</para>
    /// </summary>
    private string? RecordDeviations(AgentIpcRequest request, string workerAgentId)
    {
        var declared = DeclaredDeviations(request);
        if (!(request.NoDeviations == true) && declared.Count == 0)
        {
            return null;
        }

        var recorded = _plans.DeclareDeviations(workerAgentId, declared);
        return recorded.IsRecorded
            ? recorded.Message
            : "not recorded — " + recorded.Message;
    }

    private static IReadOnlyList<string> DeclaredDeviations(AgentIpcRequest request) =>
        (request.Deviations ?? Array.Empty<string>())
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .ToList();

    /// <summary>What am I here to plan? The brief and the live plan's state — never the task prompt.</summary>
    private AgentIpcResponse Brief(string workerAgentId)
    {
        var brief = _planGate.PlanningBriefFor(workerAgentId);
        if (brief is null)
        {
            return new AgentIpcResponse(Ok: false, Error: "this worker session is no longer live");
        }

        var live = _plans.LiveForWorker(workerAgentId);
        return new AgentIpcResponse(
            Ok: true,
            Brief: brief,
            PlanId: live?.PlanId,
            Status: live?.Status.ToString(),
            Feedback: live?.RejectionFeedback,
            Revision: live?.RevisionCount,
            MaxRevisions: _plans.MaxPlanRevisions);
    }

    /// <summary>
    /// What am I here to DO? The withheld task, released through the gate's own
    /// <c>TryReleaseTask</c> — the same single exit, with the same release-once audit record, that
    /// <see cref="BlockForDecisionAsync"/> uses on approval.
    ///
    /// <para>There is deliberately no second reading of the mode here. Whether this worker may have its
    /// task is <c>WorkerPlanGate.MayWork</c>'s answer and nothing else's; asking the switch again would
    /// be the second copy of a policy that MG-12 says goes decorative, and it would be a copy that
    /// answers about the daemon's CURRENT setting rather than about this worker's.</para>
    /// </summary>
    private AgentIpcResponse TaskFor(string workerAgentId)
    {
        if (_planGate.ModeFor(workerAgentId) is null)
        {
            return new AgentIpcResponse(Ok: false, Error: "this worker session is no longer live");
        }

        if (!_planGate.TryReleaseTask(workerAgentId, out var taskPrompt))
        {
            _planGate.MayWork(workerAgentId, out var refusal);
            return new AgentIpcResponse(Ok: false, AgentId: workerAgentId, Error: refusal);
        }

        return new AgentIpcResponse(
            Ok: true, AgentId: workerAgentId, Status: "Task", TaskPrompt: taskPrompt);
    }

    /// <summary>The worker presents the plan it authored — then blocks here until a human decides.</summary>
    private async Task<AgentIpcResponse> PresentPlanAsync(
        AgentIpcRequest request, string workerAgentId, CancellationToken ct)
    {
        // Refused BEFORE the schema check, so an ungated worker is told the real reason rather than
        // being sent to fix a plan that would be refused however well-formed it was.
        if (_planGate.RefusePlanPresentation(workerAgentId) is { } noPlanWanted)
        {
            return new AgentIpcResponse(Ok: false, AgentId: workerAgentId, Error: noPlanWanted);
        }

        if (!TryValidatePlan(request.PlanJson, out var fields, out var invalid))
        {
            return invalid;
        }

        var presentation = _plans.Present(
            workerAgentId,
            _planGate.CoordinatorFor(workerAgentId) ?? _store.Find(workerAgentId)?.ParentAgentId ?? "",
            request.Title ?? _planGate.PlanningBriefFor(workerAgentId) ?? "Untitled plan",
            fields!,
            taskPrompt: string.Empty, // the task lives in the gate, never in the plan record
            budgetUsd: _planGate.BudgetFor(workerAgentId));

        if (!presentation.IsPresented)
        {
            return new AgentIpcResponse(Ok: false, Error: presentation.Message, PlanId: presentation.PlanId);
        }

        return await BlockForDecisionAsync(workerAgentId, presentation.PlanId!, ct).ConfigureAwait(false);
    }

    /// <summary>The worker re-presents a plan revised against the human's feedback — and blocks again.</summary>
    private async Task<AgentIpcResponse> RevisePlanAsync(
        AgentIpcRequest request, string workerAgentId, CancellationToken ct)
    {
        if (_planGate.RefusePlanPresentation(workerAgentId) is { } noPlanWanted)
        {
            return new AgentIpcResponse(Ok: false, AgentId: workerAgentId, Error: noPlanWanted);
        }

        if (string.IsNullOrWhiteSpace(request.PlanId))
        {
            return new AgentIpcResponse(Ok: false, Error: "a plan id is required (mainguard-plan revise <id> <plan.json>)");
        }

        if (!OwnsPlan(workerAgentId, request.PlanId, out var ownershipError))
        {
            return ownershipError;
        }

        if (!TryValidatePlan(request.PlanJson, out var fields, out var invalid))
        {
            return invalid;
        }

        var revision = _plans.Revise(
            request.PlanId,
            request.Title ?? _planGate.PlanningBriefFor(workerAgentId) ?? "Untitled plan",
            fields!);

        if (revision.Outcome == Mainguard.Agents.Agents.Orchestrator.PlanRevisionOutcome.Escalated)
        {
            return DecisionResponse(workerAgentId, request.PlanId, revision.Plan, revision.Message);
        }

        if (!revision.IsRevised)
        {
            return new AgentIpcResponse(Ok: false, Error: revision.Message, PlanId: request.PlanId);
        }

        return await BlockForDecisionAsync(workerAgentId, request.PlanId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>rescope_plan</c> — the worker asks to widen an APPROVED plan, then blocks on the decision.
    ///
    /// <para><b>The worker is not suspended while it waits.</b> Nothing here touches
    /// <c>WorkerPlanGate.MayWork</c>, so steering, verification and above all <c>commit_work</c> keep
    /// answering off the approval the worker already holds — for exactly the scope that was approved.
    /// Blocking it would make asking legally more costly than widening quietly, which is the behaviour
    /// this op exists to make unnecessary, and it would refuse a mid-task worker the one call that lets
    /// its work outlive its jail.</para>
    ///
    /// <para>Ownership is checked the same way <c>revise</c> checks it, and answers a stranger's plan id
    /// exactly as it answers one that does not exist — this channel is not an existence oracle for other
    /// workers' plans.</para>
    /// </summary>
    private async Task<AgentIpcResponse> RescopePlanAsync(
        AgentIpcRequest request, string workerAgentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PlanId))
        {
            return new AgentIpcResponse(
                Ok: false,
                Error: "the approved plan's id is required (mainguard-plan "
                       + WorkerPlanShim.RescopeUsage + ")");
        }

        if (!OwnsPlan(workerAgentId, request.PlanId, out var ownershipError))
        {
            return ownershipError;
        }

        if (!TryValidatePlan(request.PlanJson, out var fields, out var invalid))
        {
            return invalid;
        }

        var rescope = _plans.Rescope(
            request.PlanId,
            request.Title ?? _planGate.PlanningBriefFor(workerAgentId) ?? "Untitled plan",
            fields!);

        if (!rescope.IsPresented)
        {
            return new AgentIpcResponse(Ok: false, Error: rescope.Message, PlanId: rescope.PlanId);
        }

        return await BlockForDecisionAsync(workerAgentId, rescope.PlanId!, ct).ConfigureAwait(false);
    }

    /// <summary>Re-attach after a crash or a daemon restart: block on an already-presented plan.</summary>
    private async Task<AgentIpcResponse> AwaitDecisionAsync(
        AgentIpcRequest request, string workerAgentId, CancellationToken ct)
    {
        if (_planGate.RefusePlanPresentation(workerAgentId) is { } noPlanWanted)
        {
            return new AgentIpcResponse(Ok: false, AgentId: workerAgentId, Error: noPlanWanted);
        }

        if (string.IsNullOrWhiteSpace(request.PlanId))
        {
            return new AgentIpcResponse(Ok: false, Error: "a plan id is required (mainguard-plan await <id>)");
        }

        if (!OwnsPlan(workerAgentId, request.PlanId, out var ownershipError))
        {
            return ownershipError;
        }

        return await BlockForDecisionAsync(workerAgentId, request.PlanId, ct).ConfigureAwait(false);
    }

    /// <summary>Parks the connection until the human decides, then answers with the decision.</summary>
    private async Task<AgentIpcResponse> BlockForDecisionAsync(string workerAgentId, string planId, CancellationToken ct)
    {
        Mainguard.Agents.Agents.Orchestrator.PlanDecision decision;
        try
        {
            decision = await _plans.AwaitDecisionAsync(planId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Daemon shutting down, or the worker went away. The plan stays pending; a re-attached worker
            // picks the decision back up with `mainguard-plan await <id>` — nothing is lost.
            return new AgentIpcResponse(Ok: false, Error: "the daemon is shutting down — re-await this plan", PlanId: planId);
        }

        var plan = _plans.Get(planId);
        var response = DecisionResponse(workerAgentId, planId, plan, message: null);

        // Ask the gate on EVERY decision, not only on an approval, and let it decide.
        //
        // The obvious shape here is `if (decision.Approved) { release }`. That was the first cut, and
        // mutation testing rejected it: with the rule written down in two places, breaking the gate's own
        // check changed nothing observable through this channel — the `if` was quietly doing the work,
        // and the gate's check was never reached by anything a test could see. Two copies of a policy is
        // how one of them becomes decorative (MG-12). One authority, always consulted, is the version
        // whose failure is visible.
        var released = _planGate.TryReleaseTask(workerAgentId, out var taskPrompt);
        return response with { TaskPrompt = released ? taskPrompt : string.Empty };
    }

    private AgentIpcResponse DecisionResponse(
        string workerAgentId,
        string planId,
        Mainguard.Agents.Agents.Orchestrator.PendingPlan? plan,
        string? message) =>
        new(Ok: true,
            AgentId: workerAgentId,
            Error: null,
            PlanId: planId,
            Status: plan?.Status.ToString() ?? "Unknown",
            Feedback: message ?? plan?.RejectionFeedback,
            Revision: plan?.RevisionCount ?? 0,
            RevisionsRemaining: Math.Max(0, _plans.MaxPlanRevisions - (plan?.RevisionCount ?? 0)),
            MaxRevisions: _plans.MaxPlanRevisions,
            // Carried on EVERY decision about a re-scope, not only the approval, because the refusals are
            // where it changes what the shim says: a rejected or escalated re-scope has taken nothing
            // away, and the generic wording would send a still-authorised worker away from its work.
            RescopeOf: plan?.SupersedesPlanId);

    /// <summary>A worker may only act on a plan it authored — checked daemon-side, not assumed.</summary>
    private bool OwnsPlan(string workerAgentId, string planId, out AgentIpcResponse error)
    {
        var plan = _plans.Get(planId);
        if (plan is null)
        {
            error = new AgentIpcResponse(Ok: false, Error: $"no plan '{planId}'");
            return false;
        }

        if (!string.Equals(plan.WorkerAgentId, workerAgentId, StringComparison.Ordinal))
        {
            _audit.Append(new AuditEvent("plan_ownership_denied", new Dictionary<string, string>
            {
                ["plan_id"] = planId,
                ["requesting_worker"] = workerAgentId,
                ["owning_worker"] = plan.WorkerAgentId,
            }));
            error = new AgentIpcResponse(Ok: false, Error: $"no plan '{planId}'"); // no existence oracle
            return false;
        }

        error = null!;
        return true;
    }

    /// <summary>Validates the worker's plan against the shared schema before it reaches a human.</summary>
    private static bool TryValidatePlan(
        string? planJson,
        out Mainguard.Agents.Agents.Orchestrator.TaskPlanFields? fields,
        out AgentIpcResponse error)
    {
        var validation = Mainguard.Agents.Agents.Orchestrator.TaskPlanSchema.Validate(planJson);
        if (!validation.IsValid)
        {
            fields = null;
            error = new AgentIpcResponse(
                Ok: false,
                Error: "the plan does not satisfy the plan schema",
                PlanErrors: validation.Errors.ToArray());
            return false;
        }

        fields = validation.Fields;
        error = null!;
        return true;
    }

    /// <summary>An <see cref="IProgress{T}"/> that invokes on the reporting thread. <see cref="Progress{T}"/>
    /// captures a <see cref="SynchronizationContext"/> or falls back to the thread pool, which in a daemon
    /// means progress lines can be delivered out of order and after the step they describe has finished —
    /// the one thing a progress line must not do.</summary>
    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
