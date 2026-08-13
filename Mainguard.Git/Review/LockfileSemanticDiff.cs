using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mainguard.Git.Review;

/// <summary>The dependency-manifest / lockfile formats the semantic diff understands (P2-11 §3.6).</summary>
public enum LockfileKind
{
    NpmPackageLock,
    PnpmLock,
    CsprojPackageReference,
    PoetryLock,
}

/// <summary>How a dependency changed between the two manifest versions.</summary>
public enum DependencyDeltaKind
{
    Added,
    Updated,
    Removed,
}

/// <summary>
/// One semantic row of a lockfile change: what dependency moved and the review-relevant facts about it
/// (a major version jump, whether the new version declares install scripts — arbitrary code at install
/// time — a registry/maintainer change where the format carries it, and any offline OSV CVE hits). These
/// rows render instead of a 9,000-line raw lockfile hunk; a script-bearing or CVE-hit row feeds the
/// flagged gate.
/// </summary>
/// <param name="AdvisoriesChecked">
/// Whether this row's <see cref="CveIds"/> is an <b>answer</b> rather than a silence.
///
/// <para>False means the offline <see cref="OsvSnapshot"/> was missing, unreadable or older than
/// <see cref="OsvSnapshot.MaxAge"/>, so an empty <see cref="CveIds"/> says nothing about this dependency.
/// It is carried as its own field, in the shape of <c>KillReport.RttMeasured</c> and
/// <c>ToolchainStatus.CouldNotCheck</c>, because "we checked and it is clean" and "we could not check" are
/// the two facts a reviewer's decision turns on, and an empty list renders them identically. A removed
/// dependency is <b>always</b> checked-true: nothing is being introduced, so there is nothing to be unsure
/// about.</para>
///
/// <para>Note that a false here does not empty <see cref="CveIds"/>: a stale snapshot's hits are still
/// hits. It widens the list to "at least these", never narrows it.</para>
/// </param>
public sealed record DependencyDelta(
    string Name,
    string? OldVersion,
    string? NewVersion,
    DependencyDeltaKind Kind,
    bool MajorJump,
    bool InstallScripts,
    bool RegistryOrMaintainerChange,
    IReadOnlyList<string> CveIds,
    bool AdvisoriesChecked = true)
{
    /// <summary>True when this row should feed the flagged-changes gate (install scripts or a known CVE).</summary>
    public bool FeedsFlaggedGate => InstallScripts || CveIds.Count > 0;

    /// <summary>True when this row introduces bytes whose advisory status was never established — the row
    /// a reviewer has to be told about precisely because there is nothing to report on it.</summary>
    public bool AdvisoryStatusUnknown => !AdvisoriesChecked && Kind != DependencyDeltaKind.Removed;
}

/// <summary>
/// Pure semantic lockfile diff (P2-11 extension a). No repo, no IO, <b>no network</b> — CVE hits come
/// from the shipped offline <see cref="OsvSnapshot"/>. <see cref="Parse"/> extracts per-dependency delta
/// rows for each supported <see cref="LockfileKind"/>; parsing is tolerant of format variants and never
/// throws on malformed input (it degrades to whatever it could read).
/// </summary>
public static class LockfileSemanticDiff
{
    private sealed record Entry(string Version, bool InstallScripts, string? Registry);

