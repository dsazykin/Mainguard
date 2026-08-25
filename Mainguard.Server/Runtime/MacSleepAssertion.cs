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
        psi.ArgumentList.Add("-im"); // idle sleep + disk idle; never the display
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _caffeinate = Process.Start(psi);
        _log.LogInformation("agents running — holding the sleep assertion (caffeinate pid {Pid})",
            _caffeinate?.Id);
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
