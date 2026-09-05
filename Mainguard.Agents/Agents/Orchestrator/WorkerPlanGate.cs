using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Git.Audit;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// Whether the worker this gate is holding must have a human-approved plan before it may work.
///
/// <para>Set once, at <see cref="WorkerPlanGate.Hold"/>, from the operator's
/// <see cref="PlanModeSwitch"/>. It is a property of the WORKER, not of the daemon's current setting, so
/// flipping the switch never retroactively authorises a worker already blocked at the gate nor strands a
/// worker that was already told to start.</para>
/// </summary>
public enum WorkerPlanMode
{
    /// <summary>
    /// The phase-2 model (contract §2): the task is withheld, the worker authors a plan against its
    /// brief, presents it and blocks, and a human decides. The default, and what every caller that does
    /// not say otherwise gets.
    /// </summary>
    Gated,

    /// <summary>
    /// Plan mode was OFF when this worker was spawned. It is a coordinator-delegated worker in every
    /// other respect — it is held, it counts against the cap, it is steerable, it auto-verifies, it gets
    /// a queue row — but it receives its task at spawn and no plan is required or accepted.
    ///
    /// <para><b>This is not "the gate is skipped".</b> It is a different, recorded authorisation: the
    /// operator authorised this class of work once, in advance, instead of per worker. The distinction is
    /// carried all the way to the merge record (<see cref="WorkerPlanGate.MergeEvidence"/>), because a
    /// merge whose authorisation was a standing preference must not read like one a human signed.</para>
    /// </summary>
    Ungated,
}

/// <summary>
/// One withheld task as the gate persists it. A public mirror of the gate's private record, because the
/// gate's whole claim — "the DAEMON holds the task" — was only true until the daemon restarted: the
/// dictionary behind it was memory-only while the plan store beside it was a file, so a restart with jails
/// alive left every held worker unheld. Unheld means <see cref="WorkerPlanGate.Allows"/> answers yes (the
/// merge backstop opens for an unapproved branch), <see cref="WorkerPlanGate.TryReleaseTask"/> answers
/// no (an approved worker never receives its task), and the merge record says "not a plan-gated worker"
/// about a worker that was. The record carries exactly what <c>Hold</c> was told plus the release latch.
/// </summary>
public sealed record HeldTaskRecord(
    string RepoHash,
    string AgentId,
    string CoordinatorId,
    string Title,
    string TaskPrompt,
    decimal BudgetUsd,
    bool Released,
    WorkerPlanMode Mode);

/// <summary>The persistence seam for the gate's held tasks (daemon-side, restart-safe).</summary>
public interface IHeldTaskStore
{
    /// <summary>Every persisted held task (rehydrated by the gate's constructor).</summary>
    IReadOnlyList<HeldTaskRecord> LoadAll();

    /// <summary>Upsert one held task, keyed by (repo, agent).</summary>
    void Save(HeldTaskRecord record);

    /// <summary>Drop one held task (the worker was stopped, or its spawn failed).</summary>
    void Remove(string repoHash, string agentId);
}

/// <summary>An <see cref="IHeldTaskStore"/> that forgets on restart — the default for the pure paths.</summary>
public sealed class InMemoryHeldTaskStore : IHeldTaskStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string, string), HeldTaskRecord> _records = new();

    public IReadOnlyList<HeldTaskRecord> LoadAll()
    {
        lock (_gate) { return _records.Values.ToList(); }
    }

    public void Save(HeldTaskRecord record)
    {
        lock (_gate) { _records[(record.RepoHash, record.AgentId)] = record; }
    }

    public void Remove(string repoHash, string agentId)
    {
        lock (_gate) { _records.Remove((repoHash, agentId)); }
    }
}

