using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// FAILS BEFORE / PASSES AFTER — the reported defect: "if you switch from coordinator to an agent the
/// coordinator will remain highlighted in the sidebar."
///
/// <para>It was two halves, and only both together produce the symptom. Agent rows carried no selection
/// state at all — <c>AgentRowViewModel</c> had no <c>IsSelected</c> and the row button bound nothing to
/// its <c>active</c> class — so selecting an agent could never light a row. Meanwhile the Coordinator
/// SECTION row stayed lit, because showing an agent is routed as "the Coordinator section with a
/// different panel inside it". The highlight therefore sat on the coordinator with nothing able to take
/// it away. These pin the control-centre half; the section-rail half is
/// <see cref="MainWindowSectionHighlightTests"/>.</para>
/// </summary>
public class AgentRailSelectionTests
{
    private static AgentInfo Agent(string id) =>
        new(id, "claude-code", $"agent/{id}", AgentLifecycleState.Working, "working",
            DateTimeOffset.UtcNow, Role: AgentRoles.Managed);

    private static ControlCenterViewModel Build(MockOrchestrator mock, params string[] ids) =>
        new(new OrchestratorServices(
            new FixedAgents(ids.Select(Agent).ToArray()), mock, mock, mock, mock, mock, Owner: null));

    [AvaloniaFact]
    public void SelectingAnAgent_LightsThatRow_AndOnlyThatRow()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        using var vm = Build(mock, "agent-a", "agent-b");

        vm.SelectAgent("agent-a");

        Assert.True(Row(vm, "agent-a").IsSelected);
        Assert.False(Row(vm, "agent-b").IsSelected);
    }

    [AvaloniaFact]
    public void SwitchingBetweenAgents_MovesTheHighlight()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        using var vm = Build(mock, "agent-a", "agent-b");

        vm.SelectAgent("agent-a");
        vm.SelectAgent("agent-b");

        Assert.False(Row(vm, "agent-a").IsSelected);
        Assert.True(Row(vm, "agent-b").IsSelected);
    }

    /// <summary>The other direction of the reported bug: going BACK to the coordinator has to release
    /// the agent's row, or the rail shows two things selected at once.</summary>
    [AvaloniaFact]
    public void FocusingTheCoordinator_ReleasesEveryAgentRow_AndClearsTheSelectedId()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        using var vm = Build(mock, "agent-a", "agent-b");

        vm.SelectAgent("agent-a");
        vm.FocusCoordinator();

        Assert.All(vm.Agents, row => Assert.False(row.IsSelected));

        // Cleared, not merely shadowed by IsCoordinatorFocus: two answers to "what is being viewed" is
        // how the highlight got out of step in the first place.
        Assert.Null(vm.SelectedAgentId);
        Assert.True(vm.IsCoordinatorFocus);
    }

    /// <summary>A refresh rebuilds and reorders rows. The highlight has to survive it — otherwise the
    /// selection silently drops every time the agent stream ticks.</summary>
    [AvaloniaFact]
    public void ARefresh_KeepsTheHighlightOnTheSelectedAgent()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        using var vm = Build(mock, "agent-a", "agent-b");

        vm.SelectAgent("agent-b");
        vm.RefreshAgents();

        Assert.True(Row(vm, "agent-b").IsSelected);
        Assert.False(Row(vm, "agent-a").IsSelected);
    }

    private static AgentRowViewModel Row(ControlCenterViewModel vm, string agentId) =>
        vm.Agents.Single(r => r.AgentId == agentId);

    /// <summary>An agent seam over a fixed list — the rail's selection is the only variable.</summary>
    private sealed class FixedAgents : IAgentService
    {
        private readonly IReadOnlyList<AgentInfo> _agents;

        public FixedAgents(IReadOnlyList<AgentInfo> agents) => _agents = agents;

        public IReadOnlyList<AgentInfo> ListAgents() => _agents;

        public event Action<AgentEvent>? EventReceived { add { } remove { } }

        public Task SendPromptAsync(string agentId, string prompt) => Task.CompletedTask;

        public IReadOnlyList<string> GetQueuedPrompts(string agentId) => Array.Empty<string>();

        public Task CancelQueuedPromptAsync(string agentId, int index) => Task.CompletedTask;

        public IReadOnlyList<string> GetTerminalTail(string agentId) => Array.Empty<string>();

        public IReadOnlyList<(string Step, bool Done)> GetPlanTree(string agentId)
            => Array.Empty<(string, bool)>();

        public Task PauseAgentAsync(string agentId) => Task.CompletedTask;

        public Task ResumeAgentAsync(string agentId) => Task.CompletedTask;

        public Task EndAgentAsync(string agentId) => Task.CompletedTask;
    }
}
