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
/// P2-10 merge-queue state machine — the densest suite of the milestone (plan §6 tests 1,3,5,6,14 +
/// TI-P2-10 1–6). Exhaustive legal transitions, typed-illegal transitions, the stale cascade + FIFO
/// re-queue, the loud override, immutable records, restart resume, and the NoAutoMergePathExists shape.
/// </summary>
public class MergeQueueStateMachineTests
{
    private sealed class Harness
    {
        public InMemoryMergeQueueStore StateStore = new();
        public InMemoryVerificationStore VerStore = new();
        public InMemoryAuditLog Audit = new();
        public ChangedTestCommandGate ChangedGate = new();
        public List<string> RequeueOrder = new();
        public MergeQueue Queue = null!;
        private long _tick;
        private readonly HashSet<string> _fails = new(StringComparer.Ordinal);

        public void FailFor(string agentId) => _fails.Add(agentId);

        public MergeQueue Build(bool withRequeue = true, bool withChangedGate = false)
        {
            MergeQueue queue = null!;
            Func<string, CancellationToken, Task<VerificationRecord>> run = (id, ct) =>
            {
                var when = DateTimeOffset.UnixEpoch.AddSeconds(Interlocked.Increment(ref _tick));
                var passed = !_fails.Contains(id);
                return Task.FromResult(new VerificationRecord(
                    id, queue.CurrentMainSha, passed, "log.txt", "npm test", "confighash", when));
            };

            // withRequeue: re-verify (records order). Otherwise a no-op requeue so stale states stay put
            // for the gate/override/immutability assertions (production always re-verifies).
            Func<string, CancellationToken, Task> requeue = withRequeue
                ? (id, ct) => { RequeueOrder.Add(id); return queue.RunVerificationAsync(id, ct); }
            : (id, ct) => Task.CompletedTask;

            var gates = withChangedGate ? new IMergeGate[] { ChangedGate } : Array.Empty<IMergeGate>();
            queue = new MergeQueue("repo", "sha0", StateStore, VerStore, run, requeue, gates, Audit);
            Queue = queue;
            return queue;
        }
    }

    private static async Task<MergeQueue> VerifiedAsync(Harness h, string agentId)
    {
        await h.Queue.RunVerificationAsync(agentId, CancellationToken.None);
        Assert.Equal(WorkerMergeState.Verified, h.Queue.GetState(agentId));
        return h.Queue;
    }

    // ---- Legal transitions ----------------------------------------------

    [Fact]
    public async Task RunVerification_Working_To_Verified_On_Pass()
    {
        var h = new Harness();
        h.Build();
        Assert.Equal(WorkerMergeState.Working, h.Queue.GetState("a"));

        var record = await h.Queue.RunVerificationAsync("a", CancellationToken.None);

        Assert.True(record.Passed);
        Assert.Equal("sha0", record.MainSha);
        Assert.Equal(WorkerMergeState.Verified, h.Queue.GetState("a"));
    }

    [Fact]
    public async Task RunVerification_Fail_ReturnsToWorking_NotSilentlyRetried()
    {
        var h = new Harness();
        h.Build(withRequeue: false);
        h.FailFor("a");

        var record = await h.Queue.RunVerificationAsync("a", CancellationToken.None);

        Assert.False(record.Passed);
        Assert.Equal(WorkerMergeState.Working, h.Queue.GetState("a"));
    }

    [Fact]
    public async Task Verified_To_AwaitingReview_To_Merged()
    {
        var h = new Harness();
        h.Build();
        await VerifiedAsync(h, "a");

        h.Queue.RequestReview("a");
        Assert.Equal(WorkerMergeState.AwaitingReview, h.Queue.GetState("a"));

        h.Queue.ConfirmHumanMerge("a", "sha1");
        Assert.Equal(WorkerMergeState.Merged, h.Queue.GetState("a"));
    }

    // ---- Typed-illegal transitions --------------------------------------

    [Fact]
    public void RequestReview_FromWorking_Throws_Typed()
    {
        var h = new Harness();
        h.Build();
        var ex = Assert.Throws<InvalidMergeStateTransitionException>(() => h.Queue.RequestReview("a"));
        Assert.Equal(WorkerMergeState.Working, ex.From);
        Assert.Equal(WorkerMergeState.AwaitingReview, ex.To);
    }

    [Fact]
    public void Reject_FromWorking_Throws_Typed()
    {
        var h = new Harness();
        h.Build();
        Assert.Throws<InvalidMergeStateTransitionException>(() => h.Queue.Reject("a"));
    }

