using System;
using System.Linq;
using System.Threading;
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
/// P2-47 — the end-to-end "no empty stub remains" proof for the control-center surfaces, run through the
/// REAL composition root. Each test stands up the real in-proc daemon, drives it with the shipped
/// <see cref="DaemonClient"/>, and asserts the shipped <see cref="DaemonBackedOrchestrator"/> — the exact
/// adapter MainWindow runs on — projects a real daemon action off the live RPC stream (merge queue / plan
/// approval / kill switch / telemetry / coordinator chat). No mock is involved anywhere.
/// </summary>
public sealed class AlphaControlCenterProjectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task KillSwitch_Engage_Then_Resume_ProjectsFrozenState()
    {
        using var daemon = new DaemonFixture();
        _ = daemon.Token; // force a single synchronous host build before the pumps race on it
        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Start();

        await adapter.EngageAsync();
        Assert.True(adapter.IsFrozen, "kill switch Engage did not project a frozen state");
        // The daemon-side gate is genuinely frozen (freeze-first, SA-1/F4).
        Assert.True(daemon.Services.GetRequiredService<KillSwitchGate>().IsFrozen);

        await adapter.ResumeAsync();
        Assert.False(adapter.IsFrozen, "kill switch Resume did not clear the frozen state");
        Assert.False(daemon.Services.GetRequiredService<KillSwitchGate>().IsFrozen);
    }

    [Fact]
    public async Task Plans_PresentedOnDaemon_ProjectThroughAdapter_ThenRejectSendsItBackForRevision()
    {
        using var daemon = new DaemonFixture();
        _ = daemon.Token; // force a single synchronous host build before the pumps race on it
        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Start();

        // Present a worker-authored plan directly on the daemon-side service (where the worker's plan
        // shim lands it).
        //
        // The worker id is unique per run, and has to be: TestDataRootIsolation gives the whole ASSEMBLY
        // one data root, so every DaemonFixture in the suite rehydrates from the same restart-safe plan
        // store. A literal id shared with another test therefore trips the daemon's one-live-plan-per-
        // worker invariant — which is the invariant doing its job, in the wrong place.
        var workerId = "worker-" + Guid.NewGuid().ToString("N")[..8];

        // …and it needs a live SESSION, because a plan is only a gate item while the worker it is about
        // exists. StreamPlans filters on that (see PlanGateOutlivesItsAgentTests): a card whose agent is
        // gone has nothing left to approve, steer or end, and used to sit on the surface anyway while the
        // backpressure banner beside it had already dropped the same worker. In production the plan
        // arrives over the worker's own IPC socket, so the session is always there; here it is stated.
        daemon.Services.GetRequiredService<Mainguard.Server.Runtime.AgentSessionStore>()
            .Spawn("claude-code", role: AgentRoles.Managed, agentId: workerId);

        var plans = daemon.Services.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.PlanApprovalService>();
        var presented = plans.Present(
            workerAgentId: workerId, coordinatorId: "coordinator-1", title: "Fix the flaky test",
            fields: new TaskPlanFields(new[] { "tests/FlakyTests.cs" }, "stabilize the clock", "green twice"),
            taskPrompt: "fix it", budgetUsd: 1.25m);
        Assert.True(presented.IsPresented, presented.Message);

        var projected = await WaitUntilAsync(
            () => adapter.GetPendingPlans().Any(p => p.PlanId == presented.PlanId));
        Assert.True(projected, "the adapter did not project the presented plan off StreamPlans");
        Assert.Contains(adapter.GetWorkerPlans(), c => c.PlanId == presented.PlanId && c.WorkerAgentId == workerId);

        // Reject through the real RejectPlan RPC. Phase 2: this does NOT end the plan — it leaves the
        // approvable set (the worker owes a revision) and the feedback travels with it.
        await adapter.SubmitPlanDecisionAsync(presented.PlanId!, approve: false, feedback: "narrow the scope");
        var gone = await WaitUntilAsync(() => adapter.GetPendingPlans().All(p => p.PlanId != presented.PlanId));
        Assert.True(gone, "reject did not clear the plan from the approvable projection");

        var stored = plans.Get(presented.PlanId!);
        Assert.NotNull(stored);
        Assert.Equal(Mainguard.Agents.Agents.Orchestrator.PlanStatus.Rejected, stored!.Status);
        Assert.Equal("narrow the scope", stored.RejectionFeedback);
    }

    [Fact]
    public async Task Telemetry_LiveSpend_ProjectsThroughAdapter_AndBudgetsRoundTrip()
    {
        using var daemon = new DaemonFixture();
        _ = daemon.Token; // force a single synchronous host build before the pumps race on it
        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Start();

        // Budgets round-trip through the real Get/SetBudgets RPCs.
        using var cts = new CancellationTokenSource(Timeout);
        await adapter.SetBudgetsAsync(new Budget
        {
            UsdMicrosCap = 2_000_000,
            TokenCap = 500_000,
            UsdMicrosCapPerDay = 10_000_000,
            TokenCapPerDay = 2_000_000,
        }, cts.Token);
        var read = await adapter.GetBudgetsAsync(cts.Token);
        Assert.Equal(10_000_000, read.UsdMicrosCapPerDay);
        Assert.Equal(2_000_000, read.TokenCapPerDay);

        // A real spend recorded on the daemon ledger flows to the adapter's live telemetry projection.
        var ledger = daemon.Services.GetRequiredService<BudgetLedger>();
        ledger.Record(agentId: "loom-1", model: "claude-sonnet", tokens: 1_000);

        var sampled = await WaitUntilAsync(() => adapter.History.Count > 0);
        Assert.True(sampled, "the adapter did not project a live spend sample off StreamSpend");
    }

    [Fact]
    public async Task Coordinator_SendMessage_ProjectsConversation()
    {
        using var daemon = new DaemonFixture();
        _ = daemon.Token; // force a single synchronous host build before the pumps race on it
        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Start();

        await adapter.SendAsync("split the auth work from the search work");

        var projected = await WaitUntilAsync(() =>
            adapter.GetTranscript().Any(l =>
                l.Kind == Mainguard.Agents.Agents.ChatLineKind.Human &&
                l.Text.Contains("split the auth work")));
        Assert.True(projected, "the adapter did not project the sent message off StreamConversation");
    }

    [Fact]
    public async Task MergeQueue_LiveEntry_ProjectsThroughAdapter()
    {
        using var daemon = new DaemonFixture();
        _ = daemon.Token; // force a single synchronous host build before the pumps race on it
        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Start();

        const string repoHandle = "repo-handle-1";
        const string mainSha = "main-000001";

        // Register a real MergeQueue for the repo handle and seed a Verified entry (the daemon's registry
        // is what StreamQueue resolves through).
        var registry = (MergeQueueRegistry)daemon.Services.GetRequiredService<IMergeQueueRegistry>();
        var leases = daemon.Services.GetRequiredService<IMergeLeaseStore>();
        var queue = new MergeQueue(
            repoHash: "h1",
            currentMainSha: mainSha,
            store: new InMemoryMergeQueueStore(),
            verifications: new InMemoryVerificationStore(),
            runVerification: (agentId, ct) => Task.FromResult(new Mainguard.Agents.Agents.Orchestrator.VerificationRecord(
                agentId, mainSha, Passed: true, LogArtifactPath: "", ResolvedCommand: "dotnet test",
                ConfigHash: "cfg", When: DateTimeOffset.UtcNow)));
        await queue.RunVerificationAsync("loom-9", CancellationToken.None);
        registry.Register(repoHandle, new MergeQueueContext(queue, leases));

        adapter.SetActiveRepo(repoHandle);

        var projected = await WaitUntilAsync(() => adapter.GetQueue().Any(e => e.AgentId == "loom-9"));
        Assert.True(projected, "the adapter did not project the live merge-queue entry off StreamQueue");
        Assert.Equal(mainSha, adapter.MainSha);
    }

    [Fact]
    public async Task CoordinatorRole_SurvivesTheDeltaRace_AndDeath_InTheAdapterProjection()
    {
        using var daemon = new DaemonFixture();
        _ = daemon.Token; // force a single synchronous host build before the pumps race on it
        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);

        // Let the agent stream deliver its (empty) snapshot first, so the coordinator spawn below
        // reaches the adapter as a bare STATE DELTA — the delta carries neither kind nor role, the
        // exact race that made a just-started coordinator render as a plain worker row (field bug,
        // 2026-07-17: "the coordinator shows up in the worker agent category").
        var snapshotSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.EventReceived += _ => snapshotSeen.TrySetResult();
        adapter.Start();
        await snapshotSeen.Task.WaitAsync(Timeout);

        var agentId = await client.SpawnAgentAsync(
            repoHandle: "never-provisioned", taskPrompt: "", agentKind: "claude-code",
            modelApiKey: "", CancellationToken.None, role: Mainguard.Agents.Agents.AgentRoles.Coordinator);

        // The projection must converge on the AUTHORITATIVE role, never a fabricated role-less row.
        var projectedCoordinator = await WaitUntilAsync(() =>
            adapter.ListAgents().Any(a =>
                a.AgentId == agentId && a.Role == Mainguard.Agents.Agents.AgentRoles.Coordinator));
        Assert.True(projectedCoordinator,
            "the adapter projected the coordinator without its role (the delta-race role loss)");
        Assert.Equal(agentId, adapter.CoordinatorAgentId);

        // A death (the field defect's visible end state) must keep the role on the projected row —
        // dead-but-still-the-coordinator, owned by the coordinator surface, never a worker row.
        daemon.Services.GetRequiredService<Runtime.AgentSessionStore>()
            .MarkState(agentId, "Dead", "CLI exited (0): Not logged in · Please run /login");
        var deadAndStillCoordinator = await WaitUntilAsync(() =>
            adapter.ListAgents().Any(a =>
                a.AgentId == agentId
                && a.Role == Mainguard.Agents.Agents.AgentRoles.Coordinator
                && a.State == Mainguard.Agents.Agents.AgentLifecycleState.Dead));
        Assert.True(deadAndStillCoordinator, "death dropped the coordinator role from the projection");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }
}
