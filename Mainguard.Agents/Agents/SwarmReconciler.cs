using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Audit;
using Mainguard.Git.Models;

namespace Mainguard.Agents.Agents;

/// <summary>
/// A live agent container as observed from Docker (the labels <c>mainguard.agent</c>/<c>mainguard.repo</c>
/// set by P2-07). This is the <b>only</b> liveness signal the reconciler consumes — there are no
/// PID/lock-file reads (rejection trigger).
/// </summary>
/// <param name="Running">Docker's <c>State == "running"</c>, verbatim. A <b>paused</b> container does not
/// satisfy this: Docker reports it as <c>"paused"</c>, which is a different word for a container that very
/// much still exists. Use <see cref="Live"/> for "this jail is still here".</param>
/// <param name="Paused">Docker's <c>State == "paused"</c> — the jail is frozen, not gone (ISSUES-LOG #20:
/// the daemon's tracked state has to be able to learn this from Docker, because a <c>docker pause</c>/
/// <c>unpause</c> run outside the app never reaches any RPC).</param>
/// <param name="Kind">The agent CLI this jail runs, from the <c>mainguard.kind</c> label. Empty for a jail
/// created before that label existed — an adopter then has to say so rather than guess.</param>
/// <param name="Role">The orchestration role (<see cref="AgentRoles"/>) from the
/// <c>mainguard.agent.role</c> label: <c>""</c>, <c>coordinator</c> or <c>managed</c>. Distinct from the
/// <c>mainguard.role</c> label, which says what KIND of container this is (<c>agent</c> vs
/// <c>egress-proxy</c>) and is always <c>agent</c> here.</param>
/// <param name="ParentAgentId">The coordinator that spawned this jail, from the <c>mainguard.agent.parent</c>
/// label; empty for an agent nobody spawned or a jail created before the label existed. Without it an
/// adopted worker has no parent, and its coordinator's <c>status</c>/<c>prompt</c>/<c>verify</c> — all
/// scoped to <c>ParentAgentId</c> — answer "no worker" for every worker it owns after a restart.</param>
public sealed record AgentContainerState(
    string AgentId, string RepoHash, string ContainerId, bool Running,
    bool Paused = false, string Kind = "", string Role = "", string ParentAgentId = "")
{
    /// <summary>
    /// The jail still exists and is holding the agent's process tree — <see cref="Running"/> <b>or</b>
    /// <see cref="Paused"/>.
    ///
    /// <para>This is the predicate every reconcile wants, and reading <see cref="Running"/> for it was a
    /// live defect: a kill-switch-frozen or human-paused jail reports <c>State == "paused"</c>, so a daemon
    /// restart while any agent was paused declared it dead, <b>force-removed its worktree</b> and reaped its
    /// PTY — destroying exactly the work an emergency stop exists to preserve.</para>
    /// </summary>
    public bool Live => Running || Paused;
}

/// <summary>What to do with a live container the daemon did not expect (an orphan).</summary>
public enum OrphanPolicy
{
    /// <summary>Adopt it into the expected set (default — a crash-surviving agent keeps working).</summary>
    Adopt,

    /// <summary>Stop it (a stricter posture: only daemon-launched agents may run).</summary>
    Stop,
}

/// <summary>The persistence seam for the expected-agents table — SQLite in the daemon, in-memory in tests.</summary>
public interface IExpectedAgentStore
{
    /// <summary>Every agent the daemon expected to be alive (any disposition).</summary>
    IReadOnlyList<ExpectedAgent> All();

    /// <summary>Record (or refresh) an expected agent as live.</summary>
    void Upsert(string repoHash, string agentId, string disposition);

    /// <summary>Mark an expected agent dead with the disposal reason (surfaced to the UI).</summary>
    void MarkDead(string repoHash, string agentId, string reason);
}

/// <summary>The outcome of one reconcile pass (asserted by tests, surfaced to the UI).</summary>
public sealed record ReconcileReport(
    IReadOnlyList<string> Pruned,
    IReadOnlyList<string> Adopted,
    IReadOnlyList<string> Stopped,
    IReadOnlyList<string>? Removed = null)
{
    /// <summary>Exited containers deleted from the engine (2026-09-04): a jail that is not running is not
    /// adoptable — its tmpfs home is gone — and until this pass nothing ever removed one, so they
    /// accumulated on the engine forever. Empty, never null.</summary>
    public IReadOnlyList<string> RemovedContainers => Removed ?? Array.Empty<string>();
}

