using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.UI.Theming;
using Xunit;

namespace Mainguard.Tests.Headless;

/// <summary>
/// The Control Center's panel sizing, from a live report: "window extended, still cant see the text.
/// plus the panels arent resizable, those boxes should be side scrollable too".
///
/// Three defects, one root: the surface grid was <c>ColumnDefinitions="Auto,*,8,300"</c> — a LITERAL
/// 8px gap where a GridSplitter belonged, and a hard-coded 300px merge queue. Every extra pixel of
/// window width therefore went to the terminal, the boundary had nothing to grab, and the queue's
/// identifiers (a 32-hex agent id, its <c>agent/&lt;id&gt;</c> branch) clipped mid-word.
///
/// The existing ControlCenterRenderHarness renders this same surface in all themes and never
/// caught it, because MockOrchestrator's fixtures are friendly short strings ("Loom-3",
/// "fix/auth-refresh") that fit in 300px. Production entries are not: DaemonBackedOrchestrator
/// projects <c>Name = entry.AgentId</c> and <c>Branch = $"agent/{entry.AgentId}"</c>. So every
/// assertion below runs against REALISTIC id lengths, and the captures show them.
/// </summary>
public class ControlCenterPanelSizingRenderHarness
{
    private static readonly string[] ThemeKeys = { "MidnightLoom", "DaylightLoom", "Graphite", "Atelier" };

    // The owner's own live container id + the branch the daemon derives from it.
    private const string RealAgentId = "1deb19131adb-ef9fe0bd3390433193896eca5e46145e";

