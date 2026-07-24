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
}
