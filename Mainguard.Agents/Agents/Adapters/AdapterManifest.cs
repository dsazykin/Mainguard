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

    /// <summary>The pre-approval pair (<c>preApprovedCommandArg</c> + <c>preApprovedCommandFormat</c>) is
    /// half-declared, or the format carries no <c>{command}</c> placeholder. Its own code because this
    /// pair GRANTS EXECUTION inside a sandbox. A half-declared pair must not degrade to "grant nothing"
    /// (the jail's only tool then stalls on an approval prompt no human is watching), and a
    /// placeholder-free format must not degrade to a literal (which would emit a grant naming something
    /// other than the shim — possibly something much broader).</summary>
    BadPreApproval,

    /// <summary>The <c>initialPromptStyle</c> names a delivery this build does not know. Its own code
    /// because this field decides whether a jailed worker is ever asked to START: an unrecognised value
    /// degrading to "no first turn" restores the exact deadlock the field exists to close — a CLI idling
    /// at an empty input box, in a jail whose terminal is input-locked, forever. A refusal at parse is
    /// loud; a degraded reading is a feature that looks wired and is not.</summary>
    BadInitialPrompt,

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

/// <summary>
/// How a CLI accepts the <b>first user turn</b> the daemon starts it with — the turn without which a
/// jailed worker sits at an empty input box and the phase-2 plan loop never begins.
///
/// <para>Vendor knowledge, declared per adapter exactly like <c>systemPromptArg</c> and
/// <c>preApprovedCommandArg</c>, because only the CLI's own author knows how it takes one.</para>
/// </summary>
public enum AdapterInitialPromptStyle
{
    /// <summary>This CLI is not started with a first turn (the default, and every adapter that declares
    /// nothing). Such an agent launches byte-identically to before.</summary>
    None,

    /// <summary>
    /// The turn is a bare positional argument placed <b>first</b>, immediately after the CLI's own launch
    /// argv and before every flag the daemon appends — <c>claude "&lt;turn&gt;" --append-system-prompt …</c>.
    ///
    /// <para><b>"First" is the load-bearing half of the name, measured rather than assumed.</b> Against a
    /// real claude-code 2.1.250, the same turn appended LAST — the position every other field on this
    /// launch line uses — never reached the model at all: <c>--allowedTools</c> is variadic
    /// (<c>&lt;tools...&gt;</c>), so it swallows every following positional, and the CLI idled at an empty
    /// input box for the full 90-second probe exactly as it does with no turn. Placed first, the same
    /// text ran the shim on the first action. A style that says only "positional" would therefore be a
    /// declaration that is true and still does not work.</para>
    /// </summary>
    FirstPositional,
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
    /// <summary>
    /// The file THIS CLI reads unprompted from its working directory (<c>CLAUDE.md</c> for claude-code).
    /// The daemon writes the role's operating instructions there for agents whose working directory is a
    /// real host path — i.e. workers, whose <c>/workspace</c> is the bind-mounted worktree.
    ///
    /// <para>Without this the instructions exist in the jail and are never opened, which is not
    /// hypothetical: they are also staged at <c>/opt/mainguard/ipc/MAINGUARD.md</c>, a path no CLI reads
    /// on its own, so that half of the delivery is inert until this names somewhere the CLI actually
    /// looks. Null = this CLI reads no such file, and only <see cref="SystemPromptArg"/> can reach it.</para>
    /// </summary>
    [property: JsonPropertyName("instructionsFile")] string? InstructionsFile = null,
    /// <summary>
    /// The launch flag THIS CLI accepts instruction text on (<c>--append-system-prompt</c> for
    /// claude-code), appended to the launch argv with the rendered instructions as its value.
    ///
    /// <para>Load-bearing for a COORDINATOR, and not interchangeable with
    /// <see cref="InstructionsFile"/> there: the role lock gives a coordinator an empty tmpfs at
    /// <c>/workspace</c> with no host side to write to, so a file cannot be pre-placed and the flag is
    /// the only delivery that reaches it. Null = this CLI takes no such flag.</para>
    /// </summary>
    [property: JsonPropertyName("systemPromptArg")] string? SystemPromptArg = null,
    /// <summary>
    /// The launch flag THIS CLI takes a PRE-APPROVED COMMAND list on (<c>--allowedTools</c> for
    /// claude-code). Paired with <see cref="PreApprovedCommandFormat"/>; declaring one without the other
    /// is refused (<see cref="AdapterManifestError.BadPreApproval"/>).
    ///
    /// <para><b>Why this had to exist.</b> A jailed CLI that asks a human before running a command is
    /// correct behaviour everywhere except a jail with no human in it. The coordinator's ENTIRE surface
    /// is one command — its role's shim — and a real claude-code coordinator, following its operating
    /// instructions exactly, ran it as its first action and got "This command requires approval". The
    /// headline feature stalled on its first action, permanently, with nobody watching.</para>
    ///
    /// <para><b>What it may be used for, and nothing else.</b> The daemon renders exactly ONE grant from
    /// this pair: the absolute in-jail path of the shim THIS agent's role was given
    /// (<c>AgentIpcPaths.SandboxShimPath</c>), and only for a jail that actually has an IPC dir. It is
    /// not a hook for adapter-declared allowlists — nothing in a manifest names the granted command, so a
    /// manifest edit cannot widen the grant, only change how a grant is spelled for that CLI.</para>
    /// </summary>
    [property: JsonPropertyName("preApprovedCommandArg")] string? PreApprovedCommandArg = null,
    /// <summary>
    /// How THIS CLI spells "this one command needs no approval" — a template containing the literal
    /// <c>{command}</c>, which the daemon replaces with the shim's absolute in-jail path.
    /// <c>Bash({command}:*)</c> for claude-code, whose permission rules are
    /// <c>&lt;Tool&gt;(&lt;pattern&gt;)</c> and whose <c>cmd:*</c> form is a prefix match on that command.
    ///
    /// <para>A template rather than a hardcoded string because the tool name and the pattern syntax are
    /// the vendor's, exactly like <see cref="SystemPromptArg"/>. The placeholder is MANDATORY: a format
    /// without it would produce a fixed grant that does not name the shim, which is the one way this
    /// field could widen a jail's capability rather than narrow it.</para>
    /// </summary>
    [property: JsonPropertyName("preApprovedCommandFormat")] string? PreApprovedCommandFormat = null,
    /// <summary>
    /// How THIS CLI takes the daemon's <b>first user turn</b> — the wire spelling of
    /// <see cref="AdapterInitialPromptStyle"/> (<c>"first-positional"</c> for claude-code). Absent or
    /// <c>"none"</c> = this CLI is started with no first turn, exactly as before.
    ///
    /// <para><b>Why this exists.</b> A vendor CLI does not act on a system prompt. Started with the
    /// operating instructions and nothing else, claude-code renders its banner and waits at an empty
    /// input box — so a jailed worker never ran its shim, never presented a plan, and could never be sent
    /// a first turn either, because <c>send_worker_prompt</c> is refused until a plan is approved. The
    /// loop could not start once. This field is the channel that starts it.</para>
    ///
    /// <para><b>What it may carry, and nothing else.</b> The daemon renders the turn itself
    /// (<see cref="Ipc.AgentKickoffPrompt"/>) from the agent's ROLE and its shim path — nothing in a
    /// manifest names or influences the text, so a manifest edit can change how a turn is delivered but
    /// never what it says. The task the worker was spawned for is not in scope at the point the text is
    /// built and stays where phase 2 put it: behind the plan gate.</para>
    /// </summary>
    [property: JsonPropertyName("initialPromptStyle")] string? InitialPromptStyle = null,
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

