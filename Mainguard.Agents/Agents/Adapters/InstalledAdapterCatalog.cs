using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mainguard.Agents.Agents.Adapters;

/// <summary>
/// The per-adapter install marker written to <see cref="AdapterPaths.VmRegistryDir"/> by the
/// installer (Windows side, over WSL) and read by the daemon (VM side) — the one shared artifact
/// that carries the <c>agentKind</c> → launch-argv mapping across the host/VM boundary. Written
/// LAST, only after a green version-matched health probe, so a marker's presence means "runnable".
/// </summary>
public sealed record InstalledAdapterMarker(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("launch")] IReadOnlyList<string> Launch,
    /// <summary>The env var this CLI reads its model API key from (see
    /// <see cref="AdapterSpec.ApiKeyEnvVar"/>); null = interactive login, never inject a key.</summary>
    [property: JsonPropertyName("apiKeyEnvVar")] string? ApiKeyEnvVar = null,
    /// <summary>The egress hosts this CLI needs (see <see cref="AdapterSpec.EgressHosts"/>), carried
    /// across the host/VM boundary so the daemon can auto-permit them on the default-deny proxy. Null
    /// on markers written before this field existed — re-install the CLI to backfill it.</summary>
    [property: JsonPropertyName("egressHosts")] IReadOnlyList<string>? EgressHosts = null,
    /// <summary>The $HOME-relative login-state files this CLI keeps (see
    /// <see cref="AdapterSpec.CredentialPaths"/>) — the ONLY paths the daemon will restore into /
    /// harvest from the jail's tmpfs home (client-supplied paths are filtered against this list).
    /// Null on markers written before this field existed — re-install the CLI to backfill it.</summary>
    [property: JsonPropertyName("credentialPaths")] IReadOnlyList<string>? CredentialPaths = null,
    /// <summary>The env var this CLI reads its API BASE URL from (see
    /// <see cref="AdapterSpec.BaseUrlEnvVar"/>), carried across the host/VM boundary so the spawn path
    /// can point the CLI at the daemon's model gateway (MG-4). Null on markers written before this
    /// field existed — re-install the CLI to backfill it.</summary>
    [property: JsonPropertyName("baseUrlEnvVar")] string? BaseUrlEnvVar = null,
    /// <summary>The provider host this CLI's model traffic goes to (see
    /// <see cref="AdapterSpec.ModelHost"/>), carried across the host/VM boundary so the spawn path can
    /// record the agent's gateway upstream binding. Null on markers written before this field existed —
    /// re-install the CLI to backfill it.</summary>
    [property: JsonPropertyName("modelHost")] string? ModelHost = null,
    /// <summary>The non-credential configuration files this CLI keeps (see
    /// <see cref="AdapterSpec.SettingsPaths"/>) — the ONLY entries the daemon will restore into /
    /// harvest from a jail's throwaway trees, so a permission grant survives the next spawn. Client-
    /// supplied entries are filtered against this list exactly as credential paths are. Null on markers
    /// written before this field existed — re-install the CLI to backfill it.</summary>
    [property: JsonPropertyName("settingsPaths")] IReadOnlyList<AdapterSettingsPath>? SettingsPaths = null,
    /// <summary>The file this CLI reads unprompted from its working directory (see
    /// <see cref="AdapterSpec.InstructionsFile"/>). Null on markers written before this field existed —
    /// re-install the CLI to backfill it.</summary>
    [property: JsonPropertyName("instructionsFile")] string? InstructionsFile = null,
    /// <summary>The launch flag this CLI takes instruction text on (see
    /// <see cref="AdapterSpec.SystemPromptArg"/>) — the ONLY delivery that reaches a coordinator, whose
    /// /workspace is an empty tmpfs with no host side to write a file to. Null on older markers.</summary>
    [property: JsonPropertyName("systemPromptArg")] string? SystemPromptArg = null,
    /// <summary>The launch flag this CLI takes a PRE-APPROVED COMMAND list on (see
    /// <see cref="AdapterSpec.PreApprovedCommandArg"/>). Carried across the host/VM boundary because the
    /// daemon reads the MARKER, not the manifest — a field that stopped at the manifest would leave every
    /// jail's only tool stalled on an approval prompt no human is watching. Null on markers written
    /// before this field existed; re-install the CLI to backfill it, and until then those jails behave
    /// exactly as they did before.</summary>
    [property: JsonPropertyName("preApprovedCommandArg")] string? PreApprovedCommandArg = null,
    /// <summary>How this CLI spells one pre-approved command (see
    /// <see cref="AdapterSpec.PreApprovedCommandFormat"/>) — a template containing
    /// <see cref="AdapterManifest.PreApprovedCommandPlaceholder"/>. Null on older markers.</summary>
    [property: JsonPropertyName("preApprovedCommandFormat")] string? PreApprovedCommandFormat = null,
    /// <summary>How this CLI takes the daemon's FIRST USER TURN (see
    /// <see cref="AdapterSpec.InitialPromptStyle"/>). Carried across the host/VM boundary because the
    /// daemon reads the MARKER, not the manifest — a field that stopped at the manifest would leave every
    /// worker jail launching with no first turn, which is the deadlock this field exists to close. Null
    /// on markers written before this field existed; re-install the CLI to backfill it, and until then
    /// those jails behave exactly as they did before.</summary>
    [property: JsonPropertyName("initialPromptStyle")] string? InitialPromptStyle = null)
{
    /// <summary>The parsed <see cref="InitialPromptStyle"/>; <see cref="AdapterInitialPromptStyle.None"/>
    /// for an older marker or an unreadable spelling. Unlike the manifest, a marker cannot refuse — it is
    /// already on disk — so the safe reading here is the one that changes no launch line.</summary>
    public AdapterInitialPromptStyle InitialPromptDelivery =>
        AdapterManifest.TryParseInitialPromptStyle(InitialPromptStyle, out var style)
            ? style
            : AdapterInitialPromptStyle.None;

    /// <summary>
    /// The marker an <see cref="AdapterSpec"/> describes — the <b>one</b> place a spec's fields are mapped
    /// onto a marker's.
    ///
    /// <para>It used to be a 14-argument positional constructor call inside <c>AdapterChannel</c>, which is
    /// how a manifest field reaches the daemon by being copied correctly at exactly one call site nobody
    /// is watching. Phase 3's mutation K6 already caught one such omission (<c>initialPromptStyle</c>
    /// declared, carried and honoured, and never written to the marker the daemon reads). Naming the
    /// mapping makes adding a field one edit rather than two, and makes the reverse direction — projecting
    /// the shipped manifest back over an older marker, see
    /// <see cref="WithShippedDescription"/> — the same function read backwards.</para>
    /// </summary>
    public static InstalledAdapterMarker FromSpec(AdapterSpec spec) => new(
        spec.Id,
        spec.Version,
        spec.Launch ?? Array.Empty<string>(),
        spec.ApiKeyEnvVar,
        spec.EgressHosts,
        spec.CredentialPaths,
        spec.BaseUrlEnvVar,
        spec.ModelHost,
        spec.SettingsPaths,
        spec.InstructionsFile,
        spec.SystemPromptArg,
        spec.PreApprovedCommandArg,
        spec.PreApprovedCommandFormat,
        spec.InitialPromptStyle);

    /// <summary>
    /// This marker with every <b>manifest-declared</b> field taken from <paramref name="spec"/>, keeping
    /// only the two things that are facts about the INSTALL rather than about the CLI: the
    /// <see cref="Version"/> that probed green and the <see cref="Launch"/> argv that probed green.
    ///
    /// <para><b>Why the manifest wins.</b> Everything else on a marker — which env var carries the key,
    /// which files are credentials, which are settings, which flag takes instructions, which flag takes a
    /// pre-approved command — is a description of the vendor's CLI, and a marker only ever held a COPY of
    /// the shipped manifest's answer, taken on the day the CLI was installed. That copy goes stale the
    /// moment Mainguard learns something new about a CLI it already installed, and a stale copy is read as
    /// truth: on this machine, <c>preApprovedCommandArg</c> and <c>initialPromptStyle</c> were both shipped
    /// and both absent from a marker written a day earlier, so two fixes were inert on the only install
    /// that mattered while every test stayed green.</para>
    ///
    /// <para><b>Including nulls, deliberately.</b> A field the shipped manifest no longer declares is
    /// REMOVED here rather than inherited from the marker. This set contains a grant of execution
    /// (<see cref="PreApprovedCommandArg"/>), and a revocation that an old marker could veto would not be
    /// a revocation.</para>
    /// </summary>
    public InstalledAdapterMarker WithShippedDescription(AdapterSpec spec) =>
        FromSpec(spec) with { Version = Version, Launch = Launch };

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string SerializeInstance() => JsonSerializer.Serialize(this, Options);
    public static string Serialize(InstalledAdapterMarker marker) => marker.SerializeInstance();

    public static InstalledAdapterMarker? TryDeserialize(string json)
    {
        try
        {
            var marker = JsonSerializer.Deserialize<InstalledAdapterMarker>(json, Options);
            return marker is { Id.Length: > 0, Launch.Count: > 0 } ? marker : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Daemon-side view of the dynamically installed agent CLIs: reads the
/// <c>registry/&lt;id&gt;.json</c> markers under the adapters root and answers
/// "what argv starts agentKind <c>X</c> inside its sandbox?". Reads the directory fresh per call —
/// installs happen while the daemon runs (that is the point of DYNAMIC CLIs), so nothing may cache
/// staleness across an install.
/// </summary>
public sealed class InstalledAdapterCatalog
{
    /// <summary>
    /// The shipped manifest's adapters by id, parsed once per process. The embedded
    /// <c>adapters.starter.json</c> is a compiled-in resource, so this cannot change under a running
    /// daemon and cannot fail for an environmental reason.
    ///
    /// <para>A manifest that will not parse yields an EMPTY map rather than throwing: this is read on the
    /// spawn path, and the pre-projection behaviour (markers used exactly as written) is a working
    /// daemon. <c>AgentCliCatalogTests.StarterManifest_ShouldParse_AndPinEveryAdapter</c> is what makes a
    /// bad edit fail CI instead of degrading here.</para>
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<string, AdapterSpec>> ShippedSpecs = new(() =>
    {
        try
        {
            return AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson())
                .Adapters.ToDictionary(a => a.Id, a => a, StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return new Dictionary<string, AdapterSpec>(StringComparer.Ordinal);
        }
    });

    private readonly string _registryDir;
    private readonly IReadOnlyDictionary<string, AdapterSpec> _shipped;

    /// <summary>The daemon default: the fixed VM layout (<see cref="AdapterPaths.VmRegistryDir"/>).</summary>
    public InstalledAdapterCatalog() : this(AdapterPaths.VmRegistryDir)
    {
    }

    /// <summary>The catalog over the CURRENT host's daemon-side registry — the VM layout where the
    /// daemon runs in the VM, <c>~/mainguard/adapters/registry</c> on macos-host (see
    /// <see cref="AdapterPaths.DaemonSideRoot"/>). Composition roots use THIS, not the bare ctor.</summary>
    public static InstalledAdapterCatalog CreateForHost() =>
        new(AdapterPaths.DaemonSideRegistryDir());

    /// <param name="shippedSpecs">The manifest this catalog projects over the markers it reads; null uses
    /// the app's own embedded starter manifest, which is what every composition root wants. Supplied
    /// explicitly only by the tests that need a manifest they control.</param>
    public InstalledAdapterCatalog(string registryDir, IReadOnlyDictionary<string, AdapterSpec>? shippedSpecs = null)
    {
        _registryDir = registryDir;
        _shipped = shippedSpecs ?? ShippedSpecs.Value;
        // The adapters ROOT is the registry's parent, because that is the layout the installer creates
        // (`<root>/registry/<id>.json` beside `<root>/bin`). Derived rather than hardcoded so the root
        // and the registry cannot disagree: the spawn path bind-mounts this root read-only into every
        // jail, and it used to mount AdapterPaths.VmRoot unconditionally — so a catalog pointed anywhere
        // else described CLIs that lived at one path while the jail was handed another. That is only
        // ever a silent mismatch in production (the default puts both at VmRoot); it becomes a hard
        // container-create failure the moment the two differ.
        Root = Path.GetDirectoryName(registryDir.TrimEnd('/', '\\')) is { Length: > 0 } parent
            ? parent
            : registryDir;
    }

    /// <summary>
    /// The adapters root this catalog's CLIs are installed under — the directory the spawn path
    /// bind-mounts READ-ONLY into every jail at <see cref="AdapterPaths.SandboxMount"/>.
    /// </summary>
    public string Root { get; }

    /// <summary>
    /// All currently installed agent adapters (empty when none / dir absent), each <b>projected through
    /// the shipped manifest</b> — see <see cref="InstalledAdapterMarker.WithShippedDescription"/>.
    ///
    /// <para><b>Why the projection lives on the READ and not in a migration.</b> The alternative designs
    /// were to re-derive markers when the version matches, or to rewrite them on daemon start. Both keep
    /// the marker as a second source of truth and therefore keep the failure: version-matching would not
    /// even have repaired the install that reported this defect (the CLI had been updated forward of the
    /// shipped pin, so the versions differ and every new field would have stayed masked), and a
    /// start-time rewrite is a write that can fail, that can race the installer, and that leaves a window
    /// in which the stale copy is still authoritative. Projecting on read has no window, no write, no
    /// ordering, and nothing left to migrate: a field the app learns about reaches the daemon on the very
    /// next spawn, on installs that predate it, with no re-install and no user action.</para>
    ///
    /// <para>An adapter the shipped manifest does not name — a CLI installed from a future hosted channel
    /// — is returned exactly as its marker was written. There is nothing to project it through, and
    /// inventing a description for it would be worse than carrying its own.</para>
    /// </summary>
    public IReadOnlyList<InstalledAdapterMarker> List()
    {
        if (!Directory.Exists(_registryDir))
            return Array.Empty<InstalledAdapterMarker>();

        var markers = new List<InstalledAdapterMarker>();
        foreach (var file in Directory.EnumerateFiles(_registryDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                if (InstalledAdapterMarker.TryDeserialize(File.ReadAllText(file)) is { } marker)
                    markers.Add(_shipped.TryGetValue(marker.Id, out var spec) ? marker.WithShippedDescription(spec) : marker);
            }
            catch (IOException)
            {
                // A marker mid-write (installer racing us) is skipped this call; the next read sees it.
            }
        }

        return markers;
    }

    /// <summary>The full install marker for <paramref name="agentKind"/> (launch argv + API-key env
    /// var), or null when that CLI is not installed. The agentKind IS the adapter id.</summary>
    public InstalledAdapterMarker? TryGet(string agentKind) =>
        List().FirstOrDefault(m => string.Equals(m.Id, agentKind, StringComparison.Ordinal));

    /// <summary>The launch argv for <paramref name="agentKind"/>, or null when that CLI is not
    /// installed. The agentKind IS the adapter id (e.g. <c>claude-code</c>).</summary>
    public IReadOnlyList<string>? TryGetLaunch(string agentKind) => TryGet(agentKind)?.Launch;

    /// <summary>True when at least one agent CLI is installed — the gate for strict agentKind
    /// validation (an empty catalog means a dev/unprovisioned box; spawns stay permissive there).</summary>
    public bool HasAny() => List().Count > 0;

    /// <summary>
    /// Every <c>agentKind</c> this daemon can actually launch, ordinal-sorted.
    ///
    /// <para><b>Why this is a named member and not a LINQ line at each call site.</b> Two things have to
    /// agree about which kinds exist: the operating instructions a coordinator is handed at spawn, and the
    /// refusal it gets when it names one that is not installed. Those two disagreeing is the MG-12 shape
    /// this repo keeps re-finding, and it is what let a real coordinator burn its first move on
    /// <c>spawn coder</c> — instructions that said <c>spawn &lt;agent-kind&gt;</c> and never said which.
    /// Both now read this, so neither can describe an install that does not exist.</para>
    /// </summary>
    public IReadOnlyList<string> InstalledKinds() =>
        List().Select(m => m.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
}
