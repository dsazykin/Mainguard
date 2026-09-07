using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Mainguard.Agents.UI.Controls;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The interim engine's CSI parameter accumulation is BOUNDED.
///
/// <para><b>Why.</b> <c>VtScreen</c> is the parser every terminal pane runs under the default engine
/// (<c>TerminalEngineKind.Interim</c>), and it parses on the UI thread. OSC capture has been capped at
/// 100k since it was written; CSI was not capped at all — <c>ESC [</c> followed by digits and semicolons
/// appended to a <see cref="StringBuilder"/> until a final byte arrived, and the applier then ran
/// <c>Split(';')</c> over the whole thing, allocating an array proportional to it. Megabytes of digits
/// from a jailed CLI therefore became megabytes of managed memory plus a proportional allocation, in the
/// pane's own render path. It needs no malice: a corrupted stream, or a binary file catted into the
/// terminal, produces exactly those bytes.</para>
///
/// <para>The libvterm engine parses in the daemon and is bounded there, which is why only this path
/// needed the guard — see <c>TerminalEngineKind.Interim</c>'s remarks.</para>
/// </summary>
public sealed class VtScreenCsiBoundTests
{
    private const string Esc = "\u001b";

    private static void Feed(VtScreen screen, string text) => screen.Feed(Encoding.ASCII.GetBytes(text));

    private static string Row(VtScreen screen, int row) => screen.ReadGrid().RowText(row);

    /// <summary>An unterminated CSI carrying many times the cap in parameter bytes leaves nothing behind:
    /// not on screen (it is a control sequence, malformed or not — dropping one is not the same as
    /// printing it) and not held for later. The parser then recovers and honours the next sequence.</summary>
    [Fact]
    public void Csi_WithFarMoreParametersThanTheCap_RetainsNothing()
    {
        var screen = new VtScreen(80, 24);

        var flood = new StringBuilder(Esc + "[");
        flood.Append('1', VtScreen.CsiParamCapChars * 8);
        Feed(screen, flood.ToString());

        Assert.All(Enumerable.Range(0, screen.Rows), row => Assert.Equal("", Row(screen, row)));

        Feed(screen, "m");                  // the final byte that ends the flooded sequence
        Feed(screen, Esc + "[2;3Hok");
        Assert.Contains("ok", Row(screen, 1), StringComparison.Ordinal);
    }

    /// <summary>
    /// An overflowed sequence is ABANDONED, never applied on the parameters that happened to fit.
    ///
    /// <para>A CSI whose parameter list was truncated is not the sequence the application sent, and
    /// executing a guess at it would move the cursor or repaint the screen on made-up numbers — a
    /// terminal quietly drawing something nobody asked for. Here the flood ends in <c>H</c> (cursor
    /// position): if the parameters that fit were applied, the cursor would move and the next glyph would
    /// land somewhere else.</para>
    /// </summary>
    [Fact]
    public void OverflowedCsi_IsAbandoned_RatherThanAppliedOnTruncatedParameters()
    {
        var screen = new VtScreen(80, 24);

        Feed(screen, Esc + "[5;10H");  // park the cursor on row 5, column 10 (1-based)
        Feed(screen, "here");

        var flood = new StringBuilder(Esc + "[");
        for (var i = 0; i < VtScreen.CsiParamCapChars; i++)
        {
            flood.Append("9;");        // well past the cap
        }

        flood.Append('H');
        Feed(screen, flood.ToString());

        // The abandoned sequence moved nothing: writing again continues where "here" left off.
        Feed(screen, "!");
        Assert.Contains("here!", Row(screen, 4), StringComparison.Ordinal);
    }

    /// <summary>A real-world SGR run — dozens of parameters — is nowhere near the cap and must be applied
    /// exactly as before. The bound exists to stop a pathological stream, not to clip legitimate ones.</summary>
    [Fact]
    public void OrdinarySgrRun_IsWellUnderTheCap_AndStillApplies()
    {
        var screen = new VtScreen(80, 24);

        var sgr = Esc + "[" + string.Join(';', Enumerable.Repeat("0", 64)) + ";31m";
        Assert.True(
            sgr.Length < VtScreen.CsiParamCapChars,
            "the guard must not be tight enough to clip real terminal traffic");

        Feed(screen, sgr + "red");

        Assert.Contains("red", Row(screen, 0), StringComparison.Ordinal);
    }

    /// <summary>The cost half of the same property: megabytes of CSI digits must not take
    /// megabyte-proportional work on the UI thread. Deliberately loose — a guard against unbounded growth,
    /// not a benchmark.</summary>
    [Fact]
    public void MegabytesOfCsiDigits_ParseInBoundedTime()
    {
        var screen = new VtScreen(80, 24);
        var flood = Encoding.ASCII.GetBytes(Esc + "[" + new string('7', 4 * 1024 * 1024) + "m");

        var sw = Stopwatch.StartNew();
        screen.Feed(flood);
        sw.Stop();

        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(5),
            $"parsing 4 MB of CSI parameters took {sw.Elapsed} — the accumulation is not bounded");
    }
}
