using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mainguard.Agents.Agents.Adapters;

/// <summary>Why an adapter manifest was refused. Every rejection is typed — never a bare parse throw.</summary>
public enum AdapterManifestError
{
    /// <summary>The JSON was malformed or an unknown field was present (strict schema).</summary>
    Malformed,
    /// <summary>An adapter is missing a required field (id, version, sha256, install cmd, health probe).</summary>
    MissingField,
    /// <summary>A version is not pinned to a concrete release (e.g. <c>latest</c>, <c>@latest</c>, a range).</summary>
    UnpinnedVersion,
    /// <summary>The <c>sha256</c> pin is not 64 hex characters.</summary>
    BadHash,
    /// <summary>Two adapters share an id.</summary>
    DuplicateId,

    /// <summary>The <c>platformBinary</c> block is malformed — no candidate sources, an empty target, or
    /// a path that is not a plain relative path under the adapters prefix.</summary>
    BadPlatformBinary,

    /// <summary>The <c>provenance</c> rung is absent or names a level this build does not know (MG-9).
    /// Its own code because "the maintainer forgot to say what origin assurance this CLI carries" is a
    /// different failure from a malformed field — and it must be a REFUSAL, not a default, or an
    /// unverified adapter would silently inherit whatever the weakest rung happens to be.</summary>
    MissingProvenance,
}

/// <summary>The typed refusal of an adapter manifest.</summary>
public sealed class AdapterManifestException : Exception
{
    public AdapterManifestError Error { get; }

    public AdapterManifestException(AdapterManifestError error, string message)
        : base(message) => Error = error;
}

/// <summary>A file written into the VM before the health probe (e.g. a non-interactive config so the
/// pinned CLI never blocks on a prompt).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfigShim(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content")] string Content);

/// <summary>
/// How a script-free install finishes placing a CLI whose npm package is only a LAUNCHER: the real
/// executable ships in a platform-specific subpackage, and the vendor's <c>postinstall</c> is what
/// normally hardlinks it over the placeholder at <see cref="Target"/>. Because every adapter installs
/// with <c>--ignore-scripts</c> (that postinstall is arbitrary upstream code running inside MainguardEnv
/// before any probe or sandbox applies), that step never runs and the placeholder — a stub that prints
/// "native binary not installed" and exits 1 — is what the health probe ends up executing.
///
/// <para>This block is Mainguard performing the same FILE OPERATION itself, from a reviewed manifest,
/// without executing anything the vendor shipped. <see cref="Sources"/> is an ordered candidate list
/// (a CPU-feature or libc variant per entry, exactly as the vendors' own postinstalls enumerate them);
/// each is placed in turn and validated by the adapter's real health probe, so which build is correct
/// for this machine is answered EMPIRICALLY rather than guessed from a hardcoded CPU check.</para>
///
/// <para>Every path is relative to <see cref="AdapterPaths.VmRoot"/> — the npm <c>--prefix</c> the
/// installCmd already writes into. Nothing here reaches the network: the subpackage was resolved and
/// downloaded by the very same <c>npm install</c>, as one of the package's exact-versioned
/// <c>optionalDependencies</c> (<c>--ignore-scripts</c> suppresses lifecycle hooks, never dependency
/// resolution). What this therefore does NOT establish is stated in <c>adapters.starter.json</c>: the
/// sha256 pin covers the launcher tarball, and the executable placed here comes from the unpinned
/// dependency resolution the manifest already documents as a residual gap.</para>
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlatformBinaryLink(
    [property: JsonPropertyName("sources")] IReadOnlyList<string> Sources,
    [property: JsonPropertyName("target")] string Target);

/// <summary>The command that proves the pinned CLI is installed and at the right version.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HealthProbe(
    [property: JsonPropertyName("command")] IReadOnlyList<string> Command,
    [property: JsonPropertyName("expectedVersionSubstring")] string ExpectedVersionSubstring);

