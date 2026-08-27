using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Agents.Agents.Orchestrator;
using Xunit;
using Xunit.Abstractions;

namespace Mainguard.Server.Tests;

/// <summary>
/// The whole coordinator workflow driven as ONE continuous run over the shipped path, rather than as
/// the isolated assertions the rest of the suite makes.
///
/// <para><b>Why this exists separately.</b> <c>ScriptedCoordinatorEndToEndTests</c> already walks the
/// phase-2 loop end to end — but through <c>CoordinatorTools</c>, the in-process surface that phase 3
/// §1.1 found "is NOT wired to the shipped coordinator at all". So the loop it proves and the loop a
/// user gets are different objects. Everything here goes through the production
/// <c>AgentSpawnService</c> handlers on a real <c>AgentIpcServer</c> Unix socket — the same bytes an
/// in-jail shim writes — in the order a session actually happens, so a break in the seam BETWEEN two
/// green unit tests has somewhere to show up.</para>
///
/// <para>Each stage announces itself through <see cref="ITestOutputHelper"/>. That is the point of the
/// shape: a failure names the stage it reached rather than only the assertion that tripped.</para>
/// </summary>
public sealed class BackendWorkflowSimulation : PlanGateIpcTestBase, IClassFixture<PlanGateRig>
{
    private readonly ITestOutputHelper _out;
    private int _step;

    public BackendWorkflowSimulation(PlanGateRig rig, ITestOutputHelper output) : base(rig) => _out = output;

    private void Stage(string what) => _out.WriteLine($"── {++_step,2}. {what}");

    private void Fact_(string what) => _out.WriteLine($"      ✓ {what}");

    // ---------------------------------------------------------------- phase 2

