using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Mainguard.Protos.V1;
using Mainguard.Server.Services;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Server.Tests;

/// <summary>
/// L2 + L4 over the REAL daemon — the merge conversation's audit record, written through the same
/// hash-chained <c>ChainedAuditLog</c> the shipped daemon registers, and verified with the same
/// <c>VerifyAudit</c> RPC an investigator would use.
///
/// <para><b>L2:</b> a merge driven end-to-end (<c>BeginMerge</c> → the client's merge → <c>ConfirmMerge</c>)
/// left nothing at all in the chain. There was no merge event type in the product: the live database had
/// 33 types including <c>queue_entry_discarded</c>, so DROPPING an entry was recorded and MERGING one was
/// not — for the single action that rewrites the user's main branch.</para>
///
/// <para><b>L4:</b> acknowledging the RT-D2 <c>changed-test-command</c> item — a human waiving the fact
/// that a branch CHANGED THE COMMAND THAT VERIFIES IT — landed in a plain <c>HashSet</c> and wrote
/// nothing, while the neighbouring flagged-change acks wrote <c>acknowledged_flagged_change</c>.</para>
///
/// <para>Every assertion here goes through the chain, not through an in-memory double: the point of a
/// tamper-evident record is that it is <i>in the chain</i>, hashed into it, and still verifies.</para>
/// </summary>
public sealed class MergeAuditRpcTests : IDisposable
{
    private const string MainSha = "main-sha-0000";

