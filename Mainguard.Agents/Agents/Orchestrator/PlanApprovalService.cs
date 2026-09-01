using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Audit;

namespace Mainguard.Agents.Agents.Orchestrator;

// In the Orchestrator namespace `TaskPlan` resolves to the canonical Mainguard.Agents.Agents.TaskPlan — the
// single plan type the P2-11 detector, the approval card, and the daemon all share.

/// <summary>
/// The lifecycle of a <b>worker-authored</b> plan (coordinator contract §2).
///
/// <para>Phase 2 makes two of these non-obvious, so they are spelled out:</para>
/// <list type="bullet">
/// <item><see cref="Rejected"/> is <b>not terminal</b>. It means "the human sent this back with feedback,
/// and the worker owes a revision" — the worker revises against
/// <see cref="PendingPlan.RejectionFeedback"/> and re-presents, returning the plan to
/// <see cref="Pending"/>. Rejection is feedback, not death.</item>
/// <item><see cref="Escalated"/> is terminal <i>for the automated loop</i>: the revision budget
/// (<see cref="CoordinatorLimits.MaxPlanRevisions"/>) is spent, so the worker stops rather than looping
/// and the human owns the next move.</item>
/// </list>
/// </summary>
public enum PlanStatus
{
    /// <summary>Presented to the human and blocking the worker. The only state Approve/Reject accept.</summary>
    Pending,

    /// <summary>Approved — this worker, and only this worker, is cleared to do its work.</summary>
    Approved,

    /// <summary>Sent back with feedback; the worker owes a revision. NOT terminal.</summary>
    Rejected,

    /// <summary>The revision budget is spent. The worker stopped and escalated to the human.</summary>
    Escalated,

    /// <summary>
    /// This plan was the worker's authorisation and a human has approved a <b>re-scope</b> that replaces
    /// it. Terminal, and it exists so that "the approved plan" stays exactly one object per worker.
    ///
    /// <para>The alternative was to leave both plans <see cref="Approved"/> and let
    /// <see cref="PlanApprovalService.ApprovedForWorker"/> pick the newest by timestamp. That is the shape
    /// of a defect rather than a design: the flagged-change gate compares every merge diff against "the
    /// approved scope" (phase 2 §3a), so an ordering bug there would compare a diff against a superseded
    /// authorisation and report the result as enforcement. With supersession there is nothing to order —
    /// a worker has one approved plan or none.</para>
    /// </summary>
    Superseded,
}

/// <summary>
/// What a worker has said, at commit time, about whether the work it recorded followed the
/// <see cref="TaskPlan.Approach"/> a human approved.
///
/// <para><b>Three outcomes, not two</b> — the same call <see cref="WorkerPlanGate.MergeEvidence"/> makes,
/// for the same reason. "I checked and there were none" and "nobody ever asked me" are different facts,
/// and collapsing them puts a reassurance on the review surface that no worker ever gave. The defect this
/// exists for is a worker whose approved approach said it would keep plain <c>a / b</c> and which shipped
/// a validation layer that changed three pre-existing functions: the scope was honoured, verification was
/// green because the worker wrote the tests, and nothing anywhere compared the approved approach against
/// the diff. This does not compare them either — it makes the worker state the answer, and puts an
/// unanswered question in front of the human rather than letting silence read as "nothing to see".</para>
/// </summary>
public enum DeviationDeclaration
{
    /// <summary>
    /// Nothing on record. <b>Not</b> "no deviations": the question was never answered for this branch, so
    /// the review surface flags it exactly as <c>FlaggedKind.LockfileAdvisoryUnknown</c> flags an
    /// advisory check that could not run. An omitted item is an acknowledged item.
    /// </summary>
    NotDeclared,

    /// <summary>The worker explicitly asserted that the work follows the approved approach.</summary>
    None,

    /// <summary>The worker declared one or more departures from the approved approach.</summary>
    Declared,
}

/// <summary>
/// What a human approved for one worker, and what that worker has since said about following it. One
/// object because it is one lookup: the F6 scope comparison and the approach shown at review must be
/// resolved from the <b>same</b> approved plan, and two seams into the same service is how they come to
/// name two different plans after a re-scope.
/// </summary>
/// <param name="Plan">The approved plan itself — <c>Scope</c> is F6-load-bearing, <c>Approach</c> is what
/// the reviewer compares the diff against.</param>
/// <param name="Declaration">The worker's commit-time answer about deviating from <c>Approach</c>.</param>
/// <param name="Deviations">The declared departures, verbatim; empty unless
/// <paramref name="Declaration"/> is <see cref="DeviationDeclaration.Declared"/>.</param>
public sealed record ApprovedWork(
    TaskPlan Plan,
    DeviationDeclaration Declaration,
    IReadOnlyList<string> Deviations);

/// <summary>The outcome of a worker's commit-time deviation declaration.</summary>
public enum DeviationDeclarationOutcome
{
    /// <summary>Recorded on the worker's approved plan.</summary>
    Recorded,

    /// <summary>Refused: this worker holds no approved plan, so there is no approach to deviate from.</summary>
    NoApprovedPlan,
}

/// <summary>The result of a <see cref="PlanApprovalService.DeclareDeviations"/> call.</summary>
public sealed record DeviationDeclarationResult(
    DeviationDeclarationOutcome Outcome, string Message, PendingPlan? Plan)
{
    public bool IsRecorded => Outcome == DeviationDeclarationOutcome.Recorded;
}

/// <summary>Whether a worker's plan presentation was accepted onto the queue.</summary>
public enum PlanPresentationOutcome
{
    /// <summary>Accepted — the plan is <see cref="PlanStatus.Pending"/> and the worker now blocks.</summary>
    Presented,

    /// <summary>Refused by a daemon-side invariant (one live plan per worker). Nothing was queued.</summary>
    Refused,
}

/// <summary>The outcome of a worker's attempt to re-scope an approved plan.</summary>
public enum PlanRescopeOutcome
{
    /// <summary>Accepted — a new plan is <see cref="PlanStatus.Pending"/>, and the old one still authorises.</summary>
    Presented,

    /// <summary>Refused: no such plan, it is not approved, or this worker already has a re-scope in flight.</summary>
    Refused,
}

/// <summary>The result of a <see cref="PlanApprovalService.Rescope"/> call.</summary>
public sealed record PlanRescopeResult(PlanRescopeOutcome Outcome, string Message, string? PlanId)
{
    public bool IsPresented => Outcome == PlanRescopeOutcome.Presented;
}

/// <summary>The outcome of a worker's attempt to revise a rejected plan.</summary>
public enum PlanRevisionOutcome
{
    /// <summary>The revision was accepted; the plan is <see cref="PlanStatus.Pending"/> again.</summary>
    Revised,

    /// <summary>The revision budget is spent — the plan is <see cref="PlanStatus.Escalated"/>.</summary>
    Escalated,

    /// <summary>Refused: no such plan, or the plan was not awaiting a revision.</summary>
    Refused,
}

