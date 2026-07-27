using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// One agent's workspace document (ControlCenterDesign.md §4): scripted terminal tail, the
/// read-only plan tree (P2-39.4), the ambient health strip (P2-44), the composer with its
/// visible message queue (P2-39.1), and the review section — the flagged-changes gate +
/// the CanMerge-gated Merge button (P2-11 §6.3/§6.4, item-by-item acks, never a global one).
/// </summary>
public partial class AgentDocumentViewModel : ViewModelBase
{
    private readonly IAgentService _agents;
    private readonly IMergeQueueService _queue;
    private readonly ITelemetryService _telemetry;

    public string AgentId { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _stateWord = "";
    [ObservableProperty] private string _terminalText = "";
    [ObservableProperty] private string _healthStripText = "";
    [ObservableProperty] private bool _healthHasWarnings;
    [ObservableProperty] private string _composerText = "";
    [ObservableProperty] private string _composerHint = "";
    /// <summary>False when steering is coordinator-only (File menu → Agent prompting).</summary>
    [ObservableProperty] private bool _canPromptDirectly = true;

    public ObservableCollection<PlanStepViewModel> PlanTree { get; } = new();
    public ObservableCollection<QueuedPromptViewModel> QueuedPrompts { get; } = new();
    public ObservableCollection<FlaggedItemViewModel> FlaggedItems { get; } = new();

    [ObservableProperty] private bool _hasFlaggedItems;
    [ObservableProperty] private string _reviewFactsText = "";
    [ObservableProperty] private bool _canMerge;
    [ObservableProperty] private string _mergeGateReason = "";

    public AgentDocumentViewModel(string agentId, IAgentService agents, IMergeQueueService queue, ITelemetryService telemetry)
    {
        AgentId = agentId;
        _agents = agents;
        _queue = queue;
        _telemetry = telemetry;
        Refresh();
    }

    public void Refresh()
    {
        var info = _agents.ListAgents().FirstOrDefault(a => a.AgentId == AgentId);
        if (info is null) return;
        Title = $"{info.Name} · {info.Branch}";
        StateWord = info.State.ToString();
        ComposerHint = !CanPromptDirectly
            ? "Managed by the Coordinator — steer from the Coordinator chat."
            : info.State == AgentLifecycleState.Working
                ? $"{info.Name} is streaming — messages queue until it's idle."
                : "Send a follow-up prompt";

        TerminalText = string.Join("\n", _agents.GetTerminalTail(AgentId));

        var plan = _agents.GetPlanTree(AgentId);
        PlanTree.Clear();
        foreach (var (step, done) in plan) PlanTree.Add(new PlanStepViewModel(step, done));

        var prompts = _agents.GetQueuedPrompts(AgentId);
        QueuedPrompts.Clear();
        for (int i = 0; i < prompts.Count; i++)
            QueuedPrompts.Add(new QueuedPromptViewModel(prompts[i], i, CancelQueuedAsync));

        var events = _telemetry.GetSandboxEvents(AgentId);
        var blocked = events.Count(e => e.Kind == "egress_denied");
        HealthHasWarnings = blocked > 0;
        HealthStripText = blocked > 0
            ? $"egress {blocked} blocked · procs ok"
            : "egress 0 · procs ok";

        RefreshReview();
    }

    private void RefreshReview()
    {
        var entry = _queue.GetQueue().FirstOrDefault(q => q.AgentId == AgentId);
        if (entry is null) { FlaggedItems.Clear(); HasFlaggedItems = false; CanMerge = false; MergeGateReason = "not in the merge queue"; return; }

        // Sync flagged items in place so ack checkmarks don't flicker.
        for (int i = FlaggedItems.Count - 1; i >= 0; i--)
            if (entry.FlaggedItems.All(f => f.Id != FlaggedItems[i].Id))
                FlaggedItems.RemoveAt(i);
        foreach (var item in entry.FlaggedItems)
        {
            var existing = FlaggedItems.FirstOrDefault(f => f.Id == item.Id);
            if (existing is null) FlaggedItems.Add(new FlaggedItemViewModel(item, AcknowledgeAsync));
            else existing.Update(item);
        }
        HasFlaggedItems = FlaggedItems.Count > 0;

        ReviewFactsText = entry.Verification is { } v
            ? $"verified @ {v.MainSha} · {v.TestsPassed}/{v.TestsTotal} tests green"
            : "no verification record yet";

        CanMerge = _queue.CanMerge(AgentId, out var reason);
        MergeGateReason = CanMerge ? "ready to merge" : reason;
    }

    private async Task AcknowledgeAsync(string itemId)
    {
        await _queue.AcknowledgeFlaggedChangeAsync(AgentId, itemId);
        RefreshReview();
    }

    private async Task CancelQueuedAsync(int index)
    {
        await _agents.CancelQueuedPromptAsync(AgentId, index);
        Refresh();
    }

    public void SetDirectPrompting(bool allow)
    {
        CanPromptDirectly = allow;
        Refresh();
    }

    [RelayCommand]
    private async Task SendPromptAsync()
    {
        if (!CanPromptDirectly) return; // the gate re-checks (mode may flip mid-type)
        var text = ComposerText.Trim();
        if (text.Length == 0) return;
        ComposerText = "";
        await _agents.SendPromptAsync(AgentId, text);
        Refresh();
    }

    [RelayCommand]
    private async Task MergeAsync()
    {
        if (!_queue.CanMerge(AgentId, out _)) return; // the gate re-reads state (S-3)

        // Through the runner so a refusal is a sentence the human sees, not a dropped exception: the merge
        // legitimately refuses (stale, lease held, not a fast-forward, dirty tree) and a button that goes
        // quiet on refusal reads as "merged".
        await Services.MergeActionRunner.RunAsync(_queue, AgentId);
        Refresh();
    }
}

public sealed class PlanStepViewModel
{
    public string Step { get; }
    public bool Done { get; }
    public PlanStepViewModel(string step, bool done) { Step = step; Done = done; }
}

public partial class QueuedPromptViewModel : ViewModelBase
{
    private readonly Func<int, Task> _cancel;
    public string Text { get; }
    public int Index { get; }
    public QueuedPromptViewModel(string text, int index, Func<int, Task> cancel) { Text = text; Index = index; _cancel = cancel; }
    [RelayCommand] private Task CancelAsync() => _cancel(Index);
}

/// <summary>One must-acknowledge flagged item (P2-11): its own checkbox, never a global one.</summary>
public partial class FlaggedItemViewModel : ViewModelBase
{
    private readonly Func<string, Task> _acknowledge;

    public string Id { get; }
    public string Path { get; }
    public string Fact { get; }

    [ObservableProperty] private bool _isAcknowledged;

    public FlaggedItemViewModel(FlaggedItem item, Func<string, Task> acknowledge)
    {
        _acknowledge = acknowledge;
        Id = item.Id;
        Path = item.Path;
        Fact = item.Fact;
        _isAcknowledged = item.Acknowledged;
    }

    public void Update(FlaggedItem item) => IsAcknowledged = item.Acknowledged;

    [RelayCommand]
    private Task AcknowledgeAsync() => IsAcknowledged ? Task.CompletedTask : _acknowledge(Id);
}
