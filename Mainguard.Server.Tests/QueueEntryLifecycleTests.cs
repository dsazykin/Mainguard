using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Git.Audit;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Server.Tests;

/// <summary>
/// The merge queue's <b>human entry-lifecycle actions</b>, enforced daemon-side.
///
/// <para><b>The defect.</b> A queue entry is created when an agent joins a repo and nothing has ever
/// removed one: <c>StopAgent</c> tears down the session, the PTY, the IPC endpoint and the jail, and
/// leaves the merge-queue entry exactly where it was. That entry is then unreachable in every direction —
/// it cannot be verified (verification runs in the agent's own jail, and there is no jail), it cannot be
/// reviewed (Review is offered only for <c>Verified</c>/<c>AwaitingReview</c>), it cannot be merged, and
/// there was no operation on any surface that could drop it. Entries from every past session therefore
/// accumulated on the rail forever with no control on them at all.</para>
///
/// <para><b>Why these tests live on this side of the wire.</b> The obvious "fix" is a ViewModel that
/// removes the row from its own collection — which clears the rail until the next <c>StreamQueue</c>
/// snapshot silently puts the entry back, and is the exact shape of this repository's <c>FlaggedChangeGate</c>
/// defect (a control constructed in a ViewModel and nowhere in the daemon). So every assertion below is
/// read off the <b>daemon's</b> queue, its persisted rows or its audit log, and the end-to-end case drives
/// the shipped rail ViewModel through the shipped adapter into a real in-proc daemon: a discard faked in
/// the UI layer fails all of them.</para>
/// </summary>
public sealed class QueueEntryLifecycleTests
{
    private const string MainSha = "main-sha-0000";
    private const string CoordinatorToken = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    // One handle per test instance (xUnit constructs one per test): in-proc hosts can share a daemon DB,
    // so a constant handle would let one test's lease or rows reach another's.
    private readonly string _repoHandle = "repo-lifecycle-" + Guid.NewGuid().ToString("N");

    // ---- 1. discard: the daemon moves, records and drops the entry -------------------------------

    /// <summary>
    /// The stranded entry, discarded over the real RPC. Every assertion is a fact the daemon owns: the
    /// state machine's terminal, the persisted row, the audit event, and the entry's absence from the very
    /// stream the rail renders.
    /// </summary>
    [Fact]
    public async Task DiscardEntry_MovesTheDaemonsQueueToATerminalThatIsNotMerged_AndDropsItFromTheStream()
    {
        using var host = new DaemonFixture();
        var audit = host.Services.GetRequiredService<IAuditLog>();
        var queue = SeedQueue(host, audit);
        queue.EnsureEntry("stranded", MergeEntryOrigin.Local);
        var (client, headers) = Client(host);

        // Before: the rail's own source carries it, and nothing can be done about it.
        Assert.Contains("stranded", (await SnapshotAsync(client, headers)).Entries.Select(e => e.AgentId));

        var response = await client.DiscardEntryAsync(new DiscardEntryRequest
        {
            RepoHandle = _repoHandle,
            AgentId = "stranded",
            Reason = "its agent was stopped weeks ago",
        }, headers);

        Assert.True(response.Discarded, response.Reason);

        // The daemon's state machine, not a client's opinion of it.
        Assert.Equal(WorkerMergeState.Discarded, queue.GetState("stranded"));
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState("stranded"));

        // It is gone from the stream the rail reads — which is what "removed from the queue" has to mean
        // when the queue is daemon-owned. A UI-side removal cannot produce this.
        var after = await SnapshotAsync(client, headers);
        Assert.DoesNotContain("stranded", after.Entries.Select(e => e.AgentId));

        // ...and the record survives it.
        var record = queue.GetDiscard("stranded");
        Assert.NotNull(record);
        Assert.Equal("its agent was stopped weeks ago", record!.Reason);
        Assert.Equal(WorkerMergeState.Working, record.FromState);

