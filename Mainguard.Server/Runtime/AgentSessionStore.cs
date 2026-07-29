using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using Mainguard.Git.Audit;

namespace Mainguard.Server.Runtime;

/// <summary>
/// The identity of a daemon session: the repository it belongs to <b>and</b> the agent id inside it.
///
/// <para><b>Why the repo is part of the identity.</b> An agent id is only unique <i>within</i> a repo.
/// Minted GUIDs happen to be unique everywhere, but the external-PR intake names its sessions
/// <c>pr-&lt;n&gt;</c> after the pull request number, and two subscribed repositories that each have a
/// pull request #7 both want <c>pr-7</c>. Everything downstream of the session is already repo-scoped —
/// the container name (<c>ContainerSpecBuilder.ContainerName(repoHash, agentId)</c>), its
/// <c>mainguard.repo</c>/<c>mainguard.agent</c> label pair, the MG-36 network segment derived from that
/// container name, the worktree, the <c>caches/&lt;repoHash&gt;/&lt;agentId&gt;</c> package cache and the
/// per-repo merge queue — so the store keying by id alone was the one place in the chain where two repos
/// collided.</para>
/// </summary>
/// <param name="RepoHash">The repo handle the session belongs to. The empty string is a legitimate
/// scope of its own (a session recorded before any repo is known), never a wildcard.</param>
public readonly record struct AgentSessionKey(string RepoHash, string AgentId);

