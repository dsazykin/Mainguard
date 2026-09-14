using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Mainguard.Protos.V1;

namespace Mainguard.Server.Auth;

/// <summary>
/// P2-14 role + terminal-lock enforcement, <b>daemon-side, at the gRPC layer</b> (never UI-only —
/// convention is not enforcement, plan §7 rejection trigger).
///
/// <para><b>Role (test 6, F11):</b> a connection whose bearer token is a
/// <see cref="ConnectionRole.Coordinator"/> credential may call only the RPCs on
/// <see cref="CoordinatorAllowedMethods"/> — fleet and policy READS, plus its own conversation. Every
/// other method, including one added after this file was last read, is refused with
/// <see cref="StatusCode.PermissionDenied"/>. This is default-deny by construction; it replaced a deny
/// list that was correct about each entry it had and wrong about its shape, since the ~20 methods it
/// never mentioned included spawn, stop, terminal attach, credential harvest, the kill switch,
/// verification, the merge diff, the queue stream, egress policy, budgets and repo provisioning.</para>
///
/// <para><b>Terminal input lock (test 5):</b> for <c>TerminalService.Attach</c> the request (INPUT) stream
/// is wrapped so a <c>data</c> frame toward a <see cref="TerminalLockRegistry"/>-locked agent is rejected
/// server-side — the input stream is severed here, at the interceptor, while the output (read) stream flows
/// untouched. A hand-crafted raw client cannot bypass it.</para>
/// </summary>
public sealed class RoleInterceptor : Interceptor
{
    private const string HeaderKey = "authorization";
    private const string Scheme = "bearer ";

    /// <summary>
    /// <b>F11 — the coordinator's ALLOWLIST.</b> A coordinator credential may call these RPCs and no
    /// others. Everything absent from this set is denied, including RPCs that do not exist yet.
    ///
    /// <para><b>Why this replaced a deny list.</b> The deny list was correct about every entry it had and
    /// wrong about its shape: it enumerated ~30 dangerous methods out of ~50, and the residue — the
    /// methods a coordinator was silently allowed — included <c>SpawnAgent</c> (a manual agent with a full
    /// worktree, undoing the role lock in one RPC), <c>StopAgent</c> (stop a co-tenant),
    /// <c>TerminalService/Attach</c> (the live stream <c>GetScrollback</c> was explicitly denied for),
    /// <c>HarvestAgentCredentials</c>, the kill switch, <c>RunVerification</c> /
    /// <c>GetVerificationLog</c> / <c>GetMergeDiff</c> / <c>StreamQueue</c> (F43),
    /// <c>EgressService/AddAllowlistHost</c> (widen every jail's egress), <c>GatewayService/SetBudgets</c>
    /// and the <c>RepoSyncService</c> mutations. Three auditors found different subsets of that residue
    /// independently, which is the diagnostic: a deny list fails open, and it fails open <em>quietly</em>
    /// — every new RPC is allowed to the coordinator by default, and nothing in the build says so.</para>
    ///
    /// <para><b>What is on it, and why each one.</b> Reads that the coordinator surface is built out of,
    /// plus its own conversation. Nothing here mutates queue, plan, jail, egress, budget or repo state,
    /// and nothing here carries another agent's terminal output or transcript. <c>GetJailLimits</c> is
    /// the read half of a pair whose write half was already denied; <c>CanMerge</c> is a predicate the
    /// coordinator uses to decide whether to bother a human, not an act.</para>
    ///
    /// <para><b>The cost, stated.</b> A new RPC that a coordinator legitimately needs must be added here
    /// or it is refused — loudly, with a message naming the method. That is the trade: a missing entry
    /// breaks a coordinator feature in development, where a deny list's missing entry shipped a hole. In
    /// this codebase the coordinator's production channel is the in-jail Unix socket
    /// (<see cref="Runtime.AgentIpcServer"/>), not gRPC, so the surface being small is not an accident of
    /// this list — it is what the coordinator actually uses.</para>
    /// </summary>
    private static readonly HashSet<string> CoordinatorAllowedMethods = new(StringComparer.Ordinal)
    {
        // Fleet reads: what agents exist, what they are doing, what this daemon is.
        "/mainguard.v1.AgentService/ListAgents",
        "/mainguard.v1.AgentService/StreamAgentEvents",
        "/mainguard.v1.AgentService/StreamAgentResources",
        "/mainguard.v1.AgentService/ListInstalledAdapters",
        "/mainguard.v1.AgentService/GetDaemonInfo",
        // The read half of the jail ceiling; SetJailLimits stays denied (it is the operator's lever over
        // the machine's memory).
        "/mainguard.v1.AgentService/GetJailLimits",
        // A predicate, not an act: "would this branch merge cleanly". Every RPC that actually moves the
        // queue is absent from this list.
        "/mainguard.v1.MergeQueueService/CanMerge",
        // Read-only policy/settings surfaces the coordinator renders. Their write siblings
        // (SetPlanMode, UpdatePrIntakeSettings, SetBudgets, Add/RemoveAllowlistHost) are absent.
        "/mainguard.v1.PlanApprovalService/GetPlanMode",
        "/mainguard.v1.PrIntakeService/GetPrIntakeSettings",
        "/mainguard.v1.GatewayService/GetBudgets",
        "/mainguard.v1.GatewayService/StreamSpend",
        "/mainguard.v1.EgressService/ListAllowlist",
        "/mainguard.v1.RepoSyncService/ListWorktrees",
        // The coordinator's OWN conversation — the one surface that is its own, both directions.
        "/mainguard.v1.CoordinatorService/StreamConversation",
        "/mainguard.v1.CoordinatorService/SendMessage",
    };

