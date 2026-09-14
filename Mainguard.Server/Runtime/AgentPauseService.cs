using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Runtime;

/// <summary>
/// The daemon-side ledger behind <see cref="IPauseArbiter"/>: which agents a HUMAN holds paused, and
/// which agents the MACHINE (the keep-alive rebase's yield) currently holds in its critical section.
/// One instance per daemon; both the pause RPCs and every repo's <see cref="YieldProtocol"/> read it,
/// which is what keeps the two pause owners from fighting (human pause is sticky; human unpause
/// defers to an in-flight machine hold).
///
/// <para><b>The human half is durable; the machine half is not, and that asymmetry is the design.</b>
/// A <c>docker pause</c>d jail outlives the daemon, so the record of WHO paused it has to as well —
/// without it the reconciler adopts the jail as Paused, <see cref="AgentPauseService.UnpauseAsync"/>
/// answers "this agent isn't human-paused", and the only remaining exit is a raw <c>docker unpause</c>
/// from outside the app (audit F3). A machine hold is the opposite: it is a few seconds of a rebase's
/// critical section inside one process, and a hold rehydrated from a process that no longer exists would
/// be a refusal that never self-clears — so holds stay in memory, where they die with the thing holding
/// them.</para>
/// </summary>
public sealed class HumanPauseLedger : IPauseArbiter
{
    private readonly object _gate = new();
    private readonly HashSet<string> _humanPaused = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _machineHolds = new(StringComparer.Ordinal);
    private readonly IAgentRestartLedger _persist;

    /// <summary>The daemon's ledger, written through to the durable restart store.</summary>
    public HumanPauseLedger()
        : this(null)
    {
    }

    /// <param name="persist">The restart store, or null for the process-wide one. The test seam.</param>
    internal HumanPauseLedger(IAgentRestartLedger? persist)
    {
        _persist = persist ?? AgentRestartLedger.Process;
        foreach (var record in _persist.LoadAll())
        {
            if (record.HumanPaused)
            {
                _humanPaused.Add(record.AgentId);
            }
        }
    }

    public bool IsHumanPaused(string agentId)
    {
        lock (_gate)
        {
            return _humanPaused.Contains(agentId);
        }
    }

    public bool HasMachineHold(string agentId)
    {
        lock (_gate)
        {
            return _machineHolds.TryGetValue(agentId, out var n) && n > 0;
        }
    }

    public void MarkHumanPaused(string agentId)
    {
        bool added;
        lock (_gate)
        {
            added = _humanPaused.Add(agentId);
        }

        if (added)
        {
            _persist.Update(agentId, r => r with { HumanPaused = true });
        }
    }

    public void ClearHumanPaused(string agentId)
    {
        if (ForgetHumanPause(agentId))
        {
            PersistHumanPauseCleared(agentId);
        }
    }

    /// <summary>
    /// Drops the flag from MEMORY only, leaving the durable row saying "a human holds this paused".
    /// Returns whether it had been set.
    ///
    /// <para><b>The two halves are separable because they answer to different clocks.</b> The in-memory
    /// clear has to happen BEFORE the engine call, or a machine hold that starts mid-unpause would see a
    /// human intent that has already been withdrawn and re-freeze the agent. The durable clear has to
    /// happen AFTER it, or a daemon that dies mid-unpause leaves a row reading
    /// <c>{HumanPaused:false, Frozen:["a human paused it"]}</c> — the next daemon rehydrates the axis,
    /// finds no flag, and answers "this agent isn't human-paused" about a jail it is still holding frozen.
    /// That is the audit's original wedge with the durability this PR added, which is worse than the
    /// wedge, because it now survives the restart an operator would reach for.</para>
    /// </summary>
    public bool ForgetHumanPause(string agentId)
    {
        lock (_gate)
        {
            return _humanPaused.Remove(agentId);
        }
    }

    /// <summary>Writes the cleared flag through to the restart ledger. Unconditional — the in-memory half
    /// has usually already gone (see <see cref="ForgetHumanPause"/>), so a "did it change" test here would
    /// skip exactly the write this ordering exists to perform.</summary>
    public void PersistHumanPauseCleared(string agentId)
        => _persist.Update(agentId, r => r with { HumanPaused = false });

    public IDisposable HoldForMachine(string agentId)
    {
        lock (_gate)
        {
            _machineHolds[agentId] = _machineHolds.TryGetValue(agentId, out var n) ? n + 1 : 1;
        }

        return new Hold(this, agentId);
    }

    private sealed class Hold : IDisposable
    {
        private HumanPauseLedger? _owner;
        private readonly string _agentId;

        public Hold(HumanPauseLedger owner, string agentId)
        {
            _owner = owner;
            _agentId = agentId;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }

            lock (owner._gate)
            {
                if (owner._machineHolds.TryGetValue(_agentId, out var n))
                {
                    if (n <= 1)
                    {
                        owner._machineHolds.Remove(_agentId);
                    }
                    else
                    {
                        owner._machineHolds[_agentId] = n - 1;
                    }
                }
            }
        }
    }
}

