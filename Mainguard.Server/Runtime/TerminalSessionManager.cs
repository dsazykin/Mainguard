using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;

namespace Mainguard.Server.Runtime;

/// <summary>
/// Owns the mapping from an agent <b>session identity</b> to its live terminal session. Two
/// registration shapes:
///
/// <para><b>Bound sessions (the real agent path):</b> when a spawn launches an installed CLI in its
/// jail, <see cref="AgentCliBinder"/> registers the resulting long-lived
/// <see cref="BoundTerminalSession"/> here via <see cref="Bind"/>. Attaches subscribe to the live
/// session (replay + fanout) and a detach never kills the CLI —
/// <see cref="Services.TerminalGrpcService"/> streams the REAL CLI, not the echo.</para>
///
/// <para><b>Per-attach factory (legacy/test shape):</b> a <see cref="PtySession"/> factory spawns a
/// fresh child per attach that dies on detach — the shape the TI-P2-03 wiring tests inject. With
/// neither a bound session nor a factory, the attach falls back to the P2-02 echo.</para>
///
/// <para><b>Why the key is <see cref="AgentSessionKey"/> and not the agent id.</b> An agent id is
/// unique only <i>inside</i> a repository — the external-PR intake names its workers <c>pr-&lt;n&gt;</c>
/// after the pull-request number, so two subscribed repos that each have a pull request #7 both run a
/// <c>pr-7</c>. Keyed by id alone the two collided outright, and not benignly: <see cref="Bind"/>
/// replaces a same-key registration and <b>disposes the previous session</b>, so repo B's worker
/// binding its CLI <i>killed the live CLI process of repo A's still-running worker</i> — silently, since
/// the exit watcher's id-only <c>MarkState</c> is a deliberate no-op on an ambiguous id. Every attach for
/// <c>pr-7</c> afterwards streamed repo B's CLI output to repo A's operator, and
/// <see cref="MarkBindPending"/>/<see cref="ClearBindPending"/> cleared each other's flags. Same identity
/// as <see cref="AgentSessionStore"/>, the jail's <c>mainguard.repo</c>/<c>mainguard.agent</c> label pair
/// and <see cref="Mainguard.Agents.Agents.SwarmReconciler"/>.</para>
///
/// <para>The repo-less entry points (the <c>TerminalService.Attach</c> / <c>GetScrollback</c> RPCs,
/// whose wire contract carries only an agent id) resolve id → key <b>unique-or-nothing</b>, exactly as
/// <see cref="AgentSessionStore.Find(string)"/> does: an id two repos hold resolves to NO session and the
/// attach degrades to echo, rather than being handed an arbitrary repo's live CLI.</para>
///
/// <para>Kept as host state separate from the transport (the P2-02 rejection trigger: no business
/// logic in the gRPC classes).</para>
/// </summary>
public sealed class TerminalSessionManager : IDisposable
{
    private readonly Func<string, PtySession>? _factory;
    private readonly ConcurrentDictionary<AgentSessionKey, BoundTerminalSession> _bound = new();
    // Agents whose spawn is in-flight and WILL bind a CLI shortly (container start + docker-exec-under-PTY
    // takes a few seconds). A client attaches the instant the agent appears ("Starting"), which is BEFORE
    // that bind — so the attach waits on this rather than latching into echo (the attach-before-bind race).
    private readonly ConcurrentDictionary<AgentSessionKey, byte> _bindPending = new();

    /// <summary>How long an attach waits for a pending bind before giving up (echo). The bind normally
    /// lands ~5 s after spawn; the ceiling covers a cold container start. Settable low by tests.</summary>
    public static TimeSpan BindWaitTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Poll cadence while waiting for a pending bind (a cheap concurrent-dictionary read).</summary>
    public static TimeSpan BindWaitPollInterval { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Default registration: no per-attach factory; only bound sessions stream a real PTY.</summary>
    public TerminalSessionManager()
    {
    }

    /// <summary>PTY-backed registration: <paramref name="factory"/> spawns the session for an agent id.</summary>
    public TerminalSessionManager(Func<string, PtySession> factory)
    {
        _factory = factory;
    }

    /// <summary>Whether a per-attach PTY factory is configured.</summary>
    public bool CanSpawn => _factory is not null;

    /// <summary>Spawns a per-attach PTY session for <paramref name="agentId"/>, or <c>null</c> when no factory is configured.</summary>
    public PtySession? Create(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("An agent id is required.", nameof(agentId));
        }

        return _factory?.Invoke(agentId);
    }

    /// <summary>
    /// Registers this session's long-lived CLI. A previous binding under the SAME
    /// <see cref="AgentSessionKey"/> (a re-spawn into a reused jail) is killed and replaced — one CLI
    /// per (repo, agent id). Another repository's agent of the same id is a different session and is
    /// left running; before the key carried the repo, this dispose reached across and killed it.
    /// </summary>
    public void Bind(AgentSessionKey key, BoundTerminalSession session)
    {
        if (string.IsNullOrWhiteSpace(key.AgentId))
        {
            throw new ArgumentException("An agent id is required.", nameof(key));
        }

        ArgumentNullException.ThrowIfNull(session);
        var previous = _bound.TryGetValue(key, out var existing) ? existing : null;
        _bound[key] = session;
        _bindPending.TryRemove(key, out _); // the bind we were waiting for landed
        previous?.Dispose();
    }