/// <summary>
/// P2-08 swarm reconciler. On daemon boot it makes <b>Docker the single source of truth</b> for swarm
/// state: it lists the live <c>mainguard.agent</c> containers and diffs them against the expected-agents
/// table.
/// <list type="bullet">
///   <item>Expected but no live container → prune the worktree (P2-06
///   <c>RemoveAgentWorktree(force:true)</c>) and mark the row <c>Dead</c> with a disposal reason.</item>
///   <item>Live but not expected (orphan, e.g. it survived a daemon crash) → adopt or stop per
///   <see cref="OrphanPolicy"/> (default adopt).</item>
///   <item>Live and kept (adopted, or already expected) → handed to the MG-3 ref watcher
///   (<see cref="TryWatchAgentRef"/>), because the spawn path is the only OTHER thing that ever starts a
///   watch and a survivor never runs it again.</item>
/// </list>
/// It reads <b>no</b> process-id or on-disk liveness state. Deleting any on-disk daemon state and
/// rebooting yields the same outcome, because the truth is Docker's container list.
///
/// <para>Every diff is keyed on <b>both</b> labels — <c>(mainguard.repo, mainguard.agent)</c> — the same
/// identity the live session store and the jail lookup use, since an agent id is unique only inside a
/// repository.</para>
/// </summary>
public sealed class SwarmReconciler
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<AgentContainerState>>> _listContainers;
    private readonly IExpectedAgentStore _expected;
    private readonly IAgentWorktreeManager _worktrees;
    private readonly Func<string, CancellationToken, Task> _stopContainer;
    private readonly Func<string, CancellationToken, Task> _removeContainer;
    private readonly OrphanPolicy _policy;

    /// <param name="listContainers">Lists live <c>mainguard.agent</c> containers from Docker (injected for tests).</param>
    /// <param name="expected">The expected-agents table.</param>
    /// <param name="worktrees">P2-06 worktree manager (dead agents are pruned with force).</param>
    /// <param name="stopContainer">Stops an orphan container by id (used only under <see cref="OrphanPolicy.Stop"/>).</param>
    /// <param name="policy">What to do with orphan live containers (default adopt).</param>
    public SwarmReconciler(
        Func<CancellationToken, Task<IReadOnlyList<AgentContainerState>>> listContainers,
        IExpectedAgentStore expected,
        IAgentWorktreeManager worktrees,
        Func<string, CancellationToken, Task>? stopContainer = null,
        OrphanPolicy policy = OrphanPolicy.Adopt,
        Func<string, CancellationToken, Task>? removeContainer = null)
    {
        // removeContainer deletes an EXITED container by id (the engine's force-remove). A stopped jail is
        // never adoptable — its tmpfs home died with it — and nothing else ever removed one, so every
        // crash, OOM kill and engine restart left a dead container behind for good (2026-09-04).
        _removeContainer = removeContainer ?? ((_, _) => Task.CompletedTask);
        _listContainers = listContainers ?? throw new ArgumentNullException(nameof(listContainers));
        _expected = expected ?? throw new ArgumentNullException(nameof(expected));
        _worktrees = worktrees ?? throw new ArgumentNullException(nameof(worktrees));
        _stopContainer = stopContainer ?? ((_, _) => Task.CompletedTask);
        _policy = policy;
    }

    /// <summary>Runs one reconcile pass against the current Docker state.</summary>
    public async Task<ReconcileReport> ReconcileAsync(CancellationToken ct = default)
    {
        var containers = await _listContainers(ct).ConfigureAwait(false);

        // Keyed by BOTH labels, never by the agent id alone. A bare id like `pr-7` (the external-PR
        // intake names its workers after the pull-request number) is unique only INSIDE a repository,
        // which is why the live session store is keyed the same way. Agent-id-only keying collapsed two
        // repositories' jails into one entry — and `ToDictionary` threw ArgumentException on the
        // duplicate, out of a boot sequence that is fail-fast, so two repos each running a `pr-7` took
        // the whole daemon boot down.
        var live = new Dictionary<(string RepoHash, string AgentId), AgentContainerState>();
        foreach (var container in containers)
        {
            // Live, not Running: a paused jail is present. See AgentContainerState.Live.
            if (container.Live)
            {
                live[(container.RepoHash, container.AgentId)] = container;
            }
        }

        var pruned = new List<string>();
        var adopted = new List<string>();
        var stopped = new List<string>();
        var removed = new List<string>();

        // Exited jails go first: they are unadoptable by construction, and a record that names one as
        // "Live" or "Adopted" would be describing a container that cannot run anything.
        foreach (var container in containers)
        {
            if (container.Live)
            {
                continue;
            }

            try
            {
                await _removeContainer(container.ContainerId, ct).ConfigureAwait(false);
                removed.Add(container.AgentId);
            }
            catch (Exception)
            {
                // Best effort: an exited container the engine will not delete now is the same residue it
                // was before this pass, and the next boot asks again.
            }
        }

        // 1. Expected agents whose container is gone → prune + mark Dead.
        foreach (var agent in _expected.All())
        {
            if (string.Equals(agent.Disposition, "Dead", StringComparison.Ordinal))
            {
                continue; // already accounted for.
            }

            if (!live.ContainsKey((agent.RepoHash, agent.AgentId)))
            {
                TryPruneWorktree(agent.RepoHash, agent.AgentId);
                _expected.MarkDead(agent.RepoHash, agent.AgentId,
                    "Container not running at daemon boot (Docker reported no live jail).");
                pruned.Add(agent.AgentId);
            }
        }

        // 2. Live containers the daemon did not expect → adopt or stop.
        var expectedKeys = new HashSet<(string RepoHash, string AgentId)>(
            _expected.All().Select(a => (a.RepoHash, a.AgentId)));

        foreach (var container in live.Values)
        {
            if (expectedKeys.Contains((container.RepoHash, container.AgentId)))
            {
                // Already accounted for, still running — the steady state after the FIRST restart, since
                // the expected table is SQLite-backed and keeps the row this pass wrote last time. It is
                // no less a live agent for having a row, so it needs the watch just the same.
                TryWatchAgentRef(container);
                continue;
            }

            if (_policy == OrphanPolicy.Adopt)
            {
                _expected.Upsert(container.RepoHash, container.AgentId, "Adopted");
                adopted.Add(container.AgentId);
                TryWatchAgentRef(container);
            }
            else
            {
                await _stopContainer(container.ContainerId, ct).ConfigureAwait(false);
                stopped.Add(container.AgentId);
            }
        }

        return new ReconcileReport(pruned, adopted, stopped, removed);
    }

    /// <summary>
    /// MG-3 — hand a jail that is alive right now to the ref watcher, so its own
    /// <c>refs/heads/agent/&lt;id&gt;</c> keeps being published into the mirror.
    ///
    /// <para><b>Why this belongs on the boot path.</b> The only other caller of
    /// <c>WatchAgentRef</c> is the spawn path (<c>SandboxAgentLauncher.LaunchAsync</c>), which an agent
    /// that survived a daemon restart never runs again — jails are persistent by design and the watcher's
    /// sweep set is in-process, so every survivor came back unwatched for the rest of its life. Nothing
    /// failed: the pre-verification publish (design §7's other half) still catches up whenever a
    /// verification runs, so the symptom was a review cockpit, queue projection and stale cascade sitting
    /// on a tip the agent had already moved past — "the UI is stale / the agent looks idle" rather than an
    /// error. This is the reconcile counterpart of the prune branch's <c>RemoveAgentWorktree</c>:
    /// each disposition finishes its own housekeeping.</para>
    ///
    /// <para>Best-effort, exactly like <see cref="TryPruneWorktree"/> — the boot sequence is fail-fast, so
    /// an exception from registering a sweep entry would turn housekeeping into a daemon that will not
    /// start. That swallow now lives in <see cref="AgentRefWatchRegistration.TryWatch"/>, shared with the
    /// external-PR intake's adopt path, which is the OTHER way the daemon takes over a jail it did not
    /// spawn. <c>Watch</c> is idempotent, so a repeat pass costs nothing.</para>
    /// </summary>
    private void TryWatchAgentRef(AgentContainerState container) =>
        AgentRefWatchRegistration.TryWatch(_worktrees, container.RepoHash, container.AgentId);

    private void TryPruneWorktree(string repoHash, string agentId)
    {
        try
        {
            _worktrees.RemoveAgentWorktree(repoHash, agentId, force: true);
        }
        catch (Exception)
        {
            // Best-effort: a mirror may already be gone. The row is still marked Dead so the UI is honest.
        }
    }
}

