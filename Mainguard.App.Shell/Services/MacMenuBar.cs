using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git.Actions;
using Mainguard.Git.Models;

namespace Mainguard.App.Shell.Services;

/// <summary>
/// The macOS menu bar (installed on macOS only — Windows/Linux keep the in-window chrome). A Mac
/// app without File/Repository/View menus at the top of the screen reads as broken; this builds
/// them over seams that already exist: repo actions dispatch through the shell's
/// <see cref="MainWindowViewModel.InvokeActionByIdCommand"/> — the SAME registry the shortcuts
/// and the command palette use, so availability rules hold and nothing is duplicated — and the
/// theme submenu drives <see cref="Mainguard.UI.Theming.ThemeManager"/> keys, "System" included.
/// Handlers resolve the main window's VM at CLICK time: the menu outlives any one window.
///
/// <para><b>This menu is the only route to those actions on macOS.</b> MainWindow's
/// <c>MenuBarGroup</c> — the Branch pill, the Sync menu, the Repository menu and the push-options
/// flyout — is hidden outright on macOS (<see cref="Mainguard.UI.Views.WindowChromePolicy"/>), so
/// anything that lives ONLY in that panel and not here is unreachable in the Mac build. It was:
/// switching branches, the push options, Update Project, Manage Remotes, Submodules, Git LFS,
/// Worktrees, Reflog and Operation History all had no Mac entry point at all. Every item in that
/// panel now has one here, and a new item added there needs one here too.</para>
/// </summary>
internal static class MacMenuBar
{
    private static NativeMenu? _menu;

    /// <summary>Builds the shared menu once and names the app (Avalonia's macOS backend titles
    /// the app menu from <see cref="Application.Name"/>, not the bundle). Call at startup.</summary>
    public static void Install(Application app)
    {
        if (!OperatingSystem.IsMacOS()) return;

        app.Name = "Mainguard";
        _menu = Build();
        NativeMenu.SetMenu(app, _menu);
        Mainguard.UI.Views.ChromedWindow.MenuInstaller = Attach;
    }

    /// <summary>Attaches the shared menu to a window — on macOS the menu bar follows the KEY
    /// window, so every top-level window carries it (MainWindow + every ChromedWindow call this;
    /// no-op elsewhere and before <see cref="Install"/>).</summary>
    public static void Attach(Window window)
    {
        if (_menu is not null && OperatingSystem.IsMacOS())
            NativeMenu.SetMenu(window, _menu);
    }

