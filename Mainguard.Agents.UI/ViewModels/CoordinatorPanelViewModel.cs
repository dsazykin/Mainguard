using System;
using System.Collections.Generic;
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

    /// <summary>
    /// One card per <b>blocked worker</b>, not one card for the queue.
    ///
    /// <para>This is a collection rather than a single slot because the state the gate exists to explain is
    /// specifically the plural one: blocked workers fill the worker cap and the coordinator stops spawning.
    /// Showing only the head of that queue renders the cap-saturated case as a single pending decision and
    /// leaves the operator no way to see — let alone clear — the other five.</para>
    /// </summary>
    public ObservableCollection<PlanCardViewModel> PendingPlans { get; } = new();

    /// <summary>The head of <see cref="PendingPlans"/>; kept because the single-decision path reads better.</summary>
    [ObservableProperty] private PlanCardViewModel? _pendingPlan;

    [ObservableProperty] private string _composerText = "";
    [ObservableProperty] private string _pressureText = "";

    /// <summary>The daemon's legible-stall line, e.g. "6 workers are waiting on your approval…".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGateContent))]
    private string _backpressureText = "";

    /// <summary>True when blocked plans are the reason the coordinator has stopped spawning.</summary>
    [ObservableProperty] private bool _isCapSaturatedByBlockedWorkers;

    [ObservableProperty] private bool _isEmpty;

    /// <summary>
    /// True when the gate has something the human must read: a decision to make, a worker that escalated,
    /// or the daemon's backpressure sentence. Hosts bind their whole region to this, so an idle
    /// orchestration costs no vertical space at all — the gate appears because something is waiting, which
    /// is the only reason it should ever be on screen.
    ///
    /// <para><b>Computed, not assigned.</b> It used to be a settable flag written at the end of
    /// <see cref="Refresh"/>, which made "is the gate showing?" and "what is in the gate?" two facts that
    /// could disagree whenever one of them was updated and the other was not — the same species of drift
    /// as the banner/card disagreement this surface was reported for. Derived from the collections and the
    /// sentence themselves, the region cannot be visible while empty, or collapsed while something waits.</para>
    /// </summary>
    public bool HasGateContent =>
        PendingPlans.Count > 0 || EscalatedPlans.Count > 0 || BackpressureText.Length > 0
        || !PlanModeEnabled;

    /// <summary>The daemon's plan-mode toggle. True while every worker must have an approved plan.</summary>
    [ObservableProperty] private bool _planModeEnabled = true;

    /// <summary>The daemon's own sentence for that state — rendered, never re-composed here.</summary>
    [ObservableProperty] private string _planModeSummary = "";

    /// <summary>
    /// Why the last toggle did not reach the daemon (empty when nothing is wrong). Said on the gate,
    /// beside the checkbox that snapped back, rather than escaping to the dispatcher's crash guard as a
    /// generic notice: the human who clicked is looking here, and the fact they need is that the gate
    /// is still in the state the box now shows.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlanModeError))]
    private string _planModeErrorText = "";

    public bool HasPlanModeError => PlanModeErrorText.Length > 0;

    /// <summary>The value a failed toggle asked for; cleared once the daemon is seen holding it.</summary>
    private bool? _planModeRequested;

    /// <summary>
    /// Re-raise <see cref="HasGateContent"/> when the toggle moves.
    ///
    /// <para>This is the whole reason plan mode is part of that flag: with approvals off nothing is ever
    /// pending, so a gate that only appeared for pending cards would go dark permanently — and a dark
    /// gate is exactly what an IDLE orchestration looks like. The one state a human must not have to
    /// guess at is the one where an approval step they think they have is switched off, so an off gate
    /// stays on screen saying so.</para>
    /// </summary>
    partial void OnPlanModeEnabledChanged(bool value) => OnPropertyChanged(nameof(HasGateContent));

    /// <param name="endWorker">
    /// Ends an agent by id — the release an escalated card offers. Optional because the design harness and
    /// the unit fakes have no agent to end; when it is null the card simply does not offer the action,
    /// rather than offering a button that does nothing.
    /// </param>
    public CoordinatorPanelViewModel(ICoordinatorService coordinator, Func<string, Task>? endWorker = null)
    {
        _coordinator = coordinator;
        _endWorker = endWorker;

        // The one flag both halves of the gate are read through is derived, so it has to be re-raised
        // whenever either collection moves — including the in-place reconciliation Refresh does.
        PendingPlans.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasGateContent));
        EscalatedPlans.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasGateContent));

        Refresh();
    }

    private readonly Func<string, Task>? _endWorker;

    public void Refresh()
    {
        var lines = _coordinator.GetTranscript();
        // Transcript is append-only in the mock; sync the tail.
        for (int i = Transcript.Count; i < lines.Count; i++)
            Transcript.Add(new ChatLineViewModel(lines[i]));
        IsEmpty = Transcript.Count == 0;

        var cards = _coordinator.GetWorkerPlans();
        var pending = cards.Where(c => c.IsPending).OrderBy(c => c.PresentedAt).ToList();

        // Reconcile in place rather than rebuild. A card whose (id, revision) is unchanged is the SAME
        // decision, and replacing its ViewModel would throw away the feedback the human is halfway through
        // typing and the error text from a decision that just failed — on a surface whose whole job is to
        // let them retry. Cards are only created for plans that are new or newly revised.
        var kept = new List<PlanCardViewModel>(pending.Count);
        foreach (var plan in pending)
        {
            var existing = PendingPlans.FirstOrDefault(
                c => c.PlanId == plan.PlanId && c.Revision == plan.Revision);
            kept.Add(existing ?? new PlanCardViewModel(plan, DecideAsync));
        }

        for (int i = PendingPlans.Count - 1; i >= 0; i--)
        {
            if (!kept.Contains(PendingPlans[i])) PendingPlans.RemoveAt(i);
        }

        for (int i = 0; i < kept.Count; i++)
        {
            if (i >= PendingPlans.Count) PendingPlans.Add(kept[i]);
            else if (!ReferenceEquals(PendingPlans[i], kept[i])) PendingPlans[i] = kept[i];
        }

        PendingPlan = PendingPlans.FirstOrDefault();

        // Escalated cards are rebuilt each pass. They carry no half-typed human input to preserve (unlike
        // a pending card's feedback box), and an in-flight "End" is guarded by the card's own latch.
        EscalatedPlans.Clear();
        foreach (var escalated in cards.Where(c => c.IsEscalated))
            EscalatedPlans.Add(new EscalatedPlanViewModel(escalated, _endWorker));

        PressureText = pending.Count > 2
            ? $"{pending.Count} plans pending — the oldest has waited {(int)(DateTimeOffset.Now - pending.Min(p => p.PresentedAt)).TotalMinutes} min."
            : "";

        var backpressure = _coordinator.GetBackpressure();
        BackpressureText = backpressure.Signal;
        IsCapSaturatedByBlockedWorkers = backpressure.CapSaturatedByBlockedWorkers;

        // Read from the daemon on every pass, so the checkbox tracks the gate rather than the last click.
        // A toggle that rendered the requested value would keep showing "on" after a Set that never
        // arrived — the one disagreement where a human believes they have an approval step and do not.
        var planMode = _coordinator.GetPlanMode();
        // A failed toggle's error stands until the daemon is seen holding the value that was asked for.
        if (_planModeRequested is { } requested && planMode.Enabled == requested)
        {
            _planModeRequested = null;
            PlanModeErrorText = "";
        }

        PlanModeEnabled = planMode.Enabled;
        PlanModeSummary = planMode.Summary;
    }

    /// <summary>
    /// Flips the daemon's plan-mode toggle, then re-reads it.
    ///
    /// <para>The new value is computed from the property the checkbox has ALREADY moved (the control sets
    /// its own <c>IsChecked</c> before the command runs), and the refresh afterwards puts the daemon's
    /// answer back — so a failed call ends with the checkbox showing what the daemon actually holds.</para>
    /// </summary>
    [RelayCommand]
    private async Task TogglePlanModeAsync()
    {
        var requested = PlanModeEnabled;
        try
        {
            await _coordinator.SetPlanModeAsync(requested);
            _planModeRequested = null;
            PlanModeErrorText = "";
        }
        catch (Exception ex)
        {
            // Caught here rather than left to escape the RelayCommand onto the dispatcher: the box snaps
            // back in the refresh below, and this is the sentence that says why it did.
            _planModeRequested = requested;
            PlanModeErrorText =
                $"Plan mode was not changed — {ex.Message}. The gate is still as the checkbox now shows; try again.";
        }
        finally
        {
            Refresh();
        }
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
    /// True when this card is a <b>re-scope</b>: the worker is asking to change what an approval it
    /// already holds authorises.
    ///
    /// <para>The card has to say so, because the decision is not the one it looks like. Approving a first
    /// presentation authorises work that has not started; approving this one CHANGES what a running worker
    /// is cleared to touch — and rejecting it takes nothing away, which is the opposite of what Reject
    /// means on every other card here. A human handed an identical-looking card would be answering a
    /// different question from the one being asked.</para>
    /// </summary>
    public bool IsRescope { get; }

    /// <summary>What kind of decision this is, and how many widenings have preceded it.</summary>
    public string RescopeHeadlineText { get; }

    /// <summary>The paths this plan ADDS to what was already approved — the substance of the widening.</summary>
    public string AddedScopeText { get; }

    public bool HasAddedScope { get; }

    /// <summary>
    /// The paths it DROPS. Shown separately and never folded into the added list: a re-scope that removes
    /// a path the human already agreed to is the one shape of this op that takes something away, and a
    /// card that rendered only additions is exactly where that would hide.
    /// </summary>
    public string RemovedScopeText { get; }

    public bool HasRemovedScope { get; }

    /// <summary>
    /// True when rejecting again would stop the worker rather than produce another plan. The card says so
    /// before the click, because "reject" and "give up on this worker" are different decisions and the
    /// human is the only one who can tell which they meant.
    /// </summary>
    public bool NextRejectionEscalates { get; }

    public string RejectButtonText { get; }

    /// <summary>
    /// What one more "no" would actually do, shown when this is the last round. The two card kinds have
    /// different consequences and the text must not promise the wrong one: on a first plan the worker
    /// stops, on a re-scope only the widening closes and the worker keeps its existing approval.
    /// </summary>
    public string LastRoundWarningText { get; }

    [ObservableProperty] private bool _isDeciding;

    /// <summary>Why the last decision did not land (empty when there is nothing wrong).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDecisionError))]
    private string _decisionErrorText = "";

    public bool HasDecisionError => DecisionErrorText.Length > 0;

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
        var age = (int)Math.Max(0, (DateTimeOffset.Now - plan.PresentedAt).TotalMinutes);
        var worker = plan.WorkerAgentId.Length > 0 ? plan.WorkerAgentId : "worker";

        IsRescope = plan.IsRescope;
        var added = plan.AddedScope;
        var removed = plan.RemovedScope;
        HasAddedScope = added.Count > 0;
        HasRemovedScope = removed.Count > 0;
        AddedScopeText = string.Join("\n", added);
        RemovedScopeText = string.Join("\n", removed);
        RescopeHeadlineText = IsRescope
            ? plan.RescopeCount > 1
                ? $"Widening an approved plan — this worker's scope has already been widened "
                  + $"{plan.RescopeCount - 1} time(s)"
                : "Widening an approved plan"
            : "";

        // The two decisions read differently, so they are worded differently. On a re-scope, Reject does
        // not stop the worker and does not withdraw anything: it declines the widening and the worker
        // carries on inside the scope already approved. A button reading "worker will stop" there would be
        // false, and it is the kind of false that makes a human approve something to avoid a consequence
        // that was never going to happen.
        RejectButtonText = IsRescope
            ? "Decline the widening"
            : NextRejectionEscalates ? "Reject — worker will stop" : "Reject with feedback";
        LastRoundWarningText = IsRescope
            ? "This is the last round — declining again closes the widening for good. The worker is not "
              + "stopped: it keeps working inside the scope you already approved."
            : "This is the last revision — rejecting again stops the worker and escalates to you.";
        FactsText = IsRescope
            ? $"Asked by {worker} · budget ${plan.BudgetUsd:0.00} · asked {age} min ago · it is still "
              + "approved for its current scope and is not stopped"
            : $"Written by {worker} · budget ${plan.BudgetUsd:0.00} · presented {age} min ago · the worker is blocked until you decide";
    }

    [RelayCommand] private Task ApproveAsync() => DecideAsync(approve: true, feedback: null);

    [RelayCommand] private Task RejectAsync() => DecideAsync(approve: false, feedback: FeedbackText);

    /// <summary>
    /// The one exit for both decisions, and the reason it exists is that <c>IsDeciding</c> must come back
    /// down on <b>every</b> path out of here.
    ///
    /// <para>Leaving it latched is not a cosmetic bug on this surface. The plan gate is a blocking human
    /// gate: the worker on this card is stopped, holding its jail and its slot against the worker cap, and
    /// clicking Approve is the only thing that clears it. A card whose buttons never come back therefore
    /// does not merely look broken — it removes the operator's ability to relieve backpressure, and the
    /// coordinator stays stopped spawning until the app is restarted. Two ways that used to happen: the
    /// decision threw, or it returned while the plan stayed pending with the same id and revision, in which
    /// case <see cref="CoordinatorPanelViewModel.Refresh"/> keeps this exact instance mounted rather than
    /// replacing it with a fresh, enabled one.</para>
    ///
    /// <para>A failure is <b>said out loud</b> rather than reverted quietly, because the two outcomes look
    /// identical on this card otherwise: the plan sits there either way, and a human who is not told will
    /// wait on a worker they believe they already unblocked.</para>
    /// </summary>
    private async Task DecideAsync(bool approve, string? feedback)
    {
        if (IsDeciding)
        {
            return; // a double-click is one decision, not two
        }

        IsDeciding = true;
        DecisionErrorText = "";
        try
        {
            await _decide(PlanId, approve, feedback);
        }
        catch (Exception ex)
        {
            var verb = approve ? "Approval" : "Rejection";
            DecisionErrorText = $"{verb} was not recorded — {ex.Message}. The worker is still blocked; try again.";
        }
        finally
        {
            IsDeciding = false;
        }
    }
}