/// <summary>One ordered boot step (RT-D1). Runs to completion before the next step starts.</summary>
public interface IBootTask
{
    /// <summary>A short, stable name (for logging/diagnostics).</summary>
    string Name { get; }

    /// <summary>Runs the step.</summary>
    Task RunAsync(CancellationToken ct);
}

/// <summary>
/// The RT-D1 ordered daemon boot sequence. Steps run <b>strictly in order</b>. The
/// <b>merge-reconcile slot is first</b> (master doc §3.1): once P2-10 lands, its journal replay
/// (synthesizing a missing <c>ConfirmMerge</c>) must complete before the swarm reconciler admits new
/// work or admission accepts spawns for a repo with an outstanding merge lease. The slot ships now as
/// a no-op (<see cref="MergeReconcilePlaceholderTask"/>) so the ordering seam exists before P2-10.
/// </summary>
public sealed class DaemonBootSequence
{
    private readonly IReadOnlyList<IBootTask> _tasks;

    public DaemonBootSequence(IReadOnlyList<IBootTask> tasks)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
    }

    /// <summary>The ordered task names (for the RT-D1 ordering assertion).</summary>
    public IReadOnlyList<string> TaskNames => _tasks.Select(t => t.Name).ToArray();

    /// <summary>The tasks themselves, so a composition-root test can assert HOW each was built and not
    /// only that a step with the right name is present.</summary>
    public IReadOnlyList<IBootTask> Tasks => _tasks;

    /// <summary>Runs every task in order; stops on the first failure (boot is fail-fast).</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        foreach (var task in _tasks)
        {
            await task.RunAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the canonical boot order: merge-reconcile (empty until P2-10) → swarm (container)
    /// reconcile → optional P2-09 leader reattach. The reconcile order is containers → leaders → PTY
    /// reattach; the leader step appends only when supplied (admission is passive — it gates spawns, it
    /// has no boot step).
    /// </summary>
    public static DaemonBootSequence Build(
        SwarmReconciler reconciler,
        IBootTask? mergeReconcile = null,
        IBootTask? leaderReattach = null,
        IAuditLog? audit = null,
        Action<string>? log = null)
    {
        var tasks = new List<IBootTask>
        {
            mergeReconcile ?? new MergeReconcilePlaceholderTask(),
            new SwarmReconcileTask(reconciler, audit, log),
        };

        if (leaderReattach is not null)
        {
            tasks.Add(leaderReattach);
        }

        return new DaemonBootSequence(tasks);
    }
}

