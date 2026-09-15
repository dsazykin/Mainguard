using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mainguard.App.Shell.Editions;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.UI.Theming;
using Xunit;

namespace Mainguard.Tests.Headless;

/// <summary>
/// The shell's no-repo empty state ("Select a repository to begin" + a big folder glyph) is the first
/// thing a user sees with nothing open, and the glyph is the obvious click target — but it used to be
/// a bare <c>PathIcon</c>: no command, no focus, no automation peer, nothing to activate. The only
/// routes to the repository list were the title-bar "Select Repo" button and the Mac menu item.
///
/// These tests pin the icon as a real affordance for the same <c>OpenRepoPickerCommand</c> those two
/// entry points already use. They deliberately locate it the way an automation client does — walk UP
/// from the glyph looking for an <see cref="IInvokeProvider"/>, the technique
/// <see cref="RepoPickerAccessibilityTests"/> introduced for the W2 repo rows — so they fail against an
/// inert icon instead of failing to compile against it.
/// </summary>
public class EmptyStateOpenRepoTests
{
    private const string IconName = "Open a repository";

    [AvaloniaFact]
    public void EmptyStateFolderIcon_IsANamedInvokableButton_BoundToTheRepoPickerCommand()
    {
        var win = ShowEmptyShell(out var vm, out var restore);
        try
        {
            var (icon, invoke) = FindInvokableFolderIcon(win);

            var peer = ControlAutomationPeer.CreatePeerForElement(icon);
            Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
            // An unnamed invokable element is unusable to a screen reader — and ButtonAutomationPeer's
            // Content fallback would happily report "Avalonia.Controls.PathIcon" for an icon-only
            // button, which is why the name is asserted exactly rather than merely non-empty.
            Assert.Equal(IconName, peer.GetName());
            Assert.True(peer.IsKeyboardFocusable(), "the folder icon must be reachable by keyboard");
            Assert.True(icon.IsEffectivelyVisible, "the folder icon must be visible in the empty state");
            Assert.NotNull(invoke);

            // Reuses the shipped handler rather than duplicating it: the SAME ICommand instance the
            // title-bar "Select Repo" button and MacMenuBar invoke.
            var button = Assert.IsType<Button>(icon);
            Assert.Same(vm.OpenRepoPickerCommand, button.Command);

            // Pointer affordance: the cursor says "clickable" before anything is clicked.
            Assert.Equal(new Cursor(StandardCursorType.Hand).ToString(), button.Cursor?.ToString());
            Assert.NotEqual(Cursor.Default.ToString(), button.Cursor?.ToString());
        }
        finally
        {
            Teardown(win, restore);
        }
    }

    [AvaloniaFact]
    public void InvokingTheFolderIcon_OpensTheRepoPicker_AndReusesTheOneWindow()
    {
        var win = ShowEmptyShell(out var vm, out var restore);
        try
        {
            var (_, invoke) = FindInvokableFolderIcon(win);
            Assert.Null(vm.ActiveRepoPicker);

            invoke.Invoke();
            Settle();

            var picker = vm.ActiveRepoPicker;
            Assert.NotNull(picker);
            Assert.IsType<RepoPickerWindow>(picker);
            // The picker borrows the shell's ViewModel — that is how the repository tree it shows is
            // the shell's own, and the proof that activation reached the real command.
            Assert.Same(vm, picker!.DataContext);

            // OpenRepoPicker is single-instance: a second activation activates the open window rather
            // than stacking another one on top of it.
            invoke.Invoke();
            Settle();
            Assert.Same(picker, vm.ActiveRepoPicker);
        }
        finally
        {
            Teardown(win, restore);
        }
    }

    [AvaloniaFact]
    public void FocusedFolderIcon_OpensTheRepoPicker_OnEnter()
    {
        var win = ShowEmptyShell(out var vm, out var restore);
        try
        {
            var (icon, _) = FindInvokableFolderIcon(win);

            icon.Focus();
            Settle();
            Assert.True(icon.IsFocused, "the folder icon must take keyboard focus");

            win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Settle();

            Assert.NotNull(vm.ActiveRepoPicker);
        }
        finally
        {
            Teardown(win, restore);
        }
    }

    /// <summary>
    /// Walks from the empty state's folder glyph toward the window and returns the first element that
    /// exposes an <see cref="IInvokeProvider"/> — the same search an automation client (or a screen
    /// reader) performs. Fails the test if nothing on the chain does, which is exactly the old
    /// behaviour: a <c>PathIcon</c> inside plain panels offers no activation path at all.
    /// </summary>
    private static (Control Icon, IInvokeProvider Invoke) FindInvokableFolderIcon(Window win)
    {
        var caption = win.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == "Select a repository to begin");
        Assert.True(caption is not null, "the no-repo empty state is not on screen");

        // The glyph next to the caption, inside the same centred empty-state panel.
        var panel = caption!.GetVisualAncestors().OfType<StackPanel>().First();
        var glyph = panel.GetVisualDescendants().OfType<PathIcon>().FirstOrDefault();
        Assert.True(glyph is not null, "the empty state no longer shows a folder glyph");

        for (Visual? v = glyph; v is not null and not Window; v = v.GetVisualParent())
        {
            if (v is not Control control) continue;
            if (ControlAutomationPeer.CreatePeerForElement(control).GetProvider<IInvokeProvider>() is { } provider)
                return (control, provider);
        }

        Assert.Fail(
            "No element from the empty-state folder glyph up to the shell window exposes an " +
            "InvokePattern — the icon is inert again, so it cannot open the repo picker by click, " +
            "keyboard or automation.");
        throw new InvalidOperationException(); // unreachable; keeps the compiler happy
    }

    /// <summary>
    /// The shell with NO repository open, which is what puts the empty state on screen. Client edition:
    /// it needs no orchestrator, and the empty state is edition-agnostic. The caller must pass
    /// <paramref name="restore"/> to <see cref="Teardown"/> — the edition is a static, and the whole
    /// assembly shares one headless session.
    /// </summary>
    private static Window ShowEmptyShell(out MainWindowViewModel vm, out IDisposable restore)
    {
        var savedEdition = Mainguard.App.Shell.App.Edition;
        var edition = new ClientManifest();
        Mainguard.App.Shell.App.Edition = edition;
        var seed = HarnessHygiene.SeedViewAssemblies(edition);
        restore = new EditionRestorer(seed, savedEdition);

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        vm = new MainWindowViewModel();
        var win = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        win.Show();
        Settle();

        Assert.Null(vm.CurrentWorkspace);
        return win;
    }

    /// <summary>Closes the picker by hand — NOT through <see cref="HarnessHygiene.Teardown"/>, which
    /// would dispose the shell ViewModel the picker only borrows as its DataContext — then tears the
    /// shell down so no VM timer leaks into the next test.</summary>
    private static void Teardown(Window win, IDisposable restore)
    {
        (win.DataContext as MainWindowViewModel)?.ActiveRepoPicker?.Close();
        Dispatcher.UIThread.RunJobs();
        HarnessHygiene.Teardown(win);
        restore.Dispose();
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    private static void Settle()
    {
        for (int i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
    }

    private sealed class EditionRestorer : IDisposable
    {
        private readonly IDisposable _seed;
        private readonly Mainguard.UI.Editions.IEditionManifest _saved;

        public EditionRestorer(IDisposable seed, Mainguard.UI.Editions.IEditionManifest saved)
        {
            _seed = seed;
            _saved = saved;
        }

        public void Dispose()
        {
            _seed.Dispose();
            Mainguard.App.Shell.App.Edition = _saved;
        }
    }
}
