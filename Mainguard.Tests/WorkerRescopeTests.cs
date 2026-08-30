using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Mainguard.Git.Models;
using Mainguard.Git.Review;
using Xunit;
using TaskPlan = Mainguard.Agents.Agents.TaskPlan;

namespace Mainguard.Tests;

/// <summary>
/// The <b>re-scope</b> op (contract §3.1, phase 3 §23): a worker that discovers mid-task that the job
/// needs a file its approved scope does not cover presents a wider plan against the approval it already
/// holds, and a human decides it like any other plan.
///
/// <para><b>The defect, measured on this branch before any of it was written.</b> There is one live plan
/// per worker. A worker that had been approved and then needed more was answered
/// <c>Plan '…' is already approved for this worker.</c> by <c>present</c> and <c>Plan '…' is Approved —
/// only a rejected plan can be revised.</c> by <c>revise</c>. Both refusals are correct about their own
/// op; together they left a worker trying to stay legal with two moves, both bad — exceed its scope
/// silently, or stop. <see cref="TheDeadEnd_ThatThisOpExistsToRemove"/> is that measurement, kept as a
/// test so the dead end cannot come back by having the hint edited out.</para>
///
/// <para>Every test here asserts a state the system would be in <i>differently</i> if the check under test
/// were removed, and the messages are asserted where the message IS the behaviour — a refusal that does
/// not name the way out is the defect this file is about.</para>
/// </summary>
public class WorkerRescopeTests
{
    private const string Worker = "w-1";
    private const string Approver = "uid:1000";

    private static TaskPlanFields Fields(params string[] scope) =>
        new(scope.Length == 0 ? new[] { "test.js" } : scope, "the approach", "tests green");

    private static PlanApprovalService Service(IAuditLog? audit = null, int maxRevisions = 3) =>
        new(audit: audit, limits: new CoordinatorLimits(MaxPlanRevisions: maxRevisions));

    /// <summary>Present a plan and get it approved — the state every re-scope starts from.</summary>
    private static string Approved(PlanApprovalService plans, params string[] scope)
    {
        var presented = plans.Present(Worker, "coord-1", "Fix the tests", Fields(scope), "the task", 1m);
        Assert.True(presented.IsPresented, presented.Message);
        plans.Approve(presented.PlanId!, Approver);
        return presented.PlanId!;
    }

    // ---- The defect ------------------------------------------------------

    /// <summary>
    /// Both halves of the dead end, and the one thing that changed: the refusals now name the op that
    /// works. The refusal TEXT is asserted because it is the entire remedy for a worker in this state —
    /// the daemon cannot make a model run a command it has never heard of.
    /// </summary>
    [Fact]
    public void TheDeadEnd_ThatThisOpExistsToRemove()
    {
        var plans = Service();
        var planId = Approved(plans, "test.js");

        var presentedAgain = plans.Present(
            Worker, "coord-1", "Fix the tests (wider)", Fields("test.js", "src/calc.js"), "the task", 1m);
        Assert.Equal(PlanPresentationOutcome.Refused, presentedAgain.Outcome);
        Assert.Contains("already approved", presentedAgain.Message, StringComparison.Ordinal);
        Assert.Contains(WorkerPlanShim.RescopeUsage, presentedAgain.Message, StringComparison.Ordinal);

        var revised = plans.Revise(planId, "Fix the tests (wider)", Fields("test.js", "src/calc.js"));
        Assert.Equal(PlanRevisionOutcome.Refused, revised.Outcome);
        Assert.Contains(WorkerPlanShim.RescopeUsage, revised.Message, StringComparison.Ordinal);

        // ...and the op the two refusals point at actually exists and works.
        Assert.True(plans.Rescope(planId, "Fix the tests (wider)", Fields("test.js", "src/calc.js")).IsPresented);
    }

    // ---- While it waits ---------------------------------------------------

