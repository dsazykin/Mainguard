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
        bool removed;
        lock (_gate)
        {
            removed = _humanPaused.Remove(agentId);
        }

        if (removed)
        {
            _persist.Update(agentId, r => r with { HumanPaused = false });
        }
    }

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
                _log.LogError(ex, "pause: docker pause failed agent={Agent} container={Container}",
                    agentId, session.ContainerId);
                return (false, $"couldn't pause the jail ({ex.Message})");
            }

            _store.MarkState(session.Key, "Paused", "Paused by you.");
            _store.MarkFrozen(session.Key, "a human paused it");
        }

        _ledger.MarkHumanPaused(agentId);
        _log.LogInformation("pause: agent={Agent} paused by human ({Count} session(s))", agentId, sessions.Count);
        return (true, "");
    }

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

        if (!_ledger.IsHumanPaused(agentId) && !IsUnclaimedEngineFreeze(sessions))
        {
            // Someone inside the app owns this freeze and has its own release: the kill switch's Resume,
            // the conflict card's Resolve/Abort/hand-back, the keep-alive rebase's own token. Thawing it
            // from here would wake a jail behind the owner's back — a rebase reopened mid-write, or a
            // parked conflict handed back with no permit, so its next publish is refused forever.
            return (false, "this agent isn't human-paused");
        }

        // Clear the flag BEFORE unpausing: a machine hold that starts mid-loop must see the human's
        // intent already withdrawn, or its resume would re-freeze an agent the human just woke.
        _ledger.ClearHumanPaused(agentId);

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

        _log.LogInformation("pause: agent={Agent} resumed by human ({Count} session(s))", agentId, sessions.Count);
        return (true, "");
    }

    /// <summary>
    /// True when EVERY session behind this id is frozen with the pause axis's engine-read reason and
    /// nothing else — i.e. the daemon knows the jail is paused and no ledger inside the app claims to
    /// have paused it.
    ///
    /// <para><b>Audit F3's last exit.</b> Two shapes land here: the jail a human paused before a daemon
    /// that predates the durable ledger died, and the jail somebody paused with a raw <c>docker pause</c>.
    /// The reconciler adopts both as Paused and writes exactly this reason — and before this, Unpause
    /// answered "this agent isn't human-paused" for both, which left <c>docker unpause</c> from a terminal
    /// as the only way back. The reason string is the discriminator rather than the state word because the
    /// word is <c>Paused</c> for every freeze in the system; only the axis records WHO.</para>
    /// </summary>
    private bool IsUnclaimedEngineFreeze(IReadOnlyList<AgentSession> sessions)
    {
        foreach (var session in sessions)
        {
            if (!string.Equals(
                    _store.FrozenReason(session.Key),
                    AgentSessionReconciler.DockerPausedFrozenReason,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return sessions.Count > 0;
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
