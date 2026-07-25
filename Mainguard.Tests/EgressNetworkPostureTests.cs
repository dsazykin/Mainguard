using System.Collections.Generic;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-18 — the two halves of the egress backstop that were resting on assumptions.
///
/// <para><b>(1) Network reuse.</b> <c>EnsureNetworkAsync</c> looked up <c>mainguard-agents</c> by NAME
/// and returned its ID without ever checking that it is still <c>Internal</c>. Internal is the property
/// that keeps agents off the outside world — not the proxy, not <c>HTTP_PROXY</c> — so a pre-existing or
/// drifted network with that name silently gave every jail unrestricted egress, and nothing anywhere
/// reported it: the proxy still started, the allowlist still rendered, the agents were just on the open
/// internet. These assert the reuse gate fails CLOSED on that drift.</para>
///
/// <para><b>(2) The iptables backstop.</b> Its ACCEPT rules were <c>--dport</c>-only with no
/// destination — "to port 53 anywhere", which is not a restriction on a forwarding path. They are now
/// constrained to the proxy's own address.</para>
/// </summary>
public sealed class EgressNetworkPostureTests
{
    private const string AgentNet = EgressProxyConfigurator.AgentNetworkName;
    private const string EgressNet = EgressProxyConfigurator.EgressNetworkName;

    private static NetworkResponse Network(string name, bool isInternal, string? role) =>
        new()
        {
            ID = "net-" + name,
            Name = name,
            Internal = isInternal,
            Labels = role is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { [EgressProxyConfigurator.NetworkRoleLabel] = role },
        };

    // ---- the drift that disabled enforcement ----

    [Fact]
    public void ReusingANonInternalAgentNetwork_ThrowsTyped()
    {
        // The finding, verbatim: a network named `mainguard-agents` that is NOT internal. Reuse-by-name
        // accepted it and every jail attached to it had a default route to the internet.
        var drifted = Network(AgentNet, isInternal: false, role: "agent-net");

        var ex = Assert.Throws<EgressNetworkDriftException>(() =>
            EgressProxyConfigurator.AssertNetworkMatchesPolicy(drifted, AgentNet, isInternal: true));

        Assert.Equal(AgentNet, ex.NetworkName);
        // The message has to be actionable — this fails a spawn, so it must say what to do.
        Assert.Contains("docker network rm " + AgentNet, ex.Message);
    }

    [Fact]
    public void ReusingAnUnstampedNetworkOfTheSameName_ThrowsTyped()
    {
        // Internal, but not ours: something else created a `mainguard-agents`. We only ever reuse a
        // network mainguard itself stamped, because everything else about it is unknown.
        var foreign = Network(AgentNet, isInternal: true, role: null);

        Assert.Throws<EgressNetworkDriftException>(() =>
            EgressProxyConfigurator.AssertNetworkMatchesPolicy(foreign, AgentNet, isInternal: true));
    }

    [Fact]
    public void ReusingAMislabelledNetwork_ThrowsTyped()
    {
        var mislabelled = Network(AgentNet, isInternal: true, role: "egress-net");

        Assert.Throws<EgressNetworkDriftException>(() =>
            EgressProxyConfigurator.AssertNetworkMatchesPolicy(mislabelled, AgentNet, isInternal: true));
    }

    [Fact]
    public void ReusingAnInternalisedEgressNetwork_ThrowsTyped()
    {
        // The mirror-image drift: the proxy's egress leg turned internal cuts every upstream off, so
        // nothing can leave at all. Same gate, opposite direction.
        var drifted = Network(EgressNet, isInternal: true, role: "egress-net");

        Assert.Throws<EgressNetworkDriftException>(() =>
            EgressProxyConfigurator.AssertNetworkMatchesPolicy(drifted, EgressNet, isInternal: false));
    }

    [Fact]
    public void ReusingTheNetworksWeCreated_IsAccepted()
    {
        // Non-vacuity in the other direction: the gate must not reject the networks the configurator
        // itself creates, or every second spawn would fail.
        EgressProxyConfigurator.AssertNetworkMatchesPolicy(
            Network(AgentNet, isInternal: true, role: EgressProxyConfigurator.RoleFor(true)), AgentNet, isInternal: true);
        EgressProxyConfigurator.AssertNetworkMatchesPolicy(
            Network(EgressNet, isInternal: false, role: EgressProxyConfigurator.RoleFor(false)), EgressNet, isInternal: false);
    }

    // ---- the backstop's destination constraint ----

    [Fact]
    public void Backstop_ConstrainsEveryAcceptToTheProxyAddress()
    {
        const string proxyAddress = "172.30.0.2";
        var script = EgressProxyConfig.RenderIptablesScript(EgressProxyConfigurator.ProxyPort, proxyAddress);

        foreach (var line in script.Split('\n'))
        {
            if (!line.Contains("-j ACCEPT") || line.Contains("--state") || line.Contains("-i lo"))
            {
                continue; // conntrack + loopback ACCEPTs are destination-agnostic by nature
            }

            // The bug: `-p tcp --dport 53 -j ACCEPT` accepts a DNS query to ANY destination.
            Assert.Contains("-d " + proxyAddress, line);
        }
    }

    [Fact]
    public void Backstop_FiltersWhatReachesTheProxy_NotJustWhatItForwards()
    {
        // The FORWARD chain lives in the PROXY's netns; agent packets are routed by the host-side
        // bridge and never transit it, so a FORWARD-only backstop filtered nothing an agent ever sent.
        // The chain that does sit in the path is INPUT — traffic arriving AT the proxy.
        var script = EgressProxyConfig.RenderIptablesScript(EgressProxyConfigurator.ProxyPort, "172.30.0.2");

        Assert.Contains("iptables -P INPUT DROP", script);
        Assert.Contains("iptables -A INPUT -p tcp -d 172.30.0.2 --dport 8888 -j ACCEPT", script);
        Assert.Contains("iptables -A INPUT -p udp -d 172.30.0.2 --dport 53 -j ACCEPT", script);
        Assert.Contains("iptables -A INPUT -j DROP", script);

        // The proxy must still be able to hear its own upstream replies and its own loopback traffic,
        // or a default-deny INPUT chain takes the whole egress path down with it.
        Assert.Contains("iptables -A INPUT -i lo -j ACCEPT", script);
        Assert.Contains("iptables -A INPUT -m state --state ESTABLISHED,RELATED -j ACCEPT", script);

        // FORWARD is kept as defence in depth for the day this container is given a routing role.
        Assert.Contains("iptables -P FORWARD DROP", script);
    }
}