/// <summary>One pinned agent CLI: what to install, at exactly which version, verified by which probe.
/// <para><paramref name="PayloadUrl"/> is the HTTPS URL of the pinned artifact (e.g. the exact npm
/// registry tarball) whose bytes must hash to <paramref name="Sha256"/>; <c>{payload}</c> in
/// <paramref name="InstallCmd"/> is replaced with the staged, hash-verified file's in-VM path.
/// <paramref name="Launch"/> is the argv the daemon execs INSIDE the agent sandbox to start this CLI
/// (the <c>agentKind</c>→CLI wiring); adapters land on the sandbox PATH via the read-only
/// <c>/opt/mainguard/adapters</c> mount.</para></summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdapterSpec(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("installCmd")] IReadOnlyList<string> InstallCmd,
    [property: JsonPropertyName("configShims")] IReadOnlyList<ConfigShim>? ConfigShims,
    [property: JsonPropertyName("healthProbe")] HealthProbe? HealthProbe,
    [property: JsonPropertyName("payloadUrl")] string? PayloadUrl = null,
    [property: JsonPropertyName("launch")] IReadOnlyList<string>? Launch = null,
    /// <summary>The environment variable this CLI reads its model API key from (e.g.
    /// <c>ANTHROPIC_API_KEY</c> for claude-code, <c>OPENAI_API_KEY</c> for codex). Null = the CLI
    /// authenticates interactively (login in its terminal) and no key is ever injected. The spawn
    /// path injects the caller's key under THIS name — a hardcoded <c>ANTHROPIC_API_KEY</c> for
    /// every kind was the audit-found #13 (codex/opencode never saw their keys).</summary>
    [property: JsonPropertyName("apiKeyEnvVar")] string? ApiKeyEnvVar = null,
    /// <summary>The egress hosts THIS CLI needs to function beyond the always-on model-API/registry
    /// defaults (e.g. claude-code's <c>platform.claude.com</c> auth/console + <c>statsig.anthropic.com</c>).
    /// Auto-permitted on the default-deny egress proxy when the CLI is installed (the spawn path unions
    /// these in as direct-route <c>AgentService</c> allowlist entries), so an installed CLI works out of
    /// the box without the user hand-editing the allowlist. Bare hostnames only — no scheme/path/port.
    /// A git host here is refused (A6): git-sourced installs go through the daemon read-only git proxy.</summary>
    [property: JsonPropertyName("egressHosts")] IReadOnlyList<string>? EgressHosts = null,
    /// <summary>The $HOME-relative files where THIS CLI keeps its interactive-login state (OAuth
    /// tokens, account/onboarding records — e.g. <c>.claude/.credentials.json</c>). The jail's home
    /// is a tmpfs wiped on every relaunch, so the daemon restores these at boot from the host OS
    /// keychain and the client harvests them back on stop — the login round-trip that stops every
    /// launch from demanding a fresh sign-in. Files only, relative, no <c>..</c>; null = this CLI
    /// has no persistable login state (API-key-only).</summary>
    [property: JsonPropertyName("credentialPaths")] IReadOnlyList<string>? CredentialPaths = null,
    /// <summary>The files where THIS CLI keeps its NON-credential configuration — above all the
    /// permission allowlist a user builds by approving commands. Both trees a CLI can write them to
    /// are wiped every spawn (the tmpfs <c>$HOME</c> and the per-agent worktree), so without this the
    /// user re-approves every command in every new agent. Restored into every trusted jail and
    /// harvested back per REPOSITORY (never globally): an approval given while working on repo A must
    /// not silently pre-approve the same command in repo B. Must not overlap
    /// <see cref="CredentialPaths"/> — settings go to an ordinary per-repo file, credentials only ever
    /// to the host OS keychain. Null = this CLI has no persistable settings.</summary>
    [property: JsonPropertyName("settingsPaths")] IReadOnlyList<AdapterSettingsPath>? SettingsPaths = null,
    /// <summary>The environment variable this CLI reads its API BASE URL from (e.g.
    /// <c>ANTHROPIC_BASE_URL</c> for claude-code, <c>OPENAI_BASE_URL</c> for codex). MG-4: pointing the
    /// CLI at the daemon's model gateway is what lets the jail hold only a Mainguard session token while
    /// the real provider key stays daemon-side and is injected at the network hop. Null = this CLI
    /// cannot be redirected, so it must talk to the provider directly and BYOK confinement does not
    /// apply to it.</summary>
    [property: JsonPropertyName("baseUrlEnvVar")] string? BaseUrlEnvVar = null,
    /// <summary>The provider host THIS CLI's model traffic goes to (e.g. <c>api.anthropic.com</c> for
    /// claude-code). Paired with <see cref="BaseUrlEnvVar"/>: the base-URL variable says the CLI *can* be
    /// redirected to the gateway, and this says where the gateway must then forward its traffic. Recorded
    /// as the agent's per-agent upstream binding at spawn, because once the CLI is pointed at the gateway
    /// the inbound request's Host header names the GATEWAY and can no longer identify the provider. Null =
    /// no upstream binding, so this CLI is never gateway-confined.</summary>
    [property: JsonPropertyName("modelHost")] string? ModelHost = null,
    /// <summary>MG-9: how much ORIGIN assurance this CLI's tarball is required to carry —
    /// <c>"npm-build-provenance"</c>, <c>"npm-registry-signature"</c>, or <c>"none"</c>. Mandatory on
    /// every npm-sourced adapter: <see cref="AdapterManifest.Parse"/> refuses a spec that omits it, so
    /// nobody can add a CLI without stating what can actually be verified about it. The declared rung is
    /// enforced fail-closed by <see cref="NpmProvenancePolicy"/>; the string is the wire form of
    /// <see cref="AdapterProvenanceLevel"/>.</summary>
    [property: JsonPropertyName("provenance")] string? Provenance = null,
    /// <summary>The <c>$HOME</c>-relative paths where THIS CLI keeps its CONVERSATION state — the
    /// transcripts of what the operator and the agent actually said (for claude-code,
    /// <c>.claude/projects</c>). Directories, not files: a CLI writes one transcript per session and
    /// names them itself, so there is no fixed file to declare.
    /// <para>Each declared path is bind-mounted into the jail from daemon-owned ext4, so the CLI writes
    /// its history straight onto disk that OUTLIVES the container. That is the whole design and it is
    /// not the same as <see cref="CredentialPaths"/>' harvest-on-stop round trip: the event that makes
    /// you need the conversation back is the jail dying WITHOUT a clean stop, and a harvest never runs
    /// then. See <see cref="Sandbox.ConversationStorePolicy"/>.</para>
    /// <para>Must not overlap <see cref="CredentialPaths"/> in either direction —
    /// <see cref="AdapterManifest.Parse"/> refuses the manifest and the spawn path refuses the marker.
    /// The store is daemon-owned disk that survives teardown; a credential may only ever live in the
    /// host OS keychain. Null/empty = this CLI gets no conversation persistence yet, which is an honest
    /// statement; a WRONG path would silently persist nothing.</para></summary>
    [property: JsonPropertyName("conversationPaths")] IReadOnlyList<string>? ConversationPaths = null,
    /// <summary>The extra argv THIS CLI needs to resume its previous conversation, appended to
    /// <see cref="Launch"/> (for claude-code, <c>--continue</c>).
    /// <para>Used on the ADOPT path only (a resumed stranded queue entry) and only when the agent's
    /// conversation store actually holds a transcript. Both conditions matter: an ordinary spawn is a
    /// new piece of work and must start clean, and a resume flag handed to a CLI with no prior session
    /// is a worse failure than no flag at all — a dead terminal at spawn, with nothing saying why.</para>
    /// <para>Null = this CLI declares no resume verb, so a resumed jail simply starts its CLI normally
    /// with the transcripts present. Absent is a statement, exactly as with
    /// <see cref="BaseUrlEnvVar"/>: an invented flag would make Mainguard believe a session was resumed
    /// while the CLI silently started fresh.</para></summary>
    [property: JsonPropertyName("resumeArgs")] IReadOnlyList<string>? ResumeArgs = null,
    /// <summary>For a CLI whose npm package is only a launcher: where the real executable actually
    /// lives after a script-free install, so <see cref="AdapterChannel.EnsureAsync"/> can place it over
    /// the vendor's placeholder itself instead of running the vendor's postinstall. Null = this CLI's
    /// package is self-contained (its <c>bin</c> entry is the real entry point) and nothing extra is
    /// needed. See <see cref="PlatformBinaryLink"/>.</summary>
    [property: JsonPropertyName("platformBinary")] PlatformBinaryLink? PlatformBinary = null)
{
    /// <summary>The parsed <see cref="Provenance"/> rung. Only ever reached after
    /// <see cref="AdapterManifest.Parse"/> validated it, so an unrecognised value here is a bug, not a
    /// user input — and it resolves to <see cref="AdapterProvenanceLevel.None"/>, the rung that claims
    /// nothing, rather than to anything that would read as verified.</summary>
    public AdapterProvenanceLevel ProvenanceLevel =>
        AdapterManifest.TryParseProvenance(Provenance, out var level) ? level : AdapterProvenanceLevel.None;
}

