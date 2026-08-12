using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Mainguard.Git.Review;

/// <summary>
/// Whether the offline advisory snapshot is in a state that lets an <i>absence</i> of hits mean anything.
/// </summary>
public enum OsvSnapshotState
{
    /// <summary>Loaded, parsed, and within <see cref="OsvSnapshot.MaxAge"/> — "no hits" means "no known advisories".</summary>
    Available,

    /// <summary>The embedded resource was not there at all. Nothing is known about any package.</summary>
    Missing,

    /// <summary>The resource was there and could not be parsed. Whatever it held was not read.</summary>
    Malformed,

    /// <summary>Loaded and parsed, but older than <see cref="OsvSnapshot.MaxAge"/> — hits are real, absences are not.</summary>
    Stale,
}

/// <summary>
/// The <b>offline</b> OSV (Open Source Vulnerabilities) snapshot the review path consults for CVE hits
/// (P2-11 §3.6). It is a shipped, embedded resource — a review-time <b>network call is a rejection
/// trigger</b>. The snapshot is refreshed out-of-band; <see cref="Lookup"/> only ever reads this local
/// copy. Tests can supply their own in-memory snapshot via <see cref="FromEntries"/>.
///
/// <para><b>Why an absence of hits is not by itself an answer.</b> A snapshot that is missing, malformed
/// or months out of date produces exactly the same empty <see cref="Lookup"/> result as a package with no
/// known advisory, and the two mean opposite things to a reviewer. Reporting the first as the second is the
/// "unknown reported as fine" shape this codebase carries <c>KillReport.RttMeasured</c> and
/// <c>ToolchainStatus.CouldNotCheck</c> to prevent, so this type carries the same third state explicitly:
/// <see cref="State"/> / <see cref="CanAnswer"/> say whether an absence is evidence, and every caller that
/// renders a CVE verdict is required to consult it. Hits from a stale snapshot are still real hits — a CVE
/// does not stop existing — so staleness widens the unknown rather than discarding what was read.</para>
/// </summary>
public sealed class OsvSnapshot
{
    // name (lower-case) → set of versions → advisory ids.
    private readonly Dictionary<string, Dictionary<string, List<string>>> _byName;

    private OsvSnapshot(Dictionary<string, Dictionary<string, List<string>>> byName) => _byName = byName;

    private static readonly Lazy<OsvSnapshot> _default = new(LoadEmbedded);

    /// <summary>
    /// How old a parsed snapshot may be before an <i>absence</i> of hits stops being evidence.
    ///
    /// <para>90 days is chosen against the refresh mechanism rather than against the threat: the snapshot
    /// is <b>bundled</b>, so it is refreshed by shipping a build, and a bound shorter than the release
    /// cadence would mark every install stale within weeks of release — which trains people to acknowledge
    /// the unknown-advisory item without reading it, and that is worse than not raising it. A bound longer
    /// than a release cycle would never fire at all.</para>
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    /// <summary>The shipped snapshot (embedded resource). Never performs IO beyond reading itself once.</summary>
    public static OsvSnapshot Default => _default.Value;

    /// <summary>The snapshot's stated capture date (informational; surfaced so "offline" is honest).</summary>
    public string SnapshotDate { get; private set; } = "";

    /// <summary><see cref="SnapshotDate"/> parsed, or null when it was absent or unparseable.</summary>
    public DateOnly? CapturedOn { get; private set; }

    /// <summary>Set when the snapshot could not be loaded at all; null when it loaded and only its age is
    /// in question.</summary>
    private OsvSnapshotState? _loadFailure;