    /// <summary>
    /// <b>Decision 4: the worker is not suspended while the human decides.</b> It holds an approved plan
    /// and is mid-work; asking for more does not withdraw what it already has. The alternative — treating
    /// a pending re-scope as "no approved plan" — would make the legal move more expensive than the
    /// silent one (the worker that says nothing keeps working; the one that asks stops), and would refuse
    /// <c>commit_work</c> to a running worker, which is how F1's "stopping a worker must not destroy its
    /// commits" gets undone by a human taking an hour to read a card.
    /// </summary>
    [Fact]
    public void WhileARescopeIsPending_TheWorkerIsStillAuthorisedForExactlyTheOldScope()
    {
        var plans = Service();
        var gate = new WorkerPlanGate(plans);
        var planId = Approved(plans, "test.js");

        var rescope = plans.Rescope(planId, "Wider", Fields("test.js", "src/calc.js"));
        Assert.True(rescope.IsPresented, rescope.Message);

        Assert.True(plans.HasApprovedPlan(Worker));
        Assert.True(gate.MayWork(Worker, out _));

        // The authorisation is the OLD plan, unchanged — not the pending one, and not nothing.
        var authorising = plans.ApprovedForWorker(Worker);
        Assert.NotNull(authorising);
        Assert.Equal(planId, authorising!.PlanId);
        Assert.Equal(new[] { "test.js" }, authorising.Plan.Scope);
    }

    /// <summary>
    /// <b>The trap this op walks straight into if the F6 binding is left alone.</b> The composition root
    /// used to resolve "the approved plan" as <c>LatestForWorker</c> filtered on <c>Approved</c>. A
    /// pending re-scope is NEWER than the plan it widens, so that read answers null — and null means
    /// "unmanaged" to the flagged-change detector, which then skips the out-of-scope comparison entirely.
    /// The worker would have lost its F6 coverage by the act of asking to widen legally, silently, for as
    /// long as the human took to decide.
    ///
    /// <para>Both halves are asserted: that the old shape really would have answered null (so this is not
    /// a test of nothing), and that the shape actually wired answers the approved plan.</para>
    /// </summary>
    [Fact]
    public void TheApprovedPlanF6MeasuresAgainst_IsTheAuthorisation_NotTheNewestPlan()
    {
        var plans = Service();
        var planId = Approved(plans, "test.js");
        plans.Rescope(planId, "Wider", Fields("test.js", "src/calc.js"));

        // The shape that WAS wired, reproduced here so its failure is visible rather than argued.
        var latestFiltered = plans.LatestForWorker(Worker) is { Status: PlanStatus.Approved } latest
            ? latest.Plan
            : null;
        Assert.Null(latestFiltered);

        // The shape that IS wired.
        var authorised = plans.ApprovedPlanFor(Worker);
        Assert.NotNull(authorised);
        Assert.Equal(new[] { "test.js" }, authorised!.Scope);

        // ...and the difference is not academic: it is whether the gate compares a diff at all.
        var diff = new[] { Patch("src/calc.js") };
        Assert.Contains(
            FlaggedChangeDetector.DetectFlagged(diff, authorised, managed: true),
            f => f.Kind == FlaggedKind.OutOfApprovedScope);
        Assert.DoesNotContain(
            FlaggedChangeDetector.DetectFlagged(diff, latestFiltered, managed: latestFiltered is not null),
            f => f.Kind == FlaggedKind.OutOfApprovedScope);
    }

    // ---- The decision -----------------------------------------------------

