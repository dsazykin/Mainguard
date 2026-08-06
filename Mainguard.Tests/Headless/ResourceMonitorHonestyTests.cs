using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.Agents;
using Mainguard.Agents.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests.Headless;

/// <summary>
/// The two honesty properties of the Resource Monitor, asserted on ViewModel state.
///
/// <para><b>1. Unknown is not zero.</b> The tab shipped rendering a hard-coded <c>0</c> for every agent's
/// CPU and RAM, which is indistinguishable from a genuinely idle fleet. A reading that could not be taken
/// must render as "—", and a reading of zero must still render as "0%", or the display cannot be trusted
/// in either direction.</para>
///
/// <para><b>2. Cost UI appears only where cost is measured.</b> Spend is recorded by routing model traffic
/// through Mainguard's gateway, which needs an API key to swap for a scoped token. An OAuth session
/// authenticates past that proxy, so its spend is structurally unmeasurable — and rendering <c>$0.00</c>
/// for it would read as "you have spent nothing".</para>
/// </summary>
public class ResourceMonitorHonestyTests
{
    [AvaloniaFact]
    public void UnknownReadings_RenderAsDash_NotZero()
    {
        var telemetry = new FakeTelemetry();
        telemetry.Seed(
            current: new ResourceSample(DateTimeOffset.UtcNow, null, null, null),
            usage: new[]
            {
                // Genuinely idle — a real measurement of zero.
                new AgentResourceUsage("idle", "Loom-1", "Working", false, 0, 0, 0m, "waiting", IsMetered: true),
                // Not measured — the sample failed.
                new AgentResourceUsage("unknown", "Loom-2", "Working", false, null, null, null, "compiling"),
            });

        using var vm = new ResourceMonitorViewModel(new FakeAgents(), telemetry);

        var idle = vm.Rows.Single(r => r.AgentId == "idle");
        var unknown = vm.Rows.Single(r => r.AgentId == "unknown");

        // A measured zero still reads as a number.
        Assert.Equal("0%", idle.CpuText);
        Assert.Equal("0.0 GB", idle.RamText);

        // An unmeasured reading does NOT — and specifically is not "0%".
        Assert.Equal(AgentUsageRowViewModel.Unknown, unknown.CpuText);
        Assert.Equal(AgentUsageRowViewModel.Unknown, unknown.RamText);
        Assert.NotEqual(idle.CpuText, unknown.CpuText);

        // The totals line says so too, rather than summing unknowns into a confident zero.
        Assert.Contains(AgentUsageRowViewModel.Unknown, vm.TotalsText);
        Assert.DoesNotContain("CPU 0%", vm.TotalsText);
    }

    [AvaloniaFact]
    public void CostUi_Hidden_WhenNoAgentIsMetered()
    {
        var telemetry = new FakeTelemetry();
        telemetry.Seed(
            current: new ResourceSample(DateTimeOffset.UtcNow, 12, 1.0, SpendTodayUsd: null),
            usage: new[]
            {
                new AgentResourceUsage("oauth-1", "Loom-1", "Working", false, 12, 1.0, null, "compiling", IsMetered: false),
            });

        using var vm = new ResourceMonitorViewModel(new FakeAgents(), telemetry);

        Assert.False(vm.IsCostVisible);
        Assert.True(vm.IsCostNoticeVisible, "an unmeterable fleet should be told why, not left blank");

        // The per-row spend is an explicit dash, never a reassuring $0.00.
        var row = vm.Rows.Single();
        Assert.Equal(AgentUsageRowViewModel.Unknown, row.SpendText);
        Assert.False(row.IsMetered);
        Assert.False(string.IsNullOrWhiteSpace(row.SpendTooltip));
        Assert.DoesNotContain("$0.00", row.SpendText);

        // And the totals line drops the spend clause entirely.
        Assert.DoesNotContain("spend today", vm.TotalsText);
        Assert.DoesNotContain("$0.00", vm.TotalsText);
    }