    private static NativeMenu Build()
    {
        var menu = new NativeMenu();

        // ---- File --------------------------------------------------------------------------------
        var file = new NativeMenu();
        file.Add(Item("Open Repository…", new KeyGesture(Key.O, KeyModifiers.Meta),
            vm => vm.OpenRepoPickerCommand.Execute(null)));
        file.Add(RepoItem("Close Repository", ActionIds.CloseRepository));
        file.Add(new NativeMenuItemSeparator());
        file.Add(Item("Settings…", new KeyGesture(Key.OemComma, KeyModifiers.Meta),
            vm => vm.OpenSettingsCommand.Execute(null)));
        menu.Add(new NativeMenuItem("File") { Menu = file });

        // ---- Branch ------------------------------------------------------------------------------
        // The Mac counterpart of the in-window Branch pill. "Switch To" is filled in on NeedsUpdate
        // (see PopulateSwitchTo) because the branch list is repository state, not a static menu.
        var branch = new NativeMenu();
        var switchTo = new NativeMenu();
        var switchToItem = new NativeMenuItem("Switch To") { Menu = switchTo };
        switchTo.NeedsUpdate += (_, _) => PopulateSwitchTo(switchTo);
        branch.Add(switchToItem);
        branch.Add(new NativeMenuItemSeparator());
        branch.Add(RepoItem("New Branch…", ActionIds.NewBranch, new KeyGesture(Key.B, KeyModifiers.Meta)));
        branch.Add(Workspace("New Branch from Current",
            w => w.BranchBrowser.CreateBranchFromCurrentCommand.Execute(null)));
        menu.Add(new NativeMenuItem("Branch") { Menu = branch });

        // ---- Repository --------------------------------------------------------------------------
        var repo = new NativeMenu();
        repo.Add(RepoItem("Commit", ActionIds.Commit, new KeyGesture(Key.Enter, KeyModifiers.Meta)));
        repo.Add(new NativeMenuItemSeparator());
        repo.Add(RepoItem("Fetch", ActionIds.Fetch));
        repo.Add(RepoItem("Pull", ActionIds.Pull));
        repo.Add(RepoItem("Push", ActionIds.Push, new KeyGesture(Key.P, KeyModifiers.Meta | KeyModifiers.Shift)));

        // The push-options flyout from the in-window chrome. Force-push keeps its "(with lease)"
        // wording: it is the destructive one, and the qualifier is what says which force this is.
        var pushOptions = new NativeMenu();
        pushOptions.Add(Workspace("Force Push (with lease)",
            w => w.PushForceWithLeaseCommand.Execute(null)));
        pushOptions.Add(Workspace("Push & Set Upstream", w => w.PushSetUpstreamCommand.Execute(null)));
        pushOptions.Add(Workspace("Push Tags", w => w.PushTagsCommand.Execute(null)));
        repo.Add(new NativeMenuItem("Push Options") { Menu = pushOptions });

        repo.Add(Workspace("Update Project", w => w.UpdateProjectCommand.Execute(null)));
        repo.Add(new NativeMenuItemSeparator());
        repo.Add(Workspace("Manage Remotes…", w => w.ManageRemotesCommand.Execute(null)));
        repo.Add(Workspace("Submodules…", w => w.ManageSubmodulesCommand.Execute(null)));
        repo.Add(Workspace("Git LFS…", w => w.ManageLfsCommand.Execute(null)));
        repo.Add(Workspace("Worktrees…", w => w.ManageWorktreesCommand.Execute(null)));
        repo.Add(new NativeMenuItemSeparator());
        repo.Add(Workspace("Reflog", w => w.ViewReflogCommand.Execute(null)));
        repo.Add(Workspace("Operation History", w => w.ManageOperationHistoryCommand.Execute(null)));
        repo.Add(new NativeMenuItemSeparator());
        repo.Add(RepoItem("Refresh", ActionIds.Refresh));
        repo.Add(RepoItem("Reveal in Finder", gesture: null,
            _ => FileExplorerLauncher.RevealFolder(CurrentRepoPath())));
        repo.Add(RepoItem("Open in Terminal", gesture: null,
            _ => TerminalLauncher.OpenTerminal(CurrentRepoPath())));
        menu.Add(new NativeMenuItem("Repository") { Menu = repo });

        // ---- View --------------------------------------------------------------------------------
        var view = new NativeMenu();
        view.Add(Action("Command Palette", ActionIds.OpenCommandPalette, new KeyGesture(Key.P, KeyModifiers.Meta)));
        view.Add(Action("Toggle Sidebar", ActionIds.ToggleSidebar));
        view.Add(new NativeMenuItemSeparator());
        var themes = new NativeMenu();
        themes.Add(Item("System", gesture: null, _ => Mainguard.UI.Theming.ThemeManager.Apply(
            Mainguard.UI.Theming.ThemeManager.SystemKey)));
        themes.Add(new NativeMenuItemSeparator());
        foreach (var theme in Mainguard.UI.Theming.ThemeManager.Themes)
        {
            var key = theme.Key;
            themes.Add(Item(theme.DisplayName, gesture: null, _ => Mainguard.UI.Theming.ThemeManager.Apply(key)));
        }
        view.Add(new NativeMenuItem("Theme") { Menu = themes });
        menu.Add(new NativeMenuItem("View") { Menu = view });

        // ---- Help --------------------------------------------------------------------------------
        var help = new NativeMenu();
        help.Add(Item("Mainguard Documentation", gesture: null,
            _ => BrowserLauncher.OpenUrl("https://dsazykin.github.io/Mainguard/")));
        menu.Add(new NativeMenuItem("Help") { Menu = help });

        // Every menu that carries repo-gated items re-evaluates them as it opens. NeedsUpdate is the
        // hook AppKit gives for "this menu is about to be shown"; there is no view-model event to
        // subscribe to here, and the alternative — leaving them permanently enabled — is a menu full
        // of items that look live and do nothing with no repository open.
        foreach (var owner in new[] { file, branch, repo })
        {
            owner.NeedsUpdate += (_, _) => RefreshEnablement();
        }

        return menu;
    }

