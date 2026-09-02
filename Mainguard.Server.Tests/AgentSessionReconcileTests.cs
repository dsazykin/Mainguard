using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Mainguard.Server.Runtime;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// ISSUES-LOG #18/#20, at the unit tier: the rules the live session store's reconcile follows, without a
/// container engine in the way. <see cref="AgentSessionReconcileDockerTests"/> proves the same behaviour
/// against real containers; these pin the decisions — especially the ones about what NOT to touch.
/// </summary>
public sealed class AgentSessionReconcileTests
{
    /// <summary>
    /// One switch, not two. This assembly's module initializer sets exactly one variable to keep a test
    /// daemon off the machine's live containers, and for a long time only the periodic pass read it — the
    /// boot-time <see cref="SwarmReconcileTask"/> went on adopting the developer's real jails and naming
    /// them in the test audit log. Pinning the two names together is what stops that reopening: let
    /// either drift and the harness silently covers half of what it claims to.
    /// </summary>
    [Fact]
    public void TheReconcileDisableSwitch_IsOneStringSharedWithTheBootPass()
        => Assert.Equal(
            SwarmReconcileTask.DisableVariable,
            AgentSessionReconcilerService.DisableVariable);

    private const string Repo = "repohash";

    private static AgentSessionReconciler Build(
        AgentSessionStore store, params AgentContainerState[] containers) =>
        new(store, _ => Task.FromResult<IReadOnlyList<AgentContainerState>>(containers));

    private static AgentSessionStore NewStore() => new(new InMemoryAuditLog());

