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
/// The Coordinator conversation + the <b>worker-authored</b> plan gate (contract §2 /
/// ControlCenterDesign.md §5). Cards are decisions, lines are history; Approve is the panel's one accent.
///
/// <para>Phase 2 changes what this panel is showing you. The plan on the card was written by the
/// <i>worker</i> after it inspected the repository, and that worker is <b>blocked</b> until you decide —
/// so the card names the worker, and Reject asks for feedback, because the feedback is delivered back to
/// the worker to revise against rather than thrown away. The card also shows which revision this is
/// against the daemon's budget, since the last permitted rejection stops the worker instead of producing
/// another plan, and a human deserves to know that before clicking it.</para>
///
/// <para>The panel also renders the <b>backpressure</b> state. A worker waiting on your approval still
/// holds its jail, so it counts against the worker cap; when the cap fills with blocked workers the
/// coordinator stops spawning. That is intended, and it is indistinguishable from a hang unless this
/// surface says so out loud — which is the one thing the contract calls a requirement rather than a
/// nicety.</para>
/// </summary>
public partial class CoordinatorPanelViewModel : ViewModelBase
{
    private readonly ICoordinatorService _coordinator;

    public ObservableCollection<ChatLineViewModel> Transcript { get; } = new();

    /// <summary>Workers that stopped after spending their revision budget — these need a human, not a click.</summary>
    public ObservableCollection<EscalatedPlanViewModel> EscalatedPlans { get; } = new();

    [ObservableProperty] private PlanCardViewModel? _pendingPlan;
    [ObservableProperty] private string _composerText = "";
    [ObservableProperty] private string _pressureText = "";

    /// <summary>The daemon's legible-stall line, e.g. "6 workers are waiting on your approval…".</summary>
    [ObservableProperty] private string _backpressureText = "";

    /// <summary>True when blocked plans are the reason the coordinator has stopped spawning.</summary>
    [ObservableProperty] private bool _isCapSaturatedByBlockedWorkers;

    [ObservableProperty] private bool _isEmpty;

    public CoordinatorPanelViewModel(ICoordinatorService coordinator)
    {
        _coordinator = coordinator;
        Refresh();
    }

    public void Refresh()
    {
        var lines = _coordinator.GetTranscript();
        // Transcript is append-only in the mock; sync the tail.
        for (int i = Transcript.Count; i < lines.Count; i++)
            Transcript.Add(new ChatLineViewModel(lines[i]));
        IsEmpty = Transcript.Count == 0;

        var cards = _coordinator.GetWorkerPlans();
        var pending = cards.Where(c => c.IsPending).ToList();
        var first = pending.FirstOrDefault();
        if (first is null)
            PendingPlan = null;
        else if (PendingPlan?.PlanId != first.PlanId || PendingPlan?.Revision != first.Revision)
            PendingPlan = new PlanCardViewModel(first, DecideAsync);

        EscalatedPlans.Clear();
        foreach (var escalated in cards.Where(c => c.IsEscalated))
            EscalatedPlans.Add(new EscalatedPlanViewModel(escalated));

        PressureText = pending.Count > 2
            ? $"{pending.Count} plans pending — the oldest has waited {(int)(DateTimeOffset.Now - pending.Min(p => p.PresentedAt)).TotalMinutes} min."
            : "";

        var backpressure = _coordinator.GetBackpressure();
        BackpressureText = backpressure.Signal;
        IsCapSaturatedByBlockedWorkers = backpressure.CapSaturatedByBlockedWorkers;
    }

    private async Task DecideAsync(string planId, bool approve, string? feedback)
    {
        await _coordinator.SubmitPlanDecisionAsync(planId, approve, feedback);
        Refresh();
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = ComposerText.Trim();
        if (text.Length == 0) return;
        ComposerText = "";
        await _coordinator.SendAsync(text);
        Refresh();
    }
}

/// <summary>One transcript line; Kind booleans let the View pick the rendering (no templates-by-type).</summary>
public sealed class ChatLineViewModel : ViewModelBase
{
    public string Text { get; }
    public string TimeText { get; }
    public bool IsHuman { get; }
    public bool IsCoordinator { get; }
    public bool IsToolCall { get; }
    public bool IsSystemLine { get; }
    public bool IsPlanCard { get; }
    public string SenderLabel { get; }