    /// <summary>The parsed <see cref="InitialPromptStyle"/>. Only ever reached after
    /// <see cref="AdapterManifest.Parse"/> refused every unrecognised spelling, so a miss here means the
    /// adapter declared nothing — which is <see cref="AdapterInitialPromptStyle.None"/>, the reading that
    /// changes no launch line.</summary>
    public AdapterInitialPromptStyle InitialPromptDelivery =>
        AdapterManifest.TryParseInitialPromptStyle(InitialPromptStyle, out var style)
            ? style
            : AdapterInitialPromptStyle.None;
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

            // The pre-approval pair. Both-or-neither, and the format must carry the placeholder —
            // enforced rather than tolerated because every degraded reading of a half-declared pair is
            // worse than a refusal. Missing FORMAT would append a flag with no value (or a value the CLI
            // reads as its next positional); missing ARG would compute a grant and drop it, so the jail's
            // only tool goes back to stalling on an approval prompt; a placeholder-free FORMAT would emit
            // a constant grant that does not name this role's shim. This is the one manifest field that
            // grants execution inside a sandbox, so it fails closed and loudly.
            var hasPreApprovalArg = !string.IsNullOrWhiteSpace(a.PreApprovedCommandArg);
            var hasPreApprovalFormat = !string.IsNullOrWhiteSpace(a.PreApprovedCommandFormat);
            if (hasPreApprovalArg != hasPreApprovalFormat)
                throw new AdapterManifestException(AdapterManifestError.BadPreApproval,
                    $"Adapter '{a.Id}' declares only "
                    + (hasPreApprovalArg ? "'preApprovedCommandArg'" : "'preApprovedCommandFormat'")
                    + " — the two are a pair: the flag alone has no value to carry, and the format alone "
                    + "has no flag to travel on. Declare both or neither.");
            if (hasPreApprovalFormat
                && !a.PreApprovedCommandFormat!.Contains(PreApprovedCommandPlaceholder, StringComparison.Ordinal))
                throw new AdapterManifestException(AdapterManifestError.BadPreApproval,
                    $"Adapter '{a.Id}' preApprovedCommandFormat '{a.PreApprovedCommandFormat}' contains no "
                    + $"'{PreApprovedCommandPlaceholder}' placeholder. The daemon substitutes the shim's "
                    + "own in-jail path there; without it the CLI would be handed a fixed grant naming "
                    + "something other than this agent's shim.");

