using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>Why a worker's plan phase ended.</summary>
public enum WorkerPlanPhaseOutcome
{
    /// <summary>A human approved the plan — the worker is cleared and its task is released to it.</summary>
    Approved,

    /// <summary>The revision budget was spent; the worker stopped and escalated to the human.</summary>
    Escalated,

    /// <summary>The daemon refused the presentation outright (e.g. this worker already has a live plan).</summary>
    Refused,
}

/// <summary>
/// The end of a worker's plan phase, with the arithmetic a human or a test can check.
/// </summary>
/// <param name="Revisions">How many revisions the worker produced (0 = approved on the original plan).</param>
/// <param name="Presentations">How many times a plan was put in front of the human (revisions + 1).</param>
public sealed record WorkerPlanPhaseResult(
    WorkerPlanPhaseOutcome Outcome,
    string? PlanId,
    int Revisions,
    int Presentations,
    string Message)
{
    public bool IsApproved => Outcome == WorkerPlanPhaseOutcome.Approved;
}

/// <summary>
/// The worker's authoring seam: given the brief and (on a revision) the human's rejection feedback plus
/// the plan that was rejected, produce the plan fields.
///
/// <para>A real worker implements this by <b>inspecting its repository</b> — which is the entire reason
/// authorship moved here from the coordinator (contract §2). The scripted harness implements it
/// deterministically so the loop can be driven end-to-end without a model.</para>
/// </summary>
public interface IWorkerPlanDrafter
{
    /// <summary>The worker's title for this plan (may change across revisions).</summary>
    string Title { get; }

    /// <param name="feedback">Null on the first presentation; the human's rejection text on a revision.</param>
    /// <param name="previous">Null on the first presentation; the fields that were rejected otherwise.</param>
    Task<TaskPlanFields> AuthorAsync(string? feedback, TaskPlanFields? previous, CancellationToken ct);
}

/// <summary>
/// The worker's channel to the daemon-side plan gate. In a real jail this is the in-jail
/// <c>mainguard-plan</c> shim over the worker's Unix socket; in-process it is
/// <see cref="LocalWorkerPlanChannel"/>.
/// </summary>
public interface IWorkerPlanChannel
{
    /// <summary>Presents the worker's first plan.</summary>
    Task<PlanPresentationResult> PresentAsync(string title, TaskPlanFields fields, CancellationToken ct);

    /// <summary>Re-presents a plan revised against the human's feedback.</summary>
    Task<PlanRevisionResult> ReviseAsync(string planId, string title, TaskPlanFields fields, CancellationToken ct);

    /// <summary>Blocks until the human decides.</summary>
    Task<PlanDecision> AwaitDecisionAsync(string planId, CancellationToken ct);
}

/// <summary>An in-process <see cref="IWorkerPlanChannel"/> straight onto the daemon-side service.</summary>
public sealed class LocalWorkerPlanChannel : IWorkerPlanChannel
{
    private readonly PlanApprovalService _plans;
    private readonly string _workerAgentId;
    private readonly string _coordinatorId;
    private readonly string _taskPrompt;
    private readonly decimal _budgetUsd;

    public LocalWorkerPlanChannel(
        PlanApprovalService plans,
        string workerAgentId,
        string coordinatorId = "",
        string taskPrompt = "",
        decimal budgetUsd = 0m)
    {
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _workerAgentId = workerAgentId ?? throw new ArgumentNullException(nameof(workerAgentId));
        _coordinatorId = coordinatorId ?? "";
        _taskPrompt = taskPrompt ?? "";
        _budgetUsd = budgetUsd;
    }

    public Task<PlanPresentationResult> PresentAsync(string title, TaskPlanFields fields, CancellationToken ct) =>
        Task.FromResult(_plans.Present(_workerAgentId, _coordinatorId, title, fields, _taskPrompt, _budgetUsd));

    public Task<PlanRevisionResult> ReviseAsync(string planId, string title, TaskPlanFields fields, CancellationToken ct) =>
        Task.FromResult(_plans.Revise(planId, title, fields));

    public Task<PlanDecision> AwaitDecisionAsync(string planId, CancellationToken ct) =>
        _plans.AwaitDecisionAsync(planId, ct);
}

/// <summary>
/// The worker-side half of the phase-2 plan gate (coordinator contract §2): inspect the repository, author
/// a plan, present it, <b>block</b>, and — on rejection — revise against the human's feedback and
/// re-present, until approved or until the daemon says the revision budget is spent.
///
/// <para><b>The loop does not enforce its own bound.</b> It stops when the daemon tells it to, via
/// <see cref="PlanStatus.Escalated"/> coming back from <see cref="IWorkerPlanChannel.AwaitDecisionAsync"/>
/// or <see cref="IWorkerPlanChannel.ReviseAsync"/>. A cooperative worker that counted rounds itself would
/// be enforcing a limit on the honour system; <see cref="CoordinatorLimits.MaxPlanRevisions"/> lives
/// daemon-side precisely so a worker that miscounts (or lies) still stops. The local counter here exists
/// only to report what happened.</para>
/// </summary>
public sealed class WorkerPlanAuthor
{
    private readonly IWorkerPlanDrafter _drafter;
    private readonly IWorkerPlanChannel _channel;
    private readonly Action<string>? _escalate;

