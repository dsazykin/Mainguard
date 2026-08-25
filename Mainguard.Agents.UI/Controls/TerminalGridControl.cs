using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Mainguard.Agents.Terminal;
using Mainguard.UI.Theming;
using SkiaSharp;

namespace Mainguard.Agents.UI.Controls;

/// <summary>
/// The P2-18 terminal renderer: a first-party Skia cell grid over the pure <see cref="GridModel"/>
/// (the client mirror of the daemon's libvterm screen). It implements <see cref="ITerminalView"/>
/// so the swap from the interim <see cref="TerminalControl"/> requires zero ViewModel changes —
/// <c>FeedOutput</c> receives serialized <c>TerminalOutput</c> envelopes (grid/clipboard frames)
/// from the gateway instead of raw VT bytes; all parsing already happened daemon-side.
///
/// <para><b>Rendering:</b> damage-driven — the control invalidates only when a frame arrives (or
/// selection/viewport changes), never on an idle timer; glyphs render through a bounded text-blob
/// cache keyed by (glyph, bold, italic) with per-glyph typeface fallback for CJK. Wide glyphs draw
/// across their two columns. The visible window is snapshotted per invalidation so the render
/// thread never races the UI-thread model.</para>
///
/// <para><b>Mouse-selection copy (REQUIRED v1, field promise 2026-07-22):</b> drag selects with
/// cell-accurate highlight (absolute row space, so it survives damage-only redraws and live
/// scrolling); Ctrl+Shift+C and the context menu copy with run-collapse semantics
/// (<see cref="GridSelection.ExtractText"/>). While the app tracks the mouse, Shift overrides —
/// otherwise pointer events encode as mouse reports. Ctrl+C stays SIGINT.</para>
///
/// <para><b>Clipboard bridge:</b> OSC 52 copies arrive as daemon-decoded clipboard frames → host
/// clipboard (queries never reach the client at all); the three paste chords reuse the interim
/// engine's pinned <see cref="TerminalControl.BuildPasteBytes"/> semantics via
/// <see cref="GridInputEncoder"/>. <b>IME:</b> a minimal <see cref="TextInputMethodClient"/>
/// positions composition at the cursor cell and draws the preedit overlay there.</para>
/// </summary>
public sealed class TerminalGridControl : Control, ITerminalView, ITerminalEngineControl
{
    private const double FontSize = 14.0;

    private readonly GridModel _model = new();
    private readonly GridSelection _selection = new();
    private readonly GridGlyphCache _glyphs = new(FontSize);
    private readonly GridImeClient _ime;

    private double _cellWidth;
    private double _cellHeight;
    private int _viewOffset; // rows scrolled back from live (0 = live view)
    private string? _preedit;
    private RenderSnapshot? _renderSnapshot;
    private bool _leftButtonDown;

    public TerminalGridControl()
    {
        Focusable = true;
        ClipToBounds = true;
        _ime = new GridImeClient(this);
        _cellWidth = _glyphs.CellWidth;
        _cellHeight = _glyphs.CellHeight;

        _model.Updated += OnModelUpdated;
        // OSC 52, decoded daemon-side: land it on the HOST clipboard (the whole point — without
        // this the CLI says "copied" into the void). Queries were already dropped at the daemon.
        _model.ClipboardCopyRequested += text => _ = SetHostClipboardAsync(text);

        var copyItem = new MenuItem { Header = "Copy" };
        copyItem.Click += (_, _) => _ = CopySelectionAsync();
        ContextMenu = new ContextMenu { ItemsSource = new[] { copyItem } };
        ContextMenu.Opening += (_, _) => copyItem.IsEnabled = _selection.IsActive;

        AddHandler(TextInputMethodClientRequestedEvent, (_, e) => e.Client = _ime);
    }

    // Same dirty-flag gap as TerminalControl (the other engine behind ITerminalEngineControl): a
    // theme switch changes no PTY bytes/size, so nothing here asks for a repaint on its own even
    // though ResolveColor re-reads the current theme's tokens on every call it does make.
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

    private void OnThemeChanged() => InvalidateVisual();

    /// <inheritdoc />
    public event Action<byte[]>? InputAvailable;

    /// <summary>Raised when the control's own layout produces a new (cols, rows) size.</summary>
    internal event EventHandler<TerminalResizeEventArgs>? UserResized;

