using Avalonia;
using Avalonia.Media;
using LiveChartsCore.Drawing;
using SkiaSharp;

namespace Mainguard.UI.Charts;

/// <summary>
/// Resolves LiveChartsCore paint colors from the app's theme tokens so every chart follows the active
/// theme instead of hardcoding hex (the golden rule). Charts are rebuilt per analytics load, so they
/// pick up whichever registered theme is active at open time. Nothing here invents a hue: the
/// categorical palette is the semantics-free graph-lane tokens (ordered for max colour-vision
/// separation — validated with the dataviz palette checker), churn add/remove reuse the Success/Danger
/// tokens by meaning, and the heatmap ramp blends the surface token toward the Accent token.
/// </summary>
public static class ChartTheme
{
    /// <summary>Resolve a solid-colour token to an <see cref="SKColor"/>, falling back to a literal.</summary>
    public static SKColor Color(string token, string fallback)
    {
        if (Application.Current is { } app
            && app.TryGetResource(token, app.ActualThemeVariant, out var res)
            && res is ISolidColorBrush brush)
        {
            var c = brush.Color;
            return new SKColor(c.R, c.G, c.B, c.A);
        }
        return SKColor.Parse(fallback);
    }

    public static LvcColor Lvc(string token, string fallback)
    {
        var c = Color(token, fallback);
        return new LvcColor(c.Red, c.Green, c.Blue, c.Alpha);
    }

    /// <summary>
    /// Categorical palette (identity, fixed order, never cycled) for the language breakdown — the
    /// graph-lane tokens, which the design system defines as decoupled from semantics. Ordered
    /// [violet, rose, amber, teal, sky] so no colour-vision-confusable pair sits adjacent. A series
    /// beyond the fifth folds into "Other" (<see cref="OtherColor"/>), never a generated hue.
    /// </summary>
    public static SKColor[] CategoricalPalette() => new[]
    {
        Color("Lane1", "#9F9FFC"),
        Color("Lane2", "#D0709F"),
        Color("Lane4", "#F2A918"),
        Color("Lane3", "#C0EAE3"),
        Color("Lane5", "#0B87F5"),
    };

    /// <summary>The neutral "Other" bucket colour (muted text token).</summary>
    public static SKColor OtherColor() => Color("TextMuted", "#8A93A6");

    /// <summary>
    /// Sequential single-hue ramp for the punch-card heatmap: near-surface (empty cells recede) →
    /// full Accent (peak activity). Blending toward the theme's own surface keeps it legible on both
    /// the light and dark themes without a second hue (sequential rule).
    /// </summary>
    public static LvcColor[] HeatRamp()
    {
        var accent = Color("AccentBrush", "#8487F0");
        var surface = Color("SurfaceCard", "#1A1E24");
        return new[]
        {
            ToLvc(Blend(accent, surface, 0.86f)),
            ToLvc(Blend(accent, surface, 0.40f)),
            ToLvc(accent),
        };
    }

    private static SKColor Blend(SKColor a, SKColor b, float towardB)
    {
        byte Mix(byte x, byte y) => (byte)(x + (y - x) * towardB);
        return new SKColor(Mix(a.Red, b.Red), Mix(a.Green, b.Green), Mix(a.Blue, b.Blue), 255);
    }

    private static LvcColor ToLvc(SKColor c) => new(c.Red, c.Green, c.Blue, c.Alpha);
}
