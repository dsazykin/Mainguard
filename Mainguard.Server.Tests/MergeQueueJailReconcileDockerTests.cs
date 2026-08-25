using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Xunit;
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Server.Tests;

/// <summary>
/// ISSUES-LOG #24 against a real container engine: <b>a merge-queue entry must stop claiming a sandbox
/// Docker has never heard of, and must say so again when one comes back.</b>
///
/// <para>The field case that produced this file: the daemon's <c>MergeQueueRows</c> held 15 <c>Working</c>
/// rows dated three days earlier while <c>docker ps -a</c> showed exactly ONE agent container on the whole
/// machine. Every one of those rows rendered an enabled Verify — a button whose entire behaviour would have
/// been "Agent 'x' has no live sandbox". <c>67b9cc1f</c> had closed precisely this gap for
/// <c>AgentSessionStore</c>; the merge queue's own state was never wired into it.</para>
///
/// <para>Docker is the only witness here. The jails are real, they are killed <b>out of band</b> — the way
/// the field case happened, with no RPC anywhere in the loop — and the assertions are about the queue
/// agreeing with what <c>docker inspect</c> would print. The unit tier (<c>MergeQueueJailReconcileTests</c>)
/// pins the rules; this pins that the wiring reaches a real engine.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public sealed class MergeQueueJailReconcileDockerTests
{
    private const string Image = "busybox:latest";
    private const string RepoHash = "queuejailreconcilerepohash";

    /// <summary>
    /// The whole bug, end to end. Two entries, two real jails; one is removed behind the daemon's back.
    /// Within one pass the killed one is stranded and the survivor is untouched — the second half being the
    /// one that would make this feature unshippable if it were wrong.
    /// </summary>
    [RequiresDockerDaemonFact]
    public async Task Reconcile_ShouldStrandAQueueEntryWhoseJailWasKilledOutOfBand_AndSpareTheLiveOne()
    {
        using var docker = DockerEndpointResolver.CreateClient();
        using var rig = new QueueJailRig(docker);

        var doomedJail = await rig.StartJailAsync("doomed", rig.Doomed).ConfigureAwait(false);
        await rig.StartJailAsync("survivor", rig.Survivor).ConfigureAwait(false);

        // The queue as the daemon holds it: both agents entered at Working when their jails came up.
        rig.Queue.EnsureEntry(rig.Doomed, MergeEntryOrigin.Local);
        rig.Queue.EnsureEntry(rig.Survivor, MergeEntryOrigin.Local);

        // First pass: both jails are real, so nothing is stranded and both read as live.
        var settled = await rig.Reconciler.ReconcileAsync().ConfigureAwait(false);
        Assert.False(settled.Skipped);
        Assert.Empty(settled.QueueStranded);
        Assert.True(rig.Queue.HasLiveJail(rig.Doomed));
        Assert.True(rig.Queue.HasLiveJail(rig.Survivor));

        // ---- the field event: the jail goes away with nothing telling the daemon ---------------------
        await docker.Containers.RemoveContainerAsync(
            doomedJail, new ContainerRemoveParameters { Force = true }).ConfigureAwait(false);
        rig.Forget(doomedJail);

        var report = await rig.Reconciler.ReconcileAsync().ConfigureAwait(false);

        Assert.False(report.Skipped);
        Assert.Contains($"{RepoHash}/{rig.Doomed}", report.QueueStranded);
        Assert.DoesNotContain($"{RepoHash}/{rig.Survivor}", report.QueueStranded);

        Assert.False(rig.Queue.HasLiveJail(rig.Doomed));
        Assert.True(rig.Queue.HasLiveJail(rig.Survivor));

        // …and the gate now names the action that actually moves the entry.
        Assert.False(rig.Queue.CanMerge(rig.Doomed, out var reason));
        Assert.Equal(MergeQueue.StrandedReason, reason);
        Assert.True(rig.Queue.CanMerge(rig.Survivor, out var live) is false && live == "not verified yet");

        // NOTHING was thrown away. The entry keeps its state, its row and its identity, because
        // AgentResumeService can still give it a live jail on its own branch — which an automatic discard
        // would have made impossible forever (Discarded is terminal and EnsureEntry cannot resurrect an id).
        Assert.Equal(WorkerMergeState.Working, rig.Queue.GetState(rig.Doomed));
        Assert.Null(rig.Queue.GetDiscard(rig.Doomed));
        Assert.Contains(rig.Doomed, rig.Queue.Agents);
        Assert.Equal("Working", Assert.Single(rig.Store.LoadAll(RepoHash), r => r.AgentId == rig.Doomed).State);
    }

    /// <summary>
    /// The recovery direction, which is what makes the pass safe to run on a loop forever: the same entry,
    /// given a real jail again, stops being stranded. Without this a resumed entry would carry the
    /// stranded wording — and lose its Verify button — for the rest of the daemon's life.
    /// </summary>
    [RequiresDockerDaemonFact]
    public async Task Reconcile_ShouldUnstrandTheEntryOnceItHasARealJailAgain()
    {
        using var docker = DockerEndpointResolver.CreateClient();
        using var rig = new QueueJailRig(docker);

        rig.Queue.EnsureEntry(rig.Doomed, MergeEntryOrigin.Local);

        // No jail at all yet — the shape a daemon restart leaves behind, and the shape the field case was in.
        var stranded = await rig.Reconciler.ReconcileAsync().ConfigureAwait(false);
        Assert.Contains($"{RepoHash}/{rig.Doomed}", stranded.QueueStranded);
        Assert.False(rig.Queue.HasLiveJail(rig.Doomed));

        // The resume: a real container under the same (repo, agent) identity.
        await rig.StartJailAsync("resumed", rig.Doomed).ConfigureAwait(false);

        var recovered = await rig.Reconciler.ReconcileAsync().ConfigureAwait(false);

        Assert.Contains($"{RepoHash}/{rig.Doomed}", recovered.QueueRecovered);
        Assert.True(rig.Queue.HasLiveJail(rig.Doomed));
        Assert.True(rig.Queue.CanMerge(rig.Doomed, out var reason) is false && reason == "not verified yet");
    }

    /// <summary>
    /// A frozen jail is still a jail. Docker reports a paused container as <c>"paused"</c> rather than
    /// <c>"running"</c>, and reading liveness as <c>Running</c> is the mistake this area has already paid
    /// for once — here it would strand every entry the kill switch had frozen, i.e. would take the Verify
    /// button away from exactly the work an emergency stop exists to preserve.
    /// </summary>
    [RequiresDockerDaemonFact]
    public async Task Reconcile_ShouldNotStrandAnEntryWhoseJailIsMerelyPaused()
    {
        using var docker = DockerEndpointResolver.CreateClient();
        using var rig = new QueueJailRig(docker);

        var jail = await rig.StartJailAsync("frozen", rig.Survivor).ConfigureAwait(false);
        rig.Queue.EnsureEntry(rig.Survivor, MergeEntryOrigin.Local);
        await docker.Containers.PauseContainerAsync(jail).ConfigureAwait(false);

        var report = await rig.Reconciler.ReconcileAsync().ConfigureAwait(false);

        Assert.Empty(report.QueueStranded);
        Assert.True(rig.Queue.HasLiveJail(rig.Survivor));
    }

    /// <summary>
    /// The real <see cref="AgentSessionReconciler"/> driving a real <see cref="MergeQueue"/> through a real
    /// <see cref="MergeQueueRegistry"/>, over real Docker. Only the jails and the verification runner are
    /// trivial — nothing under test is stubbed.
    /// </summary>
    private sealed class QueueJailRig : IDisposable
    {
        private readonly IDockerClient _docker;
        private readonly List<string> _containers = new();

        public QueueJailRig(IDockerClient docker)
        {
            _docker = docker;
            var stamp = Guid.NewGuid().ToString("N")[..10];
            Doomed = "qjr-doomed-" + stamp;
            Survivor = "qjr-alive-" + stamp;

            Store = new InMemoryMergeQueueStore();
            MergeQueue queue = null!;
            queue = new MergeQueue(
                RepoHash, "sha0", Store, new InMemoryVerificationStore(),
                (id, ct) => Task.FromResult(new VerificationRecord(
                    id, queue.CurrentMainSha, true, "log", "cmd", "hash", DateTimeOffset.UnixEpoch)),
                audit: new InMemoryAuditLog());
            Queue = queue;

            Registry = new MergeQueueRegistry();
            Registry.Register(RepoHash, new MergeQueueContext(Queue, new InMemoryMergeLeaseStore()));

            Sessions = new AgentSessionStore(new InMemoryAuditLog());
            Reconciler = new AgentSessionReconciler(
                Sessions,
                // Scoped to THIS test's two agent ids, so a developer box with real jails on it is neither
                // read nor written by the suite — the RequiresDocker leg must not touch live work.
                async ct =>
                {
                    var all = await DockerAgentLister.ListAsync(_docker, ct).ConfigureAwait(false);
                    var mine = new List<AgentContainerState>();
                    foreach (var c in all)
                    {
                        if (string.Equals(c.AgentId, Doomed, StringComparison.Ordinal)
                            || string.Equals(c.AgentId, Survivor, StringComparison.Ordinal))
                        {
                            mine.Add(c);
                        }
                    }

                    return mine;
                },
                // This rig has no bare mirror on disk, so adoption into the session store is declined — which
                // is exactly the interesting case: the queue sweep must reach the right answer from Docker
                // itself rather than from a session record that happens to exist.
                ownsRepo: _ => false,
                queues: Registry);
        }

        public string Doomed { get; }

        public string Survivor { get; }

        public InMemoryMergeQueueStore Store { get; }

        public MergeQueue Queue { get; }

        public MergeQueueRegistry Registry { get; }

        public AgentSessionStore Sessions { get; }

        public AgentSessionReconciler Reconciler { get; }

        /// <summary>Creates and starts a throwaway jail carrying the P2-07 label set the reconcile reads.</summary>
        public async Task<string> StartJailAsync(string label, string agentId)
        {
            var created = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = Image,
                Name = $"mainguard-qjr-{label}-{Guid.NewGuid().ToString("N")[..8]}",
                Cmd = new List<string> { "sleep", "300" },
                Labels = new Dictionary<string, string>
                {
                    ["mainguard.repo"] = RepoHash,
                    ["mainguard.agent"] = agentId,
                    ["mainguard.role"] = "agent",
                    [DockerAgentLister.KindLabel] = "claude-code",
                    [DockerAgentLister.AgentRoleLabel] = string.Empty,
                },
            }).ConfigureAwait(false);

            _containers.Add(created.ID);
            await _docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters())
                .ConfigureAwait(false);
            return created.ID;
        }

        /// <summary>Drops a container a test removed itself, so teardown does not chase it.</summary>
        public void Forget(string containerId) => _containers.Remove(containerId);

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