/// <summary>
/// A plan <b>the worker authored</b> and (optionally) a human decided.
///
/// <para>Authorship moved from the coordinator to the worker in phase 2 (contract §2): the coordinator has
/// no worktree, no git credentials and no view of repository contents, so a plan it wrote described work
/// it could not inspect. <see cref="WorkerAgentId"/> is therefore the load-bearing identity here —
/// <see cref="CoordinatorId"/> is retained only for scoping and attribution.</para>
///
/// <para><see cref="ApproverIdentity"/> is <b>daemon-derived</b> from the authenticated connection's OS
/// peer credential and set only on approval — there is no client-supplied identity anywhere in this
/// record's write path (SA-1/F2).</para>
/// </summary>
/// <param name="WorkerAgentId">The worker that inspected the repo and authored this plan.</param>
/// <param name="RevisionCount">How many revisions the worker has produced (0 = the original presentation).</param>
/// <param name="RejectionFeedback">The human's most recent rejection text — what the worker revises against.</param>
/// <param name="SupersedesPlanId">
/// Set only on a <b>re-scope</b>: the id of the approved plan this one asks to replace. It is the field
/// that makes the whole op legible rather than a second presentation — the human is approving a widening
/// of something they already approved, and this is how the card knows to say so and what to diff against.
/// </param>
/// <param name="PreviousScope">
/// The scope of the plan named by <see cref="SupersedesPlanId"/>, captured at the moment the re-scope was
/// presented. <b>Copied, not looked up</b>: the card must show what the human actually consented to, and a
/// lookup would render whatever that plan says at paint time — which is a different claim as soon as a
/// second re-scope exists. Empty on an ordinary plan.
/// </param>
/// <param name="RescopeCount">
/// How many times this worker has already had a widening approved (0 on an original plan and on the first
/// re-scope). It is <b>not a budget</b> — nothing refuses on this number; see
/// <see cref="PlanApprovalService.Rescope"/> for why the count is shown rather than capped.
/// </param>
/// <param name="Deviation">
/// The worker's commit-time answer about departing from <see cref="TaskPlan.Approach"/> — see
/// <see cref="DeviationDeclaration"/>. It lives on the plan record and not in a register of its own
/// because a deviation is a deviation <i>from this plan</i>: the record it belongs to is the one that
/// authorised the work, it is resolved by the same <see cref="PlanApprovalService.ApprovedForWorker"/>
/// the F6 scope comparison uses, and it persists through a daemon restart for free.
/// </param>
/// <param name="DeclaredDeviations">
/// The departures the worker declared, verbatim and in declaration order. Empty unless
/// <see cref="Deviation"/> is <see cref="DeviationDeclaration.Declared"/>.
/// </param>
public sealed record PendingPlan(
    TaskPlan Plan,
    string CoordinatorId,
    string TaskPrompt,
    PlanStatus Status,
    string? ApproverIdentity,
    DateTimeOffset? DecidedAt,
    string WorkerAgentId = "",
    int RevisionCount = 0,
    string? RejectionFeedback = null,
    string? SupersedesPlanId = null,
    IReadOnlyList<string>? PreviousScope = null,
    int RescopeCount = 0,
    DeviationDeclaration Deviation = DeviationDeclaration.NotDeclared,
    IReadOnlyList<string>? DeclaredDeviations = null)
{
    public string PlanId => Plan.PlanId;
    public string Title => Plan.Title;
    public decimal BudgetUsd => Plan.BudgetUsd;
    public DateTimeOffset DraftedAt => Plan.DraftedAt;

    /// <summary>True when this plan asks to widen an approval the worker already holds.</summary>
    public bool IsRescope => !string.IsNullOrEmpty(SupersedesPlanId);

    /// <summary>The declared departures, never null.</summary>
    public IReadOnlyList<string> Deviations => DeclaredDeviations ?? Array.Empty<string>();

    /// <summary>
    /// True while the worker is held at the gate — presented and undecided, or owing a revision.
    ///
    /// <para><b>A pending re-scope counts.</b> It is a card in front of a human and a worker parked on a
    /// blocking call, which is exactly what this flag feeds (the backpressure sentence and the "N workers
    /// waiting on your approval" count). What it does NOT mean is that the worker is unauthorised: that
    /// question is <see cref="PlanApprovalService.HasApprovedPlan"/>, which still answers yes off the plan
    /// the re-scope is widening. The two were always different questions; the re-scope is the first case
    /// where they have different answers at the same instant.</para>
    /// </summary>
    public bool BlocksWorker => Status is PlanStatus.Pending or PlanStatus.Rejected;
}

/// <summary>The result of a <see cref="PlanApprovalService.Present"/> call.</summary>
public sealed record PlanPresentationResult(PlanPresentationOutcome Outcome, string Message, string? PlanId)
{
    public bool IsPresented => Outcome == PlanPresentationOutcome.Presented;
}

/// <summary>The result of a <see cref="PlanApprovalService.Revise"/> call.</summary>
public sealed record PlanRevisionResult(PlanRevisionOutcome Outcome, string Message, PendingPlan? Plan)
{
    public bool IsRevised => Outcome == PlanRevisionOutcome.Revised;
}

/// <summary>
/// What a blocked worker learns when the human decides. Returned by
/// <see cref="PlanApprovalService.AwaitDecisionAsync"/> — the worker-side gate is a real block on this
/// call, not an instruction to wait.
/// </summary>
/// <param name="RevisionsRemaining">Revisions still permitted after this decision (0 ⇒ the next rejection escalates).</param>
public sealed record PlanDecision(
    string PlanId,
    PlanStatus Status,
    string? Feedback,
    int RevisionCount,
    int RevisionsRemaining,
    string? ApproverIdentity)
{
    public bool Approved => Status == PlanStatus.Approved;
    public bool Escalated => Status == PlanStatus.Escalated;
}

/// <summary>The persistence seam for plans (daemon-side, restart-safe). In-memory in most tests.</summary>
public interface IPlanApprovalStore
{
    /// <summary>All persisted plans (used to resume pending plans + decided history on daemon restart).</summary>
    IReadOnlyList<PendingPlan> LoadAll();

    /// <summary>Upsert one plan (keyed by <see cref="PendingPlan.PlanId"/>).</summary>
    void Save(PendingPlan plan);
}

/// <summary>An in-memory <see cref="IPlanApprovalStore"/> for the pure/unit paths.</summary>
public sealed class InMemoryPlanApprovalStore : IPlanApprovalStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingPlan> _plans = new(StringComparer.Ordinal);

    public IReadOnlyList<PendingPlan> LoadAll()
    {
        lock (_gate)
        {
            return _plans.Values.OrderBy(p => p.DraftedAt).ToList();
        }
    }

    public void Save(PendingPlan plan)
    {
        lock (_gate)
        {
            _plans[plan.PlanId] = plan;
        }
    }
}

