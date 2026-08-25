using System;
using System.Collections.Generic;
using System.Linq;

namespace Mainguard.Git.Actions;

/// <summary>
/// A pure, UI-free map from action id to key-gesture string (T-18). Gestures are stored as their raw
/// display strings (e.g. <c>"Ctrl+Shift+P"</c>) but compared through <see cref="NormalizeGesture"/> so
/// case and modifier order never create phantom (non-)conflicts. Rebinding returns a new map (value
/// semantics); <see cref="Conflicts"/> flags any gesture bound to more than one action so the rebind UI
/// can warn. Persisted via <see cref="Models.UserPreferences.ShortcutBindings"/> — see
/// <see cref="FromPreferences"/> / <see cref="ToPreferences"/> — so rebinds survive a restart.
/// </summary>
public sealed class ShortcutMap
{
    private static readonly string[] ModifierOrder = { "ctrl", "control", "shift", "alt", "meta", "cmd", "win" };

    private readonly Dictionary<string, string> _bindings;

    public ShortcutMap(IEnumerable<KeyValuePair<string, string>> bindings)
    {
        if (bindings is null) throw new ArgumentNullException(nameof(bindings));
        _bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in bindings)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                continue;
            _bindings[kv.Key] = kv.Value.Trim();
        }
    }

    /// <summary>
    /// The platform's primary shortcut modifier: <c>Cmd</c> (Avalonia's <c>Meta</c>) on macOS —
    /// design rule P-7, platform respect — <c>Ctrl</c> everywhere else. Defaults are built from
    /// this; a user's persisted rebinds are stored literally and never rewritten.
    /// </summary>
    public static string PrimaryModifier =>
        OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";

    /// <summary>The built-in defaults: primary+P palette, primary+Enter commit, primary+Shift+P push,
    /// F5 refresh, primary+B new branch — where primary is <see cref="PrimaryModifier"/>.</summary>
    public static ShortcutMap Default => new(new Dictionary<string, string>
    {
        [ActionIds.OpenCommandPalette] = $"{PrimaryModifier}+P",
        [ActionIds.Commit] = $"{PrimaryModifier}+Enter",
        [ActionIds.Push] = $"{PrimaryModifier}+Shift+P",
        [ActionIds.Refresh] = "F5",
        [ActionIds.NewBranch] = $"{PrimaryModifier}+B",
    });

    /// <summary>id → gesture, in no particular order.</summary>
    public IReadOnlyDictionary<string, string> Bindings => _bindings;

    /// <summary>The gesture bound to <paramref name="actionId"/>, or null if unbound.</summary>
    public string? GestureFor(string actionId) => _bindings.TryGetValue(actionId, out var g) ? g : null;

    /// <summary>The action id bound to <paramref name="gesture"/> (first, by ordinal id), or null if none.</summary>
    public string? ActionFor(string gesture)
    {
        var norm = NormalizeGesture(gesture);
        return _bindings
            .Where(kv => NormalizeGesture(kv.Value) == norm)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (string?)kv.Key)
            .FirstOrDefault();
    }

    /// <summary>Returns a new map with <paramref name="actionId"/> rebound to <paramref name="gesture"/>
    /// (or unbound when the gesture is blank).</summary>
    public ShortcutMap With(string actionId, string? gesture)
    {
        if (string.IsNullOrWhiteSpace(actionId)) throw new ArgumentException("actionId required", nameof(actionId));
        var next = new Dictionary<string, string>(_bindings, StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(gesture))
            next.Remove(actionId);
        else
            next[actionId] = gesture.Trim();
        return new ShortcutMap(next);
    }

    /// <summary>Gestures bound to two or more actions, each with the offending action ids (ordinal-sorted).</summary>
    public IReadOnlyList<ShortcutConflict> Conflicts()
    {
        return _bindings
            .GroupBy(kv => NormalizeGesture(kv.Value))
            .Where(g => g.Count() > 1)
            .Select(g => new ShortcutConflict(
                g.First().Value,
                g.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList()))
            .OrderBy(c => c.Gesture, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True when any gesture is bound to more than one action.</summary>
    public bool HasConflicts => Conflicts().Count > 0;

    /// <summary>Canonical form of a gesture for equality: lowercased, whitespace-free, modifiers sorted into a
    /// fixed order with the non-modifier key last. Pure string work — no UI-framework dependency.</summary>
    public static string NormalizeGesture(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return string.Empty;
        var parts = gesture
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .ToList();
        if (parts.Count == 0) return string.Empty;

        var modifiers = parts.Where(p => ModifierOrder.Contains(p))
                             .Select(CanonicalModifier)
                             .Distinct()
                             .OrderBy(m => Array.IndexOf(new[] { "ctrl", "shift", "alt", "meta" }, m))
                             .ToList();
        var keys = parts.Where(p => !ModifierOrder.Contains(p)).ToList();
        return string.Join("+", modifiers.Concat(keys));
    }

    private static string CanonicalModifier(string m) => m switch
    {
        "control" => "ctrl",
        "cmd" or "win" => "meta",
        _ => m,
    };

    /// <summary>Builds the effective map from persisted preferences: the built-in <see cref="Default"/>
    /// overlaid with the user's stored overrides (an override may rebind or add a gesture).</summary>
    public static ShortcutMap FromPreferences(IReadOnlyDictionary<string, string>? overrides)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in Default.Bindings) merged[kv.Key] = kv.Value;
        if (overrides != null)
            foreach (var kv in overrides)
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) merged.Remove(kv.Key);
                else merged[kv.Key] = kv.Value.Trim();
            }
        return new ShortcutMap(merged);
    }

    /// <summary>The subset of bindings that differ from <see cref="Default"/>, suitable for persisting.</summary>
    public Dictionary<string, string> ToPreferences()
    {
        var defaults = Default.Bindings;
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in _bindings)
        {
            if (!defaults.TryGetValue(kv.Key, out var d) || NormalizeGesture(d) != NormalizeGesture(kv.Value))
                overrides[kv.Key] = kv.Value;
        }
        // A default binding the user cleared should persist as an explicit empty override.
        foreach (var kv in defaults)
            if (!_bindings.ContainsKey(kv.Key))
                overrides[kv.Key] = string.Empty;
        return overrides;
    }
}

/// <summary>One gesture bound to multiple action ids (a rebind conflict).</summary>
public sealed record ShortcutConflict(string Gesture, IReadOnlyList<string> ActionIds);
