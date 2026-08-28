using System;
using Mainguard.Agents.Agents.Ipc;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The worker's FIRST USER TURN — the text without which the phase-2 plan loop cannot begin, and which
/// must nonetheless never be the task.
///
/// <para><b>The deadlock, observed live.</b> A worker jail launched
/// <c>claude --append-system-prompt &lt;operating instructions&gt;</c> and nothing else. A vendor CLI does
/// not act on a system prompt: it drew its banner and waited at an empty input box. Six minutes later the
/// outbox was empty, no transcript existed, and <c>mainguard-plan</c> had never run. Nothing could rescue
/// it: a worker's terminal is input-locked (P2-14), and the coordinator's <c>send_worker_prompt</c> is
/// refused for a worker at the gate — <c>"&lt;id&gt; has not presented a plan yet — no work is
/// authorised."</c> No first turn without a plan; no plan without a first turn.</para>
///
/// <para><b>What these tests are really guarding.</b> Not the prose — the boundary. The fix would be
/// worthless if the turn could carry the work, so the assertions below are mostly negative: the text is a
/// function of the role and the shim path and of nothing else, it names only operations the worker's own
/// shim serves, and it says in as many words that the worker does not have the task.</para>
/// </summary>
public class AgentKickoffPromptTests
{
    private const string WorkerShim = AgentIpcPaths.SandboxMount + "/" + AgentIpcPaths.PlanShimFileName;

    /// <summary>
    /// THE TEST THAT WOULD HAVE CAUGHT IT, at this layer: a worker is given a first turn at all. An empty
    /// or absent turn is the six-minute idle, and it must be a red test rather than a hang.
    /// </summary>
    [Fact]
    public void AWorkerIsGivenAFirstTurn_AndItTellsItToAskTheDaemonForItsBrief()
    {
        var turn = AgentKickoffPrompt.For(AgentIpcEndpointRole.Worker, WorkerShim);

        Assert.False(string.IsNullOrWhiteSpace(turn));
        Assert.Contains(WorkerShim + " brief", turn, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE BOUNDARY. The turn is a pure function of <c>(role, shimPath)</c>: there is no overload that
    /// takes a task, a title, an agent id or a coordinator id, so the text cannot carry the work even by
    /// mistake. Asserted by construction — two calls with the same arguments and nothing else in scope
    /// produce the identical string — because "the caller happens not to pass the task" is exactly the
    /// reasoning that stops being true without anyone noticing (phase 3 §2.6).
    /// </summary>
    [Fact]
    public void TheTurnIsAPureFunctionOfRoleAndShimPath_SoItCannotCarryTheTask()
    {
        Assert.Equal(
            AgentKickoffPrompt.For(AgentIpcEndpointRole.Worker, WorkerShim),
            AgentKickoffPrompt.For(AgentIpcEndpointRole.Worker, WorkerShim));

        // A different shim path is the ONLY thing that can change the text.
        Assert.NotEqual(
            AgentKickoffPrompt.For(AgentIpcEndpointRole.Worker, WorkerShim),
            AgentKickoffPrompt.For(AgentIpcEndpointRole.Worker, "/elsewhere/mainguard-plan"));
    }

    /// <summary>
    /// The turn tells the worker, in as many words, that it does NOT have the task and must not start.
    /// Phase 2 §2.2 makes the daemon the enforcement, and this text must not contradict it: a first turn
    /// that read like a go-ahead would produce a worker doing unauthorised work that the merge gate then
    /// refuses, which is a worse outcome than the deadlock it replaced.
    /// </summary>
    [Fact]
    public void TheTurnSaysTheWorkerDoesNotHaveTheTaskYet()
    {
        var turn = AgentKickoffPrompt.For(AgentIpcEndpointRole.Worker, WorkerShim)!;

        Assert.Contains("do not have the task yet", turn, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("must not start work", turn, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// MG-12 drift guard, the same one <c>AgentOperatingInstructionsTests</c> applies to the coordinator
    /// text: every command the turn tells a worker to run must be an operation its shim actually serves.
    /// A first turn naming an op the daemon dropped is a description that outlived what it described —
    /// and here it would strand the worker on its very first action.
    /// </summary>
    [Theory]
    [InlineData("brief")]
    [InlineData("present")]
    public void EveryOperationTheTurnNames_IsOneTheWorkersShimServes(string op)
    {
        var turn = AgentKickoffPrompt.For(AgentIpcEndpointRole.Worker, WorkerShim)!;

        Assert.Contains(WorkerShim + " " + op, turn, StringComparison.Ordinal);
        Assert.Contains("mainguard-plan " + op, WorkerPlanShim.Script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The turn never names a COORDINATOR op. A worker's endpoint answers <c>unknown op</c> to those by
    /// construction (phase 2 §2.7), so telling a worker to run one would spend its first action on a
    /// refusal.
    /// </summary>
    [Theory]
    [InlineData("spawn")]
    [InlineData("prompt")]
    [InlineData("verify")]
    [InlineData("status")]
    public void TheTurnNeverNamesACoordinatorOperation(string op)
    {
        var turn = AgentKickoffPrompt.For(AgentIpcEndpointRole.Worker, WorkerShim)!;

        Assert.DoesNotContain(WorkerShim + " " + op, turn, StringComparison.Ordinal);
    }

    /// <summary>
    /// A COORDINATOR gets no first turn, and the asymmetry is a decision rather than an oversight. Its
    /// terminal is not input-locked (only <c>AgentRoles.Managed</c> is), so a human CAN type into it —
    /// which is what makes the worker's missing turn a deadlock and the coordinator's merely a wait. And
    /// its real first turn is the operator's request, which the daemon does not have: inventing one would
    /// set a coordinator fanning out workers for work nobody asked for.
    /// </summary>
    [Fact]
    public void ACoordinatorGetsNoFirstTurn_BecauseItsFirstTurnIsTheOperatorsRequest()
    {
        Assert.Null(AgentKickoffPrompt.For(
            AgentIpcEndpointRole.Coordinator,
            AgentIpcPaths.SandboxMount + "/" + AgentIpcPaths.SpawnShimFileName));
    }

    /// <summary>
    /// No shim path, no turn. A jail with no IPC dir has no <c>mainguard-plan</c> in it — every
    /// external-PR head and every manually spawned worker — so a turn telling its CLI to run one would
    /// buy a "command not found" and an agent with no idea what to do next. Those sessions are not
    /// deadlocked either: nothing is being withheld from them.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AJailWithNoShim_GetsNoTurn(string shimPath)
    {
        Assert.Null(AgentKickoffPrompt.For(AgentIpcEndpointRole.Worker, shimPath));
    }
}
