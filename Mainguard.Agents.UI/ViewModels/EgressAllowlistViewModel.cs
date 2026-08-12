using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.UI.Services;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// The user-visible, editable default-deny egress allowlist (P2-07). Lists the hosts an agent may
/// reach through the proxy and drives add/remove through <see cref="IEgressAllowlistGateway"/> — the
/// daemon seam (edits are change-logged daemon-side). The App holds no container/egress engine
/// (ESC-I2/G-18). A git-host entry is marked as defeating A6 (it re-opens a direct route the daemon
/// git-proxy exists to remove).
/// </summary>
public partial class EgressAllowlistViewModel : ViewModelBase
{
    private readonly IEgressAllowlistGateway _gateway;

    public ObservableCollection<EgressAllowlistRowViewModel> Entries { get; } = new();

    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string _newHostPattern = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public Action? CloseAction { get; set; }

    public EgressAllowlistViewModel(IEgressAllowlistGateway gateway) => _gateway = gateway;

    /// <summary>True iff any entry re-opens a direct git-host route (A6 defeated) — shows the warning banner.</summary>
    public bool HasGitHostWarning => Entries.Any(e => e.DefeatsA6);

    /// <summary>True while a gateway round trip is in flight; the view disables editing so a second
    /// click cannot race the first against the daemon's authoritative list.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// Loads the live allowlist. Separate from the constructor because the shipped gateway is a gRPC
    /// round trip — the load used to run inside the ctor, which is exactly why the only gateway that
    /// could satisfy this ViewModel was an in-memory list, and therefore why the editor was unreachable
    /// in the app. Callers <c>await</c> this right after construction.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default) => await ReloadAsync(ct);

    private async Task ReloadAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var items = await _gateway.ListAsync(ct);
            Entries.Clear();
            foreach (var item in items.OrderBy(i => i.HostPattern, StringComparer.OrdinalIgnoreCase))
                Entries.Add(new EgressAllowlistRowViewModel(item, this));
            OnPropertyChanged(nameof(HasGitHostWarning));
        }
        catch (Exception ex)
        {
            // A daemon that is down must say so. An empty grid would read as "nothing is allowed",
            // which is a very different — and alarming — claim from "we could not ask".
            ErrorMessage = $"Could not read the allowlist from the agent daemon: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAdd => !string.IsNullOrWhiteSpace(NewName) && !string.IsNullOrWhiteSpace(NewHostPattern);

    partial void OnNewNameChanged(string value) { ErrorMessage = null; AddCommand.NotifyCanExecuteChanged(); }
    partial void OnNewHostPatternChanged(string value) { ErrorMessage = null; AddCommand.NotifyCanExecuteChanged(); }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        try
        {
            await _gateway.AddAsync(NewName.Trim(), NewHostPattern.Trim(), "Custom");
            NewName = string.Empty;
            NewHostPattern = string.Empty;
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            // The daemon refusing a widening of a default-deny control is a legitimate answer, not a
            // bug — show its reason verbatim rather than a generic failure.
            ErrorMessage = ex.Message;
        }
    }

    internal async Task RemoveRowAsync(EgressAllowlistRowViewModel row)
    {
        try
        {
            await _gateway.RemoveAsync(row.HostPattern);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}

/// <summary>One allowlist row: the host, its category, the A6 marker, and per-row remove.</summary>
public partial class EgressAllowlistRowViewModel : ViewModelBase
{
    private readonly EgressAllowlistViewModel _parent;

    public string Name { get; }
    public string HostPattern { get; }
    public string Kind { get; }
    public bool DefeatsA6 { get; }

    public EgressAllowlistRowViewModel(EgressAllowlistItem item, EgressAllowlistViewModel parent)
    {
        _parent = parent;
        Name = item.Name;
        HostPattern = item.HostPattern;
        Kind = item.Kind;
        DefeatsA6 = item.DefeatsA6;
    }

    [RelayCommand]
    private Task RemoveAsync() => _parent.RemoveRowAsync(this);
}
