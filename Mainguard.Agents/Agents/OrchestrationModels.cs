using System;
using System.Collections.Generic;

namespace Mainguard.Agents.Agents;

// Prototype data model for the Phase-2 control center (Lane E Part 3).
// Shapes mirror the future gRPC contract (OPS §3.4 events, §4.1/§4.2 state machines,
// P2-10 IMergeQueue, P2-14 TaskPlan) so the mock services can later be swapped for
// DaemonClient without touching ViewModels or Views. No daemon, no Docker, no agents
// exist behind these types today — see docs/design/ControlCenterDesign.md.

/// <summary>Agent session/process lifecycle — OPS §4.1 verbatim.</summary>
public enum AgentLifecycleState
{
    Requested, PlanPending, Provisioning, Working, Yielding, Paused,
    RateLimited, Unresponsive, AwaitingReview, ReviewHibernated,
    Merged, Rejected, Dead, TornDown,
}

/// <summary>Branch merge-eligibility lifecycle — the P2-10 enum verbatim (OPS §4.2).</summary>
public enum WorkerMergeState { Working, Verifying, Verified, StaleVerified, AwaitingReview, Merged, Rejected }

/// <summary>
/// Where a merge-queue entry came from (P2-12). <see cref="Local"/> is a locally-spawned agent whose
/// merge lands via the Windows foreground merge; <see cref="External"/> is an intake'd bot PR whose
/// merge is pushed back through the host PR merge API. The queue persists this per (repo, agent) so the
/// pluggable merge step (<c>MergeDispatch</c>) routes correctly after a daemon restart.
/// </summary>
public enum MergeEntryOrigin { Local, External }

/// <summary>
/// What a confirmed merge actually did (P2-12). The two origins land a merge in two different places — a
/// <see cref="MergeEntryOrigin.Local"/> entry is fast-forwarded into the user's own checkout, while an
/// <see cref="MergeEntryOrigin.External"/> entry is merged <b>upstream by the host</b> and the checkout is
/// then converged onto the commit that merge produced — so one "Merged agent/&lt;id&gt; into main" line is
/// true for exactly one of them. The origin travels back out with the merge so the surface can say what
/// happened instead of describing the local shape for both.
/// </summary>
/// <param name="Origin">Which transport landed the merge — the fact the success message turns on.</param>
/// <param name="AgentId">The entry's agent id (<c>pr-&lt;n&gt;</c> for an external one, where the number IS
/// the upstream pull request).</param>
/// <param name="MainBranch">The local branch the merge landed on.</param>
/// <param name="NewMainSha">The sha <c>refs/heads/main</c> ACTUALLY moved to — never a cached pre-merge
/// projection, since it is the same value the queue is confirmed against.</param>
public sealed record MergeOutcome(
    MergeEntryOrigin Origin,
    string AgentId,
    string MainBranch,
    string NewMainSha);

public sealed record AgentInfo(
    string AgentId,
    string Name,             // N-4 working name, e.g. "Loom-3"
    string Branch,
    AgentLifecycleState State,
    string Detail,           // the one live fact for the list's detail slot (E4)
    DateTimeOffset SpawnedAt,
    string Role = AgentRoles.Manual); // "", "coordinator", or "managed" (subagent)

/// <summary>P2-10: immutable verification record tied to a main SHA.</summary>
public sealed record VerificationRecord(string AgentId, string MainSha, bool Passed, int TestsPassed, int TestsTotal, DateTimeOffset When);

/// <summary>P2-11: one must-acknowledge flagged item; acks bind to the diff hash daemon-side.</summary>
public sealed record FlaggedItem(string Id, string Path, string Category, string Fact, bool Acknowledged);

public sealed record QueueEntry(
    string AgentId,
    string Name,
    string Branch,
    WorkerMergeState State,
    string Detail,
    VerificationRecord? Verification,
    IReadOnlyList<FlaggedItem> FlaggedItems);

/// <summary>P2-14: the schema-validated plan a managed worker spawns from. Scope is load-bearing.</summary>
public sealed record TaskPlan(
    string PlanId,
    string Title,
    IReadOnlyList<string> Scope,
    string Approach,
    string TestStrategy,
    decimal BudgetUsd,
    DateTimeOffset DraftedAt);