    /// <summary>
    /// The #18 case: a daemon whose session store is empty (it just restarted) meets jails that are still
    /// running. They come back as real sessions — with the identity their labels carry — so every surface
    /// built on <c>ListAgents</c> can see, monitor and stop them again.
    /// </summary>
    [Fact]
    public async Task Reconcile_ShouldAdoptALiveJailTheStoreHasNoRecordOf_WithItsKindAndRole()
    {
        var store = NewStore();
        var reconciler = Build(store, new AgentContainerState(
            "agent-1", Repo, "container-1", Running: true, Kind: "claude-code", Role: AgentRoles.Coordinator));

        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "agent-1" }, report.Adopted);
        var session = store.Find(Repo, "agent-1");
        Assert.NotNull(session);
        Assert.Equal("claude-code", session!.Kind);
        Assert.Equal(AgentRoles.Coordinator, session.Role);
        Assert.Equal("container-1", session.ContainerId);
        Assert.Equal(AgentSessionReconciler.WorkingState, session.State);
    }

    /// <summary>
    /// A worker's coordinator link is part of its identity: every coordinator tool resolves the worker
    /// through <c>ParentAgentId</c>, so an adopted worker with no parent is one its coordinator can no
    /// longer see, steer or verify after a restart. The label carries it, and adoption must read it.
    /// </summary>
    [Fact]
    public async Task Reconcile_ShouldAdoptAWorker_WithTheCoordinatorThatSpawnedIt()
    {
        var store = NewStore();
        var reconciler = Build(store, new AgentContainerState(
            "worker-1", Repo, "container-1", Running: true, Kind: "claude-code", Role: AgentRoles.Managed,
            ParentAgentId: "coord-1"));

        await reconciler.ReconcileAsync();

        Assert.Equal("coord-1", store.Find(Repo, "worker-1")!.ParentAgentId);
    }

    /// <summary>A jail that was frozen when the daemon died is adopted back <b>as frozen</b> — the surface
    /// must not offer a Pause button for a container that is already paused.</summary>
    [Fact]
    public async Task Reconcile_ShouldAdoptAPausedJail_AsPaused()
    {
        var store = NewStore();
        var reconciler = Build(store, new AgentContainerState(
            "agent-1", Repo, "container-1", Running: false, Paused: true, Kind: "claude-code"));

        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "agent-1" }, report.Adopted);
        Assert.Equal(AgentSessionReconciler.PausedState, store.Find(Repo, "agent-1")!.State);
    }

    /// <summary>
    /// ISSUES-LOG #20, the exact defect: an agent the daemon believes is <c>Paused</c> whose container was
    /// un-paused out of band (a raw <c>docker unpause</c>, an engine restart — anything that never reaches
    /// an RPC). Before this pass the stale word survived indefinitely; one agent carried it for 20+ minutes.
    /// </summary>
    [Fact]
    public async Task Reconcile_ShouldClearAStalePaused_WhenDockerSaysTheJailIsRunning()
    {
        var store = NewStore();
        store.Spawn("claude-code", agentId: "agent-1", repoHash: Repo);
        store.AttachSandbox("agent-1", "container-1", Repo);
        store.MarkState(new AgentSessionKey(Repo, "agent-1"), "Paused", "kill switch");

        var reconciler = Build(store, new AgentContainerState(
            "agent-1", Repo, "container-1", Running: true));
        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "agent-1" }, report.Corrected);
        Assert.Equal(AgentSessionReconciler.WorkingState, store.Find(Repo, "agent-1")!.State);
    }

    /// <summary>The mirror image: a jail frozen outside the app stops being reported as busy.</summary>
    [Fact]
    public async Task Reconcile_ShouldReportPaused_WhenTheJailWasFrozenOutOfBand()
    {
        var store = NewStore();
        store.Spawn("claude-code", agentId: "agent-1", repoHash: Repo);
        store.AttachSandbox("agent-1", "container-1", Repo);

        var reconciler = Build(store, new AgentContainerState(
            "agent-1", Repo, "container-1", Running: false, Paused: true));
        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "agent-1" }, report.Corrected);
        Assert.Equal(AgentSessionReconciler.PausedState, store.Find(Repo, "agent-1")!.State);
    }

    /// <summary>
    /// Only the PAUSE AXIS is corrected. A live agent's state word carries orchestration meaning the
    /// container cannot know, and flattening <c>RateLimited</c> to <c>Working</c> because the process tree
    /// happens to be scheduled would destroy more than the drift did.
    /// </summary>
    [Fact]
    public async Task Reconcile_ShouldLeaveANonPauseStateAlone_WhenTheJailIsSimplyRunning()
    {
        var store = NewStore();
        store.Spawn("claude-code", agentId: "agent-1", repoHash: Repo);
        store.AttachSandbox("agent-1", "container-1", Repo);
        store.MarkState(new AgentSessionKey(Repo, "agent-1"), "RateLimited", "429 from the provider");

        var reconciler = Build(store, new AgentContainerState(
            "agent-1", Repo, "container-1", Running: true));
        var report = await reconciler.ReconcileAsync();

        Assert.Empty(report.Corrected);
        Assert.Equal("RateLimited", store.Find(Repo, "agent-1")!.State);
    }

    /// <summary>A session whose jail is gone stops claiming to be working. Marked, never silently swept —
    /// and marked <c>Unresponsive</c> rather than <c>Paused</c>, because nothing is containing it.</summary>
    [Fact]
    public async Task Reconcile_ShouldMarkASessionUnresponsive_WhenItsContainerIsGone()
    {
        var store = NewStore();
        store.Spawn("claude-code", agentId: "agent-1", repoHash: Repo);
        store.AttachSandbox("agent-1", "container-1", Repo);

        var reconciler = Build(store); // Docker answers: no such container
        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "agent-1" }, report.Lost);
        Assert.Equal(AgentSessionReconciler.LostState, store.Find(Repo, "agent-1")!.State);
    }

    /// <summary>
    /// The failure mode that would make this pass worse than no pass at all: a container engine that does
    /// not answer must NOT read as "every jail vanished". The lister is allowed to throw for exactly this
    /// reason, and a throwing pass changes nothing.
    /// </summary>
    [Fact]
    public async Task Reconcile_ShouldChangeNothing_WhenTheContainerEngineCannotBeReached()
    {
        var store = NewStore();
        store.Spawn("claude-code", agentId: "agent-1", repoHash: Repo);
        store.AttachSandbox("agent-1", "container-1", Repo);

        var reconciler = new AgentSessionReconciler(
            store, _ => throw new InvalidOperationException("docker is not running"));
        var report = await reconciler.ReconcileAsync();

        Assert.True(report.Skipped);
        Assert.False(report.Changed);
        Assert.Equal("Working", store.Find(Repo, "agent-1")!.State);
    }

    /// <summary>A session-only record (an unprovisioned repo — no jail was ever made) has no container to
    /// be compared against and must not be declared lost for it.</summary>
    [Fact]
    public async Task Reconcile_ShouldIgnoreASessionThatNeverHadAJail()
    {
        var store = NewStore();
        store.Spawn("claude-code", agentId: "agent-1", repoHash: Repo);

        var report = await Build(store).ReconcileAsync();

        Assert.Empty(report.Lost);
        Assert.Equal("Starting", store.Find(Repo, "agent-1")!.State);
    }

    /// <summary>Two repositories can each hold a <c>pr-7</c>. Adoption keys on (repo, agent) like every
    /// other identity in the chain, so both jails come back rather than one overwriting the other.</summary>
    [Fact]
    public async Task Reconcile_ShouldAdoptTheSameAgentIdInTwoRepos_AsTwoSessions()
    {
        var store = NewStore();
        var reconciler = Build(store,
            new AgentContainerState("pr-7", "repo-a", "container-a", Running: true, Kind: "claude-code"),
            new AgentContainerState("pr-7", "repo-b", "container-b", Running: true, Kind: "claude-code"));

        var report = await reconciler.ReconcileAsync();

        Assert.Equal(2, report.Adopted.Count);
        Assert.Equal("container-a", store.Find("repo-a", "pr-7")!.ContainerId);
        Assert.Equal("container-b", store.Find("repo-b", "pr-7")!.ContainerId);
    }

    /// <summary>The pass is idempotent: a second run over an unchanged world reports nothing, so a
    /// 30-second loop does not fill the audit log with re-announcements of the same facts.</summary>
    [Fact]
    public async Task Reconcile_ShouldBeIdempotent()
    {
        var store = NewStore();
        var reconciler = Build(store, new AgentContainerState(
            "agent-1", Repo, "container-1", Running: true, Kind: "claude-code"));

        await reconciler.ReconcileAsync();
        var second = await reconciler.ReconcileAsync();

        Assert.False(second.Changed);
    }

    /// <summary>
    /// The ownership gate, and it is not cosmetic: the container engine is machine-wide and the jail
    /// labels carry no daemon identity, so without it every daemon on the box adopts every other one's
    /// jails. Two in-proc test daemons — on isolated data roots, hosting no repository — proved it by
    /// claiming a developer's live agent as their own the moment this pass existed.
    /// </summary>
    [Fact]
    public async Task Reconcile_ShouldNotAdoptAJail_ForARepositoryThisDaemonDoesNotHost()
    {
        var store = NewStore();
        var reconciler = new AgentSessionReconciler(
            store,
            _ => Task.FromResult<IReadOnlyList<AgentContainerState>>(new[]
            {
                new AgentContainerState("someone-elses", "other-repo", "container-x", Running: true),
            }),
            ownsRepo: hash => string.Equals(hash, Repo, StringComparison.Ordinal));

        var report = await reconciler.ReconcileAsync();

        Assert.Empty(report.Adopted);
        Assert.Empty(store.List());
    }

    /// <summary>
    /// The half of the same defect that lives in the OTHER two reconcilers. Docker calls a frozen container
    /// <c>"paused"</c>, not <c>"running"</c>, so reading <c>Running</c> as "still here" meant a daemon
    /// restart while any agent was paused declared it dead, force-removed its worktree and reaped its PTY —
    /// destroying exactly the work an emergency stop exists to preserve.
    /// </summary>
    [Fact]
    public void PausedContainer_ShouldCountAsLive_ForTheSwarmAndLeaderReconcilers()
    {
        var paused = new AgentContainerState("agent-1", Repo, "container-1", Running: false, Paused: true);

        Assert.False(paused.Running);
        Assert.True(paused.Live);
    }

    /// <summary>The swarm reconciler, exercised: a paused jail is kept, not pruned.</summary>
    [Fact]
    public async Task SwarmReconciler_ShouldNotPruneAnAgentWhoseJailIsMerelyPaused()
    {
        var expected = new InMemoryExpectedAgentStore();
        expected.Upsert(Repo, "agent-1", "Live");
        var worktrees = new RecordingWorktrees();

        var reconciler = new SwarmReconciler(
            _ => Task.FromResult<IReadOnlyList<AgentContainerState>>(new[]
            {
                new AgentContainerState("agent-1", Repo, "container-1", Running: false, Paused: true),
            }),
            expected, worktrees);

        var report = await reconciler.ReconcileAsync();

        Assert.Empty(report.Pruned);
        Assert.Empty(worktrees.Removed);
        Assert.NotEqual("Dead", expected.All().Single().Disposition);
    }

    /// <summary>The leader reconciler, exercised: a paused jail's PTY session survives the boot pass.</summary>
    [Fact]
    public void SessionLeader_ShouldReattachAPausedJailsSession_RatherThanReapIt()
    {
        var registryPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mg-leader-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var leader = new SessionLeader(new LeaderRegistry(registryPath));
            leader.Register(new LeaderSession("agent-1", Repo, "container-1", 80, 24, SocketPath: string.Empty));

            var report = leader.Reattach(new[]
            {
                new AgentContainerState("agent-1", Repo, "container-1", Running: false, Paused: true),
            });

            Assert.Equal(new[] { "agent-1" }, report.Reattached);
            Assert.Empty(report.Reaped);
        }
        finally
        {
            try { System.IO.File.Delete(registryPath); } catch { /* best-effort */ }
        }
    }

    private sealed class RecordingWorktrees : IAgentWorktreeManager
    {
        public List<string> Removed { get; } = new();

        public string CreateAgentWorktree(string repoHash, string agentId) => $"/wt/{repoHash}/{agentId}";

        public void RemoveAgentWorktree(string repoHash, string agentId, bool force) => Removed.Add(agentId);

        public void Prune(string repoHash) { }

        public IReadOnlyList<Mainguard.Git.Models.WorktreeItem> List(string repoHash) =>
            Array.Empty<Mainguard.Git.Models.WorktreeItem>();

        public void WatchAgentRef(string repoHash, string agentId) { }
    }
}