    // Both unique per test instance. The in-proc daemon hosts share ONE run-scoped chained audit log (the
    // trap AuditRpcTests documents), so an assertion keyed on a constant agent id would match records this
    // test never wrote — and the acknowledgment event carries no repo field to fall back on, by design:
    // it is shaped exactly like the flagged-change ack it must be consistent with.
    private readonly string _repoHandle = "repo-audit-" + Guid.NewGuid().ToString("N");
    private readonly string _agentId = "loom-audit-" + Guid.NewGuid().ToString("N");
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
    /// The whole defect, end to end: the merge three-step the UI drives, and the record it must leave.
    /// The assertions name the facts a reader of the chain needs — who, which branch, under which lease,
    /// which shas main moved between, and which verification the merge rode on — because "a merge event
    /// exists" is satisfied by an empty one.
    /// </summary>
    [Fact]
    public async Task AMergeThroughTheRpcs_LeavesOneRecordInTheChain()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        var queue = await SeedVerifiedQueueAsync(host);

        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = _agentId }, headers);
        Assert.True(begun.Granted);

        // ...the human's merge happens on their own checkout here...
        var confirmed = await client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = _agentId,
            LeaseId = begun.LeaseId,
            NewMainSha = "main-sha-0001",
        }, headers);

        Assert.True(confirmed.Confirmed);
        Assert.Equal(WorkerMergeState.Merged, queue.GetState(_agentId));

        var merged = Assert.Single(
            host.Services.GetRequiredService<IAuditLog>().Read(),
            e => e.Type == MergeQueue.MergedEvent && e.Fields.GetValueOrDefault("agent") == _agentId);

        Assert.Equal(_repoHandle, merged.Fields["repo"]);
        Assert.Equal(MergeAuthorization.ConfirmRpcSource, merged.Fields["source"]);
        Assert.Equal(begun.LeaseId, merged.Fields["lease"]);
        Assert.Equal(MainSha, merged.Fields["pre_main_sha"]);
        Assert.Equal("main-sha-0001", merged.Fields["post_main_sha"]);
        Assert.Equal("npm test", merged.Fields["verification_command"]);
        Assert.Equal("true", merged.Fields["verification_passed"]);

        // SA-1/F2: the actor is daemon-derived. ConfirmMergeRequest has no actor field precisely so no
        // caller can assert who authorised its own merge, so the one thing that must be true of `by` is
        // that it is a real resolved identity and not the placeholder for "nobody said".
        Assert.NotEqual("unknown", merged.Fields["by"]);
    }

    /// <summary>
    /// The new records participate in the hash chain like every other one: the chain still verifies, and
    /// the merge record is readable back through <c>ReadAudit</c> with its links intact. An event that
    /// broke <c>VerifyAudit</c> would trade a missing record for an unusable chain.
    /// </summary>
    [Fact]
    public async Task TheMergeRecord_IsChained_AndVerifyAuditStillValidates()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        await SeedVerifiedQueueAsync(host);

        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = _agentId }, headers);
        await client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = _agentId,
            LeaseId = begun.LeaseId,
            NewMainSha = "main-sha-0001",
        }, headers);

        var audit = new AuditService.AuditServiceClient(host.CreateChannel());
        var verify = await audit.VerifyAuditAsync(new VerifyAuditRequest(), host.AuthHeaders());
        Assert.True(verify.Valid, $"chain invalid at seq {verify.FirstBadSeq}");
        Assert.False(verify.HasFirstBadSeq);
        Assert.True(verify.Persistent, "the in-proc daemon DB opened, so the chained log must be active");

        // Anchored to the head, not to seq 1: the in-proc hosts share one run-scoped chained log, so a
        // window starting at 1 asserts against the OLDEST records and this test's own append can be off
        // the end of it (the same trap AuditRpcTests documents).
        var read = await audit.ReadAuditAsync(
            new ReadAuditRequest { FromSeq = Math.Max(1, verify.HeadSeq - 499), Take = 500 },
            host.AuthHeaders());

        var record = Assert.Single(
            read.Records,
            r => r.Type == MergeQueue.MergedEvent && r.PayloadJson.Contains(_repoHandle, StringComparison.Ordinal));
        Assert.Equal(64, record.Hash.Length);
        Assert.Equal(64, record.PrevHash.Length);
        Assert.Contains("main-sha-0001", record.PayloadJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refused-confirm record, and why it is worth one when a refused <c>BeginMerge</c> is not: by the
    /// time <c>ConfirmMerge</c> is reached the git merge has ALREADY RUN on the user's checkout. Refusing
    /// does not prevent a merge — it means the daemon and the user's repository may now disagree about
    /// what main is, and that divergence is exactly what somebody investigates later.
    /// </summary>
    [Fact]
    public async Task ARefusedConfirm_IsRecorded_AndRecordsNoMerge()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        var queue = await SeedVerifiedQueueAsync(host);

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = _agentId,
            LeaseId = "fabricated-lease",
            NewMainSha = "main-sha-0001",
        }, headers).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);

        var events = host.Services.GetRequiredService<IAuditLog>().Read();
        var refused = Assert.Single(
            events,
            e => e.Type == MergeQueueGrpcService.ConfirmRefusedEvent
                 && e.Fields.GetValueOrDefault("repo") == _repoHandle);

        Assert.Equal("lease", refused.Fields["stage"]);
        Assert.Equal("fabricated-lease", refused.Fields["lease"]);
        // "reported", never "post_main_sha": nothing verified this sha, and the record's whole point is
        // that the daemon declined to accept the claim.
        Assert.Equal("main-sha-0001", refused.Fields["reported_main_sha"]);

        // And no merge was recorded — a refusal that also wrote a merge record would be worse than the
        // silence it replaces, because a false record is believed.
        Assert.DoesNotContain(events, e => e.Type == MergeQueue.MergedEvent
                                           && e.Fields.GetValueOrDefault("agent") == _agentId);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(_agentId));
    }

    /// <summary>
    /// L4 over the wire: acknowledging the RT-D2 item through <c>AcknowledgeFlaggedChange</c> writes the
    /// waiver into the chain, with the daemon-derived actor and the drift it waived.
    /// </summary>
    [Fact]
    public async Task AcknowledgingTheChangedTestCommand_WritesTheWaiverIntoTheChain()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        var changed = await SeedFlaggedQueueAsync(host);

        Assert.True(changed.IsUnacknowledged(_agentId));

        var response = await client.AcknowledgeFlaggedChangeAsync(new AcknowledgeFlaggedChangeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = _agentId,
            ItemId = "changed-test-command",
        }, headers);

        Assert.True(response.Acknowledged);

        var waiver = Assert.Single(
            host.Services.GetRequiredService<IAuditLog>().Read(),
            e => e.Type == "acknowledged_flagged_change" && e.Fields.GetValueOrDefault("agent") == _agentId);

        Assert.Equal(ChangedTestCommandGate.TestCommandItem, waiver.Fields["item"]);
        Assert.Equal("ChangedTestCommand", waiver.Fields["kind"]);
        Assert.Equal(".mainguard/verify", waiver.Fields["path"]);
        Assert.Equal("dotnet test", waiver.Fields["from"]);
        Assert.Equal("exit 0", waiver.Fields["to"]);
        Assert.NotEqual("unknown", waiver.Fields["by"]); // daemon-derived, SA-1/F2.

        // And the chain the waiver landed in still verifies.
        var audit = new AuditService.AuditServiceClient(host.CreateChannel());
        var verify = await audit.VerifyAuditAsync(new VerifyAuditRequest(), host.AuthHeaders());
        Assert.True(verify.Valid, $"chain invalid at seq {verify.FirstBadSeq}");
    }

    /// <summary>
    /// The waived item shows up as EVIDENCE on the merge that followed it. This is the pair that makes
    /// either record worth keeping: the chain can answer "this branch rewrote its own test command, a
    /// human waived it, and here is the merge that then moved main".
    /// </summary>
    [Fact]
    public async Task TheMergeRecord_NamesTheWaiverItRodeOn()
    {
        using var host = new DaemonFixture();
        var (client, headers) = Client(host);
        await SeedFlaggedQueueAsync(host);

        await client.AcknowledgeFlaggedChangeAsync(new AcknowledgeFlaggedChangeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = _agentId,
            ItemId = "changed-test-command",
        }, headers);

        var begun = await client.BeginMergeAsync(
            new BeginMergeRequest { RepoHandle = _repoHandle, AgentId = _agentId }, headers);
        Assert.True(begun.Granted, begun.Reason);
        await client.ConfirmMergeAsync(new ConfirmMergeRequest
        {
            RepoHandle = _repoHandle,
            AgentId = _agentId,
            LeaseId = begun.LeaseId,
            NewMainSha = "main-sha-0001",
        }, headers);

        var merged = Assert.Single(
            host.Services.GetRequiredService<IAuditLog>().Read(),
            e => e.Type == MergeQueue.MergedEvent && e.Fields.GetValueOrDefault("agent") == _agentId);

        Assert.Contains(
            "changed-test-command: test command changed vs main — acknowledged",
            merged.Fields["gates"], StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------

    private static (MergeQueueService.MergeQueueServiceClient Client, Metadata Headers) Client(DaemonFixture host)
        => (new MergeQueueService.MergeQueueServiceClient(host.CreateChannel()), host.AuthHeaders());

    /// <summary>A live queue for the handle with <see cref="_agentId"/> Verified against
    /// <see cref="MainSha"/> — the state a branch is in the instant before a human merges it.</summary>
    private async Task<MergeQueue> SeedVerifiedQueueAsync(DaemonFixture host)
    {
        var (queue, _) = await SeedAsync(host);
        return queue;
    }

    /// <summary>The same, with the RT-D2 gate armed against a branch that rewrote its verify command to
    /// <c>exit 0</c> — the literal self-green the gate exists to stop.</summary>
    private async Task<ChangedTestCommandGate> SeedFlaggedQueueAsync(DaemonFixture host)
    {
        var (_, changed) = await SeedAsync(host);
        changed.SetFlagged(
            _agentId, ChangedTestCommandGate.TestCommandItem, changed: true,
            new ChangedTestCommandGate.CommandDrift(".mainguard/verify", "dotnet test\n", "exit 0\n"));
        return changed;
    }

    private async Task<(MergeQueue Queue, ChangedTestCommandGate Changed)> SeedAsync(DaemonFixture host)
    {
        var registry = host.Services.GetRequiredService<MergeQueueRegistry>();
        var leases = host.Services.GetRequiredService<IMergeLeaseStore>();
        _leases = leases;

        // The daemon's OWN chained audit log — the point of this suite is that the records land in the
        // real chain, so an in-memory double here would test nothing.
        var audit = host.Services.GetRequiredService<IAuditLog>();
        var changed = new ChangedTestCommandGate(audit);

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
            gates: new IMergeGate[] { changed },
            audit: audit);

        registry.Register(_repoHandle, new MergeQueueContext(queue, leases) { ChangedTestCommand = changed });

        await queue.RunVerificationAsync(_agentId, CancellationToken.None);
        return (queue, changed);
    }
}
