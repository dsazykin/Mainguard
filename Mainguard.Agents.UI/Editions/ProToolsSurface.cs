using System.Threading.Tasks;
using Avalonia.Controls;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.UI.Editions;

namespace Mainguard.Agents.UI.Editions;

/// <summary>
/// The Pro edition's implementation of the shell's <see cref="IProToolsSurface"/> (step 1c, reshaped
/// for the Settings tabbed-page redesign). The agent-platform Settings pages that used to live behind
/// dialog-opening methods now just construct and return their content ViewModel — no <c>Window</c>, no
/// <c>ShowDialog</c> — so <c>SettingsViewModel</c> can drop them straight into a page slot. Kept in its
/// own file: in Phase 2 this type moves to the Pro-only UI assembly (its Mainguard.Agents.Agents +
/// Pro-View dependencies belong there); the <see cref="IProToolsSurface"/> contract stays in the shared
/// shell.
/// </summary>
public sealed class ProToolsSurface : IProToolsSurface
{
    // AI Providers (T-14 vault): the API-key settings page. Trivial — the VM is fully self-defaulting.
    public object CreateAiProvidersPage() => new ApiKeySettingsViewModel();

    // Agent CLIs (P2-22 §J-5): the "add more later" surface over the same pinned channel the OOBE
    // picker installs from. A CLI installed here reaches every NEW agent sandbox immediately via the
    // read-only adapters mount — no image rebuild, no re-setup.
    public object CreateAgentClisPage()
    {
        var wsl = new Mainguard.Agents.Agents.Bootstrap.WslRunner();
        var installer = Mainguard.Agents.Agents.Adapters.AgentCliInstaller.CreateDefault(wsl);
        // The updater rides along: rows annotate with newer registry releases + one-step revert.
        var updater = Mainguard.Agents.Agents.Adapters.AgentCliUpdateService.CreateDefault(wsl);
        return new AgentCliSettingsViewModel(installer, updater);
    }

    // Daemon logs (in-depth per-subsystem logging): the read-only "recent daemon logs" surface over
    // Core's DaemonLogReader (journalctl / tail over the same WSL seam the OOBE health card uses). A
    // fresh reader every time this page is (re)activated — the page wrapper disposes the previous one.
    public object CreateDaemonLogsPage()
    {
        var reader = new Mainguard.Agents.Agents.Bootstrap.DaemonLogReader(
            new Mainguard.Agents.Agents.Bootstrap.WslRunner());
        return new DaemonLogsViewModel(reader);
    }

    // Mainguard OS (PR2 follow-up + Item 1 repair action): the post-setup repo-onboarding engine + the
    // user-triggered sandbox-image rebuild, combined into one page since Rebuild has no dialog of its
    // own. The VM is composed by ProComposition.AddReposToOsFactory (pickers parent to the Settings
    // window now, not a throwaway dialog).
    public object? CreateMainguardOsPage(Window owner)
    {
        var addRepos = ProComposition.AddReposToOsFactory?.Invoke(owner);
        return addRepos is null ? null : new MainguardOsPageViewModel(addRepos, RebuildSandboxImagesAsync);
    }

    public Task RebuildSandboxImagesAsync()
    {
        // The toast targets the shell if it is present; the rebuild fires regardless. Both the shell toast
        // and the rebuild engine (Services.SandboxImageInstaller, which stays in the shell because it
        // reaches the shell VM) are injected down through ProComposition so this Pro-only assembly never
        // reaches up into Mainguard.App.Shell (ADR-0001 / step 2e).
        ProComposition.ShowShellToast("Rebuilding sandbox images…", false);

        // Per-step build/load lines go to oobe.log; the final Installed/Updated/InstallFailed toast is
        // published by the installer's outcome sink (single-sourced with the startup path).
        var progress = new System.Progress<string>(line => ProComposition.LogOobe($"sandbox images (rebuild): {line}"));

        // Through the shared tracker, so this repair cannot start a second docker build on top of the
        // startup auto-provision (the user did exactly that on 2026-08-05: two concurrent builds of
        // mainguard-agent-base:latest, both then killed by the VM terminate on exit). If a run is
        // already in flight it IS this rebuild's work — the same images from the same sources — so
        // joining it satisfies the request without a rival build.
        _ = Mainguard.Agents.Agents.Bootstrap.SandboxImageProvisioningTracker.Shared.RunExclusiveAsync(
            () => ProComposition.RebuildSandboxImages?.Invoke(ProComposition.LogOobe, progress, true)
                  ?? Task.CompletedTask,
            onJoinedExisting: () => ProComposition.LogOobe(
                "sandbox images (rebuild): a build is already running — joined it instead of starting a second"));
        return Task.CompletedTask;
    }
}
