using Mainguard.Agents.Agents.Adapters;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-4 stage 2 — the adapter declares which env var its CLI reads its API <b>base URL</b> from.
///
/// <para>This is the seam that makes BYOK confinement possible at all. The provider key is written
/// verbatim into the agent-readable <c>/run/secrets/agent/agent.env</c> today; to stop that, the CLI has to
/// be pointed at the daemon's model gateway so the jail can hold a Mainguard session token while the
/// real key stays daemon-side and is injected at the network hop. A CLI can only be redirected if we
/// know the variable it honours (<c>ANTHROPIC_BASE_URL</c>, <c>OPENAI_BASE_URL</c>, …), and that fact
/// has to survive the manifest → install-marker → daemon hop.</para>
///
/// <para>A null value is meaningful, not merely absent: it says this CLI <b>cannot</b> be redirected,
/// so it must reach the provider directly and confinement does not apply to it.</para>
/// </summary>
public sealed class AdapterBaseUrlEnvVarTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string ManifestWithBaseUrl = $$"""
    {
      "adapters": [{
        "id": "claude-code",
        "displayName": "Claude Code",
        "version": "1.2.3",
        "provenance": "npm-registry-signature",
        "sha256": "{{Sha}}",
        "installCmd": ["npm", "install", "-g", "x"],
        "configShims": null,
        "healthProbe": { "command": ["claude", "--version"], "expectedVersionSubstring": "1.2.3" },
        "launch": ["claude"],
        "apiKeyEnvVar": "ANTHROPIC_API_KEY",
        "baseUrlEnvVar": "ANTHROPIC_BASE_URL"
      }]
    }
    """;

    [Fact]
    public void Manifest_ParsesBaseUrlEnvVar()
    {
        var manifest = AdapterManifest.Parse(ManifestWithBaseUrl);
        var spec = Assert.Single(manifest.Adapters);

        Assert.Equal("ANTHROPIC_BASE_URL", spec.BaseUrlEnvVar);
        Assert.Equal("ANTHROPIC_API_KEY", spec.ApiKeyEnvVar);
    }

    // Absent is legal and means "this CLI cannot be redirected" — it must not become a parse failure,
    // or every existing manifest breaks.
    [Fact]
    public void Manifest_WithoutBaseUrlEnvVar_ParsesAsNull()
    {
        var json = ManifestWithBaseUrl
            .Replace("\"apiKeyEnvVar\": \"ANTHROPIC_API_KEY\",", "\"apiKeyEnvVar\": \"ANTHROPIC_API_KEY\"")
            .Replace("\"baseUrlEnvVar\": \"ANTHROPIC_BASE_URL\"", string.Empty);

        var spec = Assert.Single(AdapterManifest.Parse(json).Adapters);

        Assert.Null(spec.BaseUrlEnvVar);
        Assert.Equal("ANTHROPIC_API_KEY", spec.ApiKeyEnvVar); // the rest of the record still parses
    }

    // The value is injected into the jail's environment, so a malformed name would corrupt the env
    // file for every entry — same rule the api-key variable already enforces.
    [Fact]
    public void Manifest_RejectsAMalformedBaseUrlEnvVarName()
    {
        var json = ManifestWithBaseUrl.Replace("ANTHROPIC_BASE_URL", "not a valid name");

        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(json));
        Assert.Contains("baseUrlEnvVar", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    // The daemon reads the MARKER, not the manifest — the fact is useless if it doesn't cross that hop.
    [Fact]
    public void InstallMarker_RoundTripsBaseUrlEnvVar()
    {
        var marker = new InstalledAdapterMarker(
            "claude-code", "2.1.0", new[] { "claude" },
            ApiKeyEnvVar: "ANTHROPIC_API_KEY",
            EgressHosts: null,
            CredentialPaths: null,
            BaseUrlEnvVar: "ANTHROPIC_BASE_URL",
            ModelHost: "api.anthropic.com");

        var round = InstalledAdapterMarker.TryDeserialize(InstalledAdapterMarker.Serialize(marker));

        Assert.NotNull(round);
        Assert.Equal("ANTHROPIC_BASE_URL", round!.BaseUrlEnvVar);
        // The launcher needs BOTH across this hop — a marker that carried only half the pair would make
        // TryConfineToGateway refuse, silently, on a CLI the manifest says is confinable.
        Assert.Equal("api.anthropic.com", round.ModelHost);
    }

    // ---- the BUNDLED channel's actual declarations (MG-4 item 1) ----------------------------------
    //
    // The two tests below are about the shipped data, not the parser. PR #298 built the whole
    // confinement mechanism and then left every bundled adapter with baseUrlEnvVar/modelHost ABSENT, so
    // TryConfineToGateway refused every spawn and a BYOK jail kept receiving the raw provider key. A
    // mechanism nothing declares is indistinguishable from no mechanism, and nothing failed to say so.

    /// <summary>
    /// The confinement pair is all-or-nothing. Declaring a base-URL variable without a model host (or
    /// vice versa) reads as "confinable" to a human and is refused by the launcher, which is the exact
    /// looks-applied-but-isn't shape this repo keeps producing. An adapter with no <c>apiKeyEnvVar</c>
    /// is never BYOK, so declaring the pair on it would be decoration.
    /// </summary>
    [Fact]
    public void BundledStarterCatalog_ConfinementPair_IsAllOrNothing_AndOnlyOnBYOKAdapters()
    {
        foreach (var adapter in AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson()).Adapters)
        {
            var hasBaseUrl = !string.IsNullOrWhiteSpace(adapter.BaseUrlEnvVar);
            var hasModelHost = !string.IsNullOrWhiteSpace(adapter.ModelHost);

            Assert.True(
                hasBaseUrl == hasModelHost,
                $"'{adapter.Id}' declares baseUrlEnvVar={adapter.BaseUrlEnvVar ?? "<null>"} but "
                + $"modelHost={adapter.ModelHost ?? "<null>"}. SandboxAgentLauncher.TryConfineToGateway "
                + "requires BOTH, so half a pair is a confinement that can never engage.");

            if (hasBaseUrl)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(adapter.ApiKeyEnvVar),
                    $"'{adapter.Id}' declares a confinement pair but no apiKeyEnvVar, so no BYOK key is "
                    + "ever injected for it and the pair can never be used.");
            }
        }
    }

    /// <summary>
    /// Pins the vendor facts measured against the pinned tarballs on 2026-08-05 (see the
    /// <c>_comment</c> block in <c>adapters.starter.json</c> for how each was established).
    ///
    /// <para>This is a CHANGE DETECTOR, not a restatement of the JSON: it fails when someone adds a
    /// plausible-looking variable to codex/qwen/opencode without redoing the measurement, and it fails
    /// when claude-code's or gemini's pair is dropped or renamed. A wrong name here is worse than none
    /// — the CLI would keep calling the provider directly while Mainguard believed it was fronted.</para>
    /// </summary>
    [Theory]
    [InlineData("claude-code", "ANTHROPIC_BASE_URL", "api.anthropic.com")]
    [InlineData("gemini-cli", "GOOGLE_GEMINI_BASE_URL", "generativelanguage.googleapis.com")]
    // No base-URL ENVIRONMENT VARIABLE exists in these CLIs' shipped binaries/bundles. codex takes its
    // endpoint from config.toml only; qwen needs OPENAI_MODEL alongside and is OAuth-first with no
    // apiKeyEnvVar; opencode is multi-provider with per-provider base URLs in its own config.
    [InlineData("codex", null, null)]
    [InlineData("qwen-code", null, null)]
    [InlineData("opencode", null, null)]
    public void BundledStarterCatalog_DeclaresTheMeasuredConfinementFacts(
        string id, string? baseUrlEnvVar, string? modelHost)
    {
        var adapter = Assert.Single(
            AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson()).Adapters,
            a => a.Id == id);

        Assert.Equal(baseUrlEnvVar, adapter.BaseUrlEnvVar);
        Assert.Equal(modelHost, adapter.ModelHost);
    }

    // Markers written before this field existed must still load (the field backfills on re-install).
    [Fact]
    public void InstallMarker_WrittenBeforeTheFieldExisted_StillLoads()
    {
        const string legacy = """
        {"id":"claude-code","version":"2.1.0","provenance":"none","launch":["claude"],"apiKeyEnvVar":"ANTHROPIC_API_KEY"}
        """;

        var round = InstalledAdapterMarker.TryDeserialize(legacy);

        Assert.NotNull(round);
        Assert.Null(round!.BaseUrlEnvVar);
        Assert.Equal("ANTHROPIC_API_KEY", round.ApiKeyEnvVar);
    }
}
