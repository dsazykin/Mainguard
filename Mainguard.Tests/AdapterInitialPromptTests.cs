using System;
using System.Linq;
using Mainguard.Agents.Agents.Adapters;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The manifest half of the first-turn delivery: which CLI declares that it takes one, how, and what
/// happens to every degraded declaration.
///
/// <para>The field is vendor knowledge, exactly like <c>systemPromptArg</c> and
/// <c>preApprovedCommandArg</c> beside it — only the CLI's author knows how it accepts a first turn. What
/// makes this one unusual is that a MIS-declaration is silent by nature: the agent still launches, still
/// draws its banner, and simply never does anything. So every unreadable value is refused at parse rather
/// than defaulted, and these tests are mostly about the refusals.</para>
/// </summary>
public class AdapterInitialPromptTests
{
    /// <summary>The shipped claude-code adapter declares the channel — without it, every worker jail
    /// launches into the six-minute idle the field exists to end.</summary>
    [Fact]
    public void TheShippedClaudeCodeAdapter_DeclaresAFirstPositionalTurn()
    {
        var spec = ClaudeCode();

        Assert.Equal("first-positional", spec.InitialPromptStyle);
        Assert.Equal(AdapterInitialPromptStyle.FirstPositional, spec.InitialPromptDelivery);
    }

    /// <summary>
    /// The other four shipped CLIs declare nothing and launch byte-identically to before. Asserted so the
    /// next adapter to want a first turn has to argue for it against its own vendor's flag handling —
    /// "positional" is not a universal truth, and a wrong guess here produces an agent that silently does
    /// nothing rather than a visible failure.
    /// </summary>
    [Fact]
    public void NoOtherShippedAdapterDeclaresOne()
    {
        var others = AdapterManifest.Parse(StarterManifest()).Adapters.Where(a => a.Id != "claude-code");

        Assert.NotEmpty(others);
        Assert.All(others, a =>
        {
            Assert.Null(a.InitialPromptStyle);
            Assert.Equal(AdapterInitialPromptStyle.None, a.InitialPromptDelivery);
        });
    }

    /// <summary>
    /// A spelling this build cannot read is REFUSED, not defaulted. Degrading to "no first turn" is the
    /// deadlock: a worker idling at an empty input box, in a jail whose terminal is input-locked, whose
    /// coordinator cannot steer it until it presents the plan it will never present. A red build says so;
    /// a default says nothing at all.
    /// </summary>
    [Theory]
    [InlineData("positional")]
    [InlineData("First-Positional")]
    [InlineData("--prompt")]
    [InlineData("")]
    public void AnUnreadableStyle_IsRefusedRatherThanDefaulted(string style)
    {
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(ManifestWith(style)));

        Assert.Equal(AdapterManifestError.BadInitialPrompt, ex.Error);
        Assert.Contains("first-positional", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>An explicit <c>"none"</c> is a legitimate declaration — a maintainer saying "this CLI
    /// takes no first turn" is different information from an omitted field, and must not read as an
    /// error.</summary>
    [Fact]
    public void AnExplicitNone_IsAcceptedAndChangesNothing()
    {
        var spec = AdapterManifest.Parse(ManifestWith("none")).Adapters.Single();

        Assert.Equal(AdapterInitialPromptStyle.None, spec.InitialPromptDelivery);
    }

    /// <summary>
    /// The daemon reads the MARKER, not the manifest, so the field has to survive that crossing. A marker
    /// written before the field existed deserializes to null — the documented "re-install the CLI to
    /// backfill" state — and reads as <c>None</c>, which launches exactly as that install did before.
    /// </summary>
    [Fact]
    public void TheMarkerCarriesTheField_AndAPreFieldMarkerDegradesToNoTurn()
    {
        var carried = InstalledAdapterMarker.TryDeserialize(
            InstalledAdapterMarker.Serialize(new InstalledAdapterMarker(
                "claude-code", "2.1.218", new[] { "/opt/mainguard/adapters/bin/claude" },
                InitialPromptStyle: "first-positional")));

        Assert.Equal(AdapterInitialPromptStyle.FirstPositional, carried!.InitialPromptDelivery);

        var older = InstalledAdapterMarker.TryDeserialize(
            """{"id":"claude-code","version":"2.1.218","launch":["/opt/mainguard/adapters/bin/claude"]}""");

        Assert.Null(older!.InitialPromptStyle);
        Assert.Equal(AdapterInitialPromptStyle.None, older.InitialPromptDelivery);
    }

    /// <summary>
    /// THE CROSSING, and it is here because removing it left the whole suite green. The daemon reads the
    /// MARKER, never the manifest, so a field that is declared, parsed, carried on the spec and honoured
    /// by the launcher — and simply never written into the marker — produces jails that launch with no
    /// first turn while every other test still passes. That is phase 3's M7 shape exactly (a correct
    /// builder nobody calls correctly), and it was reproduced deliberately: dropping
    /// <c>spec.InitialPromptStyle</c> from the marker <c>AdapterChannel</c> writes failed <b>nothing</b>
    /// until this test existed.
    ///
    /// <para>Driven through the real <see cref="AdapterChannel.EnsureAsync"/> rather than by asserting a
    /// call site, so it is the shipped install path that has to carry the field.</para>
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task TheInstallChannel_WritesTheFieldIntoTheMarkerTheDaemonReads()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("claude-code-payload-1.2.3");
        var sha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        var host = new AdapterChannelTests.FakeInstallHost();
        var channel = new AdapterChannel(
            new AdapterChannelTests.FakeSource { PayloadToServe = payload },
            host,
            new AdapterChannelTests.FakeCache(InstallableManifest(sha)));

        await channel.EnsureAsync("claude-code");

        var marker = InstalledAdapterMarker.TryDeserialize(
            host.Shims["/home/mainguard/mainguard/adapters/registry/claude-code.json"]);

        Assert.Equal("first-positional", marker!.InitialPromptStyle);
        Assert.Equal(AdapterInitialPromptStyle.FirstPositional, marker.InitialPromptDelivery);
    }

