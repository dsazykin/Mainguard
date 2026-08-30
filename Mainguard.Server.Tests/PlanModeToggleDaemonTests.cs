using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The plan-mode toggle <b>at the daemon</b>, over the same Unix-socket channel a jail speaks.
///
/// <para>The claim under test is the one the design turns on: <b>off is a coherent mode, not a set of
/// disabled checks.</b> A worker spawned with plan mode off gets its task, is steerable, is verifiable,
/// is committable and gets a merge-queue row — and every one of those has a paired assertion that the
/// same call still refuses with the toggle on, in the same fixture, minutes apart. A mode that only ever
/// says yes cannot be told from a gate somebody deleted, which is exactly the failure class this
/// codebase keeps finding (MG-12).</para>
///
/// <para>This class takes its OWN <see cref="PlanGateRig"/> (xUnit builds a class fixture per class), so
/// the switch it flips is its own daemon's and never leaks into the plan-gate tests next door.</para>
/// </summary>
public sealed class PlanModeToggleDaemonTests : PlanGateIpcTestBase, IClassFixture<PlanGateRig>
{
    public PlanModeToggleDaemonTests(PlanGateRig rig) : base(rig)
    {
    }

    private PlanModeSwitch Mode => Rig.Services.GetRequiredService<PlanModeSwitch>();

    private MergeQueueProvisioner Provisioner => Rig.Services.GetRequiredService<MergeQueueProvisioner>();

