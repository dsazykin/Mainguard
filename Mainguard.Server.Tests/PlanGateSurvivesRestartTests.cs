using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The plan gate across a daemon restart — phase 2's hands-on step 7, which until now existed only as a
/// manual instruction to the reviewer.
///
/// <para><b>Why it needs a test rather than a procedure.</b> The gate's whole claim is that the daemon
/// withholds the task, so the interesting question is what happens when the daemon is the thing that
/// went away. Two failures would both be invisible on a fresh boot and catastrophic in use: a pending
/// plan silently dropped (the worker blocks forever on a decision nobody can make, and the human sees no
/// card to decide on), or a pending plan rehydrated as *approved* (the daemon hands out a task no human
/// ever cleared — the one outcome the gate exists to prevent). Restart-safety is asserted here by
/// driving the store through <see cref="JsonPlanApprovalStore"/> exactly as the daemon does, so the
/// rehydration path is the shipped one.</para>
/// </summary>
public sealed class PlanGateSurvivesRestartTests
{
    private static TaskPlanFields Fields(string scope = "src/Auth/TokenClock.cs") =>
        new(new[] { scope }, "inject a fixed clock", "AuthTests plus two boundary cases");

    private static PlanApprovalService Daemon(string path) =>
        new(store: new JsonPlanApprovalStore(path));

    /// <summary>
    /// A plan pending when the daemon died is still PENDING when it comes back — not gone, and above all
    /// not approved. Both halves are asserted because only one of them is the dangerous direction.
    /// </summary>
    [Fact]
    public void APendingPlan_SurvivesARestart_AsPendingAndNotApproved()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"mg-plan-restart-{Guid.NewGuid():N}.json");
        try
        {
            // --- the daemon before the restart ---
            var before = Daemon(path);
            var presented = before.Present(
                "worker-1", "coord-1", "Fix the token clock", Fields(), "rewrite the expiry check", 1.50m);

            var pendingBefore = Assert.Single(before.Pending());
            Assert.Equal(PlanStatus.Pending, pendingBefore.Status);

            // --- the daemon after it ---
            var after = Daemon(path);

            var pendingAfter = Assert.Single(after.Pending());
            Assert.Equal(pendingBefore.PlanId, pendingAfter.PlanId);
            Assert.Equal(PlanStatus.Pending, pendingAfter.Status);
            Assert.False(
                after.HasApprovedPlan("worker-1"),
                "the worker was cleared to work by a restart rather than by a person");
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The other direction: a plan the human APPROVED before the restart is still approved after it.
    /// Losing that would re-block a worker that was already cleared, and the human would be asked to
    /// approve the same plan twice — which reads as the gate malfunctioning and trains people to click
    /// through it.
    /// </summary>
    [Fact]
    public void AnApprovedPlan_SurvivesARestart_AndTheWorkerStaysCleared()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"mg-plan-restart-{Guid.NewGuid():N}.json");
        try
        {
            var before = Daemon(path);
            var presented = before.Present(
                "worker-2", "coord-1", "T", Fields(), "the task", 1.50m);
            Assert.True(presented.IsPresented, presented.Message);
            before.Approve(presented.PlanId!, "os:tester");
            Assert.True(before.HasApprovedPlan("worker-2"));

            var after = Daemon(path);

            Assert.True(
                after.HasApprovedPlan("worker-2"),
                "an approved worker was re-blocked by a restart — the human would be asked to approve twice");
            Assert.Empty(after.Pending());
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The revision counter survives too. It is the budget that decides when a worker escalates instead
    /// of looping, so a restart that reset it would hand a worker unlimited revisions — the cap would
    /// still be described everywhere and enforced nowhere.
    /// </summary>
    [Fact]
    public void TheRevisionCounter_SurvivesARestart()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"mg-plan-restart-{Guid.NewGuid():N}.json");
        try
        {
            var before = Daemon(path);
            var presented = before.Present(
                "worker-3", "coord-1", "T", Fields(), "the task", 1.50m);
            Assert.True(presented.IsPresented, presented.Message);
            var planId = presented.PlanId!;
            before.Reject(planId, "scope it down");
            before.Revise(planId, "T", Fields("src/a.cs"));

            var spent = before.LiveForWorker("worker-3")!.RevisionCount;
            Assert.Equal(1, spent);

            var after = Daemon(path);

            Assert.Equal(
                spent,
                after.LiveForWorker("worker-3")!.RevisionCount);
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { /* best effort */ }
        }
    }
}
