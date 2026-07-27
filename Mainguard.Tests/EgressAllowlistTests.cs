using System.Linq;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Pins the A6 structural control: the default egress allowlist contains NO git-host entry, and a
/// user-added git-host entry is flagged as defeating A6. Also covers persistence round-trips and the
/// change-audit events (P2-17 transparency feed).
/// </summary>
public class EgressAllowlistTests
{
    [Fact]
    public void Defaults_ContainNoGitHostEntry()
    {
        var allowlist = EgressAllowlist.WithDefaults(new InMemoryAuditLog());

        Assert.False(allowlist.HasGitHostEntry);
        Assert.DoesNotContain(allowlist.Entries, e => e.DefeatsA6);
        // Sanity: the model APIs + registries ARE present.
        Assert.Contains(allowlist.Entries, e => e.HostPattern == "api.anthropic.com");
        Assert.Contains(allowlist.Entries, e => e.HostPattern == "registry.npmjs.org");
        // And no known git host slipped in.
        foreach (var host in new[] { "github.com", "gitlab.com", "bitbucket.org", "dev.azure.com" })
            Assert.DoesNotContain(allowlist.Entries, e => e.HostPattern == host);
    }

    [Fact]
    public void Allows_MatchesDefaults_DeniesEverythingElse()
    {
        var allowlist = EgressAllowlist.WithDefaults(new InMemoryAuditLog());
        Assert.True(allowlist.Allows("api.anthropic.com"));
        Assert.False(allowlist.Allows("example.com"));
        Assert.False(allowlist.Allows("github.com"));
    }

    [Fact]
    public void AddThenRemove_RoundTrips_AndEmitsAuditEvents()
    {
        var audit = new InMemoryAuditLog();
        var allowlist = EgressAllowlist.WithDefaults(audit);
        var before = allowlist.Entries.Count;

        allowlist.Add(new EgressAllowlistEntry("Custom", "cdn.example.com", EgressEntryKind.Custom), who: "daniel");
        Assert.True(allowlist.Allows("cdn.example.com"));
        Assert.Equal(before + 1, allowlist.Entries.Count);

        allowlist.Remove("cdn.example.com", who: "daniel");
        Assert.False(allowlist.Allows("cdn.example.com"));
        Assert.Equal(before, allowlist.Entries.Count);

        var changes = audit.Read().Where(e => e.Type == EgressAllowlist.ChangeEventType).ToList();
        Assert.Equal(2, changes.Count);
        Assert.Equal("add", changes[0].Fields["action"]);
        Assert.Equal("remove", changes[1].Fields["action"]);
        Assert.Equal("daniel", changes[0].Fields["who"]);
    }

    [Fact]
    public void AddingGitHostEntry_IsFlaggedAsDefeatingA6()
    {
        var audit = new InMemoryAuditLog();
        var allowlist = EgressAllowlist.WithDefaults(audit);

        allowlist.Add(new EgressAllowlistEntry("GitHub", "github.com", EgressEntryKind.GitHost), who: "daniel");

        Assert.True(allowlist.HasGitHostEntry);
        var entry = allowlist.Entries.Single(e => e.HostPattern == "github.com");
        Assert.True(entry.DefeatsA6);

        var change = audit.Read().Last(e => e.Type == EgressAllowlist.ChangeEventType);
        Assert.Equal("true", change.Fields["defeats_a6"]);
    }

    [Theory]
    [InlineData("github.com", true)]
    [InlineData("gitlab.com", true)]
    [InlineData("git.internal.corp", true)]
    [InlineData("api.anthropic.com", false)]
    [InlineData("registry.npmjs.org", false)]
    // MG-40: this delegates to GitHostDetector.Detect, whose Azure arms matched by substring — so an
    // attacker-registered lookalike was judged a git host (and a REAL Azure subdomain must still be one).
    [InlineData("ssh.dev.azure.com", true)]
    [InlineData("acme.visualstudio.com", true)]
    [InlineData("dev.azure.com.evil.net", false)]
    public void LooksLikeGitHost_Classifies(string host, bool expected)
    {
        Assert.Equal(expected, EgressAllowlistEntry.LooksLikeGitHost(host));
    }

    /// <summary>MG-40: the App's design-preview gateway renders the A6 warning marker from the SAME
    /// heuristic as the daemon (it used to keep a hand-copied mirror that had already drifted to a
    /// substring match on <c>dev.azure.com</c>).</summary>
    [Theory]
    [InlineData("dev.azure.com", true)]
    [InlineData("dev.azure.com.evil.net", false)]
    public void InMemoryGateway_MarksA6_UsingTheDaemonHeuristic(string host, bool expected)
    {
        var gateway = new Mainguard.Agents.UI.Services.InMemoryEgressAllowlistGateway();
        gateway.Add(host, host, "Custom");

        var item = gateway.List().Single(i => i.HostPattern == host);
        Assert.Equal(expected, item.DefeatsA6);
    }

    [Fact]
    public void CombinedWith_UnionsAdapterHosts_AsAgentService_AndDedupes()
    {
        var audit = new InMemoryAuditLog();
        var baseList = EgressAllowlist.WithDefaults(audit);
        var beforeCount = baseList.Entries.Count;

        var combined = baseList.CombinedWith(
            new[] { "platform.claude.com", "statsig.anthropic.com", "api.anthropic.com", "  ", "PLATFORM.claude.com" },
            EgressEntryKind.AgentService, "Agent CLI");

        // The two genuinely-new hosts are permitted; the already-present default, the case-variant, and
        // the blank are all de-duped away.
        Assert.True(combined.Allows("platform.claude.com"));
        Assert.True(combined.Allows("statsig.anthropic.com"));
        Assert.Equal(beforeCount + 2, combined.Entries.Count);
        Assert.All(
            combined.Entries.Where(e => e.HostPattern is "platform.claude.com" or "statsig.anthropic.com"),
            e => Assert.Equal(EgressEntryKind.AgentService, e.Kind));

        // A render-time view — no audit event, and the base allowlist is untouched.
        Assert.DoesNotContain(audit.Read(), e => e.Type == EgressAllowlist.ChangeEventType);
        Assert.Equal(beforeCount, baseList.Entries.Count);
    }

    [Fact]
    public void CombinedWith_AgentServiceHosts_AreDirectRoute_NotGatewayFronted_NorA6()
    {
        var combined = EgressAllowlist.WithDefaults(new InMemoryAuditLog())
            .CombinedWith(new[] { "platform.claude.com" }, EgressEntryKind.AgentService, "Agent CLI");

        Assert.True(combined.Allows("platform.claude.com"));                                        // permitted at the proxy
        Assert.Contains("platform.claude.com", EgressProxyConfig.RenderDnsmasqConfig(combined));    // pinned DNS resolves it
        var upstreams = EgressProxyConfig.RenderTinyproxyUpstreams(combined, "gw:1234");
        Assert.DoesNotContain("platform.claude.com", upstreams);                                    // NOT gateway-fronted (auth, not model)
        Assert.Contains("api.anthropic.com", upstreams);                                            // the model host still IS
        Assert.False(combined.HasGitHostEntry);                                                     // and it does not defeat A6
    }

    [Fact]
    public void Persistence_RoundTrips()
    {
        var audit = new InMemoryAuditLog();
        var original = EgressAllowlist.WithDefaults(audit);
        var json = original.ToPersistedForm();

        var restored = EgressAllowlist.FromPersistedForm(json, audit);
        Assert.Equal(
            original.Entries.Select(e => e.HostPattern).OrderBy(h => h),
            restored.Entries.Select(e => e.HostPattern).OrderBy(h => h));
    }
}