    [Fact]
    public async Task Merged_IsTerminal_NoFurtherTransition()
    {
        var h = new Harness();
        h.Build(withRequeue: false);
        await VerifiedAsync(h, "a");
        h.Queue.ConfirmHumanMerge("a", "sha1");

        // Any legal-looking op on a terminal branch throws (Merged has no outgoing transitions).
        Assert.Throws<InvalidMergeStateTransitionException>(() => h.Queue.RequestReview("a"));
    }

    // ---- Property test: random legal sequences never corrupt state ------

    [Fact]
    public async Task RandomLegalSequences_NeverCorruptState()
    {
        var rng = new Random(1234);
        for (var iter = 0; iter < 200; iter++)
        {
            var h = new Harness();
            h.Build(withRequeue: false);
            var id = "agent";
            for (var step = 0; step < 12; step++)
            {
                var state = h.Queue.GetState(id);
                // Pick only a legal operation for the current state.
                switch (state)
                {
                    case WorkerMergeState.Working:
                    case WorkerMergeState.StaleVerified:
                        await h.Queue.RunVerificationAsync(id, CancellationToken.None);
                        break;
                    case WorkerMergeState.Verified:
                        if (rng.Next(2) == 0) h.Queue.RequestReview(id);
                        else h.Queue.NotifyMainMoved("sha" + step);
                        break;
                    case WorkerMergeState.AwaitingReview:
                        if (rng.Next(2) == 0) h.Queue.Reject(id);
                        else h.Queue.NotifyMainMoved("sha" + step);
                        break;
                    default:
                        break; // terminal
                }

                Assert.True(Enum.IsDefined(typeof(WorkerMergeState), h.Queue.GetState(id)));
            }
        }
    }

    // ---- Stale cascade + FIFO re-queue ----------------------------------

    /// <summary>
    /// The stale cascade re-queues by ORIGINAL VERIFICATION TIME, and the entries are deliberately
    /// inserted in a different order than they are verified in.
    ///
    /// <para><b>Why the insertion order is load-bearing.</b> This test used to verify C, A, B into an
    /// empty queue, so the internal <c>Dictionary</c>'s enumeration order and the expected FIFO order were
    /// the same sequence — and deleting <c>.OrderBy(kv =&gt; _verifiedAt…)</c> from
    /// <see cref="MergeQueue.NotifyMainMoved"/> left this and 28 other tests green. (Replacing it with
    /// <c>OrderByDescending</c> did fail, so the test read order; the asserted order was simply also the
    /// incidental default.) In the daemon the two are never the same: <c>_states</c> is rehydrated from
    /// SQLite in whatever order the rows come back and then punched full of holes by <c>Cancel()</c>
    /// removals, so enumeration order is not insertion order and certainly not verification order.</para>
    ///
    /// <para>So: the entries go in A, B, C (with a cancelled decoy to leave a real hole in the dictionary,
    /// the shape <c>Cancel</c> produces) and are verified C, A, B. Enumeration order and FIFO order now
    /// disagree, and only the sort can produce the expected answer.</para>
    /// </summary>
    [Fact]
    public async Task NotifyMainMoved_FlipsAllVerifiedToStale_AndRequeuesFIFO()
    {
        var h = new Harness();
        h.Build();

        // Insertion order A, B, C — alphabetical, i.e. NOT the order they will be verified in. The decoy
        // is inserted between them and then cancelled, so the dictionary carries a freed slot exactly as
        // it does in a daemon that has removed an entry.
        h.Queue.EnsureEntry("A", MergeEntryOrigin.Local);
        h.Queue.EnsureEntry("decoy", MergeEntryOrigin.Local);
        h.Queue.EnsureEntry("B", MergeEntryOrigin.Local);
        h.Queue.EnsureEntry("C", MergeEntryOrigin.Local);
        h.Queue.Cancel("decoy");

        // Verify in the order C, A, B → their verification times order C < A < B (not alphabetical, and
        // not the insertion order above).
        await VerifiedAsync(h, "C");
        await VerifiedAsync(h, "A");
        await VerifiedAsync(h, "B");

        // Guard the guard: the assertion below is only meaningful while the queue's own enumeration
        // disagrees with the expected FIFO order. If Agents ever starts coming back in verification order
        // this test has quietly gone back to asserting the incidental default.
        Assert.NotEqual(new[] { "C", "A", "B" }, h.Queue.Agents.ToArray());

        h.Queue.NotifyMainMoved("sha1");
        await h.Queue.LastCascade;

        // FIFO by original verification time.
        Assert.Equal(new[] { "C", "A", "B" }, h.RequeueOrder);

        // After the cascade each branch is re-verified against the NEW main.
        foreach (var id in new[] { "A", "B", "C" })
        {
            Assert.Equal(WorkerMergeState.Verified, h.Queue.GetState(id));
            Assert.Equal("sha1", h.VerStore.Latest("repo", id)!.MainSha);
        }
    }

