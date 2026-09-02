using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The human half of the phase-2 plan gate: the Approve/Reject buttons must always come back.
///
/// <para>This is not a polish concern. The plan gate is a <b>blocking</b> human gate — a worker waiting on
/// a decision still holds its jail, its worktree and its slot against
/// <c>CoordinatorLimits.MaxActiveWorkers</c>. A human who cannot click Approve cannot clear that
/// backpressure, so a card that latches its buttons disabled does not degrade the UI, it deadlocks the
/// orchestration: the cap stays full, the coordinator stays stopped spawning, and the only visible symptom
/// is the stall the panel was specifically built to explain.</para>
///
/// <para>Two ways in, and the second is the ordinary one rather than the exotic one:</para>
/// <list type="number">
/// <item>the decision call <b>throws</b>; or</item>
/// <item>the decision call returns, but the plan is <b>still pending with the same id and revision</b> —
/// which is precisely what a daemon-side failure looks like, and which makes
/// <see cref="CoordinatorPanelViewModel.Refresh"/> keep the very same
/// <see cref="PlanCardViewModel"/> instance mounted rather than replacing it with a fresh, enabled one.</item>
/// </list>
///
/// <para><b>And what each button actually sends.</b> This suite used to assert only that the buttons came
/// back, which measured almost nothing: swapping <c>ApproveAsync</c> to <c>DecideAsync(approve: false)</c>
/// and <c>RejectAsync</c> to <c>DecideAsync(approve: true)</c>, and dropping <c>FeedbackText</c> from the
/// rejection entirely, left all six tests <b>green</b>. The fake counted its decisions into a
/// <c>DecisionCalls</c> property nothing ever read, and three tests rested on
/// <c>Assert.False(card.IsDeciding)</c> — which is the field's default value and therefore also passes on
/// a card that never ran a decision at all. So every decision is now recorded with its
/// <i>(plan id, approve flag, feedback)</i> and asserted, and the latch is asserted as a
/// <b>transition</b> — true while the daemon call is in flight, false after — because only the transition
/// can fail.</para>
///
/// <para>The feedback string is pinned separately and deliberately. On a rejection it is not a UI detail:
/// the daemon delivers that exact text to the worker as the thing it must revise against, and a rejection
/// that arrives empty costs one of three permitted revisions and tells the worker nothing. A dropped
/// binding there is invisible on screen — the box still holds what was typed — and shows up only as a
/// worker that keeps producing the same plan.</para>
/// </summary>
public class CoordinatorPlanDecisionTests
{
    // ---- What each button sends ------------------------------------------

    /// <summary>
    /// Approve must send an <b>approval</b>, for the plan on the card. Nothing else in the suite pinned
    /// this, so an Approve button wired to <c>DecideAsync(approve: false)</c> — which rejects the plan,
    /// spends a revision and sends the worker back to re-plan work the human just consented to — was a
    /// green build.
    /// </summary>
    [Fact]
    public async Task Approve_SendsAnApprovalForThatPlan()
    {
        var coordinator = new FakeCoordinator();
        var panel = new CoordinatorPanelViewModel(coordinator);
        var card = panel.PendingPlan!;
        Assert.NotNull(card);

        await card.ApproveCommand.ExecuteAsync(null);

        var decision = Assert.Single(coordinator.Decisions);
        Assert.Equal("plan-7", decision.PlanId);
        Assert.True(decision.Approve, "Approve sent a REJECTION — the buttons are inverted");
    }

    /// <summary>
    /// Reject must send a rejection <b>carrying the operator's words verbatim</b>. The daemon hands that
    /// string to the worker as the feedback it revises against, so a dropped binding here is not a
    /// cosmetic loss: the rejection still costs one of three revisions, and the worker is sent back to
    /// try again having been told nothing. On screen the two are identical — the box still shows what
    /// was typed either way — which is exactly why it has to be asserted at this seam.
    /// </summary>
    [Fact]
    public async Task Reject_SendsARejection_CarryingTheTypedFeedbackVerbatim()
    {
        var coordinator = new FakeCoordinator();
        var panel = new CoordinatorPanelViewModel(coordinator);
        var card = panel.PendingPlan!;
        card.FeedbackText = "narrow the scope to TokenClock; leave RefreshService alone";

        await card.RejectCommand.ExecuteAsync(null);

        var decision = Assert.Single(coordinator.Decisions);
        Assert.Equal("plan-7", decision.PlanId);
        Assert.False(decision.Approve, "Reject sent an APPROVAL — the buttons are inverted");
        Assert.Equal("narrow the scope to TokenClock; leave RefreshService alone", decision.Feedback);
    }