/// <summary>
/// The UI-facing projection of a <b>worker-authored</b> plan awaiting a human decision (contract §2).
///
/// <para>Distinct from <see cref="TaskPlan"/> because the approval card now has to render things a plan
/// record does not carry: <i>who wrote it</i>, which revision this is against the daemon-side budget, and
/// the feedback the last rejection sent back. Those are what make the card a decision rather than a
/// notification — a human rejecting a plan for the third time needs to see that the next rejection stops
/// the worker.</para>
/// </summary>
/// <param name="Status">PlanStatus name: Pending / Approved / Rejected / Escalated.</param>
/// <param name="Revision">0 for the original presentation.</param>
/// <param name="RevisionsRemaining">Revisions still permitted before the worker must escalate.</param>
public sealed record WorkerPlanCard(
    string PlanId,
    string WorkerAgentId,
    string CoordinatorId,
    string Title,
    IReadOnlyList<string> Scope,
    string Approach,
    string TestStrategy,
    decimal BudgetUsd,
    DateTimeOffset PresentedAt,
    string Status,
    int Revision,
    int RevisionsRemaining,
    int MaxRevisions,
    string RejectionFeedback)
{
    public bool IsPending => string.Equals(Status, "Pending", StringComparison.OrdinalIgnoreCase);

    public bool IsEscalated => string.Equals(Status, "Escalated", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The backpressure fact the coordinator surface must render when workers are held at the plan gate
/// (contract §2 — "the stall must be legible").
///
/// <para>A blocked worker counts against the worker cap, so a full cap of blocked workers means the
/// coordinator has stopped spawning. That is intended behaviour, and it is indistinguishable from a hang
/// unless the UI says so — which is why <see cref="Signal"/> is carried from the daemon rather than
/// re-derived in the client: the number that refuses the coordinator and the number the human reads must
/// be the same number.</para>
/// </summary>
public sealed record OrchestrationBackpressure(
    int BlockedWorkerCount,
    int EscalatedWorkerCount,
    int ActiveWorkerCount,
    int MaxActiveWorkers,
    int MaxPlanRevisions,
    string Signal)
{
    public static readonly OrchestrationBackpressure None = new(0, 0, 0, 0, 0, "");

    /// <summary>True when the worker cap is full and blocked plans are why.</summary>
    public bool CapSaturatedByBlockedWorkers =>
        BlockedWorkerCount > 0 && MaxActiveWorkers > 0 && ActiveWorkerCount >= MaxActiveWorkers;

    public bool HasSignal => Signal.Length > 0;
}

public enum ChatLineKind
{
    Human,        // the operator's message
    Coordinator,  // the coordinator's reply (model output)
    ToolCall,     // one-line mono fact, collapsed group in the view
    SystemLine,   // OPS event rendered as a history line
    PlanCard,     // carries a PlanId — the view renders the TaskPlan approval card
}

public sealed record ChatLine(ChatLineKind Kind, string Text, DateTimeOffset At, string? PlanId = null);

/// <summary>OPS §3.4-shaped notification event: seq-ordered, dedup by seq, UI projection only.</summary>
public sealed record AgentEvent(long Seq, string Type, string AgentId, string Payload, DateTimeOffset At);

/// <summary>P2-44: one sandbox telemetry fact (egress denial, secret access attempt, …).</summary>
public sealed record SandboxEvent(DateTimeOffset At, string AgentId, string Kind, string Detail, string Process);

/// <summary>P2-13 activity-bar resource sample (VM CPU/RAM + gateway token spend).</summary>
public sealed record ResourceSample(DateTimeOffset At, double CpuPercent, double RamGb, decimal SpendTodayUsd);

/// <summary>One agent's live resource row for the task-manager-style monitor (revised 2026-07-11):
/// per-agent CPU/RAM/spend plus the state word and current task, so totals decompose.</summary>
public sealed record AgentResourceUsage(
    string AgentId, string Name, string StateWord, bool IsPaused,
    double CpuPercent, double RamGb, decimal SpendUsd, string Task);

/// <summary>P2-13/P2-08 spend caps, UI-facing shape (Core DTO — no proto/UI coupling). The per-agent
/// and per-day caps the gateway <c>BudgetLedger</c> enforces daemon-side, surfaced so the Resource
/// Monitor can display + edit them; a zero cap means "no cap". Editing round-trips the whole record
/// through the SetBudgets RPC so an unedited cap is preserved rather than cleared.</summary>
public sealed record SpendBudget(
    long PerAgentUsdMicrosCap, long PerAgentTokenCap,
    long PerDayUsdMicrosCap, long PerDayTokenCap)
{
    public static SpendBudget None { get; } = new(0, 0, 0, 0);
}

/// <summary>OPS §4.5 kill-switch phases, rendered as the banner's fact line.</summary>
public enum KillSwitchPhase { Armed, QueueFrozen, PerAgentYield, Frozen, Snapshotted, Complete }

/// <summary>P3-01: a Vibe auto-checkpoint; VerifiedGreen gates triage option 2.</summary>
public sealed record Checkpoint(string Sha, string Summary, DateTimeOffset When, bool VerifiedGreen);

public enum DeployPhase { Idle, Saving, Uploading, Building, GoingLive, Live, Failed }

public sealed record DeployStatus(DeployPhase Phase, string? LiveUrl, string? FailureSummary);
