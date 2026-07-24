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
/// <see cref="CoordinatorIpcServer"/> path) — so a coordinator-spawned worker takes exactly the
/// same chain as an RPC spawn: session record → (coordinator only: IPC endpoint) → worktree +
/// hardened jail (<see cref="SandboxAgentLauncher"/>) → CLI under a real TTY
/// (<see cref="AgentCliBinder"/>) → (managed only: terminal input lock, P2-14). Kept out of the
/// gRPC class per the P2-02 rejection trigger (no business logic in transports).
/// </summary>
public sealed class AgentSpawnService
{
    private readonly AgentSessionStore _store;
    private readonly SandboxAgentLauncher _launcher;
    private readonly AgentCliBinder _binder;
    private readonly CoordinatorIpcServer _ipc;
    private readonly SessionKeyCache _keys;
    private readonly TerminalLockRegistry _locks;
    private readonly KillSwitchGate _killGate;
    private readonly AdmissionController _admission;
    private readonly Mainguard.Agents.Agents.Orchestrator.CoordinatorLimits _limits;
    private readonly IAuditLog _audit;
    private readonly ILogger _spawnLog;
    private readonly ILogger _coordLog;

    public AgentSpawnService(
        AgentSessionStore store,
        SandboxAgentLauncher launcher,
        AgentCliBinder binder,
        CoordinatorIpcServer ipc,
        SessionKeyCache keys,
        TerminalLockRegistry locks,
        KillSwitchGate killGate,
        AdmissionController admission,
        Mainguard.Agents.Agents.Orchestrator.CoordinatorLimits limits,
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
    public async Task<string> SpawnAsync(
        string repoHandle, string agentKind, string? modelApiKey, string role, CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraEnv = null,
        IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile>? cliCredentials = null,
        string? parentAgentId = null)
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
        _keys.Remember(repoHandle, agentKind, modelApiKey);
        _keys.RememberExtraEnv(repoHandle, extraEnv);
        _keys.RememberCliCredentials(repoHandle, agentKind, cliCredentials);

        // Record the session first (its id names the worktree + container), then run the real
        // P2-06/P2-07 spawn chain. A provisioned repo takes the real-jail path; an unprovisioned
        // handle degrades to a session-only record (no fabricated jail).
        var session = _store.Spawn(agentKind, role, parentAgentId);

        // Correlation: every Spawn/Egress/Terminal line for this agent shares its id — the scope
        // renders as (agentId) in the file format, so one grep follows the whole chain.
        using var scope = _spawnLog.BeginScope(session.Id);
        _spawnLog.LogInformation("spawn: session created role={Role} kind={Kind}", role, agentKind);
        // A CLI bind is expected (container start + docker-exec-under-PTY takes a few seconds). Mark it
        // NOW — before the slow launch — so an attach that races in while the agent is still "Starting"
        // waits for the bind instead of latching into echo. Cleared below if no CLI actually binds.
        _binder.MarkBindPending(session.Id);
        string? ipcDir = null;
        try
        {
            if (role == AgentRoles.Coordinator)
            {
                // The endpoint is a container mount source, so it must exist before the jail does.
                // Best-effort: a box where the Unix socket cannot bind still gets a working
                // coordinator (terminal + jail), just without the in-jail spawn tool — audited.
                try
                {
                    ipcDir = _ipc.CreateEndpoint(session.Id, HandleShimRequestAsync);
                    _coordLog.LogInformation("coordinator IPC endpoint created: {Dir}", ipcDir);
                }
                catch (Exception ex)
                {
                    _coordLog.LogWarning(ex, "coordinator IPC endpoint failed — degrading to no spawn-shim");
                    _audit.Append(new AuditEvent("ipc_endpoint_failed", new Dictionary<string, string>
                    {
                        ["agent_id"] = session.Id,
                        ["reason"] = ex.Message,
                    }));
                }
            }

            var launch = await _launcher.TryLaunchAsync(
                repoHandle, session.Id, agentKind, modelApiKey, ipcDir, ct,
                extraEnv: extraEnv ?? _keys.TryGetExtraEnv(repoHandle),
                cliCredentials: cliCredentials ?? _keys.TryGetCliCredentials(repoHandle, agentKind)).ConfigureAwait(false);
            var bound = false;
            if (launch is not null)
            {
                _store.AttachSandbox(session.Id, launch.ContainerId, repoHandle);
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
                _binder.ClearBindPending(session.Id);
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
                _store.MarkState(session.Id, "Dead", ex.Message);
            }

            if (ipcDir is not null)
            {
                _ipc.CloseEndpoint(session.Id);
            }

            _binder.ClearBindPending(session.Id); // spawn failed → no bind coming; don't leave attaches waiting
            _locks.Unlock(session.Id);
            _store.Stop(session.Id);
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
    public async Task<AgentStopResult> StopAsync(string agentId, CancellationToken ct)
    {
        // Capture the session (with its container id/repo hash) BEFORE removing it, so a real jail +
        // worktree can be torn down after the record is gone.
        var session = _store.Find(agentId);
        var stopped = _store.Stop(agentId);

        _binder.Release(agentId);
        _ipc.CloseEndpoint(agentId);
        _locks.Unlock(agentId);

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
                var activeManaged = _store.List().Count(s => s.Role == AgentRoles.Managed);
                var refusal = CoordinatorSpawnGate.Evaluate(activeManaged, _limits.MaxActiveWorkers, _admission);
                if (refusal is not null)
                {
                    _coordLog.LogWarning(
                        "shim spawn refused (coordinator={Coordinator}): {Reason}", coordinatorAgentId, refusal);
                    _audit.Append(new AuditEvent("shim_spawn_refused", new Dictionary<string, string>
                    {
                        ["coordinator_id"] = coordinatorAgentId,
                        ["active_managed"] = activeManaged.ToString(),
                        ["max_active_workers"] = _limits.MaxActiveWorkers.ToString(),
                        ["reason"] = refusal,
                    }));
                    return new AgentIpcResponse(Ok: false, Error: refusal);
                }

                try
                {
                    var agentId = await SpawnAsync(
                        repoHandle, request.AgentKind, _keys.TryGet(repoHandle, request.AgentKind),
                        AgentRoles.Managed, ct, _keys.TryGetExtraEnv(repoHandle),
                        parentAgentId: coordinatorAgentId).ConfigureAwait(false);
                    return new AgentIpcResponse(Ok: true, AgentId: agentId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new AgentIpcResponse(Ok: false, Error: ex.Message);
                }

            case AgentIpcRequest.ListOp:
                // MG-37: this returned EVERY session on the daemon — a coordinator could enumerate other
                // coordinators' workers (and other repos' agents) through its own jail's IPC socket.
                // Scope it to the sessions this coordinator actually spawned. Only coordinators get an IPC
                // endpoint, so managed workers never spawn and "children" is the full descendant set.
                var agents = _store.List()
                    .Where(s => string.Equals(s.ParentAgentId, coordinatorAgentId, StringComparison.Ordinal))
                    .Select(s => $"{s.Id}\t{s.Kind}\t{s.State}\t{s.Role}")
                    .ToArray();
                return new AgentIpcResponse(Ok: true, Agents: agents);

            default:
                return new AgentIpcResponse(Ok: false, Error: $"unknown op '{request.Op}'");
        }
    }
}
