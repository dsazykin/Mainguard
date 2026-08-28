using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents.Adapters;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <b>Defect D5a — an install that predates a manifest field must pick the field up without a re-install.</b>
///
/// <para><b>What was measured.</b> On the reporting machine
/// <c>~/mainguard/adapters/registry/claude-code.json</c> carried no <c>preApprovedCommandArg</c>,
/// no <c>preApprovedCommandFormat</c> and no <c>initialPromptStyle</c> — it had been written the day
/// before those fields existed. The daemon reads the MARKER, not the manifest, so two shipped fixes were
/// completely inert on the only install that mattered while every test stayed green. Worse, that install's
/// CLI had been UPDATED forward of the shipped pin (2.1.234 vs 2.1.218), so the obvious migration —
/// "re-derive the marker when the versions match" — would have repaired nothing at all there.</para>
///
/// <para><b>The fix.</b> The marker stops being a second source of truth. A marker is a record of an
/// INSTALL: the version that probed green and the argv that probed green. Everything else on it is a
/// description of the vendor's CLI, which the shipped manifest owns, so the catalog projects the manifest
/// over every marker it reads. No migration, no window, no write, and no field can ever be masked by an
/// older copy of itself again.</para>
/// </summary>
public class AdapterMarkerProjectionTests : IDisposable
{
    private const string Id = "probe-cli";

    private readonly string _registry =
        Path.Combine(Path.GetTempPath(), "mg-marker-projection-" + Guid.NewGuid().ToString("N")[..8]);

    public AdapterMarkerProjectionTests() => Directory.CreateDirectory(_registry);

