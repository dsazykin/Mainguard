using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using Mainguard.Git.Audit;

namespace Mainguard.Server.Runtime;

/// <summary>
/// A daemon session record. Opaque to the client — only <see cref="Id"/>/<see cref="Kind"/>/
/// <see cref="State"/>/<see cref="Role"/> cross the wire (G-14); the <see cref="ContainerId"/>/
/// <see cref="RepoHash"/> are daemon-side only (never serialized), carried so <c>StopAgent</c> can
/// tear the real jail + worktree down. <see cref="Role"/> is the orchestration role
/// (<see cref="Mainguard.Agents.Agents.AgentRoles"/>): "", "coordinator", or "managed".
/// </summary>
/// <param name="ParentAgentId">MG-37: the coordinator that spawned this agent, or null for an
/// operator-initiated spawn. Daemon-side only (never serialized). This is what lets the in-jail
/// <c>mainguard-agent list</c> be scoped to the caller's own workers instead of every session on the
/// daemon.</param>
public sealed record AgentSession(
    string Id, string Kind, string State, string? ContainerId = null, string? RepoHash = null,
    string Role = "", string? ParentAgentId = null);

/// <summary>A single agent-stream delta the store fans out to subscribers. <paramref name="Reason"/>
/// is the human-readable why of a state transition when there is one (e.g. a Dead CLI's exit tail).</summary>
public sealed record AgentDelta(string AgentId, ulong Seq, string Kind, string Payload, string? Reason = null);

/// <summary>
/// The in-memory daemon agent registry + event fan-out. This is host state, not gRPC
/// wiring: the gRPC service classes only validate + dispatch here (keeping business
/// logic out of the transport layer, per the P2-02 rejection trigger). The real
/// container/PTY-backed lifecycle replaces this in P2-06/P2-09 behind the same shape.
///
/// The event stream is <b>snapshot-then-deltas</b>: every new subscription receives a
/// fresh snapshot of the current sessions first, so a reconnecting client resyncs with
/// no server-side cursor (edge row: "daemon restart mid-stream ... resumes").
/// </summary>
public sealed class AgentSessionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AgentSession> _sessions = new();
    private readonly List<Channel<AgentDelta>> _subscribers = new();
    private readonly IAuditLog _audit;
    private ulong _seq;

    public AgentSessionStore(IAuditLog audit)
    {
        _audit = audit;
    }

    public IReadOnlyList<AgentSession> List()
    {
        lock (_gate)
        {
            return _sessions.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
        }
    }

    public AgentSession Spawn(string kind, string role = "", string? parentAgentId = null)
    {
        var session = new AgentSession(
            Guid.NewGuid().ToString("N"), kind, "Starting", Role: role ?? "", ParentAgentId: parentAgentId);
        lock (_gate)
        {
            _sessions[session.Id] = session;
        }

        _audit.Append(new AuditEvent("spawn", new Dictionary<string, string>
        {
            ["agent_id"] = session.Id,
            ["agent_kind"] = kind,
            ["role"] = session.Role,
        }));
        Broadcast(new AgentDelta(session.Id, NextSeq(), "state", session.State));
        return session;
    }

    /// <summary>
    /// Reflects a lifecycle/gateway state change for an agent (e.g. <c>RateLimited</c>, <c>Paused</c>,
    /// <c>Working</c>) in <see cref="List"/> metadata and streams it as a state delta. This is the sink
    /// the P2-09 real <c>IAgentSupervisor</c> drives so a 429/budget/yield pause becomes a visible state.
    /// A no-op for an unknown agent.
    /// </summary>
    public void MarkState(string agentId, string state, string? reason)
    {
        bool changed;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(agentId, out var session))
            {
                return;
            }

            changed = !string.Equals(session.State, state, StringComparison.Ordinal);
            _sessions[agentId] = session with { State = state };
        }

        if (changed)
        {
            Broadcast(new AgentDelta(agentId, NextSeq(), "state", state, reason));
        }
    }

    /// <summary>Look up a live session (daemon-side; carries the container id/repo hash for teardown). Null if unknown.</summary>
    public AgentSession? Find(string agentId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(agentId, out var session) ? session : null;
        }
    }

    /// <summary>
    /// Bind a real sandbox to a spawned session: record the container id + repo hash (daemon-side only)
    /// and flip the state to <c>Working</c>. This is what turns a session record into a live, jailed agent
    /// once the P2-06/P2-07 spawn chain has provisioned the worktree and started the hardened container.
    /// </summary>
    public void AttachSandbox(string agentId, string containerId, string repoHash)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(agentId, out var session))
            {
                return;
            }

            _sessions[agentId] = session with { State = "Working", ContainerId = containerId, RepoHash = repoHash };
        }

        _audit.Append(new AuditEvent("sandbox_attach", new Dictionary<string, string>
        {
            ["agent_id"] = agentId,
            ["container_id"] = containerId,
            ["repo"] = repoHash,
        }));
        Broadcast(new AgentDelta(agentId, NextSeq(), "state", "Working"));
    }

    public bool Stop(string agentId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _sessions.Remove(agentId);
        }

        if (removed)
        {
            _audit.Append(new AuditEvent("stop", new Dictionary<string, string> { ["agent_id"] = agentId }));
            Broadcast(new AgentDelta(agentId, NextSeq(), "state", "Stopped"));
        }

        return removed;
    }

    /// <summary>
    /// Opens a subscription. The returned reader yields one snapshot delta
    /// (<see cref="AgentDelta.Kind"/> == "snapshot") first, then live deltas. Dispose
    /// via <paramref name="unsubscribe"/> when the stream ends.
    /// </summary>
    /// <summary>Per-subscriber buffer cap (audit fix #15): a stalled client must not grow daemon
    /// memory without bound. On overflow the subscription is COMPLETED (not silently thinned — a
    /// dropped delta would desync the client forever); the client's stream ends and its normal
    /// reconnect gets a fresh snapshot.</summary>
    internal const int SubscriberBufferCapacity = 4096;

    public ChannelReader<AgentDelta> Subscribe(out Action unsubscribe)
    {
        var channel = Channel.CreateBounded<AgentDelta>(new BoundedChannelOptions(SubscriberBufferCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait, // Wait + TryWrite == "report full", never block
        });

        lock (_gate)
        {
            _subscribers.Add(channel);
            var snapshot = string.Join(",", _sessions.Values.Select(s => $"{s.Id}:{s.Kind}:{s.State}:{s.Role}"));
            channel.Writer.TryWrite(new AgentDelta(string.Empty, NextSeqLocked(), "snapshot", snapshot));
        }

        unsubscribe = () =>
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }
            channel.Writer.TryComplete();
        };

        return channel.Reader;
    }

    private void Broadcast(AgentDelta delta)
    {
        lock (_gate)
        {
            for (var i = _subscribers.Count - 1; i >= 0; i--)
            {
                if (!_subscribers[i].Writer.TryWrite(delta))
                {
                    // Buffer full = a stalled/dead client. End its stream (it resyncs via a fresh
                    // snapshot on reconnect) rather than buffering forever or dropping deltas.
                    _subscribers[i].Writer.TryComplete();
                    _subscribers.RemoveAt(i);
                }
            }
        }
    }

    private ulong NextSeq()
    {
        lock (_gate)
        {
            return NextSeqLocked();
        }
    }

    private ulong NextSeqLocked() => ++_seq;
}