    [AvaloniaFact]
    public void CostUi_Shown_WhenAnAgentIsMetered()
    {
        var telemetry = new FakeTelemetry();
        telemetry.Seed(
            current: new ResourceSample(DateTimeOffset.UtcNow, 30, 2.0, 1.25m),
            usage: new[]
            {
                new AgentResourceUsage("byok-1", "Loom-1", "Working", false, 30, 2.0, 1.25m, "compiling", IsMetered: true),
            });

        using var vm = new ResourceMonitorViewModel(new FakeAgents(), telemetry);

        Assert.True(vm.IsCostVisible);

        var row = vm.Rows.Single();
        Assert.Equal("$1.25", row.SpendText);
        Assert.True(row.IsMetered);
        Assert.Null(row.SpendTooltip); // nothing to explain when it IS measured
        Assert.Contains("spend today $1.25", vm.TotalsText);
    }

    /// <summary>
    /// A mixed fleet: one metered agent is enough to make the cost UI meaningful, but the unmetered row
    /// must still refuse to show a figure. Otherwise the fix would only work in the all-or-nothing cases.
    /// </summary>
    [AvaloniaFact]
    public void MixedFleet_ShowsCostUi_ButStillHidesTheUnmeteredRowsFigure()
    {
        var telemetry = new FakeTelemetry();
        telemetry.Seed(
            current: new ResourceSample(DateTimeOffset.UtcNow, 42, 3.0, 0.75m),
            usage: new[]
            {
                new AgentResourceUsage("byok-1", "Loom-1", "Working", false, 30, 2.0, 0.75m, "compiling", IsMetered: true),
                new AgentResourceUsage("oauth-1", "Loom-2", "Working", false, 12, 1.0, null, "planning", IsMetered: false),
            });

        using var vm = new ResourceMonitorViewModel(new FakeAgents(), telemetry);

        Assert.True(vm.IsCostVisible);
        Assert.Equal("$0.75", vm.Rows.Single(r => r.AgentId == "byok-1").SpendText);
        Assert.Equal(AgentUsageRowViewModel.Unknown, vm.Rows.Single(r => r.AgentId == "oauth-1").SpendText);
    }

    /// <summary>
    /// An empty tab makes NO cost claim in either direction. "Spend isn't tracked for these sessions" is
    /// false when there are no sessions — an unearned statement about nothing is the same species of
    /// error as the $0.00 this change removes.
    /// </summary>
    [AvaloniaFact]
    public void NoAgents_ShowsNeitherCostEditorNorNotTrackedNotice()
    {
        var telemetry = new FakeTelemetry();
        telemetry.Seed(new ResourceSample(DateTimeOffset.UtcNow, null, null, null), Array.Empty<AgentResourceUsage>());

        using var vm = new ResourceMonitorViewModel(new FakeAgents(), telemetry);

        Assert.Empty(vm.Rows);
        Assert.False(vm.IsCostVisible);
        Assert.False(vm.IsCostNoticeVisible);
    }

    /// <summary>The sparkline skips unmeasured points instead of plotting them on the baseline, which
    /// would draw an idle period the fleet never had.</summary>
    [AvaloniaFact]
    public void Sparkline_SkipsUnmeasuredPoints()
    {
        var telemetry = new FakeTelemetry();
        telemetry.Seed(new ResourceSample(DateTimeOffset.UtcNow, null, null, null), Array.Empty<AgentResourceUsage>());
        telemetry.Seed(new ResourceSample(DateTimeOffset.UtcNow, 50, 2.0, null), Array.Empty<AgentResourceUsage>());
        telemetry.Seed(new ResourceSample(DateTimeOffset.UtcNow, null, null, null), Array.Empty<AgentResourceUsage>());

        using var vm = new ResourceMonitorViewModel(new FakeAgents(), telemetry);

        // Three history entries, one measured → exactly one plotted point.
        Assert.Equal(3, telemetry.History.Count);
        Assert.Single(vm.CpuPoints);
    }

    private sealed class FakeTelemetry : ITelemetryService
    {
        private readonly List<ResourceSample> _history = new();
        private IReadOnlyList<AgentResourceUsage> _usage = Array.Empty<AgentResourceUsage>();
        public ResourceSample Current { get; private set; } = new(DateTimeOffset.UtcNow, null, null, null);
        public IReadOnlyList<ResourceSample> History => _history;
        public event Action? Sampled;

        public void Seed(ResourceSample current, IReadOnlyList<AgentResourceUsage> usage)
        {
            Current = current;
            _history.Add(current);
            _usage = usage;
        }

        public void RaiseSampled() => Sampled?.Invoke();
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
