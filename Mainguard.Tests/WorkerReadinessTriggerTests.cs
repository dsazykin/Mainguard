using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Xunit;
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Tests;

/// <summary>
/// Phase 2's automatic verification trigger: a delegated worker's approved work verifies itself once its
/// branch stops advancing, with no human pressing Verify.
///
/// <para><b>What every test here is really asserting</b> is that the trigger is a TRIGGER. It never
/// verifies anything itself — each fire is observed as a real state walk of a real
/// <see cref="MergeQueue"/> (<c>Working → Verifying → Verified</c>) driven by
/// <see cref="MergeQueue.RunVerificationAsync"/>, which is the same method the Verify button, the restart
/// resume and the stale cascade call. A second verification path that agreed with the first today and
/// drifted tomorrow is the defect this whole area exists to have repaired.</para>
///
/// <para>Time is a virtual clock and the sweep is hand-cranked
/// (<see cref="WorkerReadinessTrigger.DriveManually"/>), so the debounce and the cooldown are asserted
/// rather than slept on.</para>
/// </summary>
public class WorkerReadinessTriggerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private sealed class Rig : IDisposable
    {
        public DateTimeOffset Now = T0;
        public readonly InMemoryAuditLog Audit = new();
        public readonly PlanApprovalService Plans;
        public readonly WorkerPlanGate Gate;
        public readonly MergeQueueRegistry Registry = new();
        public readonly MergeQueue Queue;
        public readonly InMemoryVerificationStore VerStore = new();
        public readonly AgentRefWatcher Watcher;
        public readonly WorkerReadinessTrigger Trigger;
        public readonly List<string> Runs = new();
        public readonly List<string> Log = new();

        private readonly TaskCompletionSource _hold = new();
        private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);
        private readonly HashSet<string> _fails = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Exception> _refusals = new(StringComparer.Ordinal);
        private long _tick;

        /// <summary>Every id the queue's stale-cascade requeue delegate was asked to re-enter.</summary>
        public readonly List<string> Requeues = new();

        /// <param name="holdStale">
        /// Wire a requeue delegate that RECORDS and HOLDS (re-verifies nothing), so a stale entry rests at
        /// StaleVerified and "which road did the trigger take" is observable: the cascade's delegate, or the
        /// direct run. Null keeps the queue's default (the direct run), which is what every test written
        /// before the door existed assumes.
        /// </param>
        public Rig(CoordinatorLimits? limits = null, bool holdStale = false)
        {
            Plans = new PlanApprovalService(audit: Audit);
            Gate = new WorkerPlanGate(Plans, Audit);

            MergeQueue queue = null!;
            queue = new MergeQueue(
                repoHash: "repo",
                currentMainSha: "main0",
                store: new InMemoryMergeQueueStore(),
                verifications: VerStore,
                runVerification: async (id, ct) =>
                {
                    lock (Runs) { Runs.Add(id); }

                    // A REFUSAL: the run never happened, so there is nothing to record. This is what
                    // MergeQueueProvisioner.RunVerificationAsync does for a jail that is gone, a branch that
                    // drifted, a toolchain that is missing — and (once PR #322 lands) a verify command whose
                    // shell operators survived tokenisation.
                    if (_refusals.TryGetValue(id, out var refusal))
                    {
                        throw refusal;
                    }

                    if (_blocked.Contains(id))
                    {
                        await _hold.Task.WaitAsync(ct).ConfigureAwait(false);
                    }

                    return new VerificationRecord(
                        id, queue.CurrentMainSha, !_fails.Contains(id), "log.txt", "npm test", "cfg",
                        T0.AddSeconds(Interlocked.Increment(ref _tick)));
                },
                requeue: holdStale
                    ? (id, _) => { lock (Requeues) { Requeues.Add(id); } return Task.CompletedTask; }
                    : null,
                gates: Array.Empty<IMergeGate>(),
                audit: Audit,
                clock: () => Now);
            Queue = queue;
            Registry.Register("repo", new MergeQueueContext(Queue, new InMemoryMergeLeaseStore()));

            // A real watcher, built to run no loop of its own: this rig drives the trigger through
            // NotifyAdvanced, and the watcher→trigger subscription is proven against real git in
            // Mainguard.Server.Tests (AgentRefMediationTests).
            Watcher = new AgentRefWatcher(
                new AgentRefMediator(new AgentRepoManager(System.IO.Path.GetTempPath()), _ => System.IO.Path.GetTempPath()),
                new AgentRepoManager(System.IO.Path.GetTempPath()),
                AgentRefWatcher.DriveManually);

            Trigger = new WorkerReadinessTrigger(
                source: Watcher,
                queues: Registry,
                planGate: Gate,
                limits: limits ?? new CoordinatorLimits(),
                sweepInterval: WorkerReadinessTrigger.DriveManually,
                clock: () => Now,
                log: line => { lock (Log) { Log.Add(line); } });
        }

        /// <summary>A worker the coordinator delegated to and whose plan a human approved.</summary>
        public void ApprovedWorker(string id)
        {
            Gate.Hold(id, "coord", "Fix the clock", "do the work", 1m);
            var planId = Plans.Present(id, "coord", "Fix the clock", new TaskPlanFields(new[] { "src/a.cs" }, "how", "tests"), "", 1m).PlanId!;
            Plans.Approve(planId, "tester");
        }

        /// <summary>A delegated worker whose plan is presented but NOT approved.</summary>
        public void PendingWorker(string id)
        {
            Gate.Hold(id, "coord", "Fix the clock", "do the work", 1m);
            Plans.Present(id, "coord", "Fix the clock", new TaskPlanFields(new[] { "src/a.cs" }, "how", "tests"), "", 1m);
        }

        public void RefuseRunFor(string id, Exception refusal) => _refusals[id] = refusal;
        public void BlockRunFor(string id) => _blocked.Add(id);
        public void ReleaseBlockedRun() => _hold.TrySetResult();
        public void FailRunFor(string id) => _fails.Add(id);

        /// <summary>Undoes <see cref="FailRunFor"/> — the agent pushed a fix and its next run passes.</summary>
        public void PassRunFor(string id) => _fails.Remove(id);

        public void Advance(TimeSpan by) => Now += by;

        public IReadOnlyList<ReadinessDecision> Sweep() => Trigger.PollOnce();

        public int RunCountFor(string id)
        {
            lock (Runs) { return Runs.Count(r => r == id); }
        }

        public void Dispose()
        {
            Trigger.Dispose();
            Watcher.Dispose();
        }
    }

    private static ReadinessDecision Only(IReadOnlyList<ReadinessDecision> decisions, string agentId) =>
        Assert.Single(decisions, d => d.AgentId == agentId);

    // ---- A stale entry goes through the cascade, never the direct run ----

    /// <summary>
    /// From <c>StaleVerified</c> the trigger asks for the cascade's re-entry (reparent, then re-verify)
    /// and NOT for a direct run. The direct run verifies the branch where it stands — a pass pinned to the
    /// new main for a branch that does not descend from it, i.e. the loop-forever <c>Verified</c> the
    /// cascade exists to end — and this trigger was one of the doors back into it.
    /// </summary>
    [Fact]
    public async Task FromStaleVerified_TheTriggerTakesTheCascadesReEntry_NotTheDirectRun()
    {
        using var rig = new Rig(holdStale: true);
        rig.ApprovedWorker("w-1");
        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-a");
        rig.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(ReadinessOutcome.Fired, Only(rig.Sweep(), "w-1").Outcome);
        await rig.Trigger.LastRun;
        Assert.Equal(WorkerMergeState.Verified, rig.Queue.GetState("w-1"));
        Assert.Equal(1, rig.RunCountFor("w-1"));

        // Main moves; the cascade parks the entry at StaleVerified (the rig's requeue holds).
        rig.Queue.NotifyMainMoved("main1");
        await rig.Queue.LastCascade;
        Assert.Equal(WorkerMergeState.StaleVerified, rig.Queue.GetState("w-1"));
        Assert.Equal(new[] { "w-1" }, rig.Requeues);

        // The worker pushes again and goes quiet, past the cooldown.
        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-b");
        rig.Advance(TimeSpan.FromSeconds(700));
        Assert.Equal(ReadinessOutcome.Fired, Only(rig.Sweep(), "w-1").Outcome);
        await rig.Trigger.LastRun;

        Assert.Equal(new[] { "w-1", "w-1" }, rig.Requeues); // the cascade's door, a second time
        Assert.Equal(1, rig.RunCountFor("w-1"));            // and no direct run for the stale entry
        Assert.Contains(rig.Log, l => l.Contains("re-entry", StringComparison.Ordinal));
    }

    // ---- The gap this closes --------------------------------------------

    /// <summary>
    /// The whole point: a delegated worker with an approved plan whose branch has gone quiet reaches
    /// <c>Verified</c> with <b>nobody having pressed Verify</b>. Pre-change nothing in the daemon fired on
    /// a worker finishing, so this entry sat at <c>Working</c> forever.
    /// </summary>
    [Fact]
    public async Task AWorkerWhoseBranchGoesQuiet_IsVerified_WithNoHumanPressingVerify()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");
        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-a");

        rig.Advance(TimeSpan.FromSeconds(120));
        var fired = Only(rig.Sweep(), "w-1");

        Assert.Equal(ReadinessOutcome.Fired, fired.Outcome);
        await rig.Trigger.LastRun;

        // Observed as the queue's own state walk, driven by RunVerificationAsync — not as a call count on
        // some seam of the trigger's own.
        Assert.Equal(WorkerMergeState.Verified, rig.Queue.GetState("w-1"));
        Assert.Equal(1, rig.RunCountFor("w-1"));
    }

    /// <summary>A failing suite still settles through the SAME method — as
    /// <see cref="WorkerMergeState.VerificationFailed"/> since H2, surfaced, never silently retried. The
    /// trigger owns no part of that decision.</summary>
    [Fact]
    public async Task AFailingRun_SettlesThroughTheQueue_AsVerificationFailed()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");
        rig.FailRunFor("w-1");
        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-a");
        rig.Advance(TimeSpan.FromSeconds(120));

        Assert.Equal(ReadinessOutcome.Fired, Only(rig.Sweep(), "w-1").Outcome);
        await rig.Trigger.LastRun;

        // The run count is asserted alongside the state, and it used to be the load-bearing half precisely
        // because the destination WAS the default state: a trigger that quietly ran nothing would leave the
        // entry at Working too, and the assertion measured nothing. VerificationFailed is reachable only by
        // a run that produced a red record, which is the point of the state.
        Assert.Equal(1, rig.RunCountFor("w-1"));
        Assert.Equal(WorkerMergeState.VerificationFailed, rig.Queue.GetState("w-1"));

        // …and a failing run DID produce a record. This is the control for the refusal test below: the two
        // cases must be distinguishable, or "we could not run your tests" and "your tests failed" collapse.
        Assert.NotNull(rig.VerStore.Latest("repo", "w-1"));
        Assert.False(rig.VerStore.Latest("repo", "w-1")!.Passed);
    }

    /// <summary>
    /// H3 — the daemon logs the OUTCOME, not only that it started.
    ///
    /// <para>A real run produced exactly this shape live: six <c>auto-verify … verifying</c> lines in the
    /// daemon log and not one line saying what any of them found. The red verdict existed only as a row in
    /// SQLite and an artifact file nothing linked to, so the log — the first place anyone looks when a swarm
    /// has gone quiet — recorded six test suites STARTED and none finished. An automatic actor that spends a
    /// test run on a human's behalf and never says what it found is indistinguishable from one that hung.</para>
    ///
    /// <para>The line has to carry three things a human needs and cannot get elsewhere: the verdict, the
    /// command that produced it (so the failure is attributable) and the artifact path (so the output is one
    /// <c>cat</c> away rather than a directory to guess at).</para>
    /// </summary>
    [Fact]
    public async Task AFailedRun_LogsTheOutcome_WithTheVerdictAndTheArtifact()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");
        rig.FailRunFor("w-1");
        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-a");
        rig.Advance(TimeSpan.FromSeconds(120));
        rig.Sweep();
        await rig.Trigger.LastRun;

        var outcome = Assert.Single(rig.Log, l => l.Contains("FAILED", StringComparison.Ordinal));
        Assert.Contains("w-1", outcome);
        Assert.Contains("npm test", outcome);        // what ran
        Assert.Contains("log.txt", outcome);         // where the output is
        Assert.Contains("VerificationFailed", outcome); // where the entry ended up
        Assert.DoesNotContain("PASSED", outcome);
    }

    /// <summary>The paired positive: a PASS is logged too. A log that only spoke up on failures would be a
    /// log a reader could not trust the silence of.</summary>
    [Fact]
    public async Task APassingRun_AlsoLogsItsOutcome()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");
        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-a");
        rig.Advance(TimeSpan.FromSeconds(120));
        rig.Sweep();
        await rig.Trigger.LastRun;

        var outcome = Assert.Single(rig.Log, l => l.Contains("PASSED", StringComparison.Ordinal));
        Assert.Contains("Verified", outcome);
        Assert.DoesNotContain("FAILED", outcome);
    }

    /// <summary>
    /// H2's other half for the automatic path: an entry left at <c>VerificationFailed</c> is verified again
    /// when the agent pushes a FIX — and not before.
    ///
    /// <para>This has to work, or the new state would be a trap. Nothing in the daemon calls
    /// <c>MergeQueue.NotifyNewCommits</c> for a locally-spawned agent, so a push does not walk the entry
    /// back to <c>Working</c> on its own; without <c>VerificationFailed</c> in this trigger's eligible set,
    /// a worker that repaired its own branch would sit red forever and only a human clicking Verify would
    /// ever find out. The once-per-tip bound is what keeps it from being a retry loop: the SAME tip is never
    /// re-attempted, so this fires only for work the agent actually pushed after the failure.</para>
    /// </summary>
    [Fact]
    public async Task AFailedEntry_IsReVerified_WhenTheAgentPushesAFix_ButNotOnTheSameTip()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");
        rig.FailRunFor("w-1");
        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-a");
        rig.Advance(TimeSpan.FromSeconds(120));
        rig.Sweep();
        await rig.Trigger.LastRun;
        Assert.Equal(WorkerMergeState.VerificationFailed, rig.Queue.GetState("w-1"));

        // Re-announcing the SAME tip changes nothing — the failure is not re-run on a loop.
        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-a");
        rig.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(ReadinessOutcome.AlreadyAttempted, Only(rig.Sweep(), "w-1").Outcome);
        Assert.Equal(1, rig.RunCountFor("w-1"));

        // The agent pushes a fix. THAT is a new tip, and it is verified. (Past the cooldown, which is the
        // OTHER bound and is not what this test is about.)
        rig.PassRunFor("w-1");
        rig.Advance(TimeSpan.FromHours(1));
        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-b");
        rig.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(ReadinessOutcome.Fired, Only(rig.Sweep(), "w-1").Outcome);
        await rig.Trigger.LastRun;

        Assert.Equal(WorkerMergeState.Verified, rig.Queue.GetState("w-1"));
        Assert.Equal(2, rig.RunCountFor("w-1"));
    }

    /// <summary>
    /// <b>A refusal must never become a result.</b> When the verification is REFUSED rather than run — no
    /// live jail, a drifted branch, a missing toolchain, or (PR #322) a verify command whose shell operators
    /// survived tokenisation and now raises <c>MalformedVerificationCommandException</c> — the run never
    /// happened, so no <c>VerificationRecord</c> may exist for it. A record saying <c>Passed=false</c> would
    /// report "this branch's tests failed" about a suite that was never executed, and that distinction is
    /// the one the merge decision rests on. An automatic trigger that swallowed the exception and marked the
    /// entry failed would reintroduce exactly the defect #322 fixes, silently, for a run nobody asked for.
    ///
    /// <para>The trigger is structurally incapable of causing it — it holds no verification store and
    /// touches no queue state — but "structurally incapable" is a claim, and this is the test that makes it
    /// one that can fail. The other half is that an automatic trigger must not RETRY a deterministic refusal
    /// in a loop: the same tip is never re-attempted, so a malformed verify command costs one refusal rather
    /// than one per sweep, and the fix for it (a commit) is what re-arms the branch.</para>
    /// </summary>
    [Fact]
    public async Task ARefusedVerification_RecordsNothing_AndIsNotRetriedOnTheSameTip()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");
        rig.RefuseRunFor("w-1", new InvalidOperationException(
            "the verification command for 'w-1' contains shell operators that survived tokenisation"));
        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-a");
        rig.Advance(TimeSpan.FromSeconds(120));

        Assert.Equal(ReadinessOutcome.Fired, Only(rig.Sweep(), "w-1").Outcome);
        await rig.Trigger.LastRun; // the trigger swallows the throw; it must not translate it

        // No record at all — not a passing one, and emphatically not a failing one.
        Assert.Null(rig.VerStore.Latest("repo", "w-1"));

        // The entry is back where it can be acted on, and the refusal did not masquerade as a verdict:
        // Working — NOT VerificationFailed. A refusal is not a verdict, and H2's new state is reachable
        // only from the arm that has a red record in hand.
        Assert.Equal(WorkerMergeState.Working, rig.Queue.GetState("w-1"));
        Assert.False(rig.Queue.CanMerge("w-1", out var reason));

        // …and the refusal is what the entry SAYS. This assertion used to read
        // `Assert.Equal("not verified yet", reason)`, which is the sentence for a branch nobody has asked
        // anything of — so the one thing this whole test exists to make visible, a malformed verify
        // command that cost a real attempt, was legible only in the daemon log. It is still not shaped
        // like a test result, which is the property the paragraph above is about: nothing here says
        // failed, or tests, or a command that ran.
        Assert.Equal(
            "the verification command for 'w-1' contains shell operators that survived tokenisation",
            reason);
        Assert.DoesNotContain("not verified yet", reason);

        // …and the refusal is not retried: a deterministic refusal must cost one attempt, not one per
        // sweep, forever. Nothing is armed, and re-announcing the very same tip does not change that.
        rig.Advance(TimeSpan.FromHours(1));
        Assert.Empty(rig.Sweep());

        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-a");
        rig.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(ReadinessOutcome.AlreadyAttempted, Only(rig.Sweep(), "w-1").Outcome);
        Assert.Equal(1, rig.RunCountFor("w-1"));
    }

    // ---- "Ready" is quiescence, not movement -----------------------------

    /// <summary>
    /// The multi-commit worker — the case a per-commit trigger gets wrong and burns a whole test suite on
    /// every intermediate commit. Five advances inside the quiet period cost ONE verification, and the
    /// window is measured from the LAST advance (a trigger that kept the first timestamp would fire
    /// mid-burst, which is the per-commit behaviour wearing a debounce's clothes).
    /// </summary>
    [Fact]
    public async Task FiveCommitsInsideTheQuietPeriod_CostOneVerification_MeasuredFromTheLastOne()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");

        foreach (var tip in new[] { "tip-a", "tip-b", "tip-c", "tip-d", "tip-e" })
        {
            rig.Trigger.NotifyAdvanced("repo", "w-1", tip);
            rig.Advance(TimeSpan.FromSeconds(60)); // under the 90s window, every time
            Assert.Equal(ReadinessOutcome.Quiescing, Only(rig.Sweep(), "w-1").Outcome);
        }

        Assert.Equal(0, rig.RunCountFor("w-1"));

        // 60s after the last commit is still not quiet; 91s is.
        rig.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(ReadinessOutcome.Fired, Only(rig.Sweep(), "w-1").Outcome);
        await rig.Trigger.LastRun;

        Assert.Equal(1, rig.RunCountFor("w-1"));
        Assert.Equal(WorkerMergeState.Verified, rig.Queue.GetState("w-1"));
    }

    /// <summary>
    /// The unbounded-loop bound, half one: a tip this trigger has already attempted is never attempted
    /// again, however many times the sweep re-examines it. Producing N automatic runs therefore costs the
    /// agent N distinct commits.
    /// </summary>
    [Fact]
    public async Task TheSameTipIsNeverAutoVerifiedTwice()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");
        // A failing suite settles the entry at VerificationFailed, which is what keeps this test about the
        // once-per-tip rule: staling the entry instead would fire the queue's own re-verify cascade and
        // count a run this trigger never asked for. Both Working and VerificationFailed are states the
        // trigger CAN fire from, so the refusal below is the tip rule and nothing else.
        rig.FailRunFor("w-1");
        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-a");
        rig.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(ReadinessOutcome.Fired, Only(rig.Sweep(), "w-1").Outcome);
        await rig.Trigger.LastRun;
        Assert.Equal(WorkerMergeState.VerificationFailed, rig.Queue.GetState("w-1"));

        // The cooldown is long past and the very same tip is re-announced — the shape a re-published mirror
        // or a restarted watcher produces.
        rig.Advance(TimeSpan.FromHours(1));
        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-a");
        rig.Advance(TimeSpan.FromSeconds(120));

        Assert.Equal(ReadinessOutcome.AlreadyAttempted, Only(rig.Sweep(), "w-1").Outcome);
        Assert.Equal(1, rig.RunCountFor("w-1"));
    }

    /// <summary>
    /// The unbounded-loop bound, half two: a grinder committing just under the quiet period, forever, is
    /// held off by the cooldown instead of running the repo's suite as fast as the suite completes. The
    /// entry stays armed — this is a "not yet", not a "no".
    /// </summary>
    [Fact]
    public async Task AGrinderIsHeldOffByTheCooldown_AndStaysArmed()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");
        // Settles at VerificationFailed — a state the trigger fires from just as it fires from Working —
        // so the COOLDOWN is the only thing that can refuse the next tip.
        rig.FailRunFor("w-1");
        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-a");
        rig.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(ReadinessOutcome.Fired, Only(rig.Sweep(), "w-1").Outcome);
        await rig.Trigger.LastRun;
        Assert.Equal(WorkerMergeState.VerificationFailed, rig.Queue.GetState("w-1"));

        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-b");
        rig.Advance(TimeSpan.FromSeconds(120));
        var deferred = Only(rig.Sweep(), "w-1");
        Assert.Equal(ReadinessOutcome.Deferred, deferred.Outcome);
        Assert.Contains("cooldown", deferred.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, rig.RunCountFor("w-1"));
        Assert.Contains(("repo", "w-1"), rig.Trigger.Armed);

        // …and it is a delay, not a drop: once the cooldown expires the armed tip verifies.
        rig.Advance(TimeSpan.FromSeconds(600));
        Assert.Equal(ReadinessOutcome.Fired, Only(rig.Sweep(), "w-1").Outcome);
        await rig.Trigger.LastRun;
        Assert.Equal(2, rig.RunCountFor("w-1"));
    }

    // ---- It never fires for work nobody approved -------------------------

    /// <summary>A worker still at the plan gate never auto-verifies — the same authority
    /// (<see cref="WorkerPlanGate.MayAutoVerify"/>) that withholds its task, asked again here.</summary>
    [Fact]
    public void AWorkerWhosePlanWasNeverApproved_NeverAutoVerifies()
    {
        using var rig = new Rig();
        rig.PendingWorker("w-1");
        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-a");
        rig.Advance(TimeSpan.FromSeconds(120));

        var decision = Only(rig.Sweep(), "w-1");
        Assert.Equal(ReadinessOutcome.Ineligible, decision.Outcome);
        Assert.Contains("waiting on your approval", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, rig.RunCountFor("w-1"));
        Assert.Equal(WorkerMergeState.Working, rig.Queue.GetState("w-1"));
    }

    /// <summary>
    /// An agent the plan gate never held — a manual-mode agent, an external-PR head — is not governed by
    /// this trigger at all. The merge gate's own default answers "allowed" for these ids on purpose; reading
    /// THAT here would start spending suite runs on every agent in the daemon, including the ones a human is
    /// driving by hand.
    /// </summary>
    [Fact]
    public void AnAgentThePlanGateNeverHeld_IsNotAutoVerified()
    {
        using var rig = new Rig();
        rig.Trigger.NotifyAdvanced("repo", "manual-1", "tip-a");
        rig.Advance(TimeSpan.FromSeconds(120));

        var decision = Only(rig.Sweep(), "manual-1");
        Assert.Equal(ReadinessOutcome.Ineligible, decision.Outcome);
        Assert.Contains("not a plan-gated worker", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, rig.RunCountFor("manual-1"));

        // …and the merge gate's answer for that same id is still "allowed", which is what makes this a
        // deliberate distinction rather than an accident of one shared predicate.
        Assert.True(rig.Gate.Allows("manual-1", out _));
    }

    // ---- It never fights the in-flight guard -----------------------------

    /// <summary>
    /// <see cref="MergeQueue.RunVerificationAsync"/> throws when a run is already up for an entry. The
    /// trigger defers instead of calling — and the entry stays armed, so the work is not lost.
    /// </summary>
    [Fact]
    public async Task AVerificationAlreadyInFlight_IsDeferred_NotFoughtOver()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");
        rig.BlockRunFor("w-1");

        // Somebody else (the Verify button) starts a run that will not finish yet.
        var human = rig.Queue.RunVerificationAsync("w-1", CancellationToken.None);
        Assert.True(rig.Queue.IsVerificationInFlight("w-1"));

        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-a");
        rig.Advance(TimeSpan.FromSeconds(120));
        var decision = Only(rig.Sweep(), "w-1");

        Assert.Equal(ReadinessOutcome.Deferred, decision.Outcome);
        Assert.Equal(1, rig.RunCountFor("w-1")); // the human's run, and only it
        Assert.Contains(("repo", "w-1"), rig.Trigger.Armed);

        rig.ReleaseBlockedRun();
        await human;
        Assert.Equal(WorkerMergeState.Verified, rig.Queue.GetState("w-1"));
    }

    /// <summary>
    /// A state with no legal edge to <c>Verifying</c> is left alone rather than walked there. A worker that
    /// pushes while its entry is already <c>Verified</c> is the live example: the state machine has no
    /// <c>Verified → Verifying</c> transition, and a trigger that reached for one would be editing the merge
    /// spine instead of triggering it.
    /// </summary>
    [Fact]
    public async Task AnEntryInAStateVerificationCannotStartFrom_IsLeftAlone()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");
        await rig.Queue.RunVerificationAsync("w-1", CancellationToken.None);
        Assert.Equal(WorkerMergeState.Verified, rig.Queue.GetState("w-1"));

        rig.Trigger.NotifyAdvanced("repo", "w-1", "tip-a");
        rig.Advance(TimeSpan.FromSeconds(120));
        var decision = Only(rig.Sweep(), "w-1");

        Assert.Equal(ReadinessOutcome.Ineligible, decision.Outcome);
        Assert.Contains("Verified", decision.Reason, StringComparison.Ordinal);
        Assert.Equal(1, rig.RunCountFor("w-1"));
        Assert.Equal(WorkerMergeState.Verified, rig.Queue.GetState("w-1"));
    }

    /// <summary>A repo with no live queue is skipped rather than provisioned: an agent pushing must not be
    /// able to bring a merge queue into existence as a side effect.</summary>
    [Fact]
    public void ARepoWithNoLiveQueue_IsSkipped_NotCreated()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");
        rig.Trigger.NotifyAdvanced("other-repo", "w-1", "tip-a");
        rig.Advance(TimeSpan.FromSeconds(120));

        var decision = Only(rig.Sweep(), "w-1");
        Assert.Equal(ReadinessOutcome.Ineligible, decision.Outcome);
        Assert.Contains("no active merge queue", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(rig.Registry.Resolve("other-repo"));
    }
}