    /// <param name="drafter">Authors the plan from the repository (and from the feedback on a revision).</param>
    /// <param name="channel">The daemon-side plan gate.</param>
    /// <param name="escalate">Called with the escalation message when the budget is spent (surfaces to the human).</param>
    public WorkerPlanAuthor(IWorkerPlanDrafter drafter, IWorkerPlanChannel channel, Action<string>? escalate = null)
    {
        _drafter = drafter ?? throw new ArgumentNullException(nameof(drafter));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _escalate = escalate;
    }

    /// <summary>
    /// Runs the plan phase to completion. Returns only when the worker is cleared to work, has escalated,
    /// or was refused. <b>This method returning <see cref="WorkerPlanPhaseOutcome.Approved"/> is the
    /// worker's authorisation to begin.</b>
    /// </summary>
    public async Task<WorkerPlanPhaseResult> RunAsync(CancellationToken ct = default)
    {
        var fields = await _drafter.AuthorAsync(null, null, ct).ConfigureAwait(false);
        var presentation = await _channel.PresentAsync(_drafter.Title, fields, ct).ConfigureAwait(false);
        if (!presentation.IsPresented)
        {
            return new WorkerPlanPhaseResult(
                WorkerPlanPhaseOutcome.Refused, presentation.PlanId, 0, 0, presentation.Message);
        }

        var planId = presentation.PlanId!;
        var revisions = 0;
        var presentations = 1;

        // No local bound on this loop: the daemon ends it. See the class remarks.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var decision = await _channel.AwaitDecisionAsync(planId, ct).ConfigureAwait(false);

            if (decision.Approved)
            {
                return new WorkerPlanPhaseResult(
                    WorkerPlanPhaseOutcome.Approved, planId, revisions, presentations,
                    revisions == 0
                        ? "Plan approved — starting work."
                        : $"Plan approved after {revisions} revision(s) — starting work.");
            }

            if (decision.Escalated)
            {
                return Escalate(planId, revisions, presentations, decision.Feedback);
            }

            // Rejected: revise against the human's feedback and re-present.
            fields = await _drafter.AuthorAsync(decision.Feedback, fields, ct).ConfigureAwait(false);
            var revision = await _channel.ReviseAsync(planId, _drafter.Title, fields, ct).ConfigureAwait(false);
            if (revision.Outcome == PlanRevisionOutcome.Escalated)
            {
                return Escalate(planId, revisions, presentations, decision.Feedback);
            }

            if (revision.Outcome == PlanRevisionOutcome.Refused)
            {
                return new WorkerPlanPhaseResult(
                    WorkerPlanPhaseOutcome.Refused, planId, revisions, presentations, revision.Message);
            }

            revisions++;
            presentations++;
        }
    }

    private WorkerPlanPhaseResult Escalate(string planId, int revisions, int presentations, string? feedback)
    {
        var message =
            $"Stopping after {presentations} rejected plan(s): the revision budget is spent, so this needs " +
            "a human decision rather than another attempt." +
            (string.IsNullOrWhiteSpace(feedback) ? "" : $" Last feedback: {feedback}");
        _escalate?.Invoke(message);
        return new WorkerPlanPhaseResult(
            WorkerPlanPhaseOutcome.Escalated, planId, revisions, presentations, message);
    }
}

/// <summary>
/// A deterministic <see cref="IWorkerPlanDrafter"/> that plays a scripted sequence of plans: the first
/// entry is the original, each later entry is the revision produced after a rejection. Used by the
/// scripted end-to-end runs; the last entry repeats if the human keeps rejecting.
/// </summary>
public sealed class ScriptedWorkerPlanDrafter : IWorkerPlanDrafter
{
    private readonly IReadOnlyList<TaskPlanFields> _script;
    private readonly List<string?> _feedbackSeen = new();
    private int _index;

    public ScriptedWorkerPlanDrafter(string title, params TaskPlanFields[] script)
    {
        Title = title ?? "Untitled plan";
        if (script is null || script.Length == 0)
        {
            throw new ArgumentException("At least one scripted plan is required.", nameof(script));
        }

        _script = script;
    }

    public string Title { get; }

    /// <summary>Every feedback string the drafter was asked to revise against, in order.</summary>
    public IReadOnlyList<string?> FeedbackSeen => _feedbackSeen;

    public Task<TaskPlanFields> AuthorAsync(string? feedback, TaskPlanFields? previous, CancellationToken ct)
    {
        if (feedback is not null)
        {
            _feedbackSeen.Add(feedback);
        }

        var fields = _script[Math.Min(_index, _script.Count - 1)];
        _index++;
        return Task.FromResult(fields);
    }
}
