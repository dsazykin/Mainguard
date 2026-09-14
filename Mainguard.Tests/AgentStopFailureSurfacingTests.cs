using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.UI.Editions;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.UI.Editions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// A stop that FAILED must never be reported as a stop that happened.
///
/// <para><b>The defect.</b> <c>DaemonBackedOrchestrator.EndAgentAsync</c> caught every exception and
/// returned normally, on the reasoning that a down daemon is already reported through
/// <c>ConnectionState</c>. That is true of the connection and false of the act: <c>StopAgent</c> refuses
/// for reasons that have nothing to do with reachability (unknown session, jail mid-teardown, kill switch
/// engaged, deadline). Swallowing made three surfaces claim an agent had stopped while it kept running and
/// kept its slot against the worker cap — the escalated-worker card, whose error branch was structurally
/// unreachable; the exit sweep, which logged a clean stop for every jail; and Restart, which went on to a
/// spawn the daemon then refused, so the only message a human ever saw was about the start.</para>
///
/// <para>These pin the three call sites over a seam that refuses, which is the state that produced
/// nothing at all before. The adapter's own half — that it propagates rather than swallows — is a change
/// to a <c>try/catch</c> and is exercised transitively by every one of them.</para>
/// </summary>
public sealed class AgentStopFailureSurfacingTests
{
    private const string Refusal = "the daemon is briefly holding this agent";

    /// <summary>The escalated-worker card: End refuses, so the card says so AND stays in its confirm
    /// state, because the worker is still there to be ended.</summary>
    [Fact]
    public async Task EscalatedCard_SaysTheWorkerIsStillHoldingItsSlot_WhenEndIsRefused()
    {
        var plan = new WorkerPlanCard(
            "plan-1", "agent-a", "coordinator-1", "Add the thing", new[] { "src/" },
            "carefully", "xunit", 5m, DateTimeOffset.UtcNow,
            "Escalated", 3, 0, 3, "not that approach",
            SupersedesPlanId: "", PreviousScope: null, RescopeCount: 0, NewPlanRequested: false);

        var card = new EscalatedPlanViewModel(
            plan, endWorker: _ => Task.FromException(new InvalidOperationException(Refusal)));

        card.BeginEndCommand.Execute(null);
        await card.ConfirmEndCommand.ExecuteAsync(null);

        Assert.True(card.HasEndError);
        Assert.Contains(Refusal, card.EndErrorText, StringComparison.Ordinal);
        Assert.Contains("still holding its slot", card.EndErrorText, StringComparison.Ordinal);

        // Still armed: the worker was not ended, so the action that ends it must still be on screen.
        Assert.True(card.IsConfirmingEnd);
        Assert.False(card.IsEnding);
    }

    /// <summary>The Resources row: End refuses, so the human who confirmed it is told, rather than
    /// watching a dialog close over a row that never changes.</summary>
    [Fact]
    public async Task ResourceMonitor_ToastsTheRefusal_WhenEndIsRefused()
    {
        var toasts = new List<(string Message, bool IsWarning)>();
        var previous = ProComposition.ShowShellToast;
        ProComposition.ShowShellToast = (m, w) => toasts.Add((m, w));
        try
        {
            var agents = new RefusingAgentService(new InvalidOperationException(Refusal));
            var telemetry = new MockOrchestrator();
            var vm = new ResourceMonitorViewModel(agents, telemetry);

            vm.RequestEnd(new AgentUsageRowViewModel("agent-a", vm));
            await vm.ConfirmEndCommand.ExecuteAsync(null);

            var toast = Assert.Single(toasts);
            Assert.True(toast.IsWarning);
            Assert.Contains(Refusal, toast.Message, StringComparison.Ordinal);
            Assert.Contains("still running", toast.Message, StringComparison.Ordinal);

            // The stop was attempted — the surfacing is not standing in for a call that never happened.
            Assert.Equal(new[] { "agent-a" }, agents.Attempted);
        }
        finally
        {
            ProComposition.ShowShellToast = previous;
        }
    }

    /// <summary>
    /// The exit sweep: every agent is still attempted (one refusal must not strand the rest), and the
    /// sweep then reports the failure by NAME instead of finishing quietly. <c>AppShutdownSequence</c>
    /// catches this and logs it, which is what makes the exit record honest — before, the log's only
    /// line was "stopping N live agent(s)", followed by silence, followed by "complete".
    /// </summary>
    [Fact]
    public async Task ExitSweep_NamesTheAgentsItCouldNotStop_AndStillAttemptsThemAll()
    {
        var agents = new RefusingAgentService(new InvalidOperationException(Refusal))
        {
            Live =
            {
                new AgentInfo("worker-1", "claude-code", "agent/worker-1",
                    AgentLifecycleState.Working, "", DateTimeOffset.UtcNow),
                new AgentInfo("worker-2", "claude-code", "agent/worker-2",
                    AgentLifecycleState.Working, "", DateTimeOffset.UtcNow),
            },
        };

        var mock = new MockOrchestrator();
        var vm = new ControlCenterViewModel(
            new OrchestratorServices(agents, mock, mock, mock, mock, mock, Owner: null));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ((IAgentPlatformSurface)vm).StopAllAgentsAsync(CancellationToken.None));

        Assert.Equal(new[] { "worker-1", "worker-2" }, agents.Attempted.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Contains("worker-1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("worker-2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("still running", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>An agent seam whose stop always refuses — the state the swallow made invisible.</summary>
    private sealed class RefusingAgentService : IAgentService
    {
        private readonly Exception _failure;

        public RefusingAgentService(Exception failure) => _failure = failure;

        public List<AgentInfo> Live { get; } = new();

        public List<string> Attempted { get; } = new();

        public IReadOnlyList<AgentInfo> ListAgents() => Live.ToArray();

        public event Action<AgentEvent>? EventReceived { add { } remove { } }

        public Task EndAgentAsync(string agentId)
        {
            Attempted.Add(agentId);
            return Task.FromException(_failure);
        }

        public Task PauseAgentAsync(string agentId) => Task.CompletedTask;
        public Task ResumeAgentAsync(string agentId) => Task.CompletedTask;
        public Task SendPromptAsync(string agentId, string prompt) => Task.CompletedTask;
        public IReadOnlyList<string> GetQueuedPrompts(string agentId) => Array.Empty<string>();
        public Task CancelQueuedPromptAsync(string agentId, int index) => Task.CompletedTask;
        public IReadOnlyList<string> GetTerminalTail(string agentId) => Array.Empty<string>();
        public IReadOnlyList<(string Step, bool Done)> GetPlanTree(string agentId) =>
            Array.Empty<(string, bool)>();
    }
}
