using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.App.Shell.Editions;
using Mainguard.App.Shell.Views;
using Mainguard.Git;
using Mainguard.Git.Models;
using Mainguard.UI;
using Mainguard.UI.Editions;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Microsoft.EntityFrameworkCore;
namespace Mainguard.App.Shell.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable, IShellRailHost
{
    [ObservableProperty]
    private ObservableCollection<WorkspaceCategory> _categories =
        new();

    [ObservableProperty]
    private ViewModelBase? _currentWorkspace;

    // Dispose a workspace when it is replaced/cleared so its background resources
    // (RepositoryWatcher, AutoFetchService loop — T-10) don't leak across repos.
    partial void OnCurrentWorkspaceChanging(ViewModelBase? oldValue, ViewModelBase? newValue)
    {
        if (!ReferenceEquals(oldValue, newValue) && oldValue is System.IDisposable disposable)
            disposable.Dispose();
    }

    // Host tabs (RequiresWorkspace rail rows) enable/disable with the open workspace — the shipped rail
    // bound each host button's IsEnabled to (CurrentWorkspace != null); the data-driven rows mirror it.
    partial void OnCurrentWorkspaceChanged(ViewModelBase? value)
    {
        if (RailSections is null) return; // built in the ctor; CurrentWorkspace is only set post-ctor
        foreach (var s in RailSections)
            if (s.RequiresWorkspace) s.IsEnabled = value is not null;
    }

    /// <summary>App-lifetime in production (never called there); the headless harnesses dispose
    /// the shell so the open workspace's timers (RepoDashboard's 1-minute last-fetched ticker,
    /// AutoFetchService) and the control-center event pump can't outlive a test and fire inside
    /// a later one on the shared dispatcher — the CI "random headless victim" poisoning.</summary>
    public void Dispose()
    {
        CurrentWorkspace = null; // OnCurrentWorkspaceChanging disposes the outgoing workspace
        App.LiveAgentCountProvider = null; // this shell's control center is going away
        if (ControlCenter is { } controlCenter)
            controlCenter.DaemonReachable -= OnDaemonReachable;
        (ControlCenter as IDisposable)?.Dispose();
        foreach (var toast in Toasts) toast.Dispose();
        Toasts.Clear();
    }

    // ---- Shell-level toasts: the window-wide sibling of RepoDashboard's #85 stack, for events
    // that outrank any one repo (today: the tier-1 daemon auto-update outcome raised through
    // App.RefreshDaemonInBackground → DaemonUpdateToastPublisher). Same rules: stacked, newest
    // at the bottom, capped, each owns its auto-dismiss timer. ----

    private const int MaxShellToasts = 3;

    public ObservableCollection<ToastViewModel> Toasts { get; } = new();

    /// <summary>Raises one shell toast. UI-thread only (callers off-thread post through
    /// <c>Dispatcher.UIThread</c> — see <c>DaemonUpdateToastPublisher</c>).</summary>
    public void ShowToast(string message, bool isError)
    {
        var toast = new ToastViewModel(message, isError, t => Toasts.Remove(t));
        Toasts.Add(toast);
        while (Toasts.Count > MaxShellToasts)
        {
            var oldest = Toasts[0];
            Toasts.RemoveAt(0);
            oldest.Dispose();
        }
    }

    // ---- Degraded-entry banner (owner design, 2026-07-17): when the startup sequence exhausted an
    // essential step's budget (the daemon never came up in time), it hands MainWindow a
    // StartupResult carrying honest banner text. The banner is persistent + token-styled and clears
    // itself when the daemon connects (ControlCenter.DaemonReachable — the existing reconnect
    // machinery does the healing; this only observes it). ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartupBanner))]
    private string? _startupBanner;

    /// <summary>True while the degraded startup banner is showing.</summary>
    public bool HasStartupBanner => !string.IsNullOrEmpty(StartupBanner);

    private void OnDaemonReachable() => Dispatcher.UIThread.Post(() => StartupBanner = null);

    [ObservableProperty]
    private object? _selectedNode;

    [ObservableProperty]
    private bool _isDeleteConfirmationVisible;

    [ObservableProperty]
    private string _deleteConfirmationTitle = "Confirm Delete";

    [ObservableProperty]
    private string _deleteConfirmationMessage = string.Empty;

    private object? _nodeToDelete;

    /// <summary>Switch the app theme (File menu → Theme). Applies live and persists the choice.</summary>
    [RelayCommand]
    private void SetTheme(string themeKey) => Mainguard.UI.Theming.ThemeManager.Apply(themeKey);

    // ---- Control-center integration (Lane E, revised 2026-07-11): the coordinator
    // surfaces live inside MainWindow, navigated by the section rail. P2-47 wired this to
    // the real DaemonClient-backed bundle (App.CreateOrchestratorServices) — the shipped
    // path no longer runs on MockOrchestrator. The VM consumes only Core/Agents interfaces,
    // so this swap needed zero View changes. App-lifetime instance.
    //
    // Edition seam (1a): built by the manifest (App.Edition.CreateControlCenter) in the ctor, so it is
    // NULL under an edition with no agent platform (Client). Retyped to the IAgentPlatformSurface seam
    // — the shell no longer names ControlCenterViewModel; every dereference below is null-guarded. The
    // Pro manifest still routes through App.CreateOrchestratorServices, so the harness mock seam holds.
    public IAgentPlatformSurface? ControlCenter { get; }

    // Section model (revised 2026-07-11; data-driven rail in 1b): every rail destination is a tab in
    // the main content area — Repo viewer, Coordinator, Resources, and the four host surfaces. The rail
    // itself is now built from App.Edition.Sections (RailSections below). SelectedSectionId is the single
    // source of truth; each Is…SectionActive flag is COMPUTED from it, so the section-content panels in
    // MainWindow.axaml keep binding the exact same property names (unchanged), and each rail row's
    // IsActive is (SelectedSectionId == row.Id).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepoSectionActive))]
    [NotifyPropertyChangedFor(nameof(IsCoordinatorSectionActive))]
    [NotifyPropertyChangedFor(nameof(IsResourcesSectionActive))]
    [NotifyPropertyChangedFor(nameof(IsPullRequestsSectionActive))]
    [NotifyPropertyChangedFor(nameof(IsIssuesSectionActive))]
    [NotifyPropertyChangedFor(nameof(IsNotificationsSectionActive))]
    [NotifyPropertyChangedFor(nameof(IsReleasesSectionActive))]
    [NotifyPropertyChangedFor(nameof(IsHostSectionActive))]
    private string _selectedSectionId = "Repo";

    public bool IsRepoSectionActive => SelectedSectionId == "Repo";
    public bool IsCoordinatorSectionActive => SelectedSectionId == "Coordinator";
    public bool IsResourcesSectionActive => SelectedSectionId == "Resources";
    public bool IsPullRequestsSectionActive => SelectedSectionId == "PullRequests";
    public bool IsIssuesSectionActive => SelectedSectionId == "Issues";
    public bool IsNotificationsSectionActive => SelectedSectionId == "Notifications";
    public bool IsReleasesSectionActive => SelectedSectionId == "Releases";
    public bool IsHostSectionActive => IsPullRequestsSectionActive || IsIssuesSectionActive || IsNotificationsSectionActive || IsReleasesSectionActive;

