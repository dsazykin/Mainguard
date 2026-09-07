using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// A coordinator message that did not go must not be silently swallowed along with the text.
///
/// <para><b>The defect.</b> The composer is cleared BEFORE the send, which is what makes the box feel like
/// a chat input — and the send routed through a service that caught every transport failure and returned
/// normally. A message that never left the machine therefore vanished completely: composer empty,
/// transcript unchanged, nothing said anywhere. A connection banner does not tell anyone which sentence
/// was lost, and the human's own words are the one thing this surface must not lose.</para>
/// </summary>
public sealed class CoordinatorComposerDeliveryTests
{
    private const string Refusal = "no coordinator is running";

    [Fact]
    public async Task Send_PutsTheTextBackAndSaysWhy_WhenTheSendFails()
    {
        var coordinator = new FakeCoordinator { SendFailure = new InvalidOperationException(Refusal) };
        var vm = new CoordinatorPanelViewModel(coordinator) { ComposerText = "  rebase onto main  " };

        await vm.SendCommand.ExecuteAsync(null);

        Assert.True(vm.HasSendError);
        Assert.Contains(Refusal, vm.SendErrorText, StringComparison.Ordinal);

        // Verbatim (trimmed the way it was sent), so retrying is one keystroke rather than retyping.
        Assert.Equal("rebase onto main", vm.ComposerText);
    }

    [Fact]
    public async Task Send_ClearsTheComposerAndRaisesNoError_WhenTheSendSucceeds()
    {
        var coordinator = new FakeCoordinator();
        var vm = new CoordinatorPanelViewModel(coordinator) { ComposerText = "rebase onto main" };

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "rebase onto main" }, coordinator.Sent);
        Assert.Equal("", vm.ComposerText);
        Assert.False(vm.HasSendError);
    }

    /// <summary>A failure must not overwrite what the human has started typing since. Getting the old
    /// message back is worth less than not losing the new one.</summary>
    [Fact]
    public async Task Send_DoesNotOverwriteAFreshlyTypedMessage_WhenTheOldOneFailed()
    {
        var coordinator = new FakeCoordinator();
        CoordinatorPanelViewModel? vm = null;
        coordinator.OnSend = () =>
        {
            vm!.ComposerText = "actually, stop";       // the human types while the send is in flight
            throw new InvalidOperationException(Refusal);
        };

        vm = new CoordinatorPanelViewModel(coordinator) { ComposerText = "rebase onto main" };
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("actually, stop", vm.ComposerText);
        Assert.True(vm.HasSendError);
    }

    private sealed class FakeCoordinator : ICoordinatorService
    {
        public List<string> Sent { get; } = new();

        public Exception? SendFailure { get; set; }

        /// <summary>Runs inside the send, so a test can model the human typing mid-flight.</summary>
        public Action? OnSend { get; set; }

        public Task SendAsync(string text)
        {
            OnSend?.Invoke();
            if (SendFailure is { } ex)
            {
                return Task.FromException(ex);
            }

            Sent.Add(text);
            return Task.CompletedTask;
        }

        public IReadOnlyList<ChatLine> GetTranscript() => Array.Empty<ChatLine>();
        public IReadOnlyList<TaskPlan> GetPendingPlans() => Array.Empty<TaskPlan>();
        public TaskPlan? GetPlan(string planId) => null;
        public IReadOnlyList<WorkerPlanCard> GetWorkerPlans() => Array.Empty<WorkerPlanCard>();
        public OrchestrationBackpressure GetBackpressure() => OrchestrationBackpressure.None;
        public PlanModeView GetPlanMode() => new(true, "");
        public Task SetPlanModeAsync(bool enabled) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
        public Task SubmitPlanDecisionAsync(string planId, bool approve, string? feedback = null) =>
            Task.CompletedTask;
        public Task RequestNewPlanAsync(string planId, string guidance) => Task.CompletedTask;
    }
}
