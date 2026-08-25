using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Mainguard.Git.Graph;
using Mainguard.UI.Theming;

namespace Mainguard.App.Shell.Controls;

public class CommitGraphCanvas : Control
{
    // A dependency property so we can bind the GraphNode from XAML!
    public static readonly StyledProperty<GraphNode> NodeProperty =
        AvaloniaProperty.Register<CommitGraphCanvas, GraphNode>(nameof(Node));

    public GraphNode Node
    {
        get => GetValue(NodeProperty);
        set => SetValue(NodeProperty, value);
    }

    static CommitGraphCanvas()
    {
        AffectsRender<CommitGraphCanvas>(NodeProperty);
    }

    // Lane colors are the Lane1..Lane5 tokens from the active theme dictionary —
    // resolve them from application resources so there is a single source of
    // truth (fallbacks match the Midnight Loom defaults).
    private static readonly (string Key, string Fallback)[] _laneKeys =
    {
        ("Lane1", "#9F9FFC"),
        ("Lane2", "#D0709F"),
        ("Lane3", "#C0EAE3"),
        ("Lane4", "#F2A918"),
        ("Lane5", "#0B87F5")
    };

    private IBrush[]? _laneColorsCache;
    private Pen[]? _lanePensCache;

    private IBrush[] LaneColors => _laneColorsCache ??= ResolveLaneColors();

    // One immutable-per-theme Pen per lane color. Render() used to allocate a fresh Pen per line
    // per frame — on a wide row that is dozens of allocations every scroll tick across every
    // realized row (Hotspot Register H3). Cached pens make a steady scroll allocation-free here.
    private Pen[] LanePens => _lanePensCache ??= BuildLanePens();

    private Pen[] BuildLanePens()
    {
        var colors = LaneColors;
        var pens = new Pen[colors.Length];
        for (int i = 0; i < colors.Length; i++)
            pens[i] = new Pen(colors[i], 2) { LineCap = PenLineCap.Round };
        return pens;
    }

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
        _laneColorsCache = null;
        _lanePensCache = null;
        InvalidateVisual();
    }

    private static IBrush[] ResolveLaneColors()
    {
        var app = Application.Current;
        var brushes = new IBrush[_laneKeys.Length];
        for (int i = 0; i < _laneKeys.Length; i++)
        {
            var (key, fallback) = _laneKeys[i];
            if (app != null
                && app.TryGetResource(key, app.ActualThemeVariant, out var res)
                && res is IBrush brush)
            {
                brushes[i] = brush;
            }
            else
            {
                brushes[i] = SolidColorBrush.Parse(fallback);
            }
        }
        return brushes;
    }

    // Geometry shared with Render(): the dot radius and a comfortable extra slop so a
    // right-click near (not dead-center on) the dot still registers as a Node hit.
    // LaneSpacing is public so the row template can size the graph column to fit the
    // widest lane count currently loaded (see CommitTimelineViewModel.GraphColumnWidth).
    public const double LaneSpacing = 15.0;
    private const double DotRadius = 4.0;
    private const double HitSlop = 6.0;

    /// <summary>
    /// Hit-tests a point (in this control's coordinates) against this row's commit dot using the
    /// pure <see cref="GraphHitTester"/>. Because the graph is one canvas per row, this row is a
    /// single-node graph at RowIndex 0 with no scroll offset. Returned to the timeline so it can
    /// build the correct context menu (T-09). Labels are drawn as chips elsewhere in the row, so
    /// this per-row canvas only ever resolves Node/None.
    /// </summary>
    public GraphHit HitTest(Point p)
    {
        if (Node == null) return new GraphHit(GraphHitKind.None, null, null);
        var tester = new GraphHitTester(
            rowHeight: Bounds.Height <= 0 ? 24.0 : Bounds.Height,
            laneWidth: LaneSpacing,
            nodeRadius: DotRadius,
            hitSlop: HitSlop);
        var nodes = new[] { (RowIndex: 0, LaneIndex: Node.LaneIndex, Sha: Node.CommitSha) };
        return tester.HitTest(p, 0, nodes);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Node == null) return;

        // phase2's cached theme pens (Lane H); the render body reads the LaneSpacing const
        // directly, which already carries main's static graph column behavior (#65/#139).
        var pens = LanePens;
        double dotY = Bounds.Height / 2;

        // Draw Top-Half Lines (coming in from the row above)
        foreach (int lane in Node.IncomingLanes)
        {
            var pen = pens[lane % pens.Length];
            double x = (lane * LaneSpacing) + (LaneSpacing / 2);

            // Draw from top of the row down to the center Y
            context.DrawLine(pen, new Point(x, 0), new Point(x, dotY));
        }

        // Draw Bottom-Half Lines (routing out to parents)
        foreach (var line in Node.OutgoingLines)
        {
            var pen = pens[line.ToLane % pens.Length];

            double fromX = (line.FromLane * LaneSpacing) + (LaneSpacing / 2);
            double toX = (line.ToLane * LaneSpacing) + (LaneSpacing / 2);

            // Draw from the center Y down to the bottom of the row
            context.DrawLine(pen, new Point(fromX, dotY), new Point(toX, Bounds.Height));
        }

        // Draw the Commit Dot exactly on top of the converging lines!
        var dotColor = LaneColors[Node.LaneIndex % LaneColors.Length];
        double dotX = (Node.LaneIndex * LaneSpacing) + (LaneSpacing / 2);

        context.DrawEllipse(dotColor, null, new Point(dotX, dotY), DotRadius, DotRadius);
    }
}
