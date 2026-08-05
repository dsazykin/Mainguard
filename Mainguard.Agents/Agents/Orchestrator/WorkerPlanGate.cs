using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Git.Audit;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// The <b>daemon-side</b> enforcement of the phase-2 plan gate (coordinator contract §2 + §5).
///
/// <para><b>Why this exists separately from <see cref="PlanApprovalService"/>.</b> That service owns the
/// queue and the blocking <c>AwaitDecisionAsync</c> call. But a blocking call an agent can simply decline
/// to make is not a boundary — it is a convention, and this codebase has already shipped one control that
/// looked present and enforced nothing (MG-12: role authorization was dead code that failed open). So the
/// question "may this worker actually do its work yet?" is answered here, at the points where a worker's
/// work can have any effect, and the answer is enforced whether or not the worker cooperates:</para>
///
/// <list type="number">
/// <item><b>The daemon holds the task.</b> A worker is spawned with its task prompt <i>withheld</i>
/// (<see cref="Hold"/>). The prompt is released only by <see cref="TryReleaseTask"/>, and only once the
/// worker's own plan is approved. A worker that never presents a plan never learns what it was spawned to
/// do — there is no "start early" state to police, because the work was never handed over.</item>
/// <item><b>Steering is refused.</b> <see cref="MayReceivePrompt"/> denies the coordinator's
/// <c>send_worker_prompt</c> to a worker still at the gate, so the coordinator cannot hand over the task
/// out of band.</item>
/// <item><b>The merge queue refuses it.</b> This type is an <see cref="IMergeGate"/>: a branch whose
/// worker never had a plan approved cannot merge, no matter what it verified. That is the backstop — even
/// if a worker did work it was not cleared for, that work cannot reach <c>main</c>.</item>
/// </list>
///
/// <para><b>Backpressure legibility.</b> A blocked worker counts against
/// <see cref="CoordinatorLimits.MaxActiveWorkers"/> (it still holds a jail, tmpfs, network segment and
/// worktree). When the cap is saturated by workers awaiting approval, <see cref="BackpressureSignal"/>
/// says so in words — "6 workers waiting on your approval". A silent stall is indistinguishable from a
/// hang, so this is a requirement, not a nicety.</para>
/// </summary>
public sealed class WorkerPlanGate : IMergeGate
{
    private readonly PlanApprovalService _plans;
    private readonly IAuditLog _audit;
    private readonly object _gate = new();
    private readonly Dictionary<string, HeldTask> _held = new(StringComparer.Ordinal);

    public WorkerPlanGate(PlanApprovalService plans, IAuditLog? audit = null)
    {
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _audit = audit ?? new InMemoryAuditLog();
    }

    /// <summary>A task prompt withheld from a worker until its plan is approved.</summary>
    private sealed record HeldTask(string CoordinatorId, string Title, string TaskPrompt, decimal BudgetUsd, bool Released);

    /// <summary>Raised (off any lock) when a worker's task is released, so the daemon can deliver it.</summary>
    public event Action<string, string>? TaskReleased;

    /// <summary>
    /// Records the work a worker was spawned for <b>without giving it to the worker</b>. Called on the
    /// spawn path; idempotent per worker id.
    /// </summary>
    public void Hold(string workerAgentId, string coordinatorId, string title, string taskPrompt, decimal budgetUsd)
    {
        if (string.IsNullOrWhiteSpace(workerAgentId))
        {
            throw new ArgumentException("workerAgentId is required.", nameof(workerAgentId));
        }

        lock (_gate)
        {
            if (_held.ContainsKey(workerAgentId))
            {
                return;
            }

            _held[workerAgentId] = new HeldTask(coordinatorId ?? "", title ?? "", taskPrompt ?? "", budgetUsd, Released: false);
        }

        _audit.Append(new AuditEvent("worker_task_withheld", new Dictionary<string, string>
        {
            ["worker_agent_id"] = workerAgentId,
            ["coordinator_id"] = coordinatorId ?? "",
        }));
    }

    /// <summary>The brief a worker IS given up front: what to plan about, never what to do.</summary>
    /// <remarks>
    /// The distinction is deliberate. The worker needs enough to inspect the right part of the repository
    /// and author a meaningful plan; it does not need — and does not get — the executable task until a
    /// human has read that plan.
    /// </remarks>
    public string? PlanningBriefFor(string workerAgentId)
    {
        lock (_gate)
        {
            return _held.TryGetValue(workerAgentId, out var task) ? task.Title : null;
        }
    }

    /// <summary>The coordinator that spawned this worker (null when unknown).</summary>
    public string? CoordinatorFor(string workerAgentId)
    {
        lock (_gate)
        {
            return _held.TryGetValue(workerAgentId, out var task) ? task.CoordinatorId : null;
        }
    }

    /// <summary>The budget the coordinator allotted this worker (0 when unknown).</summary>
    public decimal BudgetFor(string workerAgentId)
    {
        lock (_gate)
        {
            return _held.TryGetValue(workerAgentId, out var task) ? task.BudgetUsd : 0m;
        }
    }