    /// <summary>
    /// The seam is a real GridSplitter, it renders with a grabbable width, and dragging it actually
    /// moves the boundary. Against the old literal "8" spacer there is no GridSplitter at all, so this
    /// fails at the first assert.
    /// </summary>
    [AvaloniaFact]
    public void Seam_IsADraggableSplitter_AndDragResizesTheQueue()
    {
        using var _seed = HarnessHygiene.SeedViewAssemblies(new Mainguard.Agents.UI.Editions.ProManifest());
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        using var vm = NewVm();
        var view = new ControlCenterView { DataContext = vm };
        var win = HostWindow(view, 1420);
        win.Show();
        Settle();

        var splitter = ColumnSeam(view);
        Assert.Equal(GridResizeDirection.Columns, splitter.ResizeDirection);
        Assert.True(splitter.Bounds.Width >= 4,
            $"the seam must be wide enough to grab; rendered {splitter.Bounds.Width}px");
        Assert.True(splitter.Bounds.Height > 100,
            $"the seam must span the panel boundary; rendered {splitter.Bounds.Height}px tall");

        var before = QueueRail(view).Bounds.Width;

        // Drag the seam left → the queue must get wider (real headless input, not a property poke).
        var origin = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), win)!.Value;
        win.MouseDown(origin, MouseButton.Left);
        win.MouseMove(new Point(origin.X - 40, origin.Y));
        win.MouseMove(new Point(origin.X - 120, origin.Y));
        win.MouseUp(new Point(origin.X - 120, origin.Y), MouseButton.Left);
        Settle();

        var after = QueueRail(view).Bounds.Width;
        Assert.True(after > before + 50,
            $"dragging the seam 120px left must widen the merge queue; {before} -> {after}");

        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// The literal complaint: "window extended, still cant see the text". A 500px wider window must
    /// reach the queue. The old hard-coded 300px column made this constant.
    /// </summary>
    [AvaloniaFact]
    public void WideningTheWindow_WidensTheMergeQueue()
    {
        using var _seed = HarnessHygiene.SeedViewAssemblies(new Mainguard.Agents.UI.Editions.ProManifest());
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        using var vm = NewVm();
        var view = new ControlCenterView { DataContext = vm };
        var win = HostWindow(view, 1100);
        win.Show();
        Settle();
        var narrow = QueueRail(view).Bounds.Width;

        win.Width = 1600;
        Settle();
        var wide = QueueRail(view).Bounds.Width;

        Assert.True(wide > narrow + 40,
            $"widening the window by 500px must give the merge queue room; {narrow} -> {wide}");
        // …but bounded: the queue must never be allowed to swallow the terminal.
        win.Width = 2600;
        Settle();
        Assert.True(QueueRail(view).Bounds.Width <= 640,
            $"the queue column is capped at 640px; got {QueueRail(view).Bounds.Width}");

        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// The text defect itself, measured off the rendered text layout rather than off a property: no
    /// line in the merge queue may be ellipsized, EXCEPT the agent name — which shares its row with the
    /// state word by design and is therefore required to carry the full value in a tooltip.
    ///
    /// This is the assertion that goes red on the original layout: with the name in a horizontal
    /// StackPanel it takes unbounded width and the state word renders as "Work…".
    /// </summary>
    [AvaloniaFact]
    public void QueueText_IsNeverClipped_AtEveryWidth()
    {
        using var _seed = HarnessHygiene.SeedViewAssemblies(new Mainguard.Agents.UI.Editions.ProManifest());
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        using var vm = NewVm();
        var view = new ControlCenterView { DataContext = vm };
        var win = HostWindow(view, 1420);
        win.Show();

        foreach (var width in new[] { 1000d, 1420d, 1920d })
        {
            win.Width = width;
            Settle();

            var rail = QueueRail(view);
            var names = vm.Queue.Entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            var checkedAny = false;

            foreach (var tb in rail.GetVisualDescendants().OfType<TextBlock>())
            {
                if (string.IsNullOrEmpty(tb.Text) || !tb.IsEffectivelyVisible) continue;

                // (1) GEOMETRIC overflow — the actual reported symptom, and the check that has to come
                // first. A horizontal StackPanel measures its children at INFINITE width, so an
                // over-long child is never "trimmed": TextLayout reports HasCollapsed == false and the
                // TextBlock is simply arranged past the panel edge, where an ancestor clips it. An
                // ellipsis-only assertion therefore passes cleanly on the broken layout and measures
                // nothing. Verified by reverting the fix: this is the assert that goes red.
                var left = tb.TranslatePoint(new Point(0, 0), rail)!.Value.X;
                var overflow = left + tb.Bounds.Width - rail.Bounds.Width;
                Assert.True(overflow <= 0.5,
                    $"@{width}px \"{tb.Text}\" is arranged {overflow:F0}px past the merge queue's right " +
                    $"edge and is clipped there — the reported 'still cant see the text'");

                // (2) ELLIPSIS — permitted for the agent name alone, and only because the full value
                // stays reachable via the tooltip.
                if (tb.TextLayout.TextLines.Any(l => l.HasCollapsed))
                {
                    Assert.True(names.Contains(tb.Text!),
                        $"@{width}px \"{tb.Text}\" was ellipsized; only the agent name may trim");
                    Assert.Equal(tb.Text, ToolTip.GetTip(tb) as string);
                }
                checkedAny = true;
            }

            Assert.True(checkedAny, $"@{width}px no queue text was inspected — the harness measured nothing");
        }

        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// Telemetry was pinned at Height="240" in the same grid — same class of defect. It must now be
    /// resizable, AND hiding it (Conversation Deck) must still collapse the row to nothing rather than
    /// leaving a 240px hole where the panel used to be.
    /// </summary>
    [AvaloniaFact]
    public void TelemetrySeam_Resizes_AndCollapsesWhenHidden()
    {
        using var _seed = HarnessHygiene.SeedViewAssemblies(new Mainguard.Agents.UI.Editions.ProManifest());
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        using var vm = NewVm();
        var view = new ControlCenterView { DataContext = vm };
        var win = HostWindow(view, 1420);
        win.Show();
        vm.FocusCoordinator();
        Settle();
        Assert.True(vm.IsFlightDeck);

        var telemetry = TelemetryHost(view);
        var seam = RowSeam(view);
        Assert.Equal(GridResizeDirection.Rows, seam.ResizeDirection);
        var before = telemetry.Bounds.Height;
        Assert.True(before >= 180, $"telemetry should rest at its MinHeight; got {before}");

        var origin = seam.TranslatePoint(new Point(seam.Bounds.Width / 2, seam.Bounds.Height / 2), win)!.Value;
        win.MouseDown(origin, MouseButton.Left);
        win.MouseMove(new Point(origin.X, origin.Y - 40));
        win.MouseMove(new Point(origin.X, origin.Y - 100));
        win.MouseUp(new Point(origin.X, origin.Y - 100), MouseButton.Left);
        Settle();
        Assert.True(telemetry.Bounds.Height > before + 40,
            $"dragging the telemetry seam up must grow telemetry; {before} -> {telemetry.Bounds.Height}");

        // Conversation Deck hides telemetry. Because the drag above rewrote the row from Auto to a
        // PIXEL length, the row would otherwise keep reserving those pixels — a hole the merge queue
        // never gets back. Measured on the queue (a hidden control keeps stale Bounds, so its own
        // height proves nothing): the queue must reclaim the telemetry's space, and the row seam goes
        // with it.
        var queueBefore = QueueRail(view).Bounds.Height;
        var reclaimed = telemetry.Bounds.Height;
        vm.SetPreset("ConversationDeck");
        Settle();
        Assert.False(vm.IsFlightDeck);
        Assert.False(telemetry.IsEffectivelyVisible);
        Assert.False(seam.IsEffectivelyVisible);
        Assert.True(QueueRail(view).Bounds.Height >= queueBefore + reclaimed,
            $"hiding telemetry must hand its {reclaimed}px back to the queue, not leave a hole; " +
            $"queue {queueBefore} -> {QueueRail(view).Bounds.Height}");

        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// The captures the fix has to be judged on: narrow / default / wide × all themes, with
    /// production-length identifiers in the queue. Daylight Loom is a LIGHT theme — the seam has to be
    /// visible there too, which is why it uses BorderHairline rather than an assumed-dark colour.
    /// </summary>
    [AvaloniaFact]
    public void Capture_QueueSizing_AllThemes_AtThreeWidths()
    {
        using var _seed = HarnessHygiene.SeedViewAssemblies(new Mainguard.Agents.UI.Editions.ProManifest());
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);
            using var vm = NewVm();
            var view = new ControlCenterView { DataContext = vm };
            var win = HostWindow(view, 1100);
            win.Show();
            Settle();
            HarnessHygiene.AssertNoUnresolvedViews(view);

            foreach (var (label, width) in new[] { ("narrow", 1100d), ("default", 1420d), ("wide", 1920d) })
            {
                win.Width = width;
                Settle();
                Assert.True(ColumnSeam(view).Bounds.Width >= 4);
                win.CaptureRenderedFrame()?.Save(
                    Path.Combine(ArtifactsDir(), $"control_center_sizing_{label}_{theme}.png"));
            }
            HarnessHygiene.Teardown(win);
        }
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    /// <summary>
    /// At rest the seam is the same BorderHairline the surrounding panels draw, so what tells the user
    /// it is a HANDLE (the "the panels arent resizable" half of the report) is that it lifts to the
    /// accent under the pointer. Verified as pixels, in every theme — including Daylight Loom, where an
    /// assumed-dark hover colour would simply vanish.
    /// </summary>
    [AvaloniaFact]
    public void Capture_SeamHoverAffordance_IsVisibleInAllThemes()
    {
        using var _seed = HarnessHygiene.SeedViewAssemblies(new Mainguard.Agents.UI.Editions.ProManifest());
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);
            using var vm = NewVm();
            var view = new ControlCenterView { DataContext = vm };
            var win = HostWindow(view, 1420);
            win.Show();
            Settle();

            var seam = ColumnSeam(view);
            var rest = SeamBrushColor(seam);

            var centre = seam.TranslatePoint(new Point(seam.Bounds.Width / 2, seam.Bounds.Height / 2), win)!.Value;
            win.MouseMove(centre);
            Settle();

            Assert.Contains("PanelSeam", seam.Classes);
            Assert.True(seam.IsPointerOver, $"[{theme}] the pointer never reached the seam");
            var hover = SeamBrushColor(seam);
            Assert.NotEqual(rest, hover);
            Assert.True(Contrast(rest, hover) > 24,
                $"[{theme}] the hover state must be visibly different from rest, not a token that " +
                $"happens to resolve near it: {rest} -> {hover}");

            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"control_center_seam_hover_{theme}.png"));
            HarnessHygiene.Teardown(win);
        }
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    /// <summary>
    /// Keyboard reachability: the seam takes focus and its arrow keys move the boundary, so resizing is
    /// not pointer-only. (GridSplitter ships KeyboardIncrement; Focusable is what makes it reachable.)
    /// </summary>
    [AvaloniaFact]
    public void Seam_IsKeyboardResizable()
    {
        using var _seed = HarnessHygiene.SeedViewAssemblies(new Mainguard.Agents.UI.Editions.ProManifest());
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        using var vm = NewVm();
        var view = new ControlCenterView { DataContext = vm };
        var win = HostWindow(view, 1420);
        win.Show();
        Settle();

        var seam = ColumnSeam(view);
        Assert.True(seam.Focusable, "a pointer-only resize handle is not accessible");
        Assert.True(seam.Focus(), "the seam refused focus");
        Settle();

        var before = QueueRail(view).Bounds.Width;
        for (int i = 0; i < 12; i++) win.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Settle();
        var after = QueueRail(view).Bounds.Width;

        Assert.True(after > before,
            $"arrow keys on the focused seam must move the boundary; {before} -> {after}");

        HarnessHygiene.Teardown(win);
    }

    // ---- helpers ------------------------------------------------------------

    private static Avalonia.Media.Color SeamBrushColor(GridSplitter seam) =>
        ((Avalonia.Media.ISolidColorBrush)seam.Background!).Color;

    /// <summary>Crude channel-sum distance — enough to catch "the hover token resolves to the rest token".</summary>
    private static int Contrast(Avalonia.Media.Color a, Avalonia.Media.Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);


    private static GridSplitter ColumnSeam(Visual root) =>
        root.GetVisualDescendants().OfType<GridSplitter>()
            .Single(s => s.ResizeDirection == GridResizeDirection.Columns && s.Classes.Contains("PanelSeam"));

    /// <summary>The right rail's queue/telemetry seam. Named rather than "the only row seam": the
    /// coordinator pane grew one of its own between the plan gate and the terminal, and an unnamed
    /// "Single row splitter" lookup would silently start matching whichever came first.</summary>
    private static GridSplitter RowSeam(Visual root) =>
        root.GetVisualDescendants().OfType<GridSplitter>().Single(s => s.Name == "TelemetrySeam");

    private static QueueRailView QueueRail(Visual root) =>
        root.GetVisualDescendants().OfType<QueueRailView>().Single();

    /// <summary>The card the telemetry panel sits in — the thing the row seam actually resizes.</summary>
    private static Border TelemetryHost(Visual root) =>
        root.GetVisualDescendants().OfType<TelemetryPanelView>().Single()
            .GetVisualAncestors().OfType<Border>().First();

    private static Window HostWindow(Control content, double width)
    {
        var win = new Window { Width = width, Height = 920, Content = content };
        if (Avalonia.Application.Current!.TryGetResource("SurfaceWindow", null, out var bg) && bg is Avalonia.Media.IBrush brush)
            win.Background = brush;
        return win;
    }

    /// <summary>
    /// MockOrchestrator's fixtures are short friendly strings, which is precisely why the existing
    /// five-theme captures never showed this bug. Rewrite the rail's entries to what
    /// DaemonBackedOrchestrator actually projects for a live agent: the 32-hex id as the name and
    /// agent/&lt;id&gt; as the branch. The mock ticks hourly, so nothing refreshes these back.
    /// </summary>
    private static ControlCenterViewModel NewVm()
    {
        var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var vm = new ControlCenterViewModel(mock);
        Assert.True(vm.Queue.Entries.Count >= 4);
        var suffixes = new[] { "a", "b", "c", "d", "e", "f", "0", "1" };
        for (int i = 0; i < vm.Queue.Entries.Count; i++)
        {
            var id = RealAgentId[..^1] + suffixes[i % suffixes.Length];
            vm.Queue.Entries[i].Name = id;
            vm.Queue.Entries[i].Branch = $"agent/{id}";
        }
        return vm;
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
