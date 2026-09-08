using System;
using Grpc.Net.Client;
using Mainguard.Agents.UI.Services;
using Xunit;
using Proto = Mainguard.Protos.V1;

namespace Mainguard.Tests;

/// <summary>
/// Every projection applier must survive a throwing subscriber.
///
/// <para><b>Why.</b> The appliers run ON their pump's thread and raise <c>Changed</c> / <c>Sampled</c>
/// synchronously, so an exception out of any handler propagates straight into the <c>await foreach</c> and
/// ends the stream. The reconnect loop then catches it and re-subscribes, which silently loses every push
/// in the two-second gap — and a handler that throws on every push simply re-throws on the re-subscribe,
/// so the stream cycles for as long as the fault lasts, with no banner, no log line, and
/// <c>DaemonClient.State</c> still reading <c>Connected</c> because the connection is fine.</para>
///
/// <para><c>ApplyAgentEvent</c> and <c>ApplyQueueUpdate</c> were hardened against exactly this (see
/// <see cref="QueuePumpResilienceTests"/>); the plan, conversation, spend and resource appliers raised
/// unisolated. This file is the missing half — one test per applier, because the four raise through two
/// different events and route through separate code paths.</para>
/// </summary>
public sealed class ProjectionRaiseIsolationTests
{
    private static DaemonClient UncontactedClient() =>
        new(() => GrpcChannel.ForAddress("http://127.0.0.1:1"), () => "token");

    [Fact]
    public void ApplyPlanUpdate_SurvivesAThrowingChangedSubscriber()
    {
        using var client = UncontactedClient();
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Changed += () => throw new InvalidOperationException("a bad UI subscriber");

        var update = new Proto.PlanUpdate { PlanModeEnabled = true, MaxActiveWorkers = 4 };
        update.Plans.Add(new Proto.PlanEntry
        {
            PlanId = "plan-1",
            WorkerAgentId = "agent-a",
            Title = "Add the thing",
            Status = "Pending",
            Approach = "carefully",
            TestStrategy = "xunit",
        });

        var ex = Record.Exception(() => adapter.ApplyPlanUpdate(update));

        Assert.Null(ex);
        // And the projection really was committed before the raise — the point of isolating rather than
        // simply not raising is that the surface's next read is correct even though its handler faulted.
        Assert.Contains(adapter.GetPendingPlans(), p => p.PlanId == "plan-1");
    }

    [Fact]
    public void ApplyConversationUpdate_SurvivesAThrowingChangedSubscriber()
    {
        using var client = UncontactedClient();
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Changed += () => throw new InvalidOperationException("a bad UI subscriber");

        var update = new Proto.ConversationUpdate();
        update.Turns.Add(new Proto.ConversationTurn { Seq = 1, Role = "Human", Text = "start the work" });

        var ex = Record.Exception(() => adapter.ApplyConversationUpdate(update));

        Assert.Null(ex);
        Assert.Contains(adapter.GetTranscript(), l => l.Text == "start the work");
    }

    [Fact]
    public void ApplySpendSample_SurvivesAThrowingSampledSubscriber()
    {
        using var client = UncontactedClient();
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Sampled += () => throw new InvalidOperationException("a bad telemetry subscriber");

        var ex = Record.Exception(() => adapter.ApplySpendSample(
            new Proto.SpendSample { AgentId = "agent-a", UsdMicrosSpent = 1_000_000, TokensSpent = 100 }));

        Assert.Null(ex);
        Assert.NotEmpty(adapter.History);
    }

    [Fact]
    public void ApplyResourceSnapshot_SurvivesAThrowingSampledSubscriber()
    {
        using var client = UncontactedClient();
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Sampled += () => throw new InvalidOperationException("a bad telemetry subscriber");

        var snapshot = new Proto.AgentResourcesSnapshot();
        snapshot.Agents.Add(new Proto.AgentResourceReading
        {
            AgentId = "agent-a",
            CpuPercent = 42.0,
            MemBytes = 1024,
            Metered = true,
        });

        var ex = Record.Exception(() => adapter.ApplyResourceSnapshot(snapshot));

        Assert.Null(ex);
        Assert.NotEmpty(adapter.History);
    }
}