    /// <summary>
    /// Approving a re-scope makes it the authorisation and retires the plan it widened. "One approved
    /// plan per worker" has to be an invariant rather than a habit: the flagged-change gate resolves that
    /// plan and compares every merge diff against its scope, so two of them would be a gate measuring
    /// against whichever one a lookup happened to return.
    /// </summary>
    [Fact]
    public void ApprovingARescope_SupersedesTheOldPlan_AndBecomesTheOneAuthorisation()
    {
        var audit = new InMemoryAuditLog();
        var plans = Service(audit);
        var oldId = Approved(plans, "test.js");
        var newId = plans.Rescope(oldId, "Wider", Fields("test.js", "src/calc.js")).PlanId!;

        plans.Approve(newId, Approver);

        Assert.Equal(PlanStatus.Superseded, plans.Get(oldId)!.Status);
        Assert.Equal(newId, plans.ApprovedForWorker(Worker)!.PlanId);
        Assert.Single(plans.All(), p => p.Status == PlanStatus.Approved && p.WorkerAgentId == Worker);
        Assert.Equal(new[] { "test.js", "src/calc.js" }, plans.ApprovedPlanFor(Worker)!.Scope);

        // The supersession is a fact about an authorisation, so it is in the audit chain, with both ids.
        var superseded = Assert.Single(audit.Read(), e => e.Type == "plan_superseded");
        Assert.Equal(oldId, superseded.Fields["plan_id"]);
        Assert.Equal(newId, superseded.Fields["superseded_by"]);
    }

    /// <summary>
    /// Declining a widening takes nothing away. This is the property the whole design rests on — it is
    /// what makes asking safe, and therefore what makes asking the thing a worker will do.
    /// </summary>
    [Fact]
    public void RejectingARescope_LeavesTheOriginalApprovalExactlyWhereItWas()
    {
        var plans = Service();
        var gate = new WorkerPlanGate(plans);
        var oldId = Approved(plans, "test.js");
        var newId = plans.Rescope(oldId, "Wider", Fields("test.js", "src/calc.js")).PlanId!;

        plans.Reject(newId, "src/calc.js is another worker's job");

        Assert.Equal(PlanStatus.Approved, plans.Get(oldId)!.Status);
        Assert.Equal(oldId, plans.ApprovedForWorker(Worker)!.PlanId);
        Assert.True(gate.MayWork(Worker, out _));
        Assert.Equal(new[] { "test.js" }, plans.ApprovedPlanFor(Worker)!.Scope);
    }

    // ---- The budget -------------------------------------------------------

    /// <summary>
    /// <b>Decision 1: a re-scope does not spend the revision budget.</b> The budget bounds "your plans
    /// keep being wrong"; a re-scope is "the job is bigger than it looked". Charging it would be wrong in
    /// the direction that matters — a worker whose plan was rejected three times and then approved would
    /// have nothing left, so the workers that had the hardest time agreeing a plan would be exactly the
    /// ones with no legal way to widen it, which re-creates the dead end for the population most likely
    /// to hit it.
    ///
    /// <para>Set up at the boundary deliberately: the worker arrives at its approval with the budget
    /// fully spent, which is the state where the two readings differ.</para>
    /// </summary>
    [Fact]
    public void AWorkerThatSpentEveryRevisionBeforeApproval_MayStillRescope()
    {
        var plans = Service(maxRevisions: 3);
        var planId = plans.Present(Worker, "coord-1", "Fix the tests", Fields("test.js"), "the task", 1m).PlanId!;
        for (var round = 1; round <= 3; round++)
        {
            plans.Reject(planId, $"not yet ({round})");
            Assert.True(plans.Revise(planId, "Fix the tests", Fields("test.js")).IsRevised);
        }

        plans.Approve(planId, Approver);
        Assert.Equal(3, plans.Get(planId)!.RevisionCount); // the budget really is spent

        var rescope = plans.Rescope(planId, "Wider", Fields("test.js", "src/calc.js"));

        Assert.True(rescope.IsPresented, rescope.Message);
        // ...and it arrives with its OWN budget, because it is a new plan and can itself be rejected.
        Assert.Equal(0, plans.Get(rescope.PlanId!)!.RevisionCount);
        Assert.True(plans.Revise(rescope.PlanId!, "Wider", Fields("test.js", "src/calc.js")).Outcome
            is PlanRevisionOutcome.Refused); // still Pending — nothing to revise against yet
    }

