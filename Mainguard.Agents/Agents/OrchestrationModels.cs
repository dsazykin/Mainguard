using System;
using System.Collections.Generic;
using System.Linq;

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
/// <see cref="Discarded"/> terminal and the red half of a verification
/// (<see cref="VerificationFailed"/>).
///
/// <para><b><see cref="Discarded"/> is deliberately its own member rather than a reuse of
/// <see cref="Rejected"/>.</b> <c>Rejected</c> means a human read this branch's diff in review and turned
/// the work down — it is only reachable from <c>AwaitingReview</c> and it is a statement about the code.
/// <c>Discarded</c> is a statement about the ENTRY: the human dropped it from the queue (its agent is gone,
/// the work was superseded, it was never going anywhere). Persisting one as the other would make the
/// queue's own record say something that did not happen, which is the failure this queue exists to
/// prevent. Neither is <see cref="Merged"/>, and nothing in this enum lets a discard be mistaken for
/// one.</para>
///
/// <para><b><see cref="VerificationFailed"/> exists because a verification has two outcomes and this enum
/// only had a word for one of them (H2).</b> A pass settled the entry to <see cref="Verified"/>; a FAIL
/// settled it back to <see cref="Working"/> — the same state an entry that has never been verified at all
/// sits in — so the row and the worker pane both told the human "not verified yet" about a branch whose
/// tests had just gone red, with Verify still offered. RED and NEVER-RUN were literally the same value,
/// and the only way to see the failure was to pay for a second, redundant run.</para>
///
/// <para>It is <b>not terminal</b>, and that is the whole shape of it: the branch is still the agent's to
/// fix. The legal moves out of it are the three honest ones — verify again (the human's retry, or the
/// automatic trigger on a NEW tip), back to <see cref="Working"/> when the agent pushes a fix
/// (<c>NotifyNewCommits</c>), and <see cref="Discarded"/> when the human drops it. It can never reach
/// <see cref="Merged"/> or <see cref="Rejected"/>: <c>Rejected</c> is a verdict on reviewed work and
/// there is nothing to review, and merging requires a passing record this entry by definition does not
/// have.</para>
/// </summary>
public enum WorkerMergeState
{
    Working, Verifying, Verified, StaleVerified, AwaitingReview, Merged, Rejected, Discarded,
    VerificationFailed,
}

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

