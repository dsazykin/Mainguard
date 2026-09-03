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
/// </summary>
public sealed class HumanPauseLedger : IPauseArbiter
{
    private readonly object _gate = new();
    private readonly HashSet<string> _humanPaused = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _machineHolds = new(StringComparer.Ordinal);

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
        lock (_gate)
        {
            _humanPaused.Add(agentId);
        }
    }

    public void ClearHumanPaused(string agentId)
    {
        lock (_gate)
        {
            _humanPaused.Remove(agentId);
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

        if (!_ledger.IsHumanPaused(agentId))
        {
            return (false, "this agent isn't human-paused");
        }

        var sessions = _store.FindAll(agentId).Where(s => !string.IsNullOrEmpty(s.ContainerId)).ToList();

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
                _log.LogError(ex, "unpause: docker unpause failed agent={Agent} container={Container}",
                    agentId, session.ContainerId);
                return (false, $"couldn't resume the jail ({ex.Message})");
            }

            _store.MarkState(session.Key, "Working", "Resumed by you.");
            _store.MarkFrozen(session.Key, null);
        }

        _log.LogInformation("pause: agent={Agent} resumed by human ({Count} session(s))", agentId, sessions.Count);
        return (true, "");
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
