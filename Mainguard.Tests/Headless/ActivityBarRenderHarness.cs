using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.ViewModels.Agents;
using Mainguard.Agents.UI.Views;
using Mainguard.Agents.UI.Views.Agents;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.UI.Theming;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// P2-13 test 5 (§5) / TI-P2-13.6: render the section rail — the "activity bar" — with its four
// scripted agents in EVERY registered theme (Daylight Loom included) for human visual review
// against ControlCenterDesign §0/§2/§4 (badge legibility, spacing, kill-switch prominence). Also
// captures the per-agent Dock.Avalonia workspace so the dock chrome gets a visual pass. PNGs land in
// artifacts_headless/.
public class ActivityBarRenderHarness
{
    private static readonly string[] ThemeKeys =
        { "MidnightLoom", "DaylightLoom", "Graphite", "Atelier" };

    [AvaloniaFact]
    public void ActivityBar_HeadlessPng_AllThemes()
    {
        // This harness renders the Pro agent rail, so select the Pro edition explicitly (step 2f: App.Edition
        // now defaults to Client, no longer Pro — each render harness names its edition).
        Mainguard.App.Shell.App.Edition = new Mainguard.Agents.UI.Editions.ProManifest();
        // Design render: inject the scripted mock behind the shipped seam so the rail shows representative
        // agents (the shipped app runs the DaemonClient-backed bundle — P2-47). Explicit, outside the app path.
        Mainguard.Agents.UI.Editions.ProComposition.OrchestratorServicesFactory = () => OrchestratorServices.FromSingle(new MockOrchestrator());
        // Seed the locator as App.Initialize does for Pro (shell + Mainguard.Agents.UI) — the bare
        // Mainguard.UI default can't resolve the Pro agent rail, so it would render "Not Found:". Restored
        // when the method's using scope closes.
        using var seed = HarnessHygiene.SeedViewAssemblies(Mainguard.App.Shell.App.Edition);

        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);
            var vm = new MainWindowViewModel();
            var win = new MainWindow { DataContext = vm, Width = 1420, Height = 920 };
            win.Show();
            Settle();

            Assert.True(vm.IsRailExpanded);
            // Agents isn't part of the slimmed IAgentPlatformSurface seam (2d); under the default Pro edition
            // the ControlCenter is the concrete VM, so cast to read the LIFO list.
            Assert.Equal(4, ((ControlCenterViewModel)vm.ControlCenter!).Agents.Count); // four scripted agents

            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"activitybar_rail_{theme}.png"));
            HarnessHygiene.Teardown(win);
        }

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    [AvaloniaFact]
    public void AgentWorkspace_Dock_FlightDeck_And_ConversationDeck()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        foreach (var (kind, name) in new[]
                 {
                     (WorkspaceLayoutKind.FlightDeck, "flightdeck"),
                     (WorkspaceLayoutKind.ConversationDeck, "conversationdeck"),
                 })
        {
            using var vm = new AgentWorkspaceViewModel(
                "loom-3", kind,
                terminal: "loom-3 $ pytest -q\n................  16 passed in 3.1s\nloom-3 $ ",
                diff: "agent diff — read-only\n+  def verify(self):\n+      return run_tests()",
                staging: "Staged\n  M  src/gateway.py\n  A  tests/test_budget.py");
            var win = new Window { Width = 1100, Height = 720, Content = new AgentWorkspaceView { DataContext = vm } };
            win.Show();
            Settle();
            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"agent_workspace_{name}.png"));
            win.Content = null;
            HarnessHygiene.Teardown(win);
        }
    }

    // W2 sibling: a COLLAPSED rail is icon-only, and Avalonia never falls back to ToolTip.Tip for the
    // accessible name (nor to a Grid/PathIcon Content), so every rail button announced nothing at all
    // to a screen reader. Each one now carries AutomationProperties.Name from its ViewModel, which is
    // why this holds in both rail states — and this test is what keeps it from silently regressing.
    [AvaloniaFact]
    public void Rail_Buttons_AreNamedForAutomation_EvenWhenCollapsed()
    {
        Mainguard.App.Shell.App.Edition = new Mainguard.Agents.UI.Editions.ProManifest();
        Mainguard.Agents.UI.Editions.ProComposition.OrchestratorServicesFactory =
            () => OrchestratorServices.FromSingle(new MockOrchestrator());
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        using var seed = HarnessHygiene.SeedViewAssemblies(Mainguard.App.Shell.App.Edition);

        var vm = new MainWindowViewModel();
        var win = new MainWindow { DataContext = vm, Width = 1420, Height = 920 };
        win.Show();
        Settle();
        try
        {
            vm.ToggleRailCommand.Execute(null); // icons only — the state the gap was reported in
            Settle();
            Assert.False(vm.IsRailExpanded);

            var railButtons = win.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("railItem"))
                .ToArray();
            Assert.NotEmpty(railButtons);

            var names = railButtons
                .Select(b => ControlAutomationPeer.CreatePeerForElement(b).GetName())
                .ToArray();

            // "Non-empty" is NOT the bar. Avalonia's ButtonAutomationPeer falls back to
            // Content?.ToString(), so an unnamed rail button reports the literal string
            // "Avalonia.Controls.Grid" — a name that exists and says nothing. An earlier version of
            // this very test asserted only non-emptiness and passed against the unfixed rail.
            var junk = names
                .Where(n => string.IsNullOrWhiteSpace(n) || n!.StartsWith("Avalonia.", StringComparison.Ordinal))
                .ToArray();
            Assert.True(junk.Length == 0,
                $"{junk.Length} of {railButtons.Length} rail buttons expose no real accessible name " +
                $"(Avalonia's Content.ToString() fallback): {string.Join(" | ", junk)}");

            // And the name must be the RIGHT text — the label the user would read when expanded.
            foreach (var section in vm.RailSections)
                Assert.Contains(section.Label, names);
        }
        finally
        {
            if (!vm.IsRailExpanded) vm.ToggleRailCommand.Execute(null); // restore the persisted default
            HarnessHygiene.Teardown(win);
        }
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
