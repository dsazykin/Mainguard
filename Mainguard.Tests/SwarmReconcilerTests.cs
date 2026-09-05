using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Git.Audit;
using Mainguard.Git.Models;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-08 test contract #7/#8 — the boot swarm reconciler. Docker is the sole source of truth: dead
/// containers are pruned + marked Dead, orphan live containers adopt-or-stop per policy, and deleting
/// the on-disk expected table yields an identical outcome. No PID/lock-file reads anywhere.
/// </summary>
public class SwarmReconcilerTests
{
    private sealed class FakeWorktreeManager : IAgentWorktreeManager
    {
        public List<(string Repo, string Agent, bool Force)> Removed { get; } = new();

        /// <summary>Every <c>(repo, agent)</c> handed to the MG-3 ref watcher, in call order — the seam
        /// <c>SandboxAgentLauncher</c> uses at spawn, so watching it here asserts the real wiring rather
        /// than a reconciler-only bookkeeping list.</summary>
        public List<(string Repo, string Agent)> Watched { get; } = new();

        public string CreateAgentWorktree(string repoHash, string agentId) => $"/wt/{repoHash}/{agentId}";

        public void RemoveAgentWorktree(string repoHash, string agentId, bool force) =>
            Removed.Add((repoHash, agentId, force));

        public void Prune(string repoHash) { }

        public IReadOnlyList<WorktreeItem> List(string repoHash) => Array.Empty<WorktreeItem>();

        public void WatchAgentRef(string repoHash, string agentId) => Watched.Add((repoHash, agentId));
    }

    private static Func<CancellationToken, Task<IReadOnlyList<AgentContainerState>>> Docker(
        params AgentContainerState[] containers) => _ => Task.FromResult<IReadOnlyList<AgentContainerState>>(containers);

    private static AgentContainerState Live(string agentId, string repo = "repo1") =>
        new(agentId, repo, $"cid-{agentId}", Running: true);

    /// <summary>
    /// The boot step must leave an ARTIFACT of what it did. It used to be
    /// <c>=&gt; _reconciler.ReconcileAsync(ct)</c>, narrowing the <see cref="ReconcileReport"/> to a bare
    /// <see cref="Task"/> — so a pass that declared agents Dead and force-removed their worktrees produced
    /// no log line, no audit entry and no UI notice at all. A user who left three agents running overnight
    /// came back to none of them and to nothing that explained it.
    /// </summary>
    [Fact]
    public async Task BootStep_RecordsWhatItPruned_RatherThanDiscardingTheReport()
    {
        var expected = new InMemoryExpectedAgentStore();
        expected.Upsert("repo1", "alive", "Live");
        expected.Upsert("repo1", "overnight-1", "Live");
        expected.Upsert("repo1", "overnight-2", "Live");
        var worktrees = new FakeWorktreeManager();
        var audit = new InMemoryAuditLog();
        var lines = new List<string>();

        var task = new SwarmReconcileTask(
            new SwarmReconciler(Docker(Live("alive")), expected, worktrees),
            audit, lines.Add);

        await task.RunAsync(CancellationToken.None);

        // The report is kept, not dropped on the floor.
        Assert.NotNull(task.LastReport);
        Assert.Equal(new[] { "overnight-1", "overnight-2" }, task.LastReport!.Pruned);

        // A durable record naming exactly which agents were destroyed.
        var entry = Assert.Single(audit.Read(), e => e.Type == SwarmReconcileTask.ReconciledEvent);
        Assert.Equal("overnight-1,overnight-2", entry.Fields["pruned"]);

        // ...and a log line, which is the artifact a human actually goes looking at first.
        Assert.Contains(lines, l => l.Contains("overnight-1", StringComparison.Ordinal));
    }

    /// <summary>A pass that changed nothing is fully described by its log line — an audit entry per boot
    /// on an idle box would bury the passes that destroyed something.</summary>
    [Fact]
    public async Task BootStep_ThatChangedNothing_LogsButDoesNotAudit()
    {
        var expected = new InMemoryExpectedAgentStore();
        expected.Upsert("repo1", "alive", "Live");
        var audit = new InMemoryAuditLog();
        var lines = new List<string>();

        var task = new SwarmReconcileTask(
            new SwarmReconciler(Docker(Live("alive")), expected, new FakeWorktreeManager()),
            audit, lines.Add);

        await task.RunAsync(CancellationToken.None);

        Assert.DoesNotContain(audit.Read(), e => e.Type == SwarmReconcileTask.ReconciledEvent);
        Assert.Contains(lines, l => l.Contains("swarm reconcile", StringComparison.Ordinal));
    }