    /// <summary>The edition's rail destinations (1b), materialized from App.Edition.Sections in the ctor.
    /// Under Pro these are the 7 shipped sections; under Client, Repo + the four host tabs (no
    /// Coordinator/Resources — those need the agent platform).</summary>
    public ObservableCollection<RailSectionViewModel> RailSections { get; }

    /// <summary>True when the shell shows the agent rail (worker list + kill switch) — Pro only; the gate
    /// removes that chrome under an edition with no agent platform (Client).</summary>
    public bool ShowsAgentRail => App.Edition.ShowsAgentRail;

    /// <summary>The Pro agent rail (worker list + kill switch) as opaque content — an
    /// <c>AgentRailViewModel</c> reached through the control center, resolved to <c>AgentRailView</c> by
    /// ViewLocator; <c>null</c> under an edition with no agent platform. The shell drops it into a
    /// <c>ContentControl</c> gated by <see cref="ShowsAgentRail"/> (2d), so it never names the Pro rail
    /// types. ControlCenter is built first in the ctor and its rail content is set once, so this is stable
    /// by the time the view binds.</summary>
    public object? AgentRailContent => ControlCenter?.AgentRailContent;

    /// <summary>True when this edition composes the agent platform (kept for later steps' gating).</summary>
    public bool HasAgentPlatform => App.Edition.HasAgentPlatform;

    /// <summary>The active host surface's ViewModel (PRs/Issues/Notifications/Releases);
    /// the view resolves through ViewLocator (PullRequestsViewModel → PullRequestsView …).</summary>
    [ObservableProperty] private ViewModelBase? _hostSectionContent;

    /// <summary>Resources tab content, created lazily on first visit; app-lifetime. Held as <c>object?</c>
    /// (2d — the shell never names <c>ResourceMonitorViewModel</c>) and dropped into a <c>ContentControl</c>
    /// that resolves <c>ResourceMonitorView</c> via ViewLocator.</summary>
    [ObservableProperty] private object? _resourceMonitor;

    private void ActivateSection(string section)
    {
        SelectedSectionId = section;
        if (!IsHostSectionActive) HostSectionContent = null;
        foreach (var s in RailSections) s.IsActive = s.Id == section;
    }

    /// <summary>Route a data-driven rail row's activation to the matching section command — the exact
    /// per-section behavior the shipped hard-coded buttons had (Coordinator focuses the conversation,
    /// Resources lazily builds its monitor, host tabs run their async open, everything else shows the
    /// repo). Passed to each <see cref="RailSectionViewModel"/> so a row activates by its id.</summary>
    private void ActivateRailSection(string id)
    {
        switch (id)
        {
            case "Coordinator": ShowCoordinatorSection(); break;
            case "Resources": ShowResourcesSection(); break;
            case "PullRequests" or "Issues" or "Notifications" or "Releases":
                ShowHostSectionCommand.Execute(id);
                break;
            default: ShowRepoSection(); break;
        }
    }

    [RelayCommand]
    private void ShowResourcesSection()
    {
        ResourceMonitor ??= ControlCenter?.CreateResourceMonitor();
        ActivateSection("Resources");
    }

    /// <summary>Open a host surface (PRs/Issues/Notifications/Releases) as a section tab.
    /// Same VMs as the legacy dialogs, built by the workspace's factories; leaving the tab
    /// refreshes ahead/behind (the dialogs did this on close).</summary>
    [RelayCommand]
    private async Task ShowHostSectionAsync(string which)
    {
        if (CurrentWorkspace is not RepoDashboardViewModel ws) return;

        var leavingHost = HostSectionContent is not null;
        ViewModelBase vm = which switch
        {
            "Issues" => ws.CreateIssuesViewModel(),
            "Notifications" => ws.CreateNotificationsViewModel(),
            "Releases" => ws.CreateReleasesViewModel(),
            _ => ws.CreatePullRequestsViewModel(),
        };

        // A "close" from inside the surface returns to the Repo viewer.
        switch (vm)
        {
            case PullRequestsViewModel pr:
                pr.CloseAction = () => ShowRepoSection();
                if (pr.IsSupported) pr.RefreshListCommand.Execute(null);
                break;
            case IssuesViewModel issues:
                issues.CloseAction = () => ShowRepoSection();
                if (issues.IsSupported) issues.RefreshListCommand.Execute(null);
                break;
            case NotificationsViewModel notifications:
                notifications.CloseAction = () => ShowRepoSection();
                if (notifications.IsSupported) notifications.RefreshCommand.Execute(null);
                break;
            case ReleasesViewModel releases:
                releases.CloseAction = () => ShowRepoSection();
                if (releases.IsSupported) releases.RefreshListCommand.Execute(null);
                break;
        }

        HostSectionContent = vm;
        ActivateSection(which);
        if (leavingHost) await ws.RefreshAfterHostSurfaceAsync();
    }

    /// <summary>The section rail's expanded/collapsed state (collapsed = icons + tooltips).</summary>
    [ObservableProperty]
    private bool _isRailExpanded = true;

    [RelayCommand]
    private void ToggleRail()
    {
        IsRailExpanded = !IsRailExpanded;
        _settingsService.Update(p => p.SectionRailExpanded = IsRailExpanded);
    }

    /// <summary>The title-bar toolbar's expanded/collapsed state (JetBrains-style hamburger toggle) —
    /// NOT the section rail above (<see cref="IsRailExpanded"/>): this is the top title-bar row.
    /// Collapsed (default) shows Branch/Sync/Repository; expanded replaces them with Select Repo/
    /// Close Repository/Settings/Exit.</summary>
    [ObservableProperty]
    private bool _isToolbarExpanded;

    [RelayCommand]
    private void ToggleToolbar()
    {
        IsToolbarExpanded = !IsToolbarExpanded;
        _settingsService.Update(p => p.ToolbarExpanded = IsToolbarExpanded);
    }

    [RelayCommand]
    private void ShowRepoSection() => ActivateSection("Repo");

    [RelayCommand]
    private void ShowCoordinatorSection()
    {
        ControlCenter?.FocusCoordinator();
        ActivateSection("Coordinator");
    }

    [RelayCommand]
    private void ShowAgent(string agentId)
    {
        ControlCenter?.SelectAgent(agentId);
        ActivateSection("Coordinator");
    }

    /// <summary>Direct-to-agent prompting (File menu → Agent prompting). "Direct" lets the
    /// composer in an agent document send straight to that agent; "Through the Coordinator"
    /// disables it — steering goes through the Coordinator chat. Persisted like Theme.</summary>
    [RelayCommand]
    private void SetAgentPrompting(string mode)
    {
        var direct = mode == "Direct";
        ControlCenter?.SetDirectPrompting(direct);
        _settingsService.Update(p => p.DirectAgentPrompting = direct);
    }

    /// <summary>Switch the coordinator-surface layout (File menu → Layout). Applies live
    /// and persists the choice — the exact Theme pattern. Repo viewer is unaffected.</summary>
    [RelayCommand]
    private void SetLayout(string layoutKey)
    {
        ControlCenter?.SetPreset(layoutKey);
        _settingsService.Update(p => p.WorkspaceLayout = layoutKey);
    }

    private Views.RepoPickerWindow? _repoPicker;

    /// <summary>The repositories tree, as a picker window on top (revised 2026-07-11 — the
    /// docked sidebar column is gone so the workspace runs full-width). One instance at a time.</summary>
    [RelayCommand]
    private void OpenRepoPicker()
    {
        if (_repoPicker is { } open)
        {
            open.Activate();
            return;
        }
        _repoPicker = new Views.RepoPickerWindow { DataContext = this };
        _repoPicker.Closed += (_, _) => _repoPicker = null;
        _repoPicker.Show();
    }

