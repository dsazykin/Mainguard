using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Services;
using Mainguard.UI.Theming;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.ViewModels;

/// <summary>
/// Settings → General: everything that used to live directly in the File dropdown's Theme/Layout/
/// Agent-prompting/Window-&amp;-Exit submenus, plus the pinned-sidebar-icons picker (formerly the
/// small standalone Settings dialog's whole content). One page, not five, since none of these
/// individually justify their own sidebar row.
/// </summary>
public partial class GeneralSettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly System.Action _onPinsChanged;

    public GeneralSettingsViewModel(
        ISettingsService settingsService,
        bool hasAgentPlatform,
        IRelayCommand<string> setLayoutCommand,
        IRelayCommand<string> setAgentPromptingCommand,
        System.Action onPinsChanged)
    {
        _settingsService = settingsService;
        HasAgentPlatform = hasAgentPlatform;
        SetLayoutCommand = setLayoutCommand;
        SetAgentPromptingCommand = setAgentPromptingCommand;
        _onPinsChanged = onPinsChanged;

        ThemeRows.Add(new SettingsThemeRowViewModel
        {
            Key = ThemeManager.SystemKey,
            DisplayName = "System",
            Tooltip = "Follow the OS appearance: dark → Midnight Loom, light → Daylight Loom",
        });
        foreach (var theme in ThemeManager.Themes)
            ThemeRows.Add(new SettingsThemeRowViewModel { Key = theme.Key, DisplayName = theme.DisplayName });
        RefreshThemeSelection();

        var pinned = _settingsService.Current.PinnedMenuIds;
        foreach (var def in PinnableMenus.All)
        {
            var row = new SettingsPinRowViewModel { Id = def.Id, Label = def.Label, IsPinned = pinned.Contains(def.Id) };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsPinRowViewModel.IsPinned)) TogglePin(row);
            };
            PinRows.Add(row);
        }
    }

    /// <summary>True when this edition composes the agent platform — gates Layout/Agent-prompting/
    /// Stop-VM-on-exit, all agent-platform-only preferences.</summary>
    public bool HasAgentPlatform { get; }

    public IRelayCommand<string> SetLayoutCommand { get; }
    public IRelayCommand<string> SetAgentPromptingCommand { get; }

    public ObservableCollection<SettingsPinRowViewModel> PinRows { get; } = new();

    public ObservableCollection<SettingsThemeRowViewModel> ThemeRows { get; } = new();

    [RelayCommand]
    private void SetTheme(string themeKey)
    {
        ThemeManager.Apply(themeKey);
        RefreshThemeSelection();
    }

    /// <summary>Marks the row matching the PERSISTED choice — the "System" pseudo-row while the OS
    /// drives the theme, else the applied key. Refreshed here rather than via the static
    /// <c>ThemeManager.ThemeChanged</c> event: this VM is rebuilt per Settings visit, and a static
    /// subscription would pin every past instance alive.</summary>
    private void RefreshThemeSelection()
    {
        var selected = ThemeManager.IsFollowingSystem ? ThemeManager.SystemKey : ThemeManager.CurrentKey;
        foreach (var row in ThemeRows)
            row.IsSelected = row.Key == selected;
    }

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

    private void TogglePin(SettingsPinRowViewModel row)
    {
        _settingsService.Update(p =>
        {
            if (row.IsPinned)
            {
                if (!p.PinnedMenuIds.Contains(row.Id)) p.PinnedMenuIds.Add(row.Id);
            }
            else
            {
                p.PinnedMenuIds.Remove(row.Id);
            }
        });
        _onPinsChanged();
    }
}
