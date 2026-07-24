using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Mainguard.Agents.Terminal.Vterm;

/// <summary>
/// The P2-18 server-side terminal engine: one libvterm instance per agent PTY, owned by the
/// session leader's <c>BoundTerminalSession</c>. PTY output bytes are fed in on the existing
/// 16 ms VT-safe cadence; screen callbacks accumulate an ordered change log (scrollback pushes
/// with content → rect scrolls → damage rects, exactly the order libvterm emits them) which
/// <see cref="DrainDelta"/> coalesces into one engine-neutral <see cref="VtermGridDelta"/> per
/// tick — a one-line scroll is a scroll op plus the newly exposed row, never a full grid.
///
/// <para><b>Threading:</b> libvterm is not thread-safe. All members must be called from one
/// thread at a time (the bound session's pump, under its gate); a cheap reentrancy guard throws
/// rather than corrupting native state. <b>Lifetime:</b> libvterm stores the callback-struct
/// pointer, so the struct lives in unmanaged memory (and the delegates are rooted as fields)
/// until <see cref="Dispose"/>.</para>
///
/// <para>Scrollback is a 10k-line ring of resolved rows (absolute-indexed for the lazy
/// <c>GetScrollback</c> fetch); <c>sb_popline</c> is answered from the ring so shrinking/growing
/// the screen round-trips content exactly. Modes libvterm does not surface (bracketed paste,
/// DECCKM, SGR mouse) and OSC 52 clipboard SETs come from <see cref="TerminalModeTracker"/> fed
/// the same bytes — clipboard queries are never answered (the jail must not read the host
/// clipboard).</para>
/// </summary>
public sealed class VtermSession : IDisposable
{
    public const int MaxScrollback = 10_000;

    /// <summary>Firehose guard: at most this many pushed-with-content rows ride one drained tick;
    /// beyond it the OLDEST pushes are dropped (the daemon ring keeps them; the client ring marks a
    /// gap). Keeps the per-tick payload bounded under <c>cat</c>-style output.</summary>
    public const int MaxPushedRowsPerDrain = 240;

    private readonly TerminalModeTracker _tracker = new();
    private readonly LinkedList<VtermCell[]> _scrollback = new();
    private long _scrollbackDropped;

    private IntPtr _vt;
    private IntPtr _screen;
    private IntPtr _callbacksMem;
    private VtermNative.VTermScreenCallbacks _callbacks; // roots the delegates for the native side

    private int _cols;
    private int _rows;

    // Per-tick accumulation (callback order preserved).
    private readonly List<VtermGridOp> _ops = new();
    private readonly List<VtermCell[]> _pushed = new();
    private bool _pushedTruncated;
    private readonly HashSet<int> _damagedRows = new();
    private bool _sawPartialWidthScroll;
    private bool _snapshotPending;

    // Cursor + prop state (from callbacks).
    private int _cursorRow;
    private int _cursorCol;
    private bool _cursorVisible = true;
    private bool _cursorMovedSinceDrain;
    private bool _altScreen;
    private int _mouseMode;
    private VtermModes _lastDrainedModes;
    private bool _modesEverDrained;

    private int _entered; // reentrancy guard — libvterm is single-threaded by contract
    private bool _disposed;

    /// <summary>The application requested a host-clipboard write via OSC 52 (SETs only — queries
    /// are dropped by the tracker and never answered).</summary>
    public event Action<string>? ClipboardCopyRequested;

    /// <summary>Whether the native libvterm can be loaded here — the daemon's engine flag degrades
    /// to the interim engine when it cannot (e.g. Windows local-dev; the library is Linux/daemon-only
    /// by design). Never throws.</summary>
    public static bool IsSupported => VtermNative.IsAvailable;

    /// <summary>
    /// MG-22 — the administrative ceiling on either grid dimension, applied immediately before the
    /// native call. Upstream libvterm 0.3.3's <c>vterm_set_size</c>/<c>alloc_buffer</c> allocates
    /// <c>sizeof(ScreenCell) * rows * cols</c> with <b>no overflow or upper-bound check</b> and
    /// dereferences the result unconditionally, so a caller-supplied dimension anywhere in
    /// <c>[1, 2^31-1]</c> (the proto carries <c>uint32</c>, cast to <c>int</c>) reached it after passing
    /// every managed check — only <c>&lt;= 0</c> was rejected. An inflated <c>cols</c> also multiplies the
    /// 10 000-line scrollback ring's footprint. 1000×1000 is far beyond any real display (a 4K monitor at
    /// a tiny font is ~500 columns) while bounding the allocation.
    /// </summary>
    public const int MaxDimension = 1000;

