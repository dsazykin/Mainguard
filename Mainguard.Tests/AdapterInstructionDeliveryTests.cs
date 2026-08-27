using System;
using System.Linq;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Ipc;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The two channels by which a role's operating instructions actually reach a CLI, and the reason there
/// have to be two.
///
/// <para>Neither is redundant. A <b>coordinator</b> cannot be given a file: the role lock leaves it an
/// empty tmpfs at <c>/workspace</c> with no host side to write to, so the launch flag is the only
/// delivery that reaches it at all. A <b>worker</b> has a real worktree, and a file there is what a CLI
/// opens unprompted — the copy staged beside the shim in <c>/opt/mainguard/ipc</c> sits at a path
/// nothing reads on its own, which is why naming the file the CLI actually looks for is load-bearing
/// rather than cosmetic.</para>
/// </summary>
public class AdapterInstructionDeliveryTests
{
    private static AdapterSpec ClaudeCode() =>
        AdapterManifest.Parse(EmbeddedStarterManifest())
            .Adapters.Single(a => a.Id == "claude-code");

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

    /// <summary>
    /// The shipped claude-code adapter declares both channels. Without <c>systemPromptArg</c> a
    /// coordinator gets no instructions by ANY route — it has no worktree to put a file in — which is
    /// the exact state the branch was in before this existed.
    /// </summary>
    [Fact]
    public void TheShippedClaudeCodeAdapterDeclaresBothDeliveryChannels()
    {
        var spec = ClaudeCode();

        Assert.Equal("CLAUDE.md", spec.InstructionsFile);
        Assert.Equal("--append-system-prompt", spec.SystemPromptArg);
    }

    /// <summary>
    /// The file name has to be one the CLI opens BY ITSELF. `MAINGUARD.md` beside the shim is staged for
    /// inspection and for a CLI that is told where to look; it is not a delivery, because nothing reads
    /// it unprompted. Naming claude-code's own convention is the whole point of the field.
    /// </summary>
    [Fact]
    public void TheInstructionsFileIsOneTheCliReadsWithoutBeingAsked()
    {
        var spec = ClaudeCode();

        Assert.NotEqual(AgentIpcPaths.InstructionsFileName, spec.InstructionsFile);
        Assert.EndsWith(".md", spec.InstructionsFile!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A manifest that declares neither channel still parses. An adapter with no instruction surface is a
    /// limitation of that CLI; refusing to install it would turn a missing convenience into a spawn
    /// failure, and the fields are optional for exactly that reason.
    /// </summary>
    [Fact]
    public void AnAdapterMayDeclareNeitherChannel()
    {
        var spec = AdapterManifest.Parse(MinimalManifest()).Adapters.Single();

        Assert.Null(spec.InstructionsFile);
        Assert.Null(spec.SystemPromptArg);
    }

    /// <summary>The marker carries both across the host/VM boundary — the daemon reads the MARKER, not
    /// the manifest, so a field that stops here never reaches a jail.</summary>
    [Fact]
    public void TheInstalledMarkerCarriesBothChannelsAcrossTheBoundary()
    {
        var marker = new InstalledAdapterMarker(
            "claude-code", "2.1.218", new[] { "/opt/mainguard/adapters/bin/claude" },
            InstructionsFile: "CLAUDE.md",
            SystemPromptArg: "--append-system-prompt");

        var round = InstalledAdapterMarker.TryDeserialize(InstalledAdapterMarker.Serialize(marker));

        Assert.NotNull(round);
        Assert.Equal("CLAUDE.md", round!.InstructionsFile);
        Assert.Equal("--append-system-prompt", round.SystemPromptArg);
    }

    /// <summary>A marker written before the fields existed still deserializes, with both null — the
    /// documented "re-install the CLI to backfill it" path, not a crash on upgrade.</summary>
    [Fact]
    public void AMarkerWrittenBeforeTheseFieldsExisted_StillDeserializes()
    {
        const string legacy = """
        {"id":"claude-code","version":"2.1.218","launch":["/opt/mainguard/adapters/bin/claude"]}
        """;

        var round = InstalledAdapterMarker.TryDeserialize(legacy);

        Assert.NotNull(round);
        Assert.Null(round!.InstructionsFile);
        Assert.Null(round.SystemPromptArg);
    }

    private static string MinimalManifest() => """
    {
      "adapters": [
        {
          "id": "toolless",
          "displayName": "Toolless",
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
