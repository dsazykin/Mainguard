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
    /// <param name="agentEnv">The credential env-file contents delivered to the jail's 0400 tmpfs. Null
    /// uses a fixed dummy key. The secret-delivery suite passes a per-run nonce instead, so that "the
    /// credential file holds what THIS spawn sent" is distinguishable from "a file was already there".</param>
    /// <param name="oobKey">The OOB session key delivered to the SUPERVISOR-owned 0400 tmpfs. Null
    /// generates a random one.</param>
    /// <param name="packageCachePath">MG-43 — the per-agent package cache to bind-mount at
    /// <see cref="PackageCachePolicy.SandboxMount"/>. Null keeps the pre-MG-43 jail (no cache mount, no
    /// cache environment); <see cref="NewTempCache"/> makes one.</param>
    public Task<SandboxHandle> SpawnAsync(
        string agentId = "agent-1", int agentUid = 1000, int supervisorUid = 1001,
        IReadOnlyDictionary<string, string>? agentEnv = null, byte[]? oobKey = null,
        string? packageCachePath = null, CancellationToken ct = default)
        => SpawnFromImageAsync(ImageRef, agentId, agentUid, supervisorUid, agentEnv, oobKey, packageCachePath, ct);

    /// <summary>
    /// MG-42 — the same hardened spawn, but from an arbitrary image ref: the per-repo toolchain layer
    /// the daemon builds is a DERIVED image, and "the jail actually starts from it and is still
    /// hardened" is only checkable by spawning from it.
    /// </summary>
    public async Task<SandboxHandle> SpawnFromImageAsync(
        string imageRef, string agentId = "agent-1", int agentUid = 1000, int supervisorUid = 1001,
        IReadOnlyDictionary<string, string>? agentEnv = null, byte[]? oobKey = null,
        string? packageCachePath = null, CancellationToken ct = default)
    {
        // Self-provision the default-deny network + proxy so a test that only spawns (the hardening
        // tests) does not depend on an egress test having run first — the `network mainguard-agents not
        // found` failure was pure test-ordering, not a product bug.
        await EnsureEgressReadyAsync(ct).ConfigureAwait(false);

        var worktree = NewTempWorktree();
        var secrets = new SandboxSecrets(
            agentEnv ?? new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-test-not-a-real-key" },
            OobKey: oobKey ?? RandomKey());

        try
        {
            var handle = await Engine.SpawnAsync(new SandboxSpawnRequest(
                RepoHash: "sandboxfixture" + Guid.NewGuid().ToString("N")[..8],
                AgentId: agentId,
                WorktreePath: worktree,
                ImageRef: imageRef,
                Limits: new SandboxLimits(1L * 1024 * 1024 * 1024, 256),
                Secrets: secrets,
                AgentUid: agentUid,
                SupervisorUid: supervisorUid,
                PackageCachePath: packageCachePath), ct).ConfigureAwait(false);

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
                $"tempRoot='{Path.GetTempPath()}' image='{imageRef}': {ex.Message}", ex);
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
    /// <param name="packageCachePath">MG-43 — an optional per-agent cache to bind-mount (see
    /// <see cref="NewTempCache"/>).</param>
    /// <param name="mutateForTest">MG-43 — a last hook to alter the create request AFTER the production
    /// builder has approved it. It exists for exactly one case: proving the in-jail probe distinguishes
    /// an unwritable mount from a missing one, which the builder (correctly) refuses to construct. Never
    /// use it to bypass a control the builder enforces — the thing under test there would be the builder.</param>
    public async Task<(string ContainerId, AgentSegment Segment)> CreateJailOnSegmentAsync(
        string repoHash, string agentId, CancellationToken ct = default,
        string? packageCachePath = null, Action<CreateContainerParameters>? mutateForTest = null)
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
            DnsServerAddress: segment.ProxyAddress,
            PackageCachePath: packageCachePath));

        mutateForTest?.Invoke(create);

        var created = await Docker.Containers.CreateContainerAsync(create, ct).ConfigureAwait(false);
        _containerIds.Add(created.ID);
        _segments.Add((repoHash, agentId));
        await Docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct).ConfigureAwait(false);
        return (created.ID, segment);
    }

    /// <summary>
    /// Creates and starts a container from an already-built create request, tracking it (and its
    /// segment) for teardown. For the MG-43 verification leg, which needs ceilings the ordinary spawn
    /// helpers fix — a Release build of a 15-project solution is not an agent CLI's working set.
    /// </summary>
    public async Task<string> CreateAndStartAsync(
        CreateContainerParameters create, string repoHash, string agentId, CancellationToken ct = default)
    {
        var created = await Docker.Containers.CreateContainerAsync(create, ct).ConfigureAwait(false);
        _containerIds.Add(created.ID);
        _segments.Add((repoHash, agentId));
        await Docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct).ConfigureAwait(false);
        return created.ID;
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

    /// <summary>
    /// Can <paramref name="containerId"/> open a TCP connection to <paramref name="hostPort"/>? The ONE
    /// reachability probe, shared by the isolation tests and the egress-failure diagnostic.
    ///
    /// <para><b>Why it is not <c>/dev/tcp</c>.</b> The diagnostic used to probe with
    /// <c>sh -c 'echo &gt; /dev/tcp/host/port'</c>. <c>/dev/tcp</c> is a <b>bash</b> builtin and that
    /// ran under <c>sh</c> — dash on Debian — which has no such feature, so it failed with
    /// <c>cannot create /dev/tcp/…: Directory nonexistent</c> and printed <b>UNREACHABLE
    /// unconditionally, on every run, including when the hop was perfectly healthy</b>. Verified: the
    /// same probe under <c>bash</c> answers CONNECTED against the same address the <c>sh</c> form calls
    /// unreachable. It was worse than useless — the diagnostic's own guide reads leg-1-UNREACHABLE as
    /// "the jail cannot reach the proxy", so it actively pointed the next investigation at the wrong
    /// leg.</para>
    ///
    /// <para><b>Why curl.</b> It is in the agent image by construction (the Dockerfile installs it, and
    /// the egress tests are built on it), so the probe cannot silently degrade to "tool missing" the way
    /// a <c>nc</c>-based one would — the image has no <c>nc</c> at all, verified. The proxy environment
    /// is explicitly disabled (<c>--noproxy '*'</c>) so the probe measures the hop it names rather than
    /// being quietly re-routed through the proxy it is trying to test.</para>
    ///
    /// <para><b>Why the verdict is the TCP handshake, not the HTTP reply.</b> The first version asked
    /// "did I get a valid HTTP status?", which conflates <i>can I reach this peer</i> with <i>does this
    /// peer like my request</i>. CI caught it: the same jail→proxy hop that answers 403 here answered
    /// <c>curl: (56) Recv failure: Connection reset by peer</c> there, and the probe called that
    /// UNREACHABLE. It is the opposite — <c>CURLE_RECV_ERROR</c> is a failure <b>after</b> the
    /// connection is established, so a reset is positive proof the SYN/ACK completed and the peer was
    /// there to reset it. Reachability is now read off curl's own connection counters
    /// (<c>num_connects</c>/<c>time_connect</c>), which state the fact directly instead of inferring it
    /// from how far the conversation got.</para>
    ///
    /// <para>Measured against a real daemon, this separates all four outcomes the tests depend on:
    /// <list type="bullet">
    ///   <item>live proxy port → <c>connects=1 tconnect=0.0005 code=403</c>, exit 0 — REACHABLE</item>
    ///   <item>accepted then RST (the CI case) → <c>connects=1</c>, exit 56 — REACHABLE</item>
    ///   <item>port dropped by the backstop → <c>connects=0</c>, exit 28 (connect timeout) — unreachable</item>
    ///   <item>another agent's segment → <c>connects=0</c>, exit 7 (no route) — unreachable</item>
    /// </list>
    /// The last two are what the isolation assertions rest on, and both still read unreachable: a
    /// blocked destination never completes a handshake, whether it is dropped or has no route at all.</para>
    /// </summary>
    public async Task<(bool Reached, string Detail)> TcpProbeAsync(
        string containerId, string hostPort, CancellationToken ct = default)
    {
        try
        {
            var result = await Engine.ExecAsync(containerId, new[]
            {
                "curl", "-sS", "--noproxy", "*", "--connect-timeout", "5", "-m", "10",
                "-o", "/dev/null",
                "-w", "connects=%{num_connects} tconnect=%{time_connect} code=%{http_code}",
                "http://" + hostPort,
            }, ct).ConfigureAwait(false);

            var stdout = result.Stdout.Trim();
            var reached = ConnectCompleted(stdout, result.ExitCode);
            return (reached,
                $"{(reached ? "REACHABLE" : "UNREACHABLE")} {hostPort}: exit={result.ExitCode} "
                + $"[{stdout}] stderr='{result.Stderr.Trim()}'");
        }
        catch (Exception ex)
        {
            return (false, $"UNREACHABLE {hostPort}: the probe itself failed — {ex.Message}");
        }
    }

    /// <summary>
    /// Did the TCP handshake complete? Read from curl's counters when they are present — they are
    /// emitted even on a failed transfer (verified for exits 7, 28 and 0) — and from the exit code
    /// otherwise. The two connect-phase failures are 7 (<c>CURLE_COULDNT_CONNECT</c>: refused, or no
    /// route) and 28 when nothing came back at all; every other failure happens after a connection
    /// exists and therefore proves reachability.
    /// </summary>
    internal static bool ConnectCompleted(string writeOut, int exitCode)
    {
        var connects = MatchNumber(writeOut, "connects=");
        var timeConnect = MatchNumber(writeOut, "tconnect=");
        if (connects is not null || timeConnect is not null)
        {
            return connects >= 1 || timeConnect > 0;
        }

        // No counters to read (curl died before writing them) — fall back to the exit code.
        return exitCode is not (6 or 7 or 28);
    }

    private static double? MatchNumber(string haystack, string key)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            haystack, System.Text.RegularExpressions.Regex.Escape(key) + @"([0-9]+(?:\.[0-9]+)?)");
        return match.Success
            && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>Inspects a spawned container (mounts, host config, state).</summary>
    public Task<ContainerInspectResponse> InspectAsync(string containerId, CancellationToken ct = default)
        => Docker.Containers.InspectContainerAsync(containerId, ct);

    /// <summary>
    /// MG-43 — a per-agent package cache directory on the same ext4 temp filesystem the worktrees use,
    /// laid out under a <c>caches/</c> parent so it satisfies the builder's MG-3 structural guard.
    ///
    /// <para><b>Why it is chmod 0777 and why that is honest.</b> In production the jail reaches this tree
    /// through the MG-17 group whose gid IS the remapped agent gid, provisioned at boot by
    /// <c>UsernsRemapPolicy.MountOwnershipScript</c> — a VM-level fact no test process can reproduce
    /// (the daemon itself cannot chown into the remapped range). Leaving the directory at the default
    /// 0700 would make these tests measure the RUNNER's uid mapping instead of the cache: on a
    /// userns-remapped runner the jail is host uid 101000 and could write nothing, and on this box it is
    /// uid 1000 and could write everything — the same green/red split that made MG-3's attack test pass
    /// for the wrong reason. 0777 removes the uid mapping from the question entirely, so what these
    /// tests measure is the MOUNT. That the production grant is really provisioned is asserted
    /// separately, and environment-independently, by <c>PackageCachePolicyTests</c> against the boot
    /// script's text.</para>
    /// </summary>
    public string NewTempCache(string agentId = "agent-1")
    {
        var path = Path.Combine(
            Path.GetTempPath(), "mainguard-sbx-cache-" + Guid.NewGuid().ToString("N"),
            PackageCachePolicy.CachesDirectoryName, "repo", agentId);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, (UnixFileMode)0b111_111_111);
        }

        _tempWorktrees.Add(path);
        return path;
    }

    /// <summary>
    /// MG-43 — a fresh, ordinary VM root (default umask, no chmod, no group provisioning), the way the
    /// merge-queue end-to-end suite builds one per test. Handed to a real <c>PackageCacheManager</c> so
    /// the SHIPPED grant logic is what makes the cache reachable, rather than a test helper's 0777.
    /// </summary>
    public string NewTempVmRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "mainguard-sbx-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempWorktrees.Add(path);
        return path;
    }

    /// <summary>MG-43 — a cache directory NOTHING can write, whatever uid the runner maps the jail to.
    /// The point of mode 0500 (rather than "owned by someone else") is that it is unwritable to the
    /// owner too, so the refusal it provokes cannot be an artefact of this box's uid mapping.</summary>
    public string NewUnwritableTempCache(string agentId = "agent-1")
    {
        var path = NewTempCache(agentId);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        return path;
    }

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
