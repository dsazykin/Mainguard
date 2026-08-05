using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// P2-22 §J-5: renders the "Agent CLIs" settings window (the add-more-later surface over the pinned
// starter channel) offscreen in ALL FIVE THEMES across every state it can show — the normal list
// (installed + not-installed + a long-name truncation row), the mid-install state (per-row progress,
// Cancel install, serialized rows), a per-row failure with its actionable cause, the loading line,
// and the catalog-read error card — so the design-system pass (tokens, component classes, icon+text
// state encoding, light Daylight Loom) can be reviewed without a display. PNGs land in the
// gitignored artifacts_headless/.
public class AgentCliSettingsRenderHarness
{
    [AvaloniaFact]
    public void Capture_AgentCliSettings_AllThemes()
    {
        try
        {
            foreach (var theme in Mainguard.UI.Theming.ThemeManager.Themes)
            {
                Mainguard.UI.Theming.ThemeManager.Apply(theme.Key, persist: false);
                Settle();

                Capture(theme.Key, "list", new AgentCliSettingsViewModel(ListMix()));
                Capture(theme.Key, "updatable", UpdatableVm());
                Capture(theme.Key, "installing", InstallingVm());
                Capture(theme.Key, "failure", new AgentCliSettingsViewModel(FailureMix()));
                Capture(theme.Key, "loading", new AgentCliSettingsViewModel(
                    Array.Empty<AgentCliRowViewModel>(), isLoading: true));
                Capture(theme.Key, "load_error", new AgentCliSettingsViewModel(
                    Array.Empty<AgentCliRowViewModel>(),
                    loadError: "Mainguard could not read its agent-CLI catalog: the Mainguard environment "
                        + "did not answer (is it still starting?). If the Mainguard environment is not "
                        + "running, open Mainguard again to start it, then Refresh."));
            }
        }
        finally
        {
            Mainguard.UI.Theming.ThemeManager.Apply(Mainguard.UI.Theming.ThemeManager.DefaultKey, persist: false);
        }
    }

    private static IEnumerable<AgentCliRowViewModel> ListMix() => new[]
    {
        new AgentCliRowViewModel("claude-code", "Claude Code", "2.1.210", isInstalled: true),
        new AgentCliRowViewModel("codex", "OpenAI Codex CLI", "0.144.4"),
        new AgentCliRowViewModel("opencode", "OpenCode", "1.18.1"),
        new AgentCliRowViewModel("long", "An Agent CLI With A Deliberately Very Long Product Name That Truncates", "10.20.300-rc.4+build.99"),
    };

    /// <summary>The Mainguard-managed updater's annotations, applied AFTER construction exactly as
    /// <c>AnnotateUpdatesAsync</c> does — so this state also exercises the row → command
    /// <c>CanExecuteChanged</c> bridge that keeps Update/Revert clickable (the "visible but disabled"
    /// regression; <see cref="RowCommandEnablementTests"/> asserts the enablement, this shows it).</summary>
    private static AgentCliSettingsViewModel UpdatableVm()
    {
        var rows = new[]
        {
            new AgentCliRowViewModel("claude-code", "Claude Code", "2.1.210", isInstalled: true),
            new AgentCliRowViewModel("codex", "OpenAI Codex CLI", "0.144.4", isInstalled: true),
            new AgentCliRowViewModel("opencode", "OpenCode", "1.18.1"),
        };
        var vm = new AgentCliSettingsViewModel(rows);
        vm.Clis[0].UpdateAvailableVersion = "2.1.220";              // update offered
        vm.Clis[1].PreviousVersion = "0.144.3";                     // revert offered
        return vm;
    }

    private static AgentCliSettingsViewModel InstallingVm()
    {
        var rows = new[]
        {
            new AgentCliRowViewModel("claude-code", "Claude Code", "2.1.210", isInstalled: true),
            new AgentCliRowViewModel("codex", "OpenAI Codex CLI", "0.144.4")
            {
                IsInstalling = true,
                StatusMessage = "Downloading, verifying, and installing — this can take a few minutes "
                    + "on a slow connection.",
            },
            new AgentCliRowViewModel("opencode", "OpenCode", "1.18.1"),
        };
        var vm = new AgentCliSettingsViewModel(rows) { IsBusy = true };
        return vm;
    }

    private static IEnumerable<AgentCliRowViewModel> FailureMix() => new[]
    {
        new AgentCliRowViewModel("claude-code", "Claude Code", "2.1.210", isInstalled: true),
        new AgentCliRowViewModel("codex", "OpenAI Codex CLI", "0.144.4")
        {
            IsFailed = true,
            StatusMessage = "codex could not be installed inside the Mainguard VM: npm exited 243 "
                + "(network unreachable). You can try again from Settings once setup finishes; "
                + "Mainguard works without it.",
        },
        new AgentCliRowViewModel("opencode", "OpenCode", "1.18.1"),
    };

    private void Capture(string themeKey, string state, AgentCliSettingsViewModel vm)
    {
        // AgentCliSettingsView is a UserControl now (embedded as a Settings page) — wrap it in a
        // plain Window for the headless render harness, same as every other migrated page harness.
        var win = new Avalonia.Controls.Window
        {
            Width = 620,
            Height = 560,
            Content = new AgentCliSettingsView { DataContext = vm },
        };
        win.Show();
        Settle();

        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(ArtifactsDir(), $"agent_cli_settings_{themeKey}_{state}.png");
        frame!.Save(path);
        Assert.True(new FileInfo(path).Length > 0, $"agent cli settings {themeKey}/{state} PNG is empty");

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
