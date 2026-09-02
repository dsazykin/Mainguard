using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Ipc;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <b>Defect D5b — a role's tool grant must not ride the per-repo settings store into another role's jail.</b>
///
/// <para><b>What was measured.</b> On the reporting machine,
/// <c>~/.mainguard/cli-settings/&lt;repo&gt;/claude-code.json</c> held a harvested
/// <c>.claude/settings.local.json</c> containing <c>Bash(/opt/mainguard/ipc/mainguard-agent *)</c> — the
/// COORDINATOR's shim, recorded when the owner answered "yes, don't ask again" in a coordinator terminal.
/// That file is restored into every later jail for that repository, workers included. It was also the only
/// reason the live coordinator worked at all, because the per-role launch grant was inert on that install
/// (D5a).</para>
///
/// <para>The rule these tests hold: <see cref="AgentIpcPaths.SandboxMount"/> is Mainguard's own mount, its
/// grants are issued per jail and per role at launch, and nothing about it survives in a persisted file.
/// Everything the owner actually approved for their own project survives untouched — that is the feature,
/// and a scrub that took it too would be a worse defect than the one it fixes.</para>
/// </summary>
public class CliSettingsGrantScrubTests
{
    private const string Mount = AgentIpcPaths.SandboxMount;
    private const string CoordinatorGrant = "Bash(" + Mount + "/mainguard-agent *)";
    private const string WorkerGrant = "Bash(" + Mount + "/mainguard-plan:*)";

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string Scrubbed(string json)
    {
        var result = CliSettingsGrantScrub.Scrub(Utf8(json));
        Assert.NotNull(result);
        return Encoding.UTF8.GetString(result!);
    }

    /// <summary>An allow-list holding the two rules, as the CLI writes them.</summary>
    private static string AllowList(params string[] rules) =>
        "{\"permissions\":{\"allow\":[" + string.Join(",", Array.ConvertAll(rules, r => "\"" + r + "\"")) + "]}}";

