using Mainguard.Agents.Agents.Adapters;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-4 stage 2 — the adapter declares which env var its CLI reads its API <b>base URL</b> from.
///
/// <para>This is the seam that makes BYOK confinement possible at all. The provider key is written
/// verbatim into the agent-readable <c>/run/secrets/agent.env</c> today; to stop that, the CLI has to
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
            BaseUrlEnvVar: "ANTHROPIC_BASE_URL");

        var round = InstalledAdapterMarker.TryDeserialize(InstalledAdapterMarker.Serialize(marker));

        Assert.NotNull(round);
        Assert.Equal("ANTHROPIC_BASE_URL", round!.BaseUrlEnvVar);
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
