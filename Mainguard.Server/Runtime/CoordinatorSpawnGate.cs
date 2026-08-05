using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;

namespace Mainguard.Server.Runtime;

/// <summary>
/// MG-2 server-side admission for the <b>wired</b> coordinator spawn shim
/// (<see cref="AgentSpawnService.HandleShimRequestAsync"/>). The in-jail <c>mainguard-agent spawn</c>
/// path previously reached <see cref="AgentSpawnService.SpawnAsync"/> with only the kill-gate check —
/// none of the caps that live in <see cref="CoordinatorTools"/> applied, so a coordinator agent could fan
/// out unlimited Managed workers and spawn under memory pressure. This evaluator re-applies the hard,
/// server-enforceable caps at the wired admission point:
/// <list type="number">
/// <item>the active-Managed-worker ceiling (<see cref="CoordinatorLimits.MaxActiveWorkers"/>), and</item>
/// <item>VM memory-headroom admission (<see cref="AdmissionController.CanSpawn"/>).</item>
/// </list>
///
/// <para><b>Phase 2 — the cap counts blocked workers, and says so.</b> A worker waiting on human plan
/// approval is a live Managed session, so it was already inside <paramref name="activeManagedWorkers"/>;
/// that is the decided behaviour, because the cap is a resource cap and a blocked worker still holds its
/// jail, tmpfs, network segment and worktree. What is new is that the refusal <i>names</i> the cause. The
/// old text — "let one finish before spawning another" — was actively misleading when nothing was going to
/// finish: the workers were not busy, they were waiting on the human reading the message. A stall that
/// cannot explain itself is indistinguishable from a hang.</para>
///
/// <para>The remaining MG-2 item — requiring an approved-plan token on the spawn itself — is deliberately
/// NOT reinstated: the contract moved the plan gate to the worker (§6). What replaces it is
/// <see cref="WorkerPlanGate"/>, which withholds the task from the spawned worker until a human approves
/// the plan that worker authored.</para>
/// </summary>
internal static class CoordinatorSpawnGate
{
    /// <summary>
    /// Returns a human-readable refusal reason, or <c>null</c> when the spawn is admitted. Order is
    /// cap-first (a deterministic, count-based refusal) then admission (the memory-pressure signal).
    /// </summary>
    /// <param name="planGate">
    /// Supplies the blocked-worker count for the refusal text. Optional so the pure cap arithmetic stays
    /// testable on its own; when it is absent the refusal degrades to the generic wording rather than
    /// asserting a cause it did not check.
    /// </param>
    internal static string? Evaluate(
        int activeManagedWorkers,
        int maxActiveWorkers,
        AdmissionController admission,
        WorkerPlanGate? planGate = null)
    {
        if (activeManagedWorkers >= maxActiveWorkers)
        {
            var head = $"Worker cap reached — {activeManagedWorkers}/{maxActiveWorkers} managed workers running.";
            var blocked = planGate?.BlockedWorkerCount ?? 0;
            if (blocked == 0)
            {
                return head + " Let one finish before spawning another.";
            }

            var noun = blocked == 1 ? "worker is" : "workers are";
            return $"{head} {blocked} {noun} waiting on human plan approval — " +
                   "no new workers until those plans are cleared.";
        }

        if (!admission.CanSpawn(out var reason))
        {
            return reason;
        }

        return null;
    }
}
