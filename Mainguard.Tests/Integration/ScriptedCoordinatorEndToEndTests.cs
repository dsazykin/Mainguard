using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Xunit;

// The P2-10 merge-queue record (7-field), disambiguated from the UI prototype VerificationRecord.
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Tests.Integration;

/// <summary>
/// The phase-2 orchestration loop end to end, deterministically (contract §2):
///
/// <code>
/// user request → coordinator spawns workers (free, within caps) → each worker inspects the repo and
/// authors ITS OWN plan → presents it and BLOCKS → human approves (or rejects with feedback, which the
/// worker revises against) → the daemon releases the task → work → verification → merge queue → human merge
/// </code>
///
/// <para>This is the deterministic leg (runs everywhere). The real-container leg — jails, real
/// verification, the stale cascade against a live runtime — is <c>MergeQueueEndToEndDockerTests</c>.</para>
/// </summary>
public class ScriptedCoordinatorEndToEndTests
{
    private sealed class ScriptedModel : ICoordinatorModel
    {
        private readonly Queue<CoordinatorModelTurn> _turns;
        public ScriptedModel(IEnumerable<CoordinatorModelTurn> turns) => _turns = new Queue<CoordinatorModelTurn>(turns);
        public Task<CoordinatorModelTurn> NextAsync(IReadOnlyList<CoordinatorMessage> transcript, CancellationToken ct) =>
            Task.FromResult(_turns.Count > 0 ? _turns.Dequeue() : CoordinatorModelTurn.Say("done"));
    }

    /// <summary>
    /// The daemon's worker seam, standing in for the real spawn chain: a spawn records a live worker and
    /// <b>hands the task to the plan gate, not to the worker</b> — which is what the production
    /// <c>AgentSpawnService</c> shim path does.
    /// </summary>
    private sealed class GatedWorkerControl : IWorkerControl
    {
        private readonly WorkerPlanGate _gate;
        private readonly string _coordinatorId;
        private int _next;

        public GatedWorkerControl(WorkerPlanGate gate, string coordinatorId)
        {
            _gate = gate;
            _coordinatorId = coordinatorId;
        }

        public List<string> Workers { get; } = new();

        public List<(string AgentId, string Prompt)> PromptsDelivered { get; } = new();

        public List<string> VerificationsRequested { get; } = new();

        public IReadOnlyList<string> ActiveWorkerIds => Workers;

        public string? WorkerStatus(string agentId) => Workers.Contains(agentId) ? "Working" : null;

        public Task<string> SpawnWorkerAsync(string title, string taskPrompt, decimal budgetUsd, CancellationToken ct)
        {
            var id = $"w-{++_next}";
            Workers.Add(id);
            _gate.Hold(id, _coordinatorId, title, taskPrompt, budgetUsd);
            return Task.FromResult(id);
        }

        public Task SendPromptAsync(string agentId, string prompt, CancellationToken ct)
        {
            PromptsDelivered.Add((agentId, prompt));
            return Task.CompletedTask;
        }

        public Task RequestVerificationAsync(string agentId, CancellationToken ct)
        {
            VerificationsRequested.Add(agentId);
            return Task.CompletedTask;
        }
    }

    private static CoordinatorToolCall Spawn(string title, string prompt) => new("spawn_worker", new Dictionary<string, object?>
    {
        ["title"] = title,
        ["task_prompt"] = prompt,
        ["budget_usd"] = "1.5",
    });

    private static TaskPlanFields Fields(params string[] scope) => new(scope, "approach", "tests green");

