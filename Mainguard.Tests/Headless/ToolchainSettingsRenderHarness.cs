using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// Renders the "Toolchains" settings page (the human-facing half of the user-managed toolchain channel)
// offscreen in ALL FIVE THEMES across every state it can show — the normal list (installed with its
// probe output + not-installed + the "a different version is present" row + a long-name truncation
// row), a mid-install row with its progress line, a per-row failure with the typed channel cause, the
// loading line, and the catalog-read error card — so the design-system pass (tokens, component
// classes, icon+text state encoding, light Daylight Loom) can be reviewed without a display. PNGs land
// in the gitignored artifacts_headless/. Design constructors only: no channel, no VM.
public class ToolchainSettingsRenderHarness
{
    [AvaloniaFact]
    public void Capture_ToolchainSettings_AllThemes()
    {
        try
        {
            foreach (var theme in Mainguard.UI.Theming.ThemeManager.Themes)
            {
                Mainguard.UI.Theming.ThemeManager.Apply(theme.Key, persist: false);
                Settle();

                Capture(theme.Key, "list", new ToolchainSettingsViewModel(ListMix()));

                // The page as the owner met it: the declaration section included, in the state that
                // produced the clipping report — nothing declared, nothing on disk, repo on `master`, so
                // every step shows its longest sentence (its refusal) beside its button. Captured at BOTH
                // ends of the Settings window's size range, because "it fits" is a claim about the
                // narrow end and "it reads" is a claim about the wide one.
                Capture(theme.Key, "declaration", DeclarationVm(), SettingsPageWidth(1040), height: 1500);
                Capture(theme.Key, "declaration_min", DeclarationVm(), SettingsPageWidth(640), height: 1800);
                Capture(theme.Key, "installing", InstallingVm());
                Capture(theme.Key, "failure", new ToolchainSettingsViewModel(FailureMix()));
                Capture(theme.Key, "loading", new ToolchainSettingsViewModel(
                    Array.Empty<ToolchainRowViewModel>(), isLoading: true));
                Capture(theme.Key, "load_error", new ToolchainSettingsViewModel(
                    Array.Empty<ToolchainRowViewModel>(),
                    loadError: "Mainguard could not read its toolchain catalog: the Mainguard environment "
                        + "did not answer (is it still starting?). If the Mainguard environment is not "
                        + "running, open Mainguard again to start it, then Refresh."));
            }
        }
        finally
        {
            Mainguard.UI.Theming.ThemeManager.Apply(Mainguard.UI.Theming.ThemeManager.DefaultKey, persist: false);
        }
    }

    private static IEnumerable<ToolchainRowViewModel> ListMix() => new[]
    {
        new ToolchainRowViewModel("python-3", "Python 3",
            "CPython 3.12 with pip — runs a repository's Python test suite and installs its dependencies from PyPI.",
            "3.12.13", isInstalled: true, detail: "3.12.13 pip 24.3.1"),
        new ToolchainRowViewModel("node-22", "Node.js 22",
            "Node 22 with npm — runs a repository's JavaScript and TypeScript test suites.",
            "22.14.0"),
        // The state the channel exists to make visible: it RUNS, but it is not what this Mainguard
        // pinned, so it is presented as not installed — in words, not as a bare "no".
        new ToolchainRowViewModel("go-1", "Go 1.24",
            "The Go toolchain — builds and tests a repository's Go modules.",
            "1.24.2", detail: "A different version is present — expected 1.24.2, the probe reported: go version go1.22.6 linux/amd64"),
        new ToolchainRowViewModel("long", "A Language Toolchain With A Deliberately Very Long Product Name That Truncates",
            "A summary long enough to wrap onto a second line so the row's vertical rhythm is reviewable too.",
            "10.20.300-rc.4+build.99"),
    };

    private static ToolchainSettingsViewModel InstallingVm()
    {
        var rows = new[]
        {
            new ToolchainRowViewModel("python-3", "Python 3",
                "CPython 3.12 with pip — runs a repository's Python test suite and installs its dependencies from PyPI.",
                "3.12.13", isInstalled: true, detail: "3.12.13 pip 24.3.1"),
            new ToolchainRowViewModel("node-22", "Node.js 22",
                "Node 22 with npm — runs a repository's JavaScript and TypeScript test suites.",
                "22.14.0")
            {
                IsWorking = true,
                StatusMessage = "Verifying the checksum…",
            },
        };
        return new ToolchainSettingsViewModel(rows) { IsBusy = true };
    }

    private static IEnumerable<ToolchainRowViewModel> FailureMix() => new[]
    {
        new ToolchainRowViewModel("python-3", "Python 3",
            "CPython 3.12 with pip — runs a repository's Python test suite and installs its dependencies from PyPI.",
            "3.12.13", isInstalled: true, detail: "3.12.13 pip 24.3.1"),
        new ToolchainRowViewModel("node-22", "Node.js 22",
            "Node 22 with npm — runs a repository's JavaScript and TypeScript test suites.",
            "22.14.0")
        {
            IsFailed = true,
            StatusMessage = "Node.js 22's download did not match the pinned checksum, so it was discarded "
                + "and nothing was unpacked. Expected sha256 "
                + "2222222222222222222222222222222222222222222222222222222222222222, got "
                + "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc.",
        },
    };

    /// <summary>The Toolchains page with its second section — the four-step declaration flow — in the
    /// state that has no declaration on either side, which is the state every step refuses in.</summary>
    private static ToolchainSettingsViewModel DeclarationVm() =>
        new(ListMix(), declaration: new ToolchainDeclarationViewModel(
            "mainguard-control-center", "master", "master",
            new[] { "python-3", "node-22", "go-1" }));

    /// <summary>The width a Settings page is given inside a Settings window of <paramref name="windowWidth"/>:
    /// the window minus the 220px page rail and the content gutter. Derived rather than typed so a change
    /// to the window's chrome moves these captures with it.</summary>
    private static int SettingsPageWidth(int windowWidth) => windowWidth - 220 - 56;

    private void Capture(string themeKey, string state, ToolchainSettingsViewModel vm, int width = 620, int height = 560)
    {
        // The page is a UserControl (it embeds in the Settings window) — wrap it in a plain Window for
        // the headless render harness, same as every other migrated page harness.
        var win = new Avalonia.Controls.Window
        {
            Width = width,
            Height = height,
            Content = new ToolchainSettingsView { DataContext = vm },
        };
        win.Show();
        Settle();

        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(ArtifactsDir(), $"toolchain_settings_{themeKey}_{state}.png");
        frame!.Save(path);
        Assert.True(new FileInfo(path).Length > 0, $"toolchain settings {themeKey}/{state} PNG is empty");

        HarnessHygiene.Teardown(win);
        Settle();
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
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
