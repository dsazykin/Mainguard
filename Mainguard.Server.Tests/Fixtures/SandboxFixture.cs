using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>
/// TI-P2-07 §A.4 infrastructure contract: spawns a real hardened agent container through the P2-07
/// engine (default-deny egress + hardened spec) for the <c>RequiresDocker</c> suite, and cleans up
/// every container/worktree it created. This is the substrate the egress / inspect / git-proxy /
/// memory-scrape tests stand on — hand-rolling around it is a review rejection.
///
/// <para>The agent base image ref comes from <c>MAINGUARD_AGENT_IMAGE</c> (default
/// <c>mainguard-agent-base:latest</c>) — CI builds it from <c>images/mainguard-agent-base/</c>; the image
/// is never built at runtime (G-16).</para>
/// </summary>
public sealed class SandboxFixture : IAsyncDisposable
{
    private readonly List<string> _containerIds = new();
    private readonly List<string> _tempWorktrees = new();

    public IDockerClient Docker { get; }
    public DockerSandboxEngine Engine { get; }
    public EgressProxyConfigurator Egress { get; }
    public string ImageRef { get; }

    public SandboxFixture()
    {
        Docker = new DockerClientConfiguration().CreateClient();
        ImageRef = Environment.GetEnvironmentVariable("MAINGUARD_AGENT_IMAGE") ?? "mainguard-agent-base:latest";
        Egress = new EgressProxyConfigurator(Docker, EgressAllowlist.WithDefaults(new Mainguard.Git.Audit.InMemoryAuditLog()));
        Engine = new DockerSandboxEngine(Docker, new SandboxEngineOptions(Egress.NetworkName, Egress.ProxyUrl));
    }

    /// <summary>Ensures the default-deny network + proxy exist before spawning agents.</summary>
    public Task EnsureEgressReadyAsync(CancellationToken ct = default) => Egress.EnsureReadyAsync(ct);

    /// <summary>Spawns a hardened agent jail on an ext4 (temp) worktree; tracks it for cleanup.</summary>
    public async Task<SandboxHandle> SpawnAsync(
        string agentId = "agent-1", int agentUid = 1000, int supervisorUid = 1001, CancellationToken ct = default)
    {
        // Self-provision the default-deny network + proxy so a test that only spawns (the hardening
        // tests) does not depend on an egress test having run first — the `network mainguard-agents not
        // found` failure was pure test-ordering, not a product bug.
        await EnsureEgressReadyAsync(ct).ConfigureAwait(false);

        var worktree = NewTempWorktree();
        var secrets = new SandboxSecrets(
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-test-not-a-real-key" },
            OobKey: RandomKey());

        try
        {
            var handle = await Engine.SpawnAsync(new SandboxSpawnRequest(
                RepoHash: "sandboxfixture" + Guid.NewGuid().ToString("N")[..8],
                AgentId: agentId,
                WorktreePath: worktree,
                ImageRef: ImageRef,
                Limits: new SandboxLimits(1L * 1024 * 1024 * 1024, 256),
                Secrets: secrets,
                AgentUid: agentUid,
                SupervisorUid: supervisorUid), ct).ConfigureAwait(false);

            _containerIds.Add(handle.ContainerId);
            return handle;
        }
        catch (Exception ex)
        {
            // Diagnostic: whether the ext4 worktree existed on disk when the daemon tried to bind it
            // disambiguates a create-failure from a daemon-visibility issue (the `bind source path does
            // not exist` failure only appeared on the stacked CI job).
            throw new InvalidOperationException(
                $"SandboxFixture.SpawnAsync failed. worktree='{worktree}' existsOnDisk={Directory.Exists(worktree)} " +
                $"tempRoot='{Path.GetTempPath()}' image='{ImageRef}': {ex.Message}", ex);
        }
    }

    /// <summary>Runs a command in a live sandbox and returns exit + output.</summary>
    public Task<SandboxExecResult> ExecAsync(string containerId, params string[] command)
        => Engine.ExecAsync(containerId, command);

    /// <summary>Inspects a spawned container (mounts, host config, state).</summary>
    public Task<ContainerInspectResponse> InspectAsync(string containerId, CancellationToken ct = default)
        => Docker.Containers.InspectContainerAsync(containerId, ct);

    private string NewTempWorktree()
    {
        // A real ext4 path on the Linux CI leg (/tmp) — never /mnt/c or a UNC (G-11).
        var path = Path.Combine(Path.GetTempPath(), "mainguard-sbx-wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempWorktrees.Add(path);
        return path;
    }

    private static byte[] RandomKey()
    {
        var key = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);
        return key;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _containerIds)
        {
            try { await Engine.RemoveAsync(id); }
            catch { /* never fail a test from cleanup */ }
        }

        // Tear down the SHARED egress proxy + networks this fixture (idempotently) created. Leaving them
        // behind let one test/feature's Docker state bleed into the next — the root of the "works alone,
        // fails when other Docker tests run in the same job" flakiness. Serial execution (assembly
        // DisableTestParallelization) means the next test recreates them cleanly via EnsureEgressReadyAsync.
        await ForceRemoveProxyAndNetworksAsync();

        foreach (var dir in _tempWorktrees)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }

        Docker.Dispose();
    }

    /// <summary>
    /// Removes the SHARED egress proxy + both mainguard networks, best-effort. Used by teardown and by
    /// the MG-18 drift test, which has to plant a deliberately-wrong network under the agent network's
    /// name and therefore needs whatever a previous test left attached to be gone first.
    /// </summary>
    public async Task ForceRemoveProxyAndNetworksAsync()
    {
        try { await Docker.Containers.RemoveContainerAsync(EgressProxyConfigurator.ProxyContainerName, new ContainerRemoveParameters { Force = true }); }
        catch { /* best effort */ }
        foreach (var network in new[] { EgressProxyConfigurator.AgentNetworkName, EgressProxyConfigurator.EgressNetworkName })
        {
            try { await RemoveNetworkByNameAsync(network); }
            catch { /* best effort */ }
        }
    }

    private async Task RemoveNetworkByNameAsync(string name)
    {
        var matches = await Docker.Networks.ListNetworksAsync(new NetworksListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>> { ["name"] = new Dictionary<string, bool> { [name] = true } },
        }).ConfigureAwait(false);
        foreach (var net in matches)
        {
            if (net.Name == name)
            {
                await Docker.Networks.DeleteNetworkAsync(net.ID).ConfigureAwait(false);
            }
        }
    }
}
