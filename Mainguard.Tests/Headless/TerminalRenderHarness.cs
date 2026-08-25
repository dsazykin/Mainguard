using System;
using System.IO;
using System.Text;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.UI.Controls;
using Mainguard.App.Shell.Controls;
using Mainguard.UI.Controls;
using Mainguard.UI.Theming;
using Xunit;

namespace Mainguard.Tests.Headless;

// TI-P2-03 §8 (v1 A.6 pattern) — renders a coloured TUI frame through the interim terminal engine
// (TerminalControl over the pure VtScreen) offscreen in two themes so the ANSI cell palette and its
// legibility can be inspected without a display. Interactive terminal *feel* (vim/htop/tmux latency,
// reflow, scroll) stays a manual matrix — the v1 boundary. Captures a PNG per theme to
// artifacts_headless/ (visual review, not pass/fail).
public class TerminalRenderHarness
{
    // ESC, kept as a char constant so no raw control byte lives in the source.
    private const char Esc = '\u001b';

    [AvaloniaFact]
    public void Capture_TerminalFrame_DarkAndLight()
    {
        try
        {
            ThemeManager.Apply("MidnightLoom", persist: false);
            CaptureOnce("terminal_frame_MidnightLoom.png");

            ThemeManager.Apply("DaylightLoom", persist: false);
            CaptureOnce("terminal_frame_DaylightLoom.png");
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        }
    }

    // Reproduces the live finding (Windows/WSL2 walkthrough, 2026-08-25): switching theme while a
    // TerminalControl is already attached and rendered left the old theme's background on screen,
    // because neither terminal engine subscribed to ThemeManager.ThemeChanged (unlike the sibling
    // CommitGraphCanvas, which does). Compares the actual encoded pixels of the SAME control before
    // and after an in-place theme switch (no new control is created, unlike Capture_TerminalFrame_
    // DarkAndLight above) — identical bytes would mean the repaint never happened. Fails before the
    // ThemeChanged hook was added, passes after.
    [AvaloniaFact]
    public void ThemeSwitch_WhileAttached_RepaintsTerminalBackground()
    {
        try
        {
            ThemeManager.Apply("MidnightLoom", persist: false);

            var terminal = new TerminalControl();
            var win = new Window { Content = terminal, Width = 200, Height = 100 };
            win.Show();
            terminal.FeedOutput(Encoding.UTF8.GetBytes("x"));
            for (var i = 0; i < 5; i++) Pump();

            var darkBytes = EncodePng(win.CaptureRenderedFrame());

            ThemeManager.Apply("DaylightLoom", persist: false);
            for (var i = 0; i < 5; i++) Pump();

            var lightBytes = EncodePng(win.CaptureRenderedFrame());

            Assert.False(darkBytes.AsSpan().SequenceEqual(lightBytes),
                "the rendered frame did not change after a theme switch — the terminal engine never repainted");
            HarnessHygiene.Teardown(win);
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        }
    }

    private static byte[] EncodePng(Avalonia.Media.Imaging.WriteableBitmap? frame)
    {
        using var ms = new MemoryStream();
        frame!.Save(ms);
        return ms.ToArray();
    }

    private static void CaptureOnce(string fileName)
    {
        var terminal = new TerminalControl();
        var win = new Window { Content = terminal, Width = 720, Height = 470 };
        win.Show();

        terminal.FeedOutput(BuildColoredFrame());
        for (var i = 0; i < 10; i++)
        {
            Pump();
        }

        var frame = win.CaptureRenderedFrame();
        frame?.Save(Path.Combine(ArtifactsDir(), fileName));

        // Sanity: the engine actually parsed the frame into the grid.
        var grid = terminal.ReadGrid();
        Assert.Contains("Mainguard", grid.RowText(0));
        HarnessHygiene.Teardown(win);
    }

    private static byte[] BuildColoredFrame()
    {
        // A small htop-ish frame: a bold title bar, coloured usage bars, and a git status panel —
        // exercising bold, the base ANSI colours, and box-drawing glyphs. Sgr(code) = ESC [ code.
        static string Sgr(string code) => Esc + "[" + code;
        var reset = Sgr("0m");
        var sb = new StringBuilder();

        sb.Append(Sgr("1;36m")).Append(" Mainguard Terminal — interim PTY engine (P2-03)").Append(reset).Append("\r\n\r\n");

        sb.Append("  CPU0 ").Append(Sgr("32m")).Append("|||||||||||||").Append(reset).Append("            23%\r\n");
        sb.Append("  CPU1 ").Append(Sgr("33m")).Append("||||||||||||||||||||||").Append(reset).Append("     61%\r\n");
        sb.Append("  CPU2 ").Append(Sgr("31m")).Append("||||||||||||||||||||||||||||||").Append(reset).Append(" 94%\r\n\r\n");

        sb.Append("  ").Append(Sgr("34m")).Append("┌───────────── git status ─────────────┐").Append(reset).Append("\r\n");
        sb.Append("  ").Append(Sgr("34m")).Append("│").Append(reset)
          .Append(" branch  ").Append(Sgr("1;35m")).Append("feature/P2-03-terminal").Append(reset)
          .Append("        ").Append(Sgr("34m")).Append("│").Append(reset).Append("\r\n");
        sb.Append("  ").Append(Sgr("34m")).Append("│").Append(reset)
          .Append(" ").Append(Sgr("32m")).Append("● staged   PtyProcessShim.cs").Append(reset)
          .Append("          ").Append(Sgr("34m")).Append("│").Append(reset).Append("\r\n");
        sb.Append("  ").Append(Sgr("34m")).Append("│").Append(reset)
          .Append(" ").Append(Sgr("31m")).Append("● modified VtBoundaryDetector.cs").Append(reset)
          .Append("      ").Append(Sgr("34m")).Append("│").Append(reset).Append("\r\n");
        sb.Append("  ").Append(Sgr("34m")).Append("└──────────────────────────────────────┘").Append(reset).Append("\r\n\r\n");

        sb.Append("  ").Append(Sgr("90m")).Append("$ ").Append(reset).Append(Sgr("97m"))
          .Append("git commit -m \"P2-03: interim terminal engine\"").Append(reset);

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(15);
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
