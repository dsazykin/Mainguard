using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents;

// The service seams the control-center ViewModels consume (Lane E Part 3). Each interface is
// shaped like the corresponding daemon surface (P2-02 gRPC services / P2-10 IMergeQueue /
// P2-14 coordinator / P2-44 telemetry) so MockOrchestrator can later be replaced by a
// DaemonClient adapter with zero View or ViewModel changes. Events may be raised on any
// thread — consumers marshal to the UI thread (the existing app pattern).

/// <summary>AgentService: list + event stream + prompt queue (P2-02 §2.4, P2-39.1).</summary>
public interface IAgentService
{
    IReadOnlyList<AgentInfo> ListAgents();
    /// <summary>Stands in for the StreamAgentEvents server-stream (OPS §3.4). Seq-ordered.</summary>
    event Action<AgentEvent>? EventReceived;
    /// <summary>Queued while the adapter streams; delivered on idle (P2-39.1).</summary>
    Task SendPromptAsync(string agentId, string prompt);
    IReadOnlyList<string> GetQueuedPrompts(string agentId);
    Task CancelQueuedPromptAsync(string agentId, int index);
    /// <summary>The scripted PTY tail for the prototype's terminal pane.</summary>
    IReadOnlyList<string> GetTerminalTail(string agentId);
    /// <summary>P2-39.4: the parsed plan/task tree beside the terminal (read-only in v1).</summary>
    IReadOnlyList<(string Step, bool Done)> GetPlanTree(string agentId);

    /// <summary>Per-agent pause — the same recoverable mechanism the kill switch fans out.</summary>
    Task PauseAgentAsync(string agentId);
    Task ResumeAgentAsync(string agentId);
    /// <summary>End task: reject the agent's work and tear its sandbox down. The branch is
    /// kept until teardown (V-5 — nothing is silently lost); UI confirms before calling.</summary>
    Task EndAgentAsync(string agentId);
}

/// <summary>
/// What a verification attempt did, phrased for display. <paramref name="Ran"/> distinguishes the one
/// thing the merge decision rests on: <i>the run never started</i> (no live jail, no configured test
/// command, no active repo — <paramref name="Reason"/> says which) versus <i>the run happened and the
/// branch's tests decided it</i>. A genuinely failing suite is <c>Ran: true, Passed: false</c> — a
/// result, not an error — and must never be rendered as a provisioning problem.
/// </summary>
public sealed record VerificationOutcome(bool Ran, bool Passed, string Reason);

/// <summary>The P2-10 queue, UI-facing shape (states + gate + verification trigger + human merge).</summary>
public interface IMergeQueueService
{
    string MainSha { get; }
    /// <summary>Fires whenever the daemon's queue projection changes — a spawn's <c>EnsureEntry</c>,
    /// a state transition, a stale cascade. Field bug (found live 2026-08-20): nothing subscribed a
    /// rail refresh to this signal, so a fresh spawn's entry sat in <see cref="GetQueue"/>'s answer
    /// correctly but unrendered until an UNRELATED event (an AgentEvent, a coordinator/kill-switch
    /// change) happened to trigger one — "the spawned agent never appeared in the queue."</summary>
    event Action? Changed;
    IReadOnlyList<QueueEntry> GetQueue();
    bool CanMerge(string agentId, out string reason);
    /// <summary>
    /// Asks the daemon to verify <paramref name="agentId"/>'s branch <b>now</b>, in that agent's own jail.
    ///
    /// <para><b>This is a trigger, not an implementation.</b> Every decision — the Verifying transition, the
    /// jail execution, the immutable record, the Verified-or-back-to-Working outcome — belongs to
    /// <c>MergeQueue.RunVerificationAsync</c> on the daemon, and this seam only asks for it. Callers must not
    /// re-implement any part of that sequence beside it: an automatic caller (phase 2's "the worker says it
    /// is ready") drives the same daemon method and therefore gets identical gates, identical jail
    /// execution, and identical state transitions.</para>
    ///
    /// <para>Verifying does <b>not</b> merge and does not weaken any gate. A passing run only moves the
    /// branch to <c>Verified</c>; <see cref="CanMerge"/> still has to agree before <see
    /// cref="ConfirmMergeAsync"/> is reachable.</para>
    /// </summary>
    Task<VerificationOutcome> RunVerificationAsync(string agentId);
    /// <summary>
    /// The human foreground merge; fires the NotifyMainMoved stale cascade.
    /// <para>Returns <see cref="MergeOutcome"/> rather than a bare task because a merge that landed is not
    /// self-describing: the entry's <see cref="MergeEntryOrigin"/> decides <i>where</i> it landed, and a
    /// caller that cannot see the origin can only report the local fast-forward's shape for both.</para>
    /// </summary>
    Task<MergeOutcome> ConfirmMergeAsync(string agentId);
    Task AcknowledgeFlaggedChangeAsync(string agentId, string itemId);

