using System;
using System.Collections.Concurrent;
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
/// <para><b>Un-containment is scoped, not absent (ISSUES-LOG #17).</b> The original version of this type
/// had no release path at all, on the reasoning that auto-unlocking on resume would clear the locks of
/// managed workers whose terminals were locked at spawn time (a role property, not a kill property) and
/// hand an operator-locked worker a typeable terminal. That reasoning is right and is preserved exactly —
/// but the conclusion drawn from it was wrong: it left the emergency stop one-way. Engage froze every jail
/// and Resume freed none, so a killed agent was unrecoverable from inside the app (the per-agent Unpause
/// RPC correctly refuses a jail it did not human-pause) while the Resource Monitor called the state
/// "(recoverable)".</para>
///
/// <para>So this target now keeps a <b>causation ledger</b>: for each session and each agent it records
/// whether <i>it</i> was the party that transitioned the jail to paused, took the terminal lock, and closed
/// the leader's input gate. <see cref="UnpauseAsync"/> reverses precisely those entries. A worker locked at
/// spawn keeps its lock; a jail a human paused before the stop, or one the keep-alive rebase was holding,
/// stays paused — and stays paused for the same reason it always did, because the kill switch never owned
/// that pause. The distinction between "paused because a human asked" and "paused because the emergency
/// stop fired" is what the ledger is; it is not weakened by making the second one reversible.</para>
/// </summary>
public sealed class SandboxKillTarget : IKillTarget
{
    private readonly AgentSessionStore _store;
    private readonly ISandboxEngine _sandboxes;
    private readonly SessionLeader _leader;
    private readonly TerminalLockRegistry _locks;
    private readonly IPauseArbiter _arbiter;
    private readonly ILogger _log;

    /// <summary>
    /// The causation ledger — what THIS target froze in the current kill epoch, and therefore the only
    /// things <see cref="UnpauseAsync"/> is entitled to release. Keyed per agent id because that is the
    /// granularity <see cref="IKillTarget"/> speaks in; the container set is recorded per entry because a
    /// single id can span repos.
    /// </summary>
    private readonly ConcurrentDictionary<string, KillContainment> _contained = new(StringComparer.Ordinal);

