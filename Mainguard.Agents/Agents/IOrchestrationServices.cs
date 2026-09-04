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

/// <summary>
/// The stdout/stderr an entry's last verification actually produced — the client half of the
/// <c>GetVerificationLog</c> RPC (H4).
///
/// <para><b>Three answers, kept apart</b>, exactly as the wire keeps them. <c>HasRecord=false</c> is "this
/// entry has never been verified"; <c>HasRecord=true</c> with a non-empty <see cref="Text"/> is the log;
/// <c>HasRecord=true</c> with an empty <see cref="Text"/> and a stated <see cref="UnavailableReason"/> is
/// "the verdict stands but its output is gone". Collapsing the third into the second would render a
/// deleted artifact as a test suite that printed nothing, which is the same quiet fabrication as the
/// "not verified yet" this whole change removes.</para>
///
/// <para><see cref="Text"/> is <b>jail-produced</b>, and arrives here already sanitized (see the
/// <c>JailText</c> helper in <c>Mainguard.Agents.UI</c>): control characters and terminal escape sequences
/// are removed at the projection boundary, so no consumer can forget to. It is also a TAIL — see
/// <see cref="Truncated"/>.</para>
/// </summary>
/// <param name="HasRecord">False when there is no verification record at all. Everything else is then empty
/// and must be rendered as "not verified yet", never as an empty log.</param>
/// <param name="Passed">The recorded verdict; meaningless when <paramref name="HasRecord"/> is false.</param>
/// <param name="ResolvedCommand">The command the run was produced by (RT-D2 provenance).</param>
/// <param name="MainSha">The main@sha the run was measured against.</param>
/// <param name="When">When the record was written; null when the daemon sent no timestamp.</param>
/// <param name="Text">The artifact's content, sanitized. Empty with <paramref name="HasRecord"/> true means
/// the artifact could not be read — see <paramref name="UnavailableReason"/>.</param>
/// <param name="Truncated">True when <paramref name="Text"/> is the END of a longer artifact. Named rather
/// than silently elided: a human reading a failure needs to know they are looking at a fragment.</param>
/// <param name="UnavailableReason">The daemon's verbatim reason when a record exists and its artifact could
/// not be read. Empty otherwise.</param>
public sealed record VerificationLog(
    bool HasRecord,
    bool Passed,
    string ResolvedCommand,
    string MainSha,
    DateTimeOffset? When,
    string Text,
    bool Truncated,
    string UnavailableReason)
{
    /// <summary>The answer for a surface that has no repo bound or could not reach the daemon: not "there
    /// is no record" (which is a claim about the entry) but a stated failure to ask.</summary>
    public static VerificationLog Unreachable(string reason) =>
        new(HasRecord: true, Passed: false, ResolvedCommand: "", MainSha: "", When: null,
            Text: "", Truncated: false, UnavailableReason: reason);
}

/// <summary>The P2-10 queue, UI-facing shape (states + gate + verification trigger + human merge).</summary>
public interface IMergeQueueService
{
    string MainSha { get; }

    /// <summary>When the daemon last tried to pull the mirror's main forward from the checkout, or null
    /// when it has not yet (2026-09-04). The rail renders the age from this; the daemon states the fact.</summary>
    DateTimeOffset? MirrorMainRefreshedAt => null;

    /// <summary>Why that last attempt failed, or null when it succeeded / has not run.</summary>
    string? MirrorMainRefreshError => null;

    /// <summary>Asks the daemon to refresh the mirror's main now — the call a human returning to the window
    /// makes. Never throws to its caller; a refusal or an outage is the next update's error field.</summary>
    Task RefreshMirrorMainAsync() => Task.CompletedTask;
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
    /// <b>Reads</b> the output of the entry's last verification — without running anything.
    ///
    /// <para>This is the whole point of it being a separate call from
    /// <see cref="RunVerificationAsync"/>. A re-run costs minutes of real test time in a jail and can
    /// legitimately answer differently, so "let me see why it failed" must never be spelled "run it
    /// again" — which is precisely what the surface forced before this existed: the daemon wrote the real
    /// output to an artifact, recorded its path in SQLite, and carried none of it on any wire, so the only
    /// way to see a failure was to pay for a second run.</para>
    ///
    /// <para>It is <b>idempotent and side-effect-free</b>: it moves no state, starts no run, and touches no
    /// gate. Called on demand — never on every queue refresh — because it is a per-entry file read on the
    /// daemon.</para>
    /// </summary>
    Task<VerificationLog> GetVerificationLogAsync(string agentId);

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