    /// <summary>This session's live bound CLI, or null when none is registered. Another repo's agent of
    /// the same id is never returned — that isolation is the whole point of the key.</summary>
    public BoundTerminalSession? TryGetBound(AgentSessionKey key) =>
        _bound.TryGetValue(key, out var session) ? session : null;

    /// <summary>
    /// The id-only lookup, for the repo-less entry points: the <c>TerminalService.Attach</c> and
    /// <c>GetScrollback</c> RPCs, whose wire contract carries an agent id and no repo. Resolves to the
    /// ONE session with this id, or null when there is none <b>or more than one</b> — never an arbitrary
    /// pick, because handing an operator another repository's live CLI stream is a cross-repo disclosure.
    /// An ambiguous id degrades the attach to echo, which is wrong-but-inert rather than wrong-and-live.
    /// </summary>
    public BoundTerminalSession? TryGetBound(string agentId) =>
        TryResolveUnique(agentId, out var key) ? TryGetBound(key) : null;

    /// <summary>Records that this session's spawn is in-flight and a CLI bind is expected —
    /// set at session creation, so an attach arriving during the spawn waits for the bind. Cleared by
    /// <see cref="Bind"/> (bind landed) or <see cref="ClearBindPending"/> (session-only / bind failed).</summary>
    public void MarkBindPending(AgentSessionKey key)
    {
        if (!string.IsNullOrWhiteSpace(key.AgentId)) _bindPending[key] = 0;
    }

    /// <summary>Clears the pending-bind flag: this agent is session-only (no CLI) or its bind failed, so an
    /// attach should stop waiting and fall back to echo/PTY. Idempotent. Scoped to one repo's session, so
    /// a failed spawn in repo B no longer drops repo A's in-flight <c>pr-7</c> into echo.</summary>
    public void ClearBindPending(AgentSessionKey key) => _bindPending.TryRemove(key, out _);

    /// <summary>Whether a CLI bind is still expected for this session (spawn in-flight).</summary>
    public bool IsBindPending(AgentSessionKey key) => _bindPending.ContainsKey(key);

    /// <summary>The id-only form for the repo-less attach path — unique-or-nothing, exactly like
    /// <see cref="TryGetBound(string)"/>. An id two repos hold reports "no bind pending", so the attach
    /// falls through to echo instead of waiting on (and then streaming) a stranger's session.</summary>
    public bool IsBindPending(string agentId) =>
        TryResolveUnique(agentId, out var key) && IsBindPending(key);

    /// <summary>
    /// Waits for this session's CLI to bind, returning it — the fix for the attach-before-bind race that
    /// left a coordinator terminal in echo. Returns as soon as a bound session appears; returns null when
    /// the pending-bind flag clears without one (session-only / bind failed), the
    /// <see cref="BindWaitTimeout"/> elapses, or the caller cancels.
    /// </summary>
    public Task<BoundTerminalSession?> WaitForBoundAsync(AgentSessionKey key, CancellationToken ct) =>
        WaitForBoundCoreAsync(() => TryGetBound(key), () => IsBindPending(key), ct);

    /// <summary>The id-only wait, for the repo-less attach path. Re-resolves the id on every poll, so a
    /// second repo spawning the same id mid-wait makes the id ambiguous and ends the wait in echo rather
    /// than binding the attach to whichever session happened to land.</summary>
    public Task<BoundTerminalSession?> WaitForBoundAsync(string agentId, CancellationToken ct) =>
        WaitForBoundCoreAsync(() => TryGetBound(agentId), () => IsBindPending(agentId), ct);

    private static async Task<BoundTerminalSession?> WaitForBoundCoreAsync(
        Func<BoundTerminalSession?> tryGetBound, Func<bool> isBindPending, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + BindWaitTimeout;
        while (true)
        {
            if (tryGetBound() is { } bound) return bound;
            if (!isBindPending()) return null;                        // no bind coming after all
            if (DateTimeOffset.UtcNow >= deadline) return null;       // took too long — degrade to echo

            try { await Task.Delay(BindWaitPollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }        // client detached
        }
    }

    /// <summary>Kills and unregisters this session's bound CLI (StopAgent / teardown). Idempotent.
    /// Deliberately has no id-only overload: killing a PTY is destructive and unrecoverable, so it must
    /// never be reachable with an identity that two repositories can answer to.</summary>
    public void Release(AgentSessionKey key)
    {
        if (_bound.TryRemove(key, out var session))
        {
            session.Dispose();
        }
    }

    /// <summary>Resolves a bare agent id to the one session that holds it, across bound and
    /// pending-bind registrations. False when no session or more than one carries the id.</summary>
    private bool TryResolveUnique(string? agentId, out AgentSessionKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(agentId))
        {
            return false;
        }

        var found = false;
        foreach (var candidate in _bound.Keys.Concat(_bindPending.Keys))
        {
            if (!string.Equals(candidate.AgentId, agentId, StringComparison.Ordinal))
            {
                continue;
            }

            if (found)
            {
                // A key can appear in both dictionaries during the bind hand-off; only a genuinely
                // DIFFERENT repo makes the id ambiguous.
                if (candidate.Equals(key)) continue;
                return false;
            }

            key = candidate;
            found = true;
        }

        return found;
    }

    public void Dispose()
    {
        foreach (var key in _bound.Keys)
        {
            Release(key);
        }
    }
}