    /// <summary>
    /// The former deny list, kept as the annotated record of WHY each of these is refused — the reasoning
    /// is not reconstructable from the allowlist's absences, and it is the reasoning a reviewer needs when
    /// somebody proposes moving one of them across. It is no longer the control: membership here is
    /// implied by absence from <see cref="CoordinatorAllowedMethods"/>. The
    /// <c>CoordinatorAllowlistCoverageTests</c> pin that every entry below is still denied, so this list
    /// cannot rot into a lie.
    /// </summary>
    internal static readonly HashSet<string> CoordinatorDeniedMethods = new(StringComparer.Ordinal)
    {
        "/mainguard.v1.MergeQueueService/BeginMerge",
        "/mainguard.v1.MergeQueueService/ConfirmMerge",
        // The third leg of the same conversation. A coordinator is denied BeginMerge, so it can never hold
        // a lease to hand back, and AbandonMerge already proves lease ownership — but the merge
        // conversation is human-only as a whole, and leaving one leg of it callable by the coordinator
        // role is the kind of gap that only looks harmless until something else changes.
        "/mainguard.v1.MergeQueueService/AbandonMerge",
        // MG-11: acknowledging a flagged change is the human review act that unblocks a merge, so it is
        // merge power by another name — a coordinator that could ack its own branch's flagged items would
        // hold the merge gate it is denied at BeginMerge/ConfirmMerge.
        "/mainguard.v1.MergeQueueService/AcknowledgeFlaggedChange",
        // Discarding an entry is merge power's mirror image and belongs to the human for the same reason.
        // An agent that could discard its own queue entry could delete the record of a branch that was
        // flagged, refused, or simply never verified — erasing the evidence instead of clearing the gate.
        // It is also the queue's only human-driven terminal besides the merge itself.
        "/mainguard.v1.MergeQueueService/DiscardEntry",
        // Rejecting is the review verdict "no" — merge power's other terminal. An agent that could
        // reject a co-tenant's verified branch would hold veto over work it competes with.
        "/mainguard.v1.MergeQueueService/RejectEntry",
        // An agent that could freeze a co-tenant's jail could rig the queue (a paused co-tenant
        // never re-verifies), and one that could unpause could break the cascade's critical section.
        "/mainguard.v1.AgentService/PauseAgent",
        "/mainguard.v1.AgentService/UnpauseAgent",
        // The two parked-conflict actions, on exactly the boundary the line above draws. Handing a
        // conflict back UNPAUSES a co-tenant's jail and then types into its CLI — UnpauseAgent plus the
        // terminal input lock's whole purpose, in one call; aborting a parked rebase rewrites a co-tenant
        // branch's parentage and resumes its jail. Both are merge-adjacent power over work an agent
        // competes with, and neither is anything an agent needs: a worker resolving its OWN conflict does
        // it with the git already in its own worktree.
        "/mainguard.v1.MergeQueueService/ResolveConflictWithAgent",
        "/mainguard.v1.MergeQueueService/AbortRebase",
        // P2-15: the audit chain carries other agents' prompts/outputs and every plan/merge
        // decision. A coordinator that could read it would hold a transcript of work it competes
        // with (and of the human's decisions about it); verify is read power's sibling here.
        "/mainguard.v1.AuditService/VerifyAudit",
        "/mainguard.v1.AuditService/ReadAudit",
        // Same boundary: clearing a stalled verification puts a branch back to Working, which is the state
        // a re-verification starts from. A coordinator that could reset its own branch's verification state
        // would be steering the merge conversation it is denied every other leg of.
        "/mainguard.v1.MergeQueueService/ClearStalledVerification",
        // Refreshing the mirror's main can fire the stale cascade at every co-tenant; that is the merge
        // conversation's own lever, and an agent does not get to pull it (2026-09-04).
        "/mainguard.v1.MergeQueueService/RefreshMirrorMain",
        // The per-jail ceiling is the operator's lever over the machine's memory. A coordinator that could
        // raise it would size its own workers' jails (2026-09-04); reading it is harmless.
        "/mainguard.v1.AgentService/SetJailLimits",
        // Resuming a stranded entry ADOPTS an existing agent id: it attaches a fresh, writable jail to
        // somebody else's `agent/<id>` branch and puts that branch back in front of the daemon's
        // verification. That is strictly more power than the merge RPCs above — an agent able to invoke it
        // could take over another agent's entry, rewrite the branch from inside the adopted jail, and have
        // the daemon verify the result under the original entry's identity. It is a human decision about
        // work a human owns, so it joins the list rather than being guarded by a field check inside a
        // shared message (which this interceptor, which dispatches by method, could not see).
        // Intake configuration is operator policy, and subscribing is a PROVISIONING act rather than a
        // preference: a subscribed repository makes the daemon fetch pull-request heads and ask the gated
        // spawn chain for a jail per open bot PR. An agent that could call these could manufacture queue
        // entries and jails on the user's machine, or widen the bot-author list until its own pull
        // requests were intake'd — merge power reached from the other end. The reads stay open; only the
        // two writes are denied, on the same boundary as BeginMerge/DiscardEntry above.
        "/mainguard.v1.PrIntakeService/UpdatePrIntakeSettings",
        "/mainguard.v1.PrIntakeService/SubscribePrIntakeSource",
        // The dev-only queue seeder composes EnsureEntry + a supplied verification outcome + the
        // merge walk in one RPC — every power this list denies the coordinator piecemeal, reachable
        // at once. Denied unconditionally (whether or not the seeding flag is up): the boot flag
        // decides whether the OPERATOR gets the surface, never whether an agent does.
        "/mainguard.v1.QueueSeedingService/SeedQueueEntries",
        "/mainguard.v1.QueueSeedingService/PushCommits",
        "/mainguard.v1.QueueSeedingService/ClearSeededEntries",
        "/mainguard.v1.QueueSeedingService/GetSeedingStatus",
        "/mainguard.v1.AgentService/ResumeAgent",
        "/mainguard.v1.PlanApprovalService/ApprovePlan",
        "/mainguard.v1.PlanApprovalService/RejectPlan",
        // Sending an escalated worker back for a fresh plan is a plan decision — the one act that reopens
        // an escalation. Same boundary as the two above.
        "/mainguard.v1.PlanApprovalService/RequestNewPlan",
        // The plan-mode toggle, on exactly the boundary above and for a strictly stronger reason. A
        // coordinator that could approve one plan would hold the gate for one worker; a coordinator that
        // could turn plan mode OFF removes the gate for every worker it spawns from then on, without a
        // human seeing a card at all. Contract §4's "plan approval" denial is about holding the gate it
        // is denied at, and this is the wholesale form of it.
        "/mainguard.v1.PlanApprovalService/SetPlanMode",
        // MG-30: GetScrollback serves any agent's daemon-side scrollback ring (up to 1000 rows per
        // page) with no ownership scoping — a coordinator could read a worker's whole session, which
        // is exactly the read the coordinator surface is not supposed to have. The operator token
        // legitimately reads every agent (it drives the UI), so the boundary is the role, and this is
        // the same gate the merge/plan RPCs use. Now genuinely enforced: MG-12 (same change) makes a
        // coordinator token authenticate, so this check is reached instead of being dead code.
        // Per-agent ownership scoping (one connection ↔ its own agents) remains a separate concern.
        "/mainguard.v1.TerminalService/GetScrollback",
        // Phase 3 §6: StreamPlans filters on a CLIENT-ASSERTED coordinator_id, so any caller may name any
        // coordinator — or omit the field and receive every pending plan on the daemon, across every
        // repository and every coordinator. That is the read GetScrollback is denied for, arriving by a
        // different door: plans carry other agents' scope, approach and task prompts, plus the human's
        // decisions about work this coordinator competes with. The durable fix is a DERIVED caller
        // identity rather than a trusted field — the same missing-identity root cause as
        // IssueCoordinatorToken having no production callers — and that is a change to the daemon's
        // authentication model, not this one. Denying the role closes the coordinator-shaped half now.
        // It is unreachable today (the in-jail coordinator has no gRPC route), which is precisely the
        // condition under which a control quietly stops being true (MG-12).
        "/mainguard.v1.PlanApprovalService/StreamPlans",

        // ---- F11: the residue the deny list left allowed. Each of these was reachable with a
        // ---- coordinator credential until the allowlist above became the control.
        //
        // SpawnAgent undoes the role lock in a single call: the coordinator's workers are managed
        // (input-locked, plan-gated) because the SPAWN made them so, and the RPC takes the role as an
        // argument. A coordinator that could call it would give itself a manual agent with a full,
        // writable worktree and no plan gate — every restriction below, bypassed by not being subject
        // to them in the first place.
        "/mainguard.v1.AgentService/SpawnAgent",
        // Stopping is the destructive twin of PauseAgent, which was already denied. It tears down a
        // co-tenant's jail and its unmerged work.
        "/mainguard.v1.AgentService/StopAgent",
        // Harvesting lifts an agent's CLI login material out of its jail. The whole point of the
        // per-agent credential tmpfs is that one agent's keys are not another's.
        "/mainguard.v1.AgentService/HarvestAgentCredentials",
        // MG-30 denied GetScrollback because it serves any agent's session history. Attach serves the
        // same content LIVE, and bidirectionally — the read the coordinator is denied, plus the input
        // stream the terminal lock exists to sever. Denying the page-at-a-time read while leaving the
        // stream open was the gap in its own right.
        "/mainguard.v1.TerminalService/Attach",
        // The emergency stop and its release. Freezing the whole fleet (or unfreezing one somebody
        // froze deliberately) is the operator's act by definition — it is the control that exists for
        // when the agents are the problem.
        "/mainguard.v1.KillSwitchService/Engage",
        "/mainguard.v1.KillSwitchService/Resume",
        // F43. Verification is the gate a branch passes before a human is asked to merge it. A
        // coordinator that could trigger it could re-run a failing branch until a flake let it
        // through; one that could read the log or the diff holds the review material for work it
        // competes with, which is the GetScrollback boundary again by another door.
        "/mainguard.v1.MergeQueueService/RunVerification",
        "/mainguard.v1.MergeQueueService/GetVerificationLog",
        "/mainguard.v1.MergeQueueService/GetMergeDiff",
        // StreamQueue is the whole queue for every repo and every agent — states, branches, verification
        // outcomes and the human's decisions. Same read boundary as StreamPlans directly above.
        "/mainguard.v1.MergeQueueService/StreamQueue",
        // Egress policy is machine-wide: an added allowlist host widens what EVERY jail may reach, and
        // a removed one can break a co-tenant's build. Reading the allowlist stays allowed.
        "/mainguard.v1.EgressService/AddAllowlistHost",
        "/mainguard.v1.EgressService/RemoveAllowlistHost",
        // The spend ceiling is the operator's lever over the bill, exactly like SetJailLimits is the
        // lever over memory. Reading budgets and the spend stream stays allowed.
        "/mainguard.v1.GatewayService/SetBudgets",
        // Repo provisioning and worktree lifecycle are the substrate's own state. RemoveWorktree in
        // particular can delete a co-tenant's working tree; ProvisionRepo/CreateWorktree manufacture
        // daemon-owned state on the user's machine. ListWorktrees stays allowed.
        "/mainguard.v1.RepoSyncService/ProvisionRepo",
        "/mainguard.v1.RepoSyncService/CreateWorktree",
        "/mainguard.v1.RepoSyncService/RemoveWorktree",
    };