    [Fact]
    public async Task StaleVerified_BlocksCanMerge_WithReason()
    {
        var h = new Harness();
        h.Build(withRequeue: false);
        await VerifiedAsync(h, "a");
        Assert.True(h.Queue.CanMerge("a", out _));

        h.Queue.NotifyMainMoved("sha1"); // a is now stale (verified against sha0)
        Assert.Equal(WorkerMergeState.StaleVerified, h.Queue.GetState("a"));
        Assert.False(h.Queue.CanMerge("a", out var reason));
        Assert.Contains("stale", reason);
    }

    // ---- Override: logged + audited, CanMerge still false ---------------

    [Fact]
    public async Task Override_LoggedAudited_ButCanMergeStillFalse()
    {
        var h = new Harness();
        h.Build(withRequeue: false);
        await VerifiedAsync(h, "a");
        h.Queue.NotifyMainMoved("sha1"); // stale

        h.Queue.RecordStaleOverrideUse("a", "human accepted the risk");

        Assert.Contains(h.Audit.Read(), e => e.Type == "stale_override_used");
        Assert.False(h.Queue.CanMerge("a", out _)); // override is a SEPARATE path
    }

    // ---- No test command: typed --------------------------------------

    [Fact]
    public async Task NoTestCommand_Throws_Typed_AndReturnsToWorking()
    {
        var store = new InMemoryMergeQueueStore();
        var ver = new InMemoryVerificationStore();
        Func<string, CancellationToken, Task<VerificationRecord>> run =
            (id, ct) => throw new NoVerificationCommandException("No verification command configured for this repository.");
        var queue = new MergeQueue("repo", "sha0", store, ver, run);

        await Assert.ThrowsAsync<NoVerificationCommandException>(() => queue.RunVerificationAsync("a", CancellationToken.None));
        Assert.Equal(WorkerMergeState.Working, queue.GetState("a"));
    }

    // ---- Immutable records ----------------------------------------------

    [Fact]
    public async Task VerificationRecord_Immutable_ReRunInsertsNewRow()
    {
        var h = new Harness();
        h.Build(withRequeue: false);
        await VerifiedAsync(h, "a");
        var first = h.VerStore.Latest("repo", "a")!;

        h.Queue.NotifyMainMoved("sha1"); // stale
        await h.Queue.RunVerificationAsync("a", CancellationToken.None); // re-verify → new row

        var history = h.VerStore.History("repo", "a");
        Assert.Equal(2, history.Count);
        Assert.Equal("sha0", history[0].MainSha);
        Assert.Equal("sha1", history[1].MainSha);
        // The first record is unchanged (immutability).
        Assert.Equal(first, history[0]);

        // The store API has no update method (compile-time immutability).
        Assert.Null(typeof(IVerificationStore).GetMethod("Update"));
    }

    // ---- Restart resume --------------------------------------------------

    [Fact]
    public async Task Restart_ResumesInterruptedVerifying_NeverStuck()
    {
        var store = new InMemoryMergeQueueStore();
        var ver = new InMemoryVerificationStore();

        // Simulate a daemon that crashed mid-Verifying: persist a Verifying row directly.
        store.Save(new Mainguard.Git.Models.MergeQueueRow
        {
            RepoHash = "repo",
            AgentId = "a",
            State = WorkerMergeState.Verifying.ToString(),
            UpdatedUtc = DateTime.UtcNow,
        });

        // A fresh queue hydrates from the store and resumes.
        MergeQueue queue = null!;
        Func<string, CancellationToken, Task<VerificationRecord>> run = (id, ct) =>
            Task.FromResult(new VerificationRecord(id, queue.CurrentMainSha, true, "log", "cmd", "hash", DateTimeOffset.UtcNow));
        queue = new MergeQueue("repo", "sha0", store, ver, run);

        Assert.Equal(WorkerMergeState.Verifying, queue.GetState("a")); // resumed the interrupted state
        var report = await queue.ResumeAfterRestartAsync(hasLiveJail: _ => true);

        Assert.Equal(WorkerMergeState.Verified, queue.GetState("a")); // terminal state reached — not stuck
        Assert.Equal(new[] { "a" }, report.ReRun);
        Assert.Empty(report.Stranded);
    }

