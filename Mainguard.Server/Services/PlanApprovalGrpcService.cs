using System;
using System.Linq;
using System.Threading;
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

    private PlanUpdate Snapshot(string? coordinatorId)
    {
        var update = new PlanUpdate();
        foreach (var plan in _plans.All().Where(p => coordinatorId is null || p.CoordinatorId == coordinatorId))
        {
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
            });
            update.Plans[^1].Scope.AddRange(plan.Plan.Scope);
        }

        update.PressureSignal = coordinatorId is not null ? _plans.PressureSignal(coordinatorId) ?? "" : "";

        // Backpressure (contract §2). The counted population is every live Managed session — the same
        // population the wired spawn gate counts — so what the UI reports and what refuses the coordinator
        // are the same number. Reporting a different one would be a surface that disagrees with its gate.
        var activeWorkers = _sessions.List().Count(s => s.Role == AgentRoles.Managed);
        update.ActiveWorkerCount = activeWorkers;
        update.MaxActiveWorkers = _limits.MaxActiveWorkers;
        update.MaxPlanRevisions = _limits.MaxPlanRevisions;
        update.BlockedWorkerCount = _planGate.BlockedWorkerCount;
        update.EscalatedWorkerCount = _planGate.EscalatedWorkerCount;
        update.BackpressureSignal = _planGate.BackpressureSignal(activeWorkers, _limits.MaxActiveWorkers) ?? "";
        return update;
    }
}
