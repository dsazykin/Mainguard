using System;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.UI.Controls;
using Mainguard.Tests.Headless;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// ISSUES-LOG #22 — a rehydrated coordinator's replayed scrollback must be parsed against the pane's
/// REAL column count, not the engine's constructor placeholder.
///
/// <para>The daemon replays the whole 512 KB ring within milliseconds of attach. Avalonia's arrange
/// pass (which is the only thing that knows the pane's width) runs later, and the ViewModel debounces
/// the resize another ~50 ms behind that — so without the deferral the entire replay is parsed at
/// 80×24 and, because this interim engine has no reflow, stays wrapped at 80 columns for the rest of
/// the session. That is the mid-word wrapping and misplaced fragments reported after the #21 fix made
/// the replay visible at all.</para>
///
/// <para>The pairing matters: #21's regression is "the replay never renders", #22's is "the replay
/// renders wrong". Every test here asserts the content is BOTH present and correctly laid out, so a
/// future change cannot trade one bug for the other.</para>
/// </summary>
public sealed class TerminalReplayGeometryTests
{
    private const string Esc = "\u001b";

    /// <summary>A stand-in for the daemon's replay ring: content recorded by a 120-column session —
    /// a line wider than 80 columns and an absolute cursor position past column 80, the two things an
    /// 80-column parse cannot get right.</summary>
    private static byte[] Recording()
    {
        var sb = new StringBuilder();
        sb.Append(Esc).Append("[2J").Append(Esc).Append("[H");
        sb.Append(Esc).Append("[1m");
        sb.Append("Welcome back Daniel! Approaching your limit, run /model and select a smaller model.");
        sb.Append(Esc).Append("[0m").Append("\r\n");
        sb.Append(Esc).Append("[3;96H").Append("right-edge marker");
        sb.Append(Esc).Append("[5;1H").Append("tail line");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string[] Text(VtScreen screen)
    {
        var grid = screen.ReadGrid();
        return Enumerable.Range(0, grid.Rows).Select(grid.RowText).ToArray();
    }

    private static VtScreen ParsedNatively(int cols, int rows)
    {
        var screen = new VtScreen(cols, rows);
        screen.Feed(Recording());
        return screen;
    }

    [Fact]
    public void ReplayFedBeforeLayout_IsParsedAtTheRealWidth_NotTheConstructorDefault()
    {
        // The engine the control creates: 80×24 placeholder, geometry pending.
        var deferred = new VtScreen(80, 24, awaitGeometry: true);
        Assert.True(deferred.GeometryPending);

        deferred.Feed(Recording()); // the replay burst — before any layout pass has happened
        deferred.Resize(120, 32);   // the first arrange finally reports the pane's real size

        Assert.False(deferred.GeometryPending);

        // Identical to having been parsed at 120 columns all along.
        Assert.Equal(Text(ParsedNatively(120, 32)), Text(deferred));

        // And specifically: no mid-word wrap, and the far-right fragment sits where it was recorded.
        Assert.EndsWith("select a smaller model.", Text(deferred)[0]);
        Assert.Equal(95, Text(deferred)[2].IndexOf("right-edge marker", System.StringComparison.Ordinal));
        Assert.Equal("tail line", Text(deferred)[4]);
    }

    [Fact]
    public void ParsingBeforeTheWidthIsKnown_Garbles_WhichIsWhatTheDeferralPrevents()
    {
        // The pre-fix path, kept as an explicit witness: parse first, resize after. VtScreen has no
        // reflow (Resize copies the top-left region), so the 80-column wrap is permanent.
        var garbled = new VtScreen(80, 24);
        garbled.Feed(Recording());
        garbled.Resize(120, 32);

        var correct = Text(ParsedNatively(120, 32));
        Assert.NotEqual(correct, Text(garbled));
        Assert.DoesNotContain("right-edge marker", Text(garbled)[2]);
    }

    [Fact]
    public void GeometryPending_HoldsOutputUntilLayout_ThenNeverLosesIt()
    {
        // The #21 guard at the engine layer: deferral must delay the replay, never drop it. Nothing
        // renders before layout (the pane has no size yet, so there is nothing to render into) — but
        // the very first resize brings all of it back.
        var screen = new VtScreen(80, 24, awaitGeometry: true);
        screen.Feed(Recording());
        Assert.All(Text(screen), row => Assert.Equal(string.Empty, row));

        screen.Resize(120, 32);
        Assert.Contains("Welcome back Daniel!", Text(screen)[0]);
    }

    [Fact]
    public void ResizeToTheSameSizeAsThePlaceholder_StillReleasesTheReplay()
    {
        // The strand-the-buffer edge: a pane that happens to lay out at exactly 80×24 must still
        // count as "geometry established", or waiting for a size CHANGE would reintroduce #21's
        // blank pane for that one layout.
        var screen = new VtScreen(80, 24, awaitGeometry: true);
        screen.Feed(Recording());
        screen.Resize(80, 24);

        Assert.False(screen.GeometryPending);
        Assert.Equal(Text(ParsedNatively(80, 24)), Text(screen));
    }

    [Fact]
    public void SplitChunks_AreReplayedInOrderWithTheirOriginalBoundaries()
    {
        // The daemon replays the ring chunk by chunk and the incremental UTF-8 decoder carries state
        // across chunks; the held bytes must be re-fed with the identical boundaries, in order.
        var whole = Encoding.UTF8.GetBytes("héllo ✓ wörld — a line with multi-byte glyphs");
        var deferred = new VtScreen(80, 24, awaitGeometry: true);
        for (var i = 0; i < whole.Length; i += 3)
        {
            deferred.Feed(whole.AsSpan(i, System.Math.Min(3, whole.Length - i)));
        }

        deferred.Resize(120, 32);

        var direct = new VtScreen(120, 32);
        direct.Feed(whole);
        Assert.Equal(Text(direct), Text(deferred));
    }

    [Fact]
    public void PendingBytesAreCapped_AndOverflowParsesRatherThanDrops()
    {
        // A pane that never lays out must not buffer without bound. Past the cap the engine stops
        // waiting and parses at whatever size it has — wrapped wrong beats lost.
        var screen = new VtScreen(80, 24, awaitGeometry: true);
        var filler = Encoding.ASCII.GetBytes(new string('x', 64 * 1024) + "\r\n");
        for (var i = 0; i < 40; i++) // 2.5 MB — past the 2 MB cap
        {
            screen.Feed(filler);
        }

        Assert.False(screen.GeometryPending);
        screen.Feed(Encoding.ASCII.GetBytes("\u001b[2J\u001b[Hstill alive"));
        Assert.Equal("still alive", Text(screen)[0]);
    }

    /// <summary>
    /// The whole restart-resume ordering, end to end through the real control and a real Avalonia
    /// layout pass: the replay arrives BEFORE the pane has ever been laid out (exactly what a
    /// rehydrated agent does), and only afterwards does the window show and arrange it. Asserts both
    /// halves at once — the content is there (ISSUES-LOG #21) and it is laid out at the pane's real
    /// width rather than wrapped at the 80-column placeholder (ISSUES-LOG #22).
    /// </summary>
    [AvaloniaFact]
    public void OutputFedBeforeTheFirstLayoutPass_RendersAtTheRealPaneWidth()
    {
        var terminal = new TerminalControl();
        terminal.FeedOutput(Recording()); // no window, no bounds: nothing knows the width yet

        var win = new Window { Content = terminal, Width = 2000, Height = 600 };
        win.Show();
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }

        var grid = terminal.ReadGrid();
        try
        {
            Assert.True(grid.Cols > 112, $"the harness window must be wider than the recording ({grid.Cols} cols)");

            // #21: not blank.
            Assert.Contains("Welcome back Daniel!", grid.RowText(0));

            // #22: the 82-column line is intact on ONE row (at 80 columns it wrapped mid-word onto
            // row 1), and the absolute-position fragment landed at the column it was recorded at.
            Assert.EndsWith("select a smaller model.", grid.RowText(0));
            Assert.Equal(string.Empty, grid.RowText(1));
            Assert.Equal(95, grid.RowText(2).IndexOf("right-edge marker", StringComparison.Ordinal));
        }
        finally
        {
            HarnessHygiene.Teardown(win);
        }
    }
}
