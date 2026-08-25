using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Mainguard.UI.Theming;

namespace Mainguard.App.Shell.Controls;

/// <summary>
/// The classic image-editor transparency checkerboard, drawn from design tokens (T-13 image-diff
/// polish). A transparent PNG rendered straight onto a flat card surface is ambiguous — the viewer
/// cannot tell transparent pixels from surface-coloured pixels, which is exactly the question an
/// image diff asks. The checker alternates the <c>SurfaceDeep</c> and <c>SurfaceCard</c> tokens
/// (resolved at render time, like <see cref="CommitGraphCanvas"/>, so it reads correctly in all
/// registered themes and follows live theme switches) — a deliberately quiet two-surface check rather
/// than the hard grey/white of raster editors, so the images stay the loudest thing on the stage.
/// </summary>
public sealed class CheckerboardBackdrop : Control
{
    private const double CellSize = 8.0;

    private static readonly (string Key, string Fallback) Light = ("SurfaceCard", "#161B22");
    private static readonly (string Key, string Fallback) Dark = ("SurfaceDeep", "#0D1117");

    private IBrush? _lightBrush;
    private IBrush? _darkBrush;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        _lightBrush = null;
        _darkBrush = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var light = _lightBrush ??= Resolve(Light.Key, Light.Fallback);
        var dark = _darkBrush ??= Resolve(Dark.Key, Dark.Fallback);

        // Base coat in the darker tone, then the alternating lighter cells on top (half the rects).
        context.FillRectangle(dark, new Rect(0, 0, bounds.Width, bounds.Height));

        int cols = (int)System.Math.Ceiling(bounds.Width / CellSize);
        int rows = (int)System.Math.Ceiling(bounds.Height / CellSize);
        for (int r = 0; r < rows; r++)
        {
            for (int c = (r & 1); c < cols; c += 2)
            {
                double x = c * CellSize, y = r * CellSize;
                context.FillRectangle(light, new Rect(
                    x, y,
                    System.Math.Min(CellSize, bounds.Width - x),
                    System.Math.Min(CellSize, bounds.Height - y)));
            }
        }
    }

    private static IBrush Resolve(string key, string fallback)
    {
        var app = Application.Current;
        if (app != null
            && app.TryGetResource(key, app.ActualThemeVariant, out var res)
            && res is IBrush brush)
        {
            return brush;
        }
        return SolidColorBrush.Parse(fallback);
    }
}