    private const string AttachMethod = "/mainguard.v1.TerminalService/Attach";

    private readonly ConnectionRoleRegistry _roles;
    private readonly TerminalLockRegistry _locks;
    private readonly SessionTokenFile _tokenFile;

    public RoleInterceptor(ConnectionRoleRegistry roles, TerminalLockRegistry locks, SessionTokenFile tokenFile)
    {
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _tokenFile = tokenFile ?? throw new ArgumentNullException(nameof(tokenFile));
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        DenyIfCoordinatorForbidden(context);
        return continuation(request, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        DenyIfCoordinatorForbidden(context);
        return continuation(requestStream, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        DenyIfCoordinatorForbidden(context);
        return continuation(request, responseStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        DenyIfCoordinatorForbidden(context);

        // Terminal input lock: wrap the Attach INPUT stream so a data frame to a locked agent is rejected
        // at the interceptor — output (read) still flows.
        if (context.Method == AttachMethod && requestStream is IAsyncStreamReader<TerminalInput> terminalInput)
        {
            var filtered = new LockedInputReader(terminalInput, _locks);
            return continuation((IAsyncStreamReader<TRequest>)(object)filtered, responseStream, context);
        }

        return continuation(requestStream, responseStream, context);
    }

    /// <summary>
    /// F11: default-deny. A coordinator credential proceeds only for a method on
    /// <see cref="CoordinatorAllowedMethods"/>; anything else — including an RPC added tomorrow — is
    /// refused, and the refusal names the method so a legitimately-missing entry is a one-line diagnosis
    /// rather than a mystery.
    /// </summary>
    private void DenyIfCoordinatorForbidden(ServerCallContext context)
    {
        if (CoordinatorAllowedMethods.Contains(context.Method))
        {
            return;
        }

        var token = ExtractBearer(context);
        if (_roles.Resolve(token, _tokenFile.Token) == ConnectionRole.Coordinator)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                $"The coordinator role cannot invoke '{context.Method}'. A coordinator credential is "
                + "limited to an explicit allowlist — fleet/policy READS and its own conversation. Merge, "
                + "entry-lifecycle, plan-approval, spawn/stop, terminal, kill-switch, egress, budget and "
                + "repo-provisioning RPCs are the operator's, and every RPC not on the allowlist is "
                + "refused by default."));
        }
    }

