using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// In the Orchestrator namespace, the parent Mainguard.Agents.Agents (AdmissionController, …) is in scope
// automatically, and `TaskPlan` resolves to the domain type here (shadowing the UI prototype).
namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>The status of a coordinator tool call.</summary>
public enum CoordinatorToolStatus
{
    /// <summary>The tool succeeded.</summary>
    Ok,

    /// <summary>The tool was refused by a hard cap (worker cap / admission / budget) or a frozen queue.</summary>
    Rejected,

    /// <summary>A resource ceiling was hit (maps to gRPC <c>RESOURCE_EXHAUSTED</c>).</summary>
    ResourceExhausted,
}

/// <summary>A tool call's typed result, surfaced back into the coordinator's chat loop.</summary>
/// <param name="AgentId">The worker id, on a successful <c>spawn_worker</c>.</param>
public sealed record CoordinatorToolResult(
    CoordinatorToolStatus Status, string Message, string? PlanId = null, string? AgentId = null)
{
    public bool IsOk => Status == CoordinatorToolStatus.Ok;

    public static CoordinatorToolResult Ok(string message, string? planId = null, string? agentId = null) =>
        new(CoordinatorToolStatus.Ok, message, planId, agentId);

    public static CoordinatorToolResult Rejected(string message) => new(CoordinatorToolStatus.Rejected, message);

    public static CoordinatorToolResult Exhausted(string message) => new(CoordinatorToolStatus.ResourceExhausted, message);
}

/// <summary>The daemon-side worker seam the coordinator's tools reach through.</summary>
public interface IWorkerControl
{
    /// <summary>
    /// The live worker ids. <b>This includes workers blocked on plan approval</b> — they still hold a
    /// jail, tmpfs, network segment and worktree, so they are part of the population the cap governs.
    /// </summary>
    IReadOnlyList<string> ActiveWorkerIds { get; }

    /// <summary>A one-line status word for a worker (or null when unknown).</summary>
    string? WorkerStatus(string agentId);

    /// <summary>
    /// Spawns a worker for the described task. The task prompt travels to the <i>daemon</i>, which withholds
    /// it from the worker until the worker's own plan is approved (contract §2 — the coordinator does not
    /// hand work over, the daemon does).
    /// </summary>
    Task<string> SpawnWorkerAsync(string title, string taskPrompt, decimal budgetUsd, CancellationToken ct);

    /// <summary>Sends a steering prompt to a managed worker (only to workers the coordinator owns).</summary>
    Task SendPromptAsync(string agentId, string prompt, CancellationToken ct);

    /// <summary>Requests verification of a worker's branch (the P2-10 verify path).</summary>
    Task RequestVerificationAsync(string agentId, CancellationToken ct);
}

/// <summary>
/// The coordinator's tool surface (contract §3). The four tools — <c>spawn_worker</c>,
/// <c>get_worker_status</c>, <c>send_worker_prompt</c>, <c>request_verification</c> — and nothing else.
///
/// <para><b>Phase 2 inverts <c>spawn_worker</c>.</b> It used to take a coordinator-authored
/// <c>TaskPlanFields</c> and draft a plan for approval, spawning nothing. It now spawns a worker directly,
/// within the caps and with no human approval, because the plan gate moved: the worker inspects the
/// repository, authors its own plan, presents it, and blocks. The coordinator never sees plan fields at
/// all — it has no worktree, no git credentials and no view of repository contents, so anything it wrote
/// there would describe work it could not inspect.</para>
///
/// <para><b>The caps still bind, and they count blocked workers.</b> A worker awaiting plan approval
/// consumes a slot against <see cref="CoordinatorLimits.MaxActiveWorkers"/>, so a coordinator that fans
/// out faster than a human approves is refused — backpressure, by design. The refusal
/// <i>says which</i>: a stall the operator cannot distinguish from a hang is the failure mode this
/// codebase keeps paying for.</para>
///
/// <para>Note on where enforcement actually lives: the shipped coordinator is a real CLI in a jail, so its
/// spawns arrive at the daemon's <c>mainguard-agent</c> IPC handler, which re-applies these same caps
/// server-side. This class is the in-process tool surface used by the scripted coordinator loop; both
/// paths are covered, because a cap enforced on only one of them is not enforced.</para>
/// </summary>
public sealed class CoordinatorTools
{
    private readonly string _coordinatorId;
    private readonly AdmissionController _admission;
    private readonly Func<bool> _budgetExceeded;
    private readonly Func<int> _activeWorkerCount;
    private readonly IWorkerControl _workers;
    private readonly KillSwitchGate _killGate;
    private readonly CoordinatorLimits _limits;
    private readonly WorkerPlanGate? _planGate;

    public CoordinatorTools(
        string coordinatorId,
        AdmissionController admission,
        IWorkerControl workers,
        Func<int>? activeWorkerCount = null,
        Func<bool>? budgetExceeded = null,
        KillSwitchGate? killGate = null,
        CoordinatorLimits? limits = null,
        WorkerPlanGate? planGate = null)
    {
        _coordinatorId = string.IsNullOrWhiteSpace(coordinatorId)
            ? throw new ArgumentException("coordinatorId is required.", nameof(coordinatorId))
            : coordinatorId;
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _workers = workers ?? throw new ArgumentNullException(nameof(workers));
        _activeWorkerCount = activeWorkerCount ?? (() => _workers.ActiveWorkerIds.Count);
        _budgetExceeded = budgetExceeded ?? (() => false);
        _killGate = killGate ?? new KillSwitchGate();
        _limits = limits ?? new CoordinatorLimits();
        _planGate = planGate;
    }