    /// <summary>
    /// <b>"Let the agent resolve"</b> — unpause the jail the daemon parked mid-rebase and instruct the
    /// worker to finish resolving its own conflict.
    ///
    /// <para>It acts on an entry the daemon blocked with "the agent is paused with the rebase in progress
    /// and needs a human to resolve it" — a sentence naming a required human action that, until this
    /// existed, the surface had no operation for. The jail was paused, so nothing could even be exec'd in
    /// it, and the row's only controls were Verify (which cannot run in a paused jail), the verification
    /// log, and Discard.</para>
    ///
    /// <para>Every decision is the daemon's: whether a rebase is really parked, whether it is still in
    /// progress, whether the jail exists, and whether the instruction was actually submitted to the CLI.
    /// This seam asks; it asserts nothing.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The daemon declined; the message is its reason, already
    /// phrased for display, and the conflict is exactly as it was.</exception>
    Task ResolveConflictWithAgentAsync(string agentId);

    /// <summary>
    /// <b>"Abort rebase"</b> — <c>git rebase --abort</c> in the parked worktree, then let the jail run
    /// again. The branch returns to its pre-rebase tip and the entry to the queue, needing verification
    /// against the new main.
    /// </summary>
    /// <exception cref="InvalidOperationException">The daemon declined; the message is its reason, and the
    /// worktree is unchanged.</exception>
    Task AbortRebaseAsync(string agentId);
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

/// <summary>
/// The operator's plan-mode toggle as the client sees it: the state, and the daemon's own sentence for it.
/// </summary>
/// <param name="Enabled">True while every delegated worker must have a human-approved plan first.</param>
/// <param name="Summary">
/// The daemon's rendering. Carried rather than composed here — two spellings of one setting is how the
/// screen and the gate come to say different things.
/// </param>
public sealed record PlanModeView(bool Enabled, string Summary)
{
    /// <summary>What the client shows before the daemon has answered: the gate is on, because that is the
    /// state it is safe to be wrong about.</summary>
    public static readonly PlanModeView Unknown = new(true, "");
}

/// <summary>The coordinator conversation + the worker-authored plan gate (contract §2).</summary>
public interface ICoordinatorService
{
    IReadOnlyList<ChatLine> GetTranscript();
    IReadOnlyList<TaskPlan> GetPendingPlans();
    TaskPlan? GetPlan(string planId);

    /// <summary>
    /// The worker-authored plans the human still has to act on: those awaiting a decision, and those whose
    /// worker escalated after spending its revision budget. Richer than
    /// <see cref="GetPendingPlans"/> because the card has to show authorship and the revise loop.
    /// </summary>
    IReadOnlyList<WorkerPlanCard> GetWorkerPlans();

    /// <summary>The daemon's backpressure fact — how many workers are held at the gate, and whether that
    /// is why the coordinator has stopped spawning.</summary>
    OrchestrationBackpressure GetBackpressure();

    /// <summary>
    /// The operator's plan-mode toggle, as the daemon currently holds it.
    ///
    /// <para>Read from the daemon rather than from a client preference, and rendered with the daemon's own
    /// sentence, for the standing §2.6 reason: a surface that disagrees with its gate is how somebody
    /// comes to believe they still have an approval step they switched off.</para>
    /// </summary>
    PlanModeView GetPlanMode();

    /// <summary>Turns the plan gate on or off for every worker spawned from now on.</summary>
    Task SetPlanModeAsync(bool enabled);

    event Action? Changed;
    Task SendAsync(string text);

    /// <param name="feedback">
    /// On a rejection this is <b>delivered to the worker</b>, which revises against it and re-presents —
    /// so an empty string is a real cost to the human, not just a missing field.
    /// </param>
    Task SubmitPlanDecisionAsync(string planId, bool approve, string? feedback = null);

    /// <summary>Asks an ESCALATED worker for one fresh plan, with guidance (owner decision, 2026-09-03).
    /// The only human act that reopens an escalation; a second escalation is terminal. Throws on refusal
    /// so the card can say why, exactly as <see cref="SubmitPlanDecisionAsync"/> does.</summary>
    Task RequestNewPlanAsync(string planId, string guidance);
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
