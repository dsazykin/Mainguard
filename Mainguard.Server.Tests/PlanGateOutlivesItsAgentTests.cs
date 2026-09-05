using System;
using System.Linq;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.UI.Services;
using Mainguard.Protos.V1;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// <b>A plan-gate card cannot outlive the agent it is about, and the banner cannot disagree with it.</b>
///
/// <para>Reported from a live stress run: a worker escalated, the human ended it and its jail was torn
/// down — and its escalated card stayed on the plan gate, stacked above the next pending plan and pushing
/// that plan off the surface. At the same instant the amber backpressure banner had already stopped
/// counting it. One update, one message, two contradictory statements about the same worker.</para>
///
/// <para>The cause was two populations. The card list came straight off the persisted plan store, while
/// the blocked/escalated counts came from the plan gate's held-task table — which the stop path clears the
/// moment a session goes away. So ending an agent removed it from the counts and left it in the cards, and
/// no ordering of the two updates could have fixed it, because they were answers to different questions.</para>
///
/// <para><see cref="Mainguard.Server.Services.PlanApprovalGrpcService"/> now builds both from the entries
/// it emits: liveness is applied once, and the numbers are arithmetic over what was emitted. These tests
/// pin both halves of that — the card goes, and the banner and the cards move together.</para>
/// </summary>
public sealed class PlanGateOutlivesItsAgentTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AnEscalatedCard_DisappearsWhenItsAgentDoes_AndTheBannerGoesWithIt()
    {
        using var daemon = new DaemonFixture();
        _ = daemon.Token;
        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Start();

        var sessions = daemon.Services.GetRequiredService<Mainguard.Server.Runtime.AgentSessionStore>();
        var plans = daemon.Services.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.PlanApprovalService>();
        var limits = daemon.Services.GetRequiredService<CoordinatorLimits>();

        // A real worker session, because that is what makes its plan a live gate item.
        var workerId = "worker-" + Guid.NewGuid().ToString("N")[..8];
        var session = sessions.Spawn("claude-code", role: AgentRoles.Managed, agentId: workerId);

        var presented = plans.Present(
            workerAgentId: workerId, coordinatorId: "coordinator-1", title: "multiply helper",
            fields: new TaskPlanFields(new[] { "calc.js" }, "add multiply", "node test.js"),
            taskPrompt: "add multiply", budgetUsd: 1m);
        Assert.True(presented.IsPresented, presented.Message);

        // Spend the revision budget so the worker escalates — the exact state that was reported.
        for (var i = 0; i <= limits.MaxPlanRevisions; i++)
        {
            plans.Reject(presented.PlanId!, "not yet");
            if (plans.Get(presented.PlanId!)!.Status == PlanStatus.Escalated)
            {
                break;
            }

            plans.Revise(
                presented.PlanId!, "multiply helper",
                new TaskPlanFields(new[] { "calc.js" }, $"attempt {i}", "node test.js"));
        }

        Assert.Equal(PlanStatus.Escalated, plans.Get(presented.PlanId!)!.Status);

        var carded = await WaitUntilAsync(
            () => adapter.GetWorkerPlans().Any(c => c.PlanId == presented.PlanId && c.IsEscalated));
        Assert.True(carded, "an escalated worker with a live session produced no card");

        // The banner counts it too. Agreement in the state we EXPECT to be consistent is the control for
        // the assertion below — otherwise "they agree afterwards" could just mean both are empty.
        var counted = await WaitUntilAsync(() => adapter.GetBackpressure().EscalatedWorkerCount == 1);
        Assert.True(counted, "the banner did not count an escalated worker its own cards were showing");
        Assert.Contains("escalated", adapter.GetBackpressure().Signal, StringComparison.OrdinalIgnoreCase);

        // Now the agent goes away — the jail torn down, the session removed. Nothing touches the plan
        // store: the plan record is still there, exactly as it was in the field.
        sessions.Stop(session.Key);
        Assert.Equal(PlanStatus.Escalated, plans.Get(presented.PlanId!)!.Status);

        var cleared = await WaitUntilAsync(
            () => adapter.GetWorkerPlans().All(c => c.PlanId != presented.PlanId));
        Assert.True(cleared,
            "the escalated card outlived its agent — there is nothing left to steer or end, and it is "
            + "stacking above decisions that can still be made");

        var backpressure = adapter.GetBackpressure();
        Assert.Equal(0, backpressure.EscalatedWorkerCount);
        Assert.Equal("", backpressure.Signal);
    }

    /// <summary>
    /// The general form: whatever the cards say, the counts are that same list counted. Asserted over a
    /// mixed population — one blocked worker that exists, one whose session is gone — because that is the
    /// shape where two independent reads diverge.
    /// </summary>
    [Fact]
    public async Task TheBannersNumbers_AreTheCardListCounted()
    {
        using var daemon = new DaemonFixture();
        _ = daemon.Token;
        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Start();

        var sessions = daemon.Services.GetRequiredService<Mainguard.Server.Runtime.AgentSessionStore>();
        var plans = daemon.Services.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.PlanApprovalService>();

        var liveId = "live-" + Guid.NewGuid().ToString("N")[..8];
        var goneId = "gone-" + Guid.NewGuid().ToString("N")[..8];
        sessions.Spawn("claude-code", role: AgentRoles.Managed, agentId: liveId);
        var doomed = sessions.Spawn("claude-code", role: AgentRoles.Managed, agentId: goneId);

        foreach (var (id, title) in new[] { (liveId, "still here"), (goneId, "about to vanish") })
        {
            var p = plans.Present(
                workerAgentId: id, coordinatorId: "coordinator-1", title: title,
                fields: new TaskPlanFields(new[] { "calc.js" }, "do the thing", "node test.js"),
                taskPrompt: "do the thing", budgetUsd: 1m);
            Assert.True(p.IsPresented, p.Message);
        }

        var both = await WaitUntilAsync(() => adapter.GetBackpressure().BlockedWorkerCount == 2);
        Assert.True(both, "two blocked workers with live sessions were not both counted");

        sessions.Stop(doomed.Key);

        var settled = await WaitUntilAsync(() =>
        {
            var cards = adapter.GetWorkerPlans().Where(c => c.IsPending).ToList();
            var pressure = adapter.GetBackpressure();
            return cards.Count == 1 && pressure.BlockedWorkerCount == 1;
        });

        Assert.True(settled, "the card list and the banner settled on different numbers");
        var remaining = Assert.Single(adapter.GetWorkerPlans(), c => c.IsPending);
        Assert.Equal(liveId, remaining.WorkerAgentId);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        return predicate();
    }
}