    /// <summary>The coordinator this surface belongs to.</summary>
    public string CoordinatorId => _coordinatorId;

    /// <summary>
    /// <c>spawn_worker(title, task_prompt, budget)</c> — spawns a worker, within the caps, with no human
    /// approval (contract §6). Applies, in order: the kill switch, the active-worker cap (blocked workers
    /// included), VM memory admission, and the per-day budget.
    /// </summary>
    public async Task<CoordinatorToolResult> SpawnWorkerAsync(
        string title, string taskPrompt, decimal budgetUsd, CancellationToken ct = default)
    {
        // Frozen (kill switch) → refuse loudly (SA-1/F4 — spawn is one of the frozen paths).
        if (_killGate.IsFrozen)
        {
            return CoordinatorToolResult.Rejected("Everything is frozen (kill switch engaged) — resume before spawning.");
        }

        // Active-worker cap. Blocked workers are counted: MaxActiveWorkers is a RESOURCE cap and a worker
        // waiting on your approval still holds its jail, tmpfs, network segment and worktree. Exempting
        // them would convert a bounded system into an unbounded one at exactly the moment a human is too
        // busy to approve.
        var active = _activeWorkerCount();
        if (active >= _limits.MaxActiveWorkers)
        {
            return CoordinatorToolResult.Rejected(CapRefusal(active));
        }

        // Admission (VM memory headroom).
        if (!_admission.CanSpawn(out var admissionReason))
        {
            return CoordinatorToolResult.Rejected(admissionReason);
        }

        // Per-day budget.
        if (_budgetExceeded())
        {
            return CoordinatorToolResult.Rejected("The per-day budget is exhausted — no new workers today.");
        }

        var agentId = await _workers.SpawnWorkerAsync(
            title ?? "Untitled task", taskPrompt ?? "", budgetUsd, ct).ConfigureAwait(false);

        return CoordinatorToolResult.Ok(
            $"Worker {agentId} spawned — it will inspect the repo, author its plan, and block for approval.",
            agentId: agentId);
    }

    /// <summary><c>get_worker_status</c> — read-only. All workers, or one when <paramref name="agentId"/> is given.</summary>
    public CoordinatorToolResult GetWorkerStatus(string? agentId = null)
    {
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            var status = _workers.WorkerStatus(agentId);
            return status is null
                ? CoordinatorToolResult.Rejected($"No worker '{agentId}'.")
                : CoordinatorToolResult.Ok($"{agentId}: {DecorateStatus(agentId, status)}");
        }

        var ids = _workers.ActiveWorkerIds;
        if (ids.Count == 0)
        {
            return CoordinatorToolResult.Ok("No workers running.");
        }

        var lines = ids.Select(id => $"{id}: {DecorateStatus(id, _workers.WorkerStatus(id) ?? "Unknown")}");
        var body = string.Join("; ", lines);
        var backpressure = _planGate?.BackpressureSignal(ids.Count, _limits.MaxActiveWorkers);
        return CoordinatorToolResult.Ok(backpressure is null ? body : $"{body} — {backpressure}");
    }

    /// <summary>
    /// <c>send_worker_prompt</c> — steer a managed worker. Refused while the worker is held at the plan
    /// gate: otherwise the coordinator could simply hand over the task the daemon is withholding.
    /// </summary>
    public async Task<CoordinatorToolResult> SendWorkerPromptAsync(string agentId, string prompt, CancellationToken ct = default)
    {
        if (_killGate.IsFrozen)
        {
            return CoordinatorToolResult.Rejected("Everything is frozen (kill switch engaged) — resume first.");
        }

        if (_workers.WorkerStatus(agentId) is null)
        {
            return CoordinatorToolResult.Rejected($"No worker '{agentId}'.");
        }

        if (_planGate is not null && !_planGate.MayReceivePrompt(agentId, out var planReason))
        {
            return CoordinatorToolResult.Rejected(planReason);
        }

        await _workers.SendPromptAsync(agentId, prompt, ct).ConfigureAwait(false);
        return CoordinatorToolResult.Ok($"Prompt sent to {agentId}.");
    }

    /// <summary>
    /// <c>request_verification</c> — kick off the P2-10 verify for a worker (never a merge). Refused for a
    /// worker that never had a plan approved: there is no authorised work to verify.
    /// </summary>
    public async Task<CoordinatorToolResult> RequestVerificationAsync(string agentId, CancellationToken ct = default)
    {
        if (_workers.WorkerStatus(agentId) is null)
        {
            return CoordinatorToolResult.Rejected($"No worker '{agentId}'.");
        }

        if (_planGate is not null && !_planGate.MayRequestVerification(agentId, out var planReason))
        {
            return CoordinatorToolResult.Rejected(planReason);
        }

        await _workers.RequestVerificationAsync(agentId, ct).ConfigureAwait(false);
        return CoordinatorToolResult.Ok($"Verification requested for {agentId}.");
    }

    /// <summary>The cap refusal, naming the blocked workers when they are what filled the cap.</summary>
    private string CapRefusal(int active)
    {
        var head = $"Worker cap reached — {active}/{_limits.MaxActiveWorkers} running.";
        var blocked = _planGate?.BlockedWorkerCount ?? 0;
        if (blocked == 0)
        {
            return head + " Let one finish first.";
        }

        var noun = blocked == 1 ? "worker is" : "workers are";
        return $"{head} {blocked} {noun} waiting on human plan approval — no new workers until those plans are cleared.";
    }

    private string DecorateStatus(string agentId, string status)
    {
        if (_planGate is null || _planGate.MayWork(agentId, out var reason))
        {
            return status;
        }

        return $"{status} ({reason})";
    }
}
