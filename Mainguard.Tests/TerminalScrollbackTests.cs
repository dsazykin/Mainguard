using System.Text;
using Mainguard.Agents.UI.Controls;
using Mainguard.App.Shell.Controls;
using Mainguard.UI.Controls;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-P2-03 §7 / plan §6 row 8 — the interim engine's 10k-line circular scrollback (oldest lines
/// dropped, memory bounded) plus the grid-readback the P2-04 harness drives.
/// </summary>
public sealed class TerminalScrollbackTests
{
    [Fact]
    public void Scrollback_ShouldCapAt10kLines_Circular()
    {
        var screen = new VtScreen(20, 5);

        for (var i = 0; i < 20_000; i++)
        {
            screen.Feed(Encoding.ASCII.GetBytes($"line{i}\r\n"));
        }

        // Capped: the buffer never exceeds 10k lines regardless of how many scrolled off.
        Assert.Equal(VtScreen.MaxScrollback, screen.ScrollbackCount);

        // Circular: the oldest lines were dropped — "line0" is long gone.
        for (var i = 0; i < screen.ScrollbackCount; i++)
        {
            Assert.NotEqual("line0", screen.ScrollbackLineText(i));
        }

        // The most-recent scrolled line is retained (bounded window tracks the tail). With 5 visible
        // rows, "lineN" scrolls "line(N-4)" off the top, so the last line pushed is line19995.
        Assert.Equal("line19995", screen.ScrollbackLineText(screen.ScrollbackCount - 1));
    }

    /// <summary>The renderer's window into history: <c>ScrollbackLine</c> returns the CELLS of a
    /// retained line, matching the text seam index-for-index — the wheel-scroll view (the terminal
    /// held 10k lines of scrollback nobody could ever see) renders through this.</summary>
    [Fact]
    public void ScrollbackLine_ReturnsTheCellsOfTheRetainedLine()
    {
        var screen = new VtScreen(20, 5);
        for (var i = 0; i < 20; i++)
        {
            screen.Feed(Encoding.ASCII.GetBytes($"line{i}\r\n"));
        }

        Assert.True(screen.ScrollbackCount > 0);
        for (var i = 0; i < screen.ScrollbackCount; i++)
        {
            var cells = screen.ScrollbackLine(i);
            var text = string.Concat(cells.Select(c => c.Glyph)).TrimEnd();
            Assert.Equal(screen.ScrollbackLineText(i), text);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => screen.ScrollbackLine(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => screen.ScrollbackLine(screen.ScrollbackCount));
    }

    [Fact]
    public void ReadGrid_ShouldReflectTextAndSgrColour()
    {
        var screen = new VtScreen(40, 4);

        // "hello" default, then bright-red "world".
        screen.Feed(Encoding.ASCII.GetBytes("hello"));
        screen.Feed(Encoding.ASCII.GetBytes("\u001b[31mworld"));

        var grid = screen.ReadGrid();
        Assert.Equal("helloworld", grid.RowText(0));

        // 'h' (col 0) uses the default foreground; 'w' (col 5) is ANSI red (index 1).
        Assert.Equal(-1, grid.Cells[0][0].Fg);
        Assert.Equal(1, grid.Cells[0][5].Fg);
    }

    [Fact]
    public void ReadGrid_ShouldHonourCursorPositioning()
    {
        var screen = new VtScreen(40, 6);

        // CUP to row 3, col 5 (1-based), then write 'X'.
        screen.Feed(Encoding.ASCII.GetBytes("\u001b[3;5HX"));

        var grid = screen.ReadGrid();
        Assert.Equal("X", grid.Cells[2][4].Glyph);
        Assert.Equal(2, grid.CursorRow);
        Assert.Equal(5, grid.CursorCol);
    }

    [Fact]
    public void Feed_MultiByteUtf8_ShouldLandAsSingleGlyph()
    {
        var screen = new VtScreen(20, 2);
        screen.Feed(Encoding.UTF8.GetBytes("€"));

        var grid = screen.ReadGrid();
        Assert.Equal("€", grid.Cells[0][0].Glyph);
    }
}
