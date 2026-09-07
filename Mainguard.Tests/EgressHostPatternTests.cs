using System;
using System.Linq;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// F31 — a host pattern is validated BEFORE it is stored, and therefore before it is rendered into a
/// tinyproxy regex, a dnsmasq directive or an iptables rule, none of which quote it.
///
/// <para>The three concrete exploits the finding named are each pinned below: <c>a|.*</c> (an
/// alternation that allows every host on the internet, defeating default-deny while the UI still shows
/// one innocuous entry), <c>(</c> (an uncompilable filter, i.e. a fleet-wide egress outage), and a
/// pattern carrying <c>/</c> or a newline (dnsmasq directive injection, which can restore the default
/// upstream that <c>no-resolv</c> exists to remove and re-open DNS exfiltration).</para>
/// </summary>
public class EgressHostPatternTests
{
    [Theory]
    [InlineData("api.anthropic.com")]
    [InlineData("*.example.com")]
    [InlineData("crates.io")]
    [InlineData("host.docker.internal")]
    [InlineData("172.17.0.1")]        // the gateway's own address is allowlisted as one of these
    [InlineData("127.0.0.1")]
    [InlineData("my-registry.internal")]
    [InlineData("example.test")]
    public void LegitimateHosts_AreAccepted(string pattern) =>
        Assert.True(EgressHostPattern.IsValid(pattern), pattern);

    [Theory]
    // Regex injection into the tinyproxy filter.
    [InlineData("a|.*")]
    [InlineData(".*")]
    [InlineData("(")]
    [InlineData("^.*$")]
    [InlineData("example.com|evil.com")]
    [InlineData("[a-z]+.example.com")]
    // dnsmasq directive injection (the config is line- and slash-delimited).
    [InlineData("example.com/1.2.3.4")]
    [InlineData("example.com\nserver=8.8.8.8")]
    [InlineData("example.com\r\nno-resolv")]
    // Shell/argument shapes and plain nonsense.
    [InlineData("example.com;rm -rf /")]
    [InlineData("exa mple.com")]
    [InlineData("*")]
    [InlineData("**.example.com")]
    [InlineData("-example.com")]
    [InlineData("example-.com")]
    [InlineData("example.com.")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void HostilePatterns_AreRefused(string? pattern) =>
        Assert.False(EgressHostPattern.IsValid(pattern), pattern ?? "<null>");

    [Fact]
    public void OverlongPattern_IsRefused() =>
        Assert.False(EgressHostPattern.IsValid(new string('a', 60) + "." + new string('b', 200)));

    [Fact]
    public void Add_RefusesAHostilePattern_SoItNeverReachesTheStore()
    {
        var allowlist = EgressAllowlist.WithDefaults(new InMemoryAuditLog());

        Assert.Throws<ArgumentException>(() =>
            allowlist.Add(new EgressAllowlistEntry("wide open", "a|.*", EgressEntryKind.Custom), "uid:501"));

        Assert.DoesNotContain(allowlist.Entries, e => e.HostPattern.Contains('|', StringComparison.Ordinal));
    }

    [Fact]
    public void Render_DropsAnEntryThatSomehowBypassedValidation()
    {
        // Constructed directly (not through Add) — the shape a hand-edited store or a future caller
        // could produce. The renderer is the last line of defence and must not emit it.
        var allowlist = new EgressAllowlist(
            new[]
            {
                new EgressAllowlistEntry("Good", "api.anthropic.com", EgressEntryKind.ModelApi),
                new EgressAllowlistEntry("Hostile", "a|.*", EgressEntryKind.ModelApi),
            },
            new InMemoryAuditLog());

        var filter = EgressProxyConfig.RenderTinyproxyFilter(allowlist);
        var dnsmasq = EgressProxyConfig.RenderDnsmasqConfig(allowlist);
        var upstreams = EgressProxyConfig.RenderTinyproxyUpstreams(allowlist, "127.0.0.1:5251");

        Assert.Contains("api\\.anthropic\\.com", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("a|.*", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("a|.*", dnsmasq, StringComparison.Ordinal);
        Assert.DoesNotContain("a|.*", upstreams, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistedForm_DropsAnInvalidEntryWithoutLosingTheValidOnes()
    {
        const string json =
            "[{\"Name\":\"Good\",\"HostPattern\":\"api.openai.com\",\"Kind\":\"ModelApi\"},"
            + "{\"Name\":\"Hostile\",\"HostPattern\":\"a|.*\",\"Kind\":\"Custom\"}]";

        var allowlist = EgressAllowlist.FromPersistedForm(json, new InMemoryAuditLog());

        Assert.Equal(new[] { "api.openai.com" }, allowlist.Entries.Select(e => e.HostPattern));
    }

    [Fact]
    public void CombinedWith_SkipsAnInvalidExtraHost_RatherThanFailingTheSpawn()
    {
        var allowlist = EgressAllowlist
            .WithDefaults(new InMemoryAuditLog())
            .CombinedWith(new[] { "auth.example.com", "a|.*" }, EgressEntryKind.AgentService, "Agent CLI");

        Assert.Contains(allowlist.Entries, e => e.HostPattern == "auth.example.com");
        Assert.DoesNotContain(allowlist.Entries, e => e.HostPattern == "a|.*");
    }
}