    /// <summary>
    /// The boot pass must honour the same switch the periodic session reconciler does.
    ///
    /// <para>It did not, and the exposure was real rather than theoretical: the container engine is
    /// machine-wide, so an in-proc test daemon on an isolated data root still sees a developer's live
    /// jails. <c>Mainguard.Server.Tests</c>' module initializer set the variable expecting it to hold for
    /// the whole assembly; the boot pass ignored it, adopted two of the developer's real containers and
    /// wrote them into the test audit log by name. The pruning direction of the same pass force-removes
    /// worktrees, so this is what stood between a test run and someone's overnight work.</para>
    ///
    /// <para>The setup deliberately has agents to prune: a disabled pass that merely found nothing would
    /// pass a weaker assertion, so the test only holds if the pass genuinely did not run.</para>
    /// </summary>
    [Fact]
    public async Task BootStep_HonoursTheDisableSwitch_AndDoesNotTouchTheEngine()
    {
        var expected = new InMemoryExpectedAgentStore();
        expected.Upsert("repo1", "someones-live-agent", "Live");
        var worktrees = new FakeWorktreeManager();
        var audit = new InMemoryAuditLog();
        var lines = new List<string>();

        var task = new SwarmReconcileTask(
            new SwarmReconciler(
                _ => throw new InvalidOperationException(
                    "the disabled boot pass reached the container engine"),
                expected,
                worktrees),
            audit,
            lines.Add);

        var previous = Environment.GetEnvironmentVariable(SwarmReconcileTask.DisableVariable);
        Environment.SetEnvironmentVariable(SwarmReconcileTask.DisableVariable, "1");
        try
        {
            await task.RunAsync(CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SwarmReconcileTask.DisableVariable, previous);
        }

        Assert.Null(task.LastReport);
        Assert.DoesNotContain(audit.Read(), e => e.Type == SwarmReconcileTask.ReconciledEvent);
        Assert.Contains(lines, l => l.Contains("disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeadContainer_IsPrunedAndMarkedDead_LiveAgentsRetained()
    {
        var expected = new InMemoryExpectedAgentStore();
        expected.Upsert("repo1", "a1", "Live");
        expected.Upsert("repo1", "a2", "Live");
        expected.Upsert("repo1", "dead", "Live");
        var worktrees = new FakeWorktreeManager();

        var reconciler = new SwarmReconciler(
            Docker(Live("a1"), Live("a2")), expected, worktrees);

        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "dead" }, report.Pruned);
        Assert.Contains(("repo1", "dead", true), worktrees.Removed); // pruned with force
        Assert.DoesNotContain(worktrees.Removed, r => r.Agent == "a1");

        var dead = expected.All().Single(a => a.AgentId == "dead");
        Assert.Equal("Dead", dead.Disposition);
        Assert.False(string.IsNullOrWhiteSpace(dead.DisposalReason));
        Assert.All(new[] { "a1", "a2" }, id =>
            Assert.NotEqual("Dead", expected.All().Single(a => a.AgentId == id).Disposition));
    }

    [Fact]
    public async Task OrphanLiveContainer_IsAdopted_UnderDefaultPolicy()
    {
        var expected = new InMemoryExpectedAgentStore();
        var reconciler = new SwarmReconciler(
            Docker(Live("orphan")), expected, new FakeWorktreeManager(), policy: OrphanPolicy.Adopt);

        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "orphan" }, report.Adopted);
        Assert.Empty(report.Stopped);
        Assert.Equal("Adopted", expected.All().Single(a => a.AgentId == "orphan").Disposition);
    }

    [Fact]
    public async Task OrphanLiveContainer_IsStopped_UnderStopPolicy()
    {
        var expected = new InMemoryExpectedAgentStore();
        var stopped = new List<string>();

        var reconciler = new SwarmReconciler(
            Docker(Live("orphan")), expected, new FakeWorktreeManager(),
            stopContainer: (id, _) => { stopped.Add(id); return Task.CompletedTask; },
            policy: OrphanPolicy.Stop);

        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "orphan" }, report.Stopped);
        Assert.Contains("cid-orphan", stopped);
        Assert.Empty(report.Adopted);
    }

    [Fact]
    public async Task RebootWith3Live1Dead_Adopts3Prunes1()
    {
        // The daemon's expected table survived only the (now dead) agent; Docker shows 3 live jails.
        var expected = new InMemoryExpectedAgentStore();
        expected.Upsert("repo1", "dead", "Live");
        var worktrees = new FakeWorktreeManager();

        var reconciler = new SwarmReconciler(
            Docker(Live("a1"), Live("a2"), Live("a3")), expected, worktrees);

        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "dead" }, report.Pruned);
        Assert.Equal(new[] { "a1", "a2", "a3" }, report.Adopted.OrderBy(x => x).ToArray());
        Assert.Contains(("repo1", "dead", true), worktrees.Removed);
    }

    [Fact]
    public async Task ShouldTrustDockerOnly_DeletingExpectedState_YieldsIdenticalOutcome()
    {
        // Reboot with the on-disk expected table wiped: the outcome is driven purely by Docker.
        var wiped = new InMemoryExpectedAgentStore();
        var reconciler = new SwarmReconciler(
            Docker(Live("a1"), Live("a2"), Live("a3")), wiped, new FakeWorktreeManager());

        var report = await reconciler.ReconcileAsync();

        // Every live container is adopted; nothing is pruned because Docker is the truth.
        Assert.Empty(report.Pruned);
        Assert.Equal(new[] { "a1", "a2", "a3" }, report.Adopted.OrderBy(x => x).ToArray());
        Assert.Equal(3, wiped.All().Count);
        Assert.All(wiped.All(), a => Assert.Equal("Adopted", a.Disposition));
    }

    /// <summary>
    /// MG-3 — an agent that survives a daemon restart must be handed to the ref watcher by the boot
    /// reconcile, because the ONLY other place <c>WatchAgentRef</c> is called is
    /// <c>SandboxAgentLauncher.LaunchAsync</c>, which a survivor never runs again. Without this, every
    /// agent alive across a restart spends the rest of its life unwatched: its own
    /// <c>refs/heads/agent/&lt;id&gt;</c> moves and nothing publishes it into the mirror, so the review
    /// cockpit, the queue projection and the stale cascade see the old tip until some verification
    /// happens to re-fetch. That failure presents as "the UI is stale / the agent looks idle", never as
    /// an error — which is why it needs an assertion rather than a log line.
    /// </summary>
    [Fact]
    public async Task AdoptedOrphan_IsHandedToTheRefWatcher()
    {
        var worktrees = new FakeWorktreeManager();
        var reconciler = new SwarmReconciler(
            Docker(Live("orphan")), new InMemoryExpectedAgentStore(), worktrees, policy: OrphanPolicy.Adopt);

        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "orphan" }, report.Adopted);
        Assert.Equal(new[] { ("repo1", "orphan") }, worktrees.Watched);
    }

    /// <summary>
    /// The same guarantee for the case the adopt branch does NOT cover, which is the steady state: the
    /// expected-agents table is SQLite-backed, so the second restart of a long-lived agent finds a row
    /// already there (the first restart's <c>Upsert(..., "Adopted")</c>) and takes the
    /// already-expected path. A live jail is a live agent whichever branch it arrives on, so watching
    /// only the newly-adopted ones would fix the first restart and nothing after it.
    /// </summary>
    [Fact]
    public async Task LiveContainerAlreadyInTheExpectedTable_IsStillHandedToTheRefWatcher()
    {
        var expected = new InMemoryExpectedAgentStore();
        expected.Upsert("repo1", "survivor", "Adopted");
        var worktrees = new FakeWorktreeManager();

        var reconciler = new SwarmReconciler(Docker(Live("survivor")), expected, worktrees);

        var report = await reconciler.ReconcileAsync();

        Assert.Empty(report.Pruned);   // its container is up; nothing to prune
        Assert.Empty(report.Adopted);  // already expected — not a new adoption
        Assert.Equal(new[] { ("repo1", "survivor") }, worktrees.Watched);
    }

    /// <summary>An orphan the stricter posture STOPS is not watched — watching a jail we just killed
    /// would leave a sweep entry publishing from a repository about to be pruned.</summary>
    [Fact]
    public async Task StoppedOrphan_IsNotWatched()
    {
        var worktrees = new FakeWorktreeManager();
        var reconciler = new SwarmReconciler(
            Docker(Live("orphan")), new InMemoryExpectedAgentStore(), worktrees,
            stopContainer: (_, _) => Task.CompletedTask,
            policy: OrphanPolicy.Stop);

        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "orphan" }, report.Stopped);
        Assert.Empty(worktrees.Watched);
    }

    /// <summary>
    /// The #281 scoping bug, in the reconciler. A bare agent id like <c>pr-7</c> is unique only INSIDE a
    /// repository (the intake names external-PR workers after the pull-request number), so keying the
    /// live set by the <c>mainguard.agent</c> label alone collapses two repositories' jails into one
    /// entry — and <c>ToDictionary</c> on the duplicate key throws, out of a boot sequence that is
    /// fail-fast. Both labels are the key, exactly as <c>AgentSessionKey</c> and <c>ResolveRunningJail</c>
    /// already do it, so both jails are adopted and both are watched independently.
    /// </summary>
    [Fact]
    public async Task SameAgentIdInTwoRepos_IsTwoAgents_BothAdoptedAndBothWatched()
    {
        var worktrees = new FakeWorktreeManager();
        var reconciler = new SwarmReconciler(
            Docker(Live("pr-7", "repoA"), Live("pr-7", "repoB")),
            new InMemoryExpectedAgentStore(), worktrees);

        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "pr-7", "pr-7" }, report.Adopted);
        Assert.Equal(
            new[] { ("repoA", "pr-7"), ("repoB", "pr-7") },
            worktrees.Watched.OrderBy(w => w.Repo).ToArray());
    }

    /// <summary>
    /// The prune half of the same scoping bug: repo A's <c>pr-7</c> is gone, repo B's is running. Keyed
    /// by agent id alone, repo B's live container answers for repo A's dead one — so a dead agent's
    /// worktree is never pruned and its row never marked Dead, and the UI keeps reporting it live.
    /// </summary>
    [Fact]
    public async Task DeadAgentInOneRepo_IsPruned_EvenWhenAnotherRepoRunsTheSameId()
    {
        var expected = new InMemoryExpectedAgentStore();
        expected.Upsert("repoA", "pr-7", "Live");
        var worktrees = new FakeWorktreeManager();

        var reconciler = new SwarmReconciler(Docker(Live("pr-7", "repoB")), expected, worktrees);

        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "pr-7" }, report.Pruned);
        Assert.Contains(("repoA", "pr-7", true), worktrees.Removed);
        Assert.Equal("Dead", expected.All().Single(a => a.RepoHash == "repoA").Disposition);

        // repo B's jail is untouched: adopted, watched, not pruned.
        Assert.DoesNotContain(worktrees.Removed, r => r.Repo == "repoB");
        Assert.Contains(("repoB", "pr-7"), worktrees.Watched);
    }

    /// <summary>A watcher that throws must not take the daemon down: the boot sequence is fail-fast, so
    /// an exception out of the sweep registration would turn a housekeeping failure into a daemon that
    /// does not start. The rest of the pass still happens.</summary>
    [Fact]
    public async Task WatchFailure_DoesNotFailTheReconcile()
    {
        var expected = new InMemoryExpectedAgentStore();
        var reconciler = new SwarmReconciler(
            Docker(Live("orphan")), expected, new ThrowingWatchManager());

        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "orphan" }, report.Adopted);
        Assert.Equal("Adopted", expected.All().Single().Disposition);
    }

    private sealed class ThrowingWatchManager : IAgentWorktreeManager
    {
        public string CreateAgentWorktree(string repoHash, string agentId) => string.Empty;

        public void RemoveAgentWorktree(string repoHash, string agentId, bool force) { }

        public void Prune(string repoHash) { }

        public IReadOnlyList<WorktreeItem> List(string repoHash) => Array.Empty<WorktreeItem>();

        public void WatchAgentRef(string repoHash, string agentId) =>
            throw new InvalidOperationException("the watcher is unavailable");
    }

    [Fact]
    public void BootSequence_RunsMergeReconcileBeforeSwarm_RtD1Ordering()
    {
        var reconciler = new SwarmReconciler(
            Docker(), new InMemoryExpectedAgentStore(), new FakeWorktreeManager());

        var sequence = DaemonBootSequence.Build(reconciler);

        // RT-D1: the merge-reconcile slot is FIRST (empty until P2-10), then the swarm reconcile.
        Assert.Equal(new[] { "merge-reconcile", "swarm-reconcile" }, sequence.TaskNames);
    }

    [Fact]
    public async Task BootSequence_RunsTasksInOrder()
    {
        var order = new List<string>();
        var sequence = new DaemonBootSequence(new IBootTask[]
        {
            new RecordingTask("first", order),
            new RecordingTask("second", order),
        });

        await sequence.RunAsync();

        Assert.Equal(new[] { "first", "second" }, order);
    }

    private sealed class RecordingTask : IBootTask
    {
        private readonly List<string> _order;

        public RecordingTask(string name, List<string> order)
        {
            Name = name;
            _order = order;
        }

        public string Name { get; }

        public Task RunAsync(CancellationToken ct)
        {
            _order.Add(Name);
            return Task.CompletedTask;
        }
    }
}
