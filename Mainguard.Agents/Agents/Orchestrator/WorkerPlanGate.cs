using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Git.Audit;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// The <b>daemon-side</b> enforcement of the phase-2 plan gate (coordinator contract §2 + §5).
///
/// <para><b>Why this exists separately from <see cref="PlanApprovalService"/>.</b> That service owns the
/// queue and the blocking <c>AwaitDecisionAsync</c> call. But a blocking call an agent can simply decline
/// to make is not a boundary — it is a convention, and this codebase has already shipped one control that
/// looked present and enforced nothing (MG-12: role authorization was dead code that failed open). So the
/// question "may this worker actually do its work yet?" is answered here, at the points where a worker's
/// work can have any effect, and the answer is enforced whether or not the worker cooperates:</para>
///
/// <list type="number">
/// <item><b>The daemon holds the task.</b> A worker is spawned with its task prompt <i>withheld</i>
/// (<see cref="Hold"/>). The prompt is released only by <see cref="TryReleaseTask"/>, and only once the
/// worker's own plan is approved. A worker that never presents a plan never learns what it was spawned to
/// do — there is no "start early" state to police, because the work was never handed over.</item>
/// <item><b>Steering is refused.</b> <see cref="MayReceivePrompt"/> denies the coordinator's
/// <c>send_worker_prompt</c> to a worker still at the gate, so the coordinator cannot hand over the task
/// out of band.</item>
/// <item><b>The merge queue refuses it.</b> This type is an <see cref="IMergeGate"/>: a branch whose
/// worker never had a plan approved cannot merge, no matter what it verified. That is the backstop — even
/// if a worker did work it was not cleared for, that work cannot reach <c>main</c>.</item>
/// </list>
///
/// <para><b>Backpressure legibility.</b> A blocked worker counts against
/// <see cref="CoordinatorLimits.MaxActiveWorkers"/> (it still holds a jail, tmpfs, network segment and
/// worktree). When the cap is saturated by workers awaiting approval, <see cref="BackpressureSignal"/>
/// says so in words — "6 workers waiting on your approval". A silent stall is indistinguishable from a
/// hang, so this is a requirement, not a nicety.</para>
/// </summary>
public sealed class WorkerPlanGate : IMergeGate
{
    private readonly PlanApprovalService _plans;
    private readonly IAuditLog _audit;
    private readonly object _gate = new();

    /// <summary>
    /// Held tasks, keyed by <b>(RepoHash, AgentId)</b> — never the bare agent id.
    ///
    /// <para>An agent id is unique only <i>within</i> a repo. Minted GUIDs happen to be unique everywhere,
    /// but the external-PR intake names its sessions <c>pr-&lt;n&gt;</c>, and two subscribed repositories
    /// that each have a pull request #7 both want <c>pr-7</c>. Keying by id alone is the bug this codebase
    /// has now fixed three times (#281 <c>AgentSessionStore</c>, #284 <c>SwarmReconciler</c>, #286
    /// <c>TerminalSessionManager</c>), and it would land here in a particularly bad form: <see cref="Hold"/>
    /// is idempotent per key, so a second repo's <c>pr-7</c> would silently <i>inherit the first repo's
    /// held task</i> — and approving one repo's plan would authorise the other repo's worker.</para>
    ///
    /// <para><b>Not reachable today, fixed at the key anyway.</b> The only plan-gated spawn path is the
    /// coordinator's <c>spawn</c> op, which never names an id, so every held worker currently has a minted
    /// GUID. That is a property of one call site, not of this type — and "the caller happens not to do the
    /// dangerous thing" is exactly the kind of reasoning that stops being true without anyone noticing.</para>
    /// </summary>
    private readonly Dictionary<(string RepoHash, string AgentId), HeldTask> _held = new();

    public WorkerPlanGate(PlanApprovalService plans, IAuditLog? audit = null)
    {
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _audit = audit ?? new InMemoryAuditLog();
    }

    /// <summary>A task prompt withheld from a worker until its plan is approved.</summary>
    private sealed record HeldTask(
        string RepoHash, string CoordinatorId, string Title, string TaskPrompt, decimal BudgetUsd, bool Released);

