using System;
using System.Linq;
using Mainguard.Agents.Agents.Adapters;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The manifest half of the coordinator's pre-approval: how a CLI spells "this one command needs no
/// approval", and the refusals that keep that declaration from turning into a wider grant than intended.
///
/// <para><b>The defect this closes.</b> A real claude-code coordinator, following its operating
/// instructions exactly, ran <c>/opt/mainguard/ipc/mainguard-agent</c> as its first action and got "This
/// command requires approval" — in a jail with no human to answer it. That one command IS the
/// coordinator's entire surface (contract §3's four tools are its four subcommands), so the branch's
/// headline feature stalled on its very first action and stayed stalled. Reproduced outside Mainguard
/// against claude-code 2.1.250 before the fix: the same prompt answers DENIED without
/// <c>--allowedTools</c> and runs the command with it.</para>
///
/// <para><b>Why the checks below are refusals rather than defaults.</b> This is the only manifest field
/// that grants EXECUTION inside a sandbox, so every degraded reading of a half-declared pair is worse
/// than a red build: a missing format appends a flag with no value, a missing flag computes a grant and
/// silently drops it (back to the stall), and a placeholder-free format emits a constant grant that does
/// not name this agent's shim — the one way this field could widen a jail rather than narrow it.</para>
/// </summary>
public class AdapterPreApprovalTests
{
    private static AdapterSpec Adapter(string id) =>
        AdapterManifest.Parse(EmbeddedStarterManifest()).Adapters.Single(a => a.Id == id);

    /// <summary>The shipped claude-code adapter declares the pair, in that CLI's own syntax.</summary>
    [Fact]
    public void TheShippedClaudeCodeAdapterDeclaresThePreApprovalPair()
    {
        var spec = Adapter("claude-code");

        Assert.Equal("--allowedTools", spec.PreApprovedCommandArg);
        Assert.Equal("Bash({command}:*)", spec.PreApprovedCommandFormat);
    }

    /// <summary>
    /// No OTHER shipped adapter declares one. Stated as a test because the fix's whole claim to being
    /// minimal is that exactly one CLI's jails were widened: a manifest edit adding the pair to another
    /// adapter should have to argue for itself here, having verified the vendor's real flag against the
    /// pinned binary, rather than riding in unnoticed.
    /// </summary>
    [Fact]
    public void NoOtherShippedAdapterGrantsAnything()
    {
        var others = AdapterManifest.Parse(EmbeddedStarterManifest())
            .Adapters.Where(a => a.Id != "claude-code");

        Assert.All(others, a =>
        {
            Assert.Null(a.PreApprovedCommandArg);
            Assert.Null(a.PreApprovedCommandFormat);
        });
    }

