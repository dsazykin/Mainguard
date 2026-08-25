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
/// ISSUES-LOG #24 — <b>a merge-queue entry must be able to stop claiming an agent that does not exist.</b>
///
/// <para>The field case: 15 <c>Working</c> rows three days stale, against exactly ONE real container on the
/// machine, every one of them rendering an enabled Verify. Queue state is push-only — stopping an agent is
/// not a transition and a jail dying out of band is not one either — so nothing ever walked those rows back
/// toward reality, exactly as nothing walked <c>AgentSession.State</c> back before <c>67b9cc1f</c>.</para>
///
/// <para>These tests pin the two halves of the answer separately, because they are separate promises. The
/// LIVENESS half must move (and move back, and be published). The MERGE-STATE half must not move at all:
/// <c>AgentResumeService</c> exists to hand a stranded entry a live jail again on its own branch with its
/// commits intact, and <c>Discarded</c> is terminal with no path back — so a reconcile that discarded these
/// would convert every recoverable entry into an unrecoverable one, which is worse than the bug.</para>
/// </summary>
public class MergeQueueJailReconcileTests
{
    private static MergeQueue NewQueue(
        InMemoryMergeQueueStore? store = null,
        InMemoryAuditLog? audit = null,
        Func<string, CancellationToken, Task<VerificationRecord>>? run = null)
    {
        MergeQueue queue = null!;
        run ??= (id, ct) => Task.FromResult(new VerificationRecord(
            id, queue.CurrentMainSha, true, "log.txt", "npm test", "hash", DateTimeOffset.UnixEpoch));

        queue = new MergeQueue(
            "repo", "sha0", store ?? new InMemoryMergeQueueStore(), new InMemoryVerificationStore(),
            run, audit: audit);
        return queue;
    }

    /// <summary>
    /// The unmeasured answer is <c>null</c>, not <c>false</c> — and the distinction is the whole reason the
    /// wire field is three-valued. The rail withholds Verify on <c>false</c> and offers it on <c>null</c>,
    /// so a queue nobody has reconciled yet answering a confident "no jail" would disable the only action
    /// every entry has, on the strength of a fact nothing established.
    /// </summary>
    [Fact]
    public void HasLiveJail_ShouldBeUnknownUntilAPassHasActuallyLooked()
    {
        var queue = NewQueue();
        queue.EnsureEntry("a", MergeEntryOrigin.Local);

        Assert.Null(queue.HasLiveJail("a"));

        queue.ReconcileJails(_ => true);

        Assert.True(queue.HasLiveJail("a"));
        // An id this queue does not track has no answer either, measured pass or not.
        Assert.Null(queue.HasLiveJail("never-seen"));
    }

    /// <summary>The field repro, minimised: an entry left at <c>Working</c> whose jail is gone.</summary>
    [Fact]
    public void ReconcileJails_ShouldStrandAWorkingEntryWhoseJailIsGone()
    {
        var audit = new InMemoryAuditLog();
        var queue = NewQueue(audit: audit);
        queue.EnsureEntry("a", MergeEntryOrigin.Local);

        var report = queue.ReconcileJails(_ => false);

        Assert.Equal(new[] { "a" }, report.Stranded);
        Assert.Empty(report.Recovered);
        Assert.False(queue.HasLiveJail("a"));

        // The merge state is UNTOUCHED. This is the load-bearing assertion of the whole file.
        Assert.Equal(WorkerMergeState.Working, queue.GetState("a"));
        Assert.Null(queue.GetDiscard("a"));

        // …and the gate now says the true thing instead of "not verified yet", which is a sentence about a
        // branch still on its way.
        Assert.False(queue.CanMerge("a", out var reason));
        Assert.Equal(MergeQueue.StrandedReason, reason);

        var evt = Assert.Single(audit.Read(), e => e.Type == MergeQueue.JailReconciledEvent);
        Assert.Equal("stranded", evt.Fields["outcome"]);
        Assert.Equal("a", evt.Fields["agent"]);
        // Never a person's name: nobody decided this.
        Assert.Equal(MergeQueue.ReconcilerActor, evt.Fields["by"]);
        Assert.StartsWith("system:", evt.Fields["by"], StringComparison.Ordinal);
    }

    /// <summary>
    /// An entry whose agent is genuinely alive must be left completely alone — the failure mode that would
    /// make this feature unshippable is a reconcile that strands live work.
    /// </summary>
    [Fact]
    public void ReconcileJails_ShouldNotTouchAnEntryWhoseAgentIsStillAlive()
    {
        var audit = new InMemoryAuditLog();
        var queue = NewQueue(audit: audit);
        queue.EnsureEntry("alive", MergeEntryOrigin.Local);
        queue.EnsureEntry("dead", MergeEntryOrigin.Local);

        var report = queue.ReconcileJails(id => id == "alive");

        Assert.Equal(new[] { "dead" }, report.Stranded);
        Assert.True(queue.HasLiveJail("alive"));
        Assert.False(queue.HasLiveJail("dead"));

        Assert.True(queue.CanMerge("alive", out var aliveReason) is false && aliveReason == "not verified yet");
        Assert.DoesNotContain(
            audit.Read().Where(e => e.Type == MergeQueue.JailReconciledEvent),
            e => e.Fields["agent"] == "alive");
    }