/// <summary>The RT-D1 merge-reconcile slot. A no-op until P2-10 supplies the journal-replay pass.</summary>
public sealed class MergeReconcilePlaceholderTask : IBootTask
{
    public string Name => "merge-reconcile";

    public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// The boot step that runs the <see cref="SwarmReconciler"/> — <b>and records what it did</b>.
///
/// <para>This used to be <c>=&gt; _reconciler.ReconcileAsync(ct)</c>, narrowing a
/// <see cref="ReconcileReport"/> to a bare <see cref="Task"/>. That report is the only account of a boot
/// pass that declares agents Dead and force-removes their worktrees, adopts orphan jails, or stops them:
/// dropping it meant a user who left three agents running overnight came back to none of them, with no
/// log line, no audit entry and nothing anywhere to explain it. Every destructive outcome now leaves an
/// artifact before the daemon finishes booting.</para>
/// </summary>
public sealed class SwarmReconcileTask : IBootTask
{
    /// <summary>The G-17 audit type for one boot swarm-reconcile pass.</summary>
    public const string ReconciledEvent = "boot_swarm_reconcile";

    /// <summary>
    /// Switches this pass off — the SAME variable the periodic
    /// <c>AgentSessionReconcilerService</c> reads, deliberately one string rather than two.
    ///
    /// <para><b>Why it has to cover this step too.</b> Both passes reconcile daemon state against a
    /// container engine that is <b>machine-wide</b>, and the Mac substrate's mirror root
    /// (<c>~/mainguard</c>) is not governed by <c>MAINGUARD_DATA_ROOT</c> — so an in-proc daemon on an
    /// isolated data root still answers "yes, I host that repository" for a developer's real jails.
    /// <c>Mainguard.Server.Tests</c>' module initializer set this expecting it to hold "for the whole
    /// assembly", but only the periodic pass read it: the boot pass kept adopting live jails, wrote
    /// <c>boot_swarm_reconcile</c> into the test audit log naming the developer's own containers, and
    /// with <see cref="OrphanPolicy"/> free to prune it could as easily have declared them Dead and
    /// force-removed their worktrees. A switch that covers one of two identical exposures is the
    /// control-that-is-not-reached shape, so it now covers both.</para>
    ///
    /// <para>Nothing in production sets it; the RequiresDocker tier drives the reconciler directly
    /// against containers it created itself rather than through boot.</para>
    /// </summary>
    public const string DisableVariable = "MAINGUARD_DISABLE_SESSION_RECONCILE";

    /// <summary>Whether the boot pass is switched off for this process.</summary>
    public static bool Disabled =>
        Environment.GetEnvironmentVariable(DisableVariable) == "1";

