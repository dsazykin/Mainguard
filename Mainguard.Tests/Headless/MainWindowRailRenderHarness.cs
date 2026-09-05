using System;
using System.IO;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.UI.Theming;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// Renders the integrated MainWindow (revised control-center shell): the section rail in its
// expanded and collapsed states, the Repo viewer section (default), and the coordinator
// section swapped in. Uses the real MainWindowViewModel — its ctor touches settings + the
// repo DB the same way the shipped app does at startup.
public class MainWindowRailRenderHarness
{
    [AvaloniaFact]
    public void Capture_MainWindow_Rail_Sections()
    {
        Mainguard.App.Shell.App.Edition = new Mainguard.Agents.UI.Editions.ProManifest();
        Mainguard.Agents.UI.Editions.ProComposition.OrchestratorServicesFactory = () => OrchestratorServices.FromSingle(new MockOrchestrator());
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        // Seed the locator as App.Initialize does for Pro (shell + Mainguard.Agents.UI) so the rail's
        // ContentControls resolve real Views, not the "Not Found:" placeholder. Restored at method exit.
        using var seed = HarnessHygiene.SeedViewAssemblies(Mainguard.App.Shell.App.Edition);
        var vm = new MainWindowViewModel();
        var win = new MainWindow { DataContext = vm, Width = 1420, Height = 920 };
        win.Show();
        Settle();

        Assert.True(vm.IsRepoSectionActive);
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "mainwindow_rail_repo.png"));

        vm.ShowCoordinatorSectionCommand.Execute(null);
        Settle();
        Assert.False(vm.IsRepoSectionActive);
        // IsCoordinatorFocus isn't part of the IAgentPlatformSurface seam; under the default Pro edition
        // ControlCenter is the concrete ControlCenterViewModel, so cast to read it.
        Assert.True(((ControlCenterViewModel)vm.ControlCenter!).IsCoordinatorFocus);
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "mainwindow_rail_coordinator.png"));

        vm.ToggleRailCommand.Execute(null); // collapsed: icons + tooltips only
        Settle();
        Assert.False(vm.IsRailExpanded);
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "mainwindow_rail_collapsed.png"));

        vm.ToggleRailCommand.Execute(null); // restore the persisted default
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void Capture_ResourceMonitor_TaskManager()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var ccVm = new ControlCenterViewModel();
        using var vm = ccVm.CreateResourceMonitor();
        var win = new Window { Content = new ResourceMonitorView { DataContext = vm }, Width = 720, Height = 520 };
        win.Show();
        Settle();

        Assert.Equal(4, vm.Rows.Count);
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "resource_monitor.png"));

        // The End-task confirmation names the agent and what is kept (C-1/C-2) — and "names the agent"
        // means the ROW's identity, not the CLI kind every row shares.
        vm.RequestEnd(vm.Rows[0]);
        Settle();
        Assert.True(vm.IsEndConfirmVisible);
        Assert.Contains(vm.Rows[0].IdentityLine, vm.EndConfirmTitle, StringComparison.Ordinal);
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "resource_monitor_end_confirm.png"));
        vm.CancelEndCommand.Execute(null);
        HarnessHygiene.Teardown(win);
        ccVm.Dispose();
    }

    [AvaloniaFact]
    public void Capture_RepoPicker()
    {
        Mainguard.App.Shell.App.Edition = new Mainguard.Agents.UI.Editions.ProManifest();
        Mainguard.Agents.UI.Editions.ProComposition.OrchestratorServicesFactory = () => OrchestratorServices.FromSingle(new MockOrchestrator());
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        using var seed = HarnessHygiene.SeedViewAssemblies(Mainguard.App.Shell.App.Edition);
        var vm = new MainWindowViewModel();
        var win = new RepoPickerWindow { DataContext = vm };
        win.Show();
        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "repo_picker.png"));
        HarnessHygiene.Teardown(win);
    }

    private static void Settle()
    {
        for (int i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
