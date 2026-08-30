using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Logging;

// NOTE: Mainguard.Agents.Agents.Orchestrator is deliberately NOT imported — its PlanApprovalService collides
// with the proto-generated PlanApprovalService. The Core service is referenced fully-qualified below.
namespace Mainguard.Server.Services;

/// <summary>
/// gRPC transport for <see cref="PlanApprovalService"/> (P2-14). Validation + dispatch only — the pending
/// queue, the S-8 caps, the persistence, and the approval record live in the daemon-side
/// <see cref="Mainguard.Agents.Agents.Orchestrator.PlanApprovalService"/>.
///
/// <para><b>SA-1/F2 (binding):</b> <see cref="ApprovePlan"/> takes only a <c>plan_id</c>. The approver
/// identity is resolved <b>daemon-side</b> from the authenticated connection via
/// <see cref="IApproverIdentityResolver"/> — the request carries no identity field, so a client cannot
/// influence the recorded approver (test 11).</para>
/// </summary>
public sealed class PlanApprovalGrpcService : PlanApprovalService.PlanApprovalServiceBase
{
    private readonly Mainguard.Agents.Agents.Orchestrator.PlanApprovalService _plans;
    private readonly Mainguard.Agents.Agents.Orchestrator.WorkerPlanGate _planGate;
    private readonly Mainguard.Agents.Agents.Orchestrator.CoordinatorLimits _limits;
    private readonly Runtime.AgentSessionStore _sessions;
    private readonly IApproverIdentityResolver _identity;
    private readonly ILogger _log;

