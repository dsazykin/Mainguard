using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mainguard.Agents.Agents.Toolchains;

/// <summary>Why a manifest was refused. Typed so a bad edit fails loudly at parse rather than producing
/// a half-usable channel.</summary>
public sealed class ToolchainManifestException : Exception
{
    public ToolchainManifestException(string message) : base(message)
    {
    }
}

/// <summary>The command that proves an installed toolchain actually RUNS, and the version it must
/// report.</summary>
/// <param name="Command">argv, run in the VM after install and expanded through
/// <see cref="ToolchainEntry.Expand"/>. Never a shell string — there is no shell to quote for.</param>
/// <param name="ExpectedVersionSubstring">A substring the probe's stdout must contain. Exit code alone
/// is not enough: a launcher stub can exit 0 and a wrong version can exit 0, and both have shipped.</param>
public sealed record ToolchainProbe(ImmutableArray<string> Command, string ExpectedVersionSubstring);

/// <summary>
/// One curated toolchain a HUMAN may choose to install. Every field is product source, shipped in
/// <c>toolchains.starter.json</c> and reviewed here — a repository can only ever name <see cref="Id"/>.
///
/// <para>That asymmetry is unchanged from <see cref="Sandbox.ToolchainCatalog"/> and is the whole
/// security design: making the SET of installed toolchains a user decision does not make the CONTENTS of
/// a toolchain a repository decision. A <c>.mainguard/toolchain</c> that could name a package, a URL or a
/// command would be an install-time code-execution surface in a file an agent can write to its own
/// branch, and it still is not one.</para>
/// </summary>
/// <param name="Id">The stable id a repo declares and the directory name it installs into.</param>
/// <param name="DisplayName">Human copy for the settings surface.</param>
/// <param name="Summary">One line saying what this toolchain gives a jail.</param>
/// <param name="Version">The pinned version. Carried into the probe, so a payload that is not this
/// version fails the install rather than silently becoming what runs.</param>
/// <param name="PayloadUrl">The version-addressed HTTPS URL of the payload tarball.</param>
/// <param name="Sha256">The payload's SHA-256, as published by upstream. Verified in the VM before a
/// single byte is extracted.</param>
/// <param name="StripComponents">Leading path components the tarball carries above the install root
/// (upstream tarballs usually nest everything under one directory).</param>
/// <param name="PathEntries">Directories, relative to the install root, prepended to a jail's PATH.</param>
/// <param name="Environment">Extra environment a jail needs. Never secrets: this is world-readable
/// inside the jail and <see cref="Sandbox.ContainerSpecBuilder"/> re-asserts G-13 over it anyway.</param>
/// <param name="Probe">What proves the toolchain runs.</param>
/// <param name="InstallEgressHosts">The hosts the INSTALL reaches, for the record. These are reached by
/// the daemon on the VM's own network, at a moment a human chose — they are NOT added to any jail's
/// egress allowlist and no jail can reach them.</param>
public sealed record ToolchainEntry(
    string Id,
    string DisplayName,
    string Summary,
    string Version,
    string PayloadUrl,
    string Sha256,
    int StripComponents,
    ImmutableArray<string> PathEntries,
    ImmutableArray<KeyValuePair<string, string>> Environment,
    ToolchainProbe Probe,
    ImmutableArray<string> InstallEgressHosts)
{
    /// <summary>The token in a path/env/probe value that expands to this toolchain's install root. It
    /// exists because the SAME entry is used against two different roots — the VM path when the install
    /// is probed, the in-jail mount path when a container is built — and hard-coding either would make
    /// one of them wrong.</summary>
    public const string RootToken = "{toolchain}";

    /// <summary>The token that expands to the per-agent package cache mount. This is where a package
    /// manager's writes go, because the toolchain itself is read-only in the jail.</summary>
    public const string CacheToken = "{cache}";

    /// <summary>Expands the two tokens in <paramref name="value"/>.</summary>
    public static string Expand(string value, string root, string cacheMount) =>
        value.Replace(RootToken, root, StringComparison.Ordinal)
             .Replace(CacheToken, cacheMount, StringComparison.Ordinal);

    /// <summary>This entry's probe argv, expanded against <paramref name="root"/>.</summary>
    public IReadOnlyList<string> ProbeCommand(string root, string cacheMount) =>
        Probe.Command.Select(a => Expand(a, root, cacheMount)).ToArray();

    /// <summary>The PATH entries this toolchain contributes, expanded against <paramref name="root"/>.</summary>
    public IEnumerable<string> ResolvedPathEntries(string root, string cacheMount) =>
        PathEntries.Select(p => Expand(p, root, cacheMount));

    /// <summary>The <c>NAME=value</c> environment this toolchain contributes, expanded against
    /// <paramref name="root"/>.</summary>
    public IEnumerable<string> ResolvedEnvironment(string root, string cacheMount) =>
        Environment.Select(kv => $"{kv.Key}={Expand(kv.Value, root, cacheMount)}");
}

/// <summary>
/// The curated set of toolchains a human may install, parsed from the shipped
/// <c>toolchains.starter.json</c>.
///
/// <para><b>Why a manifest rather than more C# constants.</b> The closed catalog stays closed — this is
/// still a fixed table a repository selects from by id. What changes is that adding an entry is a data
/// edit in one reviewed file rather than a code change spread across a recipe, a PATH list and an
/// environment block, which is what makes "Node and Go are a manifest change, not a code change" true
/// rather than aspirational.</para>
/// </summary>
public sealed class ToolchainManifest
{
    /// <summary>The entries, in manifest order (which is the order the settings surface shows).</summary>
    public ImmutableArray<ToolchainEntry> Entries { get; }

    private ToolchainManifest(ImmutableArray<ToolchainEntry> entries) => Entries = entries;

