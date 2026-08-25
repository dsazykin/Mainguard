using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// The macos-host <see cref="IDaemonUpdater"/>: the tier-1 refresh here is simply stop the local
/// daemon and start it again FROM the payload — the daemon already runs off the payload directory
/// the app ships, so "deploy the new build" and "restart onto it" are the same act (no staging
/// copy, no systemd unit, no VM; contrast <see cref="DaemonUpdater"/>). A replaced-in-place dll
/// never disturbs the running process on Unix, so the swap is safe at any moment.
/// </summary>
public sealed class MacDaemonUpdater : IDaemonUpdater
{
    private readonly MacDaemonController _controller = new();

    public async Task<DaemonRefreshResult> RefreshAsync(string payloadDirectory, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(payloadDirectory, "Mainguard.Server.dll")))
        {
            return new DaemonRefreshResult(false, $"no daemon payload at '{payloadDirectory}'.");
        }

        try
        {
            await _controller.StopAsync(payloadDirectory, ct).ConfigureAwait(false);
            var started = await _controller.EnsureStartedAsync(payloadDirectory, ct).ConfigureAwait(false);
            return started
                ? new DaemonRefreshResult(true, "restarted mainguardd from the payload.")
                : new DaemonRefreshResult(false, "mainguardd did not start from the payload.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DaemonRefreshResult(false, ex.Message);
        }
    }
}