    /// <summary>
    /// The hole a fresh budget would open if the path could be re-entered: reject ×4 escalates, and a
    /// worker allowed to re-scope again would get another three rounds, forever, without a human ever
    /// saying yes. Escalation is therefore terminal for this path — and the worker keeps the approval it
    /// already had, because nothing about a refused widening withdraws one.
    /// </summary>
    [Fact]
    public void AnEscalatedRescope_IsTerminal_AndTheWorkerKeepsWorkingUnderItsOldApproval()
    {
        var plans = Service(maxRevisions: 3);
        var gate = new WorkerPlanGate(plans);
        var oldId = Approved(plans, "test.js");
        var rescopeId = plans.Rescope(oldId, "Wider", Fields("test.js", "src/calc.js")).PlanId!;

        for (var round = 1; round <= 3; round++)
        {
            plans.Reject(rescopeId, "no");
            Assert.True(plans.Revise(rescopeId, "Wider", Fields("test.js", "src/calc.js")).IsRevised);
        }

        var escalated = plans.Reject(rescopeId, "still no");
        Assert.Equal(PlanStatus.Escalated, escalated.Status);

        var again = plans.Rescope(oldId, "Wider still", Fields("test.js", "src/calc.js", "src/util.js"));
        Assert.Equal(PlanRescopeOutcome.Refused, again.Outcome);
        Assert.Contains("already escalated", again.Message, StringComparison.Ordinal);

        // The worker is NOT stopped: the widening closed, the original authorisation did not.
        Assert.True(plans.HasApprovedPlan(Worker));
        Assert.True(gate.MayWork(Worker, out _));
        Assert.Equal(oldId, plans.ApprovedForWorker(Worker)!.PlanId);
    }

    /// <summary>One live re-scope at a time — the same reason there is one live plan: a worker cannot put
    /// more cards in front of a human than the human agreed to look at.</summary>
    [Fact]
    public void AWorkerMayHaveOnlyOneLiveRescope()
    {
        var plans = Service();
        var oldId = Approved(plans, "test.js");
        var first = plans.Rescope(oldId, "Wider", Fields("test.js", "src/calc.js"));

        var second = plans.Rescope(oldId, "Wider again", Fields("test.js", "src/util.js"));

        Assert.Equal(PlanRescopeOutcome.Refused, second.Outcome);
        Assert.Equal(first.PlanId, second.PlanId);
        Assert.Single(plans.All(), p => p.IsRescope);

        // ...and once it is decided, the worker may ask again. The invariant is "one in flight", not "one".
        plans.Reject(first.PlanId!, "no");
        Assert.True(plans.Revise(first.PlanId!, "Wider", Fields("test.js", "src/calc.js")).IsRevised);
        plans.Approve(first.PlanId!, Approver);
        Assert.True(plans.Rescope(first.PlanId!, "Wider again", Fields("test.js", "src/calc.js", "src/util.js")).IsPresented);
    }

    // ---- Which plan may be re-scoped --------------------------------------

