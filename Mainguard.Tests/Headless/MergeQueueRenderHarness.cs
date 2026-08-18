using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.UI.Theming;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

/// <summary>
/// Renders the P2-10 Merge Queue Rail (bound to the REAL <see cref="MergeQueue"/>) offscreen in EVERY
/// theme (never assume dark). It drives a small swarm through the state machine — Working / Verifying /
/// Verified / StaleVerified — so the state chips, the <c>main@sha</c> labels, the CanMerge gate reasons,
/// and the override affordance all render. PNGs go to artifacts_headless/.
/// </summary>
public class MergeQueueRenderHarness
{
    private static readonly string[] ThemeKeys = { "MidnightLoom", "DaylightLoom", "Graphite", "Atelier" };

    [AvaloniaFact]
    public void Capture_MergeQueueRail_AllThemes()
    {
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);
            var queue = BuildPopulatedQueue();
            using var vm = new MergeQueueViewModel(queue);
            var win = HostWindow(new MergeQueueView { DataContext = vm });
            win.Show();
            Settle();

            Assert.True(vm.Rows.Count >= 3);
            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"merge_queue_{theme}.png"));
            HarnessHygiene.Teardown(win);
        }

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    private static MergeQueue BuildPopulatedQueue()
    {
        MergeQueue queue = null!;
        Func<string, CancellationToken, Task<VerificationRecord>> run = (id, ct) =>
            Task.FromResult(new VerificationRecord(id, queue.CurrentMainSha, true, "log", "npm test", "hash", DateTimeOffset.UtcNow));
        // A no-op requeue keeps loom-2 visibly StaleVerified for the capture (production re-verifies).
        Func<string, CancellationToken, Task> noRequeue = (id, ct) => Task.CompletedTask;
        queue = new MergeQueue("repo", "a1b2c3d4e5", new InMemoryMergeQueueStore(), new InMemoryVerificationStore(), run, noRequeue);

        // loom-1 Verified (mergeable), loom-2 verified then made stale, loom-3 left Working.
        queue.RunVerificationAsync("loom-1", CancellationToken.None).GetAwaiter().GetResult();
        queue.RunVerificationAsync("loom-2", CancellationToken.None).GetAwaiter().GetResult();
        queue.NotifyNewCommits("loom-3"); // registers as Working
        queue.NotifyMainMoved("f6e5d4c3b2"); // loom-2 → StaleVerified (re-verifying)
        return queue;
    }

    private static Window HostWindow(Control content)
    {
        var win = new Window { Width = 420, Height = 620, Content = content };
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
