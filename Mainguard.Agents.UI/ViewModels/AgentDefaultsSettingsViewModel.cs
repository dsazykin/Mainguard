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
/// Settings → Agent Defaults: what every agent spawned from now on gets — whether a worker must have an
/// approved plan, and which model each role is launched with.
///
/// <para><b>Why plan approval is reachable here.</b> Its only control lived inside the plan gate, which
/// the control centre renders only when there IS gate content — a pending plan, an escalation,
/// backpressure, or plan mode already being OFF. So with it ON and nothing waiting, the control had no
/// surface at all: a human could turn the gate back on but never off, a one-way door reached through a
/// setting rather than a decision. This page is the surface that has no such condition.</para>
///
/// <para>The gate KEEPS its toggle — this does not replace it. Both write through the same daemon RPC,
/// so the two cannot disagree, and a control beside the decisions it governs is worth having when that
/// is what you are looking at. What was missing was a place to reach it when the gate is silent.</para>
///
/// <para>Daemon state throughout (see <see cref="IAgentDefaultsGateway"/>), so a write re-renders from
/// what the daemon PERSISTED rather than from what was typed, and an unreachable daemon is an error on
/// the page rather than a silent success.</para>
/// </summary>
public partial class AgentDefaultsSettingsViewModel : ViewModelBase
{
    private readonly IAgentDefaultsGateway _gateway;

    public AgentDefaultsSettingsViewModel(IAgentDefaultsGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _ = LoadAsync();
    }

    /// <summary>Whether a delegated worker must have an approved plan before it is given its task.
    /// One-way from the daemon: the checkbox moves, the command tells the daemon, and the reload puts
    /// the daemon's answer back — so what is ticked is what is enforced.</summary>
    [ObservableProperty] private bool _planModeEnabled = true;

    /// <summary>The daemon's own sentence for the plan-mode state, never a second wording of it.</summary>
    [ObservableProperty] private string _planModeSummary = "";

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private bool _statusIsError;

    public bool HasStatus => StatusMessage.Length > 0;

    /// <summary>One row per installed CLI.</summary>
    public ObservableCollection<AgentModelRowViewModel> Models { get; } = new();

    /// <summary>True when the daemon reported no installed CLIs at all — the honest empty state, rather
    /// than a page of controls for agents that cannot be spawned.</summary>
    [ObservableProperty] private bool _hasNoClis;

    [RelayCommand]
    private async Task LoadAsync()
    {
        await RunAsync(ct => _gateway.LoadAsync(ct), successMessage: null).ConfigureAwait(true);
    }

    /// <summary>
    /// Flips the plan-approval gate. Bound as a Command rather than a two-way checkbox for the reason
    /// the gate's own toggle was: a two-way binding would let the screen show a setting the daemon
    /// rejected or never received.
    /// </summary>
    [RelayCommand]
    private async Task TogglePlanModeAsync()
    {
        var wanted = !PlanModeEnabled;
        await RunAsync(
            ct => _gateway.SetPlanModeAsync(wanted, ct),
            successMessage: wanted
                ? "Workers must have an approved plan before they start."
                : "Workers now start straight away, with no plan approval.")
            .ConfigureAwait(true);
    }

    /// <summary>Applies one row's edited model. Called by the row, which owns the text.</summary>
    internal async Task SetModelAsync(AgentModelRoleView role, string agentKind, string model)
    {
        await RunAsync(
            ct => _gateway.SetModelAsync(role, agentKind, model, ct),
            successMessage: model.Trim().Length == 0
                ? $"{agentKind} {Word(role)}s use the CLI's own default model."
                : $"{agentKind} {Word(role)}s now use '{model.Trim()}'.")
            .ConfigureAwait(true);
    }

    private static string Word(AgentModelRoleView role) =>
        role == AgentModelRoleView.Coordinator ? "coordinator" : "worker";