    /// <summary>
    /// Whether an absence of hits from this snapshot is evidence, evaluated at <paramref name="asOf"/>.
    /// </summary>
    public OsvSnapshotState StateAt(DateTimeOffset asOf)
    {
        if (_loadFailure is { } failure)
        {
            return failure;
        }

        // A snapshot with no stated capture date cannot be shown to be current, and "we do not know how old
        // this is" is not a reason to treat its silence as a clean bill of health.
        if (CapturedOn is not { } captured)
        {
            return OsvSnapshotState.Stale;
        }

        var age = asOf - new DateTimeOffset(captured.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return age > MaxAge ? OsvSnapshotState.Stale : OsvSnapshotState.Available;
    }

    /// <summary><see cref="StateAt"/> now.</summary>
    public OsvSnapshotState State => StateAt(DateTimeOffset.UtcNow);

    /// <summary>
    /// True when an empty <see cref="Lookup"/> may be reported as "no known advisories". False sets
    /// <paramref name="reason"/> to the sentence a reviewer reads instead of a clean verdict.
    /// </summary>
    public bool CanAnswerAt(DateTimeOffset asOf, out string reason)
    {
        switch (StateAt(asOf))
        {
            case OsvSnapshotState.Available:
                reason = "";
                return true;

            case OsvSnapshotState.Missing:
                reason = "the offline advisory snapshot is missing from this build";
                return false;

            case OsvSnapshotState.Malformed:
                reason = "the offline advisory snapshot could not be read";
                return false;

            default:
                reason = CapturedOn is { } captured
                    ? $"the offline advisory snapshot was captured {captured:yyyy-MM-dd} and is older than "
                      + $"{(int)MaxAge.TotalDays} days"
                    : "the offline advisory snapshot states no capture date, so its age is unknown";
                return false;
        }
    }

    /// <summary><see cref="CanAnswerAt"/> now.</summary>
    public bool CanAnswer(out string reason) => CanAnswerAt(DateTimeOffset.UtcNow, out reason);

    /// <summary>The advisory ids affecting <paramref name="name"/> at exactly <paramref name="version"/>.</summary>
    public IReadOnlyList<string> Lookup(string name, string? version)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
        {
            return Array.Empty<string>();
        }

        if (_byName.TryGetValue(name.Trim().ToLowerInvariant(), out var byVersion)
            && byVersion.TryGetValue(version.Trim(), out var ids))
        {
            return ids;
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Builds an in-memory snapshot from (id, name, versions) advisory tuples.
    /// </summary>
    /// <param name="advisories">The advisories this snapshot knows about.</param>
    /// <param name="capturedOn">
    /// The capture date to date the snapshot at. Defaults to <b>today</b> so a hand-built snapshot is
    /// <see cref="OsvSnapshotState.Available"/> rather than accidentally stale; pass an old date to build
    /// the stale case deliberately.
    /// </param>
    public static OsvSnapshot FromEntries(
        IEnumerable<(string Id, string Name, IReadOnlyList<string> Versions)> advisories,
        DateOnly? capturedOn = null)
    {
        var byName = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        foreach (var (id, name, versions) in advisories)
        {
            Add(byName, id, name, versions);
        }

        var captured = capturedOn ?? DateOnly.FromDateTime(DateTime.UtcNow);
        return new OsvSnapshot(byName)
        {
            CapturedOn = captured,
            SnapshotDate = captured.ToString("yyyy-MM-dd"),
        };
    }

    /// <summary>
    /// A snapshot that knows nothing and says so — the exact value <see cref="LoadEmbedded"/> produces when
    /// the embedded resource is absent or unreadable. Exposed so the unavailable path can be exercised
    /// against the same object the product would build, rather than against a stand-in that only resembles it.
    /// </summary>
    public static OsvSnapshot Unavailable(OsvSnapshotState state = OsvSnapshotState.Missing)
        => new(new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal))
        {
            _loadFailure = state == OsvSnapshotState.Malformed
                ? OsvSnapshotState.Malformed
                : OsvSnapshotState.Missing,
        };

    private static OsvSnapshot LoadEmbedded()
    {
        var byName = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        var snapshot = new OsvSnapshot(byName);

        try
        {
            var asm = typeof(OsvSnapshot).Assembly;
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("OsvSnapshot.json", StringComparison.Ordinal));
            if (resourceName is null)
            {
                snapshot._loadFailure = OsvSnapshotState.Missing;
                return snapshot;
            }

            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                snapshot._loadFailure = OsvSnapshotState.Missing;
                return snapshot;
            }

            using var reader = new StreamReader(stream);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            var root = doc.RootElement;

            if (root.TryGetProperty("snapshotDate", out var date) && date.ValueKind == JsonValueKind.String)
            {
                snapshot.SnapshotDate = date.GetString() ?? "";
                if (DateOnly.TryParse(
                        snapshot.SnapshotDate,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var parsed))
                {
                    snapshot.CapturedOn = parsed;
                }
            }

            if (root.TryGetProperty("advisories", out var advisories) && advisories.ValueKind == JsonValueKind.Array)
            {
                foreach (var adv in advisories.EnumerateArray())
                {
                    var id = adv.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var name = adv.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var versions = new List<string>();
                    if (adv.TryGetProperty("versions", out var vArr) && vArr.ValueKind == JsonValueKind.Array)
                    {
                        versions.AddRange(vArr.EnumerateArray()
                            .Where(v => v.ValueKind == JsonValueKind.String)
                            .Select(v => v.GetString()!)
                            .Where(s => !string.IsNullOrWhiteSpace(s)));
                    }

                    Add(byName, id!, name!, versions);
                }
            }
        }
        catch (JsonException)
        {
            // A malformed snapshot must not crash review — but it must not degrade to "no known CVEs"
            // either. It degrades to "not checked", which is what Malformed carries to the reviewer.
            snapshot._loadFailure = OsvSnapshotState.Malformed;
        }

        return snapshot;
    }

    private static void Add(
        Dictionary<string, Dictionary<string, List<string>>> byName,
        string id,
        string name,
        IReadOnlyList<string> versions)
    {
        var key = name.Trim().ToLowerInvariant();
        if (!byName.TryGetValue(key, out var byVersion))
        {
            byVersion = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            byName[key] = byVersion;
        }

        foreach (var version in versions)
        {
            var v = version.Trim();
            if (!byVersion.TryGetValue(v, out var ids))
            {
                ids = new List<string>();
                byVersion[v] = ids;
            }

            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }
    }
}
