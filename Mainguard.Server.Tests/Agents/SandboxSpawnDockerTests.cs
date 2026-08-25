using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using LibGit2Sharp;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// P2-47 #8 RequiresDocker leg — the real sandboxed spawn behind <c>AgentService.SpawnAgent</c>, proven
/// against a real container runtime through the exact P2-06 → P2-07 chain the daemon uses. A provisioned
/// repo drives <see cref="SandboxAgentLauncher.TryLaunchAsync"/> (provision worktree → ensure default-deny
/// egress → start the hardened jail) and the resulting container carries the <c>mainguard.repo</c>/
/// <c>mainguard.agent</c>/<c>mainguard.role</c> labels; the test then tears the jail + worktree back down. An
/// <b>unprovisioned</b> handle degrades to a session-only launch (no jail, no Docker) — the headless Alpha
/// loop smoke rides that path. Gated by <see cref="RequiresDockerFactAttribute"/> (needs the CI-built
/// agent-base image), so a Docker-less dev box skips and the CI <c>sandbox-security</c> Linux leg runs it.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public class SandboxSpawnDockerTests
{
    [RequiresDockerFact]
    public async Task SpawnAgainstProvisionedRepo_StartsRealJail_WithLabels_ThenTearsDown()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;

        var vmRoot = NewTempDir("mainguard-spawn-vm-");
        var sourceRepo = NewTempDir("mainguard-spawn-src-");
        SeedRepo(sourceRepo);

        var environment = new Wsl2AgentEnvironment(vmRoot: vmRoot);
        var launcher = new SandboxAgentLauncher(environment);
        using var docker = Mainguard.Agents.Agents.Sandbox.DockerEndpointResolver.CreateClient();

        // Provision the bare mirror (the P2-06 clone-once path), then spawn a real jailed agent against it.
        var provision = environment.Repos.Provision(sourceRepo);
        const string agentId = "spawn-agent-1";
        SandboxLaunchResult? launch = null;
        try
        {
            launch = await launcher.TryLaunchAsync(
                provision.RepoHash, agentId, agentKind: "worker", modelApiKey: "sk-test-not-a-real-key", ipcDirPath: null, ct);

            Assert.NotNull(launch);
            Assert.False(string.IsNullOrWhiteSpace(launch!.ContainerId));

            // The jail is real: docker-inspect shows the hardened spec's identifying labels.
            var inspect = await docker.Containers.InspectContainerAsync(launch.ContainerId, ct);
            var labels = inspect.Config.Labels ?? new Dictionary<string, string>();
            Assert.Equal(provision.RepoHash, labels["mainguard.repo"]);
            Assert.Equal(agentId, labels["mainguard.agent"]);
            Assert.Equal("agent", labels["mainguard.role"]);

            // The ext4 worktree the jail mounts was actually materialized on disk.
            Assert.True(Directory.Exists(launch.WorktreePath));
        }
        finally
        {
            if (launch is not null)
            {
                await launcher.TeardownAsync(provision.RepoHash, agentId, launch.ContainerId, CancellationToken.None);
                // The teardown removed the container.
                await Assert.ThrowsAsync<DockerContainerNotFoundException>(
                    () => docker.Containers.InspectContainerAsync(launch.ContainerId, CancellationToken.None));
            }

            await CleanupEgressAsync(docker);
            TryDelete(vmRoot);
            TryDelete(sourceRepo);
        }
    }

    [RequiresDockerFact]
    public async Task EnsureReady_ProxyStoppedByVmShutdown_RestartsIt_AndStillPushesConfig()
    {
        // Field bug (2026-07-22): StopVmOnExit leaves the proxy container Exited, and the next
        // session's EnsureReadyAsync went straight to the config exec against the stopped container
        // — Docker 409 "Container ... is not running" — killing EVERY spawn until it was removed by
        // hand. EnsureReady must treat an existing-but-stopped proxy as restartable, not as ready.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        using var docker = Mainguard.Agents.Agents.Sandbox.DockerEndpointResolver.CreateClient();
        var egress = new EgressProxyConfigurator(
            docker, EgressAllowlist.WithDefaults(new Mainguard.Git.Audit.InMemoryAuditLog()));
        try
        {
            await egress.EnsureReadyAsync(ct); // create + start + push
            await docker.Containers.StopContainerAsync(
                EgressProxyConfigurator.ProxyContainerName, new ContainerStopParameters(), ct);

            await egress.EnsureReadyAsync(ct); // must restart the corpse, then exec — was the 409

            var inspect = await docker.Containers.InspectContainerAsync(
                EgressProxyConfigurator.ProxyContainerName, ct);
            Assert.True(inspect.State.Running);
        }
        finally
        {
            await CleanupEgressAsync(docker);
        }
    }

    [RequiresDockerFact]
    public async Task SpawnAgainstUnprovisionedRepo_DegradesToSessionOnly_NoJail()
    {
        // No mirror on disk for this handle → the launcher returns null (session-only), never a container.
        var vmRoot = NewTempDir("mainguard-spawn-vm-");
        try
        {
            var environment = new Wsl2AgentEnvironment(vmRoot: vmRoot);
            var launcher = new SandboxAgentLauncher(environment);
            var launch = await launcher.TryLaunchAsync(
                "never-provisioned-hash", "agent-x", "worker", modelApiKey: null, ipcDirPath: null, ct: CancellationToken.None);
            Assert.Null(launch);
        }
        finally
        {
            TryDelete(vmRoot);
        }
    }

    private static void SeedRepo(string path)
    {
        Repository.Init(path);
        using var repo = new Repository(path);
        repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
        repo.Config.Set("user.email", "test@mainguard.local", ConfigurationLevel.Local);
        var file = Path.Combine(path, "README.md");
        File.WriteAllText(file, "seed\n");
        Commands.Stage(repo, "README.md");
        var sig = new Signature("test-user", "test@mainguard.local", DateTimeOffset.Now);
        repo.Commit("seed commit", sig, sig);
    }

    private static async Task CleanupEgressAsync(IDockerClient docker)
    {
        // Serial execution (DisableTestParallelization) means the next Docker test recreates the shared
        // default-deny proxy + networks cleanly via EnsureReadyAsync; leaving them would bleed state.
        try { await docker.Containers.RemoveContainerAsync(EgressProxyConfigurator.ProxyContainerName, new ContainerRemoveParameters { Force = true }); }
        catch { /* best effort */ }
        foreach (var network in new[] { EgressProxyConfigurator.AgentNetworkName, EgressProxyConfigurator.EgressNetworkName })
        {
            try
            {
                var matches = await docker.Networks.ListNetworksAsync(new NetworksListParameters
                {
                    Filters = new Dictionary<string, IDictionary<string, bool>> { ["name"] = new Dictionary<string, bool> { [network] = true } },
                });
                foreach (var net in matches)
                {
                    if (net.Name == network)
                    {
                        await docker.Networks.DeleteNetworkAsync(net.ID);
                    }
                }
            }
            catch { /* best effort */ }
        }
    }

    private static string NewTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch { /* never fail a test from cleanup */ }
    }
}
