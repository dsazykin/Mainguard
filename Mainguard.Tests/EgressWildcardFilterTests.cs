using System.Text.RegularExpressions;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-28 — the rendered tinyproxy filter must mean the same thing as the allowlist it renders.
///
/// <para>Every entry used to render as <c>^{host with dots escaped}$</c>. For a wildcard entry that
/// produces an <b>invalid regex</b>: <c>*.example.com</c> became <c>^*\.example\.com$</c>, whose
/// leading <c>*</c> is a quantifier with nothing to repeat. So the policy the UI showed and the filter
/// tinyproxy actually enforced diverged — for exactly the entries a user is most likely to hand-add.</para>
///
/// <para>These assert the property that matters: for any host, the rendered regex and
/// <see cref="EgressAllowlist.Allows"/> agree. A filter that merely "looks right" is not enough — it
/// has to be a valid regex AND encode the same rule.</para>
/// </summary>
public sealed class EgressWildcardFilterTests
{
    private static EgressAllowlist ListWith(string pattern)
    {
        var list = new EgressAllowlist(System.Array.Empty<EgressAllowlistEntry>(), new InMemoryAuditLog());
        list.Add(new EgressAllowlistEntry("test-entry", pattern, EgressEntryKind.AgentService), "test");
        return list;
    }

    private static Regex Rendered(string pattern) =>
        new(EgressProxyConfig.RenderHostPattern(pattern), RegexOptions.IgnoreCase);

    // The bug: this used to throw — ^*\.example\.com$ is not a valid regex at all.
    [Fact]
    public void WildcardEntry_RendersAValidRegex()
    {
        var ex = Record.Exception(() => Rendered("*.example.com"));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("api.example.com", true)]
    [InlineData("deep.sub.example.com", true)]
    [InlineData("example.com", true)]          // the apex — HostMatches allows it
    [InlineData("notexample.com", false)]      // must NOT match by bare suffix
    [InlineData("example.com.evil.net", false)]
    [InlineData("evil.net", false)]
    public void WildcardEntry_RenderedFilter_AgreesWithAllows(string host, bool expected)
    {
        var list = ListWith("*.example.com");

        Assert.Equal(expected, list.Allows(host));                       // enforced policy
        Assert.Equal(expected, Rendered("*.example.com").IsMatch(host)); // rendered filter
    }

    [Theory]
    [InlineData("api.anthropic.com", true)]
    [InlineData("evil-api.anthropic.com", false)]
    [InlineData("api.anthropic.com.evil.net", false)]
    [InlineData("anthropic.com", false)]
    public void ExactEntry_RenderedFilter_AgreesWithAllows(string host, bool expected)
    {
        var list = ListWith("api.anthropic.com");

        Assert.Equal(expected, list.Allows(host));
        Assert.Equal(expected, Rendered("api.anthropic.com").IsMatch(host));
    }

    // The dot must stay escaped — an unescaped '.' would let 'apiXanthropic.com' through.
    [Fact]
    public void DotsAreEscaped_SoTheyAreNotWildcards()
    {
        Assert.DoesNotMatch(Rendered("api.anthropic.com"), "apiXanthropic.com");
        Assert.DoesNotMatch(Rendered("*.example.com"), "apiXexample.com");
    }

    // The whole file must be valid, since tinyproxy loads it as one filter list.
    [Fact]
    public void RenderedFilterFile_IsAllValidRegexes_IncludingWildcards()
    {
        var list = new EgressAllowlist(EgressAllowlist.DefaultEntries, new InMemoryAuditLog());
        list.Add(new EgressAllowlistEntry("wild", "*.example.com", EgressEntryKind.AgentService), "test");

        foreach (var line in EgressProxyConfig.RenderTinyproxyFilter(list)
                     .Split('\n', System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            Assert.Null(Record.Exception(() => new Regex(line)));
        }
    }
}