    [Fact]
    public async Task FullLoop_TwoWorkers_EachAuthorsItsOwnPlan_OneIsRejectedAndRevised_ThenBothMerge()
    {
        var audit = new InMemoryAuditLog();
        var plans = new PlanApprovalService(audit: audit, limits: new CoordinatorLimits(MaxPlanRevisions: 3));
        var planGate = new WorkerPlanGate(plans, audit);
        var admission = new AdmissionController(sampler: () => new MemorySample(100, 90)); // headroom
        var workers = new GatedWorkerControl(planGate, "coord-1");
        var tools = new CoordinatorTools("coord-1", admission, workers, planGate: planGate);

        // ---- Phase 1: the coordinator decomposes into two independent tasks and SPAWNS. No approval. ----
        var model = new ScriptedModel(new[]
        {
            CoordinatorModelTurn.Call(Spawn("task-1", "fix the token clock")),
            CoordinatorModelTurn.Call(Spawn("task-2", "rebuild the search index")),
            CoordinatorModelTurn.Say("Two workers started — they'll present plans for your approval."),
        });
        var coordinator = new CoordinatorAgent("coord-1", model, tools);

        await coordinator.SendAsync("Split the refactor into two independent tasks.");

        Assert.Equal(2, workers.Workers.Count);        // spawning needed no human
        Assert.Empty(plans.All());                     // and the coordinator authored NO plan
        var (w1, w2) = (workers.Workers[0], workers.Workers[1]);

        // Neither worker has its task, and neither may be steered or verified.
        Assert.False(planGate.TryReleaseTask(w1, out _));
        Assert.Equal(CoordinatorToolStatus.Rejected, (await tools.SendWorkerPromptAsync(w1, "start now")).Status);
        Assert.Equal(CoordinatorToolStatus.Rejected, (await tools.RequestVerificationAsync(w1, CancellationToken.None)).Status);
        Assert.Empty(workers.PromptsDelivered);

        // ---- Phase 2: each worker inspects its repo, authors its own plan, presents it and BLOCKS ----
        var author1 = new WorkerPlanAuthor(
            new ScriptedWorkerPlanDrafter("Fix the token clock", Fields("src/Auth/TokenClock.cs")),
            new LocalWorkerPlanChannel(plans, w1, "coord-1"));
        var author2 = new WorkerPlanAuthor(
            new ScriptedWorkerPlanDrafter(
                "Rebuild the search index",
                Fields("src/**"),                       // too wide — the human will send this back
                Fields("src/Search/Indexer.cs")),
            new LocalWorkerPlanChannel(plans, w2, "coord-1"));

        var run1 = author1.RunAsync();
        var run2 = author2.RunAsync();

        await WaitAsync(() => plans.Pending().Count == 2);
        Assert.Equal(2, planGate.BlockedWorkerCount);
        Assert.All(plans.Pending(), p => Assert.Contains(p.WorkerAgentId, new[] { w1, w2 }));

        // ---- Phase 3: the human approves one and rejects the other WITH FEEDBACK ----
        var plan1 = plans.LiveForWorker(w1)!;
        var plan2 = plans.LiveForWorker(w2)!;
        plans.Approve(plan1.PlanId, "uid:1000");
        plans.Reject(plan2.PlanId, "src/** is the whole tree — scope this to the indexer");

        // The rejected worker revises against the feedback and re-presents; the human approves that.
        await WaitAsync(() => plans.LiveForWorker(w2)?.Status == PlanStatus.Pending &&
                              plans.LiveForWorker(w2)?.RevisionCount == 1);
        var revised = plans.LiveForWorker(w2)!;
        Assert.Equal(new[] { "src/Search/Indexer.cs" }, revised.Plan.Scope);
        plans.Approve(revised.PlanId, "uid:1000");

        var result1 = await run1.WaitAsync(TimeSpan.FromSeconds(10));
        var result2 = await run2.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkerPlanPhaseOutcome.Approved, result1.Outcome);
        Assert.Equal(0, result1.Revisions);
        Assert.Equal(WorkerPlanPhaseOutcome.Approved, result2.Outcome);
        Assert.Equal(1, result2.Revisions);

        // ---- Phase 4: the daemon releases the withheld tasks — now, and not before ----
        Assert.True(planGate.TryReleaseTask(w1, out var task1));
        Assert.True(planGate.TryReleaseTask(w2, out var task2));
        Assert.Equal("fix the token clock", task1);
        Assert.Equal("rebuild the search index", task2);
        Assert.Equal(0, planGate.BlockedWorkerCount);

        // Steering and verification are now permitted.
        Assert.Equal(CoordinatorToolStatus.Ok, (await tools.SendWorkerPromptAsync(w1, "carry on")).Status);
        Assert.Equal(CoordinatorToolStatus.Ok, (await tools.RequestVerificationAsync(w1, CancellationToken.None)).Status);
        Assert.Equal(w1, workers.VerificationsRequested.Single());