    /// <summary>
    /// Resolves a bare agent id to its held-task key, <b>unique-or-nothing</b>.
    ///
    /// <para>Most entry points here receive an id with no repo attached (the worker's IPC socket carries no
    /// repo — identity is positional). Following the <c>AgentSessionStore.Find(agentId)</c> precedent, an
    /// id held by two repos resolves to <i>nothing</i> rather than to an arbitrary one of them: every
    /// caller of this treats "no held task" as "not authorised", so ambiguity fails closed. Returning
    /// either candidate would be the aliasing bug wearing a lookup's clothes.</para>
    /// </summary>
    private (string RepoHash, string AgentId)? ResolveKeyLocked(string agentId)
    {
        (string RepoHash, string AgentId)? found = null;
        foreach (var key in _held.Keys)
        {
            if (!string.Equals(key.AgentId, agentId, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                return null; // ambiguous — two repos hold this id; refuse rather than guess.
            }

            found = key;
        }

        return found;
    }

    /// <summary>The held task for a bare agent id, or null when unknown or ambiguous.</summary>
    private HeldTask? FindLocked(string agentId) =>
        ResolveKeyLocked(agentId) is { } key ? _held[key] : null;

    /// <summary>
    /// How many (repo, agent id) tasks this gate is holding. Exposed because it is the only way to observe
    /// that two repositories each hold their <i>own</i> task for the same agent id — the distinction the
    /// composite key exists to preserve, and one that no id-keyed accessor can show.
    /// </summary>
    public int HeldTaskCount
    {
        get { lock (_gate) { return _held.Count; } }
    }

    /// <summary>Raised (off any lock) when a worker's task is released, so the daemon can deliver it.</summary>
    public event Action<string, string>? TaskReleased;

    /// <summary>
    /// Records the work a worker was spawned for <b>without giving it to the worker</b>. Called on the
    /// spawn path; idempotent per worker id.
    /// </summary>
    public void Hold(
        string workerAgentId, string coordinatorId, string title, string taskPrompt, decimal budgetUsd,
        string repoHash = "")
    {
        if (string.IsNullOrWhiteSpace(workerAgentId))
        {
            throw new ArgumentException("workerAgentId is required.", nameof(workerAgentId));
        }

        var key = (repoHash ?? string.Empty, workerAgentId);
        lock (_gate)
        {
            if (_held.ContainsKey(key))
            {
                return;
            }

            _held[key] = new HeldTask(
                repoHash ?? string.Empty, coordinatorId ?? "", title ?? "", taskPrompt ?? "", budgetUsd, Released: false);
        }

        _audit.Append(new AuditEvent("worker_task_withheld", new Dictionary<string, string>
        {
            ["worker_agent_id"] = workerAgentId,
            ["coordinator_id"] = coordinatorId ?? "",
            ["repo_hash"] = repoHash ?? string.Empty,
        }));
    }

    /// <summary>The brief a worker IS given up front: what to plan about, never what to do.</summary>
    /// <remarks>
    /// The distinction is deliberate. The worker needs enough to inspect the right part of the repository
    /// and author a meaningful plan; it does not need — and does not get — the executable task until a
    /// human has read that plan.
    /// </remarks>
    public string? PlanningBriefFor(string workerAgentId)
    {
        lock (_gate)
        {
            return FindLocked(workerAgentId)?.Title;
        }
    }

    /// <summary>The coordinator that spawned this worker (null when unknown).</summary>
    public string? CoordinatorFor(string workerAgentId)
    {
        lock (_gate)
        {
            return FindLocked(workerAgentId)?.CoordinatorId;
        }
    }

    /// <summary>The budget the coordinator allotted this worker (0 when unknown).</summary>
    public decimal BudgetFor(string workerAgentId)
    {
        lock (_gate)
        {
            return FindLocked(workerAgentId)?.BudgetUsd ?? 0m;
        }
    }

    /// <summary>
    /// Releases the withheld task prompt — <b>only</b> when this worker holds an approved plan. Returns
    /// false and yields nothing otherwise; there is no override parameter, because an override is how a
    /// gate becomes decorative.
    ///
    /// <para><b>Idempotent, and the two halves of that word are load-bearing in opposite directions.</b>
    /// This is called more than once by design: <c>mainguard-plan await &lt;id&gt;</c> is the documented
    /// re-attach after a worker crash or a daemon restart, and the daemon answers it by asking the gate
    /// again. So a repeat call must keep <b>answering</b> the same — a re-attached worker that holds an
    /// approved plan and gets an empty prompt back is stranded with no way to learn its task. But the
    /// <b>side effects</b> of the release happen exactly once: the <c>worker_task_released</c> audit record
    /// exists to prove this gate authorised this worker's task one time, and a second copy of it is not
    /// extra evidence but corrupted evidence; and <see cref="TaskReleased"/> is the daemon's instruction to
    /// deliver, so firing it twice is the same task handed out twice.</para>
    ///
    /// <para>The once-only decision is made <i>under the lock</i>, not by a read-then-write around it: the
    /// racing-callers case is exactly the one the re-attach path produces (a reconnecting worker while the
    /// approval is still landing), and a check-then-act there lets every racer believe it won.</para>
    /// </summary>
    public bool TryReleaseTask(string workerAgentId, out string taskPrompt)
    {
        taskPrompt = string.Empty;
        if (!_plans.HasApprovedPlan(workerAgentId))
        {
            _audit.Append(new AuditEvent("worker_task_release_denied", new Dictionary<string, string>
            {
                ["worker_agent_id"] = workerAgentId,
                ["cause"] = "no-approved-plan",
            }));
            return false;
        }

        bool isFirstRelease;
        lock (_gate)
        {
            if (ResolveKeyLocked(workerAgentId) is not { } key)
            {
                return false;
            }

            var task = _held[key];
            taskPrompt = task.TaskPrompt;
            isFirstRelease = !task.Released;
            if (isFirstRelease)
            {
                // Written back under the COMPOSITE key — the same key the read above resolved.
                //
                // This line is where the release-exactly-once fix and the (RepoHash, AgentId) re-key meet,
                // so it is worth saying what does and does not protect it. Writing the bare
                // `_held[workerAgentId]` cannot happen silently: the dictionary is tuple-keyed, so that
                // does not compile. What CAN happen silently is a write-back under a *different* composite
                // key — `("", workerAgentId)` being the obvious one, since it is what a resolution that
                // reconciled the two changes carelessly would produce. That inserts a second entry instead
                // of latching the real one: `Released` stays false on the held task, every repeat call
                // looks like a first release, and the idempotence is undone with nothing failing to build.
                // `ReleasingTwiceForARepoScopedWorker_StillAuditsAndAnnouncesOnce` is the test that fails
                // if it ever is — the pre-existing release-once tests all hold at the default empty repo
                // hash, where the wrong key and the right key coincide.
                _held[key] = task with { Released = true };
            }
        }

        if (!isFirstRelease)
        {
            // Already handed over. Answer truthfully, record nothing, announce nothing.
            return true;
        }

        _audit.Append(new AuditEvent("worker_task_released", new Dictionary<string, string>
        {
            ["worker_agent_id"] = workerAgentId,
        }));
        TaskReleased?.Invoke(workerAgentId, taskPrompt);
        return true;
    }

    /// <summary>True once this worker's task has actually been handed over.</summary>
    public bool TaskWasReleased(string workerAgentId)
    {
        lock (_gate)
        {
            return FindLocked(workerAgentId) is { Released: true };
        }
    }

    /// <summary>Drops a stopped worker's held task (teardown; keeps the gate from growing unboundedly).</summary>
    public void Forget(string workerAgentId)
    {
        lock (_gate)
        {
            if (ResolveKeyLocked(workerAgentId) is { } key)
            {
                _held.Remove(key);
            }
        }
    }

    /// <summary>
    /// May this worker do the work it was spawned for? True only with an approved plan. The reason is
    /// written for a human to read verbatim.
    /// </summary>
    public bool MayWork(string workerAgentId, out string reason)
    {
        if (_plans.HasApprovedPlan(workerAgentId))
        {
            reason = string.Empty;
            return true;
        }

        var plan = _plans.LatestForWorker(workerAgentId);
        reason = plan?.Status switch
        {
            PlanStatus.Pending =>
                $"{workerAgentId} is waiting on your approval of its plan — it has not started work.",
            PlanStatus.Rejected =>
                $"{workerAgentId} is revising its plan against your feedback (revision " +
                $"{plan.RevisionCount + 1} of {_plans.MaxPlanRevisions}).",
            PlanStatus.Escalated =>
                $"{workerAgentId} stopped after {_plans.MaxPlanRevisions} rejected plans and escalated to you.",
            _ => $"{workerAgentId} has not presented a plan yet — no work is authorised.",
        };
        return false;
    }

    /// <summary>Whether the coordinator may steer this worker (denied while it is held at the gate).</summary>
    public bool MayReceivePrompt(string workerAgentId, out string reason) => MayWork(workerAgentId, out reason);

    /// <summary>Whether this worker's branch may be proposed for verification (denied at the gate).</summary>
    public bool MayRequestVerification(string workerAgentId, out string reason) => MayWork(workerAgentId, out reason);

    /// <summary>
    /// <see cref="IMergeGate"/> — the backstop. A branch whose worker never had a plan approved cannot
    /// merge, whatever its tests said.
    /// </summary>
    public bool Allows(string agentId, out string reason)
    {
        // Workers the gate never held (manual-mode agents, external-PR heads) are not governed by the plan
        // gate at all — this gate answers only for the workers it was asked to hold. Answering "no" for
        // every unknown id would silently block every non-coordinated branch in the queue.
        lock (_gate)
        {
            if (ResolveKeyLocked(agentId) is null)
            {
                reason = string.Empty;
                return true;
            }
        }

        return MayWork(agentId, out reason);
    }

    // ---- backpressure -----------------------------------------------------

    /// <summary>The workers this gate is holding that are still at the plan gate.</summary>
    public IReadOnlyList<string> BlockedWorkerIds()
    {
        var blocked = _plans.BlockedWorkerIds();
        lock (_gate)
        {
            return blocked.Where(id => ResolveKeyLocked(id) is not null).ToList();
        }
    }

    /// <summary>How many workers are currently waiting on a human plan decision.</summary>
    public int BlockedWorkerCount => BlockedWorkerIds().Count;

    /// <summary>How many workers stopped and escalated after spending their revision budget.</summary>
    public int EscalatedWorkerCount
    {
        get
        {
            var escalated = _plans.EscalatedWorkerIds();
            lock (_gate)
            {
                return escalated.Count(id => ResolveKeyLocked(id) is not null);
            }
        }
    }

    /// <summary>
    /// The legible stall (contract §2). When workers are waiting on a human, say so — and say it more
    /// urgently when they are the reason the coordinator has stopped spawning.
    ///
    /// <para>Returns null when there is nothing to say. Never returns a vague string: the whole point is
    /// that a stall which cannot explain itself is indistinguishable from a hang.</para>
    /// </summary>
    /// <param name="activeWorkers">The live worker population counted against the cap.</param>
    /// <param name="maxActiveWorkers">The cap itself (<see cref="CoordinatorLimits.MaxActiveWorkers"/>).</param>
    public string? BackpressureSignal(int activeWorkers, int maxActiveWorkers)
    {
        var blocked = BlockedWorkerCount;
        var escalated = EscalatedWorkerCount;
        if (blocked == 0 && escalated == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (blocked > 0)
        {
            parts.Add($"{blocked} {Workers(blocked)} waiting on your approval");
        }

        if (escalated > 0)
        {
            parts.Add($"{escalated} escalated after {_plans.MaxPlanRevisions} rejected plans");
        }

        var head = string.Join(" · ", parts);
        if (blocked > 0 && activeWorkers >= maxActiveWorkers)
        {
            return $"{head}. The worker cap ({activeWorkers}/{maxActiveWorkers}) is full — " +
                   "the coordinator has stopped spawning until you clear plans.";
        }

        return head + ".";
    }

    private static string Workers(int n) => n == 1 ? "worker is" : "workers are";
}
