using System.Collections.Generic;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Google.Protobuf;
using Mainguard.Agents.UI.Controls;
using Mainguard.Protos.V1;
using Mainguard.Tests.Terminal;
using Xunit;

namespace Mainguard.Tests.Headless;

/// <summary>
/// The wheel, through the REAL engine controls rather than the pure
/// <see cref="WheelScrollAccumulator"/> the sibling unit tests drive — the wiring is the half a
/// pure test cannot see. Both engines are covered because both had the same bug (the terminal
/// scrolled far too fast), and both are reachable in the shipped app behind
/// <c>MAINGUARD_TERMINAL_ENGINE</c>.
/// </summary>
public class TerminalWheelScrollHarness
{
    private static readonly Point Middle = new(100, 60);

    [AvaloniaFact]
    public void InterimEngine_OneNotch_ScrollsOneLine_AndMicroTicksAddUp()
    {
        var terminal = new TerminalControl();
        var win = new Window { Content = terminal, Width = 400, Height = 200 };
        win.Show();

        // Enough output to build a scrollback ring to move through.
        var sb = new StringBuilder();
        for (var i = 0; i < 200; i++)
        {
            sb.Append("line").Append(i).Append("\r\n");
        }

        terminal.FeedOutput(Encoding.UTF8.GetBytes(sb.ToString()));
        terminal.Focus();
        Pump();
        Assert.Equal(0, terminal.ScrollOffset);

        win.MouseWheel(Middle, new Vector(0, 1));
        Pump();
        Assert.Equal(1, terminal.ScrollOffset);

        // Ten trackpad micro-ticks are one line's worth of movement — not ten, and not the thirty
        // the old Math.Round(Delta.Y * 3)-with-a-floor-of-one produced.
        for (var i = 0; i < 10; i++)
        {
            win.MouseWheel(Middle, new Vector(0, 0.1));
        }

        Pump();
        Assert.Equal(2, terminal.ScrollOffset);

        // Back down, and a keystroke still snaps to live.
        win.MouseWheel(Middle, new Vector(0, -1));
        Pump();
        Assert.Equal(1, terminal.ScrollOffset);

        win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Pump();
        Assert.Equal(0, terminal.ScrollOffset);

        HarnessHygiene.Teardown(win);
    }

    // A sub-line tick is absorbed into the carry, not ignored — so the terminal keeps the event
    // instead of handing a slow trackpad gesture to whatever ancestor scrolls.
    [AvaloniaFact]
    public void InterimEngine_ASubLineTick_IsConsumedByTheTerminal()
    {
        var terminal = new TerminalControl();
        var win = new Window { Content = terminal, Width = 400, Height = 200 };
        win.Show();
        terminal.FeedOutput(Encoding.UTF8.GetBytes("hello\r\n"));
        Pump();

        var handled = false;
        terminal.AddHandler(
            InputElement.PointerWheelChangedEvent,
            (object? _, PointerWheelEventArgs e) => handled = e.Handled,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);

        win.MouseWheel(Middle, new Vector(0, 0.1));
        Pump();
        Assert.True(handled, "a sub-line tick must be consumed by the terminal, not bubbled");

        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void GridEngine_OneNotch_ScrollsOneLine_AndMicroTicksAddUp()
    {
        var terminal = new TerminalGridControl();
        var win = new Window { Content = terminal, Width = 400, Height = 200 };
        win.Show();

        terminal.FeedOutput(Envelope(Snapshot(20, 4)));

        // Push scrolled-off rows into the client ring so there is history to scroll into.
        var withPushes = GridModelTests.Delta(20, 4);
        for (var i = 0; i < 50; i++)
        {
            withPushes.Pushed.Add(GridModelTests.Row(0, GridModelTests.Glyphs(0, "old" + i)));
        }

        terminal.FeedOutput(Envelope(withPushes));
        terminal.Focus();
        Pump();
        Assert.Equal(0, terminal.ViewOffset);

        win.MouseWheel(Middle, new Vector(0, 1));
        Pump();
        Assert.Equal(1, terminal.ViewOffset);

        // The old engine stepped a flat ±3 per event regardless of the delta: this gesture moved 30.
        for (var i = 0; i < 10; i++)
        {
            win.MouseWheel(Middle, new Vector(0, 0.1));
        }

        Pump();
        Assert.Equal(2, terminal.ViewOffset);

        win.MouseWheel(Middle, new Vector(0, -1));
        Pump();
        Assert.Equal(1, terminal.ViewOffset);

        win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Pump();
        Assert.Equal(0, terminal.ViewOffset);

        HarnessHygiene.Teardown(win);
    }

    // GridModelTests.Snapshot turns mouse reporting ON, which is a different wheel destination
    // (the app's own reports) — this case is about the local ring, so modes stay off.
    private static GridUpdate Snapshot(int cols, int rows) => new()
    {
        Cols = (uint)cols,
        Rows = (uint)rows,
        Snapshot = true,
        Cursor = new GridCursor { Row = 0, Col = 0, Visible = true },
        Modes = new GridModes(),
    };

    // The other two wheel destinations of the grid engine, which used to run at the same hardcoded
    // three-per-event: an app that tracks the mouse gets the reports, and an alt-screen app (vim,
    // less) gets the arrow shim. Both now spend the same accumulated line count.
    [AvaloniaFact]
    public void GridEngine_MouseReportingApp_GetsOneReportPerNotch_AndNoneForAMicroTick()
    {
        var terminal = new TerminalGridControl();
        var win = new Window { Content = terminal, Width = 400, Height = 200 };
        win.Show();

        var snapshot = Snapshot(20, 4);
        snapshot.Modes = new GridModes { Mouse = 2, MouseSgr = true };
        terminal.FeedOutput(Envelope(snapshot));
        Pump();

        var reports = 0;
        terminal.InputAvailable += _ => reports++;

        win.MouseWheel(Middle, new Vector(0, 1));
        Pump();
        Assert.Equal(1, reports);

        // A single trackpad micro-tick is not a line, so the app hears nothing — it used to hear a
        // full wheel report for every one of them.
        win.MouseWheel(Middle, new Vector(0, 0.1));
        Pump();
        Assert.Equal(1, reports);

        for (var i = 0; i < 9; i++)
        {
            win.MouseWheel(Middle, new Vector(0, 0.1));
        }

        Pump();
        Assert.Equal(2, reports);

        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void GridEngine_AltScreenApp_GetsOneArrowPerNotch()
    {
        var terminal = new TerminalGridControl();
        var win = new Window { Content = terminal, Width = 400, Height = 200 };
        win.Show();

        var snapshot = Snapshot(20, 4);
        snapshot.Modes = new GridModes { AltScreen = true };
        terminal.FeedOutput(Envelope(snapshot));
        Pump();

        var arrows = new List<string>();
        terminal.InputAvailable += bytes => arrows.Add(Encoding.ASCII.GetString(bytes));

        // One notch used to send three arrow keys; a trackpad sent three per micro-tick.
        win.MouseWheel(Middle, new Vector(0, -1));
        Pump();
        Assert.Equal(new[] { "\u001b[B" }, arrows);

        win.MouseWheel(Middle, new Vector(0, 0.1));
        Pump();
        Assert.Single(arrows);

        HarnessHygiene.Teardown(win);
    }

    private static byte[] Envelope(GridUpdate update) =>
        new TerminalOutput { Grid = update }.ToByteArray();

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(15);
    }
}
