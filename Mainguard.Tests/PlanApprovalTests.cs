using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Plan-approval governance: the approver identity is daemon-derived and persisted, a decision is
/// attributable, and the pressure signal reflects what the human owes.
///
/// <para>Phase 2 rewrote two of these. "A rejected plan leaves no residue" survives, but its meaning has
/// narrowed to what it always actually asserted — <b>rejection starts nothing</b> — because rejection is
/// now feedback the worker revises against rather than the end of the attempt. The S-8 per-coordinator
/// drafting caps are gone with the coordinator's authoring path; the invariant that replaced them (one
/// live plan per worker) is covered in <c>WorkerAuthoredPlanTests</c>.</para>
/// </summary>
public class PlanApprovalTests
{
    private static TaskPlanFields Fields() => new(new[] { "src/a.cs" }, "do the thing", "tests green");

    [Fact]
    public void PlanRejected_StartsNothing_AndLeavesNoResidue()
    {
        var audit = new InMemoryAuditLog();
        var svc = new PlanApprovalService(audit: audit);

        // A spy release path that would create a "worktree" dir if it ever ran (it must not, on reject).
        var worktreeRoot = Path.Combine(Path.GetTempPath(), "mainguard-plan-noresidue", Guid.NewGuid().ToString("N"));
        var releaseCount = 0;
        svc.PlanApproved += plan =>
        {
            releaseCount++;
            Directory.CreateDirectory(Path.Combine(worktreeRoot, plan.PlanId));
        };

        var presented = svc.Present("w-1", "coord-1", "Fix A", Fields(), "prompt", 1.5m);
        Assert.True(presented.IsPresented);

        svc.Reject(presented.PlanId!, "not this way");

        // Nothing was released to the worker; no residue.
        Assert.Equal(0, releaseCount);
        Assert.False(Directory.Exists(worktreeRoot));
        Assert.Equal(PlanStatus.Rejected, svc.Get(presented.PlanId!)!.Status);

        // Audit records the rejection, never an approval.
        var types = audit.Read().Select(e => e.Type).ToArray();
        Assert.Contains("plan_rejected", types);
        Assert.DoesNotContain("plan_approved", types);
    }

    [Fact]
    public void Approval_PersistsIdentity_SurvivesRestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mainguard-plan-persist", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var storePath = Path.Combine(dir, "plans.json");

        string planId;
        try
        {
            // First daemon instance: present + approve with a DAEMON-DERIVED identity (never client-supplied).
            var svc1 = new PlanApprovalService(store: new JsonPlanApprovalStore(storePath));
            var presented = svc1.Present("w-1", "coord-1", "Refactor token refresh", Fields(), "prompt", 1.5m);
            planId = presented.PlanId!;
            var approved = svc1.Approve(planId, "uid:1000");
            Assert.Equal("uid:1000", approved.ApproverIdentity);
            Assert.NotNull(approved.DecidedAt);

            // Second daemon instance over the SAME store (a restart): the record + identity survive.
            var svc2 = new PlanApprovalService(store: new JsonPlanApprovalStore(storePath));
            var reloaded = svc2.Get(planId);
            Assert.NotNull(reloaded);
            Assert.Equal(PlanStatus.Approved, reloaded!.Status);
            Assert.Equal("uid:1000", reloaded.ApproverIdentity);
            Assert.Equal("Refactor token refresh", reloaded.Title);
            Assert.Equal("w-1", reloaded.WorkerAgentId);
            Assert.Equal(new[] { "src/a.cs" }, reloaded.Plan.Scope);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* cleanup only */ }
        }
    }

    [Fact]
    public void Approval_RequiresNonEmptyDaemonIdentity()
    {
        var svc = new PlanApprovalService();
        var presented = svc.Present("w-1", "coord-1", "X", Fields(), "p", 1m);
        // An empty approver would be an unattributable approval — refused.
        Assert.Throws<ArgumentException>(() => svc.Approve(presented.PlanId!, ""));
    }

    [Fact]
    public void PressureSignal_ReportsThePlansTheHumanOwes_AndStaysQuietBelowTheThreshold()
    {
        var svc = new PlanApprovalService();
        svc.Present("w-1", "coord-1", "A", Fields(), "p", 1m);
        svc.Present("w-2", "coord-1", "B", Fields(), "p", 1m);

        Assert.Null(svc.PressureSignal("coord-1")); // two is not worth interrupting anyone for

        svc.Present("w-3", "coord-1", "C", Fields(), "p", 1m);

        var pressure = svc.PressureSignal("coord-1");
        Assert.NotNull(pressure);
        Assert.Contains("3 plans pending", pressure);

        // Deciding one drops the count back under the threshold.
        svc.Approve(svc.Pending("coord-1")[0].PlanId, "uid:1000");
        Assert.Null(svc.PressureSignal("coord-1"));
    }

    [Fact]
    public void PressureSignal_IsScopedToOneCoordinator()
    {
        var svc = new PlanApprovalService();
        foreach (var w in new[] { "w-1", "w-2", "w-3" })
        {
            svc.Present(w, "coord-1", "A", Fields(), "p", 1m);
        }

        Assert.NotNull(svc.PressureSignal("coord-1"));
        Assert.Null(svc.PressureSignal("coord-2"));
    }
}
