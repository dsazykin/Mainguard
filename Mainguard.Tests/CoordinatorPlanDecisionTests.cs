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
/// </summary>
public class CoordinatorPlanDecisionTests
{
    // ---- The failing decision --------------------------------------------

    [Fact]
    public async Task AnApprovalThatThrows_LeavesTheButtonsUsable()
    {
        var panel = new CoordinatorPanelViewModel(new FakeCoordinator { Throw = new InvalidOperationException("daemon unreachable") });
        var card = panel.PendingPlan!;
        Assert.NotNull(card);

        await card.ApproveCommand.ExecuteAsync(null);

        Assert.False(card.IsDeciding); // the human can try again
    }

    [Fact]
    public async Task ARejectionThatThrows_LeavesTheButtonsUsable()
    {
        var panel = new CoordinatorPanelViewModel(new FakeCoordinator { Throw = new InvalidOperationException("daemon unreachable") });
        var card = panel.PendingPlan!;
        card.FeedbackText = "narrow the scope";

        await card.RejectCommand.ExecuteAsync(null);

        Assert.False(card.IsDeciding);
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
        var orchestrator = new DaemonBackedOrchestrator(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.SubmitPlanDecisionAsync("plan-7", approve: true));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.SubmitPlanDecisionAsync("plan-7", approve: false, feedback: "narrow it"));
    }

    // ---- fake ------------------------------------------------------------

    /// <summary>A coordinator holding one pending plan that never resolves unless told to.</summary>
    private sealed class FakeCoordinator : ICoordinatorService
    {
        private string _status = "Pending";

        /// <summary>Set to make the decision call fail the way an unreachable daemon does.</summary>
        public Exception? Throw { get; set; }

        public int DecisionCalls { get; private set; }

        public IReadOnlyList<ChatLine> GetTranscript() => Array.Empty<ChatLine>();

        public IReadOnlyList<WorkerPlanCard> GetWorkerPlans() => new[]
        {
            new WorkerPlanCard(
                "plan-7", "loom-4", "coordinator", "Fix token expiry off-by-one",
                new[] { "src/Auth/TokenClock.cs" },
                "Extract the clock behind ITokenClock.",
                "AuthTests green plus expiry-boundary cases.",
                1.50m, DateTimeOffset.Now.AddMinutes(-2), _status, 0, 3, 3, ""),
        };

        public IReadOnlyList<TaskPlan> GetPendingPlans() => GetWorkerPlans()
            .Where(c => c.IsPending)
            .Select(c => new TaskPlan(c.PlanId, c.Title, c.Scope, c.Approach, c.TestStrategy, c.BudgetUsd, c.PresentedAt))
            .ToArray();

        public TaskPlan? GetPlan(string planId) => GetPendingPlans().FirstOrDefault(p => p.PlanId == planId);

        public OrchestrationBackpressure GetBackpressure() => OrchestrationBackpressure.None;

        public event Action? Changed { add { } remove { } }

        public Task SendAsync(string text) => Task.CompletedTask;

        public Task SubmitPlanDecisionAsync(string planId, bool approve, string? feedback = null)
        {
            DecisionCalls++;
            if (Throw is not null)
            {
                throw Throw;
            }

            // Deliberately does NOT move the plan: the daemon may not have applied the decision yet, and
            // that is the case the panel has to survive.
            return Task.CompletedTask;
        }
    }
}
