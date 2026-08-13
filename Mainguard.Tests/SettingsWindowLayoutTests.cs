using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.Git.Actions;
using Mainguard.Git.Models;
using Mainguard.Git.Security;
using Mainguard.Git.Services;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// File → Settings: every page must fit the window it is shown in, at the SMALLEST size the window can
/// be dragged to. The owner's Toolchains screenshot showed the opposite — the "Write file" /
/// "Stage &amp; commit" / "Push" / "Install toolchain" buttons ran off the right edge and the page did
/// not reflow — because the pages were built from fixed pixel widths (label columns, MaxWidth-bounded
/// prose sitting in horizontal StackPanels, which measure with INFINITE width) instead of star columns.
///
/// <para>These tests host each real page view inside the REAL <see cref="SettingsWindow"/>, sized to its
/// own declared <c>MinWidth</c>/<c>MinHeight</c>, run a real layout pass, and then walk the visual tree
/// asserting that nothing is arranged past the page's own right edge. They therefore measure the same
/// thing the eye does — clipped content — rather than a proxy for it. The window's minimum size is read
/// FROM the window, so shrinking it below what the pages survive fails here rather than in a screenshot.</para>
///
/// <para>No display is required: <c>[AvaloniaFact]</c> runs the headless Skia app from
/// <c>Headless/TestAppBuilder.cs</c>.</para>
/// </summary>
public class SettingsWindowLayoutTests
{
    /// <summary>Sub-pixel arrangement noise; anything past this is real, visible clipping.</summary>
    private const double Epsilon = 0.5;

    // ---- the harness -------------------------------------------------------------------------------

    /// <summary>
    /// Shows the real Settings window at the smallest size it allows, drops <paramref name="page"/> into
    /// the real content ScrollViewer, and returns every visual arranged past the page's right edge.
    /// </summary>
    private static IReadOnlyList<string> OverflowsAtMinimumSize(Control page, out double viewport)
    {
        var window = new SettingsWindow();
        window.Show();
        window.UpdateLayout();

        var scroller = window.GetControl<ScrollViewer>("PageScroller");

        // The headless platform hands every window its own client size and ignores Window.Width, so the
        // minimum is applied to the content area directly: exactly the room the page has when the window
        // is dragged to its own declared MinWidth/MinHeight, with the real sidebar and title bar measured
        // rather than assumed.
        var chromeWidth = window.Bounds.Width - scroller.Bounds.Width;
        var chromeHeight = window.Bounds.Height - scroller.Bounds.Height;
        scroller.Width = window.MinWidth - chromeWidth;
        scroller.Height = window.MinHeight - chromeHeight;

        scroller.Content = page;

        // Twice: pass one settles text wrapping and reveals the vertical scrollbar, pass two lays the
        // content out inside the width that scrollbar leaves behind.
        window.UpdateLayout();
        window.UpdateLayout();

        viewport = scroller.Viewport.Width - scroller.Padding.Left - scroller.Padding.Right;

        var problems = new List<string>();

        // 1. The page must not want more width than it was given. Horizontal scrolling is off (a settings
        //    page that scrolls sideways is the defect, not the fix), so every pixel it wants beyond the
        //    viewport is a pixel the window edge cuts off.
        if (page.DesiredSize.Width > page.Bounds.Width + Epsilon)
        {
            problems.Add(Describe(page)
                + $" wants {F(page.DesiredSize.Width)} but was given {F(page.Bounds.Width)}"
                + $" (viewport {F(viewport)})");
        }

        // 2. Nothing inside the page may be arranged past the page's own right edge. This is what a
        //    fixed-width child inside a star column does: the Grid hands it less than it asked for and
        //    it draws over (or past) whatever is to its right.
        var right = page.Bounds.Width;
        foreach (var visual in page.GetVisualDescendants().OfType<Visual>())
        {
            if (visual is Control { IsVisible: false }) continue;
            if (visual.Bounds.Width <= 0 || visual.Bounds.Height <= 0) continue;

            var corner = visual.TranslatePoint(new Point(visual.Bounds.Width, 0), page);
            if (corner is null) continue;
            if (corner.Value.X > right + Epsilon)
                problems.Add(Describe(visual) + $" ends at {F(corner.Value.X)}, page is {F(right)} wide");
        }

        window.Close();
        return problems;
    }