/// <summary>
/// A JSON-file <see cref="IPlanApprovalStore"/> (mirrors <c>LeaderRegistry</c>'s durable pattern — no EF
/// migration needed). Two service instances over the same path prove restart-safety: the second instance
/// rehydrates every presented + decided plan, approver identity, revision count and rejection feedback
/// intact — the last three matter because a daemon restart mid-revision must not silently hand a worker
/// its budget back.
/// </summary>
public sealed class JsonPlanApprovalStore : IPlanApprovalStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public JsonPlanApprovalStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public IReadOnlyList<PendingPlan> LoadAll()
    {
        lock (_gate)
        {
            return LoadDtosLocked().Select(FromDto).OrderBy(p => p.DraftedAt).ToList();
        }
    }

    public void Save(PendingPlan plan)
    {
        lock (_gate)
        {
            var dtos = LoadDtosLocked();
            dtos.RemoveAll(d => d.PlanId == plan.PlanId);
            dtos.Add(ToDto(plan));
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Write-rename so a crash mid-write never leaves a torn file.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dtos));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    private List<PlanDto> LoadDtosLocked()
    {
        if (!File.Exists(_path))
        {
            return new();
        }

        try
        {
            return JsonSerializer.Deserialize<List<PlanDto>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }

    private static PlanDto ToDto(PendingPlan p) => new()
    {
        PlanId = p.Plan.PlanId,
        CoordinatorId = p.CoordinatorId,
        WorkerAgentId = p.WorkerAgentId,
        Title = p.Plan.Title,
        Scope = p.Plan.Scope.ToList(),
        Approach = p.Plan.Approach,
        TestStrategy = p.Plan.TestStrategy,
        TaskPrompt = p.TaskPrompt,
        BudgetUsd = p.Plan.BudgetUsd,
        DraftedAt = p.Plan.DraftedAt,
        Status = p.Status.ToString(),
        ApproverIdentity = p.ApproverIdentity,
        DecidedAt = p.DecidedAt,
        RevisionCount = p.RevisionCount,
        RejectionFeedback = p.RejectionFeedback,
        SupersedesPlanId = p.SupersedesPlanId,
        PreviousScope = p.PreviousScope?.ToList(),
        RescopeCount = p.RescopeCount,
        // Persisted for the same reason SupersedesPlanId is: a daemon restart between the worker's commit
        // and its branch's verification must not turn "the worker asserted it followed the approach" back
        // into "nobody ever asked" — which is a must-acknowledge flag the human would have to clear for a
        // question that WAS answered. The three states are written by name, never as an int, so a
        // hand-read store file says what it means.
        Deviation = p.Deviation.ToString(),
        DeclaredDeviations = p.DeclaredDeviations?.ToList(),
    };

    private static PendingPlan FromDto(PlanDto d) => new(
        new TaskPlan(d.PlanId, d.Title, d.Scope ?? new List<string>(), d.Approach ?? "", d.TestStrategy ?? "", d.BudgetUsd, d.DraftedAt),
        d.CoordinatorId,
        d.TaskPrompt ?? "",
        Enum.TryParse<PlanStatus>(d.Status, out var s) ? s : PlanStatus.Pending,
        d.ApproverIdentity,
        d.DecidedAt,
        d.WorkerAgentId ?? "",
        d.RevisionCount,
        d.RejectionFeedback,
        // Persisted, because a daemon restart must not turn a re-scope back into an ordinary plan: the
        // supersession that runs on approval is what keeps "one approved plan per worker" true, and a
        // rehydrated plan that had forgotten what it supersedes would leave two.
        d.SupersedesPlanId,
        d.PreviousScope,
        d.RescopeCount,
        // An unparseable or absent value rehydrates as NotDeclared — the fail-closed direction. A store
        // file written before this field existed genuinely holds no answer, and reading one as "None"
        // would manufacture the assertion this whole mechanism exists to stop being assumed.
        Enum.TryParse<DeviationDeclaration>(d.Deviation, out var dev) ? dev : DeviationDeclaration.NotDeclared,
        d.DeclaredDeviations);

    private sealed class PlanDto
    {
        public string PlanId { get; set; } = "";
        public string CoordinatorId { get; set; } = "";
        public string? WorkerAgentId { get; set; }
        public string Title { get; set; } = "";
        public List<string>? Scope { get; set; }
        public string? Approach { get; set; }
        public string? TestStrategy { get; set; }
        public string? TaskPrompt { get; set; }
        public decimal BudgetUsd { get; set; }
        public DateTimeOffset DraftedAt { get; set; }
        public string Status { get; set; } = "Pending";
        public string? ApproverIdentity { get; set; }
        public DateTimeOffset? DecidedAt { get; set; }
        public int RevisionCount { get; set; }
        public string? RejectionFeedback { get; set; }
        public string? SupersedesPlanId { get; set; }
        public List<string>? PreviousScope { get; set; }
        public int RescopeCount { get; set; }
        public string? Deviation { get; set; }
        public List<string>? DeclaredDeviations { get; set; }
    }
}

/// <summary>
/// The worker-plan gate's queue and decision record (coordinator contract §2).
///
/// <para><b>What changed in phase 2.</b> This service used to own a queue of plans the <i>coordinator</i>
/// drafted before any worker existed (the P2-14 two-phase spawn gate). The contract deliberately inverts
/// that: the coordinator now spawns freely within the caps, and each worker inspects its repository and
/// authors its own plan, which it <see cref="Present"/>s and then <b>blocks</b> on. The coordinator has no
/// authoring entry point here at all any more — the old <c>Draft</c> method was removed rather than left
/// dormant, because a dormant authoring path is exactly the sort of thing that gets re-wired by accident.</para>
///
/// <para><b>The blocking is real.</b> <see cref="AwaitDecisionAsync"/> does not return until a human
/// decides; the worker-side loop parks on it. Enforcement that a worker cannot <i>act</i> before approval
/// is separate and lives in <see cref="WorkerPlanGate"/> — a blocking call an agent can decline to make is
/// not a boundary, so the boundary is that the daemon withholds the task and refuses the merge queue.</para>
///
/// <para><b>Rejection carries feedback and is bounded.</b> <see cref="Reject"/> records the human's text on
/// the plan and sends it back for revision; the worker <see cref="Revise"/>s against it. The budget is
/// <see cref="CoordinatorLimits.MaxPlanRevisions"/> — daemon-side, never prompted — and the rejection that
/// exhausts it moves the plan to <see cref="PlanStatus.Escalated"/> so the worker cannot even attempt
/// another round.</para>
///
/// <para><b>An approval can be widened, and only by asking.</b> <see cref="Rescope"/> is the op for a
/// worker that discovers mid-task that the job needs a file its approved scope does not cover. It presents
/// a new plan against the approved one and a human decides it like any other; the approved plan keeps
/// authorising the worker until that decision lands, and is retired to
/// <see cref="PlanStatus.Superseded"/> the moment the wider one is approved. Before it existed the store
/// answered such a worker <i>"already approved for this worker"</i> and offered nothing further, so its
/// only remaining moves were to exceed its scope silently or to stop.</para>
/// </summary>
public sealed class PlanApprovalService
{
    private readonly IPlanApprovalStore _store;
    private readonly IAuditLog _audit;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CoordinatorLimits _limits;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingPlan> _plans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TaskCompletionSource<PlanDecision>>> _waiters = new(StringComparer.Ordinal);

    public PlanApprovalService(
        IPlanApprovalStore? store = null,
        IAuditLog? audit = null,
        Func<DateTimeOffset>? clock = null,
        CoordinatorLimits? limits = null)
    {
        _store = store ?? new InMemoryPlanApprovalStore();
        _audit = audit ?? new InMemoryAuditLog();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _limits = limits ?? new CoordinatorLimits();

        // Restart resume: rehydrate every persisted plan (pending + decided), revision budgets included.
        foreach (var plan in _store.LoadAll())
        {
            _plans[plan.PlanId] = plan;
        }
    }

    /// <summary>The revision budget this service enforces (surfaced so the UI can render "revision 2 of 3").</summary>
    public int MaxPlanRevisions => _limits.MaxPlanRevisions;

    /// <summary>Raised (off any lock) after every present/revise/approve/reject so the gRPC stream / UI can re-read.</summary>
    public event Action? Changed;

    /// <summary>Raised when a plan is approved — the worker-release path subscribes (the task prompt is handed over there).</summary>
    public event Action<PendingPlan>? PlanApproved;

    /// <summary>Raised when a plan is sent back with feedback — the worker's revise loop wakes on it.</summary>
    public event Action<PendingPlan>? PlanRejected;

    /// <summary>Raised when the revision budget is spent and the worker escalates to the human.</summary>
    public event Action<PendingPlan>? PlanEscalated;

    // ---- Worker-side authorship ------------------------------------------