/// <summary>
/// A worker that spent its revision budget and stopped. There is no plan decision on this card — the loop
/// is over and there is nothing left to approve.
///
/// <para><b>There IS a release, and there has to be.</b> An escalated worker is still a live agent: it
/// keeps its jail and it keeps counting against <c>MaxActiveWorkers</c>, so until someone ends it the
/// coordinator has one fewer slot to spawn into. The card's own sentence has always said "steer it or end
/// it" while offering neither, and the only route that existed — Resources → right-click → End task — is
/// a context menu on a row that names no agent. A promise of an action, with the action reachable only by
/// guessing, is how a user ends up with a cap they cannot get back.</para>
///
/// <para>It is deliberately the <b>same</b> act as the Resources one (<c>EndAgentAsync</c>), stated with
/// the same consequences, rather than a gentler-sounding neighbour of it. Ending and discarding a queue
/// entry are different things and this card must not blur them: discard drops a merge-queue row, ending
/// stops the agent.</para>
/// </summary>
public sealed partial class EscalatedPlanViewModel : ViewModelBase
{
    private readonly Func<string, Task>? _endWorker;

    public string PlanId { get; }
    public string WorkerAgentId { get; }
    public string Title { get; }
    public string HeadlineText { get; }
    public string LastFeedbackText { get; }
    public bool HasLastFeedback { get; }