/// <summary>
/// A JSON-file <see cref="IHeldTaskStore"/>, written beside <see cref="JsonPlanApprovalStore"/> with the
/// same write-rename discipline. Reads fail CLOSED in the only direction that is safe here: an unreadable
/// file rehydrates as <i>nothing held</i>, which is the pre-existing restart behaviour, never as a task
/// handed to a worker whose approval cannot be established.
/// </summary>
public sealed class JsonHeldTaskStore : IHeldTaskStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public JsonHeldTaskStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public IReadOnlyList<HeldTaskRecord> LoadAll()
    {
        lock (_gate)
        {
            return LoadDtosLocked().Select(FromDto).ToList();
        }
    }

    public void Save(HeldTaskRecord record)
    {
        lock (_gate)
        {
            var dtos = LoadDtosLocked();
            dtos.RemoveAll(d => d.RepoHash == record.RepoHash && d.AgentId == record.AgentId);
            dtos.Add(ToDto(record));
            WriteLocked(dtos);
        }
    }

    public void Remove(string repoHash, string agentId)
    {
        lock (_gate)
        {
            var dtos = LoadDtosLocked();
            if (dtos.RemoveAll(d => d.RepoHash == repoHash && d.AgentId == agentId) > 0)
            {
                WriteLocked(dtos);
            }
        }
    }

    private void WriteLocked(List<HeldTaskDto> dtos)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            System.IO.Directory.CreateDirectory(dir);
        }

        var tmp = _path + ".tmp";
        System.IO.File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(dtos));
        System.IO.File.Move(tmp, _path, overwrite: true);
    }

    private List<HeldTaskDto> LoadDtosLocked()
    {
        if (!System.IO.File.Exists(_path))
        {
            return new();
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<HeldTaskDto>>(System.IO.File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or System.IO.IOException)
        {
            return new();
        }
    }

    private static HeldTaskDto ToDto(HeldTaskRecord r) => new()
    {
        RepoHash = r.RepoHash,
        AgentId = r.AgentId,
        CoordinatorId = r.CoordinatorId,
        Title = r.Title,
        TaskPrompt = r.TaskPrompt,
        BudgetUsd = r.BudgetUsd,
        Released = r.Released,
        // Written by name, never as an int, so a hand-read store file says what it means — and an
        // unparseable value rehydrates as Gated, the fail-closed direction.
        Mode = r.Mode.ToString(),
    };

    private static HeldTaskRecord FromDto(HeldTaskDto d) => new(
        d.RepoHash ?? string.Empty,
        d.AgentId ?? string.Empty,
        d.CoordinatorId ?? string.Empty,
        d.Title ?? string.Empty,
        d.TaskPrompt ?? string.Empty,
        d.BudgetUsd,
        d.Released,
        Enum.TryParse<WorkerPlanMode>(d.Mode, out var mode) ? mode : WorkerPlanMode.Gated);

    private sealed class HeldTaskDto
    {
        public string? RepoHash { get; set; }
        public string? AgentId { get; set; }
        public string? CoordinatorId { get; set; }
        public string? Title { get; set; }
        public string? TaskPrompt { get; set; }
        public decimal BudgetUsd { get; set; }
        public bool Released { get; set; }
        public string? Mode { get; set; }
    }
}

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
/// <para><b>The plan-mode toggle (2026-08-30).</b> Whether a delegated worker is actually withheld from
/// is now the operator's <see cref="PlanModeSwitch"/>, read ONCE per spawn into the worker's own
/// <see cref="WorkerPlanMode"/>. Off does not remove this gate from any path: the worker is still held
/// here, still counted, still asked about by the merge queue and the readiness trigger, and its mode is
/// still on its merge record. What changes is the one thing the switch names — the task is not withheld,
/// so <see cref="MayWork"/> answers yes from the start and every predicate that delegates to it follows.
/// Implementing "off" as "don't call <see cref="Hold"/>" was the tempting alternative and is the one the
/// codebase already calls out as strictly worse (<c>AgentSpawnService.SpawnWorkerAsync</c>): an unheld
/// worker is invisible to <see cref="MayAutoVerify"/>, gets the manual-agent wording on its merge record,
/// and has no recorded authorisation at all.</para>
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
    private readonly IHeldTaskStore _store;
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

    /// <param name="store">
    /// Where held tasks outlive the process. Defaults to memory for the pure paths; the daemon passes a
    /// <see cref="JsonHeldTaskStore"/> beside the plan store, because a gate that forgets what it is
    /// holding on restart is a gate only until the first daemon update.
    /// </param>
    public WorkerPlanGate(PlanApprovalService plans, IAuditLog? audit = null, IHeldTaskStore? store = null)
    {
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _audit = audit ?? new InMemoryAuditLog();
        _store = store ?? new InMemoryHeldTaskStore();

        // Restart resume: every task the previous daemon was holding, released latch included. No audit
        // event here — nothing was withheld or released by this rehydration, and a second
        // `worker_task_withheld` per restart would inflate how many workers were ever gated.
        foreach (var record in _store.LoadAll())
        {
            if (string.IsNullOrWhiteSpace(record.AgentId))
            {
                continue;
            }

            _held[(record.RepoHash ?? string.Empty, record.AgentId)] = new HeldTask(
                record.RepoHash ?? string.Empty, record.CoordinatorId ?? string.Empty, record.Title ?? string.Empty,
                record.TaskPrompt ?? string.Empty, record.BudgetUsd, record.Released, record.Mode);
        }
    }

    private static HeldTaskRecord ToRecord((string RepoHash, string AgentId) key, HeldTask task) => new(
        key.RepoHash, key.AgentId, task.CoordinatorId, task.Title, task.TaskPrompt, task.BudgetUsd,
        task.Released, task.Mode);

    /// <summary>A task prompt withheld from a worker until its plan is approved.</summary>
    /// <param name="Mode">
    /// Whether this worker's task is actually withheld. <see cref="WorkerPlanMode.Ungated"/> means plan
    /// mode was off when it was spawned, so the same record is kept — the worker is still delegated,
    /// still counted, still merge-gate-visible — and only the withholding is not applied.
    /// </param>
    private sealed record HeldTask(
        string RepoHash, string CoordinatorId, string Title, string TaskPrompt, decimal BudgetUsd, bool Released,
        WorkerPlanMode Mode);

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
    /// The longest a brief may be. It is a headline on a human's plan-approval card, and a card whose
    /// headline is a paragraph is a card nobody reads — which is the decision the gate exists to make
    /// possible. Deliberately generous (a long pull-request title fits) so this refuses the class of
    /// caller that pasted the task in, not the one that wrote a wordy title.
    /// </summary>
    public const int MaxBriefLength = 120;

    /// <summary>
    /// Why this (title, task) pair may not be held — or <c>null</c> when it may. The reasons are written
    /// for the <b>coordinator</b> to read verbatim and retry correctly on its next turn, because that is
    /// who receives them: <c>AgentSpawnService</c> returns this text as the <c>spawn</c> refusal.
    ///
    /// <para><b>Why refuse rather than derive (contract §3 change, 2026-08-29).</b> The defect this
    /// closes was a derivation: the shim sent no title, the daemon filled the hole with
    /// <c>Title ?? TaskPrompt</c>, and <see cref="PlanningBriefFor"/> therefore handed every worker its
    /// task verbatim — the documented "never the task itself" made false by a fallback, with nothing
    /// failing anywhere. Any replacement fallback recreates that. Truncating the task would be worse
    /// still: it would look like a title while still leaking the work. So a spawn without a real brief
    /// does not happen, and the coordinator is told exactly what to send instead.</para>
    ///
    /// <para><b>What the equality check is and is not.</b> It is a tripwire for exactly the defect above
    /// — a caller passing one string as both — not a semantic guarantee that a title paraphrasing its
    /// task is caught. Stated plainly, because a check described as more than it is becomes the reason
    /// nobody looks again.</para>
    /// </summary>
    public static string? RefuseBrief(string? title, string? taskPrompt)
    {
        var usage = "Spawn again as: " + Mainguard.Agents.Agents.Ipc.AgentSpawnShim.SpawnUsage;

        if (string.IsNullOrWhiteSpace(taskPrompt))
        {
            return "a task is required — that is the work the worker is spawned to do. " + usage;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return "a title is required: it is the BRIEF the worker plans against before its task is "
                 + "released, and the headline you put in front of the human. " + usage;
        }

        if (title.Length > MaxBriefLength)
        {
            return "the title is a one-line headline on a human's approval card, not the task — keep it "
                 + $"under {MaxBriefLength} characters ({title.Length} given). " + usage;
        }

        if (title.AsSpan().IndexOfAny('\n', '\r') >= 0)
        {
            return "the title is a single line. " + usage;
        }

        if (string.Equals(title.Trim(), taskPrompt.Trim(), StringComparison.Ordinal))
        {
            return "the title must not be the task: the worker is given the title up front and the task "
                 + "only after a human approves its plan, so a title that repeats the task hands over the "
                 + "work the gate is there to withhold. Write a short headline instead. " + usage;
        }

        return null;
    }

    /// <summary>
    /// Records the work a worker was spawned for <b>without giving it to the worker</b>. Called on the
    /// spawn path; idempotent per worker id.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The brief is missing, over-long, multi-line, or is the task itself (<see cref="RefuseBrief"/>) —
    /// or the worker id is blank. Thrown rather than stored, because a held task with no real brief is
    /// the defect, not a degraded case of it.
    /// </exception>
    /// <param name="mode">
    /// Whether this worker's task is actually withheld — the operator's <see cref="PlanModeSwitch"/>,
    /// read ONCE here and then owned by this worker. It defaults to <see cref="WorkerPlanMode.Gated"/>
    /// so that a caller which has not been taught about the switch keeps the gate, rather than a caller
    /// which forgot to pass it silently producing an unauthorised worker.
    /// </param>
    public void Hold(
        string workerAgentId, string coordinatorId, string title, string taskPrompt, decimal budgetUsd,
        string repoHash = "", WorkerPlanMode mode = WorkerPlanMode.Gated)
    {
        if (string.IsNullOrWhiteSpace(workerAgentId))
        {
            throw new ArgumentException("workerAgentId is required.", nameof(workerAgentId));
        }

        // The brief/task separation is checked HERE, at the one object that stores both strings and is
        // the sole source of PlanningBriefFor. A caller-side check alone would be the second copy of a
        // policy that this codebase's standing lesson (MG-12) says goes decorative.
        if (RefuseBrief(title, taskPrompt) is { } refusal)
        {
            throw new ArgumentException(refusal, nameof(title));
        }

        var key = (repoHash ?? string.Empty, workerAgentId);
        lock (_gate)
        {
            if (_held.ContainsKey(key))
            {
                return;
            }

            var task = new HeldTask(
                repoHash ?? string.Empty, coordinatorId ?? "", title ?? "", taskPrompt ?? "", budgetUsd,
                Released: false, Mode: mode);
            _held[key] = task;
            _store.Save(ToRecord(key, task));
        }

        // One event, with the mode ON it, rather than two event names. A reader counting "how many
        // workers were spawned under a plan requirement" must not have to know that a second name exists
        // — the shape that hides a mode is how a missing case becomes an unnoticed one.
        _audit.Append(new AuditEvent("worker_task_withheld", new Dictionary<string, string>
        {
            ["worker_agent_id"] = workerAgentId,
            ["coordinator_id"] = coordinatorId ?? "",
            ["repo_hash"] = repoHash ?? string.Empty,
            ["plan_mode"] = mode == WorkerPlanMode.Gated ? "on" : "off",
        }));
    }

    /// <summary>
    /// The mode this worker was held under, or <c>null</c> when this gate does not hold it (a manual-mode
    /// agent, an external-PR head, or an id it has already forgotten).
    /// </summary>
    public WorkerPlanMode? ModeFor(string workerAgentId)
    {
        lock (_gate)
        {
            return FindLocked(workerAgentId)?.Mode;
        }
    }

    /// <summary>
    /// True when this gate holds the worker AND it was spawned with plan mode off.
    ///
    /// <para>Deliberately false for a worker this gate never held: "we are not withholding anything from
    /// it" and "plan mode was off for it" are different facts, and only the second one authorises the
    /// daemon to hand over a task it is holding.</para>
    /// </summary>
    public bool IsUngated(string workerAgentId) => ModeFor(workerAgentId) == WorkerPlanMode.Ungated;

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

        // MayWork, not a second reading of HasApprovedPlan. The two used to be independent spellings of
        // the same policy, which is how one of them goes decorative (MG-12) — and the plan-mode switch is
        // exactly the change that would have split them: an ungated worker has no approved plan and must
        // still be given its task. There is one authority now, and it is the one every other entry point
        // here already delegates to.
        if (!MayWork(workerAgentId, out _))
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
                var released = task with { Released = true };
                _held[key] = released;
                // Persisted with the latch: a restart between the release and the next call must not
                // re-announce, re-audit or re-deliver a task the previous daemon already handed over.
                _store.Save(ToRecord(key, released));
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
                _store.Remove(key.RepoHash, key.AgentId);
            }
        }
    }

    /// <summary>
    /// May this worker do the work it was spawned for? True only with an approved plan. The reason is
    /// written for a human to read verbatim.
    /// </summary>
    public bool MayWork(string workerAgentId, out string reason)
    {
        // Plan mode was off when this worker was spawned, so there is no plan to wait for and never was.
        // Placed FIRST and answered from the worker's own recorded mode rather than from the current
        // setting: this must stay true for the whole life of a worker that was told to start, even if the
        // operator turns plan mode back on a second later.
        if (IsUngated(workerAgentId))
        {
            reason = string.Empty;
            return true;
        }

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

    /// <summary>
    /// Why this worker may not present a plan — or <c>null</c> when it may. Non-null only for a worker
    /// spawned while plan mode was off.
    ///
    /// <para><b>Refused rather than accepted-and-ignored.</b> An ungated worker that presents a plan is a
    /// worker following stale instructions, and the two silent alternatives are both worse than a
    /// refusal: accepting it queues a card in front of a human who has switched approvals off and is not
    /// watching for one, and the worker then <c>await</c>s a decision that will never come — it would
    /// block forever holding a jail, having already been given its task. The text is written for the
    /// worker to read and act on in one turn.</para>
    /// </summary>
    public string? RefusePlanPresentation(string workerAgentId) =>
        IsUngated(workerAgentId)
            ? "plan mode is off for this worker, so no plan is required and none will be reviewed — "
              + "nobody is waiting to approve one. You already have your task: ask for it again if you "
              + "need it, then do the work and commit it."
            : null;

    /// <summary>Whether the coordinator may steer this worker (denied while it is held at the gate).</summary>
    public bool MayReceivePrompt(string workerAgentId, out string reason) => MayWork(workerAgentId, out reason);

    /// <summary>Whether this worker's branch may be proposed for verification (denied at the gate).</summary>
    public bool MayRequestVerification(string workerAgentId, out string reason) => MayWork(workerAgentId, out reason);

    /// <summary>
    /// Whether the daemon may verify this worker's branch <b>on its own initiative</b>, with nobody having
    /// asked — the predicate <c>WorkerReadinessTrigger</c> consults before it fires.
    ///
    /// <para>It is deliberately STRICTER than <see cref="MayRequestVerification"/> in one direction and it
    /// is not <see cref="Allows"/>. <see cref="Allows"/> answers "true" for a worker this gate never held,
    /// because a manual-mode agent or an external-PR head is not governed by the plan gate and must not be
    /// blocked by it. That default is right for a merge gate and wrong here: an automatic trigger reading it
    /// would begin spending test-suite runs on every agent in the daemon, including the ones whose owner is
    /// a human driving them by hand. So an unheld worker is <i>ineligible</i> rather than permitted — the
    /// human Verify button is what those entries have always used, and it is untouched.</para>
    ///
    /// <para>In the other direction it is exactly <see cref="MayWork"/>, and it must stay exactly that: a
    /// second opinion about what "approved" means is how one of the two copies becomes decorative (MG-12).
    /// A worker whose plan was never approved never auto-verifies, for the same reason it never received
    /// its task — and a worker spawned with plan mode OFF auto-verifies from the start, for the same
    /// reason it did receive it. The heldness check above is what keeps that from leaking to manual-mode
    /// agents: they are not held at all, so "plan mode is off" never becomes "everything auto-verifies".</para>
    /// </summary>
    /// <param name="reason">Render/log-verbatim explanation when this returns false.</param>
    public bool MayAutoVerify(string workerAgentId, out string reason)
    {
        lock (_gate)
        {
            // Resolved unique-or-nothing, like every other bare-id entry point here: an id two repos both
            // hold is ambiguous, and an ambiguous id must not license the daemon to start a test run on
            // its own initiative against whichever repo the dictionary happened to enumerate first.
            if (FindLocked(workerAgentId) is null)
            {
                reason = $"{workerAgentId} is not a plan-gated worker — automatic verification governs "
                         + "coordinator-delegated workers only.";
                return false;
            }
        }

        return MayWork(workerAgentId, out reason);
    }

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

    /// <summary>
    /// What this gate had established about the branch at merge time (see
    /// <see cref="IMergeGate.MergeEvidence"/>). It says whether the merged work was governed by an
    /// approved plan at all: a manual-mode agent and a coordinator worker whose plan a human approved are
    /// the same <c>Allows == true</c>, and only one of them has a plan behind it.
    ///
    /// <para><b>K5, the half of it that is about merge identity.</b> "plan approved" named no plan, so the
    /// merge record said a decision had been made and gave no way to find WHICH decision — and a worker
    /// can present, revise and re-present plans, so "some plan was approved for this worker" is not a
    /// reference to anything. The plan id is now carried, which is the only thing that makes the line
    /// answerable later; the plan's own title comes with it because an id is not readable and the
    /// investigating human is reading, not querying.</para>
    ///
    /// <para>What this deliberately does NOT do is tie the plan to the code: an approved <c>TaskPlan</c>
    /// still carries no repo sha, no branch sha and no diff hash, so this line still cannot say the merged
    /// bytes are inside the approved scope. That is real and it is left alone here on purpose — a
    /// concurrent change owns re-scoping — and it is recorded in §23 with the design it needs.</para>
    /// </summary>
    public string? MergeEvidence(string agentId)
    {
        WorkerPlanMode mode;
        lock (_gate)
        {
            if (FindLocked(agentId) is not { } held)
            {
                return "plan gate: not a plan-gated worker";
            }

            mode = held.Mode;
        }

        // THREE outcomes, not two. A worker spawned while plan mode was off satisfies every predicate
        // here and has no plan behind it, so collapsing it into "plan approved" would put a sentence on
        // the merge record asserting a human decision that never happened — the single worst thing this
        // record could say, and the reason the mode is carried this far at all. Nor is it "not a
        // plan-gated worker": that is the manual-agent/external-PR wording, and a coordinator-delegated
        // worker is neither of those.
        if (mode == WorkerPlanMode.Ungated)
        {
            return "plan gate: OFF at spawn — delegated worker, no plan was authored or approved";
        }

        if (!MayWork(agentId, out var reason))
        {
            return $"plan gate: NOT satisfied — {reason}";
        }

        var plan = _plans.LatestForWorker(agentId);
        return plan is null
            // MayWork said yes and the plan cannot be named. Said as its own sentence rather than
            // collapsed into the ordinary one: an audit line that claims an approval it cannot identify is
            // the fabrication this whole lane exists to remove.
            ? "plan gate: plan approved (the approved plan could not be identified)"
            : $"plan gate: plan approved — {plan.PlanId} '{plan.Plan.Title}'";
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
    public string? BackpressureSignal(int activeWorkers, int maxActiveWorkers) =>
        BackpressureSignal(BlockedWorkerCount, EscalatedWorkerCount, activeWorkers, maxActiveWorkers);

    /// <summary>
    /// The same sentence, written over counts the CALLER already established.
    ///
    /// <para>It exists so a surface that renders a list of blocked/escalated workers can state the numbers
    /// it actually rendered, in this type's wording, instead of re-asking for a second set that was
    /// computed from a different population. That divergence is not hypothetical: the gate forgets a
    /// worker's held task the moment its session is torn down, so the counts above drop to zero while the
    /// worker's plan record — and therefore its card — is still there. The banner then said one thing and
    /// the cards said another, at the same instant, about the same worker.</para>
    ///
    /// <para>The wording stays here rather than being duplicated at the call site for the standing reason
    /// (MG-12): a second copy of a sentence is a copy that goes stale.</para>
    /// </summary>
    public string? BackpressureSignal(int blocked, int escalated, int activeWorkers, int maxActiveWorkers)
    {
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
