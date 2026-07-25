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
    /// <summary>The environment variable this CLI reads its API BASE URL from (e.g.
    /// <c>ANTHROPIC_BASE_URL</c> for claude-code, <c>OPENAI_BASE_URL</c> for codex). MG-4: pointing the
    /// CLI at the daemon's model gateway is what lets the jail hold only a Mainguard session token while
    /// the real provider key stays daemon-side and is injected at the network hop. Null = this CLI
    /// cannot be redirected, so it must talk to the provider directly and BYOK confinement does not
    /// apply to it.</summary>
    [property: JsonPropertyName("baseUrlEnvVar")] string? BaseUrlEnvVar = null);

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
