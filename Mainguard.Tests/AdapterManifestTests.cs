using Mainguard.Agents.Agents.Adapters;

namespace Mainguard.Tests;

/// <summary>TI-P2-22 #4: adapter manifest schema corpus — valid, missing probe, unpinned version
/// (<c>@latest</c> refused by schema), unknown fields, bad hash, duplicate id.</summary>
public class AdapterManifestTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static string Manifest(string body) => $$"""{ "adapters": [ {{body}} ] }""";

    private const string ValidAdapter = $$"""
    {
      "id": "claude-code",
      "displayName": "Claude Code",
      "version": "1.2.3",
      "provenance": "npm-registry-signature",
      "sha256": "{{Sha}}",
      "installCmd": ["npm", "install", "-g", "@anthropic-ai/claude-code@1.2.3"],
      "configShims": [{ "path": "/home/agent/.claude/settings.json", "content": "{}" }],
      "healthProbe": { "command": ["claude", "--version"], "expectedVersionSubstring": "1.2.3" }
    }
    """;

    [Fact]
    public void Valid_ShouldParse()
    {
        var m = AdapterManifest.Parse(Manifest(ValidAdapter));
        var a = Assert.Single(m.Adapters);
        Assert.Equal("claude-code", a.Id);
        Assert.Equal("1.2.3", a.Version);
        Assert.Equal("1.2.3", a.HealthProbe!.ExpectedVersionSubstring);
        Assert.Single(a.ConfigShims!);
    }

    [Fact]
    public void EgressHosts_Valid_ParseAndAreReadable()
    {
        var body = $$"""
        { "id": "claude-code", "displayName": "Claude Code", "version": "1.2.3", "provenance": "none", "sha256": "{{Sha}}",
          "installCmd": ["true"], "healthProbe": { "command": ["x"], "expectedVersionSubstring": "1" },
          "egressHosts": ["platform.claude.com", "statsig.anthropic.com"] }
        """;
        var a = Assert.Single(AdapterManifest.Parse(Manifest(body)).Adapters);
        Assert.Equal(new[] { "platform.claude.com", "statsig.anthropic.com" }, a.EgressHosts);
    }

    [Fact]
    public void EgressHosts_GitHost_IsRejected_A6()
    {
        var body = $$"""
        { "id": "x", "displayName": "X", "version": "1.0.0", "provenance": "none", "sha256": "{{Sha}}",
          "installCmd": ["true"], "healthProbe": { "command": ["x"], "expectedVersionSubstring": "1" },
          "egressHosts": ["github.com"] }
        """;
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(body)));
        Assert.Equal(AdapterManifestError.Malformed, ex.Error);
    }

    [Theory]
    [InlineData("https://platform.claude.com")]
    [InlineData("platform.claude.com/path")]
    [InlineData("platform.claude.com:443")]
    [InlineData("has space")]
    [InlineData("nodot")]
    public void EgressHosts_NotBareHostname_IsRejected(string host)
    {
        var body = $$"""
        { "id": "x", "displayName": "X", "version": "1.0.0", "provenance": "none", "sha256": "{{Sha}}",
          "installCmd": ["true"], "healthProbe": { "command": ["x"], "expectedVersionSubstring": "1" },
          "egressHosts": ["{{host}}"] }
        """;
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(body)));
        Assert.Equal(AdapterManifestError.Malformed, ex.Error);
    }

    [Fact]
    public void MissingHealthProbe_ShouldBeRejected()
    {
        var body = $$"""
        { "id": "x", "displayName": "X", "version": "1.0.0", "provenance": "none", "sha256": "{{Sha}}",
          "installCmd": ["true"] }
        """;
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(body)));
        Assert.Equal(AdapterManifestError.MissingField, ex.Error);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("^1.0.0")]
    [InlineData("*")]
    // MG-40: a wildcard dot-segment is a RANGE — `1.x` resolves to whatever the registry serves at
    // install time, so the manifest's sha256 would stop describing the bytes that land. These parsed
    // as pinned because the old wildcard guard only fired when the version carried no digit at all.
    [InlineData("1.x")]
    [InlineData("1.X")]
    [InlineData("1.2.x")]
    [InlineData("1.x.x")]
    public void UnpinnedVersion_ShouldBeRejected(string version)
    {
        var body = $$"""
        { "id": "x", "displayName": "X", "version": "{{version}}", "provenance": "none", "sha256": "{{Sha}}",
          "installCmd": ["true"], "healthProbe": { "command": ["x"], "expectedVersionSubstring": "1" } }
        """;
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(body)));
        Assert.Equal(AdapterManifestError.UnpinnedVersion, ex.Error);
    }

    /// <summary>MG-40, both directions: a range is not a pin, and a concrete tag that merely contains an
    /// <c>x</c> still is (the fix must not start refusing real releases like <c>1.0.0-hotfix</c>).</summary>
    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("0.2.71", true)]
    [InlineData("1.0.0-hotfix", true)]
    [InlineData("2.1.0-linux-x64", true)]
    [InlineData("1.x", false)]
    [InlineData("1.2.X", false)]
    [InlineData("x", false)]
    [InlineData("~1.2.3", false)]
    public void IsPinnedVersion_TreatsRangesAsUnpinned(string version, bool pinned)
        => Assert.Equal(pinned, AdapterManifest.IsPinnedVersion(version));

    [Fact]
    public void InstallCmdWithAtLatest_ShouldBeRejected()
    {
        var body = $$"""
        { "id": "x", "displayName": "X", "version": "1.0.0", "provenance": "none", "sha256": "{{Sha}}",
          "installCmd": ["npm", "install", "-g", "claude@latest"],
          "healthProbe": { "command": ["x"], "expectedVersionSubstring": "1" } }
        """;
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(body)));
        Assert.Equal(AdapterManifestError.UnpinnedVersion, ex.Error);
    }

    [Fact]
    public void UnknownField_ShouldBeRejectedByStrictSchema()
    {
        var body = $$"""
        { "id": "x", "displayName": "X", "version": "1.0.0", "provenance": "none", "sha256": "{{Sha}}",
          "installCmd": ["true"], "surpriseField": true,
          "healthProbe": { "command": ["x"], "expectedVersionSubstring": "1" } }
        """;
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(body)));
        Assert.Equal(AdapterManifestError.Malformed, ex.Error);
    }

    [Fact]
    public void BadHash_ShouldBeRejected()
    {
        var body = $$"""
        { "id": "x", "displayName": "X", "version": "1.0.0", "provenance": "none", "sha256": "not-a-hash",
          "installCmd": ["true"], "healthProbe": { "command": ["x"], "expectedVersionSubstring": "1" } }
        """;
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(body)));
        Assert.Equal(AdapterManifestError.BadHash, ex.Error);
    }

    [Fact]
    public void DuplicateId_ShouldBeRejected()
    {
        var m = Manifest($"{ValidAdapter}, {ValidAdapter}");
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(m));
        Assert.Equal(AdapterManifestError.DuplicateId, ex.Error);
    }

    // ---- Audit fix #13: per-adapter API-key env var --------------------------------------------------

    [Fact]
    public void ApiKeyEnvVar_Valid_ShouldParse()
    {
        var adapter = ValidAdapter.TrimEnd().TrimEnd('}') + @", ""apiKeyEnvVar"": ""OPENAI_API_KEY"" }";
        var m = AdapterManifest.Parse(Manifest(adapter));

        Assert.Equal("OPENAI_API_KEY", Assert.Single(m.Adapters).ApiKeyEnvVar);
    }

    [Fact]
    public void ApiKeyEnvVar_Absent_IsNull_MeaningInteractiveLogin()
    {
        Assert.Null(Assert.Single(AdapterManifest.Parse(Manifest(ValidAdapter)).Adapters).ApiKeyEnvVar);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1BAD")]
    [InlineData("HAS SPACE")]
    [InlineData("HAS-DASH")]
    public void ApiKeyEnvVar_Invalid_ShouldBeRejected(string bad)
    {
        var adapter = ValidAdapter.TrimEnd().TrimEnd('}') + $@", ""apiKeyEnvVar"": ""{bad}"" }}";
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(adapter)));
        Assert.Equal(AdapterManifestError.Malformed, ex.Error);
    }

    // ---- CLI login persistence: credentialPaths ------------------------------------------------------

    [Fact]
    public void CredentialPaths_Valid_ParseAndAreReadable()
    {
        var adapter = ValidAdapter.TrimEnd().TrimEnd('}')
            + @", ""credentialPaths"": ["".claude/.credentials.json"", "".claude.json""] }";
        var a = Assert.Single(AdapterManifest.Parse(Manifest(adapter)).Adapters);
        Assert.Equal(new[] { ".claude/.credentials.json", ".claude.json" }, a.CredentialPaths);
    }

    [Fact]
    public void CredentialPaths_Absent_IsNull_MeaningNoLoginState()
    {
        Assert.Null(Assert.Single(AdapterManifest.Parse(Manifest(ValidAdapter)).Adapters).CredentialPaths);
    }

    [Theory]
    [InlineData("/etc/passwd")]              // absolute — escapes $HOME
    [InlineData("~/.claude.json")]           // tilde — not a literal path in the jail
    [InlineData("../other-home/creds")]      // dot-dot — escapes $HOME
    [InlineData(".claude/../../etc/creds")]  // embedded dot-dot
    [InlineData(".claude\\creds.json")]      // backslash — Windows separator smuggling
    [InlineData("")]                         // empty
    public void CredentialPaths_Unsafe_ShouldBeRejected(string bad)
    {
        var adapter = ValidAdapter.TrimEnd().TrimEnd('}')
            + $@", ""credentialPaths"": [""{bad.Replace("\\", "\\\\")}""] }}";
        var ex = Assert.Throws<AdapterManifestException>(() => AdapterManifest.Parse(Manifest(adapter)));
        Assert.Equal(AdapterManifestError.Malformed, ex.Error);
    }

    // ---- MG-9: every shipped install runs script-free ------------------------------------------

    [Fact]
    public void BundledStarterCatalog_EveryInstallCommand_IsScriptFree()
    {
        // `npm install <tarball>` runs the preinstall/install/postinstall lifecycle scripts of the
        // pinned package AND of every dependency npm resolves for it — arbitrary upstream code
        // executing inside MainguardEnv at install time, before any health probe or sandbox boundary
        // applies. The sha256 pin does not help: it proves the tarball is the one we chose, not that
        // running the code inside it is safe.
        //
        // This is a structural guard, so a future adapter cannot be added without the flag. See
        // AdapterChannelTests' poison canary for the behavioural half.
        foreach (var adapter in AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson()).Adapters)
        {
            var manager = adapter.InstallCmd[0];
            if (manager is not ("npm" or "pnpm" or "yarn"))
                continue; // a non-JS installer has no lifecycle-script surface to close

            Assert.True(
                adapter.InstallCmd.Contains("--ignore-scripts"),
                $"'{adapter.Id}' installs with {manager} but without --ignore-scripts: an upstream "
                + "postinstall (its own, or any transitive dependency's) would execute in the VM.");
        }
    }

    [Fact]
    public void BundledStarterCatalog_StillConsumesTheHashVerifiedPayload()
    {
        // --ignore-scripts must not have been bought by loosening anything else: every install still
        // consumes the staged {payload} file the pin covers, rather than re-resolving from a registry.
        foreach (var adapter in AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson()).Adapters)
        {
            Assert.Contains(AdapterChannel.PayloadToken, adapter.InstallCmd);
        }
    }

    [Fact]
    public void BundledStarterCatalog_CredentialPaths_AllPassTheHomeRelativeGate()
    {
        // The shipped catalog must never regress the gate its own spawn/harvest paths trust.
        foreach (var adapter in AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson()).Adapters)
        {
            foreach (var path in adapter.CredentialPaths ?? System.Array.Empty<string>())
            {
                Assert.True(AdapterManifest.IsHomeRelativeFilePath(path),
                    $"'{adapter.Id}' ships an unsafe credentialPaths entry: '{path}'");
            }
        }
    }
}