    /// <inheritdoc />
    event EventHandler<TerminalResizeEventArgs>? ITerminalEngineControl.UserResized
    {
        add => UserResized += value;
        remove => UserResized -= value;
    }

    /// <summary>Test seam: the pure model this control renders.</summary>
    internal GridModel Model => _model;

    /// <summary>Test seam: the selection model.</summary>
    internal GridSelection Selection => _selection;

    /// <inheritdoc />
    public void FeedOutput(ReadOnlyMemory<byte> data)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _model.ApplyEnvelope(data.Span);
        }
        else
        {
            var copy = data.ToArray();
            Dispatcher.UIThread.Post(() => _model.ApplyEnvelope(copy));
        }
    }

    /// <inheritdoc />
    public void Resize(int cols, int rows)
    {
        // Intentionally nothing to resize locally: the daemon owns the one authoritative grid size
        // (PTY + vterm resize together); the reflowed grid arrives as a snapshot update. The layout
        // event below is what asks the daemon for the new size.
    }

    /// <inheritdoc />
    public object GetStateSnapshot() => _model;

    /// <inheritdoc />
    public void RestoreState(object snapshot)
    {
        // Reattach state comes from the daemon (snapshot + deltas) — nothing client-side to restore.
    }

    /// <inheritdoc />
    public void Clear()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ClearCore();
        }
        else
        {
            Dispatcher.UIThread.Post(ClearCore);
        }
    }

    private void ClearCore()
    {
        _selection.Clear();
        _model.Reset(); // raises Updated(geometryChanged: true) → snap-to-live + full rebuild
    }

    private void OnModelUpdated(bool geometryChanged)
    {
        if (geometryChanged)
        {
            _viewOffset = 0;
        }

        _ime.NotifyCursorMoved();
        RebuildRenderSnapshotAndInvalidate();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? _cellWidth * 80 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? _cellHeight * 24 : availableSize.Height;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        if (_cellWidth > 0 && _cellHeight > 0)
        {
            var cols = Math.Max(1, (int)(finalSize.Width / _cellWidth));
            var rows = Math.Max(1, (int)(finalSize.Height / _cellHeight));
            if (cols != _model.Cols || rows != _model.Rows)
            {
                UserResized?.Invoke(this, new TerminalResizeEventArgs(cols, rows));
            }
        }

        return result;
    }

    // ---- rendering ----

    public override void Render(DrawingContext context)
    {
        var background = ResolveColor("TerminalBackground", 0xFF0B0D10);
        context.FillRectangle(new SolidColorBrush(Color.FromUInt32(background)), new Rect(Bounds.Size));

        _renderSnapshot ??= BuildRenderSnapshot();
        context.Custom(new GridDrawOperation(new Rect(Bounds.Size), _renderSnapshot, _glyphs));
    }

    private void RebuildRenderSnapshotAndInvalidate()
    {
        _renderSnapshot = BuildRenderSnapshot();
        InvalidateVisual();
    }

    /// <summary>Snapshots the visible window (grid + selection + cursor + preedit + palette) on the
    /// UI thread so the render-thread draw op touches no mutable state.</summary>
    private RenderSnapshot BuildRenderSnapshot()
    {
        var rows = _model.Rows;
        var cols = _model.Cols;
        var firstAbsolute = _model.TotalRows - rows - _viewOffset;
        var cells = new GridCellData[rows][];
        var selected = new bool[rows][];
        for (var r = 0; r < rows; r++)
        {
            var absolute = firstAbsolute + r;
            var source = _model.GetAbsoluteRow(absolute);
            var row = new GridCellData[cols];
            var sel = new bool[cols];
            for (var c = 0; c < cols; c++)
            {
                row[c] = c < source.Count ? source[c] : GridCellData.Blank;
                sel[c] = _selection.Contains(absolute, c);
            }

            cells[r] = row;
            selected[r] = sel;
        }

        var cursorScreenRow = _model.CursorRow + _viewOffset >= 0 && _viewOffset == 0 ? _model.CursorRow : -1;
        return new RenderSnapshot(
            cols,
            rows,
            cells,
            selected,
            CursorRow: _model.CursorVisible && _viewOffset == 0 ? _model.CursorRow : -1,
            CursorCol: _model.CursorCol,
            Preedit: _preedit,
            DefaultFg: ResolveColor("TerminalForeground", 0xFFE6E9EF),
            DefaultBg: ResolveColor("TerminalBackground", 0xFF0B0D10),
            CursorColor: ResolveColor("TerminalCursor", 0xFF8B8BF5),
            SelectionColor: ResolveColor("TerminalSelection", 0x668B8BF5),
            AnsiBase: ResolveAnsiBase());
    }

    private uint[] ResolveAnsiBase()
    {
        var palette = new uint[16];
        for (var i = 0; i < 16; i++)
        {
            palette[i] = ResolveColor("TerminalAnsi" + i, GridPalette.XtermArgb(i));
        }

        return palette;
    }

    private uint ResolveColor(string key, uint fallbackArgb)
        => this.TryFindResource(key, out var value) && value is ISolidColorBrush brush
            ? brush.Color.ToUInt32()
            : fallbackArgb;

    // ---- keyboard / paste / copy ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Order matters: copy chord, then paste chords, then the general key map (which would
        // otherwise turn Ctrl+Shift+C into 0x03 and Ctrl+V into 0x16). Ctrl+C stays SIGINT.
        if (e.Key == Key.C && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            _ = CopySelectionAsync();
            return;
        }

        if (TerminalControl.IsPasteChord(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            _ = PasteFromHostClipboardAsync();
            return;
        }

        var bytes = GridInputEncoder.MapKey(e.Key, e.KeyModifiers, _model.CursorKeysApplication);
        if (bytes is not null)
        {
            SnapToLive();
            InputAvailable?.Invoke(bytes);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            SnapToLive();
            InputAvailable?.Invoke(System.Text.Encoding.UTF8.GetBytes(e.Text));
            e.Handled = true;
            return;
        }

        base.OnTextInput(e);
    }

    private async System.Threading.Tasks.Task CopySelectionAsync()
    {
        var text = _selection.ExtractText(_model);
        if (text.Length > 0)
        {
            await SetHostClipboardAsync(text);
        }
    }

    private async System.Threading.Tasks.Task SetHostClipboardAsync(string text)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch (Exception)
        {
            // Clipboard unavailable (headless/locked) — the copy is simply lost, never a crash.
        }
    }

    private async System.Threading.Tasks.Task PasteFromHostClipboardAsync()
    {
        string? text = null;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                text = await clipboard.GetTextAsync();
            }
        }
        catch (Exception)
        {
            // Clipboard unavailable — paste is a no-op, never a crash.
        }

        var bytes = GridInputEncoder.EncodePaste(text, _model.BracketedPaste);
        if (bytes is not null)
        {
            SnapToLive();
            InputAvailable?.Invoke(bytes);
        }
    }

    // ---- pointer: selection vs mouse reporting ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var point = e.GetCurrentPoint(this);
        var (row, col) = CellAt(point.Position);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // While the app tracks the mouse, events are ITS events — Shift is the standard override
        // that reclaims local selection instead of stealing the app's mouse (field contract).
        if (_model.MouseTracking && !shift && _viewOffset == 0)
        {
            var button = point.Properties.IsRightButtonPressed ? 2
                : point.Properties.IsMiddleButtonPressed ? 1 : 0;
            var bytes = GridInputEncoder.EncodeMousePress(button, col + 1, row + 1, _model.MouseSgr);
            if (bytes is not null)
            {
                InputAvailable?.Invoke(bytes);
            }

            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            _leftButtonDown = true;
            _selection.Begin(ToAbsoluteRow(row), col);
            RebuildRenderSnapshotAndInvalidate();
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var (row, col) = CellAt(e.GetPosition(this));

        if (_selection.IsDragging)
        {
            _selection.ExtendTo(ToAbsoluteRow(row), col);
            RebuildRenderSnapshotAndInvalidate();
            e.Handled = true;
            return;
        }

        if (_model.MouseTracking && _model.MouseMode >= 2 && _leftButtonDown
            && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _viewOffset == 0)
        {
            var bytes = GridInputEncoder.EncodeMouseDrag(0, col + 1, row + 1, _model.MouseSgr);
            if (bytes is not null)
            {
                InputAvailable?.Invoke(bytes);
            }
        }

        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var (row, col) = CellAt(e.GetPosition(this));
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (_selection.IsDragging)
        {
            _selection.EndDrag();
            e.Pointer.Capture(null);
            e.Handled = true;
        }
        else if (_model.MouseTracking && !shift && _viewOffset == 0 && _leftButtonDown)
        {
            var bytes = GridInputEncoder.EncodeMouseRelease(0, col + 1, row + 1, _model.MouseSgr);
            if (bytes is not null)
            {
                InputAvailable?.Invoke(bytes);
            }
        }

        _leftButtonDown = false;
        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var up = e.Delta.Y > 0;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (_model.MouseTracking && !shift && _viewOffset == 0)
        {
            var (row, col) = CellAt(e.GetPosition(this));
            var bytes = GridInputEncoder.EncodeWheel(up, col + 1, row + 1, _model.MouseSgr);
            if (bytes is not null)
            {
                InputAvailable?.Invoke(bytes);
            }

            e.Handled = true;
            return;
        }

        if (_model.AltScreen)
        {
            // Alt-screen apps have no scrollback; wheel maps to arrows (the classic terminal shim).
            var arrow = GridInputEncoder.MapKey(up ? Key.Up : Key.Down, KeyModifiers.None, _model.CursorKeysApplication);
            if (arrow is not null)
            {
                for (var i = 0; i < 3; i++)
                {
                    InputAvailable?.Invoke(arrow);
                }
            }

            e.Handled = true;
            return;
        }

        // Primary screen: scroll the local ring viewport.
        var step = up ? 3 : -3;
        var next = Math.Clamp(_viewOffset + step, 0, _model.ScrollbackCount);
        if (next != _viewOffset)
        {
            _viewOffset = next;
            RebuildRenderSnapshotAndInvalidate();
        }

        e.Handled = true;
    }

    private void SnapToLive()
    {
        if (_viewOffset != 0)
        {
            _viewOffset = 0;
            RebuildRenderSnapshotAndInvalidate();
        }
    }

    private (int Row, int Col) CellAt(Point position)
    {
        var col = Math.Clamp((int)(position.X / _cellWidth), 0, Math.Max(0, _model.Cols - 1));
        var row = Math.Clamp((int)(position.Y / _cellHeight), 0, Math.Max(0, _model.Rows - 1));
        return (row, col);
    }

    private int ToAbsoluteRow(int screenRow) => _model.TotalRows - _model.Rows - _viewOffset + screenRow;

    // ---- IME ----

    internal void SetPreeditOverlay(string? preedit)
    {
        _preedit = string.IsNullOrEmpty(preedit) ? null : preedit;
        RebuildRenderSnapshotAndInvalidate();
    }

    internal Rect CursorCellRect() => new(
        _model.CursorCol * _cellWidth, _model.CursorRow * _cellHeight, _cellWidth, _cellHeight);

    /// <summary>Minimal IME client: composition anchors at the cursor cell; the preedit renders as
    /// an overlay; committed text flows through the normal <see cref="OnTextInput"/> path once.</summary>
    private sealed class GridImeClient : TextInputMethodClient
    {
        private readonly TerminalGridControl _owner;

        public GridImeClient(TerminalGridControl owner) => _owner = owner;

        public override Avalonia.Visual TextViewVisual => _owner;

        public override Rect CursorRectangle => _owner.CursorCellRect();

        public override bool SupportsPreedit => true;

        public override bool SupportsSurroundingText => false;

        public override string SurroundingText => string.Empty;

        public override TextSelection Selection
        {
            get => new(0, 0);
            set { }
        }

        public override void SetPreeditText(string? preeditText) => _owner.SetPreeditOverlay(preeditText);

        public void NotifyCursorMoved() => RaiseCursorRectangleChanged();
    }

    // ---- the render-thread draw op ----

    private sealed record RenderSnapshot(
        int Cols,
        int Rows,
        GridCellData[][] Cells,
        bool[][] Selected,
        int CursorRow,
        int CursorCol,
        string? Preedit,
        uint DefaultFg,
        uint DefaultBg,
        uint CursorColor,
        uint SelectionColor,
        uint[] AnsiBase);

    private sealed class GridDrawOperation : ICustomDrawOperation
    {
        private readonly RenderSnapshot _snapshot;
        private readonly GridGlyphCache _glyphs;

        public GridDrawOperation(Rect bounds, RenderSnapshot snapshot, GridGlyphCache glyphs)
        {
            Bounds = bounds;
            _snapshot = snapshot;
            _glyphs = glyphs;
        }

        public Rect Bounds { get; }

        public void Dispose()
        {
        }

        public bool Equals(ICustomDrawOperation? other) => false;

        public bool HitTest(Point p) => Bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null)
            {
                return; // no Skia surface (headless) — the model/tests don't depend on pixels
            }

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;
            var s = _snapshot;
            var cw = (float)_glyphs.CellWidth;
            var ch = (float)_glyphs.CellHeight;

            using var paint = new SKPaint { IsAntialias = true };

            for (var r = 0; r < s.Rows; r++)
            {
                var y = r * ch;
                var row = s.Cells[r];

                // 1) backgrounds (runs of equal bg), then selection wash, then glyphs.
                for (var c = 0; c < s.Cols;)
                {
                    var bg = row[c].Bg;
                    var start = c;
                    while (c < s.Cols && s.Cells[r][c].Bg == bg)
                    {
                        c++;
                    }

                    if (GridModel.KindOf(bg) != GridModel.ColorKind.Default)
                    {
                        paint.Color = ToSkColor(bg, s, foreground: false);
                        canvas.DrawRect(start * cw, y, (c - start) * cw, ch, paint);
                    }
                }

                for (var c = 0; c < s.Cols; c++)
                {
                    if (s.Selected[r][c])
                    {
                        paint.Color = new SKColor(s.SelectionColor);
                        canvas.DrawRect(c * cw, y, cw, ch, paint);
                    }
                }

                for (var c = 0; c < s.Cols; c++)
                {
                    var cell = row[c];
                    if (cell.Width == 0 || !cell.HasContent || cell.Glyph.Length == 0 || cell.Glyph == " ")
                    {
                        continue;
                    }

                    var reverse = (cell.Attrs & 0x8) != 0;
                    var fgEncoded = reverse ? cell.Bg : cell.Fg;
                    var fgColor = GridModel.KindOf(fgEncoded) == GridModel.ColorKind.Default && reverse
                        ? new SKColor(s.DefaultBg)
                        : ToSkColor(fgEncoded, s, foreground: true);

                    _glyphs.Draw(canvas, cell.Glyph, (cell.Attrs & 0x1) != 0, (cell.Attrs & 0x2) != 0,
                        c * cw, y, fgColor);

                    paint.Color = fgColor;
                    if ((cell.Attrs & 0x4) != 0) // underline
                    {
                        canvas.DrawRect(c * cw, y + ch - 1.5f, cw * cell.Width, 1f, paint);
                    }

                    if ((cell.Attrs & 0x10) != 0) // strike
                    {
                        canvas.DrawRect(c * cw, y + ch / 2f, cw * cell.Width, 1f, paint);
                    }
                }
            }

            if (s.CursorRow >= 0 && s.CursorRow < s.Rows)
            {
                paint.Color = new SKColor(s.CursorColor).WithAlpha(128);
                canvas.DrawRect(s.CursorCol * cw, s.CursorRow * ch, cw, ch, paint);
            }

            if (s.Preedit is { Length: > 0 } preedit && s.CursorRow >= 0)
            {
                var x = s.CursorCol * cw;
                var y = s.CursorRow * ch;
                paint.Color = new SKColor(s.DefaultBg);
                canvas.DrawRect(x, y, preedit.Length * cw, ch, paint);
                _glyphs.DrawString(canvas, preedit, x, y, new SKColor(s.DefaultFg));
                paint.Color = new SKColor(s.DefaultFg);
                canvas.DrawRect(x, y + ch - 1.5f, preedit.Length * cw, 1f, paint);
            }
        }

        private static SKColor ToSkColor(uint encoded, RenderSnapshot s, bool foreground)
        {
            switch (GridModel.KindOf(encoded))
            {
                case GridModel.ColorKind.Indexed:
                    var index = GridModel.IndexOf(encoded);
                    return new SKColor(index < 16 ? s.AnsiBase[index] : GridPalette.XtermArgb(index));
                case GridModel.ColorKind.Rgb:
                    var (r, g, b) = GridModel.RgbOf(encoded);
                    return new SKColor(r, g, b);
                default:
                    return new SKColor(foreground ? s.DefaultFg : s.DefaultBg);
            }
        }
    }
}