    /// <summary>
    /// A worker presents the plan it authored after inspecting its repository. The plan becomes
    /// <see cref="PlanStatus.Pending"/> and the worker is expected to block on
    /// <see cref="AwaitDecisionAsync"/>.
    ///
    /// <para><b>One live plan per worker</b> is the daemon-side invariant that replaces the old S-8
    /// per-coordinator drafting caps. Those caps existed to stop a coordinator queueing more approvals
    /// than a human would ever read; the coordinator no longer authors plans, so keeping a
    /// <c>MaxPendingPerCoordinator</c> smaller than <see cref="CoordinatorLimits.MaxActiveWorkers"/> would
    /// have <i>deadlocked</i> the very workers the worker cap admitted — the 6th worker could never present
    /// a plan and so could never finish. Presentation volume is now bounded by the worker cap itself, which
    /// is the real resource cap, and per-worker spam is bounded by this invariant plus the revision budget.</para>
    /// </summary>
    public PlanPresentationResult Present(
        string workerAgentId,
        string coordinatorId,
        string title,
        TaskPlanFields fields,
        string taskPrompt,
        decimal budgetUsd)
    {
        if (string.IsNullOrWhiteSpace(workerAgentId))
        {
            throw new ArgumentException("workerAgentId is required.", nameof(workerAgentId));
        }

        ArgumentNullException.ThrowIfNull(fields);

        PendingPlan presented;
        lock (_gate)
        {
            var live = LiveForWorkerLocked(workerAgentId);
            if (live is not null)
            {
                AuditLocked("plan_presentation_refused", new Dictionary<string, string>
                {
                    ["worker_agent_id"] = workerAgentId,
                    ["cause"] = "one-live-plan-per-worker",
                    ["existing_plan_id"] = live.PlanId,
                    ["existing_status"] = live.Status.ToString(),
                });
                return new PlanPresentationResult(
                    PlanPresentationOutcome.Refused,
                    live.Status switch
                    {
                        PlanStatus.Rejected =>
                            $"Plan '{live.PlanId}' was sent back for revision — revise that one instead of "
                            + "presenting a new plan.",

                        // The dead end this branch used to be. "already approved" is true and was the whole
                        // answer: a worker that had discovered it must touch a neighbouring file was told
                        // its plan was fine and given no way to change what that plan authorises, so its
                        // only moves were to exceed the scope silently or to stop. It now names the op.
                        PlanStatus.Approved =>
                            $"Plan '{live.PlanId}' is already approved for this worker. To change what it "
                            + "authorises — a file the approved scope does not cover — re-scope it: "
                            + "mainguard-plan " + Mainguard.Agents.Agents.Ipc.WorkerPlanShim.RescopeUsage,

                        _ => $"Plan '{live.PlanId}' is already {live.Status.ToString().ToLowerInvariant()} for this worker.",
                    },
                    live.PlanId);
            }

            var plan = new TaskPlan(
                PlanId: Guid.NewGuid().ToString("N"),
                Title: string.IsNullOrWhiteSpace(title) ? "Untitled plan" : title,
                Scope: fields.Scope,
                Approach: fields.Approach,
                TestStrategy: fields.TestStrategy,
                BudgetUsd: budgetUsd,
                DraftedAt: _clock());

            presented = new PendingPlan(
                plan, coordinatorId ?? "", taskPrompt ?? "", PlanStatus.Pending, null, null,
                WorkerAgentId: workerAgentId);
            _plans[presented.PlanId] = presented;
            _store.Save(presented);

            AuditLocked("plan_presented", new Dictionary<string, string>
            {
                ["plan_id"] = presented.PlanId,
                ["worker_agent_id"] = workerAgentId,
                ["coordinator_id"] = presented.CoordinatorId,
                ["scope_count"] = plan.Scope.Count.ToString(),
            });
        }

        Changed?.Invoke();
        return new PlanPresentationResult(
            PlanPresentationOutcome.Presented, "Plan presented — the worker is blocked awaiting approval.", presented.PlanId);
    }

    /// <summary>
    /// The worker re-presents a plan it revised against the human's rejection feedback.
    ///
    /// <para>The budget check is here as well as in <see cref="Reject"/> — belt and braces. A plan that has
    /// already spent its budget is <see cref="PlanStatus.Escalated"/> and this call refuses; a plan that
    /// would exceed it on this revision escalates rather than re-presenting. Neither path trusts the worker
    /// to have counted correctly, which is the whole point of the limit living daemon-side.</para>
    /// </summary>
    public PlanRevisionResult Revise(string planId, string title, TaskPlanFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        PendingPlan revised;
        var escalatedNow = false;
        lock (_gate)
        {
            if (!_plans.TryGetValue(planId, out var plan))
            {
                return new PlanRevisionResult(PlanRevisionOutcome.Refused, $"No plan '{planId}'.", null);
            }

            if (plan.Status != PlanStatus.Rejected)
            {
                // The two verbs are refused in complementary states, and each refusal names the other.
                // That mutual exclusion is what makes `revise` and `rescope` safe to hand a model: a
                // mis-picked verb is always refused rather than plausibly accepted, and the refusal is
                // the correction.
                return new PlanRevisionResult(
                    PlanRevisionOutcome.Refused,
                    plan.Status == PlanStatus.Approved
                        ? $"Plan '{planId}' is Approved — only a rejected plan can be revised. To widen "
                          + "what an approved plan authorises, re-scope it: mainguard-plan "
                          + Mainguard.Agents.Agents.Ipc.WorkerPlanShim.RescopeUsage
                        : $"Plan '{planId}' is {plan.Status} — only a rejected plan can be revised.",
                    plan);
            }

            var nextRevision = plan.RevisionCount + 1;
            if (nextRevision > _limits.MaxPlanRevisions)
            {
                // Unreachable through Reject (which escalates first), so reaching it means a worker tried
                // to revise past the budget by some other route. Escalate here too rather than trusting it.
                revised = plan with { Status = PlanStatus.Escalated, DecidedAt = _clock() };
                _plans[planId] = revised;
                _store.Save(revised);
                escalatedNow = true;
                AuditLocked("plan_escalated", new Dictionary<string, string>
                {
                    ["plan_id"] = planId,
                    ["worker_agent_id"] = revised.WorkerAgentId,
                    ["revisions"] = plan.RevisionCount.ToString(),
                    ["max_revisions"] = _limits.MaxPlanRevisions.ToString(),
                    ["cause"] = "revise-past-budget",
                });
            }
            else
            {
                revised = plan with
                {
                    Plan = plan.Plan with
                    {
                        Title = string.IsNullOrWhiteSpace(title) ? plan.Plan.Title : title,
                        Scope = fields.Scope,
                        Approach = fields.Approach,
                        TestStrategy = fields.TestStrategy,
                    },
                    Status = PlanStatus.Pending,
                    RevisionCount = nextRevision,
                    DecidedAt = null,
                };
                _plans[planId] = revised;
                _store.Save(revised);
                AuditLocked("plan_revised", new Dictionary<string, string>
                {
                    ["plan_id"] = planId,
                    ["worker_agent_id"] = revised.WorkerAgentId,
                    ["revision"] = nextRevision.ToString(),
                    ["max_revisions"] = _limits.MaxPlanRevisions.ToString(),
                });
            }
        }

        if (escalatedNow)
        {
            CompleteWaiters(revised);
            Changed?.Invoke();
            PlanEscalated?.Invoke(revised);
            return new PlanRevisionResult(
                PlanRevisionOutcome.Escalated,
                $"The revision budget of {_limits.MaxPlanRevisions} is spent — stop and escalate to the human.",
                revised);
        }

        Changed?.Invoke();
        return new PlanRevisionResult(
            PlanRevisionOutcome.Revised,
            $"Revision {revised.RevisionCount} of {_limits.MaxPlanRevisions} presented — awaiting approval.",
            revised);
    }

