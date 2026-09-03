using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The phase-2 plan gate, worker side (coordinator contract §2): the worker authors the plan, presents it
/// and <b>blocks</b>; rejection returns the human's feedback and the worker revises; the rejection that
/// spends the daemon-side revision budget escalates instead of looping.
///
/// <para>Every test here asserts a state the system would be in <i>differently</i> if the check under test
/// were removed — the non-vacuity discipline. Where a test could pass by accident (an assertion on a
/// message string, a count that is already zero) it is paired with the opposite case.</para>
/// </summary>
public class WorkerAuthoredPlanTests
{
    private static TaskPlanFields Fields(string scope = "src/a.cs") =>
        new(new[] { scope }, "approach", "tests");

    private static PlanApprovalService Service(IAuditLog? audit = null, int maxRevisions = 3) =>
        new(audit: audit, limits: new CoordinatorLimits(MaxPlanRevisions: maxRevisions));

    // ---- Authorship ------------------------------------------------------

    [Fact]
    public void PresentedPlan_IsAttributedToTheWorkerThatAuthoredIt()
    {
        var plans = Service();

        var result = plans.Present("w-1", "coord-1", "Fix the clock", Fields(), "do it", 1.5m);

        Assert.True(result.IsPresented);
        var plan = plans.Get(result.PlanId!)!;
        Assert.Equal("w-1", plan.WorkerAgentId);
        Assert.Equal("coord-1", plan.CoordinatorId);
        Assert.Equal(PlanStatus.Pending, plan.Status);
        Assert.Equal(0, plan.RevisionCount);
    }

    [Fact]
    public void AWorkerMayHoldOnlyOneLivePlan()
    {
        // The invariant that replaced the S-8 per-coordinator drafting caps. Without it a worker could
        // queue an unbounded number of approvals; with a per-coordinator cap instead, the Nth worker
        // admitted by MaxActiveWorkers could never present at all — a deadlock, not backpressure.
        var plans = Service();
        var first = plans.Present("w-1", "coord-1", "A", Fields(), "p", 1m);

        var second = plans.Present("w-1", "coord-1", "B", Fields("src/b.cs"), "p", 1m);

        Assert.Equal(PlanPresentationOutcome.Refused, second.Outcome);
        Assert.Single(plans.All());
        Assert.Equal(first.PlanId, plans.All().Single().PlanId);

        // A DIFFERENT worker is unaffected — the cap is per worker, not per coordinator.
        Assert.True(plans.Present("w-2", "coord-1", "C", Fields(), "p", 1m).IsPresented);
        Assert.Equal(2, plans.All().Count);
    }

    // ---- The block is real ------------------------------------------------

    [Fact]
    public async Task AwaitDecision_DoesNotReturnUntilAHumanDecides()
    {
        var plans = Service();
        var presented = plans.Present("w-1", "coord-1", "A", Fields(), "p", 1m);

        var waiting = plans.AwaitDecisionAsync(presented.PlanId!);

        // The worker is genuinely parked: nothing completes this task but a decision.
        var finishedEarly = await Task.WhenAny(waiting, Task.Delay(200)) == waiting;
        Assert.False(finishedEarly, "AwaitDecisionAsync returned before any human decision");

        plans.Approve(presented.PlanId!, "uid:1000");

        var decision = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(decision.Approved);
        Assert.Equal("uid:1000", decision.ApproverIdentity);
    }

    [Fact]
    public async Task AwaitDecision_OnAnAlreadyDecidedPlan_ReturnsImmediately()
    {
        // A worker that reconnects after a daemon restart must not be stranded waiting for an event that
        // already fired.
        var plans = Service();
        var presented = plans.Present("w-1", "coord-1", "A", Fields(), "p", 1m);
        plans.Approve(presented.PlanId!, "uid:1000");

        var decision = await plans.AwaitDecisionAsync(presented.PlanId!).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(decision.Approved);
    }

    // ---- Rejection is feedback -------------------------------------------

