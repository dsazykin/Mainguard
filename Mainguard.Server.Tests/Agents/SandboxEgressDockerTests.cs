using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// TI-P2-07 RequiresDocker egress matrix — each row its own test (P2-07 §4). Proves default-deny with
/// the iptables backstop (not proxy-env alone), pinned DNS (NXDOMAIN for exfil names), fast refusal
/// (not timeout), a live <c>devbox add</c>, and A6 (a direct git-host clone from the agent fails fast
/// because the git host is absent from the agent allowlist). Gated on Docker availability.
/// </summary>
[Trait("Category", "RequiresDocker")]
public class SandboxEgressDockerTests
{
    // A non-allowlisted destination must be refused within this budget — refused, not hung.
    private static readonly TimeSpan FastFailBudget = TimeSpan.FromSeconds(5);

    [RequiresDockerFact]
    public async Task Curl_AllowlistedModelApi_ShouldSucceedViaProxy()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        // A 200/401 both prove the connection reached the API through the proxy (auth aside).
        var result = await fx.ExecAsync(handle.ContainerId,
            "curl", "-sS", "-o", "/dev/null", "-w", "%{http_code}", "https://api.anthropic.com/v1/models");
        Assert.Matches(@"^\d{3}$", result.Stdout.Trim());
        Assert.NotEqual("000", result.Stdout.Trim()); // 000 = never connected
    }

    [RequiresDockerFact]
    public async Task Curl_NonAllowlistedDomain_ShouldFailFast_RefusedNotTimeout()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        var sw = Stopwatch.StartNew();
        var result = await fx.ExecAsync(handle.ContainerId,
            "curl", "-sS", "-m", "8", "-o", "/dev/null", "-w", "%{http_code}", "https://example.com");
        sw.Stop();

        Assert.NotEqual(0, result.ExitCode);       // refused
        Assert.True(sw.Elapsed < FastFailBudget, $"expected fast refusal, took {sw.Elapsed}");
    }

    [RequiresDockerFact]
    public async Task DirectIpEgress_ShouldBeDropped_DespiteProxyEnvUnset()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        // Bypass HTTP_PROXY entirely and dial a raw IP: the iptables backstop must DROP it.
        var result = await fx.ExecAsync(handle.ContainerId,
            "env", "-u", "HTTP_PROXY", "-u", "HTTPS_PROXY", "-u", "http_proxy", "-u", "https_proxy",
            "curl", "-sS", "-m", "8", "-o", "/dev/null", "http://1.1.1.1");
        Assert.NotEqual(0, result.ExitCode);
    }

    // MG-7 — this test used to query `secret-data.attacker.tld`, which NXDOMAINs on ANY resolver on
    // earth. It therefore passed with ZERO DNS pinning in place: the jail was on Docker's embedded
    // 127.0.0.11 (forwarding to the VM's upstream DNS) and the NXDOMAIN-pinned dnsmasq existed only
    // inside the proxy container, consulted by nothing. The probe must use a name that a real resolver
    // WOULD answer, so that an answer is proof the query left through an unpinned resolver.
    [RequiresDockerFact]
    public async Task DnsExfil_ResolvableNonAllowlistedName_ShouldNotLeaveTheJail()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        var result = await fx.ExecAsync(handle.ContainerId, "getent", "hosts", "example.com");

        // dnsmasq's catch-all answers 0.0.0.0 for anything not allowlisted; a resolver that actually
        // reached the internet returns example.com's real (routable) address instead. Either the lookup
        // fails outright or it is answered by the sinkhole — a routable answer means the pin is off.
        var answer = result.Stdout.Trim();
        Assert.True(
            result.ExitCode != 0 || answer.StartsWith("0.0.0.0", StringComparison.Ordinal),
            $"example.com resolved to a real address inside the jail — the query left through an unpinned "
            + $"resolver, so pinned DNS is not in the resolution path. getent said: '{answer}'");
    }

    [RequiresDockerFact]
    public async Task JailResolver_ShouldBeTheProxy_NotDockersEmbeddedResolver()
    {
        // The structural half of MG-7, read off the live container: /etc/resolv.conf must name the
        // proxy's dnsmasq and must NOT name Docker's embedded 127.0.0.11.
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        var proxyAddress = await fx.Egress.ResolveProxyAddressAsync();
        Assert.False(string.IsNullOrEmpty(proxyAddress), "the egress proxy has no address on the agent network");

        var resolv = await fx.ExecAsync(handle.ContainerId, "cat", "/etc/resolv.conf");
        Assert.Contains("nameserver " + proxyAddress, resolv.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.11", resolv.Stdout, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    public async Task AllowlistedName_ShouldStillResolve_UnderThePinnedResolver()
    {
        // Non-vacuity in the other direction: a pin that resolves NOTHING would also pass the exfil
        // probe above while breaking every agent. The allowlisted model API must still resolve.
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        var result = await fx.ExecAsync(handle.ContainerId, "getent", "hosts", "api.anthropic.com");
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("0.0.0.0", result.Stdout, StringComparison.Ordinal);
    }

    // The toolchain is PRE-BAKED into the agent image (A6 decision): devbox's runtime `add` resolves
    // nixpkgs from the git host, which the default-deny jail forbids, so the curated toolchain is Nix-
    // installed at build time into a persistent /opt/toolchain profile and is on PATH from the read-only
    // image — needing ZERO runtime egress (not even cache.nixos.org). This is the toolchain-sideload
    // edge case's real intent: tools are available in a live session and running one never severs it
    // (G-16). Adding an ARBITRARY new tool at runtime is a documented v1.x item.
    [RequiresDockerFact]
    public async Task PrebakedToolchain_ShouldBeAvailableInLiveSession()
    {
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync();

        // Every curated tool resolves on PATH inside the hardened jail (no runtime fetch — A6 intact).
        foreach (var tool in new[] { "jq", "rg", "fd", "make", "node", "python3", "go" })
        {
            var where = await fx.ExecAsync(handle.ContainerId, "sh", "-c", "command -v " + tool);
            Assert.True(where.ExitCode == 0,
                $"pre-baked tool '{tool}' not on PATH in the sandbox.\nstdout: {where.Stdout}\nstderr: {where.Stderr}");
        }

        // Running a tool mid-session succeeds and does not sever the session (the G-16 rationale for
        // baking over a runtime image build).
        var run = await fx.ExecAsync(handle.ContainerId, "sh", "-c", "jq --version");
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("jq", run.Stdout, StringComparison.Ordinal);
    }

    // MG-18 — the regression the finding asks for: a network that already exists under the agent
    // network's name but is NOT internal. Reuse-by-name accepted it, so every jail attached to it had a
    // default route to the internet with no error reported anywhere. It must now fail closed.
    [RequiresDockerFact]
    public async Task PreexistingNonInternalAgentNetwork_ShouldRefuseToSpawn_NotSilentlyDisableEgress()
    {
        await using var fx = new SandboxFixture();

        // Clear whatever a previous test left behind, then plant the drifted network.
        await fx.ForceRemoveProxyAndNetworksAsync();
        await fx.Docker.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = EgressProxyConfigurator.AgentNetworkName,
            Driver = "bridge",
            Internal = false, // the drift
            Labels = new Dictionary<string, string>
            {
                [EgressProxyConfigurator.NetworkRoleLabel] = EgressProxyConfigurator.RoleFor(true),
            },
        });

        var ex = await Assert.ThrowsAsync<EgressNetworkDriftException>(() => fx.EnsureEgressReadyAsync());
        Assert.Equal(EgressProxyConfigurator.AgentNetworkName, ex.NetworkName);
    }

    [RequiresDockerFact]
    public async Task ProxyContainer_ShouldRunHardened_NoNetRaw_SeccompAndReadOnlyRootfs()
    {
        // MG-25 read off the live proxy: the chokepoint every agent's egress crosses carries the same
        // class of controls as the jails it fronts.
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();

        var inspect = await fx.Docker.Containers.InspectContainerAsync(EgressProxyConfigurator.ProxyContainerName);
        var host = inspect.HostConfig;

        Assert.Contains("ALL", host.CapDrop);
        Assert.Contains("NET_ADMIN", host.CapAdd);
        Assert.DoesNotContain("NET_RAW", host.CapAdd);
        Assert.Contains(host.SecurityOpt, o => o.StartsWith("seccomp=", StringComparison.Ordinal));
        Assert.True(host.ReadonlyRootfs);
        Assert.True(host.Memory > 0);
        Assert.True(host.NanoCPUs > 0);

        // The read-only rootfs must not have broken the proxy: both daemons are up and the policy the
        // daemon rendered is on the /run tmpfs where reload.sh reads it.
        Assert.True(inspect.State.Running);
        var filter = await fx.Engine.ExecAsync(inspect.ID, new[] { "cat", "/run/mainguard/tinyproxy-filter" });
        Assert.Equal(0, filter.ExitCode);
        Assert.Contains("api.anthropic.com", filter.Stdout, StringComparison.Ordinal);
    }

    [RequiresDockerFact]
    public async Task DirectGitHostClone_FromAgent_ShouldFailFast()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        // A6: the git host is deliberately absent from the agent allowlist — a direct clone fails fast.
        var sw = Stopwatch.StartNew();
        var result = await fx.ExecAsync(handle.ContainerId,
            "git", "-c", "http.lowSpeedLimit=1", "-c", "http.lowSpeedTime=5",
            "clone", "--depth", "1", "https://github.com/git/git.git", "/tmp/should-not-clone");
        sw.Stop();

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"expected fast refusal, took {sw.Elapsed}");
    }
}
