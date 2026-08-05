using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Xunit;

// The P2-10 merge-queue record (7-field), disambiguated from the UI prototype VerificationRecord.
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Tests;

/// <summary>
/// The daemon-side half of "a worker does not start work until its plan is approved" (contract §2 + §5).
///
/// <para>The claim being tested is deliberately not "the worker waits when asked to". It is that the
/// worker <b>cannot</b> start, because the daemon never gave it the task and refuses every path by which
/// the work could take effect. MG-12 is the reason for that framing: role authorization once looked
/// present in the code and enforced nothing, so a gate is only real if removing the check changes an
/// observable outcome.</para>
/// </summary>
public class WorkerPlanGateTests
{
    private static TaskPlanFields Fields(string scope = "src/a.cs") => new(new[] { scope }, "approach", "tests");

    private static (PlanApprovalService Plans, WorkerPlanGate Gate) Rig(IAuditLog? audit = null, int maxRevisions = 3)
    {
        var plans = new PlanApprovalService(audit: audit, limits: new CoordinatorLimits(MaxPlanRevisions: maxRevisions));
        return (plans, new WorkerPlanGate(plans, audit));
    }

    // ---- The daemon holds the task ---------------------------------------

    [Fact]
    public void TheTaskIsWithheldUntilThePlanIsApproved()
    {
        var (plans, gate) = Rig();
        gate.Hold("w-1", "coord-1", "Fix the clock", "rewrite TokenClock and add boundary tests", 2m);

        // Nothing presented yet: no task.
        Assert.False(gate.TryReleaseTask("w-1", out var beforePlan));
        Assert.Equal(string.Empty, beforePlan);

        // Presented and pending: still no task.
        var planId = plans.Present("w-1", "coord-1", "Fix the clock", Fields(), "", 2m).PlanId!;
        Assert.False(gate.TryReleaseTask("w-1", out var whilePending));
        Assert.Equal(string.Empty, whilePending);
        Assert.False(gate.TaskWasReleased("w-1"));

        // Rejected: still no task (the worker owes a revision, not work).
        plans.Reject(planId, "narrow it");
        Assert.False(gate.TryReleaseTask("w-1", out _));

        // Approved: and only now does the worker learn what it was spawned to do.
        plans.Revise(planId, "Fix the clock", Fields("src/Clock.cs"));
        plans.Approve(planId, "uid:1000");
        Assert.True(gate.TryReleaseTask("w-1", out var released));
        Assert.Equal("rewrite TokenClock and add boundary tests", released);
        Assert.True(gate.TaskWasReleased("w-1"));
    }

    [Fact]
    public void TheBriefIsAvailableBeforeApproval_ButItIsNotTheTask()
    {
        // The worker needs enough to inspect the right code and write a meaningful plan. That is the
        // title, not the executable task — the distinction is the whole gate.
        var (_, gate) = Rig();
        gate.Hold("w-1", "coord-1", "Fix the clock", "rewrite TokenClock and add boundary tests", 2m);

        Assert.Equal("Fix the clock", gate.PlanningBriefFor("w-1"));
        Assert.False(gate.TryReleaseTask("w-1", out _));
    }