    public void Dispose()
    {
        try { Directory.Delete(_registry, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>A spec carrying every manifest-declared field, so a projection that quietly drops one is
    /// visible rather than merely untested.</summary>
    private static AdapterSpec Spec(string version = "2.0.0") => new(
        Id: Id,
        DisplayName: "Probe CLI",
        Version: version,
        Sha256: new string('a', 64),
        InstallCmd: new[] { "npm", "i", "{payload}" },
        ConfigShims: null,
        HealthProbe: new HealthProbe(new[] { "probe", "--version" }, version),
        PayloadUrl: "https://example.invalid/probe.tgz",
        Launch: new[] { "/opt/mainguard/adapters/bin/probe" },
        ApiKeyEnvVar: "PROBE_API_KEY",
        EgressHosts: new[] { "probe.example.invalid" },
        CredentialPaths: new[] { ".probe/creds.json" },
        SettingsPaths: new[] { new AdapterSettingsPath("workspace", ".probe/settings.local.json") },
        BaseUrlEnvVar: "PROBE_BASE_URL",
        ModelHost: "api.probe.invalid",
        InstructionsFile: "PROBE.md",
        SystemPromptArg: "--append-system-prompt",
        PreApprovedCommandArg: "--allowedTools",
        PreApprovedCommandFormat: "Bash({command}:*)",
        InitialPromptStyle: "first-positional");

    private InstalledAdapterCatalog CatalogOver(AdapterSpec? shipped) =>
        new(_registry, shipped is null
            ? new Dictionary<string, AdapterSpec>(StringComparer.Ordinal)
            : new Dictionary<string, AdapterSpec>(StringComparer.Ordinal) { [shipped.Id] = shipped });

    private void WriteMarker(InstalledAdapterMarker marker) =>
        File.WriteAllText(Path.Combine(_registry, marker.Id + ".json"), InstalledAdapterMarker.Serialize(marker));

    /// <summary>
    /// <b>The defect, reproduced and closed.</b> The marker found on the reporting machine — an older
    /// install with none of the newer fields — reads back with them, because the shipped manifest declares
    /// them. This is the state in which the pre-approval fix and the first-turn fix were both inert.
    /// </summary>
    [Fact]
    public void AMarkerWrittenBeforeAFieldExisted_ReadsBackWithIt()
    {
        WriteMarker(new InstalledAdapterMarker(Id, "2.1.234", new[] { "/opt/mainguard/adapters/bin/probe" }));

        var marker = CatalogOver(Spec()).TryGet(Id);

        Assert.NotNull(marker);
        Assert.Equal("--allowedTools", marker!.PreApprovedCommandArg);
        Assert.Equal("Bash({command}:*)", marker.PreApprovedCommandFormat);
        Assert.Equal(AdapterInitialPromptStyle.FirstPositional, marker.InitialPromptDelivery);
        Assert.Equal("--append-system-prompt", marker.SystemPromptArg);
        Assert.Equal("PROBE.md", marker.InstructionsFile);
    }

    /// <summary>
    /// <b>The version gate that was NOT chosen, held as a test.</b> The install that reported this defect
    /// had been updated forward of the shipped pin, so "re-derive when the version matches" would have
    /// left it exactly as broken. Projection is by ADAPTER ID: the fields describe a vendor's CLI, and the
    /// only copy of that description a marker ever held came from a manifest anyway — an older one.
    /// </summary>
    [Fact]
    public void TheProjectionAppliesEvenWhenTheInstalledVersionHasMovedPastTheShippedPin()
    {
        WriteMarker(new InstalledAdapterMarker(Id, "2.1.234", new[] { "/opt/mainguard/adapters/bin/probe" }));

        var marker = CatalogOver(Spec("2.1.218")).TryGet(Id);

        Assert.Equal("--allowedTools", marker!.PreApprovedCommandArg);
    }

    /// <summary>
    /// The two things the marker keeps, because only the INSTALL knows them: the version that probed green
    /// and the argv that probed green. Taking the manifest's argv would claim runnability for a path
    /// nothing on this machine ever executed.
    /// </summary>
    [Fact]
    public void TheInstalledVersionAndArgv_AreNeverOverwrittenByTheManifest()
    {
        WriteMarker(new InstalledAdapterMarker(Id, "2.1.234", new[] { "/somewhere/else/probe" }));

        var marker = CatalogOver(Spec("2.1.218")).TryGet(Id);

        Assert.Equal("2.1.234", marker!.Version);
        Assert.Equal(new[] { "/somewhere/else/probe" }, marker.Launch);
    }

    /// <summary>
    /// <b>Revocation works, and that is why nulls project too.</b> This set contains a grant of execution.
    /// A manifest that stops declaring <c>preApprovedCommandArg</c> is withdrawing it, and a withdrawal an
    /// old marker could veto would not be a withdrawal.
    /// </summary>
    [Fact]
    public void AFieldTheShippedManifestNoLongerDeclares_IsRemovedRatherThanInherited()
    {
        WriteMarker(new InstalledAdapterMarker(
            Id, "2.0.0", new[] { "/opt/mainguard/adapters/bin/probe" },
            PreApprovedCommandArg: "--allowedTools",
            PreApprovedCommandFormat: "Bash({command}:*)"));

        var revoked = Spec() with { PreApprovedCommandArg = null, PreApprovedCommandFormat = null };
        var marker = CatalogOver(revoked).TryGet(Id);

        Assert.Null(marker!.PreApprovedCommandArg);
        Assert.Null(marker.PreApprovedCommandFormat);
    }

    /// <summary>
    /// An adapter the shipped manifest does not name — a CLI from some future hosted channel — is returned
    /// exactly as its marker was written. There is nothing to project it through, and inventing a
    /// description for it would be worse than carrying its own.
    /// </summary>
    [Fact]
    public void AnAdapterTheShippedManifestDoesNotName_IsLeftExactlyAsWritten()
    {
        WriteMarker(new InstalledAdapterMarker(
            "third-party", "1.0.0", new[] { "/opt/mainguard/adapters/bin/tp" },
            ApiKeyEnvVar: "TP_KEY", SystemPromptArg: "--sys"));

        var marker = CatalogOver(Spec()).TryGet("third-party");

        Assert.Equal("TP_KEY", marker!.ApiKeyEnvVar);
        Assert.Equal("--sys", marker.SystemPromptArg);
    }

    /// <summary>
    /// The mapping used by the writer and the projector is one function, so a manifest field that reaches
    /// neither is a single omission rather than two. Phase 3's mutation K6 was exactly this shape: a field
    /// declared, carried and honoured, and never written into the marker the daemon reads.
    /// </summary>
    [Fact]
    public void FromSpec_CarriesEveryManifestDeclaredField()
    {
        var spec = Spec();
        var marker = InstalledAdapterMarker.FromSpec(spec);

        Assert.Equal(spec.Id, marker.Id);
        Assert.Equal(spec.Version, marker.Version);
        Assert.Equal(spec.Launch, marker.Launch);
        Assert.Equal(spec.ApiKeyEnvVar, marker.ApiKeyEnvVar);
        Assert.Equal(spec.EgressHosts, marker.EgressHosts);
        Assert.Equal(spec.CredentialPaths, marker.CredentialPaths);
        Assert.Equal(spec.BaseUrlEnvVar, marker.BaseUrlEnvVar);
        Assert.Equal(spec.ModelHost, marker.ModelHost);
        Assert.Equal(spec.SettingsPaths, marker.SettingsPaths);
        Assert.Equal(spec.InstructionsFile, marker.InstructionsFile);
        Assert.Equal(spec.SystemPromptArg, marker.SystemPromptArg);
        Assert.Equal(spec.PreApprovedCommandArg, marker.PreApprovedCommandArg);
        Assert.Equal(spec.PreApprovedCommandFormat, marker.PreApprovedCommandFormat);
        Assert.Equal(spec.InitialPromptStyle, marker.InitialPromptStyle);
    }

    /// <summary>
    /// <b>The end-to-end statement, over the SHIPPED manifest and the real default catalog.</b> Every
    /// starter adapter's marker, however old, reads back describing the CLI the app currently ships — so
    /// this cannot pass by a fixture agreeing with itself. Without it the tests above would all be about a
    /// manifest the daemon never loads.
    /// </summary>
    [Fact]
    public void EveryShippedAdapter_ProjectsFromTheRealBundledManifest()
    {
        var shipped = AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson()).Adapters;
        foreach (var spec in shipped)
        {
            WriteMarker(new InstalledAdapterMarker(spec.Id, "0.0.1-ancient", new[] { "/bin/" + spec.Id }));
        }

        var catalog = new InstalledAdapterCatalog(_registry);

        foreach (var spec in shipped)
        {
            var marker = catalog.TryGet(spec.Id);
            Assert.NotNull(marker);
            Assert.Equal(spec.PreApprovedCommandArg, marker!.PreApprovedCommandArg);
            Assert.Equal(spec.InitialPromptStyle, marker.InitialPromptStyle);
            Assert.Equal(spec.SettingsPaths, marker.SettingsPaths);
            // …and still the install's own facts.
            Assert.Equal("0.0.1-ancient", marker.Version);
        }

        // The concrete case the defect was reported against.
        var claude = catalog.TryGet("claude-code");
        Assert.NotNull(claude);
        Assert.Equal("--allowedTools", claude!.PreApprovedCommandArg);
        Assert.Equal(AdapterInitialPromptStyle.FirstPositional, claude.InitialPromptDelivery);
    }

    /// <summary>The kinds the catalog reports are the marker ids, ordinal-sorted — the one set both the
    /// coordinator's instructions and its spawn refusal read (D1).</summary>
    [Fact]
    public void InstalledKinds_AreTheMarkerIds_Sorted()
    {
        foreach (var id in new[] { "zeta-cli", "alpha-cli" })
        {
            WriteMarker(new InstalledAdapterMarker(id, "1.0.0", new[] { "/bin/" + id }));
        }

        Assert.Equal(new[] { "alpha-cli", "zeta-cli" }, new InstalledAdapterCatalog(_registry).InstalledKinds());
    }
}
