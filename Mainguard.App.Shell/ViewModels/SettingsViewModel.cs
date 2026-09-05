using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.App.Shell.Views;
using Mainguard.Git;
using Mainguard.Git.Services;
using Mainguard.UI.Editions;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.ViewModels;

/// <summary>One pinnable sidebar-icon row (#78): its label + a checkbox-backed pin toggle. Lives on
/// <see cref="GeneralSettingsViewModel"/> (the General page), not the top-level Settings VM.</summary>
public partial class SettingsPinRowViewModel : ViewModelBase
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;

    [ObservableProperty]
    private bool _isPinned;
}

/// <summary>One theme choice in Settings → General: a ThemeManager key (or the "System" pseudo-key)
/// plus the selected marker. Rows are built from <c>ThemeManager.Themes</c> so the picker follows
/// the registered lineup instead of restating it. Lives on <see cref="GeneralSettingsViewModel"/>.</summary>
public partial class SettingsThemeRowViewModel : ViewModelBase
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Tooltip { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// File → Settings…: the page-rail host, restructured from a single small dialog into a sidebar of
/// pages + a content panel — the same <c>RailSectionViewModel</c>/<c>ActivateSection</c> pattern
/// <c>MainWindowViewModel</c> already uses for its own section rail, scaled down to Settings' needs
/// (every page is always enabled; no adornments).
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    public ObservableCollection<SettingsPageRowViewModel> Pages { get; } = new();

    [ObservableProperty]
    private object? _activePageContent;

    /// <summary>Set by <c>MainWindowViewModel.OpenSettingsAsync</c> right after the hosting window is
    /// constructed — needed by the Mainguard OS page, whose folder-picker dialogs need a real Window
    /// owner. Read lazily (pages build on first activation, by which point this is always set).</summary>
    public Window? OwnerWindow { get; set; }

    public SettingsViewModel(
        ISettingsService settingsService,
        bool hasAgentPlatform,
        IRelayCommand<string> setLayoutCommand,
        IRelayCommand<string> setAgentPromptingCommand,
        Action onPinsChanged,
        Func<ShortcutSettingsViewModel> buildShortcutSettings,
        Func<string?> currentRepoPath,
        Func<Task>? refreshCurrentWorkspace,
        IProToolsSurface? proTools,
        VersionsViewModel? versions = null)
    {
        var activeVersions = versions ?? new VersionsViewModel();

        Pages.Add(new SettingsPageRowViewModel("General", "General", "MenuIcon",
            () => new GeneralSettingsViewModel(settingsService, hasAgentPlatform, setLayoutCommand, setAgentPromptingCommand, onPinsChanged),
            ActivateRow));

        Pages.Add(new SettingsPageRowViewModel("KeyboardShortcuts", "Keyboard Shortcuts", "KeyIcon",
            () => buildShortcutSettings(), ActivateRow));

        Pages.Add(new SettingsPageRowViewModel("Accounts", "Accounts", "LockIcon",
            BuildAccountsPage, ActivateRow));

        Pages.Add(new SettingsPageRowViewModel("SshKeys", "SSH Keys", "KeyIcon",
            () => new SshKeysViewModel(), ActivateRow));

        Pages.Add(new SettingsPageRowViewModel("GitProfiles", "Git Profiles", "PersonIcon",
            () => new ProfilesPageViewModel(
                new ProfilesViewModel(new ProfileService(() => new AppDbContext(), new GitService()), currentRepoPath()),
                refreshCurrentWorkspace),
            ActivateRow));

        // Agent-platform-only pages (Pro edition): omitted entirely under an edition with no agent
        // platform, same as RebuildRailSections filtering unpinned rail rows.
        if (hasAgentPlatform && proTools is not null)
        {
            Pages.Add(new SettingsPageRowViewModel("AiProviders", "AI Providers", "KeyIcon",
                proTools.CreateAiProvidersPage, ActivateRow));
            Pages.Add(new SettingsPageRowViewModel("AgentClis", "Agent CLIs", "TerminalIcon",
                proTools.CreateAgentClisPage, ActivateRow));
            Pages.Add(new SettingsPageRowViewModel("Toolchains", "Toolchains", "TerminalIcon",
                proTools.CreateToolchainsPage, ActivateRow));
            // External-PR intake (P2-12). It sits next to the agent-platform pages because that is what
            // it configures: an intake'd pull request becomes a jailed worker and a merge-queue entry
            // exactly like a local agent. Before this row the feature's settings surface existed as an
            // orphaned Window nothing constructed, so intake was unconfigurable in the shipped app.
            Pages.Add(new SettingsPageRowViewModel("PrIntake", "PR Intake", "PullRequestIcon",
                proTools.CreatePrIntakePage, ActivateRow));
            // The per-jail memory/CPU ceiling (owner decision 2026-09-04): daemon state, so a weaker
            // machine can shrink what every spawn is created with.
            Pages.Add(new SettingsPageRowViewModel("AgentJails", "Agent Jails", "ShieldIcon",
                proTools.CreateJailLimitsPage, ActivateRow));
            Pages.Add(new SettingsPageRowViewModel("MainguardOs", "Mainguard OS", "FolderIcon",
                () => proTools.CreateMainguardOsPage(OwnerWindow!) ?? new object(), ActivateRow));
            Pages.Add(new SettingsPageRowViewModel("DaemonLogs", "Daemon Logs", "TerminalIcon",
                proTools.CreateDaemonLogsPage, ActivateRow));
        }

        Pages.Add(new SettingsPageRowViewModel("About", "About", "DocumentIcon", () => activeVersions, ActivateRow));

        ActivatePage("General");
    }

    private object BuildAccountsPage()
    {
        var authContext = new Mainguard.Git.Sync.HostAuthContext
        {
            PresentDeviceCode = device =>
            {
                var dlg = new DeviceFlowAuthDialog(device.VerificationUri, device.UserCode);
                if (OwnerWindow is not null) _ = dlg.ShowDialog(OwnerWindow);
                return Task.CompletedTask;
            },
            BrowserOpener = new Mainguard.App.Shell.Services.BrowserOpener(),
            LoopbackChannelFactory = () => new Mainguard.Git.Security.HttpListenerCallbackChannel(),
        };
        return new AccountsViewModel(authContext: authContext);
    }

    private void ActivateRow(string id) => ActivatePage(id);

    /// <summary>Switches the active page, running any <see cref="ISettingsPage"/> deactivate/activate
    /// hooks and discarding the cache of any outgoing <see cref="IDisposable"/> page (so the next visit
    /// rebuilds fresh rather than reusing a disposed instance — needed by Daemon Logs).
    /// <paramref name="focusHost"/> pre-fills the Accounts page's "add a host" field — the auth-failure
    /// deep link (<c>RepoDashboardViewModel.HandleGitActionException</c>) routes through this.</summary>
    public void ActivatePage(string pageId, string? focusHost = null)
    {
        var previous = Pages.FirstOrDefault(p => p.IsActive);
        if (previous?.Content is ISettingsPage leaving) leaving.OnDeactivated();
        if (previous?.Content is IDisposable) previous.Rebuild();

        var target = Pages.FirstOrDefault(p => p.Id == pageId) ?? Pages[0];
        foreach (var p in Pages) p.IsActive = p == target;
        ActivePageContent = target.Content;
        if (ActivePageContent is ISettingsPage entering) entering.OnActivated();

        if (!string.IsNullOrEmpty(focusHost) && ActivePageContent is AccountsViewModel accounts)
        {
            accounts.NewHost = focusHost;
            accounts.AddCustomHostCommand.Execute(null);
        }
    }
}
