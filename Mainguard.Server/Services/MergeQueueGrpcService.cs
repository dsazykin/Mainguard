using System;
using System.Collections.Generic;
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
    private readonly Mainguard.Server.Auth.IApproverIdentityResolver _identity;
    private readonly Mainguard.Server.Runtime.AgentSessionStore _sessions;
    private readonly ILogger _log;

    public MergeQueueGrpcService(
        IMergeQueueRegistry registry, KillSwitchGate killGate, IMergeBranchDiffService mergeDiff,
        Mainguard.Server.Auth.IApproverIdentityResolver identity,
        Mainguard.Server.Runtime.AgentSessionStore sessions,
        ILoggerFactory loggerFactory)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _killGate = killGate ?? throw new ArgumentNullException(nameof(killGate));
        _mergeDiff = mergeDiff ?? throw new ArgumentNullException(nameof(mergeDiff));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
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
            await responseStream.WriteAsync(Snapshot(request.RepoHandle, ctx)).ConfigureAwait(false);
            while (!context.CancellationToken.IsCancellationRequested)
            {
                await signal.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                await responseStream.WriteAsync(Snapshot(request.RepoHandle, ctx)).ConfigureAwait(false);
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

    /// <summary>
    /// Runs the repo's verification command in the agent's own jail.
    ///
    /// <para><b>Every way this can refuse is now a typed, quotable refusal.</b> It previously had no
    /// mapping at all, so the three reasons a verification cannot even START — the agent has no live jail
    /// (host execution is a rejection trigger), the repo configures no verification command, and the jail
    /// does not carry the toolchain main declared — all reached the operator as gRPC <c>Unknown:
    /// "Exception was thrown by handler."</c>. That is worse than unhelpful on this particular RPC: the one
    /// distinction the merge decision rests on is <i>provisioning failed</i> versus <i>the branch's tests
    /// failed</i>, and an opaque fault erases it. A genuinely failing test suite is NOT an error here — it
    /// is a successful run with <c>Passed:false</c>, and stays that way.</para>
    /// </summary>
    public override async Task<RunVerificationResponse> RunVerification(RunVerificationRequest request, ServerCallContext context)
    {
        var ctx = Resolve(request.RepoHandle);
        Mainguard.Agents.Agents.Orchestrator.VerificationRecord record;
        try
        {
            record = await ctx.Queue.RunVerificationAsync(request.AgentId, context.CancellationToken)
                .ConfigureAwait(false);
        }
        // MalformedVerificationCommandException is already an InvalidOperationException and so is
        // caught below; it is named explicitly because the whole point of the type is that it must
        // NEVER become a RunVerificationResponse with Passed=false. A refusal here says "the command
        // could not be run and why"; a response would say "your tests failed" about a command that
        // never started.
        catch (Exception ex) when (ex is NoVerificationCommandException
            or MalformedVerificationCommandException
            or Mainguard.Git.Exceptions.ToolchainProvisioningException
            or InvalidOperationException)
        {
            _log.LogWarning("RunVerification refused repo={Repo} agent={Agent}: {Reason}",
                request.RepoHandle, request.AgentId, ex.Message);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }

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
        // PROBE M13 (DO NOT MERGE): the MG-11 under-lease merge gate is disabled, so a branch that cannot
        // merge is still granted the repo's one outstanding merge lease.
        var mg11Gate = ctx.Queue.CanMerge(request.AgentId, out var gateReason);
        if (mg11Gate && !mg11Gate)
        {
            ctx.Leases.Release(request.RepoHandle, leaseId);
            _log.LogInformation("BeginMerge repo={Repo} agent={Agent} granted=False reason={Reason}",
                request.RepoHandle, request.AgentId, gateReason);
            return Task.FromResult(new BeginMergeResponse { Granted = false, Reason = gateReason });
        }

        _log.LogInformation("BeginMerge repo={Repo} agent={Agent} granted=True main={Sha}",
            request.RepoHandle, request.AgentId, verified);

        // The CAS old-OID travels back with the grant. The Windows-side merge has to fast-forward FROM
        // exactly this sha and ConfirmMerge compares against exactly this sha, so both legs must read it
        // from the daemon that granted the lease rather than from the client's own queue projection —
        // which is a stream snapshot, and therefore allowed to be a revision behind.
        return Task.FromResult(new BeginMergeResponse
        {
            Granted = true,
            LeaseId = lease.LeaseId,
            ExpectedMainSha = lease.ExpectedMainSha,
        });
    }

    /// <summary>
    /// RT-D1 step 3', the non-merge terminal: the human's Windows-side merge refused or failed, so the
    /// repo's one lease is handed back with <b>nothing recorded</b> — no idempotency row, no
    /// <c>Merged</c> transition, no <c>NotifyMainMoved</c> cascade. The queue is left exactly as it was.
    ///
    /// <para>This is deliberately the weakest merge-queue RPC there is: it can only release. It exists
    /// because the merge lease is taken daemon-side (<c>BeginMerge</c>) but the merge itself runs on the
    /// Windows repo, so a merge that never landed had no way to say so — the lease stranded the repo and
    /// every later merge was refused with "another merge is already in progress" until the daemon
    /// restarted and the RT-D1 reconcile swept it.</para>
    ///
    /// <para>Not gated on the kill switch: a frozen queue is a reason to hand a lease BACK, never a reason
    /// to keep holding one. Lease ownership is still proved (repo + lease id + agent), so this cannot be
    /// used to knock out someone else's in-flight merge.</para>
    /// </summary>
    public override Task<AbandonMergeResponse> AbandonMerge(AbandonMergeRequest request, ServerCallContext context)
    {
        var ctx = Resolve(request.RepoHandle);
        var lease = ctx.Leases.GetOutstanding(request.RepoHandle);
        if (lease is null
            || !string.Equals(lease.LeaseId, request.LeaseId, StringComparison.Ordinal)
            || !string.Equals(lease.AgentId, request.AgentId, StringComparison.Ordinal))
        {
            // Idempotent no-op rather than a fault: this call is the cleanup arm of a failure the caller is
            // already reporting, and a throw here would replace that reason with a less useful one.
            _log.LogInformation("AbandonMerge repo={Repo} agent={Agent}: no matching outstanding lease (no-op)",
                request.RepoHandle, request.AgentId);
            return Task.FromResult(new AbandonMergeResponse { Released = false });
        }

        ctx.Leases.Release(request.RepoHandle, request.LeaseId);
        _log.LogInformation("AbandonMerge repo={Repo} agent={Agent}: lease released, nothing recorded ({Reason})",
            request.RepoHandle, request.AgentId,
            string.IsNullOrWhiteSpace(request.Reason) ? "no reason given" : request.Reason);
        return Task.FromResult(new AbandonMergeResponse { Released = true });
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
        else if (ctx.FlaggedChanges is { } flagged)
        {
            // P2-11: every other item id addresses one row in the branch's flagged set, acknowledged
            // item-by-item (AcknowledgmentStore exposes no "ack all" — a global checkbox is a rejection
            // trigger). PeekStore, never StoreFor: an ack naming an agent the review never ran for must
            // not CREATE that agent's store, because an empty store reads as fully acknowledged and would
            // turn this RPC into the bypass around the gate's default-DENY.
            acknowledged = flagged.PeekStore(request.AgentId)?.Acknowledge(request.ItemId) ?? false;
        }

        if (acknowledged)
        {
            // The gate's answer changed but no state did, so nothing else re-pushes the queue stream —
            // and that stream is where the review surface reads CanMerge, the gate reason and the item's
            // own acknowledged flag from. See MergeQueue.NotifyGateChanged.
            ctx.Queue.NotifyGateChanged();
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

    /// <summary>
    /// The human drops a queue entry — the terminal <c>Discarded</c> transition, enforced here rather than
    /// anywhere upstream.
    ///
    /// <para><b>Why this is a daemon RPC and not a ViewModel command.</b> The queue is daemon-owned and
    /// persisted; a client-side "remove from list" would clear a rail that the next <c>StreamQueue</c>
    /// snapshot immediately refills, which is the same shape as this repository's <c>FlaggedChangeGate</c>
    /// defect — a control that existed only in the UI layer. Everything that makes a discard mean anything
    /// (the state transition, the persisted record, the audit event, the refusal when a merge is in
    /// flight) is on this side of the wire.</para>
    ///
    /// <para><b>Refused while this repo holds an outstanding merge lease for the entry.</b> A discard
    /// during the window between <c>BeginMerge</c> and <c>ConfirmMerge</c> would move the branch to a
    /// terminal state under a merge that is already executing on the user's checkout, and
    /// <c>ConfirmMerge</c> would then fail to record a merge that really landed — the queue disagreeing
    /// with git, which is the one outcome this whole subsystem exists to prevent.</para>
    ///
    /// <para>The kill switch does NOT gate it. Freezing the queue stops merges; it is not a reason to
    /// forbid the human from tidying an entry that can no longer merge either way.</para>
    /// </summary>
    public override Task<DiscardEntryResponse> DiscardEntry(DiscardEntryRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RepoHandle) || string.IsNullOrWhiteSpace(request.AgentId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "repo_handle and agent_id are required."));
        }

        var ctx = Resolve(request.RepoHandle);

        var lease = ctx.Leases.GetOutstanding(request.RepoHandle);
        if (lease is not null && string.Equals(lease.AgentId, request.AgentId, StringComparison.Ordinal))
        {
            const string held =
                "a merge is in progress for this entry — finish or abandon it before discarding";
            _log.LogWarning("DiscardEntry refused repo={Repo} agent={Agent}: {Reason}",
                request.RepoHandle, request.AgentId, held);
            return Task.FromResult(new DiscardEntryResponse { Discarded = false, Reason = held });
        }

        // SA-1/F2: the actor comes from the connection, never from the message — there is no actor field
        // on DiscardEntryRequest precisely so that no caller can assert one.
        var actor = _identity.Resolve(context);

        if (!ctx.Queue.TryDiscard(request.AgentId, actor, request.Reason ?? string.Empty, out var refusal))
        {
            _log.LogWarning("DiscardEntry refused repo={Repo} agent={Agent}: {Reason}",
                request.RepoHandle, request.AgentId, refusal);
            return Task.FromResult(new DiscardEntryResponse { Discarded = false, Reason = refusal });
        }

        var record = ctx.Queue.GetDiscard(request.AgentId);
        _log.LogInformation(
            "DiscardEntry repo={Repo} agent={Agent} by={By} from={From} reason={Reason}",
            request.RepoHandle, request.AgentId, actor,
            record?.FromState?.ToString() ?? "(unknown)",
            string.IsNullOrWhiteSpace(request.Reason) ? "(none given)" : request.Reason);

        return Task.FromResult(new DiscardEntryResponse
        {
            Discarded = true,
            Reason = "",
            DiscardedBy = record?.By ?? actor,
            DiscardedAt = record?.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "",
        });
    }

    /// <summary>
    /// Clears a <c>Verifying</c> state with no run behind it (see the RPC comment in the proto). The
    /// "is anything actually running" question is only answerable here — the in-flight set is daemon
    /// memory — so both the decision and the refusal live daemon-side.
    /// </summary>
    public override Task<ClearStalledVerificationResponse> ClearStalledVerification(
        ClearStalledVerificationRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RepoHandle) || string.IsNullOrWhiteSpace(request.AgentId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "repo_handle and agent_id are required."));
        }

        var ctx = Resolve(request.RepoHandle);
        var actor = _identity.Resolve(context);
        var cleared = ctx.Queue.TryClearStalledVerification(request.AgentId, actor, out var refusal);

        _log.Log(cleared ? LogLevel.Information : LogLevel.Warning,
            "ClearStalledVerification repo={Repo} agent={Agent} cleared={Cleared} {Reason}",
            request.RepoHandle, request.AgentId, cleared, refusal);

        return Task.FromResult(new ClearStalledVerificationResponse
        {
            Cleared = cleared,
            Reason = refusal,
            // Only for an entry the queue actually tracks. GetState defaults an unknown agent to
            // Working, so reporting it unconditionally would answer a "this entry is not in the merge
            // queue" refusal with state="Working" — asserting a state for an entry that does not exist.
            State = cleared || ctx.Queue.Agents.Contains(request.AgentId)
                ? ctx.Queue.GetState(request.AgentId).ToString()
                : "",
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

    private QueueUpdate Snapshot(string repoHandle, MergeQueueContext ctx)
    {
        var queue = ctx.Queue;
        var update = new QueueUpdate { MainSha = queue.CurrentMainSha };
        foreach (var agentId in queue.Agents)
        {
            var can = queue.CanMerge(agentId, out var reason);
            var entry = new QueueEntry
            {
                AgentId = agentId,
                State = queue.GetState(agentId).ToString(),
                VerifiedMainSha = queue.CurrentMainSha,
                CanMerge = can,
                GateReason = reason,
                // P2-13 carried-in from P2-12: badge external-PR intake entries as such.
                Origin = queue.GetOrigin(agentId).ToString(),
                // Only the daemon can tell a branch being verified from a row that merely says so.
                VerificationInFlight = queue.IsVerificationInFlight(agentId),
                // …and only the daemon holds the session table, so whether this entry still HAS a jail is
                // a fact no client can derive. Keyed on (repo, agent) — an id is unique per repo and not
                // globally, and answering from another repo's live `pr-7` would report a stranded entry
                // as healthy, which is the precise failure that keeps it stranded.
                HasLiveSandbox = _sessions.Find(
                    new Mainguard.Server.Runtime.AgentSessionKey(repoHandle, agentId))
                    ?.ContainerId is { Length: > 0 },
            };

            entry.FlaggedItems.Add(FlaggedItemsFor(ctx, agentId));
            update.Entries.Add(entry);
        }

        return update;
    }

    /// <summary>
    /// The must-acknowledge items blocking <paramref name="agentId"/>, as the review surface has to render
    /// them. <b>These never used to leave the daemon.</b> The RT-D2 gate lives here, the acknowledgment RPC
    /// is addressed by item id, and the client's queue projection hardcoded an empty list — so a branch the
    /// daemon had flagged reached the cockpit as "cannot merge" with no item to clear, and the only route
    /// out was an ack call for an id nothing had told the client about.
    /// </summary>
    private static IEnumerable<FlaggedItem> FlaggedItemsFor(MergeQueueContext ctx, string agentId)
    {
        if (ctx.ChangedTestCommand is { } changed)
        {
            var drifted = changed.FlaggedItems(agentId);
            if (drifted.Count > 0)
            {
                // One row, addressed by the id the daemon's own AcknowledgeFlaggedChange accepts. The gate
                // acknowledges its drift items together (a human clearing one while another went unread is
                // the failure it was shaped to prevent), so it presents as one item naming everything that
                // drifted.
                yield return new FlaggedItem
                {
                    Id = ChangedTestCommandItemId,
                    Path = "(verification command)",
                    Category = "ExecutableConfig",
                    Fact = $"the {string.Join(" and the ", drifted)} changed on this branch vs main "
                        + "— a branch cannot be allowed to self-green",
                    Acknowledged = !changed.IsUnacknowledged(agentId),
                };
            }
        }

        // P2-11: the risk-hunk and out-of-approved-scope rows the flagged-change gate is blocking on. These
        // used to have no daemon-side source at all — the gate was constructed nowhere in the daemon — so a
        // branch touching a CI workflow or a git hook reached the human with nothing to review. PeekStore
        // rather than StoreFor: rendering the queue must never manufacture an "already reviewed" record for
        // an agent whose diff was never classified (see FlaggedChangeGate.PeekStore).
        var store = ctx.FlaggedChanges?.PeekStore(agentId);
        if (store is null)
        {
            yield break;
        }

        foreach (var item in store.Items)
        {
            yield return new FlaggedItem
            {
                // FlaggedChange.Id is kind|path|contentHash — stable within a flagged set and content-bound,
                // so an ack cannot survive the push that changes the bytes it was granted for.
                Id = item.Id,
                Path = item.Path,
                Category = item.Category.ToString(),
                Fact = item.Detail,
                Acknowledged = store.IsAcknowledged(item.Id),
            };
        }
    }
}
