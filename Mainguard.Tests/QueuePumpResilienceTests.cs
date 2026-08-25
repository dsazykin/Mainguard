using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Mainguard.Agents.UI.Services;
using Xunit;
using Proto = Mainguard.Protos.V1;

namespace Mainguard.Tests;

/// <summary>
/// The merge-queue pump's reconnect property, pinned — the sibling of
/// <see cref="AgentPumpResilienceTests"/> for the OTHER stream the control center is derived from.
///
/// <para><b>Why these exist.</b> ISSUES-LOG #11 (found live, 2026-08-20): a running session's Merge Queue
/// panel went to "Nothing queued" mid-session and stayed there, while the daemon's own database held every
/// row for the bound repo and every other RPC on the same connection kept succeeding. The daemon log showed
/// exactly ONE <c>StreamQueue</c> call, ended after 32 ms, and not one retry over the next 5m45s — a direct
/// contradiction of <c>QueuePumpAsync</c>'s own documented contract ("this is what makes an empty projection
/// live — it keeps trying"). The agent pump's reconnect had a regression test; the queue pump's did not, so
/// nothing anywhere pinned the property the whole rail depends on.</para>
///
/// <para>Both tests assert the user-visible consequence — does a later daemon push still reach
/// <see cref="DaemonBackedOrchestrator.GetQueue"/>, the projection the rail renders — rather than a
/// private field.</para>
/// </summary>
public sealed class QueuePumpResilienceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Never contacted: QueueStreamOverride replaces the stream, and DaemonClient does no I/O at
    // construction (its channel factory is lazy).
    private static DaemonClient UncontactedClient() =>
        new(() => GrpcChannel.ForAddress("http://127.0.0.1:1"), () => "token");

    private static Proto.QueueUpdate Update(string agentId)
    {
        var update = new Proto.QueueUpdate { MainSha = "abc123" };
        update.Entries.Add(new Proto.QueueEntry
        {
            AgentId = agentId,
            State = "Working",
            CanMerge = false,
            GateReason = "not verified",
        });
        return update;
    }

    /// <summary>A stream that ends cleanly after its batch — the exact shape the server takes on the
    /// teardown path that produced #11's <c>status=OK duration_ms=32</c> log line.</summary>
    private static async IAsyncEnumerable<Proto.QueueUpdate> OneShot(
        Proto.QueueUpdate update, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        yield return update;
        await Task.Yield();
    }

    /// <summary>A stream that faults instead of ending — a dropped connection rather than a clean close.</summary>
    private static async IAsyncEnumerable<Proto.QueueUpdate> Faulting(
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Yield();
        throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "dropped"));
#pragma warning disable CS0162 // unreachable — required to make this an iterator
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>
    /// The queue stream ENDS (cleanly, status OK — #11's observed signature). The pump must re-subscribe
    /// and the rail's projection must pick up what the new subscription carries. A stream that completes
    /// without error is the case a reader is most likely to treat as "we're done"; the reconnect loop
    /// exists precisely because the daemon side can end a call for reasons the client must not accept as
    /// final.
    /// </summary>
    [Fact]
    public async Task QueuePump_ReSubscribes_AfterTheStreamEndsCleanly()
    {
        var previousDelay = DaemonBackedOrchestrator.ReconnectDelay;
        DaemonBackedOrchestrator.ReconnectDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            using var client = UncontactedClient();
            using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);

            var subscribes = 0;
            var handles = new List<string>();
            var secondSubscribeDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            adapter.QueueStreamOverride = (handle, ct) =>
            {
                lock (handles) handles.Add(handle);
                var n = Interlocked.Increment(ref subscribes);
                return OneShot(Update(n == 1 ? "agent-a" : "agent-b"), ct);
            };
            adapter.Changed += () =>
            {
                if (adapter.GetQueue().Any(e => e.AgentId == "agent-b"))
                {
                    secondSubscribeDelivered.TrySetResult();
                }
            };

            using var cts = new CancellationTokenSource();
            var pump = adapter.RunQueuePumpForTestAsync("handle-a", cts.Token);

            var completed = await Task.WhenAny(secondSubscribeDelivered.Task, Task.Delay(Timeout));
            cts.Cancel();
            await pump;

            Assert.True(
                ReferenceEquals(completed, secondSubscribeDelivered.Task),
                $"the queue pump never re-subscribed after the stream ended (subscribes={subscribes}); "
                + "the Merge Queue panel reads 'Nothing queued' for the rest of the session with no banner "
                + "and no log line, recoverable only by restarting the app (ISSUES-LOG #11)");

            lock (handles)
            {
                Assert.All(handles, h => Assert.Equal("handle-a", h));
            }
        }
        finally
        {
            DaemonBackedOrchestrator.ReconnectDelay = previousDelay;
        }
    }

    /// <summary>The other half: a stream that FAULTS (a dropped connection) must also be retried, and the
    /// retry must survive the fault rather than ending the pump task.</summary>
    [Fact]
    public async Task QueuePump_ReSubscribes_AfterTheStreamFaults()
    {
        var previousDelay = DaemonBackedOrchestrator.ReconnectDelay;
        DaemonBackedOrchestrator.ReconnectDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            using var client = UncontactedClient();
            using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);

            var subscribes = 0;
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            adapter.QueueStreamOverride = (handle, ct) =>
                Interlocked.Increment(ref subscribes) == 1 ? Faulting(ct) : OneShot(Update("agent-b"), ct);
            adapter.Changed += () =>
            {
                if (adapter.GetQueue().Any(e => e.AgentId == "agent-b")) delivered.TrySetResult();
            };

            using var cts = new CancellationTokenSource();
            var pump = adapter.RunQueuePumpForTestAsync("handle-a", cts.Token);

            var completed = await Task.WhenAny(delivered.Task, Task.Delay(Timeout));
            cts.Cancel();
            await pump;

            Assert.True(
                ReferenceEquals(completed, delivered.Task),
                $"the queue pump never re-subscribed after the stream faulted (subscribes={subscribes})");
        }
        finally
        {
            DaemonBackedOrchestrator.ReconnectDelay = previousDelay;
        }
    }

    /// <summary>A throwing <c>Changed</c> subscriber must not take the queue stream down with it. The
    /// appliers run ON the pump thread and raise synchronously, so an unisolated raise propagated out of
    /// the <c>await foreach</c> — the reconnect loop then caught it and re-subscribed, silently dropping
    /// every push in the gap and cycling the stream for as long as the handler kept faulting.
    /// <c>ApplyAgentEvent</c> was hardened against exactly this; <c>ApplyQueueUpdate</c> was not.</summary>
    [Fact]
    public void ApplyQueueUpdate_SurvivesAThrowingChangedSubscriber()
    {
        using var client = UncontactedClient();
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);

        adapter.Changed += () => throw new InvalidOperationException("a bad UI subscriber");

        var ex = Record.Exception(() => adapter.ApplyQueueUpdate(Update("agent-a")));

        Assert.Null(ex);
        Assert.Contains(adapter.GetQueue(), e => e.AgentId == "agent-a");
    }
}