    /// <summary>
    /// The format is a TEMPLATE, and the daemon is what fills it in. A format that hardcoded a command
    /// would be a grant chosen in a manifest rather than by the daemon that knows which shim this jail
    /// actually has — so the placeholder is required, and nothing shipped may omit it.
    /// </summary>
    [Fact]
    public void TheShippedFormatCarriesThePlaceholderTheDaemonSubstitutes()
    {
        Assert.Contains(
            AdapterManifest.PreApprovedCommandPlaceholder,
            Adapter("claude-code").PreApprovedCommandFormat!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The grant is ONE absolute path and not a wildcard. <c>Bash(/opt/mainguard/ipc/*)</c> or a bare
    /// <c>Bash</c> would pre-approve whatever else ever lands in that directory — the opposite of what a
    /// least-privilege jail is for, and an easy "fix" for someone who finds a later shim also prompting.
    /// </summary>
    [Fact]
    public void TheShippedFormatIsNotAWildcardOverTheIpcDirectory()
    {
        var rendered = AdapterManifest.RenderPreApproval(
            Adapter("claude-code").PreApprovedCommandFormat, "/opt/mainguard/ipc/mainguard-agent");

        Assert.Equal("Bash(/opt/mainguard/ipc/mainguard-agent:*)", rendered);
        Assert.DoesNotContain("/ipc/*", rendered!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"preApprovedCommandArg\": \"--allowedTools\",")]
    [InlineData("\"preApprovedCommandFormat\": \"Bash({command}:*)\",")]
    public void HalfDeclaringThePair_IsRefused(string half)
    {
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(ManifestWith(half)));

        Assert.Equal(AdapterManifestError.BadPreApproval, ex.Error);
    }

    [Fact]
    public void AFormatWithoutThePlaceholder_IsRefused()
    {
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(ManifestWith(
            "\"preApprovedCommandArg\": \"--allowedTools\", \"preApprovedCommandFormat\": \"Bash(rm -rf:*)\",")));

        Assert.Equal(AdapterManifestError.BadPreApproval, ex.Error);
    }

    /// <summary>An adapter that declares neither still parses — a CLI with no pre-approval channel is a
    /// limitation of that CLI, and refusing to install it would turn a missing convenience into a spawn
    /// failure.</summary>
    [Fact]
    public void AnAdapterMayDeclareNeither()
    {
        var spec = AdapterManifest.Parse(ManifestWith(string.Empty)).Adapters.Single();

        Assert.Null(spec.PreApprovedCommandArg);
        Assert.Null(spec.PreApprovedCommandFormat);
    }

    /// <summary><see cref="AdapterManifest.RenderPreApproval"/> answers null rather than something
    /// half-formed for every missing input. "No grant" is a working agent that asks a human; a
    /// mis-rendered grant is a permission rule whose contents nobody chose.</summary>
    [Theory]
    [InlineData(null, "/opt/mainguard/ipc/mainguard-agent")]
    [InlineData("", "/opt/mainguard/ipc/mainguard-agent")]
    [InlineData("Bash({command}:*)", null)]
    [InlineData("Bash({command}:*)", "")]
    [InlineData("Bash(anything:*)", "/opt/mainguard/ipc/mainguard-agent")]
    public void RenderPreApproval_AnswersNullRatherThanSomethingHalfFormed(string? format, string? command) =>
        Assert.Null(AdapterManifest.RenderPreApproval(format, command));

    /// <summary>The marker carries the pair across the host/VM boundary. The daemon reads the MARKER, not
    /// the manifest, so a field that stopped at the manifest would leave every jail exactly as broken as
    /// before — which is why this is pinned rather than assumed.</summary>
    [Fact]
    public void TheInstalledMarkerCarriesThePairAcrossTheBoundary()
    {
        var marker = new InstalledAdapterMarker(
            "claude-code", "2.1.218", new[] { "/opt/mainguard/adapters/bin/claude" },
            PreApprovedCommandArg: "--allowedTools",
            PreApprovedCommandFormat: "Bash({command}:*)");

        var round = InstalledAdapterMarker.TryDeserialize(InstalledAdapterMarker.Serialize(marker));

        Assert.NotNull(round);
        Assert.Equal("--allowedTools", round!.PreApprovedCommandArg);
        Assert.Equal("Bash({command}:*)", round.PreApprovedCommandFormat);
    }

    /// <summary>A marker written before these fields existed still deserializes, with both null — the
    /// documented "re-install the CLI to backfill it" path, and until then that CLI's jails behave
    /// exactly as they did before rather than crashing on upgrade.</summary>
    [Fact]
    public void AMarkerWrittenBeforeTheseFieldsExisted_StillDeserializes()
    {
        const string legacy = """
        {"id":"claude-code","version":"2.1.218","launch":["/opt/mainguard/adapters/bin/claude"]}
        """;

        var round = InstalledAdapterMarker.TryDeserialize(legacy);

        Assert.NotNull(round);
        Assert.Null(round!.PreApprovedCommandArg);
        Assert.Null(round.PreApprovedCommandFormat);
    }

    private static string EmbeddedStarterManifest() =>
        System.IO.File.ReadAllText(StarterManifestPath());

    private static string StarterManifestPath()
    {
        var dir = AppContext.BaseDirectory;
        for (var probe = new System.IO.DirectoryInfo(dir); probe is not null; probe = probe.Parent)
        {
            var candidate = System.IO.Path.Combine(
                probe.FullName, "Mainguard.Agents", "Agents", "Adapters", "adapters.starter.json");
            if (System.IO.File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("adapters.starter.json not found above " + dir);
    }

    private static string ManifestWith(string preApprovalFields) => $$"""
    {
      "adapters": [
        {
          "id": "probe",
          "displayName": "Probe",
          {{preApprovalFields}}
          "version": "1.0.0",
          "provenance": "none",
          "sha256": "3a434c8bcb493e9ca87315d9aa6064835c5987e8fbc85c181bb76157dd5c45d8",
          "installCmd": ["true"],
          "configShims": null,
          "healthProbe": {
            "command": ["/bin/true", "--version"],
            "expectedVersionSubstring": "1.0.0"
          }
        }
      ]
    }
    """;
}
