using System;
using System.Collections.Generic;
using System.Text;

namespace Mainguard.Agents.UI.Controls;

/// <summary>One rendered terminal cell: the grapheme plus its resolved colours.</summary>
internal struct TerminalCell
{
    public string Glyph;
    public int Fg; // ANSI index 0–255, or -1 for the default foreground
    public int Bg; // ANSI index 0–255, or -1 for the default background
    public bool Bold;
}

/// <summary>An opaque, deep-copied snapshot of the visible grid + cursor for the readback hook.</summary>
internal sealed class TerminalGridSnapshot
{
    public required int Cols { get; init; }
    public required int Rows { get; init; }
    public required TerminalCell[][] Cells { get; init; }
    public required int CursorRow { get; init; }
    public required int CursorCol { get; init; }

    /// <summary>The visible text of a row with trailing blanks trimmed (test convenience).</summary>
    public string RowText(int row)
    {
        var sb = new StringBuilder();
        foreach (var cell in Cells[row])
        {
            sb.Append(string.IsNullOrEmpty(cell.Glyph) ? " " : cell.Glyph);
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// A pure, UI-thread-agnostic VT screen model: the interim terminal engine's parser + grid. It is
/// deliberately Avalonia-free so it is unit-testable (scrollback cap, grid readback) and so the
/// P2-04 "feed bytes → read grid" harness can drive it directly through
/// <see cref="TerminalControl"/>'s internal hook. It handles the common escape repertoire
/// (SGR colour, cursor motion, erase, alternate wrapping) well enough for a non-blank, coloured
/// frame; full VT conformance is P2-04/P2-18 territory, not this interim engine.
///
/// <para>Scrollback is a 10k-line circular buffer (oldest lines dropped), so memory stays bounded
/// under a firehose (invariant / edge row 3 on the render side).</para>
/// </summary>
internal sealed class VtScreen
{
    public const int MaxScrollback = 10_000;

    private const char Esc = '\u001b';
    private const char Bel = '\u0007';

    private TerminalCell[][] _screen;
    private readonly LinkedList<TerminalCell[]> _scrollback = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    private int _cursorRow;
    private int _cursorCol;
    private int _fg = -1;
    private int _bg = -1;
    private bool _bold;

    // Parser state.
    private ParseState _state = ParseState.Ground;
    private readonly StringBuilder _params = new();

    // OSC payload capture (OSC 52 clipboard only; DCS/PM/APC strings are consumed uncaptured).
    // Bounded so a malformed endless OSC can't grow memory; an overflowed payload is discarded whole.
    private const int OscCaptureCap = 100_000;
    private readonly StringBuilder _osc = new();
    private bool _oscCapture;
    private bool _oscOverflow;

    /// <summary>Raised when the application requests a clipboard write via OSC 52 (e.g. claude-code's
    /// "c to copy" login screen) — the decoded text to place on the HOST clipboard. Queries (payload
    /// "?") are ignored: answering one would leak the host clipboard to the jailed CLI.</summary>
    public event Action<string>? ClipboardCopyRequested;

    /// <summary>The application enabled bracketed paste (DECSET 2004) — pasted input should be wrapped
    /// in ESC[200~ / ESC[201~ so the CLI treats it as one paste, not typed keystrokes.</summary>
    public bool BracketedPaste { get; private set; }

    public VtScreen(int cols, int rows)
    {
        Cols = Math.Max(1, cols);
        Rows = Math.Max(1, rows);
        _screen = NewScreen(Cols, Rows);
    }

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    /// <summary>Number of lines currently held in scrollback (capped at <see cref="MaxScrollback"/>).</summary>
    public int ScrollbackCount => _scrollback.Count;

    /// <summary>One scrollback line's cells (0 = oldest retained) — the renderer's window into
    /// history when the user wheels up. O(n) walk of the linked list; the renderer touches at most
    /// one viewport of lines per frame, so this stays cheap at the 10k cap.</summary>
    public TerminalCell[] ScrollbackLine(int index)
    {
        if (index < 0 || index >= _scrollback.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var node = _scrollback.First!;
        for (var i = 0; i < index; i++)
        {
            node = node.Next!;
        }

        return node.Value;
    }

    /// <summary>The text of a scrollback line (0 = oldest retained). Test seam.</summary>
    public string ScrollbackLineText(int index)
    {
        if (index < 0 || index >= _scrollback.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var node = _scrollback.First!;
        for (var i = 0; i < index; i++)
        {
            node = node.Next!;
        }

        return RowText(node.Value);
    }

    private enum ParseState
    {
        Ground,
        Esc,
        Csi,
        Osc,
        OscEsc,
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        // Decode UTF-8 incrementally (the streamer already avoids splitting codepoints, but the
        // decoder tolerates a partial tail across Feed calls regardless).
        var bytes = data.ToArray();
        var charCount = _decoder.GetCharCount(bytes, 0, bytes.Length, false);
        var chars = charCount <= 0 ? Array.Empty<char>() : new char[charCount];
        var produced = _decoder.GetChars(bytes, 0, bytes.Length, chars, 0, false);
        for (var i = 0; i < produced; i++)
        {
            Process(chars[i]);
        }
    }

    private void Process(char c)
    {
        switch (_state)
        {
            case ParseState.Ground:
                ProcessGround(c);
                break;
            case ParseState.Esc:
                ProcessEsc(c);
                break;
            case ParseState.Csi:
                ProcessCsi(c);
                break;
            case ParseState.Osc:
                if (c == Bel)
                {
                    CompleteOsc();
                    _state = ParseState.Ground;
                }
                else if (c == Esc)
                {
                    _state = ParseState.OscEsc;
                }
                else if (_oscCapture)
                {
                    if (_osc.Length < OscCaptureCap)
                    {
                        _osc.Append(c);
                    }
                    else
                    {
                        _oscOverflow = true;
                    }
                }

                break;
            case ParseState.OscEsc:
                if (c == '\\')
                {
                    CompleteOsc();
                    _state = ParseState.Ground;
                }
                else
                {
                    // The ESC wasn't a terminator — it belongs to the payload stream; keep consuming.
                    _state = ParseState.Osc;
                }

                break;
        }
    }

    private void ProcessGround(char c)
    {
        switch (c)
        {
            case Esc:
                _state = ParseState.Esc;
                break;
            case '\r':
                _cursorCol = 0;
                break;
            case '\n':
                NewLine();
                break;
            case '\b':
                if (_cursorCol > 0)
                {
                    _cursorCol--;
                }

                break;
            case '\t':
                _cursorCol = Math.Min(Cols - 1, (_cursorCol / 8 + 1) * 8);
                break;
            case Bel:
                break; // bell
            default:
                if (!char.IsControl(c))
                {
                    PutGlyph(c.ToString());
                }

                break;
        }
    }

    private void ProcessEsc(char c)
    {
        switch (c)
        {
            case '[':
                _params.Clear();
                _state = ParseState.Csi;
                break;
            case ']':
                _osc.Clear();
                _oscCapture = true;
                _oscOverflow = false;
                _state = ParseState.Osc;
                break;
            case 'P':
            case 'X':
            case '^':
            case '_':
                _oscCapture = false; // DCS/SOS/PM/APC: consume to ST, never capture
                _state = ParseState.Osc;
                break;
            case 'c':
                Reset();
                _state = ParseState.Ground;
                break;
            default:
                _state = ParseState.Ground;
                break;
        }
    }

    /// <summary>An OSC string just terminated (BEL or ST): act on the ones we support — OSC 52, the
    /// application-driven clipboard write. Everything else (titles, hyperlinks) is consumed silently.</summary>
    private void CompleteOsc()
    {
        var capture = _oscCapture && !_oscOverflow;
        _oscCapture = false;
        if (!capture || _osc.Length < 3)
        {
            return;
        }

        var payload = _osc.ToString();
        _osc.Clear();
        if (!payload.StartsWith("52;", StringComparison.Ordinal))
        {
            return;
        }

        // OSC 52 ; <selection targets> ; <base64 data>. A "?" payload is a clipboard QUERY — never
        // answered (that would hand the host clipboard to the jailed CLI).
        var dataStart = payload.IndexOf(';', 3);
        if (dataStart < 0)
        {
            return;
        }

        var data = payload[(dataStart + 1)..];
        if (data.Length == 0 || data == "?")
        {
            return;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(data));
            if (text.Length > 0)
            {
                ClipboardCopyRequested?.Invoke(text);
            }
        }
        catch (FormatException)
        {
            // Not valid base64 — a malformed or truncated sequence; nothing to copy.
        }
    }

    private void ProcessCsi(char c)
    {
        if ((c >= '0' && c <= '9') || c == ';' || c == '?' || c == ':')
        {
            _params.Append(c);
            return;
        }

        ApplyCsi(c, _params.ToString());
        _state = ParseState.Ground;
    }

    private void ApplyCsi(char final, string paramText)
    {
        var hasPrivate = paramText.StartsWith('?');
        var cleaned = hasPrivate ? paramText[1..] : paramText;
        var parts = cleaned.Length == 0 ? Array.Empty<string>() : cleaned.Split(';');

        int Param(int index, int fallback)
        {
            if (index < parts.Length && int.TryParse(parts[index], out var v))
            {
                return v;
            }

            return fallback;
        }

        switch (final)
        {
            case 'm':
                ApplySgr(parts);
                break;
            case 'H':
            case 'f':
                _cursorRow = Math.Clamp(Param(0, 1) - 1, 0, Rows - 1);
                _cursorCol = Math.Clamp(Param(1, 1) - 1, 0, Cols - 1);
                break;
            case 'A':
                _cursorRow = Math.Max(0, _cursorRow - Math.Max(1, Param(0, 1)));
                break;
            case 'B':
                _cursorRow = Math.Min(Rows - 1, _cursorRow + Math.Max(1, Param(0, 1)));
                break;
            case 'C':
                _cursorCol = Math.Min(Cols - 1, _cursorCol + Math.Max(1, Param(0, 1)));
                break;
            case 'D':
                _cursorCol = Math.Max(0, _cursorCol - Math.Max(1, Param(0, 1)));
                break;
            case 'G':
                _cursorCol = Math.Clamp(Param(0, 1) - 1, 0, Cols - 1);
                break;
            case 'J':
                EraseDisplay(Param(0, 0));
                break;
            case 'K':
                EraseLine(Param(0, 0));
                break;
            case 'h':
            case 'l':
                // DEC private modes: only bracketed paste (2004) is tracked — the paste path wraps
                // pasted text in ESC[200~/201~ when the CLI asked for it. Other modes are ignored
                // by this interim engine (full conformance is P2-18 territory).
                if (hasPrivate && Array.IndexOf(parts, "2004") >= 0)
                {
                    BracketedPaste = final == 'h';
                }

                break;
        }
    }

    private void ApplySgr(string[] parts)
    {
        if (parts.Length == 0)
        {
            ResetSgr();
            return;
        }

        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var code))
            {
                continue;
            }

            switch (code)
            {
                case 0:
                    ResetSgr();
                    break;
                case 1:
                    _bold = true;
                    break;
                case 22:
                    _bold = false;
                    break;
                case 39:
                    _fg = -1;
                    break;
                case 49:
                    _bg = -1;
                    break;
                case 38:
                    i = ReadExtendedColor(parts, i, ref _fg);
                    break;
                case 48:
                    i = ReadExtendedColor(parts, i, ref _bg);
                    break;
                default:
                    if (code >= 30 && code <= 37)
                    {
                        _fg = code - 30;
                    }
                    else if (code >= 40 && code <= 47)
                    {
                        _bg = code - 40;
                    }
                    else if (code >= 90 && code <= 97)
                    {
                        _fg = code - 90 + 8;
                    }
                    else if (code >= 100 && code <= 107)
                    {
                        _bg = code - 100 + 8;
                    }

                    break;
            }
        }
    }

    private static int ReadExtendedColor(string[] parts, int i, ref int slot)
    {
        // 38;5;n (256-colour) or 38;2;r;g;b (truecolour, folded to a 256-index approximation).
        if (i + 1 >= parts.Length || !int.TryParse(parts[i + 1], out var mode))
        {
            return i;
        }

        if (mode == 5 && i + 2 < parts.Length && int.TryParse(parts[i + 2], out var idx))
        {
            slot = Math.Clamp(idx, 0, 255);
            return i + 2;
        }

        if (mode == 2 && i + 4 < parts.Length
            && int.TryParse(parts[i + 2], out var r)
            && int.TryParse(parts[i + 3], out var g)
            && int.TryParse(parts[i + 4], out var b))
        {
            // Fold to the 6×6×6 colour cube (index 16–231) — good enough for the interim renderer.
            var ri = r * 5 / 255;
            var gi = g * 5 / 255;
            var bi = b * 5 / 255;
            slot = 16 + 36 * ri + 6 * gi + bi;
            return i + 4;
        }

        return i;
    }

    private void PutGlyph(string glyph)
    {
        if (_cursorCol >= Cols)
        {
            _cursorCol = 0;
            NewLine();
        }

        _screen[_cursorRow][_cursorCol] = new TerminalCell
        {
            Glyph = glyph,
            Fg = _fg,
            Bg = _bg,
            Bold = _bold,
        };
        _cursorCol++;
    }

    private void NewLine()
    {
        if (_cursorRow < Rows - 1)
        {
            _cursorRow++;
            return;
        }

        // Scroll: the top visible row falls into scrollback (circular cap).
        var top = _screen[0];
        _scrollback.AddLast(top);
        while (_scrollback.Count > MaxScrollback)
        {
            _scrollback.RemoveFirst();
        }

        for (var r = 0; r < Rows - 1; r++)
        {
            _screen[r] = _screen[r + 1];
        }

        _screen[Rows - 1] = NewRow(Cols);
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (var r = _cursorRow + 1; r < Rows; r++)
                {
                    _screen[r] = NewRow(Cols);
                }

                break;
            case 1:
                for (var r = 0; r < _cursorRow; r++)
                {
                    _screen[r] = NewRow(Cols);
                }

                EraseLine(1);
                break;
            default:
                for (var r = 0; r < Rows; r++)
                {
                    _screen[r] = NewRow(Cols);
                }

                _cursorRow = 0;
                _cursorCol = 0;
                break;
        }
    }