    // --- Settings + pinned sidebar icons (#78, repurposed). Pinning used to feed a top-bar icon
    // strip that the phase-2 shell never rendered (the section rail owns navigation instead), so
    // the toggle had no visible effect. It now controls which of the host rail destinations
    // (Pull requests/Issues/Notifications/Releases) actually appear in that same section rail. ---

    /// <summary>Rebuilds the rail from the edition's manifest, hiding any host destination the user
    /// has unpinned in Settings. Called from the ctor and again — live — every time a pin is
    /// toggled, so unchecking an item removes it from the sidebar immediately.</summary>
    private void RebuildRailSections()
    {
        var pinned = _settingsService.Current.PinnedMenuIds;
        var pinnableIds = new HashSet<string>(PinnableMenus.All.Select(d => d.Id));

        RailSections.Clear();
        var leadingDividerPlaced = false;
        foreach (var descriptor in App.Edition.Sections)
        {
            if (pinnableIds.Contains(descriptor.Id) && !pinned.Contains(descriptor.Id))
                continue;

            var showsLeadingDivider = descriptor.RequiresWorkspace && !leadingDividerPlaced;
            if (showsLeadingDivider) leadingDividerPlaced = true;
            RailSections.Add(new RailSectionViewModel(descriptor, ActivateRailSection, showsLeadingDivider)
            {
                IsActive = descriptor.Id == SelectedSectionId,
                // A freshly constructed row's ctor always starts host tabs disabled (it assumes no
                // workspace is open yet) and only OnCurrentWorkspaceChanged flips that later — which
                // never fires again just because a pin toggled. Sync it here so re-pinning a host tab
                // while a repo is already open doesn't leave the row permanently unselectable.
                IsEnabled = !descriptor.RequiresWorkspace || CurrentWorkspace is not null,
            });
        }

        // The active row may have just been unpinned out of the rail — fall back to Repo rather
        // than leaving the content pane pointed at a destination with no rail row left to show it.
        ActivateSection(RailSections.Any(s => s.Id == SelectedSectionId) ? SelectedSectionId : "Repo");
    }

    private Views.SettingsWindow? _settingsWindow;

    /// <summary>Opens the Settings window (the hamburger's expanded "Settings" button), or focuses the
    /// existing one and jumps straight to <paramref name="pageId"/> if it's already open — the same
    /// singleton-reuse pattern as <see cref="OpenRepoPicker"/>. <paramref name="focusHost"/> pre-fills
    /// the Accounts page's "add a host" field; used by the git-auth-failure deep link
    /// (<c>RepoDashboardViewModel.HandleGitActionException</c>), which used to open a standalone
    /// AccountsWindow directly.</summary>
    public async Task OpenSettingsAsync(string pageId = "General", string? focusHost = null)
    {
        if (_settingsWindow is { } open)
        {
            open.Activate();
            (open.DataContext as SettingsViewModel)?.ActivatePage(pageId, focusHost);
            return;
        }

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
            return;

        var vm = new SettingsViewModel(
            _settingsService,
            HasAgentPlatform,
            SetLayoutCommand,
            SetAgentPromptingCommand,
            RebuildRailSections,
            BuildShortcutSettingsPage,
            currentRepoPath: () => Dashboard?.RepositoryPath,
            refreshCurrentWorkspace: () => Dashboard?.RefreshAfterHostSurfaceAsync() ?? Task.CompletedTask,
            proTools: App.Edition.ProTools);

        _settingsWindow = new SettingsWindow { DataContext = vm };
        vm.OwnerWindow = _settingsWindow;
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        if (pageId != "General") vm.ActivatePage(pageId, focusHost);

        await _settingsWindow.ShowDialog(desktop.MainWindow);
    }

    [RelayCommand]
    private Task OpenSettings() => OpenSettingsAsync();