/// <summary>The standard xterm 256-colour table (shared by the grid renderer).</summary>
internal static class GridPalette
{
    public static uint XtermArgb(int index)
    {
        ReadOnlySpan<uint> baseColors = stackalloc uint[]
        {
            0xFF000000, 0xFFCD3131, 0xFF2AA745, 0xFFC7C43B, 0xFF3B6FD6, 0xFFB454C9, 0xFF2EC5CE, 0xFFD0D0D0,
            0xFF6A737D, 0xFFF14C4C, 0xFF3FC56B, 0xFFEAE05B, 0xFF5C8DF0, 0xFFD67BEA, 0xFF4FE0E8, 0xFFFFFFFF,
        };

        if (index is >= 0 and < 16)
        {
            return baseColors[index];
        }

        if (index is >= 16 and < 232)
        {
            var i = index - 16;
            var r = Step(i / 36);
            var g = Step(i / 6 % 6);
            var b = Step(i % 6);
            return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        }

        if (index is >= 232 and < 256)
        {
            var gray = (byte)(8 + (index - 232) * 10);
            return 0xFF000000u | ((uint)gray << 16) | ((uint)gray << 8) | gray;
        }

        return 0xFFFFFFFF;
    }

    private static byte Step(int cubeComponent) => cubeComponent == 0 ? (byte)0 : (byte)(55 + cubeComponent * 40);
}

