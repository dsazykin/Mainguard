using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Mainguard.Agents.Terminal;
using Mainguard.UI.Theming;

namespace Mainguard.Agents.UI.Controls;

/// <summary>Raised when the control's layout resolves a new terminal size (columns × rows).</summary>
internal sealed class TerminalResizeEventArgs : EventArgs
{
    public TerminalResizeEventArgs(int cols, int rows)
    {
        Cols = cols;
        Rows = rows;
    }

    public int Cols { get; }
    public int Rows { get; }
}

/// <summary>
/// The interim terminal renderer: a themed monospace cell-grid drawn from a pure
/// <see cref="VtScreen"/>, exposed to the ViewModel <b>only</b> through <see cref="ITerminalView"/>
/// so the P2-18 libvterm engine can replace it without any ViewModel change (invariant 3). No VT
/// parsing or terminal logic lives in the View/code-behind — it all lives in <see cref="VtScreen"/>
/// behind this control.
///
/// <para>Rendering is dirty-flag driven: the control invalidates only when new output arrives or a
/// resize happens, never on an idle timer, so there are no idle redraws. A test-only grid-readback
/// hook (<see cref="ReadGrid"/> / <see cref="FeedSync"/>) is exposed to <c>Mainguard.Tests</c> for the
/// P2-04 "feed bytes → read grid" harness.</para>
/// </summary>
public sealed class TerminalControl : Control, ITerminalView, ITerminalEngineControl
{
    private const double FontSize = 14.0;
    private static readonly FontFamily MonoFamily =
        new("Cascadia Mono,Cascadia Code,Consolas,Menlo,DejaVu Sans Mono,monospace");

    private readonly Typeface _typeface = new(MonoFamily);
    private readonly Dictionary<uint, ImmutableSolidColorBrush> _brushCache = new();

