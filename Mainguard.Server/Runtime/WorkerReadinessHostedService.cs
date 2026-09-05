using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Server.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Runtime;

/// <summary>
/// The daemon-lifecycle slot that RESOLVES <see cref="WorkerReadinessTrigger"/> — which is the entire job,
/// and the reason this class exists rather than a comment in the registration file.
///
/// <para>A DI singleton is constructed on first resolve. The trigger has no RPC, no client and no other
/// consumer: register it and nothing would ever ask for it, so it would never subscribe to the ref watcher
/// and never sweep. That is precisely the shape of defect this subsystem was built to repair — a mechanism
/// that is complete, tested, registered, and never actually running (MG-10's empty registry, the
/// verification path with no caller). Forcing construction here at boot is what makes the wiring real, and
/// <c>WorkerReadinessTriggerWiringTests</c> asserts it from the real composition root.</para>
///
/// <para>Stopping disposes the trigger: it unsubscribes from the watcher and waits for the sweep in flight,
/// so shutdown does not leave a sweep running against directories the host is about to release.</para>
/// </summary>
public sealed class WorkerReadinessHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger _log;
    private WorkerReadinessTrigger? _trigger;
    private int _stopped;

    public WorkerReadinessHostedService(IServiceProvider services, ILoggerFactory loggerFactory)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _log = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.Merge);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // GetService, not GetRequiredService: a daemon whose substrate cannot supply a ref watcher must
        // still start and still serve the human Verify button. The automatic trigger is an addition to that
        // path, never a precondition for it.
        _trigger = _services.GetService<WorkerReadinessTrigger>();
        if (_trigger is null)
        {
            _log.LogInformation(
                "automatic verification trigger not available — merge-queue entries verify on the human Verify button only");
            return Task.CompletedTask;
        }

        _log.LogInformation(
            "automatic verification trigger running — a delegated worker's branch verifies once it stops advancing");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            _trigger?.Dispose();
        }

        return Task.CompletedTask;
    }
}