    /// <param name="store">The live session registry (agent set, container ids, state fan-out).</param>
    /// <param name="sandboxes">The P2-07 sandbox engine — <c>docker pause</c> lives here.</param>
    /// <param name="leader">The P2-09 session leader owning the per-agent PTY input gate.</param>
    /// <param name="locks">The daemon-side terminal input lock the gRPC layer enforces.</param>
    /// <param name="arbiter">The human/machine pause ledger. Read on the RELEASE path only: a human pause
    /// is sticky through a kill-switch cycle, so an agent a human holds paused is never woken by Resume
    /// even if the kill switch's own pause call is the one that reached Docker first.</param>
    /// <param name="loggerFactory">Daemon logging (the kill category).</param>
    public SandboxKillTarget(
        AgentSessionStore store,
        ISandboxEngine sandboxes,
        SessionLeader leader,
        TerminalLockRegistry locks,
        IPauseArbiter arbiter,
        ILoggerFactory loggerFactory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sandboxes = sandboxes ?? throw new ArgumentNullException(nameof(sandboxes));
        _leader = leader ?? throw new ArgumentNullException(nameof(leader));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _arbiter = arbiter ?? throw new ArgumentNullException(nameof(arbiter));
        _log = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.KillSwitch);
    }

    /// <summary>One agent's line in the causation ledger: which jails this kill switch actually froze, and
    /// whether it was the party that took the terminal lock / closed the leader's input gate.</summary>
    private sealed record KillContainment(
        IReadOnlyList<string> PausedContainers,
        bool TookTerminalLock,
        bool ClosedInputGate);

    /// <summary>
    /// The agents in scope for the kill, by id. <see cref="IKillTarget"/> is an id-only contract while a
    /// session's identity is (repo, id), so an id two repos both hold appears ONCE here and
    /// <see cref="PauseAsync"/> fans out over every session behind it — the emergency stop must contain
    /// both jails, and it must not ask for the same id twice.
    /// </summary>
    public IReadOnlyList<string> ActiveAgentIds =>
        _store.List().Select(s => s.Id).Distinct(StringComparer.Ordinal).ToList();

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
        //
        // Both flags are read BEFORE the mutation, and both reads are I/O-free, so the causation ledger
        // costs the emergency stop nothing. "Was it already locked/gated?" is the whole question the
        // release side needs answered: a managed worker locked at spawn, or an agent whose input the
        // gateway's 429 back-off had already gated, must come out of a kill-switch cycle exactly as it
        // went in.
        var tookLock = !_locks.IsLocked(agentId);
        var closedGate = !_leader.IsPaused(agentId);
        _locks.Lock(agentId);
        _leader.PauseInput(agentId);

        var pausedByThisKill = new List<string>();

        // MERGED, never overwritten. Engage is idempotent and the UI's control is a toggle, so a second
        // Engage before any Resume is ordinary: its own pause calls 409 against jails the FIRST engage
        // froze and its `tookLock` reads false, so overwriting here would erase the record of what the
        // first one owned and Resume would then release nothing at all — the original bug, restored by
        // a double click.
        void Record() => _contained.AddOrUpdate(
            agentId,
            _ => new KillContainment(pausedByThisKill, tookLock, closedGate),
            (_, existing) => new KillContainment(
                existing.PausedContainers.Union(pausedByThisKill, StringComparer.Ordinal).ToList(),
                existing.TookTerminalLock || tookLock,
                existing.ClosedInputGate || closedGate));

        // EVERY session behind this id, across repos. An agent id is unique only within a repository (the
        // external-PR intake names its sessions `pr-<n>` after the pull request number), so resolving one
        // session here would leave the other repo's jail RUNNING through an emergency stop — the exact
        // failure MG-8 exists to prevent, reintroduced by a lookup rather than by a placeholder.
        var sessions = _store.FindAll(agentId);
        if (sessions.Count == 0)
        {
            // Raced with a stop between ActiveAgentIds and here. The input sever above already happened;
            // there is no session left to mark — but it IS still something to undo, so the ledger records
            // the sever even with no jail behind it.
            Record();
            _log.LogWarning("kill: agent={Agent} has no session — terminal input severed only", agentId);
            return;
        }

        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        foreach (var session in sessions)
        {
            var containerId = session.ContainerId;
            if (string.IsNullOrEmpty(containerId))
            {
                // A session-only record: the spawn chain degraded (unprovisioned repo / failed bind) and no
                // jail was ever attached, so there is no container to freeze and no worker process to stop.
                // The input sever is the whole of the available containment; reporting PauseFailed here
                // would cry wolf on every degraded spawn and bury the failures that DO mean an agent is
                // still running.
                _store.MarkState(session.Key, "Paused", "Kill switch engaged — no jail bound; terminal input severed.");
                _log.LogWarning("kill: agent={Agent} repo={Repo} has no container — terminal input severed only",
                    agentId, session.RepoHash);
                continue;
            }

            Exception? pauseError = null;
            try
            {
                await _sandboxes.PauseAsync(containerId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Captured rather than handled inline: telling "it refused because it is ALREADY frozen"
                // apart from "it refused because the engine is unreachable" needs an await, and an
                // exception filter cannot await.
                pauseError = ex;
            }

            if (pauseError is null)
            {
                // This call is what transitioned the jail, so this kill switch owns the pause and Resume
                // may reverse it.
                pausedByThisKill.Add(containerId);
                _store.MarkState(session.Key, "Paused",
                    "Kill switch engaged — jail paused, terminal input severed. Resume to recover.");
                _log.LogWarning("kill: agent={Agent} repo={Repo} container={Container} paused; terminal input severed",
                    agentId, session.RepoHash, containerId);
                continue;
            }

            if (await IsPausedSafeAsync(containerId, ct).ConfigureAwait(false))
            {
                // The jail IS frozen — it just wasn't this call that froze it (a human pause, or the
                // keep-alive rebase's yield hold; Docker answers 409 to a second pause). Containment is
                // satisfied, so this is not a PauseFailed — and the container is deliberately NOT recorded
                // as ours, because Resume must leave a pause it did not cause exactly where it found it.
                // Decided by engine STATE, never by matching the error text: engine wordings differ per
                // version, the same rule AgentPauseService already follows.
                _store.MarkState(session.Key, "Paused",
                    "Kill switch engaged — this jail was already paused; it stays paused after Resume.");
                _log.LogWarning(pauseError,
                    "kill: agent={Agent} repo={Repo} container={Container} was ALREADY paused — left to its owner",
                    agentId, session.RepoHash, containerId);
                continue;
            }

            // MG-8's core lesson: an uncontained worker must NEVER project as "Paused". "Unresponsive"
            // is the honest word and, unlike an invented one, survives the client's state mapping
            // instead of falling into its Working default. The failure is rethrown after the loop so
            // the KillSwitch records PauseFailed — but only AFTER every other session behind this id
            // has been contained, because a first jail that refuses to pause must not spare a second.
            _store.MarkState(session.Key, "Unresponsive",
                $"Kill switch engaged — docker pause FAILED ({pauseError.Message}); the jail may still be running.");
            _log.LogError(pauseError,
                "kill: docker pause FAILED agent={Agent} repo={Repo} container={Container} — the jail may still be RUNNING",
                agentId, session.RepoHash, containerId);
            failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(pauseError);
        }

        // Recorded even when a session failed to freeze: the ledger is what the release reverses, and the
        // sessions that DID freeze behind a partially-failed id must still be released.
        Record();
        failure?.Throw();
    }

    /// <summary>
    /// Releases exactly the containment <see cref="PauseAsync"/> applied — see the type doc and
    /// ISSUES-LOG #17. Nothing outside the ledger is touched, and a human pause outranks the ledger.
    /// </summary>
    public async Task UnpauseAsync(string agentId, CancellationToken ct)
    {
        if (!_contained.TryRemove(agentId, out var contained))
        {
            // Not frozen by this kill switch (or already released by an earlier Resume). Doing nothing is
            // the correct, idempotent answer — NOT an error, and emphatically not a reason to unpause
            // something on spec.
            _log.LogInformation("resume: agent={Agent} was not contained by the kill switch — nothing to release", agentId);
            return;
        }

        // A human pause is sticky through a kill-switch cycle. It outranks the ledger because the two can
        // race: if a human pause landed while the kill's own pause call was in flight, the kill switch may
        // hold the ledger entry for a jail the human now considers theirs. The terminal sever is still
        // reversed — the human paused the agent's work, not the operator's ability to type.
        var humanPaused = _arbiter.IsHumanPaused(agentId);

        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        if (!humanPaused)
        {
            var sessions = _store.FindAll(agentId);
            foreach (var containerId in contained.PausedContainers)
            {
                var session = sessions
                    .FirstOrDefault(s => string.Equals(s.ContainerId, containerId, StringComparison.Ordinal));

                var state = await ProbeAsync(containerId, ct).ConfigureAwait(false);
                if (state == JailState.Gone)
                {
                    // Torn down during the freeze. Released by definition — reporting this as a failed
                    // release would make a dead agent look like a jail that is still running, and would
                    // abandon the remaining containers behind this id.
                    _log.LogWarning("resume: agent={Agent} container={Container} no longer exists — nothing to unpause",
                        agentId, containerId);
                    continue;
                }

                if (state == JailState.Running)
                {
                    // Somebody already woke it (a raw docker unpause during the freeze). Nothing to do.
                    MarkResumed(session);
                    continue;
                }

                try
                {
                    // Paused, or Unknown — in the unknown case the unpause call itself is the arbiter, and
                    // its error is the honest answer. Never assume a jail we cannot see is awake.
                    await _sandboxes.UnpauseAsync(containerId, ct).ConfigureAwait(false);
                }
                catch (Docker.DotNet.DockerContainerNotFoundException)
                {
                    _log.LogWarning("resume: agent={Agent} container={Container} disappeared mid-release", agentId, containerId);
                    continue;
                }
                catch (Exception ex)
                {
                    if (session is not null)
                    {
                        _store.MarkState(session.Key, "Unresponsive",
                            $"Resume FAILED ({ex.Message}) — the jail is STILL paused. Press Resume again.");
                    }

                    _log.LogError(ex,
                        "resume: docker unpause FAILED agent={Agent} container={Container} — the jail is STILL PAUSED",
                        agentId, containerId);
                    failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                    continue;
                }

                MarkResumed(session);
                _log.LogWarning("resume: agent={Agent} container={Container} unpaused (kill switch released)",
                    agentId, containerId);
            }
        }
        else
        {
            _log.LogInformation(
                "resume: agent={Agent} is human-paused — the kill switch released its terminal lock only, "
                + "the jail stays paused until you resume it", agentId);
        }

        // The input sever, reversed to exactly the state it was in before the kill. A worker locked at
        // spawn (a role property) keeps its lock: `tookLock`/`closedGate` were false for it, so nothing
        // here touches it. This is the concern the original "deliberately no un-containment" note was
        // protecting, honoured precisely rather than by refusing to recover at all.
        if (contained.TookTerminalLock)
        {
            _locks.Unlock(agentId);
        }

        if (contained.ClosedInputGate)
        {
            _leader.ResumeInput(agentId);
        }

        if (failure is not null)
        {
            // Put the entry back so pressing Resume again retries this agent rather than reporting
            // "nothing to release" for a jail that is demonstrably still frozen.
            _contained[agentId] = contained with { TookTerminalLock = false, ClosedInputGate = false };
            failure.Throw();
        }
    }

    private void MarkResumed(AgentSession? session)
    {
        if (session is not null)
        {
            _store.MarkState(session.Key, "Working", "Resumed — the kill switch released this jail.");
        }
    }

    /// <summary>What the engine says about a jail. <see cref="Gone"/> and <see cref="Unknown"/> are kept
    /// apart on purpose: a container that no longer exists needs no release, while one the engine could not
    /// answer for must still be attempted, or a wedged engine would silently read as "all recovered".</summary>
    private enum JailState { Paused, Running, Gone, Unknown }

    private async Task<JailState> ProbeAsync(string containerId, CancellationToken ct)
    {
        try
        {
            return await _sandboxes.IsPausedAsync(containerId, ct).ConfigureAwait(false)
                ? JailState.Paused
                : JailState.Running;
        }
        catch (Docker.DotNet.DockerContainerNotFoundException)
        {
            return JailState.Gone;
        }
        catch
        {
            return JailState.Unknown;
        }
    }

    /// <summary>Engine state, never an error string — the same rule <c>AgentPauseService</c> follows. False
    /// on an unknown answer, so an unreadable engine is reported as a pause that FAILED rather than as a
    /// jail somebody else had already frozen.</summary>
    private async Task<bool> IsPausedSafeAsync(string containerId, CancellationToken ct)
    {
        try
        {
            return await _sandboxes.IsPausedAsync(containerId, ct).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A point-in-time state word per agent id for the journal snapshot. Grouped by id because
    /// two repos can hold one (<c>pr-&lt;n&gt;</c>): a plain <c>ToDictionary</c> would throw on the
    /// duplicate key and take the whole kill's snapshot down with it. When an id really is held twice and
    /// the two differ, both words are reported rather than one silently winning.</summary>
    public IReadOnlyDictionary<string, string> CaptureStates() =>
        _store.List()
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => string.Join("/", g.Select(s => s.State).Distinct(StringComparer.Ordinal)),
                StringComparer.Ordinal);
}