    [Fact]
    public void AnEscalatedWorkerNeverGetsItsTask()
    {
        var (plans, gate) = Rig(maxRevisions: 1);
        gate.Hold("w-1", "coord-1", "T", "the work", 1m);
        var planId = plans.Present("w-1", "coord-1", "T", Fields(), "", 1m).PlanId!;

        plans.Reject(planId, "no");
        plans.Revise(planId, "T", Fields("src/b.cs"));
        plans.Reject(planId, "still no"); // spends the budget → Escalated

        Assert.Equal(PlanStatus.Escalated, plans.Get(planId)!.Status);
        Assert.False(gate.TryReleaseTask("w-1", out _));
        Assert.False(gate.MayWork("w-1", out var reason));
        Assert.Contains("escalated", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleaseIsAudited_AndSoIsEveryDenial()
    {
        var audit = new InMemoryAuditLog();
        var (plans, gate) = Rig(audit);
        gate.Hold("w-1", "coord-1", "T", "the work", 1m);
        var planId = plans.Present("w-1", "coord-1", "T", Fields(), "", 1m).PlanId!;

        Assert.False(gate.TryReleaseTask("w-1", out _));
        Assert.Contains("worker_task_release_denied", audit.Read().Select(e => e.Type));

        plans.Approve(planId, "uid:1000");
        Assert.True(gate.TryReleaseTask("w-1", out _));
        Assert.Contains("worker_task_released", audit.Read().Select(e => e.Type));
    }

    // ---- MayWork ----------------------------------------------------------

    [Fact]
    public void MayWork_IsFalseAtEveryStageBeforeApproval_AndTrueOnlyAfter()
    {
        var (plans, gate) = Rig();
        gate.Hold("w-1", "coord-1", "T", "the work", 1m);

        Assert.False(gate.MayWork("w-1", out var noPlan));
        Assert.Contains("has not presented a plan", noPlan, StringComparison.Ordinal);

        var planId = plans.Present("w-1", "coord-1", "T", Fields(), "", 1m).PlanId!;
        Assert.False(gate.MayWork("w-1", out var pending));
        Assert.Contains("waiting on your approval", pending, StringComparison.Ordinal);

        plans.Reject(planId, "narrow it");
        Assert.False(gate.MayWork("w-1", out var revising));
        Assert.Contains("revising its plan", revising, StringComparison.Ordinal);

        plans.Revise(planId, "T", Fields("src/b.cs"));
        plans.Approve(planId, "uid:1000");
        Assert.True(gate.MayWork("w-1", out var cleared));
        Assert.Equal(string.Empty, cleared);
    }

    // ---- The merge-queue backstop ----------------------------------------

    [Fact]
    public void AsAMergeGate_ABlockedWorkersBranchCannotMerge()
    {
        var (plans, gate) = Rig();
        gate.Hold("w-1", "coord-1", "T", "the work", 1m);
        var planId = plans.Present("w-1", "coord-1", "T", Fields(), "", 1m).PlanId!;

        Assert.False(((IMergeGate)gate).Allows("w-1", out var blockedReason));
        Assert.NotEqual(string.Empty, blockedReason);

        plans.Approve(planId, "uid:1000");
        Assert.True(((IMergeGate)gate).Allows("w-1", out _));
    }

    [Fact]
    public void AsAMergeGate_AgentsTheGateNeverHeldAreNotBlocked()
    {
        // The negative control that keeps this gate from silently blocking every manual-mode agent and
        // every external-PR head: it answers only for the workers it was asked to hold.
        var (_, gate) = Rig();

        Assert.True(((IMergeGate)gate).Allows("some-manual-agent", out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public async Task InARealMergeQueue_ThePlanGateBlocksAVerifiedGreenBranch()
    {
        // End of the enforcement chain: even a branch that verified GREEN cannot merge without an
        // approved plan. This is the assertion that would still hold if a rogue worker did the work
        // anyway — which is exactly why it is here and not just at the IPC layer.
        var (plans, gate) = Rig();
        gate.Hold("w-1", "coord-1", "T", "the work", 1m);
        var planId = plans.Present("w-1", "coord-1", "T", Fields(), "", 1m).PlanId!;

        MergeQueue queue = null!;
        queue = new MergeQueue(
            "repo", "sha0", new InMemoryMergeQueueStore(), new InMemoryVerificationStore(),
            runVerification: (id, ct) => Task.FromResult(new VerificationRecord(
                id, queue.CurrentMainSha, true, "log", "test", "cfg", DateTimeOffset.UtcNow)),
            gates: new IMergeGate[] { gate });

        await queue.RunVerificationAsync("w-1", CancellationToken.None);
        Assert.Equal(WorkerMergeState.Verified, queue.GetState("w-1"));

        Assert.False(queue.CanMerge("w-1", out var reason));
        Assert.Contains("waiting on your approval", reason, StringComparison.Ordinal);

        plans.Approve(planId, "uid:1000");
        Assert.True(queue.CanMerge("w-1", out _));
    }

    // ---- Backpressure legibility -----------------------------------------

    [Fact]
    public void BackpressureSignal_NamesTheCountAndTheSaturatedCap()
    {
        var (plans, gate) = Rig();
        for (var i = 1; i <= 6; i++)
        {
            gate.Hold($"w-{i}", "coord-1", "T", "work", 1m);
            plans.Present($"w-{i}", "coord-1", "T", Fields(), "", 1m);
        }

        var signal = gate.BackpressureSignal(activeWorkers: 6, maxActiveWorkers: 6);

        Assert.NotNull(signal);
        Assert.Contains("6 workers are waiting on your approval", signal!, StringComparison.Ordinal);
        Assert.Contains("The worker cap (6/6) is full", signal, StringComparison.Ordinal);
        Assert.Contains("stopped spawning", signal, StringComparison.Ordinal);
    }

    [Fact]
    public void BackpressureSignal_DoesNotClaimASaturatedCapWhenThereIsHeadroom()
    {
        // The paired negative: with room to spawn, the signal must report the waiting plans WITHOUT
        // asserting the coordinator has stopped — a stall claim nobody checked is the bug class here.
        var (plans, gate) = Rig();
        gate.Hold("w-1", "coord-1", "T", "work", 1m);
        plans.Present("w-1", "coord-1", "T", Fields(), "", 1m);

        var signal = gate.BackpressureSignal(activeWorkers: 1, maxActiveWorkers: 6);

        Assert.Equal("1 worker is waiting on your approval.", signal);
    }

    [Fact]
    public void BackpressureSignal_IsNullWhenNothingIsWaiting()
    {
        var (_, gate) = Rig();
        Assert.Null(gate.BackpressureSignal(activeWorkers: 3, maxActiveWorkers: 6));
    }

    [Fact]
    public void BackpressureSignal_CountsEscalatedWorkersSeparately()
    {
        var (plans, gate) = Rig(maxRevisions: 1);
        gate.Hold("w-1", "coord-1", "T", "work", 1m);
        var planId = plans.Present("w-1", "coord-1", "T", Fields(), "", 1m).PlanId!;
        plans.Reject(planId, "no");
        plans.Revise(planId, "T", Fields("src/b.cs"));
        plans.Reject(planId, "still no");

        Assert.Equal(0, gate.BlockedWorkerCount);
        Assert.Equal(1, gate.EscalatedWorkerCount);
        Assert.Contains("1 escalated after 1 rejected plans", gate.BackpressureSignal(1, 6)!, StringComparison.Ordinal);
    }

    [Fact]
    public void ForgettingAStoppedWorker_RemovesItFromTheGateAndTheCounts()
    {
        var (plans, gate) = Rig();
        gate.Hold("w-1", "coord-1", "T", "work", 1m);
        plans.Present("w-1", "coord-1", "T", Fields(), "", 1m);
        Assert.Equal(1, gate.BlockedWorkerCount);

        gate.Forget("w-1");

        Assert.Equal(0, gate.BlockedWorkerCount);
        Assert.False(gate.TryReleaseTask("w-1", out _));
    }

    // ---- phase 3 · the held task is keyed by (RepoHash, AgentId), never the bare id -----------

    /// <summary>
    /// Two repositories, each with a worker called <c>pr-7</c> — the id collision the external-PR intake
    /// produces, and the one this codebase has already fixed three times elsewhere (#281, #284, #286).
    ///
    /// <para>Keyed by the bare id, <see cref="WorkerPlanGate.Hold"/> is idempotent per id, so repo B's
    /// <c>pr-7</c> would silently inherit repo A's held task and repo B's own task would be dropped on the
    /// floor. Keyed by (repo, id) they are two distinct holds, which is what this asserts.</para>
    /// </summary>
    [Fact]
    public void TwoRepositoriesMayEachHoldATaskForTheSameAgentId()
    {
        var (_, gate) = Rig();

        gate.Hold("pr-7", "coord-a", "Repo A title", "repo A task", 1m, repoHash: "repo-a");
        gate.Hold("pr-7", "coord-b", "Repo B title", "repo B task", 1m, repoHash: "repo-b");

        // Both holds exist independently. Under the bare-id key the second Hold returned early and repo
        // B's task never existed at all — HeldTaskCount would be 1.
        Assert.Equal(2, gate.HeldTaskCount);
    }

    /// <summary>
    /// The fail-closed half. An id held by two repos is <b>ambiguous</b>, and every question about it is
    /// answered "not authorised" rather than answered from whichever repo happened to be found first.
    ///
    /// <para>This is the security-relevant direction: approving repo B's plan must never release repo A's
    /// withheld task. Resolving an ambiguous id to an arbitrary candidate is the aliasing bug wearing a
    /// lookup's clothes, so the resolver returns nothing and the gate refuses.</para>
    /// </summary>
    [Fact]
    public void AnAmbiguousAgentId_FailsClosed_RatherThanResolvingToEitherRepo()
    {
        var (plans, gate) = Rig();
        gate.Hold("pr-7", "coord-a", "Repo A title", "repo A task", 1m, repoHash: "repo-a");
        gate.Hold("pr-7", "coord-b", "Repo B title", "repo B task", 1m, repoHash: "repo-b");

        // A plan approved for the *bare* id must not hand either repo's task over.
        var planId = plans.Present("pr-7", "coord-b", "Repo B title", Fields(), "", 1m).PlanId!;
        plans.Approve(planId, "uid:1000");

        Assert.False(
            gate.TryReleaseTask("pr-7", out var released),
            "an ambiguous agent id released a withheld task — one repo's approval authorised another's worker.");
        Assert.Equal(string.Empty, released);

        // And the brief/budget lookups refuse too, rather than serving repo A's title to repo B.
        Assert.Null(gate.PlanningBriefFor("pr-7"));
        Assert.Null(gate.CoordinatorFor("pr-7"));
    }

    /// <summary>
    /// The paired positive, so the test above cannot pass by the gate simply refusing everything: an
    /// UNambiguous id still resolves and still releases on approval.
    /// </summary>
    [Fact]
    public void AnUnambiguousAgentId_StillResolvesAndReleasesNormally()
    {
        var (plans, gate) = Rig();
        gate.Hold("pr-7", "coord-a", "Repo A title", "repo A task", 1m, repoHash: "repo-a");

        var planId = plans.Present("pr-7", "coord-a", "Repo A title", Fields(), "", 1m).PlanId!;
        plans.Approve(planId, "uid:1000");

        Assert.True(gate.TryReleaseTask("pr-7", out var released));
        Assert.Equal("repo A task", released);
        Assert.Equal("Repo A title", gate.PlanningBriefFor("pr-7"));
    }

    /// <summary>
    /// <b>Where the release-exactly-once fix and the (RepoHash, AgentId) re-key meet.</b>
    ///
    /// <para>Every other release-once test holds at the DEFAULT empty repo hash, where the composite key
    /// is <c>("", id)</c> — so a write-back that used <c>("", id)</c> instead of the resolved key would be
    /// indistinguishable from a correct one, and all of them would stay green. This test holds under a real
    /// repo hash, which is the only arrangement where the two keys differ and the wrong one is visible: the
    /// <c>Released</c> flag would never latch on the held entry, every repeat call would look like a first
    /// release, and the task would be audited and announced once per call.</para>
    ///
    /// <para>It exists because merging phase 3 onto the idempotence fix put both changes on the same three
    /// lines, and a conflict resolution is exactly the moment an enforcement quietly re-opens.</para>
    /// </summary>
    [Fact]
    public void ReleasingTwiceForARepoScopedWorker_StillAuditsAndAnnouncesOnce()
    {
        var audit = new InMemoryAuditLog();
        var (plans, gate) = Rig(audit);
        var announced = new List<string>();
        gate.TaskReleased += (workerId, prompt) => announced.Add($"{workerId}:{prompt}");

        gate.Hold("pr-7", "coord-a", "Repo A title", "repo A task", 1m, repoHash: "repo-a");
        var planId = plans.Present("pr-7", "coord-a", "Repo A title", Fields(), "", 1m).PlanId!;
        plans.Approve(planId, "uid:1000");

        // The re-attach, under a repo-scoped hold: same answer every time…
        Assert.True(gate.TryReleaseTask("pr-7", out var first));
        Assert.Equal("repo A task", first);
        Assert.True(gate.TryReleaseTask("pr-7", out var second));
        Assert.Equal("repo A task", second);
        Assert.True(gate.TryReleaseTask("pr-7", out var third));
        Assert.Equal("repo A task", third);

        // …authorised, recorded and announced exactly once.
        Assert.Single(audit.Read(), e => e.Type == "worker_task_released");
        Assert.Equal(new[] { "pr-7:repo A task" }, announced);
        Assert.True(gate.TaskWasReleased("pr-7"));
    }

    // ---- Release happens exactly once ------------------------------------

    /// <summary>
    /// A worker reaches this path more than once by design, not by malfunction: <c>mainguard-plan await
    /// &lt;id&gt;</c> is the documented re-attach after a worker crash or a daemon restart, and the daemon
    /// answers it by asking the gate again. So the second call must still hand back the task — a
    /// re-attached worker holding an approved plan and an empty prompt is stranded — while the two things
    /// that must NOT repeat are the ones a repeat corrupts: the <c>worker_task_released</c> audit record
    /// that exists to prove the gate authorised this exactly once, and the <see cref="WorkerPlanGate.TaskReleased"/>
    /// event, whose subscriber delivers the task and would deliver it twice.
    /// </summary>
    [Fact]
    public void ReleasingTwice_YieldsTheTaskAgain_ButAuditsAndAnnouncesItOnlyOnce()
    {
        var audit = new InMemoryAuditLog();
        var (plans, gate) = Rig(audit);
        var announced = new List<string>();
        gate.TaskReleased += (workerId, prompt) => announced.Add($"{workerId}:{prompt}");

        gate.Hold("w-1", "coord-1", "T", "rewrite TokenClock", 1m);
        var planId = plans.Present("w-1", "coord-1", "T", Fields(), "", 1m).PlanId!;
        plans.Approve(planId, "uid:1000");

        Assert.True(gate.TryReleaseTask("w-1", out var first));
        Assert.Equal("rewrite TokenClock", first);

        // The re-attach: same answer, because the worker still needs its task.
        Assert.True(gate.TryReleaseTask("w-1", out var second));
        Assert.Equal("rewrite TokenClock", second);
        Assert.True(gate.TryReleaseTask("w-1", out var third));
        Assert.Equal("rewrite TokenClock", third);

        // …but authorised, recorded and announced exactly once.
        Assert.Single(audit.Read(), e => e.Type == "worker_task_released");
        Assert.Equal(new[] { "w-1:rewrite TokenClock" }, announced);
    }

    /// <summary>
    /// The same claim under a race: the winner must be decided <i>under the gate's lock</i>, not by a
    /// check-then-act around it that the scheduler can interleave — a duplicate
    /// <see cref="WorkerPlanGate.TaskReleased"/> is the same task handed out twice.
    ///
    /// <para><b>Why this runs rounds instead of one race.</b> A racy implementation does not lose every
    /// time; when this test was first written against a deliberately check-then-act version it caught it in
    /// roughly four runs out of five, which is a detector that a real regression can slip past. Repeating
    /// the race makes a miss vanishingly unlikely while staying deterministic against the correct
    /// implementation, whose lock leaves no interleaving to find. Each round gets a fresh gate, because a
    /// gate that has already released has nothing left to race over.</para>
    /// </summary>
    [Fact]
    public void ConcurrentReleases_AnnounceAndAuditExactlyOnce()
    {
        const int rounds = 25;
        const int racers = 32;

        for (var round = 0; round < rounds; round++)
        {
            var audit = new InMemoryAuditLog();
            var (plans, gate) = Rig(audit);
            var announced = 0;
            gate.TaskReleased += (_, _) => Interlocked.Increment(ref announced);

            gate.Hold("w-1", "coord-1", "T", "the work", 1m);
            var planId = plans.Present("w-1", "coord-1", "T", Fields(), "", 1m).PlanId!;
            plans.Approve(planId, "uid:1000");

            // Spin on a volatile flag rather than parking on an event: a kernel wait wakes the racers one
            // at a time, which lets the first caller finish before the rest have started and hides the very
            // interleaving this test is looking for. Spinning keeps all of them hot and releases them
            // together. `ready` makes the starter wait until every racer is already spinning.
            var prompts = new string[racers];
            var results = new bool[racers];
            var go = false;
            var ready = 0;
            var threads = Enumerable.Range(0, racers).Select(i => new Thread(() =>
            {
                Interlocked.Increment(ref ready);
                while (!Volatile.Read(ref go)) Thread.SpinWait(1);
                results[i] = gate.TryReleaseTask("w-1", out var prompt);
                prompts[i] = prompt;
            })).ToList();

            foreach (var t in threads) t.Start();
            while (Volatile.Read(ref ready) < racers) Thread.SpinWait(1);
            Volatile.Write(ref go, true);
            foreach (var t in threads) t.Join();

            // Every racer gets a truthful answer…
            Assert.All(results, Assert.True);
            Assert.All(prompts, p => Assert.Equal("the work", p));

            // …and exactly one of them was the release.
            Assert.Equal(1, announced);
            Assert.Single(audit.Read(), e => e.Type == "worker_task_released");
        }
    }
}