    // ---- Switch To ---------------------------------------------------------------------------

    /// <summary>The branch list the last <see cref="PopulateSwitchTo"/> rendered, and when. Both
    /// halves of the cheap-rebuild guard described there.</summary>
    private static DateTime _switchToBuiltAtUtc = DateTime.MinValue;
    private static string _switchToBuiltFor = string.Empty;

    /// <summary>
    /// Rebuilds the "Switch To" submenu from the open repository's branches.
    ///
    /// <para>Rate-limited, and that is not an optimisation. AppKit raises
    /// <c>NativeMenu.NeedsUpdate</c> not only when a menu is about to be displayed but while it
    /// matches KEY EQUIVALENTS, which happens on keystrokes that never open a menu at all —
    /// enumerating a repository's refs on that path would put a libgit2 walk on the UI thread once
    /// per key. So the list is rebuilt at most every half second, and always when the repository or
    /// the checked-out branch has changed since it was built (the one staleness a human would
    /// actually notice: their own checkout).</para>
    /// </summary>
    private static void PopulateSwitchTo(NativeMenu switchTo)
    {
        var workspace = CurrentWorkspace();
        if (workspace is null)
        {
            switchTo.Items.Clear();
            switchTo.Add(Disabled("No repository open"));
            _switchToBuiltFor = string.Empty;
            return;
        }

        var stamp = workspace.RepositoryPath + "\n" + workspace.BranchBrowser.CurrentBranchName;
        var now = DateTime.UtcNow;
        if (stamp == _switchToBuiltFor && now - _switchToBuiltAtUtc < TimeSpan.FromMilliseconds(500))
        {
            return;
        }

        _switchToBuiltFor = stamp;
        _switchToBuiltAtUtc = now;

        switchTo.Items.Clear();
        var branches = workspace.BranchBrowser.ListBranchesForSwitchMenu();
        if (branches.Count == 0)
        {
            switchTo.Add(Disabled("No branches"));
            return;
        }

        // Locals, then remote-tracking under a heading — the flat shape a menu can carry. The
        // sidebar's folder grouping is deliberately not reproduced: a native submenu per path
        // segment turns "switch to a branch" into a hunt through nested menus.
        var addedRemoteHeading = false;
        foreach (var item in branches)
        {
            if (item.IsRemote && !addedRemoteHeading)
            {
                addedRemoteHeading = true;
                switchTo.Add(new NativeMenuItemSeparator());
                switchTo.Add(Disabled("Remote"));
            }

            var target = item;
            var entry = new NativeMenuItem(item.FriendlyName)
            {
                // The checked-out branch is shown and ticked, never offered: checking out the
                // branch you are on is the one entry here that can do nothing.
                IsChecked = target.IsCurrentRepositoryHead,
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsEnabled = !target.IsCurrentRepositoryHead,
            };
            entry.Click += (_, _) =>
                CurrentWorkspace()?.BranchBrowser.CheckoutBranchCommand.Execute(target);
            switchTo.Add(entry);
        }
    }

    // ---- Item factories ----------------------------------------------------------------------