    /// <summary>
    /// The other arm, and the reason the resume takes a jail probe at all. An entry whose sandbox died with
    /// the daemon <b>cannot</b> verify — verification runs in the worker's own jail (§3.2) — so a resume
    /// that "re-drove" it would announce Verifying → Verifying to every observer and then fail on the
    /// no-jail refusal. It is returned to Working instead, with the reason recorded, and it does NOT run.
    /// </summary>
    [Fact]
    public async Task Restart_WhenTheJailIsGone_ReturnsTheEntryToWorking_WithoutClaimingToVerifyInIt()
    {
        var store = new InMemoryMergeQueueStore();
        store.Save(new Mainguard.Git.Models.MergeQueueRow
        {
            RepoHash = "repo",
            AgentId = "a",
            State = WorkerMergeState.Verifying.ToString(),
            UpdatedUtc = DateTime.UtcNow,
        });

        var ran = 0;
        var audit = new InMemoryAuditLog();
        var queue = new MergeQueue(
            "repo", "sha0", store, new InMemoryVerificationStore(),
            (id, ct) =>
            {
                Interlocked.Increment(ref ran);
                return Task.FromResult(new VerificationRecord(id, "sha0", true, "l", "c", "h", DateTimeOffset.UnixEpoch));
            },
            audit: audit);

        var report = await queue.ResumeAfterRestartAsync(hasLiveJail: _ => false);

        Assert.Equal(0, ran); // nothing was executed — there was nowhere to execute it
        Assert.Equal(WorkerMergeState.Working, queue.GetState("a"));
        Assert.Equal("Working", store.LoadAll("repo").Single(r => r.AgentId == "a").State);
        Assert.Equal(new[] { "a" }, report.Stranded);
        Assert.Empty(report.ReRun);

        // Recorded as the daemon reconciling itself, not as a human clearing anything.
        var audited = Assert.Single(audit.Read(), e => e.Type == MergeQueue.RestartResumeEvent);
        Assert.Equal("stranded", audited.Fields["outcome"]);
        Assert.Equal("a", audited.Fields["agent"]);
        Assert.DoesNotContain(audit.Read(), e => e.Type == MergeQueue.StalledVerificationClearedEvent);
    }

    /// <summary>
    /// The human wins a race with the resume. The jail probe blocks on the container runtime — ten seconds
    /// in the daemon — which is ample room for a discard to land, and the resume runs on a background task
    /// whose exceptions nobody sees.
    ///
    /// <para>Two things must hold, and neither did before the guard: the report must not name a run that
    /// never happened, and <c>IsVerificationInFlight</c> must not be left answering TRUE forever. The
    /// second is the nastier one — <c>RunVerificationAsync</c> adds to the in-flight set and THEN attempts
    /// the transition, so an illegal <c>Discarded → Verifying</c> throws with the id already marked, and
    /// every path that would remove it is downstream of the throw. That is this subsystem's own defect
    /// shape (a row claiming an activity that is not happening) reintroduced by its fix.</para>
    /// </summary>
    [Fact]
    public async Task Restart_WhenTheEntryIsDiscardedWhileTheJailProbeRuns_DoesNotRunIt_AndLeavesNoPhantomInFlight()
    {
        var store = new InMemoryMergeQueueStore();
        store.Save(new Mainguard.Git.Models.MergeQueueRow
        {
            RepoHash = "repo",
            AgentId = "a",
            State = WorkerMergeState.Verifying.ToString(),
            UpdatedUtc = DateTime.UtcNow,
        });

        var ran = 0;
        MergeQueue queue = null!;
        queue = new MergeQueue(
            "repo", "sha0", store, new InMemoryVerificationStore(),
            (id, ct) =>
            {
                Interlocked.Increment(ref ran);
                return Task.FromResult(new VerificationRecord(id, "sha0", true, "l", "c", "h", DateTimeOffset.UnixEpoch));
            });

        // The probe is where the daemon blocks on Docker; the human's discard lands inside it.
        var report = await queue.ResumeAfterRestartAsync(hasLiveJail: id =>
        {
            Assert.True(queue.TryDiscard(id, "uid:1000", "its agent is gone", out var refusal), refusal);
            return true; // ...and the jail really is up, so this is the arm that would have re-run it.
        });

        Assert.Equal(0, ran);
        Assert.Empty(report.ReRun); // not reported as re-driven — nothing was
        Assert.Empty(report.Stranded);

        // The human's decision stands, and nothing claims to be verifying it.
        Assert.Equal(WorkerMergeState.Discarded, queue.GetState("a"));
        Assert.False(queue.IsVerificationInFlight("a"));
    }