    private void EraseLine(int mode)
    {
        var row = _screen[_cursorRow];
        var from = mode == 0 ? _cursorCol : 0;
        var to = mode == 1 ? _cursorCol : Cols - 1;
        for (var c = from; c <= to && c < Cols; c++)
        {
            row[c] = Blank();
        }
    }

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        if (cols == Cols && rows == Rows)
        {
            return;
        }

        var next = NewScreen(cols, rows);
        var copyRows = Math.Min(rows, Rows);
        var copyCols = Math.Min(cols, Cols);
        for (var r = 0; r < copyRows; r++)
        {
            for (var c = 0; c < copyCols; c++)
            {
                next[r][c] = _screen[r][c];
            }
        }

        _screen = next;
        Cols = cols;
        Rows = rows;
        _cursorRow = Math.Min(_cursorRow, rows - 1);
        _cursorCol = Math.Min(_cursorCol, cols - 1);
    }

    public TerminalGridSnapshot ReadGrid()
    {
        var cells = new TerminalCell[Rows][];
        for (var r = 0; r < Rows; r++)
        {
            cells[r] = new TerminalCell[Cols];
            Array.Copy(_screen[r], cells[r], Cols);
        }

        return new TerminalGridSnapshot
        {
            Cols = Cols,
            Rows = Rows,
            Cells = cells,
            CursorRow = _cursorRow,
            CursorCol = _cursorCol,
        };
    }

    /// <summary>Rehydrates the grid from a snapshot produced by <see cref="ReadGrid"/> (opaque restore).</summary>
    public void Restore(TerminalGridSnapshot snapshot)
    {
        Cols = snapshot.Cols;
        Rows = snapshot.Rows;
        _screen = new TerminalCell[Rows][];
        for (var r = 0; r < Rows; r++)
        {
            _screen[r] = new TerminalCell[Cols];
            Array.Copy(snapshot.Cells[r], _screen[r], Cols);
        }

        _cursorRow = Math.Clamp(snapshot.CursorRow, 0, Rows - 1);
        _cursorCol = Math.Clamp(snapshot.CursorCol, 0, Cols - 1);
    }

    /// <summary>Direct visible-grid access for the renderer (no copy).</summary>
    public TerminalCell[][] VisibleRows => _screen;

    public int CursorRow => _cursorRow;
    public int CursorCol => _cursorCol;

    private void Reset()
    {
        ResetSgr();
        for (var r = 0; r < Rows; r++)
        {
            _screen[r] = NewRow(Cols);
        }

        _cursorRow = 0;
        _cursorCol = 0;
        BracketedPaste = false; // ESC c full reset clears private modes
    }

    private void ResetSgr()
    {
        _fg = -1;
        _bg = -1;
        _bold = false;
    }

    private static string RowText(TerminalCell[] row)
    {
        var sb = new StringBuilder();
        foreach (var cell in row)
        {
            sb.Append(string.IsNullOrEmpty(cell.Glyph) ? " " : cell.Glyph);
        }

        return sb.ToString().TrimEnd();
    }

    private static TerminalCell[][] NewScreen(int cols, int rows)
    {
        var screen = new TerminalCell[rows][];
        for (var r = 0; r < rows; r++)
        {
            screen[r] = NewRow(cols);
        }

        return screen;
    }

    private static TerminalCell[] NewRow(int cols)
    {
        var row = new TerminalCell[cols];
        for (var c = 0; c < cols; c++)
        {
            row[c] = Blank();
        }

        return row;
    }

    private static TerminalCell Blank() => new() { Glyph = " ", Fg = -1, Bg = -1, Bold = false };
}