/// <summary>The <c>adapters.json</c> channel manifest: the full set of pinned agent CLIs.
/// <para><c>_comment</c> is the ONE tolerated free-form field (JSON has no comment syntax, and the
/// pinning rules a maintainer must follow have to live next to the pins). It is documentation only —
/// never read by any code path. The strict no-unknown-fields rule still holds everywhere else,
/// including inside each adapter spec.</para></summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdapterManifest(
    [property: JsonPropertyName("adapters")] IReadOnlyList<AdapterSpec> Adapters,
    [property: JsonPropertyName("_comment")] JsonElement? Comment = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>
    /// Parses and schema-validates an <c>adapters.json</c>. Rejects malformed JSON, unknown fields,
    /// missing required fields, a non-64-hex <c>sha256</c>, duplicate ids, and — critically — any
    /// version that is not pinned to a concrete release (<c>latest</c>, <c>@latest</c>, or a range is
    /// refused; <c>@latest</c> installs are a rejection trigger, so they cannot even parse).
    /// </summary>
    public static AdapterManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        AdapterManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AdapterManifest>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new AdapterManifestException(AdapterManifestError.Malformed, $"Manifest JSON invalid: {ex.Message}");
        }

        if (manifest?.Adapters is null)
            throw new AdapterManifestException(AdapterManifestError.MissingField, "Manifest has no 'adapters' array.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in manifest.Adapters)
        {
            if (string.IsNullOrWhiteSpace(a.Id))
                throw new AdapterManifestException(AdapterManifestError.MissingField, "An adapter is missing 'id'.");
            if (!seen.Add(a.Id))
                throw new AdapterManifestException(AdapterManifestError.DuplicateId, $"Duplicate adapter id '{a.Id}'.");
            if (string.IsNullOrWhiteSpace(a.DisplayName))
                throw new AdapterManifestException(AdapterManifestError.MissingField, $"Adapter '{a.Id}' is missing 'displayName'.");
            if (string.IsNullOrWhiteSpace(a.Version))
                throw new AdapterManifestException(AdapterManifestError.MissingField, $"Adapter '{a.Id}' is missing 'version'.");
            if (!IsPinnedVersion(a.Version))
                throw new AdapterManifestException(AdapterManifestError.UnpinnedVersion,
                    $"Adapter '{a.Id}' version '{a.Version}' is not pinned to a concrete release.");
            if (a.InstallCmd is null || a.InstallCmd.Count == 0)
                throw new AdapterManifestException(AdapterManifestError.MissingField, $"Adapter '{a.Id}' is missing 'installCmd'.");
            if (a.InstallCmd.Any(ContainsUnpinnedToken))
                throw new AdapterManifestException(AdapterManifestError.UnpinnedVersion,
                    $"Adapter '{a.Id}' install command uses an unpinned tag (e.g. @latest).");
            if (!IsSha256(a.Sha256))
                throw new AdapterManifestException(AdapterManifestError.BadHash, $"Adapter '{a.Id}' sha256 must be 64 hex chars.");
            if (a.HealthProbe is null || a.HealthProbe.Command is null || a.HealthProbe.Command.Count == 0
                || string.IsNullOrWhiteSpace(a.HealthProbe.ExpectedVersionSubstring))
                throw new AdapterManifestException(AdapterManifestError.MissingField, $"Adapter '{a.Id}' is missing a valid 'healthProbe'.");
            if (a.PayloadUrl is not null
                && !a.PayloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new AdapterManifestException(AdapterManifestError.Malformed,
                    $"Adapter '{a.Id}' payloadUrl must be HTTPS (a plaintext channel defeats the hash pin).");
            // MG-9: every adapter must STATE what can be verified about its origin. Absent is refused
            // rather than defaulted — a default would be a silent answer to the one question this field
            // exists to force someone to answer out loud, and the honest answer for a CLI with no
            // signing story ("none") is available and costs one reviewed line.
            if (string.IsNullOrWhiteSpace(a.Provenance))
                throw new AdapterManifestException(AdapterManifestError.MissingProvenance,
                    $"Adapter '{a.Id}' is missing 'provenance'. Declare one of "
                    + $"{string.Join(", ", ProvenanceNames.Keys.Select(k => $"'{k}'"))} — an adapter whose "
                    + "origin cannot be verified must say so explicitly, never inherit a default.");
            if (!TryParseProvenance(a.Provenance, out _))
                throw new AdapterManifestException(AdapterManifestError.MissingProvenance,
                    $"Adapter '{a.Id}' provenance '{a.Provenance}' is not a level this build knows "
                    + $"({string.Join(", ", ProvenanceNames.Keys.Select(k => $"'{k}'"))}). Refusing rather "
                    + "than guessing: an unknown rung must never resolve to a weaker check.");
            if (a.PlatformBinary is { } platform)
            {
                // Every path is joined onto AdapterPaths.VmRoot and handed straight to `ln`/`cp` in the
                // VM, so the same relative-path rule the credentialPaths gate enforces applies here: no
                // absolute path, no '..' escape, no backslash or control character. A manifest is
                // reviewed, but this is the one field that names files an install WRITES, so it is
                // validated rather than trusted.
                if (platform.Sources is null || platform.Sources.Count == 0)
                    throw new AdapterManifestException(AdapterManifestError.BadPlatformBinary,
                        $"Adapter '{a.Id}' platformBinary lists no 'sources' — there would be nothing to place.");
                foreach (var source in platform.Sources)
                {
                    if (!IsHomeRelativeFilePath(source))
                        throw new AdapterManifestException(AdapterManifestError.BadPlatformBinary,
                            $"Adapter '{a.Id}' platformBinary source '{source}' must be a plain relative path under the adapters prefix.");
                }

                if (!IsHomeRelativeFilePath(platform.Target))
                    throw new AdapterManifestException(AdapterManifestError.BadPlatformBinary,
                        $"Adapter '{a.Id}' platformBinary target '{platform.Target}' must be a plain relative path under the adapters prefix.");
            }

            if (a.Launch is not null && (a.Launch.Count == 0 || a.Launch.Any(string.IsNullOrWhiteSpace)))
                throw new AdapterManifestException(AdapterManifestError.MissingField,
                    $"Adapter '{a.Id}' has an empty 'launch' command.");
            if (a.ApiKeyEnvVar is not null && !IsEnvVarName(a.ApiKeyEnvVar))
                throw new AdapterManifestException(AdapterManifestError.Malformed,
                    $"Adapter '{a.Id}' apiKeyEnvVar '{a.ApiKeyEnvVar}' is not a valid environment variable name.");
            if (a.BaseUrlEnvVar is not null && !IsEnvVarName(a.BaseUrlEnvVar))
                throw new AdapterManifestException(AdapterManifestError.Malformed,
                    $"Adapter '{a.Id}' baseUrlEnvVar '{a.BaseUrlEnvVar}' is not a valid environment variable name.");
            if (a.CredentialPaths is not null)
            {
                foreach (var path in a.CredentialPaths)
                {
                    if (!IsHomeRelativeFilePath(path))
                        throw new AdapterManifestException(AdapterManifestError.Malformed,
                            $"Adapter '{a.Id}' credentialPaths entry '{path}' must be a $HOME-relative file path (no leading '/', '~', '..' segments, backslashes, or control characters).");
                }
            }

            if (a.SettingsPaths is not null)
            {
                // The credential list, as the SHAPE GATE already accepted it — the comparison below has
                // to be against paths that are really restorable, not against raw manifest text.
                var credentialPaths = new HashSet<string>(
                    (a.CredentialPaths ?? Array.Empty<string>()).Where(IsHomeRelativeFilePath),
                    StringComparer.Ordinal);
                var seenSettings = new HashSet<(string Root, string Path)>();
                foreach (var entry in a.SettingsPaths)
                {
                    if (entry is null || !AdapterSettingsPath.TryParseRoot(entry.Root, out _))
                        throw new AdapterManifestException(AdapterManifestError.Malformed,
                            $"Adapter '{a.Id}' settingsPaths entry has root '{entry?.Root}' — declare one of "
                            + $"{string.Join(", ", AdapterSettingsPath.RootSpellings.Select(r => $"'{r}'"))}. "
                            + "Refusing rather than defaulting: a guessed root would decide whether Mainguard "
                            + "writes into the jail's throwaway home or into the user's real checkout.");
                    if (!IsHomeRelativeFilePath(entry.Path))
                        throw new AdapterManifestException(AdapterManifestError.Malformed,
                            $"Adapter '{a.Id}' settingsPaths entry '{entry.Path}' must be a plain relative file "
                            + "path (no leading '/', '~', '..' segments, backslashes, or control characters).");
                    if (!seenSettings.Add((entry.Root, entry.Path)))
                        throw new AdapterManifestException(AdapterManifestError.Malformed,
                            $"Adapter '{a.Id}' declares settingsPaths entry '{entry.Root}:{entry.Path}' twice.");
                    // The boundary this field exists beside, enforced rather than described: a settings
                    // path is persisted to an ORDINARY per-repo JSON file, a credential path only ever to
                    // the host OS keychain. One path in both lists would quietly route a credential into
                    // the plaintext store, which is precisely the standing rule this must not break.
                    if (entry.ParsedRoot == AdapterSettingsRoot.Home && credentialPaths.Contains(entry.Path))
                        throw new AdapterManifestException(AdapterManifestError.Malformed,
                            $"Adapter '{a.Id}' lists '{entry.Path}' in BOTH credentialPaths and settingsPaths. "
                            + "Credentials are persisted only to the host OS keychain; settings go to an "
                            + "ordinary per-repo file. A path cannot be both without leaking the credential.");
                }
            }

            if (a.ConversationPaths is not null)
            {
                foreach (var path in a.ConversationPaths)
                {
                    if (!IsHomeRelativeFilePath(path))
                        throw new AdapterManifestException(AdapterManifestError.Malformed,
                            $"Adapter '{a.Id}' conversationPaths entry '{path}' must be a $HOME-relative path (no leading '/', '~', '..' segments, backslashes, or control characters).");
                }

                // THE INVARIANT THAT MAKES CONVERSATION PERSISTENCE SAFE TO SHIP, enforced rather than
                // documented. A conversation store is daemon-owned ext4 that deliberately OUTLIVES the
                // jail; a credential may only ever live in the host OS keychain (the owner's standing
                // rule). One declared path that CONTAINS the other therefore quietly persists a token to
                // plain disk and remounts it into every later jail for that agent id.
                //
                // Containment, not equality: the accident this exists to stop is a manifest that declares
                // '.claude' — where the transcripts live, and which also contains '.claude/.credentials.json'.
                // An equality-only check passes that case, which is the only case worth checking.
                //
                // Refused, not filtered: dropping the offending path and continuing would leave the
                // feature looking configured while persisting a subset nobody chose, and would leave the
                // wrong declaration in the manifest. Sandbox.ConversationStorePolicy owns the rule so the
                // spawn path (which reads an install MARKER, not this file) asks the same question of the
                // same implementation.
                try
                {
                    Sandbox.ConversationStorePolicy.AssertNoCredentialOverlap(
                        a.Id, a.ConversationPaths, a.CredentialPaths);
                }
                catch (Git.Exceptions.ConversationStoreOverlapException ex)
                {
                    throw new AdapterManifestException(AdapterManifestError.Malformed, ex.Message);
                }
            }

            if (a.ResumeArgs is not null && (a.ResumeArgs.Count == 0 || a.ResumeArgs.Any(string.IsNullOrWhiteSpace)))
                throw new AdapterManifestException(AdapterManifestError.MissingField,
                    $"Adapter '{a.Id}' has an empty 'resumeArgs' entry. Omit the field entirely to say this CLI "
                    + "declares no resume verb — absent is a statement, blank is a typo.");

            if (a.ResumeArgs is { Count: > 0 } && a.ConversationPaths is not { Count: > 0 })
                throw new AdapterManifestException(AdapterManifestError.Malformed,
                    $"Adapter '{a.Id}' declares 'resumeArgs' but no 'conversationPaths'. There would be nothing "
                    + "for the CLI to resume FROM — its transcripts would still die with the jail's tmpfs $HOME, "
                    + "so the flag would resume an empty history on every spawn.");

            if (a.EgressHosts is not null)
            {
                foreach (var host in a.EgressHosts)
                {
                    if (!IsBareHostname(host))
                        throw new AdapterManifestException(AdapterManifestError.Malformed,
                            $"Adapter '{a.Id}' egressHosts entry '{host}' must be a bare hostname (no scheme, path, port, or spaces).");
                    if (Sandbox.EgressAllowlistEntry.LooksLikeGitHost(host))
                        throw new AdapterManifestException(AdapterManifestError.Malformed,
                            $"Adapter '{a.Id}' egressHosts may not include a git host ('{host}') — A6: git access is via the daemon read-only git proxy, never the agent's own egress.");
                }
            }
        }

        return manifest;
    }

    /// <summary>The wire spellings of <see cref="AdapterProvenanceLevel"/>. Ordinal and exact — a
    /// case-insensitive or fuzzy match here would let a typo'd rung become a weaker one.</summary>
    private static readonly IReadOnlyDictionary<string, AdapterProvenanceLevel> ProvenanceNames =
        new Dictionary<string, AdapterProvenanceLevel>(StringComparer.Ordinal)
        {
            ["npm-build-provenance"] = AdapterProvenanceLevel.NpmBuildProvenance,
            ["npm-registry-signature"] = AdapterProvenanceLevel.NpmRegistrySignature,
            ["none"] = AdapterProvenanceLevel.None,
        };

    /// <summary>Maps a manifest <c>provenance</c> string to its rung. False for anything unrecognised —
    /// the caller refuses; it never falls back.</summary>
    public static bool TryParseProvenance(string? value, out AdapterProvenanceLevel level)
    {
        level = AdapterProvenanceLevel.None;
        return value is not null && ProvenanceNames.TryGetValue(value, out level);
    }

    /// <summary>A version is pinned iff it is concrete: has a digit, and carries no range/wildcard/tag.</summary>
    public static bool IsPinnedVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var v = version.Trim();
        if (v.Contains("latest", StringComparison.OrdinalIgnoreCase)) return false;
        if (v.Contains('*')) return false;
        // Range / wildcard operators disqualify a pin.
        if (v.StartsWith('^') || v.StartsWith('~') || v.StartsWith('>') || v.StartsWith('<') || v.StartsWith('='))
            return false;
        if (v.Contains("||", StringComparison.Ordinal) || v.Contains(" - ", StringComparison.Ordinal)) return false;
        // An `x`/`X` occupying a WHOLE dot-segment is npm's wildcard range (`1.x`, `1.x.x`, `1.2.X`): it
        // names "whatever the registry serves today", which is exactly the drift the sha256 pin exists to
        // stop — the resolved bytes would change under a manifest that never did. The old guard only
        // rejected an `x` when the string ALSO had no digit, so every real range form (`1.x`) passed as a
        // pin. Matching whole segments (not any `x` in the string) keeps concrete tags that merely contain
        // the letter — `1.0.0-hotfix`, `2.1.0-linux` — pinned.
        if (v.Split('.').Any(s => s.Equals("x", StringComparison.OrdinalIgnoreCase))) return false;
        return v.Any(char.IsDigit);
    }

    private static bool ContainsUnpinnedToken(string token) =>
        token.Contains("@latest", StringComparison.OrdinalIgnoreCase)
        || token.Equals("latest", StringComparison.OrdinalIgnoreCase)
        || token.EndsWith("@*", StringComparison.Ordinal)
        || token.EndsWith("@next", StringComparison.OrdinalIgnoreCase);

    /// <summary>A bare hostname the proxy/DNS can answer: labels of alnum/hyphen split by dots, an
    /// optional leading <c>*.</c> wildcard, and no scheme/path/port/whitespace.</summary>
    private static bool IsBareHostname(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim();
        if (h.Length > 253) return false;
        if (h.Contains("://", StringComparison.Ordinal) || h.Contains('/') || h.Contains(':') || h.Any(char.IsWhiteSpace))
            return false;
        var body = h.StartsWith("*.", StringComparison.Ordinal) ? h[2..] : h;
        return body.Length > 0 && body.Contains('.') && body.All(c => char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-');
    }

    /// <summary>A credentialPaths entry the sandbox can safely resolve under the agent's $HOME:
    /// relative (no leading <c>/</c> or <c>~</c>), no <c>..</c> segment (never escapes the home),
    /// forward slashes only, and no control characters or whitespace-only segments. This is the
    /// SINGLE gate for every path that later becomes <c>/home/agent/&lt;path&gt;</c> in an exec —
    /// the spawn/harvest sides trust it and never re-derive their own rules.</summary>
    public static bool IsHomeRelativeFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Trim();
        if (p != path) return false; // no leading/trailing whitespace hiding in the manifest
        if (p.StartsWith('/') || p.StartsWith('~')) return false;
        if (p.Contains('\\') || p.Any(char.IsControl)) return false;
        var segments = p.Split('/');
        return segments.All(s => s.Length > 0 && s != "." && s != "..");
    }

    /// <summary><c>[A-Za-z_][A-Za-z0-9_]*</c> — the portable env-var-name shape.</summary>
    private static bool IsEnvVarName(string name) =>
        name.Length > 0
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static bool IsSha256(string? hash) =>
        !string.IsNullOrEmpty(hash) && hash.Length == 64
        && hash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
}
