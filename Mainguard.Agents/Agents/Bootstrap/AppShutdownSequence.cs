using System;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>The ordered status strings the shutdown window shows. Constants so the ordering test
/// asserts the exact sequence.</summary>
public static class ShutdownStatus
{
    /// <summary>Always shown first: releasing the VM keep-alive holder.</summary>
    public const string ReleasingKeepAlive = "Releasing the Mainguard OS environment…";

    /// <summary>Only when StopVmOnExit is on: terminating MainguardEnv (scoped, G-12).</summary>
    public const string StoppingVm = "Stopping Mainguard OS…";

    /// <summary>Shown INSTEAD of <see cref="StoppingVm"/> when a sandbox-image build is still running:
    /// the distro is deliberately left up so the build is not killed mid-layer.</summary>
    public const string LeavingVmForImageBuild = "Finishing the sandbox image update in the background…";

    /// <summary>Terminal line before the process actually exits.</summary>
    public const string Done = "Mainguard is closing.";
}

/// <summary>
/// The seam the <see cref="AppShutdownSequence"/> visualizes. It exposes ONLY what already happens
/// at exit today (release the keep-alive; when StopVmOnExit is on, terminate MainguardEnv) so the
/// shutdown window adds no new teardown behavior. Interface-first per Core convention; the App
/// supplies the real implementation and tests supply a fake.
/// </summary>
public interface IAppShutdownEnvironment
{
    /// <summary>Whether the user's StopVmOnExit setting is on (the VM terminate leg runs only then).</summary>
    bool StopVmOnExit { get; }

    /// <summary>Disposes the VM keep-alive holder (idempotent) so the optional stop isn't fighting a
    /// holder that would reboot the VM.</summary>
    void ReleaseKeepAlive();

    /// <summary>Terminates MainguardEnv (scoped — never the VM-wide shutdown verb). Called only when
    /// <see cref="StopVmOnExit"/> is true; idempotent.</summary>
    Task StopVmAsync(CancellationToken ct);

    /// <summary>
    /// True while a sandbox-image provisioning build is running inside MainguardEnv
    /// (<see cref="SandboxImageProvisioningTracker.IsProvisioning"/>). When it is, the
    /// <see cref="StopVmOnExit"/> terminate is SKIPPED: <c>wsl --terminate</c> kills the in-flight
    /// <c>docker build</c>, and because the expensive agent-base step is a single layer, the kill
    /// discards the whole build — which is precisely how the images stayed stale across every restart
    /// in the 2026-08-05 field failure.
    /// </summary>
    bool SandboxProvisioningInFlight { get; }

    /// <summary>oobe.log breadcrumb sink.</summary>
    void Log(string message);
}

/// <summary>
/// Visualizes the app's existing exit teardown as an orderly, reentrancy-guarded sequence (owner
/// design, 2026-07-17): release the VM keep-alive, and — when StopVmOnExit is on — stop MainguardEnv,
/// completing BEFORE the process exits. A second exit request must never double-run the teardown
/// (<see cref="_ran"/> guard), and hide-to-tray never reaches here (that path never triggers a full
/// exit). No new behavior — only a witnessed form of what already ran on <c>desktop.Exit</c>.
/// </summary>
public sealed class AppShutdownSequence
{
    private readonly IAppShutdownEnvironment _env;
    private int _ran;

    /// <summary>Constructs the sequence over the exit-teardown seam.</summary>
    public AppShutdownSequence(IAppShutdownEnvironment env)
    {
        _env = env ?? throw new ArgumentNullException(nameof(env));
    }

    /// <summary>True once <see cref="RunAsync"/> has begun — a second call is a no-op.</summary>
    public bool HasRun => Volatile.Read(ref _ran) != 0;

    /// <summary>Runs the teardown once, reporting each status line through <paramref name="status"/>.
    /// Reentrancy-guarded: a concurrent/second call returns immediately without re-running.</summary>
    public async Task RunAsync(IProgress<string>? status, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _ran, 1) != 0)
        {
            _env.Log("shutdown: second exit request ignored (already tearing down)");
            return;
        }

        status?.Report(ShutdownStatus.ReleasingKeepAlive);
        _env.Log("shutdown: releasing VM keep-alive");
        try
        {
            _env.ReleaseKeepAlive();
        }
        catch (Exception ex)
        {
            _env.Log($"shutdown: releasing keep-alive failed (non-fatal): {ex.Message}");
        }

        if (_env.StopVmOnExit)
        {
            // A provisioning build in flight outranks the StopVmOnExit convenience: terminating the
            // distro under it destroys minutes of work and leaves the images stale, so the NEXT launch
            // refuses to start the coordinator too. Leaving MainguardEnv up costs the user some idle
            // RAM until WSL's own idle timer reaps it; killing the build costs them the feature.
            if (_env.SandboxProvisioningInFlight)
            {
                status?.Report(ShutdownStatus.LeavingVmForImageBuild);
                _env.Log("shutdown: a sandbox image build is still running — leaving MainguardEnv up "
                    + "(StopVmOnExit skipped) so the build is not killed mid-layer");
            }
            else
            {
                status?.Report(ShutdownStatus.StoppingVm);
                _env.Log("shutdown: stopping MainguardEnv (StopVmOnExit)");
                try
                {
                    await _env.StopVmAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _env.Log($"shutdown: stopping MainguardEnv failed (non-fatal): {ex.Message}");
                }
            }
        }

        status?.Report(ShutdownStatus.Done);
        _env.Log("shutdown: complete");
    }
}
