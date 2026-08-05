using System;
using System.Linq;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-4 — the rendered egress policy that lets a CONFINED jail actually reach the model gateway, and
/// the two things it must not disturb on the way.
///
/// <para><b>Why this exists.</b> PR #298 pointed a confined CLI's base-URL variable at
/// <c>http://&lt;gateway&gt;:&lt;port&gt;</c> and stopped there. The jail's <c>HTTP_PROXY</c>/<c>HTTPS_PROXY</c>
/// name the egress proxy and <c>NO_PROXY</c> covers only loopback plus the internal git proxy, so that
/// request arrives at tinyproxy naming the GATEWAY as its destination — and tinyproxy runs
/// <c>FilterDefaultDeny</c> against an anchored allowlist of provider and CLI-service hosts that has
/// never contained the daemon's own address. Mainguard's own proxy refused it. Turning the gateway on
/// would therefore have BROKEN every BYOK agent it touched rather than metering it, and nothing in the
/// suite would have said so, because every gateway test to date built the request in-process.</para>
///
/// <para>The fix is deliberately one allowlist entry. It moves no existing host's route.</para>
/// </summary>
public sealed class GatewayReachabilityPolicyTests
{
    private const string GatewayEndpoint = "10.42.0.1:5251";
    private const string GatewayHost = "10.42.0.1";

    private static EgressAllowlist Defaults() => EgressAllowlist.WithDefaults(new InMemoryAuditLog());

    /// <summary>The filter regex tinyproxy would need for a request destined to the gateway.</summary>
    private static string GatewayFilterPattern => EgressProxyConfig.RenderHostPattern(GatewayHost);

    // ---- the reachability the confinement depends on ---------------------------------------------

    [Fact]
    public void WithConfinementConfigured_TheRenderedFilter_PermitsTheGatewaysOwnHost()
    {
        var effective = EgressProxyConfigurator.CombineGatewayHost(Defaults(), GatewayEndpoint);

        var filter = EgressProxyConfig.RenderTinyproxyFilter(effective);

        Assert.Contains(GatewayFilterPattern, filter, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression pin for the defect this change fixes. Without it the daemon points the CLI somewhere
    /// its own proxy will not carry it to — the failure is a 403 from Mainguard, not from the provider,
    /// and it looks exactly like a provider outage from inside the jail.
    /// </summary>
    [Fact]
    public void WithoutTheGatewayEntry_TheRenderedFilter_WouldRefuseTheGateway()
    {
        var filter = EgressProxyConfig.RenderTinyproxyFilter(Defaults());

        Assert.DoesNotContain(GatewayFilterPattern, filter, StringComparison.Ordinal);
    }

    /// <summary>
    /// The port must be stripped. tinyproxy's <c>Filter</c> matches a destination HOSTNAME, so rendering
    /// the <c>host:port</c> form would emit <c>^10\.42\.0\.1:5251$</c> — a pattern no request can match.
    /// That is the same silent-no-op class as declaring the wrong base-URL variable: perfectly
    /// applied-looking policy that never fires.
    /// </summary>
    [Theory]
    [InlineData("10.42.0.1:5251", "10.42.0.1")]
    [InlineData("127.0.0.1:5251", "127.0.0.1")]
    [InlineData("10.42.0.1", "10.42.0.1")]      // already bare
    [InlineData("[fd00::1]:5251", "fd00::1")]   // bracketed IPv6 literal
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void GatewayHostOf_TakesTheHostOnly(string? endpoint, string? expected)
        => Assert.Equal(expected, EgressProxyConfigurator.GatewayHostOf(endpoint));

    [Fact]
    public void TheGatewayEntry_IsRenderedAsABareHost_NeverHostColonPort()
    {
        var effective = EgressProxyConfigurator.CombineGatewayHost(Defaults(), GatewayEndpoint);

        var filter = EgressProxyConfig.RenderTinyproxyFilter(effective);

        Assert.DoesNotContain(":5251", filter, StringComparison.Ordinal);
    }

    // ---- what must NOT change: the OAuth path ----------------------------------------------------

    /// <summary>
    /// <b>The highest-value assertion in this file.</b> Confinement must not put a tinyproxy
    /// <c>upstream</c> directive in front of the provider hosts.
    ///
    /// <para>An <c>upstream</c> is keyed on the DESTINATION HOST, on the single proxy every agent in the
    /// VM shares. Fronting <c>api.anthropic.com</c> that way would drag OAuth agents' traffic through
    /// the gateway too — agents holding a provider session Mainguard never issued, presenting no
    /// <c>mg_sess_</c> token, which the gateway would answer 401. That breaks interactive login, which
    /// is the one path that must not change. Confinement is per-AGENT (the CLI's own base URL, and only
    /// when that agent is BYOK) precisely so this stays a non-event for OAuth.</para>
    /// </summary>
    [Fact]
    public void EnablingConfinement_AddsNoUpstreamDirective_SoOAuthTrafficKeepsItsDirectRoute()
    {
        var effective = EgressProxyConfigurator.CombineGatewayHost(Defaults(), GatewayEndpoint);

        // This is the argument pair the production substrate passes: a gateway to be REACHABLE at, and
        // no gateway to FRONT model hosts through.
        var upstreams = EgressProxyConfig.RenderTinyproxyUpstreams(effective, gatewayHostPort: null);

        Assert.DoesNotContain("upstream ", upstreams, StringComparison.Ordinal);
        foreach (var modelHost in Defaults().Entries.Where(e => e.Kind == EgressEntryKind.ModelApi))
        {
            Assert.DoesNotContain(modelHost.HostPattern, upstreams, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The provider hosts stay on the allowlist. An OAuth CLI holds no API key, so it cannot be
    /// gateway-confined and this is its only route; removing these entries to close the BYOK bypass
    /// would kill interactive login outright.
    /// </summary>
    [Fact]
    public void EnablingConfinement_LeavesTheProviderHostsOnTheAllowlist()
    {
        var effective = EgressProxyConfigurator.CombineGatewayHost(Defaults(), GatewayEndpoint);

        var filter = EgressProxyConfig.RenderTinyproxyFilter(effective);

        Assert.Contains(EgressProxyConfig.RenderHostPattern("api.anthropic.com"), filter, StringComparison.Ordinal);
        Assert.Contains(EgressProxyConfig.RenderHostPattern("api.openai.com"), filter, StringComparison.Ordinal);
    }

    /// <summary>
    /// The default posture — no gateway — must render byte-identical policy to before this change, or
    /// "purely additive" is not true of the deployments that never turn confinement on.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithNoGateway_TheRenderedPolicy_IsUnchanged(string? endpoint)
    {
        var baseline = EgressProxyConfig.RenderTinyproxyFilter(Defaults());

        var withNoGateway = EgressProxyConfig.RenderTinyproxyFilter(
            EgressProxyConfigurator.CombineGatewayHost(Defaults(), endpoint));

        Assert.Equal(baseline, withNoGateway);
    }

    /// <summary>
    /// The gateway is a direct-route service host, not a git host — so it must not trip the A6 warning
    /// the UI raises when the allowlist regains a route to a git provider.
    /// </summary>
    [Fact]
    public void TheGatewayEntry_DoesNotDefeatA6()
    {
        var effective = EgressProxyConfigurator.CombineGatewayHost(Defaults(), GatewayEndpoint);

        Assert.False(effective.HasGitHostEntry);
        var entry = Assert.Single(effective.Entries, e => e.HostPattern == GatewayHost);
        Assert.Equal(EgressEntryKind.AgentService, entry.Kind);
    }
}