    /// <summary>
    /// A worker asks to <b>widen an approved plan</b>: it started the work, found that doing the job
    /// properly needs a file the approved scope does not cover, and presents a revised plan against the
    /// approval it already holds. A human decides it exactly as they decide any other plan.
    ///
    /// <para><b>Why this is not <see cref="Revise"/>.</b> A revision answers a rejection and spends the
    /// revision budget; this follows an approval and spends none. Charging it would be wrong in the
    /// direction that matters: a worker whose plan was rejected three times and then approved would have
    /// nothing left, so the workers that had the hardest time agreeing a plan would be exactly the ones
    /// with no legal way to widen it — which re-creates the defect this op exists to remove, for the
    /// population most likely to hit it. The budget bounds "your plans keep being wrong"; a re-scope is
    /// "the job is bigger than it looked", and those are different failures with different remedies.</para>
    ///
    /// <para><b>The re-scope carries its own, fresh revision budget</b>, because it is a new plan record
    /// and it can itself be rejected. That would be a hole if it could be re-entered: reject ×4 escalates,
    /// and a worker allowed to re-scope again would get another three rounds, forever, without a human
    /// ever having said yes. So escalation is terminal for this path too — a worker whose re-scope
    /// escalated is refused another, and keeps the approval it already had.</para>
    ///
    /// <para><b>Why the number of re-scopes is not capped.</b> Every one of them costs a human approval,
    /// which is the actual scarce resource and the actual gate; a numeric cap would put a worker back at
    /// "no legal path" at an arbitrary boundary, which is the defect wearing a limit. What runaway
    /// widening needs is to be VISIBLE to the person paying for it, so <see cref="PendingPlan.RescopeCount"/>
    /// travels to the card instead. The two loops that could run without a human — repeated presentation,
    /// and reject→re-scope→reject — are closed above by "one live re-scope at a time" and by escalation
    /// being terminal.</para>
    ///
    /// <para><b>What it does NOT do: inspect the worktree.</b> A worker may ask to re-scope after it has
    /// already touched the extra file, and that is neither refused nor separately flagged here. The
    /// flagged-change gate already puts every file outside the approved scope in front of a human at
    /// verification and blocks the merge until they acknowledge it (phase 2 §3a, F6) — so the out-of-scope
    /// work is caught by exactly one mechanism, whatever the answer here. Refusing a late re-scope would
    /// re-open the dead end (a worker that already slipped could never get legal again), and adding a
    /// second check here would be two controls answering one question, which is how one of them goes
    /// decorative (MG-12).</para>
    /// </summary>
    /// <param name="planId">The <b>approved</b> plan being widened. Required — never inferred.</param>
    public PlanRescopeResult Rescope(string planId, string title, TaskPlanFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        PendingPlan presented;
        lock (_gate)
        {
            if (!_plans.TryGetValue(planId, out var approved))
            {
                // Exactly what it says: this service has no such plan. It is deliberately NOT the answer a
                // worker spawned with plan mode OFF gets — that worker has no plans at all, so this
                // sentence would be true of the lookup and wrong about the world, implying a bad id when
                // the truth is that no plan was ever required of it. This service cannot tell the two
                // apart (an unresolvable plan id names no worker, and the mode lives on the worker), so
                // `AgentSpawnService.RescopePlanAsync` answers the ungated case up front with
                // `WorkerPlanGate.RefusePlanPresentation` and only a genuine miss reaches here.
                return new PlanRescopeResult(PlanRescopeOutcome.Refused, $"No plan '{planId}'.", null);
            }

            if (approved.Status != PlanStatus.Approved)
            {
                return new PlanRescopeResult(
                    PlanRescopeOutcome.Refused,
                    approved.Status switch
                    {
                        PlanStatus.Pending =>
                            $"Plan '{planId}' is still awaiting the human's decision — wait for it "
                            + $"(mainguard-plan await {planId}) rather than re-scoping it.",
                        PlanStatus.Rejected =>
                            $"Plan '{planId}' was sent back for revision — revise it "
                            + $"(mainguard-plan revise {planId} <plan.json>) rather than re-scoping it.",
                        _ =>
                            $"Plan '{planId}' is {approved.Status} — only an approved plan can be re-scoped.",
                    },
                    planId);
            }

            // Escalation is terminal for this path, which is what stops reject→re-scope→reject being an
            // unbounded loop with a fresh budget each time round.
            var escalatedRescope = _plans.Values.FirstOrDefault(p =>
                p.IsRescope &&
                p.Status == PlanStatus.Escalated &&
                string.Equals(p.WorkerAgentId, approved.WorkerAgentId, StringComparison.Ordinal));
            if (escalatedRescope is not null)
            {
                AuditLocked("plan_rescope_refused", new Dictionary<string, string>
                {
                    ["worker_agent_id"] = approved.WorkerAgentId,
                    ["cause"] = "rescope-already-escalated",
                    ["escalated_plan_id"] = escalatedRescope.PlanId,
                });
                return new PlanRescopeResult(
                    PlanRescopeOutcome.Refused,
                    $"A re-scope for this worker already escalated after {_limits.MaxPlanRevisions} "
                    + $"rejections (plan '{escalatedRescope.PlanId}'). You are still approved for plan "
                    + $"'{planId}' — finish what it covers, or report to the human.",
                    planId);
            }

            // One live re-scope at a time, for the same reason there is one live plan: a worker cannot put
            // more cards in front of a human than the human agreed to look at.
            var inFlight = _plans.Values.FirstOrDefault(p =>
                p.IsRescope &&
                p.BlocksWorker &&
                string.Equals(p.WorkerAgentId, approved.WorkerAgentId, StringComparison.Ordinal));
            if (inFlight is not null)
            {
                AuditLocked("plan_rescope_refused", new Dictionary<string, string>
                {
                    ["worker_agent_id"] = approved.WorkerAgentId,
                    ["cause"] = "one-live-rescope-per-worker",
                    ["existing_plan_id"] = inFlight.PlanId,
                    ["existing_status"] = inFlight.Status.ToString(),
                });
                return new PlanRescopeResult(
                    PlanRescopeOutcome.Refused,
                    inFlight.Status == PlanStatus.Rejected
                        ? $"Re-scope '{inFlight.PlanId}' was sent back for revision — revise that one "
                          + $"(mainguard-plan revise {inFlight.PlanId} <plan.json>)."
                        : $"Re-scope '{inFlight.PlanId}' is already in front of the human for this worker.",
                    inFlight.PlanId);
            }

            var plan = new TaskPlan(
                PlanId: Guid.NewGuid().ToString("N"),
                Title: string.IsNullOrWhiteSpace(title) ? approved.Plan.Title : title,
                Scope: fields.Scope,
                Approach: fields.Approach,
                TestStrategy: fields.TestStrategy,
                BudgetUsd: approved.Plan.BudgetUsd,
                DraftedAt: _clock());

            presented = new PendingPlan(
                plan,
                approved.CoordinatorId,
                approved.TaskPrompt,
                PlanStatus.Pending,
                ApproverIdentity: null,
                DecidedAt: null,
                WorkerAgentId: approved.WorkerAgentId,
                RevisionCount: 0,
                RejectionFeedback: null,
                SupersedesPlanId: approved.PlanId,
                PreviousScope: approved.Plan.Scope.ToList(),
                RescopeCount: approved.RescopeCount + 1);

            _plans[presented.PlanId] = presented;
            _store.Save(presented);

            AuditLocked("plan_rescope_presented", new Dictionary<string, string>
            {
                ["plan_id"] = presented.PlanId,
                ["supersedes_plan_id"] = approved.PlanId,
                ["worker_agent_id"] = presented.WorkerAgentId,
                ["coordinator_id"] = presented.CoordinatorId,
                ["scope_count"] = plan.Scope.Count.ToString(),
                ["previous_scope_count"] = approved.Plan.Scope.Count.ToString(),
                ["rescope_count"] = presented.RescopeCount.ToString(),
            });
        }

        Changed?.Invoke();
        return new PlanRescopeResult(
            PlanRescopeOutcome.Presented,
            "Re-scope presented — your existing approval still stands while the human decides.",
            presented.PlanId);
    }

