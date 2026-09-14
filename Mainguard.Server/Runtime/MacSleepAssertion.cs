using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Mainguard.Agents.Agents.Sandbox;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Runtime;

/// <summary>
/// macos-host only (registered on macOS alone): while any agent jail is RUNNING, hold a system
/// power assertion so idle sleep and App Nap throttling cannot stall a verification or a working
/// agent under a closed lid's timer or an occluded window. Held via a child
/// <c>caffeinate -im -w &lt;daemon pid&gt;</c> — the <c>-w</c> ties the assertion to THIS daemon's
/// lifetime (a killed daemon can never leak a machine that refuses to sleep), and the child is
/// killed the moment the last agent stops so an idle Mainguard never costs battery. Polls the
/// same <c>mainguard.agent</c> container label the boot reconciler reads — Docker is the source
/// of truth for liveness here, exactly as everywhere else (no PID files).
/// </summary>
internal sealed class MacSleepAssertion : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(60);

    private readonly IDockerClient _docker;
    private readonly ILogger<MacSleepAssertion> _log;
    private Process? _caffeinate;

    public MacSleepAssertion(IDockerClient docker, ILogger<MacSleepAssertion> log)
    {
        _docker = docker;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var anyRunning = (await DockerAgentLister.ListAsync(_docker, stoppingToken).ConfigureAwait(false))
                    .Any(a => a.Running);

                if (anyRunning) EnsureHeld();
                else Release();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // An unreachable engine means no jails can be running — drop the assertion and
                // keep polling; the assertion is a courtesy, never worth failing the daemon over.
                _log.LogDebug("sleep-assertion poll failed (non-fatal): {Message}", ex.Message);
                Release();
            }

            try { await Task.Delay(Poll, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        Release();
    }

    private void EnsureHeld()
    {
        if (_caffeinate is { HasExited: false }) return;

        var psi = new ProcessStartInfo("/usr/bin/caffeinate") { UseShellExecute = false };
        // F60: -i (idle sleep) + -m (disk idle) + -s (SYSTEM sleep). -s was missing, and it is the one
        // that covers the case the other two do not: an explicit sleep request — Apple menu > Sleep, a
        // power-button press, the "sleep after N minutes" system setting firing on AC. Never -d: the
        // display is the operator's, and keeping a laptop screen lit for a background daemon is a
        // battery bug, not a feature.
        psi.ArgumentList.Add("-ims");
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _caffeinate = Process.Start(psi);
        _log.LogInformation("agents running — holding the sleep assertion (caffeinate pid {Pid})",
            _caffeinate?.Id);

        // Said once per acquisition, and deliberately: no power assertion available to an unprivileged
        // process prevents CLAMSHELL sleep. Closing the lid on a Mac with no external display suspends
        // the machine regardless of -s, and on battery -s is not honoured at all. So a jail CAN be frozen
        // mid-verification by a closed lid, the daemon's own long-lived gRPC streams go half-open with it,
        // and neither this assertion nor the HTTP/2 keepalive can prevent that — the keepalive only makes
        // the resulting dead stream SURFACE as a fault the client reconnects from, instead of hanging
        // until the OS TCP timeout. Stating the limit in the log beats letting an operator infer a
        // guarantee that macOS does not offer.
        _log.LogInformation(
            "note: a power assertion cannot prevent lid-close (clamshell) sleep, and -s is ignored on "
            + "battery. Keep the lid open (or an external display attached, on AC) for a long "
            + "verification; a closed-lid suspend pauses every jail until the machine wakes.");
    }

    private void Release()
    {
        if (_caffeinate is not { HasExited: false } held) return;
        try
        {
            held.Kill();
            _log.LogInformation("no agents running — released the sleep assertion");
        }
        catch
        {
            // Already gone.
        }
        finally
        {
            held.Dispose();
            _caffeinate = null;
        }
    }
}
