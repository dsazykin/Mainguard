using System;
using System.Collections.Generic;
using System.Linq;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-36 — east-west isolation between agent jails, pinned at the level that can be asserted without a
/// Docker daemon. The behavioural half (agent A genuinely cannot reach agent B) is
/// <c>SandboxNetworkIsolationDockerTests</c>.
///
/// <para><b>Why segments rather than an intra-network policy.</b> Docker's one knob for east-west
/// traffic on a bridge is <c>com.docker.network.bridge.enable_icc=false</c>, and it is all-or-nothing:
/// measured against a real daemon, it drops jail→jail AND jail→proxy alike, which takes the whole
/// egress path down with it. There is no per-peer exception, so "one network, tenants isolated from
/// each other but not from the proxy" is not expressible. One internal network per agent, with the
/// proxy as the only other member, is — and it needs no host-level iptables, which matters because the
/// daemon runs as an unprivileged VM user whose only capability is the docker socket.</para>
/// </summary>
public sealed class EgressSegmentationTests
{
    private const string RepoHash = "abcdef0123456789abcdef";
    private const string ProxyAddressA = "172.30.0.2";
    private const string ProxyAddressB = "172.31.0.2";

    // ---- segment naming ----

    [Fact]
    public void EachAgent_GetsItsOwnSegmentName()
    {
        var a = EgressProxyConfigurator.AgentSegmentName(RepoHash, "agent-a");
        var b = EgressProxyConfigurator.AgentSegmentName(RepoHash, "agent-b");

        Assert.NotEqual(a, b);
        Assert.StartsWith(EgressProxyConfigurator.AgentSegmentPrefix, a, StringComparison.Ordinal);

        // Stable across calls: a per-spawn random name would leak one docker network per relaunch, and
        // Docker's default local bridge pool is only ~32 networks deep.
        Assert.Equal(a, EgressProxyConfigurator.AgentSegmentName(RepoHash, "agent-a"));

        // Correlated with the jail it isolates, so an operator reading `docker network ls` beside
        // `docker ps` can pair them without a lookup table.
        Assert.Contains(
            ContainerSpecBuilder.ContainerName(RepoHash, "agent-a")["mainguard-".Length..], a, StringComparison.Ordinal);
    }

    [Fact]
    public void SegmentNames_SurviveAnAgentIdThatIsNotDockerSafe()
    {
        // Docker network names are constrained; the agent id is not. The same sanitisation the
        // container name uses has to apply, or the segment create fails for an id a user can type.
        var name = EgressProxyConfigurator.AgentSegmentName(RepoHash, "agent/one two:three");
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(' ', name);
        Assert.DoesNotContain(':', name);
    }

    // ---- the predicate every fail-closed gate now keys on ----

    [Fact]
    public void DefaultDenyPredicate_CoversTheSharedNetworkAndEverySegment_AndNothingElse()
    {
        Assert.True(EgressProxyConfigurator.IsDefaultDenyAgentNetwork(EgressProxyConfigurator.AgentNetworkName));
        Assert.True(EgressProxyConfigurator.IsDefaultDenyAgentNetwork(
            EgressProxyConfigurator.AgentSegmentName(RepoHash, "agent-a")));

        // The proxy's egress-capable leg is emphatically NOT default-deny — pinning a resolver there
        // (or asserting Internal on it) would cut the proxy off from every upstream.
        Assert.False(EgressProxyConfigurator.IsDefaultDenyAgentNetwork(EgressProxyConfigurator.EgressNetworkName));
        Assert.False(EgressProxyConfigurator.IsDefaultDenyAgentNetwork("bridge"));
        Assert.False(EgressProxyConfigurator.IsDefaultDenyAgentNetwork(null));
    }