    /// <summary>
    /// The other direction, which is what makes this a reconcile rather than a one-way marking: a resumed
    /// entry (or an adopted survivor, or a Docker that was merely unreachable last pass) comes back.
    /// </summary>
    [Fact]
    public void ReconcileJails_ShouldUnstrandAnEntryWhoseJailComesBack()
    {
        var audit = new InMemoryAuditLog();
        var queue = NewQueue(audit: audit);
        queue.EnsureEntry("a", MergeEntryOrigin.Local);

        queue.ReconcileJails(_ => false);
        Assert.False(queue.HasLiveJail("a"));

        var back = queue.ReconcileJails(_ => true);

        Assert.Equal(new[] { "a" }, back.Recovered);
        Assert.True(queue.HasLiveJail("a"));
        Assert.True(queue.CanMerge("a", out var reason) is false && reason == "not verified yet");
        Assert.Contains(
            audit.Read().Where(e => e.Type == MergeQueue.JailReconciledEvent),
            e => e.Fields["outcome"] == "recovered");
    }

    /// <summary>
    /// A repeat pass over an unchanged world reports nothing and publishes nothing. Without this the rail
    /// would be re-pushed every 30 seconds forever and the audit log would fill with the same sentence.
    /// </summary>
    [Fact]
    public void ReconcileJails_ShouldReportOnlyTransitionsAndPublishOnlyOnThem()
    {
        var audit = new InMemoryAuditLog();
        var queue = NewQueue(audit: audit);
        queue.EnsureEntry("a", MergeEntryOrigin.Local);

        var published = 0;
        queue.Changed += () => published++;

        Assert.Single(queue.ReconcileJails(_ => false).Stranded);
        Assert.Equal(1, published);

        var second = queue.ReconcileJails(_ => false);
        Assert.False(second.Changed);
        Assert.Empty(second.Stranded);
        Assert.Empty(second.Recovered);
        Assert.Equal(1, published);
        Assert.Single(audit.Read(), e => e.Type == MergeQueue.JailReconciledEvent);
    }

    /// <summary>
    /// A probe that throws means "no answer", never "no jail". An unreachable container engine must not
    /// read as every agent in the queue vanishing at once — the same rule the session reconciler's lister
    /// is written around.
    /// </summary>
    [Fact]
    public void ReconcileJails_ShouldLeaveEntriesAloneWhenTheProbeThrows()
    {
        var queue = NewQueue();
        queue.EnsureEntry("a", MergeEntryOrigin.Local);

        var report = queue.ReconcileJails(_ => throw new InvalidOperationException("docker is down"));

        Assert.False(report.Changed);
        Assert.Null(queue.HasLiveJail("a"));
        Assert.True(queue.CanMerge("a", out var reason) is false && reason == "not verified yet");
    }

    /// <summary>
    /// Terminal entries are skipped: a Merged branch's jail being gone is not news, and a Discarded entry
    /// has already left the live queue. Neither may be re-announced on every pass, and neither may have its
    /// terminal gate wording replaced by a liveness sentence.
    /// </summary>
    [Fact]
    public async Task ReconcileJails_ShouldSkipTerminalEntries()
    {
        var queue = NewQueue();
        queue.EnsureEntry("merged", MergeEntryOrigin.Local);
        queue.EnsureEntry("dropped", MergeEntryOrigin.Local);

        await queue.RunVerificationAsync("merged", CancellationToken.None);
        queue.ConfirmHumanMerge("merged", "sha0");
        Assert.True(queue.TryDiscard("dropped", "uid:1000", "tidy", out var refusal), refusal);

        var report = queue.ReconcileJails(_ => false);

        Assert.Empty(report.Stranded);
        Assert.Equal(WorkerMergeState.Merged, queue.GetState("merged"));
        Assert.Equal(WorkerMergeState.Discarded, queue.GetState("dropped"));
        Assert.True(queue.CanMerge("merged", out var mergedReason) is false && mergedReason == "already merged");
    }

    /// <summary>
    /// An entry with a verification GENUINELY in flight is skipped, because by construction it has a jail
    /// it is running in — and because stranding a live run would put the stranded sentence on the one row
    /// where "verifying" is the truth.
    /// </summary>
    [Fact]
    public async Task ReconcileJails_ShouldSkipAnEntryWithARunActuallyInFlight()
    {
        var release = new TaskCompletionSource();
        MergeQueue queue = null!;
        queue = NewQueue(run: async (id, ct) =>
        {
            await release.Task.ConfigureAwait(false);
            return new VerificationRecord(id, "sha0", true, "l", "c", "h", DateTimeOffset.UnixEpoch);
        });

        queue.EnsureEntry("a", MergeEntryOrigin.Local);
        var inFlight = queue.RunVerificationAsync("a", CancellationToken.None);
        Assert.True(queue.IsVerificationInFlight("a"));

        var report = queue.ReconcileJails(_ => false);

        Assert.Empty(report.Stranded);
        Assert.Null(queue.HasLiveJail("a"));

        release.SetResult();
        await inFlight;
    }