    /// <summary>
    /// The same leak, asserted directly on <see cref="MergeQueue.RunVerificationAsync"/>, because the guard
    /// lives there and the resume is only one of its callers (the stale cascade's auto re-queue and the
    /// Verify RPC reach it too).
    /// </summary>
    [Fact]
    public async Task RunVerification_OnAnEntryThatWentTerminal_ThrowsWithoutLeavingItMarkedInFlight()
    {
        var h = new Harness();
        h.Build(withRequeue: false);
        h.Queue.EnsureEntry("a", MergeEntryOrigin.Local);
        Assert.True(h.Queue.TryDiscard("a", "uid:1000", "", out var refusal), refusal);

        await Assert.ThrowsAsync<InvalidMergeStateTransitionException>(
            () => h.Queue.RunVerificationAsync("a", CancellationToken.None));

        Assert.False(h.Queue.IsVerificationInFlight("a"));
        Assert.Equal(WorkerMergeState.Discarded, h.Queue.GetState("a"));
    }

    /// <summary>
    /// A resume must not touch a verification that is genuinely running. The in-flight set is the only
    /// thing that tells the two apart, and re-entering <c>RunVerificationAsync</c> for a live run would
    /// throw "already in flight" out of a background task nobody awaits.
    /// </summary>
    [Fact]
    public async Task Restart_SkipsAVerificationThatIsActuallyRunning()
    {
        var release = new TaskCompletionSource();
        var starts = 0;
        MergeQueue queue = null!;
        queue = new MergeQueue(
            "repo", "sha0", new InMemoryMergeQueueStore(), new InMemoryVerificationStore(),
            async (id, ct) =>
            {
                Interlocked.Increment(ref starts);
                await release.Task;
                return new VerificationRecord(id, queue.CurrentMainSha, true, "l", "c", "h", DateTimeOffset.UnixEpoch);
            });

        var inFlight = queue.RunVerificationAsync("a", CancellationToken.None);
        Assert.Equal(WorkerMergeState.Verifying, queue.GetState("a"));
        Assert.True(queue.IsVerificationInFlight("a"));

        // Both arms must leave it alone: it is neither re-run nor stranded.
        var live = await queue.ResumeAfterRestartAsync(hasLiveJail: _ => true);
        var dead = await queue.ResumeAfterRestartAsync(hasLiveJail: _ => false);

        Assert.Empty(live.ReRun);
        Assert.Empty(live.Stranded);
        Assert.Empty(dead.ReRun);
        Assert.Empty(dead.Stranded);
        Assert.Equal(1, starts);
        Assert.Equal(WorkerMergeState.Verifying, queue.GetState("a"));

        release.SetResult();
        await inFlight;
        Assert.Equal(WorkerMergeState.Verified, queue.GetState("a"));
    }

    // ---- NoAutoMergePathExists (API shape) ------------------------------

