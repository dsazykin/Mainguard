using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Services;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Xunit;
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Tests;

/// <summary>
/// P2-12 merge-path dispatch (plan §6 test 6 / TI-P2-12 6): the pluggable merge step routes by the queue
/// entry's <see cref="MergeEntryOrigin"/> — a local agent through the foreground service, an external PR
/// through the host merge API — and BOTH fire the queue's <c>NotifyMainMoved</c> cascade.
/// </summary>
public class MergeDispatchTests
{
    private const string RepoPath = "/repo";
    private const string RepoHash = "hash0";

    /// <summary>
    /// Stands in for the external transport (host PR merge + local reconcile). It records the calls that
    /// reached it and, by default, reports the merge as landed on <see cref="MergedSha"/>.
    /// <see cref="OnMerge"/> is the seam the MG-23 tests use to hold a merge open (concurrency) or to make
    /// the transport refuse.
    /// </summary>
    private sealed class RecordingExternalMerge : IExternalPrMergeExecutor
    {
        private int _mergeCalls;

        public int MergeCalls => Volatile.Read(ref _mergeCalls);
        public string? LastAgentId { get; private set; }

        /// <summary>The sha the transport reports main landed on (the real one proves it against git).</summary>
        public string MergedSha { get; set; } = "sha-ext-1";

        /// <summary>Runs inside <see cref="MergeExternalPrAsync"/>, after the call is counted. Returning a
        /// non-null result makes the transport refuse with it.</summary>
        public Func<string, CancellationToken, Task<ForegroundMergeResult?>>? OnMerge { get; set; }

        public async Task<ForegroundMergeResult> MergeExternalPrAsync(
            ForegroundMergeRequest request, MergeLeaseRow lease, CancellationToken ct)
        {
            Interlocked.Increment(ref _mergeCalls);
            LastAgentId = request.AgentId;
            if (OnMerge is not null && await OnMerge(request.AgentId, ct).ConfigureAwait(false) is { } refusal)
            {
                return refusal;
            }

            return new ForegroundMergeResult(true, MergedSha, CasLost: false, Reason: null);
        }
    }

    /// <summary>Mimics the real foreground service: on merge it fires the daemon-wired <c>onMerged</c>
    /// callback (→ <c>queue.ConfirmHumanMerge</c> → <c>NotifyMainMoved</c>), exactly as P2-10 does.</summary>
    private sealed class FakeForeground : IForegroundMergeService
    {
        private readonly Action<string, string> _onMerged;
        private readonly string _newSha;
        public bool Called { get; private set; }

        public FakeForeground(Action<string, string> onMerged, string newSha)
        {
            _onMerged = onMerged;
            _newSha = newSha;
        }

        public ForegroundMergeResult MergeAgentBranch(ForegroundMergeRequest request)
        {
            Called = true;
            _onMerged(request.AgentId, _newSha);
            return new ForegroundMergeResult(true, _newSha, CasLost: false, Reason: null);
        }
    }

    private static MergeQueue BuildVerifiedQueue(string agentId, MergeEntryOrigin origin, Action<string, string> onMerged) =>
        BuildVerifiedQueue((agentId, origin));

    /// <summary>A queue at <c>main@sha0</c> with every listed entry verified against that sha.</summary>
    private static MergeQueue BuildVerifiedQueue(params (string AgentId, MergeEntryOrigin Origin)[] entries)
    {
        var queue = new MergeQueue(
            RepoHash, "sha0",
            new InMemoryMergeQueueStore(),
            new InMemoryVerificationStore(),
            runVerification: (id, ct) => Task.FromResult(new VerificationRecord(
                id, "sha0", true, "log.txt", "npm test", "cfg", DateTimeOffset.UnixEpoch)),
            requeue: (id, ct) => Task.CompletedTask);

        foreach (var (agentId, origin) in entries)
        {
            queue.EnsureEntry(agentId, origin);
            queue.RunVerificationAsync(agentId, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(WorkerMergeState.Verified, queue.GetState(agentId));
        }

        return queue;
    }

    private static MergeDispatch BuildDispatch(
        MergeQueue queue, IExternalPrMergeExecutor external, IMergeLeaseStore leases) =>
        new(
            new FakeForeground((id, sha) => { }, "unused"), external, leases,
            resolveQueue: rh => rh == RepoHash ? queue : null);

    [Fact]
    public async Task MergePathDispatch_ShouldUseHostApiForPrEntries_AndLocalForegroundForLocalAgents()
    {
        var notified = new List<string>();

        // ---- Local origin → foreground service; NotifyMainMoved via its onMerged wiring ----
        var localQueue = BuildVerifiedQueue("local", MergeEntryOrigin.Local, (id, sha) => { });
        Action<string, string> localOnMerged = (id, sha) =>
        {
            notified.Add(id);
            localQueue.ConfirmHumanMerge(id, sha); // fires NotifyMainMoved
        };
        var localForeground = new FakeForeground(localOnMerged, "sha-local-1");
        var localExternal = new RecordingExternalMerge();
        var localDispatch = new MergeDispatch(
            localForeground, localExternal, new InMemoryMergeLeaseStore(),
            resolveQueue: rh => rh == RepoHash ? localQueue : null);

        var localOutcome = await localDispatch.DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "local", "sha0"), CancellationToken.None);

