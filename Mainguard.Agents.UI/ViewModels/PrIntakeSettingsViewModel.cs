using System;
using System.Collections.Generic;
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
/// Settings → PR Intake (P2-12): the thin UI over the <b>daemon's</b> external-PR-intake configuration —
/// whether intake runs, the poll cadence, the shared bot-author allow-list, and the subscribed sources.
///
/// <para><b>Everything here is daemon state, reached over gRPC.</b> This page used to take the daemon's
/// own <c>IPrIntakeStore</c> and default to an in-process implementation, which meant a page that saved
/// successfully into an object the daemon could never see. The daemon is what polls the host, what
/// fetches PR heads, and what asks the gated spawn chain for a jail per intake'd pull request — so it
/// owns the configuration, and this page is a client of <see cref="IPrIntakeGateway"/>. Two consequences
/// are deliberate: the cadence/bot-list fields are re-populated from the daemon's PERSISTED values after
/// a save (it clamps the interval and substitutes its default bot list for an empty one), and a daemon
/// that cannot be reached produces an error on the page rather than a silent success.</para>
///
/// <para>Constructed directly (no DI), like every other Settings page. The gateway is required — there is
/// no self-defaulting fallback, because a fallback here is precisely the bug.</para>
/// </summary>
public partial class PrIntakeSettingsViewModel : ViewModelBase
{
    private readonly IPrIntakeGateway _gateway;

    public ObservableCollection<PrIntakeSourceRowViewModel> Sources { get; } = new();

    /// <summary>Whether the daemon's poll loop materializes anything. Off parks intake without
    /// unsubscribing every repository.</summary>
    [ObservableProperty]
    private bool _intakeEnabled = true;

    [ObservableProperty]
    private string _newHost = "github.com";

    [ObservableProperty]
    private string _newOwner = string.Empty;

    [ObservableProperty]
    private string _newRepo = string.Empty;

    /// <summary>Optional per-source author filter; blank falls back to the shared bot list.</summary>
    [ObservableProperty]
    private string _newAuthorFilter = string.Empty;

    /// <summary>The shared bot-author allow-list, edited as a comma-separated list.</summary>
    [ObservableProperty]
    private string _botAuthors = string.Empty;

    /// <summary>The poll cadence in seconds. The daemon clamps it to [10, 3600] and this field is
    /// rewritten from what it persisted, so the number on screen is the number being polled at.</summary>
    [ObservableProperty]
    private int _pollIntervalSeconds = 60;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>True while an RPC is in flight — the page disables its buttons rather than letting a
    /// second save race the first.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Set when the daemon could not be reached or refused. Rendered as the daemon's own words:
    /// the one thing this page must never do is look like it saved when it did not.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>True once the first load has come back, so the page can distinguish "no subscriptions"
    /// from "not loaded yet".</summary>
    [ObservableProperty]
    private bool _isLoaded;

    public PrIntakeSettingsViewModel(IPrIntakeGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        // Fire-and-forget the initial read: a Settings page is constructed on the UI thread when its row
        // is first activated, and blocking a constructor on a gRPC round trip would freeze the window.
        _ = LoadAsync();
    }

    /// <summary>Whether to render the "nothing subscribed" line. Gated on <see cref="IsLoaded"/> so an
    /// empty list is never shown before the daemon has answered — "no repositories are subscribed" and
    /// "we have not asked yet" are different claims and only one of them is honest at startup.</summary>
    public bool ShowNoSources => IsLoaded && Sources.Count == 0;

    /// <summary>The parsed bot-author allow-list (empty entries dropped).</summary>
    public IReadOnlyList<string> ParsedBotAuthors =>
        BotAuthors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private bool CanAdd =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(NewHost)
        && !string.IsNullOrWhiteSpace(NewOwner)
        && !string.IsNullOrWhiteSpace(NewRepo);

    private bool CanSave => !IsBusy;

    partial void OnNewHostChanged(string value) => AddSourceCommand.NotifyCanExecuteChanged();
    partial void OnNewOwnerChanged(string value) => AddSourceCommand.NotifyCanExecuteChanged();
    partial void OnNewRepoChanged(string value) => AddSourceCommand.NotifyCanExecuteChanged();

    partial void OnIsLoadedChanged(bool value) => OnPropertyChanged(nameof(ShowNoSources));

    partial void OnIsBusyChanged(bool value)
    {
        AddSourceCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        LoadCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Reads the daemon's configuration into the page (also the Reload button).</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
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
            // Not swallowed and not turned into a default: a page that quietly showed the compiled-in
            // defaults after a failed read would be telling the user those are the daemon's settings.
            ErrorMessage = $"Could not read the daemon's intake settings: {ex.Message}";
            StatusMessage = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Writes the cadence, the on/off switch and the bot list to the daemon, then re-renders
    /// from what the daemon persisted.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            var persisted = await _gateway
                .SaveAsync(IntakeEnabled, PollIntervalSeconds, ParsedBotAuthors, CancellationToken.None)
                .ConfigureAwait(true);

            Apply(persisted);
            ErrorMessage = null;
            StatusMessage = persisted.Enabled
                ? $"Saved. The daemon polls every {persisted.PollIntervalSeconds}s."
                : "Saved. Intake is off — the daemon polls nothing until it is switched back on.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"The daemon did not save these settings: {ex.Message}";
            StatusMessage = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    public async Task AddSourceAsync()
    {
        var filter = string.IsNullOrWhiteSpace(NewAuthorFilter) ? null : NewAuthorFilter.Trim();
        var key = $"{NewHost.Trim()}/{NewOwner.Trim()}/{NewRepo.Trim()}";

        IsBusy = true;
        try
        {
            var (added, configuration) = await _gateway
                .SubscribeAsync(NewHost.Trim(), NewOwner.Trim(), NewRepo.Trim(), filter, CancellationToken.None)
                .ConfigureAwait(true);

            Apply(configuration);
            ErrorMessage = null;
            StatusMessage = added ? $"Subscribed to {key}." : $"{key} is already subscribed.";

            NewOwner = string.Empty;
            NewRepo = string.Empty;
            NewAuthorFilter = string.Empty;
        }
        catch (Exception ex)
        {
            // The inputs are deliberately left populated so the user can correct and retry.
            ErrorMessage = $"The daemon did not subscribe {key}: {ex.Message}";
            StatusMessage = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Renders one daemon-supplied configuration into the page's fields.</summary>
    private void Apply(PrIntakeConfiguration configuration)
    {
        IntakeEnabled = configuration.Enabled;
        PollIntervalSeconds = configuration.PollIntervalSeconds;
        BotAuthors = string.Join(", ", configuration.BotAuthors);

        Sources.Clear();
        foreach (var source in configuration.Sources)
        {
            Sources.Add(new PrIntakeSourceRowViewModel(source));
        }

        OnPropertyChanged(nameof(ShowNoSources));
    }
}

/// <summary>One subscribed source row: its <c>host/owner/repo</c> and the author filter in effect.</summary>
public sealed class PrIntakeSourceRowViewModel
{
    public PrIntakeSourceRowViewModel(PrIntakeSourceItem source)
    {
        Key = source.Key;
        AuthorFilter = string.IsNullOrWhiteSpace(source.AuthorFilter)
            ? "default bot list"
            : source.AuthorFilter;
    }

    public string Key { get; }
    public string AuthorFilter { get; }
}