        // ---- Phase 5: both verify green against main@sha0, with the plan gate ANDed into the queue ----
        MergeQueue queue = null!;
        queue = new MergeQueue("repo", "sha0", new InMemoryMergeQueueStore(), new InMemoryVerificationStore(),
            runVerification: (id, ct) => Task.FromResult(new VerificationRecord(
                id, queue.CurrentMainSha, true, "log", "test", "cfg", DateTimeOffset.UtcNow)),
            requeue: (id, ct) => queue.RunVerificationAsync(id, ct),
            gates: new IMergeGate[] { planGate },
            audit: audit);

        await queue.RunVerificationAsync(w1, CancellationToken.None);
        await queue.RunVerificationAsync(w2, CancellationToken.None);
        Assert.True(queue.CanMerge(w1, out _));
        Assert.True(queue.CanMerge(w2, out _));

        // ---- Phase 6: merge w1 (human path). Main moves → w2 goes StaleVerified (the cascade) ----
        queue.RequestReview(w1);
        queue.ConfirmHumanMerge(w1, "sha1");
        Assert.Equal(WorkerMergeState.Merged, queue.GetState(w1));
        await queue.LastCascade;

        // ---- Phase 7: w2 re-verified against the new main, then merged ----
        Assert.Equal(WorkerMergeState.Verified, queue.GetState(w2));
        queue.RequestReview(w2);
        queue.ConfirmHumanMerge(w2, "sha2");
        Assert.Equal(WorkerMergeState.Merged, queue.GetState(w2));

        // ---- The whole story, in the audit trail ----
        var types = audit.Read().Select(e => e.Type).ToList();
        Assert.Equal(2, types.Count(t => t == "plan_presented"));
        Assert.Equal(1, types.Count(t => t == "plan_revised"));
        Assert.Equal(1, types.Count(t => t == "plan_rejected"));
        Assert.Equal(2, types.Count(t => t == "plan_approved"));
        Assert.Equal(2, types.Count(t => t == "worker_task_released"));
        Assert.DoesNotContain("plan_escalated", types);
    }

    [Fact]
    public async Task WhenTheCapIsSaturatedByBlockedWorkers_TheCoordinatorIsRefused_AndToldWhy()
    {
        var plans = new PlanApprovalService();
        var planGate = new WorkerPlanGate(plans);
        var admission = new AdmissionController(sampler: () => new MemorySample(100, 90));
        var workers = new GatedWorkerControl(planGate, "coord-1");
        var tools = new CoordinatorTools(
            "coord-1", admission, workers, limits: new CoordinatorLimits(MaxActiveWorkers: 2), planGate: planGate);

        var model = new ScriptedModel(new[]
        {
            CoordinatorModelTurn.Call(Spawn("t1", "a")),
            CoordinatorModelTurn.Call(Spawn("t2", "b")),
            CoordinatorModelTurn.Call(Spawn("t3", "c")),  // must be refused
            CoordinatorModelTurn.Say("Two started; the third is waiting on you."),
        });
        var coordinator = new CoordinatorAgent("coord-1", model, tools);

        await coordinator.SendAsync("Start three tasks.");
        Assert.Equal(2, workers.Workers.Count);

        // Both workers present plans and block. The cap is now full of workers doing nothing.
        foreach (var id in workers.Workers.ToList())
        {
            plans.Present(id, "coord-1", "t", Fields("src/a.cs"), "", 1m);
        }

        var refused = await tools.SpawnWorkerAsync("t4", "d", 1m);

        Assert.Equal(CoordinatorToolStatus.Rejected, refused.Status);
        Assert.Equal(2, workers.Workers.Count); // still no third worker
        Assert.Contains("2 workers are waiting on human plan approval", refused.Message, StringComparison.Ordinal);

        // And the coordinator's own transcript carries the refusal, so the operator can see the stall.
        var toolTurns = coordinator.Transcript.Where(m => m.Role == CoordinatorRole.Tool).Select(m => m.Content).ToList();
        Assert.Contains(toolTurns, t => t.Contains("Worker cap reached", StringComparison.Ordinal));
    }

    private static async Task WaitAsync(Func<bool> condition, int attempts = 300)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("the awaited orchestration condition never became true");
    }
}
