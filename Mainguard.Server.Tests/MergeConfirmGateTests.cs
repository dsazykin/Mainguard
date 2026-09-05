using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Protos.V1;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Server.Tests;

/// <summary>
/// MG-11 — <c>ConfirmMerge</c> enforces the merge contract DAEMON-SIDE.
///
/// <para>It used to call <c>Leases.Confirm</c> (a silent no-op when no lease is held) and then
/// <c>ConfirmHumanMerge</c> unconditionally: no <c>CanMerge</c>, no staleness compare, no gate, and no
/// requirement that the caller hold a lease at all. Every one of those checks lived in the client review
/// cockpit, so the only thing standing between an unreviewed or stale branch and <c>Merged</c> was a
/// renderer — and a hand-written client, or a cockpit that lost a race to a co-tenant's merge, walked
/// straight past it.</para>
///
/// <para>Each test asserts the REFUSAL REASON, not merely that something was refused: a gate that refuses
/// for the wrong reason is a gate that will be "fixed" into uselessness by the next person who reads it.</para>
/// </summary>
public sealed class MergeConfirmGateTests : IDisposable
{
    // K3/§23.4 — real object ids, because ConfirmMerge now screens the sha the CALLER reports for shape
    // before it records anything. These used to be "main-sha-0000"-style placeholders: readable, and
    // exactly the kind of value the daemon must not accept as a claim about what a ref now holds.
    private const string MainSha = "0000000000000000000000000000000000000aa0";
    private const string PostMergeSha = "0000000000000000000000000000000000000bb1";
    private const string CoTenantMergedSha = "0000000000000000000000000000000000000cc9";
    private const string VerifiedBranchSha = "0000000000000000000000000000000000000dd2";
    private const string AgentId = "loom-11";

    // A fresh handle per test. The daemon's merge-lease store is DB-backed and — see the note on
    // DaemonFixture's isolation in the report — in-proc hosts can end up sharing one daemon DB, so a
    // constant handle would let one test's outstanding lease refuse the next test's BeginMerge. Keying on
    // the test instance (xUnit constructs one per test) makes each test's serialization domain its own.
    private readonly string _repoHandle = "repo-mg11-" + Guid.NewGuid().ToString("N");
    private IMergeLeaseStore? _leases;

    /// <summary>Hands back any lease a test deliberately left outstanding, so nothing survives the run.</summary>
    public void Dispose()
    {
        var outstanding = _leases?.GetOutstanding(_repoHandle);
        if (outstanding is not null)
        {
            _leases!.Release(_repoHandle, outstanding.LeaseId);
        }
    }

    [Fact]
    public async Task ConfirmMerge_WithNoHeldLease_IsRefused()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        var queue = await SeedVerifiedQueueAsync(host);

