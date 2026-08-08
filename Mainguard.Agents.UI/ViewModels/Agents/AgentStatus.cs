using Mainguard.Agents.Agents;

namespace Mainguard.Agents.UI.ViewModels.Agents;

/// <summary>
/// The UI badge status for an agent row (P2-13). A deliberate nine-value simplification of the
/// richer OPS <see cref="AgentLifecycleState"/> / P2-10 <see cref="WorkerMergeState"/> machines,
/// so the section-rail micro-badge reads at a glance. Exactly one converter
/// (<c>AgentStatusBrushConverter</c>) maps this to a theme token — never a raw brush.
/// </summary>
public enum AgentStatus
{
    Working,
    Verifying,
    Verified,
    Stale,
    AwaitingReview,
    Conflict,
    RateLimited,
    Dead,
    Paused,
}

/// <summary>
/// Pure, total projections from the daemon-shaped lifecycle/merge enums onto the badge
/// <see cref="AgentStatus"/>. Total by construction (every input has a case), so the converter
/// and the rail never see an unmapped value.
/// </summary>
public static class AgentStatusMap
{
    /// <summary>OPS §4.1 session lifecycle → badge status.</summary>
    public static AgentStatus FromLifecycle(AgentLifecycleState state) => state switch
    {
        AgentLifecycleState.Requested => AgentStatus.Working,
        AgentLifecycleState.Provisioning => AgentStatus.Working,
        AgentLifecycleState.Working => AgentStatus.Working,
        AgentLifecycleState.Yielding => AgentStatus.Verifying,
        AgentLifecycleState.PlanPending => AgentStatus.AwaitingReview,
        AgentLifecycleState.AwaitingReview => AgentStatus.AwaitingReview,
        AgentLifecycleState.ReviewHibernated => AgentStatus.Paused,
        AgentLifecycleState.Paused => AgentStatus.Paused,
        AgentLifecycleState.RateLimited => AgentStatus.RateLimited,
        // An unresponsive agent is stuck/blocked and needs human intervention — it reads as a
        // conflict on the rail (attention), not as a silent failure.
        AgentLifecycleState.Unresponsive => AgentStatus.Conflict,
        AgentLifecycleState.Merged => AgentStatus.Verified,
        AgentLifecycleState.Rejected => AgentStatus.Dead,
        AgentLifecycleState.Dead => AgentStatus.Dead,
        AgentLifecycleState.TornDown => AgentStatus.Dead,
        _ => AgentStatus.Working,
    };

    /// <summary>P2-10 branch merge-eligibility → badge status (queue-side badges).</summary>
    public static AgentStatus FromMergeState(WorkerMergeState state) => state switch
    {
        WorkerMergeState.Working => AgentStatus.Working,
        WorkerMergeState.Verifying => AgentStatus.Verifying,
        WorkerMergeState.Verified => AgentStatus.Verified,
        WorkerMergeState.StaleVerified => AgentStatus.Stale,
        WorkerMergeState.AwaitingReview => AgentStatus.AwaitingReview,
        WorkerMergeState.Merged => AgentStatus.Verified,
        WorkerMergeState.Rejected => AgentStatus.Dead,
        // Dead, like Rejected: finished, not merged, nothing further will happen to it. Falling through
        // to the Working default would have badged a dropped entry as live work.
        WorkerMergeState.Discarded => AgentStatus.Dead,
        _ => AgentStatus.Working,
    };
}