    /// <summary>
    /// An empty box is still a rejection, and the emptiness has to reach the daemon as emptiness rather
    /// than be quietly turned into something. The shipped orchestrator substitutes an honest placeholder
    /// at the wire; the panel's job is only to not invent words the operator did not type.
    /// </summary>
    [Fact]
    public async Task RejectWithNothingTyped_IsStillARejection_AndInventsNoFeedback()
    {
        var coordinator = new FakeCoordinator();
        var panel = new CoordinatorPanelViewModel(coordinator);
        var card = panel.PendingPlan!;

        await card.RejectCommand.ExecuteAsync(null);

        var decision = Assert.Single(coordinator.Decisions);
        Assert.False(decision.Approve);
        Assert.Equal("", decision.Feedback);
    }

    // ---- The latch, asserted as a transition ------------------------------

    /// <summary>
    /// <c>IsDeciding</c> asserted the only way it can fail. <c>Assert.False(card.IsDeciding)</c> at the
    /// end of a decision is the field's <b>default</b>, so it passes on a card that never latched, never
    /// ran, or has no decision path at all. What has to hold is the transition: latched while the daemon
    /// call is in flight — so a second click cannot spend a second revision — and released afterwards.
    /// </summary>
    [Fact]
    public async Task ADecisionInFlight_LatchesTheButtons_AndReleasesThemAfterwards()
    {
        var gate = new TaskCompletionSource();
        var coordinator = new FakeCoordinator { Gate = gate };
        var panel = new CoordinatorPanelViewModel(coordinator);
        var card = panel.PendingPlan!;
        Assert.False(card.IsDeciding);

        var inFlight = card.ApproveCommand.ExecuteAsync(null);

        Assert.True(card.IsDeciding, "the card never latched — a second click would spend a second decision");
        Assert.False(inFlight.IsCompleted);

        gate.SetResult();
        await inFlight;

        Assert.False(card.IsDeciding); // the human can act again
        Assert.Single(coordinator.Decisions);
    }

    // ---- The failing decision --------------------------------------------

    [Fact]
    public async Task AnApprovalThatThrows_LeavesTheButtonsUsable()
    {
        var coordinator = new FakeCoordinator { Throw = new InvalidOperationException("daemon unreachable") };
        var panel = new CoordinatorPanelViewModel(coordinator);
        var card = panel.PendingPlan!;
        Assert.NotNull(card);

        await card.ApproveCommand.ExecuteAsync(null);

        // The approval was attempted, as an approval — the failure is the daemon's, not a mis-wired button.
        Assert.True(Assert.Single(coordinator.Decisions).Approve);
        Assert.False(card.IsDeciding); // the human can try again
    }

    [Fact]
    public async Task ARejectionThatThrows_LeavesTheButtonsUsable()
    {
        var coordinator = new FakeCoordinator { Throw = new InvalidOperationException("daemon unreachable") };
        var panel = new CoordinatorPanelViewModel(coordinator);
        var card = panel.PendingPlan!;
        card.FeedbackText = "narrow the scope";

        await card.RejectCommand.ExecuteAsync(null);

        var attempt = Assert.Single(coordinator.Decisions);
        Assert.False(attempt.Approve);
        Assert.Equal("narrow the scope", attempt.Feedback);
        Assert.False(card.IsDeciding);
    }

    /// <summary>
    /// The retry must be a retry of the <b>same</b> decision, with the same words. A card that keeps its
    /// feedback on screen but drops it on the second attempt sends the worker an empty rejection that the
    /// operator has no way to notice: the box in front of them still reads what they wrote.
    /// </summary>
    [Fact]
    public async Task ARetriedRejection_SendsTheSameFeedbackAgain()
    {
        var coordinator = new FakeCoordinator { Throw = new InvalidOperationException("daemon unreachable") };
        var panel = new CoordinatorPanelViewModel(coordinator);
        var card = panel.PendingPlan!;
        card.FeedbackText = "narrow the scope";

        await card.RejectCommand.ExecuteAsync(null);
        coordinator.Throw = null;
        await card.RejectCommand.ExecuteAsync(null);

        Assert.Equal(2, coordinator.Decisions.Count);
        Assert.All(coordinator.Decisions, d =>
        {
            Assert.False(d.Approve);
            Assert.Equal("narrow the scope", d.Feedback);
        });
    }

