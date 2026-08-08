using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.Git.Models;
using Mainguard.Tests.Fixtures;
using Mainguard.UI.Theming;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// Renders the real MainWindow shell with a repository open, so the top-nav toolbar — the branch
// dropdown plus the grouped Sync / Tools menus (#67; no Collaborate on phase2 — the host surfaces
// live in the section rail) — and the opening overlay (#63) can be inspected. Visual review,
// not pass/fail.
public class MainWindowShellRenderHarness
{
    [AvaloniaFact]
    public void Capture_MainWindow_TopNavAndShell()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("readme.md", "# demo repo\n", "chore: seed");
        fx.CommitFile("src/app.cs", "class App { }\n", "feat: app");
        fx.CreateBranch("feature/work");

        try
        {
            ThemeManager.Apply("MidnightLoom", persist: false);

            var vm = new MainWindowViewModel();
            var win = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            win.Show();

            vm.OpenRepository(new Repository { Path = fx.RepoPath, DisplayName = "demo" });

            // Wait for the async open (Task.Run VM build) to land, then let the dashboard's initial
            // load settle so the toolbar and workspace are fully painted.
            for (int i = 0; i < 200 && vm.CurrentWorkspace == null; i++) Pump();
            for (int i = 0; i < 80; i++) Pump();

            Assert.NotNull(vm.CurrentWorkspace);

            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "mainwindow_shell.png"));
            HarnessHygiene.Teardown(win);
        }
        finally
        {
            ThemeManager.Apply("MidnightLoom", persist: false);
        }
    }

    [AvaloniaFact]
    public void Capture_Toasts_Stacked()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("readme.md", "# demo repo\n", "chore: seed");

        try
        {
            ThemeManager.Apply("MidnightLoom", persist: false);

            var vm = new MainWindowViewModel();
            var win = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            win.Show();
            vm.OpenRepository(new Repository { Path = fx.RepoPath, DisplayName = "demo" });
            for (int i = 0; i < 200 && vm.CurrentWorkspace == null; i++) Pump();
            for (int i = 0; i < 40; i++) Pump();

            // Stack a few toasts (#85): a normal one, an error one, and a long one to show trimming.
            var dash = Assert.IsType<RepoDashboardViewModel>(vm.CurrentWorkspace);
            dash.ShowNotification("Fetched origin — 3 new commits.", isError: false);
            dash.ShowNotification("Push failed: remote rejected (non-fast-forward).", isError: true);
            dash.ShowNotification("Rebased feature/login onto main; resolved 2 conflicts and re-applied 5 commits successfully.", isError: false);
            for (int i = 0; i < 80; i++) Pump();
            Assert.Equal(3, dash.Toasts.Count);

            // The DASHBOARD toast host must agree with the shell-level one: bottom-right. Its
            // Grid.Row was "1" against a single-row Grid (out of range, silently clamped back to 0);
            // this pins the corner so correcting that index cannot have moved it.
            var host = win.GetVisualDescendants().OfType<ItemsControl>()
                .Single(ic => ReferenceEquals(ic.ItemsSource, dash.Toasts));
            var origin = host.TranslatePoint(new Point(0, 0), win);
            Assert.True(origin.HasValue, "the dashboard toast host was not laid out");
            var bottom = origin!.Value.Y + host.Bounds.Height;
            var right = origin.Value.X + host.Bounds.Width;
            Assert.True(origin.Value.Y > win.Height / 2,
                $"dashboard toasts render at y={origin.Value.Y:F0} in a {win.Height:F0}px window — top half, not bottom-right.");
            Assert.True(win.Height - bottom < 60, $"dashboard toasts sit {win.Height - bottom:F0}px above the window bottom.");
            Assert.True(win.Width - right < 60, $"dashboard toasts sit {win.Width - right:F0}px from the right edge.");

            // Same window, every theme — the corner is a layout property, the palette is not.
            foreach (var theme in ThemeManager.Themes)
            {
                ThemeManager.Apply(theme.Key, persist: false);
                for (int i = 0; i < 10; i++) Pump();
                win.CaptureRenderedFrame()?.Save(
                    Path.Combine(ArtifactsDir(), $"toasts_stacked_{theme.Key}.png"));
            }

            HarnessHygiene.Teardown(win);
        }
        finally
        {
            ThemeManager.Apply("MidnightLoom", persist: false);
        }
    }

    // The shell-level toast host (MainWindowViewModel.Toasts — daemon auto-update outcomes and
    // anything else that outranks one repo) must sit in the BOTTOM-right corner of the window, the
    // same corner RepoDashboardView's toasts use. It regressed to the TOP-right because the
    // ItemsControl carried no Grid.Row and so landed in row 0 — the 44px custom title bar — where
    // VerticalAlignment="Bottom" anchors to the bottom of the TITLE BAR, i.e. the top of the window
    // (and grew that Auto row to fit). Geometry is asserted, not eyeballed; the PNGs are for the
    // design-system pass across all five themes.
    [AvaloniaFact]
    public void ShellToasts_ShouldSitInTheBottomRightCorner_InEveryTheme()
    {
        try
        {
            foreach (var theme in ThemeManager.Themes)
            {
                ThemeManager.Apply(theme.Key, persist: false);

                var vm = new MainWindowViewModel();
                var win = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
                win.Show();
                for (int i = 0; i < 20; i++) Pump();

                vm.ShowToast("Mainguard updated its background service to 0.9.14.", isError: false);
                vm.ShowToast("Mainguard could not reach the update server — it will retry later.", isError: true);
                for (int i = 0; i < 20; i++) Pump();

                var host = win.GetVisualDescendants().OfType<ItemsControl>()
                    .Single(ic => ReferenceEquals(ic.ItemsSource, vm.Toasts));
                var origin = host.TranslatePoint(new Point(0, 0), win);
                Assert.True(origin.HasValue, $"[{theme.Key}] the shell toast host was not laid out");

                var top = origin!.Value.Y;
                var left = origin.Value.X;
                var bottom = top + host.Bounds.Height;
                var right = left + host.Bounds.Width;

                // Bottom half of the window, not the title bar (the exact regression), and hugging
                // the right edge — the same corner as the dashboard's toasts.
                Assert.True(top > win.Height / 2,
                    $"[{theme.Key}] shell toasts render at y={top:F0} in a {win.Height:F0}px window — they are "
                    + "in the TOP half. The toast host must be anchored to the bottom of the CONTENT row, "
                    + "not the title-bar row.");
                Assert.True(win.Height - bottom < 60,
                    $"[{theme.Key}] shell toasts end {win.Height - bottom:F0}px above the window bottom — not bottom-anchored.");
                Assert.True(win.Width - right < 60,
                    $"[{theme.Key}] shell toasts end {win.Width - right:F0}px from the right edge — not right-anchored.");

                win.CaptureRenderedFrame()?.Save(
                    Path.Combine(ArtifactsDir(), $"shell_toasts_bottom_right_{theme.Key}.png"));
                HarnessHygiene.Teardown(win);
                for (int i = 0; i < 5; i++) Pump();
            }
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        }
    }

    // Newest-at-the-bottom stacking has to stay correct now that the host is bottom-anchored: a new
    // toast must grow the stack UPWARD (older toasts move away from the corner) rather than push the
    // newest one off the bottom edge.
    [AvaloniaFact]
    public void ShellToasts_ShouldStackUpward_NewestNearestTheCorner()
    {
        try
        {
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

            var vm = new MainWindowViewModel();
            var win = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
            win.Show();
            for (int i = 0; i < 20; i++) Pump();

            vm.ShowToast("First — the oldest toast.", isError: false);
            for (int i = 0; i < 15; i++) Pump();
            var host = win.GetVisualDescendants().OfType<ItemsControl>()
                .Single(ic => ReferenceEquals(ic.ItemsSource, vm.Toasts));
            var firstBottomAlone = host.TranslatePoint(new Point(0, host.Bounds.Height), win)!.Value.Y;

            vm.ShowToast("Second — the newest toast.", isError: false);
            for (int i = 0; i < 15; i++) Pump();

            Assert.Equal(2, vm.Toasts.Count);
            var stackBottom = host.TranslatePoint(new Point(0, host.Bounds.Height), win)!.Value.Y;
            var stackTop = host.TranslatePoint(new Point(0, 0), win)!.Value.Y;

            // The stack grew upward: the bottom edge stayed put, the top edge rose.
            Assert.True(Math.Abs(stackBottom - firstBottomAlone) < 2,
                $"the toast stack's bottom edge moved from {firstBottomAlone:F0} to {stackBottom:F0} — a new "
                + "toast must grow the stack upward, never push the corner-anchored one off-screen.");
            Assert.True(stackTop < stackBottom - 40, "the second toast did not extend the stack upward");
            Assert.True(stackBottom <= win.Height, "the toast stack overflows the window bottom");

            HarnessHygiene.Teardown(win);
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        }
    }

    [AvaloniaFact]
    public void Capture_SettingsWindow_PinnedMenuPicker()
    {
        try
        {
            ThemeManager.Apply("MidnightLoom", persist: false);

            var vm = new SettingsViewModel(
                Mainguard.App.Shell.App.Settings,
                hasAgentPlatform: false,
                setLayoutCommand: new CommunityToolkit.Mvvm.Input.RelayCommand<string>(_ => { }),
                setAgentPromptingCommand: new CommunityToolkit.Mvvm.Input.RelayCommand<string>(_ => { }),
                onPinsChanged: () => { },
                buildShortcutSettings: () => new ShortcutSettingsViewModel(
                    Mainguard.Git.Actions.ShortcutMap.FromPreferences(new System.Collections.Generic.Dictionary<string, string>()),
                    System.Array.Empty<(string Id, string Title)>(),
                    _ => { }),
                currentRepoPath: () => null,
                refreshCurrentWorkspace: null,
                proTools: null);
            var win = new SettingsWindow { DataContext = vm };
            win.Show();
            for (int i = 0; i < 30; i++) Pump();

            var general = Assert.IsType<GeneralSettingsViewModel>(vm.Pages[0].Content);
            Assert.NotEmpty(general.PinRows);

            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "settings_window.png"));
            HarnessHygiene.Teardown(win);
        }
        finally
        {
            ThemeManager.Apply("MidnightLoom", persist: false);
        }
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(25);
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
