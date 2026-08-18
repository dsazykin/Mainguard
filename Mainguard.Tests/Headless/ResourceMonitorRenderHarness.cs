using System;
using System.Collections.Generic;
using System.IO;
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
/// Renders the Resources tab offscreen in every state that matters, in EVERY theme (Daylight Loom is
/// light — never assume dark). PNGs land in <c>artifacts_headless/</c> and are meant to be LOOKED AT: the
/// load-bearing question is whether "not measured" is visibly different from "zero", and whether the cost
/// UI is absent when cost is not being measured. The VM truths are asserted alongside each capture so a
/// silently-blank surface cannot pass as a green test.
/// </summary>
public class ResourceMonitorRenderHarness
{
    private static readonly string[] ThemeKeys = { "MidnightLoom", "DaylightLoom", "Graphite", "CommandDeck", "Atelier", "LoomAurora" };

    /// <summary>A metered (BYOK) fleet with live readings: the full cost UI must be present.</summary>
    [AvaloniaFact]
    public void Capture_LiveAgents_Byok_AllFiveThemes()
    {
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);
            var telemetry = new FakeTelemetry();
            telemetry.Seed(
                new ResourceSample(DateTimeOffset.UtcNow, 62, 3.4, 1.25m),
                new[]
                {
                    new AgentResourceUsage("a", "Loom-1", "Working", false, 41.2, 1.9, 0.75m, "compiling", IsMetered: true),
                    new AgentResourceUsage("b", "Loom-2", "Verifying", false, 20.8, 1.5, 0.50m, "pytest -q", IsMetered: true),
                });

            using var vm = new ResourceMonitorViewModel(new FakeAgents(), telemetry);
            var win = HostWindow(new ResourceMonitorView { DataContext = vm });
            win.Show();
            Settle();