    /// <summary>
    /// The human drops an entry from the queue (terminal <c>Discarded</c>, recorded with a daemon-derived
    /// actor and timestamp). Until this existed, an entry whose agent had been stopped had no reachable
    /// operation at all: it was not verifiable (no jail), not reviewable (not Verified), not mergeable and
    /// not removable, so it sat on the rail forever.
    /// </summary>
    /// <param name="reason">The human's verbatim reason; may be empty.</param>
    /// <returns>The record the daemon wrote.</returns>
    /// <exception cref="InvalidOperationException">The daemon refused; the message is the reason, already
    /// phrased for display, and the queue is unchanged.</exception>
    Task<QueueEntryDiscardOutcome> DiscardEntryAsync(string agentId, string reason);

    /// <summary>
    /// Rejects a VERIFIED (or awaiting-review) entry — the review verdict "no", terminal. Distinct from
    /// <see cref="DiscardEntryAsync"/>, which is entry housekeeping legal from any non-terminal state:
    /// rejecting is a judgment about reviewed work, refused for entries that were never verified.
    /// </summary>
    /// <param name="reason">The reviewer's verbatim reason; may be empty.</param>
    /// <returns>The record the daemon wrote.</returns>
    /// <exception cref="InvalidOperationException">The daemon refused; the message is the reason, already
    /// phrased for display, and the queue is unchanged.</exception>
    Task<QueueEntryRejectOutcome> RejectEntryAsync(string agentId, string reason);

    /// <summary>
    /// Clears a <c>Verifying</c> entry that has no run behind it, returning it to <c>Working</c>.
    /// Refused (as an exception carrying the daemon's reason) when a verification really is executing.
    /// </summary>
    Task ClearStalledVerificationAsync(string agentId);

    /// <summary>
    /// <b>Resume a stranded entry</b>: give it a live jail again, standing on its own
    /// <c>agent/&lt;id&gt;</c> branch with its commits intact, so it can be verified and merged instead of
    /// only discarded.
    ///
    /// <para>The entry is <b>adopted, not replaced</b> — same agent id, same branch, same row, same
    /// origin, same verification history. Nothing here creates a queue entry; the daemon refuses outright
    /// for an id its queue does not already hold.</para>
    ///
    /// <para>Every decision is the daemon's (does this repo's queue hold the entry, is a verification
    /// really running, is a merge lease open, does the branch still exist, does the id already have a
    /// session), and adoption is denied to the coordinator role at the interceptor — an agent that could
    /// adopt an arbitrary id could hijack another agent's entry.</para>
    /// </summary>
    /// <param name="agentId">The stranded entry to resume.</param>
    /// <param name="agentKind">Which CLI to run in the resumed jail. The queue entry does not record the
    /// kind that produced the branch, so this is the human's choice — asked, not guessed.</param>
    /// <exception cref="InvalidOperationException">The daemon refused; the message is its reason, already
    /// phrased for display, and nothing was changed.</exception>
    Task<QueueEntryResumeOutcome> ResumeEntryAsync(string agentId, string agentKind);
}