    private readonly SwarmReconciler _reconciler;
    private readonly IAuditLog? _audit;
    private readonly Action<string>? _log;

    /// <param name="reconciler">The reconciler this step drives.</param>
    /// <param name="audit">G-17 sink for the pass's outcome. Optional so the many unit tests that drive
    /// this step directly stay unchanged; the daemon passes it.</param>
    /// <param name="log">Milestone sink (the daemon's boot log category).</param>
    public SwarmReconcileTask(SwarmReconciler reconciler, IAuditLog? audit = null, Action<string>? log = null)
    {
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _audit = audit;
        _log = log;
    }

    public string Name => "swarm-reconcile";

    /// <summary>Whether this step will leave a durable record of a destructive pass. The audit sink is an
    /// optional argument, so the daemon failing to pass it restores the silent behaviour exactly with
    /// every test here still green — the composition root asserts on this.</summary>
    public bool RecordsOutcome => _audit is not null;

    /// <summary>The most recent pass's report — null before the first run. Kept so a caller (and the
    /// boot-order test) can assert on the outcome rather than only on the fact that a Task completed.</summary>
    public ReconcileReport? LastReport { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        if (Disabled)
        {
            // Deliberately leaves LastReport null: no pass ran, and an empty report would claim one did
            // and found nothing — which is the thing this step exists to stop being indistinguishable.
            _log?.Invoke($"swarm reconcile disabled by {DisableVariable}");
            return;
        }

        var report = await _reconciler.ReconcileAsync(ct).ConfigureAwait(false);
        LastReport = report;

        _log?.Invoke(
            $"swarm reconcile: pruned={Describe(report.Pruned)} adopted={Describe(report.Adopted)} "
            + $"stopped={Describe(report.Stopped)}");

        // Only when something actually happened: an audit entry per boot on an idle box is noise that
        // buries the passes that mattered. A pass that changed nothing is fully described by the log line.
        if (report.Pruned.Count + report.Adopted.Count + report.Stopped.Count > 0)
        {
            _audit?.Append(new AuditEvent(ReconciledEvent, new Dictionary<string, string>
            {
                ["pruned"] = string.Join(",", report.Pruned),
                ["adopted"] = string.Join(",", report.Adopted),
                ["stopped"] = string.Join(",", report.Stopped),
                ["when"] = DateTimeOffset.UtcNow.ToString(
                    "O", System.Globalization.CultureInfo.InvariantCulture),
            }));
        }
    }

    private static string Describe(IReadOnlyList<string> ids) =>
        ids.Count == 0 ? "none" : string.Join(",", ids);
}

/// <summary>An in-memory <see cref="IExpectedAgentStore"/> for tests and the pre-persistence daemon path.</summary>
public sealed class InMemoryExpectedAgentStore : IExpectedAgentStore
{
    private readonly object _gate = new();
    private readonly List<ExpectedAgent> _rows = new();
    private long _nextId;

    public IReadOnlyList<ExpectedAgent> All()
    {
        lock (_gate)
        {
            return _rows.Select(Clone).ToArray();
        }
    }

    public void Upsert(string repoHash, string agentId, string disposition)
    {
        lock (_gate)
        {
            var existing = Find(repoHash, agentId);
            if (existing is null)
            {
                _rows.Add(new ExpectedAgent
                {
                    Id = ++_nextId,
                    RepoHash = repoHash,
                    AgentId = agentId,
                    Disposition = disposition,
                });
            }
            else
            {
                existing.Disposition = disposition;
                existing.DisposalReason = null;
            }
        }
    }

    public void MarkDead(string repoHash, string agentId, string reason)
    {
        lock (_gate)
        {
            var existing = Find(repoHash, agentId);
            if (existing is null)
            {
                _rows.Add(new ExpectedAgent
                {
                    Id = ++_nextId,
                    RepoHash = repoHash,
                    AgentId = agentId,
                    Disposition = "Dead",
                    DisposalReason = reason,
                });
            }
            else
            {
                existing.Disposition = "Dead";
                existing.DisposalReason = reason;
            }
        }
    }

    private ExpectedAgent? Find(string repoHash, string agentId) =>
        _rows.FirstOrDefault(a =>
            string.Equals(a.RepoHash, repoHash, StringComparison.Ordinal) &&
            string.Equals(a.AgentId, agentId, StringComparison.Ordinal));

    private static ExpectedAgent Clone(ExpectedAgent a) => new()
    {
        Id = a.Id,
        RepoHash = a.RepoHash,
        AgentId = a.AgentId,
        Disposition = a.Disposition,
        DisposalReason = a.DisposalReason,
    };
}
