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
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.UI.Theming;
using Xunit;

namespace Mainguard.Tests.Headless;

/// <summary>
/// The four phase-2 plan-gate states of the coordinator panel, rendered in EVERY one of the five themes
/// for human visual review (contract §2). PNGs land in artifacts_headless/.
///
/// <para>The states are here because each of them is otherwise invisible, and three of them are new:</para>
/// <list type="number">
/// <item><b>plan-pending</b> — a worker wrote this and is blocked on you;</item>
/// <item><b>rejected/revising</b> — the plan came back revised, with the revision counter against the
/// daemon's budget and the feedback it was written against;</item>
/// <item><b>escalated</b> — the budget is spent and the worker stopped: a card with no decision on it,
/// because the decision is no longer a click;</item>
/// <item><b>backpressure</b> — blocked workers have filled the worker cap, so the coordinator has stopped
/// spawning. This one is the reason the harness exists at all: a stall nobody can see is
/// indistinguishable from a hang.</item>
/// </list>
///
/// <para>The assertions are not decoration. Each state asserts the fact the human is supposed to be able
/// to read off the surface, so a render that silently loses it fails here rather than in the screenshot
/// nobody opened.</para>
/// </summary>
public class CoordinatorPlanGateRenderHarness
{
    private static readonly string[] ThemeKeys =
        { "MidnightLoom", "DaylightLoom", "CommandDeck", "Atelier", "LoomAurora" };

    [AvaloniaFact]
    public void PlanPending_HeadlessPng_AllThemes()
    {
        RenderAllThemes("plan_gate_pending", () => Fake.Pending(), vm =>
        {
            Assert.NotNull(vm.PendingPlan);
            Assert.Contains("Written by loom-4", vm.PendingPlan!.FactsText, StringComparison.Ordinal);
            Assert.Contains("blocked until you decide", vm.PendingPlan.FactsText, StringComparison.Ordinal);
            Assert.False(vm.PendingPlan.IsRevision);
            Assert.False(vm.PendingPlan.NextRejectionEscalates);
        });
    }

    [AvaloniaFact]
    public void RejectedAndRevised_HeadlessPng_AllThemes()
    {
        RenderAllThemes("plan_gate_revising", () => Fake.Revised(), vm =>
        {
            Assert.NotNull(vm.PendingPlan);
            Assert.Equal("revision 2 of 3", vm.PendingPlan!.RevisionText);
            Assert.Contains("revised against: too wide", vm.PendingPlan.RevisedAgainstText, StringComparison.Ordinal);
        });
    }

    [AvaloniaFact]
    public void LastRevision_WarnsThatRejectingAgainStopsTheWorker_AllThemes()
    {
        RenderAllThemes("plan_gate_last_revision", () => Fake.LastRevision(), vm =>
        {
            Assert.NotNull(vm.PendingPlan);
            Assert.True(vm.PendingPlan!.NextRejectionEscalates);
            Assert.Equal("Reject — worker will stop", vm.PendingPlan.RejectButtonText);
        });
    }

    [AvaloniaFact]
    public void Escalated_HeadlessPng_AllThemes()
    {
        RenderAllThemes("plan_gate_escalated", () => Fake.Escalated(), vm =>
        {
            Assert.Null(vm.PendingPlan); // nothing to decide — that is the point of this state
            var card = Assert.Single(vm.EscalatedPlans);
            Assert.Contains("stopped after 3 rejected plans", card.HeadlineText, StringComparison.Ordinal);
            Assert.Contains("your last feedback", card.LastFeedbackText, StringComparison.Ordinal);
        });
    }

    [AvaloniaFact]
    public void BackpressureCapSaturated_HeadlessPng_AllThemes()
    {
        RenderAllThemes("plan_gate_backpressure", () => Fake.Backpressure(), vm =>
        {
            Assert.True(vm.IsCapSaturatedByBlockedWorkers);
            Assert.Contains("6 workers are waiting on your approval", vm.BackpressureText, StringComparison.Ordinal);
            Assert.Contains("stopped spawning", vm.BackpressureText, StringComparison.Ordinal);
        });
    }

    [AvaloniaFact]
    public void NoBlockedWorkers_ShowsNoBackpressureLine()
    {
        // The negative control for the harness above: with nothing waiting, the surface must NOT claim a
        // stall. An always-on warning is the same as no warning.
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var vm = new CoordinatorPanelViewModel(Fake.Quiet());

        Assert.Equal("", vm.BackpressureText);
        Assert.False(vm.IsCapSaturatedByBlockedWorkers);
        Assert.Empty(vm.EscalatedPlans);
    }

    private static void RenderAllThemes(
        string name, Func<Fake> service, Action<CoordinatorPanelViewModel> assert)
    {
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);

            var vm = new CoordinatorPanelViewModel(service());
            assert(vm);

