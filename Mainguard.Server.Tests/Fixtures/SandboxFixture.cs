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

    /// <summary>MG-36 — the per-agent segments this fixture asked for, so teardown reclaims them.
    /// Docker's default local bridge pool is only ~32 networks deep; a suite that leaked one per test
    /// would eventually fail every subsequent create with an address-pool error.</summary>
    private readonly List<(string RepoHash, string AgentId)> _segments = new();

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

    /// <summary>
    /// MG-36 — creates and starts a hardened jail on <b>this agent's own default-deny segment</b>,
    /// building the create request with the production <see cref="ContainerSpecBuilder"/>.
    ///
    /// <para>Deliberately NOT <see cref="SpawnAsync"/>: that path delivers secrets over an exec's
    /// stdin, and a hijacked-stream stdin exec is exactly what some Docker endpoints (Docker Desktop's
    /// WSL2 socket proxy, verified) do not deliver — which would make a NETWORK test fail for reasons
    /// that have nothing to do with the network. What this test needs from a jail is that it is a real
    /// hardened container sitting on a real segment; it needs no credentials at all. Everything about
    /// the spec — capabilities, seccomp, read-only rootfs, the MG-7 resolver pin, the segment — comes
    /// from the same builder the daemon uses.</para>
    /// </summary>
    public async Task<(string ContainerId, AgentSegment Segment)> CreateJailOnSegmentAsync(
        string repoHash, string agentId, CancellationToken ct = default)
    {
        await EnsureEgressReadyAsync(ct).ConfigureAwait(false);
        var segment = await Egress.EnsureAgentSegmentAsync(repoHash, agentId, ct).ConfigureAwait(false);

        var create = ContainerSpecBuilder.Build(new ContainerSpecRequest(
            RepoHash: repoHash,
            AgentId: agentId,
            WorktreePath: NewTempWorktree(),
            ImageRef: ImageRef,
            Limits: new SandboxLimits(1L * 1024 * 1024 * 1024, 256),
            NetworkName: segment.NetworkName,
            Credentials: CredTmpfsSpec.Create(1000, 1001),
            ProxyUrl: segment.ProxyUrl(EgressProxyConfigurator.ProxyPort)!,
            DnsServerAddress: segment.ProxyAddress));

        var created = await Docker.Containers.CreateContainerAsync(create, ct).ConfigureAwait(false);
        _containerIds.Add(created.ID);
        _segments.Add((repoHash, agentId));
        await Docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct).ConfigureAwait(false);
        return (created.ID, segment);
    }

    /// <summary>The container's IPv4 on <paramref name="networkName"/> (its segment).</summary>
    public async Task<string?> AddressOnAsync(string containerId, string networkName, CancellationToken ct = default)
    {
        var inspect = await Docker.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
        return inspect.NetworkSettings?.Networks is { } nets && nets.TryGetValue(networkName, out var ep)
            ? ep?.IPAddress
            : null;
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

        // MG-36: reclaim this test's per-agent segments before the shared teardown (the proxy has to
        // still exist for the disconnect leg to be meaningful).
        foreach (var (repoHash, agentId) in _segments)
        {
            try { await Egress.RemoveAgentSegmentAsync(repoHash, agentId); }
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

        // MG-36: sweep every per-agent segment, including ones a failed/aborted test never registered.
        // A segment left behind is a bridge-pool slot left behind, and the pool is small.
        try
        {
            var all = await Docker.Networks.ListNetworksAsync();
            foreach (var net in all)
            {
                if (net.Name is not null
                    && net.Name.StartsWith(EgressProxyConfigurator.AgentSegmentPrefix, StringComparison.Ordinal))
                {
                    try { await RemoveNetworkByNameAsync(net.Name); }
                    catch { /* best effort */ }
                }
            }
        }
        catch { /* best effort */ }
    }

    private async Task RemoveNetworkByNameAsync(string name)
    {
        var matches = await Docker.Networks.ListNetworksAsync(new NetworksListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>> { ["name"] = new Dictionary<string, bool> { [name] = true } },
        }).ConfigureAwait(false);
        foreach (var net in matches)
        {
            if (net.Name != name)
            {
                continue;
            }

            // Docker refuses to delete a network that still has endpoints, so a jail a previous test
            // left behind silently pins the network in place — and the next test then reuses the OLD
            // network instead of the one it meant to create. That is invisible until a test depends on
            // the network's properties (the MG-18 drift test does), at which point it fails for a
            // reason that has nothing to do with what it is testing. Evict the endpoints first.
            var inspect = await Docker.Networks.InspectNetworkAsync(net.ID).ConfigureAwait(false);
            foreach (var endpoint in inspect.Containers ?? new Dictionary<string, EndpointResource>())
            {
                try
                {
                    await Docker.Networks.DisconnectNetworkAsync(net.ID,
                        new NetworkDisconnectParameters { Container = endpoint.Key, Force = true }).ConfigureAwait(false);
                }
                catch { /* best effort — the delete below reports the real problem */ }
            }

            await Docker.Networks.DeleteNetworkAsync(net.ID).ConfigureAwait(false);
        }
    }
}
