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
/// The spend ledger's re-subscribe property, pinned.
///
/// <para><b>Why these exist.</b> <c>GatewayService.StreamSpend</c> is a REPLAY stream: the daemon walks the
/// whole spend ledger from the beginning for each subscriber and then follows it live. The client
/// ACCUMULATES what arrives, which is right for one subscription and wrong for two — and the spend pump
/// re-subscribes on every dropped stream (a daemon restart, a tier-1 auto-update bouncing
/// <c>mainguardd</c>, an HTTP/2 reset). Each of those therefore added one full ledger to the Resources
/// rail's running total and to every per-agent row, so a long session on a flaky daemon reported a
/// multiple of what was actually spent with no way for a human to tell.</para>
///
/// <para>Both tests assert the user-visible number — the rail's fleet spend and the monitor's per-agent
/// row — rather than a private field, and both drive the real pump so the reset is exercised where it
/// lives rather than being called directly.</para>
/// </summary>
public sealed class SpendLedgerReplayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Never contacted: SpendStreamOverride replaces the stream, and DaemonClient does no I/O at
    // construction (its channel factory is lazy).
    private static DaemonClient UncontactedClient() =>
        new(() => GrpcChannel.ForAddress("http://127.0.0.1:1"), () => "token");

    /// <summary>One ledger: two entries totalling $3.00 / 300 tokens, both attributed to one agent. This
    /// is what the daemon replays to EVERY subscriber, not a delta.</summary>
    /// <param name="thenHold">
    /// False ends the stream cleanly after the replay — the shape a daemon restart or a stream reset
    /// takes, and what makes the pump re-subscribe and receive the same ledger a second time. True holds
    /// the subscription open, which is how the assertions read a SETTLED projection: a third subscribe
    /// would zero the accumulators again (that reset IS the fix), so the test would end up measuring the
    /// reset rather than the double count it exists to catch.
    /// </param>
    private static async IAsyncEnumerable<Proto.SpendSample> LedgerReplay(
        string agentId, bool thenHold, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        yield return new Proto.SpendSample { AgentId = agentId, UsdMicrosSpent = 2_000_000, TokensSpent = 200 };
        yield return new Proto.SpendSample { AgentId = agentId, UsdMicrosSpent = 1_000_000, TokensSpent = 100 };

        if (!thenHold)
        {
            await Task.Yield();
            yield break;
        }

        var forever = new TaskCompletionSource();
        using (ct.Register(() => forever.TrySetResult()))
        {
            await forever.Task.ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Marks the agent METERED, which is what makes the rail report a spend figure at all
    /// (an unmetered fleet reports null so it can say "not measured" instead of "$0.00").</summary>
    private static Proto.AgentResourcesSnapshot MeteredReading(string agentId)
    {
        var snapshot = new Proto.AgentResourcesSnapshot();
        snapshot.Agents.Add(new Proto.AgentResourceReading
        {
            AgentId = agentId,
            CpuPercent = 12.5,
            MemBytes = 512L * 1024 * 1024,
            Metered = true,
        });
        return snapshot;
    }

    private static Proto.AgentEvent OneAgentSnapshot(string agentId)
    {
        var snapshot = new Proto.AgentSnapshot();
        snapshot.Agents.Add(new Proto.AgentInfo
        {
            AgentId = agentId,
            AgentKind = "claude-code",
            State = "Working",
            Role = "worker",
        });
        return new Proto.AgentEvent { AgentId = agentId, Snapshot = snapshot };
    }

    /// <summary>
    /// The fleet total after two subscriptions must be the ledger, not twice the ledger. Before the reset,
    /// the second replay landed on top of the first and the Resources rail read $6.00 for $3.00 of spend.
    /// </summary>
    [Fact]
    public async Task SpendPump_DoesNotDoubleTheFleetTotal_WhenTheStreamReSubscribes()
    {
        var previousDelay = DaemonBackedOrchestrator.ReconnectDelay;
        DaemonBackedOrchestrator.ReconnectDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            using var client = UncontactedClient();
            using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
            adapter.ApplyResourceSnapshot(MeteredReading("agent-a"));

            var subscribes = 0;
            var samples = 0;
            var secondReplayDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            adapter.Sampled += () =>
            {
                // Four raises = both replays fully applied. Read off the projection's own signal rather
                // than a delay, so the test is not timing-dependent.
                if (Interlocked.Increment(ref samples) >= 4) secondReplayDone.TrySetResult();
            };
            adapter.SpendStreamOverride = ct =>
                LedgerReplay("agent-a", thenHold: Interlocked.Increment(ref subscribes) > 1, ct);

            using var cts = new CancellationTokenSource();
            var pump = adapter.RunSpendPumpForTestAsync(cts.Token);

            var completed = await Task.WhenAny(secondReplayDone.Task, Task.Delay(Timeout));
            cts.Cancel();
            await pump;

            Assert.True(
                ReferenceEquals(completed, secondReplayDone.Task),
                $"the spend pump never re-subscribed (subscribes={subscribes}); this test cannot say "
                + "anything about double counting without a second subscription");

            Assert.Equal(3.00m, adapter.Current.SpendTodayUsd);
        }
        finally
        {
            DaemonBackedOrchestrator.ReconnectDelay = previousDelay;
        }
    }

    /// <summary>The per-row half: the monitor's per-agent spend is the same figure after a re-subscribe.
    /// Fleet total and rows accumulate through separate state, so one being right does not imply the
    /// other — and both were wrong.</summary>
    [Fact]
    public async Task SpendPump_DoesNotDoubleAPerAgentRow_WhenTheStreamReSubscribes()
    {
        var previousDelay = DaemonBackedOrchestrator.ReconnectDelay;
        DaemonBackedOrchestrator.ReconnectDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            using var client = UncontactedClient();
            using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
            adapter.ApplyAgentEvent(OneAgentSnapshot("agent-a"));
            adapter.ApplyResourceSnapshot(MeteredReading("agent-a"));

            var subscribes = 0;
            var samples = 0;
            var secondReplayDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            adapter.Sampled += () =>
            {
                // Four raises = both replays fully applied. Read off the projection's own signal rather
                // than a delay, so the test is not timing-dependent.
                if (Interlocked.Increment(ref samples) >= 4) secondReplayDone.TrySetResult();
            };
            adapter.SpendStreamOverride = ct =>
                LedgerReplay("agent-a", thenHold: Interlocked.Increment(ref subscribes) > 1, ct);

            using var cts = new CancellationTokenSource();
            var pump = adapter.RunSpendPumpForTestAsync(cts.Token);

            var completed = await Task.WhenAny(secondReplayDone.Task, Task.Delay(Timeout));
            cts.Cancel();
            await pump;

            Assert.True(ReferenceEquals(completed, secondReplayDone.Task), "the spend pump never re-subscribed");

            var row = Assert.Single(adapter.GetAgentUsage(), u => u.AgentId == "agent-a");
            Assert.Equal(3.00m, row.SpendUsd);
        }
        finally
        {
            DaemonBackedOrchestrator.ReconnectDelay = previousDelay;
        }
    }
}