    // main's #62 ExitApplication (plain Shutdown) is superseded by phase2's full-exit
    // version further down — App.RequestFullExit() honors close-to-tray and stop-VM.

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        IsDeleteConfirmationVisible = false;
        if (_nodeToDelete is WorkspaceCategory cat)
        {
            ExecuteDeleteCategory(cat);
        }
        else if (_nodeToDelete is Repository repo)
        {
            await ExecuteRemoveRepositoryAsync(repo);
        }
        _nodeToDelete = null;
    }

    [RelayCommand]
    private void CancelDelete()
    {
        IsDeleteConfirmationVisible = false;
        _nodeToDelete = null;
    }

    [ObservableProperty]
    private bool _isSidebarOpen = true;

    [ObservableProperty]
    private bool _isInvalidRepoCardVisible;

    [ObservableProperty]
    private Repository? _invalidRepository;

    [ObservableProperty]
    private bool _isReopenRepoCardVisible;

    [ObservableProperty]
    private string _reopenRepositoryPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoDetectPath))]
    private string _autoDetectPath = string.Empty;

    public bool HasAutoDetectPath => !string.IsNullOrEmpty(AutoDetectPath);

    // Shared with the rest of the app (Mainguard.App.Shell.App.Settings) — a private instance here would cache
    // its own UserPreferences snapshot and clobber concurrent writes from other owners (#83).
    private readonly Mainguard.Git.Services.ISettingsService _settingsService = Mainguard.App.Shell.App.Settings;

    // --- App lifecycle settings (File > Settings) + full exit -----------------------------------

    /// <summary>X hides to the tray instead of exiting (persisted; MainWindow's OnClosing reads it).</summary>
    public bool CloseToTray
    {
        get => _settingsService.Current.CloseToTray;
        set
        {
            if (_settingsService.Current.CloseToTray == value) return;
            _settingsService.Update(p => p.CloseToTray = value);
            OnPropertyChanged();
        }
    }

    /// <summary>A FULL exit terminates the MainguardEnv VM (scoped, G-12) to free its resources.</summary>
    public bool StopVmOnExit
    {
        get => _settingsService.Current.StopVmOnExit;
        set
        {
            if (_settingsService.Current.StopVmOnExit == value) return;
            _settingsService.Update(p => p.StopVmOnExit = value);
            OnPropertyChanged();
        }
    }

    /// <summary>File > Exit — the FULL exit (bypasses close-to-tray, stops the VM if configured).
    /// Guarded: warns first when the exit would stop the VM under live agents (PR3).</summary>
    [RelayCommand]
    private Task ExitApplication() => App.RequestFullExitGuardedAsync();

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarOpen = !IsSidebarOpen;
        if (IsSidebarOpen)
        {
            SidebarColumnWidth = new Avalonia.Controls.GridLength(_settingsService.Current.SidebarWidth, Avalonia.Controls.GridUnitType.Pixel);
            SidebarColumnMinWidth = 200;
        }
        else
        {
            SidebarColumnWidth = new Avalonia.Controls.GridLength(0, Avalonia.Controls.GridUnitType.Pixel);
            SidebarColumnMinWidth = 0;
        }
    }

    [ObservableProperty]
    private Avalonia.Controls.GridLength _sidebarColumnWidth;

    [ObservableProperty]
    private double _sidebarColumnMinWidth = 200;

    partial void OnSidebarColumnWidthChanged(Avalonia.Controls.GridLength value)
    {
        if (value.IsAbsolute && value.Value > 0)
        {
            _settingsService.Update(p => p.SidebarWidth = value.Value);
        }
    }

    // --- Command palette & keyboard shortcuts (T-18) ---

    [ObservableProperty]
    private bool _isCommandPaletteOpen;

    // The UI-free action catalog the palette and the global shortcuts both invoke. Built once; each
    // action's CanExecute/Execute closes over live state, so availability tracks the open repo.
    private readonly Mainguard.Git.Actions.ActionRegistry _actionRegistry = new();

    /// <summary>The palette overlay's ViewModel (fuzzy filtering + ranked rows live here).</summary>
    public CommandPaletteViewModel CommandPalette { get; }

    /// <summary>The effective shortcut bindings = built-in defaults overlaid with the user's saved overrides.</summary>
    public Mainguard.Git.Actions.ShortcutMap Shortcuts =>
        Mainguard.Git.Actions.ShortcutMap.FromPreferences(_settingsService.Current.ShortcutBindings);

    /// <summary>Raised when the palette opens, so the view can focus the query box.</summary>
    public event System.Action? CommandPaletteOpened;

    /// <summary>Raised after the user saves rebinds, so the window can rebuild its KeyBindings.</summary>
    public event System.Action? ShortcutsChanged;

    /// <summary>Builds the Keyboard Shortcuts Settings page's content — was a standalone
    /// ShortcutSettingsWindow; now a factory `SettingsViewModel` calls to build that page lazily.</summary>
    private ShortcutSettingsViewModel BuildShortcutSettingsPage()
    {
        var actions = _actionRegistry.All.Select(a => (a.Id, a.Title)).ToList();
        return new ShortcutSettingsViewModel(Shortcuts, actions, overrides =>
        {
            _settingsService.Update(p => p.ShortcutBindings = overrides);
            ShortcutsChanged?.Invoke();
        });
    }

    private RepoDashboardViewModel? Dashboard => CurrentWorkspace as RepoDashboardViewModel;

    [RelayCommand]
    private void OpenCommandPalette()
    {
        CommandPalette.Reset();
        IsCommandPaletteOpen = true;
        CommandPaletteOpened?.Invoke();
    }

    [RelayCommand]
    private void CloseCommandPalette() => IsCommandPaletteOpen = false;

    /// <summary>Routes a global keyboard gesture to its action (built from the ShortcutMap by the window).
    /// Silently ignores unknown or currently-unavailable actions.</summary>
    [RelayCommand]
    private void InvokeActionById(string? actionId)
    {
        if (string.IsNullOrEmpty(actionId)) return;
        var action = _actionRegistry.Find(actionId);
        if (action is null) return;
        bool available;
        try { available = action.CanExecute(); } catch { available = false; }
        if (!available) return;
        _ = action.Execute();
    }

    // Builds the palette candidate set fresh on each open: enabled actions, then the open repo's local
    // branches, then bookmarked repositories.
    private System.Collections.Generic.IReadOnlyList<PaletteEntry> BuildPaletteEntries()
    {
        var entries = new System.Collections.Generic.List<PaletteEntry>();
        var shortcuts = Shortcuts;

        foreach (var action in _actionRegistry.Enabled())
        {
            entries.Add(new PaletteEntry(
                action.Title,
                action.Category,
                shortcuts.GestureFor(action.Id) ?? string.Empty,
                action.Execute));
        }

        if (Dashboard is { } dash)
        {
            foreach (var branch in dash.ListLocalBranches().Where(b => !b.IsCurrentRepositoryHead))
            {
                var b = branch;
                entries.Add(new PaletteEntry(
                    $"Checkout {b.FriendlyName}", "Branch", string.Empty,
                    () => { dash.CheckoutBranchFromPalette(b); return System.Threading.Tasks.Task.CompletedTask; }));
            }
        }

        foreach (var repo in Categories.SelectMany(c => c.Repositories))
        {
            var r = repo;
            entries.Add(new PaletteEntry(
                $"Open {r.DisplayName}", "Repository", string.Empty,
                () => { OpenRepository(r); return System.Threading.Tasks.Task.CompletedTask; }));
        }

        return entries;
    }

    private void RegisterActions()
    {
        var ids = typeof(Mainguard.Git.Actions.ActionIds);
        void Add(string id, string title, string category, System.Func<bool> can, System.Action run) =>
            _actionRegistry.Register(new Mainguard.Git.Actions.AppAction
            {
                Id = id,
                Title = title,
                Category = category,
                CanExecute = can,
                Execute = () => { run(); return System.Threading.Tasks.Task.CompletedTask; },
            });

        Add(Mainguard.Git.Actions.ActionIds.OpenCommandPalette, "Open Command Palette", "General",
            () => true, OpenCommandPalette);
        Add(Mainguard.Git.Actions.ActionIds.Commit, "Commit", "Repository",
            () => Dashboard?.StagingPanel.CommitCommand.CanExecute(null) ?? false,
            () => Dashboard?.StagingPanel.CommitCommand.Execute(null));
        Add(Mainguard.Git.Actions.ActionIds.Push, "Push", "Repository",
            () => Dashboard?.PushCommand.CanExecute(null) ?? false,
            () => Dashboard?.PushCommand.Execute(null));
        Add(Mainguard.Git.Actions.ActionIds.Pull, "Pull", "Repository",
            () => Dashboard?.PullCommand.CanExecute(null) ?? false,
            () => Dashboard?.PullCommand.Execute(null));
        Add(Mainguard.Git.Actions.ActionIds.Fetch, "Fetch", "Repository",
            () => Dashboard?.FetchCommand.CanExecute(null) ?? false,
            () => Dashboard?.FetchCommand.Execute(null));
        Add(Mainguard.Git.Actions.ActionIds.Refresh, "Refresh Status", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.StagingPanel.RefreshCommand.Execute(null));
        Add(Mainguard.Git.Actions.ActionIds.NewBranch, "New Branch…", "Branch",
            () => Dashboard is not null,
            () => Dashboard?.BranchBrowser.OpenCreateBranchDialogCommand.Execute(null));
        Add(Mainguard.Git.Actions.ActionIds.CloseRepository, "Close Repository", "General",
            () => CurrentWorkspace is not null, CloseRepository);
        Add(Mainguard.Git.Actions.ActionIds.ToggleSidebar, "Toggle Sidebar", "View",
            () => true, ToggleSidebar);
        Add(Mainguard.Git.Actions.ActionIds.ManageRemotes, "Manage Remotes…", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.ManageRemotesCommand.Execute(null));
        Add(Mainguard.Git.Actions.ActionIds.ManageSubmodules, "Manage Submodules…", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.ManageSubmodulesCommand.Execute(null));
        Add(Mainguard.Git.Actions.ActionIds.ManageLfs, "Manage Git LFS…", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.ManageLfsCommand.Execute(null));
        Add(Mainguard.Git.Actions.ActionIds.ViewReflog, "View Reflog…", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.ViewReflogCommand.Execute(null));
        Add(Mainguard.Git.Actions.ActionIds.ViewPullRequests, "Pull Requests…", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.ManagePullRequestsCommand.Execute(null));
        Add(Mainguard.Git.Actions.ActionIds.ViewIssues, "Issues…", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.ManageIssuesCommand.Execute(null));
        Add(Mainguard.Git.Actions.ActionIds.ViewNotifications, "Notifications…", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.ManageNotificationsCommand.Execute(null));
        Add(Mainguard.Git.Actions.ActionIds.ViewReleases, "Releases…", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.ManageReleasesCommand.Execute(null));
        Add(Mainguard.Git.Actions.ActionIds.OpenAnalytics, "Open Analytics", "View",
            () => Dashboard is not null,
            () => { if (Dashboard is { } d) OpenAnalytics(new Repository { Path = d.RepositoryPath, DisplayName = d.RepositoryName }); });
        Add(Mainguard.Git.Actions.ActionIds.OpenCloudSync, "Clone / Cloud Sync", "General",
            () => true, OpenCloudSync);
    }

    // This is automatically triggered by the MVVM Toolkit whenever _selectedNode changes!
    partial void OnSelectedNodeChanged(object? value)
    {
        ClearAllSelections(Categories);

        if (value is Repository repo)
        {
            repo.IsSelected = true;
        }
        else if (value is WorkspaceCategory cat)
        {
            cat.IsSelected = true;
        }
    }

    private void ClearAllSelections(IEnumerable<WorkspaceCategory> categories)
    {
        foreach (var cat in categories)
        {
            cat.IsSelected = false;
            foreach (var repo in cat.Repositories)
            {
                repo.IsSelected = false;
            }
            if (cat.SubCategories != null)
            {
                ClearAllSelections(cat.SubCategories);
            }
        }
    }

    // True while a repo is being opened — lets the shell show it's doing something instead of
    // just freezing with no feedback while the dashboard's VM graph loads (#63).
    [ObservableProperty]
    private bool _isOpeningRepo;

    /// <summary>Fire-and-forget entry point for callers that can't await (XAML bindings, delegates).</summary>
    public void OpenRepository(Repository repo) => _ = OpenRepositoryAsync(repo);

    public async Task OpenRepositoryAsync(Repository repo)
    {
        var gitService = new Mainguard.Git.Services.GitService();
        if (!gitService.IsGitRepository(repo.Path))
        {
            InvalidRepository = repo;
            IsInvalidRepoCardVisible = true;
            return;
        }

        IsOpeningRepo = true;
        try
        {
            // RepoDashboardViewModel's constructor kicks off its initial load via Dispatcher-marshalled
            // work rather than ambient SynchronizationContext capture, so building the whole VM graph
            // (including the branch/author/path enumeration that used to block the UI thread here) off
            // the UI thread is safe and keeps the shell responsive while a repo loads.
            var dashboard = await Task.Run(() => new RepoDashboardViewModel(repo,
                // The callback lets the submodules panel (T-16) open a submodule as its own
                // top-level repository through the normal open path.
                openRepositoryPath: path => OpenRepository(
                    new Repository { Path = path, DisplayName = Path.GetFileName(path.TrimEnd('/', '\\')) })));

            CurrentWorkspace = dashboard;
            // (main's #61 sidebar auto-close is moot here: the phase-2 shell has no docked
            // sidebar — the repositories tree lives in RepoPickerWindow.)

            _settingsService.Update(p => p.LastOpenedRepoPath = repo.Path);
            IsReopenRepoCardVisible = false;

            // P2-06: best-effort registration of the daemon-owned sync remote. If the daemon is
            // reachable and provisions the repo, register whatever remote name/URL it resolved
            // (never a hardcoded literal). A missing/unreachable daemon is a silent no-op — this
            // never blocks or fails opening a repo.
            _ = TryRegisterSyncRemoteAsync(repo.Path);
        }
        finally
        {
            IsOpeningRepo = false;
        }
    }

    /// <summary>The repo whose agent provisioning failed, kept for the retry card. Cleared when a
    /// retry succeeds, another repo opens, or the user dismisses the card.</summary>
    private string? _agentProvisionRetryPath;

    [ObservableProperty]
    private bool _isAgentProvisionRetryVisible;

    [ObservableProperty]
    private string _agentProvisionFailureReason = string.Empty;

    [RelayCommand]
    private void DismissAgentProvisionRetry()
    {
        IsAgentProvisionRetryVisible = false;
        _agentProvisionRetryPath = null;
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RetryAgentProvisioningAsync()
    {
        if (_agentProvisionRetryPath is { } path)
        {
            IsAgentProvisionRetryVisible = false;
            await TryRegisterSyncRemoteAsync(path).ConfigureAwait(true);
        }
    }

    private async System.Threading.Tasks.Task TryRegisterSyncRemoteAsync(string repoPath)
    {
        // Provisioning the repo into the daemon is a Pro concern reached through the edition seam (2f):
        // the reference-clean shell never names DaemonClient. Under the plain Git client ControlCenter is
        // null → no-op. On success the sync-remote binding is registered on the host repo with the shell's
        // own IGitService (P2-06, host side) and the live merge-queue projection is pointed at the daemon's
        // repo handle (P2-47 #1). On FAILURE the reason is surfaced (toast + retry card) — for most of
        // this feature's life a failed provision was swallowed and the agent platform simply looked dead.
        if (ControlCenter is not { } controlCenter)
        {
            return;
        }

        IsAgentProvisionRetryVisible = false;
        _agentProvisionRetryPath = null;

        try
        {
            switch (await controlCenter.ProvisionRepoAsync(repoPath).ConfigureAwait(true))
            {
                case { Binding: { } binding }:
                    // Bind the live queue projection FIRST. Registering the sync remote is host-side git
                    // work on the user's own checkout and can genuinely throw (a held .git/index.lock, a
                    // read-only or malformed config); it used to run first, so any such throw fell to the
                    // catch below, surfaced a toast, and left the queue pump — which ProvisionRepoAsync
                    // had already torn down — dead for the rest of the session with the rail reading
                    // "Nothing queued" (ISSUES-LOG #11). Order matters and only in this direction:
                    // observing the queue never needed the remote, only MERGING does, and the merge path
                    // refuses honestly on its own when the fetch can't resolve.
                    controlCenter.SetActiveRepo(binding.RepoHandle);
                    new Services.SyncRemoteRegistrar(new Mainguard.Git.Services.GitService())
                        .Register(repoPath, binding.SyncRemoteName, binding.SyncRemoteUrl);
                    break;
                case { FailureReason: { } reason }:
                    SurfaceAgentProvisionFailure(repoPath, reason);
                    break;
                    // null = no daemon behind this surface (mock/design harness): not an error.
            }
        }
        catch (Exception ex)
        {
            // A fault in the seam itself (not a daemon refusal) still must not block opening the
            // repo — but it is surfaced the same way rather than swallowed.
            SurfaceAgentProvisionFailure(repoPath, ex.Message);
        }
    }

    private void SurfaceAgentProvisionFailure(string repoPath, string reason)
    {
        _agentProvisionRetryPath = repoPath;
        AgentProvisionFailureReason = reason;
        IsAgentProvisionRetryVisible = true;
        ShowToast($"Agents are unavailable for this repo — {reason}", isError: true);
    }

    [RelayCommand]
    private void CloseRepository()
    {
        CurrentWorkspace = null;
    }

    [RelayCommand]
    public void OpenAnalytics(Repository repo)
    {
        var gitService = new Mainguard.Git.Services.GitService();
        if (!gitService.IsGitRepository(repo.Path))
        {
            InvalidRepository = repo;
            IsInvalidRepoCardVisible = true;
            return;
        }

        // Load the analytics workspace
        CurrentWorkspace = new AnalyticsViewModel(repo.Path);
    }

    [RelayCommand]
    public void OpenCloudSync()
    {
        var cloneDashboard = new CloneDashboardViewModel();

        // Wire up the dialog
        cloneDashboard.ShowDeviceFlowDialogAction = (deviceFlow) =>
        {
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
                {
                    var dialog = new DeviceFlowAuthDialog(deviceFlow.VerificationUri, deviceFlow.UserCode);

                    cloneDashboard.CloseDeviceFlowDialogAction = () =>
                    {
                        Dispatcher.UIThread.Post(() => dialog.Close());
                    };

                    dialog.Closed += (s, e) =>
                    {
                        if (cloneDashboard.CancelLoginCommand.CanExecute(null))
                        {
                            cloneDashboard.CancelLoginCommand.Execute(null);
                        }
                    };

                    await dialog.ShowDialog(desktop.MainWindow);
                }
            });
        };

        cloneDashboard.OnCloneRequested = async (repo) =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                var folder = await desktop.MainWindow.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Select Clone Destination",
                    AllowMultiple = false
                });

                if (folder.Count > 0)
                {
                    var targetFolder = System.IO.Path.Combine(folder[0].Path.LocalPath, repo.Name);

                    // T-21: clone through the progress-reporting, cancellable ICloneService (a cancelled
                    // clone deletes the partial directory). The bar/cancel live on the CloneDashboard.
                    var ok = await cloneDashboard.RunCloneAsync(repo.CloneUrl, targetFolder);
                    if (ok)
                    {
                        var cat = Categories.FirstOrDefault(c => c.Name == "Uncategorized");
                        if (cat != null)
                        {
                            cat.Repositories.Add(new Repository { DisplayName = repo.Name, Path = targetFolder });
                        }
                        cloneDashboard.StatusMessage = $"Successfully cloned {repo.Name}!";
                    }
                }
            }
        };

        CurrentWorkspace = cloneDashboard;
    }

    public MainWindowViewModel()
        : this(degradedBanner: null)
    {
    }

    /// <summary>The shell, entered from the startup loading screen. <paramref name="degradedBanner"/> carries
    /// the degraded-entry banner TEXT when an essential startup step exhausted its budget (null = ready).
    /// The Pro startup loader passes <c>StartupResult.DegradedBanner</c> through the CreateShellWindow seam;
    /// the reference-clean shell takes only the string, never naming the Pro <c>StartupResult</c> type (2f).</summary>
    public MainWindowViewModel(string? degradedBanner)
    {
        // Edition seam (1a): the manifest builds the Pro control center (Pro routes through
        // App.CreateOrchestratorServices, preserving the harness mock-injection seam) or none under
        // the Client edition. Set FIRST so every use below sees it; null ⇒ no agent platform.
        ControlCenter = App.Edition.CreateControlCenter();

        _startupBanner = degradedBanner;
        // Clear the degraded banner the moment the daemon connects (the existing reconnect machinery
        // heals it; we only observe the cheapest correct signal). Subscribed before any load below.
        if (ControlCenter is { } controlCenter)
            controlCenter.DaemonReachable += OnDaemonReachable;

        // Build the data-driven section rail from the edition's manifest (1b). The first workspace-scoped
        // destination carries the shipped hairline that split the git/agent sections from the host tabs.
        // The initially-active row matches SelectedSectionId (Repo), reproducing the shipped default.
        RailSections = new ObservableCollection<RailSectionViewModel>();
        RebuildRailSections();

        RegisterActions();
        CommandPalette = new CommandPaletteViewModel(BuildPaletteEntries);
        CommandPalette.RequestClose += () => IsCommandPaletteOpen = false;

        SidebarColumnWidth = new Avalonia.Controls.GridLength(_settingsService.Current.SidebarWidth, Avalonia.Controls.GridUnitType.Pixel);
        AutoDetectPath = _settingsService.Current.AutoDetectPath;

        // Restore the control-center layout + rail + prompting mode (persisted like Theme).
        ControlCenter?.SetPreset(_settingsService.Current.WorkspaceLayout);
        ControlCenter?.SetDirectPrompting(_settingsService.Current.DirectAgentPrompting);
        IsRailExpanded = _settingsService.Current.SectionRailExpanded;

        // PR3: the exit guard's live-agent count — a full exit that would stop the VM warns first.
        // Null under an edition with no agent platform (Client) ⇒ zero live agents.
        App.LiveAgentCountProvider = () => ControlCenter?.LiveAgentCount ?? 0;

        LoadCategories();

        var lastRepoPath = _settingsService.Current.LastOpenedRepoPath;

        if (!string.IsNullOrEmpty(lastRepoPath) && Directory.Exists(lastRepoPath))
        {
            ReopenRepositoryPath = lastRepoPath;
            IsReopenRepoCardVisible = true;
        }

        if (HasAutoDetectPath)
        {
            ScanAutoDetectFolderAsync().ContinueWith(_ => { });
        }
    }

    [RelayCommand]
    private void DismissReopenRepoCard()
    {
        IsReopenRepoCardVisible = false;
    }

    [RelayCommand]
    private void ReopenLastRepo()
    {
        IsReopenRepoCardVisible = false;
        var repo = Categories.SelectMany(c => c.Repositories).FirstOrDefault(r => r.Path == ReopenRepositoryPath);
        if (repo != null)
        {
            OpenRepository(repo);
        }
        else
        {
            var newRepo = new Repository { Path = ReopenRepositoryPath, DisplayName = Path.GetFileName(ReopenRepositoryPath) };
            OpenRepository(newRepo);
        }
    }

    private void LoadCategories()
    {
        using var dbContext = new AppDbContext();
        var allCategories = dbContext.WorkspaceCategories
            .Include(c => c.Repositories)
            .ToList();

        var rootCategories = allCategories
            .Where(c => c.ParentCategoryId == null)
            .OrderBy(c => c.DisplayOrder)
            .ToList();

        Categories.Clear();
        foreach (var cat in rootCategories)
        {
            cat.Repositories = new ObservableCollection<Repository>(cat.Repositories);
            SetupCategory(cat);
        }
    }

    private void SetupCategory(WorkspaceCategory cat)
    {
        cat.IsExpanded = Mainguard.App.Shell.App.Settings.Current.SidebarExpandedStates.GetValueOrDefault("Workspace_" + cat.Name, false);
        cat.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceCategory.IsExpanded))
            {
                Mainguard.App.Shell.App.Settings.Update(p => p.SidebarExpandedStates["Workspace_" + cat.Name] = cat.IsExpanded);
            }
        };

        if (cat.SubCategories != null)
        {
            cat.SubCategories = new ObservableCollection<WorkspaceCategory>(cat.SubCategories);
            foreach (var sub in cat.SubCategories)
            {
                sub.Repositories = new ObservableCollection<Repository>(sub.Repositories);
                SetupCategorySub(sub);
            }
        }
        else
        {
            cat.SubCategories = new ObservableCollection<WorkspaceCategory>();
        }

        Categories.Add(cat);
    }

    private void SetupCategorySub(WorkspaceCategory cat)
    {
        cat.IsExpanded = Mainguard.App.Shell.App.Settings.Current.SidebarExpandedStates.GetValueOrDefault("Workspace_" + cat.Name, false);
        cat.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceCategory.IsExpanded))
            {
                Mainguard.App.Shell.App.Settings.Update(p => p.SidebarExpandedStates["Workspace_" + cat.Name] = cat.IsExpanded);
            }
        };
    }

    [RelayCommand]
    private void CreateCategory()
    {
        using var dbContext = new AppDbContext();
        var newCat = new WorkspaceCategory { Name = "New Category", DisplayOrder = Categories.Count };
        dbContext.WorkspaceCategories.Add(newCat);
        dbContext.SaveChanges();

        newCat.IsEditingName = true;
        SetupCategory(newCat);
    }

    [RelayCommand]
    private void CreateSubCategory(WorkspaceCategory parentCat)
    {
        using var dbContext = new AppDbContext();
        var newSubCat = new WorkspaceCategory { Name = "New Sub-Category", DisplayOrder = parentCat.SubCategories?.Count ?? 0, ParentCategoryId = parentCat.CategoryId };
        dbContext.WorkspaceCategories.Add(newSubCat);
        dbContext.SaveChanges();

        LoadCategories();

        var loadedParent = FindCategoryById(parentCat.CategoryId, Categories);
        if (loadedParent != null)
        {
            loadedParent.IsExpanded = true;
            var sub = loadedParent.SubCategories.FirstOrDefault(s => s.CategoryId == newSubCat.CategoryId);
            if (sub != null)
            {
                sub.IsEditingName = true;
            }
        }
    }

    private WorkspaceCategory? FindCategoryById(int id, IEnumerable<WorkspaceCategory> list)
    {
        foreach (var cat in list)
        {
            if (cat.CategoryId == id) return cat;
            if (cat.SubCategories != null)
            {
                var found = FindCategoryById(id, cat.SubCategories);
                if (found != null) return found;
            }
        }
        return null;
    }

    [RelayCommand]
    private void RenameCategory(WorkspaceCategory cat)
    {
        cat.IsEditingName = true;
    }

    [RelayCommand]
    private void SaveCategoryName(WorkspaceCategory cat)
    {
        cat.IsEditingName = false;
        using var dbContext = new AppDbContext();
        var dbCat = dbContext.WorkspaceCategories.Find(cat.CategoryId);
        if (dbCat != null)
        {
            dbCat.Name = cat.Name;
            dbContext.SaveChanges();
        }
    }

    [RelayCommand]
    private void CancelCategoryName(WorkspaceCategory cat)
    {
        cat.IsEditingName = false;
        if (cat.Name == "New Category" || cat.Name == "New Sub-Category")
        {
            // User cancelled creating a new category, delete it silently
            ExecuteDeleteCategory(cat);
        }
    }

    [RelayCommand]
    private void DeleteCategory(WorkspaceCategory cat)
    {
        _nodeToDelete = cat;
        DeleteConfirmationTitle = "Delete Category";
        DeleteConfirmationMessage = $"Are you sure you want to delete the category '{cat.Name}' and all its contents?";
        IsDeleteConfirmationVisible = true;
    }

    private void ExecuteDeleteCategory(WorkspaceCategory cat)
    {
        using var dbContext = new AppDbContext();
        var dbCat = dbContext.WorkspaceCategories.Include(c => c.Repositories).FirstOrDefault(c => c.CategoryId == cat.CategoryId);
        if (dbCat != null)
        {
            // Move its repos to the first available category if any
            var otherCat = dbContext.WorkspaceCategories.FirstOrDefault(c => c.CategoryId != cat.CategoryId && c.ParentCategoryId == null);
            if (otherCat != null)
            {
                foreach (var r in dbCat.Repositories.ToList())
                {
                    r.CategoryId = otherCat.CategoryId;
                }
            }
            else if (dbCat.Repositories.Any())
            {
                // Cannot delete the only category if it has repos
                return;
            }
            dbContext.WorkspaceCategories.Remove(dbCat);
            dbContext.SaveChanges();
            LoadCategories();
        }
    }

    [RelayCommand]
    private async Task AddRepositoryToCategoryAsync(WorkspaceCategory category)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var storageProvider = desktop.MainWindow.StorageProvider;
            var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"Select Git Repository for {category.Name}",
                AllowMultiple = false
            });

            if (result.Count > 0)
            {
                string path = result[0].Path.LocalPath;
                var gitService = new Mainguard.Git.Services.GitService();

                if (gitService.IsGitRepository(path))
                {
                    using var dbContext = new AppDbContext();
                    if (await dbContext.Repositories.AnyAsync(r => r.Path == path)) return;

                    var repo = new Repository
                    {
                        Path = path,
                        DisplayName = Path.GetFileName(path),
                        CategoryId = category.CategoryId,
                        LastAccessed = System.DateTime.UtcNow
                    };

                    dbContext.Repositories.Add(repo);
                    await dbContext.SaveChangesAsync();
                    LoadCategories(); // Refresh Sidebar
                }
            }
        }
    }

    [RelayCommand]
    private async Task MoveRepositoryAsync(Repository repo)
    {
        using var dbContext = new AppDbContext();

        // Find the OTHER category. If it's in Personal, find Work. If Work, find Personal.
        var targetCategory = await dbContext.WorkspaceCategories.FirstOrDefaultAsync(c => c.CategoryId != repo.CategoryId);

        if (targetCategory != null)
        {
            var dbRepo = await dbContext.Repositories.FindAsync(repo.RepositoryId);
            if (dbRepo != null)
            {
                dbRepo.CategoryId = targetCategory.CategoryId;
                await dbContext.SaveChangesAsync();
                LoadCategories();
            }
        }
    }

    public void MoveRepositoryToCategory(Repository repo, WorkspaceCategory targetCategory)
    {
        if (repo.CategoryId == targetCategory.CategoryId) return; // Already there!

        using var dbContext = new AppDbContext();
        var dbRepo = dbContext.Repositories.Find(repo.RepositoryId);

        if (dbRepo != null)
        {
            dbRepo.CategoryId = targetCategory.CategoryId;
            dbContext.SaveChanges();
            LoadCategories();
        }
    }

    public void MoveCategoryToCategory(WorkspaceCategory source, WorkspaceCategory? target)
    {
        if (target != null && source.CategoryId == target.CategoryId) return;
        if (target != null && target.ParentCategoryId == source.CategoryId) return; // Can't move parent into child

        using var dbContext = new AppDbContext();
        var dbCat = dbContext.WorkspaceCategories.Find(source.CategoryId);
        if (dbCat != null)
        {
            dbCat.ParentCategoryId = target?.CategoryId;
            dbContext.SaveChanges();
            LoadCategories();
        }
    }

    [RelayCommand]
    private void RemoveRepository(Repository repo)
    {
        _nodeToDelete = repo;
        DeleteConfirmationTitle = "Remove Repository";
        DeleteConfirmationMessage = $"Are you sure you want to remove '{repo.DisplayName}' from Mainguard? (Your local files will not be deleted)";
        IsDeleteConfirmationVisible = true;
    }

    private async Task ExecuteRemoveRepositoryAsync(Repository repo)
    {
        using var dbContext = new AppDbContext();
        var dbRepo = await dbContext.Repositories.FindAsync(repo.RepositoryId);
        if (dbRepo != null)
        {
            dbContext.Repositories.Remove(dbRepo);
            await dbContext.SaveChangesAsync();
            LoadCategories();

            // If they removed the repo they are currently looking at, close the dashboard!
            if (CurrentWorkspace is RepoDashboardViewModel rvm && rvm.RepositoryName == repo.DisplayName)
            {
                CurrentWorkspace = null;
            }
        }
    }

    [RelayCommand]
    private void CancelInvalidRepoCard()
    {
        IsInvalidRepoCardVisible = false;
        InvalidRepository = null;
    }

    [RelayCommand]
    private async Task RemoveInvalidRepoAsync()
    {
        if (InvalidRepository != null)
        {
            await ExecuteRemoveRepositoryAsync(InvalidRepository);
            CancelInvalidRepoCard();
        }
    }

    [RelayCommand]
    private async Task ChangeInvalidRepoPathAsync()
    {
        if (InvalidRepository == null) return;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var storageProvider = desktop.MainWindow.StorageProvider;
            var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"Select new Git Repository location for {InvalidRepository.DisplayName}",
                AllowMultiple = false
            });

            if (result.Count > 0)
            {
                string path = result[0].Path.LocalPath;
                var gitService = new Mainguard.Git.Services.GitService();

                if (gitService.IsGitRepository(path))
                {
                    using var dbContext = new AppDbContext();
                    var dbRepo = await dbContext.Repositories.FindAsync(InvalidRepository.RepositoryId);
                    if (dbRepo != null)
                    {
                        dbRepo.Path = path;
                        dbRepo.DisplayName = Path.GetFileName(path);
                        await dbContext.SaveChangesAsync();

                        LoadCategories(); // Refresh Sidebar

                        var updatedRepo = dbRepo;
                        CancelInvalidRepoCard();
                        OpenRepository(updatedRepo);
                    }
                }
            }
        }
    }

    [RelayCommand]
    private void SetRepoColorRed(Repository repo) => SetRepoColor(repo, "#FF5252");
    [RelayCommand]
    private void SetRepoColorGreen(Repository repo) => SetRepoColor(repo, "#4CAF50");
    [RelayCommand]
    private void SetRepoColorBlue(Repository repo) => SetRepoColor(repo, "#569CD6");
    [RelayCommand]
    private void SetRepoColorYellow(Repository repo) => SetRepoColor(repo, "#FFEB3B");
    [RelayCommand]
    private void SetRepoColorPurple(Repository repo) => SetRepoColor(repo, "#9C27B0");

    private void SetRepoColor(Repository repo, string hexColor)
    {
        repo.CustomIconColor = hexColor;
        using var db = new AppDbContext();
        var dbRepo = db.Repositories.Find(repo.RepositoryId);
        if (dbRepo != null)
        {
            dbRepo.CustomIconColor = hexColor;
            db.SaveChanges();
        }
    }

    [RelayCommand]
    private async Task SelectAutoDetectFolderAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var storageProvider = desktop.MainWindow.StorageProvider;
            var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select folder for auto-detecting repositories",
                AllowMultiple = false
            });

            if (result.Count > 0)
            {
                AutoDetectPath = result[0].Path.LocalPath;
                _settingsService.Update(p => p.AutoDetectPath = AutoDetectPath);
                await ScanAutoDetectFolderAsync();
            }
        }
    }

    [ObservableProperty]
    private bool _isScanning;

    [RelayCommand]
    private async Task ScanAutoDetectFolderAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        var minTask = Task.Delay(1000);

        try
        {
            if (string.IsNullOrEmpty(AutoDetectPath) || !Directory.Exists(AutoDetectPath)) return;

            var gitService = new Mainguard.Git.Services.GitService();
            using var dbContext = new AppDbContext();

            var defaultCategory = await dbContext.WorkspaceCategories.FirstOrDefaultAsync(c => c.Name == "Personal")
                                ?? await dbContext.WorkspaceCategories.FirstOrDefaultAsync();

            if (defaultCategory == null) return;

            // The directory walk itself lives in the pure, unit-pinned Services/AutoDetectScan —
            // including the case this method used to get wrong: a chosen root that IS a repository
            // is that one repository (walking its children added its `.git` dir as a repo named
            // ".git" — walkthrough bug W3).
            var candidates = Services.AutoDetectScan.Scan(AutoDetectPath, gitService.IsGitRepository);
            bool anyAdded = false;
            var groupCategories = new Dictionary<string, WorkspaceCategory>(StringComparer.Ordinal);

            foreach (var candidate in candidates)
            {
                if (await dbContext.Repositories.AnyAsync(r => r.Path == candidate.Path)) continue;

                var category = defaultCategory;
                if (!string.IsNullOrEmpty(candidate.CategoryName))
                {
                    var groupName = candidate.CategoryName!;
                    if (!groupCategories.TryGetValue(groupName, out var groupCategory))
                    {
                        // A grouping folder only becomes a category when it actually contributes a
                        // new repository — an all-known group adds nothing, as before.
                        groupCategory = await dbContext.WorkspaceCategories.FirstOrDefaultAsync(c => c.Name == groupName);
                        if (groupCategory == null)
                        {
                            groupCategory = new WorkspaceCategory { Name = groupName };
                            dbContext.WorkspaceCategories.Add(groupCategory);
                            await dbContext.SaveChangesAsync();
                        }
                        groupCategories[groupName] = groupCategory;
                    }
                    category = groupCategory;
                }

                dbContext.Repositories.Add(new Repository
                {
                    Path = candidate.Path,
                    DisplayName = candidate.DisplayName,
                    CategoryId = category.CategoryId,
                    LastAccessed = System.DateTime.UtcNow
                });
                anyAdded = true;
            }

            if (anyAdded)
            {
                await dbContext.SaveChangesAsync();
                LoadCategories();
            }
        }
        finally
        {
            await minTask;
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task CreateGitRepositoryAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var storageProvider = desktop.MainWindow.StorageProvider;
            var result = await storageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Select Empty Folder to Initialize Git Repository",
                AllowMultiple = false
            });

            if (result.Count > 0)
            {
                string path = result[0].Path.LocalPath;
                try
                {
                    LibGit2Sharp.Repository.Init(path);

                    using var dbContext = new AppDbContext();
                    if (!await dbContext.Repositories.AnyAsync(r => r.Path == path))
                    {
                        var defaultCategory = await dbContext.WorkspaceCategories.FirstOrDefaultAsync();
                        if (defaultCategory != null)
                        {
                            var repo = new Repository
                            {
                                Path = path,
                                DisplayName = Path.GetFileName(path),
                                CategoryId = defaultCategory.CategoryId,
                                LastAccessed = System.DateTime.UtcNow
                            };
                            dbContext.Repositories.Add(repo);
                            await dbContext.SaveChangesAsync();
                            LoadCategories();
                            OpenRepository(repo);
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    // Initialization failed
                    System.Console.WriteLine("Git Init Failed: " + ex.Message);
                }
            }
        }
    }

    [RelayCommand]
    private async Task ChangeRepoIconColorAsync(Repository repo)
    {
        // For simplicity, cycle through some nice colors: Cyan, Red, Green, Purple, Yellow
        var colors = new[] { "#00CED1", "#FF5C5C", "#4CAF50", "#9C27B0", "#FFC107" };
        var currentColor = repo.CustomIconColor;
        var nextColor = colors[0];

        var index = System.Array.IndexOf(colors, currentColor);
        if (index >= 0 && index < colors.Length - 1)
            nextColor = colors[index + 1];
        else if (index == colors.Length - 1)
            nextColor = colors[0];

        repo.CustomIconColor = nextColor;

        using var dbContext = new AppDbContext();
        var dbRepo = await dbContext.Repositories.FindAsync(repo.RepositoryId);
        if (dbRepo != null)
        {
            dbRepo.CustomIconColor = nextColor;
            await dbContext.SaveChangesAsync();
            LoadCategories();
        }
    }
}
