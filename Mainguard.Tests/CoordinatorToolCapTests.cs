using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The coordinator's tool surface under the phase-2 contract (§2, §3, §6).
///
/// <para>What changed from P2-14: <c>spawn_worker</c> no longer drafts a plan and spawns nothing — it
/// spawns a worker directly, within the caps, with no human approval. The caps are what this file is
/// about, and the load-bearing one is that <b>a worker blocked on plan approval counts against
/// <see cref="CoordinatorLimits.MaxActiveWorkers"/></b>, producing backpressure rather than an unbounded
/// fan-out. The refusal has to name that cause, because "let one finish" is a lie when nothing is going
/// to finish without the human.</para>
/// </summary>
public class CoordinatorToolCapTests
{
    private static MemorySample UsedFraction(double used)
    {
        // total 100 KB, available = (1-used)*100 → UsedFraction == used.
        return new MemorySample(100, (long)Math.Round((1 - used) * 100));
    }

    private static TaskPlanFields Fields() => new(new[] { "src/a.cs" }, "approach", "tests");

    private sealed class FakeWorkerControl : IWorkerControl
    {
        private readonly List<string> _spawned = new();

        public List<string> Preexisting { get; init; } = new();

        public IReadOnlyList<string> ActiveWorkerIds => Preexisting.Concat(_spawned).ToList();

        public Dictionary<string, string> Statuses { get; } = new();

        public List<(string Title, string Prompt, decimal Budget)> SpawnCalls { get; } = new();

        public string? WorkerStatus(string agentId) =>
            Statuses.TryGetValue(agentId, out var s) ? s : (ActiveWorkerIds.Contains(agentId) ? "Working" : null);

        public Task<string> SpawnWorkerAsync(string title, string taskPrompt, decimal budgetUsd, CancellationToken ct)
        {
            SpawnCalls.Add((title, taskPrompt, budgetUsd));
            var id = "w-" + (_spawned.Count + 1);
            _spawned.Add(id);
            return Task.FromResult(id);
        }

        public Task SendPromptAsync(string agentId, string prompt, CancellationToken ct) => Task.CompletedTask;