        // A caller that never called BeginMerge — the shape a hand-written client takes when it skips the
        // cockpit entirely. Previously this confirmed the merge outright.
        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            LeaseId = "fabricated-lease",
            NewMainSha = PostMergeSha,
        }, headers).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("No outstanding merge lease", ex.Status.Detail);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
    }

    [Fact]
    public async Task ConfirmMerge_WithAnotherAgentsLease_IsRefused()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        var queue = await SeedVerifiedQueueAsync(host, extraAgents: "other-agent");

        // The lease belongs to "other-agent"; confirming AgentId under it would let one branch ride
        // another's authorization — the per-repo lease is not a per-repo free pass.
        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = "other-agent" }, headers);
        Assert.True(begun.Granted);

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            LeaseId = begun.LeaseId,
            NewMainSha = PostMergeSha,
        }, headers).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("No outstanding merge lease", ex.Status.Detail);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
    }

    [Fact]
    public async Task ConfirmMerge_WhenMainMovedAfterBeginMerge_IsRefusedAsStale()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        var queue = await SeedVerifiedQueueAsync(host);

        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers);
        Assert.True(begun.Granted);

        // A co-tenant's merge lands between BeginMerge and ConfirmMerge: the branch is now Verified@old.
        // The old code confirmed it anyway — the exact "confirmed while still Verified@old" window.
        queue.NotifyMainMoved(CoTenantMergedSha);

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            LeaseId = begun.LeaseId,
            NewMainSha = PostMergeSha,
        }, headers).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("stale", ex.Status.Detail);
        Assert.Contains("main moved", ex.Status.Detail);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
    }

    [Fact]
    public async Task ConfirmMerge_WhileAFlaggedGateBlocks_IsRefusedWithTheGateReason()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        var queue = await SeedVerifiedQueueAsync(host);

        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers);
        Assert.True(begun.Granted);

        // RT-D2 fires after the lease is taken (a push lands, the resolver re-runs). The branch rewrote its
        // own test command, so it must not reach Merged on an unacknowledged flag.
        Context(host).ChangedTestCommand!.SetFlagged(AgentId, changed: true);

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            LeaseId = begun.LeaseId,
            NewMainSha = PostMergeSha,
        }, headers).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("test command changed", ex.Status.Detail);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
    }

    [Fact]
    public async Task BeginMerge_WhileAGateBlocks_IsNotGranted_AndDoesNotStrandTheLease()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        await SeedVerifiedQueueAsync(host);
        Context(host).ChangedTestCommand!.SetFlagged(AgentId, changed: true);

        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers);

        Assert.False(begun.Granted);
        Assert.Contains("test command changed", begun.Reason);
        // A refused BeginMerge must hand the lease back, or one blocked branch freezes every merge on the
        // repo until the daemon restarts.
        Assert.Null(host.Services.GetRequiredService<IMergeLeaseStore>().GetOutstanding(_repoHandle));
    }

    [Fact]
    public async Task AcknowledgeFlaggedChange_ClearsTheGate_AndTheMergeThenConfirms()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        var queue = await SeedVerifiedQueueAsync(host);
        Context(host).ChangedTestCommand!.SetFlagged(AgentId, changed: true);

        // The human acknowledges the RT-D2 item over the RPC — the daemon owns the ledger the gate reads,
        // so this is what actually unblocks the merge (the cockpit's local ack never reached it).
        var ack = await client.AcknowledgeFlaggedChangeAsync(new AcknowledgeFlaggedChangeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            ItemId = "changed-test-command",
        }, headers);
        Assert.True(ack.Acknowledged);
        Assert.True(ack.CanMerge);

        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers);
        Assert.True(begun.Granted);

        var confirmed = await client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            LeaseId = begun.LeaseId,
            NewMainSha = PostMergeSha,
        }, headers);

        Assert.True(confirmed.Confirmed);
        Assert.Equal(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Equal(PostMergeSha, queue.CurrentMainSha);
        // The idempotency record is written only on a merge that was actually authorized.
        Assert.Null(host.Services.GetRequiredService<IMergeLeaseStore>().GetOutstanding(_repoHandle));
    }

    // ---- K3/§23.4: the post-merge sha is a CLAIM, and it is now screened -------------------------

    /// <summary>
    /// The sha main allegedly moved to is a claim, made by the caller, about a ref on the caller's own
    /// machine. Nothing here used to look at it: the daemon wrote it into the idempotency record, set the
    /// queue's authoritative main to it, and fired the cascade at every co-tenant on that basis. A wrong
    /// value reparents and re-verifies every co-tenant against a main that may not exist, and
    /// <c>CanMerge</c> then compares its evidence against a phantom forever.
    /// </summary>
    [Fact]
    public async Task ConfirmMerge_WithAPostMergeShaThatIsNotACommitId_IsRefused_AndRecordsNothing()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        var queue = await SeedVerifiedQueueAsync(host);

        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers);
        Assert.True(begun.Granted);

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            LeaseId = begun.LeaseId,
            NewMainSha = "whatever-the-client-says",
        }, headers).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("not a commit id", ex.Status.Detail);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Equal(MainSha, queue.CurrentMainSha); // the queue's main was NOT walked to the claim
        // The lease is handed back rather than stranding the repo behind a refused confirm.
        Assert.Null(host.Services.GetRequiredService<IMergeLeaseStore>().GetOutstanding(_repoHandle));
    }

    /// <summary>A confirm reporting the main it was authorized against is a merge that moved nothing.</summary>
    [Fact]
    public async Task ConfirmMerge_ReportingTheMainItWasAuthorizedAgainst_IsRefused()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        var queue = await SeedVerifiedQueueAsync(host);

        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers);
        Assert.True(begun.Granted);
        Assert.Equal(MainSha, begun.ExpectedMainSha);

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            LeaseId = begun.LeaseId,
            NewMainSha = MainSha,
        }, headers).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("nothing moved", ex.Status.Detail);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
    }

    /// <summary>
    /// The exact one. A LOCAL entry merges by <c>git merge --ff-only agent/&lt;id&gt;</c>, and a
    /// fast-forward leaves main AT the source's tip — so the sha main moved to must BE the
    /// <c>agent/&lt;id&gt;</c> tip the queue verified. The daemon put that sha on the lease itself at
    /// <c>BeginMerge</c>, so the caller's claim is checkable against the daemon's own record without
    /// reading the caller's repository at all.
    /// </summary>
    [Fact]
    public async Task ConfirmMerge_ReportingAMainThatIsNotTheVerifiedBranchTip_IsRefused()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        var queue = await SeedVerifiedQueueAsync(host, branchSha: VerifiedBranchSha);

        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers);
        Assert.True(begun.Granted);
        // The grant carries BOTH halves of the identity, so the client merges the branch the daemon
        // authorized rather than whatever its own projection last saw.
        Assert.Equal(VerifiedBranchSha, begun.ExpectedBranchSha);

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            LeaseId = begun.LeaseId,
            NewMainSha = PostMergeSha, // well-formed, moved, and not the branch that was authorized
        }, headers).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("not the branch this merge was authorized for", ex.Status.Detail);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Equal(MainSha, queue.CurrentMainSha);
    }

    /// <summary>
    /// The control, and the one that makes the test above measure the identity rather than a confirm that
    /// refuses everything: reporting the verified branch tip — what a real fast-forward leaves behind —
    /// confirms.
    /// </summary>
    [Fact]
    public async Task ConfirmMerge_ReportingTheVerifiedBranchTip_Confirms()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        var queue = await SeedVerifiedQueueAsync(host, branchSha: VerifiedBranchSha);

        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers);
        Assert.True(begun.Granted);

        var confirmed = await client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            LeaseId = begun.LeaseId,
            NewMainSha = VerifiedBranchSha,
        }, headers);

        Assert.True(confirmed.Confirmed);
        Assert.Equal(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Equal(VerifiedBranchSha, queue.CurrentMainSha);
    }

    /// <summary>
    /// The late confirm. The worker pushed between BeginMerge and ConfirmMerge, so the invalidator walked
    /// the row off Verified and the gate refuses on state — but the client merged the VERIFIED tip (the
    /// K2 identity guarantees that), and the reported main is exactly that tip. The reviewed bytes landed
    /// on the user's main; the daemon records that, late and under its own source, instead of leaving the
    /// repository and the queue permanently disagreeing.
    /// </summary>
    [Fact]
    public async Task ConfirmMerge_WhenTheGateRefusesButTheReportedMainIsTheAuthorizedTip_RecordsTheMergeLate()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        var queue = await SeedVerifiedQueueAsync(host, branchSha: VerifiedBranchSha);

        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers);
        Assert.True(begun.Granted);
        Assert.Equal(VerifiedBranchSha, begun.ExpectedBranchSha);

        // The worker pushes while the human is merging: the row is no longer Verified.
        Assert.True(queue.NotifyBranchAdvanced(AgentId, CoTenantMergedSha));
        Assert.Equal(WorkerMergeState.Working, queue.GetState(AgentId));

        var confirmed = await client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            LeaseId = begun.LeaseId,
            NewMainSha = VerifiedBranchSha,
        }, headers);

        Assert.True(confirmed.Confirmed);
        Assert.Contains("recorded after the fact", confirmed.Note);
        Assert.Equal(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Equal(VerifiedBranchSha, queue.CurrentMainSha);
        Assert.Null(_leases!.GetOutstanding(_repoHandle));
    }

    /// <summary>
    /// The other arm: a gate refusal whose reported sha is NOT provably the authorized tip (here the lease
    /// carries no branch sha at all) keeps the lease OUTSTANDING. Releasing it used to rest on a boot
    /// reconcile that never ran and could not have seen a released lease anyway; held, it is exactly what
    /// the queue-creation and on-demand reconciles act on.
    /// </summary>
    [Fact]
    public async Task ConfirmMerge_RefusedAtTheGate_WithoutTheAuthorizedTip_KeepsTheLeaseOutstanding()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        var queue = await SeedVerifiedQueueAsync(host);

        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers);
        Assert.True(begun.Granted);
        Assert.Equal("", begun.ExpectedBranchSha);

        Assert.True(queue.NotifyBranchAdvanced(AgentId, CoTenantMergedSha));

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            LeaseId = begun.LeaseId,
            NewMainSha = PostMergeSha,
        }, headers).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("stays outstanding", ex.Status.Detail);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Equal(begun.LeaseId, _leases!.GetOutstanding(_repoHandle)?.LeaseId);
    }

    // ---- helpers ---------------------------------------------------------

    private static (MergeQueueService.MergeQueueServiceClient Client, Metadata Headers) Client(DaemonFixture host)
        => (new MergeQueueService.MergeQueueServiceClient(host.CreateChannel()), host.AuthHeaders());

    private MergeQueueContext Context(DaemonFixture host)
        => host.Services.GetRequiredService<IMergeQueueRegistry>().Resolve(_repoHandle)!;

    /// <summary>
    /// Registers a live queue for the handle with <see cref="AgentId"/> Verified against
    /// <see cref="MainSha"/> — the state a branch is in the instant before a human merges it. Built with the
    /// daemon's own lease-store singleton so the lease checks under test are the real ones.
    /// </summary>
    private async Task<MergeQueue> SeedVerifiedQueueAsync(
        DaemonFixture host, string branchSha = "", params string[] extraAgents)
    {
        var registry = host.Services.GetRequiredService<MergeQueueRegistry>();
        var leases = host.Services.GetRequiredService<IMergeLeaseStore>();
        _leases = leases;
        var changed = new ChangedTestCommandGate();

        MergeQueue queue = null!;
        queue = new MergeQueue(
            repoHash: _repoHandle,
            currentMainSha: MainSha,
            store: new InMemoryMergeQueueStore(),
            verifications: new InMemoryVerificationStore(),
            // BranchSha defaults to "" — the pre-K3 shape, which every identity compare reads as "not
            // measured" and declines to answer on. The tests that are ABOUT the identity pass one.
            runVerification: (id, _) => Task.FromResult(new VerificationRecord(
                id, queue.CurrentMainSha, Passed: true, LogArtifactPath: "", ResolvedCommand: "npm test",
                ConfigHash: "cfg", When: DateTimeOffset.UtcNow, BranchSha: branchSha)),
            // No re-verify on the cascade: the tests want the intermediate stale window to stay observable.
            requeue: (_, _) => Task.CompletedTask,
            gates: new IMergeGate[] { changed });

        registry.Register(_repoHandle, new MergeQueueContext(queue, leases) { ChangedTestCommand = changed });

        await queue.RunVerificationAsync(AgentId, CancellationToken.None);
        foreach (var extra in extraAgents)
        {
            await queue.RunVerificationAsync(extra, CancellationToken.None);
        }

        Assert.True(queue.CanMerge(AgentId, out _));
        return queue;
    }
}