    /// <summary>Spawns a worker through the coordinator's shim with the toggle in a known state.</summary>
    private async Task<(string CoordinatorId, string WorkerId, string Status)> SpawnWithPlanModeAsync(
        bool planModeEnabled, string task, string title = "Fix the token clock")
    {
        Mode.Set(planModeEnabled, "os:test");
        var coordinatorId = await SpawnCoordinatorAsync();
        var response = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.SpawnOp, AgentKind: "claude-code", TaskPrompt: task, Title: title));
        Assert.True(response.Ok, response.Error);
        Track(response.AgentId!);
        return (coordinatorId, response.AgentId!, response.Status!);
    }

    // ---- OFF: the task arrives at spawn ---------------------------------------------------

    /// <summary>
    /// <b>The behaviour the toggle names, end to end through the wire.</b> With plan mode off the worker's
    /// <c>task</c> op answers immediately with the exact prompt the coordinator sent — the daemon is no
    /// longer withholding it — and the coordinator is told "Working" rather than "AwaitingPlan", because a
    /// coordinator told to wait for an approval that is never coming is §12.2/F2 at the other end of the
    /// loop.
    /// </summary>
    [Fact]
    public async Task WithPlanModeOff_TheWorkerGetsItsTaskAtOnce_AndTheCoordinatorIsToldItIsWorking()
    {
        const string task = "rewrite TokenClock so expiry is computed in UTC and add boundary tests";
        var (_, workerId, status) = await SpawnWithPlanModeAsync(planModeEnabled: false, task);

        Assert.Equal("Working", status);

        var response = await CallAsync(workerId, new AgentIpcRequest(AgentIpcRequest.TaskOp));
        Assert.True(response.Ok, response.Error);
        Assert.Equal("Task", response.Status);
        Assert.Equal(task, response.TaskPrompt);

        // The brief is STILL not the task — the toggle changed which door opens, not what a brief is.
        var brief = await CallAsync(workerId, new AgentIpcRequest(AgentIpcRequest.BriefOp));
        Assert.Equal("Fix the token clock", brief.Brief);
        Assert.True(string.IsNullOrEmpty(brief.TaskPrompt));
    }

    /// <summary>
    /// The paired negative, and the one that proves the toggle is doing the work. Same fixture, same
    /// wire, same op — with plan mode ON the task is refused, and the refusal names what is being waited
    /// for.
    /// </summary>
    [Fact]
    public async Task WithPlanModeOn_TheTaskOpIsRefusedUntilAPlanIsApproved()
    {
        const string task = "rewrite the expiry clock";
        var (_, workerId, status) = await SpawnWithPlanModeAsync(planModeEnabled: true, task);
        Assert.Equal("AwaitingPlan", status);

        var refused = await CallAsync(workerId, new AgentIpcRequest(AgentIpcRequest.TaskOp));
        Assert.False(refused.Ok);
        Assert.Contains("has not presented a plan yet", refused.Error!, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(refused.TaskPrompt));

        // …and yields the task once a human has approved, through the same one exit.
        var present = CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.PresentPlanOp, PlanJson: PlanJson("src/TokenClock.cs")));
        var plan = await WaitForPendingPlanAsync(workerId);
        Rig.Plans.Approve(plan.PlanId, "os:test");
        Assert.True((await present).Ok);

        var granted = await CallAsync(workerId, new AgentIpcRequest(AgentIpcRequest.TaskOp));
        Assert.True(granted.Ok, granted.Error);
        Assert.Equal(task, granted.TaskPrompt);
    }

    /// <summary>
    /// An ungated worker that presents a plan anyway — a worker following stale instructions, or an old
    /// jail — is <b>refused</b>. Humouring it would queue a card in front of an operator who switched
    /// approvals off, and the worker would then block on the decision forever while holding a jail and a
    /// cap slot, having already been given its task.
    /// </summary>
    [Fact]
    public async Task WithPlanModeOff_PresentingAPlanIsRefused_AndQueuesNothing()
    {
        var (_, workerId, _) = await SpawnWithPlanModeAsync(planModeEnabled: false, "do the work");

        foreach (var request in new[]
                 {
                     new AgentIpcRequest(AgentIpcRequest.PresentPlanOp, PlanJson: PlanJson("src/a.cs")),
                     new AgentIpcRequest(AgentIpcRequest.RevisePlanOp, PlanId: "p1", PlanJson: PlanJson("src/a.cs")),
                     new AgentIpcRequest(AgentIpcRequest.AwaitDecisionOp, PlanId: "p1"),
                 })
        {
            var response = await CallWithinAsync(workerId, request);
            Assert.False(response.Ok);
            Assert.Contains("plan mode is off", response.Error!, StringComparison.Ordinal);
        }

        Assert.Null(Rig.Plans.LatestForWorker(workerId));
    }

    /// <summary>
    /// <b>Not a half-enforced gate.</b> Every path the plan gate governs answers YES for an ungated
    /// worker: steering, verification, and committing. One of them still refusing would leave a worker
    /// that has been handed its task and cannot be steered, verified or have its work recorded — which is
    /// strictly worse than either mode.
    /// </summary>
    [Fact]
    public async Task WithPlanModeOff_SteeringVerificationAndCommittingAreAllPermitted()
    {
        var (coordinatorId, workerId, _) = await SpawnWithPlanModeAsync(planModeEnabled: false, "do the work");

        // Steering: the plan gate does not refuse it. (The delivery itself needs a bound terminal, which
        // this substrate has none of — so what is asserted is that the refusal is NOT the gate's.)
        var prompt = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: "prefer the stdlib"));
        Assert.DoesNotContain("no work is authorised", prompt.Error ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("waiting on your approval", prompt.Error ?? string.Empty, StringComparison.Ordinal);

        var verify = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.VerifyOp, AgentId: workerId));
        Assert.DoesNotContain("no work is authorised", verify.Error ?? string.Empty, StringComparison.Ordinal);

        var commit = await CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.CommitWorkOp, Message: "feat: do the work"));
        Assert.DoesNotContain("no work is authorised", commit.Error ?? string.Empty, StringComparison.Ordinal);

        // And the gate itself agrees, which is the authority the three above delegate to.
        Assert.True(Rig.Gate.MayWork(workerId, out _));
        Assert.True(Rig.Gate.MayAutoVerify(workerId, out _));
    }

    /// <summary>
    /// <b>An ungated worker is not asked to declare anything.</b> With plan mode off there is no approved
    /// <c>approach</c>, so there is nothing to have departed from and demanding a declaration would be a
    /// ritual — the exact "a control that is always present and never means anything" shape this codebase
    /// keeps deleting. It is <c>ApprovedForWorker</c> that decides this, the same single authority the
    /// scope comparison uses, and not a second reading of the mode switch.
    ///
    /// <para>A declaration it volunteers anyway is <b>told</b> it was not recorded rather than silently
    /// dropped or turned into a failed commit: the commit is the thing that must not be lost, and quiet
    /// discarding is what sits at the bottom of most of this subsystem's defects.</para>
    /// </summary>
    [Fact]
    public async Task WithPlanModeOff_ACommitNeedsNoDeclaration_AndAVolunteeredOneIsSaidToBeUnrecorded()
    {
        var (_, workerId, _) = await SpawnWithPlanModeAsync(planModeEnabled: false, "do the work");

        var bare = await CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.CommitWorkOp, Message: "feat: do the work"));
        Assert.True(bare.Ok, bare.Error);
        Assert.DoesNotContain(
            "deviation declaration", bare.Error ?? string.Empty, StringComparison.Ordinal);

        var volunteered = await CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.CommitWorkOp, Message: "feat: more work", NoDeviations: true));
        Assert.True(volunteered.Ok, volunteered.Error);
        Assert.Contains("not recorded", volunteered.Feedback ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(
            "no approved plan", volunteered.Feedback ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>The paired negative: with plan mode on, those same three are refused BY THE GATE.</summary>
    [Fact]
    public async Task WithPlanModeOn_SteeringVerificationAndCommittingAreAllRefusedByTheGate()
    {
        var (coordinatorId, workerId, _) = await SpawnWithPlanModeAsync(planModeEnabled: true, "do the work");

        var prompt = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.PromptOp, AgentId: workerId, Prompt: "prefer the stdlib"));
        Assert.False(prompt.Ok);
        Assert.Contains("has not presented a plan yet", prompt.Error!, StringComparison.Ordinal);

        var verify = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.VerifyOp, AgentId: workerId));
        Assert.False(verify.Ok);
        Assert.Contains("has not presented a plan yet", verify.Error!, StringComparison.Ordinal);

        var commit = await CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.CommitWorkOp, Message: "feat: do the work"));
        Assert.False(commit.Ok);
        Assert.Contains("has not presented a plan yet", commit.Error!, StringComparison.Ordinal);

        Assert.False(Rig.Gate.MayAutoVerify(workerId, out _));
    }

    /// <summary>
    /// <b>G1, the queue row.</b> A queue row is a claim on a human's attention that arrives carrying
    /// Verify, so phase 3 made it ask the plan gate. With plan mode off there is nothing to wait for, so
    /// the row must exist at once — a worker doing real work whose branch never appears in the queue is
    /// the same silent dead end §12.2 was about.
    /// </summary>
    [Fact]
    public async Task WithPlanModeOff_TheMergeQueueRowIsCreatedAtSpawn_AndWithItOnItIsDeferred()
    {
        var (_, ungated, _) = await SpawnWithPlanModeAsync(planModeEnabled: false, "do the work");
        Assert.DoesNotContain(
            Provisioner.DeferredEntries(), e => string.Equals(e.AgentId, ungated, StringComparison.Ordinal));

        var (_, gated, _) = await SpawnWithPlanModeAsync(planModeEnabled: true, "do the other work");
        Assert.Contains(
            Provisioner.DeferredEntries(), e => string.Equals(e.AgentId, gated, StringComparison.Ordinal));
    }

    /// <summary>
    /// The merge record must never say "plan approved" about a worker that never had a plan, and must not
    /// borrow the manual-agent wording either. This is the sentence a later reader reconstructs the
    /// authorisation from, so all three cases are distinct.
    /// </summary>
    [Fact]
    public async Task TheMergeRecordSaysWhichAuthorisationTheWorkerActuallyHad()
    {
        var (_, ungated, _) = await SpawnWithPlanModeAsync(planModeEnabled: false, "do the work");

        var evidence = Rig.Gate.MergeEvidence(ungated)!;
        Assert.Contains("OFF at spawn", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("plan approved", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("not a plan-gated worker", evidence, StringComparison.Ordinal);
    }

    // ---- The RPC surface --------------------------------------------------------------------

    /// <summary>
    /// The toggle over gRPC, read back from the daemon rather than echoed. The response is what the client
    /// renders, so it has to be the daemon's state and not the request.
    /// </summary>
    [Fact]
    public async Task SetPlanMode_ChangesIt_AndGetPlanModeReadsItBack()
    {
        using var fixture = new DaemonFixture();
        var plans = new Mainguard.Protos.V1.PlanApprovalService.PlanApprovalServiceClient(fixture.CreateChannel());

        var initial = await plans.GetPlanModeAsync(new GetPlanModeRequest(), fixture.AuthHeaders());
        Assert.True(initial.Enabled);
        Assert.Contains("ON", initial.Summary, StringComparison.Ordinal);

        var off = await plans.SetPlanModeAsync(new SetPlanModeRequest { Enabled = false }, fixture.AuthHeaders());
        Assert.False(off.Enabled);
        Assert.Contains("OFF", off.Summary, StringComparison.Ordinal);
        Assert.False((await plans.GetPlanModeAsync(new GetPlanModeRequest(), fixture.AuthHeaders())).Enabled);
        Assert.False(fixture.Services.GetRequiredService<PlanModeSwitch>().Enabled);

        var on = await plans.SetPlanModeAsync(new SetPlanModeRequest { Enabled = true }, fixture.AuthHeaders());
        Assert.True(on.Enabled);
    }

    /// <summary>
    /// <b>The state travels on the plan stream, on every update including the empty one.</b> An empty plan
    /// gate has two explanations — nothing is running, or approvals are off — and this field is the only
    /// thing that tells a human which. Carried from the daemon rather than derived client-side for the
    /// §2.6 reason: a surface that disagrees with its gate is how somebody comes to believe they still
    /// have an approval step they switched off.
    /// </summary>
    [Fact]
    public async Task ThePlanStreamCarriesThePlanModeState()
    {
        using var fixture = new DaemonFixture();
        var plans = new Mainguard.Protos.V1.PlanApprovalService.PlanApprovalServiceClient(fixture.CreateChannel());
        await plans.SetPlanModeAsync(new SetPlanModeRequest { Enabled = false }, fixture.AuthHeaders());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Filtered to a coordinator that has never existed, so this update is genuinely EMPTY — the case
        // the field exists for, and one an unfiltered stream cannot produce here (the plan store is one
        // file for the whole assembly, so other tests' plans are in it).
        using var stream = plans.StreamPlans(
            new StreamPlansRequest { CoordinatorId = "coordinator-" + Guid.NewGuid().ToString("N") },
            fixture.AuthHeaders(), cancellationToken: cts.Token);
        Assert.True(await stream.ResponseStream.MoveNext(cts.Token));

        var update = stream.ResponseStream.Current;
        Assert.Empty(update.Plans);            // the empty gate — the case that needs the field most
        Assert.False(update.PlanModeEnabled);
        Assert.Contains("OFF", update.PlanModeSummary, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Contract §4, extended.</b> A coordinator that could turn plan mode off would hold the gate it is
    /// denied at — and hold it wholesale, for every worker it spawns from then on, with no card ever
    /// reaching a human. Denied at the interceptor, where the daemon serves the call, and asserted on the
    /// ROLE gate's own message so it cannot pass because the bearer layer rejected it first (MG-12).
    /// </summary>
    [Fact]
    public async Task RoleInterceptor_DeniesSetPlanModeToACoordinator()
    {
        const string coordinatorToken = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        using var fixture = new DaemonFixture();
        fixture.Services.GetRequiredService<ConnectionRoleRegistry>().RegisterCoordinatorToken(coordinatorToken);
        var plans = new Mainguard.Protos.V1.PlanApprovalService.PlanApprovalServiceClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() => plans.SetPlanModeAsync(
            new SetPlanModeRequest { Enabled = false },
            fixture.AuthHeaders(coordinatorToken)).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Contains("coordinator role", ex.Status.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Invalid bearer token", ex.Status.Detail, StringComparison.Ordinal);

        // And it did not take effect: the gate is exactly where it was.
        Assert.True(fixture.Services.GetRequiredService<PlanModeSwitch>().Enabled);
    }

    /// <summary>
    /// One request with a DEADLINE — and a failed assertion, never a hang, when it is missed.
    ///
    /// <para>These three ops block <b>by design</b> once they are accepted: <c>present</c>/<c>revise</c>
    /// park on the socket until a human decides, and that parking IS the gate, so the shim gives them no
    /// timeout. The claim under test is that an ungated worker is refused <i>before</i> it can park —
    /// which makes "the call never returns" the exact failure mode, and a test that hangs on its own
    /// failure reports nothing at all.</para>
    ///
    /// <para>Not hypothetical: with the ungated refusal mutated out, this test parked the whole tier for
    /// fifteen minutes until the run was killed, and the mutation scored no result. A guard whose failure
    /// mode is a hang is indistinguishable from a guard nobody tested.</para>
    /// </summary>
    private async Task<AgentIpcResponse> CallWithinAsync(
        string agentId, AgentIpcRequest request, int seconds = 30)
    {
        var call = CallAsync(agentId, request);
        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(seconds)));
        Assert.True(
            ReferenceEquals(finished, call),
            $"`{request.Op}` did not return within {seconds}s. An ungated worker's plan ops must be "
            + "REFUSED, not parked on a gate that nobody is watching — a worker that parks there holds "
            + "its jail and its cap slot forever, having already been given its task.");
        return await call;
    }

    /// <summary>Waits for this worker's presented plan without racing the blocking present call.</summary>
    private async Task<PendingPlan> WaitForPendingPlanAsync(string workerAgentId)
    {
        for (var i = 0; i < 200; i++)
        {
            if (Rig.Plans.LatestForWorker(workerAgentId) is { Status: PlanStatus.Pending } plan)
            {
                return plan;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"{workerAgentId} never presented a plan.");
    }
}
