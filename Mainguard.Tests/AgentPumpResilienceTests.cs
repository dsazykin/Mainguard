using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Mainguard.Agents.UI.Services;
using Proto = Mainguard.Protos.V1;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The agent-event pump is the stream every other agent surface is derived from — the rail, the
/// coordinator card, the spawn watchdogs, the egress-block prompt. It was the ONE pump in
/// <see cref="DaemonBackedOrchestrator"/> with no <c>ReconnectLoopAsync</c>: a bare
/// <c>await foreach</c> inside <c>catch (Exception) { }</c>. Any fault ended the task for good, and
/// <c>Start()</c> guards on <c>_agentPump is not null</c>, so nothing could restart it for the
/// lifetime of the process. Nothing reported it either — the connection itself is fine, so
/// <c>ConnectionState</c> stays <c>Connected</c> and no banner appears.
///
/// <para>Both tests here assert the USER-VISIBLE consequence, not a private field: after the fault,
/// does a subsequent daemon event still reach the projection the rail renders
/// (<see cref="DaemonBackedOrchestrator.ListAgents"/>) and still raise the <c>Changed</c>
/// notification that redraws it?</para>
/// </summary>
public sealed class AgentPumpResilienceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The pump never touches this client in these tests (AgentEventStreamOverride replaces the
    // stream), and DaemonClient does no I/O at construction — the channel factory is lazy.
    private static DaemonClient UncontactedClient() =>
        new(() => GrpcChannel.ForAddress("http://127.0.0.1:1"), () => "token");

    private static Proto.AgentEvent Snapshot(params string[] agentIds)
    {
        var snapshot = new Proto.AgentSnapshot();
        foreach (var id in agentIds)
        {
            snapshot.Agents.Add(new Proto.AgentInfo
            {
                AgentId = id,
                AgentKind = "claude-code",
                State = "Working",
                Role = string.Empty,
            });
        }

        return new Proto.AgentEvent { AgentId = agentIds.FirstOrDefault() ?? string.Empty, Snapshot = snapshot };
    }

    /// <summary>
    /// A stream that ends after yielding its batch, so a re-subscribe is observable as a second
    /// invocation. This is the shape the real <c>DaemonClient.StreamAgentEventsAsync</c> takes when it
    /// gives up — it <c>yield break</c>s on a terminal <c>PermissionDenied</c> (a rotated daemon token),
    /// which is precisely the case the missing reconnect made permanent.
    /// </summary>
    private static async IAsyncEnumerable<Proto.AgentEvent> OneShot(
        IEnumerable<Proto.AgentEvent> batch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var e in batch)
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
            await Task.Yield();
        }
    }

    /// <summary>
    /// FAILS BEFORE / PASSES AFTER. The stream ends (the daemon's terminal-status path); the pump must
    /// re-subscribe and the agent list must pick up what the new subscription carries. Before the fix
    /// the pump's task simply completed and the rail stayed frozen on "agent-a" forever.
    /// </summary>
    [Fact]
    public async Task AgentPump_ReSubscribes_AfterTheStreamEnds_SoTheRailKeepsUpdating()
    {
        var previousDelay = DaemonBackedOrchestrator.ReconnectDelay;
        DaemonBackedOrchestrator.ReconnectDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            using var client = UncontactedClient();
            using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);

            var subscribes = 0;
            var secondSubscribeDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            adapter.AgentEventStreamOverride = ct =>
            {
                // First subscription carries agent-a, every later one carries agent-b.
                var n = Interlocked.Increment(ref subscribes);
                return OneShot(new[] { Snapshot(n == 1 ? "agent-a" : "agent-b") }, ct);
            };
            adapter.Changed += () =>
            {
                if (adapter.ListAgents().Any(a => a.AgentId == "agent-b"))
                {
                    secondSubscribeDelivered.TrySetResult();
                }
            };

            using var cts = new CancellationTokenSource();
            var pump = adapter.RunAgentPumpForTestAsync(cts.Token);

            var completed = await Task.WhenAny(secondSubscribeDelivered.Task, Task.Delay(Timeout));
            cts.Cancel();
            await pump;

            Assert.True(
                ReferenceEquals(completed, secondSubscribeDelivered.Task),
                $"the agent pump never re-subscribed after the stream ended (subscribes={subscribes}); "
                + "the rail is frozen on its last snapshot for the lifetime of the process, with no banner");
        }
        finally
        {
            DaemonBackedOrchestrator.ReconnectDelay = previousDelay;
        }
    }

    /// <summary>
    /// FAILS BEFORE / PASSES AFTER. <c>ApplyAgentEvent</c> raises <c>EventReceived</c>, <c>Changed</c>
    /// and <c>EgressBlocked</c> synchronously on the pump thread, so one throwing UI subscriber used to
    /// (a) skip every later raise in the same call — a throwing <c>EventReceived</c> handler meant
    /// <c>Changed</c> never fired, so the rail did not redraw even though the projection HAD updated —
    /// and (b) end the stream permanently. Reconnecting alone would not have fixed (a), and would have
    /// spun on (b) since the re-subscribed snapshot re-throws.
    ///
    /// <para>Asserted through the public surface: after the bad subscriber throws, does the rail's
    /// redraw notification still fire, and does the projection still show the new agent?</para>
    /// </summary>
    [Fact]
    public void ApplyAgentEvent_OneThrowingSubscriber_StillRedrawsTheRail()
    {
        using var client = UncontactedClient();
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);

        adapter.EventReceived += _ => throw new InvalidOperationException(
            "a UI subscriber blew up (e.g. a disposed dispatcher during shutdown)");

        var changedRaised = 0;
        adapter.Changed += () => Interlocked.Increment(ref changedRaised);

        // Must not escape to the pump: this exact throw is what ended the stream for good.
        var ex = Record.Exception(() => adapter.ApplyAgentEvent(Snapshot("agent-a")));

        Assert.Null(ex);
        Assert.Equal(1, changedRaised);
        Assert.Contains(adapter.ListAgents(), a => a.AgentId == "agent-a");
    }
}
