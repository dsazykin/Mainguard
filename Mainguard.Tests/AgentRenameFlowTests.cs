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
/// The rename path from the row's menu item to the name the rail renders.
///
/// <para>It is driven through the SHIPPED view model — the row command, the pending-prompt card, the
/// confirm — rather than by calling the store directly, because the parts that can go wrong are the
/// joins: a card that opens on the wrong agent, a confirm that stores the stale box contents, a reset
/// that does not put the derived name back.</para>
/// </summary>
public class AgentRenameFlowTests
{
    private static AgentInfo Agent(string id, string title = "", string userName = "") =>
        new(id, "claude-code", $"agent/{id}", AgentLifecycleState.Working, "working",
            DateTimeOffset.UtcNow, Role: AgentRoles.Managed, Title: title, UserName: userName);

    [AvaloniaFact]
    public void RenamingFromTheMenu_OpensACardForThatAgent_SeededWithTheNameInForce()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var agents = new RenamableAgents(new[] { Agent("agent-a", title: "rewrite the diff gutter") });
        using var vm = new ControlCenterViewModel(
            new OrchestratorServices(agents, mock, mock, mock, mock, mock, Owner: null));

        Row(vm, "agent-a").RenameCommand.Execute(null);

        Assert.NotNull(vm.AgentAction);
        Assert.Equal(AgentActionKind.Rename, vm.AgentAction!.Kind);
        Assert.Equal("agent-a", vm.AgentAction.AgentId);
        Assert.True(vm.AgentAction.NeedsInput);

        // Seeded with what the person is looking at, not an empty box.
        Assert.Equal("rewrite the diff gutter", vm.AgentActionInput);
    }

    [AvaloniaFact]
    public async Task Confirming_StoresTheName_AndTheRowRendersIt()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var agents = new RenamableAgents(new[] { Agent("agent-a", title: "rewrite the diff gutter") });
        using var vm = new ControlCenterViewModel(
            new OrchestratorServices(agents, mock, mock, mock, mock, mock, Owner: null));

        Row(vm, "agent-a").RenameCommand.Execute(null);
        vm.AgentActionInput = "gutter work";
        await vm.ConfirmAgentActionCommand.ExecuteAsync(null);

        Assert.Equal("gutter work", agents.NameOf("agent-a"));
        Assert.Equal("gutter work", Row(vm, "agent-a").DisplayName);
        Assert.True(Row(vm, "agent-a").IsRenamed);

        // And the card is gone, rather than left open over a rename that already happened.
        Assert.Null(vm.AgentAction);
        Assert.False(vm.IsAgentActionPending);
    }

    [AvaloniaFact]
    public async Task Cancelling_StoresNothing()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var agents = new RenamableAgents(new[] { Agent("agent-a", title: "rewrite the diff gutter") });
        using var vm = new ControlCenterViewModel(
            new OrchestratorServices(agents, mock, mock, mock, mock, mock, Owner: null));

        Row(vm, "agent-a").RenameCommand.Execute(null);
        vm.AgentActionInput = "gutter work";
        vm.CancelAgentActionCommand.Execute(null);

        Assert.Equal("", agents.NameOf("agent-a"));
        Assert.Equal("rewrite the diff gutter", Row(vm, "agent-a").DisplayName);
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public void ResetName_IsOfferedOnlyWhenThereIsANameToReset()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var agents = new RenamableAgents(new[]
        {
            Agent("plain", title: "rewrite the diff gutter"),
            Agent("named", title: "rewrite the diff gutter", userName: "gutter work"),
        });
        using var vm = new ControlCenterViewModel(
            new OrchestratorServices(agents, mock, mock, mock, mock, mock, Owner: null));

        // Otherwise the item claims to undo something that never happened.
        Assert.False(Row(vm, "plain").ResetNameCommand.CanExecute(null));
        Assert.True(Row(vm, "named").ResetNameCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void ResetName_PutsTheDerivedNameBack()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var agents = new RenamableAgents(new[]
        {
            Agent("agent-a", title: "rewrite the diff gutter", userName: "gutter work"),
        });
        using var vm = new ControlCenterViewModel(
            new OrchestratorServices(agents, mock, mock, mock, mock, mock, Owner: null));

        Assert.Equal("gutter work", Row(vm, "agent-a").DisplayName);

        Row(vm, "agent-a").ResetNameCommand.Execute(null);

        // Back to the live brief — the whole point of the reset being available at all.
        Assert.Equal("rewrite the diff gutter", Row(vm, "agent-a").DisplayName);
        Assert.False(Row(vm, "agent-a").IsRenamed);
    }

    private static AgentRowViewModel Row(ControlCenterViewModel vm, string agentId) =>
        vm.Agents.Single(r => r.AgentId == agentId);

    /// <summary>An agent seam that really honours RenameAgent, so the flow is asserted end to end
    /// rather than against a stub that swallows the name.</summary>
    private sealed class RenamableAgents : IAgentService
    {
        private readonly Dictionary<string, AgentInfo> _agents;

        public RenamableAgents(IReadOnlyList<AgentInfo> agents) =>
            _agents = agents.ToDictionary(a => a.AgentId, a => a, StringComparer.Ordinal);

        public string NameOf(string agentId) => _agents[agentId].UserName;

        public IReadOnlyList<AgentInfo> ListAgents() => _agents.Values.ToArray();

        public string RenameAgent(string agentId, string? name)
        {
            // Cleaned the way the real store cleans it, so the test sees what would really be kept.
            var cleaned = Mainguard.Agents.UI.Services.AgentNameStore.Clean(name);
            _agents[agentId] = _agents[agentId] with { UserName = cleaned };
            return cleaned;
        }

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
