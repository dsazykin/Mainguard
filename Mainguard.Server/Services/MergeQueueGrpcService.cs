using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Protos.V1;
using Mainguard.Server.Logging;
using Mainguard.Server.Runtime;
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

    /// <summary>The queue provisioner, for the post-confirm mirror-main refresh. Optional (null in
    /// the slimmest unit fixtures): without one the mirror simply catches up at the next provision.</summary>
    private readonly Mainguard.Agents.Agents.Orchestrator.MergeQueueProvisioner? _queues;

    /// <summary>The G-17 sink for <see cref="ConfirmRefusedEvent"/> — the one merge-conversation fact that
    /// is knowable only at this layer (see the event's own note).</summary>
    private readonly Mainguard.Git.Audit.IAuditLog _audit;
    private readonly ILogger _log;

    public MergeQueueGrpcService(
        IMergeQueueRegistry registry, KillSwitchGate killGate, IMergeBranchDiffService mergeDiff,
        Mainguard.Server.Auth.IApproverIdentityResolver identity,
        Mainguard.Server.Runtime.AgentSessionStore sessions,
        Mainguard.Git.Audit.IAuditLog audit,
        ILoggerFactory loggerFactory,
        Mainguard.Agents.Agents.Orchestrator.MergeQueueProvisioner? queues = null)
    {
        _queues = queues;
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
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

        // Asked BEFORE the run, because it is the one condition under which starting a verification is
        // guaranteed to be pointless: the test command runs inside the worker's own jail (§3.2 — never on
        // the host), and a frozen jail runs nothing. `docker exec` against a paused container answers
        // "Container ... is paused, unpause the container before exec", which reaches the human as a
        // provisioning failure — the one thing that must never be confused with "your tests failed", on
        // the one screen where that distinction decides a merge.
        //
        // The predicate is FrozenJailPolicy's, deliberately shared with the coordinator's
        // `request_verification` guard rather than restated here: the human's Verify and an agent's
        // verify op reach the same queue by different paths, and two spellings of "is this jail frozen"
        // is how one of them stops agreeing with the state word the surface renders. The WORDING is not
        // shared — that policy's sentences are written for an agent to read and act on in one turn ("do
        // not keep polling it"), and this reader is a person looking at a card.
        var frozenSession = _sessions.Find(new Mainguard.Server.Runtime.AgentSessionKey(
            request.RepoHandle, request.AgentId));
        if (FrozenJailPolicy.IsFrozen(
                frozenSession?.State,
                frozenSession is null ? null : _sessions.FrozenReason(frozenSession.Key)))
        {
            var frozen =
                "this agent's jail is frozen, so its tests cannot run — verification runs the test command "
                + "inside the worker's own sandbox and a frozen jail runs nothing. If its keep-alive rebase "
                + "conflicted, hand the conflict back to the agent or abort the rebase; otherwise resume "
                + "the agent, then verify again.";
            _log.LogWarning("RunVerification refused repo={Repo} agent={Agent}: {Reason}",
                request.RepoHandle, request.AgentId, frozen);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, frozen));
        }

        Mainguard.Agents.Agents.Orchestrator.VerificationRecord record;
        try
        {
            if (ctx.Queue.GetState(request.AgentId) == Mainguard.Agents.Agents.WorkerMergeState.StaleVerified)
            {
                // A STALE entry is re-entered through the cascade — reparent, then re-verify — never
                // verified where it stands. The direct run pins a pass to the new main for a branch that
                // does not descend from it: green rail, enabled Merge, and a `--ff-only` that refuses
                // forever. The cascade's re-entry is the one path that puts the branch on top of main
                // first, so the human's Verify takes that road too.
                await ctx.Queue.RequeueStaleAsync(request.AgentId, context.CancellationToken).ConfigureAwait(false);

                var settled = ctx.Queue.GetState(request.AgentId);
                var last = ctx.Queue.LastVerification(request.AgentId);
                if (settled is not (Mainguard.Agents.Agents.WorkerMergeState.Verified
                        or Mainguard.Agents.Agents.WorkerMergeState.VerificationFailed)
                    || last is null)
                {
                    // The re-entry ended at one of the cascade's measured termini (no jail, no worktree,
                    // a conflict, a rebase that did not land) rather than in a run. Its sentence is the
                    // queue's own reason, and it is a refusal here for the same reason every other
                    // pre-run refusal is: no verdict exists to answer with.
                    ctx.Queue.CanMerge(request.AgentId, out var blocked);
                    throw new InvalidOperationException(blocked);
                }

                record = last;
            }
            else
            {
                record = await ctx.Queue.RunVerificationAsync(request.AgentId, context.CancellationToken)
                    .ConfigureAwait(false);
            }
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

        var state = ctx.Queue.GetState(request.AgentId).ToString();

        // H3, the human-driven half: this handler logged every REFUSAL and never once logged a RESULT, so
        // the daemon log recorded that verifications had been refused and never that any of them ran. The
        // verdict is the daemon-observed container exit the queue itself settled the state from — not a
        // second opinion — and the resulting state is reported separately because a run whose entry was
        // discarded mid-flight settles nowhere.
        _log.LogInformation(
            "RunVerification {Verdict} repo={Repo} agent={Agent} command={Command} main={Main} state={State} output={Artifact}",
            record.Passed ? "PASSED" : "FAILED", request.RepoHandle, request.AgentId,
            record.ResolvedCommand, record.MainSha, state, record.LogArtifactPath);

        return new RunVerificationResponse
        {
            AgentId = record.AgentId,
            MainSha = record.MainSha,
            Passed = record.Passed,
            ResolvedCommand = record.ResolvedCommand,
            ConfigHash = record.ConfigHash,
            State = state,
        };
    }

    /// <summary>
    /// The maximum artifact bytes <see cref="GetVerificationLog"/> will return. A failing test suite is
    /// read by a human, and the last 256 KiB of one is comfortably more than a person reads; an unbounded
    /// read would let a runaway suite's output decide the size of a gRPC message.
    /// </summary>
    internal const int MaxVerificationLogBytes = 256 * 1024;

    /// <summary>
    /// H4 — the stdout/stderr of the entry's last verification, which nothing could reach before.
    ///
    /// <para>The daemon wrote the real output to <c>VerificationRecord.LogArtifactPath</c>, stored the path
    /// in SQLite, and put none of it on any wire. So the whole of what a human was told about a red branch
    /// was a one-line gate reason, and the artifact holding the actual failure — the assertion that broke,
    /// the stack, the compiler error — was reachable only by opening the daemon's database by hand. A
    /// human cannot act on a failure they cannot read.</para>
    ///
    /// <para><b>Content, never the path.</b> The artifact lives under the daemon's data directory, G-14
    /// keeps daemon filesystem paths off the wire, and a path is meaningless to a client that is not on
    /// this machine anyway.</para>
    ///
    /// <para><b>Three answers, kept apart.</b> No record at all (<c>has_record=false</c> — the entry has
    /// never been verified); a record whose artifact reads (the log); and a record whose artifact does NOT
    /// read, which answers with the verdict and a stated <c>unavailable_reason</c>. Collapsing the third
    /// into an empty log would render a deleted artifact as a test suite that printed nothing, which is
    /// the same shape of quiet fabrication as the "not verified yet" this whole change removes.</para>
    /// </summary>
    public override Task<GetVerificationLogResponse> GetVerificationLog(
        GetVerificationLogRequest request, ServerCallContext context)
    {
        var ctx = Resolve(request.RepoHandle);
        var record = ctx.Queue.LastVerification(request.AgentId);
        if (record is null)
        {
            return Task.FromResult(new GetVerificationLogResponse { HasRecord = false });
        }

        var response = new GetVerificationLogResponse
        {
            HasRecord = true,
            Passed = record.Passed,
            ResolvedCommand = record.ResolvedCommand ?? string.Empty,
            MainSha = record.MainSha ?? string.Empty,
            When = record.When.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        };

        if (string.IsNullOrWhiteSpace(record.LogArtifactPath))
        {
            // The runner's artifact write is best-effort by design (losing the artifact must not lose the
            // record), so a record with no path is a real state and not an impossible one.
            response.UnavailableReason = "this verification recorded no output artifact";
            return Task.FromResult(response);
        }

        try
        {
            var info = new System.IO.FileInfo(record.LogArtifactPath);
            if (!info.Exists)
            {
                response.UnavailableReason =
                    "the run's output artifact is no longer on disk — the verdict above is still the "
                    + "recorded one, but its output cannot be shown";
                return Task.FromResult(response);
            }

            response.Log = ReadTail(record.LogArtifactPath, MaxVerificationLogBytes, out var truncated);
            response.Truncated = truncated;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            _log.LogWarning(ex, "GetVerificationLog could not read the artifact repo={Repo} agent={Agent}",
                request.RepoHandle, request.AgentId);
            response.UnavailableReason = $"the run's output artifact could not be read: {ex.Message}";
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// The last <paramref name="maxBytes"/> of a file, decoded as UTF-8.
    ///
    /// <para>The TAIL and not the head, deliberately: a test runner prints its failures last, so truncating
    /// from the front is truncating away the reason the human opened the log. <paramref name="truncated"/>
    /// is set whenever anything was dropped, so the surface can say so rather than present a fragment as
    /// the whole run.</para>
    /// </summary>
    internal static string ReadTail(string path, int maxBytes, out bool truncated)
    {
        using var stream = new System.IO.FileStream(
            path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
        truncated = stream.Length > maxBytes;
        if (truncated)
        {
            stream.Seek(-maxBytes, System.IO.SeekOrigin.End);
        }

        using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>On-demand mirror refresh (2026-09-04) — the same call the daemon's interval sweep makes.</summary>
    public override Task<MirrorMainState> RefreshMirrorMain(RefreshMirrorMainRequest request, ServerCallContext context)
    {
        var ctx = Resolve(request.RepoHandle);
        if (_queues is null)
        {
            return Task.FromResult(new MirrorMainState
            {
                MainSha = ctx.Queue.CurrentMainSha,
                RefreshedAt = string.Empty,
                Error = "this daemon has no provisioner to refresh the mirror with",
            });
        }

        var refresh = _queues.RefreshMainFromCheckout(request.RepoHandle);
        return Task.FromResult(new MirrorMainState
        {
            MainSha = refresh.MainSha,
            RefreshedAt = refresh.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Error = refresh.Error ?? string.Empty,
            Moved = refresh.Moved,
        });
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
        // K3 — the identity this lease authorizes, both halves of it. The branch sha is the daemon's OWN
        // record of the tip the verification ran on, read here rather than anywhere downstream: every
        // component that could re-derive it is a component that could re-derive it into agreement with
        // whatever the branch happens to be now (K4).
        var verifiedBranch = ctx.Queue.LastVerification(request.AgentId)?.BranchSha ?? string.Empty;
        var lease = ctx.Leases.TryBegin(
            request.RepoHandle, leaseId, request.AgentId, verified, "main", verifiedBranch);
        if (lease is null && _queues is not null
            && _queues.TryReconcileLandedLease(request.RepoHandle, out _))
        {
            // The held lease was a merge that had already landed (a crash after the client's merge and
            // before its ConfirmMerge). It is recorded now, so this repo is free again — retry once.
            verified = ctx.Queue.CurrentMainSha;
            lease = ctx.Leases.TryBegin(
                request.RepoHandle, leaseId, request.AgentId, verified, "main", verifiedBranch);
        }

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
            ExpectedBranchSha = lease.ExpectedBranchSha,
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
    ///
    /// <para><b>K3/§23.4 adds a fourth, between the first and the second: the sha the caller REPORTS.</b>
    /// Every check above is about the daemon's own state; <c>NewMainSha</c> is a claim about a ref on
    /// somebody else's machine, and it was written into the idempotency record, installed as the queue's
    /// authoritative main, and cascaded at every co-tenant without anything looking at it. See the call
    /// site for the three things that can honestly be checked from here, and the one that cannot.</para>
    /// </summary>
    public override Task<ConfirmMergeResponse> ConfirmMerge(ConfirmMergeRequest request, ServerCallContext context)
    {
        // SA-1/F4: a frozen queue refuses the merge confirmation too.
        ThrowIfFrozen("ConfirmMerge");
        var ctx = Resolve(request.RepoHandle);

        // SA-1/F2: the actor comes from the connection, never from the message — there is no actor field
        // on ConfirmMergeRequest, precisely so no caller can assert who authorised its own merge.
        var actor = _identity.Resolve(context);

        // (1) A held lease is the caller's proof it went through BeginMerge for THIS agent. Leases.Confirm
        // is idempotent and silently no-ops on an unknown lease, so calling it was never a check.
        var lease = ctx.Leases.GetOutstanding(request.RepoHandle);
        if (lease is null
            || !string.Equals(lease.LeaseId, request.LeaseId, StringComparison.Ordinal)
            || !string.Equals(lease.AgentId, request.AgentId, StringComparison.Ordinal))
        {
            _log.LogWarning("ConfirmMerge refused repo={Repo} agent={Agent}: no matching outstanding merge lease",
                request.RepoHandle, request.AgentId);
            AuditConfirmRefused(request, actor, "lease", lease?.ExpectedMainSha ?? "",
                "No outstanding merge lease for this repository and agent.");
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "No outstanding merge lease for this repository and agent — call BeginMerge first."));
        }

        // (1.5) K3 — the reported post-merge sha, which nothing here used to look at.
        //
        // `NewMainSha` is a CLAIM, made by the caller, about a ref on the caller's own machine. The daemon
        // wrote it into the idempotency record, set the queue's authoritative main to it, and fired the
        // cascade at every co-tenant in the repo on the strength of that claim. A wrong value is not a
        // cosmetic error: every co-tenant is then rebased onto, and re-verified against, a main that may
        // not exist, and `CanMerge` compares its evidence against a phantom forever.
        //
        // What can honestly be checked here is checked here; what cannot, is not invented.
        if (!IsWellFormedSha(request.NewMainSha))
        {
            const string malformed =
                "The post-merge sha this confirm reports is not a commit id; nothing was recorded.";
            _log.LogWarning("ConfirmMerge refused repo={Repo} agent={Agent}: malformed post-merge sha",
                request.RepoHandle, request.AgentId);
            ctx.Leases.Release(request.RepoHandle, lease.LeaseId);
            AuditConfirmRefused(request, actor, "identity", lease.ExpectedMainSha, malformed);
            throw new RpcException(new Status(StatusCode.InvalidArgument, malformed));
        }

        if (string.Equals(request.NewMainSha, lease.ExpectedMainSha, StringComparison.Ordinal))
        {
            const string didNotMove =
                "This confirm reports the same main the merge was authorized against — nothing moved, so "
                + "nothing was recorded as merged.";
            _log.LogWarning("ConfirmMerge refused repo={Repo} agent={Agent}: post-merge sha == expected main",
                request.RepoHandle, request.AgentId);
            ctx.Leases.Release(request.RepoHandle, lease.LeaseId);
            AuditConfirmRefused(request, actor, "identity", lease.ExpectedMainSha, didNotMove);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, didNotMove));
        }

        // The strong one, and it is exact rather than probabilistic: a LOCAL entry merges by
        // `git merge --ff-only agent/<id>`, and a fast-forward sets main TO the source's tip. So the sha
        // main moved to must BE the agent/<id> tip the queue verified. The daemon knows that sha — it put
        // it on the lease at grant time — so the client's claim is checkable against the daemon's own
        // record without reading the client's repository at all.
        //
        // Only for Local, and stated as a limit rather than stretched: the P2-12 external leg lands the
        // HOST's merge commit, which is not the PR head and could not be, so the same equality would be
        // false for every honest external merge. That path has its own head compare-and-swap (K4) and the
        // host's own `sha` merge parameter. An empty ExpectedBranchSha is an unknown, not a mismatch.
        if (ctx.Queue.GetOrigin(request.AgentId) != Mainguard.Agents.Agents.MergeEntryOrigin.External
            && !string.IsNullOrEmpty(lease.ExpectedBranchSha)
            && !string.Equals(request.NewMainSha, lease.ExpectedBranchSha, StringComparison.OrdinalIgnoreCase))
        {
            var mismatch =
                "This confirm reports a post-merge main that is not the branch this merge was authorized "
                + "for. A fast-forward merge leaves main AT the merged branch's tip, and the queue verified "
                + $"agent/{request.AgentId} at {Short(lease.ExpectedBranchSha)}. Nothing was recorded.";
            _log.LogWarning(
                "ConfirmMerge refused repo={Repo} agent={Agent}: reported main={Reported} != verified branch={Branch}",
                request.RepoHandle, request.AgentId, request.NewMainSha, lease.ExpectedBranchSha);
            ctx.Leases.Release(request.RepoHandle, lease.LeaseId);
            AuditConfirmRefused(request, actor, "identity", lease.ExpectedMainSha, mismatch);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, mismatch));
        }

        // (2)+(3) Gate and freshness, atomically with the Merged transition.
        if (!ctx.Queue.TryConfirmHumanMerge(
                request.AgentId, request.NewMainSha, lease.ExpectedMainSha, out var reason,
                MergeAuthorization.ConfirmRpc(actor, lease.LeaseId)))
        {
            // By the time this RPC is reached the client's git operation has ALREADY RUN (§21.2), so a
            // refusal here does not prevent a merge — it decides whether the daemon reflects one. When the
            // reported sha IS the branch tip the lease authorized (Local entries fast-forward main exactly
            // there), the reviewed bytes demonstrably landed: record it, late, under its own source. The
            // shape this closes: the worker pushed between BeginMerge and ConfirmMerge, the invalidator
            // walked the row to Working, the gate refused on state — and the user's main had moved while
            // the queue said "not merged", with nothing anywhere that would ever reconcile the two.
            var landedTheAuthorizedTip =
                ctx.Queue.GetOrigin(request.AgentId) != Mainguard.Agents.Agents.MergeEntryOrigin.External
                && !string.IsNullOrEmpty(lease.ExpectedBranchSha)
                && string.Equals(request.NewMainSha, lease.ExpectedBranchSha, StringComparison.OrdinalIgnoreCase);
            if (landedTheAuthorizedTip)
            {
                ctx.Queue.ConfirmHumanMerge(
                    request.AgentId, request.NewMainSha, MergeAuthorization.ConfirmRpcLate(actor, lease.LeaseId));
                ctx.Leases.Confirm(request.RepoHandle, request.LeaseId, request.NewMainSha);
                _log.LogWarning(
                    "ConfirmMerge recorded LATE repo={Repo} agent={Agent} newMainSha={Sha}: the gate said "
                    + "\"{Reason}\" but the reported sha is the authorized branch tip, so the merge had landed",
                    request.RepoHandle, request.AgentId, request.NewMainSha, reason);
                if (_queues is not null && !_queues.TryRefreshMirrorMainAfterMerge(request.RepoHandle, out var lateRefresh))
                {
                    _log.LogWarning("ConfirmMerge: mirror main refresh failed repo={Repo}: {Reason}",
                        request.RepoHandle, lateRefresh);
                }

                return Task.FromResult(new ConfirmMergeResponse
                {
                    Confirmed = true,
                    Note = "recorded after the fact — the daemon's gate refused this confirm (" + reason
                         + "), but the reported main is exactly the branch tip this merge was authorized "
                         + "for, so the reviewed work had already landed.",
                });
            }

            // Otherwise the lease stays OUTSTANDING. Releasing it here used to be justified by "the boot
            // reconcile synthesizes the confirm from the journal" — which was false twice over: the
            // reconcile never resolved a repo, and a released lease is never reconciled at all. Held, the
            // lease is exactly what the queue-creation and on-demand reconciles act on; the repo is not
            // stranded, because BeginMerge reconciles a landed merge before refusing on a held lease.
            _log.LogWarning("ConfirmMerge refused repo={Repo} agent={Agent}: {Reason} (lease kept outstanding for the reconcile)",
                request.RepoHandle, request.AgentId, reason);
            AuditConfirmRefused(request, actor, "gate", lease.ExpectedMainSha, reason);
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                reason + " The merge lease stays outstanding until the daemon can establish what landed."));
        }

        // Only now is the idempotency record written: a confirmed lease is the daemon's statement that this
        // merge landed, and the boot reconcile reads it to tell a landed merge from an abandoned one.
        ctx.Leases.Confirm(request.RepoHandle, request.LeaseId, request.NewMainSha);
        _log.LogInformation("ConfirmMerge repo={Repo} agent={Agent} newMainSha={Sha}",
            request.RepoHandle, request.AgentId, request.NewMainSha);

        // Pull the mirror's main forward NOW rather than at the next repo-open: a spawn in the gap
        // would base its worktree on the pre-merge main, and EnsureQueue's reconcile — which trusts
        // the mirror — would walk the queue's authoritative main BACKWARDS to it, making every later
        // verification coherent-but-unmergeable (observed live; the E2E suite never walks this window
        // because it verifies before merging). Best-effort by design: the merge has landed either way.
        if (_queues is not null && !_queues.TryRefreshMirrorMainAfterMerge(request.RepoHandle, out var refresh))
        {
            _log.LogWarning("ConfirmMerge: mirror main refresh failed repo={Repo}: {Reason}",
                request.RepoHandle, refresh);
        }

        return Task.FromResult(new ConfirmMergeResponse { Confirmed = true });
    }

    /// <summary>
    /// The audit event for a <c>ConfirmMerge</c> the daemon REFUSED.
    ///
    /// <para><b>Why a refusal is worth a tamper-evident record when a <c>BeginMerge</c> refusal is
    /// not.</b> The three-step is <c>BeginMerge</c> → the client merges on the user's own checkout →
    /// <c>ConfirmMerge</c>. By the time this RPC is reached the git operation has ALREADY RUN: the caller
    /// is reporting a post-merge sha it claims a ref now holds. Refusing therefore does not prevent a
    /// merge — it means the daemon and the user's repository may now disagree about what main is, which is
    /// the single failure mode this whole subsystem exists to make impossible. That divergence has to
    /// leave an artifact for the person who later asks "why does the queue think this never merged". A
    /// refused <c>BeginMerge</c>, by contrast, is a merge that has not happened and will not, so recording
    /// it would fill the chain with non-events.</para>
    ///
    /// <para>Best-effort, unlike <see cref="MergeQueue.MergedEvent"/>: the refusal itself is the caller's
    /// answer and must survive an audit outage, so a throwing append is swallowed into the daemon log
    /// rather than replacing a precise refusal reason with an audit-store error.</para>
    /// </summary>
    internal const string ConfirmRefusedEvent = "merge_confirm_refused";

    /// <summary>
    /// Whether a string is shaped like a git object id at all: 7–64 lowercase-or-uppercase hex characters.
    /// Deliberately a SHAPE check and nothing more — the daemon cannot resolve a sha in a repository it does
    /// not hold, and a shape check that pretended to be an existence check would be the same fabrication as
    /// the claim it is screening.
    /// </summary>
    private static bool IsWellFormedSha(string? sha)
    {
        if (string.IsNullOrEmpty(sha) || sha!.Length is < 7 or > 64)
        {
            return false;
        }

        foreach (var c in sha)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    private void AuditConfirmRefused(
        ConfirmMergeRequest request, string actor, string stage, string expectedMainSha, string reason)
    {
        try
        {
            _audit.Append(new Mainguard.Git.Audit.AuditEvent(ConfirmRefusedEvent,
                new Dictionary<string, string>
                {
                    ["repo"] = request.RepoHandle ?? string.Empty,
                    ["agent"] = request.AgentId ?? string.Empty,
                    ["by"] = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor,
                    ["lease"] = request.LeaseId ?? string.Empty,
                    ["stage"] = stage,
                    ["expected_main_sha"] = expectedMainSha ?? string.Empty,
                    // The sha the CALLER says main now is. Named "reported" and never "post_main_sha":
                    // nothing here verified it, and the whole point of the record is that the daemon
                    // declined to accept the claim.
                    ["reported_main_sha"] = request.NewMainSha ?? string.Empty,
                    ["reason"] = reason ?? string.Empty,
                    ["when"] = DateTimeOffset.UtcNow.ToString(
                        "O", System.Globalization.CultureInfo.InvariantCulture),
                }));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ConfirmMerge refusal could not be audited repo={Repo} agent={Agent}",
                request.RepoHandle, request.AgentId);
        }
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
            // SA-1/F2: the waiver's actor comes from the connection, never from the message. This is the
            // one acknowledgment in the product that lets a branch self-green, so an unattributed one
            // would be the least useful record in the chain.
            changed.Acknowledge(request.AgentId, _identity.Resolve(context));

            // Acknowledge is a no-op for an agent that is not flagged, so the "was it really cleared?"
            // answer is read back off the gate rather than assumed from the call having been made.
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
    /// The review verdict "no" — the human judged a verified branch's work and rejected it (terminal).
    /// Mirrors <see cref="DiscardEntry"/> exactly: daemon-derived actor (no actor field on the request),
    /// refusal-as-response, refused while this entry holds the outstanding merge lease, NOT gated by
    /// the kill switch (freezing merges is not a reason to forbid a review verdict). The state rules —
    /// only Verified/AwaitingReview can be rejected — live in <c>MergeQueue.TryReject</c>.
    /// </summary>
    public override Task<RejectEntryResponse> RejectEntry(RejectEntryRequest request, ServerCallContext context)
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
                "a merge is in progress for this entry — finish or abandon it before rejecting";
            _log.LogWarning("RejectEntry refused repo={Repo} agent={Agent}: {Reason}",
                request.RepoHandle, request.AgentId, held);
            return Task.FromResult(new RejectEntryResponse { Rejected = false, Reason = held });
        }

        // SA-1/F2: the actor comes from the connection, never from the message.
        var actor = _identity.Resolve(context);
        var when = DateTimeOffset.UtcNow;

        if (!ctx.Queue.TryReject(request.AgentId, actor, request.Reason ?? string.Empty, out var refusal))
        {
            _log.LogWarning("RejectEntry refused repo={Repo} agent={Agent}: {Reason}",
                request.RepoHandle, request.AgentId, refusal);
            return Task.FromResult(new RejectEntryResponse { Rejected = false, Reason = refusal });
        }

        _log.LogInformation(
            "RejectEntry repo={Repo} agent={Agent} by={By} reason={Reason}",
            request.RepoHandle, request.AgentId, actor,
            string.IsNullOrWhiteSpace(request.Reason) ? "(none given)" : request.Reason);

        return Task.FromResult(new RejectEntryResponse
        {
            Rejected = true,
            Reason = "",
            RejectedBy = actor,
            RejectedAt = when.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
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

    /// <summary>
    /// "Let the agent resolve": unpause the parked jail and hand the conflict back to the worker that
    /// wrote half of it. Transport only — the parking record, the unpause, the prompt delivery and every
    /// refusal live on <see cref="MergeQueueProvisioner.LetAgentResolveConflictAsync"/>.
    ///
    /// <para><b>Gated by the kill switch, unlike Discard.</b> Discard is housekeeping on an entry that
    /// cannot merge either way; this one <c>docker unpause</c>s a jail and then types at its CLI, which is
    /// exactly the pair of things an emergency stop exists to prevent. A frozen queue must not have a
    /// button on it that wakes an agent up.</para>
    /// </summary>
    public override async Task<ResolveConflictWithAgentResponse> ResolveConflictWithAgent(
        ResolveConflictWithAgentRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RepoHandle) || string.IsNullOrWhiteSpace(request.AgentId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "repo_handle and agent_id are required."));
        }

        ThrowIfFrozen("handing a rebase conflict back to its agent");
        Resolve(request.RepoHandle);

        if (_queues is null)
        {
            const string unwired =
                "this daemon has no queue provisioner wired, so it holds no record of parked conflicts";
            _log.LogWarning("ResolveConflictWithAgent refused repo={Repo} agent={Agent}: {Reason}",
                request.RepoHandle, request.AgentId, unwired);
            return new ResolveConflictWithAgentResponse { HandedBack = false, Reason = unwired };
        }

        var result = await _queues
            .LetAgentResolveConflictAsync(request.RepoHandle, request.AgentId, context.CancellationToken)
            .ConfigureAwait(false);

        _log.Log(result.Done ? LogLevel.Information : LogLevel.Warning,
            "ResolveConflictWithAgent repo={Repo} agent={Agent} handedBack={HandedBack} {Reason}",
            request.RepoHandle, request.AgentId, result.Done, result.Reason);

        return new ResolveConflictWithAgentResponse { HandedBack = result.Done, Reason = result.Reason };
    }

    /// <summary>
    /// "Abort rebase": <c>git rebase --abort</c> in the parked worktree, then let the jail run again.
    /// Transport only, same as above.
    ///
    /// <para><b>Kill-switch gated for the same reason</b> — it ends by resuming the jail — and refused
    /// while this entry holds the outstanding merge lease, for the reason <see cref="DiscardEntry"/> is:
    /// moving a branch's parentage under a merge that is already executing on the user's checkout is the
    /// queue disagreeing with git.</para>
    /// </summary>
    public override async Task<AbortRebaseResponse> AbortRebase(
        AbortRebaseRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RepoHandle) || string.IsNullOrWhiteSpace(request.AgentId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "repo_handle and agent_id are required."));
        }

        ThrowIfFrozen("aborting a parked rebase");
        var ctx = Resolve(request.RepoHandle);

        var lease = ctx.Leases.GetOutstanding(request.RepoHandle);
        if (lease is not null && string.Equals(lease.AgentId, request.AgentId, StringComparison.Ordinal))
        {
            const string held =
                "a merge is in progress for this entry — finish or abandon it before aborting the rebase";
            _log.LogWarning("AbortRebase refused repo={Repo} agent={Agent}: {Reason}",
                request.RepoHandle, request.AgentId, held);
            return new AbortRebaseResponse { Aborted = false, Reason = held };
        }

        if (_queues is null)
        {
            const string unwired =
                "this daemon has no queue provisioner wired, so it holds no record of parked conflicts";
            _log.LogWarning("AbortRebase refused repo={Repo} agent={Agent}: {Reason}",
                request.RepoHandle, request.AgentId, unwired);
            return new AbortRebaseResponse { Aborted = false, Reason = unwired };
        }

        var result = await _queues
            .AbortParkedRebaseAsync(request.RepoHandle, request.AgentId, context.CancellationToken)
            .ConfigureAwait(false);

        _log.Log(result.Done ? LogLevel.Information : LogLevel.Warning,
            "AbortRebase repo={Repo} agent={Agent} aborted={Aborted} {Reason}",
            request.RepoHandle, request.AgentId, result.Done, result.Reason);

        return new AbortRebaseResponse { Aborted = result.Done, Reason = result.Reason };
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
        if (_queues?.LastMainRefresh(repoHandle) is { } refresh)
        {
            update.MirrorMainRefreshedAt = refresh.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            update.MirrorMainRefreshError = refresh.Error ?? string.Empty;
        }

        var orderedAgentIds = OrderForDisplay(queue.Agents, queue.GetState, queue.LastChangedAt);
        foreach (var agentId in orderedAgentIds)
        {
            var can = queue.CanMerge(agentId, out var reason);
            var record = queue.LastVerification(agentId);
            var entry = new QueueEntry
            {
                AgentId = agentId,
                State = queue.GetState(agentId).ToString(),
                // The main the RECORD ran against, not the main of this instant. The wire has always
                // documented this field as "the main@sha this branch's verification ran against" and the
                // daemon has always filled it with `queue.CurrentMainSha`, so the cockpit's "verified @
                // <sha>" stamp named today's main whatever the evidence was measured on — most visibly for
                // a StaleVerified entry, where the two are guaranteed to differ and the stamp asserted the
                // freshness the state exists to deny. Falls back to current main only when there is no
                // record at all, where nothing is being claimed about a run.
                VerifiedMainSha = record?.MainSha is { Length: > 0 } ran ? ran : queue.CurrentMainSha,
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
                //
                // ISSUES-LOG #24: the RECONCILED answer wins when there is one. The session store alone was
                // wrong in both directions and stayed wrong — it is memory-only, so after a daemon restart
                // every surviving entry read as jail-less until something re-spawned it; and a session the
                // reconciler has marked Unresponsive keeps its container id, so a dead agent went on
                // reporting a live sandbox and went on offering Verify. MergeQueue.ReconcileJails measures
                // this against Docker every 30s and answers null only until its first pass, which is what
                // the store is still consulted for.
                HasLiveSandbox = queue.HasLiveJail(agentId)
                    ?? (_sessions.Find(new Mainguard.Server.Runtime.AgentSessionKey(repoHandle, agentId))
                        ?.ContainerId is { Length: > 0 }),
            };

            // H2/H4 — the verdict behind the state word. The queue has held this record all along and no
            // wire ever carried it, so a client could see that an entry was blocked and never what the
            // verification actually found. The record is the queue's OWN settled one, so what is rendered
            // cannot disagree with the state it is rendered under. Left absent — not false — when there is
            // no record, because an absent verdict and a failing one must stay distinguishable.
            if (record is { } verification)
            {
                entry.LastVerificationPassed = verification.Passed;
                entry.LastVerificationCommand = verification.ResolvedCommand ?? string.Empty;
                entry.LastVerificationAt = verification.When.ToString(
                    "O", System.Globalization.CultureInfo.InvariantCulture);
            }

            // What the human APPROVED, carried to the surface that reviews the result of it. Without
            // this the cockpit renders a diff and nothing to compare it against: the approved approach
            // was written by the worker, read by the human, decided on — and then never surfaced again,
            // so a branch that did the opposite of it looked exactly like one that did not. Left empty
            // for an entry with no approved plan, which is how the surface knows not to draw a panel
            // asserting an approval that does not exist.
            if (ctx.ResolveApprovedWork?.Invoke(agentId) is { } approved)
            {
                entry.ApprovedPlanId = approved.Plan.PlanId ?? string.Empty;
                entry.ApprovedPlanTitle = approved.Plan.Title ?? string.Empty;
                entry.ApprovedPlanApproach = approved.Plan.Approach ?? string.Empty;
                entry.DeviationDeclaration = approved.Declaration.ToString();
            }

            // The facts behind the conflict card. The gate reason has always said that a rebase conflict
            // needs a human; WHERE the parked worktree is and WHAT conflicts lived only in one audit event
            // and one log line, so the card named a required human action without saying what it was
            // about. Present only while something really is parked — the projection never invents an empty
            // conflict, because an empty path list would read as "nothing conflicts".
            if (_queues?.ParkedConflicts.Find(repoHandle, agentId) is { } parked)
            {
                var conflict = new RebaseConflict
                {
                    Worktree = parked.WorktreePath,
                    MainBranch = parked.MainBranch,
                    ParkedAt = parked.ParkedAt.ToString(
                        "O", System.Globalization.CultureInfo.InvariantCulture),
                };
                conflict.Paths.Add(parked.ConflictedPaths);
                entry.RebaseConflict = conflict;
            }

            entry.FlaggedItems.Add(FlaggedItemsFor(ctx, agentId));
            update.Entries.Add(entry);
        }

        return update;
    }

    /// <summary>
    /// The rail's display order. <c>MergeQueue.Agents</c> is stable dictionary-insertion order — oldest
    /// entry first, forever. Merged/Rejected rows are kept as a permanent record (by design — see
    /// <c>MergeQueue.Agents</c>' own doc comment on Discard), so on a repo with any testing/iteration
    /// history they accumulate at the FRONT of that order and bury every new, actionable entry at the
    /// bottom of the rail behind a thin scrollbar with no "N more" cue. Live-clicking this (2026-08-20)
    /// reproduced exactly the "my spawned agent isn't in the queue" symptom — the entry was there, just
    /// scrolled out of view. A stable partition (actionable first, terminal last, relative order
    /// preserved within each) fixes discoverability without changing queue membership or any state
    /// semantics. <c>internal</c> so it's independently unit-testable without a live queue.
    ///
    /// <para><b>Within the terminal group the order is newest decision first</b>, by
    /// <c>MergeQueue.LastChangedAt</c> — because insertion order is SPAWN order, not decision order. With
    /// the partition alone, a branch rejected thirty seconds ago renders behind every Merged/Rejected row
    /// spawned before it, i.e. at the very bottom of a list several viewports tall — and the human who
    /// just clicked Reject sees the entry disappear off the end of the rail instead of taking its place in
    /// the history. That was filed as a HIGH "rejected entries vanish from the queue" regression
    /// (walkthrough 2026-08-20, ISSUES-LOG #13) when nothing had vanished: the row was rendering, 13th of
    /// 13, below the fold. The actionable group is deliberately NOT re-sorted — it is a work queue, and
    /// oldest-first is the order work should be picked up in.</para>
    /// </summary>
    /// <param name="decidedAt">
    /// When an entry last transitioned. Optional: <c>null</c> (and a null answer for any single id) keeps
    /// that entry in its insertion position within its group, so a caller with no clock — and a row
    /// written by a daemon that predates the field — degrades to the plain stable partition.
    /// </param>
    internal static IEnumerable<string> OrderForDisplay(
        IEnumerable<string> agentIds,
        Func<string, Mainguard.Agents.Agents.WorkerMergeState> stateOf,
        Func<string, DateTimeOffset?>? decidedAt = null)
    {
        var ordered = agentIds.ToList();
        var actionable = ordered.Where(id => !IsTerminalForDisplay(stateOf(id)));
        var terminal = ordered.Where(id => IsTerminalForDisplay(stateOf(id)));

        if (decidedAt is not null)
        {
            // Descending by decision time; OrderByDescending is stable, so entries with no timestamp
            // (all equal at DateTimeOffset.MinValue) keep their relative insertion order at the back.
            terminal = terminal.OrderByDescending(id => decidedAt(id) ?? DateTimeOffset.MinValue);
        }

        return actionable.Concat(terminal);
    }

    private static bool IsTerminalForDisplay(Mainguard.Agents.Agents.WorkerMergeState state)
        => state is Mainguard.Agents.Agents.WorkerMergeState.Merged
            or Mainguard.Agents.Agents.WorkerMergeState.Rejected;

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