            var win = new Window
            {
                Width = 520,
                Height = 780,
                Content = new CoordinatorPanelView { DataContext = vm },
            };
            win.Show();
            Settle();
            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"{name}_{theme}.png"));
            win.Content = null;
            HarnessHygiene.Teardown(win);
        }

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    private static void Settle()
    {
        for (int i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }

    /// <summary>A deterministic <see cref="ICoordinatorService"/> pinned to one plan-gate state.</summary>
    private sealed class Fake : ICoordinatorService
    {
        private readonly IReadOnlyList<ChatLine> _transcript;
        private readonly IReadOnlyList<WorkerPlanCard> _cards;
        private readonly OrchestrationBackpressure _backpressure;

        private Fake(
            IReadOnlyList<ChatLine> transcript,
            IReadOnlyList<WorkerPlanCard> cards,
            OrchestrationBackpressure backpressure)
        {
            _transcript = transcript;
            _cards = cards;
            _backpressure = backpressure;
        }

        public static Fake Pending() => new(
            Lines("Loom-4 presented its plan and is blocked awaiting your approval."),
            new[] { Card(revision: 0, remaining: 3, status: "Pending", feedback: "") },
            new OrchestrationBackpressure(1, 0, 2, 6, 3, "1 worker is waiting on your approval."));

        public static Fake Revised() => new(
            Lines("Loom-4 revised its plan against your feedback (revision 2 of 3)."),
            new[] { Card(revision: 2, remaining: 1, status: "Pending", feedback: "too wide — scope it to the clock") },
            new OrchestrationBackpressure(1, 0, 2, 6, 3, "1 worker is waiting on your approval."));

        public static Fake LastRevision() => new(
            Lines("Loom-4 revised its plan against your feedback (revision 3 of 3)."),
            new[] { Card(revision: 3, remaining: 0, status: "Pending", feedback: "still touching the refresh service") },
            new OrchestrationBackpressure(1, 0, 2, 6, 3, "1 worker is waiting on your approval."));

        public static Fake Escalated() => new(
            Lines("Loom-4 stopped after 3 rejected plans and escalated to you."),
            new[] { Card(revision: 3, remaining: 0, status: "Escalated", feedback: "this still is not the right approach") },
            new OrchestrationBackpressure(0, 1, 2, 6, 3, "1 escalated after 3 rejected plans."));

        public static Fake Backpressure()
        {
            var cards = Enumerable.Range(1, 6)
                .Select(i => Card(revision: 0, remaining: 3, status: "Pending", feedback: "", worker: $"loom-{i}"))
                .ToArray();
            return new Fake(
                Lines("spawn_worker → [Rejected] Worker cap reached — 6/6 running."),
                cards,
                new OrchestrationBackpressure(6, 0, 6, 6, 3,
                    "6 workers are waiting on your approval. The worker cap (6/6) is full — " +
                    "the coordinator has stopped spawning until you clear plans."));
        }

        public static Fake Quiet() => new(
            Lines("Nothing is waiting on you."), Array.Empty<WorkerPlanCard>(), OrchestrationBackpressure.None);

        private static IReadOnlyList<ChatLine> Lines(string systemLine) => new[]
        {
            new ChatLine(ChatLineKind.Human, "Fix the token expiry off-by-one.", DateTimeOffset.Now.AddMinutes(-9)),
            new ChatLine(ChatLineKind.Coordinator,
                "Starting a worker on the auth path — it will inspect the repo and write its own plan.",
                DateTimeOffset.Now.AddMinutes(-9)),
            new ChatLine(ChatLineKind.ToolCall, "spawn_worker(title=\"Fix token expiry\")", DateTimeOffset.Now.AddMinutes(-8)),
            new ChatLine(ChatLineKind.SystemLine, systemLine, DateTimeOffset.Now.AddMinutes(-2)),
        };

        private static WorkerPlanCard Card(
            int revision, int remaining, string status, string feedback, string worker = "loom-4") => new(
            "plan-7", worker, "coordinator", "Fix token expiry off-by-one",
            new[] { "src/Auth/TokenClock.cs", "src/Auth/RefreshService.cs", "tests/AuthTests.cs" },
            "Extract the clock behind ITokenClock; inject a fixed clock in tests; correct the expiry comparison.",
            "AuthTests green plus two new expiry-boundary cases.",
            1.50m, DateTimeOffset.Now.AddMinutes(-2), status, revision, remaining, 3, feedback);

        public IReadOnlyList<ChatLine> GetTranscript() => _transcript;

        public IReadOnlyList<TaskPlan> GetPendingPlans() => _cards
            .Where(c => c.IsPending)
            .Select(c => new TaskPlan(c.PlanId, c.Title, c.Scope, c.Approach, c.TestStrategy, c.BudgetUsd, c.PresentedAt))
            .ToArray();

        public TaskPlan? GetPlan(string planId) => GetPendingPlans().FirstOrDefault(p => p.PlanId == planId);

        public IReadOnlyList<WorkerPlanCard> GetWorkerPlans() => _cards;

        public OrchestrationBackpressure GetBackpressure() => _backpressure;

        public event Action? Changed { add { } remove { } }

        public Task SendAsync(string text) => Task.CompletedTask;

        public Task SubmitPlanDecisionAsync(string planId, bool approve, string? feedback = null) => Task.CompletedTask;
    }
}
