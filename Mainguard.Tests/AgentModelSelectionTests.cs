using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Orchestrator;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Choosing which model an agent is launched with: the declaration, the store, and the launch line.
///
/// <para>Before this, every jail ran whatever its CLI defaults to and nothing could say otherwise —
/// the model has to be on the launch line, which the daemon builds, and for a worker no client is even
/// in the loop (the coordinator spawns it from inside its own jail).</para>
/// </summary>
public class AgentModelSelectionTests
{
    // ---- the declaration ------------------------------------------------------------------------

    /// <summary>
    /// Every shipped CLI declares how it takes a model, and each was verified against the exact pinned
    /// artifact the manifest installs rather than assumed from its neighbours. Pinned here because a
    /// WRONG flag does not degrade gracefully: it fails every spawn of that kind with a vendor error
    /// message, which reads as a Mainguard bug.
    /// </summary>
    [Fact]
    public void EveryShippedAdapter_DeclaresItsVerifiedModelFlag()
    {
        var adapters = AdapterManifest.Parse(StarterManifest()).Adapters;

        Assert.Equal(
            new[] { "claude-code", "codex", "gemini-cli", "opencode", "qwen-code" },
            adapters.Select(a => a.Id).OrderBy(id => id, StringComparer.Ordinal));

        foreach (var adapter in adapters)
        {
            Assert.Equal("--model", adapter.ModelArg);
        }
    }

    /// <summary>
    /// claude-code's suggestions are the ALIASES its own help documents ("fable", "opus", "sonnet"),
    /// not pinned model ids — so the picker keeps naming the latest model of each tier instead of
    /// freezing one the day it was written.
    /// </summary>
    [Fact]
    public void ClaudeCodeSuggests_TheAliasesItsOwnHelpDocuments()
    {
        var claude = AdapterManifest.Parse(StarterManifest()).Adapters.Single(a => a.Id == "claude-code");

        Assert.NotNull(claude.Models);
        Assert.Contains("opus", claude.Models!);
        Assert.Contains("sonnet", claude.Models!);
        Assert.Contains("fable", claude.Models!);

        // Aliases, never pinned ids — an id here would go stale on the next model release.
        Assert.DoesNotContain(claude.Models!, m => m.StartsWith("claude-", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other four declare the FLAG but no suggestions, and that is the honest state: their model
    /// names were not verified, and a wrong suggestion is worse than none. opencode is the sharpest
    /// case — it takes `provider/model`, so a bare name offered there could not work at all.
    /// </summary>
    [Fact]
    public void TheOtherAdapters_OfferNoUnverifiedSuggestions()
    {
        var others = AdapterManifest.Parse(StarterManifest()).Adapters.Where(a => a.Id != "claude-code");

        foreach (var adapter in others)
        {
            Assert.True(
                adapter.Models is null or { Count: 0 },
                $"{adapter.Id} offers model suggestions that were never verified");
        }
    }

    /// <summary>
    /// FAILS if the model fields are ever left out of the shipped-manifest projection — defect D5a, the
    /// one that makes a feature silently inert on the only install that matters.
    ///
    /// <para>A marker is an ordinary file written the day the CLI was installed, so every real install
    /// has one that predates this field. Two shipped fixes were already completely dead that way while
    /// every test stayed green. <c>WithShippedDescription</c> is what repairs it — a marker keeps only
    /// the two facts about the INSTALL (its probed version and argv) and takes the CLI's DESCRIPTION
    /// from the manifest — so the model flag has to travel through it.</para>
    /// </summary>
    [Fact]
    public void AStaleMarker_GetsTheModelFlagBack_FromTheShippedManifest()
    {
        var spec = AdapterManifest.Parse(StarterManifest()).Adapters.Single(a => a.Id == "claude-code");

        // A marker as an older build wrote it: probed version and argv, and no model fields at all.
        var stale = new InstalledAdapterMarker(
            "claude-code", "2.1.218", new[] { "/opt/mainguard/adapters/bin/claude" });
        Assert.Null(stale.ModelArg);

        var repaired = stale.WithShippedDescription(spec);

        Assert.Equal("--model", repaired.ModelArg);
        Assert.NotNull(repaired.Models);

        // And the two install facts are still the marker's own, not the manifest's.
        Assert.Equal("2.1.218", repaired.Version);
        Assert.Equal(new[] { "/opt/mainguard/adapters/bin/claude" }, repaired.Launch);
    }

    // ---- the store ------------------------------------------------------------------------------

    [Fact]
    public void WithNoChoice_TheAnswerIsEmpty_SoTheCliKeepsItsOwnDefault()
    {
        var models = new AgentModelSwitch();

        // Not a default model of Mainguard's choosing: picking one would be deciding how somebody
        // else's money is spent.
        Assert.Equal("", models.ModelFor(AgentModelRole.Worker, "claude-code"));
        Assert.Equal("", models.ModelFor(AgentModelRole.Coordinator, "claude-code"));
    }

    [Fact]
    public void RoleAndKind_AreSeparateChoices()
    {
        var models = new AgentModelSwitch();

        models.Set(AgentModelRole.Coordinator, "claude-code", "sonnet");
        models.Set(AgentModelRole.Worker, "claude-code", "opus");

        // The whole reason the key is a pair: a cheap planner driving expensive workers is a thing an
        // operator reasonably wants, and one shared setting could not express it.
        Assert.Equal("sonnet", models.ModelFor(AgentModelRole.Coordinator, "claude-code"));
        Assert.Equal("opus", models.ModelFor(AgentModelRole.Worker, "claude-code"));

        // And a choice for one CLI never leaks to another, whose vocabulary is different anyway.
        Assert.Equal("", models.ModelFor(AgentModelRole.Worker, "codex"));
    }

    [Fact]
    public void ABlankModel_ClearsTheChoice_RatherThanStoringEmptiness()
    {
        var models = new AgentModelSwitch();
        models.Set(AgentModelRole.Worker, "claude-code", "opus");

        Assert.Equal("", models.Set(AgentModelRole.Worker, "claude-code", "   "));
        Assert.Equal("", models.ModelFor(AgentModelRole.Worker, "claude-code"));
    }

    [Fact]
    public void AChoiceSurvivesARestart_ThroughTheJsonStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "mg-models-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new AgentModelSwitch(new JsonAgentModelStore(path)).Set(AgentModelRole.Worker, "claude-code", "opus");

            var reloaded = new AgentModelSwitch(new JsonAgentModelStore(path));
            Assert.Equal("opus", reloaded.ModelFor(AgentModelRole.Worker, "claude-code"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ACorruptStore_ReadsAsNoChoice_NeverAsSomeoneElsesModel()
    {
        var path = Path.Combine(Path.GetTempPath(), "mg-models-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ not json at all");

            // The only safe direction: a mangled file must not be able to silently redirect an
            // operator's spend to a model they never picked.
            Assert.Equal("", new AgentModelSwitch(new JsonAgentModelStore(path))
                .ModelFor(AgentModelRole.Worker, "claude-code"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string StarterManifest() => File.ReadAllText(StarterManifestPath());

    private static string StarterManifestPath()
    {
        for (var probe = new DirectoryInfo(AppContext.BaseDirectory); probe is not null; probe = probe.Parent)
        {
            var candidate = Path.Combine(
                probe.FullName, "Mainguard.Agents", "Agents", "Adapters", "adapters.starter.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("adapters.starter.json was not found above the test output directory");
    }
}