    private static string? ExtractBearer(ServerCallContext context)
    {
        var header = context.RequestHeaders.GetValue(HeaderKey);
        return header is not null && header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            ? header[Scheme.Length..]
            : null;
    }

    /// <summary>
    /// Wraps the <c>Attach</c> request stream: the first <c>agent_id</c> frame selects the agent, and any
    /// subsequent <c>data</c> (input) frame toward a locked agent throws <see cref="StatusCode.PermissionDenied"/>.
    /// Resize frames are harmless (window geometry) and pass through; the output stream is untouched.
    /// </summary>
    // internal (not private) so the MG-31 regression test can drive this reader directly. A gRPC-level
    // test cannot prove this layer: TerminalGrpcService re-checks the lock, so an end-to-end assertion
    // passes whether or not the interceptor tracks the Attach oneof.
    internal sealed class LockedInputReader : IAsyncStreamReader<TerminalInput>
    {
        private readonly IAsyncStreamReader<TerminalInput> _inner;
        private readonly TerminalLockRegistry _locks;
        private string? _agentId;

        public LockedInputReader(IAsyncStreamReader<TerminalInput> inner, TerminalLockRegistry locks)
        {
            _inner = inner;
            _locks = locks;
        }

        public TerminalInput Current => _inner.Current;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            var moved = await _inner.MoveNext(cancellationToken).ConfigureAwait(false);
            if (!moved)
            {
                return false;
            }

            var frame = _inner.Current;
            if (frame.InputCase == TerminalInput.InputOneofCase.AgentId)
            {
                _agentId = frame.AgentId;
            }
            else if (frame.InputCase == TerminalInput.InputOneofCase.Attach)
            {
                // MG-31: a P2-18 grid-capable client selects its agent with the Attach handshake
                // instead of the bare agent_id frame. Tracking only AgentId left `_agentId` null for
                // those clients, so every later Data frame sailed past this gate and the input-lock
                // layer was a no-op for them. TerminalGrpcService re-checks the lock (it reads both
                // oneofs), so this was defense-in-depth rather than a live bypass — but the
                // interceptor is the layer that is supposed to sever input, so it must see both.
                _agentId = frame.Attach.AgentId;
            }
            else if (frame.InputCase == TerminalInput.InputOneofCase.Data
                     && _agentId is not null && _locks.IsLocked(_agentId))
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied,
                    "This terminal is locked (managed worker) — input is denied. The read stream stays open."));
            }

            return true;
        }
    }
}