/// <summary>
/// The human per-agent Pause/Resume bodies behind <c>AgentService.PauseAgent</c>/<c>UnpauseAgent</c>.
/// Not containment: unlike <see cref="SandboxKillTarget"/> this locks no terminal input (keystrokes
/// just buffer against the frozen jail) and touches one agent. Fans over EVERY session behind the id
/// (an id is unique per repo, not globally — pausing one <c>pr-7</c> and leaving the other running
/// would report a pause that is half false). Refusals are answers, not exceptions.
/// </summary>
public sealed class AgentPauseService
{
    private readonly AgentSessionStore _store;
    private readonly IAgentEnvironment _environment;
    private readonly HumanPauseLedger _ledger;
    private readonly KillSwitchGate _killGate;
    private readonly ILogger<AgentPauseService> _log;

    public AgentPauseService(
        AgentSessionStore store,
        IAgentEnvironment environment,
        HumanPauseLedger ledger,
        KillSwitchGate killGate,
        ILogger<AgentPauseService> log)
    {
        _store = store;
        _environment = environment;
        _ledger = ledger;
        _killGate = killGate;
        _log = log;
    }

    public async Task<(bool Done, string Reason)> PauseAsync(string agentId, CancellationToken ct)
    {
        if (_killGate.IsFrozen)
        {
            return (false, "the kill switch is engaged — every jail is already frozen");
        }

        var sessions = _store.FindAll(agentId).Where(s => !string.IsNullOrEmpty(s.ContainerId)).ToList();
        if (sessions.Count == 0)
        {
            return (false, "this agent has no live jail to pause");
        }

        // The CLAIM is persisted before the freeze it explains, and that order is the durable half of the
        // audit's wedge read backwards. The old order wrote the axis first, so a daemon dying inside the
        // loop left `{HumanPaused:false, Frozen:["a human paused it"]}` — a jail the next daemon adopts
        // frozen, with no record of an owner who could release it. Claiming first can only ever leave the
        // opposite shape, `{HumanPaused:true}` with a jail that is still RUNNING, and that one self-heals:
        // Resume finds nothing paused, unpauses nothing, and clears the flag.
        var claimed = _ledger.IsHumanPaused(agentId);
        _ledger.MarkHumanPaused(agentId);

        var frozenAny = false;
        foreach (var session in sessions)
        {
            try
            {
                // Tolerate "already paused" BY STATE, not by error-message substring: the jail may be
                // yield-paused by the cascade at this instant, and engine wordings differ per version.
                if (!await IsPausedAsync(session.ContainerId!, ct).ConfigureAwait(false))
                {
                    await _environment.Sandboxes.PauseAsync(session.ContainerId!, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (!frozenAny && !claimed)
                {
                    // Nothing behind this id is frozen and nothing was before we started, so the claim
                    // describes nothing — withdraw it rather than leave a human pause nobody asked for on
                    // a running agent. Once ANY session has frozen the claim is true of that session and
                    // stays; Resume is what releases it.
                    _ledger.ClearHumanPaused(agentId);
                }

                _log.LogError(ex, "pause: docker pause failed agent={Agent} container={Container}",
                    agentId, session.ContainerId);
                return (false, $"couldn't pause the jail ({ex.Message})");
            }

            _store.MarkState(session.Key, "Paused", "Paused by you.");
            _store.MarkFrozen(session.Key, HumanPausedFrozenReason);
            frozenAny = true;
        }

        _log.LogInformation("pause: agent={Agent} paused by human ({Count} session(s))", agentId, sessions.Count);
        return (true, "");
    }

    /// <summary>
    /// The pause-axis reason THIS service writes — the one freeze whose owner is a human rather than a
    /// mechanism. Named rather than repeated as a literal because
    /// <see cref="IsReleasableFreeze"/> discriminates on it: a jail carrying this reason is releasable by
    /// Resume whether or not a <c>HumanPaused</c> row survived, which is what makes the crash window
    /// between the two writes recoverable instead of durable.
    /// </summary>
    internal const string HumanPausedFrozenReason = "a human paused it";

    public async Task<(bool Done, string Reason)> UnpauseAsync(string agentId, CancellationToken ct)
    {
        if (_killGate.IsFrozen)
        {
            return (false, "the kill switch is engaged — resume the queue first");
        }

        if (_ledger.HasMachineHold(agentId))
        {
            // The keep-alive rebase is mid-critical-section under this jail's pause — seconds. The
            // refusal self-clears; overriding it would break the rebase open mid-write.
            return (false, "the daemon is briefly holding this agent for a queue update — try again in a moment");
        }

        var sessions = _store.FindAll(agentId).Where(s => !string.IsNullOrEmpty(s.ContainerId)).ToList();
        if (sessions.Count == 0)
        {
            return (false, "this agent has no live jail to resume");
        }

        if (!_ledger.IsHumanPaused(agentId) && !IsReleasableFreeze(sessions))
        {
            // Someone inside the app owns this freeze and has its own release: the kill switch's Resume,
            // the conflict card's Resolve/Abort/hand-back, the keep-alive rebase's own token. Thawing it
            // from here would wake a jail behind the owner's back — a rebase reopened mid-write, or a
            // parked conflict handed back with no permit, so its next publish is refused forever.
            return (false, "this agent isn't human-paused");
        }

        // Clear the flag from MEMORY before unpausing: a machine hold that starts mid-loop must see the
        // human's intent already withdrawn, or its resume would re-freeze an agent the human just woke.
        // The DURABLE clear waits until the engine has actually thawed every jail — see ForgetHumanPause.
        _ledger.ForgetHumanPause(agentId);

        foreach (var session in sessions)
        {
            try
            {
                if (await IsPausedAsync(session.ContainerId!, ct).ConfigureAwait(false))
                {
                    await _environment.Sandboxes.UnpauseAsync(session.ContainerId!, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Audit F17. The clear above is correct WHILE the unpause runs and wrong once it has
                // failed: the jail is still frozen, and with the flag gone the very next Unpause answered
                // "this agent isn't human-paused" about a jail this operation had just failed to thaw.
                // The documented workaround was "pause it again, then unpause" — i.e. re-assert by hand
                // the fact this line drops. Putting it back is what makes a retry a retry; the mid-loop
                // window the clear exists for has closed by the time we are here.
                _ledger.MarkHumanPaused(agentId);
                _log.LogError(ex, "unpause: docker unpause failed agent={Agent} container={Container}",
                    agentId, session.ContainerId);
                return (false,
                    $"couldn't resume the jail ({ex.Message}) — it is still paused, so press Resume again");
            }

            _store.MarkState(session.Key, "Working", "Resumed by you.");
            _store.MarkFrozen(session.Key, null);
        }

        // Only now. Every jail behind this id is thawed and every axis is cleared, so the durable row can
        // stop saying a human is holding this agent. A daemon that dies anywhere above leaves the row
        // intact, which is what lets the next one recognise the freeze and offer Resume again.
        _ledger.PersistHumanPauseCleared(agentId);

        _log.LogInformation("pause: agent={Agent} resumed by human ({Count} session(s))", agentId, sessions.Count);
        return (true, "");
    }

    /// <summary>
    /// True when at least one session behind this id carries a freeze THIS service is entitled to release,
    /// and no session carries one somebody else owns. Two reasons qualify, and nothing else does.
    ///
    /// <para><b>The engine's own reading</b> (<see cref="AgentSessionReconciler.DockerPausedFrozenReason"/>)
    /// — audit F3's last exit. Two shapes land here: the jail a human paused before a daemon that predates
    /// the durable ledger died, and the jail somebody paused with a raw <c>docker pause</c>. The reconciler
    /// adopts both as Paused and writes exactly this reason; before this, Unpause answered "this agent
    /// isn't human-paused" for both, leaving <c>docker unpause</c> from a terminal as the only way back.
    /// </para>
    ///
    /// <para><b>This service's own reason</b> (<see cref="HumanPausedFrozenReason"/>) — the crash window
    /// between the two writes a human pause performs, closed from the reading side as well as the writing
    /// side. The axis is the daemon's record that a HUMAN froze this jail; a <c>HumanPaused</c> row that
    /// did not survive alongside it does not make the freeze somebody else's, and refusing on that
    /// combination is precisely the "frozen, refuses every exit" wedge the ledger exists to end.</para>
    ///
    /// <para>A session that is not frozen at all is not evidence either way and does not veto: a pause
    /// that failed partway leaves exactly that mix, and it is the sessions that DID freeze which need the
    /// exit. Anything else — the kill switch's Resume, the conflict card's Resolve/Abort/hand-back, the
    /// keep-alive rebase's token — is refused, because its owner's release does more than call unpause.
    /// The reason string is the discriminator rather than the state word because the word is
    /// <c>Paused</c> for every freeze in the system; only the axis records WHO.</para>
    /// </summary>
    private bool IsReleasableFreeze(IReadOnlyList<AgentSession> sessions)
    {
        var releasable = false;
        foreach (var session in sessions)
        {
            var reason = _store.FrozenReason(session.Key);
            if (string.IsNullOrEmpty(reason))
            {
                continue; // not frozen — there is no owner here to refuse on behalf of
            }

            if (string.Equals(reason, AgentSessionReconciler.DockerPausedFrozenReason, StringComparison.Ordinal)
                || string.Equals(reason, HumanPausedFrozenReason, StringComparison.Ordinal))
            {
                releasable = true;
                continue;
            }

            return false; // an in-app owner claims this freeze; its release does more than call unpause
        }

        return releasable;
    }

    private async Task<bool> IsPausedAsync(string containerId, CancellationToken ct)
    {
        try
        {
            return await _environment.Sandboxes.IsPausedAsync(containerId, ct).ConfigureAwait(false);
        }
        catch
        {
            // Unknown state: let the pause/unpause itself be the arbiter (its error will be honest).
            return false;
        }
    }
}