    /// <summary>
    /// Only an approved plan can be re-scoped, and every other state is refused with the op that WOULD
    /// have worked. The mutual exclusion is what makes <c>revise</c> and <c>rescope</c> safe to hand a
    /// model: a mis-picked verb is always refused rather than plausibly accepted, and the refusal is the
    /// correction.
    /// </summary>
    [Theory]
    [InlineData(PlanStatus.Pending, "await")]
    [InlineData(PlanStatus.Rejected, "revise")]
    [InlineData(PlanStatus.Escalated, "only an approved plan")]
    [InlineData(PlanStatus.Superseded, "only an approved plan")]
    public void OnlyAnApprovedPlanCanBeRescoped(PlanStatus state, string expectedHint)
    {
        // maxRevisions 0 makes the FIRST rejection escalate, so the two rejection-shaped states are
        // reachable without four rounds of setup — and they stay distinguishable, which is the point.
        var plans = Service(maxRevisions: state == PlanStatus.Escalated ? 0 : 3);
        var planId = plans.Present(Worker, "coord-1", "Fix the tests", Fields("test.js"), "the task", 1m).PlanId!;
        switch (state)
        {
            case PlanStatus.Pending:
                break;
            case PlanStatus.Rejected:
            case PlanStatus.Escalated:
                plans.Reject(planId, "no");
                break;
            case PlanStatus.Superseded:
                plans = Service();
                planId = Approved(plans, "test.js");
                var wider = plans.Rescope(planId, "Wider", Fields("test.js", "src/calc.js")).PlanId!;
                plans.Approve(wider, Approver);
                break;
        }

        Assert.Equal(state, plans.Get(planId)!.Status);

        var refused = plans.Rescope(planId, "Wider", Fields("test.js", "src/calc.js"));

        Assert.Equal(PlanRescopeOutcome.Refused, refused.Outcome);
        Assert.Contains(expectedHint, refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A plan id nobody has is refused, and nothing is queued behind it.</summary>
    [Fact]
    public void AnUnknownPlanCannotBeRescoped()
    {
        var plans = Service();
        Approved(plans, "test.js");

        var refused = plans.Rescope("no-such-plan", "Wider", Fields("test.js", "src/calc.js"));

        Assert.Equal(PlanRescopeOutcome.Refused, refused.Outcome);
        Assert.DoesNotContain(plans.All(), p => p.IsRescope);
    }

    // ---- What the card is given -------------------------------------------

    /// <summary>
    /// The human is approving a widening of something they already approved, so the record carries what
    /// changed. <see cref="PendingPlan.PreviousScope"/> is COPIED at presentation rather than looked up:
    /// the card must show what was actually consented to, and a lookup renders a different claim as soon
    /// as a second re-scope exists.
    /// </summary>
    [Fact]
    public void ARescopeCarriesWhatChanged_AndTheCopyIsNotALookup()
    {
        var plans = Service();
        var firstId = Approved(plans, "test.js");
        var secondId = plans.Rescope(firstId, "Wider", Fields("test.js", "src/calc.js")).PlanId!;
        plans.Approve(secondId, Approver);
        var thirdId = plans.Rescope(secondId, "Wider still", Fields("src/calc.js", "src/util.js")).PlanId!;

        var second = plans.Get(secondId)!;
        Assert.True(second.IsRescope);
        Assert.Equal(firstId, second.SupersedesPlanId);
        Assert.Equal(new[] { "test.js" }, second.PreviousScope);
        Assert.Equal(1, second.RescopeCount);

        var third = plans.Get(thirdId)!;
        Assert.Equal(secondId, third.SupersedesPlanId);
        Assert.Equal(new[] { "test.js", "src/calc.js" }, third.PreviousScope);
        Assert.Equal(2, third.RescopeCount);

        // The card's arithmetic: what is added, and — separately — what is dropped.
        var card = Card(third);
        Assert.Equal(new[] { "src/util.js" }, card.AddedScope);
        Assert.Equal(new[] { "test.js" }, card.RemovedScope);
    }

    /// <summary>An ordinary plan is not a re-scope, and its card must not render a diff against nothing.</summary>
    [Fact]
    public void AnOrdinaryPlan_IsNotARescope_AndShowsNoChangeSet()
    {
        var plans = Service();
        var card = Card(plans.Get(Approved(plans, "test.js"))!);

        Assert.False(card.IsRescope);
        Assert.Empty(card.AddedScope);
        Assert.Empty(card.RemovedScope);
    }

    // ---- Already-out-of-scope work ----------------------------------------

    /// <summary>
    /// <b>Decision 3: a worker may re-scope after it has already touched the extra file, and this path
    /// does not look.</b> The flagged-change gate already puts every out-of-scope file in front of a human
    /// at verification and blocks the merge until they acknowledge it, so the work is caught by exactly
    /// one mechanism whatever the answer here. Refusing a late re-scope would re-open the dead end — a
    /// worker that already slipped could never get legal again — and a second check would be two controls
    /// answering one question, which is how one of them goes decorative (MG-12).
    ///
    /// <para>The two outcomes are asserted, and both end at a human: approved, and the file is inside the
    /// authorisation F6 measures against; declined, and it is flagged exactly as it was.</para>
    /// </summary>
    [Fact]
    public void ALateRescope_IsNotRefused_AndTheOneMechanismStillDecidesTheOutOfScopeFile()
    {
        var diff = new[] { Patch("src/calc.js") };

        var declined = Service();
        var declinedId = Approved(declined, "test.js");
        var declinedRescope = declined.Rescope(declinedId, "Wider", Fields("test.js", "src/calc.js"));
        Assert.True(declinedRescope.IsPresented, "asking late is not refused");
        declined.Reject(declinedRescope.PlanId!, "no");
        Assert.Contains(
            FlaggedChangeDetector.DetectFlagged(diff, declined.ApprovedPlanFor(Worker), managed: true),
            f => f.Kind == FlaggedKind.OutOfApprovedScope);

        var allowed = Service();
        var allowedId = Approved(allowed, "test.js");
        var allowedRescope = allowed.Rescope(allowedId, "Wider", Fields("test.js", "src/calc.js"));
        allowed.Approve(allowedRescope.PlanId!, Approver);
        Assert.DoesNotContain(
            FlaggedChangeDetector.DetectFlagged(diff, allowed.ApprovedPlanFor(Worker), managed: true),
            f => f.Kind == FlaggedKind.OutOfApprovedScope);
    }

    // ---- Restart safety ---------------------------------------------------

    /// <summary>
    /// A daemon restart must not turn a re-scope back into an ordinary plan. The supersession that runs on
    /// approval is what keeps "one approved plan per worker" true, and a rehydrated plan that had
    /// forgotten what it supersedes would leave two — which is the state the invariant exists to prevent.
    /// </summary>
    [Fact]
    public void ARescopeSurvivesADaemonRestart_AndStillSupersedesOnApproval()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mg-rescope-{Guid.NewGuid():N}", "plans.json");
        try
        {
            string oldId, newId;
            {
                var plans = new PlanApprovalService(new JsonPlanApprovalStore(path));
                oldId = Approved(plans, "test.js");
                newId = plans.Rescope(oldId, "Wider", Fields("test.js", "src/calc.js")).PlanId!;
            }

            var restarted = new PlanApprovalService(new JsonPlanApprovalStore(path));
            var rehydrated = restarted.Get(newId)!;
            Assert.Equal(oldId, rehydrated.SupersedesPlanId);
            Assert.Equal(new[] { "test.js" }, rehydrated.PreviousScope);
            Assert.Equal(1, rehydrated.RescopeCount);
            Assert.Equal(oldId, restarted.ApprovedForWorker(Worker)!.PlanId);

            restarted.Approve(newId, Approver);
            Assert.Equal(PlanStatus.Superseded, restarted.Get(oldId)!.Status);
            Assert.Equal(newId, restarted.ApprovedForWorker(Worker)!.PlanId);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---- helpers ----------------------------------------------------------

    private static WorkerPlanCard Card(PendingPlan plan) => new(
        plan.PlanId, plan.WorkerAgentId, plan.CoordinatorId, plan.Title, plan.Plan.Scope,
        plan.Plan.Approach, plan.Plan.TestStrategy, plan.BudgetUsd, plan.DraftedAt,
        plan.Status.ToString(), plan.RevisionCount, 3, 3, plan.RejectionFeedback ?? "",
        plan.SupersedesPlanId ?? "", plan.PreviousScope, plan.RescopeCount);

    private static FilePatch Patch(string path) => new()
    {
        Header = $"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n",
        Hunks = new List<DiffHunk>
        {
            new()
            {
                OldStart = 1, OldCount = 1, NewStart = 1, NewCount = 2,
                Lines = new List<DiffLine> { new() { Kind = DiffLineKind.Add, Text = "var x = 1;" } },
            },
        },
    };
}
