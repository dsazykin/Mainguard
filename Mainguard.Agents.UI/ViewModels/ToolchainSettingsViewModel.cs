using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents.Toolchains;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// Settings → Toolchains: the human-facing half of the user-managed toolchain channel. Lists every
/// curated language toolchain with its LIVE state (established by running it inside the VM, not by
/// reading a marker), installs one at its pinned, checksum-verified version, and removes it again.
///
/// <para>This is the layer that makes a toolchain a person's decision rather than a repository's: a
/// repo may name an <c>id</c> and nothing else, and whether that id is actually present on this machine
/// is decided here. Deliberately the same shape as <see cref="AgentCliSettingsViewModel"/> — serialized
/// operations (<see cref="IsBusy"/>; two installs must never share the toolchains root), failure
/// isolated to the row that failed with the typed channel refusal as its actionable cause, a
/// catalog-read failure that explains itself inline instead of throwing, and a cancel that reassures
/// rather than alarms.</para>
///
/// <para>Constructed directly (no DI); the channel is the injectable seam for tests/harness.</para>
/// </summary>
public partial class ToolchainSettingsViewModel : ViewModelBase, ISettingsPage
{
    private readonly ToolchainChannel? _channel;
    private CancellationTokenSource? _cts;

    /// <summary>Live constructor: the real channel over the VM install host.</summary>
    /// <param name="channel">The curated toolchain channel.</param>
    /// <param name="declaration">The optional "declare a toolchain in this repository" flow rendered as
    /// a second section on this page (see <see cref="Declaration"/>). Null in every call site that has
    /// no repository context — the section is then simply absent rather than present and inert.</param>
    public ToolchainSettingsViewModel(ToolchainChannel channel, ToolchainDeclarationViewModel? declaration = null)
    {
        WatchRows();
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        Declaration = declaration;
    }

    /// <summary>Design/render constructor: fixed representative rows, no service behind them. Takes the
    /// declaration section too, so a rendered/measured page can be the WHOLE shipped page rather than its
    /// top half — the half that was missing is the one the owner's clipping screenshot was of.</summary>
    public ToolchainSettingsViewModel(
        IEnumerable<ToolchainRowViewModel> rows, bool isLoading = false, string? loadError = null,
        ToolchainDeclarationViewModel? declaration = null)
    {
        WatchRows();
        foreach (var row in rows)
            Toolchains.Add(row);
        _isLoading = isLoading;
        _loadError = loadError;
        Declaration = declaration;
    }

    public ObservableCollection<ToolchainRowViewModel> Toolchains { get; } = new();

    /// <summary>
    /// The second section of this page: the four-step, one-button-per-step flow for declaring a
    /// toolchain in the open repository (<see cref="ToolchainDeclarationViewModel"/>). It lives here
    /// rather than behind its own navigation entry because the two halves answer the same question from
    /// opposite ends — "is this toolchain on my machine" and "does this repository ask for it" — and
    /// splitting them across pages is how a user ends up with one without the other.
    ///
    /// <para>Null on the design/harness constructor and on any call site with no repository context, in
    /// which case the section is not rendered at all.</para>
    /// </summary>
    public ToolchainDeclarationViewModel? Declaration { get; }

    /// <summary>Whether to render the declaration section.</summary>
    public bool HasDeclaration => Declaration is not null;

    // ---- per-row command enablement --------------------------------------------------------------
    //
    // Install/Remove are commands on THIS view model, but their CanExecute predicates read state off the
    // ROW passed as CommandParameter (row.CanInstall / row.CanRemove). [NotifyCanExecuteChangedFor] on
    // IsBusy covers only half of that: when a row changed — a probe flipping IsInstalled, an install
    // finishing — the row raised PropertyChanged (so the button turned VISIBLE, which always worked)
    // while the command never re-published CanExecuteChanged. A Button caches its last CanExecute
    // result, so it rendered visible and permanently DISABLED. Bridging row notifications to the
    // commands is the fix; do not add a row-state-reading CanExecute without adding its property here.

