using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Models;
using Mainguard.Git.Review;
using Mainguard.Protos.V1;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Server.Tests;

/// <summary>
/// P2-11 — the flagged-change gate refuses the merge <b>at the daemon</b>.
///
/// <para><b>What was wrong.</b> <see cref="FlaggedChangeGate"/> shipped complete and had no production
/// wiring at all: it was constructed in exactly one non-test place — <c>ReviewCockpitViewModel</c>'s local
/// composition branch, which the shipped app never takes — and was never registered in the daemon, never
/// passed to <c>MergeQueue</c>'s <c>gates</c>, and never consulted by <c>CanMerge</c>. The daemon's flagged
/// item projection read <see cref="ChangedTestCommandGate"/> alone. So a branch that edited a CI workflow,
/// a git hook, an executable config or a security-sensitive path — or that reached outside the scope its
/// plan was approved for — merged unflagged and unremarked, and the human review boundary existed only as
/// a renderer. This is the MG-12 / MG-10 shape: a control that is present in the source and not reached.</para>
///
/// <para><b>Why these tests live here and not beside the detector.</b> The detector was never broken; the
/// spine was. Every assertion below therefore goes through the <b>gRPC surface</b> — the refusal has to come
/// from the daemon serving <c>BeginMerge</c>, not from a ViewModel that happens to agree with it, because a
/// ViewModel is exactly where this check used to live while the daemon waved the merge through.</para>
/// </summary>
public sealed class FlaggedChangeMergeGateTests : IDisposable
{
    private const string MainSha = "main-sha-p211";
    private const string AgentId = "loom-p211";

    // A fresh handle per test: the daemon's merge-lease store is DB-backed and in-proc hosts can share one
    // daemon DB, so a constant handle would let one test's outstanding lease refuse the next test's
    // BeginMerge (the isolation MergeConfirmGateTests documents).
    private readonly string _repoHandle = "repo-p211-" + Guid.NewGuid().ToString("N");
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

    /// <summary>
    /// <b>The decisive test.</b> A managed worker whose branch touches a file outside its approved
    /// <c>TaskPlan.Scope</c> is verified, green, and refused a merge by the daemon until a human
    /// acknowledges the out-of-scope item — and the acknowledgment travels over the same
    /// <c>AcknowledgeFlaggedChange</c> RPC the coordinator is denied (contract §4).
    /// </summary>
    [Fact]
    public async Task AnOutOfScopeChange_IsRefusedAMergeByTheDaemon_UntilAcknowledged()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        var ctx = await SeedVerifiedQueueAsync(host, OutOfScopeItem());

        // (a) The daemon itself says no. CanMerge is the queue's own answer, served over gRPC.
        var can = await client.CanMergeAsync(
            new CanMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers);
        Assert.False(can.CanMerge);
        Assert.Contains("acknowledgment", can.Reason);

        // (b) The blocking item reaches the human, addressed by the id the ack RPC accepts. It used to
        //     reach nobody: the projection had no source for it, so the branch arrived as "cannot merge"
        //     with nothing to clear.
        var item = Assert.Single(await FlaggedItemsAsync(client, headers));
        Assert.Equal(FlaggedKind.OutOfApprovedScope.ToString() + "|src/payments.cs|", item.Id[..(item.Id.LastIndexOf('|') + 1)]);
        Assert.Equal("src/payments.cs", item.Path);
        Assert.Contains("outside approved scope", item.Fact);
        Assert.False(item.Acknowledged);