/// <summary>
/// Bounded glyph-run cache for the Skia grid: a resolved, style-configured <see cref="SKPaint"/>
/// per (glyph, bold, italic) — the expensive part is typeface resolution (CJK/emoji fall back
/// through <see cref="SKFontManager.MatchCharacter(int)"/> when the mono face lacks the
/// codepoint); Skia's own glyph atlas then caches rasterization. Measure-once cell metrics come
/// from the mono face. Only ever touched from the render thread.
/// </summary>
internal sealed class GridGlyphCache
{
    private const int CacheCap = 8192;

    private static readonly string[] MonoCandidates =
    {
        "Cascadia Mono", "Cascadia Code", "Consolas", "Menlo", "DejaVu Sans Mono", "monospace",
    };

    private readonly float _fontSize;
    private readonly SKTypeface _mono;
    private readonly Dictionary<(string Glyph, bool Bold, bool Italic), SKPaint> _paints = new();

    public GridGlyphCache(double fontSize)
    {
        _fontSize = (float)fontSize;
        _mono = ResolveMono();
        using var probe = new SKPaint { Typeface = _mono, TextSize = _fontSize };
        CellWidth = probe.MeasureText("M");
        var metrics = probe.FontMetrics;
        CellHeight = Math.Ceiling(metrics.Descent - metrics.Ascent + metrics.Leading);
        Baseline = -metrics.Ascent;
    }

