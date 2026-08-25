using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Mainguard.UI.Theming;

/// <summary>
/// Opt-in macOS vibrancy for the main window's OUTER chrome (window background, title bar,
/// section rail). The window binds those three paints to <c>ChromeWindowBackground</c> /
/// <c>ChromePanelBackground</c> — indirection tokens every theme defines as exact opaque
/// duplicates of SurfaceWindow/SurfacePanel. Enabling vibrancy asks the window for
/// <see cref="WindowTransparencyLevel.AcrylicBlur"/> (NSVisualEffectView behind the window) and,
/// ONLY if the platform actually granted it, shadows the two chrome tokens at app level with the
/// theme's <c>SurfaceWindowVibrant</c>/<c>SurfacePanelVibrant</c> translucent variants — direct
/// app-resource entries win over merged theme dictionaries and survive ThemeManager's
/// "/Themes/" sweep, and re-resolving on <see cref="ThemeManager.ThemeChanged"/> keeps a live
/// theme switch translucent. Content surfaces (cards, diff, terminal) never route through the
/// chrome tokens and stay opaque for legibility. Non-macOS and headless hosts are a hard no-op:
/// the opaque defaults are the canonical, harness-verified rendering.
/// </summary>
public static class VibrancyManager
{
    private static Window? _window;
    private static bool _enabled;
    private static bool _subscribed;

    /// <summary>The main window whose chrome the toggle drives. Idempotent; call once at startup.
    /// Subscribes to the window's ACTUAL transparency level — the platform grants (or refuses,
    /// e.g. under Reduce-Transparency) the hint asynchronously, so the token overrides follow the
    /// granted level rather than the request.</summary>
    public static void Attach(Window window)
    {
        _window = window;
        if (!OperatingSystem.IsMacOS()) return;
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == TopLevel.ActualTransparencyLevelProperty)
                Refresh();
        };
    }

    public static bool IsEnabled => _enabled;

    /// <summary>Turn the translucent chrome on/off. Safe to call on any platform (no-op off macOS)
    /// and before <see cref="Attach"/> (no-op until a window exists).</summary>
    public static void SetEnabled(bool enabled)
    {
        _enabled = enabled && OperatingSystem.IsMacOS();
        if (_window is null || !OperatingSystem.IsMacOS()) return;

        _window.TransparencyLevelHint = _enabled
            ? new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None }
            : new[] { WindowTransparencyLevel.None };
        EnsureThemeSubscription();
        Refresh();
    }

    /// <summary>Shadow the chrome tokens only while the platform has actually composited a blur —
    /// translucent paints over an opaque window would just dim the chrome, not reveal anything.</summary>
    private static void Refresh()
    {
        if (_enabled && _window?.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur)
            ApplyVibrantOverrides();
        else
            RemoveVibrantOverrides();
    }

    private static void ApplyVibrantOverrides()
    {
        var app = Application.Current;
        if (app is null) return;

        // Resolve from the ACTIVE theme dictionary (the app-level override for a key must be
        // removed first or TryGetResource would read back our own previous override).
        RemoveVibrantOverrides();
        foreach (var (chromeKey, vibrantKey) in Mapping())
        {
            if (app.TryGetResource(vibrantKey, app.ActualThemeVariant, out var value) && value is IBrush brush)
                app.Resources[chromeKey] = brush;
        }
    }

    private static void RemoveVibrantOverrides()
    {
        var app = Application.Current;
        if (app is null) return;
        foreach (var (chromeKey, _) in Mapping())
            app.Resources.Remove(chromeKey);
    }

    private static IEnumerable<(string ChromeKey, string VibrantKey)> Mapping()
    {
        yield return ("ChromeWindowBackground", "SurfaceWindowVibrant");
        yield return ("ChromePanelBackground", "SurfacePanelVibrant");
    }

    private static void EnsureThemeSubscription()
    {
        if (_subscribed) return;
        ThemeManager.ThemeChanged += Refresh;
        _subscribed = true;
    }
}
