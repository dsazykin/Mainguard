using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>The outcome of a boot reattach reconcile (asserted by tests, surfaced to the daemon).</summary>
public sealed record LeaderReconcileReport(
    IReadOnlyList<string> Reattached,
    IReadOnlyList<string> Reaped);

/// <summary>
/// P2-09 session leader (contract §2.3). A long-lived owner of the per-agent PTY file descriptors,
/// intended to run <b>outside the daemon's lifetime</b> so a daemon <c>kill -9</c> does not tear down
/// the agents' terminals; the daemon talks to it (attach/detach/spawn/kill) and, on boot, reattaches
/// through the durable <see cref="LeaderRegistry"/> — leader-owned state, no daemon-side pidfiles.
///
/// <para><b>Input pause is the leader's job:</b> <see cref="PauseInput"/>/<see cref="ResumeInput"/> gate
/// keystrokes toward the CLI. They are what the real <c>IAgentSupervisor</c> drives for the P2-08 429 /
/// budget pause and the P2-09 yield window; the terminal input path consults <see cref="IsPaused"/>
/// before forwarding bytes.</para>
///
/// <para><b>Boot reconcile</b> (<see cref="Reattach"/>) runs after the container reconciler and resolves
/// every mismatch toward Docker truth: a registry session whose container is not live is reaped.</para>
///
/// <para>Registration carries an opaque handle (the PTY session) plus a killer callback, so this Core
/// type owns no <c>Porta.Pty</c> dependency directly and stays unit-testable; the daemon supplies the
/// real <see cref="PtySession"/> and its <c>Kill</c>.</para>
/// </summary>
public sealed class SessionLeader
{
    private readonly LeaderRegistry _registry;
    private readonly ConcurrentDictionary<string, LeaderEntry> _sessions = new(StringComparer.Ordinal);

    public SessionLeader(LeaderRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>The leader-owned durable registry (the daemon reads this on boot).</summary>
    public LeaderRegistry Registry => _registry;

    /// <summary>
    /// Records ownership of an agent's PTY session and persists it to the registry. <paramref name="kill"/>
    /// terminates the underlying PTY (the daemon passes <c>PtySession.Kill</c>).
    /// </summary>
    public void Register(LeaderSession session, Action? kill = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sessions[session.AgentId] = new LeaderEntry(session, kill);
        _registry.Upsert(session);
    }

    /// <summary>Gates keystrokes toward the agent's CLI (429 / budget / yield pause). Idempotent.</summary>
    public void PauseInput(string agentId)
    {
        if (_sessions.TryGetValue(agentId, out var entry))
        {
            entry.Paused = true;
        }
    }

    /// <summary>Resumes keystroke forwarding. Idempotent.</summary>
    public void ResumeInput(string agentId)
    {
        if (_sessions.TryGetValue(agentId, out var entry))
        {
            entry.Paused = false;
        }
    }

    /// <summary>True while the agent's input is paused (the terminal path checks this before forwarding).</summary>
    public bool IsPaused(string agentId) =>
        _sessions.TryGetValue(agentId, out var entry) && entry.Paused;

    /// <summary>True iff the leader currently owns a session for the agent.</summary>
    public bool HasSession(string agentId) => _sessions.ContainsKey(agentId);

    /// <summary>Kills the agent's PTY and drops it from the leader + registry (idempotent).</summary>
    public void Kill(string agentId)
    {
        if (_sessions.TryRemove(agentId, out var entry))
        {
            try
            {
                entry.Kill?.Invoke();
            }
            catch (Exception)
            {
                // Best-effort reap; the child may already be gone.
            }
        }

        _registry.Remove(agentId);
    }

    /// <summary>
    /// Boot reconcile: given the live container set (from the P2-08 container reconciler), reattach every
    /// registry session whose container is still live and reap the rest (Docker-as-truth). Mutates the
    /// registry to match. Reattach is the leader → PTY step, ordered after the container step.
    /// </summary>
    public LeaderReconcileReport Reattach(IReadOnlyCollection<AgentContainerState> liveContainers)
    {
        ArgumentNullException.ThrowIfNull(liveContainers);

        // Live, not Running: a PAUSED jail is present, and reaping its leader session kills the PTY of an
        // agent the user (or the kill switch) deliberately froze. See AgentContainerState.Live.
        var liveContainerIds = new HashSet<string>(
            liveContainers.Where(c => c.Live).Select(c => c.ContainerId), StringComparer.Ordinal);
        var liveAgentIds = new HashSet<string>(
            liveContainers.Where(c => c.Live).Select(c => c.AgentId), StringComparer.Ordinal);

        var reattached = new List<string>();
        var reaped = new List<string>();
        var survivors = new List<LeaderSession>();

        foreach (var session in _registry.Load())
        {
            var alive = liveContainerIds.Contains(session.ContainerId) || liveAgentIds.Contains(session.AgentId);
            if (alive)
            {
                reattached.Add(session.AgentId);
                survivors.Add(session);
            }
            else
            {
                // Container dead ⇒ reap the leader session; kill any in-proc PTY we still hold.
                if (_sessions.TryRemove(session.AgentId, out var entry))
                {
                    try
                    {
                        entry.Kill?.Invoke();
                    }
                    catch (Exception)
                    {
                        // Best-effort.
                    }
                }

                reaped.Add(session.AgentId);
            }
        }

        _registry.Save(survivors);
        return new LeaderReconcileReport(reattached, reaped);
    }

    private sealed class LeaderEntry
    {
        public LeaderEntry(LeaderSession session, Action? kill)
        {
            Session = session;
            Kill = kill;
        }

        public LeaderSession Session { get; }

        public Action? Kill { get; }

        public volatile bool Paused;
    }
}
