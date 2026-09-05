using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Live bug (found 2026-08-20 during a full manual click-through): a fresh spawn's queue entry sat in
/// <see cref="IMergeQueueService.GetQueue"/>'s answer correctly the whole time — the daemon-side
/// <c>EnsureEntry</c>/<c>MergeQueue.Changed</c>/<c>StreamQueue</c> push all worked — but the rail never
/// rendered it until an UNRELATED event (an AgentEvent, a coordinator or kill-switch change) happened to
/// call <see cref="QueueRailViewModel.Refresh"/> again. <c>ControlCenterViewModel</c> subscribed to the
/// coordinator's and kill switch's <c>Changed</c> events but never to the queue service's own — so a
/// queue-only change (the common case: an agent spawns or commits with nothing else happening) had no path
/// to a rail refresh at all. This test isolates exactly that: it fires ONLY <see
/// cref="IMergeQueueService.Changed"/>, via a proxy that never raises any other event, and asserts the
/// rail picks up a queue change it could not have learned about any other way.
/// </summary>
public class QueueChangedRefreshesRailTests
{
    [AvaloniaFact]
    public async Task QueueChanged_Alone_RefreshesTheRail_WithNoAgentEventOrOtherSignal()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var proxy = new QueueOnlyChangeProxy(mock);
        var bundle = new OrchestratorServices(mock, proxy, mock, mock, mock, mock, mock);
        using var vm = new ControlCenterViewModel(bundle);

        Assert.Empty(vm.Queue.Entries);

        // Simulate the daemon-side EnsureEntry push landing: GetQueue() now answers with a fresh entry,
        // and ONLY the queue's own Changed fires — no AgentEvent, no coordinator/kill Changed.
        proxy.Entries = new[]
        {
            new QueueEntry(
                "fresh-agent", "fresh-agent", "agent/fresh-agent", WorkerMergeState.Working,
                "not verified yet", Verification: null, FlaggedItems: Array.Empty<FlaggedItem>()),
        };
        proxy.RaiseChangedOnly();

        // OnChanged marshals through Dispatcher.UIThread.Post; give the loop a chance to drain.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Single(vm.Queue.Entries);
        Assert.Equal("fresh-agent", vm.Queue.Entries[0].AgentId);
    }

    /// <summary>Delegates every read/action to the inner mock but owns its OWN <c>Changed</c> event and
    /// its OWN <see cref="GetQueue"/> answer, so a test can fire a queue-only change signal that the
    /// inner mock's coupled AgentEvent+Changed pattern cannot isolate.</summary>
    private sealed class QueueOnlyChangeProxy : IMergeQueueService
    {
        private readonly IMergeQueueService _inner;

        public QueueOnlyChangeProxy(IMergeQueueService inner) => _inner = inner;

        public IReadOnlyList<QueueEntry> Entries { get; set; } = Array.Empty<QueueEntry>();

        public event Action? Changed;

        public void RaiseChangedOnly() => Changed?.Invoke();

        public string MainSha => _inner.MainSha;
        public IReadOnlyList<QueueEntry> GetQueue() => Entries;
        public bool CanMerge(string agentId, out string reason) => _inner.CanMerge(agentId, out reason);
        public Task<VerificationOutcome> RunVerificationAsync(string agentId) => _inner.RunVerificationAsync(agentId);
        public Task<VerificationLog> GetVerificationLogAsync(string agentId) => _inner.GetVerificationLogAsync(agentId);
        public Task<MergeOutcome> ConfirmMergeAsync(string agentId) => _inner.ConfirmMergeAsync(agentId);
        public Task AcknowledgeFlaggedChangeAsync(string agentId, string itemId) => _inner.AcknowledgeFlaggedChangeAsync(agentId, itemId);
        public Task<QueueEntryDiscardOutcome> DiscardEntryAsync(string agentId, string reason) => _inner.DiscardEntryAsync(agentId, reason);
        public Task<QueueEntryRejectOutcome> RejectEntryAsync(string agentId, string reason) => _inner.RejectEntryAsync(agentId, reason);
        public Task ClearStalledVerificationAsync(string agentId) => _inner.ClearStalledVerificationAsync(agentId);
        public Task ResolveConflictWithAgentAsync(string agentId) => _inner.ResolveConflictWithAgentAsync(agentId);
        public Task AbortRebaseAsync(string agentId) => _inner.AbortRebaseAsync(agentId);
        public Task<QueueEntryResumeOutcome> ResumeEntryAsync(string agentId, string agentKind) => _inner.ResumeEntryAsync(agentId, agentKind);
    }
}
