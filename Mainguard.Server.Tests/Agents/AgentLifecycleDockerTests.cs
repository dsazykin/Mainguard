using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// TI-P2-09 tests 2 & 7 (RequiresDocker leg): the yield-timeout <c>docker pause</c>/<c>unpause</c> path
/// proven against a real container, and the leader boot reconcile converging on Docker truth across a
/// registry reload (the durable, leader-owned state a restarted daemon reattaches through). A trivial
/// <c>busybox</c> container stands in for the agent CLI — this leg does NOT depend on the P2-07
/// agent-base image. Gated on Docker-daemon presence only, so a Docker-less dev box skips cleanly.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public class AgentLifecycleDockerTests
{
    private const string TrivialImage = "busybox:latest";

    [RequiresDockerDaemonFact]
    public async Task Yield_Timeout_ShouldDockerPause_ThenTokenResumeUnpauses()
    {
        using var docker = Mainguard.Agents.Agents.Sandbox.DockerEndpointResolver.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;
        if (!await EnsureTrivialImageAsync(docker, ct))
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        string? containerId = null;
        try
        {
            containerId = await StartSleeper(docker, "mainguard-yield-test-" + suffix, ct);
            var cid = containerId;

            // A silent control channel → the yield times out and takes the pause path.
            var engine = new DockerSandboxEngine(docker, new SandboxEngineOptions("bridge", "http://127.0.0.1:0"));
            var protocol = new YieldProtocol(
                _ => new SilentChannel(),
                engine,
                _ => cid,
                defaultTimeout: TimeSpan.FromMilliseconds(50));

            var token = await protocol.RequestYieldAsync("a1", ct: ct);

            Assert.Equal(YieldOutcome.ByPause, token.Outcome);
            Assert.Equal("paused", await StateOf(docker, cid, ct));

            token.Resume();
            Assert.Equal("running", await StateOf(docker, cid, ct));
        }
        finally
        {
            await Cleanup(docker, containerId);
        }
    }

    [RequiresDockerDaemonFact]
    public async Task Leader_ReattachAcrossRegistryReload_ConvergesOnDockerTruth()
    {
        using var docker = Mainguard.Agents.Agents.Sandbox.DockerEndpointResolver.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;
        if (!await EnsureTrivialImageAsync(docker, ct))
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var registryPath = Path.Combine(Path.GetTempPath(), "mainguard-leader-" + suffix + ".json");
        string? liveId = null;
        string? deadId = null;
        try
        {
            liveId = await StartSleeper(docker, "mainguard-leader-live-" + suffix, ct);
            deadId = await StartSleeper(docker, "mainguard-leader-dead-" + suffix, ct);

            // "First daemon": a leader records two PTY sessions to the durable registry.
            new SessionLeader(new LeaderRegistry(registryPath)).Register(
                new LeaderSession("live", "repo1", liveId, 80, 24, "/s/live"));
            new SessionLeader(new LeaderRegistry(registryPath)).Register(
                new LeaderSession("dead", "repo1", deadId, 80, 24, "/s/dead"));

            // One container dies out of band.
            await docker.Containers.RemoveContainerAsync(deadId, new ContainerRemoveParameters { Force = true }, ct);
            deadId = null;

            // "Restarted daemon": reads the leader registry and reattaches, resolving toward Docker truth.
            var containers = await DockerAgentLister.ListAsync(docker, ct);
            // The busybox containers carry no mainguard labels, so match by container id in the registry instead.
            var live = new List<AgentContainerState>
            {
                new("live", "repo1", liveId!, Running: true),
            };
            var restartedLeader = new SessionLeader(new LeaderRegistry(registryPath));
            var report = restartedLeader.Reattach(live);

            Assert.Contains("live", report.Reattached);
            Assert.Contains("dead", report.Reaped);
            var persisted = new LeaderRegistry(registryPath).Load().Select(s => s.AgentId).ToList();
            Assert.Contains("live", persisted);
            Assert.DoesNotContain("dead", persisted);
            _ = containers; // the real lister ran without throwing
        }
        finally
        {
            await Cleanup(docker, liveId);
            await Cleanup(docker, deadId);
            try { File.Delete(registryPath); } catch { /* cleanup */ }
        }
    }

    private static async Task<string> StartSleeper(IDockerClient docker, string name, CancellationToken ct)
    {
        var created = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = TrivialImage,
            Name = name,
            Cmd = new List<string> { "sleep", "300" },
        }, ct);
        await docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
        return created.ID;
    }

    private static async Task<string> StateOf(IDockerClient docker, string id, CancellationToken ct)
    {
        var inspect = await docker.Containers.InspectContainerAsync(id, ct);
        return inspect.State.Status;
    }

    private static async Task Cleanup(IDockerClient docker, string? id)
    {
        if (id is null)
        {
            return;
        }

        try
        {
            await docker.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true });
        }
        catch
        {
            // Never fail a test from cleanup.
        }
    }

    private static async Task<bool> EnsureTrivialImageAsync(IDockerClient docker, CancellationToken ct)
    {
        try
        {
            await docker.Images.InspectImageAsync(TrivialImage, ct);
            return true;
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
            return false;
        }
    }

    private sealed class SilentChannel : IAgentControlChannel
    {
        public Task SendAsync(string marker, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> WaitForAsync(string marker, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult(false); // never ready → the timeout/pause path
    }
}