        var audited = Assert.Single(audit.Read(), e => e.Type == MergeQueue.DiscardedEvent);
        Assert.Equal("stranded", audited.Fields["agent"]);
        Assert.Equal(_repoHandle, audited.Fields["repo"]);
        Assert.False(string.IsNullOrWhiteSpace(audited.Fields["when"]));
    }

    /// <summary>
    /// The discarding identity is derived from the connection, and there is no way for a caller to assert
    /// one — the request message carries no actor field at all. A record a token-holder can populate is an
    /// attribution anyone can forge, which is the same reasoning SA-1/F2 applies to plan approvals.
    /// </summary>
    [Fact]
    public async Task DiscardEntry_AttributesTheDiscardToTheDaemonDerivedIdentity_NotToAnythingOnTheRequest()
    {
        using var host = new DaemonFixture();
        var queue = SeedQueue(host, host.Services.GetRequiredService<IAuditLog>());
        queue.EnsureEntry("stranded", MergeEntryOrigin.Local);
        var (client, headers) = Client(host);

        var response = await client.DiscardEntryAsync(
            new DiscardEntryRequest { RepoHandle = _repoHandle, AgentId = "stranded", Reason = "" }, headers);
        Assert.True(response.Discarded, response.Reason);

        // The value the daemon's own resolver produces — the same one PlanApprovalGrpcService records.
        var expected = host.Services.GetRequiredService<IApproverIdentityResolver>()
            .Resolve(new NullServerCallContext());
        Assert.Equal(expected, response.DiscardedBy);
        Assert.Equal(expected, queue.GetDiscard("stranded")!.By);
        Assert.False(string.IsNullOrWhiteSpace(response.DiscardedAt));

        // The forgery this closes is not "the resolver returned the wrong thing", it is "the client named
        // an actor". There is no field to name one with, and this asserts that structurally.
        var fields = DiscardEntryRequest.Descriptor.Fields.InDeclarationOrder().Select(f => f.Name).ToArray();
        Assert.Equal(new[] { "repo_handle", "agent_id", "reason" }, fields);
    }

    /// <summary>
    /// A discard must not land inside the merge window. Between <c>BeginMerge</c> and <c>ConfirmMerge</c>
    /// the merge is already executing against the user's checkout; moving the entry to a terminal state
    /// there would make <c>ConfirmMerge</c> refuse to record a merge that really landed — the queue
    /// disagreeing with git, which is the one outcome this subsystem exists to prevent.
    /// </summary>
    [Fact]
    public async Task DiscardEntry_IsRefusedWhileThatEntryHoldsTheReposMergeLease()
    {
        using var host = new DaemonFixture();
        var leases = host.Services.GetRequiredService<IMergeLeaseStore>();
        var queue = SeedQueue(host, host.Services.GetRequiredService<IAuditLog>());
        var (client, headers) = Client(host);
        await queue.RunVerificationAsync("merging", CancellationToken.None);

        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = "merging" }, headers);
        Assert.True(begun.Granted, begun.Reason);

        try
        {
            var response = await client.DiscardEntryAsync(
                new DiscardEntryRequest { RepoHandle = _repoHandle, AgentId = "merging" }, headers);

            Assert.False(response.Discarded);
            Assert.Contains("merge is in progress", response.Reason);
            Assert.Equal(WorkerMergeState.Verified, queue.GetState("merging"));
            Assert.Null(queue.GetDiscard("merging"));
        }
        finally
        {
            leases.Release(_repoHandle, begun.LeaseId);
        }
    }

    /// <summary>A terminal entry refuses, and says which terminal it already is.</summary>
    [Fact]
    public async Task DiscardEntry_IsRefusedOnAnAlreadyDiscardedEntry()
    {
        using var host = new DaemonFixture();
        var queue = SeedQueue(host, host.Services.GetRequiredService<IAuditLog>());
        queue.EnsureEntry("stranded", MergeEntryOrigin.Local);
        var (client, headers) = Client(host);

        var request = new DiscardEntryRequest { RepoHandle = _repoHandle, AgentId = "stranded" };
        Assert.True((await client.DiscardEntryAsync(request, headers)).Discarded);

        var second = await client.DiscardEntryAsync(request, headers);
        Assert.False(second.Discarded);
        Assert.Contains("already discarded", second.Reason);
    }

    // ---- 2. the stalled verification -------------------------------------------------------------

    /// <summary>
    /// A row that says <c>Verifying</c> with nothing running behind it — the shape a daemon restart
    /// leaves, since queue state is persisted per transition while the in-flight set is memory and
    /// <c>ResumeAfterRestartAsync</c> has no production caller. The stream says so (the daemon is the only
    /// side that can), the gate reason says so, and the clear puts the entry back to Working.
    /// </summary>
    [Fact]
    public async Task ClearStalledVerification_UnsticksAnEntryFrozenMidVerify_AndTheStreamSaysNoRunIsLive()
    {
        using var host = new DaemonFixture();
        var store = new InMemoryMergeQueueStore();
        store.Save(new Mainguard.Git.Models.MergeQueueRow
        {
            RepoHash = _repoHandle,
            AgentId = "frozen",
            State = WorkerMergeState.Verifying.ToString(),
            UpdatedUtc = DateTime.UtcNow,
        });
        var queue = SeedQueue(host, host.Services.GetRequiredService<IAuditLog>(), store);
        var (client, headers) = Client(host);

        var before = (await SnapshotAsync(client, headers)).Entries.Single(e => e.AgentId == "frozen");
        Assert.Equal("Verifying", before.State);
        Assert.False(before.VerificationInFlight);
        Assert.Equal("verification stalled — no run in progress", before.GateReason);

        var cleared = await client.ClearStalledVerificationAsync(
            new ClearStalledVerificationRequest { RepoHandle = _repoHandle, AgentId = "frozen" }, headers);

        Assert.True(cleared.Cleared, cleared.Reason);
        Assert.Equal("Working", cleared.State);
        Assert.Equal(WorkerMergeState.Working, queue.GetState("frozen"));
        Assert.Equal("Working", store.LoadAll(_repoHandle).Single(r => r.AgentId == "frozen").State);
    }

    // ---- 3. the coordinator may not do either ----------------------------------------------------

    /// <summary>
    /// Contract §4: the coordinator has no merge power, and both of these are merge power. A discard an
    /// agent could invoke is a way to erase the evidence blocking its own branch rather than clear the
    /// gate; clearing a stalled verification puts a branch into the state a re-verification starts from.
    /// Enforced at the interceptor, so a hand-written client cannot walk past it either.
    /// </summary>
    [Fact]
    public async Task Coordinator_IsDeniedBothLifecycleRpcs_ByTheRoleLayer()
    {
        using var host = new DaemonFixture();
        host.Services.GetRequiredService<ConnectionRoleRegistry>().RegisterCoordinatorToken(CoordinatorToken);
        var queue = SeedQueue(host, host.Services.GetRequiredService<IAuditLog>());
        queue.EnsureEntry("stranded", MergeEntryOrigin.Local);

        var client = new MergeQueueService.MergeQueueServiceClient(host.CreateChannel());
        var coordinator = host.AuthHeaders(CoordinatorToken);

        var discard = await Assert.ThrowsAsync<RpcException>(() => client.DiscardEntryAsync(
            new DiscardEntryRequest { RepoHandle = _repoHandle, AgentId = "stranded" }, coordinator).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, discard.StatusCode);
        // The ROLE gate, not the bearer layer — otherwise this would pass for a token that never
        // authenticated, which is how the older role assertions in this suite proved nothing.
        Assert.Contains("coordinator role", discard.Status.Detail);
        Assert.DoesNotContain("Invalid bearer token", discard.Status.Detail);

        var clear = await Assert.ThrowsAsync<RpcException>(() => client.ClearStalledVerificationAsync(
            new ClearStalledVerificationRequest { RepoHandle = _repoHandle, AgentId = "stranded" }, coordinator).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, clear.StatusCode);
        Assert.Contains("coordinator role", clear.Status.Detail);

        // Nothing moved. The denial is the point, but a denial that still mutated would be worse than none.
        Assert.Equal(WorkerMergeState.Working, queue.GetState("stranded"));
        Assert.Null(queue.GetDiscard("stranded"));
    }

    /// <summary>
    /// The in-jail agent surface cannot address these operations either, and cannot grow them by accident:
    /// the coordinator→daemon channel speaks two ops, both about spawning. This is the layer BELOW the role
    /// interceptor — an agent does not hold a gRPC channel at all — so it is asserted separately.
    /// </summary>
    [Fact]
    public void AgentIpcSurface_HasNoEntryLifecycleOp()
    {
        var ops = typeof(Mainguard.Agents.Agents.Ipc.AgentIpcRequest)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal(new[] { "list", "spawn" }, ops);
    }

    // ---- 4. the end-to-end: the shipped rail, through the shipped adapter, into the real daemon ---

    /// <summary>
    /// <b>The decisive one.</b> The shipped <see cref="QueueRailViewModel"/>'s discard command, driven
    /// through the shipped <see cref="DaemonBackedOrchestrator"/> against a real in-proc daemon. The
    /// assertions are the DAEMON's queue state and the DAEMON's persisted record — so a discard that only
    /// emptied a ViewModel collection fails this test, and so does one whose adapter reported success off
    /// a refusal.
    /// </summary>
    [Fact]
    public async Task RailDiscardCommand_ReachesTheDaemon_AndTheEntryIsGoneFromTheProjectionItRendersFrom()
    {
        using var host = new DaemonFixture();
        var queue = SeedQueue(host, host.Services.GetRequiredService<IAuditLog>());
        queue.EnsureEntry("stranded", MergeEntryOrigin.Local);

        using var client = new DaemonClient(host.CreateChannel, () => host.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.SetActiveRepo(_repoHandle, localRepoPath: null, syncRemoteName: null);

        Assert.True(await WaitUntilAsync(() => adapter.GetQueue().Any(e => e.AgentId == "stranded")),
            "the queue projection never arrived — the rail could not have rendered the entry");

        var reported = new System.Collections.Generic.List<(string Message, bool IsWarning)>();
        var rail = new QueueRailViewModel(adapter, _ => { }, (m, w) => reported.Add((m, w)));
        adapter.Changed += rail.Refresh;

        var row = Assert.Single(rail.Entries, e => e.AgentId == "stranded");
        // The affordance exists at all — the entry's whole problem was having none. (Asserting the COMMAND
        // rather than a rendered button on purpose: this is the ViewModel's contract, and the render
        // harness covers the visual half.)
        Assert.True(row.CanDiscard);

        row.BeginDiscardCommand.Execute(null);
        Assert.True(row.IsConfirmingDiscard, "the destructive action must be confirmed before it fires");

        await row.ConfirmDiscardCommand.ExecuteAsync(null);

        // The daemon moved. This is the assertion a UI-only implementation cannot satisfy.
        Assert.Equal(WorkerMergeState.Discarded, queue.GetState("stranded"));
        Assert.NotNull(queue.GetDiscard("stranded"));

        // ...and the rail's own source no longer carries it, so the row does not come back on the next push.
        Assert.True(await WaitUntilAsync(() => !adapter.GetQueue().Any(e => e.AgentId == "stranded")),
            "the daemon still streams the discarded entry");

        var (message, isWarning) = Assert.Single(reported);
        Assert.False(isWarning);
        Assert.Contains("Dropped agent/stranded from the merge queue", message);
        // The sentence must not overclaim: a discard does not touch the branch.
        Assert.Contains("branch and its commits are untouched", message);
    }

    /// <summary>
    /// The other half of the same guarantee: when the daemon REFUSES, the rail must keep the row and say
    /// why. A ViewModel that removed the row locally would show a queue the daemon disagrees with — the
    /// "looks applied but isn't" failure in its purest form.
    /// </summary>
    [Fact]
    public async Task RailDiscardCommand_WhenTheDaemonRefuses_KeepsTheRow_AndReportsTheReasonVerbatim()
    {
        using var host = new DaemonFixture();
        var leases = host.Services.GetRequiredService<IMergeLeaseStore>();
        var queue = SeedQueue(host, host.Services.GetRequiredService<IAuditLog>());
        await queue.RunVerificationAsync("merging", CancellationToken.None);

        using var client = new DaemonClient(host.CreateChannel, () => host.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.SetActiveRepo(_repoHandle, localRepoPath: null, syncRemoteName: null);
        Assert.True(await WaitUntilAsync(() => adapter.GetQueue().Any(e => e.AgentId == "merging")));

        // The repo's one merge lease is out for this very entry.
        var begun = await new MergeQueueService.MergeQueueServiceClient(host.CreateChannel())
            .BeginMergeAsync(new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = "merging" }, host.AuthHeaders());
        Assert.True(begun.Granted, begun.Reason);

        try
        {
            var reported = new System.Collections.Generic.List<(string Message, bool IsWarning)>();
            var rail = new QueueRailViewModel(adapter, _ => { }, (m, w) => reported.Add((m, w)));
            adapter.Changed += rail.Refresh;

            var row = Assert.Single(rail.Entries, e => e.AgentId == "merging");
            row.BeginDiscardCommand.Execute(null);
            await row.ConfirmDiscardCommand.ExecuteAsync(null);

            // Nothing moved, the row is still there, and the human was told why.
            Assert.Equal(WorkerMergeState.Verified, queue.GetState("merging"));
            rail.Refresh();
            Assert.Contains(rail.Entries, e => e.AgentId == "merging");

            var (message, isWarning) = Assert.Single(reported);
            Assert.True(isWarning, "a refused discard must not be reported as a success");
            Assert.Contains("merge is in progress", message);
        }
        finally
        {
            leases.Release(_repoHandle, begun.LeaseId);
        }
    }

    // ---- helpers ---------------------------------------------------------

    private static (MergeQueueService.MergeQueueServiceClient Client, Metadata Headers) Client(DaemonFixture host)
        => (new MergeQueueService.MergeQueueServiceClient(host.CreateChannel()), host.AuthHeaders());

    /// <summary>
    /// Registers a live queue for the handle, built with the daemon's own lease store and audit sink so
    /// the checks under test are the real ones. "merging" is always tracked (the lease cases verify it);
    /// callers add whatever else they need.
    /// </summary>
    private MergeQueue SeedQueue(DaemonFixture host, IAuditLog audit, IMergeQueueStore? store = null)
    {
        var registry = host.Services.GetRequiredService<MergeQueueRegistry>();
        var leases = host.Services.GetRequiredService<IMergeLeaseStore>();

        MergeQueue queue = null!;
        queue = new MergeQueue(
            repoHash: _repoHandle,
            currentMainSha: MainSha,
            store: store ?? new InMemoryMergeQueueStore(),
            verifications: new InMemoryVerificationStore(),
            runVerification: (id, _) => Task.FromResult(new VerificationRecord(
                id, queue.CurrentMainSha, Passed: true, LogArtifactPath: "", ResolvedCommand: "npm test",
                ConfigHash: "cfg", When: DateTimeOffset.UtcNow)),
            requeue: (_, _) => Task.CompletedTask,
            audit: audit);

        queue.EnsureEntry("merging", MergeEntryOrigin.Local);
        registry.Register(_repoHandle, new MergeQueueContext(queue, leases));
        return queue;
    }

    private async Task<QueueUpdate> SnapshotAsync(
        MergeQueueService.MergeQueueServiceClient client, Metadata headers)
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var call = client.StreamQueue(
            new StreamQueueRequest { RepoHandle = _repoHandle }, headers, cancellationToken: cts.Token);
        Assert.True(await call.ResponseStream.MoveNext(cts.Token), "the queue stream produced no snapshot");
        return call.ResponseStream.Current;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Timeout)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return predicate();
    }

    /// <summary>
    /// A minimal <see cref="ServerCallContext"/> for calling the identity resolver directly. The resolver
    /// deliberately ignores the request/context entirely (SA-1/F2), which is exactly why a null-ish one is
    /// a legitimate way to ask it what it would have produced.
    /// </summary>
    private sealed class NullServerCallContext : ServerCallContext
    {
        protected override string MethodCore => "";
        protected override string HostCore => "";
        protected override string PeerCore => "";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore =>
            new(null, new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
            => throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
            => throw new NotSupportedException();
    }
}