/// <summary>
/// What a resume actually did. Returned rather than a bare task for the same reason
/// <see cref="QueueEntryDiscardOutcome"/> is: the surface has to state what happened instead of narrating
/// an outcome it assumed — and one of these facts is a state change the human did not directly ask for.
/// </summary>
/// <param name="AgentId">The entry that was resumed — the SAME id, never a newly minted one.</param>
/// <param name="Branch">The branch the resumed jail's worktree stands on.</param>
/// <param name="State">The entry's merge state after the resume.</param>
/// <param name="ClearedStalledVerification">True when the entry had been sitting at <c>Verifying</c> with
/// no run behind it and the resume walked it back to <c>Working</c>. Surfaced rather than hidden: a stale
/// "verifying" claim for a jail that no longer existed must not be left standing, and the human is told
/// that their resume retracted it.</param>
public sealed record QueueEntryResumeOutcome(
    string AgentId, string Branch, WorkerMergeState State, bool ClearedStalledVerification);

/// <summary>
/// What a discard recorded. Returned rather than a bare task for the same reason
/// <see cref="MergeOutcome"/> is: the surface has to be able to state what happened — including WHO the
/// daemon attributed it to — instead of narrating an outcome it assumed.
/// </summary>
/// <param name="AgentId">The entry that was dropped.</param>
/// <param name="DiscardedBy">Daemon-derived actor (see <c>MergeQueueRow.DiscardedBy</c> for its limits).</param>
/// <param name="DiscardedAt">When the daemon recorded it; null when the daemon sent no timestamp.</param>
public sealed record QueueEntryDiscardOutcome(
    string AgentId, string DiscardedBy, DateTimeOffset? DiscardedAt);

/// <summary>What a reject recorded — same rationale as <see cref="QueueEntryDiscardOutcome"/>.</summary>
public sealed record QueueEntryRejectOutcome(
    string AgentId, string RejectedBy, DateTimeOffset? RejectedAt);

/// <summary>P2-14: the coordinator conversation + two-phase plan approval.</summary>
public interface ICoordinatorService
{
    IReadOnlyList<ChatLine> GetTranscript();
    IReadOnlyList<TaskPlan> GetPendingPlans();
    TaskPlan? GetPlan(string planId);
    event Action? Changed;
    Task SendAsync(string text);
    Task SubmitPlanDecisionAsync(string planId, bool approve);
}

/// <summary>P2-14 kill switch: freeze-queue-first, then yield fan-out. Recoverable by design.</summary>
public interface IKillSwitchService
{
    bool IsFrozen { get; }
    KillSwitchPhase Phase { get; }
    /// <summary>The banner's fact line, e.g. "queue frozen · 3 of 4 agents paused".</summary>
    string PhaseText { get; }
    event Action? Changed;
    Task EngageAsync();
    Task ResumeAsync();
}

/// <summary>P2-44 sandbox health + P2-13 resource monitor.</summary>
public interface ITelemetryService
{
    IReadOnlyList<SandboxEvent> GetSandboxEvents(string? agentId = null);
    ResourceSample Current { get; }
    IReadOnlyList<ResourceSample> History { get; }
    /// <summary>Per-agent decomposition of the current sample (the task-manager rows).</summary>
    IReadOnlyList<AgentResourceUsage> GetAgentUsage();
    event Action? Sampled;

    /// <summary>Reads the per-agent + per-day spend caps (P2-13 editable budget). Distinct name from the
    /// DaemonBackedOrchestrator's proto-typed round-trip so both can coexist without a return-type clash.</summary>
    Task<SpendBudget> GetSpendBudgetAsync(System.Threading.CancellationToken ct = default);

    /// <summary>Writes the per-agent + per-day spend caps (persisted + reflected in the live ledger via SetBudgets).</summary>
    Task SetSpendBudgetAsync(SpendBudget budget, System.Threading.CancellationToken ct = default);
}

/// <summary>P3-01/P3-04: the Vibe substrate — checkpoints and one-click deploy.</summary>
public interface IVibeService
{
    IReadOnlyList<Checkpoint> GetCheckpoints();
    Checkpoint? LastVerifiedGreen { get; }
    Task RestoreCheckpointAsync(string sha);
    DeployStatus Deploy { get; }
    event Action? DeployChanged;
    Task PublishAsync();
}
