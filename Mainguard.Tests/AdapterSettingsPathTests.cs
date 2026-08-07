using System;
using System.Linq;
using Mainguard.Agents.Agents.Adapters;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The manifest half of the CLI-settings round trip: what an adapter may declare as a settings file,
/// and the one overlap the parser must refuse.
///
/// <para>These are schema tests with a security edge. <c>settingsPaths</c> names files Mainguard will
/// WRITE into a jail and READ back out of it, and their content is a permission allowlist — so a
/// malformed root, an escaping path, or a path shared with <c>credentialPaths</c> are not cosmetic
/// problems. The bundled starter manifest is asserted too, because a field nothing declares is a
/// feature nobody gets.</para>
/// </summary>
public class AdapterSettingsPathTests
{
    /// <summary>The shipped channel, as the app really loads it.</summary>
    private static AdapterManifest Starter() =>
        AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson());

    [Fact]
    public void TheBundledClaudeCodeAdapter_DeclaresBothSettingsRoots()
    {
        var claude = Starter().Adapters.Single(a => a.Id == "claude-code");

        Assert.NotNull(claude.SettingsPaths);
        var roots = claude.SettingsPaths!.Select(s => s.Root).ToArray();
        Assert.Contains("workspace", roots);
        Assert.Contains("home", roots);

        // The workspace entry is the one that carries "yes, and don't ask again" grants. Declaring only
        // the home file would persist something the CLI never writes — a fix that looks applied and
        // changes nothing, which is exactly the failure mode this repository keeps paying for.
        var workspace = claude.SettingsPaths!.Single(s => s.Root == "workspace");
        Assert.Equal(".claude/settings.local.json", workspace.Path);
    }

    [Fact]
    public void EveryBundledSettingsPath_IsWellFormed()
    {
        foreach (var adapter in Starter().Adapters)
        {
            foreach (var entry in adapter.SettingsPaths ?? Array.Empty<AdapterSettingsPath>())
            {
                Assert.True(entry.IsWellFormed(), $"{adapter.Id}: '{entry.Root}:{entry.Path}' is not well formed");
            }
        }
    }

    [Fact]
    public void NoBundledAdapter_ListsAPathAsBothACredentialAndASetting()
    {
        // The storage boundary in one assertion: credentials go to the OS keychain, settings to a
        // plaintext per-repo file. A shared path would route a credential into the wrong one.
        foreach (var adapter in Starter().Adapters)
        {
            var credentials = adapter.CredentialPaths ?? Array.Empty<string>();
            foreach (var entry in adapter.SettingsPaths ?? Array.Empty<AdapterSettingsPath>())
            {
                Assert.DoesNotContain(entry.Path, credentials);
            }
        }
    }

    [Fact]
    public void AManifestThatListsOnePathAsBothACredentialAndASetting_IsRefused()
    {
        var ex = Assert.Throws<AdapterManifestException>(
            () => AdapterManifest.Parse(ManifestWith(
                """
                "credentialPaths": [".claude/.credentials.json"],
                "settingsPaths": [{ "root": "home", "path": ".claude/.credentials.json" }]
                """)));

        Assert.Equal(AdapterManifestError.Malformed, ex.Error);
        Assert.Contains("credentialPaths", ex.Message);
    }

    [Theory]
    [InlineData("hOme")]      // wrong case — an exact match, never a fuzzy one
    [InlineData("root")]      // a plausible-looking root this build does not know
    [InlineData("")]
    public void AnUnknownSettingsRoot_IsRefused_RatherThanDefaulted(string root)
    {
        var ex = Assert.Throws<AdapterManifestException>(
            () => AdapterManifest.Parse(ManifestWith(
                $$"""
                "settingsPaths": [{ "root": "{{root}}", "path": ".probe/settings.json" }]
                """)));

        Assert.Equal(AdapterManifestError.Malformed, ex.Error);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../../../etc/passwd")]
    [InlineData("~/.ssh/authorized_keys")]
    [InlineData(".claude\\settings.json")]
    public void ASettingsPathThatEscapesItsRoot_IsRefused(string path)
    {
        var ex = Assert.Throws<AdapterManifestException>(
            () => AdapterManifest.Parse(ManifestWith(
                $$"""
                "settingsPaths": [{ "root": "home", "path": "{{path.Replace("\\", "\\\\")}}" }]
                """)));

        Assert.Equal(AdapterManifestError.Malformed, ex.Error);
    }

    [Fact]
    public void ADuplicateSettingsEntry_IsRefused()
    {
        Assert.Throws<AdapterManifestException>(
            () => AdapterManifest.Parse(ManifestWith(
                """
                "settingsPaths": [
                  { "root": "home", "path": ".probe/settings.json" },
                  { "root": "home", "path": ".probe/settings.json" }
                ]
                """)));
    }

    [Fact]
    public void ASettingsPathIsAcceptedUnderEitherRoot()
    {
        var manifest = AdapterManifest.Parse(ManifestWith(
            """
            "settingsPaths": [
              { "root": "home", "path": ".probe/settings.json" },
              { "root": "workspace", "path": ".probe/settings.local.json" }
            ]
            """));

        var adapter = Assert.Single(manifest.Adapters);
        Assert.Equal(
            new[] { AdapterSettingsRoot.Home, AdapterSettingsRoot.Workspace },
            adapter.SettingsPaths!.Select(s => s.ParsedRoot).ToArray());
    }

    /// <summary>A minimal, otherwise-valid one-adapter manifest with <paramref name="extra"/> spliced in,
    /// so each test's refusal can only come from the field it is about.</summary>
    private static string ManifestWith(string extra) =>
        $$"""
        {
          "adapters": [
            {
              "id": "probe-cli",
              "displayName": "Probe",
              "version": "1.0.0",
              "provenance": "none",
              "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
              "installCmd": ["npm", "install", "-g", "--ignore-scripts", "{payload}"],
              "healthProbe": { "command": ["probe", "--version"], "expectedVersionSubstring": "1.0.0" },
              {{extra}}
            }
          ]
        }
        """;
}