    // awaitGeometry: the engine holds bytes until the first layout pass tells it the pane's real
    // (cols, rows). A rehydrated agent's whole scrollback replays within milliseconds of attach —
    // far ahead of Avalonia's arrange, and ahead of the ViewModel's ~50 ms debounced resize — and
    // this engine has no reflow, so anything parsed at this 80×24 placeholder stays wrapped at 80
    // columns forever. That was the garbled restart-resume replay (ISSUES-LOG #22).
    private VtScreen _screen = new(80, 24, awaitGeometry: true);
    private double _cellWidth;
    private double _cellHeight;

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        MeasureCell();
        // OSC 52: the jailed CLI's own "copy" (claude-code's login screen `c`) lands on the HOST
        // clipboard — the whole point of the sequence; without this the CLI says "copied" into the void.
        _screen.ClipboardCopyRequested += OnClipboardCopyRequested;
    }

    // The engine is dirty-flag driven (see the class doc) so a theme switch — which changes no
    // PTY bytes, no size — never triggers a repaint on its own, leaving the old theme's colors on
    // screen until the next byte/resize/scroll forces one. ResolveBrush/ResolveColor already
    // re-read the current theme's tokens every call; the only missing piece is asking for a
    // repaint when the theme actually changes.
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        _brushCache.Clear();
        InvalidateVisual();
    }

    private void OnClipboardCopyRequested(string text) => _ = SetHostClipboardAsync(text);

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

    /// <inheritdoc />
    public void FeedOutput(ReadOnlyMemory<byte> data)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply(data.Span);
        }
        else
        {
            var copy = data.ToArray();
            Dispatcher.UIThread.Post(() => Apply(copy));
        }
    }

    /// <inheritdoc />
    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0)
        {
            return;
        }

        _screen.Resize(cols, rows);
        InvalidateVisual();
    }

    /// <inheritdoc />
    public object GetStateSnapshot() => _screen.ReadGrid();

    /// <inheritdoc />
    public void RestoreState(object snapshot)
    {
        if (snapshot is TerminalGridSnapshot grid)
        {
            _screen.Restore(grid);
            InvalidateVisual();
        }
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
        // A fresh VtScreen at the current geometry IS the pristine state (screen + scrollback + modes).
        var (cols, rows) = (_screen.Cols, _screen.Rows);
        var awaitGeometry = _screen.GeometryPending; // cleared only by a real layout pass, never by Clear
        _screen.ClipboardCopyRequested -= OnClipboardCopyRequested;
        _screen = new VtScreen(cols, rows, awaitGeometry);
        _screen.ClipboardCopyRequested += OnClipboardCopyRequested;
        InvalidateVisual();
    }

    /// <summary>Test-only readback hook (P2-04): the current visible grid + cursor.</summary>
    internal TerminalGridSnapshot ReadGrid() => _screen.ReadGrid();

    /// <summary>Test-only synchronous feed (P2-04): parse bytes without the UI-thread marshal.</summary>
    internal void FeedSync(ReadOnlySpan<byte> data) => _screen.Feed(data);

    private void Apply(ReadOnlySpan<byte> data)
    {
        _screen.Feed(data);
        InvalidateVisual(); // dirty-flag: redraw only when bytes arrive
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Fill the available space; the renderer maps pixels → cells.
        var w = double.IsInfinity(availableSize.Width) ? _cellWidth * 80 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? _cellHeight * 24 : availableSize.Height;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        UpdateGridSizeFromBounds(finalSize);
        return result;
    }

    private void UpdateGridSizeFromBounds(Size size)
    {
        if (_cellWidth <= 0 || _cellHeight <= 0)
        {
            return;
        }

        var cols = Math.Max(1, (int)(size.Width / _cellWidth));
        var rows = Math.Max(1, (int)(size.Height / _cellHeight));
        if (cols == _screen.Cols && rows == _screen.Rows && !_screen.GeometryPending)
        {
            return;
        }

        // Size the engine from THIS layout pass, not from the ViewModel's debounced round trip: the
        // debounce exists so a drag-resize does not spam the daemon with SIGWINCH, but it also means
        // the engine would spend ~50 ms at the wrong width — which is precisely the window a
        // restart-resume replay lands in. Resizing here also releases any bytes the engine held while
        // its geometry was still unknown. The ViewModel's later Resize(cols, rows) is then a no-op,
        // and its SendResizeAsync still tells the daemon, unchanged.
        _screen.Resize(cols, rows);
        InvalidateVisual();

        UserResized?.Invoke(this, new TerminalResizeEventArgs(cols, rows));
    }

    /// <summary>How many lines above the live screen the view is scrolled (0 = live). Clamped to
    /// the scrollback the VtScreen actually holds; any keystroke snaps back to live.</summary>
    private int _scrollOffset;

    /// <summary>Wheel-scroll through the VtScreen's scrollback ring. The buffer always existed
    /// (10k lines, unit-tested) — the control just never rendered it, so the terminal LOOKED
    /// unscrollable. Three lines per notch, the terminal-emulator convention.</summary>
    protected override void OnPointerWheelChanged(Avalonia.Input.PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var delta = (int)Math.Round(e.Delta.Y * 3);
        if (delta == 0)
        {
            delta = e.Delta.Y > 0 ? 1 : e.Delta.Y < 0 ? -1 : 0;
        }

        var next = Math.Clamp(_scrollOffset + delta, 0, _screen.ScrollbackCount);
        if (next != _scrollOffset)
        {
            _scrollOffset = next;
            InvalidateVisual();
            e.Handled = true;
        }
    }

    public override void Render(DrawingContext context)
    {
        var background = ResolveBrush("TerminalBackground", 0xFF0B0D10);
        var foreground = ResolveBrush("TerminalForeground", 0xFFE6E9EF);
        context.FillRectangle(background, new Rect(Bounds.Size));

        // Scrolled view: the viewport ends `_scrollOffset` lines above the live bottom. History is
        // scrollback lines followed by the live screen rows; render the window that ends there.
        var offset = Math.Min(_scrollOffset, _screen.ScrollbackCount);
        var rows = _screen.VisibleRows;
        for (var r = 0; r < _screen.Rows; r++)
        {
            // The absolute index of the line shown at viewport row r, counted from the oldest
            // scrollback line: total = ScrollbackCount + Rows; the window ends at total - offset.
            var absolute = _screen.ScrollbackCount + r - offset;
            if (absolute < 0)
            {
                continue; // scrolled past the oldest retained line — leave the row blank
            }

            var line = absolute < _screen.ScrollbackCount
                ? _screen.ScrollbackLine(absolute)
                : rows[absolute - _screen.ScrollbackCount];
            RenderRow(context, line, r, foreground, background);
        }

        // The cursor only exists in the live view — drawing it over history would claim the
        // terminal is somewhere it is not. Its absence is also the "you are scrolled" signal.
        if (offset == 0)
        {
            RenderCursor(context);
        }
    }

    private void RenderRow(DrawingContext context, TerminalCell[] row, int rowIndex, IBrush defaultFg, IBrush defaultBg)
    {
        var y = rowIndex * _cellHeight;

        // 1) Backgrounds: fill runs of non-default background.
        for (var c = 0; c < row.Length;)
        {
            var bg = row[c].Bg;
            if (bg < 0)
            {
                c++;
                continue;
            }

            var start = c;
            while (c < row.Length && row[c].Bg == bg)
            {
                c++;
            }

            var brush = BrushForAnsi(bg, defaultBg);
            context.FillRectangle(brush, new Rect(start * _cellWidth, y, (c - start) * _cellWidth, _cellHeight));
        }

        // 2) Glyph runs: group by (fg, bold), skip blanks.
        var sb = new StringBuilder();
        var runStart = 0;
        var runFg = int.MinValue;
        var runBold = false;

        void Flush(int endExclusive)
        {
            if (sb.Length == 0)
            {
                return;
            }

            var brush = BrushForAnsi(runFg, defaultFg);
            var typeface = runBold ? new Typeface(MonoFamily, FontStyle.Normal, FontWeight.Bold) : _typeface;
            var text = new FormattedText(
                sb.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, FontSize, brush);
            context.DrawText(text, new Point(runStart * _cellWidth, y));
            sb.Clear();
        }

        for (var c = 0; c < row.Length; c++)
        {
            var cell = row[c];
            var glyph = string.IsNullOrEmpty(cell.Glyph) ? " " : cell.Glyph;
            if (glyph == " ")
            {
                Flush(c);
                runStart = c + 1;
                continue;
            }

            if (sb.Length == 0)
            {
                runStart = c;
                runFg = cell.Fg;
                runBold = cell.Bold;
            }
            else if (cell.Fg != runFg || cell.Bold != runBold)
            {
                Flush(c);
                runStart = c;
                runFg = cell.Fg;
                runBold = cell.Bold;
            }

            sb.Append(glyph);
        }

        Flush(row.Length);
    }

    private void RenderCursor(DrawingContext context)
    {
        if (_screen.Rows == 0 || _screen.Cols == 0)
        {
            return;
        }

        var cursor = ResolveBrush("TerminalCursor", 0xFF8B8BF5);
        var rect = new Rect(_screen.CursorCol * _cellWidth, _screen.CursorRow * _cellHeight, _cellWidth, _cellHeight);
        context.FillRectangle(new ImmutableSolidColorBrush(((ISolidColorBrush)cursor).Color, 0.5), rect);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Typing means "back to live" — every terminal emulator's convention. The snap happens
        // before the key is even mapped, so input never lands invisibly below a scrolled view.
        if (_scrollOffset != 0)
        {
            _scrollOffset = 0;
            InvalidateVisual();
        }

        // Paste chords are handled BEFORE MapKey (which would otherwise turn Ctrl+V into a raw 0x16).
        // Ctrl+C stays SIGINT — copy OUT of the terminal is the application's job (OSC 52 above).
        if (IsPasteChord(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            _ = PasteFromHostClipboardAsync();
            return;
        }

        var bytes = MapKey(e.Key, e.KeyModifiers);
        if (bytes is not null)
        {
            InputAvailable?.Invoke(bytes);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>Ctrl+V, Ctrl+Shift+V, or Shift+Insert — the three paste chords terminals honour.</summary>
    internal static bool IsPasteChord(Key key, KeyModifiers modifiers) =>
        (key == Key.V && modifiers.HasFlag(KeyModifiers.Control))
        || (key == Key.Insert && modifiers == KeyModifiers.Shift);

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

        var bytes = BuildPasteBytes(text, _screen.BracketedPaste);
        if (bytes is not null)
        {
            InputAvailable?.Invoke(bytes);
        }
    }

    /// <summary>
    /// The bytes a paste sends toward the PTY: newlines normalized to CR (what a terminal's Enter
    /// sends — LF would double-advance in raw mode), wrapped in ESC[200~/ESC[201~ when the CLI
    /// enabled bracketed paste (so multi-line pastes arrive as ONE paste, not typed keystrokes).
    /// Null when there is nothing to paste. Internal for direct testing.
    /// </summary>
    internal static byte[]? BuildPasteBytes(string? text, bool bracketedPaste)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var normalized = text.Replace("\r\n", "\r").Replace('\n', '\r');
        var payload = bracketedPaste ? "\u001b[200~" + normalized + "\u001b[201~" : normalized;
        return Encoding.UTF8.GetBytes(payload);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            InputAvailable?.Invoke(Encoding.UTF8.GetBytes(e.Text));
            e.Handled = true;
            return;
        }

        base.OnTextInput(e);
    }

    /// <summary>Maps a keystroke to the bytes sent toward the PTY. Internal for direct testing.</summary>
    internal static byte[]? MapKey(Key key, KeyModifiers modifiers)
    {
        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);

        // Shift+Tab → CSI Z (back-tab): what CLIs like Claude Code bind to mode switching. The bare
        // switch below would otherwise send a plain 0x09 and the chord silently degrades to Tab.
        if (key == Key.Tab && shift)
        {
            return Esc("[Z");
        }

        // Shift+Enter → CSI-u 13;2u, the sequence Claude Code's /terminal-setup teaches terminals to
        // send for "insert newline without submitting".
        if (key == Key.Enter && shift && !ctrl && !alt)
        {
            return Esc("[13;2u");
        }

        // Ctrl+<letter> → the corresponding C0 control byte (Ctrl+C = 0x03, Ctrl+D = 0x04, …);
        // Alt adds the xterm ESC meta prefix.
        if (ctrl && key >= Key.A && key <= Key.Z)
        {
            return Meta(alt, (byte)(key - Key.A + 1));
        }

        // Ctrl+<punctuation> C0 controls (Ctrl+Space = NUL, Ctrl+[ = ESC, Ctrl+_ = US, …).
        if (ctrl && CtrlPunctuationByte(key) is { } c0)
        {
            return Meta(alt, c0);
        }

        // Ctrl+Backspace → BS (0x08), distinct from plain Backspace's DEL so CLIs can bind word-delete.
        if (ctrl && key == Key.Back)
        {
            return Meta(alt, 0x08);
        }

        // Alt+<letter/digit/Enter/Backspace> → ESC-prefixed byte. Windows swallows Alt+letter as a
        // menu mnemonic (no OnTextInput ever fires), so the meta chord must be encoded here or lost.
        if (alt && !ctrl)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                return new byte[] { 0x1B, (byte)((shift ? 'A' : 'a') + (key - Key.A)) };
            }

            if (key >= Key.D0 && key <= Key.D9 && !shift)
            {
                return new byte[] { 0x1B, (byte)('0' + (key - Key.D0)) };
            }

            switch (key)
            {
                case Key.Enter: return new byte[] { 0x1B, 0x0D }; // ESC CR: newline in Ink-based CLIs
                case Key.Back: return new byte[] { 0x1B, 0x7F };  // ESC DEL: delete word backward
            }
        }

        // Modified arrows/nav/function keys → the xterm CSI modifier parameter (1 + Shift·1 + Alt·2 + Ctrl·4).
        var mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        if (mod > 1 && ModifiedSpecialSequence(key, mod) is { } modified)
        {
            return Esc(modified);
        }

        return key switch
        {
            Key.Enter => new byte[] { 0x0D },
            Key.Back => new byte[] { 0x7F },
            Key.Tab => new byte[] { 0x09 },
            Key.Escape => new byte[] { 0x1B },
            Key.Up => Esc("[A"),
            Key.Down => Esc("[B"),
            Key.Right => Esc("[C"),
            Key.Left => Esc("[D"),
            Key.Home => Esc("[H"),
            Key.End => Esc("[F"),
            Key.PageUp => Esc("[5~"),
            Key.PageDown => Esc("[6~"),
            Key.Delete => Esc("[3~"),
            Key.Insert => Esc("[2~"),
            Key.F1 => Esc("OP"),
            Key.F2 => Esc("OQ"),
            Key.F3 => Esc("OR"),
            Key.F4 => Esc("OS"),
            Key.F5 => Esc("[15~"),
            Key.F6 => Esc("[17~"),
            Key.F7 => Esc("[18~"),
            Key.F8 => Esc("[19~"),
            Key.F9 => Esc("[20~"),
            Key.F10 => Esc("[21~"),
            Key.F11 => Esc("[23~"),
            Key.F12 => Esc("[24~"),
            _ => null,
        };
    }

    /// <summary>ESC-prefix the byte when Alt is held (xterm meta), else the bare byte.</summary>
    private static byte[] Meta(bool alt, byte value)
        => alt ? new byte[] { 0x1B, value } : new[] { value };

    /// <summary>C0 bytes xterm sends for Ctrl+punctuation; null when the chord has no control byte.</summary>
    private static byte? CtrlPunctuationByte(Key key) => key switch
    {
        Key.Space => 0x00,             // Ctrl+Space → NUL
        Key.D2 => 0x00,                // Ctrl+@ → NUL
        Key.OemOpenBrackets => 0x1B,   // Ctrl+[ → ESC
        Key.OemPipe => 0x1C,           // Ctrl+\ → FS
        Key.OemCloseBrackets => 0x1D,  // Ctrl+] → GS
        Key.D6 => 0x1E,                // Ctrl+^ → RS
        Key.OemMinus => 0x1F,          // Ctrl+_ → US
        Key.OemQuestion => 0x1F,       // Ctrl+/ → US (xterm)
        _ => null,
    };

    /// <summary>The xterm CSI tail for a modifier-carrying special key (mod = 1 + Shift·1 + Alt·2 + Ctrl·4).</summary>
    private static string? ModifiedSpecialSequence(Key key, int mod) => key switch
    {
        Key.Up => $"[1;{mod}A",
        Key.Down => $"[1;{mod}B",
        Key.Right => $"[1;{mod}C",
        Key.Left => $"[1;{mod}D",
        Key.Home => $"[1;{mod}H",
        Key.End => $"[1;{mod}F",
        Key.Insert => $"[2;{mod}~",
        Key.Delete => $"[3;{mod}~",
        Key.PageUp => $"[5;{mod}~",
        Key.PageDown => $"[6;{mod}~",
        Key.F1 => $"[1;{mod}P",
        Key.F2 => $"[1;{mod}Q",
        Key.F3 => $"[1;{mod}R",
        Key.F4 => $"[1;{mod}S",
        Key.F5 => $"[15;{mod}~",
        Key.F6 => $"[17;{mod}~",
        Key.F7 => $"[18;{mod}~",
        Key.F8 => $"[19;{mod}~",
        Key.F9 => $"[20;{mod}~",
        Key.F10 => $"[21;{mod}~",
        Key.F11 => $"[23;{mod}~",
        Key.F12 => $"[24;{mod}~",
        _ => null,
    };

    private static byte[] Esc(string tail)
    {
        var bytes = new byte[tail.Length + 1];
        bytes[0] = 0x1B;
        Encoding.ASCII.GetBytes(tail, 0, tail.Length, bytes, 1);
        return bytes;
    }

    private void MeasureCell()
    {
        var probe = new FormattedText(
            "M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, Brushes.White);
        _cellWidth = probe.WidthIncludingTrailingWhitespace > 0 ? probe.WidthIncludingTrailingWhitespace : FontSize * 0.6;
        _cellHeight = probe.Height > 0 ? probe.Height : FontSize * 1.3;
    }

    private IBrush ResolveBrush(string key, uint fallbackArgb)
    {
        if (this.TryFindResource(key, out var value) && value is ISolidColorBrush scb)
        {
            return scb;
        }

        return BrushFor(fallbackArgb);
    }

    private IBrush BrushForAnsi(int index, IBrush fallback)
    {
        if (index < 0)
        {
            return fallback;
        }

        if (index < 16)
        {
            return ResolveBrush("TerminalAnsi" + index, XtermArgb(index));
        }

        return BrushFor(XtermArgb(index));
    }

    private ImmutableSolidColorBrush BrushFor(uint argb)
    {
        if (_brushCache.TryGetValue(argb, out var brush))
        {
            return brush;
        }

        brush = new ImmutableSolidColorBrush(Color.FromUInt32(argb));
        _brushCache[argb] = brush;
        return brush;
    }

    /// <summary>Standard xterm 256-colour value for an index (used for 16–255, and as a token fallback).</summary>
    private static uint XtermArgb(int index)
    {
        // 0–15: base ANSI (approximate; the theme tokens override these for 0–15 at render time).
        ReadOnlySpan<uint> baseColors = new uint[]
        {
            0xFF000000, 0xFFCD3131, 0xFF2AA745, 0xFFC7C43B, 0xFF3B6FD6, 0xFFB454C9, 0xFF2EC5CE, 0xFFD0D0D0,
            0xFF6A737D, 0xFFF14C4C, 0xFF3FC56B, 0xFFEAE05B, 0xFF5C8DF0, 0xFFD67BEA, 0xFF4FE0E8, 0xFFFFFFFF,
        };

        if (index < 16)
        {
            return baseColors[index];
        }

        if (index < 232)
        {
            var i = index - 16;
            var r = i / 36;
            var g = i / 6 % 6;
            var b = i % 6;
            return Rgb(Step(r), Step(g), Step(b));
        }

        var gray = (byte)(8 + (index - 232) * 10);
        return Rgb(gray, gray, gray);
    }

    private static byte Step(int cubeComponent) => cubeComponent == 0 ? (byte)0 : (byte)(55 + cubeComponent * 40);

    private static uint Rgb(byte r, byte g, byte b)
        => 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
}