    /// <summary>
    /// Releases the withheld task prompt — <b>only</b> when this worker holds an approved plan. Returns
    /// false and yields nothing otherwise; there is no override parameter, because an override is how a
    /// gate becomes decorative.
    /// </summary>
    public bool TryReleaseTask(string workerAgentId, out string taskPrompt)
    {
        taskPrompt = string.Empty;
        if (!_plans.HasApprovedPlan(workerAgentId))
        {
            _audit.Append(new AuditEvent("worker_task_release_denied", new Dictionary<string, string>
            {
                ["worker_agent_id"] = workerAgentId,
                ["cause"] = "no-approved-plan",
            }));
            return false;
        }

        lock (_gate)
        {
            if (!_held.TryGetValue(workerAgentId, out var task))
            {
                return false;
            }

            taskPrompt = task.TaskPrompt;
            _held[workerAgentId] = task with { Released = true };
        }

        _audit.Append(new AuditEvent("worker_task_released", new Dictionary<string, string>
        {
            ["worker_agent_id"] = workerAgentId,
        }));
        TaskReleased?.Invoke(workerAgentId, taskPrompt);
        return true;
    }

    /// <summary>True once this worker's task has actually been handed over.</summary>
    public bool TaskWasReleased(string workerAgentId)
    {
        lock (_gate)
        {
            return _held.TryGetValue(workerAgentId, out var task) && task.Released;
        }
    }

    /// <summary>Drops a stopped worker's held task (teardown; keeps the gate from growing unboundedly).</summary>
    public void Forget(string workerAgentId)
    {
        lock (_gate)
        {
            _held.Remove(workerAgentId);
        }
    }

    /// <summary>
    /// May this worker do the work it was spawned for? True only with an approved plan. The reason is
    /// written for a human to read verbatim.
    /// </summary>
    public bool MayWork(string workerAgentId, out string reason)
    {
        if (_plans.HasApprovedPlan(workerAgentId))
        {
            reason = string.Empty;
            return true;
        }

        var plan = _plans.LatestForWorker(workerAgentId);
        reason = plan?.Status switch
        {
            PlanStatus.Pending =>
                $"{workerAgentId} is waiting on your approval of its plan — it has not started work.",
            PlanStatus.Rejected =>
                $"{workerAgentId} is revising its plan against your feedback (revision " +
                $"{plan.RevisionCount + 1} of {_plans.MaxPlanRevisions}).",
            PlanStatus.Escalated =>
                $"{workerAgentId} stopped after {_plans.MaxPlanRevisions} rejected plans and escalated to you.",
            _ => $"{workerAgentId} has not presented a plan yet — no work is authorised.",
        };
        return false;
    }

    /// <summary>Whether the coordinator may steer this worker (denied while it is held at the gate).</summary>
    public bool MayReceivePrompt(string workerAgentId, out string reason) => MayWork(workerAgentId, out reason);

    /// <summary>Whether this worker's branch may be proposed for verification (denied at the gate).</summary>
    public bool MayRequestVerification(string workerAgentId, out string reason) => MayWork(workerAgentId, out reason);

    /// <summary>
    /// <see cref="IMergeGate"/> — the backstop. A branch whose worker never had a plan approved cannot
    /// merge, whatever its tests said.
    /// </summary>
    public bool Allows(string agentId, out string reason)
    {
        // Workers the gate never held (manual-mode agents, external-PR heads) are not governed by the plan
        // gate at all — this gate answers only for the workers it was asked to hold. Answering "no" for
        // every unknown id would silently block every non-coordinated branch in the queue.
        lock (_gate)
        {
            if (!_held.ContainsKey(agentId))
            {
                reason = string.Empty;
                return true;
            }
        }

        return MayWork(agentId, out reason);
    }

    // ---- backpressure -----------------------------------------------------

    /// <summary>The workers this gate is holding that are still at the plan gate.</summary>
    public IReadOnlyList<string> BlockedWorkerIds()
    {
        var blocked = _plans.BlockedWorkerIds();
        lock (_gate)
        {
            return blocked.Where(_held.ContainsKey).ToList();
        }
    }

    /// <summary>How many workers are currently waiting on a human plan decision.</summary>
    public int BlockedWorkerCount => BlockedWorkerIds().Count;

    /// <summary>How many workers stopped and escalated after spending their revision budget.</summary>
    public int EscalatedWorkerCount
    {
        get
        {
            var escalated = _plans.EscalatedWorkerIds();
            lock (_gate)
            {
                return escalated.Count(_held.ContainsKey);
            }
        }
    }

    /// <summary>
    /// The legible stall (contract §2). When workers are waiting on a human, say so — and say it more
    /// urgently when they are the reason the coordinator has stopped spawning.
    ///
    /// <para>Returns null when there is nothing to say. Never returns a vague string: the whole point is
    /// that a stall which cannot explain itself is indistinguishable from a hang.</para>
    /// </summary>
    /// <param name="activeWorkers">The live worker population counted against the cap.</param>
    /// <param name="maxActiveWorkers">The cap itself (<see cref="CoordinatorLimits.MaxActiveWorkers"/>).</param>
    public string? BackpressureSignal(int activeWorkers, int maxActiveWorkers)
    {
        var blocked = BlockedWorkerCount;
        var escalated = EscalatedWorkerCount;
        if (blocked == 0 && escalated == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (blocked > 0)
        {
            parts.Add($"{blocked} {Workers(blocked)} waiting on your approval");
        }

        if (escalated > 0)
        {
            parts.Add($"{escalated} escalated after {_plans.MaxPlanRevisions} rejected plans");
        }

        var head = string.Join(" · ", parts);
        if (blocked > 0 && activeWorkers >= maxActiveWorkers)
        {
            return $"{head}. The worker cap ({activeWorkers}/{maxActiveWorkers}) is full — " +
                   "the coordinator has stopped spawning until you clear plans.";
        }

        return head + ".";
    }

    private static string Workers(int n) => n == 1 ? "worker is" : "workers are";
}