    [Fact]
    public async Task Rejection_DeliversTheHumansFeedbackToTheWorker_AndIsNotTerminal()
    {
        var plans = Service();
        var presented = plans.Present("w-1", "coord-1", "A", Fields(), "p", 1m);
        var waiting = plans.AwaitDecisionAsync(presented.PlanId!);

        plans.Reject(presented.PlanId!, "the scope is too wide — touch only the clock");

        var decision = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlanStatus.Rejected, decision.Status);
        Assert.Equal("the scope is too wide — touch only the clock", decision.Feedback);
        Assert.False(decision.Approved);
        Assert.False(decision.Escalated);

        // Not terminal: the worker can revise the same plan.
        var revised = plans.Revise(presented.PlanId!, "A (narrowed)", Fields("src/Clock.cs"));
        Assert.True(revised.IsRevised);
        Assert.Equal(PlanStatus.Pending, plans.Get(presented.PlanId!)!.Status);
        Assert.Equal(1, plans.Get(presented.PlanId!)!.RevisionCount);
        Assert.Equal(new[] { "src/Clock.cs" }, plans.Get(presented.PlanId!)!.Plan.Scope);
    }

    [Fact]
    public void ARejectedPlanCannotBeApproved_ItMustBeRevisedFirst()
    {
        var plans = Service();
        var presented = plans.Present("w-1", "coord-1", "A", Fields(), "p", 1m);
        plans.Reject(presented.PlanId!, "no");

        // Approving a plan the human already sent back would approve fields nobody re-read.
        Assert.Throws<InvalidOperationException>(() => plans.Approve(presented.PlanId!, "uid:1000"));
    }

    [Fact]
    public void ReviseIsRefusedForAPlanThatIsNotAwaitingARevision()
    {
        var plans = Service();
        var presented = plans.Present("w-1", "coord-1", "A", Fields(), "p", 1m);

        var refused = plans.Revise(presented.PlanId!, "A", Fields("src/b.cs"));

        Assert.Equal(PlanRevisionOutcome.Refused, refused.Outcome);
        Assert.Equal(new[] { "src/a.cs" }, plans.Get(presented.PlanId!)!.Plan.Scope); // untouched
    }

    // ---- The revision budget is daemon-side and bounded --------------------

    [Fact]
    public void ThreeRejectionsGiveThreeRevisions_AndTheFourthRejectionEscalates()
    {
        // The arithmetic pinned: MaxPlanRevisions = 3 means reject → revise ×3, then the 4th rejection
        // stops the worker. The worker never counts this itself — the daemon decides.
        var audit = new InMemoryAuditLog();
        var plans = Service(audit, maxRevisions: 3);
        var presented = plans.Present("w-1", "coord-1", "A", Fields(), "p", 1m);
        var planId = presented.PlanId!;

        for (var round = 1; round <= 3; round++)
        {
            var rejected = plans.Reject(planId, $"feedback {round}");
            Assert.Equal(PlanStatus.Rejected, rejected.Status);

            var revised = plans.Revise(planId, "A", Fields($"src/{round}.cs"));
            Assert.Equal(PlanRevisionOutcome.Revised, revised.Outcome);
            Assert.Equal(round, plans.Get(planId)!.RevisionCount);
        }

        var fourth = plans.Reject(planId, "still not it");

        Assert.Equal(PlanStatus.Escalated, fourth.Status);
        Assert.Contains("plan_escalated", audit.Read().Select(e => e.Type));

        // And the loop really is over: no further revision is accepted.
        Assert.Equal(PlanRevisionOutcome.Refused, plans.Revise(planId, "A", Fields("src/x.cs")).Outcome);
    }

    [Fact]
    public void AnEscalatedWorker_CannotPresentAgain_UntilAHumanAsksForANewPlan_AndThenOnlyOnce()
    {
        var audit = new InMemoryAuditLog();
        var plans = Service(audit, maxRevisions: 1);
        var first = plans.Present("w-1", "coord-1", "A", Fields(), "p", 1m).PlanId!;
        plans.Reject(first, "no");
        plans.Revise(first, "A", Fields("src/b.cs"));
        plans.Reject(first, "still no");
        Assert.Equal(PlanStatus.Escalated, plans.Get(first)!.Status);

        // Escalation is terminal on the worker's side: a fresh present is refused, not re-budgeted.
        var refused = plans.Present("w-1", "coord-1", "B", Fields(), "p", 1m);
        Assert.Equal(PlanPresentationOutcome.Refused, refused.Outcome);
        Assert.Contains("the human owns the next move", refused.Message);

        // The one human act that reopens it, with guidance the worker reads as feedback.
        var reopened = plans.RequestNewPlan(first, "keep it to src/a.cs", "tester");
        Assert.True(reopened.NewPlanRequested);
        Assert.Equal("keep it to src/a.cs", plans.AwaitingNewPlanFor("w-1")!.RejectionFeedback);
        Assert.Contains("plan_new_plan_requested", audit.Read().Select(e => e.Type));

        var second = plans.Present("w-1", "coord-1", "B", Fields(), "p", 1m);
        Assert.Equal(PlanPresentationOutcome.Presented, second.Outcome);
        Assert.Null(plans.AwaitingNewPlanFor("w-1")); // answered: the fresh plan is the live one now

        // A second escalation is terminal for good — no further present, and no second request.
        plans.Reject(second.PlanId!, "no");
        plans.Revise(second.PlanId!, "B", Fields("src/c.cs"));
        plans.Reject(second.PlanId!, "no again");
        Assert.Equal(PlanStatus.Escalated, plans.Get(second.PlanId!)!.Status);
        var terminal = plans.Present("w-1", "coord-1", "C", Fields(), "p", 1m);
        Assert.Equal(PlanPresentationOutcome.Refused, terminal.Outcome);
        Assert.Contains("escalated twice", terminal.Message);
        Assert.Throws<InvalidOperationException>(() => plans.RequestNewPlan(second.PlanId!, "again", "tester"));

        // And only an escalated plan can be sent back for a new one.
        var pending = plans.Present("w-2", "coord-1", "A", Fields(), "p", 1m).PlanId!;
        Assert.Throws<InvalidOperationException>(() => plans.RequestNewPlan(pending, "x", "tester"));
    }

    [Fact]
    public async Task TheEscalatingRejection_WakesTheBlockedWorkerWithEscalated_NotRejected()
    {
        var plans = Service(maxRevisions: 1);
        var presented = plans.Present("w-1", "coord-1", "A", Fields(), "p", 1m);
        var planId = presented.PlanId!;

        plans.Reject(planId, "first");
        plans.Revise(planId, "A", Fields("src/b.cs"));

        var waiting = plans.AwaitDecisionAsync(planId);
        plans.Reject(planId, "second");

        var decision = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(decision.Escalated);
        Assert.Equal(PlanStatus.Escalated, decision.Status);
    }

    [Fact]
    public void TheBudgetIsConfigurable_AndAHigherBudgetGrantsMoreRounds()
    {
        // The negative control for the escalation test: with a larger budget the SAME sequence does not
        // escalate, which is what shows the escalation came from the limit rather than from anything else.
        var plans = Service(maxRevisions: 5);
        var planId = plans.Present("w-1", "coord-1", "A", Fields(), "p", 1m).PlanId!;

        for (var round = 1; round <= 4; round++)
        {
            Assert.Equal(PlanStatus.Rejected, plans.Reject(planId, "no").Status);
            Assert.True(plans.Revise(planId, "A", Fields($"src/{round}.cs")).IsRevised);
        }

        Assert.Equal(PlanStatus.Rejected, plans.Reject(planId, "no").Status);
    }

    // ---- The full worker loop --------------------------------------------

    [Fact]
    public async Task WorkerPlanAuthor_RevisesAgainstFeedback_AndIsApprovedOnTheSecondPresentation()
    {
        var plans = Service();
        var drafter = new ScriptedWorkerPlanDrafter(
            "Fix the clock",
            Fields("src/everything.cs"),
            Fields("src/Clock.cs"));
        var author = new WorkerPlanAuthor(drafter, new LocalWorkerPlanChannel(plans, "w-1", "coord-1"));

        var run = author.RunAsync();

        // Reject the first plan with feedback, then approve the revision.
        var first = await WaitForPendingAsync(plans);
        plans.Reject(first, "too wide — only the clock");
        var second = await WaitForPendingAsync(plans, afterRevision: 1);
        plans.Approve(second, "uid:1000");

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkerPlanPhaseOutcome.Approved, result.Outcome);
        Assert.Equal(1, result.Revisions);
        Assert.Equal(2, result.Presentations);
        Assert.Equal(new[] { "too wide — only the clock" }, drafter.FeedbackSeen);
        Assert.Equal(new[] { "src/Clock.cs" }, plans.Get(second)!.Plan.Scope);
    }

    [Fact]
    public async Task WorkerPlanAuthor_StopsAndEscalatesOnTheFourthRejection_RatherThanLooping()
    {
        var plans = Service(maxRevisions: 3);
        var drafter = new ScriptedWorkerPlanDrafter("Fix the clock", Fields());
        var escalations = new List<string>();
        var author = new WorkerPlanAuthor(
            drafter, new LocalWorkerPlanChannel(plans, "w-1", "coord-1"), escalations.Add);

        var run = author.RunAsync();

        for (var round = 0; round < 4; round++)
        {
            var planId = await WaitForPendingAsync(plans, afterRevision: round);
            plans.Reject(planId, $"no ({round})");
        }

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkerPlanPhaseOutcome.Escalated, result.Outcome);
        Assert.Equal(3, result.Revisions);          // three revisions were permitted
        Assert.Equal(4, result.Presentations);      // and four plans reached the human
        Assert.Single(escalations);
        Assert.Contains("needs a human decision", escalations[0], StringComparison.Ordinal);
        Assert.Equal(PlanStatus.Escalated, plans.Get(result.PlanId!)!.Status);
    }

    // ---- Persistence ------------------------------------------------------

    [Fact]
    public void RevisionStateSurvivesARestart_SoTheBudgetIsNotHandedBack()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mainguard-plan-tests", Guid.NewGuid().ToString("N"), "plans.json");
        try
        {
            string planId;
            {
                var plans = new PlanApprovalService(
                    new JsonPlanApprovalStore(path), limits: new CoordinatorLimits(MaxPlanRevisions: 3));
                planId = plans.Present("w-1", "coord-1", "A", Fields(), "p", 1m).PlanId!;
                plans.Reject(planId, "narrow it");
                plans.Revise(planId, "A", Fields("src/b.cs"));
                plans.Reject(planId, "still wide");
            }

            // A "restarted daemon" reading the same store.
            var resumed = new PlanApprovalService(
                new JsonPlanApprovalStore(path), limits: new CoordinatorLimits(MaxPlanRevisions: 3));
            var plan = resumed.Get(planId);

            Assert.NotNull(plan);
            Assert.Equal(PlanStatus.Rejected, plan!.Status);
            Assert.Equal(1, plan.RevisionCount);          // the spent revision is remembered
            Assert.Equal("still wide", plan.RejectionFeedback);
            Assert.Equal("w-1", plan.WorkerAgentId);
        }
        finally
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (dir is not null && System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>Waits for the worker's next presentation to reach the queue (the loop is asynchronous).</summary>
    private static async Task<string> WaitForPendingAsync(PlanApprovalService plans, int afterRevision = 0)
    {
        for (var i = 0; i < 200; i++)
        {
            var pending = plans.Pending().FirstOrDefault(p => p.RevisionCount == afterRevision);
            if (pending is not null)
            {
                return pending.PlanId;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"no pending plan at revision {afterRevision} appeared");
    }
}
