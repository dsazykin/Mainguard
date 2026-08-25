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
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.Git.Models;
using Mainguard.UI.Theming;
using Xunit;

namespace Mainguard.Tests.Headless;

/// <summary>
/// W2 regression: every row in the repository picker must offer a real activation path to UI
/// Automation and to the keyboard, not just to a mouse double-click.
///
/// The bug these tests pin: the row was a bare <c>Border</c>/<c>Grid</c> with raw pointer handlers,
/// so the deepest thing UI Automation could see was the name <c>TextBlock</c> — <c>ControlType.Text</c>,
/// <c>IsKeyboardFocusable=false</c>, and NO <c>InvokePattern</c> anywhere between it and the window.
/// A screen-reader user (or any automation driver) therefore had no way to open a repository at all.
///
/// Both tests deliberately locate the row the way an automation client does — walk up from the name
/// label until something exposes <see cref="IInvokeProvider"/> — rather than by naming the control
/// type. That is what makes them fail against the old tree instead of failing to compile.
/// </summary>
public class RepoPickerAccessibilityTests
{
    [AvaloniaFact]
    public void RepoRow_ExposesInvokePattern_AndOpensTheRepository()
    {
        var win = ShowPicker(out var vm, out var repo);
        try
        {
            var (row, invoke) = FindInvokableRow(win, repo);

            var peer = ControlAutomationPeer.CreatePeerForElement(row);
            Assert.Equal(AutomationControlType.ListItem, peer.GetAutomationControlType());
            // The row must NAME itself — an unnamed invokable element is unusable to a screen reader.
            Assert.Equal(repo.DisplayName, peer.GetName());
            Assert.True(peer.IsKeyboardFocusable(), "the row must be reachable by keyboard");

            invoke.Invoke();
            Settle();

            // Invoking selects and opens, exactly as double-click does. The seeded path is not a git
            // repo, so OpenRepository parks it in InvalidRepository — which is precisely the proof
            // that activation reached the ViewModel with THIS row's repository.
            Assert.Same(repo, vm.SelectedNode);
            Assert.Same(repo, vm.InvalidRepository);
        }
        finally
        {
            HarnessHygiene.Teardown(win);
        }
    }

    [AvaloniaFact]
    public void RepoRow_OpensOnEnter_WhenFocused()
    {
        var win = ShowPicker(out var vm, out var repo);
        try
        {
            var (row, _) = FindInvokableRow(win, repo);

            row.Focus();
            Settle();
            Assert.True(row.IsFocused, "the row must take keyboard focus");

            win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Settle();

            Assert.Same(repo, vm.SelectedNode);
            Assert.Same(repo, vm.InvalidRepository);
        }
        finally
        {
            HarnessHygiene.Teardown(win);
        }
    }

    /// <summary>
    /// Walks from the row's name label toward the window and returns the first element that exposes
    /// an <see cref="IInvokeProvider"/> — the same search an automation client (or the Windows
    /// walkthrough's <c>TreeWalker</c>) performs. Fails the test if nothing on the chain does.
    /// </summary>
    private static (Control Row, IInvokeProvider Invoke) FindInvokableRow(Window win, Repository repo)
    {
        var label = win.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == repo.DisplayName);
        Assert.NotNull(label);

        for (Visual? v = label; v is not null and not Window; v = v.GetVisualParent())
        {
            if (v is not Control control) continue;
            if (ControlAutomationPeer.CreatePeerForElement(control).GetProvider<IInvokeProvider>() is { } provider)
                return (control, provider);
        }

        Assert.Fail(
            $"No element from the '{repo.DisplayName}' row up to the picker window exposes an " +
            "InvokePattern — the repository list has no UI Automation activation path (W2).");
        throw new InvalidOperationException(); // unreachable; keeps the compiler happy
    }

    private static Window ShowPicker(out MainWindowViewModel vm, out Repository repo)
    {
        Mainguard.App.Shell.App.Edition = new Mainguard.Agents.UI.Editions.ProManifest();
        Mainguard.Agents.UI.Editions.ProComposition.OrchestratorServicesFactory =
            () => OrchestratorServices.FromSingle(new MockOrchestrator());
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        vm = new MainWindowViewModel();
        // Replace whatever the isolated test DB happened to hold with one known, expanded row.
        repo = new Repository
        {
            RepositoryId = 4242,
            CategoryId = 99,
            DisplayName = "uia-probe-repo",
            // Deliberately not a git repo: OpenRepository then records it as InvalidRepository
            // synchronously, giving the assertions a signal without needing a real repository.
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mainguard-uia-probe-not-a-repo"),
        };
        var category = new WorkspaceCategory { CategoryId = 99, Name = "Probe", IsExpanded = true };
        category.Repositories.Add(repo);
        vm.Categories.Clear();
        vm.Categories.Add(category);

        var win = new RepoPickerWindow { DataContext = vm };
        win.Show();
        Settle();
        return win;
    }

    private static void Settle()
    {
        for (int i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
    }
}
