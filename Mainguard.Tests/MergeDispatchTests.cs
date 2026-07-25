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

    /// <summary>Records the host merge call; everything else is an unused stub. <see cref="OnMerge"/> is
    /// the seam the MG-23 tests use to hold a merge open (concurrency) or make the host refuse it.</summary>
    private sealed class RecordingPrService : IPullRequestService
    {
        private int _mergeCalls;

        public int MergeCalls => Volatile.Read(ref _mergeCalls);
        public int LastMergedNumber { get; private set; }

        /// <summary>Runs inside <see cref="MergeAsync"/>, after the call is counted.</summary>
        public Func<int, CancellationToken, Task>? OnMerge { get; set; }

        public async Task<PullRequestItem> MergeAsync(string repoPath, int number, PullRequestMergeMethod method, CancellationToken ct)
        {
            Interlocked.Increment(ref _mergeCalls);
            LastMergedNumber = number;
            if (OnMerge is not null)
            {
                await OnMerge(number, ct).ConfigureAwait(false);
            }

            return new PullRequestItem { Number = number, State = PullRequestState.Merged };
        }

        public bool IsSupported(string repoPath) => true;
        public Task<IReadOnlyList<PullRequestItem>> ListAsync(string repoPath, PullRequestState filter, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PullRequestItem>>(Array.Empty<PullRequestItem>());
        public Task<PullRequestDetail> GetAsync(string repoPath, int number, CancellationToken ct) => Task.FromResult(new PullRequestDetail());
        public Task<PullRequestItem> CreateAsync(string repoPath, CreatePullRequest request, CancellationToken ct) => Task.FromResult(new PullRequestItem());
        public Task CloseAsync(string repoPath, int number, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(string repoPath, int number, CancellationToken ct) => Task.FromResult<IReadOnlyList<PullRequestReview>>(Array.Empty<PullRequestReview>());
        public Task<IReadOnlyList<ReviewComment>> GetReviewCommentsAsync(string repoPath, int number, CancellationToken ct) => Task.FromResult<IReadOnlyList<ReviewComment>>(Array.Empty<ReviewComment>());
        public Task<PullRequestReview> SubmitReviewAsync(string repoPath, int number, SubmitReview review, CancellationToken ct) => Task.FromResult(new PullRequestReview());
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
        MergeQueue queue, IPullRequestService pr, IMergeLeaseStore leases, string mergedSha = "sha-ext-1") =>
        new(
            new FakeForeground((id, sha) => { }, "unused"), pr, leases,
            resolveQueue: rh => rh == RepoHash ? queue : null,
            fetchMergedMainSha: (req, ct) => Task.FromResult(mergedSha));

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
        var localPr = new RecordingPrService();
        var localDispatch = new MergeDispatch(
            localForeground, localPr, new InMemoryMergeLeaseStore(),
            resolveQueue: rh => rh == RepoHash ? localQueue : null,
            fetchMergedMainSha: (req, ct) => Task.FromResult("unused"));

        var localOutcome = await localDispatch.DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "local", "sha0"), CancellationToken.None);

        Assert.True(localOutcome.Merged);
        Assert.True(localForeground.Called);                 // routed to the foreground service
        Assert.Equal(0, localPr.MergeCalls);                 // NOT the host API
        Assert.Equal(WorkerMergeState.Merged, localQueue.GetState("local"));
        Assert.Equal("sha-local-1", localQueue.CurrentMainSha); // NotifyMainMoved fired
        Assert.Contains("local", notified);

        // ---- External origin → host merge API, then NotifyMainMoved after the merged sha lands ----
        var extQueue = BuildVerifiedQueue("pr-7", MergeEntryOrigin.External, (id, sha) => { });
        var extPr = new RecordingPrService();
        var extForeground = new FakeForeground((id, sha) => { }, "unused");
        var extDispatch = new MergeDispatch(
            extForeground, extPr, new InMemoryMergeLeaseStore(),
            resolveQueue: rh => rh == RepoHash ? extQueue : null,
            fetchMergedMainSha: (req, ct) => Task.FromResult("sha-ext-1"));

        var extOutcome = await extDispatch.DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "pr-7", "sha0", PrNumber: 7), CancellationToken.None);

        Assert.True(extOutcome.Merged);
        Assert.False(extForeground.Called);                  // NOT the foreground service
        Assert.Equal(1, extPr.MergeCalls);                   // routed to the host merge API
        Assert.Equal(7, extPr.LastMergedNumber);
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
        var pr = new RecordingPrService
        {
            // Only PR 7 is held. If the lease ever stops covering the external path, PR 8 must be free to
            // run to completion so this test FAILS on the merge count rather than deadlocking on the gate.
            OnMerge = (number, ct) =>
            {
                if (number != 7)
                {
                    return Task.CompletedTask;
                }

                entered.TrySetResult();
                return release.Task;
            },
        };

        var dispatch = BuildDispatch(queue, pr, leases);

        var first = dispatch.DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "pr-7", "sha0", PrNumber: 7), CancellationToken.None);
        await entered.Task; // the first merge is now in flight, holding the repo's lease

        var second = await dispatch.DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "pr-8", "sha0", PrNumber: 8), CancellationToken.None);

        Assert.False(second.Merged);
        Assert.Equal("another merge is already in progress for this repository", second.Reason);
        Assert.Equal(1, pr.MergeCalls);                       // the second never reached the host API
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

        var pr = new RecordingPrService();
        var outcome = await BuildDispatch(queue, pr, leases).DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "pr-7", "sha0", PrNumber: 7), CancellationToken.None);

        Assert.False(outcome.Merged);
        Assert.Equal(0, pr.MergeCalls);
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
        var pr = new RecordingPrService();

        // The entry is verified against sha0 (CanMerge is true), but the caller's request was built from
        // an older read of main — the freshness CAS is what catches that.
        var outcome = await BuildDispatch(queue, pr, leases).DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "pr-7", "sha-older", PrNumber: 7), CancellationToken.None);

        Assert.True(queue.CanMerge("pr-7", out _));           // the gate alone would have let this through
        Assert.False(outcome.Merged);
        Assert.True(outcome.CasLost);
        Assert.Equal(0, pr.MergeCalls);
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
        var pr = new RecordingPrService();

        var outcome = await BuildDispatch(queue, pr, leases).DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "pr-7", "sha0", PrNumber: 7), CancellationToken.None);

        Assert.False(outcome.Merged);
        Assert.Equal(0, pr.MergeCalls);
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
        var pr = new RecordingPrService
        {
            OnMerge = (number, ct) => throw new InvalidOperationException("Base branch was modified."),
        };

        var dispatch = BuildDispatch(queue, pr, leases);
        var outcome = await dispatch.DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "pr-7", "sha0", PrNumber: 7), CancellationToken.None);

        Assert.False(outcome.Merged);
        Assert.True(outcome.CasLost);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState("pr-7"));
        Assert.Null(leases.GetOutstanding(RepoHash));

        // And the repo is genuinely usable again: the very next merge attempt can take the lease.
        pr.OnMerge = null;
        var retry = await dispatch.DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "pr-7", "sha0", PrNumber: 7), CancellationToken.None);
        Assert.True(retry.Merged);
    }
}