    [Fact]
    public async Task NoAutoMergePathExists()
    {
        // 1. IMergeQueue's surface exposes no method that can reach Merged.
        var methods = typeof(IMergeQueue).GetMethods().Select(m => m.Name).ToArray();
        Assert.DoesNotContain("ConfirmHumanMerge", methods);
        Assert.DoesNotContain("Merge", methods);
        Assert.DoesNotContain("Confirm", methods);
        Assert.Equal(
            new[] { "GetState", "RunVerificationAsync", "NotifyMainMoved", "CanMerge" }.OrderBy(x => x),
            methods.OrderBy(x => x));

        // 2. Driving every IMergeQueue operation on a fresh Verified branch never yields Merged.
        var h = new Harness();
        IMergeQueue queue = h.Build(withRequeue: false);
        await queue.RunVerificationAsync("a", CancellationToken.None);
        queue.NotifyMainMoved("sha1");
        queue.CanMerge("a", out _);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState("a"));
    }

    // ---- Human entry lifecycle: discard + the stalled-verification clear ----

    /// <summary>
    /// The defect this whole change answers, in its simplest form: an entry left at <c>Working</c> by an
    /// agent that is gone. It is not verifiable (no jail), not reviewable, not mergeable — and before
    /// <c>TryDiscard</c> there was no operation of any kind that could act on it, so it sat on the queue
    /// forever. Discard is legal from <c>Working</c>, and it is terminal.
    /// </summary>
    [Fact]
    public void Discard_FromWorking_IsTerminal_AndUnambiguouslyNotMerged()
    {
        var h = new Harness();
        h.Build(withRequeue: false);
        h.Queue.EnsureEntry("stranded", MergeEntryOrigin.Local);
        Assert.Equal(WorkerMergeState.Working, h.Queue.GetState("stranded"));

        Assert.True(h.Queue.TryDiscard("stranded", "uid:1000", "its agent is gone", out var refusal), refusal);

        // Terminal, and terminal as the thing it actually is.
        Assert.Equal(WorkerMergeState.Discarded, h.Queue.GetState("stranded"));
        Assert.NotEqual(WorkerMergeState.Merged, h.Queue.GetState("stranded"));

        // ...in the STORE too, which is what the daemon rehydrates from and what any later reader trusts.
        var row = h.StateStore.LoadAll("repo").Single(r => r.AgentId == "stranded");
        Assert.Equal("Discarded", row.State);
        Assert.Equal("uid:1000", row.DiscardedBy);
        Assert.Equal("its agent is gone", row.DiscardReason);
        Assert.NotNull(row.DiscardedAtUtc);

        // ...and in the gate, whose reason must not read like an entry that might still get there.
        Assert.False(h.Queue.CanMerge("stranded", out var gate));
        Assert.Contains("discarded", gate);

        // The audit event is its own type — nothing about this can be read as a merge.
        var audited = Assert.Single(h.Audit.Read(), e => e.Type == MergeQueue.DiscardedEvent);
        Assert.Equal("uid:1000", audited.Fields["by"]);
        Assert.Equal("Working", audited.Fields["from_state"]);
        Assert.DoesNotContain(h.Audit.Read(), e => e.Type.Contains("merge", StringComparison.OrdinalIgnoreCase));

        // Nothing terminal is reachable from a terminal entry, in either direction.
        Assert.False(h.Queue.TryDiscard("stranded", "uid:1000", "", out var again));
        Assert.Contains("already discarded", again);
        Assert.Throws<InvalidMergeStateTransitionException>(() => h.Queue.RequestReview("stranded"));
    }

    /// <summary>
    /// A discarded entry leaves the LIVE queue and stays gone across a daemon restart — while the record of
    /// it does not. Both halves matter: an entry that reappears on the rail was not removed, and an entry
    /// that vanishes from the store leaves no evidence of who dropped it.
    /// </summary>
    [Fact]
    public async Task Discard_LeavesTheLiveQueue_AndSurvivesARestartAsARecord()
    {
        var h = new Harness();
        h.Build(withRequeue: false);
        await VerifiedAsync(h, "keep");
        h.Queue.EnsureEntry("drop", MergeEntryOrigin.Local);
        Assert.Equal(new[] { "drop", "keep" }, h.Queue.Agents.OrderBy(x => x));

        Assert.True(h.Queue.TryDiscard("drop", "uid:1000", "superseded", out _));

        Assert.Equal(new[] { "keep" }, h.Queue.Agents);
        Assert.Equal(new[] { "drop" }, h.Queue.DiscardedAgents);

        // Restart: a brand-new queue over the SAME store (what MergeQueueProvisioner rebuilds).
        var restarted = new MergeQueue(
            "repo", "sha0", h.StateStore, h.VerStore,
            (id, ct) => Task.FromResult(new VerificationRecord(id, "sha0", true, "log", "cmd", "hash", DateTimeOffset.UnixEpoch)));

        Assert.DoesNotContain("drop", restarted.Agents);
        Assert.Equal(WorkerMergeState.Discarded, restarted.GetState("drop"));
        var record = restarted.GetDiscard("drop");
        Assert.NotNull(record);
        Assert.Equal("uid:1000", record!.By);
        Assert.Equal("superseded", record.Reason);

        // And it cannot be resurrected by the ordinary "an agent joined this repo" path.
        restarted.EnsureEntry("drop", MergeEntryOrigin.Local);
        Assert.Equal(WorkerMergeState.Discarded, restarted.GetState("drop"));
        Assert.DoesNotContain("drop", restarted.Agents);
    }

    /// <summary>
    /// A discarded entry whose branch then moves must be a no-op, not a throw.
    ///
    /// <para><c>NotifyNewCommits</c> guarded terminality by naming <c>Merged</c> and <c>Rejected</c>, so
    /// adding <c>Discarded</c> to the enum silently turned this into an illegal <c>Discarded → Working</c>
    /// transition. It is reachable in production: <c>ExternalPrIntake</c> calls <c>NotifyNewCommits</c>
    /// when an intake'd PR's head moves, swallows the exception per-PR, and therefore never reaches
    /// <c>SetSeenHead</c> — so the same head is re-fetched and the same exception thrown on every poll,
    /// forever.</para>
    /// </summary>
    [Fact]
    public void NotifyNewCommits_OnADiscardedEntry_IsANoOp_NotAThrow()
    {
        var h = new Harness();
        h.Build(withRequeue: false);
        h.Queue.EnsureEntry("pr-7", MergeEntryOrigin.External);
        Assert.True(h.Queue.TryDiscard("pr-7", "uid:1000", "not wanted", out var refusal), refusal);

        h.Queue.NotifyNewCommits("pr-7"); // must not throw

        // ...and the human's decision still stands: a head move does not resurrect a dropped entry.
        Assert.Equal(WorkerMergeState.Discarded, h.Queue.GetState("pr-7"));
        Assert.DoesNotContain("pr-7", h.Queue.Agents);
    }

    /// <summary>An id the queue never tracked must not be discardable into existence.</summary>
    [Fact]
    public void Discard_OfAnUntrackedEntry_IsRefused_AndCreatesNothing()
    {
        var h = new Harness();
        h.Build(withRequeue: false);

        Assert.False(h.Queue.TryDiscard("never-seen", "uid:1000", "", out var refusal));
        Assert.Contains("not in the merge queue", refusal);
        Assert.Empty(h.StateStore.LoadAll("repo"));
        Assert.Empty(h.Queue.DiscardedAgents);
    }

    /// <summary>
    /// The human's decision beats a run that was already executing. Without the terminal guard in the
    /// verification completion path, the run finishing would attempt <c>Discarded → Verified</c> and throw
    /// an illegal-transition exception out of a background continuation nobody awaits.
    /// </summary>
    [Fact]
    public async Task Discard_DuringAVerification_WinsOverTheRunThatFinishesAfterIt()
    {
        var release = new TaskCompletionSource();
        var store = new InMemoryMergeQueueStore();
        MergeQueue queue = null!;
        var run = new Func<string, CancellationToken, Task<VerificationRecord>>(async (id, ct) =>
        {
            await release.Task;
            return new VerificationRecord(id, queue.CurrentMainSha, true, "log", "cmd", "hash", DateTimeOffset.UnixEpoch);
        });
        queue = new MergeQueue("repo", "sha0", store, new InMemoryVerificationStore(), run);

        var inFlight = queue.RunVerificationAsync("a", CancellationToken.None);
        Assert.Equal(WorkerMergeState.Verifying, queue.GetState("a"));
        Assert.True(queue.IsVerificationInFlight("a"));

        Assert.True(queue.TryDiscard("a", "uid:1000", "changed my mind", out var refusal), refusal);

        release.SetResult();
        await inFlight; // must not throw an illegal-transition exception

        Assert.Equal(WorkerMergeState.Discarded, queue.GetState("a"));
        Assert.Equal("Discarded", store.LoadAll("repo").Single(r => r.AgentId == "a").State);
    }

    /// <summary>
    /// The stalled-verification case. A daemon that restarts mid-run rehydrates <c>Verifying</c> with
    /// nothing executing (the in-flight set is memory; the state is persisted) — so until something
    /// re-drives it the entry reports "verifying" about a run that does not exist. The clear says so, and
    /// returns it to Working.
    ///
    /// <para><c>MergeQueueProvisioner</c> now starts a <c>ResumeAfterRestartAsync</c> pass whenever it
    /// rebuilds a repo's queue, so the daemon reaches this shape by itself. This stays the human's escape
    /// hatch for what a resume cannot establish — a repo whose queue is never rebuilt, or a container
    /// runtime the probe could not reach — and it is asserted here directly on the queue, with no resume in
    /// the picture, so the two mechanisms cannot mask each other.</para>
    /// </summary>
    [Fact]
    public void ClearStalledVerification_UnsticksARowWithNoRunBehindIt()
    {
        var store = new InMemoryMergeQueueStore();
        store.Save(new Mainguard.Git.Models.MergeQueueRow
        {
            RepoHash = "repo",
            AgentId = "a",
            State = WorkerMergeState.Verifying.ToString(),
            UpdatedUtc = DateTime.UtcNow,
        });

        var audit = new InMemoryAuditLog();
        var queue = new MergeQueue(
            "repo", "sha0", store, new InMemoryVerificationStore(),
            (id, ct) => Task.FromResult(new VerificationRecord(id, "sha0", true, "l", "c", "h", DateTimeOffset.UnixEpoch)),
            audit: audit);

        Assert.Equal(WorkerMergeState.Verifying, queue.GetState("a"));
        Assert.False(queue.IsVerificationInFlight("a"));

        // The gate must not claim an activity that is not happening.
        Assert.False(queue.CanMerge("a", out var reason));
        Assert.Equal("verification stalled — no run in progress", reason);

        Assert.True(queue.TryClearStalledVerification("a", "uid:1000", out var refusal), refusal);
        Assert.Equal(WorkerMergeState.Working, queue.GetState("a"));
        Assert.Equal("Working", store.LoadAll("repo").Single().State);
        Assert.Contains(audit.Read(), e => e.Type == MergeQueue.StalledVerificationClearedEvent);
    }

    /// <summary>
    /// ...and it refuses while a run really is executing. The honest answer there is "wait" — and moving a
    /// live run's entry to Working would make that run's own completion an illegal Working → Verified.
    /// </summary>
    [Fact]
    public async Task ClearStalledVerification_IsRefusedWhileARunIsActuallyInFlight()
    {
        var release = new TaskCompletionSource();
        MergeQueue queue = null!;
        var run = new Func<string, CancellationToken, Task<VerificationRecord>>(async (id, ct) =>
        {
            await release.Task;
            return new VerificationRecord(id, queue.CurrentMainSha, true, "log", "cmd", "hash", DateTimeOffset.UnixEpoch);
        });
        queue = new MergeQueue("repo", "sha0", new InMemoryMergeQueueStore(), new InMemoryVerificationStore(), run);

        var inFlight = queue.RunVerificationAsync("a", CancellationToken.None);
        Assert.True(queue.IsVerificationInFlight("a"));
        Assert.True(queue.CanMerge("a", out var busy) is false && busy == "verifying");

        Assert.False(queue.TryClearStalledVerification("a", "uid:1000", out var refusal));
        Assert.Contains("running for this entry right now", refusal);
        Assert.Equal(WorkerMergeState.Verifying, queue.GetState("a"));

        release.SetResult();
        await inFlight;
        Assert.Equal(WorkerMergeState.Verified, queue.GetState("a"));
    }

    /// <summary>Wrong state ⇒ refused, and said plainly rather than silently no-op'd.</summary>
    [Fact]
    public async Task ClearStalledVerification_OnAnEntryThatIsNotVerifying_IsRefused()
    {
        var h = new Harness();
        h.Build(withRequeue: false);
        await VerifiedAsync(h, "a");

        Assert.False(h.Queue.TryClearStalledVerification("a", "uid:1000", out var refusal));
        Assert.Contains("not stuck verifying", refusal);
        Assert.Equal(WorkerMergeState.Verified, h.Queue.GetState("a"));
    }

    /// <summary>
    /// The lifecycle actions stay OFF <see cref="IMergeQueue"/>, exactly as <c>ConfirmHumanMerge</c> does.
    /// That interface is what the orchestration machinery holds; an agent-reachable discard would be a way
    /// for a branch to erase the evidence blocking it rather than clear the gate.
    /// </summary>
    [Fact]
    public void EntryLifecycleActions_AreNotOnIMergeQueue()
    {
        var methods = typeof(IMergeQueue).GetMethods().Select(m => m.Name).ToArray();
        Assert.DoesNotContain("TryDiscard", methods);
        Assert.DoesNotContain("TryClearStalledVerification", methods);
        // The full shape is asserted by NoAutoMergePathExists; this is the discard-specific half.
    }

    // ---- RT-D2: gamed test command flagged before a silent merge --------

    [Fact]
    public async Task GamedTestCommand_ShouldBeFlaggedBeforeSilentMerge()
    {
        var h = new Harness();
        h.Build(withRequeue: false, withChangedGate: true);
        await VerifiedAsync(h, "a");

        // RT-D2: the resolver detected the branch rewrote its test command vs the main baseline.
        var resolution = VerificationCommandResolver.Resolve(branchConfigContent: "exit 0", mainConfigContent: "npm test");
        Assert.True(resolution.ChangedVsMain);
        h.ChangedGate.SetFlagged("a", resolution.ChangedVsMain);

        // The merge is blocked with the dedicated reason until the item is acknowledged.
        Assert.False(h.Queue.CanMerge("a", out var reason));
        Assert.Contains("test command changed", reason);

        h.ChangedGate.Acknowledge("a");
        Assert.True(h.Queue.CanMerge("a", out _));
    }

    [Fact]
    public void VerificationCommandResolver_HashesConfig_AndDetectsDrift()
    {
        var same = VerificationCommandResolver.Resolve("npm test", "npm test");
        Assert.False(same.ChangedVsMain);
        Assert.Equal(VerificationCommandResolver.Sha256("npm test"), same.ConfigHash);

        var drift = VerificationCommandResolver.Resolve("exit 0", "npm test");
        Assert.True(drift.ChangedVsMain);

        // A human-owned command pin overrides branch config and is never flagged.
        var pinned = VerificationCommandResolver.Resolve("exit 0", "npm test", pinnedCommand: "npm run verify");
        Assert.False(pinned.ChangedVsMain);
        Assert.Equal(new[] { "npm", "run", "verify" }, pinned.Command);

        Assert.Throws<NoVerificationCommandException>(() => VerificationCommandResolver.Resolve(null, "npm test"));
    }
}
