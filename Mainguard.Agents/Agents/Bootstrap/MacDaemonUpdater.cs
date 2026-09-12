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
    private readonly MacDaemonLaunchAgent _launchAgent = new();

    public async Task<DaemonRefreshResult> RefreshAsync(string payloadDirectory, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(payloadDirectory, "Mainguard.Server.dll")))
        {
            return new DaemonRefreshResult(false, $"no daemon payload at '{payloadDirectory}'.");
        }

        try
        {
            // F55/F63: when launchd owns the daemon, launchd restarts it. The old code ran its own
            // stop+start regardless, which fought the job: `KeepAlive` respawned the daemon the instant
            // StopAsync's SIGTERM landed, so the "start" here raced launchd's respawn and the pair
            // routinely produced two daemons contending for the port — the loser of which (pre-F55)
            // rotated the winner's token and mTLS material on its way out. `kickstart -k` is the one
            // atomic restart: launchd stops the old process and starts the new one, and there is never
            // a moment with two of them or with none.
            if (_launchAgent.IsInstalled())
            {
                // Re-stage first: the job runs from the staged copy, never from the bundle (F63e), so a
                // refresh that did not re-stage would restart onto the OLD assemblies.
                //
                // B2: the copy lands BESIDE the live payload and its result is CHECKED. The previous
                // version deleted the staged directory and copied into it while the job executed from it
                // — the mixed-assembly window (e) exists to close — and then ran `kickstart -k` whatever
                // the copy did, so a copy that failed halfway left the plist pointing at an incomplete
                // payload, the daemon crashing on a missing assembly, and `Crashed:true` respawning it
                // every 30 s forever. A refusal here leaves the daemon that is already running alone.
                var incoming = MacDaemonLaunchAgent.StageIncomingPayload(payloadDirectory);
                if (incoming is null)
                {
                    return new DaemonRefreshResult(
                        false,
                        "could not stage the daemon payload; the running daemon was left untouched.");
                }

                var restarted = await _launchAgent
                    .RestartOntoStagedPayloadAsync(incoming, ct).ConfigureAwait(false);
                return restarted
                    ? new DaemonRefreshResult(true, "launchd restarted mainguardd from the staged payload.")
                    : new DaemonRefreshResult(false, "launchd did not restart mainguardd from the staged payload.");
            }

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
