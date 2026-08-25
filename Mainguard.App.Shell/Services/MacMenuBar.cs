using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git.Actions;

namespace Mainguard.App.Shell.Services;

/// <summary>
/// The macOS menu bar (installed on macOS only — Windows/Linux keep the in-window chrome). A Mac
/// app without File/Repository/View menus at the top of the screen reads as broken; this builds
/// them over seams that already exist: repo actions dispatch through the shell's
/// <see cref="MainWindowViewModel.InvokeActionByIdCommand"/> — the SAME registry the shortcuts
/// and the command palette use, so availability rules hold and nothing is duplicated — and the
/// theme submenu drives <see cref="Mainguard.UI.Theming.ThemeManager"/> keys, "System" included.
/// Handlers resolve the main window's VM at CLICK time: the menu outlives any one window.
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
        file.Add(Action("Close Repository", ActionIds.CloseRepository));
        file.Add(new NativeMenuItemSeparator());
        file.Add(Item("Settings…", new KeyGesture(Key.OemComma, KeyModifiers.Meta),
            vm => vm.OpenSettingsCommand.Execute(null)));
        menu.Add(new NativeMenuItem("File") { Menu = file });

        // ---- Repository --------------------------------------------------------------------------
        var repo = new NativeMenu();
        repo.Add(Action("Commit", ActionIds.Commit, new KeyGesture(Key.Enter, KeyModifiers.Meta)));
        repo.Add(new NativeMenuItemSeparator());
        repo.Add(Action("Fetch", ActionIds.Fetch));
        repo.Add(Action("Pull", ActionIds.Pull));
        repo.Add(Action("Push", ActionIds.Push, new KeyGesture(Key.P, KeyModifiers.Meta | KeyModifiers.Shift)));
        repo.Add(new NativeMenuItemSeparator());
        repo.Add(Action("New Branch…", ActionIds.NewBranch, new KeyGesture(Key.B, KeyModifiers.Meta)));
        repo.Add(Action("Refresh", ActionIds.Refresh));
        repo.Add(new NativeMenuItemSeparator());
        repo.Add(Item("Reveal in Finder", gesture: null,
            _ => FileExplorerLauncher.RevealFolder(CurrentRepoPath())));
        repo.Add(Item("Open in Terminal", gesture: null,
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

        return menu;
    }

    private static NativeMenuItem Action(string header, string actionId, KeyGesture? gesture = null) =>
        Item(header, gesture, vm => vm.InvokeActionByIdCommand.Execute(actionId));

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

    private static MainWindowViewModel? MainViewModel() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?
            .MainWindow?.DataContext as MainWindowViewModel;

    /// <summary>The open repo's path — the one place the shell records it (UserPreferences,
    /// written by MainWindowViewModel on every open); null-safe when nothing is open.</summary>
    private static string? CurrentRepoPath() =>
        App.Settings?.Current.LastOpenedRepoPath is { Length: > 0 } path
            && MainViewModel()?.CurrentWorkspace is not null
        ? path
        : null;
}