    /// <summary>Clamps a grid dimension into <c>[1, <see cref="MaxDimension"/>]</c>. Public because
    /// callers that drive the PTY and this grid together must clamp identically (see
    /// <c>BoundTerminalSession.Resize</c>) or the two sizes diverge.</summary>
    public static int ClampDimension(int value) => value < 1 ? 1 : (value > MaxDimension ? MaxDimension : value);

    public VtermSession(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0)
        {
            throw new ArgumentOutOfRangeException(cols <= 0 ? nameof(cols) : nameof(rows));
        }

        // Oversized geometry is clamped rather than rejected: construction must not fail on a large
        // request, but nothing above the ceiling may reach the native allocator.
        cols = ClampDimension(cols);
        rows = ClampDimension(rows);

        VtermNative.EnsureResolver();
        _cols = cols;
        _rows = rows;
        _tracker.ClipboardCopyRequested += text => ClipboardCopyRequested?.Invoke(text);

        _vt = VtermNative.vterm_new(rows, cols);
        if (_vt == IntPtr.Zero)
        {
            throw new InvalidOperationException("vterm_new failed.");
        }

        VtermNative.vterm_set_utf8(_vt, 1);
        _screen = VtermNative.vterm_obtain_screen(_vt);

        _callbacks = new VtermNative.VTermScreenCallbacks
        {
            Damage = OnDamage,
            MoveRect = OnMoveRect,
            MoveCursor = OnMoveCursor,
            SetTermProp = OnSetTermProp,
            SbPushLine = OnSbPushLine,
            SbPopLine = OnSbPopLine,
        };
        _callbacksMem = Marshal.AllocHGlobal(Marshal.SizeOf<VtermNative.VTermScreenCallbacks>());
        Marshal.StructureToPtr(_callbacks, _callbacksMem, false);
        VtermNative.vterm_screen_set_callbacks(_screen, _callbacksMem, IntPtr.Zero);

        VtermNative.vterm_screen_enable_altscreen(_screen, 1);
        VtermNative.vterm_screen_set_damage_merge(_screen, VtermNative.DamageScroll);
        VtermNative.vterm_screen_reset(_screen, 1);