    /// <param name="oldText">The base side's full manifest text (empty when the file is being added).</param>
    /// <param name="newText">The branch side's full manifest text (empty when the file is being removed).</param>
    /// <param name="kind">Which manifest format to parse.</param>
    /// <param name="osv">The offline advisory snapshot; the shipped <see cref="OsvSnapshot.Default"/> when null.</param>
    /// <param name="asOf">
    /// The instant the snapshot's age is judged against (defaults to now). Only the CVE column depends on
    /// it: a snapshot past <see cref="OsvSnapshot.MaxAge"/> stamps every introduced row
    /// <see cref="DependencyDelta.AdvisoriesChecked"/> = false rather than reporting it clean.
    /// </param>
    public static IReadOnlyList<DependencyDelta> Parse(
        string oldText, string newText, LockfileKind kind, OsvSnapshot? osv = null, DateTimeOffset? asOf = null)
    {
        var snapshot = osv ?? OsvSnapshot.Default;
        var checkedAdvisories = snapshot.CanAnswerAt(asOf ?? DateTimeOffset.UtcNow, out _);
        var oldMap = ParseManifest(oldText ?? string.Empty, kind);
        var newMap = ParseManifest(newText ?? string.Empty, kind);

        var names = new SortedSet<string>(StringComparer.Ordinal);
        names.UnionWith(oldMap.Keys);
        names.UnionWith(newMap.Keys);

        var rows = new List<DependencyDelta>();
        foreach (var name in names)
        {
            var hasOld = oldMap.TryGetValue(name, out var oldEntry);
            var hasNew = newMap.TryGetValue(name, out var newEntry);

            if (hasOld && hasNew)
            {
                if (string.Equals(oldEntry!.Version, newEntry!.Version, StringComparison.Ordinal)
                    && oldEntry.InstallScripts == newEntry.InstallScripts
                    && string.Equals(oldEntry.Registry, newEntry.Registry, StringComparison.Ordinal))
                {
                    continue; // unchanged
                }

                var registryChanged = !string.Equals(oldEntry.Registry, newEntry.Registry, StringComparison.Ordinal)
                    && oldEntry.Registry is not null && newEntry.Registry is not null;

                rows.Add(new DependencyDelta(
                    name,
                    oldEntry.Version,
                    newEntry.Version,
                    DependencyDeltaKind.Updated,
                    IsMajorJump(oldEntry.Version, newEntry.Version),
                    newEntry.InstallScripts,
                    registryChanged,
                    snapshot.Lookup(name, newEntry.Version),
                    checkedAdvisories));
            }
            else if (hasNew)
            {
                rows.Add(new DependencyDelta(
                    name,
                    null,
                    newEntry!.Version,
                    DependencyDeltaKind.Added,
                    MajorJump: false,
                    newEntry.InstallScripts,
                    RegistryOrMaintainerChange: false,
                    snapshot.Lookup(name, newEntry.Version),
                    checkedAdvisories));
            }
            else
            {
                // A removal introduces nothing, so there is nothing whose advisory status could be unknown.
                rows.Add(new DependencyDelta(
                    name,
                    oldEntry!.Version,
                    null,
                    DependencyDeltaKind.Removed,
                    MajorJump: false,
                    InstallScripts: false,
                    RegistryOrMaintainerChange: false,
                    Array.Empty<string>(),
                    AdvisoriesChecked: true));
            }
        }

        return rows;
    }

    private static Dictionary<string, Entry> ParseManifest(string text, LockfileKind kind) => kind switch
    {
        LockfileKind.NpmPackageLock => ParseNpm(text),
        LockfileKind.PnpmLock => ParsePnpm(text),
        LockfileKind.CsprojPackageReference => ParseCsproj(text),
        LockfileKind.PoetryLock => ParsePoetry(text),
        _ => new Dictionary<string, Entry>(StringComparer.Ordinal),
    };

    // package-lock.json v2/v3 (`packages`) with a v1 (`dependencies`) fallback.
    private static Dictionary<string, Entry> ParseNpm(string text)
    {
        var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return map;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return map;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return map;
            }

            if (root.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Object)
            {
                foreach (var pkg in packages.EnumerateObject())
                {
                    var name = NpmNameFromKey(pkg.Name);
                    if (name is null || pkg.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue; // root package ("") or non-object.
                    }

                    var version = pkg.Value.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString() ?? "" : "";
                    var install = pkg.Value.TryGetProperty("hasInstallScript", out var hs)
                        && hs.ValueKind == JsonValueKind.True;
                    var registry = pkg.Value.TryGetProperty("resolved", out var r) && r.ValueKind == JsonValueKind.String
                        ? RegistryHost(r.GetString()) : null;

                    map[name] = new Entry(version, install, registry);
                }

                return map;
            }

