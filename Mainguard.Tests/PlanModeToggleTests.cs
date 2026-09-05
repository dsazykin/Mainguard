using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The operator's <b>plan-mode toggle</b>, both ways round.
///
/// <para>The interesting half is OFF, and the claim being tested is not "the checks are skipped" — it is
/// that <b>off is a different, coherent, recorded mode</b>. A worker spawned with plan mode off is still
/// held by the gate, still counted, still asked about by the merge queue and the readiness trigger, and
/// still carries what it was on its merge record; the single thing that changes is the one the toggle
/// names. Every test below that asserts something works with the toggle off has a paired test asserting
/// the same thing still refuses with the toggle on, because a mode that only ever says yes is
/// indistinguishable from a gate somebody deleted.</para>
/// </summary>
public class PlanModeToggleTests
{
    private static TaskPlanFields Fields() => new(new[] { "src/a.cs" }, "approach", "tests");

    private static (PlanApprovalService Plans, WorkerPlanGate Gate, InMemoryAuditLog Audit) Rig()
    {
        var audit = new InMemoryAuditLog();
        var plans = new PlanApprovalService(audit: audit, limits: new CoordinatorLimits());
        return (plans, new WorkerPlanGate(plans, audit), audit);
    }

    private static void Hold(WorkerPlanGate gate, string id, WorkerPlanMode mode) =>
        gate.Hold(id, "coordinator-1", "Fix the token clock", "rewrite TokenClock in UTC", 5m, "", mode);

    // ---- The switch itself ---------------------------------------------------------------

    /// <summary>
    /// <b>Fail-closed, stated as a test rather than as a comment.</b> A settings file that does not exist,
    /// or cannot be read, must not be a way to remove a human-approval gate — so "nothing persisted"
    /// resolves to ON. This is the mutation that matters most on this type: flipping the default to false
    /// would disable approvals on every fresh install and on every box whose settings file got corrupted,
    /// silently, and nothing else in the system would notice.
    /// </summary>
    [Fact]
    public void WithNothingPersisted_PlanModeIsOn()
    {
        Assert.True(new PlanModeSwitch().Enabled);
        Assert.True(PlanModeSwitch.DefaultEnabled);
        Assert.Equal(WorkerPlanMode.Gated, new PlanModeSwitch().ModeForNewWorker);
    }

