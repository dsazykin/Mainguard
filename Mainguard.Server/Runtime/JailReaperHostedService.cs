using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Runtime;

/// <summary>
/// Stops the jails nobody will (owner decision, 2026-09-04): every
/// <see cref="CoordinatorLimits.JailReapSweepSeconds"/> it walks the live sessions and asks
/// <see cref="JailReapPolicy"/>. A reap is the ordinary <see cref="AgentSpawnService.StopAsync(AgentSessionKey, CancellationToken)"/>
/// — logins harvested, the branch published and kept where it carries work, the jail and worktree torn
/// down — so it reclaims memory without losing anything a human could still want.
/// </summary>
public sealed class JailReaperHostedService : IHostedService, IDisposable
{
    public const string ReapedEvent = "jail_reaped";

    private readonly AgentSessionStore _sessions;
    private readonly TerminalSessionManager _terminals;
    private readonly IMergeQueueRegistry _queues;
    private readonly AgentSpawnService _spawns;
    private readonly CoordinatorLimits _limits;
    private readonly IAuditLog _audit;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<AgentSessionKey, DateTimeOffset> _idleSince = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public JailReaperHostedService(
        AgentSessionStore sessions,
        TerminalSessionManager terminals,
        IMergeQueueRegistry queues,
        AgentSpawnService spawns,
        CoordinatorLimits limits,
        IAuditLog audit,
        ILoggerFactory loggerFactory)
        : this(sessions, terminals, queues, spawns, limits, audit, loggerFactory, null)
    {
    }