        // (c) BeginMerge — the RPC that actually hands out merge authority — refuses, and hands the lease
        //     straight back rather than stranding the repo behind a merge it was always going to refuse.
        var refused = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers);
        Assert.False(refused.Granted);
        Assert.Contains("acknowledgment", refused.Reason);
        Assert.Null(host.Services.GetRequiredService<IMergeLeaseStore>().GetOutstanding(_repoHandle));

        // (d) The human acknowledges THAT item. This is the act contract §4 denies the coordinator.
        var ack = await client.AcknowledgeFlaggedChangeAsync(new AcknowledgeFlaggedChangeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            ItemId = item.Id,
        }, headers);
        Assert.True(ack.Acknowledged);
        Assert.True(ack.CanMerge);

        // (e) ...and only now does the daemon grant the merge.
        var granted = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers);
        Assert.True(granted.Granted);
        Assert.True(ctx.Queue.CanMerge(AgentId, out _));
    }

    [Fact]
    public async Task APoisonedExecutableConfig_IsRefusedAMergeByTheDaemon_UntilAcknowledged()
    {
        // The risk-category arm, which is live in the daemon today (it needs no approved plan). A branch
        // that verifies GREEN while adding a postinstall that runs arbitrary shell is precisely the case
        // the verification result cannot decide.
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        await SeedVerifiedQueueAsync(host, PoisonedPackageJsonItem());

        var refused = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers);
        Assert.False(refused.Granted);
        Assert.Contains("acknowledgment", refused.Reason);

        var item = Assert.Single(await FlaggedItemsAsync(client, headers));
        Assert.Equal(RiskCategory.ExecutableConfig.ToString(), item.Category);

        var ack = await client.AcknowledgeFlaggedChangeAsync(new AcknowledgeFlaggedChangeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            ItemId = item.Id,
        }, headers);
        Assert.True(ack.Acknowledged);

        Assert.True((await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers)).Granted);
    }

    [Fact]
    public async Task AcknowledgingOneItem_LeavesTheOtherBlocking()
    {
        // Item-by-item, never "all" — a global ack is a rejection trigger, and the store exposes no such
        // method. The failure this guards is a human clearing the item they read while another went unread.
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        await SeedVerifiedQueueAsync(host, OutOfScopeItem(), PoisonedPackageJsonItem());

        var items = await FlaggedItemsAsync(client, headers);
        Assert.Equal(2, items.Count);

        var first = await client.AcknowledgeFlaggedChangeAsync(new AcknowledgeFlaggedChangeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            ItemId = items[0].Id,
        }, headers);

        Assert.True(first.Acknowledged);
        Assert.False(first.CanMerge);
        Assert.Contains("1 flagged change needs acknowledgment", first.Reason);

        Assert.False((await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers)).Granted);
    }

    [Fact]
    public async Task AcknowledgingAnItemIdTheBranchDoesNotHave_ClearsNothing()
    {
        // The gate must not be openable by naming an id. Acknowledge returns false for an unknown id, and
        // the merge stays refused — otherwise "acknowledged" would mean "an RPC was called".
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        await SeedVerifiedQueueAsync(host, OutOfScopeItem());

        var ack = await client.AcknowledgeFlaggedChangeAsync(new AcknowledgeFlaggedChangeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = AgentId,
            ItemId = "OutOfApprovedScope|src/payments.cs|not-the-real-content-hash",
        }, headers);

        Assert.False(ack.Acknowledged);
        Assert.False(ack.CanMerge);
        Assert.False((await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = AgentId }, headers)).Granted);
    }

    [Fact]
    public async Task AcknowledgingForAnAgentWhoseReviewNeverRan_DoesNotManufactureAPass()
    {
        // MG-40 fail-closed, and the specific hole an ack path can punch in it. The gate's per-agent stores
        // are created on demand, and a NEW store holds no items — which reads as "everything acknowledged".
        // So an acknowledgment naming an agent the review never ran for must not be allowed to CREATE that
        // agent's store, or this RPC becomes the bypass around the default-DENY. Hence PeekStore.
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        var ctx = await SeedVerifiedQueueAsync(host, new[] { OutOfScopeItem() }, extraVerifiedAgent: "never-reviewed");

        Assert.Null(ctx.FlaggedChanges!.PeekStore("never-reviewed"));
        Assert.False(ctx.Queue.CanMerge("never-reviewed", out var reason));
        Assert.Contains("flagged-change review has not run", reason);

        var ack = await client.AcknowledgeFlaggedChangeAsync(new AcknowledgeFlaggedChangeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = "never-reviewed",
            ItemId = "OutOfApprovedScope|src/payments.cs|anything",
        }, headers);

        Assert.False(ack.Acknowledged);
        Assert.False(ack.CanMerge);
        // The read must have left no trace: a store created here would have opened the gate permanently.
        Assert.Null(ctx.FlaggedChanges.PeekStore("never-reviewed"));
        Assert.False((await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = "never-reviewed" }, headers)).Granted);
    }

    // ---- helpers ---------------------------------------------------------

    private static (MergeQueueService.MergeQueueServiceClient Client, Metadata Headers) Client(DaemonFixture host)
        => (new MergeQueueService.MergeQueueServiceClient(host.CreateChannel()), host.AuthHeaders());

    /// <summary>The flagged items the daemon publishes for <see cref="AgentId"/> on its queue stream — the
    /// only place a review surface can learn what it must acknowledge.</summary>
    private async Task<IReadOnlyList<FlaggedItem>> FlaggedItemsAsync(
        MergeQueueService.MergeQueueServiceClient client, Metadata headers)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stream = client.StreamQueue(
            new StreamQueueRequest { RepoHandle = _repoHandle }, headers, cancellationToken: cts.Token);

        Assert.True(await stream.ResponseStream.MoveNext(cts.Token));
        var entry = Assert.Single(stream.ResponseStream.Current.Entries, e => e.AgentId == AgentId);
        return entry.FlaggedItems.ToList();
    }

    /// <summary>SA-1/F6: a benign-looking source edit that is flag-worthy for one reason only — the approved
    /// plan did not cover it.</summary>
    private static FlaggedChange OutOfScopeItem() => new(
        "src/payments.cs",
        RiskCategory.Source,
        FlaggedKind.OutOfApprovedScope,
        AcknowledgmentStore.HashContent("src/payments.cs|out-of-scope"),
        "outside approved scope — the plan covered 1 path pattern(s), this touches src/payments.cs");

    private static FlaggedChange PoisonedPackageJsonItem() => new(
        "package.json",
        RiskCategory.ExecutableConfig,
        FlaggedKind.RiskCategory,
        AcknowledgmentStore.HashContent("package.json|postinstall"),
        "executable config edited (scripts run at install/build)");

    /// <summary>
    /// Registers a live queue for the handle with <see cref="AgentId"/> Verified against
    /// <see cref="MainSha"/> and the given flagged set installed — the state a branch is in the instant
    /// before a human merges it. The gate list is the production one (<see cref="ChangedTestCommandGate"/>
    /// AND <see cref="FlaggedChangeGate"/>), built with the daemon's own lease-store singleton so the
    /// checks under test are the real ones.
    /// </summary>
    private async Task<MergeQueueContext> SeedVerifiedQueueAsync(
        DaemonFixture host, params FlaggedChange[] flaggedItems)
        => await SeedVerifiedQueueAsync(host, flaggedItems, extraVerifiedAgent: null);

    private async Task<MergeQueueContext> SeedVerifiedQueueAsync(
        DaemonFixture host, FlaggedChange[] flaggedItems, string? extraVerifiedAgent)
    {
        var registry = host.Services.GetRequiredService<MergeQueueRegistry>();
        var leases = host.Services.GetRequiredService<IMergeLeaseStore>();
        _leases = leases;
        var changed = new ChangedTestCommandGate();
        var flagged = new FlaggedChangeGate();

        MergeQueue queue = null!;
        queue = new MergeQueue(
            repoHash: _repoHandle,
            currentMainSha: MainSha,
            store: new InMemoryMergeQueueStore(),
            verifications: new InMemoryVerificationStore(),
            runVerification: (id, _) => Task.FromResult(new VerificationRecord(
                id, queue.CurrentMainSha, Passed: true, LogArtifactPath: "", ResolvedCommand: "npm test",
                ConfigHash: "cfg", When: DateTimeOffset.UtcNow)),
            requeue: (_, _) => Task.CompletedTask,
            gates: new IMergeGate[] { changed, flagged });

        var ctx = new MergeQueueContext(queue, leases) { ChangedTestCommand = changed, FlaggedChanges = flagged };
        registry.Register(_repoHandle, ctx);

        // The review ran and classified the branch — the daemon-side act MergeQueueProvisioner performs at
        // verification time. Only agents this happened for have a store; that is the point of the last test.
        flagged.StoreFor(AgentId).SetFlagged(flaggedItems);

        await queue.RunVerificationAsync(AgentId, CancellationToken.None);
        if (extraVerifiedAgent is not null)
        {
            await queue.RunVerificationAsync(extraVerifiedAgent, CancellationToken.None);
        }

        return ctx;
    }
}