    /// <summary>An unreadable store is the same answer as an absent one — never "off".</summary>
    [Fact]
    public void WithAnUnreadableStore_PlanModeIsStillOn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mg-planmode-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not json");
        try
        {
            Assert.Null(new JsonPlanModeStore(path).Load());
            Assert.True(new PlanModeSwitch(new JsonPlanModeStore(path)).Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The setting survives a restart in BOTH directions. Off that came back on would be a preference the
    /// operator has to re-apply after every daemon restart without being told; on that came back off would
    /// be the gate quietly disappearing.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheSettingRoundTripsThroughItsStore(bool enabled)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mg-planmode-{Guid.NewGuid():N}.json");
        try
        {
            new PlanModeSwitch(new JsonPlanModeStore(path)).Set(enabled, "os:test");
            Assert.Equal(enabled, new PlanModeSwitch(new JsonPlanModeStore(path)).Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Turning a human-approval gate off is recorded, with who asked. The audit chain is the only place a
    /// later reader can reconstruct why a merge record says "OFF at spawn", so the two halves have to both
    /// exist.
    /// </summary>
    [Fact]
    public void TurningPlanModeOff_IsAudited_AndOnlyWhenItActuallyChanges()
    {
        var audit = new InMemoryAuditLog();
        var mode = new PlanModeSwitch(new InMemoryPlanModeStore(), audit);

        Assert.False(mode.Set(true, "os:alice"));   // already on — no change, no event
        Assert.True(mode.Set(false, "os:alice"));
        Assert.False(mode.Set(false, "os:alice"));  // idempotent

        var events = audit.Read().Where(e => e.Type == "plan_mode_changed").ToList();
        var change = Assert.Single(events);
        Assert.Equal("false", change.Fields["enabled"]);
        Assert.Equal("os:alice", change.Fields["actor"]);
    }

    /// <summary>The two summaries are different sentences and each names its own state.</summary>
    [Fact]
    public void TheSummarySaysWhichStateItIs()
    {
        var mode = new PlanModeSwitch();
        Assert.Contains("ON", mode.Summary, StringComparison.Ordinal);
        mode.Set(false);
        Assert.Contains("OFF", mode.Summary, StringComparison.Ordinal);
        Assert.Contains("start implementing", mode.Summary, StringComparison.Ordinal);
    }

    // ---- OFF: the task is delivered, and everything else still holds -----------------------

    /// <summary>
    /// The one behaviour the toggle names. With plan mode off the daemon hands the task over at once; with
    /// it on the same call is refused with the reason a human reads.
    /// </summary>
    [Fact]
    public void WithPlanModeOff_TheTaskIsReleasedWithoutAPlan_AndWithItOnItIsNot()
    {
        var (_, gate, _) = Rig();
        Hold(gate, "w-off", WorkerPlanMode.Ungated);
        Hold(gate, "w-on", WorkerPlanMode.Gated);

        Assert.True(gate.TryReleaseTask("w-off", out var task));
        Assert.Equal("rewrite TokenClock in UTC", task);

        Assert.False(gate.TryReleaseTask("w-on", out var withheld));
        Assert.Equal(string.Empty, withheld);
    }

    /// <summary>
    /// <b>The gate still holds it.</b> "Plan mode off" implemented as "don't call Hold" was the tempting
    /// shape and is the one the spawn path already calls strictly worse: an unheld worker is invisible to
    /// <see cref="WorkerPlanGate.MayAutoVerify"/> and carries the manual-agent wording on its merge
    /// record. This pins that the record exists and knows which mode it is.
    /// </summary>
    [Fact]
    public void AnUngatedWorkerIsStillHeld_AndItsModeIsRecorded()
    {
        var (_, gate, audit) = Rig();
        Hold(gate, "w-1", WorkerPlanMode.Ungated);

        Assert.Equal(1, gate.HeldTaskCount);
        Assert.Equal(WorkerPlanMode.Ungated, gate.ModeFor("w-1"));
        Assert.True(gate.IsUngated("w-1"));
        Assert.Equal("Fix the token clock", gate.PlanningBriefFor("w-1"));
        Assert.Equal("coordinator-1", gate.CoordinatorFor("w-1"));
        Assert.Equal(5m, gate.BudgetFor("w-1"));

        var withheld = Assert.Single(audit.Read(), e => e.Type == "worker_task_withheld");
        Assert.Equal("off", withheld.Fields["plan_mode"]);
    }

    /// <summary>The same event on a gated worker says the opposite, so the field is a real discriminator.</summary>
    [Fact]
    public void AGatedWorkersHoldEventSaysPlanModeIsOn()
    {
        var (_, gate, audit) = Rig();
        Hold(gate, "w-1", WorkerPlanMode.Gated);
        Assert.Equal(
            "on",
            Assert.Single(audit.Read(), e => e.Type == "worker_task_withheld").Fields["plan_mode"]);
    }

    /// <summary>
    /// Every predicate that keys off the gate answers YES for an ungated worker — steering, verification,
    /// automatic verification, committing (via <see cref="WorkerPlanGate.MayWork"/>) and the merge gate.
    /// A half-enforced mode is the failure this test exists to catch: one of these still refusing would
    /// leave a worker that has its task and cannot be steered, verified or merged.
    /// </summary>
    [Fact]
    public void WithPlanModeOff_EveryGatePredicateAllowsTheWorker()
    {
        var (_, gate, _) = Rig();
        Hold(gate, "w-1", WorkerPlanMode.Ungated);

        Assert.True(gate.MayWork("w-1", out var work));
        Assert.Equal(string.Empty, work);
        Assert.True(gate.MayReceivePrompt("w-1", out _));
        Assert.True(gate.MayRequestVerification("w-1", out _));
        Assert.True(gate.MayAutoVerify("w-1", out _));
        Assert.True(gate.Allows("w-1", out _));
    }

    /// <summary>The paired negative: with plan mode on, the very same calls all refuse.</summary>
    [Fact]
    public void WithPlanModeOn_EveryGatePredicateStillRefusesBeforeApproval()
    {
        var (_, gate, _) = Rig();
        Hold(gate, "w-1", WorkerPlanMode.Gated);

        Assert.False(gate.MayWork("w-1", out var work));
        Assert.Contains("has not presented a plan yet", work, StringComparison.Ordinal);
        Assert.False(gate.MayReceivePrompt("w-1", out _));
        Assert.False(gate.MayRequestVerification("w-1", out _));
        Assert.False(gate.MayAutoVerify("w-1", out _));
        Assert.False(gate.Allows("w-1", out _));
    }

    /// <summary>
    /// <b>The toggle does not leak to agents the gate never held.</b> A manual-mode agent and an
    /// external-PR head are not governed by the plan gate at all, and "plan mode is off" must not become
    /// "the daemon now starts test runs on every agent on the box on its own initiative".
    /// </summary>
    [Fact]
    public void PlanModeOff_DoesNotMakeUnheldAgentsAutoVerifiable()
    {
        var (_, gate, _) = Rig();
        Hold(gate, "w-1", WorkerPlanMode.Ungated);

        Assert.False(gate.IsUngated("stranger"));
        Assert.False(gate.MayAutoVerify("stranger", out var reason));
        Assert.Contains("not a plan-gated worker", reason, StringComparison.Ordinal);

        // …while still not BLOCKING them at the merge queue, which is the other half of the same rule.
        Assert.True(gate.Allows("stranger", out _));
    }

    // ---- The merge record ------------------------------------------------------------------

    /// <summary>
    /// <b>Three outcomes, not two.</b> The merge record must never say "plan approved" about a worker that
    /// never had a plan — that is the single worst sentence this record could carry — and must not say
    /// "not a plan-gated worker" either, which is the manual-agent wording. Collapsing either way is the
    /// mutation this test is written to kill.
    /// </summary>
    [Fact]
    public void TheMergeRecordDistinguishesOffFromApprovedAndFromUnmanaged()
    {
        var (plans, gate, _) = Rig();
        Hold(gate, "w-off", WorkerPlanMode.Ungated);
        Hold(gate, "w-on", WorkerPlanMode.Gated);
        var presented = plans.Present("w-on", "coordinator-1", "Fix the token clock", Fields(), string.Empty, 0m);
        plans.Approve(presented.PlanId!, "os:alice");

        var off = gate.MergeEvidence("w-off")!;
        Assert.Contains("OFF at spawn", off, StringComparison.Ordinal);
        Assert.DoesNotContain("plan approved", off, StringComparison.Ordinal);

        // The approved outcome NAMES its plan since the merge-identity work: an audit line that
        // claims an approval it cannot identify is the fabrication that lane exists to remove.
        Assert.Equal(
            $"plan gate: plan approved — {presented.PlanId} 'Fix the token clock'",
            gate.MergeEvidence("w-on"));
        Assert.Equal("plan gate: not a plan-gated worker", gate.MergeEvidence("stranger"));
    }

    // ---- Presenting a plan nobody wants ----------------------------------------------------

    /// <summary>
    /// An ungated worker following stale instructions is <b>refused</b>, not humoured. Accepting the plan
    /// would queue a card in front of a human who switched approvals off and is not watching for one, and
    /// the worker would then block on <c>await</c> forever — holding a jail, having already been given its
    /// task. The refusal is written for the worker to act on in one turn.
    /// </summary>
    [Fact]
    public void AnUngatedWorkerIsRefusedWhenItPresentsAPlan()
    {
        var (_, gate, _) = Rig();
        Hold(gate, "w-off", WorkerPlanMode.Ungated);
        Hold(gate, "w-on", WorkerPlanMode.Gated);

        var refusal = gate.RefusePlanPresentation("w-off");
        Assert.NotNull(refusal);
        Assert.Contains("plan mode is off", refusal!, StringComparison.Ordinal);
        Assert.Contains("nobody is waiting to approve", refusal!, StringComparison.Ordinal);

        // The gated worker — and an agent the gate never held — are not refused here.
        Assert.Null(gate.RefusePlanPresentation("w-on"));
        Assert.Null(gate.RefusePlanPresentation("stranger"));
    }

    // ---- The mode belongs to the worker, not to the current setting -------------------------

    /// <summary>
    /// <b>Flipping the switch is not retroactive, in either direction.</b> A worker already blocked at the
    /// gate must not be authorised by a toggle nobody pointed at it (an approval nobody gave), and a
    /// worker that has already been told to start must not be stranded mid-task by a preference change.
    /// The mode is read once, at Hold, and owned by the worker from then on.
    /// </summary>
    [Fact]
    public void TogglingAfterASpawn_ChangesNothingAboutAWorkerAlreadyHeld()
    {
        var (_, gate, _) = Rig();
        var mode = new PlanModeSwitch();

        Hold(gate, "spawned-while-on", mode.ModeForNewWorker);
        mode.Set(false);
        Assert.False(gate.MayWork("spawned-while-on", out _));
        Assert.False(gate.TryReleaseTask("spawned-while-on", out _));

        Hold(gate, "spawned-while-off", mode.ModeForNewWorker);
        mode.Set(true);
        Assert.True(gate.MayWork("spawned-while-off", out _));
        Assert.True(gate.TryReleaseTask("spawned-while-off", out _));
    }

    /// <summary>
    /// The release-once accounting is the same in both modes: the audit record proves the gate authorised
    /// this task exactly once, and a second copy of it is corrupted evidence rather than extra evidence.
    /// </summary>
    [Fact]
    public void AnUngatedReleaseIsStillRecordedExactlyOnce()
    {
        var (_, gate, audit) = Rig();
        var announced = 0;
        gate.TaskReleased += (_, _) => announced++;
        Hold(gate, "w-1", WorkerPlanMode.Ungated);

        Assert.True(gate.TryReleaseTask("w-1", out var first));
        Assert.True(gate.TryReleaseTask("w-1", out var second));
        Assert.Equal(first, second);          // a re-attach must keep getting its task
        Assert.Equal(1, announced);
        Assert.Single(audit.Read(), e => e.Type == "worker_task_released");
        Assert.True(gate.TaskWasReleased("w-1"));
    }

    /// <summary>
    /// A gated worker's refused release is still recorded as a denial, so the toggle did not quietly turn
    /// the deny path into a no-op.
    /// </summary>
    [Fact]
    public void AGatedWorkersRefusedReleaseIsStillAudited()
    {
        var (_, gate, audit) = Rig();
        Hold(gate, "w-1", WorkerPlanMode.Gated);
        Assert.False(gate.TryReleaseTask("w-1", out _));
        Assert.Equal(
            "no-approved-plan",
            Assert.Single(audit.Read(), e => e.Type == "worker_task_release_denied").Fields["cause"]);
    }

    // ---- Backpressure: an ungated worker never blocks anyone --------------------------------

    /// <summary>
    /// With plan mode off nothing is ever waiting on a human, so the backpressure sentence must be silent
    /// — a banner saying "0 workers waiting on your approval" on a surface with no approvals is exactly
    /// the kind of sentence that trains people to stop reading the surface.
    /// </summary>
    [Fact]
    public void UngatedWorkersProduceNoBackpressure()
    {
        var (_, gate, _) = Rig();
        Hold(gate, "w-1", WorkerPlanMode.Ungated);
        Hold(gate, "w-2", WorkerPlanMode.Ungated);

        Assert.Equal(0, gate.BlockedWorkerCount);
        Assert.Equal(0, gate.EscalatedWorkerCount);
        Assert.Null(gate.BackpressureSignal(2, 2));
    }

    // ---- The jail text ---------------------------------------------------------------------

    /// <summary>
    /// <b>Instructions that assert a gate the daemon is not applying are worse than none.</b> With plan
    /// mode off a worker told to "present your plan, then wait for the human" would block forever on a
    /// card nobody is reviewing, having already been handed its task. This pins that the two texts are
    /// actually different where it matters, in both directions.
    /// </summary>
    [Fact]
    public void TheUngatedWorkerTextDoesNotPromiseAGate()
    {
        var gated = AgentOperatingInstructions.Worker(WorkerPlanMode.Gated);
        var ungated = AgentOperatingInstructions.Worker(WorkerPlanMode.Ungated);

        Assert.Contains("You do not yet have a task", gated, StringComparison.Ordinal);
        Assert.Contains("present <plan.json>", gated, StringComparison.Ordinal);
        Assert.Contains("withholds that", gated, StringComparison.Ordinal);

        Assert.DoesNotContain("You do not yet have a task", ungated, StringComparison.Ordinal);
        Assert.DoesNotContain("present <plan.json>", ungated, StringComparison.Ordinal);
        Assert.DoesNotContain("block until the human decides", ungated, StringComparison.Ordinal);
        Assert.Contains("Plan mode is off", ungated, StringComparison.Ordinal);
        Assert.Contains("`present` is refused", ungated, StringComparison.Ordinal);
    }

    /// <summary>
    /// The half that is NOT about plans is identical in both, because it is the half that has nothing to
    /// do with the toggle and everything to do with the worktree dying with the jail — and a second
    /// wording of it would drift. Both texts must name the commit command and say uncommitted work is
    /// lost.
    /// </summary>
    [Theory]
    [InlineData(WorkerPlanMode.Gated)]
    [InlineData(WorkerPlanMode.Ungated)]
    public void BothWorkerTextsStillSayThatUncommittedWorkIsLost(WorkerPlanMode mode)
    {
        var text = AgentOperatingInstructions.Worker(mode);
        Assert.Contains("commit \"<message>\"", text, StringComparison.Ordinal);
        Assert.Contains("only way your work leaves this jail", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <b>coordinator's</b> text stops asserting the gate too. A coordinator that believed the shipped
    /// paragraphs would report a stall that is not happening and would treat a working <c>prompt</c> as a
    /// bug — and its "the task is withheld" sentence would simply be false.
    /// </summary>
    [Fact]
    public void TheCoordinatorTextStopsClaimingTheTaskIsWithheldWhenItIsNot()
    {
        var catalog = new InstalledAdapterCatalog(Path.Combine(Path.GetTempPath(), $"mg-registry-{Guid.NewGuid():N}"));
        var gated = AgentOperatingInstructions.Coordinator(catalog, WorkerPlanMode.Gated);
        var ungated = AgentOperatingInstructions.Coordinator(catalog, WorkerPlanMode.Ungated);

        Assert.Contains("is withheld until a human approves", gated, StringComparison.Ordinal);
        Assert.Contains("`prompt` and `verify` are refused", gated, StringComparison.Ordinal);

        Assert.DoesNotContain("is withheld until a human approves", ungated, StringComparison.Ordinal);
        Assert.DoesNotContain("`prompt` and `verify` are refused", ungated, StringComparison.Ordinal);
        Assert.Contains("Plan mode is off", ungated, StringComparison.Ordinal);

        // Still true in both: the coordinator never writes the plan, because it cannot see the code.
        Assert.Contains("do not write task plans", gated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not write task plans", ungated, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The kickoff turn — the one thing that makes a jailed worker start at all — must send an ungated
    /// worker at its task rather than at a plan. And it must still be a pure function of (role, shim
    /// path): the task is not a parameter of either variant, so neither can carry the work.
    /// </summary>
    [Fact]
    public void TheUngatedKickoffTurnAsksForTheTask_AndCarriesNoTask()
    {
        const string shim = "/opt/mainguard/ipc/mainguard-plan";
        var gated = AgentKickoffPrompt.For(AgentIpcEndpointRole.Worker, shim, WorkerPlanMode.Gated)!;
        var ungated = AgentKickoffPrompt.For(AgentIpcEndpointRole.Worker, shim, WorkerPlanMode.Ungated)!;

        Assert.Contains($"{shim} brief", gated, StringComparison.Ordinal);
        Assert.Contains($"{shim} task", ungated, StringComparison.Ordinal);
        Assert.DoesNotContain($"{shim} brief", ungated, StringComparison.Ordinal);
        Assert.DoesNotContain("present", ungated.Split("Plan mode is off")[0], StringComparison.Ordinal);

        // Both still tell the worker to commit — the step whose absence lost a finished change once.
        Assert.Contains($"{shim} commit", gated, StringComparison.Ordinal);
        Assert.Contains($"{shim} commit", ungated, StringComparison.Ordinal);

        // A coordinator still gets no first turn in either mode: its first turn is the operator's request.
        Assert.Null(AgentKickoffPrompt.For(AgentIpcEndpointRole.Coordinator, shim, WorkerPlanMode.Ungated));
    }

    /// <summary>
    /// The default of every mode-taking entry point is the gated text. A caller that has not been taught
    /// about the toggle must render the text that describes a gate — never the text that promises there
    /// is none.
    /// </summary>
    [Fact]
    public void EveryModeDefaultIsTheGatedOne()
    {
        var catalog = new InstalledAdapterCatalog(Path.Combine(Path.GetTempPath(), $"mg-registry-{Guid.NewGuid():N}"));
        Assert.Equal(AgentOperatingInstructions.Worker(WorkerPlanMode.Gated), AgentOperatingInstructions.Worker());
        Assert.Equal(
            AgentOperatingInstructions.Coordinator(catalog, WorkerPlanMode.Gated),
            AgentOperatingInstructions.Coordinator(catalog));
        Assert.Equal(
            AgentOperatingInstructions.For(AgentIpcEndpointRole.Worker, catalog, WorkerPlanMode.Gated),
            AgentOperatingInstructions.For(AgentIpcEndpointRole.Worker, catalog));
        Assert.Equal(
            AgentKickoffPrompt.For(AgentIpcEndpointRole.Worker, "/s", WorkerPlanMode.Gated),
            AgentKickoffPrompt.For(AgentIpcEndpointRole.Worker, "/s"));

        // And the gate's own default: a Hold that says nothing about the mode keeps the gate.
        var (_, gate, _) = Rig();
        gate.Hold("w-1", "c", "A title", "the task", 0m);
        Assert.Equal(WorkerPlanMode.Gated, gate.ModeFor("w-1"));
    }

    /// <summary>
    /// <b>The mode must reach the text through <c>For</c>, which is the entry point the launcher calls</b>
    /// — not merely through the two methods it delegates to.
    ///
    /// <para>Written because mutation M9 (dropping the mode inside <c>For</c>, so every jail gets the
    /// gated text whatever the operator set) scored <b>zero red across both tiers</b> until this existed.
    /// <see cref="EveryModeDefaultIsTheGatedOne"/> compares the no-argument overload against
    /// <c>Gated</c>, which a <c>For</c> that always renders gated satisfies perfectly, and every other
    /// text assertion calls <c>Worker</c>/<c>Coordinator</c> directly and so never crosses the one
    /// forwarding step that can drop the argument. A guard no test can turn red is indistinguishable
    /// from a guard that was deleted.</para>
    /// </summary>
    [Fact]
    public void ForRoutesTheModeToBothRolesTexts()
    {
        var catalog = new InstalledAdapterCatalog(
            Path.Combine(Path.GetTempPath(), $"mg-registry-{Guid.NewGuid():N}"));

        // It forwards, rather than rendering something of its own…
        Assert.Equal(
            AgentOperatingInstructions.Worker(WorkerPlanMode.Ungated),
            AgentOperatingInstructions.For(AgentIpcEndpointRole.Worker, catalog, WorkerPlanMode.Ungated));
        Assert.Equal(
            AgentOperatingInstructions.Coordinator(catalog, WorkerPlanMode.Ungated),
            AgentOperatingInstructions.For(AgentIpcEndpointRole.Coordinator, catalog, WorkerPlanMode.Ungated));

        // …and the argument actually changes what comes back, for BOTH roles. Equality alone would hold
        // for a `For` that ignored the mode if `Worker()`/`Coordinator()` ignored it too.
        Assert.NotEqual(
            AgentOperatingInstructions.For(AgentIpcEndpointRole.Worker, catalog, WorkerPlanMode.Gated),
            AgentOperatingInstructions.For(AgentIpcEndpointRole.Worker, catalog, WorkerPlanMode.Ungated));
        Assert.NotEqual(
            AgentOperatingInstructions.For(AgentIpcEndpointRole.Coordinator, catalog, WorkerPlanMode.Gated),
            AgentOperatingInstructions.For(AgentIpcEndpointRole.Coordinator, catalog, WorkerPlanMode.Ungated));
    }

    /// <summary>
    /// The worker's shim teaches <c>task</c>, and it is on the exhaustive worker op list — the object the
    /// daemon builds its handler table against, so an op missing from it is unreachable.
    /// </summary>
    [Fact]
    public void TheTaskOpIsOnTheWorkersSurfaceAndInItsShim()
    {
        Assert.Contains(AgentIpcRequest.TaskOp, AgentIpcRequest.WorkerOps);
        Assert.DoesNotContain(AgentIpcRequest.TaskOp, AgentIpcRequest.CoordinatorOps);
        Assert.Empty(AgentIpcRequest.WorkerOps.Intersect(AgentIpcRequest.CoordinatorOps, StringComparer.Ordinal));
        Assert.Contains("mainguard-plan task", WorkerPlanShim.Script, StringComparison.Ordinal);
        Assert.Contains("\"op\": \"task\"", WorkerPlanShim.Script.Replace("{\"op\": \"task\"}", "\"op\": \"task\""), StringComparison.Ordinal);
    }
}