    /// <summary>
    /// Nothing about a stranding is persisted, and that is deliberate. Liveness is a MEASUREMENT of the
    /// container engine, not a decision this queue made, and a measurement in SQLite outlives its own truth
    /// — the row would go on asserting "stranded" after a resume, from a daemon that never looked again.
    /// A fresh queue over the same store therefore comes back unmeasured.
    /// </summary>
    [Fact]
    public void ReconcileJails_ShouldNotPersistLivenessAcrossARestart()
    {
        var store = new InMemoryMergeQueueStore();
        var queue = NewQueue(store);
        queue.EnsureEntry("a", MergeEntryOrigin.Local);
        queue.ReconcileJails(_ => false);
        Assert.False(queue.HasLiveJail("a"));

        // The restart: a brand-new queue over the SAME store, exactly as MergeQueueProvisioner rebuilds one.
        var rebuilt = NewQueue(store);

        Assert.Equal(WorkerMergeState.Working, rebuilt.GetState("a"));
        Assert.Null(rebuilt.HasLiveJail("a"));
        Assert.DoesNotContain(
            store.LoadAll("repo").Select(r => r.State), s => s.Contains("trand", StringComparison.Ordinal));
    }

    /// <summary>
    /// A stranded entry stays fully actionable by a human: Discard still works (with the honest
    /// <c>from_state</c>), and the stranding never pre-empts that decision.
    /// </summary>
    [Fact]
    public void ReconcileJails_ShouldLeaveTheHumanDiscardPathIntact()
    {
        var audit = new InMemoryAuditLog();
        var queue = NewQueue(audit: audit);
        queue.EnsureEntry("a", MergeEntryOrigin.Local);
        queue.ReconcileJails(_ => false);

        Assert.True(queue.TryDiscard("a", "uid:1000", "its agent is long gone", out var refusal), refusal);

        Assert.Equal(WorkerMergeState.Discarded, queue.GetState("a"));
        var discard = queue.GetDiscard("a");
        Assert.NotNull(discard);
        Assert.Equal("uid:1000", discard!.By);
        Assert.Equal(WorkerMergeState.Working, discard.FromState);
    }

    /// <summary>
    /// A stale-verified entry whose jail is gone must not keep promising "re-verifying". That promise is
    /// the cascade's, and the cascade needs a jail to keep it — so with no sandbox the sentence describes
    /// work that will never start.
    /// </summary>
    [Fact]
    public async Task ReconcileJails_ShouldReplaceTheStaleCascadePromiseWhenThereIsNoJailToKeepIt()
    {
        MergeQueue queue = null!;
        queue = new MergeQueue(
            "repo", "sha0", new InMemoryMergeQueueStore(), new InMemoryVerificationStore(),
            (id, ct) => Task.FromResult(new VerificationRecord(
                id, queue.CurrentMainSha, true, "l", "c", "h", DateTimeOffset.UnixEpoch)),
            // A no-op requeue so the entry stays parked at StaleVerified for the assertion.
            requeue: (id, ct) => Task.CompletedTask);

        await queue.RunVerificationAsync("a", CancellationToken.None);
        queue.NotifyMainMoved("sha1");
        Assert.Equal(WorkerMergeState.StaleVerified, queue.GetState("a"));
        Assert.True(queue.CanMerge("a", out var before) is false && before == "verification is stale — re-verifying");

        queue.ReconcileJails(_ => false);

        Assert.Equal(WorkerMergeState.StaleVerified, queue.GetState("a"));
        Assert.False(queue.CanMerge("a", out var after));
        Assert.Equal(MergeQueue.StrandedReason, after);
    }

    /// <summary>
    /// A cancelled entry (the P2-12 closed-PR path — gone, not terminal) must not leave a liveness mark
    /// behind for an id that is no longer in the queue at all.
    /// </summary>
    [Fact]
    public void ReconcileJails_ShouldForgetAnEntryThatLeavesTheQueueEntirely()
    {
        var queue = NewQueue();
        queue.EnsureEntry("pr-7", MergeEntryOrigin.External);
        queue.ReconcileJails(_ => false);
        Assert.False(queue.HasLiveJail("pr-7"));

        queue.Cancel("pr-7");
        Assert.Null(queue.HasLiveJail("pr-7"));

        // Re-materialising the same PR gets a clean entry, not an inherited stranding.
        queue.EnsureEntry("pr-7", MergeEntryOrigin.External);
        Assert.True(queue.CanMerge("pr-7", out var reason) is false && reason == "not verified yet");
    }
}