    private static void AssertFits(Control page, string pageName)
    {
        var problems = OverflowsAtMinimumSize(page, out var viewport);
        if (problems.Count == 0) return;

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Settings → {pageName} is clipped at the window's minimum size ");
        sb.Append(CultureInfo.InvariantCulture, $"({F(viewport)}px of page width). {problems.Count} overflowing visual(s):");
        foreach (var p in problems.Take(12))
            sb.Append(CultureInfo.InvariantCulture, $"{Environment.NewLine}  · {p}");
        Assert.Fail(sb.ToString());
    }

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Describe(Visual v)
    {
        var name = v is Control c && !string.IsNullOrEmpty(c.Name) ? $" '{c.Name}'" : string.Empty;
        var text = v switch
        {
            TextBlock tb when !string.IsNullOrWhiteSpace(tb.Text) => $" \"{Trim(tb.Text!)}\"",
            ContentControl { Content: string s } when !string.IsNullOrWhiteSpace(s) => $" \"{Trim(s)}\"",
            _ => string.Empty,
        };
        return v.GetType().Name + name + text;
    }

    private static string Trim(string s) =>
        s.Length <= 44 ? s.Replace(Environment.NewLine, " ") : s[..44].Replace(Environment.NewLine, " ") + "…";

    // ---- one test per Settings page ----------------------------------------------------------------

    [AvaloniaFact]
    public void General_FitsTheWindowAtItsMinimumSize() =>
        AssertFits(new GeneralSettingsView { DataContext = GeneralVm() }, "General");

    [AvaloniaFact]
    public void KeyboardShortcuts_FitsTheWindowAtItsMinimumSize() =>
        AssertFits(new ShortcutSettingsView { DataContext = ShortcutsVm() }, "Keyboard Shortcuts");

    [AvaloniaFact]
    public void Accounts_FitsTheWindowAtItsMinimumSize() =>
        AssertFits(new AccountsView { DataContext = new AccountsViewModel(new FakeKeyring()) }, "Accounts");

    [AvaloniaFact]
    public void SshKeys_FitsTheWindowAtItsMinimumSize() =>
        AssertFits(new SshKeysView { DataContext = new SshKeysViewModel() }, "SSH Keys");

    [AvaloniaFact]
    public void AgentClis_FitsTheWindowAtItsMinimumSize() =>
        AssertFits(new AgentCliSettingsView { DataContext = AgentClisVm() }, "Agent CLIs");

    [AvaloniaFact]
    public void Toolchains_FitsTheWindowAtItsMinimumSize() =>
        AssertFits(new ToolchainSettingsView { DataContext = ToolchainsVm() }, "Toolchains");

    /// <summary>
    /// The exact surface in the owner's screenshot: the four-step declaration flow, in the state that
    /// screenshot was taken in (nothing declared anywhere, repository on <c>master</c>). Every step then
    /// renders its longest sentence — the refusal — next to its button, which is the worst case for width.
    /// </summary>
    [AvaloniaFact]
    public void ToolchainDeclaration_FitsTheWindowAtItsMinimumSize() =>
        AssertFits(new ToolchainDeclarationView { DataContext = DeclarationVm() }, "Toolchains → declaration flow");

    [AvaloniaFact]
    public void PrIntake_FitsTheWindowAtItsMinimumSize() =>
        AssertFits(new PrIntakeSettingsView { DataContext = new PrIntakeSettingsViewModel(new InMemoryPrIntakeGateway()) },
            "PR Intake");