    /// <summary>The exact file found on the reporting machine: the coordinator's grant is removed, and the
    /// grant the owner made for their own project is kept.</summary>
    [Fact]
    public void TheHarvestedCoordinatorGrant_IsRemoved_AndTheOwnersOwnApprovalsSurvive()
    {
        var scrubbed = Scrubbed(AllowList("Bash(node *)", CoordinatorGrant));

        Assert.DoesNotContain(Mount, scrubbed, StringComparison.Ordinal);
        Assert.Contains("Bash(node *)", scrubbed, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both roles' shims, and the mount under any spelling. The rule is the MOUNT, not one shim filename:
    /// a scrub keyed on <c>mainguard-agent</c> would let a worker's <c>mainguard-plan</c> grant persist and
    /// reach a coordinator, which is the same defect with the roles swapped.
    /// </summary>
    [Theory]
    [InlineData(CoordinatorGrant)]
    [InlineData(WorkerGrant)]
    [InlineData("Bash(" + Mount + "/*)")]
    [InlineData("Read(" + Mount + "/MAINGUARD.md)")]
    public void NoRuleNamingTheDaemonsMount_Survives(string rule)
    {
        var scrubbed = Scrubbed(AllowList("Bash(git status:*)", rule));

        Assert.DoesNotContain(Mount, scrubbed, StringComparison.Ordinal);
        Assert.Contains("Bash(git status:*)", scrubbed, StringComparison.Ordinal);
    }

    /// <summary>
    /// A DENY naming the mount goes too, and the class header says why that is not a widening: what
    /// replaces it is the daemon's own per-jail grant — one absolute path, this jail's own shim — not
    /// "anything goes". Pinned as its own test because it is the one direction of this change a reviewer
    /// should argue with.
    /// </summary>
    [Fact]
    public void ADenyNamingTheMount_IsAlsoRemoved()
    {
        var scrubbed = Scrubbed(
            "{\"permissions\":{\"deny\":[\"" + CoordinatorGrant + "\",\"Bash(rm -rf /:*)\"]}}");

        Assert.DoesNotContain(Mount, scrubbed, StringComparison.Ordinal);
        Assert.Contains("Bash(rm -rf /:*)", scrubbed, StringComparison.Ordinal);
    }

    /// <summary>Nesting is not an escape: the walk is recursive, not a scan of one well-known key. A CLI
    /// that keeps its allowlist under a profile does not get to smuggle the grant through.</summary>
    [Fact]
    public void TheRuleIsFoundAtAnyDepth()
    {
        var scrubbed = Scrubbed(
            "{\"profiles\":{\"a\":{\"permissions\":{\"allow\":[\"" + CoordinatorGrant + "\"]}}}}");

        Assert.DoesNotContain(Mount, scrubbed, StringComparison.Ordinal);
    }

    /// <summary>A property NAME that is a path under the mount is dropped with its value — several CLIs
    /// key per-directory settings by the directory itself.</summary>
    [Fact]
    public void APropertyKeyedByAPathUnderTheMount_IsDropped()
    {
        var scrubbed = Scrubbed(
            "{\"trustedPaths\":{\"" + Mount + "\":true,\"/workspace\":true}}");

        Assert.DoesNotContain(Mount, scrubbed, StringComparison.Ordinal);
        Assert.Contains("/workspace", scrubbed, StringComparison.Ordinal);
    }

    /// <summary>
    /// JSON has more than one spelling of a slash, and the CLI's parser reads all of them. A grant written
    /// with escaped separators contains no literal mount path, so a scrub that decided on the raw bytes
    /// let it through byte-identical — and the file is agent-writable in the jail, so the agent is who
    /// would write it that way. The decision is now made on the parsed document.
    /// </summary>
    [Theory]
    [InlineData("Bash(\\/opt\\/mainguard\\/ipc\\/mainguard-agent *)")]
    [InlineData("Bash(\\u002fopt\\u002fmainguard\\u002fipc\\u002fmainguard-plan:*)")]
    public void AnEscapedSpellingOfTheMount_IsScrubbedTheSameAsTheLiteralOne(string escapedRule)
    {
        var json = "{ \"permissions\": { \"allow\": [ \"Bash(node *)\", \"" + escapedRule + "\" ] } }";

        var result = CliSettingsGrantScrub.Scrub(Utf8(json));

        Assert.NotNull(result);
        var allow = JsonNode.Parse(result!)!["permissions"]!["allow"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "Bash(node *)" }, allow);
    }

    /// <summary>
    /// <b>The common path is lossless.</b> A settings file that says nothing about the mount comes back
    /// byte-identical — never reformatted, never re-ordered. This is the owner's own configuration file,
    /// and rewriting it as a side effect of a security scrub would be its own defect.
    /// </summary>
    [Fact]
    public void AFileThatNeverNamesTheMount_IsReturnedByteIdentical()
    {
        var original = Utf8("{\n  \"permissions\" : { \"allow\": [ \"Bash(node *)\" ] }\n}\n");

        Assert.Same(original, CliSettingsGrantScrub.Scrub(original));
    }

    /// <summary>
    /// Fail closed. A file that names the mount and is not parseable JSON does not travel at all: refusing
    /// to carry it costs a re-approval, while carrying bytes nobody can read is exactly how a grant
    /// survives unseen. The manifest schema does not require a settings file to be JSON, so this is a real
    /// shape rather than a hypothetical.
    /// </summary>
    [Fact]
    public void AFileThatNamesTheMountAndCannotBeParsed_DoesNotTravel()
    {
        Assert.Null(CliSettingsGrantScrub.Scrub(Utf8("allow = \"" + Mount + "/mainguard-agent\"\n")));
    }

    /// <summary>The paired non-refusal: unparseable content that says nothing about the mount is none of
    /// this function's business and passes through untouched.</summary>
    [Fact]
    public void UnparseableContentThatNeverNamesTheMount_IsLeftAlone()
    {
        var original = Utf8("theme = \"dark\"\n");

        Assert.Same(original, CliSettingsGrantScrub.Scrub(original));
    }

    /// <summary>Empty/absent content is not a decision — it stays what it was, so a caller cannot read
    /// "there was nothing to carry" as "this was refused".</summary>
    [Fact]
    public void EmptyContentIsUntouched()
    {
        Assert.Null(CliSettingsGrantScrub.Scrub(null));
        Assert.Empty(CliSettingsGrantScrub.Scrub(Array.Empty<byte>())!);
    }

    /// <summary>The exclusion is derived from the mount constant and never spelled a second time — so
    /// moving the mount moves what is excluded, and the two cannot drift (MG-12).</summary>
    [Fact]
    public void TheExcludedPrefixIsTheDaemonsOwnMount()
        => Assert.Equal(Mount, CliSettingsGrantScrub.DaemonOwnedPathPrefix);
}