    /// <summary>
    /// Spawn → brief → the gate holds the task → reject with feedback → revise → approve → the task is
    /// finally yielded. The load-bearing claim is the middle: a blocking call an agent can decline to
    /// make is a convention, so the daemon must withhold the task itself.
    /// </summary>
    [Fact]
    public async Task Phase2_TheWorkerAuthorsItsPlan_AndTheDaemonWithholdsTheTaskUntilApproval()
    {
        Stage("coordinator spawns, then spawns a worker over its own socket");
        var (coordinatorId, workerId) = await SpawnCoordinatorAndWorkerAsync("fix the token expiry off-by-one");
        Fact_($"coordinator={coordinatorId[..8]} worker={workerId[..8]}");

        Stage("the worker asks what it is meant to plan about");
        var brief = await CallAsync(workerId, new AgentIpcRequest(AgentIpcRequest.BriefOp));
        Assert.True(brief.Ok, brief.Error);
        Assert.Equal("fix the token expiry off-by-one", brief.Brief);
        Assert.True(
            string.IsNullOrEmpty(brief.TaskPrompt),
            "the brief carried the task prompt — the gate is decorative if the work is already in hand");
        Fact_($"brief delivered, task withheld, maxRevisions={brief.MaxRevisions}");

        Stage("the worker presents a plan and BLOCKS on the socket");
        var presenting = CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.PresentPlanOp,
            Title: "Fix token expiry off-by-one",
            PlanJson: PlanJson("src/Auth/TokenClock.cs")));

        var pending = await WaitForAsync(() =>
            Rig.Plans.Pending().FirstOrDefault(p => p.WorkerAgentId == workerId));
        Assert.False(presenting.IsCompleted, "present_plan returned before any human decided");
        Fact_($"plan {pending.PlanId[..8]} pending; the call has not returned");

        Stage("while it is blocked, the daemon holds the task");
        Assert.False(Rig.Gate.MayWork(workerId, out var why), "the worker was cleared to work with no approved plan");
        Assert.False(Rig.Gate.TaskWasReleased(workerId), "the task was released before any approval");
        Fact_($"worker may not work: {why}");

        Stage("a human rejects with feedback");
        Rig.Plans.Reject(pending.PlanId, "Don't add a dependency. Standard library only.");
        var decision = await presenting;
        Assert.True(decision.Ok, decision.Error);
        Assert.Equal("Rejected", decision.Status);
        Assert.Equal("Don't add a dependency. Standard library only.", decision.Feedback);
        Fact_($"feedback returned to the worker verbatim, revisionsRemaining={decision.RevisionsRemaining}");

        Stage("the worker revises rather than dying");
        var revising = CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.RevisePlanOp,
            PlanId: pending.PlanId,
            Title: "Fix token expiry off-by-one",
            PlanJson: PlanJson("src/Auth/TokenClock.cs")));
        var revised = await WaitForAsync(() =>
            Rig.Plans.LiveForWorker(workerId) is { Status: PlanStatus.Pending, RevisionCount: 1 } p ? p : null);
        Assert.False(revising.IsCompleted, "the revision did not block the way the first plan did");
        Fact_($"revision {revised.PlanId[..8]} pending at revisionCount={revised.RevisionCount}");

        Stage("a human approves");
        Rig.Plans.Approve(revised.PlanId, "os:simulation");
        var approved = await revising.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(approved.Ok, approved.Error);
        Assert.Equal("Approved", approved.Status);
        Fact_("approval returned to the worker");

        Stage("ONLY NOW does the task reach the worker — on the approval response itself");
        Assert.False(
            string.IsNullOrEmpty(approved.TaskPrompt),
            "approved, but the task never arrived — the worker has nothing to do");
        Assert.Contains("token expiry", approved.TaskPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.True(Rig.Gate.MayWork(workerId, out _), "approval did not clear the worker to work");
        Fact_($"task delivered after approval, not before: \"{approved.TaskPrompt}\"");
    }

    // ---------------------------------------------------------------- phase 3

    /// <summary>
    /// The four tools, driven in the order a coordinator uses them, on the real socket. The
    /// interesting one is <c>status</c>: phase 3 put the plan-gate reason on those rows precisely so a
    /// coordinator asking "why is this worker doing nothing?" is answered inside the surface rather
    /// than pushed toward reading the worker's terminal, which §4 denies.
    /// </summary>
    [Fact]
    public async Task Phase3_TheFourTools_AnswerInOrder_OverTheRealSocket()
    {
        Stage("spawn_worker");
        var (coordinatorId, workerId) = await SpawnCoordinatorAndWorkerAsync("tidy the retry helper");
        Fact_($"worker {workerId[..8]} minted");

        Stage("get_worker_status — before any plan exists");
        var status = await CallAsync(coordinatorId, new AgentIpcRequest(AgentIpcRequest.StatusOp));
        Assert.True(status.Ok, status.Error);
        Assert.NotNull(status.Agents);
        Assert.Contains(status.Agents!, row => row.Contains(workerId, StringComparison.Ordinal));
        _out.WriteLine("      rows: " + string.Join(" | ", status.Agents!));
        Fact_($"{status.Agents!.Length} row(s), the worker among them");

        Stage("send_worker_prompt BEFORE approval is refused — steering an unauthorised worker is work");
        var early = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: "prefer the stdlib"));
        Assert.False(early.Ok, "an unplanned worker accepted a prompt");
        Fact_($"refused: {early.Error}");

        Stage("the worker plans and a human approves — the coordinator is not involved");
        var presenting = CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.PresentPlanOp, Title: "Tidy retry helper", PlanJson: PlanJson("src/Retry.cs")));
        var plan = await WaitForAsync(() =>
            Rig.Plans.LiveForWorker(workerId) is { Status: PlanStatus.Pending } p ? p : null);
        Rig.Plans.Approve(plan.PlanId, "os:simulation");
        await presenting.WaitAsync(TimeSpan.FromSeconds(10));
        Fact_("worker authorised");

        Stage("get_worker_status — the row's reason must now change");
        var after = await CallAsync(coordinatorId, new AgentIpcRequest(AgentIpcRequest.StatusOp));
        Assert.True(after.Ok, after.Error);
        _out.WriteLine("      rows: " + string.Join(" | ", after.Agents!));

        // The plan gate no longer refuses these. What still can, in THIS rig, is the substrate: the fake
        // environment has no pty, so delivery fails. The assertion that matters here is therefore not
        // "it worked" but "it failed for the honest reason" — a raw errno reaching a coordinator was a
        // real defect (`TrySendPromptAsync` is a bool-returning Try that let IOException escape), and
        // the true positive belongs to the Docker leg, where a pty exists.
        Stage("send_worker_prompt — past the gate; delivery is the substrate's problem now");
        var prompt = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: "prefer the stdlib"));
        Assert.DoesNotContain("Input/output error", prompt.Error ?? string.Empty, StringComparison.Ordinal);
        Assert.False(
            (prompt.Error ?? string.Empty).Contains("no work is authorised", StringComparison.Ordinal),
            "an approved worker was still refused by the plan gate");
        Fact_(prompt.Ok ? "prompt delivered" : $"not delivered, honestly: {prompt.Error}");

        Stage("request_verification — past the gate");
        var verify = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.VerifyOp, AgentId: workerId));
        Assert.False(
            (verify.Error ?? string.Empty).Contains("no work is authorised", StringComparison.Ordinal),
            "an approved worker was still refused by the plan gate");
        Fact_(verify.Ok ? "verification requested" : $"not requested: {verify.Error}");

        Stage("a STRANGER's worker reads as missing, not as forbidden");
        var stranger = await SpawnCoordinatorAsync();
        var peek = await CallAsync(stranger, new AgentIpcRequest(
            AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: "you work for me now"));
        Assert.False(peek.Ok, "one coordinator steered another's worker");
        Fact_($"refused: {peek.Error}");
    }

    /// <summary>
    /// The refusals. A surface is defined as much by what it will not do, and each of these was a real
    /// hole phase 3 closed rather than a hypothetical.
    /// </summary>
    [Fact]
    public async Task Phase3_TheSurfaceRefusesEverythingOutsideTheFourTools()
    {
        var (coordinatorId, workerId) = await SpawnCoordinatorAndWorkerAsync("something");

        Stage("an op outside the allow-list is refused BY NAME");
        var bogus = await CallAsync(coordinatorId, new AgentIpcRequest("read_repository"));
        Assert.False(bogus.Ok, "an unknown op was served");
        Fact_($"refused: {bogus.Error}");

        Stage("a worker's own channel cannot spawn");
        var workerSpawn = await CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.SpawnOp, AgentKind: "claude-code", TaskPrompt: "escalate"));
        Assert.False(workerSpawn.Ok, "a worker spawned a worker — the role split is decorative");
        Fact_($"refused: {workerSpawn.Error}");

        Stage("a hostile AgentId on spawn is ignored, not honoured");
        var hostile = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.SpawnOp, AgentKind: "claude-code", TaskPrompt: "adopt", AgentId: workerId));
        Assert.True(hostile.Ok, hostile.Error);
        Assert.NotEqual(workerId, hostile.AgentId);
        Fact_($"minted {hostile.AgentId![..8]} instead of adopting {workerId[..8]}");

        Stage("each jail is TOLD what it is — the delivery phase 3 left missing");
        foreach (var (id, role) in new[] { (coordinatorId, "coordinator"), (workerId, "worker") })
        {
            var path = Path.Combine(Rig.Ipc.DirFor(id), AgentIpcPaths.InstructionsFileName);
            Assert.True(File.Exists(path), $"the {role}'s jail carries no operating instructions");
            var text = File.ReadAllText(path);
            Assert.False(string.IsNullOrWhiteSpace(text), $"the {role}'s instructions are empty");
            Fact_($"{role}: {AgentIpcPaths.InstructionsFileName} staged, {text.Length} chars, "
                + $"first line \"{text.Split('\n')[0].Trim()}\"");
        }

        Stage("the coordinator's own jail carries no spawn shim on the worker side");
        Assert.Equal(AgentIpcEndpointRole.Coordinator, Rig.Ipc.RoleOf(coordinatorId));
        Assert.Equal(AgentIpcEndpointRole.Worker, Rig.Ipc.RoleOf(workerId));
        Assert.True(File.Exists(Path.Combine(Rig.Ipc.DirFor(coordinatorId), AgentIpcPaths.SpawnShimFileName)));
        Assert.False(File.Exists(Path.Combine(Rig.Ipc.DirFor(workerId), AgentIpcPaths.SpawnShimFileName)));
        Fact_("shims are role-scoped on disk");
    }

    // ---------------------------------------------------------------- backpressure

    /// <summary>
    /// The stall a user actually experiences: blocked workers fill the cap, the coordinator stops
    /// spawning, and the refusal has to SAY so. A silent stall is indistinguishable from a hang, which
    /// is the whole reason the plan gate sits above the terminal in the UI.
    /// </summary>
    [Fact]
    public async Task Backpressure_BlockedWorkersFillTheCap_AndTheRefusalExplainsItself()
    {
        var coordinatorId = await SpawnCoordinatorAsync();
        var cap = Rig.Limits.MaxActiveWorkers;
        Stage($"filling the cap of {cap} with workers blocked on approval");

        for (var i = 0; i < cap; i++)
        {
            var id = await ShimSpawnAsync(coordinatorId, $"task {i}");
            _ = CallAsync(id, new AgentIpcRequest(
                AgentIpcRequest.PresentPlanOp, Title: $"plan {i}", PlanJson: PlanJson("src/a.cs")));
            await WaitForAsync(() => Rig.Plans.Pending().FirstOrDefault(p => p.WorkerAgentId == id));
        }
        Fact_($"{cap} workers blocked");

        Stage("the next spawn is refused, and says why");
        var refused = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.SpawnOp, AgentKind: "claude-code", TaskPrompt: "one more"));
        Assert.False(refused.Ok, "the cap did not hold at the daemon");
        Assert.False(string.IsNullOrWhiteSpace(refused.Error), "refused with no reason a human could read");
        Fact_($"refused: {refused.Error}");
    }
}
