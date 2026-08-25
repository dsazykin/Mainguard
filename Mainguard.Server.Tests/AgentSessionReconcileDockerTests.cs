using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// ISSUES-LOG #18 and #20, end to end against a real container engine: <b>the daemon's idea of what is
/// running must be able to come back from being wrong</b>.
///
/// <para>Two reconcilers already ran at daemon boot, and neither ever touched the live session store —
/// the in-memory registry <c>ListAgents</c>, <c>StreamAgentEvents</c>, the resource monitor and the kill
/// switch all render. So a restarted daemon reported zero agents while their jails kept running, kept
/// burning CPU and kept holding worktrees, with no UI anywhere that could see or stop them. Confirmed
/// live twice: two jails older than the daemon process (#18), and a jail the daemon called <c>Paused</c>
/// for 20+ minutes after a raw <c>docker unpause</c> had made it run again (#20).</para>
///
/// <para>These tests use Docker as the only witness. The store starts EMPTY, which is exactly what a
/// daemon restart leaves behind, and every assertion is about the store agreeing with what
/// <c>docker inspect</c> would print.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public sealed class AgentSessionReconcileDockerTests
{
    /// <summary>A trivial jail — the assertions are about identity and state, not the agent image.</summary>
    private const string Image = "busybox:latest";

    private const string RepoHash = "reconciledockerrepohash";

    /// <summary>
    /// The #18 walk-through, in one pass: a live labelled jail, a daemon that has never heard of it, and
    /// afterwards a session carrying the jail's own identity — kind, orchestration role and container id.
    /// </summary>
    [RequiresDockerDaemonFact]
    public async Task Reconcile_ShouldAdoptASurvivingJailIntoTheLiveSessionStore()
    {
        using var docker = DockerEndpointResolver.CreateClient();
        using var rig = new LiveJailRig(docker);
        var containerId = await rig.StartJailAsync("adopt", agentId: rig.AgentId, role: AgentRoles.Coordinator)
            .ConfigureAwait(false);

        // The daemon as it is one millisecond after a restart: containers running, store empty.
        Assert.Empty(rig.Store.List());

        var report = await rig.Reconciler.ReconcileAsync().ConfigureAwait(false);

        Assert.False(report.Skipped);
        Assert.Contains(rig.AgentId, report.Adopted);

        var session = rig.Store.Find(RepoHash, rig.AgentId);
        Assert.NotNull(session);
        Assert.Equal("claude-code", session!.Kind);
        Assert.Equal(AgentRoles.Coordinator, session.Role);
        Assert.Equal(containerId, session.ContainerId);
        Assert.Equal(AgentSessionReconciler.WorkingState, session.State);
    }

    /// <summary>
    /// ISSUES-LOG #20 in full, with the drift created the same way the field case was — by going around
    /// the app entirely and driving the engine directly. Pause it behind the daemon's back, reconcile, and
    /// the daemon knows; un-pause it behind the daemon's back, reconcile, and the daemon knows that too.
    /// The second half is the one that was broken for 20+ minutes on a live session.
    /// </summary>
    [RequiresDockerDaemonFact]
    public async Task Reconcile_ShouldFollowAnOutOfBandPauseAndUnpause()
    {
        using var docker = DockerEndpointResolver.CreateClient();
        using var rig = new LiveJailRig(docker);
        var containerId = await rig.StartJailAsync("drift", agentId: rig.AgentId).ConfigureAwait(false);

        await rig.Reconciler.ReconcileAsync().ConfigureAwait(false);
        Assert.Equal(AgentSessionReconciler.WorkingState, rig.Store.Find(RepoHash, rig.AgentId)!.State);

        // ---- frozen out of band -------------------------------------------------------------------
        await docker.Containers.PauseContainerAsync(containerId).ConfigureAwait(false);
        Assert.True(await rig.IsPausedAsync(containerId).ConfigureAwait(false));

        var paused = await rig.Reconciler.ReconcileAsync().ConfigureAwait(false);

        Assert.Contains(rig.AgentId, paused.Corrected);
        Assert.Equal(AgentSessionReconciler.PausedState, rig.Store.Find(RepoHash, rig.AgentId)!.State);

        // ---- un-frozen out of band: the exact #20 repro ---------------------------------------------
        await docker.Containers.UnpauseContainerAsync(containerId).ConfigureAwait(false);
        Assert.False(await rig.IsPausedAsync(containerId).ConfigureAwait(false));

        var resumed = await rig.Reconciler.ReconcileAsync().ConfigureAwait(false);

        Assert.Contains(rig.AgentId, resumed.Corrected);
        Assert.Equal(AgentSessionReconciler.WorkingState, rig.Store.Find(RepoHash, rig.AgentId)!.State);
    }

    /// <summary>
    /// A jail that was FROZEN when the daemon died comes back frozen, not dead. This is the half the two
    /// existing boot reconcilers got wrong in the destructive direction: Docker calls a paused container
    /// <c>"paused"</c>, not <c>"running"</c>, so reading liveness as <c>Running</c> made a restart during
    /// an engaged kill switch declare the agent gone and force-remove its worktree.
    /// </summary>
    [RequiresDockerDaemonFact]
    public async Task Reconcile_ShouldAdoptAJailThatWasPausedWhenTheDaemonDied_AsPausedRatherThanLost()
    {
        using var docker = DockerEndpointResolver.CreateClient();
        using var rig = new LiveJailRig(docker);
        var containerId = await rig.StartJailAsync("frozen", agentId: rig.AgentId).ConfigureAwait(false);
        await docker.Containers.PauseContainerAsync(containerId).ConfigureAwait(false);

        var report = await rig.Reconciler.ReconcileAsync().ConfigureAwait(false);

        Assert.Contains(rig.AgentId, report.Adopted);
        var session = rig.Store.Find(RepoHash, rig.AgentId);
        Assert.NotNull(session);
        Assert.Equal(AgentSessionReconciler.PausedState, session!.State);

        // …and the P2-08 swarm reconciler agrees it is alive, so nothing prunes its worktree.
        var live = await DockerAgentLister.ListAsync(docker).ConfigureAwait(false);
        var mine = Assert.Single(live, c => c.ContainerId == containerId);
        Assert.False(mine.Running);
        Assert.True(mine.Live);
    }

    /// <summary>
    /// The other direction, which is what makes this pass safe to run on a loop: a session whose container
    /// really is gone stops being reported as working. Marked, not swept — the daemon's job here is to
    /// stop lying, not to clean up after the user.
    /// </summary>
    [RequiresDockerDaemonFact]
    public async Task Reconcile_ShouldMarkASessionUnresponsive_OnceItsRealJailIsRemoved()
    {
        using var docker = DockerEndpointResolver.CreateClient();
        using var rig = new LiveJailRig(docker);
        var containerId = await rig.StartJailAsync("vanish", agentId: rig.AgentId).ConfigureAwait(false);

        await rig.Reconciler.ReconcileAsync().ConfigureAwait(false);
        Assert.Equal(AgentSessionReconciler.WorkingState, rig.Store.Find(RepoHash, rig.AgentId)!.State);

        await docker.Containers.RemoveContainerAsync(
            containerId, new ContainerRemoveParameters { Force = true }).ConfigureAwait(false);
        rig.Forget(containerId);

        var report = await rig.Reconciler.ReconcileAsync().ConfigureAwait(false);

        Assert.Contains(rig.AgentId, report.Lost);
        var session = rig.Store.Find(RepoHash, rig.AgentId);
        Assert.NotNull(session);
        Assert.Equal(AgentSessionReconciler.LostState, session!.State);
    }

    /// <summary>The real <see cref="AgentSessionStore"/> and a real Docker-backed lister behind the real
    /// <see cref="AgentSessionReconciler"/>. Only the jails are trivial.</summary>
    private sealed class LiveJailRig : IDisposable
    {
        private readonly IDockerClient _docker;
        private readonly List<string> _containers = new();

        public LiveJailRig(IDockerClient docker)
        {
            _docker = docker;
            AgentId = "recon" + Guid.NewGuid().ToString("N")[..12];
            Store = new AgentSessionStore(new InMemoryAuditLog());
            Reconciler = new AgentSessionReconciler(
                Store,
                // Scoped to THIS test's agent id so a developer box with real jails on it is neither read
                // nor written by the suite — the RequiresDocker leg must not touch a live session.
                async ct =>
                {
                    var all = await DockerAgentLister.ListAsync(_docker, ct).ConfigureAwait(false);
                    var mine = new List<AgentContainerState>();
                    foreach (var c in all)
                    {
                        if (string.Equals(c.AgentId, AgentId, StringComparison.Ordinal))
                        {
                            mine.Add(c);
                        }
                    }

                    return mine;
                });
        }

        public string AgentId { get; }

        public AgentSessionStore Store { get; }

        public AgentSessionReconciler Reconciler { get; }

        /// <summary>Creates and starts a throwaway jail carrying the P2-07 label set, remembering it for
        /// teardown. The labels are written here rather than through <c>ContainerSpecBuilder</c> so the
        /// test states the contract the reconciler reads instead of inheriting it.</summary>
        public async Task<string> StartJailAsync(string label, string agentId, string role = "")
        {
            var created = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = Image,
                Name = $"mainguard-recon-{label}-{Guid.NewGuid().ToString("N")[..8]}",
                Cmd = new List<string> { "sleep", "300" },
                Labels = new Dictionary<string, string>
                {
                    ["mainguard.repo"] = RepoHash,
                    ["mainguard.agent"] = agentId,
                    ["mainguard.role"] = "agent",
                    [DockerAgentLister.KindLabel] = "claude-code",
                    [DockerAgentLister.AgentRoleLabel] = role,
                },
            }).ConfigureAwait(false);

            _containers.Add(created.ID);
            await _docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters())
                .ConfigureAwait(false);
            return created.ID;
        }

        /// <summary>Drops a container a test removed itself, so teardown does not chase it.</summary>
        public void Forget(string containerId) => _containers.Remove(containerId);

        /// <summary>Docker's own answer — the field <c>docker inspect -f '{{.State.Paused}}'</c> prints,
        /// read straight off the daemon rather than through anything under test.</summary>
        public async Task<bool> IsPausedAsync(string containerId)
        {
            var inspect = await _docker.Containers.InspectContainerAsync(containerId).ConfigureAwait(false);
            return inspect.State?.Paused ?? false;
        }

        public void Dispose()
        {
            foreach (var containerId in _containers)
            {
                try
                {
                    _docker.Containers.RemoveContainerAsync(
                        containerId, new ContainerRemoveParameters { Force = true }).GetAwaiter().GetResult();
                }
                catch
                {
                    // Never fail a test from cleanup — a forced remove works on a paused container too.
                }
            }
        }
    }
}