        Assert.True(localOutcome.Merged);
        Assert.True(localForeground.Called);                 // routed to the foreground service
        Assert.Equal(0, localExternal.MergeCalls);           // NOT the host transport
        Assert.Equal(WorkerMergeState.Merged, localQueue.GetState("local"));
        Assert.Equal("sha-local-1", localQueue.CurrentMainSha); // NotifyMainMoved fired
        Assert.Contains("local", notified);

        // ---- External origin → host transport, then NotifyMainMoved after the merged sha lands ----
        var extQueue = BuildVerifiedQueue("pr-7", MergeEntryOrigin.External, (id, sha) => { });
        var extExternal = new RecordingExternalMerge();
        var extForeground = new FakeForeground((id, sha) => { }, "unused");
        var extDispatch = new MergeDispatch(
            extForeground, extExternal, new InMemoryMergeLeaseStore(),
            resolveQueue: rh => rh == RepoHash ? extQueue : null);

        var extOutcome = await extDispatch.DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "pr-7", "sha0"), CancellationToken.None);

        Assert.True(extOutcome.Merged);
        Assert.False(extForeground.Called);                  // NOT the foreground service
        Assert.Equal(1, extExternal.MergeCalls);             // routed to the host transport
        Assert.Equal("pr-7", extExternal.LastAgentId);
        Assert.Equal(WorkerMergeState.Merged, extQueue.GetState("pr-7"));
        Assert.Equal("sha-ext-1", extQueue.CurrentMainSha);  // NotifyMainMoved fired for the external path too
    }

    // ---- MG-23: the external transport is inside the same serialization as the local one ----

    /// <summary>
    /// MG-23 (the race itself). Two external-PR merges on the SAME repo, genuinely concurrent: the first
    /// is held open inside the host merge API while the second is dispatched. Only one may reach the
    /// host — the per-repo merge lease is what makes "one outstanding merge per repository" true, and it
    /// has to cover the external transport, not just the foreground one. Before the fix the external
    /// path took no lease at all, so both calls went through and both confirmed against a main the other
    /// had already moved.
    /// </summary>
    [Fact]
    public async Task ExternalMerge_ShouldRefuseASecondConcurrentMerge_OnTheSameRepoLease()
    {
        var queue = BuildVerifiedQueue(("pr-7", MergeEntryOrigin.External), ("pr-8", MergeEntryOrigin.External));
        var leases = new InMemoryMergeLeaseStore();

        // Hold the first merge open inside the host API so the second dispatch overlaps it for real.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var external = new RecordingExternalMerge
        {
            // Only PR 7 is held. If the lease ever stops covering the external path, PR 8 must be free to
            // run to completion so this test FAILS on the merge count rather than deadlocking on the gate.
            OnMerge = async (agentId, ct) =>
            {
                if (agentId != "pr-7")
                {
                    return null;
                }

                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
                return null;
            },
        };

        var dispatch = BuildDispatch(queue, external, leases);

        var first = dispatch.DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "pr-7", "sha0"), CancellationToken.None);
        await entered.Task; // the first merge is now in flight, holding the repo's lease

        var second = await dispatch.DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "pr-8", "sha0"), CancellationToken.None);

        Assert.False(second.Merged);
        Assert.Equal("another merge is already in progress for this repository", second.Reason);
        Assert.Equal(1, external.MergeCalls);                 // the second never reached the host
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState("pr-8"));

        release.TrySetResult();
        var firstOutcome = await first;
        Assert.True(firstOutcome.Merged);
        Assert.Equal(WorkerMergeState.Merged, queue.GetState("pr-7"));
        Assert.Null(leases.GetOutstanding(RepoHash));         // the lease was confirmed, not stranded
    }

    /// <summary>
    /// MG-23 (cross-origin). The lease is one per repo, not one per transport: while a foreground merge
    /// holds it, an external-PR merge on the same repo must wait — otherwise the two land on main
    /// simultaneously and each confirms against a sha the other invalidated.
    /// </summary>
    [Fact]
    public async Task ExternalMerge_ShouldWait_WhileAForegroundMergeHoldsTheRepoLease()
    {
        var queue = BuildVerifiedQueue(("pr-7", MergeEntryOrigin.External));
        var leases = new InMemoryMergeLeaseStore();

        // The Windows foreground merge takes the repo's lease first (ForegroundMergeService.BeginMerge).
        Assert.NotNull(leases.TryBegin(RepoHash, "foreground-lease", "loom-1", "sha0", "main"));

        var external = new RecordingExternalMerge();
        var outcome = await BuildDispatch(queue, external, leases).DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "pr-7", "sha0"), CancellationToken.None);

        Assert.False(outcome.Merged);
        Assert.Equal(0, external.MergeCalls);
        Assert.Equal("another merge is already in progress for this repository", outcome.Reason);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState("pr-7"));
    }

    /// <summary>
    /// MG-23 (the CAS). The host merge API takes no old-OID, so the expected <c>main@sha</c> is compared
    /// under the lease instead. A caller carrying a stale sha loses the compare-and-swap exactly like a
    /// <c>git merge --ff-only</c> refusal: nothing merges, nothing confirms, and the lease is handed back
    /// so the next merge can proceed.
    /// </summary>
    [Fact]
    public async Task ExternalMerge_ShouldLoseTheCas_WhenTheExpectedMainShaIsStale()
    {
        var queue = BuildVerifiedQueue(("pr-7", MergeEntryOrigin.External));
        var leases = new InMemoryMergeLeaseStore();
        var external = new RecordingExternalMerge();

        // The entry is verified against sha0 (CanMerge is true), but the caller's request was built from
        // an older read of main — the freshness CAS is what catches that.
        var outcome = await BuildDispatch(queue, external, leases).DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "pr-7", "sha-older"), CancellationToken.None);

        Assert.True(queue.CanMerge("pr-7", out _));           // the gate alone would have let this through
        Assert.False(outcome.Merged);
        Assert.True(outcome.CasLost);
        Assert.Equal(0, external.MergeCalls);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState("pr-7"));
        Assert.Null(leases.GetOutstanding(RepoHash));         // released, not stranded
    }

    /// <summary>
    /// MG-23 (the gate). External entries obey <c>CanMerge</c> like local ones — here the P2-14 kill
    /// switch has frozen the queue, and a frozen queue must refuse an external merge too. Before the fix
    /// the external path never consulted the gate at all.
    /// </summary>
    [Fact]
    public async Task ExternalMerge_ShouldObeyCanMerge_WhenTheQueueIsFrozen()
    {
        var queue = BuildVerifiedQueue(("pr-7", MergeEntryOrigin.External));
        queue.IsFrozen = true;
        var leases = new InMemoryMergeLeaseStore();
        var external = new RecordingExternalMerge();

        var outcome = await BuildDispatch(queue, external, leases).DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "pr-7", "sha0"), CancellationToken.None);

        Assert.False(outcome.Merged);
        Assert.Equal(0, external.MergeCalls);
        Assert.Contains("frozen", outcome.Reason!);
        Assert.Null(leases.GetOutstanding(RepoHash));
    }

    /// <summary>
    /// MG-23 (host refusal + no stranded lease). A host that refuses the merge — the PR stopped being
    /// mergeable, or it was already merged upstream — is the external transport's lost CAS. It must not
    /// crash the caller, must not confirm, and must release the lease so the repo is not wedged.
    /// </summary>
    [Fact]
    public async Task ExternalMerge_ShouldReportCasLost_AndReleaseTheLease_WhenTheHostRefuses()
    {
        var queue = BuildVerifiedQueue(("pr-7", MergeEntryOrigin.External));
        var leases = new InMemoryMergeLeaseStore();
        var external = new RecordingExternalMerge
        {
            OnMerge = (agentId, ct) => Task.FromResult<ForegroundMergeResult?>(
                new ForegroundMergeResult(false, null, CasLost: true, "the host refused the merge")),
        };

        var dispatch = BuildDispatch(queue, external, leases);
        var outcome = await dispatch.DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "pr-7", "sha0"), CancellationToken.None);

        Assert.False(outcome.Merged);
        Assert.True(outcome.CasLost);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState("pr-7"));
        Assert.Null(leases.GetOutstanding(RepoHash));

        // And the repo is genuinely usable again: the very next merge attempt can take the lease.
        external.OnMerge = null;
        var retry = await dispatch.DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "pr-7", "sha0"), CancellationToken.None);
        Assert.True(retry.Merged);
    }
}
