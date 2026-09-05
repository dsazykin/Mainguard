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
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _terminals = terminals ?? throw new ArgumentNullException(nameof(terminals));
        _queues = queues ?? throw new ArgumentNullException(nameof(queues));
        _spawns = spawns ?? throw new ArgumentNullException(nameof(spawns));
        _limits = limits ?? new CoordinatorLimits();
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _log = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.Spawn);
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
            + "{Minutes} min, is stopped", (int)interval.TotalSeconds, _limits.IdleJailReapMinutes);
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

                try
                {
                    await SweepOnceAsync(DateTimeOffset.UtcNow, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "jail reaper: sweep threw");
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