        // The reset's full-screen damage is start-of-life noise; the first update is a snapshot.
        ClearTickState();
        _snapshotPending = true;
    }

    public int Cols => _cols;

    public int Rows => _rows;

    /// <summary>Lines currently retained in the scrollback ring.</summary>
    public int ScrollbackCount
    {
        get
        {
            lock (_scrollback)
            {
                return _scrollback.Count;
            }
        }
    }

    /// <summary>Absolute index of the oldest retained scrollback line (lines dropped by the cap).</summary>
    public long ScrollbackStart
    {
        get
        {
            lock (_scrollback)
            {
                return _scrollbackDropped;
            }
        }
    }

    /// <summary>True when the next update must be a full snapshot (initial state, or a resize
    /// reflowed the grid). Cleared by <see cref="DrainDelta"/>.</summary>
    public bool SnapshotPending => _snapshotPending;

    /// <summary>Current mode state (vterm props + the DECSET tracker).</summary>
    public VtermModes Modes => new(
        _altScreen, _tracker.BracketedPaste, _tracker.CursorKeysApplication, _mouseMode, _tracker.MouseSgr);

    /// <summary>Feeds PTY output bytes into the parser (and the mode tracker).</summary>
    public unsafe void Feed(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        using var _ = Enter();
        _tracker.Feed(data);
        fixed (byte* p = data)
        {
            VtermNative.vterm_input_write(_vt, p, (UIntPtr)data.Length);
        }
    }

    /// <summary>
    /// Resizes the terminal (the vterm half of the one-authoritative-size rule — the caller
    /// resizes the PTY in the same breath). Reflow pushes/pops scrollback as needed; the next
    /// drained update is a full snapshot so the client never reinterprets old rows at a new width.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0)
        {
            return;
        }

        // MG-22: clamp before the native call — see MaxDimension. Done after the <=0 guard and before
        // the no-op comparison so a repeated oversized request still settles at the ceiling exactly once.
        cols = ClampDimension(cols);
        rows = ClampDimension(rows);

        if (cols == _cols && rows == _rows)
        {
            return;
        }

        using var _ = Enter();
        _cols = cols;
        _rows = rows;
        VtermNative.vterm_set_size(_vt, rows, cols);
        _snapshotPending = true;
    }

    /// <summary>
    /// Flushes pending damage and returns everything that changed since the last drain as one
    /// coalesced tick. When <see cref="SnapshotPending"/> was set the structural log is meaningless
    /// (reflow); the caller should send <see cref="Snapshot"/> instead — this method still clears
    /// the tick state and the flag.
    /// </summary>
    public VtermGridDelta DrainDelta()
    {
        using var _ = Enter();
        VtermNative.vterm_screen_flush_damage(_screen);

        // The rare libvterm merge branch that neither translates nor flushes damage across a
        // partial-width scroll leaves stale coordinates; repaint the scrolled rows too (cheap —
        // such rects are usually a single row from ICH/DCH). Full-width vertical scrolls (the
        // steady-scroll hot path) never take this branch, preserving O(changed rows) traffic.
        if (_sawPartialWidthScroll && _damagedRows.Count > 0)
        {
            foreach (var op in _ops)
            {
                if (op is VtermGridOp.Scroll s && (s.Left > 0 || s.Right < _cols))
                {
                    for (var r = Math.Max(0, s.Top); r < Math.Min(_rows, s.Bottom); r++)
                    {
                        _damagedRows.Add(r);
                    }
                }
            }
        }

        var damaged = new List<(int Row, VtermCell[] Cells)>(_damagedRows.Count);
        foreach (var row in _damagedRows.Where(r => r >= 0 && r < _rows).OrderBy(r => r))
        {
            damaged.Add((row, ReadRow(row)));
        }

        var modes = Modes;
        var modesChanged = !_modesEverDrained || modes != _lastDrainedModes;
        _lastDrainedModes = modes;
        _modesEverDrained = true;

        var delta = new VtermGridDelta
        {
            Cols = _cols,
            Rows = _rows,
            PushedRows = _pushed.ToArray(),
            PushedTruncated = _pushedTruncated,
            Ops = _ops.ToArray(),
            DamagedRows = damaged,
            CursorRow = _cursorRow,
            CursorCol = _cursorCol,
            CursorVisible = _cursorVisible,
            CursorMoved = _cursorMovedSinceDrain,
            Modes = modes,
            ModesChanged = modesChanged,
        };

        ClearTickState();
        _snapshotPending = false;
        return delta;
    }

    /// <summary>A deep snapshot of the visible grid + cursor + modes (attach / post-resize).</summary>
    public VtermGrid Snapshot()
    {
        using var _ = Enter();
        VtermNative.vterm_screen_flush_damage(_screen);
        var cells = new VtermCell[_rows][];
        for (var r = 0; r < _rows; r++)
        {
            cells[r] = ReadRow(r);
        }

        return new VtermGrid(_cols, _rows, cells, _cursorRow, _cursorCol, _cursorVisible, Modes);
    }

    /// <summary>Scrollback rows for the lazy fetch: absolute-indexed, oldest first, clamped to what
    /// is retained. Thread-safe (serves the GetScrollback RPC without entering the vterm thread).</summary>
    public IReadOnlyList<(long Index, VtermCell[] Cells)> GetScrollback(long start, int count)
    {
        var result = new List<(long, VtermCell[])>();
        if (count <= 0)
        {
            return result;
        }

        lock (_scrollback)
        {
            var first = _scrollbackDropped;
            var index = first;
            foreach (var row in _scrollback)
            {
                if (index >= start && result.Count < count)
                {
                    result.Add((index, row));
                }

                index++;
                if (result.Count >= count)
                {
                    break;
                }
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        using (Enter())
        {
            _disposed = true;
            if (_vt != IntPtr.Zero)
            {
                VtermNative.vterm_free(_vt);
                _vt = IntPtr.Zero;
                _screen = IntPtr.Zero;
            }

            if (_callbacksMem != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_callbacksMem);
                _callbacksMem = IntPtr.Zero;
            }
        }
    }

    // ---- native callbacks (fire synchronously inside vterm_input_write / flush / set_size) ----

    private int OnDamage(VtermNative.VTermRect rect, IntPtr user)
    {
        for (var r = rect.StartRow; r < rect.EndRow; r++)
        {
            _damagedRows.Add(r);
        }

        return 1;
    }

    private int OnMoveRect(VtermNative.VTermRect dest, VtermNative.VTermRect src, IntPtr user)
    {
        var op = new VtermGridOp.Scroll(
            Top: Math.Min(dest.StartRow, src.StartRow),
            Bottom: Math.Max(dest.EndRow, src.EndRow),
            Left: Math.Min(dest.StartCol, src.StartCol),
            Right: Math.Max(dest.EndCol, src.EndCol),
            RowDelta: dest.StartRow - src.StartRow,
            ColDelta: dest.StartCol - src.StartCol);
        _ops.Add(op);
        if (op.Left > 0 || op.Right < _cols)
        {
            _sawPartialWidthScroll = true;
        }

        return 1;
    }

    private int OnMoveCursor(VtermNative.VTermPos pos, VtermNative.VTermPos old, int visible, IntPtr user)
    {
        _cursorRow = pos.Row;
        _cursorCol = pos.Col;
        _cursorMovedSinceDrain = true;
        return 1;
    }

    private int OnSetTermProp(int prop, IntPtr value, IntPtr user)
    {
        switch (prop)
        {
            case VtermNative.PropCursorVisible:
                _cursorVisible = Marshal.ReadInt32(value) != 0;
                break;
            case VtermNative.PropAltScreen:
                _altScreen = Marshal.ReadInt32(value) != 0;
                break;
            case VtermNative.PropMouse:
                _mouseMode = Marshal.ReadInt32(value);
                break;
        }

        return 1;
    }

    private unsafe int OnSbPushLine(int cols, IntPtr cellsPtr, IntPtr user)
    {
        // The buffer is vterm-owned and valid only during the callback — copy now.
        var row = new VtermCell[cols];
        var native = (VtermNative.VTermScreenCell*)cellsPtr;
        for (var c = 0; c < cols; c++)
        {
            row[c] = Convert(ref native[c]);
        }

        lock (_scrollback)
        {
            _scrollback.AddLast(row);
            while (_scrollback.Count > MaxScrollback)
            {
                _scrollback.RemoveFirst();
                _scrollbackDropped++;
            }
        }

        _pushed.Add(row);
        if (_pushed.Count > MaxPushedRowsPerDrain)
        {
            _pushed.RemoveAt(0); // keep the newest — ring continuity at the tail matters most
            _pushedTruncated = true;
        }

        return 1;
    }

    private unsafe int OnSbPopLine(int cols, IntPtr cellsPtr, IntPtr user)
    {
        VtermCell[]? row;
        lock (_scrollback)
        {
            if (_scrollback.Count == 0)
            {
                return 0;
            }

            row = _scrollback.Last!.Value;
            _scrollback.RemoveLast();
        }

        var native = (VtermNative.VTermScreenCell*)cellsPtr;
        for (var c = 0; c < cols; c++)
        {
            var cell = c < row.Length ? row[c] : VtermCell.Blank;
            WriteNative(ref native[c], cell);
        }

        // Coalesce consecutive pops (resize reflow pops one line per row grown).
        if (_ops.Count > 0 && _ops[^1] is VtermGridOp.PopRows pop)
        {
            _ops[^1] = new VtermGridOp.PopRows(pop.Count + 1);
        }
        else
        {
            _ops.Add(new VtermGridOp.PopRows(1));
        }

        return 1;
    }

    // ---- cell conversion ----

    private unsafe VtermCell[] ReadRow(int row)
    {
        var cells = new VtermCell[_cols];
        for (var c = 0; c < _cols; c++)
        {
            VtermNative.vterm_screen_get_cell(
                _screen, new VtermNative.VTermPos { Row = row, Col = c }, out var native);
            cells[c] = Convert(ref native);
        }

        return cells;
    }

    private static unsafe VtermCell Convert(ref VtermNative.VTermScreenCell native)
    {
        var fg = VtermNative.DecodeColor(native.Fg, isForeground: true);
        var bg = VtermNative.DecodeColor(native.Bg, isForeground: false);
        var attrs = VtermCellAttrs.None;
        if (native.Bold)
        {
            attrs |= VtermCellAttrs.Bold;
        }

        if (native.Italic)
        {
            attrs |= VtermCellAttrs.Italic;
        }

        if (native.Underline)
        {
            attrs |= VtermCellAttrs.Underline;
        }

        if (native.Reverse)
        {
            attrs |= VtermCellAttrs.Reverse;
        }

        if (native.Strike)
        {
            attrs |= VtermCellAttrs.Strike;
        }

        var first = native.Chars[0];
        if (first == VtermNative.WideSpacerChar)
        {
            return new VtermCell(string.Empty, false, fg, bg, attrs, Width: 0);
        }

        if (first == 0)
        {
            return new VtermCell(string.Empty, false, fg, bg, attrs, Width: 1);
        }

        var sb = new StringBuilder(2);
        fixed (uint* chars = native.Chars)
        {
            for (var i = 0; i < VtermNative.MaxCharsPerCell && chars[i] != 0; i++)
            {
                sb.Append(char.ConvertFromUtf32((int)chars[i]));
            }
        }

        var width = native.Width == 2 ? (byte)2 : (byte)1;
        return new VtermCell(sb.ToString(), true, fg, bg, attrs, width);
    }

    private static unsafe void WriteNative(ref VtermNative.VTermScreenCell native, VtermCell cell)
    {
        native = default;
        fixed (uint* chars = native.Chars)
        {
            if (cell.Width == 0)
            {
                chars[0] = VtermNative.WideSpacerChar; // get_cell's spacer marker, mirrored back
            }
            else if (cell.HasContent && cell.Text.Length > 0)
            {
                var i = 0;
                foreach (var rune in cell.Text.EnumerateRunes())
                {
                    if (i >= VtermNative.MaxCharsPerCell)
                    {
                        break;
                    }

                    chars[i++] = (uint)rune.Value;
                }

                if (i < VtermNative.MaxCharsPerCell)
                {
                    chars[i] = 0;
                }
            }
        }

        native.Width = cell.Width == 2 ? (byte)2 : (byte)1;
        var attrs = 0u;
        if (cell.Attrs.HasFlag(VtermCellAttrs.Bold))
        {
            attrs |= 0x1;
        }

        if (cell.Attrs.HasFlag(VtermCellAttrs.Underline))
        {
            attrs |= 0x2; // underline "single" in the 2-bit field
        }

        if (cell.Attrs.HasFlag(VtermCellAttrs.Italic))
        {
            attrs |= 0x8;
        }

        if (cell.Attrs.HasFlag(VtermCellAttrs.Reverse))
        {
            attrs |= 0x20;
        }

        if (cell.Attrs.HasFlag(VtermCellAttrs.Strike))
        {
            attrs |= 0x80;
        }

        native.Attrs = attrs;
        native.Fg = VtermNative.EncodeColor(cell.Fg, isForeground: true);
        native.Bg = VtermNative.EncodeColor(cell.Bg, isForeground: false);
    }

    private void ClearTickState()
    {
        _ops.Clear();
        _pushed.Clear();
        _pushedTruncated = false;
        _damagedRows.Clear();
        _sawPartialWidthScroll = false;
        _cursorMovedSinceDrain = false;
    }

    private Guard Enter()
    {
        ObjectDisposedException.ThrowIf(_disposed && _vt == IntPtr.Zero, this);
        if (Interlocked.CompareExchange(ref _entered, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "VtermSession is single-threaded (one session = one pump thread); concurrent entry detected.");
        }

        return new Guard(this);
    }

    private readonly struct Guard : IDisposable
    {
        private readonly VtermSession _session;

        public Guard(VtermSession session) => _session = session;

        public void Dispose() => Interlocked.Exchange(ref _session._entered, 0);
    }
}