    /// <summary>
    /// The worker's commit-time answer about departing from the <see cref="TaskPlan.Approach"/> a human
    /// approved. Recorded on the approving plan itself, audited, and read back at review.
    ///
    /// <para><b>Why the answer is required rather than offered.</b> The failure this closes is a worker
    /// that shipped the opposite of what its approved approach said it would do, with the file scope
    /// honoured, verification green (it wrote the tests), and nothing in the system holding the approach
    /// against the diff. A declaration nobody has to make is a field that is empty on exactly the runs
    /// that needed it, so the daemon refuses the commit without one — and refuses it in a way that costs
    /// the worker one re-run and nothing else, because the worktree is untouched by a refusal.</para>
    ///
    /// <para><b>A declaration cannot be walked back.</b> A worker commits several times; if any of those
    /// commits declared a departure, the branch has departed. So <see cref="DeviationDeclaration.Declared"/>
    /// is sticky and later texts accumulate (ordinal-distinct, order preserved), and a subsequent "none"
    /// records nothing new rather than clearing what was already said. The opposite rule would let the
    /// last commit erase the disclosure of the first, which is the rubber stamp this mechanism must not
    /// become.</para>
    ///
    /// <para>It is deliberately <b>not</b> a check of anything: nothing here reads the diff, and nothing
    /// compares the declaration to it. This records what the worker claims so a human can hold it against
    /// the approach, which is on their screen for the first time as of this change.</para>
    /// </summary>
    /// <param name="workerAgentId">The committing worker.</param>
    /// <param name="deviations">The declared departures; empty/null means the worker asserted there are none.</param>
    public DeviationDeclarationResult DeclareDeviations(
        string workerAgentId, IReadOnlyList<string>? deviations)
    {
        var declared = (deviations ?? Array.Empty<string>())
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Bound(d.Trim()))
            .ToList();

        PendingPlan updated;
        lock (_gate)
        {
            if (ApprovedForWorkerLocked(workerAgentId) is not { } approved)
            {
                return new DeviationDeclarationResult(
                    DeviationDeclarationOutcome.NoApprovedPlan,
                    "this worker holds no approved plan, so there is no approved approach to deviate from.",
                    null);
            }

            // The overflow notice is not a deviation, so it never counts toward the cap and never
            // survives into the next round's arithmetic — it is recomputed from what actually dropped.
            var merged = approved.Deviations
                .Where(d => !d.StartsWith(OverflowPrefix, StringComparison.Ordinal))
                .ToList();
            var dropped = 0;
            foreach (var d in declared)
            {
                if (merged.Contains(d, StringComparer.Ordinal))
                {
                    continue;
                }

                if (merged.Count >= MaxDeclaredDeviations)
                {
                    dropped++;
                    continue;
                }

                merged.Add(d);
            }

            // Loud, not quiet, and never a dead end. `commit_work` may be called any number of times and
            // records on a clean tree too, so an unbounded accumulation is an agent-controlled growth
            // path through a file the daemon rewrites on every save — the one agent-authored field with
            // no oversized guard, while `TaskPlanSchema` bounds every sibling (MaxScopeFiles,
            // MaxFieldLength, MaxPlanBytes). Refusing the commit at the cap would be worse than the
            // growth: a worker that hit it could never commit again, which is the "limit that is really
            // a dead end" shape `Rescope` argues against at length. So the excess is DECLARED as
            // unrecorded, in a row the human reads, rather than dropped in silence.
            if (dropped > 0)
            {
                merged.Add(OverflowNotice(dropped));
            }

            // Sticky: once a departure is on record the branch has departed, whatever a later commit says.
            var declaration = merged.Count > 0
                ? DeviationDeclaration.Declared
                : DeviationDeclaration.None;

            updated = approved with { Deviation = declaration, DeclaredDeviations = merged };
            _plans[updated.PlanId] = updated;
            _store.Save(updated);

            AuditLocked("worker_deviation_declared", new Dictionary<string, string>
            {
                ["plan_id"] = updated.PlanId,
                ["worker_agent_id"] = workerAgentId,
                ["declaration"] = declaration.ToString(),
                ["declared_now"] = declared.Count.ToString(),
                ["declared_total"] = merged.Count.ToString(),
                // The texts themselves, joined, because the audit chain is where "what did the worker say
                // it had done differently" has to be answerable after the jail is gone.
                ["deviations"] = string.Join(" ⏎ ", declared),
            });
        }