    /// <summary>The entry with <paramref name="id"/>, or null when it is not curated.</summary>
    public ToolchainEntry? TryGet(string? id) =>
        id is null ? null : Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    /// <summary>The curated ids, sorted — what an unknown-id failure lists for the human.</summary>
    public IReadOnlyList<string> KnownIds =>
        Entries.Select(e => e.Id).OrderBy(id => id, StringComparer.Ordinal).ToImmutableArray();

    /// <summary>The shipped manifest, parsed and validated. Cached: it is embedded, immutable and read
    /// on every settings open and every spawn.</summary>
    public static ToolchainManifest Shipped => _shipped.Value;

    private static readonly Lazy<ToolchainManifest> _shipped = new(() => Parse(ShippedJson()));

    /// <summary>The embedded manifest JSON.</summary>
    public static string ShippedJson()
    {
        var assembly = typeof(ToolchainManifest).Assembly;
        const string resource = "Mainguard.Agents.Agents.Toolchains.toolchains.starter.json";
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new ToolchainManifestException(
                $"Embedded resource '{resource}' is missing from Mainguard.Agents.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Parses and VALIDATES a manifest. Every rule below exists so that a bad edit fails in CI (the
    /// shipped manifest is parsed by a test) rather than at a user's install: an un-pinned URL, a
    /// short hash, a probe with no expected version, or an id that could be read as a path are all
    /// refused here.
    /// </summary>
    public static ToolchainManifest Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ToolchainManifestException($"The toolchain manifest is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("toolchains", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                throw new ToolchainManifestException("The toolchain manifest has no 'toolchains' array.");
            }

            var entries = list.EnumerateArray().Select(ParseEntry).ToImmutableArray();

            var duplicate = entries.GroupBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
            {
                throw new ToolchainManifestException(
                    $"The toolchain manifest declares '{duplicate.Key}' more than once; an id is a lookup key.");
            }

            return new ToolchainManifest(entries);
        }
    }

    private static ToolchainEntry ParseEntry(JsonElement e)
    {
        var id = RequireString(e, "id");
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, Sandbox.ToolchainCatalog.IdPattern))
        {
            // The same narrow grammar the declaration parser enforces: an id is a lookup key AND a
            // directory name, so anything that could be read as a path, a flag or a shell fragment is
            // refused here rather than relied upon to miss a table later.
            throw new ToolchainManifestException(
                $"Toolchain id '{id}' does not match the id grammar '{Sandbox.ToolchainCatalog.IdPattern}'.");
        }

        var payloadUrl = RequireString(e, "payloadUrl");
        if (!Uri.TryCreate(payloadUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            // A plaintext payload URL would let a MITM swap the bytes before the hash pin could be
            // written down — the pin protects against a changed upstream, not against an open channel.
            throw new ToolchainManifestException(
                $"Toolchain '{id}' has a non-HTTPS payloadUrl; toolchain payloads are fetched over HTTPS only.");
        }

        var sha = RequireString(e, "sha256").ToLowerInvariant();
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit))
        {
            throw new ToolchainManifestException(
                $"Toolchain '{id}' has a sha256 that is not 64 hex characters; the payload pin must be a full SHA-256.");
        }

        var probeElement = e.TryGetProperty("probe", out var p) && p.ValueKind == JsonValueKind.Object
            ? p
            : throw new ToolchainManifestException($"Toolchain '{id}' has no 'probe' object.");

        var probeCommand = RequireStringArray(probeElement, "command", id);
        if (probeCommand.Length == 0)
        {
            // A toolchain with no probe is a toolchain whose absence — or whose broken install — cannot
            // be detected, which is the exact failure this channel exists to make impossible.
            throw new ToolchainManifestException($"Toolchain '{id}' has an empty probe command.");
        }

        var expected = RequireString(probeElement, "expectedVersionSubstring");
        if (expected.Length == 0)
        {
            throw new ToolchainManifestException(
                $"Toolchain '{id}' has an empty probe expectedVersionSubstring; an exit code alone does not "
                + "prove which version landed.");
        }

        var environment = ImmutableArray<KeyValuePair<string, string>>.Empty;
        if (e.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.Object)
        {
            environment = env.EnumerateObject()
                .Select(prop => new KeyValuePair<string, string>(prop.Name, prop.Value.GetString() ?? string.Empty))
                .ToImmutableArray();
        }

        var strip = e.TryGetProperty("stripComponents", out var s) && s.ValueKind == JsonValueKind.Number
            ? s.GetInt32()
            : 0;
        if (strip is < 0 or > 8)
        {
            throw new ToolchainManifestException(
                $"Toolchain '{id}' has an out-of-range stripComponents ({strip.ToString(CultureInfo.InvariantCulture)}).");
        }

        return new ToolchainEntry(
            Id: id,
            DisplayName: RequireString(e, "displayName"),
            Summary: RequireString(e, "summary"),
            Version: RequireString(e, "version"),
            PayloadUrl: payloadUrl,
            Sha256: sha,
            StripComponents: strip,
            PathEntries: RequireStringArray(e, "pathEntries", id).ToImmutableArray(),
            Environment: environment,
            Probe: new ToolchainProbe(probeCommand.ToImmutableArray(), expected),
            InstallEgressHosts: (e.TryGetProperty("installEgressHosts", out _)
                ? RequireStringArray(e, "installEgressHosts", id)
                : Array.Empty<string>()).ToImmutableArray());
    }

    private static string RequireString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new ToolchainManifestException($"A toolchain entry is missing the string field '{name}'.");

    private static string[] RequireStringArray(JsonElement e, string name, string id) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray()
            : throw new ToolchainManifestException($"Toolchain '{id}' is missing the array field '{name}'.");
}
