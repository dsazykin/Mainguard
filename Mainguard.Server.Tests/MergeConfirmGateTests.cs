using System;
using System.IO;
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
    /// F36 — the late-confirm path is a GATE, not a formality.
    ///
    /// <para>The worker pushed between BeginMerge and ConfirmMerge, so the invalidator walked the row off
    /// Verified and the gate refuses on state. The caller reports the tip the lease authorized, which is
    /// exactly what a real fast-forward leaves behind — and which is also exactly what a client that
    /// merged nothing would report, since the daemon put that value on the lease and handed it over at
    /// BeginMerge.</para>
    ///
    /// <para><b>The claim used to be the proof.</b> The identity check at (1.5) already refuses every
    /// Local confirm whose reported sha is NOT the authorized tip, so the condition guarding this branch
    /// was true by construction the moment it was reached: every gate refusal became a terminal
    /// <c>Merged</c> plus a fired stale cascade, and <c>confirm_rpc_late</c> was the source recorded for
    /// all of them. Here the daemon has no mirror it can read main from, so it cannot observe the merge —
    /// and an unobservable merge is refused, with the lease kept for the reconcile.</para>
    /// </summary>
    [Fact]
    public async Task ConfirmMerge_WhenTheGateRefusesAndTheMergeCannotBeObserved_IsRefused()
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

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            LeaseId = begun.LeaseId,
            NewMainSha = VerifiedBranchSha, // the claim, and nothing but the claim
        }, headers).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        // The refusal says BOTH halves: the gate's own reason, and that the daemon looked and could not
        // find the merge. A reader who sees only the first would go on believing the claim was checked.
        Assert.Contains("could not observe this merge on the checkout", ex.Status.Detail);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Equal(MainSha, queue.CurrentMainSha);

        // The lease is HELD, which is what the queue-creation and on-demand reconciles act on.
        Assert.NotNull(_leases!.GetOutstanding(_repoHandle));
    }

    /// <summary>
    /// The other side of F36: when the merge really did land, the daemon can SEE it and records it late
    /// under <see cref="MergeAuthorization.ConfirmRpcLateSource"/>.
    ///
    /// <para>Nothing here is asserted from the caller's message. A real checkout is built with main
    /// fast-forwarded onto the branch tip, a real mirror of it sits where the provisioner looks, and the
    /// daemon fetches main from that checkout and asks git the two questions: is main the sha this
    /// confirm reports, and does it contain the tip the lease authorized.</para>
    /// </summary>
    [Fact]
    public async Task ConfirmMerge_WhenTheGateRefusesAndTheMergeIsObservedOnTheCheckout_RecordsItAsConfirmRpcLate()
    {
        using var host = new DaemonFixture();
        var world = LandedMergeWorld.Build(host, _repoHandle);
        var (client, headers) = Client(host);
        var queue = await SeedVerifiedQueueAsync(
            host, branchSha: world.BranchSha, mainSha: world.PreMergeMainSha);

        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers);
        Assert.True(begun.Granted);
        Assert.Equal(world.BranchSha, begun.ExpectedBranchSha);

        // The worker pushes while the human is merging: the gate will refuse on state.
        Assert.True(queue.NotifyBranchAdvanced(AgentId, CoTenantMergedSha));
        Assert.Equal(WorkerMergeState.Working, queue.GetState(AgentId));

        var confirmed = await client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            LeaseId = begun.LeaseId,
            NewMainSha = world.BranchSha,
        }, headers);

        Assert.True(confirmed.Confirmed);
        Assert.Contains("recorded after the fact", confirmed.Note);
        Assert.Equal(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Equal(world.BranchSha, queue.CurrentMainSha);
        Assert.Null(_leases!.GetOutstanding(_repoHandle));

        // The record names the source, so a reader can tell a late reconciliation from an ordinary
        // confirm without inferring it from timestamps.
        // Scoped to THIS test's repo handle: in-proc daemon fixtures can share one audit chain, and the
        // agent id is a constant across the class.
        var merged = Assert.Single(
            host.Services.GetRequiredService<Mainguard.Git.Audit.IAuditLog>().Read(),
            e => e.Type == MergeQueue.MergedEvent
                 && e.Fields.GetValueOrDefault("agent") == AgentId
                 && e.Fields.GetValueOrDefault("repo") == _repoHandle);
        Assert.Equal(MergeAuthorization.ConfirmRpcLateSource, merged.Fields["source"]);
    }

    /// <summary>
    /// F37 — a branch main ALREADY contains can reach <c>Merged</c>.
    ///
    /// <para><c>merge --ff-only</c> of a contained branch exits 0 and moves nothing, so the client
    /// honestly reports the pre-merge sha — which <c>ConfirmMerge</c> refused as "nothing moved, so
    /// nothing was recorded as merged". Reachable after an Undecidable boot reconcile or a hand pull, and
    /// once there the row could reach no terminal but Discard: the only way to close a branch that HAD
    /// landed was to record that it had not. The daemon now asks git whether main contains the authorized
    /// tip, and records the truth.</para>
    /// </summary>
    [Fact]
    public async Task ConfirmMerge_WhenMainAlreadyContainsTheAuthorizedTip_RecordsItAsMerged()
    {
        using var host = new DaemonFixture();
        var world = LandedMergeWorld.Build(host, _repoHandle);
        var (client, headers) = Client(host);

        // The queue's main is ALREADY the post-merge sha — the branch is contained — so the client's
        // ff-only did nothing and it reports that same sha back.
        var queue = await SeedVerifiedQueueAsync(
            host, branchSha: world.BranchSha, mainSha: world.BranchSha);

        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers);
        Assert.True(begun.Granted);

        var confirmed = await client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            LeaseId = begun.LeaseId,
            NewMainSha = world.BranchSha, // == ExpectedMainSha: nothing moved
        }, headers);

        Assert.True(confirmed.Confirmed);
        Assert.Contains("already contained", confirmed.Note);
        Assert.Equal(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.Null(_leases!.GetOutstanding(_repoHandle));
    }

    /// <summary>
    /// ...and the control that keeps the test above from being "confirm accepts anything when nothing
    /// moved": a branch the checkout's main does NOT contain is still refused.
    /// </summary>
    [Fact]
    public async Task ConfirmMerge_WhenNothingMovedAndMainDoesNotContainTheBranch_IsStillRefused()
    {
        using var host = new DaemonFixture();
        var world = LandedMergeWorld.Build(host, _repoHandle);
        var (client, headers) = Client(host);

        // The lease's branch tip is a commit this checkout has never heard of, so `--is-ancestor` says no.
        var queue = await SeedVerifiedQueueAsync(
            host, branchSha: VerifiedBranchSha, mainSha: world.BranchSha);

        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers);
        Assert.True(begun.Granted);

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            LeaseId = begun.LeaseId,
            NewMainSha = world.BranchSha,
        }, headers).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("nothing moved", ex.Status.Detail);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
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
        DaemonFixture host, string branchSha = "", string mainSha = MainSha, params string[] extraAgents)
    {
        var registry = host.Services.GetRequiredService<MergeQueueRegistry>();
        var leases = host.Services.GetRequiredService<IMergeLeaseStore>();
        _leases = leases;
        var changed = new ChangedTestCommandGate();

        MergeQueue queue = null!;
        queue = new MergeQueue(
            repoHash: _repoHandle,
            currentMainSha: mainSha,
            store: new InMemoryMergeQueueStore(),
            verifications: new InMemoryVerificationStore(),
            // BranchSha defaults to "" — the pre-K3 shape, which every identity compare reads as "not
            // measured" and declines to answer on. The tests that are ABOUT the identity pass one.
            runVerification: (id, _) => Task.FromResult(new VerificationRecord(
                id, queue.CurrentMainSha, Passed: true, LogArtifactPath: "", ResolvedCommand: "npm test",
                ConfigHash: "cfg", When: DateTimeOffset.UtcNow, BranchSha: branchSha)),
            // No re-verify on the cascade: the tests want the intermediate stale window to stay observable.
            requeue: (_, _) => Task.CompletedTask,
            gates: new IMergeGate[] { changed },
            // The DAEMON's audit chain, so a test can read back what the queue recorded about a merge —
            // in particular which authorization source it landed under.
            audit: host.Services.GetRequiredService<Mainguard.Git.Audit.IAuditLog>());

        registry.Register(_repoHandle, new MergeQueueContext(queue, leases) { ChangedTestCommand = changed });

        await queue.RunVerificationAsync(AgentId, CancellationToken.None);
        foreach (var extra in extraAgents)
        {
            await queue.RunVerificationAsync(extra, CancellationToken.None);
        }

        Assert.True(queue.CanMerge(AgentId, out _));
        return queue;
    }

    /// <summary>
    /// A REAL repository the daemon can look at: a checkout whose <c>main</c> has been fast-forwarded onto
    /// the agent's tip, plus a bare mirror of it sitting exactly where the daemon's provisioner looks, with
    /// <c>origin</c> pointing back at the checkout.
    ///
    /// <para>This exists because F36's fix is "stop believing the caller and go and look". A fixture made
    /// of literal sha constants can only ever exercise the refusal half; the confirm half has to be
    /// measured against git, in the place the daemon actually reads main from.</para>
    /// </summary>
    private sealed record LandedMergeWorld(string PreMergeMainSha, string BranchSha)
    {
        public static LandedMergeWorld Build(DaemonFixture host, string repoHandle)
        {
            var barePath = host.Services
                .GetRequiredService<Mainguard.Agents.Agents.IAgentEnvironment>()
                .Repos.BareRepoPathFor(repoHandle);

            var checkout = Path.Combine(
                Path.GetTempPath(), "mainguard-confirm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(checkout);

            Git(checkout, "-c", "init.defaultBranch=main", "init");
            Git(checkout, "config", "user.name", "T");
            Git(checkout, "config", "user.email", "t@mainguard.local");
            Git(checkout, "config", "commit.gpgsign", "false");

            File.WriteAllText(Path.Combine(checkout, "README.md"), "seed\n");
            Git(checkout, "add", "-A");
            Git(checkout, "commit", "-m", "seed");
            var preMerge = Rev(checkout, "main");

            // The agent's commit, fast-forwarded onto main exactly as `merge --ff-only` leaves it.
            File.WriteAllText(Path.Combine(checkout, "feature.txt"), "agent work\n");
            Git(checkout, "add", "-A");
            Git(checkout, "commit", "-m", "agent commit");
            var branchSha = Rev(checkout, "main");

            // The mirror, where MergeQueueProvisioner.BareRepoPathFor says it is. Cloned from the checkout
            // so `origin` is the checkout — which is the whole mechanism the observation relies on.
            Directory.CreateDirectory(Path.GetDirectoryName(barePath)!);
            if (Directory.Exists(barePath))
            {
                Directory.Delete(barePath, recursive: true);
            }

            Git(Path.GetDirectoryName(barePath)!, "clone", "--bare", checkout, barePath);

            return new LandedMergeWorld(preMerge, branchSha);
        }

        // Plain process invocation rather than GitService.RunGit: that helper is internal to Mainguard.Git
        // and this assembly is not one of its friends. These are fixture repositories, not a git surface
        // under test.
        private static void Git(string cwd, params string[] args)
        {
            var (code, _, err) = Run(cwd, args);
            if (code != 0)
            {
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({code}): {err}");
            }
        }

        private static string Rev(string repo, string reference)
            => Run(repo, new[] { "rev-parse", "--verify", reference }).Out.Trim();

        private static (int Code, string Out, string Err) Run(string cwd, string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = cwd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("could not start git");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdout, stderr);
        }
    }
}
