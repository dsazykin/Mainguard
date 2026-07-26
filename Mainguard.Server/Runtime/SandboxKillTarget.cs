using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Auth;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Runtime;

/// <summary>
/// The daemon <see cref="IKillTarget"/> — the half of the kill switch that actually <b>stops work</b>.
///
/// <para>MG-8: its predecessor (<c>SessionStoreKillTarget</c>) only relabelled state —
/// <c>_store.MarkState(agentId, "Paused")</c> — so engaging the emergency stop froze the merge queue and
/// changed a word in the UI while every worker kept executing and every terminal stayed typeable. The
/// freeze half was always sound; the containment half was a placeholder. This target supplies it:</para>
/// <list type="number">
///   <item><description><b>Sever terminal input</b> via <see cref="TerminalLockRegistry"/> (the
///   <c>RoleInterceptor</c> rejects every subsequent <c>data</c> frame daemon-side, and the bound
///   session's OSC 52 copy-out is suppressed) plus <see cref="SessionLeader.PauseInput"/> (the
///   leader-owned PTY gate). Both are in-proc and I/O-free, so they run <b>before</b> the Docker
///   round-trip: an unreachable container engine must never leave keystrokes still reaching a
///   killed agent. The registry is the enforcing layer; the leader flag is the P2-09 seam.</description></item>
///   <item><description><b><c>docker pause</c> the jail</b> via <see cref="ISandboxEngine.PauseAsync"/> —
///   SIGSTOP through the freezer cgroup, which needs no cooperation from the (untrusted) agent or its
///   supervisor. This is what stops the worker's CPU.</description></item>
///   <item><description><b>Mark the session state</b>, preserved verbatim from the old target because the
///   status word is still what the control center renders. It is now a <i>report</i> of containment that
///   happened rather than a substitute for it — and a jail that could NOT be paused is marked
///   <c>Unresponsive</c>, never <c>Paused</c>.</description></item>
/// </list>
///
/// <para><b>Deliberately no un-containment here.</b> <see cref="KillSwitch.Resume"/> clears the freeze gate
/// only; it does not unlock terminals or unpause jails. Auto-unlocking on resume would clear the locks of
/// managed workers whose terminals were locked at spawn time (a role property, not a kill property) and
/// hand an operator-locked worker a typeable terminal — a worse failure than a lock that outlives the
/// kill. Recovery is an explicit per-agent action.</para>
/// </summary>
public sealed class SandboxKillTarget : IKillTarget
{
    private readonly AgentSessionStore _store;
    private readonly ISandboxEngine _sandboxes;
    private readonly SessionLeader _leader;
    private readonly TerminalLockRegistry _locks;
    private readonly ILogger _log;

    /// <param name="store">The live session registry (agent set, container ids, state fan-out).</param>
    /// <param name="sandboxes">The P2-07 sandbox engine — <c>docker pause</c> lives here.</param>
    /// <param name="leader">The P2-09 session leader owning the per-agent PTY input gate.</param>
    /// <param name="locks">The daemon-side terminal input lock the gRPC layer enforces.</param>
    /// <param name="loggerFactory">Daemon logging (the kill category).</param>
    public SandboxKillTarget(
        AgentSessionStore store,
        ISandboxEngine sandboxes,
        SessionLeader leader,
        TerminalLockRegistry locks,
        ILoggerFactory loggerFactory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sandboxes = sandboxes ?? throw new ArgumentNullException(nameof(sandboxes));
        _leader = leader ?? throw new ArgumentNullException(nameof(leader));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _log = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.KillSwitch);
    }

    public IReadOnlyList<string> ActiveAgentIds => _store.List().Select(s => s.Id).ToList();

    public Task<bool> RequestYieldAsync(string agentId, TimeSpan timeout, CancellationToken ct)
    {
        // No cooperative-yield control channel is bound in the daemon host yet (P2-09's IAgentControlChannel
        // has no production transport), so answer "did not yield" honestly. This costs nothing now that the
        // pause is unconditional (MG-39(a)) — the ACK never gated containment in the first place.
        return Task.FromResult(false);
    }

    public async Task PauseAsync(string agentId, CancellationToken ct)
    {
        // ---- Terminal input first: in-proc, instant, cannot fail, independent of Docker ----
        _locks.Lock(agentId);
        _leader.PauseInput(agentId);

        var containerId = _store.Find(agentId)?.ContainerId;
        if (string.IsNullOrEmpty(containerId))
        {
            // A session-only record: the spawn chain degraded (unprovisioned repo / failed bind) and no jail
            // was ever attached, so there is no container to freeze and no worker process to stop. The input
            // sever is the whole of the available containment; reporting PauseFailed here would cry wolf on
            // every degraded spawn and bury the failures that DO mean an agent is still running.
            _store.MarkState(agentId, "Paused", "Kill switch engaged — no jail bound; terminal input severed.");
            _log.LogWarning("kill: agent={Agent} has no container — terminal input severed only", agentId);
            return;
        }

        try
        {
            await _sandboxes.PauseAsync(containerId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // MG-8's core lesson: an uncontained worker must NEVER project as "Paused". "Unresponsive" is
            // the honest word and, unlike an invented one, survives the client's state mapping instead of
            // falling into its Working default. Rethrow so the KillSwitch records PauseFailed in the report
            // and the journal snapshot — the kill still completes, it just tells the truth about this agent.
            _store.MarkState(agentId, "Unresponsive",
                $"Kill switch engaged — docker pause FAILED ({ex.Message}); the jail may still be running.");
            _log.LogError(ex,
                "kill: docker pause FAILED agent={Agent} container={Container} — the jail may still be RUNNING",
                agentId, containerId);
            throw;
        }

        _store.MarkState(agentId, "Paused", "Kill switch engaged — jail paused, terminal input severed (recoverable).");
        _log.LogWarning("kill: agent={Agent} container={Container} paused; terminal input severed", agentId, containerId);
    }

    public IReadOnlyDictionary<string, string> CaptureStates() =>
        _store.List().ToDictionary(s => s.Id, s => s.State, StringComparer.Ordinal);
}