            Assert.True(vm.IsCostVisible, "BYOK fleet must keep the full cost UI");
            Assert.Equal(2, vm.Rows.Count);
            Assert.Contains("spend today $1.25", vm.TotalsText);
            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"resources_byok_{theme}.png"));
            HarnessHygiene.Teardown(win);
        }

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    /// <summary>An OAuth fleet: no cap editor, no Save, no spend figure — an honest statement instead.</summary>
    [AvaloniaFact]
    public void Capture_LiveAgents_Oauth_AllFiveThemes()
    {
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);
            var telemetry = new FakeTelemetry();
            telemetry.Seed(
                new ResourceSample(DateTimeOffset.UtcNow, 55, 2.9, SpendTodayUsd: null),
                new[]
                {
                    new AgentResourceUsage("a", "Loom-1", "Working", false, 33.4, 1.7, null, "compiling", IsMetered: false),
                    new AgentResourceUsage("b", "Loom-2", "Verifying", false, 21.6, 1.2, null, "pytest -q", IsMetered: false),
                });

            using var vm = new ResourceMonitorViewModel(new FakeAgents(), telemetry);
            var win = HostWindow(new ResourceMonitorView { DataContext = vm });
            win.Show();
            Settle();

            Assert.False(vm.IsCostVisible, "an unmeterable fleet must not show the cost UI");
            Assert.DoesNotContain("spend today", vm.TotalsText);
            Assert.DoesNotContain("$0.00", vm.TotalsText);
            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"resources_oauth_{theme}.png"));
            HarnessHygiene.Teardown(win);
        }

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    /// <summary>A failed sample next to a genuinely idle agent — the capture that shows "—" is not "0%".</summary>
    [AvaloniaFact]
    public void Capture_FailedSample_NextToIdle_AllFiveThemes()
    {
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);
            var telemetry = new FakeTelemetry();
            telemetry.Seed(
                new ResourceSample(DateTimeOffset.UtcNow, null, null, null),
                new[]
                {
                    new AgentResourceUsage("idle", "Loom-1", "Working", false, 0, 0, 0m, "waiting", IsMetered: true),
                    new AgentResourceUsage("lost", "Loom-2", "Working", false, null, null, null, "sample failed"),
                });

            using var vm = new ResourceMonitorViewModel(new FakeAgents(), telemetry);
            var win = HostWindow(new ResourceMonitorView { DataContext = vm });
            win.Show();
            Settle();

            Assert.Equal("0%", vm.Rows[0].CpuText);
            Assert.Equal(AgentUsageRowViewModel.Unknown, vm.Rows[1].CpuText);
            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"resources_unknown_{theme}.png"));
            HarnessHygiene.Teardown(win);
        }

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    /// <summary>No agents at all — the empty tab must not imply a measured, idle fleet.</summary>
    [AvaloniaFact]
    public void Capture_NoAgents()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var telemetry = new FakeTelemetry();
        telemetry.Seed(new ResourceSample(DateTimeOffset.UtcNow, null, null, null), Array.Empty<AgentResourceUsage>());

        using var vm = new ResourceMonitorViewModel(new FakeAgents(), telemetry);
        var win = HostWindow(new ResourceMonitorView { DataContext = vm });
        win.Show();
        Settle();

        Assert.Empty(vm.Rows);
        Assert.Contains("0 agents", vm.TotalsText);
        Assert.DoesNotContain("spend today", vm.TotalsText);
        // Neither cost block: with no sessions there is nothing to bill AND nothing to disclaim.
        Assert.False(vm.IsCostVisible);
        Assert.False(vm.IsCostNoticeVisible);
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "resources_no_agents.png"));
        HarnessHygiene.Teardown(win);
    }

    private static Window HostWindow(Control content)
    {
        var win = new Window { Width = 1100, Height = 700, Content = content };
        if (Avalonia.Application.Current!.TryGetResource("SurfaceWindow", null, out var bg) && bg is Avalonia.Media.IBrush brush)
            win.Background = brush;
        return win;
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
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

    private sealed class FakeTelemetry : ITelemetryService
    {
        private readonly List<ResourceSample> _history = new();
        private IReadOnlyList<AgentResourceUsage> _usage = Array.Empty<AgentResourceUsage>();
        public ResourceSample Current { get; private set; } = new(DateTimeOffset.UtcNow, null, null, null);
        public IReadOnlyList<ResourceSample> History => _history;
        public event Action? Sampled { add { } remove { } }

        public void Seed(ResourceSample current, IReadOnlyList<AgentResourceUsage> usage)
        {
            Current = current;
            _history.Add(current);
            _usage = usage;
        }

        public IReadOnlyList<AgentResourceUsage> GetAgentUsage() => _usage;
        public IReadOnlyList<SandboxEvent> GetSandboxEvents(string? agentId = null) => Array.Empty<SandboxEvent>();
        public Task<SpendBudget> GetSpendBudgetAsync(CancellationToken ct = default) => Task.FromResult(SpendBudget.None);
        public Task SetSpendBudgetAsync(SpendBudget budget, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeAgents : IAgentService
    {
        public IReadOnlyList<AgentInfo> ListAgents() => Array.Empty<AgentInfo>();
        public event Action<AgentEvent>? EventReceived { add { } remove { } }
        public Task SendPromptAsync(string agentId, string prompt) => Task.CompletedTask;
        public IReadOnlyList<string> GetQueuedPrompts(string agentId) => Array.Empty<string>();
        public Task CancelQueuedPromptAsync(string agentId, int index) => Task.CompletedTask;
        public IReadOnlyList<string> GetTerminalTail(string agentId) => Array.Empty<string>();
        public IReadOnlyList<(string Step, bool Done)> GetPlanTree(string agentId) => Array.Empty<(string, bool)>();
        public Task PauseAgentAsync(string agentId) => Task.CompletedTask;
        public Task ResumeAgentAsync(string agentId) => Task.CompletedTask;
        public Task EndAgentAsync(string agentId) => Task.CompletedTask;
    }
}