    // THE regression this whole change could most easily introduce. MG-7's fail-closed resolver pin
    // used to test `NetworkName == "mainguard-agents"`. Moving jails onto per-agent segments would have
    // silently switched that gate off: no exception, no log, just a jail back on Docker's embedded
    // 127.0.0.11 resolver with DNS exfiltration walking straight out of a "default-deny" network.
    [Fact]
    public void AJailOnASegment_WithNoResolverPin_IsStillRefused()
    {
        var segment = EgressProxyConfigurator.AgentSegmentName(RepoHash, "agent-a");

        var ex = Assert.Throws<SandboxSpecException>(() => ContainerSpecBuilder.Build(Request(segment, dns: null)));

        Assert.Contains("MG-7", ex.Message, StringComparison.Ordinal);
        Assert.Contains(segment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AJailOnASegment_IsBuiltOntoThatSegment_AndPointedAtThatSegmentsProxy()
    {
        var segment = EgressProxyConfigurator.AgentSegmentName(RepoHash, "agent-a");

        var create = ContainerSpecBuilder.Build(Request(segment, ProxyAddressA));

        Assert.Equal(segment, create.HostConfig.NetworkMode);
        Assert.Equal(ProxyAddressA, Assert.Single(create.HostConfig.DNS));

        // The proxy is addressed by IP, not by name: one dnsmasq cannot answer `mainguard-egress-proxy`
        // with a different address per segment, and every OTHER segment's address is unreachable from
        // here by construction.
        Assert.Contains($"HTTP_PROXY=http://{ProxyAddressA}:{EgressProxyConfigurator.ProxyPort}", create.Env);
        Assert.Contains($"https_proxy=http://{ProxyAddressA}:{EgressProxyConfigurator.ProxyPort}", create.Env);
    }

    // ---- the proxy's address set, and the backstop rendered from it ----

    [Fact]
    public void ProxyAddresses_AreCollectedFromEverySegment_ButNeverFromTheEgressLeg()
    {
        var inspect = Inspect(new Dictionary<string, string>
        {
            [EgressProxyConfigurator.AgentNetworkName] = "172.20.0.2",
            [EgressProxyConfigurator.AgentSegmentName(RepoHash, "agent-a")] = ProxyAddressA,
            [EgressProxyConfigurator.AgentSegmentName(RepoHash, "agent-b")] = ProxyAddressB,
            [EgressProxyConfigurator.EgressNetworkName] = "10.9.9.9",
        });

        var addresses = EgressProxyConfigurator.ProxyAddressesOf(inspect);

        Assert.Equal(new[] { "172.20.0.2", ProxyAddressA, ProxyAddressB }, addresses);
        Assert.DoesNotContain("10.9.9.9", addresses);
    }

    [Fact]
    public void ProxyAddress_IsResolvedPerSegment()
    {
        var segmentA = EgressProxyConfigurator.AgentSegmentName(RepoHash, "agent-a");
        var segmentB = EgressProxyConfigurator.AgentSegmentName(RepoHash, "agent-b");
        var inspect = Inspect(new Dictionary<string, string>
        {
            [segmentA] = ProxyAddressA,
            [segmentB] = ProxyAddressB,
        });

        Assert.Equal(ProxyAddressA, EgressProxyConfigurator.ProxyAddressOf(inspect, segmentA));
        Assert.Equal(ProxyAddressB, EgressProxyConfigurator.ProxyAddressOf(inspect, segmentB));
        Assert.Null(EgressProxyConfigurator.ProxyAddressOf(inspect, "mainguard-agent-nobody"));
    }

    // MG-18's destination constraint is what makes the backstop's ACCEPTs meaningful ("to port 53
    // anywhere" is not a restriction). Segmenting gives the proxy an address per segment, so a
    // single-address render would DROP the traffic of every segment created after the first — the
    // default-deny chain would quietly break egress for agents 2..N.
    [Fact]
    public void Backstop_AdmitsEveryProxyAddress_AndStillEndsInDeny()
    {
        var script = EgressProxyConfig.RenderIptablesScript(
            EgressProxyConfigurator.ProxyPort, new[] { ProxyAddressA, ProxyAddressB });

        foreach (var address in new[] { ProxyAddressA, ProxyAddressB })
        {
            Assert.Contains($"iptables -A INPUT -p tcp -d {address} --dport {EgressProxyConfigurator.ProxyPort} -j ACCEPT", script);
            Assert.Contains($"iptables -A INPUT -p udp -d {address} --dport 53 -j ACCEPT", script);
        }

        Assert.Contains("iptables -P INPUT DROP", script);
        Assert.EndsWith("iptables -A FORWARD -j DROP\n", script, StringComparison.Ordinal);

        // Every ACCEPT is still destination-constrained — the MG-18 property, preserved per address.
        foreach (var line in script.Split('\n'))
        {
            if (!line.Contains("-j ACCEPT") || line.Contains("--state") || line.Contains("-i lo"))
            {
                continue;
            }

            Assert.True(
                line.Contains(" -d " + ProxyAddressA) || line.Contains(" -d " + ProxyAddressB),
                $"unconstrained ACCEPT survived the multi-address render: {line}");
        }

        // The terminal DROP is reached, not shadowed: every ACCEPT precedes it.
        var lines = script.Split('\n').ToList();
        Assert.True(
            lines.FindLastIndex(l => l.Contains("-j ACCEPT")) < lines.FindIndex(l => l == "iptables -A INPUT -j DROP")
            || lines.FindLastIndex(l => l.Contains("INPUT") && l.Contains("-j ACCEPT"))
               < lines.FindIndex(l => l == "iptables -A INPUT -j DROP"),
            "an ACCEPT landed after the terminal INPUT DROP");
    }

    [Fact]
    public void Backstop_SingleAddressForm_IsUnchanged()
    {
        // The pre-MG-36 overload has to keep rendering exactly what it rendered, or the MG-18 tests are
        // asserting a shape nothing produces any more.
        Assert.Equal(
            EgressProxyConfig.RenderIptablesScript(EgressProxyConfigurator.ProxyPort, new[] { ProxyAddressA }),
            EgressProxyConfig.RenderIptablesScript(EgressProxyConfigurator.ProxyPort, ProxyAddressA));
    }

    private static ContainerSpecRequest Request(string network, string? dns) =>
        new(
            RepoHash: RepoHash,
            AgentId: "agent-a",
            WorktreePath: "/home/mainguard/mainguard/worktrees/abc/agent-a",
            ImageRef: "sha256:" + new string('a', 64),
            Limits: SandboxLimits.Default,
            NetworkName: network,
            Credentials: CredTmpfsSpec.Create(1000, 1001),
            ProxyUrl: dns is null ? "http://mainguard-egress-proxy:8888" : $"http://{dns}:{EgressProxyConfigurator.ProxyPort}",
            DnsServerAddress: dns);

    private static ContainerInspectResponse Inspect(IReadOnlyDictionary<string, string> networkToAddress) =>
        new()
        {
            NetworkSettings = new NetworkSettings
            {
                Networks = networkToAddress.ToDictionary(
                    kv => kv.Key, kv => new EndpointSettings { IPAddress = kv.Value }),
            },
        };
}