            if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
            {
                foreach (var dep in deps.EnumerateObject())
                {
                    var version = dep.Value.ValueKind == JsonValueKind.Object
                        && dep.Value.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString() ?? "" : "";
                    map[dep.Name] = new Entry(version, InstallScripts: false, Registry: null);
                }
            }
        }

        return map;
    }

    private static string? NpmNameFromKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null; // the root package.
        }

        const string marker = "node_modules/";
        var idx = key.LastIndexOf(marker, StringComparison.Ordinal);
        var name = idx < 0 ? key : key[(idx + marker.Length)..];
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string? RegistryHost(string? resolved)
    {
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return null;
        }

        return Uri.TryCreate(resolved, UriKind.Absolute, out var uri) ? uri.Host : resolved;
    }

    // pnpm-lock.yaml: minimal line scan of the `packages:` section (v5 `/name/ver:` and v6+ `/name@ver:`).
    private static Dictionary<string, Entry> ParsePnpm(string text)
    {
        var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return map;
        }

        string? currentName = null;
        string currentVersion = "";
        var inPackages = false;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmedStart = raw.TrimStart();
            if (raw.StartsWith("packages:", StringComparison.Ordinal))
            {
                inPackages = true;
                continue;
            }

            if (!inPackages)
            {
                continue;
            }

            // A new top-level section ends the packages block.
            if (raw.Length > 0 && !char.IsWhiteSpace(raw[0]) && raw.TrimEnd().EndsWith(":", StringComparison.Ordinal))
            {
                break;
            }

            var keyMatch = Regex.Match(trimmedStart, "^'?/(?<name>@?[^@/]+(?:/[^@/]+)?)[@/](?<ver>[^:'()]+)");
            if (keyMatch.Success && (raw.StartsWith("  /", StringComparison.Ordinal) || raw.StartsWith("  '/", StringComparison.Ordinal)))
            {
                Flush(map, currentName, currentVersion, false);
                currentName = keyMatch.Groups["name"].Value;
                currentVersion = keyMatch.Groups["ver"].Value;
                continue;
            }

            if (currentName is not null && trimmedStart.StartsWith("requiresBuild:", StringComparison.Ordinal)
                && trimmedStart.Contains("true", StringComparison.Ordinal))
            {
                map[currentName] = new Entry(currentVersion, InstallScripts: true, Registry: null);
                // Mark so a later Flush does not overwrite the install flag.
                currentName = null;
            }
        }

        Flush(map, currentName, currentVersion, false);
        return map;

        static void Flush(Dictionary<string, Entry> m, string? name, string version, bool install)
        {
            if (name is not null && !m.ContainsKey(name))
            {
                m[name] = new Entry(version, install, null);
            }
        }
    }

    // *.csproj <PackageReference Include="X" Version="Y" /> (attribute order tolerant).
    private static Dictionary<string, Entry> ParseCsproj(string text)
    {
        var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return map;
        }

        foreach (Match m in Regex.Matches(text, "<PackageReference\\b[^>]*?/?>", RegexOptions.Singleline))
        {
            var element = m.Value;
            var include = Regex.Match(element, "Include\\s*=\\s*\"([^\"]+)\"");
            if (!include.Success)
            {
                continue;
            }

            var version = Regex.Match(element, "Version\\s*=\\s*\"([^\"]+)\"");
            map[include.Groups[1].Value] = new Entry(version.Success ? version.Groups[1].Value : "", false, null);
        }

        return map;
    }

    // poetry.lock: [[package]] blocks with name = "x" / version = "y".
    private static Dictionary<string, Entry> ParsePoetry(string text)
    {
        var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return map;
        }

        string? name = null;
        string version = "";
        var inPackage = false;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line == "[[package]]")
            {
                if (name is not null)
                {
                    map[name] = new Entry(version, false, null);
                }

                name = null;
                version = "";
                inPackage = true;
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line != "[[package]]")
            {
                inPackage = false;
            }

            if (!inPackage)
            {
                continue;
            }

            var nameMatch = Regex.Match(line, "^name\\s*=\\s*\"([^\"]+)\"");
            if (nameMatch.Success)
            {
                name = nameMatch.Groups[1].Value;
                continue;
            }

            var verMatch = Regex.Match(line, "^version\\s*=\\s*\"([^\"]+)\"");
            if (verMatch.Success)
            {
                version = verMatch.Groups[1].Value;
            }
        }

        if (name is not null)
        {
            map[name] = new Entry(version, false, null);
        }

        return map;
    }

    private static bool IsMajorJump(string? oldVersion, string? newVersion)
    {
        var oldMajor = LeadingMajor(oldVersion);
        var newMajor = LeadingMajor(newVersion);
        return oldMajor is not null && newMajor is not null && oldMajor != newMajor;
    }

    private static int? LeadingMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var start = 0;
        while (start < version.Length && !char.IsDigit(version[start]))
        {
            start++;
        }

        var end = start;
        while (end < version.Length && char.IsDigit(version[end]))
        {
            end++;
        }

        return start < end && int.TryParse(version[start..end], out var major) ? major : null;
    }
}
