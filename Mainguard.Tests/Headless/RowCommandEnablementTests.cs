using System;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

/// <summary>
/// The "visible but permanently disabled button" regression (owner report: <i>"the update cli button
/// isn't clickable"</i>), pinned at the level the user actually experiences — a real
/// <see cref="AgentCliSettingsView"/> in a real visual tree, asserting the rendered
/// <see cref="Button"/> is ENABLED, not merely visible.
///
/// <para>The bug class: the per-row Install/Update/Revert commands live on the PARENT
/// (<see cref="AgentCliSettingsViewModel"/>) but their <c>CanExecute</c> predicates read state off the
/// ROW. <c>[NotifyCanExecuteChangedFor]</c> only fires from the parent's own <c>IsBusy</c>, so a row
/// gaining an update raised <c>PropertyChanged</c> (the button became VISIBLE — that path always
/// worked) while <c>ICommand.CanExecuteChanged</c> never fired. Avalonia's <see cref="Button"/>
/// caches the last <c>CanExecute</c> result, so the button stayed disabled forever.</para>
///
/// <para>Visibility is deliberately NOT the assertion here: visibility is exactly what already
/// worked. <c>IsEffectivelyEnabled</c> is the only thing that separates fixed from broken.</para>
/// </summary>
public class RowCommandEnablementTests
{
    [AvaloniaFact]
    public void UpdateButton_ShouldBecomeClickable_WhenTheRowGainsAnAvailableUpdate()
    {
        var row = new AgentCliRowViewModel("claude-code", "Claude Code", "2.1.210", isInstalled: true);
        var vm = new AgentCliSettingsViewModel(new[] { row });

        var win = Show(vm);
        try
        {
            // Rendered while up to date: the Update button exists but is hidden and disabled. This is
            // the state whose stale CanExecute the bug froze.
            var button = FindRowButton(win, b => ReferenceEquals(b.Command, vm.UpdateCommand));
            Assert.False(button.IsVisible);
            Assert.False(button.IsEffectivelyEnabled);

            // The updater annotates the row (AgentCliSettingsViewModel.AnnotateUpdatesAsync).
            row.UpdateAvailableVersion = "2.1.220";
            Settle();

            Assert.True(button.IsVisible, "the row offers an update, so the button must show");
            Assert.True(button.IsEffectivelyEnabled,
                "the Update button rendered visible but DISABLED — UpdateCommand.CanExecute was last "
                + "evaluated when the row had no update and was never re-published, so the user can see "
                + "the button and cannot click it.");
        }
        finally
        {
            HarnessHygiene.Teardown(win);
        }
    }

    [AvaloniaFact]
    public void RevertButton_ShouldBecomeClickable_WhenTheRowGainsAPreviousVersion()
    {
        // Same shape as Update — CanRevert also reads row state the parent never re-publishes.
        var row = new AgentCliRowViewModel("claude-code", "Claude Code", "2.1.220", isInstalled: true);
        var vm = new AgentCliSettingsViewModel(new[] { row });

        var win = Show(vm);
        try
        {
            var button = FindRowButton(win, b => ReferenceEquals(b.Command, vm.RevertCommand));
            Assert.False(button.IsEffectivelyEnabled);

            row.PreviousVersion = "2.1.210";
            Settle();

            Assert.True(button.IsVisible);
            Assert.True(button.IsEffectivelyEnabled,
                "the Revert button rendered visible but DISABLED — same stale-CanExecute bug as Update.");
        }
        finally
        {
            HarnessHygiene.Teardown(win);
        }
    }

    [AvaloniaFact]
    public void InstallButton_ShouldBecomeClickableAgain_WhenAnInstallFails()
    {
        // A row that finished installing and came back not-installed (the retry case) must re-offer a
        // LIVE Install button, not a dead one.
        var row = new AgentCliRowViewModel("codex", "OpenAI Codex CLI", "0.144.4", isInstalled: true);
        var vm = new AgentCliSettingsViewModel(new[] { row });

        var win = Show(vm);
        try
        {
            var button = FindRowButton(win, b => ReferenceEquals(b.Command, vm.InstallCommand));
            Assert.False(button.IsEffectivelyEnabled);

            row.IsInstalled = false; // the probe now says it is gone → CanInstall flips true
            Settle();

            Assert.True(button.IsVisible);
            Assert.True(button.IsEffectivelyEnabled,
                "the Install button rendered visible but DISABLED — same stale-CanExecute bug as Update.");
        }
        finally
        {
            HarnessHygiene.Teardown(win);
        }
    }

    private static Window Show(AgentCliSettingsViewModel vm)
    {
        var win = new Window
        {
            Width = 620,
            Height = 400,
            Content = new AgentCliSettingsView { DataContext = vm },
        };
        win.Show();
        Settle();
        return win;
    }

    /// <summary>The one row-template button whose Command is the given parent command.</summary>
    private static Button FindRowButton(Window win, Func<Button, bool> match)
    {
        var button = win.GetVisualDescendants().OfType<Button>().SingleOrDefault(match);
        Assert.True(button is not null, "the row template did not realize the expected button");
        return button!;
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
    }
}
