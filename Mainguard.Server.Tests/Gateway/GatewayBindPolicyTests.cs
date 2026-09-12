using System;
using System.Linq;
using System.Net;
using Mainguard.Server.Gateway;
using Xunit;

namespace Mainguard.Server.Tests.Gateway;

/// <summary>
/// MG-13 — the model gateway is the ONE listener allowed off loopback, and this is the rule that keeps
/// that relaxation narrow.
///
/// <para>The daemon otherwise binds loopback only ("never binds a wildcard / non-loopback address",
/// invariant 2), which is load-bearing: MG-19's finding is that loopback + a bearer token IS the trust
/// boundary. The gateway has to be reachable from the agent network (an <c>Internal=true</c> Docker
/// network where <c>127.0.0.1</c> means the container itself), so it may bind the Docker bridge — but
/// a wildcard bind would turn a jail-facing port into an internet-facing one, which is precisely the
/// mistake this refuses.</para>
/// </summary>
public sealed class GatewayBindPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]     // loopback
    [InlineData("::1")]           // loopback v6
    [InlineData("172.17.0.1")]    // Docker's default bridge — the real target
    [InlineData("172.31.255.254")]
    [InlineData("10.202.0.1")]    // Mainguard's dedicated subnet
    [InlineData("192.168.1.5")]
    [InlineData("169.254.10.1")]  // link-local
    [InlineData("fd00::1")]       // unique-local v6
    public void PrivateAndLoopbackAddresses_ArePermitted(string ip)
    {
        Assert.True(GatewayBindPolicy.IsPermitted(IPAddress.Parse(ip), out var reason), reason);
        Assert.Empty(reason);
    }

    // The failure this policy exists for: 0.0.0.0 listens on EVERY interface, including whatever
    // public network the host is on.
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void WildcardBind_IsRefused(string ip)
    {
        Assert.False(GatewayBindPolicy.IsPermitted(IPAddress.Parse(ip), out var reason));
        Assert.Contains("wildcard", reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]    // just OUTSIDE 172.16/12 — the off-by-one that would open a public bind
    [InlineData("172.15.0.1")]    // just BELOW the range
    [InlineData("11.0.0.1")]      // adjacent to 10/8
    [InlineData("2606:4700::1")]  // public v6
    public void PublicAddresses_AreRefused(string ip)
    {
        Assert.False(GatewayBindPolicy.IsPermitted(IPAddress.Parse(ip), out var reason));
        Assert.Contains("public", reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NullAddress_IsRefused()
    {
        Assert.False(GatewayBindPolicy.IsPermitted(null, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    // The invariant in one assertion: nothing routable from the public internet is ever permitted.
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("8.8.8.8")]
    [InlineData("203.0.113.7")]
    public void NothingPubliclyRoutable_IsEverPermitted(string ip)
    {
        Assert.False(GatewayBindPolicy.IsPermitted(IPAddress.Parse(ip), out _));
    }

    // ---- MG-4 item 3: the gateway is ON by default -----------------------------------------------

    /// <summary>
    /// The default posture. This is the whole point of MG-4 item 3: the gateway used to be reachable
    /// only through <c>MAINGUARD_GATEWAY_BIND</c>, which nothing in the repo ever set, so a BYOK jail
    /// received the raw provider key in every supported deployment.
    ///
    /// <para>Written as an implication rather than "is not null" so it is honest on a host with no
    /// private address (where disabled IS the correct answer): if the resolver found one, the default
    /// must be it, and it must satisfy the bind policy.</para>
    /// </summary>
    [Fact]
    public void ByDefault_TheGatewayBinds_WhateverTheResolverFound()
    {
        var resolved = GatewayBindPolicy.TryResolvePrivateHostAddress();

        Assert.Equal(resolved, new DaemonOptions().GatewayBindAddress);

        if (resolved is not null)
        {
            Assert.True(GatewayBindPolicy.IsPermitted(IPAddress.Parse(resolved), out var reason), reason);
        }
    }

    // ---- F25: the default bind is narrow, not LAN-facing ------------------------------------------

    /// <summary>
    /// F25 — the default must never be an address the operator's LAN can reach.
    ///
    /// <para>The finding: the resolver picked the lexicographically lowest private IPv4 on any up NIC,
    /// which on a Mac without a 10.x/172.x interface is the Wi-Fi address. The daemon therefore
    /// published a plaintext HTTP gateway, authenticated by a token the agent itself can read and
    /// commit, on whatever network the laptop was on. The default is now loopback on Docker Desktop
    /// (reached through <c>host.docker.internal</c>) and the Docker bridge on Linux — both reachable
    /// only from this machine and its containers.</para>
    ///
    /// <para>Asserted as a property of the resolved address rather than by mocking the platform, so it
    /// holds on whichever host runs the suite.</para>
    /// </summary>
    [Fact]
    public void DefaultBind_IsNeverALanFacingAddress()
    {
        var resolved = GatewayBindPolicy.TryResolvePrivateHostAddress();
        if (resolved is null)
        {
            return; // no bridge and not Docker Desktop — disabled is the correct answer.
        }

        var address = IPAddress.Parse(resolved);
        if (IPAddress.IsLoopback(address))
        {
            return; // narrowest possible; nothing off the box can reach it at all.
        }

        // Otherwise it must be an address a Docker bridge holds on THIS host — never a Wi-Fi/Ethernet
        // address the LAN shares. "Assigned to an interface named docker0 or br-*" is the check, because
        // that is precisely the property the resolver now selects on.
        var bridges = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.Name == "docker0" || n.Name.StartsWith("br-", System.StringComparison.Ordinal))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address.ToString())
            .ToArray();

        Assert.Contains(resolved, bridges);
    }

    // ---- Audit B1: an IDLE docker0 is still the bind address ---------------------------------------

    /// <summary>
    /// B1, the regression this suite failed to catch the first time: the resolver filtered interfaces to
    /// <c>OperationalStatus == Up</c> before matching <c>docker0</c>.
    ///
    /// <para>A Linux bridge reports CARRIER, not admin state. With no port attached it is
    /// <c>&lt;NO-CARRIER,BROADCAST,MULTICAST,UP&gt; state DOWN</c> — measured on this engine against a
    /// freshly created, empty bridge — and .NET maps that to <c>Down</c>. Mainguard puts every container
    /// on a user-defined network and never on the default bridge, so docker0 is idle on exactly the hosts
    /// this resolver exists for: the filter excluded it, the resolver returned null, the gateway was
    /// disabled, and every BYOK spawn handed the raw provider key to its jail.</para>
    ///
    /// <para>The status is now absent from the selector's input shape entirely, so this asserts the
    /// property rather than a particular filter: a docker0 that holds a private IPv4 IS the answer,
    /// whatever its carrier is doing.</para>
    /// </summary>
    [Fact]
    public void IdleDockerBridge_IsStillTheBindAddress()
    {
        var resolved = GatewayBindPolicy.SelectDockerBridgeAddress(new[]
        {
            Nic("eth0", "192.168.1.20"),
            // The idle default bridge: addressed, admin-up, carrier-down.
            Nic("docker0", "172.17.0.1"),
        });

        Assert.Equal("172.17.0.1", resolved);
    }

    /// <summary>Only <c>docker0</c>. A per-agent segment's own <c>br-*</c> bridge is on-link from inside
    /// that segment, so binding there would let a jail dial the gateway past its proxy.</summary>
    [Fact]
    public void SegmentBridges_AreNeverChosen()
    {
        var resolved = GatewayBindPolicy.SelectDockerBridgeAddress(new[]
        {
            Nic("br-1f9ce2f2f6d2", "192.168.166.1"),
            Nic("mainguard-agent", "10.202.0.1"),
            Nic("eth0", "10.0.5.7"),
        });

        Assert.Null(resolved);
    }

    /// <summary>A LAN address on a non-bridge interface is never the answer — F25's whole point.</summary>
    [Fact]
    public void WifiAddress_IsNeverChosen_EvenWhenItIsTheOnlyPrivateOne()
    {
        Assert.Null(GatewayBindPolicy.SelectDockerBridgeAddress(new[] { Nic("en0", "192.168.1.42") }));
    }

    /// <summary>Loopback, link-local and IPv6 on docker0 are not bindable gateway addresses; the IPv4
    /// unicast is, and it is chosen deterministically when there are several.</summary>
    [Fact]
    public void DockerBridge_PicksItsPrivateIPv4_Deterministically()
    {
        var resolved = GatewayBindPolicy.SelectDockerBridgeAddress(new[]
        {
            new GatewayBindPolicy.HostInterface("docker0", new[]
            {
                IPAddress.Parse("fe80::1c4c:38ff:fe9a:ae18"),
                IPAddress.Parse("169.254.7.7"),
                IPAddress.Parse("172.18.0.1"),
                IPAddress.Parse("172.17.0.1"),
            }),
        });

        Assert.Equal("172.17.0.1", resolved);
    }

    /// <summary>A host with no default bridge at all still resolves to null — disabled is the correct
    /// answer there, and it is the only case that may produce one.</summary>
    [Fact]
    public void NoDockerBridge_ResolvesToNull() =>
        Assert.Null(GatewayBindPolicy.SelectDockerBridgeAddress(Array.Empty<GatewayBindPolicy.HostInterface>()));

    /// <summary>
    /// The host-level half of B1, and the one that fails on the Linux CI runner if the resolver ever
    /// regresses: on a Linux host that HAS a docker0 holding a private IPv4, the resolver must return
    /// that address. The two existing default-bind tests both pass when the resolver returns null, which
    /// is exactly why the regression shipped.
    /// </summary>
    [Fact]
    public void OnLinux_WithADockerBridge_TheResolverMustReturnIt()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // docker0 lives in the VM on Docker Desktop/OrbStack hosts; there is nothing to assert.
        }

        var bridge = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.Name == "docker0")
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(a => a.ToString())
            .FirstOrDefault();

        if (bridge is null)
        {
            return; // no default bridge on this host (rootless Docker, `bridge: none`) — nothing to bind.
        }

        var resolved = GatewayBindPolicy.TryResolvePrivateHostAddress();

        Assert.True(
            resolved is not null,
            $"this host has docker0 at {bridge} and the gateway bind resolved to NOTHING. The daemon would "
            + "disable the gateway and every BYOK spawn would inject the raw provider key into its jail.");
        Assert.Equal(bridge, resolved);
    }

    private static GatewayBindPolicy.HostInterface Nic(string name, string address) =>
        new(name, new[] { IPAddress.Parse(address) });

    /// <summary>
    /// F25 — the bind address and the address a CONTAINER dials are the same string except for
    /// loopback, and loopback is the case that matters: a jail's <c>NO_PROXY</c> covers
    /// <c>127.0.0.1</c>, so a jail pointed at loopback dials ITSELF rather than the daemon.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1", "host.docker.internal")]
    [InlineData("127.0.0.53", "host.docker.internal")]
    [InlineData("::1", "host.docker.internal")]
    [InlineData("172.17.0.1", "172.17.0.1")]
    [InlineData("10.202.0.1", "10.202.0.1")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void ProxyReachableHostFor_TranslatesOnlyLoopback(string? bind, string? expected) =>
        Assert.Equal(expected, GatewayBindPolicy.ProxyReachableHostFor(bind));

    /// <summary>The alias is one contract stated in two assemblies (the daemon names it, the proxy is
    /// created with it). A drift would silently disable confinement, so it is pinned.</summary>
    [Fact]
    public void ProxyReachableAlias_MatchesTheOneTheProxyIsCreatedWith() =>
        Assert.Equal(
            Mainguard.Agents.Agents.Sandbox.EgressProxyConfigurator.GatewayHostAlias,
            GatewayBindPolicy.ProxyReachableHostAlias);

    /// <summary>
    /// The memoization contract, unchanged by F25 and restated because the doc comment depends on it:
    /// the chosen address is written into every confined jail's base-URL variable, so it has to stay
    /// stable for the life of the process.
    /// </summary>
    [Fact]
    public void DefaultBind_IsMemoized_SoItCannotDriftUnderARunningDaemon() =>
        Assert.Equal(
            GatewayBindPolicy.TryResolvePrivateHostAddress(),
            GatewayBindPolicy.TryResolvePrivateHostAddress());

    /// <summary>
    /// The escape hatch has to keep working, from either source, or "purely additive" is not true for an
    /// operator who wants the old posture back.
    /// </summary>
    [Theory]
    [InlineData("off", null)]
    [InlineData("OFF", null)]
    [InlineData("  off  ", null)]
    [InlineData("172.17.0.1", "172.17.0.1")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    public void ResolveBindAddress_HonoursOffAndExplicitAddresses(string configured, string? expected)
        => Assert.Equal(expected, DaemonOptions.ResolveBindAddress(configured));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public void ResolveBindAddress_UnsetOrAuto_ResolvesTheSameAddress(string? configured)
        => Assert.Equal(
            GatewayBindPolicy.TryResolvePrivateHostAddress(),
            DaemonOptions.ResolveBindAddress(configured));

    /// <summary>
    /// B1's second half: "no gateway" has two causes with the same consequence (the raw provider key in
    /// every BYOK jail) and very different meanings. An operator who wrote <c>off</c> chose it; a bind
    /// that auto-resolved to nothing is a defect, and the daemon says so at error level at boot and per
    /// spawn. They must not be conflated.
    /// </summary>
    [Fact]
    public void GatewayOff_IsDistinguishedFromABindThatResolvedToNothing()
    {
        var chosen = new DaemonOptions
        {
            GatewayBindConfigured = "off",
            GatewayBindAddress = DaemonOptions.ResolveBindAddress("off"),
        };
        Assert.Null(chosen.GatewayBindAddress);
        Assert.False(chosen.GatewayDisabledUnintentionally);

        // Nobody asked for this one — it is what a resolver that finds nothing produces.
        var unresolved = new DaemonOptions { GatewayBindConfigured = null, GatewayBindAddress = null };
        Assert.True(unresolved.GatewayDisabledUnintentionally);

        // And a daemon that DID bind one is never "unintentionally disabled".
        var bound = new DaemonOptions { GatewayBindConfigured = null, GatewayBindAddress = "172.17.0.1" };
        Assert.False(bound.GatewayDisabledUnintentionally);
    }

    /// <summary>
    /// An impermissible EXPLICIT address must still reach the policy and fail startup loudly — the
    /// resolver must not quietly swallow it into an auto-resolved one.
    /// </summary>
    [Fact]
    public void ResolveBindAddress_PassesAnImpermissibleAddressThrough_SoStartupCanRefuseIt()
    {
        Assert.Equal("0.0.0.0", DaemonOptions.ResolveBindAddress("0.0.0.0"));
        Assert.False(GatewayBindPolicy.IsPermitted(IPAddress.Parse("0.0.0.0"), out _));
    }
}