    public ChatLineViewModel(ChatLine line)
    {
        Text = line.Text;
        TimeText = line.At.ToLocalTime().ToString("HH:mm");
        IsHuman = line.Kind == ChatLineKind.Human;
        IsCoordinator = line.Kind == ChatLineKind.Coordinator;
        IsToolCall = line.Kind == ChatLineKind.ToolCall;
        IsSystemLine = line.Kind == ChatLineKind.SystemLine;
        IsPlanCard = line.Kind == ChatLineKind.PlanCard;
        SenderLabel = IsHuman ? "You" : IsCoordinator ? "Coordinator" : "";
    }
}

/// <summary>
/// The worker-authored plan approval card. Scope is the load-bearing field (§5.2); the worker's identity,
/// the revision counter and the rejection-feedback box are what phase 2 adds.
/// </summary>
public partial class PlanCardViewModel : ViewModelBase
{
    private readonly Func<string, bool, string?, Task> _decide;

    public string PlanId { get; }
    public string WorkerAgentId { get; }
    public string Title { get; }
    public string ScopeText { get; }
    public string Approach { get; }
    public string TestStrategy { get; }
    public string FactsText { get; }

    /// <summary>Which revision this is, e.g. "revision 2 of 3" — empty on the original presentation.</summary>
    public string RevisionText { get; }

    /// <summary>The feedback this revision was written against (empty on the original presentation).</summary>
    public string RevisedAgainstText { get; }

    public int Revision { get; }
    public bool IsRevision { get; }
    public bool HasFeedbackHistory { get; }

    /// <summary>
    /// True when rejecting again would stop the worker rather than produce another plan. The card says so
    /// before the click, because "reject" and "give up on this worker" are different decisions and the
    /// human is the only one who can tell which they meant.
    /// </summary>
    public bool NextRejectionEscalates { get; }

    public string RejectButtonText { get; }

    [ObservableProperty] private bool _isDeciding;

    /// <summary>What the human types back to the worker on a rejection.</summary>
    [ObservableProperty] private string _feedbackText = "";

    public PlanCardViewModel(WorkerPlanCard plan, Func<string, bool, string?, Task> decide)
    {
        _decide = decide;
        PlanId = plan.PlanId;
        WorkerAgentId = plan.WorkerAgentId;
        Title = plan.Title;
        ScopeText = string.Join("\n", plan.Scope) + $"\n({plan.Scope.Count} files)";
        Approach = plan.Approach;
        TestStrategy = plan.TestStrategy;
        Revision = plan.Revision;
        IsRevision = plan.Revision > 0;
        RevisionText = IsRevision ? $"revision {plan.Revision} of {plan.MaxRevisions}" : "";
        RevisedAgainstText = plan.RejectionFeedback.Length > 0 ? $"revised against: {plan.RejectionFeedback}" : "";
        HasFeedbackHistory = RevisedAgainstText.Length > 0;
        NextRejectionEscalates = plan.RevisionsRemaining <= 0;
        RejectButtonText = NextRejectionEscalates ? "Reject — worker will stop" : "Reject with feedback";
        var age = (int)Math.Max(0, (DateTimeOffset.Now - plan.PresentedAt).TotalMinutes);
        var worker = plan.WorkerAgentId.Length > 0 ? plan.WorkerAgentId : "worker";
        FactsText = $"Written by {worker} · budget ${plan.BudgetUsd:0.00} · presented {age} min ago · the worker is blocked until you decide";
    }

    [RelayCommand] private Task ApproveAsync() { IsDeciding = true; return _decide(PlanId, true, null); }

    [RelayCommand] private Task RejectAsync() { IsDeciding = true; return _decide(PlanId, false, FeedbackText); }
}

/// <summary>
/// A worker that spent its revision budget and stopped. Rendered as a distinct, non-actionable card: there
/// is no button here on purpose — the loop is over and the next move is the human's.
/// </summary>
public sealed class EscalatedPlanViewModel : ViewModelBase
{
    public string PlanId { get; }
    public string WorkerAgentId { get; }
    public string Title { get; }
    public string HeadlineText { get; }
    public string LastFeedbackText { get; }
    public bool HasLastFeedback { get; }

    public EscalatedPlanViewModel(WorkerPlanCard plan)
    {
        PlanId = plan.PlanId;
        WorkerAgentId = plan.WorkerAgentId;
        Title = plan.Title;
        var worker = plan.WorkerAgentId.Length > 0 ? plan.WorkerAgentId : "This worker";
        HeadlineText =
            $"{worker} stopped after {plan.MaxRevisions} rejected plans and escalated to you. " +
            "It will not try again — steer it or end it.";
        LastFeedbackText = plan.RejectionFeedback.Length > 0 ? $"your last feedback: {plan.RejectionFeedback}" : "";
        HasLastFeedback = LastFeedbackText.Length > 0;
    }
}
