using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Runtime;

/// <summary>
/// Shuts the daemon down once the build it was launched from no longer exists on disk.
///
/// <para><b>Why this exists.</b> The daemon is started detached — <c>MacDaemonController</c>
/// <c>EnsureStartedAsync</c> discards the process handle on purpose, because the daemon must outlive the
/// UI so agents keep running after the window closes. On POSIX that means it re-parents to init when the
/// launcher exits, and nothing owns its lifetime any more. That is fine for an INSTALLED daemon, which
/// launchd supervises. It is not fine for one started out of a build tree: when that tree is deleted —
/// a removed git worktree, a cleaned build output, a checkout thrown away — the daemon keeps running
/// forever, holding the loopback port and the data root against every future daemon, while being
/// invisible to every payload-scoped stop path. One squatted on port 5250 for fifteen days after its
/// worktree was removed, and the only symptom was that the next <c>dotnet run</c> died on the bind.</para>
///
/// <para><b>Why the check is the payload, not the parent.</b> Tying the daemon's life to its launcher's
/// pid would also stop the daemon every time the user closes the window, which is exactly what the
/// detached start is designed to avoid. "My own binary has been deleted" is a different question, and it
/// has only one honest answer: this process can never be restarted, updated, or reached by its own
/// controller again, so it is not a service any more — it is a leak.</para>
///
/// <para><b>Why it waits.</b> <see cref="MacDaemonUpdater"/> replaces the payload underneath a running
/// daemon during a tier-1 refresh, so the dll can be legitimately absent for a moment mid-swap. The
/// shutdown therefore needs <see cref="RequiredConsecutiveMisses"/> consecutive misses — minutes, not
/// one sweep — and any single reappearance resets the count. A slow swap costs nothing; a deleted tree
/// never comes back.</para>
/// </summary>
public sealed class StalePayloadShutdownHostedService : IHostedService, IDisposable
{
    /// <summary>How often the payload is probed.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Consecutive misses before the daemon stops itself. Five sweeps is five minutes — far longer than
    /// any payload swap, and short enough that an orphan does not outlive the session that made it.
    /// </summary>
    public const int RequiredConsecutiveMisses = 5;

    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<StalePayloadShutdownHostedService> _log;
    private readonly string? _payloadPath;
    private readonly TimeSpan _interval;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _misses;

    public StalePayloadShutdownHostedService(
        IHostApplicationLifetime lifetime,
        ILogger<StalePayloadShutdownHostedService> log)
        : this(lifetime, log, ResolvePayloadPath(), SweepInterval)
    {
    }

    /// <summary>Explicit form — the tests supply a path they can delete and a interval they can drive.</summary>
    internal StalePayloadShutdownHostedService(
        IHostApplicationLifetime lifetime,
        ILogger<StalePayloadShutdownHostedService> log,
        string? payloadPath,
        TimeSpan interval)
    {
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _payloadPath = payloadPath;
        _interval = interval;
    }

    /// <summary>
    /// The file whose disappearance means this build is gone: the entry assembly's own location. Null
    /// for a single-file or trimmed host that reports no location — in which case this service does
    /// nothing rather than guess at a path and stop a healthy daemon.
    /// </summary>
    internal static string? ResolvePayloadPath()
    {
        var location = Assembly.GetEntryAssembly()?.Location;
        return string.IsNullOrEmpty(location) ? null : location;
    }

    /// <summary>One probe. Returns true when the daemon should now stop.</summary>
    internal bool ObserveOnce()
    {
        if (_payloadPath is null)
        {
            return false;
        }

        if (File.Exists(_payloadPath))
        {
            _misses = 0;
            return false;
        }

        _misses++;
        if (_misses < RequiredConsecutiveMisses)
        {
            _log.LogWarning(
                "payload missing at {Path} ({Miss}/{Needed} sweeps) — if it stays missing this daemon "
                + "will stop itself, because a daemon whose build was deleted can never be restarted, "
                + "updated or stopped by its own controller again",
                _payloadPath, _misses, RequiredConsecutiveMisses);
            return false;
        }

        return true;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_payloadPath is null)
        {
            _log.LogInformation("stale-payload watch disabled: this host reports no assembly location");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        _log.LogInformation(
            "stale-payload watch running — every {Seconds}s; after {Needed} consecutive misses of {Path} "
            + "this daemon stops itself rather than squat on the port and the data root",
            _interval.TotalSeconds, RequiredConsecutiveMisses, _payloadPath);

        _loop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_interval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    if (ObserveOnce())
                    {
                        _log.LogWarning(
                            "stopping: the payload this daemon was launched from ({Path}) has been gone "
                            + "for {Needed} consecutive sweeps. Its build tree was deleted, so nothing "
                            + "can restart or update this process; holding the port and data root any "
                            + "longer would only block the next daemon",
                            _payloadPath, RequiredConsecutiveMisses);
                        _lifetime.StopApplication();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // A probe that throws must never take the daemon down — that would invert this
                    // service's whole purpose.
                    _log.LogWarning("stale-payload sweep threw {Error}", ex);
                }
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is { } loop)
        {
            try
            {
                await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Shutdown is not the place to surface a cancelled sweep.
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
