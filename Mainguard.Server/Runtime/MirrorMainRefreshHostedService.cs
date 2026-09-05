using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Runtime;

/// <summary>
/// Pulls every live queue's mirror main forward from the user's checkout on an interval
/// (<see cref="CoordinatorLimits.MirrorRefreshSeconds"/>) — owner decision 2026-09-04.
///
/// <para>Before this the mirror was refreshed only when something else happened to (repo-open,
/// merge-confirm, the cascade, the reconcile), so a pull or a commit on main outside Mainguard left the
/// queue measured against a main the checkout no longer had, and nothing on any surface said so. The
/// sweep is the bound behind the rail's "refreshed N min ago"; the on-demand RPC is the same call made
/// when a human comes back to the window. All the behaviour is
/// <see cref="MergeQueueProvisioner.RefreshMainFromCheckout"/>; this only asks for it on a clock.</para>
/// </summary>
public sealed class MirrorMainRefreshHostedService : IHostedService, IDisposable
{
    private readonly MergeQueueProvisioner _queues;
    private readonly IMergeQueueRegistry _registry;
    private readonly TimeSpan _interval;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public MirrorMainRefreshHostedService(
        MergeQueueProvisioner queues,
        IMergeQueueRegistry registry,
        CoordinatorLimits limits,
        ILoggerFactory loggerFactory)
    {
        _queues = queues ?? throw new ArgumentNullException(nameof(queues));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _interval = TimeSpan.FromSeconds(Math.Max(5, (limits ?? new CoordinatorLimits()).MirrorRefreshSeconds));
        _log = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.Merge);
    }

    /// <summary>One pass over every live queue. Public so a test can drive it without a clock.</summary>
    public void SweepOnce()
    {
        foreach (var repoHandle in _registry.Handles())
        {
            try
            {
                _queues.RefreshMainFromCheckout(repoHandle);
            }
            catch (Exception ex)
            {
                // One repo's unreadable checkout must not stop the sweep for the others.
                _log.LogWarning(ex, "mirror refresh threw for repo={Repo}", repoHandle);
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation(
            "mirror refresh running — every {Seconds}s each live queue's mirror main is pulled forward from the checkout",
            (int)_interval.TotalSeconds);
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_interval, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                SweepOnce();
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
