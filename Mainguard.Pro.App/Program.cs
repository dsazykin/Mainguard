using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Mainguard.Agents.UI.Editions;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell;
using Mainguard.App.Shell.Editions;
using Mainguard.App.Shell.Services;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.Git.Services;
using Mainguard.UI;
using Mainguard.UI.Editions;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using ShellApp = Mainguard.App.Shell.App;

namespace Mainguard.Pro.App;

/// <summary>
/// The Pro edition exe head (step 2f). It is the ONE place the reference-clean shell and the Pro-UI
/// assembly meet: it selects the Pro edition, points the shell's Pro-launch seams at
/// <see cref="ProDesktopHost"/>, and — once <see cref="ShellApp.Settings"/> exists — bridges the shell's
/// composition-root capabilities DOWN into <see cref="ProComposition"/> (settings, oobe.log, the repo
/// store, the sync-remote registrar, the shell window, the daemon version probe). Because it references
/// BOTH assemblies, it can name shell types (MainWindow, RepoCatalog, …) AND Pro-UI types
/// (ProDesktopHost, DaemonClient, …) that neither side may name across the one-way boundary.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Git-editor / credential self-invocation shims run and return FIRST (before the single-instance
        // guard), so the app's own rebase/credential calls of itself are never blocked. The shim's exit
        // code is the contract with git: 0 means "the editor wrote your todo", so a failed shim MUST
        // report non-zero or git rebases with its own default plain-`pick` todo and calls it a success.
        if (ShellEntryPoint.TryHandleShim(args, out int shimExitCode))
        {
            Environment.ExitCode = shimExitCode;
            return;
        }

        // This process is the Pro edition: the full agent platform.
        ShellApp.Edition = new ProManifest();

        // The shell's Pro-launch seams → the Agents.UI implementations. Under the Client head these stay
        // null and the shell takes its MainguardOS-free path.
        ShellApp.ProDesktopStarter = ProDesktopHost.Start;
        ShellApp.VisualizedShutdownAsync = ProDesktopHost.RunVisualizedShutdownThenExitAsync;

        // Bridge shell capabilities DOWN into the Pro-UI composition seam — deferred to AfterInitialize so
        // ShellApp.Settings (created in App.Initialize) is available.
        ShellApp.AfterInitialize = WireProComposition;

        ShellEntryPoint.RunDesktop(args);
    }

    // Avalonia configuration / visual-designer entry point; delegates to the shell's shared builder.
    public static AppBuilder BuildAvaloniaApp() => ShellEntryPoint.BuildAvaloniaApp();

    /// <summary>
    /// Injects the shell's composition-root capabilities DOWN into the Pro-UI assembly's seam
    /// (<see cref="ProComposition"/>) once Settings exists — the exact inversion the design system uses for
    /// <c>ThemeManager.PersistKey</c>. Was the old shell-side <c>App.WireProComposition</c>; in the split
    /// layout it lives HERE, the only place that references both the shell (ShellApp.Settings, MainWindow,
    /// RepoCatalog, SyncRemoteRegistrar, the host-rail ViewModels) and the Pro UI (ProComposition,
    /// ProDesktopHost, DaemonClient, SandboxImageInstaller).
    /// </summary>
    private static void WireProComposition()
    {
        // The per-agent workspace Dock theme the shell's App.axaml omits (Dock is a Pro dependency).
        ProDesktopHost.InjectProChrome();

        ProComposition.Settings = ShellApp.Settings;
        ProComposition.LogOobe = ShellApp.LogOobe;

        ProComposition.ShowShellToast = static (message, isError) =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.DataContext is MainWindowViewModel shell)
            {
                shell.ShowToast(message, isError);
            }
        };

        // Build the shell's main window from the (optional) startup result — the Pro OOBE / startup loaders
        // swap the desktop's MainWindow to it on completion. Only the degraded-banner TEXT crosses the
        // boundary (the shell VM never names the Pro StartupResult).
        ProComposition.CreateShellWindow = static result =>
            new MainWindow { DataContext = new MainWindowViewModel(result?.DegradedBanner) };

        // (ProComposition.RebuildSandboxImages defaults to SandboxImageInstaller.RunAsync inside the Pro-UI
        // assembly — no shell dependency, so the head need not wire it.)
        ProComposition.AddReposToOsFactory = ProDesktopHost.CreateAddReposToOsViewModel;

        // The shell-owned repo store + the daemon-provision→sync-remote pipeline (bridges the Pro-UI
        // DaemonClient to the shell's SyncRemoteRegistrar) the OOBE / Add-Repos steps call.
        ProComposition.PersistRepo = RepoCatalog.EnsureRegistered;
        ProComposition.ProvisionRepoIntoOs = ProvisionRepoIntoOsAsync;

        // The host-collab rail destinations (Pull requests / Issues / Notifications / Releases) name the
        // shell's own ViewModels — which the Pro-UI assembly must not reference, so the head injects them
        // into ProManifest's rail-composition seam (was EditionManifests' static ctor).
        ProComposition.HostRailSections = HostRailSections;

        // The Settings "About / versions" daemon/OS probe (null under the Client head).
        ShellVersionProbe.Query = ProDesktopHost.QueryDaemonVersionsAsync;
    }

    /// <summary>The shared host-collab rail destinations, naming the shell's host ViewModels (2f — moved off
    /// the removed <c>EditionManifests</c>). ProManifest composes its three Pro destinations with these.</summary>
    private static readonly IReadOnlyList<RailSectionDescriptor> HostRailSections = new RailSectionDescriptor[]
    {
        new("PullRequests",  "Pull requests", "PullRequestIcon", true, RailAdornmentKind.None, typeof(PullRequestsViewModel)),
        new("Issues",        "Issues",        "IssueIcon",       true, RailAdornmentKind.None, typeof(IssuesViewModel)),
        new("Notifications", "Notifications", "BellIcon",        true, RailAdornmentKind.None, typeof(NotificationsViewModel)),
        new("Releases",      "Releases",      "TagIcon",         true, RailAdornmentKind.None, typeof(ReleasesViewModel)),
    };

    /// <summary>The OOBE / Add-Repos per-repo pipeline (was <c>App.ProvisionRepoIntoOsAsync</c>): provision
    /// the host repo into the daemon (one gRPC touch-point, generous deadline) then register the
    /// daemon-resolved sync remote idempotently. Bridges the Pro-UI DaemonClient to the shell's
    /// SyncRemoteRegistrar — which only the head may name together.</summary>
    private static async Task ProvisionRepoIntoOsAsync(string repoPath, CancellationToken ct)
    {
        using var daemon = DaemonClient.ForLoopback();
        var provisioned = await daemon
            .ProvisionRepoAsync(repoPath, ct, deadline: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        new SyncRemoteRegistrar(new GitService())
            .Register(repoPath, provisioned.SyncRemoteName, provisioned.SyncRemoteUrl);
    }
}
