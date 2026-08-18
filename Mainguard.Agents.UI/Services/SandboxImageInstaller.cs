using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.UI.Editions;
using Mainguard.UI.Editions;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The App's one-call glue for sandbox-image provisioning (field failure 2026-07-17, twice: a fresh
/// <c>MainguardEnv</c> import AND the tier-2 VM upgrade both leave the VM's docker image store empty, so
/// the first agent spawn fails). Runs Core's <see cref="SandboxImageAutoProvision"/> — probe the two
/// jail images in-distro, then <c>docker load</c> the bundled CI tar (else <c>docker build</c>) any
/// missing OR version-stale one — then, mirroring <see cref="DaemonUpdateToastPublisher"/>, posts a
/// shell toast only for outcomes that changed something (Installed / Updated / InstallFailed via the
/// pure <see cref="SandboxImageToast"/> policy); every quieter outcome stays oobe.log-only. The Core
/// flow never throws, and the caller is a background task — the launch path is never blocked.
/// </summary>
internal static class SandboxImageInstaller
{
    /// <param name="log">The oobe.log breadcrumb sink.</param>
    /// <param name="progress">Optional per-step progress lines (build/load) — previously discarded;
    /// now threaded so the background build and the Tools rebuild both leave a live trace.</param>
    /// <param name="force">True for the user-triggered "Rebuild sandbox images" repair (rebuild every
    /// image regardless of the probe); false for the startup missing/stale auto-repair.</param>
    public static Task RunAsync(Action<string> log, IProgress<string>? progress = null, bool force = false) =>
        SandboxImageAutoProvision.RunAsync(
            // The runner is the substrate seam: in-distro over WSL on Windows, the host's own
            // docker CLI on macOS (HostCommandRunner interprets the same in-distro shapes).
            new SandboxImageProvisioner(
                OperatingSystem.IsMacOS() ? new HostCommandRunner() : new WslRunner()),
            SandboxImageProvisioner.DefaultBundledImagesDirectory(),
            log,
            CancellationToken.None,
            onOutcome: Publish,
            progress: progress,
            force: force);

    private static void Publish(SandboxImageProvisionOutcome outcome)
    {
        if (SandboxImageToast.TryCompose(outcome) is not { } toast)
        {
            return; // all-present / no-sources / faulted — no toast, ever
        }

        // Post onto the UI thread through the shell-toast seam the Pro head wired (step 2f — this Pro-UI
        // assembly must not name the shell's MainWindowViewModel). No shell present ⇒ nothing happens.
        Dispatcher.UIThread.Post(() => ProComposition.ShowShellToast(toast.Message, toast.IsWarning));
    }
}
