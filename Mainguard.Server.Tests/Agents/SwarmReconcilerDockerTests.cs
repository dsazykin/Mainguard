using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Models;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// TI-P2-08 test 7 (RequiresDocker) — the swarm reconciler's <b>Docker-as-truth convergence</b> proven
/// against the <b>real</b> <see cref="DockerAgentLister"/> rather than a simulated listing. A trivial
/// <c>busybox</c> container (NOT the P2-07 agent-base image) carrying the real
/// <c>mainguard.agent</c>/<c>mainguard.repo</c> labels stands in for an agent jail; an out-of-band
/// <c>docker rm -f</c> is followed by a boot reconcile that must prune it and mark it <c>Dead</c>.
/// Gated on Docker-daemon presence only, so a Docker-less dev box skips cleanly.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public class SwarmReconcilerDockerTests
{
    private const string TrivialImage = "busybox:latest";

    private sealed class NoopWorktreeManager : IAgentWorktreeManager
    {
        public List<string> Removed { get; } = new();

        public string CreateAgentWorktree(string repoHash, string agentId) => string.Empty;

        public void RemoveAgentWorktree(string repoHash, string agentId, bool force) => Removed.Add(agentId);

        public void Prune(string repoHash) { }

        public IReadOnlyList<WorktreeItem> List(string repoHash) => Array.Empty<WorktreeItem>();
    }

    [RequiresDockerDaemonFact]
    public async Task Reconciler_OutOfBandDockerRm_ShouldConvergeOnBoot()
    {
        using var docker = new DockerClientConfiguration().CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;

        if (!await EnsureTrivialImageAsync(docker, ct))
        {
            return; // Docker present but the trivial image is unavailable (no registry) — nothing to prove.
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoHash = "recon-" + suffix;
        var agentId = "agent-" + suffix;
        var worktrees = new NoopWorktreeManager();
        string? containerId = null;

        try
        {
            // A trivial long-lived container carrying the REAL agent labels — stands in for a jail.
            var created = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = TrivialImage,
                Name = "mainguard-recon-test-" + suffix,
                Cmd = new List<string> { "sleep", "300" },
                Labels = new Dictionary<string, string>
                {
                    ["mainguard.agent"] = agentId,
                    ["mainguard.repo"] = repoHash,
                },
            }, ct);
            containerId = created.ID;
            await docker.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);

            var expected = new InMemoryExpectedAgentStore();
            expected.Upsert(repoHash, agentId, "Live");

            var reconciler = new SwarmReconciler(
                listContainers: c => DockerAgentLister.ListAsync(docker, c),
                expected: expected,
                worktrees: worktrees,
                stopContainer: (id, c) => docker.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, c));

            // 1. The real lister sees the live labelled container → not pruned, still Live.
            var first = await reconciler.ReconcileAsync(ct);
            Assert.DoesNotContain(agentId, first.Pruned);
            Assert.NotEqual("Dead", expected.All().Single(a => a.AgentId == agentId).Disposition);

            // 2. Out-of-band removal — Docker is now the sole truth that the agent is gone.
            await docker.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }, ct);
            containerId = null;

            // 3. The boot reconcile converges: the dead agent is pruned + marked Dead.
            var second = await reconciler.ReconcileAsync(ct);
            Assert.Contains(agentId, second.Pruned);
            Assert.Contains(agentId, worktrees.Removed);
            var row = expected.All().Single(a => a.AgentId == agentId);
            Assert.Equal("Dead", row.Disposition);
            Assert.False(string.IsNullOrWhiteSpace(row.DisposalReason));
        }
        finally
        {
            if (containerId is not null)
            {
                try
                {
                    await docker.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });
                }
                catch
                {
                    // Never fail a test from cleanup.
                }
            }
        }
    }

    /// <summary>Ensures the trivial image is present (inspect, else pull). False if it can't be obtained.</summary>
    private static async Task<bool> EnsureTrivialImageAsync(IDockerClient docker, CancellationToken ct)
    {
        try
        {
            await docker.Images.InspectImageAsync(TrivialImage, ct);
            return true; // already present
        }
        catch (DockerImageNotFoundException)
        {
            // fall through to pull
        }

        try
        {
            await docker.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = "busybox", Tag = "latest" },
                authConfig: null,
                progress: new Progress<JSONMessage>(),
                cancellationToken: ct);
            return true;
        }
        catch
        {
            return false; // Docker up but no registry access — the caller returns without asserting.
        }
    }
}
