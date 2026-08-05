using System;
using System.Collections.Generic;
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
public sealed record AgentStopResult(
    bool Stopped, string AgentKind,
    IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile> CliCredentials);

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
    private readonly IAuditLog _audit;
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
        IAuditLog audit,
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
        _audit = audit;
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _spawnLog = loggerFactory.CreateLogger(DaemonLogCategories.Spawn);
        _coordLog = loggerFactory.CreateLogger(DaemonLogCategories.Coordinator);
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
    /// <param name="withoutHostCredentials">
    /// TRUST BOUNDARY. Set for a jail running code from outside the user's own machine (an external pull
    /// request head). A normal spawn falls back to the per-(repo, kind) memory cache for extra env and CLI
    /// login files so a coordinator-spawned worker inherits the login the user just performed; an
    /// untrusted head must inherit <b>nothing</b>, and must not seed that cache either.
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
        decimal heldBudgetUsd = 0m)
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

        // Memory-only, per (repo, kind): lets a coordinator-initiated worker of the same kind IN THE
        // SAME REPO reuse the key the client last supplied (the daemon has no keystore of its own —
        // P2-01 is host-side). MG-6: scoping by repo keeps one repo's credentials out of another's
        // spawns; a miss simply yields no key rather than substituting a stranger's.
        if (!withoutHostCredentials)
        {
            _keys.Remember(repoHandle, agentKind, modelApiKey);
            _keys.RememberExtraEnv(repoHandle, extraEnv);
            _keys.RememberCliCredentials(repoHandle, agentKind, cliCredentials);
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
        var planGated = heldTaskTitle is not null || heldTaskPrompt is not null;
        if (planGated)
        {
            // The repo is half the key (see WorkerPlanGate._held): a held task belongs to (repo, agent id),
            // never to the bare id.
            _planGate.Hold(
                session.Id, parentAgentId ?? "", heldTaskTitle ?? "Untitled task", heldTaskPrompt ?? "",
                heldBudgetUsd, session.RepoHash ?? string.Empty);
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
            if (role == AgentRoles.Coordinator || planGated)
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
                    ipcDir = _ipc.CreateEndpoint(
                        session.Id,
                        endpointRole == Mainguard.Agents.Agents.Ipc.AgentIpcEndpointRole.Coordinator
                            ? HandleShimRequestAsync
                            : HandleWorkerPlanRequestAsync,
                        endpointRole);
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
            var launch = await _launcher.TryLaunchAsync(
                repoHandle, session.Id, agentKind, modelApiKey, ipcDir, ct,
                extraEnv: launchEnv,
                cliCredentials: launchCredentials,
                withoutRepositoryAccess: isCoordinator).ConfigureAwait(false);
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
                Array.Empty<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile>());
        }

        var credentials = await _launcher.HarvestCliCredentialsAsync(
            containerId, session.Kind, ct).ConfigureAwait(false);

        // Same in-memory cache StopAsync refreshes, so a worker spawned by a coordinator later in this
        // daemon session (no client in the loop) boots with the login the user just performed (MG-6
        // scoping: per repo + kind). Only remember a NON-empty harvest — caching an empty result would
        // erase a good cached login for every later worker of this kind.
        if (credentials.Count > 0)
        {
            _keys.RememberCliCredentials(session.RepoHash ?? string.Empty, session.Kind, credentials);
        }

        _spawnLog.LogInformation(
            "harvest: agent={Agent} credentialFiles={Files} (agent left running)", agentId, credentials.Count);
        return new AgentStopResult(false, session.Kind, credentials);
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
        }

        IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile> credentials =
            Array.Empty<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile>();
        if (stopped && session?.ContainerId is { Length: > 0 } containerId)
        {
            credentials = await _launcher.HarvestCliCredentialsAsync(
                containerId, session.Kind, ct).ConfigureAwait(false);
            // Keep the memory-only per (repo, kind) cache current too, so a worker of this kind spawned
            // later in THIS daemon session against THE SAME REPO (coordinator IPC — no client in the
            // loop) boots with the login the user just performed in the stopped jail. A session with no
            // repo hash caches nothing rather than leaking into a repo-less bucket (MG-6).
            _keys.RememberCliCredentials(session.RepoHash ?? string.Empty, session.Kind, credentials);
            await _launcher.TeardownAsync(session.RepoHash, agentId, containerId, ct).ConfigureAwait(false);
        }

        _spawnLog.LogInformation(
            "stop: agent={Agent} stopped={Stopped} credentialFiles={Files}", agentId, stopped, credentials.Count);
        return new AgentStopResult(stopped, session?.Kind ?? string.Empty, credentials);
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

        switch (request.Op)
        {
            case AgentIpcRequest.SpawnOp:
                if (string.IsNullOrWhiteSpace(request.AgentKind))
                {
                    return new AgentIpcResponse(Ok: false, Error: "an agent kind is required (mainguard-agent spawn <agent-kind>)");
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
                        heldTaskTitle: request.Title ?? request.TaskPrompt ?? "Untitled task",
                        heldTaskPrompt: request.TaskPrompt ?? "").ConfigureAwait(false);

                    return new AgentIpcResponse(
                        Ok: true,
                        AgentId: agentId,
                        Status: "AwaitingPlan",
                        Error: null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new AgentIpcResponse(Ok: false, Error: ex.Message);
                }

            case AgentIpcRequest.ListOp:
            case AgentIpcRequest.StatusOp:
                return Status(request, coordinatorAgentId);

            case AgentIpcRequest.PromptOp:
                return await PromptAsync(request, coordinatorAgentId, ct).ConfigureAwait(false);

            case AgentIpcRequest.VerifyOp:
                return await VerifyAsync(request, coordinatorAgentId, ct).ConfigureAwait(false);

            default:
                // Contract §3 is EXHAUSTIVE: "Anything not on this list is denied." This is the whole of
                // the coordinator's deny path, and it is deny-by-default — an op is served only because a
                // case above names it, never because nothing refused it. CoordinatorSurfaceLockTests walks
                // every op name in the protocol (including the worker's) plus the RPC names §4 forbids and
                // asserts each one lands here.
                return new AgentIpcResponse(Ok: false, Error: $"unknown op '{request.Op}'");
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

        var delivered = await _binder.TrySendPromptAsync(owned.Key, request.Prompt, ct).ConfigureAwait(false);
        if (!delivered)
        {
            return new AgentIpcResponse(Ok: false, Error: $"{owned.Id} has no live CLI to steer.");
        }

        _audit.Append(new AuditEvent("coordinator_worker_prompt", new Dictionary<string, string>
        {
            ["coordinator_id"] = coordinatorAgentId,
            ["worker_agent_id"] = owned.Id,
            ["repo_hash"] = owned.RepoHash ?? string.Empty,
        }));

        return new AgentIpcResponse(Ok: true, AgentId: owned.Id, Status: "PromptSent");
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

        // One method per op. Each plan-gate op has real branching, and a braced `case { … }` block is
        // both harder to read and (per .editorconfig) indented two levels deeper than the code around it.
        // The switch stays a routing table.
        return request.Op switch
        {
            AgentIpcRequest.BriefOp => Brief(workerAgentId),
            AgentIpcRequest.PresentPlanOp => await PresentPlanAsync(request, workerAgentId, ct).ConfigureAwait(false),
            AgentIpcRequest.RevisePlanOp => await RevisePlanAsync(request, workerAgentId, ct).ConfigureAwait(false),
            AgentIpcRequest.AwaitDecisionOp => await AwaitDecisionAsync(request, workerAgentId, ct).ConfigureAwait(false),
            _ => new AgentIpcResponse(Ok: false, Error: $"unknown op '{request.Op}'"),
        };
    }

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

    /// <summary>The worker presents the plan it authored — then blocks here until a human decides.</summary>
    private async Task<AgentIpcResponse> PresentPlanAsync(
        AgentIpcRequest request, string workerAgentId, CancellationToken ct)
    {
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

    /// <summary>Re-attach after a crash or a daemon restart: block on an already-presented plan.</summary>
    private async Task<AgentIpcResponse> AwaitDecisionAsync(
        AgentIpcRequest request, string workerAgentId, CancellationToken ct)
    {
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
            MaxRevisions: _plans.MaxPlanRevisions);

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
}