    /// <param name="sweepSegments">The MG-36 network-segment sweep (audit F27), or null for the
    /// production one. The test seam — a unit test drives the decision without a container engine.</param>
    internal JailReaperHostedService(
        AgentSessionStore sessions,
        TerminalSessionManager terminals,
        IMergeQueueRegistry queues,
        AgentSpawnService spawns,
        CoordinatorLimits limits,
        IAuditLog audit,
        ILoggerFactory loggerFactory,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? sweepSegments)
    {
        _sweepSegments = sweepSegments ?? SweepSegmentsWithDockerAsync;
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _terminals = terminals ?? throw new ArgumentNullException(nameof(terminals));
        _queues = queues ?? throw new ArgumentNullException(nameof(queues));
        _spawns = spawns ?? throw new ArgumentNullException(nameof(spawns));
        _limits = limits ?? new CoordinatorLimits();
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _log = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.Spawn);
    }

    /// <summary>The audit type for a leaked MG-36 network segment this host reclaimed.</summary>
    public const string SegmentReapedEvent = "jail_segment_reaped";

    /// <summary>
    /// How often the network-segment sweep runs — much less often than the jail sweep, because the two
    /// answer different questions on different timescales. A jail goes idle in minutes and the sweep has
    /// to notice; a leaked segment is inert until a few dozen of them exhaust Docker's address pool, and
    /// the sweep's own grace is ten minutes, so running it every jail pass would be a network listing a
    /// minute to decide nothing.
    /// </summary>
    public static readonly TimeSpan SegmentSweepInterval = TimeSpan.FromMinutes(5);

    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _sweepSegments;
    private DateTimeOffset _lastSegmentSweep = DateTimeOffset.MinValue;

    /// <summary>
    /// <b>Audit F27 — the sweep half, wired.</b> Reclaims per-agent Docker networks whose jail no longer
    /// exists: the segment is created BEFORE the container, so a spawn that fails in between leaks one,
    /// and before this it was reclaimed only by a clean teardown. Docker's default local address pool is
    /// about 32 <c>/16</c>s, so a few dozen leaks make every spawn fail at network creation on a machine
    /// with no running agents — a symptom that names nothing about its cause.
    ///
    /// <para><b>This is a different axis from the jail sweep and must not borrow its rules.</b> The jail
    /// sweep asks "has this agent been idle too long", and PR #366 made a jail with an in-flight merge
    /// entry exempt from that. A segment is reaped on a different question entirely — is there any
    /// container, running <i>or stopped</i>, that this network exists for — so a live-but-idle jail's
    /// segment is saved by the jail's mere existence, whatever the queue thinks of it, and the in-flight
    /// exemption has nothing to add. Conflating them would mean an exempt jail's network could be swept
    /// while the jail ran on it, which is worse than either defect.</para>
    /// </summary>
    public async Task<IReadOnlyList<string>> SweepSegmentsOnceAsync(
        DateTimeOffset now, CancellationToken ct = default)
    {
        _lastSegmentSweep = now;
        IReadOnlyList<string> reaped;
        try
        {
            reaped = await _sweepSegments(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A reaper that throws is a reaper someone disables. The jail sweep is the load-bearing half
            // of this host and must not be lost to an engine that would not answer a network listing.
            _log.LogWarning(ex, "jail reaper: the network-segment sweep threw");
            return Array.Empty<string>();
        }

        foreach (var segment in reaped)
        {
            _log.LogInformation("jail reaper: reclaimed leaked network segment {Segment}", segment);
            _audit.Append(new AuditEvent(SegmentReapedEvent, new Dictionary<string, string>
            {
                ["segment"] = segment,
                ["when"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            }));
        }

        return reaped;
    }

    private static async Task<IReadOnlyList<string>> SweepSegmentsWithDockerAsync(CancellationToken ct)
    {
        // A client per sweep, like the session reconciler's own lister: this runs every few minutes and a
        // long-lived connection to an engine that may be restarted underneath it buys nothing.
        using var docker = Mainguard.Agents.Agents.Sandbox.DockerEndpointResolver.CreateClient();
        return await new Mainguard.Agents.Agents.Sandbox.SandboxSegmentReaper(docker)
            .SweepAsync(ct).ConfigureAwait(false);
    }

    /// <summary>One pass at <paramref name="now"/>. Public, and clocked by the caller, so a test drives the
    /// idle allowance without waiting it out. Returns the agent ids it stopped.</summary>
    public async Task<IReadOnlyList<string>> SweepOnceAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var reaped = new List<string>();
        var allowance = TimeSpan.FromMinutes(Math.Max(1, _limits.IdleJailReapMinutes));
        foreach (var session in _sessions.List())
        {
            if (string.IsNullOrEmpty(session.ContainerId))
            {
                continue; // session-only records hold no jail
            }

            // Audit F20. A session the reconciler has marked Unresponsive has no jail left — Docker has
            // no live container for it — so there is no memory, no CPU and no container to reclaim, which
            // is this sweep's entire remit. What a reap WOULD still do is delete the agent's worktree, and
            // that worktree is the only place its uncommitted work exists. Deleting it on a timer put the
            // decision to discard a dead agent's work in the hands of a 30-minute clock; the record stays,
            // visible and Stoppable, until a human makes it.
            if (string.Equals(session.State, AgentSessionReconciler.LostState, StringComparison.Ordinal))
            {
                continue;
            }

            var key = session.Key;
            WorkerMergeState? entry = null;
            if (_queues.Resolve(session.RepoHash ?? string.Empty) is { } context
                && context.Queue.Agents.Contains(session.Id))
            {
                entry = context.Queue.GetState(session.Id);
            }

            var hasLiveCli = _terminals.TryGetBound(key) is not null
                && !string.Equals(session.State, "Dead", StringComparison.Ordinal);
            DateTimeOffset? idleSince = null;
            if (hasLiveCli)
            {
                _idleSince.TryRemove(key, out _);
            }
            else
            {
                idleSince = _idleSince.GetOrAdd(key, now);
            }

            var verdict = JailReapPolicy.Decide(entry, hasLiveCli, idleSince, now, allowance);
            if (!verdict.Reap)
            {
                continue;
            }

            _log.LogInformation(
                "jail reaper: stopping agent={Agent} repo={Repo} — {Reason}", session.Id, session.RepoHash, verdict.Reason);
            _audit.Append(new AuditEvent(ReapedEvent, new Dictionary<string, string>
            {
                ["repo"] = session.RepoHash ?? string.Empty,
                ["agent"] = session.Id,
                ["cause"] = verdict.Cause.ToString(),
                ["reason"] = verdict.Reason,
                ["when"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            }));
            try
            {
                await _spawns.StopAsync(key, ct).ConfigureAwait(false);
                reaped.Add(session.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "jail reaper: stop failed for agent={Agent}", session.Id);
            }
            finally
            {
                _idleSince.TryRemove(key, out _);
            }
        }

        return reaped;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _limits.JailReapSweepSeconds));
        _log.LogInformation(
            "jail reaper running — every {Seconds}s a jail whose entry is terminal, or that has had no CLI for "
            + "{Minutes} min, is stopped; every {SegmentMinutes} min a leaked network segment is reclaimed",
            (int)interval.TotalSeconds, _limits.IdleJailReapMinutes, (int)SegmentSweepInterval.TotalMinutes);
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                try
                {
                    await SweepOnceAsync(now, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "jail reaper: sweep threw");
                }

                // Its own, much slower cadence — see SegmentSweepInterval. Ordered after the jail sweep on
                // purpose: a jail this pass just stopped has had its segment released by the teardown, so
                // the segment sweep is never asked about a network the same pass is still using.
                if (now - _lastSegmentSweep >= SegmentSweepInterval)
                {
                    await SweepSegmentsOnceAsync(now, _stop.Token).ConfigureAwait(false);
                }
            }
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Idempotent, and safe after Dispose: a WebApplicationFactory parent disposes its derived hosts
        // again, so StopAsync can arrive after Dispose — a throw here fails every test in that host.
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (_loop is not null)
        {
            try
            {
                await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stop.Dispose();
        }
    }

    private int _disposed;
}
