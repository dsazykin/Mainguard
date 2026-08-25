using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Mainguard.Git.Actions;
using Mainguard.Git.Models;
using Xunit;

namespace Mainguard.Tests;

// TI-18: the shortcut map is pure and persisted. It must detect gesture conflicts (two ids → one gesture),
// normalise gestures so case/modifier-order don't create phantom conflicts, and round-trip through
// UserPreferences so rebinds survive a restart.
public class ShortcutMapTests
{
    [Fact]
    public void Default_ShouldCarryTheFiveDocumentedGestures()
    {
        // The primary modifier is per-platform (design rule P-7): Cmd on macOS, Ctrl elsewhere.
        var m = ShortcutMap.PrimaryModifier;
        var map = ShortcutMap.Default;
        Assert.Equal($"{m}+P", map.GestureFor(ActionIds.OpenCommandPalette));
        Assert.Equal($"{m}+Enter", map.GestureFor(ActionIds.Commit));
        Assert.Equal($"{m}+Shift+P", map.GestureFor(ActionIds.Push));
        Assert.Equal("F5", map.GestureFor(ActionIds.Refresh));
        Assert.Equal($"{m}+B", map.GestureFor(ActionIds.NewBranch));
        Assert.False(map.HasConflicts);
    }

    [Fact]
    public void ConflictDetection_ShouldFlagDuplicateGesture()
    {
        var map = new ShortcutMap(new Dictionary<string, string>
        {
            ["commit"] = "Ctrl+Enter",
            ["push"] = "Ctrl+Shift+P",
            ["refresh"] = "ctrl+enter", // same gesture as commit, different case
        });

        Assert.True(map.HasConflicts);
        var conflict = Assert.Single(map.Conflicts());
        Assert.Equal(new[] { "commit", "refresh" }, conflict.ActionIds.ToArray());
    }

    [Fact]
    public void NormalizeGesture_ShouldIgnoreCaseAndModifierOrder()
    {
        Assert.Equal(ShortcutMap.NormalizeGesture("Ctrl+Shift+P"), ShortcutMap.NormalizeGesture("shift+ctrl+p"));
        Assert.Equal(ShortcutMap.NormalizeGesture("Ctrl+P"), ShortcutMap.NormalizeGesture("Control+p"));
        Assert.NotEqual(ShortcutMap.NormalizeGesture("Ctrl+P"), ShortcutMap.NormalizeGesture("Ctrl+Shift+P"));
    }

    [Fact]
    public void With_ShouldRebind_WithoutMutatingOriginal()
    {
        var map = ShortcutMap.Default;
        var rebound = map.With(ActionIds.Refresh, "Ctrl+R");

        Assert.Equal("F5", map.GestureFor(ActionIds.Refresh));         // original untouched
        Assert.Equal("Ctrl+R", rebound.GestureFor(ActionIds.Refresh)); // new map rebound
    }

    [Fact]
    public void ActionFor_ShouldResolveGestureToActionId()
    {
        var m = ShortcutMap.PrimaryModifier.ToLowerInvariant();
        var map = ShortcutMap.Default;
        Assert.Equal(ActionIds.Push, map.ActionFor($"{m}+shift+p"));
        Assert.Null(map.ActionFor($"{m}+Q"));
    }

    [Fact]
    public void FromPreferences_ShouldOverlayOverridesOnDefaults()
    {
        var overrides = new Dictionary<string, string> { [ActionIds.Refresh] = "Ctrl+R" };
        var map = ShortcutMap.FromPreferences(overrides);

        Assert.Equal("Ctrl+R", map.GestureFor(ActionIds.Refresh));    // overridden
        Assert.Equal($"{ShortcutMap.PrimaryModifier}+P", map.GestureFor(ActionIds.OpenCommandPalette)); // default retained
    }

    [Fact]
    public void ShouldRoundTripThroughPreferences()
    {
        // Rebind refresh, persist the override into UserPreferences, serialize+deserialize (as SettingsService
        // does), and confirm the rebind survives.
        var rebound = ShortcutMap.Default.With(ActionIds.Refresh, "Ctrl+R");
        var prefs = new UserPreferences { ShortcutBindings = rebound.ToPreferences() };

        var json = JsonSerializer.Serialize(prefs);
        var restored = JsonSerializer.Deserialize<UserPreferences>(json)!;

        var restoredMap = ShortcutMap.FromPreferences(restored.ShortcutBindings);
        Assert.Equal("Ctrl+R", restoredMap.GestureFor(ActionIds.Refresh));
        // Untouched defaults still resolve after the round-trip.
        Assert.Equal($"{ShortcutMap.PrimaryModifier}+P", restoredMap.GestureFor(ActionIds.OpenCommandPalette));
    }

    [Fact]
    public void ToPreferences_ShouldOnlyPersistDifferencesFromDefault()
    {
        var rebound = ShortcutMap.Default.With(ActionIds.Refresh, "Ctrl+R");
        var overrides = rebound.ToPreferences();

        Assert.True(overrides.ContainsKey(ActionIds.Refresh));
        Assert.False(overrides.ContainsKey(ActionIds.OpenCommandPalette)); // unchanged default not persisted
    }
}