    [AvaloniaFact]
    public void MainguardOs_FitsTheWindowAtItsMinimumSize() =>
        AssertFits(new MainguardOsPageView { DataContext = MainguardOsVm() }, "Mainguard OS");

    [AvaloniaFact]
    public void DaemonLogs_FitsTheWindowAtItsMinimumSize() =>
        AssertFits(new DaemonLogsView { DataContext = DaemonLogsVm() }, "Daemon Logs");

    [AvaloniaFact]
    public void About_FitsTheWindowAtItsMinimumSize() =>
        AssertFits(new VersionsView { DataContext = new VersionsViewModel() }, "About");

    // ---- representative page state -----------------------------------------------------------------
    //
    // Every page is given its WIDEST realistic content: long repository/branch names, long paths, long
    // refusal sentences. A page that only fits when its strings are short is not responsive.

    private static GeneralSettingsViewModel GeneralVm() =>
        new(new FakeSettings(), hasAgentPlatform: true,
            new RelayCommand<string>(_ => { }), new RelayCommand<string>(_ => { }), () => { });

    private static ShortcutSettingsViewModel ShortcutsVm() =>
        new(ShortcutMap.Default,
            new (string, string)[]
            {
                (ActionIds.OpenCommandPalette, "Open the command palette"),
                (ActionIds.Commit, "Commit the staged changes"),
                (ActionIds.Push, "Push the current branch to its upstream remote"),
                (ActionIds.NewBranch, "Create a branch from the current commit"),
                (ActionIds.ManageSubmodules, "Manage this repository's submodules"),
            },
            _ => { });

    private static AgentCliSettingsViewModel AgentClisVm() =>
        new(new[]
        {
            new AgentCliRowViewModel("claude-code", "Claude Code (Anthropic's official CLI)", "1.2.34", isInstalled: true),
            new AgentCliRowViewModel("codex", "Codex CLI", "0.9.1"),
        });

    private static ToolchainSettingsViewModel ToolchainsVm() =>
        new(new[]
            {
                new ToolchainRowViewModel("python-3", "Python 3.12 (CPython, checksum-pinned)",
                    "The interpreter every Python repository in this workspace is built and tested with.",
                    "3.12.4", isInstalled: true,
                    detail: "Python 3.12.4 (main, Jun  7 2026, 10:12:03) [GCC 13.2.0] on linux"),
                new ToolchainRowViewModel("node-22", "Node.js 22 LTS",
                    "The runtime a JavaScript or TypeScript repository asks for by name.", "22.4.1"),
            },
            declaration: DeclarationVm());

    /// <summary>The owner's state: nothing declared, nothing on disk, repository on <c>master</c>.</summary>
    private static ToolchainDeclarationViewModel DeclarationVm() =>
        new("mainguard-control-center", "master", "master", new[] { "python-3", "node-22" });

    private static MainguardOsPageViewModel MainguardOsVm() =>
        new(new AddReposToOsViewModel(), () => System.Threading.Tasks.Task.CompletedTask);

    private static DaemonLogsViewModel DaemonLogsVm() =>
        new("2026-08-12T09:14:02.881Z  info  mainguardd  worker/agent-7 accepted a control frame\n"
            + "2026-08-12T09:14:02.902Z  warn  mainguardd  quarantine remote refused a non-fast-forward push\n");

    // ---- seams -------------------------------------------------------------------------------------

    private sealed class FakeSettings : ISettingsService
    {
        public UserPreferences Current { get; } = new();
        public void Update(Action<UserPreferences> updateAction) => updateAction(Current);
        public void Load() { }
        public void Save() { }
    }

    private sealed class FakeKeyring : ISecureKeyring
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public void SaveSecret(string key, string secret) => _values[key] = secret;
        public string? RetrieveSecret(string key) => _values.TryGetValue(key, out var v) ? v : null;
        public void DeleteSecret(string key) => _values.Remove(key);
    }
}