    /// <summary>A marker carrying a spelling this build cannot read degrades to <c>None</c> rather than
    /// throwing. Unlike a manifest, a marker is already on disk and cannot be refused — and the safe
    /// reading of an unknown delivery is the one that changes no launch line.</summary>
    [Fact]
    public void AMarkerWithAnUnreadableStyle_DegradesToNoTurnRatherThanThrowing()
    {
        var marker = new InstalledAdapterMarker(
            "claude-code", "2.1.218", new[] { "/opt/mainguard/adapters/bin/claude" },
            InitialPromptStyle: "second-positional");

        Assert.Equal(AdapterInitialPromptStyle.None, marker.InitialPromptDelivery);
    }

    private static AdapterSpec ClaudeCode() =>
        AdapterManifest.Parse(StarterManifest()).Adapters.Single(a => a.Id == "claude-code");

    /// <summary>One adapter with everything the parser requires, so the only variable is the style.</summary>
    private static string ManifestWith(string style) =>
        $$"""
        {
          "adapters": [
            {
              "id": "probe-cli",
              "displayName": "Probe CLI",
              "version": "1.0.0",
              "provenance": "none",
              "sha256": "{{new string('a', 64)}}",
              "payloadUrl": "https://registry.npmjs.org/probe/-/probe-1.0.0.tgz",
              "installCmd": ["npm", "install", "--ignore-scripts", "{payload}"],
              "launch": ["/opt/mainguard/adapters/bin/probe"],
              "initialPromptStyle": "{{style}}",
              "healthProbe": { "command": ["probe", "--version"], "expectedVersionSubstring": "1.0.0" }
            }
          ]
        }
        """;

    /// <summary>The same adapter, plus the <c>launch</c> argv that makes the channel write a marker at
    /// all (a launch-less adapter is a tool, not an agent, and gets none).</summary>
    private static string InstallableManifest(string sha) =>
        $$"""
        {
          "adapters": [
            {
              "id": "claude-code",
              "displayName": "Claude Code",
              "version": "1.2.3",
              "provenance": "npm-registry-signature",
              "sha256": "{{sha}}",
              "installCmd": ["npm", "install", "-g", "tool@1.2.3"],
              "launch": ["/opt/mainguard/adapters/bin/claude"],
              "initialPromptStyle": "first-positional",
              "healthProbe": { "command": ["tool", "--version"], "expectedVersionSubstring": "1.2.3" }
            }
          ]
        }
        """;

    private static string StarterManifest() => System.IO.File.ReadAllText(StarterManifestPath());

    private static string StarterManifestPath()
    {
        for (var probe = new System.IO.DirectoryInfo(AppContext.BaseDirectory); probe is not null; probe = probe.Parent)
        {
            var candidate = System.IO.Path.Combine(
                probe.FullName, "Mainguard.Agents", "Agents", "Adapters", "adapters.starter.json");
            if (System.IO.File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("adapters.starter.json not found above " + AppContext.BaseDirectory);
    }
}
