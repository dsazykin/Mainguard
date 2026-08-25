using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Audit;
using Mainguard.Server.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Runtime;

/// <summary>
/// P2-15 retention: once at boot and every 24 h, expire audit records older than 90 days — as
/// chained REDACTION events (payload tombstoned, row count unchanged, chain verifiable), never
/// deletion; the schema's triggers would refuse a delete anyway. A no-op when the daemon runs on
/// the in-memory journal (nothing persisted → nothing to expire).
/// </summary>
public sealed class AuditRetentionService : BackgroundService
{
    /// <summary>The default retention window (master doc §P2-15: "retention default 90 d").</summary>
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(90);

    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceProvider _services;
    private readonly ILogger _log;

    public AuditRetentionService(IServiceProvider services, ILoggerFactory loggerFactory)
    {
        _services = services;
        _log = loggerFactory.CreateLogger(DaemonLogCategories.Lifecycle);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var chained = _services.GetService<IChainedAuditLog>();
        if (chained is null)
        {
            return; // in-memory journal — dies with the process, retention is meaningless
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var redacted = chained.ApplyRetention(RetentionPeriod);
                if (redacted > 0)
                {
                    _log.LogInformation("audit retention: {Count} record(s) redacted (>{Days}d)",
                        redacted, RetentionPeriod.TotalDays);
                }
            }
            catch (Exception ex)
            {
                // Retention failing must never take the daemon down; the next sweep retries.
                _log.LogWarning(ex, "audit retention sweep failed: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