    /// <summary>
    /// The one path every daemon call takes: busy flag, the daemon's answer applied to the page, and a
    /// failure reported rather than swallowed. A settings page that silently keeps its old values after
    /// a failed write is a page claiming a setting that is not in force.
    /// </summary>
    private async Task RunAsync(Func<CancellationToken, Task<AgentDefaultsView>> call, string? successMessage)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "";
        StatusIsError = false;
        try
        {
            var view = await call(CancellationToken.None).ConfigureAwait(true);
            Apply(view);

            if (view.Error.Length > 0)
            {
                // The daemon's refusal outranks any success line: it is the reason nothing changed.
                StatusMessage = view.Error;
                StatusIsError = true;
            }
            else if (successMessage is { Length: > 0 })
            {
                StatusMessage = successMessage;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't reach the daemon — {ex.Message}";
            StatusIsError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(AgentDefaultsView view)
    {
        PlanModeEnabled = view.PlanModeEnabled;
        PlanModeSummary = view.PlanModeSummary;

        // Rows are synced in place so an open dropdown and a half-typed custom model survive a reload
        // that did not change which CLIs are installed.
        for (var i = Models.Count - 1; i >= 0; i--)
        {
            if (view.Models.All(m => m.AgentKind != Models[i].AgentKind))
            {
                Models.RemoveAt(i);
            }
        }

        for (var i = 0; i < view.Models.Count; i++)
        {
            var option = view.Models[i];
            var existing = Models.FirstOrDefault(r => r.AgentKind == option.AgentKind);
            if (existing is null)
            {
                Models.Insert(Math.Min(i, Models.Count), new AgentModelRowViewModel(option, this));
            }
            else
            {
                existing.Update(option);
            }
        }

        HasNoClis = Models.Count == 0;
    }
}

/// <summary>One installed CLI's row: its two model choices, and what it is capable of.</summary>
public partial class AgentModelRowViewModel : ViewModelBase
{
    private readonly AgentDefaultsSettingsViewModel _owner;

    public string AgentKind { get; }

    public AgentModelRowViewModel(AgentModelOptionView option, AgentDefaultsSettingsViewModel owner)
    {
        AgentKind = option.AgentKind;
        _owner = owner;
        Update(option);
    }

    /// <summary>False when this CLI declares no model flag. The row then says so instead of offering a
    /// picker whose value would be accepted and then dropped at spawn.</summary>
    [ObservableProperty] private bool _canSetModel = true;

    /// <summary>The models this CLI is KNOWN to accept. Suggestions only — the row always accepts a
    /// typed value, because vendors add models faster than Mainguard ships.</summary>
    public ObservableCollection<string> KnownModels { get; } = new();

    public bool HasKnownModels => KnownModels.Count > 0;

    /// <summary>The coordinator's model, as text. Empty means the CLI's own default.</summary>
    [ObservableProperty] private string _coordinatorModel = "";

    /// <summary>The workers' model, as text. Empty means the CLI's own default.</summary>
    [ObservableProperty] private string _workerModel = "";

    /// <summary>The line shown when this CLI's model cannot be set at all.</summary>
    public string NotSettableText =>
        $"Mainguard can't set {AgentKind}'s model — this CLI doesn't declare a model flag.";

    public void Update(AgentModelOptionView option)
    {
        CanSetModel = option.CanSetModel;
        CoordinatorModel = option.CoordinatorModel;
        WorkerModel = option.WorkerModel;

        KnownModels.Clear();
        foreach (var model in option.KnownModels)
        {
            KnownModels.Add(model);
        }

        OnPropertyChanged(nameof(HasKnownModels));
    }

    [RelayCommand]
    private Task ApplyCoordinatorAsync() =>
        _owner.SetModelAsync(AgentModelRoleView.Coordinator, AgentKind, CoordinatorModel);

    [RelayCommand]
    private Task ApplyWorkerAsync() =>
        _owner.SetModelAsync(AgentModelRoleView.Worker, AgentKind, WorkerModel);

    /// <summary>Picks a suggestion into the coordinator box and applies it in one gesture — a dropdown
    /// that only filled a box the human then had to press Apply on would be two steps for one choice.</summary>
    [RelayCommand]
    private Task PickCoordinatorAsync(string? model)
    {
        CoordinatorModel = model ?? "";
        return ApplyCoordinatorAsync();
    }

    [RelayCommand]
    private Task PickWorkerAsync(string? model)
    {
        WorkerModel = model ?? "";
        return ApplyWorkerAsync();
    }
}