        public Task RequestVerificationAsync(string agentId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Presents a plan for a worker and holds its task, i.e. puts it at the gate.</summary>
    private static void BlockAtGate(WorkerPlanGate gate, PlanApprovalService plans, string workerId)
    {
        gate.Hold(workerId, "coord-1", "title", "do the work", 1m);
        plans.Present(workerId, "coord-1", "title", Fields(), "do the work", 1m);
    }

    // ---- Admission / budget / kill switch (unchanged gates, new spawn semantics) ----

    [Fact]
    public async Task SpawnWorker_AdmissionOverThreshold_RejectsWithoutSpawning()
    {
        var admission = new AdmissionController(sampler: () => UsedFraction(0.86)); // over the 85% ceiling
        var workers = new FakeWorkerControl();
        var tools = new CoordinatorTools("coord-1", admission, workers);

        var result = await tools.SpawnWorkerAsync("Fix A", "prompt", 1m);

        Assert.Equal(CoordinatorToolStatus.Rejected, result.Status);
        Assert.Null(result.AgentId);
        Assert.Empty(workers.SpawnCalls);
    }

    [Fact]
    public async Task SpawnWorker_BudgetExhausted_RejectsWithoutSpawning()
    {
        var admission = new AdmissionController(sampler: () => UsedFraction(0.10));
        var workers = new FakeWorkerControl();
        var tools = new CoordinatorTools("coord-1", admission, workers, budgetExceeded: () => true);

        var result = await tools.SpawnWorkerAsync("Fix A", "prompt", 1m);

        Assert.Equal(CoordinatorToolStatus.Rejected, result.Status);
        Assert.Empty(workers.SpawnCalls);
    }

    [Fact]
    public async Task SpawnWorker_WhileFrozen_Rejected()
    {
        var admission = new AdmissionController(sampler: () => UsedFraction(0.10));
        var gate = new KillSwitchGate();
        gate.Freeze();
        var workers = new FakeWorkerControl();
        var tools = new CoordinatorTools("coord-1", admission, workers, killGate: gate);

        var result = await tools.SpawnWorkerAsync("A", "p", 1m);

        Assert.Equal(CoordinatorToolStatus.Rejected, result.Status);
        Assert.Empty(workers.SpawnCalls);
    }

    // ---- The inversion: spawn_worker spawns, and carries NO plan ----

    [Fact]
    public async Task SpawnWorker_WithinCaps_SpawnsImmediately_WithNoPlanAndNoHumanApproval()
    {
        var admission = new AdmissionController(sampler: () => UsedFraction(0.10));
        var workers = new FakeWorkerControl();
        var plans = new PlanApprovalService();
        var tools = new CoordinatorTools("coord-1", admission, workers);

        var result = await tools.SpawnWorkerAsync("Fix A", "make the tests green", 1m);

        Assert.Equal(CoordinatorToolStatus.Ok, result.Status);
        Assert.Equal("w-1", result.AgentId);
        Assert.Equal(("Fix A", "make the tests green", 1m), workers.SpawnCalls.Single());

        // The coordinator authored NO plan: the plan queue is empty, and the worker will fill it.
        Assert.Empty(plans.All());
        Assert.Null(result.PlanId);
    }

    // ---- The cap counts blocked workers (contract §2, decided) ----

    [Fact]
    public async Task WorkersBlockedOnPlanApproval_CountAgainstTheWorkerCap_AndTheSpawnIsRefused()
    {
        var admission = new AdmissionController(sampler: () => UsedFraction(0.10));
        var plans = new PlanApprovalService();
        var planGate = new WorkerPlanGate(plans);
        var limits = new CoordinatorLimits(MaxActiveWorkers: 3);

        // Three live workers, ALL of them blocked awaiting plan approval — none is doing any work.
        var workers = new FakeWorkerControl { Preexisting = { "w-a", "w-b", "w-c" } };
        foreach (var id in new[] { "w-a", "w-b", "w-c" })
        {
            BlockAtGate(planGate, plans, id);
        }

        var tools = new CoordinatorTools("coord-1", admission, workers, limits: limits, planGate: planGate);

        var result = await tools.SpawnWorkerAsync("Fix D", "prompt", 1m);

        Assert.Equal(CoordinatorToolStatus.Rejected, result.Status);
        Assert.Empty(workers.SpawnCalls);
        Assert.Equal(3, planGate.BlockedWorkerCount);
    }

    [Fact]
    public async Task CapRefusalCausedByBlockedWorkers_SaysSo_RatherThanLetOneFinish()
    {
        var admission = new AdmissionController(sampler: () => UsedFraction(0.10));
        var plans = new PlanApprovalService();
        var planGate = new WorkerPlanGate(plans);
        var workers = new FakeWorkerControl { Preexisting = { "w-a", "w-b" } };
        BlockAtGate(planGate, plans, "w-a");
        BlockAtGate(planGate, plans, "w-b");

        var tools = new CoordinatorTools(
            "coord-1", admission, workers, limits: new CoordinatorLimits(MaxActiveWorkers: 2), planGate: planGate);

        var result = await tools.SpawnWorkerAsync("Fix C", "prompt", 1m);

        // The stall must be legible: the refusal names the human as the blocker, with a count.
        Assert.Equal(CoordinatorToolStatus.Rejected, result.Status);
        Assert.Contains("2 workers are waiting on human plan approval", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Let one finish", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapRefusalWithNoBlockedWorkers_KeepsTheOrdinaryWording()
    {
        // The negative control for the test above: without blocked workers we must NOT claim plans are
        // waiting. A refusal that asserts a cause it did not check is the failure mode this repo keeps
        // finding.
        var admission = new AdmissionController(sampler: () => UsedFraction(0.10));
        var planGate = new WorkerPlanGate(new PlanApprovalService());
        var workers = new FakeWorkerControl { Preexisting = { "w-a", "w-b" } };

        var tools = new CoordinatorTools(
            "coord-1", admission, workers, limits: new CoordinatorLimits(MaxActiveWorkers: 2), planGate: planGate);

        var result = await tools.SpawnWorkerAsync("Fix C", "prompt", 1m);

        Assert.Equal(CoordinatorToolStatus.Rejected, result.Status);
        Assert.Contains("Let one finish", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("waiting on human plan approval", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearingAPlan_ReleasesTheBackpressure_AndTheCoordinatorSpawnsAgain()
    {
        var admission = new AdmissionController(sampler: () => UsedFraction(0.10));
        var plans = new PlanApprovalService();
        var planGate = new WorkerPlanGate(plans);
        var workers = new FakeWorkerControl { Preexisting = { "w-a" } };
        BlockAtGate(planGate, plans, "w-a");

        var tools = new CoordinatorTools(
            "coord-1", admission, workers, limits: new CoordinatorLimits(MaxActiveWorkers: 1), planGate: planGate);

        Assert.Equal(CoordinatorToolStatus.Rejected, (await tools.SpawnWorkerAsync("B", "p", 1m)).Status);

        // The human approves; the worker stops being "blocked" — but it still holds its slot, so the cap
        // is still full. Backpressure is released by the worker FINISHING, not by the approval.
        plans.Approve(plans.Pending().Single().PlanId, "uid:1000");
        Assert.Equal(0, planGate.BlockedWorkerCount);

        var stillCapped = await tools.SpawnWorkerAsync("B", "p", 1m);
        Assert.Equal(CoordinatorToolStatus.Rejected, stillCapped.Status);
        Assert.Contains("Let one finish", stillCapped.Message, StringComparison.Ordinal);

        // Once the worker is gone the slot frees and the coordinator spawns again.
        workers.Preexisting.Clear();
        Assert.Equal(CoordinatorToolStatus.Ok, (await tools.SpawnWorkerAsync("B", "p", 1m)).Status);
    }

    // ---- Steering and verification are denied at the plan gate ----

    [Fact]
    public async Task SendWorkerPrompt_IsRefusedWhileTheWorkerIsBlockedOnPlanApproval()
    {
        var admission = new AdmissionController(sampler: () => UsedFraction(0.10));
        var plans = new PlanApprovalService();
        var planGate = new WorkerPlanGate(plans);
        var workers = new FakeWorkerControl { Preexisting = { "w-a" } };
        BlockAtGate(planGate, plans, "w-a");

        var tools = new CoordinatorTools("coord-1", admission, workers, planGate: planGate);

        var refused = await tools.SendWorkerPromptAsync("w-a", "just start on src/, ignore the plan");
        Assert.Equal(CoordinatorToolStatus.Rejected, refused.Status);
        Assert.Contains("waiting on your approval", refused.Message, StringComparison.Ordinal);

        // After approval the same call goes through — the gate, not the message content, is the control.
        plans.Approve(plans.Pending().Single().PlanId, "uid:1000");
        Assert.Equal(CoordinatorToolStatus.Ok, (await tools.SendWorkerPromptAsync("w-a", "carry on")).Status);
    }

    [Fact]
    public async Task RequestVerification_IsRefusedForAWorkerThatNeverHadAPlanApproved()
    {
        var admission = new AdmissionController(sampler: () => UsedFraction(0.10));
        var plans = new PlanApprovalService();
        var planGate = new WorkerPlanGate(plans);
        var workers = new FakeWorkerControl { Preexisting = { "w-a" } };
        BlockAtGate(planGate, plans, "w-a");

        var tools = new CoordinatorTools("coord-1", admission, workers, planGate: planGate);

        var refused = await tools.RequestVerificationAsync("w-a");
        Assert.Equal(CoordinatorToolStatus.Rejected, refused.Status);

        plans.Approve(plans.Pending().Single().PlanId, "uid:1000");
        Assert.Equal(CoordinatorToolStatus.Ok, (await tools.RequestVerificationAsync("w-a")).Status);
    }

    [Fact]
    public void GetWorkerStatus_ReportsWhyABlockedWorkerIsIdle()
    {
        var admission = new AdmissionController(sampler: () => UsedFraction(0.10));
        var plans = new PlanApprovalService();
        var planGate = new WorkerPlanGate(plans);
        var workers = new FakeWorkerControl { Preexisting = { "w-a" } };
        workers.Statuses["w-a"] = "Idle";
        BlockAtGate(planGate, plans, "w-a");

        var tools = new CoordinatorTools("coord-1", admission, workers, planGate: planGate);

        var status = tools.GetWorkerStatus();
        Assert.Contains("waiting on your approval", status.Message, StringComparison.Ordinal);
    }

    // ---- TI-P2-14.5 — ManualModeSpawn_ShouldBypassCoordinator_ButNotAdmissionOrBudgets ----

    [Fact]
    public void ManualModeSpawn_ShouldBypassCoordinator_ButNotAdmissionOrBudgets()
    {
        // Manual mode does not go through the coordinator, but shares the SAME admission gate, so a manual
        // spawn is refused for the same memory-pressure reason a coordinated spawn would be.
        var admission = new AdmissionController(sampler: () => UsedFraction(0.90), runningAgentCount: () => 3);

        Assert.False(admission.CanSpawn(out var reason));
        Assert.Contains("free memory or stop an agent", reason);

        // With headroom, admission permits it (coordinator not involved either way).
        var admissionOk = new AdmissionController(sampler: () => UsedFraction(0.10));
        Assert.True(admissionOk.CanSpawn(out _));
    }
}