    public PlanApprovalGrpcService(
        Mainguard.Agents.Agents.Orchestrator.PlanApprovalService plans,
        Mainguard.Agents.Agents.Orchestrator.WorkerPlanGate planGate,
        Mainguard.Agents.Agents.Orchestrator.CoordinatorLimits limits,
        Runtime.AgentSessionStore sessions,
        IApproverIdentityResolver identity,
        ILoggerFactory loggerFactory)
    {
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _planGate = planGate ?? throw new ArgumentNullException(nameof(planGate));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _log = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.Approval);
    }

    public override async Task StreamPlans(
        StreamPlansRequest request, IServerStreamWriter<PlanUpdate> responseStream, ServerCallContext context)
    {
        var coordinatorId = string.IsNullOrWhiteSpace(request.CoordinatorId) ? null : request.CoordinatorId;

        using var signal = new SemaphoreSlim(0);
        void OnChanged() => signal.Release();
        _plans.Changed += OnChanged;

        // …and on session deltas, because a plan's card can now stop being a card without the plan store
        // moving at all. Ending an escalated worker changes no plan record — it removes a session — and a
        // stream woken only by _plans.Changed would keep serving that worker's card until something else
        // happened to touch a plan. The snapshot is what decides; this is only what makes it re-run.
        var sessions = _sessions.Subscribe(out var unsubscribeSessions);
        var pump = Task.Run(async () =>
        {
            try
            {
                while (await sessions.WaitToReadAsync(context.CancellationToken).ConfigureAwait(false))
                {
                    while (sessions.TryRead(out _))
                    {
                    }

                    signal.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Client detached — normal teardown.
            }
            catch (ChannelClosedException)
            {
                // The store completed this subscription (slow-consumer guard); plan changes still wake us.
            }
        });

        try
        {
            await responseStream.WriteAsync(Snapshot(coordinatorId)).ConfigureAwait(false);
            while (!context.CancellationToken.IsCancellationRequested)
            {
                await signal.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                await responseStream.WriteAsync(Snapshot(coordinatorId)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client detached — normal teardown.
        }
        finally
        {
            _plans.Changed -= OnChanged;
            unsubscribeSessions();
            await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        }
    }

    public override Task<ApprovePlanResponse> ApprovePlan(ApprovePlanRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.PlanId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "plan_id is required."));
        }

        // SA-1/F2: the approver is the connection's OS peer credential — NEVER anything in the request.
        var approver = _identity.Resolve(context);
        try
        {
            var approved = _plans.Approve(request.PlanId, approver);
            _log.LogInformation("ApprovePlan plan={Plan} approver={Approver}", request.PlanId, approver);
            return Task.FromResult(new ApprovePlanResponse
            {
                Approved = true,
                ApproverIdentity = approved.ApproverIdentity ?? approver,
            });
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning("ApprovePlan refused plan={Plan}: {Message}", request.PlanId, ex.Message);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    public override Task<RejectPlanResponse> RejectPlan(RejectPlanRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.PlanId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "plan_id is required."));
        }

        try
        {
            // The reason is FEEDBACK, not an epitaph: it goes back to the worker, which revises against it
            // and re-presents. The rejection that spends the revision budget escalates instead — decided
            // daemon-side by PlanApprovalService, and reported here so the UI can render the right card.
            var decided = _plans.Reject(request.PlanId, request.Reason ?? "");
            var escalated = decided.Status == Mainguard.Agents.Agents.Orchestrator.PlanStatus.Escalated;
            _log.LogInformation(
                "RejectPlan plan={Plan} escalated={Escalated} revision={Revision}",
                request.PlanId, escalated, decided.RevisionCount);
            return Task.FromResult(new RejectPlanResponse
            {
                Rejected = true,
                Escalated = escalated,
                RevisionsRemaining = escalated ? 0 : Math.Max(0, _limits.MaxPlanRevisions - decided.RevisionCount),
            });
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning("RejectPlan refused plan={Plan}: {Message}", request.PlanId, ex.Message);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    /// <summary>
    /// The one projection the plan surface is built from — <b>cards and counts together</b>.
    ///
    /// <para><b>Why they are built together.</b> They used to be two independent reads of two different
    /// populations: the entries came straight off the persisted plan store, while
    /// <c>BlockedWorkerCount</c>/<c>EscalatedWorkerCount</c> counted only workers the plan gate was still
    /// holding a task for. The gate forgets a held task the instant a session is torn down, so ending an
    /// escalated worker dropped it from the counts — the amber banner lost it — while its plan record, and
    /// therefore its card, stayed exactly where it was. One update, sent in one message, asserting two
    /// different things about the same worker.</para>
    ///
    /// <para><b>What a plan whose agent is gone should be.</b> Not a gate item. Every move either kind of
    /// card offers needs a live agent behind it: approving a pending plan releases the withheld task to a
    /// session, and an escalated card's copy offers to steer or end a worker. With the session gone there
    /// is nothing to release, steer or end — the plan is history, and it stays in the daemon's store as
    /// history (it is not deleted, and the decided statuses still travel). It simply stops being streamed
    /// as something the human is being asked about.</para>
    ///
    /// <para>So liveness is applied ONCE, to the gate-relevant statuses, and the counts and the sentence
    /// are then derived from the entries this method actually emitted. The banner cannot mention a worker
    /// with no card, and no card can appear that the banner does not count.</para>
    /// </summary>
    private PlanUpdate Snapshot(string? coordinatorId)
    {
        var update = new PlanUpdate();

        // Read the session table ONCE: `live` decides which plans are gate items and `activeWorkers` is the
        // population the spawn cap counts, and the two must not be read a moment apart.
        var sessions = _sessions.List();
        var live = sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var activeWorkers = sessions.Count(s => s.Role == AgentRoles.Managed);

        foreach (var plan in _plans.All().Where(p => coordinatorId is null || p.CoordinatorId == coordinatorId))
        {
            // Pending and Escalated are the two the client renders as cards, and they are the two that
            // claim a human owes an answer. Filtered by liveness so that claim can only be made about a
            // worker that exists. Decided plans are unfiltered history and travel as they always did.
            var isGateItem = plan.Status is Mainguard.Agents.Agents.Orchestrator.PlanStatus.Pending
                or Mainguard.Agents.Agents.Orchestrator.PlanStatus.Escalated;
            if (isGateItem && !live.Contains(plan.WorkerAgentId))
            {
                continue;
            }

            update.Plans.Add(new PlanEntry
            {
                PlanId = plan.PlanId,
                CoordinatorId = plan.CoordinatorId,
                WorkerAgentId = plan.WorkerAgentId,
                Title = plan.Title,
                Approach = plan.Plan.Approach,
                TestStrategy = plan.Plan.TestStrategy,
                Status = plan.Status.ToString(),
                BudgetUsd = (double)plan.BudgetUsd,
                ApproverIdentity = plan.ApproverIdentity ?? "",
                Revision = plan.RevisionCount,
                RevisionsRemaining = Math.Max(0, _limits.MaxPlanRevisions - plan.RevisionCount),
                RejectionFeedback = plan.RejectionFeedback ?? "",
                SupersedesPlanId = plan.SupersedesPlanId ?? "",
                RescopeCount = plan.RescopeCount,
            });
            update.Plans[^1].Scope.AddRange(plan.Plan.Scope);
            update.Plans[^1].PreviousScope.AddRange(plan.PreviousScope ?? Array.Empty<string>());
        }

        update.PressureSignal = coordinatorId is not null ? _plans.PressureSignal(coordinatorId) ?? "" : "";

        // Backpressure (contract §2). ActiveWorkerCount is every live Managed session — the same
        // population the wired spawn gate counts — so what the UI reports and what refuses the coordinator
        // are the same number. Reporting a different one would be a surface that disagrees with its gate.
        //
        // Blocked/escalated are counted off the entries JUST EMITTED, by distinct worker, so the banner is
        // arithmetic over the card list rather than a second opinion about it. Distinct because these are
        // worker counts, not plan counts, and that is what the cap is measured in.
        update.ActiveWorkerCount = activeWorkers;
        update.MaxActiveWorkers = _limits.MaxActiveWorkers;
        update.MaxPlanRevisions = _limits.MaxPlanRevisions;
        update.BlockedWorkerCount = CountWorkers(update, "Pending");
        update.EscalatedWorkerCount = CountWorkers(update, "Escalated");
        update.BackpressureSignal = _planGate.BackpressureSignal(
            update.BlockedWorkerCount, update.EscalatedWorkerCount,
            activeWorkers, _limits.MaxActiveWorkers) ?? "";
        return update;
    }

    /// <summary>Distinct workers among the emitted entries in one status — the banner's arithmetic.</summary>
    private static int CountWorkers(PlanUpdate update, string status) => update.Plans
        .Where(p => string.Equals(p.Status, status, StringComparison.OrdinalIgnoreCase))
        .Select(p => p.WorkerAgentId)
        .Distinct(StringComparer.Ordinal)
        .Count();
}