            // The first-turn delivery. Refused rather than defaulted for the same reason the pair above
            // is: a declaration this build cannot read must not quietly become "no first turn", because
            // that is the deadlock — a worker idling at an empty input box in a jail whose terminal is
            // input-locked, with the coordinator's only steering tool refused until it presents the plan
            // it will never present. An unreadable value is a manifest bug and says so at parse.
            if (a.InitialPromptStyle is not null
                && !TryParseInitialPromptStyle(a.InitialPromptStyle, out _))
                throw new AdapterManifestException(AdapterManifestError.BadInitialPrompt,
                    $"Adapter '{a.Id}' initialPromptStyle '{a.InitialPromptStyle}' is not a delivery this "
                    + "build knows. Use 'first-positional' (the turn is a bare positional argument placed "
                    + "before every flag the daemon appends) or 'none'.");

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

            // The instructions file is a path the daemon WRITES, at the root of the user's own checkout,
            // and whose name is also what gets excluded from the agent's commits. Both halves need it to
            // be a plain relative path: `Path.Combine(worktree, "../../x")` writes outside the worktree,
            // and a name git cannot match as a pattern is an exclusion that silently covers nothing.
            // Refused rather than sanitized — a quietly rewritten name would be delivered to a path the
            // CLI does not read, which is the exact inert delivery this field was added to fix.
            if (a.InstructionsFile is not null && !IsHomeRelativeFilePath(a.InstructionsFile))
                throw new AdapterManifestException(AdapterManifestError.Malformed,
                    $"Adapter '{a.Id}' instructionsFile '{a.InstructionsFile}' must be a plain relative file "
                    + "path inside the worktree (no leading '/', '~', '..' segments, backslashes, or control "
                    + "characters) — the daemon writes it at the worktree root and excludes it by that name.");

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

    /// <summary>The literal an adapter's <see cref="AdapterSpec.PreApprovedCommandFormat"/> must contain,
    /// and which the daemon replaces with the shim's absolute in-jail path. One constant, shared by the
    /// parser that requires it and the launcher that substitutes it, so the two cannot disagree about
    /// what a template looks like.</summary>
    public const string PreApprovedCommandPlaceholder = "{command}";

    /// <summary>
    /// Renders the ONE pre-approval grant for <paramref name="command"/> in <paramref name="format"/>'s
    /// spelling — or null when the adapter declares no pre-approval channel, or when the caller has no
    /// command to grant.
    ///
    /// <para>The single substitution point, used by the daemon and asserted by its tests. Null rather
    /// than a bare format string when anything is missing: "no grant" is a working agent that asks a
    /// human, while a mis-rendered grant is a permission rule whose contents nobody chose.</para>
    /// </summary>
    public static string? RenderPreApproval(string? format, string? command) =>
        string.IsNullOrWhiteSpace(format)
        || string.IsNullOrWhiteSpace(command)
        || !format.Contains(PreApprovedCommandPlaceholder, StringComparison.Ordinal)
            ? null
            : format.Replace(PreApprovedCommandPlaceholder, command, StringComparison.Ordinal);

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

    /// <summary>The wire spellings of <see cref="AdapterInitialPromptStyle"/>. Ordinal and exact, for the
    /// same reason as the provenance rungs: a fuzzy match would let a typo become a different
    /// behaviour.</summary>
    private static readonly IReadOnlyDictionary<string, AdapterInitialPromptStyle> InitialPromptStyleNames =
        new Dictionary<string, AdapterInitialPromptStyle>(StringComparer.Ordinal)
        {
            ["first-positional"] = AdapterInitialPromptStyle.FirstPositional,
            ["none"] = AdapterInitialPromptStyle.None,
        };

    /// <summary>Maps a manifest <c>initialPromptStyle</c> string to its delivery. False for anything
    /// unrecognised — including null, which the caller reads as "not declared" rather than as a
    /// refusal.</summary>
    public static bool TryParseInitialPromptStyle(string? value, out AdapterInitialPromptStyle style)
    {
        style = AdapterInitialPromptStyle.None;
        return value is not null && InitialPromptStyleNames.TryGetValue(value, out style);
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