/// <summary>
/// The <b>verdict</b> of an entry's last verification, in exactly the three facts the wire carries (H4:
/// <c>QueueEntry.last_verification_passed</c> / <c>_command</c> / <c>_at</c>).
///
/// <para><b>It deliberately has no test counts.</b> It replaced a <c>VerificationRecord</c> carrying
/// <c>TestsPassed</c>/<c>TestsTotal</c> — two numbers no wire has ever carried and the daemon has never
/// measured. Verification observes a process EXIT CODE in the worker's jail; nothing parses anyone's test
/// runner, so there is no "58 of 58" anywhere in this system to project. Filling those fields to satisfy
/// the old shape would have printed invented counts into a review surface, which is strictly worse than
/// printing none: a reviewer who reads "58/58 green" believes a measurement that was never taken. The type
/// was narrowed to the real fields rather than the projection inventing values for it.</para>
///
/// <para>The <c>main@sha</c> the run was measured against is <b>not</b> here either — it is
/// <see cref="QueueEntry.VerifiedMainSha"/>, straight off the wire's own <c>verified_main_sha</c>. One
/// fact, one home.</para>
/// </summary>
/// <param name="Passed">The verdict itself. A <see cref="QueueEntry.Verification"/> of <c>null</c> is the
/// materially different answer <i>no record</i> — never-run and failed must not share a representation,
/// which is exactly why the wire field is <c>optional</c> rather than a plain bool.</param>
/// <param name="ResolvedCommand">The RT-D2 command the verdict was produced by; empty when the daemon sent
/// none. Provenance, not decoration — a branch that rewrote its own test command produces a green that
/// means nothing, and the reviewer has to see WHAT passed.</param>
/// <param name="When">When the record was written; null when the daemon sent no timestamp. A week-old red
/// is a different fact from one that landed thirty seconds ago.</param>
public sealed record VerificationVerdict(bool Passed, string ResolvedCommand, DateTimeOffset? When);

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
/// <param name="VerifiedMainSha">
/// The <c>main@sha</c> this branch's verification ran against, straight off the daemon's
/// <c>verified_main_sha</c>. It is what the review cockpit's "verified @ &lt;sha&gt;" stamp reads, and the
/// only thing that tells a reviewer whether the green they are looking at was measured against today's
/// main or a week-old one.
///
/// <para>Deliberately its own field rather than folded into <see cref="Verification"/>: the sha is known
/// for entries that have no verdict at all, and a verdict-shaped wrapper around it would have meant
/// inventing a pass/fail to carry a sha — the kind of fabricated reassurance this surface exists to
/// prevent. Null means the daemon did not say.</para>
/// </param>
/// <param name="Verification">
/// The entry's last verification VERDICT, or <c>null</c> for <b>no record at all</b>. Those two answers are
/// kept apart at every layer — the wire field is <c>optional</c>, this is nullable, and the surfaces render
/// three outcomes (green / red / never run) rather than the two the old projection could express.
/// </param>
public sealed record QueueEntry(
    string AgentId,
    string Name,
    string Branch,
    WorkerMergeState State,
    string Detail,
    VerificationVerdict? Verification,
    IReadOnlyList<FlaggedItem> FlaggedItems,
    bool VerificationInFlight = false,
    bool? HasLiveSandbox = null,
    string? VerifiedMainSha = null);

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
/// <param name="Status">PlanStatus name: Pending / Approved / Rejected / Escalated / Superseded.</param>
/// <param name="Revision">0 for the original presentation.</param>
/// <param name="RevisionsRemaining">Revisions still permitted before the worker must escalate.</param>
/// <param name="SupersedesPlanId">
/// Set only on a <b>re-scope</b>: the approved plan this one asks to replace. It is what makes this card a
/// different decision from a first presentation — the human is approving a WIDENING of something they
/// already said yes to.
/// </param>
/// <param name="PreviousScope">
/// The superseded plan's scope, so the card can show what CHANGED rather than only what is now being asked
/// for. A human cannot judge a widening against a list they are expected to remember.
/// </param>
/// <param name="RescopeCount">
/// How many widenings this worker has already had approved. Rendered, not enforced: runaway scope creep is
/// meant to be visible to the person paying for it rather than silently capped at a number nobody could
/// justify — see <c>PlanApprovalService.Rescope</c>.
/// </param>
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
    string RejectionFeedback,
    string SupersedesPlanId = "",
    IReadOnlyList<string>? PreviousScope = null,
    int RescopeCount = 0)
{
    public bool IsPending => string.Equals(Status, "Pending", StringComparison.OrdinalIgnoreCase);

    public bool IsEscalated => string.Equals(Status, "Escalated", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this card asks to widen an approval the worker already holds.</summary>
    public bool IsRescope => SupersedesPlanId.Length > 0;

    /// <summary>Paths this plan adds to what was already approved (empty on an ordinary plan).</summary>
    public IReadOnlyList<string> AddedScope => IsRescope
        ? Scope.Except(PreviousScope ?? Array.Empty<string>(), StringComparer.Ordinal).ToList()
        : Array.Empty<string>();

    /// <summary>
    /// Paths the approved plan covered that this one drops. Rendered because a "re-scope" is not
    /// necessarily a widening: a plan that quietly removes a path the human already agreed to is the one
    /// shape of this op that could take something away, and a card that only ever showed additions would
    /// be the place it hid.
    /// </summary>
    public IReadOnlyList<string> RemovedScope => IsRescope
        ? (PreviousScope ?? Array.Empty<string>()).Except(Scope, StringComparer.Ordinal).ToList()
        : Array.Empty<string>();
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
/// <param name="Name">The agent's CLI kind — "claude-code", "codex". <b>Not an identity.</b> Four agents
/// of the same kind produce four identical values, which is exactly the row the resource monitor used to
/// render: four lines reading <c>claude-code</c>, with no way to tell a worker from the coordinator whose
/// death ends the session.</param>
/// <param name="Role">Coordinator / managed worker / manual (<see cref="AgentRoles"/>) — the first thing a
/// human actually recognises about an agent, and the one that decides how bad ending it is.</param>
/// <param name="Title">The worker's brief: the headline its plan was written against. Empty for a
/// coordinator (it has no brief) and for a worker that has not presented a plan yet.</param>
public sealed record AgentResourceUsage(
    string AgentId, string Name, string StateWord, bool IsPaused,
    double? CpuPercent, double? RamGb, decimal? SpendUsd, string Task, bool IsMetered = false,
    string Role = AgentRoles.Manual, string Title = "");

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
