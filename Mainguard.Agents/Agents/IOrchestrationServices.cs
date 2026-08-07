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
}

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
