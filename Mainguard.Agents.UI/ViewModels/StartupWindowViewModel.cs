using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// The startup loading screen (owner design, 2026-07-17): the <see cref="BootstrapStageViewModel"/>
/// glyph-checklist idiom (one row per <see cref="StartupStage"/>) plus a one-line changing
/// <see cref="StatusText"/>, driven by <see cref="AppStartupSequence"/>. It also hosts the tier-2 OS
/// upgrade offer INSIDE the loading surface (<see cref="PendingUpgrade"/>) so the consented,
/// fully-blocking upgrade runs here rather than in a separate post-startup window. On completion it
/// raises <see cref="Completed"/> with the typed <see cref="StartupResult"/> the shell carries into
/// its degraded banner. Implements <see cref="IProgress{StartupProgress}"/> so the sequence reports
/// straight onto the checklist (marshalled to the UI thread).
/// </summary>
public sealed partial class StartupWindowViewModel : ViewModelBase, IProgress<StartupProgress>
{
    /// <summary>The checklist rows, in stage order (index == <see cref="StartupStage"/> value).</summary>
    private static readonly (StartupStage Stage, string Label)[] StageDefs =
    {
        (StartupStage.PrepareEnvironment, "Start the Mainguard OS environment"),
        (StartupStage.ConnectDaemon, "Connect to the Mainguard OS daemon"),
        (StartupStage.ApplyUpdates, "Apply updates"),
        (StartupStage.SandboxImages, "Check sandbox images"),
    };

    private CancellationTokenSource? _cts;

    /// <summary>Live constructor: seeds one pending row per stage.</summary>
    public StartupWindowViewModel()
    {
        foreach (var def in StageDefs)
        {
            Stages.Add(new BootstrapStageViewModel(def.Label));
        }
    }

    /// <summary>Design/render constructor: fixed checklist + status (+ optional hosted upgrade offer),
    /// so the headless harness can capture every state without running the real sequence.</summary>
    public StartupWindowViewModel(
        IEnumerable<BootstrapStageViewModel> stages,
        string statusText,
        VmUpgradeOfferViewModel? pendingUpgrade = null,
        bool isDegraded = false)
    {
        foreach (var stage in stages)
        {
            Stages.Add(stage);
        }

        _statusText = statusText;
        _pendingUpgrade = pendingUpgrade;
        _isDegraded = isDegraded;
    }

    /// <summary>The glyph checklist (same row type as the OOBE import stages / the VM upgrade steps).</summary>
    public ObservableCollection<BootstrapStageViewModel> Stages { get; } = new();

    /// <summary>The one-line changing status text under the checklist.</summary>
    [ObservableProperty]
    private string _statusText = StartupStatus.WakingEnvironment;

    /// <summary>The hosted tier-2 offer/progress VM while the upgrade phase is active; null otherwise.
    /// When set, the loading surface shows the consent copy + the upgrade step checklist inline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpgrading))]
    private VmUpgradeOfferViewModel? _pendingUpgrade;

    /// <summary>True while the tier-2 upgrade offer/progress is hosted in the loading surface.</summary>
    public bool IsUpgrading => PendingUpgrade is not null;

    /// <summary>The hosted "another daemon holds the port" offer while that phase is active; null
    /// otherwise. Same inline-hosting shape as <see cref="PendingUpgrade"/>, because it is the same kind
    /// of moment: startup is blocked on something only the user may authorise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResolvingPortConflict))]
    private DaemonPortConflictViewModel? _pendingPortConflict;

    /// <summary>True while the port-conflict offer is hosted in the loading surface.</summary>
    public bool IsResolvingPortConflict => PendingPortConflict is not null;

    /// <summary>True once an essential step failed and the app is entering degraded (banner) mode —
    /// tints the status line so the honest failure reads as a failure, never colour alone.</summary>
    [ObservableProperty]
    private bool _isDegraded;

    /// <summary>Wired by the App: runs the Core sequence, receiving this VM as the progress sink.</summary>
    public Func<IProgress<StartupProgress>, CancellationToken, Task<StartupResult>>? SequenceRunner { get; set; }

    /// <summary>Raised (on the UI thread) when the sequence finishes; carries the entry result the
    /// shell binds to its degraded banner. The View swaps to MainWindow on this.</summary>
    public event EventHandler<StartupResult>? Completed;

    /// <summary>Drives the sequence to completion, then raises <see cref="Completed"/>. Fail-safe: any
    /// unexpected fault still completes (degraded) so the app never wedges on the loading screen.</summary>
    public async Task StartAsync()
    {
        if (SequenceRunner is null)
        {
            return; // design/render instance — no sequence behind it
        }

        _cts = new CancellationTokenSource();
        StartupResult result;
        try
        {
            result = await SequenceRunner(this, _cts.Token).ConfigureAwait(true);
        }
        catch (Exception)
        {
            result = new StartupResult(false, StartupStatus.DaemonUnreachableBanner);
        }

        Completed?.Invoke(this, result);
    }

    /// <summary>Hosts the tier-2 offer/progress VM in the loading surface (called on the UI thread by
    /// the production startup environment during the consent/upgrade phase).</summary>
    public void BeginVmUpgrade(VmUpgradeOfferViewModel upgrade)
    {
        PendingUpgrade = upgrade;
        StatusText = StartupStatus.UpgradingOs;
    }

    /// <summary>Tears down the hosted tier-2 surface once the upgrade phase resolves.</summary>
    public void EndVmUpgrade() => PendingUpgrade = null;

    /// <summary>Hosts the "another daemon holds the port" offer in the loading surface (UI thread).</summary>
    public void BeginPortConflict(DaemonPortConflictViewModel conflict)
    {
        PendingPortConflict = conflict;
        StatusText = StartupStatus.ResolvingPortConflict;
    }

    /// <summary>Tears that surface down once the user has answered.</summary>
    public void EndPortConflict() => PendingPortConflict = null;

    /// <summary>Sequence progress sink: marshals each tick onto the checklist row + status line.</summary>
    public void Report(StartupProgress value) => Dispatcher.UIThread.Post(() => Apply(value));

    private void Apply(StartupProgress value)
    {
        var index = (int)value.Stage;
        if (index >= 0 && index < Stages.Count)
        {
            Stages[index].State = value.State;
        }

        if (!string.IsNullOrEmpty(value.Status))
        {
            StatusText = value.Status;
        }

        if (value.State == BootstrapStageState.Failed)
        {
            IsDegraded = true;
        }
    }
}