    /// <summary>A non-clickable label used as an in-menu heading / empty-state line.</summary>
    private static NativeMenuItem Disabled(string header) =>
        new(header) { IsEnabled = false };

    private static NativeMenuItem Action(string header, string actionId, KeyGesture? gesture = null) =>
        Item(header, gesture, vm => vm.InvokeActionByIdCommand.Execute(actionId));

    /// <summary>An <see cref="ActionRegistry"/> action that needs an open repository — greyed out
    /// while there is none, rather than silently doing nothing when clicked.</summary>
    private static NativeMenuItem RepoItem(string header, string actionId, KeyGesture? gesture = null) =>
        RepoItem(header, gesture, vm => vm.InvokeActionByIdCommand.Execute(actionId));

    private static NativeMenuItem RepoItem(string header, KeyGesture? gesture, Action<MainWindowViewModel> onClick)
    {
        var item = Item(header, gesture, onClick);
        GateOnOpenRepo(item);
        return item;
    }

    /// <summary>An action that lives on the repo dashboard rather than the action registry (the
    /// in-window Sync/Repository/push-options menus bind these commands directly). Gated on an open
    /// repository for the same reason: every one of them needs a workspace to act on.</summary>
    private static NativeMenuItem Workspace(string header, Action<RepoDashboardViewModel> onClick)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) =>
        {
            if (CurrentWorkspace() is { } workspace) onClick(workspace);
        };
        GateOnOpenRepo(item);
        return item;
    }

    private static NativeMenuItem Item(string header, KeyGesture? gesture, Action<MainWindowViewModel> onClick)
    {
        var item = new NativeMenuItem(header);
        if (gesture is not null) item.Gesture = gesture;
        item.Click += (_, _) =>
        {
            if (MainViewModel() is { } vm) onClick(vm);
        };
        return item;
    }

    /// <summary>Greys an item out while no repository is open. Evaluated on NeedsUpdate — which
    /// AppKit raises before the menu is shown — so it tracks open/close without anything here
    /// subscribing to the view model's lifetime.</summary>
    private static void GateOnOpenRepo(NativeMenuItem item)
    {
        _repoGated.Add(item);
        item.IsEnabled = CurrentWorkspace() is not null;
    }

    /// <summary>Items that must follow "is a repository open". Held so
    /// <see cref="RefreshEnablement"/> can sweep them; the menu is a process-lifetime singleton, so
    /// this list is built once and never grows after startup.</summary>
    private static readonly List<NativeMenuItem> _repoGated = new();

    /// <summary>Re-evaluates the repo-gated items. Wired to every top-level submenu's NeedsUpdate
    /// in <see cref="Install"/>'s menu — see <see cref="GateOnOpenRepo"/>.</summary>
    internal static void RefreshEnablement()
    {
        var open = CurrentWorkspace() is not null;
        foreach (var item in _repoGated) item.IsEnabled = open;
    }

    /// <summary>The open repository's dashboard, or null when none is open. Mirrors
    /// <c>MainWindowViewModel.Dashboard</c>: <c>CurrentWorkspace</c> is typed as the base view model
    /// so the edition heads can seat other surfaces in it, and every item in this menu acts on a git
    /// repository specifically.</summary>
    private static RepoDashboardViewModel? CurrentWorkspace() =>
        MainViewModel()?.CurrentWorkspace as RepoDashboardViewModel;

    private static MainWindowViewModel? MainViewModel() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?
            .MainWindow?.DataContext as MainWindowViewModel;

    /// <summary>The open repo's path — the one place the shell records it (UserPreferences,
    /// written by MainWindowViewModel on every open); null-safe when nothing is open.</summary>
    private static string? CurrentRepoPath() =>
        App.Settings?.Current.LastOpenedRepoPath is { Length: > 0 } path
            && CurrentWorkspace() is not null
        ? path
        : null;
}
