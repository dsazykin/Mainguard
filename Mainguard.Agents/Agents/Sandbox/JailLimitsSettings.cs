using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Mainguard.Git.Audit;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>What the operator persisted for every jail's ceiling — absent means "the compiled defaults".</summary>
public sealed record JailLimitsDocument(long MemoryBytes, double Cpus);

/// <summary>The persistence seam for <see cref="JailLimitsSettings"/> (daemon-side, restart-safe).</summary>
public interface IJailLimitsStore
{
    /// <summary>The persisted document, or <c>null</c> when nothing has been persisted (or it is unreadable).</summary>
    JailLimitsDocument? Load();

    void Save(JailLimitsDocument document);
}

/// <summary>An <see cref="IJailLimitsStore"/> that forgets on restart. The default in tests.</summary>
public sealed class InMemoryJailLimitsStore : IJailLimitsStore
{
    private JailLimitsDocument? _value;

    public InMemoryJailLimitsStore(JailLimitsDocument? seed = null) => _value = seed;

    public JailLimitsDocument? Load() => _value;

    public void Save(JailLimitsDocument document) => _value = document;
}

/// <summary>
/// A one-file JSON <see cref="IJailLimitsStore"/>, written beside the plan store for the same reason that
/// one is a file: the launcher reads it at every spawn and must not need a database for it. Every failure
/// reads as "nothing persisted" — the compiled defaults — never as a ceiling of zero.
/// </summary>
public sealed class JsonJailLimitsStore : IJailLimitsStore
{
    private readonly string _path;

    public JsonJailLimitsStore(string path) => _path = path ?? throw new ArgumentNullException(nameof(path));

    public JailLimitsDocument? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<JailLimitsDocument>(File.ReadAllText(_path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(JailLimitsDocument document)
    {
        var dir = Path.GetDirectoryName(_path);
        if (dir is { Length: > 0 })
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_path, JsonSerializer.Serialize(document));
    }
}

/// <summary>
/// The per-jail memory/CPU ceiling every spawn is created with, as an operator setting (owner decision
/// 2026-09-04: a setting, defaults kept, no automatic fleet cap). Until this the ceiling was the compiled
/// <see cref="SandboxLimits.Default"/> — 2 GiB and 2 CPUs per jail — which on a 16 GB laptop is the
/// whole machine by the fifth worker. The daemon owns it because the daemon is what spawns; the page is
/// its client over gRPC.
///
/// <para>Clamped, not validated: a value outside the band is pulled to its edge and persisted that way,
/// and the caller renders what was persisted. The band is wide on purpose — the setting exists so a
/// weaker machine can go smaller, and a build server larger; it is not the place to second-guess either.
/// Applies to jails created after the save; a running jail keeps the ceiling it was created with.</para>
/// </summary>
public sealed class JailLimitsSettings
{
    public const string ChangedEvent = "jail_limits_changed";
    public const long MinMemoryBytes = 512L * 1024 * 1024;
    public const long MaxMemoryBytes = 64L * 1024 * 1024 * 1024;
    public const double MinCpus = 0.5;
    public const double MaxCpus = 64;

    private readonly IJailLimitsStore _store;
    private readonly IAuditLog? _audit;
    private readonly object _gate = new();
    private SandboxLimits? _current;

    public JailLimitsSettings(IJailLimitsStore store, IAuditLog? audit = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _audit = audit;
    }

    /// <summary>The ceiling the next spawn is created with: the persisted document (clamped again on
    /// read, so a hand-edited file cannot smuggle a zero) over <see cref="SandboxLimits.Default"/>.</summary>
    public SandboxLimits Current
    {
        get
        {
            lock (_gate)
            {
                if (_current is null)
                {
                    var doc = _store.Load();
                    _current = doc is null ? SandboxLimits.Default : Apply(doc);
                }

                return _current;
            }
        }
    }

    /// <summary>True while nothing has been persisted or the persisted values equal the defaults.</summary>
    public bool IsDefault => Current.MemoryBytes == SandboxLimits.Default.MemoryBytes
        && Current.Cpus == SandboxLimits.Default.Cpus;

    /// <summary>Clamps, persists, audits, and returns the ceiling AS PERSISTED.</summary>
    public SandboxLimits Set(long memoryBytes, double cpus, string actor)
    {
        var doc = new JailLimitsDocument(ClampMemory(memoryBytes), ClampCpus(cpus));
        SandboxLimits previous;
        SandboxLimits next;
        lock (_gate)
        {
            previous = Current;
            _store.Save(doc);
            next = Apply(doc);
            _current = next;
        }

        _audit?.Append(new AuditEvent(ChangedEvent, new Dictionary<string, string>
        {
            ["actor"] = actor ?? string.Empty,
            ["memory_bytes"] = next.MemoryBytes.ToString(CultureInfo.InvariantCulture),
            ["cpus"] = next.Cpus.ToString(CultureInfo.InvariantCulture),
            ["previous_memory_bytes"] = previous.MemoryBytes.ToString(CultureInfo.InvariantCulture),
            ["previous_cpus"] = previous.Cpus.ToString(CultureInfo.InvariantCulture),
        }));
        return next;
    }

    public static long ClampMemory(long memoryBytes) => Math.Clamp(memoryBytes, MinMemoryBytes, MaxMemoryBytes);

    public static double ClampCpus(double cpus) =>
        double.IsNaN(cpus) ? SandboxLimits.DefaultCpus : Math.Clamp(cpus, MinCpus, MaxCpus);

    private static SandboxLimits Apply(JailLimitsDocument doc) => SandboxLimits.Default with
    {
        MemoryBytes = ClampMemory(doc.MemoryBytes),
        Cpus = ClampCpus(doc.Cpus),
    };
}
