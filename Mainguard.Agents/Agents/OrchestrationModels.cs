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

/// <summary>
/// Branch merge-eligibility lifecycle — the P2-10 enum (OPS §4.2), plus the human's
/// <see cref="Discarded"/> terminal.
///
/// <para><b><see cref="Discarded"/> is deliberately its own member rather than a reuse of
/// <see cref="Rejected"/>.</b> <c>Rejected</c> means a human read this branch's diff in review and turned
/// the work down — it is only reachable from <c>AwaitingReview</c> and it is a statement about the code.
/// <c>Discarded</c> is a statement about the ENTRY: the human dropped it from the queue (its agent is gone,
/// the work was superseded, it was never going anywhere). Persisting one as the other would make the
/// queue's own record say something that did not happen, which is the failure this queue exists to
/// prevent. Neither is <see cref="Merged"/>, and nothing in this enum lets a discard be mistaken for
/// one.</para>
/// </summary>
public enum WorkerMergeState { Working, Verifying, Verified, StaleVerified, AwaitingReview, Merged, Rejected, Discarded }

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

/// <param name="VerificationInFlight">Whether a verification run is really executing for this entry, as
/// opposed to the entry merely being in <see cref="WorkerMergeState.Verifying"/>. Only the daemon can
/// answer this (its in-flight set is memory; the state is persisted), and the two disagree after any
/// restart mid-run. Defaults to false so a projection that cannot answer never claims a run is
/// happening — the direction that matters, since claiming one is what makes an entry look busy forever.</param>
/// <param name="HasLiveSandbox">
/// Whether this entry's agent still has a jail — <b>three-valued, and null means "not known here"</b>.
///
/// <para>It decides whether the entry is workable at all: verification runs in the worker's own sandbox
/// and never on the host, so an entry whose jail is gone cannot be verified, cannot reach
/// <see cref="WorkerMergeState.Verified"/>, and therefore cannot merge — its only honest actions are
/// resume and discard. Only the daemon holds the session table, so no client can derive it.</para>
///
/// <para><b>Why not a bool.</b> <c>false</c> is the answer that makes an entry render as stranded and
/// offers to spawn a jail for it. A projection that simply cannot answer — the mock, a daemon predating
/// the field — must not give that answer by default, so "no jail" and "no idea" are different values.
/// Null leaves every surface exactly as it was.</para>
/// </param>
public sealed record QueueEntry(
    string AgentId,
    string Name,
    string Branch,
    WorkerMergeState State,
    string Detail,
    VerificationRecord? Verification,
    IReadOnlyList<FlaggedItem> FlaggedItems,
    bool VerificationInFlight = false,
    bool? HasLiveSandbox = null);

/// <summary>P2-14: the schema-validated plan a managed worker spawns from. Scope is load-bearing.</summary>
public sealed record TaskPlan(
    string PlanId,
    string Title,
    IReadOnlyList<string> Scope,
    string Approach,
    string TestStrategy,
    decimal BudgetUsd,
    DateTimeOffset DraftedAt);

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

/// <summary>
/// P2-13 activity-bar resource sample (VM CPU/RAM + gateway token spend).
///
/// <para><b>Every reading is nullable, and null means NOT MEASURED — never zero.</b> These were plain
/// doubles, and the daemon-backed client filled them with literal <c>0</c> because nothing sampled the
/// containers; the monitor therefore displayed a confident "CPU 0% · RAM 0.0 GB" for a fleet of busy
/// agents. Making absence representable is what stops that from being expressible again: a formatter
/// cannot render an unknown as 0 if the unknown never becomes a 0.</para>
/// </summary>
/// <param name="SpendTodayUsd">Null when spend is not measurable — see <see cref="AgentResourceUsage.IsMetered"/>.</param>
public sealed record ResourceSample(DateTimeOffset At, double? CpuPercent, double? RamGb, decimal? SpendTodayUsd);

/// <summary>One agent's live resource row for the task-manager-style monitor (revised 2026-07-11):
/// per-agent CPU/RAM/spend plus the state word and current task, so totals decompose.
/// Null CPU/RAM/spend mean not measured, never zero (see <see cref="ResourceSample"/>).</summary>
/// <param name="IsMetered">Whether this agent's model spend is measurable at all: true exactly when the
/// daemon issued it a gateway confinement token at spawn, so its traffic transits the metering proxy.
/// False for OAuth sessions (they authenticate past the proxy with a credential Mainguard never issued),
/// for BYOK CLIs that declare no base-URL/model-host pair (codex, qwen-code, opencode), and when the
/// gateway is off. When false the UI must show no spend figure rather than <c>$0.00</c>, which would read
/// as "you have spent nothing". See <c>docs/design/oauth-budgeting.md</c>.</param>
public sealed record AgentResourceUsage(
    string AgentId, string Name, string StateWord, bool IsPaused,
    double? CpuPercent, double? RamGb, decimal? SpendUsd, string Task, bool IsMetered = false);

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
