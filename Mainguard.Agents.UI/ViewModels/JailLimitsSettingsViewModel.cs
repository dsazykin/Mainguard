using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.UI.Services;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// Settings → Agent Jails (owner decision 2026-09-04): the per-jail memory/CPU ceiling every spawn is
/// created with. Daemon state over <see cref="IJailLimitsGateway"/>, exactly like PR Intake — the daemon
/// spawns, so it owns the number; a save re-renders from what the daemon PERSISTED (clamped), and a daemon
/// that cannot be reached is an error on the page, never a silent success.
/// </summary>
public partial class JailLimitsSettingsViewModel : ViewModelBase
{
    private readonly IJailLimitsGateway _gateway;

    [ObservableProperty]
    private double _memoryGiB = 2;

    [ObservableProperty]
    private double _cpus = 2;

    [ObservableProperty]
    private double _minMemoryGiB = 0.5;

    [ObservableProperty]
    private double _maxMemoryGiB = 64;

    [ObservableProperty]
    private double _minCpus = 0.5;

    [ObservableProperty]
    private double _maxCpus = 64;

    /// <summary>True while the daemon still runs the compiled defaults.</summary>
    [ObservableProperty]
    private bool _isDefault = true;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLoaded;

    public JailLimitsSettingsViewModel(IJailLimitsGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _ = LoadAsync();
    }

    /// <summary>What the numbers mean for the machine, in one line the operator can check against Activity
    /// Monitor: with N workers live, the jails alone may take N × memory.</summary>
    public string FleetNote => string.Format(
        CultureInfo.CurrentCulture,
        "Every jail may use up to {0:0.##} GiB and {1:0.##} CPUs. Six workers at once can therefore take up to {2:0.##} GiB on their own.",
        MemoryGiB, Cpus, MemoryGiB * 6);

    partial void OnMemoryGiBChanged(double value) => OnPropertyChanged(nameof(FleetNote));

    partial void OnCpusChanged(double value) => OnPropertyChanged(nameof(FleetNote));

    private bool CanAct => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        LoadCommand.NotifyCanExecuteChanged();
        ResetToDefaultsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            Apply(await _gateway.LoadAsync(CancellationToken.None).ConfigureAwait(true));
            ErrorMessage = null;
            IsLoaded = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not read the daemon's jail limits: {ex.Message}";
            StatusMessage = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    public async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            Apply(await _gateway.SaveAsync(MemoryGiB, Cpus, CancellationToken.None).ConfigureAwait(true));
            ErrorMessage = null;
            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                "Saved: {0:0.##} GiB and {1:0.##} CPUs per jail. Applies to jails started from now on; running jails keep their current ceiling.",
                MemoryGiB, Cpus);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"The daemon did not save the jail limits: {ex.Message}";
            StatusMessage = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Puts the compiled defaults (2 GiB, 2 CPUs) back on the page; Save persists them.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    public void ResetToDefaults()
    {
        MemoryGiB = 2;
        Cpus = 2;
        StatusMessage = "Defaults restored on the page — Save to apply them.";
    }

    private void Apply(JailLimitsView view)
    {
        MinMemoryGiB = view.MinMemoryGiB;
        MaxMemoryGiB = view.MaxMemoryGiB;
        MinCpus = view.MinCpus;
        MaxCpus = view.MaxCpus;
        MemoryGiB = view.MemoryGiB;
        Cpus = view.Cpus;
        IsDefault = view.IsDefault;
    }
}
