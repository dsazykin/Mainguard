using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mainguard.Git;

namespace Mainguard.Agents.Agents.Adapters;

/// <summary>A previously effective pin, kept so a user-applied CLI update can be reverted.</summary>
public sealed record AdapterPinSnapshot(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("payloadUrl")] string PayloadUrl,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>
/// A user-applied pin override for one adapter: the version/payload/sha256 that replaces the bundled
/// manifest's pin when the user accepted an update (<see cref="AgentCliUpdateService"/>). The pinning
/// DISCIPLINE is unchanged — an override is still a concrete version with a sha256 computed from the
/// exact bytes that will be installed; only the pin's ORIGIN moves from the shipped manifest to the
/// user's explicit choice. <see cref="Previous"/> is the one-step history the settings "Revert"
/// restores when a new CLI version breaks the app.
/// </summary>
public sealed record AdapterPinOverride(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("payloadUrl")] string PayloadUrl,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("previous")] AdapterPinSnapshot? Previous = null)
{
    /// <summary>The manifest spec with this override applied: version, payload, hash, and the
    /// version-matched probe substring all move together (a probe still expecting the old version
    /// would fail every install as a VersionMismatch).</summary>
    public AdapterSpec Apply(AdapterSpec spec) => spec with
    {
        Version = Version,
        PayloadUrl = PayloadUrl,
        Sha256 = Sha256,
        HealthProbe = spec.HealthProbe is null
            ? null
            : spec.HealthProbe with { ExpectedVersionSubstring = Version },
    };
}

/// <summary>Persistence for the per-adapter pin overrides. File-backed in production; injectable
/// for tests (the update/revert flows are exercised with an in-memory fake).</summary>
public interface IAdapterPinOverrideStore
{
    AdapterPinOverride? TryGet(string adapterId);
    void Set(string adapterId, AdapterPinOverride pin);
    void Remove(string adapterId);
}

/// <summary>
/// The file-backed override store: one JSON dictionary at
/// <c>%LocalAppData%\Mainguard\adapters\pin-overrides.json</c>. Entries are validated like the manifest
/// parser (a concrete pinned version, a 64-hex sha256, an HTTPS payload URL) on <b>both</b> sides of the
/// file — <see cref="Set"/> refuses to write one, and <see cref="ReadAll"/> drops one it finds — so a
/// corrupt or hand-edited entry can never weaken what <see cref="AdapterChannel.EnsureAsync"/> installs.
/// Validating only on write trusted the file itself, which is exactly the input an attacker (or a botched
/// merge) can supply: this file is a plain user-writable JSON on disk, not something only this class
/// authors, so an unvalidated read let an `http://` payload URL or a `latest` version back in. A corrupt
/// FILE, and now any invalid ENTRY within a good file, reads as "no override" — the bundled pin applies,
/// which is the safe direction.
/// </summary>
public sealed class FileAdapterPinOverrideStore : IAdapterPinOverrideStore
{
    private readonly string _path;

    public FileAdapterPinOverrideStore(string? path = null)
    {
        // MainguardPaths, not GetFolderPath: same service-context relative-path hazard as the
        // manifest cache next to this file.
        _path = path ?? Path.Combine(MainguardPaths.DataRoot(), "adapters", "pin-overrides.json");
    }

    public AdapterPinOverride? TryGet(string adapterId)
        => ReadAll().TryGetValue(adapterId, out var pin) ? pin : null;

    public void Set(string adapterId, AdapterPinOverride pin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        Validate(pin);
        var all = ReadAll();
        all[adapterId] = pin;
        WriteAll(all);
    }

    public void Remove(string adapterId)
    {
        var all = ReadAll();
        if (all.Remove(adapterId))
        {
            WriteAll(all);
        }
    }

    private static void Validate(AdapterPinOverride pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        var reason = Invalid(pin);
        if (reason is not null) throw new ArgumentException(reason);
    }

    /// <summary>The single pin-discipline rule, phrased as a reason-or-null so the write path can throw
    /// it and the read path can silently drop the entry. One rule, two callers — a second copy is how
    /// the read side drifted into trusting the file in the first place.</summary>
    private static string? Invalid(AdapterPinOverride? pin)
    {
        if (pin is null) return "Override entry is null.";
        if (!AdapterManifest.IsPinnedVersion(pin.Version))
            return $"Override version '{pin.Version}' is not pinned to a concrete release.";
        if (pin.Sha256 is not { Length: 64 })
            return "Override sha256 must be 64 hex chars.";
        if (pin.PayloadUrl is null || !pin.PayloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "Override payloadUrl must be HTTPS.";
        return null;
    }

    private Dictionary<string, AdapterPinOverride> ReadAll()
    {
        try
        {
            if (!File.Exists(_path))
                return new Dictionary<string, AdapterPinOverride>(StringComparer.Ordinal);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, AdapterPinOverride>>(File.ReadAllText(_path))
                ?? new Dictionary<string, AdapterPinOverride>(StringComparer.Ordinal);

            // Re-validate on the way IN: the file is user-writable, so "we validated it when we wrote it"
            // is not a property of what we just read back. An entry that fails the pin discipline is
            // dropped rather than thrown, so one bad hand-edit degrades to the bundled pin for THAT
            // adapter instead of bricking every install. Dropping it here also prunes it from the next
            // Set(), because Set writes this same (filtered) dictionary back.
            var clean = new Dictionary<string, AdapterPinOverride>(StringComparer.Ordinal);
            foreach (var (id, pin) in parsed)
            {
                if (!string.IsNullOrWhiteSpace(id) && Invalid(pin) is null) clean[id] = pin;
            }

            return clean;
        }
        catch (Exception)
        {
            // A corrupt override file must never brick installs — the bundled pins simply apply.
            return new Dictionary<string, AdapterPinOverride>(StringComparer.Ordinal);
        }
    }

    private void WriteAll(Dictionary<string, AdapterPinOverride> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
    }
}
