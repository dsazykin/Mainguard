using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Protos.V1;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Services;

/// <summary>
/// gRPC transport for <see cref="MergeQueueService"/> (P2-10). Validation + dispatch only — the state
/// machine, verification provenance, and the CanMerge gate all live in the daemon-side
/// <see cref="MergeQueue"/> resolved through the <see cref="IMergeQueueRegistry"/>. There is no
/// auto-merge RPC: <c>ConfirmMerge</c> only records the outcome of a merge the human already drove.
/// </summary>
public sealed class MergeQueueGrpcService : MergeQueueService.MergeQueueServiceBase
{
    /// <summary>The item id the review cockpit renders for the RT-D2 gate item (it has no
    /// <c>FlaggedChange</c> row — the gate owns it), so client and daemon address it by the same name.</summary>
    internal const string ChangedTestCommandItemId = "changed-test-command";

    private readonly IMergeQueueRegistry _registry;
    private readonly KillSwitchGate _killGate;
    private readonly IMergeBranchDiffService _mergeDiff;
    private readonly ILogger _log;

    public MergeQueueGrpcService(
        IMergeQueueRegistry registry, KillSwitchGate killGate, IMergeBranchDiffService mergeDiff,
        ILoggerFactory loggerFactory)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _killGate = killGate ?? throw new ArgumentNullException(nameof(killGate));
        _mergeDiff = mergeDiff ?? throw new ArgumentNullException(nameof(mergeDiff));
        _log = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.Merge);
    }

    public override async Task StreamQueue(
        StreamQueueRequest request,
        IServerStreamWriter<QueueUpdate> responseStream,
        ServerCallContext context)
    {
        var ctx = Resolve(request.RepoHandle);
        var queue = ctx.Queue;

        // Snapshot-then-deltas: push the current state, then re-push on every change until detach.
        using var signal = new SemaphoreSlim(0);
        void OnChanged() => signal.Release();
        queue.Changed += OnChanged;
        try
        {
            await responseStream.WriteAsync(Snapshot(queue)).ConfigureAwait(false);
            while (!context.CancellationToken.IsCancellationRequested)
            {
                await signal.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                await responseStream.WriteAsync(Snapshot(queue)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client detached — normal teardown.
        }
        finally
        {
            queue.Changed -= OnChanged;
        }
    }

    public override async Task<RunVerificationResponse> RunVerification(RunVerificationRequest request, ServerCallContext context)
    {
        var ctx = Resolve(request.RepoHandle);
        var record = await ctx.Queue.RunVerificationAsync(request.AgentId, context.CancellationToken).ConfigureAwait(false);
        return new RunVerificationResponse
        {
            AgentId = record.AgentId,
            MainSha = record.MainSha,
            Passed = record.Passed,
            ResolvedCommand = record.ResolvedCommand,
            ConfigHash = record.ConfigHash,
            State = ctx.Queue.GetState(request.AgentId).ToString(),
        };
    }

    public override Task<CanMergeResponse> CanMerge(CanMergeRequest request, ServerCallContext context)
    {
        var ctx = Resolve(request.RepoHandle);
        var can = ctx.Queue.CanMerge(request.AgentId, out var reason);
        return Task.FromResult(new CanMergeResponse { CanMerge = can, Reason = reason });
    }

    public override Task<BeginMergeResponse> BeginMerge(BeginMergeRequest request, ServerCallContext context)
    {
        // SA-1/F4: while the kill switch holds the queue frozen, no merge may begin — loudly.
        ThrowIfFrozen("BeginMerge");
        var ctx = Resolve(request.RepoHandle);
        var leaseId = Guid.NewGuid().ToString("N");
        var verified = ctx.Queue.CurrentMainSha;
        var lease = ctx.Leases.TryBegin(request.RepoHandle, leaseId, request.AgentId, verified, "main");
        if (lease is null)
        {
            _log.LogInformation("BeginMerge repo={Repo} agent={Agent} granted=False (lease held)",
                request.RepoHandle, request.AgentId);
            return Task.FromResult(new BeginMergeResponse
            {
                Granted = false,
                Reason = "another merge is already in progress for this repository",
            });
        }

        // MG-11: the gate is read UNDER the lease, never before it (the MG-23 ordering the external
        // dispatch already uses). Granting a lease to a branch that cannot merge is worse than useless: it
        // blocks every other merge on the repo while the caller goes off to perform a merge the daemon was
        // always going to refuse at ConfirmMerge.
        if (!ctx.Queue.CanMerge(request.AgentId, out var gateReason))
        {
            ctx.Leases.Release(request.RepoHandle, leaseId);
            _log.LogInformation("BeginMerge repo={Repo} agent={Agent} granted=False reason={Reason}",
                request.RepoHandle, request.AgentId, gateReason);
            return Task.FromResult(new BeginMergeResponse { Granted = false, Reason = gateReason });
        }

        _log.LogInformation("BeginMerge repo={Repo} agent={Agent} granted=True", request.RepoHandle, request.AgentId);
        return Task.FromResult(new BeginMergeResponse { Granted = true, LeaseId = lease.LeaseId });
    }

    /// <summary>
    /// RT-D1 step 3 — record a merge the human already drove. <b>MG-11: this is an ENFORCEMENT point, not a
    /// bookkeeping call.</b> It used to invoke <c>Leases.Confirm</c> (a no-op when no lease is held) and then
    /// <c>ConfirmHumanMerge</c> unconditionally: no <c>CanMerge</c>, no freshness compare, no gate, and no
    /// requirement that the caller hold the merge lease at all. Every check lived in the client cockpit, so
    /// any caller that skipped the cockpit — or any cockpit that lost a race to a co-tenant's merge — moved
    /// a branch to <c>Merged</c> on stale or unreviewed evidence. Three things are now required, in order:
    /// the caller's lease must be the repo's outstanding one and name this agent; the queue's merge gate
    /// must pass; and the lease's expected <c>main@sha</c> must still be the queue's current main — the last
    /// two evaluated together under the queue lock by <see cref="MergeQueue.TryConfirmHumanMerge"/>.
    /// </summary>
    public override Task<ConfirmMergeResponse> ConfirmMerge(ConfirmMergeRequest request, ServerCallContext context)
    {
        // SA-1/F4: a frozen queue refuses the merge confirmation too.
        ThrowIfFrozen("ConfirmMerge");
        var ctx = Resolve(request.RepoHandle);

        // (1) A held lease is the caller's proof it went through BeginMerge for THIS agent. Leases.Confirm
        // is idempotent and silently no-ops on an unknown lease, so calling it was never a check.
        var lease = ctx.Leases.GetOutstanding(request.RepoHandle);
        if (lease is null
            || !string.Equals(lease.LeaseId, request.LeaseId, StringComparison.Ordinal)
            || !string.Equals(lease.AgentId, request.AgentId, StringComparison.Ordinal))
        {
            _log.LogWarning("ConfirmMerge refused repo={Repo} agent={Agent}: no matching outstanding merge lease",
                request.RepoHandle, request.AgentId);
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "No outstanding merge lease for this repository and agent — call BeginMerge first."));
        }

        // (2)+(3) Gate and freshness, atomically with the Merged transition.
        if (!ctx.Queue.TryConfirmHumanMerge(request.AgentId, request.NewMainSha, lease.ExpectedMainSha, out var reason))
        {
            // Nothing moved to Merged, so the lease must not strand the repo — the same "every non-merged
            // exit hands the lease back" rule MergeDispatch follows. A merge that really did land on a ref
            // despite the refusal is still recoverable: it left a T-19 journal entry, and the boot reconcile
            // synthesizes the confirm from it.
            ctx.Leases.Release(request.RepoHandle, lease.LeaseId);
            _log.LogWarning("ConfirmMerge refused repo={Repo} agent={Agent}: {Reason}",
                request.RepoHandle, request.AgentId, reason);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, reason));
        }

        // Only now is the idempotency record written: a confirmed lease is the daemon's statement that this
        // merge landed, and the boot reconcile reads it to tell a landed merge from an abandoned one.
        ctx.Leases.Confirm(request.RepoHandle, request.LeaseId, request.NewMainSha);
        _log.LogInformation("ConfirmMerge repo={Repo} agent={Agent} newMainSha={Sha}",
            request.RepoHandle, request.AgentId, request.NewMainSha);
        return Task.FromResult(new ConfirmMergeResponse { Confirmed = true });
    }

    /// <summary>
    /// P2-11 step 4 — acknowledge one must-acknowledge flagged item so the merge gate can pass. A gate the
    /// daemon evaluates but no human can clear is not a gate, it is a permanently unmergeable branch; this
    /// is the missing half of moving the RT-D2 gate daemon-side. Per item, never "all" (a global ack is a
    /// rejection trigger, which is why <c>AcknowledgmentStore</c> exposes no such method either).
    /// </summary>
    public override Task<AcknowledgeFlaggedChangeResponse> AcknowledgeFlaggedChange(
        AcknowledgeFlaggedChangeRequest request, ServerCallContext context)
    {
        var ctx = Resolve(request.RepoHandle);
        var acknowledged = false;

        if (string.Equals(request.ItemId, ChangedTestCommandItemId, StringComparison.Ordinal)
            && ctx.ChangedTestCommand is { } changed)
        {
            // Acknowledge is a no-op for an agent that is not flagged, so the "was it really cleared?"
            // answer is read back off the gate rather than assumed from the call having been made.
            changed.Acknowledge(request.AgentId);
            acknowledged = !changed.IsUnacknowledged(request.AgentId);
        }

        var can = ctx.Queue.CanMerge(request.AgentId, out var reason);
        _log.LogInformation(
            "AcknowledgeFlaggedChange repo={Repo} agent={Agent} item={Item} acknowledged={Ack} canMerge={Can}",
            request.RepoHandle, request.AgentId, request.ItemId, acknowledged, can);
        return Task.FromResult(new AcknowledgeFlaggedChangeResponse
        {
            Acknowledged = acknowledged,
            CanMerge = can,
            Reason = reason,
        });
    }

    public override Task<GetMergeDiffResponse> GetMergeDiff(GetMergeDiffRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RepoHandle) || string.IsNullOrWhiteSpace(request.AgentId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "repo_handle and agent_id are required."));
        }

        try
        {
            var diff = _mergeDiff.Compute(request.RepoHandle, request.AgentId);
            return Task.FromResult(new GetMergeDiffResponse
            {
                Branch = diff.Branch,
                MainBranch = diff.MainBranch,
                UnifiedDiff = diff.UnifiedDiff,
            });
        }
        catch (Mainguard.Git.Exceptions.RepoProvisioningException ex)
        {
            // No provisioned mirror / no such branch — a typed NOT_FOUND rather than an opaque Internal.
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
    }

    private void ThrowIfFrozen(string operation)
    {
        if (_killGate.IsFrozen)
        {
            _log.LogWarning("{Operation} refused: merge queue frozen (kill switch engaged)", operation);
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"The merge queue is frozen (kill switch engaged) — {operation} is refused. Resume first."));
        }
    }

    private MergeQueueContext Resolve(string repoHandle)
    {
        return _registry.Resolve(repoHandle)
            ?? throw new RpcException(new Status(StatusCode.NotFound,
                $"No active merge queue for repo handle '{repoHandle}'."));
    }

    private static QueueUpdate Snapshot(MergeQueue queue)
    {
        var update = new QueueUpdate { MainSha = queue.CurrentMainSha };
        foreach (var agentId in queue.Agents)
        {
            var can = queue.CanMerge(agentId, out var reason);
            update.Entries.Add(new QueueEntry
            {
                AgentId = agentId,
                State = queue.GetState(agentId).ToString(),
                VerifiedMainSha = queue.CurrentMainSha,
                CanMerge = can,
                GateReason = reason,
                // P2-13 carried-in from P2-12: badge external-PR intake entries as such.
                Origin = queue.GetOrigin(agentId).ToString(),
            });
        }

        return update;
    }
}
