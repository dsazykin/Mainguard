using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Git.Audit;
using Mainguard.Git.Review;
using Microsoft.EntityFrameworkCore;
using Xunit;
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Tests;

/// <summary>
/// The regression suite for the defect that <b>a <c>Verified</c> row froze forever</b>, and therefore
/// invited a human to merge code the product told them was verified.
///
/// <h3>What was observed, with ground truth</h3>
///
/// <para>2026-08-30, agent <c>4c43d17a</c> (daemon DB rows 50–52 and <c>~/.mainguard/logs</c>):</para>
/// <list type="bullet">
///   <item>01:35:12 — auto-verify PASSED against <c>main@ffbc3bc7</c>; the entry settled to
///   <c>Verified</c> and its row never moved again.</item>
///   <item>01:41:20, 01:59:28, 02:13:29 — three further <c>commit_work</c> ops from the same worker. Not
///   one of them produced a verification, because <c>WorkerReadinessTrigger</c> only starts runs from
///   <c>Working</c>/<c>StaleVerified</c>/<c>VerificationFailed</c> and nothing walked the entry out of
///   <c>Verified</c>: <c>MergeQueue.NotifyNewCommits</c> had two callers, an upstream-PR poll and the dev
///   seeder, and neither fires for a worker in a jail.</item>
///   <item>02:18:33 — the human pressed the Verify button the rail was still offering:
///   <c>RunVerification refused … Illegal merge-state transition Verified → Verifying</c>. It had never
///   been capable of anything else from that state.</item>
///   <item>Throughout — <c>ArmFlaggedChangeReview</c> runs only inside a verification, so the F6
///   out-of-scope gate stayed armed against the diff of two commits earlier. The cockpit listed the old
///   files, stamped the header "verified", footed "ready to merge", and left <b>Merge enabled</b>, for a
///   tip carrying an out-of-scope change and arithmetic that fails the repo's own tests.</item>
/// </list>
///
/// <h3>The state model these tests pin</h3>
///
/// <para>The agent's own commits do not make a verdict <i>stale</i>; they make it <i>void</i>.
/// <c>StaleVerified</c> is the co-tenant case — main moved, the branch's bytes did not, so the record and
/// every acknowledgment bound to them are still true statements about this tree and a rebase-and-re-run
/// reproduces the green. When the AGENT's branch moves there is no tree left to be right about: a
/// different diff, a different flagged set, void acks, a scope verdict computed from bytes nobody will
/// merge. <c>Working</c> already means "no evidence about this branch", <c>Verified → Working</c> was
/// already a legal edge documented as "new commits from the agent invalidate", and
/// <see cref="MergeQueue.NotifyNewCommits"/> already implemented it. So no state and no edge were added —
/// only the missing caller (<see cref="BranchTipInvalidator"/>), off the ref-watcher sweep the readiness
/// trigger was already riding.</para>
/// </summary>
public class VerifiedFreezeTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    /// <summary>
    /// The whole loop, with nothing simulated between the ref watcher and the merge gate: a real
    /// <see cref="MergeQueue"/>, a real <see cref="BranchTipInvalidator"/> and a real
    /// <see cref="WorkerReadinessTrigger"/>, both subscribed to one real <see cref="AgentRefWatcher"/>.
    ///
    /// <para>The only thing faked is the jail: <c>runVerification</c> returns a record pinned to whatever
    /// tip <see cref="Tip"/> currently holds — which is exactly what the provisioner does when it resolves
    /// <c>agent/&lt;id&gt;</c> out of the mirror after publishing.</para>
    /// </summary>
    private sealed class Rig : IDisposable
    {
        public DateTimeOffset Now = T0;
        public readonly InMemoryAuditLog Audit = new();
        public readonly PlanApprovalService Plans;
        public readonly WorkerPlanGate PlanGate;
        public readonly MergeQueueRegistry Registry = new();
        public readonly MergeQueue Queue;
        public readonly AgentRefWatcher Watcher;
        public readonly WorkerReadinessTrigger Trigger;
        public readonly BranchTipInvalidator Invalidator;
        public readonly FlaggedChangeGate Flagged = new();
        public readonly List<string> Runs = new();
        public readonly List<string> Log = new();

        /// <summary>The mirror's <c>agent/&lt;id&gt;</c> tip, per agent, as the rig's git stand-in.</summary>
        public readonly Dictionary<string, string> Tip = new(StringComparer.Ordinal);

        /// <summary>What <see cref="ArmFlagged"/> will put in the gate on the NEXT run, per agent — the
        /// stand-in for what the detector finds in the branch's diff at that tip.</summary>
        public readonly Dictionary<string, IReadOnlyList<FlaggedChange>> FlaggedAtTip =
            new(StringComparer.Ordinal);

        private readonly HashSet<string> _fails = new(StringComparer.Ordinal);
        private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _hold = new();
        private Action? _duringRun;

        public Rig(bool withFlaggedGate = false)
        {
            Plans = new PlanApprovalService(audit: Audit);
            PlanGate = new WorkerPlanGate(Plans, Audit);

            MergeQueue queue = null!;
            queue = new MergeQueue(
                repoHash: "repo",
                currentMainSha: "main0",
                store: new InMemoryMergeQueueStore(),
                verifications: new InMemoryVerificationStore(),
                runVerification: async (id, ct) =>
                {
                    lock (Runs) { Runs.Add(id); }

                    // The provisioner arms both gates from the committed trees BEFORE the run, inside this
                    // same call. Mirrored here so the CADENCE — the thing that was wrong — is what the
                    // tests exercise, rather than a hand-driven gate that would arm whenever a test said so.
                    ArmFlagged(id);

                    // A hook for "the agent committed while its own tests were running".
                    _duringRun?.Invoke();

                    // …and one for "this entry really is mid-run", which is the only honest way to reach
                    // the Verifying state: a row that merely SAYS verifying is a different fact.
                    if (_blocked.Contains(id))
                    {
                        await _hold.Task.WaitAsync(ct).ConfigureAwait(false);
                    }

                    return new VerificationRecord(
                        id, queue.CurrentMainSha, !_fails.Contains(id), "log.txt", "npm test", "cfg",
                        Now, TipOf(id));
                },
                requeue: null,
                gates: withFlaggedGate ? new IMergeGate[] { Flagged } : Array.Empty<IMergeGate>(),
                audit: Audit,
                clock: () => Now);
            Queue = queue;
            Registry.Register("repo", new MergeQueueContext(Queue, new InMemoryMergeLeaseStore()));

            // A real watcher built to run no loop of its own — the rig drives both subscribers through
            // Advance(). The watcher→subscriber edge over real git is proven in Mainguard.Server.Tests.
            Watcher = new AgentRefWatcher(
                new AgentRefMediator(
                    new AgentRepoManager(System.IO.Path.GetTempPath()), _ => System.IO.Path.GetTempPath()),
                new AgentRepoManager(System.IO.Path.GetTempPath()),
                AgentRefWatcher.DriveManually);

            Trigger = new WorkerReadinessTrigger(
                source: Watcher, queues: Registry, planGate: PlanGate,
                limits: new CoordinatorLimits(), sweepInterval: WorkerReadinessTrigger.DriveManually,
                clock: () => Now, log: line => { lock (Log) { Log.Add(line); } });

            Invalidator = new BranchTipInvalidator(
                source: Watcher, queues: Registry, log: line => { lock (Log) { Log.Add(line); } });
        }

        public string TipOf(string id) => Tip.TryGetValue(id, out var t) ? t : string.Empty;

        private void ArmFlagged(string id) =>
            Flagged.StoreFor(id).SetFlagged(
                FlaggedAtTip.TryGetValue(id, out var items) ? items : Array.Empty<FlaggedChange>());

        /// <summary>A delegated worker whose plan a human approved (the auto-verify authorization).</summary>
        public void ApprovedWorker(string id)
        {
            PlanGate.Hold(id, "coord", "Fix the clock", "do the work", 1m);
            var planId = Plans.Present(
                id, "coord", "Fix the clock", new TaskPlanFields(new[] { "src/a.cs" }, "how", "tests"),
                "", 1m).PlanId!;
            Plans.Approve(planId, "tester");
        }

        /// <summary>
        /// The agent pushed. Moves the rig's mirror tip and delivers the observation to BOTH subscribers,
        /// in the order the real watcher raises it — one event, two listeners.
        /// </summary>
        public void Advance(string id, string newTip)
        {
            Tip[id] = newTip;
            Invalidator.NotifyAdvanced("repo", id, newTip);
            Trigger.NotifyAdvanced("repo", id, newTip);
        }

        public void FailRunFor(string id) => _fails.Add(id);

        /// <summary>Parks the next run for this agent so the entry genuinely sits at <c>Verifying</c>.</summary>
        public void BlockRunFor(string id) => _blocked.Add(id);

        public void PassRunFor(string id) => _fails.Remove(id);

        /// <summary>Runs <paramref name="action"/> in the middle of the next verification.</summary>
        public void DuringNextRun(Action action) => _duringRun = () => { _duringRun = null; action(); };

        public void Wait(TimeSpan by) => Now += by;

        public IReadOnlyList<ReadinessDecision> Sweep() => Trigger.PollOnce();

        public int RunCountFor(string id)
        {
            lock (Runs) { return Runs.Count(r => r == id); }
        }

        /// <summary>
        /// Drives one full auto-verification: quiet period, sweep, await the run.
        ///
        /// <para>The wait clears the per-worker COOLDOWN as well as the quiet period. The cooldown is a
        /// real bound and it has its own tests; a suite about invalidation that tripped over it would be
        /// asserting the wrong thing, and would say "Deferred" where it means "did not re-verify".</para>
        /// </summary>
        public async Task<ReadinessOutcome> AutoVerifyAsync(string id)
        {
            Wait(TimeSpan.FromHours(1));
            var decision = Assert.Single(Sweep(), d => d.AgentId == id);
            await Trigger.LastRun;
            return decision.Outcome;
        }

        public void Dispose()
        {
            // Release anything parked, or a blocked run would outlive the test that started it.
            _hold.TrySetResult();
            Invalidator.Dispose();
            Trigger.Dispose();
            Watcher.Dispose();
        }
    }

    // ---- The test that would have caught it ------------------------------

    /// <summary>
    /// <b>The one.</b> A <c>Verified</c> row that receives a new commit must not remain mergeable.
    ///
    /// <para>Every assertion here is on the merge DECISION rather than on the state word, because the
    /// state word was never the harm: a green badge is embarrassing, an enabled Merge button on untested
    /// code is the thing that ships. Before the fix this test fails on its very first post-push assertion
    /// — <c>CanMerge</c> answered true, with reason <c>""</c>, for a tip nothing had run against.</para>
    /// </summary>
    [Fact]
    public async Task AVerifiedEntry_ThatReceivesANewCommit_IsNoLongerMergeable()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");
        rig.Advance("w-1", "tip-a");
        Assert.Equal(ReadinessOutcome.Fired, await rig.AutoVerifyAsync("w-1"));

        Assert.Equal(WorkerMergeState.Verified, rig.Queue.GetState("w-1"));
        Assert.True(rig.Queue.CanMerge("w-1", out _), "a fresh green entry should be mergeable");

        // The worker commits again. This is the exact moment the product used to stop telling the truth.
        rig.Advance("w-1", "tip-b");

        Assert.False(rig.Queue.CanMerge("w-1", out var reason));
        Assert.Equal(MergeQueue.BranchMovedReason, reason);
        Assert.Equal(WorkerMergeState.Working, rig.Queue.GetState("w-1"));

        // …and the evidence is GONE, not merely outranked. A record left standing is what the cockpit
        // renders its "verified @" stamp and its verdict line from, so a row that says Working while
        // still carrying a pass is the same lie wearing a different word.
        Assert.Null(rig.Queue.LastVerification("w-1"));
    }

    /// <summary>
    /// The second half of the same guarantee: the entry does not merely stop being mergeable, it
    /// RE-VERIFIES on its own — the automatic path, with nobody pressing anything.
    ///
    /// <para>This is what the freeze cost in practice. Agent <c>4c43d17a</c> sat green for 40 minutes and
    /// three commits; the contrast in the same log is agent <c>9b4a546f</c>, which went red at 02:23 and
    /// was re-verified at 02:33 the instant it pushed a fix — because <c>VerificationFailed</c> IS in the
    /// trigger's eligible set. Recovery worked for the failing branch and not for the passing one.</para>
    /// </summary>
    [Fact]
    public async Task AVerifiedEntry_ThatReceivesANewCommit_ReVerifies_AgainstTheNewTip()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");
        rig.Advance("w-1", "tip-a");
        await rig.AutoVerifyAsync("w-1");
        Assert.Equal(1, rig.RunCountFor("w-1"));

        rig.Advance("w-1", "tip-b");
        Assert.Equal(ReadinessOutcome.Fired, await rig.AutoVerifyAsync("w-1"));

        Assert.Equal(2, rig.RunCountFor("w-1"));
        Assert.Equal(WorkerMergeState.Verified, rig.Queue.GetState("w-1"));

        // Verified against the tip that exists, not the one that did.
        Assert.Equal("tip-b", rig.Queue.LastVerification("w-1")!.BranchSha);
        Assert.True(rig.Queue.CanMerge("w-1", out _));
    }

    /// <summary>
    /// The same walk when the new commit makes the branch RED. The freeze's worst shape was not "green
    /// goes stale" but "green outlives code that no longer passes": the observed branch's arithmetic
    /// failed the repo's own tests at the tip the human was being offered.
    /// </summary>
    [Fact]
    public async Task AVerifiedEntry_WhoseNewCommitBreaksTheTests_EndsRed_NotGreen()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");
        rig.Advance("w-1", "tip-a");
        await rig.AutoVerifyAsync("w-1");
        Assert.Equal(WorkerMergeState.Verified, rig.Queue.GetState("w-1"));

        rig.FailRunFor("w-1");
        rig.Advance("w-1", "tip-b");
        await rig.AutoVerifyAsync("w-1");

        Assert.Equal(WorkerMergeState.VerificationFailed, rig.Queue.GetState("w-1"));
        Assert.False(rig.Queue.CanMerge("w-1", out var reason));
        Assert.Contains("FAILED", reason, StringComparison.Ordinal);
    }

    // ---- The gate that does not depend on an event firing -----------------

    /// <summary>
    /// The branch moved <b>while its own tests were running</b>. The verdict is true and it is about a
    /// tree nobody is going to merge, so it must not be promoted to this entry's standing evidence —
    /// settling <c>Verified</c> here would be the freeze again, one run later and with a fresher
    /// timestamp on it.
    ///
    /// <para>This is also the one reachable exercise of the branch-side compare in <c>CanMerge</c>: the
    /// invalidator cannot act on a <c>Verifying</c> entry (the run owns it), so the settle is what has to
    /// notice.</para>
    /// </summary>
    [Fact]
    public async Task AVerificationOvertakenMidRun_DoesNotBecomeAGreen()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");
        rig.Advance("w-1", "tip-a");

        // The agent commits between the run starting and the record coming back. The entry is Verifying,
        // so the invalidator records the tip and deliberately moves nothing.
        rig.DuringNextRun(() => rig.Invalidator.NotifyAdvanced("repo", "w-1", "tip-b"));

        rig.Wait(TimeSpan.FromSeconds(120));
        rig.Sweep();
        await rig.Trigger.LastRun;

        Assert.Equal(WorkerMergeState.Working, rig.Queue.GetState("w-1"));
        Assert.False(rig.Queue.CanMerge("w-1", out var reason));
        Assert.Equal(MergeQueue.BranchMovedReason, reason);

        // The record itself is still history — immutability is not negotiable — it is simply not this
        // entry's evidence any more.
        Assert.Null(rig.Queue.LastVerification("w-1"));
        Assert.Equal("tip-b", rig.Queue.ObservedBranchTip("w-1"));
    }

    /// <summary>
    /// The other direction, and the one a defensive check gets wrong: a re-verification after the branch
    /// moved with NO announcement must NOT be refused.
    ///
    /// <para>The stale cascade rebases a branch and then re-runs its tests, and a rebase reaches the mirror
    /// without ever raising <c>Advanced</c> — so the queue legitimately measures a tip it has never been
    /// told about. If the settle did not advance the queue's known tip to the one the run measured, the
    /// branch-side compare in <c>CanMerge</c> would see the pre-rebase tip against a post-rebase record and
    /// refuse forever: a <c>Verified</c> entry, green by every other check, permanently unmergeable with
    /// nothing anywhere saying why. That is the same shape as the un-reparented-branch loop
    /// <c>TryReturnToWorking</c> exists to break, and it is what a freshness gate costs when it answers a
    /// question it does not have the facts for.</para>
    /// </summary>
    [Fact]
    public async Task ARebasedBranchThatWasNeverAnnounced_IsStillMergeableAfterItReVerifies()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");
        rig.Advance("w-1", "tip-a");
        await rig.AutoVerifyAsync("w-1");
        Assert.Equal("tip-a", rig.Queue.ObservedBranchTip("w-1"));

        // A co-tenant merges; the cascade rebases this branch onto the new main. The rebase moves the
        // mirror ref without the sweep announcing it — nothing calls NotifyBranchAdvanced.
        rig.Queue.NotifyMainMoved("main1");
        Assert.Equal(WorkerMergeState.StaleVerified, rig.Queue.GetState("w-1"));
        rig.Tip["w-1"] = "tip-a-rebased";

        await rig.Queue.RunVerificationAsync("w-1", CancellationToken.None);

        Assert.Equal(WorkerMergeState.Verified, rig.Queue.GetState("w-1"));
        Assert.True(
            rig.Queue.CanMerge("w-1", out var reason),
            $"a rebased-and-re-verified branch was refused: {reason}");
        Assert.Equal("tip-a-rebased", rig.Queue.ObservedBranchTip("w-1"));
    }

    /// <summary>
    /// <b>The structural pairing.</b> Every state <see cref="MergeQueue.CanMerge"/> can answer TRUE from
    /// must be a state an advanced branch invalidates.
    ///
    /// <para>The freeze was exactly a violation of this: <c>Verified</c> could merge and nothing
    /// invalidated it. The pair is maintained in two different places — <c>CanMergeLocked</c>'s admit set
    /// and <c>NotifyBranchAdvanced</c>'s demote set — so this asserts the relation itself rather than
    /// either list, and it goes red if a future state joins one without joining the other.</para>
    /// </summary>
    [Theory]
    [InlineData(WorkerMergeState.Working)]
    [InlineData(WorkerMergeState.Verifying)]
    [InlineData(WorkerMergeState.Verified)]
    [InlineData(WorkerMergeState.StaleVerified)]
    [InlineData(WorkerMergeState.AwaitingReview)]
    [InlineData(WorkerMergeState.VerificationFailed)]
    [InlineData(WorkerMergeState.Merged)]
    [InlineData(WorkerMergeState.Rejected)]
    [InlineData(WorkerMergeState.Discarded)]
    public async Task EveryStateThatCanMerge_IsAStateAnAdvancedBranchInvalidates(WorkerMergeState state)
    {
        using var rig = new Rig();
        await DriveToAsync(rig, "w-1", state);
        Assert.Equal(state, rig.Queue.GetState("w-1"));

        if (!rig.Queue.CanMerge("w-1", out _))
        {
            return; // Nothing to protect: this state cannot hand anyone a merge in the first place.
        }

        rig.Invalidator.NotifyAdvanced("repo", "w-1", "tip-moved");

        Assert.False(
            rig.Queue.CanMerge("w-1", out _),
            $"a branch that moved is still mergeable from {state} — CanMerge admits a state "
            + "NotifyBranchAdvanced does not invalidate");
    }

    /// <summary>Puts an entry into a requested state through the queue's own public transitions.</summary>
    private static async Task DriveToAsync(Rig rig, string id, WorkerMergeState target)
    {
        rig.ApprovedWorker(id);
        rig.Tip[id] = "tip-a";

        switch (target)
        {
            case WorkerMergeState.Working:
                break;
            case WorkerMergeState.Verifying:
                // A REAL in-flight run, parked. The rig releases it on dispose.
                rig.BlockRunFor(id);
                _ = rig.Queue.RunVerificationAsync(id, CancellationToken.None);
                break;
            case WorkerMergeState.Verified:
                await rig.Queue.RunVerificationAsync(id, CancellationToken.None);
                break;
            case WorkerMergeState.StaleVerified:
                await rig.Queue.RunVerificationAsync(id, CancellationToken.None);
                rig.Queue.NotifyMainMoved("main1");
                break;
            case WorkerMergeState.AwaitingReview:
                await rig.Queue.RunVerificationAsync(id, CancellationToken.None);
                rig.Queue.RequestReview(id);
                break;
            case WorkerMergeState.VerificationFailed:
                rig.FailRunFor(id);
                await rig.Queue.RunVerificationAsync(id, CancellationToken.None);
                rig.PassRunFor(id);
                break;
            case WorkerMergeState.Merged:
                await rig.Queue.RunVerificationAsync(id, CancellationToken.None);
                rig.Queue.ConfirmHumanMerge(id, "main1");
                break;
            case WorkerMergeState.Rejected:
                await rig.Queue.RunVerificationAsync(id, CancellationToken.None);
                rig.Queue.RequestReview(id);
                rig.Queue.Reject(id);
                break;
            case WorkerMergeState.Discarded:
                // EnsureEntry first: an id this queue has never written a row for is deliberately not
                // discardable, so there would be nothing to drive.
                rig.Queue.EnsureEntry(id, MergeEntryOrigin.Local);
                Assert.True(rig.Queue.TryDiscard(id, "tester", "not wanted", out var refusal), refusal);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, "unhandled state");
        }
    }

    // ---- The gate the freeze disarmed ------------------------------------

    /// <summary>
    /// The flagged-change gate is re-armed against the <b>new</b> diff, and the acknowledgment that
    /// covered the old one is gone.
    ///
    /// <para>This is the second thing the freeze broke, and the more dangerous one.
    /// <c>ArmFlaggedChangeReview</c> runs only inside a verification, and
    /// <c>MergeQueueProvisioner.cs</c> justified that cadence in a comment asserting "a branch that pushes
    /// new work re-verifies, re-classifies, and drops every ack that covered the old bytes". It did not.
    /// A green branch that pushed was never re-verified, so the F6 out-of-scope classification and every
    /// human acknowledgment stayed pinned to a diff two commits old — which means a newly introduced CI
    /// workflow, git hook, executable config or out-of-scope file was never even DETECTED, let alone
    /// acknowledged. The observed branch's <c>src/calc.js</c> was exactly that.</para>
    ///
    /// <para>The arming here happens where it happens in production — inside the run, off the tip — so
    /// what is under test is the cadence, not a hand-driven gate.</para>
    /// </summary>
    [Fact]
    public async Task TheFlaggedChangeGate_IsReArmedAgainstTheNewDiff_AndOldAcksAreDropped()
    {
        using var rig = new Rig(withFlaggedGate: true);
        rig.ApprovedWorker("w-1");

        // Tip A touches one in-scope file, flagged for review; the human reads it and acknowledges.
        var inScope = new FlaggedChange(
            "src/a.cs", RiskCategory.Source, FlaggedKind.OutOfApprovedScope, "hash-a", "outside the approved scope");
        rig.FlaggedAtTip["w-1"] = new[] { inScope };
        rig.Advance("w-1", "tip-a");
        await rig.AutoVerifyAsync("w-1");

        Assert.True(rig.Flagged.StoreFor("w-1").Acknowledge(inScope.Id));
        Assert.True(rig.Queue.CanMerge("w-1", out _), "an acknowledged flagged set should merge");

        // Tip B introduces a DIFFERENT out-of-scope file. Nothing has acknowledged this one.
        var newlyOutOfScope = new FlaggedChange(
            "src/calc.js", RiskCategory.Source, FlaggedKind.OutOfApprovedScope, "hash-b",
            "outside the approved scope");
        rig.FlaggedAtTip["w-1"] = new[] { newlyOutOfScope };
        rig.Advance("w-1", "tip-b");
        await rig.AutoVerifyAsync("w-1");

        Assert.Equal(WorkerMergeState.Verified, rig.Queue.GetState("w-1"));

        // The gate holds the NEW item…
        var item = Assert.Single(rig.Flagged.StoreFor("w-1").Items);
        Assert.Equal("src/calc.js", item.Path);

        // …it is NOT acknowledged (the old ack covered bytes that are gone)…
        Assert.False(rig.Flagged.StoreFor("w-1").IsAcknowledged(newlyOutOfScope.Id));

        // …and the merge is refused because of it, not because of anything else.
        Assert.False(rig.Queue.CanMerge("w-1", out var reason));
        Assert.Contains("acknowledgment", reason, StringComparison.Ordinal);
    }

    // ---- The button that was offered and always failed --------------------

    /// <summary>
    /// …and the rail must therefore stop OFFERING it. The Verify button is enabled for exactly the states
    /// the daemon can start a run from, and for no others.
    ///
    /// <para>It used to be offered on <c>Verified</c> and <c>AwaitingReview</c> too, on a comment's belief
    /// that "RE-verifying against a moved main is the normal way a stale entry gets fresh again" — which
    /// is true, and is about <c>StaleVerified</c>, a state that was already in the set. The two illegal
    /// ones did the same thing every press for the life of the feature: an error message in the row. An
    /// action that is offered and always fails is worse than an absent one, because it reads as the
    /// recovery the human is looking for.</para>
    ///
    /// <para>The states in the enabled set are read from the same fact the daemon decides on — the
    /// <c>Legal</c> transition table's sources for <c>Verifying</c> — so the pairing is asserted rather
    /// than restated.</para>
    /// </summary>
    [Theory]
    [InlineData(WorkerMergeState.Working, true)]
    [InlineData(WorkerMergeState.StaleVerified, true)]
    [InlineData(WorkerMergeState.VerificationFailed, true)]
    [InlineData(WorkerMergeState.Verified, false)]
    [InlineData(WorkerMergeState.AwaitingReview, false)]
    [InlineData(WorkerMergeState.Verifying, false)]
    [InlineData(WorkerMergeState.Merged, false)]
    [InlineData(WorkerMergeState.Rejected, false)]
    [InlineData(WorkerMergeState.Discarded, false)]
    public void TheVerifyButton_IsOfferedOnlyWhereTheDaemonCanActuallyStartARun(
        WorkerMergeState state, bool expected)
    {
        var row = new QueueEntryViewModel("w-1", _ => { }, new StubQueue());
        row.Update(
            new QueueEntry(
                "w-1", "Loom-1", "agent/w-1", state, "", Verification: null,
                FlaggedItems: Array.Empty<FlaggedItem>(), HasLiveSandbox: true),
            new StubQueue());

        Assert.Equal(expected, row.CanVerify);
    }

    // ---- …and it has to survive a restart ---------------------------------

    /// <summary>
    /// The branch sha round-trips through the daemon's SQLite store.
    ///
    /// <para>Without this column every check that reads it silently disables itself at the next daemon
    /// bounce: <c>MergeQueue.Hydrate</c> rebuilds <c>_lastVerification</c> from the store, so a record
    /// that came back with an empty <c>BranchSha</c> is a record the freshness compare declines to answer
    /// about — a gate that works until you restart is not a gate, and it fails in the direction of
    /// allowing the merge.</para>
    ///
    /// <para>The empty case is asserted alongside, because it is the shape of every row written before
    /// this column existed and it must remain readable rather than becoming a null-reference at boot.</para>
    /// </summary>
    [Fact]
    public void TheBranchSha_RoundTripsThroughTheDaemonStore()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Mainguard.Git.AppDbContext>()
            .UseSqlite(connection).Options;
        using (var db = new Mainguard.Git.AppDbContext(options))
        {
            db.Database.EnsureCreated();
        }

        var store = new DbVerificationStore(() => new Mainguard.Git.AppDbContext(options));
        store.Insert("repo", new VerificationRecord(
            "w-1", "main-sha", true, "log.txt", "npm test", "cfg", T0, "branch-tip-sha"));
        store.Insert("repo", new VerificationRecord(
            "w-2", "main-sha", true, "log.txt", "npm test", "cfg", T0));

        Assert.Equal("branch-tip-sha", store.Latest("repo", "w-1")!.BranchSha);
        Assert.Equal(string.Empty, store.Latest("repo", "w-2")!.BranchSha);
    }

    /// <summary>A rail row needs a queue seam; nothing here presses anything, so every call throws.</summary>
    private sealed class StubQueue : IMergeQueueService
    {
        public event Action? Changed;

        public string MainSha => "main0";

        public IReadOnlyList<QueueEntry> GetQueue() => Array.Empty<QueueEntry>();

        public bool CanMerge(string agentId, out string reason)
        {
            reason = "not verified yet";
            return false;
        }

        public Task<VerificationOutcome> RunVerificationAsync(string agentId) =>
            throw new NotSupportedException();

        public Task<VerificationLog> GetVerificationLogAsync(string agentId) =>
            throw new NotSupportedException();

        public Task<MergeOutcome> ConfirmMergeAsync(string agentId) => throw new NotSupportedException();

        public Task AcknowledgeFlaggedChangeAsync(string agentId, string itemId) => Task.CompletedTask;

        public Task<QueueEntryDiscardOutcome> DiscardEntryAsync(string agentId, string reason) =>
            throw new NotSupportedException();

        public Task<QueueEntryRejectOutcome> RejectEntryAsync(string agentId, string reason) =>
            throw new NotSupportedException();

        public Task ClearStalledVerificationAsync(string agentId) => Task.CompletedTask;

        // Not exercised by this fixture: nothing here is parked mid-rebase, and a double that pretended
        // otherwise would let a test pass on a conflict the projection never carried.
        public Task ResolveConflictWithAgentAsync(string agentId) =>
            throw new NotSupportedException("this fixture has no parked rebase conflicts");

        public Task AbortRebaseAsync(string agentId) =>
            throw new NotSupportedException("this fixture has no parked rebase conflicts");

        public Task<QueueEntryResumeOutcome> ResumeEntryAsync(string agentId, string agentKind) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// A verification refused for the state it was asked from is a REFUSAL: no record is written and the
    /// entry does not move. Pinned because the human Verify button's failure mode was exactly this, five
    /// hundred times, and the fix must not have quietly turned it into a result.
    /// </summary>
    [Fact]
    public async Task VerifyFromVerified_IsStillRefused_AndWritesNothing()
    {
        using var rig = new Rig();
        rig.ApprovedWorker("w-1");
        rig.Advance("w-1", "tip-a");
        await rig.AutoVerifyAsync("w-1");

        var before = rig.Queue.LastVerification("w-1");
        var refusal = await Assert.ThrowsAsync<InvalidMergeStateTransitionException>(
            () => rig.Queue.RunVerificationAsync("w-1", CancellationToken.None));

        Assert.Equal(WorkerMergeState.Verified, refusal.From);
        Assert.Equal(WorkerMergeState.Verifying, refusal.To);
        Assert.Equal(WorkerMergeState.Verified, rig.Queue.GetState("w-1"));
        Assert.Same(before, rig.Queue.LastVerification("w-1"));

        // …and it must not leave the in-flight latch set, or the entry reads as permanently busy.
        Assert.False(rig.Queue.IsVerificationInFlight("w-1"));
    }
}