/// <summary>
/// A daemon session record. Opaque to the client — only <see cref="Id"/>/<see cref="Kind"/>/
/// <see cref="State"/>/<see cref="Role"/> cross the wire (G-14); the <see cref="ContainerId"/>/
/// <see cref="RepoHash"/> are daemon-side only (never serialized), carried so <c>StopAgent</c> can
/// tear the real jail + worktree down. <see cref="Role"/> is the orchestration role
/// (<see cref="Mainguard.Agents.Agents.AgentRoles"/>): "", "coordinator", or "managed".
///
/// <para><see cref="Id"/> is NOT the session's identity — <see cref="Key"/> is. See
/// <see cref="AgentSessionKey"/>.</para>
/// </summary>
/// <param name="ParentAgentId">MG-37: the coordinator that spawned this agent, or null for an
/// operator-initiated spawn. Daemon-side only (never serialized). This is what lets the in-jail
/// <c>mainguard-agent list</c> be scoped to the caller's own workers instead of every session on the
/// daemon.</param>
public sealed record AgentSession(
    string Id, string Kind, string State, string? ContainerId = null, string? RepoHash = null,
    string Role = "", string? ParentAgentId = null)
{
    /// <summary>This session's identity in the store: (repo, agent id). Never just the id.</summary>
    public AgentSessionKey Key => new(RepoHash ?? string.Empty, Id);
}

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
    private readonly Dictionary<AgentSessionKey, AgentSession> _sessions = new();
    private readonly List<Channel<AgentDelta>> _subscribers = new();
    private readonly IAuditLog _audit;
    private ulong _seq;

    public AgentSessionStore(IAuditLog audit)
    {
        _audit = audit;
    }

    /// <summary>Every live session on the daemon, across every repo. Ordered by (id, repo) so a repeated
    /// id is deterministic rather than dictionary-ordered.</summary>
    public IReadOnlyList<AgentSession> List()
    {
        lock (_gate)
        {
            return _sessions.Values
                .OrderBy(s => s.Id, StringComparer.Ordinal)
                .ThenBy(s => s.RepoHash ?? string.Empty, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <param name="agentId">
    /// An explicit session id, or null for the usual minted GUID. The ONE caller that names its own id is
    /// the external-PR intake, whose entries must be <c>pr-&lt;n&gt;</c>: that id is simultaneously the
    /// worktree name, the <c>agent/pr-&lt;n&gt;</c> branch, the jail's <c>mainguard.agent</c> label, the
    /// per-agent package-cache directory and the merge-queue key, so the id has to be derivable from the
    /// pull request rather than minted. Before this the store could only mint GUIDs, which is why an
    /// intake'd pull request could not be given a jail at all.
    /// </param>
    /// <param name="repoHash">
    /// The repo this session belongs to — <b>part of its identity</b>, not metadata. Supplied at spawn
    /// (the spawning caller always knows it) rather than waiting for <see cref="AttachSandbox"/>, because
    /// the id alone cannot tell two repos' <c>pr-&lt;n&gt;</c> sessions apart. Null/empty is the repo-less
    /// scope, which is a scope like any other and not a wildcard.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// This (repo, id) pair is already live. Deliberately loud rather than a silent overwrite: replacing
    /// the record would drop the running jail's container id on the floor, leaking the container, its
    /// MG-36 network segment and its package-cache lease with no way left to find them. The SAME id under
    /// a DIFFERENT repo is not a duplicate and is admitted — that is the point of the key.
    /// </exception>
    public AgentSession Spawn(
        string kind, string role = "", string? parentAgentId = null, string? agentId = null,
        string? repoHash = null)
    {
        var scope = repoHash ?? string.Empty;
        var session = new AgentSession(
            string.IsNullOrWhiteSpace(agentId) ? Guid.NewGuid().ToString("N") : agentId,
            kind, "Starting", RepoHash: scope, Role: role ?? "", ParentAgentId: parentAgentId);
        lock (_gate)
        {
            if (_sessions.ContainsKey(session.Key))
            {
                throw new InvalidOperationException(
                    $"An agent session with id '{session.Id}' is already live in repo '{scope}'.");
            }

            _sessions[session.Key] = session;
        }

        _audit.Append(new AuditEvent("spawn", new Dictionary<string, string>
        {
            ["agent_id"] = session.Id,
            ["repo"] = scope,
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
    /// A no-op for an unknown session.
    /// </summary>
    public void MarkState(AgentSessionKey key, string state, string? reason)
    {
        bool changed;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var session))
            {
                return;
            }

            changed = !string.Equals(session.State, state, StringComparison.Ordinal);
            _sessions[key] = session with { State = state };
        }

        if (changed)
        {
            Broadcast(new AgentDelta(key.AgentId, NextSeq(), "state", state, reason));
        }
    }

    /// <summary>
    /// The id-only entry point, for the daemon subsystems that are still keyed by agent id alone (the PTY
    /// binder's exit watcher, the P2-09 supervisor). Applies to the ONE session with this id and is a
    /// no-op when two repos hold it — an ambiguous id must not have a state word guessed for it. Prefer
    /// <see cref="MarkState(AgentSessionKey,string,string?)"/> wherever the repo is in hand.
    /// </summary>
    public void MarkState(string agentId, string state, string? reason)
    {
        if (FindUnique(agentId) is { } session)
        {
            MarkState(session.Key, state, reason);
        }
    }

    /// <summary>Look up a live session by its full identity (daemon-side; carries the container id for
    /// teardown). Null if this repo has no session under that id — including when ANOTHER repo does,
    /// which is exactly the isolation this key exists for.</summary>
    public AgentSession? Find(AgentSessionKey key)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(key, out var session) ? session : null;
        }
    }

    /// <summary>Convenience for <see cref="Find(AgentSessionKey)"/>.</summary>
    public AgentSession? Find(string repoHash, string agentId) => Find(new AgentSessionKey(repoHash, agentId));

    /// <summary>
    /// The id-only lookup kept for the daemon-global entry points whose caller has no repo to give (the
    /// <c>StopAgent</c>/<c>HarvestAgentCredentials</c> RPCs, the coordinator IPC shim's own session).
    /// Resolves to the one session with this id, or null when there is none <b>or more than one</b> —
    /// never an arbitrary pick, because acting on the wrong repo's jail is unrecoverable. Every id a
    /// caller can reach this way is a minted GUID today; only intake-named <c>pr-&lt;n&gt;</c> sessions
    /// can repeat, and every path that handles those carries the repo hash.
    /// </summary>
    public AgentSession? Find(string agentId) => FindUnique(agentId);

    /// <summary>Every session carrying this agent id, across all repos (usually zero or one). The
    /// fan-out the kill switch needs: an emergency stop must contain BOTH repos' <c>pr-7</c>, not
    /// whichever one it happened to resolve.</summary>
    public IReadOnlyList<AgentSession> FindAll(string agentId)
    {
        lock (_gate)
        {
            return _sessions.Values
                .Where(s => string.Equals(s.Id, agentId, StringComparison.Ordinal))
                .OrderBy(s => s.RepoHash ?? string.Empty, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private AgentSession? FindUnique(string agentId)
    {
        lock (_gate)
        {
            AgentSession? only = null;
            foreach (var session in _sessions.Values)
            {
                if (!string.Equals(session.Id, agentId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (only is not null)
                {
                    return null; // ambiguous — two repos hold this id; the caller must name one.
                }

                only = session;
            }

            return only;
        }
    }

    /// <summary>
    /// Bind a real sandbox to a spawned session: record the container id (daemon-side only) and flip the
    /// state to <c>Working</c>. This is what turns a session record into a live, jailed agent once the
    /// P2-06/P2-07 spawn chain has provisioned the worktree and started the hardened container.
    ///
    /// <para>The repo hash is no longer written here — it is part of the key the session was spawned
    /// under, so a mismatched repo resolves to no session at all rather than silently re-homing one.</para>
    /// </summary>
    public void AttachSandbox(AgentSessionKey key, string containerId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var session))
            {
                return;
            }

            _sessions[key] = session with { State = "Working", ContainerId = containerId };
        }

        _audit.Append(new AuditEvent("sandbox_attach", new Dictionary<string, string>
        {
            ["agent_id"] = key.AgentId,
            ["container_id"] = containerId,
            ["repo"] = key.RepoHash,
        }));
        Broadcast(new AgentDelta(key.AgentId, NextSeq(), "state", "Working"));
    }

    /// <summary>Convenience for <see cref="AttachSandbox(AgentSessionKey,string)"/>: the repo hash names
    /// the session rather than being written into it.</summary>
    public void AttachSandbox(string agentId, string containerId, string repoHash)
        => AttachSandbox(new AgentSessionKey(repoHash, agentId), containerId);

    /// <summary>Removes one session by its full identity. Another repo's session under the same agent id
    /// is untouched.</summary>
    public bool Stop(AgentSessionKey key)
    {
        bool removed;
        lock (_gate)
        {
            removed = _sessions.Remove(key);
        }

        if (removed)
        {
            _audit.Append(new AuditEvent("stop", new Dictionary<string, string>
            {
                ["agent_id"] = key.AgentId,
                ["repo"] = key.RepoHash,
            }));
            Broadcast(new AgentDelta(key.AgentId, NextSeq(), "state", "Stopped"));
        }

        return removed;
    }

    /// <summary>The id-only stop, for the daemon-global entry points. Stops the one session with this id;
    /// stops NOTHING and returns false when two repos hold it, because stopping the wrong repo's jail
    /// tears down a running container that nothing else will restart.</summary>
    public bool Stop(string agentId) => FindUnique(agentId) is { } session && Stop(session.Key);

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