    /// <summary>
    /// A failure the human is not told about is worse than the failure: they watch a plan they believe they
    /// approved sit there, and the worker they believe they unblocked stays blocked.
    /// </summary>
    [Fact]
    public async Task AFailedDecision_SaysSoOnTheCard()
    {
        var panel = new CoordinatorPanelViewModel(new FakeCoordinator { Throw = new InvalidOperationException("daemon unreachable") });
        var card = panel.PendingPlan!;

        await card.ApproveCommand.ExecuteAsync(null);

        Assert.True(card.HasDecisionError);
        Assert.Contains("not recorded", card.DecisionErrorText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("daemon unreachable", card.DecisionErrorText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A re-scope's worker is NOT blocked by the pending widening — it keeps working under the scope already
    /// approved — so the generic "the worker is still blocked" would be false on that card.
    /// </summary>
    [Fact]
    public async Task AFailedDecisionOnARescope_DoesNotSayTheWorkerIsBlocked()
    {
        var panel = new CoordinatorPanelViewModel(
            new FakeCoordinator { Rescope = true, Throw = new InvalidOperationException("daemon unreachable") });
        var card = panel.PendingPlan!;
        Assert.True(card.IsRescope);

        await card.ApproveCommand.ExecuteAsync(null);

        Assert.True(card.HasDecisionError);
        Assert.DoesNotContain("still blocked", card.DecisionErrorText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not blocked", card.DecisionErrorText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A double-click that races the plan stream gets the daemon's "already Approved" refusal back. That
    /// decision LANDED; rendering it as "not recorded — the worker is still blocked" says the opposite.
    /// </summary>
    [Fact]
    public async Task AnAlreadyDecidedRefusal_IsNotRenderedAsAFailure()
    {
        var panel = new CoordinatorPanelViewModel(new FakeCoordinator
        {
            Throw = new InvalidOperationException(
                "Plan 'plan-7' is Approved — only a plan awaiting your decision can be approved or rejected."),
        });
        var card = panel.PendingPlan!;

        await card.ApproveCommand.ExecuteAsync(null);

        Assert.False(card.HasDecisionError);
        Assert.Equal("", card.DecisionErrorText);
        Assert.False(card.IsDeciding);
    }

    /// <summary>A retry after a failure must clear the stale error, or the card lies about the new attempt.</summary>
    [Fact]
    public async Task ARetryThatSucceeds_ClearsTheError()
    {
        var coordinator = new FakeCoordinator { Throw = new InvalidOperationException("daemon unreachable") };
        var panel = new CoordinatorPanelViewModel(coordinator);
        var card = panel.PendingPlan!;

        await card.ApproveCommand.ExecuteAsync(null);
        Assert.True(card.HasDecisionError);

        coordinator.Throw = null;
        await card.ApproveCommand.ExecuteAsync(null);

        Assert.False(card.HasDecisionError);
        Assert.Equal("", card.DecisionErrorText);
        Assert.False(card.IsDeciding);
    }

    // ---- The card's age --------------------------------------------------

    /// <summary>
    /// The reconciliation keeps a card's instance while its (id, revision) is unchanged — which is right
    /// for the feedback the human is typing, and wrong for "presented N min ago", which was computed once
    /// in the constructor and then never moved on the one surface whose purpose is to make waiting visible.
    /// </summary>
    [Fact]
    public void AKeptCardsAge_AdvancesOnRefresh()
    {
        var panel = new CoordinatorPanelViewModel(new FakeCoordinator());
        var card = panel.PendingPlan!;
        Assert.Contains("presented 2 min ago", card.FactsText, StringComparison.Ordinal);

        panel.Clock = () => DateTimeOffset.Now.AddMinutes(7);
        panel.Refresh();

        Assert.Same(card, panel.PendingPlan); // still the same decision, not a rebuilt card
        Assert.Contains("presented 9 min ago", card.FactsText, StringComparison.Ordinal);
    }

    // ---- The plan-mode toggle --------------------------------------------

    /// <summary>
    /// A toggle that never reached the daemon used to escape the command onto the dispatcher, where the
    /// crash guard turned it into a generic notice. The checkbox snapped back correctly, but nothing on
    /// the gate said why — and this is the one disagreement where the human believes they have (or have
    /// switched off) an approval step and the daemon disagrees.
    /// </summary>
    [Fact]
    public async Task AFailedPlanModeToggle_SaysSoOnTheGate_AndShowsTheDaemonsValue()
    {
        var coordinator = new FakeCoordinator { SetPlanModeThrow = new InvalidOperationException("daemon unreachable") };
        var panel = new CoordinatorPanelViewModel(coordinator);
        Assert.True(panel.PlanModeEnabled);

        panel.PlanModeEnabled = false; // the checkbox moved before the command ran
        await panel.TogglePlanModeCommand.ExecuteAsync(null);

        Assert.True(panel.HasPlanModeError);
        Assert.Contains("daemon unreachable", panel.PlanModeErrorText, StringComparison.Ordinal);
        Assert.True(panel.PlanModeEnabled, "the box must show the daemon's value, not the requested one");
        Assert.Empty(coordinator.PlanModeSets);
    }

    /// <summary>The error is cleared by success — a retry that lands, or the daemon seen holding the value.</summary>
    [Fact]
    public async Task APlanModeError_ClearsWhenTheDaemonHoldsTheRequestedValue()
    {
        var coordinator = new FakeCoordinator { SetPlanModeThrow = new InvalidOperationException("daemon unreachable") };
        var panel = new CoordinatorPanelViewModel(coordinator);
        panel.PlanModeEnabled = false;
        await panel.TogglePlanModeCommand.ExecuteAsync(null);
        Assert.True(panel.HasPlanModeError);

        // An unrelated refresh with the daemon unchanged keeps the error: nothing has been resolved.
        panel.Refresh();
        Assert.True(panel.HasPlanModeError);

        coordinator.SetPlanModeThrow = null;
        panel.PlanModeEnabled = false;
        await panel.TogglePlanModeCommand.ExecuteAsync(null);

        Assert.False(panel.HasPlanModeError);
        Assert.Equal("", panel.PlanModeErrorText);
        Assert.False(panel.PlanModeEnabled);
        Assert.Equal(new[] { false }, coordinator.PlanModeSets);
    }

    // ---- The silent failure ----------------------------------------------

    /// <summary>
    /// The path that actually ships: <c>DaemonBackedOrchestrator.SubmitPlanDecisionAsync</c> used to
    /// swallow every exception, so a failed decision looked to this panel exactly like a successful one
    /// whose plan happened to still be pending. The card is then NOT replaced (same id, same revision), and
    /// before the fix that same instance kept <c>IsDeciding == true</c> for the rest of the session.
    /// </summary>
    [Fact]
    public async Task WhenThePlanStaysPending_TheSameCardStaysUsable()
    {
        var coordinator = new FakeCoordinator(); // succeeds, but never decides the plan
        var panel = new CoordinatorPanelViewModel(coordinator);
        var card = panel.PendingPlan!;

        await card.ApproveCommand.ExecuteAsync(null);

        // The refresh kept this very instance — same id and revision, nothing to replace it with…
        Assert.Same(card, panel.PendingPlan);
        // …so the buttons on it have to be live, because there is no other card to click.
        Assert.False(card.IsDeciding);
    }

    // ---- The daemon must not swallow the failure -------------------------

    /// <summary>
    /// The UI can only report a failed decision if it is told about one, so this pins the contract against
    /// the <b>shipped</b> orchestrator rather than a fake that would only be testing itself:
    /// <see cref="DaemonBackedOrchestrator.SubmitPlanDecisionAsync"/> used to wrap the whole call in
    /// <c>catch (Exception) { }</c>, on the theory that a failure was "surfaced via ConnectionState". It was
    /// not — an approval that never reached the daemon returned a completed task indistinguishable from a
    /// successful one, which made "tell the human it failed" unimplementable no matter what the ViewModel
    /// did, and left the worker blocked while the human believed they had unblocked it.
    ///
    /// <para>The daemon here is unreachable in the bluntest available way: the channel factory throws. Any
    /// decision against it must come back as a failure.</para>
    /// </summary>
    [Fact]
    public async Task TheShippedOrchestrator_DoesNotSwallowAFailedDecision()
    {
        using var client = new DaemonClient(
            () => throw new InvalidOperationException("no daemon in a unit test"),
            () => "token");
        using var orchestrator = new DaemonBackedOrchestrator(client, ownsClient: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.SubmitPlanDecisionAsync("plan-7", approve: true));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.SubmitPlanDecisionAsync("plan-7", approve: false, feedback: "narrow it"));
    }

    // ---- fake ------------------------------------------------------------

    /// <summary>What the panel actually asked the daemon to do. The whole point of the fake.</summary>
    private sealed record RecordedDecision(string PlanId, bool Approve, string? Feedback);

    /// <summary>A coordinator holding one pending plan that never resolves unless told to.</summary>
    private sealed class FakeCoordinator : ICoordinatorService
    {
        private string _status = "Pending";

        /// <summary>Set to make the decision call fail the way an unreachable daemon does.</summary>
        public Exception? Throw { get; set; }

        /// <summary>Set to hold the decision open, so the in-flight latch is observable.</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>Set to present the plan as a RE-SCOPE of an already-approved plan.</summary>
        public bool Rescope { get; set; }

        /// <summary>
        /// Every decision, in order, with the arguments it carried. This replaces a bare
        /// <c>DecisionCalls</c> counter that no test ever read — and which therefore let an inverted
        /// Approve button and a dropped feedback string both ship green.
        /// </summary>
        public List<RecordedDecision> Decisions { get; } = new();

        public IReadOnlyList<ChatLine> GetTranscript() => Array.Empty<ChatLine>();

        public IReadOnlyList<WorkerPlanCard> GetWorkerPlans() => new[]
        {
            new WorkerPlanCard(
                "plan-7", "loom-4", "coordinator", "Fix token expiry off-by-one",
                new[] { "src/Auth/TokenClock.cs" },
                "Extract the clock behind ITokenClock.",
                "AuthTests green plus expiry-boundary cases.",
                1.50m, DateTimeOffset.Now.AddMinutes(-2), _status, 0, 3, 3, "",
                SupersedesPlanId: Rescope ? "plan-6" : "",
                PreviousScope: Rescope ? new[] { "src/Auth/ITokenClock.cs" } : null),
        };

        public IReadOnlyList<TaskPlan> GetPendingPlans() => GetWorkerPlans()
            .Where(c => c.IsPending)
            .Select(c => new TaskPlan(c.PlanId, c.Title, c.Scope, c.Approach, c.TestStrategy, c.BudgetUsd, c.PresentedAt))
            .ToArray();

        public TaskPlan? GetPlan(string planId) => GetPendingPlans().FirstOrDefault(p => p.PlanId == planId);

        public OrchestrationBackpressure GetBackpressure() => OrchestrationBackpressure.None;

        /// <summary>The plan-mode toggle this fake reports, and every value it was asked to set.</summary>
        public PlanModeView PlanMode { get; set; } = new(true, "Plan mode is ON — every worker authors a plan.");

        public List<bool> PlanModeSets { get; } = new();

        public PlanModeView GetPlanMode() => PlanMode;

        /// <summary>Set to make the toggle fail the way an unreachable daemon does.</summary>
        public Exception? SetPlanModeThrow { get; set; }

        public Task SetPlanModeAsync(bool enabled)
        {
            if (SetPlanModeThrow is not null)
            {
                throw SetPlanModeThrow;
            }

            PlanModeSets.Add(enabled);
            PlanMode = new PlanModeView(enabled, enabled ? "ON" : "OFF");
            return Task.CompletedTask;
        }

        public event Action? Changed { add { } remove { } }

        public Task SendAsync(string text) => Task.CompletedTask;

        public async Task SubmitPlanDecisionAsync(string planId, bool approve, string? feedback = null)
        {
            Decisions.Add(new RecordedDecision(planId, approve, feedback));
            if (Throw is not null)
            {
                throw Throw;
            }

            if (Gate is not null)
            {
                await Gate.Task;
            }

            // Deliberately does NOT move the plan: the daemon may not have applied the decision yet, and
            // that is the case the panel has to survive.
        }
    }
}