    public double CellWidth { get; }

    public double CellHeight { get; }

    public float Baseline { get; }

    public void Draw(SKCanvas canvas, string glyph, bool bold, bool italic, float x, float y, SKColor color)
    {
        var key = (glyph, bold, italic);
        if (!_paints.TryGetValue(key, out var paint))
        {
            if (_paints.Count >= CacheCap)
            {
                foreach (var stale in _paints.Values)
                {
                    stale.Dispose();
                }

                _paints.Clear(); // simple bound: a pathological glyph storm resets the cache
            }

            paint = BuildPaint(glyph, bold, italic);
            _paints[key] = paint;
        }

        paint.Color = color;
        canvas.DrawText(glyph, x, y + Baseline, paint);
    }

    public void DrawString(SKCanvas canvas, string text, float x, float y, SKColor color)
    {
        using var paint = new SKPaint
        {
            Typeface = _mono,
            TextSize = _fontSize,
            IsAntialias = true,
            Color = color,
        };
        canvas.DrawText(text, x, y + Baseline, paint);
    }

    private SKPaint BuildPaint(string glyph, bool bold, bool italic)
    {
        var style = new SKFontStyle(
            bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        var face = SKFontManager.Default.MatchTypeface(_mono, style) ?? _mono;

        // CJK/emoji fallback: when the mono face lacks the lead codepoint, match a system face.
        var codepoint = char.ConvertToUtf32(glyph, 0);
        var glyphIds = face.GetGlyphs(glyph);
        if (glyphIds.Length == 0 || Array.IndexOf(glyphIds, (ushort)0) >= 0)
        {
            face = SKFontManager.Default.MatchCharacter(null, style, null, codepoint) ?? face;
        }

        return new SKPaint
        {
            Typeface = face,
            TextSize = _fontSize,
            IsAntialias = true,
            SubpixelText = true,
        };
    }

    private static SKTypeface ResolveMono()
    {
        foreach (var name in MonoCandidates)
        {
            var face = SKFontManager.Default.MatchFamily(name);
            if (face is not null)
            {
                return face;
            }
        }

        return SKTypeface.Default;
    }
}