    /// <summary>Whether this card can actually end its worker. False when no ending seam was supplied
    /// (harness/fakes) or the plan named no worker — an action that cannot run is not offered.</summary>
    public bool ShowEndAction { get; }

    /// <summary>What ending this worker does, in the C-pattern the other confirms use: the object, what
    /// changes, what is kept.</summary>
    public string EndConfirmText { get; }

    /// <summary>The two-step guard. Nothing has been asked of the daemon while this is true.</summary>
    [ObservableProperty] private bool _isConfirmingEnd;

    /// <summary>True only while this card's own end request is in flight.</summary>
    [ObservableProperty] private bool _isEnding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEndError))]
    private string _endErrorText = "";

    public bool HasEndError => EndErrorText.Length > 0;

    public EscalatedPlanViewModel(WorkerPlanCard plan, Func<string, Task>? endWorker = null)
    {
        _endWorker = endWorker;
        PlanId = plan.PlanId;
        WorkerAgentId = plan.WorkerAgentId;
        Title = plan.Title;
        var worker = plan.WorkerAgentId.Length > 0 ? plan.WorkerAgentId : "This worker";

        // An escalated RE-SCOPE has not stopped the worker, and saying it has would be a lie that costs
        // something: a human reading "it stopped" ends a worker that is still doing approved work. What
        // actually happened is narrower — the widening was refused enough times that the worker may not
        // ask again, and it carries on inside the scope that is still approved.
        HeadlineText = plan.IsRescope
            ? $"{worker} asked {plan.MaxRevisions} times to widen its approved scope and you declined each "
              + "time, so it will not ask again. It is NOT stopped — it is still approved for its original "
              + "scope and still working. Steer it if the answer should change."
            : $"{worker} stopped after {plan.MaxRevisions} rejected plans and escalated to you. " +
              "It will not try again — steer it or end it.";
        LastFeedbackText = plan.RejectionFeedback.Length > 0 ? $"your last feedback: {plan.RejectionFeedback}" : "";
        HasLastFeedback = LastFeedbackText.Length > 0;

        ShowEndAction = endWorker is not null && plan.WorkerAgentId.Length > 0;
        var named = plan.Title.Length > 0 ? plan.Title : worker;
        EndConfirmText =
            $"End {named}? Its work is rejected and its sandbox is torn down, which frees the slot it is "
            + "holding against the worker cap. Its branch is kept until teardown, so nothing is silently lost.";
    }

    [RelayCommand] private void BeginEnd() => IsConfirmingEnd = true;

    [RelayCommand] private void CancelEnd() => IsConfirmingEnd = false;

    [RelayCommand]
    private async Task ConfirmEndAsync()
    {
        if (IsEnding || _endWorker is null) return;

        IsEnding = true;
        EndErrorText = "";
        try
        {
            await _endWorker(WorkerAgentId);
            IsConfirmingEnd = false;
        }
        catch (Exception ex)
        {
            // Said, never swallowed: the card looks identical whether the agent went away or the RPC
            // failed, and the operator's next move depends entirely on which happened.
            EndErrorText = $"Could not end this worker — {ex.Message}. It is still holding its slot; try again.";
        }
        finally
        {
            IsEnding = false;
        }
    }
}