    /// <summary>Rows we have subscribed to. <c>Clear()</c> raises Reset with no OldItems, so the
    /// subscriptions have to be tracked rather than recovered from the event.</summary>
    private readonly List<ToolchainRowViewModel> _watched = new();

    /// <summary>Row properties every per-row command's CanExecute reads.</summary>
    private static readonly HashSet<string> RowCommandInputs = new(StringComparer.Ordinal)
    {
        nameof(ToolchainRowViewModel.CanInstall),
        nameof(ToolchainRowViewModel.CanRemove),
    };

    private void WatchRows() => Toolchains.CollectionChanged += OnToolchainsChanged;

    private void OnToolchainsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in _watched)
            row.PropertyChanged -= OnRowChanged;
        _watched.Clear();
        foreach (var row in Toolchains)
        {
            row.PropertyChanged += OnRowChanged;
            _watched.Add(row);
        }

        NotifyRowCommands();
        OnPropertyChanged(nameof(ShowEmpty)); // ShowEmpty reads Toolchains.Count, which does not notify
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A null/empty name means "everything changed" (INotifyPropertyChanged convention).
        if (string.IsNullOrEmpty(e.PropertyName) || RowCommandInputs.Contains(e.PropertyName))
            NotifyRowCommands();
    }

    private void NotifyRowCommands()
    {
        InstallCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>True while the manifest is read and every toolchain re-probed — a named "checking" line
    /// shows, never a blank list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private string? _loadError;

    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

    /// <summary>An install or removal is in flight — every row's buttons disable (operations are
    /// serialized: one toolchains root, one writer).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    /// <summary>Loaded fine, this release just curates nothing (honest empty).</summary>
    public bool ShowEmpty => !IsLoading && !HasLoadError && Toolchains.Count == 0;

    /// <summary>Initial load and the "Refresh" action: re-reads the curated manifest and re-probes each
    /// toolchain INSIDE the VM, so the list is a measurement rather than a memory.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        if (_channel is null)
            return; // design/render instance
        LoadError = null;
        IsLoading = true;
        try
        {
            var statuses = await _channel.ListAsync(CancellationToken.None).ConfigureAwait(true);

            // The channel no longer throws when the environment cannot be reached — it answers
            // "could not check" per toolchain, so the SPAWN path can tell that apart from "not
            // installed" instead of dying on a raw platform exception. This page must not lose the
            // distinction in the other direction: if NOTHING could be checked, the environment is
            // down, and a list of rows each mumbling "Could not check" is a worse answer than one
            // banner saying so. A page that reports per-item uncertainty when the whole substrate is
            // unreachable buries the only fact that matters.
            if (statuses.Count > 0 && statuses.All(s => s.CouldNotCheck))
            {
                LoadError = $"{statuses[0].Detail}. Start the Mainguard environment, then Refresh.";
                Toolchains.Clear();
                return;
            }

            Toolchains.Clear();
            foreach (var status in statuses)
                Toolchains.Add(new ToolchainRowViewModel(status));
        }
        catch (ToolchainChannelException ex)
        {
            LoadError = $"{ex.Message} Try again once that is addressed.";
        }
        catch (Exception ex)
        {
            LoadError = $"Mainguard could not read its toolchain catalog: {ex.Message} "
                + "If the Mainguard environment is not running, open Mainguard again to start it, then Refresh.";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmpty));
        }
    }

    private bool CanRefresh() => !IsBusy;

    /// <summary>Installs one toolchain at its pinned version (fetch → sha256-verify → unpack → run it).
    /// The row carries its own progress and, on failure, the typed cause.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync(ToolchainRowViewModel row)
    {
        if (_channel is null || row.IsInstalled || row.IsWorking)
            return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        row.IsFailed = false;
        row.IsWorking = true;
        row.StatusMessage = $"Downloading, verifying, and installing {row.DisplayName} {row.Version} — a "
            + "language toolchain is a few hundred megabytes, so this can take several minutes.";

        // Progress<T> posts through the captured context, so a report can land AFTER the operation has
        // returned. `live` closes that window: the outcome written below must never be overwritten by a
        // late "Unpacking…", which would leave an installed row mid-sentence or bury a failure's cause.
        var live = true;
        var progress = new Progress<string>(line =>
        {
            if (live)
                row.StatusMessage = line;
        });

        try
        {
            ToolchainStatus status;
            try
            {
                status = await _channel.InstallAsync(row.Id, progress, _cts.Token).ConfigureAwait(true);
            }
            finally
            {
                live = false;
            }

            row.Detail = status.Detail;
            row.IsInstalled = status.IsInstalled;
            row.StatusMessage = null;
        }
        catch (OperationCanceledException)
        {
            live = false;
            row.StatusMessage = "Cancelled. Nothing was left half-installed — you can install it again anytime.";
        }
        catch (ToolchainChannelException ex)
        {
            live = false;
            row.IsFailed = true;
            row.StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            live = false;
            row.IsFailed = true;
            row.StatusMessage = $"{row.DisplayName} could not be installed. {ex.Message}";
        }
        finally
        {
            row.IsWorking = false;
            IsBusy = false;
        }
    }

    private bool CanInstall(ToolchainRowViewModel? row) => !IsBusy && row is { CanInstall: true };

    /// <summary>Removes one installed toolchain. No confirmation — nothing of the user's is destroyed
    /// (a toolchain is a pinned, re-downloadable payload, and the per-agent package caches it wrote into
    /// are not touched) — but the list is ALWAYS re-read afterwards, because "removed" that only removed
    /// half is exactly the state a settings page must not be able to misreport.</summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync(ToolchainRowViewModel row)
    {
        if (_channel is null || !row.IsInstalled || row.IsWorking)
            return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        row.IsFailed = false;
        row.IsWorking = true;
        row.StatusMessage = $"Removing {row.DisplayName}…";

        string? failure = null;
        try
        {
            await _channel.RemoveAsync(row.Id, _cts.Token).ConfigureAwait(true);
            row.StatusMessage = null;
        }
        catch (OperationCanceledException)
        {
            row.StatusMessage = "Cancelled. The toolchain is still there — nothing was changed.";
        }
        catch (ToolchainChannelException ex)
        {
            failure = ex.Message;
        }
        catch (Exception ex)
        {
            failure = $"{row.DisplayName} could not be removed. {ex.Message}";
        }
        finally
        {
            row.IsWorking = false;
            IsBusy = false;
        }

        // Re-probe unconditionally: after a FAILED removal the VM's state is the least knowable, so
        // that is when the list is least allowed to be a guess.
        await RefreshAsync().ConfigureAwait(true);
        if (failure is null)
            return;

        // Refresh rebuilds the rows, so the cause is re-attached to whichever row now stands for this id.
        var refreshed = Toolchains.FirstOrDefault(t => string.Equals(t.Id, row.Id, StringComparison.Ordinal));
        if (refreshed is not null)
        {
            refreshed.IsFailed = true;
            refreshed.StatusMessage = failure;
        }
    }

    private bool CanRemove(ToolchainRowViewModel? row) => !IsBusy && row is { CanRemove: true };

    /// <summary>Aborts the in-flight install/removal; the row reports the cancellation on itself.</summary>
    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>As a Settings page: kick the initial probe pass the moment this page becomes visible.</summary>
    public void OnActivated()
    {
        if (RefreshCommand.CanExecute(null))
            RefreshCommand.Execute(null);

        // The declaration flow measures the repository the same way: on open, and after every step it
        // performs. A section that showed a remembered state would be the exact defect the flow's own
        // re-evaluation exists to prevent.
        if (Declaration is not null)
            _ = Declaration.RefreshAsync();
    }

    public void OnDeactivated()
    {
    }
}