        Changed?.Invoke();
        return new DeviationDeclarationResult(
            DeviationDeclarationOutcome.Recorded,
            updated.Deviation == DeviationDeclaration.Declared
                ? $"{updated.Deviations.Count} declared deviation(s) recorded — the human sees them beside your approved approach."
                : "recorded: no deviation from the approved approach.",
            updated);
    }

    /// <summary>
    /// The most declared deviations one plan's record will hold. Twenty departures from one approved
    /// approach describes a plan that was wrong rather than a declaration that needs more room; the cap
    /// exists to bound the file, and what it drops is stated rather than silently lost.
    /// </summary>
    public const int MaxDeclaredDeviations = 20;

    private const string OverflowPrefix = "…and ";

    private static string OverflowNotice(int dropped) =>
        $"{OverflowPrefix}{dropped} further declared deviation(s) that this record cannot hold "
        + $"(the cap is {MaxDeclaredDeviations}) — this branch has departed from its approved approach "
        + "more times than can be listed here.";

    /// <summary>
    /// One deviation, bounded to the same length <see cref="TaskPlanSchema.MaxFieldLength"/> allows the
    /// <c>approach</c> it is about. Marked when it truncates: a cut the reader cannot see is the one way
    /// truncation is worse than refusing, and refusing here would block a commit over prose.
    /// </summary>
    private static string Bound(string text) =>
        text.Length <= TaskPlanSchema.MaxFieldLength
            ? text
            : text[..TaskPlanSchema.MaxFieldLength] + " …[truncated]";

    /// <summary>
    /// Blocks until the human decides <paramref name="planId"/>, then returns that decision. An
    /// already-decided plan returns immediately (so a worker that reconnects after a daemon restart is not
    /// stranded waiting for an event that already fired).
    /// </summary>
    public Task<PlanDecision> AwaitDecisionAsync(string planId, CancellationToken ct = default)
    {
        TaskCompletionSource<PlanDecision> tcs;
        lock (_gate)
        {
            if (!_plans.TryGetValue(planId, out var plan))
            {
                return Task.FromException<PlanDecision>(new InvalidOperationException($"No plan '{planId}'."));
            }

            if (plan.Status != PlanStatus.Pending)
            {
                return Task.FromResult(DecisionOf(plan));
            }

            tcs = new TaskCompletionSource<PlanDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_waiters.TryGetValue(planId, out var list))
            {
                list = new List<TaskCompletionSource<PlanDecision>>();
                _waiters[planId] = list;
            }

            list.Add(tcs);
        }

        if (!ct.CanBeCanceled)
        {
            return tcs.Task;
        }

        return AwaitWithCancellationAsync(planId, tcs, ct);
    }

    private async Task<PlanDecision> AwaitWithCancellationAsync(
        string planId, TaskCompletionSource<PlanDecision> tcs, CancellationToken ct)
    {
        using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (_waiters.TryGetValue(planId, out var list))
                {
                    list.Remove(tcs);
                    if (list.Count == 0)
                    {
                        _waiters.Remove(planId);
                    }
                }
            }
        }
    }

    // ---- Human decisions --------------------------------------------------

    /// <summary>
    /// Approves a pending plan. The <paramref name="approverIdentity"/> is resolved by the caller <b>from
    /// the daemon side</b> (the authenticated connection's OS peer credential) — this method takes no
    /// client-supplied identity and the proto exposes no such field (SA-1/F2). Records
    /// <c>(plan, approver, timestamp)</c>, persists, unblocks the worker, and raises
    /// <see cref="PlanApproved"/> so the task prompt is released to it.
    /// </summary>
    public PendingPlan Approve(string planId, string approverIdentity)
    {
        if (string.IsNullOrWhiteSpace(approverIdentity))
        {
            // Never record an empty approver — that would be an unattributable approval.
            throw new ArgumentException("approverIdentity is required (daemon-derived).", nameof(approverIdentity));
        }

        PendingPlan approved;
        PendingPlan? superseded = null;
        lock (_gate)
        {
            var plan = GetPendingLocked(planId);
            approved = plan with
            {
                Status = PlanStatus.Approved,
                ApproverIdentity = approverIdentity,
                DecidedAt = _clock(),
            };
            _plans[planId] = approved;
            _store.Save(approved);

            // Approving a re-scope RETIRES the plan it widens, in the same lock that approves it.
            //
            // This is what keeps "a worker has one approved plan" an invariant rather than a habit. The
            // flagged-change gate resolves that plan and compares every merge diff against its scope
            // (phase 2 §3a), so two approved plans for one worker would not be an untidy record — it
            // would be a gate measuring against whichever one a lookup happened to return. Under the
            // lock, because a release racing this must see one or the other and never both.
            if (approved.IsRescope &&
                _plans.TryGetValue(approved.SupersedesPlanId!, out var previous) &&
                previous.Status == PlanStatus.Approved)
            {
                superseded = previous with { Status = PlanStatus.Superseded, DecidedAt = _clock() };
                _plans[superseded.PlanId] = superseded;
                _store.Save(superseded);
            }
        }

        _audit.Append(new AuditEvent("plan_approved", new Dictionary<string, string>
        {
            ["plan_id"] = approved.PlanId,
            ["worker_agent_id"] = approved.WorkerAgentId,
            ["coordinator_id"] = approved.CoordinatorId,
            ["approver"] = approverIdentity,
            ["revision"] = approved.RevisionCount.ToString(),
            ["scope_count"] = approved.Plan.Scope.Count.ToString(),
            ["supersedes_plan_id"] = approved.SupersedesPlanId ?? string.Empty,
            ["rescope_count"] = approved.RescopeCount.ToString(),
        }));

        if (superseded is not null)
        {
            _audit.Append(new AuditEvent("plan_superseded", new Dictionary<string, string>
            {
                ["plan_id"] = superseded.PlanId,
                ["superseded_by"] = approved.PlanId,
                ["worker_agent_id"] = superseded.WorkerAgentId,
                ["approver"] = approverIdentity,
                ["previous_scope_count"] = superseded.Plan.Scope.Count.ToString(),
                ["scope_count"] = approved.Plan.Scope.Count.ToString(),
            }));
        }

        CompleteWaiters(approved);
        Changed?.Invoke();
        PlanApproved?.Invoke(approved);
        return approved;
    }

    /// <summary>
    /// Sends a pending plan back to its worker with the human's feedback.
    ///
    /// <para>This is the phase-2 behaviour change: rejection no longer kills the attempt. The plan goes to
    /// <see cref="PlanStatus.Rejected"/>, <paramref name="feedback"/> is recorded on it, and the worker
    /// revises against that text and re-presents. The rejection that would push the worker past
    /// <see cref="CoordinatorLimits.MaxPlanRevisions"/> instead moves the plan to
    /// <see cref="PlanStatus.Escalated"/> — the worker stops and the human owns it.</para>
    /// </summary>
    public PendingPlan Reject(string planId, string feedback)
    {
        PendingPlan decided;
        var escalated = false;
        lock (_gate)
        {
            var plan = GetPendingLocked(planId);
            var nextRevision = plan.RevisionCount + 1;
            escalated = nextRevision > _limits.MaxPlanRevisions;

            decided = plan with
            {
                Status = escalated ? PlanStatus.Escalated : PlanStatus.Rejected,
                RejectionFeedback = feedback ?? "",
                DecidedAt = _clock(),
            };
            _plans[planId] = decided;
            _store.Save(decided);
        }

        _audit.Append(new AuditEvent(escalated ? "plan_escalated" : "plan_rejected", new Dictionary<string, string>
        {
            ["plan_id"] = decided.PlanId,
            ["worker_agent_id"] = decided.WorkerAgentId,
            ["coordinator_id"] = decided.CoordinatorId,
            ["reason"] = feedback ?? "",
            ["revisions_used"] = decided.RevisionCount.ToString(),
            ["max_revisions"] = _limits.MaxPlanRevisions.ToString(),
        }));

        CompleteWaiters(decided);
        Changed?.Invoke();
        if (escalated)
        {
            PlanEscalated?.Invoke(decided);
        }
        else
        {
            PlanRejected?.Invoke(decided);
        }

        return decided;
    }

    // ---- Reads ------------------------------------------------------------

    /// <summary>Every plan (presented + decided), presentation order.</summary>
    public IReadOnlyList<PendingPlan> All()
    {
        lock (_gate)
        {
            return _plans.Values.OrderBy(p => p.DraftedAt).ToList();
        }
    }

    /// <summary>The plans still awaiting a decision, optionally scoped to one coordinator.</summary>
    public IReadOnlyList<PendingPlan> Pending(string? coordinatorId = null)
    {
        lock (_gate)
        {
            return _plans.Values
                .Where(p => p.Status == PlanStatus.Pending)
                .Where(p => coordinatorId is null || p.CoordinatorId == coordinatorId)
                .OrderBy(p => p.DraftedAt)
                .ToList();
        }
    }

    /// <summary>The concurrent pending count for one coordinator (the pressure signal source).</summary>
    public int PendingCount(string coordinatorId)
    {
        lock (_gate)
        {
            return _plans.Values.Count(p => p.Status == PlanStatus.Pending && p.CoordinatorId == coordinatorId);
        }
    }

    /// <summary>The look-up for one plan (null when unknown).</summary>
    public PendingPlan? Get(string planId)
    {
        lock (_gate)
        {
            return _plans.TryGetValue(planId, out var p) ? p : null;
        }
    }

    /// <summary>The worker's live plan (pending or awaiting revision), or null.</summary>
    public PendingPlan? LiveForWorker(string workerAgentId)
    {
        lock (_gate)
        {
            return LiveForWorkerLocked(workerAgentId);
        }
    }

    /// <summary>The worker's most recent plan in any state, or null.</summary>
    public PendingPlan? LatestForWorker(string workerAgentId)
    {
        lock (_gate)
        {
            return _plans.Values
                .Where(p => string.Equals(p.WorkerAgentId, workerAgentId, StringComparison.Ordinal))
                .OrderByDescending(p => p.DraftedAt)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// True iff this worker holds an approved plan — the one fact that clears it to work.
    ///
    /// <para>Unchanged by the re-scope op, and that is the decision rather than an oversight: a worker
    /// with a re-scope pending still holds the approval it is asking to widen, so it stays authorised for
    /// exactly the scope that was approved. Suspending it would punish the legal move — the worker that
    /// stays quiet keeps working and the one that asks is stopped — and would refuse
    /// <c>commit_work</c> to a worker mid-task, which is how F1's "stopping a worker must not destroy its
    /// commits" is undone by a human taking an hour to read a card.</para>
    /// </summary>
    public bool HasApprovedPlan(string workerAgentId)
    {
        lock (_gate)
        {
            return ApprovedForWorkerLocked(workerAgentId) is not null;
        }
    }

    /// <summary>
    /// <b>The</b> plan that currently authorises this worker — approved, and not since superseded — or
    /// null. This is the single authority for "what is this worker cleared to do", and the composition
    /// root's <c>resolveApprovedPlan</c> is a call to it.
    ///
    /// <para><b>Why it is not <see cref="LatestForWorker"/> filtered on Approved.</b> That was the wiring
    /// before the re-scope op, and it was correct only while a worker's newest plan was always its
    /// authorisation. A pending re-scope is newer than the plan it widens, so the filtered-latest read
    /// answers <c>null</c> — and null means "unmanaged" to the flagged-change gate, which skips the
    /// out-of-scope comparison entirely. The worker would have lost its F6 coverage by the act of asking
    /// to widen legally, silently, for as long as the human took to decide. Phase 2 §3a's requirement is
    /// that F6 measures against the CURRENT authorisation; this method is what that sentence means.</para>
    /// </summary>
    public PendingPlan? ApprovedForWorker(string workerAgentId)
    {
        lock (_gate)
        {
            return ApprovedForWorkerLocked(workerAgentId);
        }
    }

    /// <summary>
    /// <see cref="ApprovedForWorker"/> as the flagged-change gate wants it — the approved
    /// <see cref="TaskPlan"/> itself, or null. Exists so the composition root holds no policy of its own.
    /// </summary>
    public TaskPlan? ApprovedPlanFor(string workerAgentId) => ApprovedForWorker(workerAgentId)?.Plan;

    /// <summary>
    /// <see cref="ApprovedForWorker"/> as everything downstream of a merge wants it: the approved plan
    /// <b>and</b> the worker's declaration about following its approach, resolved in one lookup. Null when
    /// the worker holds no approval (a manual agent, an external-PR head, a worker spawned with plan mode
    /// off) — which reads as "unmanaged" and means neither the scope comparison nor the deviation rows
    /// apply.
    ///
    /// <para>One method rather than two seams on purpose. The F6 scope comparison and the approach the
    /// reviewer is shown must describe the SAME approved plan; two independent resolvers would be free to
    /// name two different ones the instant a re-scope lands, and the surface would then present an
    /// approach the diff was never measured against.</para>
    /// </summary>
    public ApprovedWork? ApprovedWorkFor(string workerAgentId)
    {
        var approved = ApprovedForWorker(workerAgentId);
        return approved is null
            ? null
            : new ApprovedWork(approved.Plan, approved.Deviation, approved.Deviations);
    }

    /// <summary>The workers currently held at the plan gate (presented-undecided, or owing a revision).</summary>
    public IReadOnlyList<string> BlockedWorkerIds()
    {
        lock (_gate)
        {
            return _plans.Values
                .Where(p => p.BlocksWorker && p.WorkerAgentId.Length > 0)
                .OrderBy(p => p.DraftedAt)
                .Select(p => p.WorkerAgentId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>The workers that spent their revision budget and stopped for the human.</summary>
    public IReadOnlyList<string> EscalatedWorkerIds()
    {
        lock (_gate)
        {
            return _plans.Values
                .Where(p => p.Status == PlanStatus.Escalated && p.WorkerAgentId.Length > 0)
                .OrderBy(p => p.DraftedAt)
                .Select(p => p.WorkerAgentId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// The pressure signal for a coordinator: the "N plans pending" fact line surfaced on the coordinator
    /// surface, or null when there is no pressure worth showing (≤ the soft threshold of 2).
    /// </summary>
    public string? PressureSignal(string coordinatorId)
    {
        List<PendingPlan> pending;
        lock (_gate)
        {
            pending = _plans.Values
                .Where(p => p.Status == PlanStatus.Pending && p.CoordinatorId == coordinatorId)
                .OrderBy(p => p.DraftedAt)
                .ToList();
        }

        if (pending.Count <= 2)
        {
            return null;
        }

        var oldest = (int)Math.Max(0, (_clock() - pending[0].DraftedAt).TotalMinutes);
        return $"{pending.Count} plans pending — the oldest has waited {oldest} min.";
    }

    // ---- internals --------------------------------------------------------

    /// <summary>
    /// The worker's approved plan — <b>single-or-nothing</b>. The supersession in <see cref="Approve"/> is
    /// what makes at most one exist; this refuses to guess if that invariant were ever broken, following
    /// <c>WorkerPlanGate.ResolveKeyLocked</c>'s precedent, because every caller reads null as "not
    /// authorised" and an arbitrary pick would be an authorisation decided by dictionary order.
    /// </summary>
    private PendingPlan? ApprovedForWorkerLocked(string workerAgentId)
    {
        PendingPlan? found = null;
        foreach (var plan in _plans.Values)
        {
            if (plan.Status != PlanStatus.Approved ||
                !string.Equals(plan.WorkerAgentId, workerAgentId, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                return null; // ambiguous — refuse rather than authorise by enumeration order.
            }

            found = plan;
        }

        return found;
    }

    private PendingPlan? LiveForWorkerLocked(string workerAgentId) =>
        _plans.Values
            .Where(p => string.Equals(p.WorkerAgentId, workerAgentId, StringComparison.Ordinal))
            .Where(p => p.Status is PlanStatus.Pending or PlanStatus.Rejected or PlanStatus.Approved)
            .OrderByDescending(p => p.DraftedAt)
            .FirstOrDefault();

    private PendingPlan GetPendingLocked(string planId)
    {
        if (!_plans.TryGetValue(planId, out var plan))
        {
            throw new InvalidOperationException($"No plan '{planId}'.");
        }

        if (plan.Status != PlanStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Plan '{planId}' is {plan.Status} — only a plan awaiting your decision can be approved or rejected.");
        }

        return plan;
    }

    private PlanDecision DecisionOf(PendingPlan plan) => new(
        plan.PlanId,
        plan.Status,
        plan.RejectionFeedback,
        plan.RevisionCount,
        Math.Max(0, _limits.MaxPlanRevisions - plan.RevisionCount),
        plan.ApproverIdentity);

    private void CompleteWaiters(PendingPlan plan)
    {
        List<TaskCompletionSource<PlanDecision>>? waiters;
        PlanDecision decision;
        lock (_gate)
        {
            decision = DecisionOf(plan);
            if (!_waiters.TryGetValue(plan.PlanId, out waiters))
            {
                return;
            }

            _waiters.Remove(plan.PlanId);
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(decision);
        }
    }

    private void AuditLocked(string type, Dictionary<string, string> fields) =>
        _audit.Append(new AuditEvent(type, fields));
}
