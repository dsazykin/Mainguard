using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The in-proc daemon with a fake substrate, shared by a plan-gate test class. Shared because a
/// <see cref="DaemonFixture"/> host build is the dominant cost of this tier; the tests stay independent
/// by each spawning their own coordinator and stopping the workers they created, and by scoping every
/// assertion to their own agent ids rather than to daemon-global counts.
/// </summary>
public sealed class PlanGateRig : IDisposable
{
    public const string RepoHandle = "fake-repo-hash-plan";

    private readonly DaemonFixture _daemon = new();
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;
    private readonly string _root;

    public PlanGateRig()
    {
        _root = Path.Combine(Path.GetTempPath(), "mg-plangate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "repos", RepoHandle)); // "provisioned"
        Environment = new AgentSessionRepoScopingTests.FakeAgentEnvironment(
            _root, new AgentSessionRepoScopingTests.RecordingEngine());
        var environment = Environment;
        _host = _daemon.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<IAgentEnvironment>(environment)));
        _ = _host.Services; // build once, here, not inside the first test
    }

    /// <summary>The substrate this daemon was built over — the only way to see what the daemon asked it
    /// to do (which worktree it committed on, and on whose behalf).</summary>
    internal AgentSessionRepoScopingTests.FakeAgentEnvironment Environment { get; }

    public AgentSpawnService Spawns => _host.Services.GetRequiredService<AgentSpawnService>();

    public AgentIpcServer Ipc => _host.Services.GetRequiredService<AgentIpcServer>();

    public PlanApprovalService Plans => _host.Services.GetRequiredService<PlanApprovalService>();

    public WorkerPlanGate Gate => _host.Services.GetRequiredService<WorkerPlanGate>();

    public CoordinatorLimits Limits => _host.Services.GetRequiredService<CoordinatorLimits>();

    public AgentSessionStore Sessions => _host.Services.GetRequiredService<AgentSessionStore>();

    /// <summary>
    /// The bound-terminal registry, so a test can give a worker a writable session. Without one,
    /// <c>send_worker_prompt</c> can only ever be observed FAILING on this substrate — which is part of
    /// how two of the contract's four tools ended up with no positive coverage anywhere.
    /// </summary>
    public TerminalSessionManager Terminals => _host.Services.GetRequiredService<TerminalSessionManager>();

    public void Dispose()
    {
        _host.Dispose();
        _daemon.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// The phase-2 plan gate <b>at the daemon</b>, over the real Unix-socket channel the jail uses
/// (coordinator contract §2 + §5).
///
/// <para>This is the file that answers "is the gate enforced, or merely described?". Everything here goes
/// through the production <see cref="AgentSpawnService"/> handlers on a real
/// <see cref="AgentIpcServer"/> endpoint — the same bytes an in-jail shim writes. Nothing is asserted
/// about prompts; MG-12 is the standing reason (role authorization once looked present in the source and
/// was dead code that failed open).</para>
/// </summary>
public sealed class WorkerPlanChannelIpcTests : PlanGateIpcTestBase, IClassFixture<PlanGateRig>
{
    public WorkerPlanChannelIpcTests(PlanGateRig rig) : base(rig)
    {
    }

    // ---- the daemon withholds the task -----------------------------------

    /// <summary>
    /// <b>The separation, at the worker's own channel.</b> The brief is the coordinator's <c>--title</c>
    /// and it is NOT the task — the assertion this test could not previously make, because until
    /// 2026-08-29 the daemon derived the brief from the task (<c>Title ?? TaskPrompt</c>) and this test's
    /// own expectation was the task prompt it had just spawned with. It passed, and the thing it was
    /// named for was false.
    /// </summary>
    [Fact]
    public async Task ACoordinatorSpawnedWorker_GetsAPlanShimAndABrief_ButNotItsTask()
    {
        const string title = "Fix the token clock";
        const string task = "rewrite TokenClock so expiry is computed in UTC and add boundary tests";
        var (_, workerId) = await SpawnCoordinatorAndWorkerAsync(task, title);

        // The worker's jail carries mainguard-plan and NOT the coordinator's spawn shim.
        var dir = Rig.Ipc.DirFor(workerId);
        Assert.True(File.Exists(Path.Combine(dir, AgentIpcPaths.PlanShimFileName)));
        Assert.False(File.Exists(Path.Combine(dir, AgentIpcPaths.SpawnShimFileName)));
        Assert.Equal(AgentIpcEndpointRole.Worker, Rig.Ipc.RoleOf(workerId));

        // `brief` tells it what to plan about — and carries no task prompt.
        var brief = await CallAsync(workerId, new AgentIpcRequest(AgentIpcRequest.BriefOp));
        Assert.True(brief.Ok);
        Assert.Equal(title, brief.Brief);
        Assert.True(string.IsNullOrEmpty(brief.TaskPrompt));
        Assert.Equal(Rig.Limits.MaxPlanRevisions, brief.MaxRevisions);

        // The point: the brief is not the task, and does not contain it. Asserted both ways so a
        // reinstated `Title ?? TaskPrompt` fallback fails here rather than passing quietly.
        Assert.NotEqual(task, brief.Brief);
        Assert.DoesNotContain(task, brief.Brief!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A coordinator that sends no title is <b>refused</b>, and no worker is minted for it.
    ///
    /// <para>This is the daemon half of the contract §3 change, and it is asserted through the socket
    /// rather than against the shim, because the shim is a file in a read-only mount and the wire is what
    /// a jail can actually speak. A shim-only check would be the convention MG-12 warns about.</para>
    ///
    /// <para><b>The refusal must also not fall through to an ungated spawn.</b> <c>SpawnAsync</c> reads
    /// "neither title nor task" as "this spawn is not plan-gated" — the operator's own spelling — so the
    /// dangerous failure mode is not a bad brief but a Managed worker with no gate at all. Hence the
    /// worker-count assertion.</para>
    /// </summary>
    [Theory]
    // The dangerous row, and the reason this check lives at the CHANNEL: with neither field,
    // SpawnAsync would read the request as "not plan-gated" and mint an UNGATED managed worker.
    [InlineData(null, null, "a task is required")]
    [InlineData(null, "rewrite TokenClock", "a title is required")]
    [InlineData("   ", "rewrite TokenClock", "a title is required")]
    [InlineData("Fix the clock", null, "a task is required")]
    [InlineData("Fix the clock", "   ", "a task is required")]
    [InlineData("rewrite TokenClock", "rewrite TokenClock", "must not be the task")]
    public async Task ASpawnWhoseBriefIsMissingOrIsTheTask_IsRefused_AndSpawnsNothing(
        string? title, string? task, string expected)
    {
        var coordinatorId = await SpawnCoordinatorAsync();
        var before = Rig.Sessions.List().Count;

        var response = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.SpawnOp, AgentKind: "claude-code", TaskPrompt: task, Title: title));

        Assert.False(response.Ok);
        Assert.Contains(expected, response.Error!, StringComparison.Ordinal);
        Assert.Null(response.AgentId);
        Assert.Equal(before, Rig.Sessions.List().Count);
    }

    /// <summary>
    /// The same rule one layer down, at <c>SpawnAsync</c> itself — the entry point every future
    /// plan-gated caller reaches, not just the shim channel. It refuses BEFORE <c>_store.Spawn</c>, so a
    /// bad brief costs no session record; letting it reach <c>Hold</c>'s throw instead would leave an
    /// orphan session behind. Asserting the session count is what makes the placement testable rather
    /// than a comment.
    /// </summary>
    [Fact]
    public async Task SpawnAsync_WithAPlanGatedTaskAndNoBrief_ThrowsBeforeMintingASession()
    {
        var before = Rig.Sessions.List().Count;

        await Assert.ThrowsAsync<ArgumentException>(() => Rig.Spawns.SpawnAsync(
            PlanGateRig.RepoHandle, "claude-code", null, AgentRoles.Managed, CancellationToken.None,
            heldTaskTitle: null, heldTaskPrompt: "rewrite TokenClock"));

        Assert.Equal(before, Rig.Sessions.List().Count);
    }

    /// <summary>
    /// An over-long or multi-line title is refused too: it is the headline on a human's approval card,
    /// and "paste the task in as the title" is the shape the fallback used to produce automatically.
    /// </summary>
    [Fact]
    public async Task ATitleThatIsNotAHeadline_IsRefused()
    {
        var coordinatorId = await SpawnCoordinatorAsync();

        var tooLong = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.SpawnOp, AgentKind: "claude-code", TaskPrompt: "do the work",
            Title: new string('x', WorkerPlanGate.MaxBriefLength + 1)));
        Assert.False(tooLong.Ok);
        Assert.Contains("headline", tooLong.Error!, StringComparison.Ordinal);

        var multiLine = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.SpawnOp, AgentKind: "claude-code", TaskPrompt: "do the work",
            Title: "Fix the clock\nand everything else"));
        Assert.False(multiLine.Ok);
        Assert.Contains("single line", multiLine.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PresentingAPlan_BlocksTheWorkerOnTheSocket_UntilAHumanApproves_AndOnlyThenYieldsTheTask()
    {
        var (_, workerId) = await SpawnCoordinatorAndWorkerAsync("rewrite TokenClock");

        var call = CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.PresentPlanOp,
            PlanJson: PlanJson("src/Auth/TokenClock.cs"),
            Title: "Fix the clock"));

        // The socket really is parked: the worker is not going anywhere without a human.
        var returnedEarly = await Task.WhenAny(call, Task.Delay(400)) == call;
        Assert.False(returnedEarly, "present_plan returned before a human decided");
        Assert.Contains(workerId, Rig.Gate.BlockedWorkerIds());

        var pending = await WaitForAsync(() => Rig.Plans.LiveForWorker(workerId));
        Assert.Equal(PlanStatus.Pending, pending.Status);
        Rig.Plans.Approve(pending.PlanId, "uid:1000");

        var response = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(response.Ok);
        Assert.Equal("Approved", response.Status);
        Assert.Equal("rewrite TokenClock", response.TaskPrompt); // released with the approval, never before
        Assert.DoesNotContain(workerId, Rig.Gate.BlockedWorkerIds());
    }

    [Fact]
    public async Task RejectionComesBackAsFeedback_AndTheRevisedPlanBlocksAgain()
    {
        var (_, workerId) = await SpawnCoordinatorAndWorkerAsync("rebuild the index");

        var first = CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.PresentPlanOp, PlanJson: PlanJson("src/**"), Title: "Rebuild"));
        var pending = await WaitForAsync(() => Rig.Plans.LiveForWorker(workerId));
        Rig.Plans.Reject(pending.PlanId, "src/** is the whole tree — scope it");

        var rejected = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(rejected.Ok);
        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal("src/** is the whole tree — scope it", rejected.Feedback);
        Assert.True(string.IsNullOrEmpty(rejected.TaskPrompt)); // still no task
        Assert.Equal(Rig.Limits.MaxPlanRevisions, rejected.RevisionsRemaining);

        // The revision re-presents and blocks again; approving it releases the task.
        var second = CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.RevisePlanOp, PlanId: pending.PlanId,
            PlanJson: PlanJson("src/Search/Indexer.cs"), Title: "Rebuild (scoped)"));
        var revised = await WaitForAsync(() =>
            Rig.Plans.LiveForWorker(workerId) is { Status: PlanStatus.Pending, RevisionCount: 1 } p ? p : null);
        Assert.Equal(new[] { "src/Search/Indexer.cs" }, revised.Plan.Scope);

        Rig.Plans.Approve(revised.PlanId, "uid:1000");
        var approved = await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Approved", approved.Status);
        Assert.Equal("rebuild the index", approved.TaskPrompt);
    }

    [Fact]
    public async Task TheRejectionThatSpendsTheBudget_TellsTheWorkerToStop()
    {
        var (_, workerId) = await SpawnCoordinatorAndWorkerAsync("do the thing");
        var max = Rig.Limits.MaxPlanRevisions;

        var call = CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.PresentPlanOp, PlanJson: PlanJson("src/a.cs"), Title: "T"));
        var plan = await WaitForAsync(() => Rig.Plans.LiveForWorker(workerId));

        for (var round = 0; round <= max; round++)
        {
            await WaitForAsync(() => Rig.Plans.Get(plan.PlanId) is { Status: PlanStatus.Pending } p ? p : null);
            Rig.Plans.Reject(plan.PlanId, $"no ({round})");

            var answer = await call.WaitAsync(TimeSpan.FromSeconds(10));
            if (round < max)
            {
                Assert.Equal("Rejected", answer.Status);
                call = CallAsync(workerId, new AgentIpcRequest(
                    AgentIpcRequest.RevisePlanOp, PlanId: plan.PlanId,
                    PlanJson: PlanJson($"src/{round}.cs"), Title: "T"));
            }
            else
            {
                // The (max+1)th rejection: the daemon stops the loop rather than inviting another plan.
                Assert.Equal("Escalated", answer.Status);
                Assert.True(string.IsNullOrEmpty(answer.TaskPrompt));
            }
        }

        // And a worker that tries anyway is refused daemon-side.
        var defiant = await CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.RevisePlanOp, PlanId: plan.PlanId, PlanJson: PlanJson("src/z.cs"), Title: "T"));
        Assert.False(defiant.Ok);
        Assert.Equal(PlanStatus.Escalated, Rig.Plans.Get(plan.PlanId)!.Status);
    }

    // ---- role scoping and ownership --------------------------------------

    [Fact]
    public async Task AWorkerCannotReachACoordinatorOp_AndACoordinatorCannotReachAPlanOp()
    {
        var (coordinatorId, workerId) = await SpawnCoordinatorAndWorkerAsync("t");
        var managedBefore = Rig.Sessions.List().Count(s => s.Role == AgentRoles.Managed);

        // The endpoint's role decides which handler serves it, so neither can borrow the other's verbs.
        var workerSpawning = await CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.SpawnOp, AgentKind: "claude-code", TaskPrompt: "spawn me a friend"));
        Assert.False(workerSpawning.Ok);
        Assert.Contains("unknown op", workerSpawning.Error!, StringComparison.Ordinal);

        var coordinatorPlanning = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.PresentPlanOp, PlanJson: PlanJson("src/a.cs"), Title: "mine"));
        Assert.False(coordinatorPlanning.Ok);
        Assert.Contains("unknown op", coordinatorPlanning.Error!, StringComparison.Ordinal);

        // The worker's spawn attempt made nothing, and the coordinator presented no plan.
        Assert.Equal(managedBefore, Rig.Sessions.List().Count(s => s.Role == AgentRoles.Managed));
        Assert.Null(Rig.Plans.LiveForWorker(coordinatorId));
    }

    [Fact]
    public async Task AWorkerCannotAwaitOrReviseAnotherWorkersPlan()
    {
        var (coordinatorId, first) = await SpawnCoordinatorAndWorkerAsync("t1");
        var second = await ShimSpawnAsync(coordinatorId, "t2");

        var call = CallAsync(first, new AgentIpcRequest(
            AgentIpcRequest.PresentPlanOp, PlanJson: PlanJson("src/a.cs"), Title: "first's plan"));
        var plan = await WaitForAsync(() => Rig.Plans.LiveForWorker(first));

        var stolen = await CallAsync(second, new AgentIpcRequest(
            AgentIpcRequest.AwaitDecisionOp, PlanId: plan.PlanId));
        Assert.False(stolen.Ok);
        // The same answer a nonexistent plan gets — the channel is not an existence oracle.
        Assert.Contains($"no plan '{plan.PlanId}'", stolen.Error!, StringComparison.Ordinal);

        var hijacked = await CallAsync(second, new AgentIpcRequest(
            AgentIpcRequest.RevisePlanOp, PlanId: plan.PlanId, PlanJson: PlanJson("src/evil.cs"), Title: "x"));
        Assert.False(hijacked.Ok);
        Assert.Equal(new[] { "src/a.cs" }, Rig.Plans.Get(plan.PlanId)!.Plan.Scope); // untouched

        Rig.Plans.Approve(plan.PlanId, "uid:1000");
        await call.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AManagedWorkerThePlanGateIsNotHolding_GetsNoChannelAtAll()
    {
        // Least privilege: the plan channel exists for coordinator-spawned workers, which the gate holds.
        // An external-PR head (untrusted code from outside this machine) and a manually spawned worker
        // are not governed by the gate, so they get no socket — otherwise an untrusted head would hold a
        // capability to queue approval cards in front of the human for no reason at all.
        var id = await Rig.Spawns.SpawnAsync(
            PlanGateRig.RepoHandle, "external-pr", null, AgentRoles.Managed, CancellationToken.None);
        Track(id);

        Assert.Null(Rig.Ipc.RoleOf(id));
        Assert.False(Directory.Exists(Rig.Ipc.DirFor(id)));

        // And it is not blocked from merging either — the gate answers only for what it holds.
        Assert.True(((IMergeGate)Rig.Gate).Allows(id, out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public async Task AnInvalidPlanIsRefusedBySchemaBeforeItReachesAHuman()
    {
        var (_, workerId) = await SpawnCoordinatorAndWorkerAsync("t");

        var refused = await CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.PresentPlanOp, PlanJson: """{"scope":[],"approach":"x"}""", Title: "T"));

        Assert.False(refused.Ok);
        Assert.NotNull(refused.PlanErrors);
        Assert.Contains("testStrategy is required", refused.PlanErrors!);
        Assert.Null(Rig.Plans.LiveForWorker(workerId)); // nothing reached the approval queue
    }

    // ---- commit_work: the rung the loop was missing ------------------------
    //
    // The first end-to-end run ended with a worker holding a 20-line UNCOMMITTED diff. Its worktree was
    // deleted with the jail, agent/<id> carried no commit, and the readiness trigger — which fires on
    // that ref advancing and then going quiet — had nothing to observe. Everything below drives the same
    // bytes an in-jail shim writes, over the real socket.

    /// <summary>
    /// The gate answers commit exactly as it answers steering and verification. A worker whose plan is
    /// not approved has no authorised work, so it has nothing legitimate to record — and the refusal is
    /// the same sentence a human reads elsewhere, because it is the same gate rather than a second
    /// opinion about what "approved" means.
    /// </summary>
    [Fact]
    public async Task AWorkerStillAtThePlanGate_MayNotCommit()
    {
        var (_, workerId) = await SpawnCoordinatorAndWorkerAsync("fix the clock");
        var before = Rig.Environment.WorkerCommits.Count;

        var refused = await CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.CommitWorkOp, Message: "feat: work nobody approved"));

        Assert.False(refused.Ok);
        Assert.Contains("has not presented a plan yet", refused.Error);
        Assert.Equal(before, Rig.Environment.WorkerCommits.Count); // and nothing was committed
    }

    /// <summary>
    /// The positive: once a human approves, the worker's own shim channel records the work. The daemon
    /// commits on the worker's (repo, agent) and answers with the sha — which is what makes
    /// <c>refs/heads/agent/&lt;id&gt;</c> move and therefore what the readiness trigger can see.
    /// </summary>
    [Fact]
    public async Task AnApprovedWorker_CommitsItsWorkThroughItsOwnChannel()
    {
        var (_, workerId) = await SpawnCoordinatorAndWorkerAsync("rewrite TokenClock");
        await ApproveAsync(workerId);

        var response = await CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.CommitWorkOp, Message: "feat: rewrite the clock"));

        Assert.True(response.Ok, response.Error);
        Assert.True(response.Committed);
        Assert.False(string.IsNullOrEmpty(response.CommitSha));
        Assert.Equal("agent/" + workerId, response.Status);

        var commit = Assert.Single(Rig.Environment.WorkerCommits, c => c.AgentId == workerId);
        Assert.Equal(PlanGateRig.RepoHandle, commit.RepoHash);
        Assert.Equal("feat: rewrite the clock", commit.Message);
    }

    /// <summary>
    /// <b>The worker names only the message.</b> <c>AgentIpcRequest.AgentId</c> exists because the
    /// coordinator's ops need it, so a worker CAN put another agent's id on the wire — and it must have
    /// no effect whatsoever. The commit's (repo, agent) come from the endpoint the request arrived on,
    /// the same way <c>AgentRefMediator</c> refuses to let an agent name a ref at all.
    ///
    /// <para>Asserted behaviourally rather than by field-absence, for the reason
    /// <c>QueueEntryResumeTests</c> writes down: a structural proof that stops being available has to be
    /// replaced by one that survives the field existing.</para>
    /// </summary>
    [Fact]
    public async Task ACommitIgnoresAnyAgentIdTheRequestCarries_AndLandsOnTheCallersOwnBranch()
    {
        var (_, mine) = await SpawnCoordinatorAndWorkerAsync("my task");
        var (_, theirs) = await SpawnCoordinatorAndWorkerAsync("their task");
        await ApproveAsync(mine);
        await ApproveAsync(theirs);

        var response = await CallAsync(mine, new AgentIpcRequest(
            AgentIpcRequest.CommitWorkOp, AgentId: theirs, Message: "feat: onto someone else's branch"));

        Assert.True(response.Ok, response.Error);
        Assert.Equal("agent/" + mine, response.Status);
        Assert.Contains(Rig.Environment.WorkerCommits, c => c.AgentId == mine);
        Assert.DoesNotContain(Rig.Environment.WorkerCommits, c => c.AgentId == theirs);
    }

    /// <summary>
    /// A clean tree answers truthfully. The worker asked correctly and there was nothing to record, so
    /// the call succeeds — and says <c>committed: false</c>, because the branch did not move. Reporting
    /// it as a commit would tell a worker its work is safe while its branch sits exactly where it was:
    /// the original defect wearing a success message, and no longer visible to anyone.
    /// </summary>
    [Fact]
    public async Task ACleanTree_AnswersNothingToCommit_RatherThanClaimingACommit()
    {
        var (_, workerId) = await SpawnCoordinatorAndWorkerAsync("nothing to do");
        await ApproveAsync(workerId);
        Rig.Environment.NextCommitOutcome = AgentWorkCommitOutcome.NothingToCommit;
        try
        {
            var response = await CallAsync(workerId, new AgentIpcRequest(
                AgentIpcRequest.CommitWorkOp, Message: "feat: nothing happened"));

            Assert.True(response.Ok, response.Error);
            Assert.False(response.Committed);
            Assert.Null(response.CommitSha);
        }
        finally
        {
            Rig.Environment.NextCommitOutcome = AgentWorkCommitOutcome.Committed;
        }
    }

    /// <summary>
    /// A worktree that has wandered off <c>agent/&lt;id&gt;</c> is a refusal the worker is told about, not
    /// a silent success. A commit made on some other branch is reachable from nothing the mediator
    /// publishes, the queue reads or the trigger watches — lost exactly as an uncommitted diff is.
    /// </summary>
    [Fact]
    public async Task ARefusedCommit_IsReportedAsAFailure_NotAsDone()
    {
        var (_, workerId) = await SpawnCoordinatorAndWorkerAsync("work on the wrong branch");
        await ApproveAsync(workerId);
        Rig.Environment.NextCommitOutcome = AgentWorkCommitOutcome.RefusedBranch;
        try
        {
            var response = await CallAsync(workerId, new AgentIpcRequest(
                AgentIpcRequest.CommitWorkOp, Message: "feat: somewhere else"));

            Assert.False(response.Ok);
            Assert.False(response.Committed);
            Assert.False(string.IsNullOrEmpty(response.Error));
        }
        finally
        {
            Rig.Environment.NextCommitOutcome = AgentWorkCommitOutcome.Committed;
        }
    }

    // ---- rescope_plan: the worker's legal way to widen an approved scope ---
    //
    // Live testing found a worker that TRIED to widen legitimately and was refused — one live plan per
    // worker, and no op that acted on an approved one. It had two moves left, both bad: exceed its scope
    // silently, or stop. Everything below drives the same bytes an in-jail shim writes.

    /// <summary>
    /// The dead end, at the daemon, and the way out of it. Both refusals are asserted in the same test
    /// because neither is wrong on its own — it is the PAIR that left the worker with nowhere to go, and a
    /// test that only checked one would keep passing if the other lost its hint.
    /// </summary>
    [Fact]
    public async Task AnApprovedWorker_IsToldHowToWiden_ByBothOpsThatRefuseIt()
    {
        var (_, workerId) = await SpawnCoordinatorAndWorkerAsync("rewrite the calculator");
        var approvedId = await ApproveAsync(workerId);

        var presentedAgain = await CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.PresentPlanOp, PlanJson: PlanJson("src/calc.js"), Title: "Wider"));
        Assert.False(presentedAgain.Ok);
        Assert.Contains(WorkerPlanShim.RescopeUsage, presentedAgain.Error!, StringComparison.Ordinal);

        var revised = await CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.RevisePlanOp, PlanId: approvedId,
            PlanJson: PlanJson("src/calc.js"), Title: "Wider"));
        Assert.False(revised.Ok);
        Assert.Contains(WorkerPlanShim.RescopeUsage, revised.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The whole op, end to end, plus the property the design rests on.</b> The re-scope parks on the
    /// human exactly as a presentation does — and while it is parked the worker is still authorised: it
    /// commits, over its own channel, mid-wait. That is asserted here rather than only in the pure tests
    /// because <c>commit_work</c> asks <c>WorkerPlanGate.MayWork</c>, and "the gate still says yes" is a
    /// claim about the daemon's wiring, not about the plan store.
    /// </summary>
    [Fact]
    public async Task ARescopeBlocksOnTheHuman_AndTheWorkerKeepsWorkingAndCommittingWhileItWaits()
    {
        var (_, workerId) = await SpawnCoordinatorAndWorkerAsync("rewrite the calculator");
        var approvedId = await ApproveAsync(workerId);

        var call = CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.RescopePlanOp, PlanId: approvedId,
            PlanJson: PlanJson("src/calc.js"), Title: "Also the calculator"));
        var pending = await WaitForAsync(() =>
            Rig.Plans.LiveForWorker(workerId) is { IsRescope: true } p ? p : null);

        // Parked: nothing but a human completes it.
        Assert.False(await Task.WhenAny(call, Task.Delay(200)) == call, "the re-scope returned undecided");

        // ...and the worker is NOT suspended. It still holds the approval it is asking to widen.
        Assert.Equal(approvedId, Rig.Plans.ApprovedForWorker(workerId)!.PlanId);
        var commit = await CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.CommitWorkOp, Message: "feat: work the approved plan already covers"));
        Assert.True(commit.Ok, commit.Error);
        Assert.True(commit.Committed);

        Rig.Plans.Approve(pending.PlanId, "uid:1000");

        var decided = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(decided.Ok, decided.Error);
        Assert.Equal("Approved", decided.Status);
        Assert.Equal(approvedId, decided.RescopeOf); // the shim needs this to report it as a widening
        Assert.Equal(pending.PlanId, Rig.Plans.ApprovedForWorker(workerId)!.PlanId);
    }

    /// <summary>
    /// A declined widening takes nothing away, and the answer says so — <c>rescopeOf</c> is what stops the
    /// shim printing the generic "STOP: do not attempt another plan" at a worker that is still cleared for
    /// its original scope and would otherwise abandon work it may legitimately finish.
    /// </summary>
    [Fact]
    public async Task ADeclinedRescope_LeavesTheWorkerAuthorised_AndSaysWhichApprovalStands()
    {
        var (_, workerId) = await SpawnCoordinatorAndWorkerAsync("rewrite the calculator");
        var approvedId = await ApproveAsync(workerId);

        var call = CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.RescopePlanOp, PlanId: approvedId,
            PlanJson: PlanJson("src/calc.js"), Title: "Also the calculator"));
        var pending = await WaitForAsync(() =>
            Rig.Plans.LiveForWorker(workerId) is { IsRescope: true } p ? p : null);
        Rig.Plans.Reject(pending.PlanId, "src/calc.js belongs to another worker");

        var decided = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Rejected", decided.Status);
        Assert.Equal(approvedId, decided.RescopeOf);

        Assert.Equal(approvedId, Rig.Plans.ApprovedForWorker(workerId)!.PlanId);
        Assert.True(Rig.Gate.MayWork(workerId, out _));
        var commit = await CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.CommitWorkOp, Message: "feat: carrying on inside the approved scope"));
        Assert.True(commit.Ok, commit.Error);
    }

    /// <summary>
    /// Ownership is checked here exactly as it is for <c>revise</c>, and a stranger's plan id is answered
    /// as a plan that does not exist — otherwise the re-scope op would be the one place on this channel
    /// that told a worker which other workers' plans are approved.
    /// </summary>
    [Fact]
    public async Task AWorkerCannotRescopeAnotherWorkersPlan()
    {
        var (_, mine) = await SpawnCoordinatorAndWorkerAsync("my task");
        var (_, theirs) = await SpawnCoordinatorAndWorkerAsync("their task");
        await ApproveAsync(mine);
        var theirPlan = await ApproveAsync(theirs);

        var refused = await CallAsync(mine, new AgentIpcRequest(
            AgentIpcRequest.RescopePlanOp, PlanId: theirPlan,
            PlanJson: PlanJson("src/calc.js"), Title: "Wider"));

        Assert.False(refused.Ok);
        Assert.Equal($"no plan '{theirPlan}'", refused.Error); // the same answer as a plan that is not there
        Assert.False(Rig.Plans.LiveForWorker(theirs)!.IsRescope); // and nothing was queued on their behalf
    }

    /// <summary>
    /// A re-scope naming no plan is refused with the form to use, and nothing is queued. Deriving the plan
    /// from "whatever this worker has approved" was the obvious alternative and is the same call §13.3
    /// made about a missing <c>--title</c>: a guessed target produces a plausible card for an
    /// authorisation nobody named.
    /// </summary>
    [Fact]
    public async Task ARescopeThatNamesNoPlan_IsRefused_AndQueuesNothing()
    {
        var (_, workerId) = await SpawnCoordinatorAndWorkerAsync("rewrite the calculator");
        var approvedId = await ApproveAsync(workerId);

        var refused = await CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.RescopePlanOp, PlanJson: PlanJson("src/calc.js"), Title: "Wider"));

        Assert.False(refused.Ok);
        Assert.Contains(WorkerPlanShim.RescopeUsage, refused.Error!, StringComparison.Ordinal);
        Assert.Equal(approvedId, Rig.Plans.LiveForWorker(workerId)!.PlanId);
    }

    /// <summary>Presents a plan and has a human approve it, so the worker is past the gate.</summary>
    /// <returns>The approved plan's id — what a re-scope has to name.</returns>
    private async Task<string> ApproveAsync(string workerId)
    {
        var call = CallAsync(workerId, new AgentIpcRequest(
            AgentIpcRequest.PresentPlanOp, PlanJson: PlanJson("src/a.cs"), Title: "T"));
        var pending = await WaitForAsync(() => Rig.Plans.LiveForWorker(workerId));
        Rig.Plans.Approve(pending.PlanId, "uid:1000");
        var approved = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Approved", approved.Status);
        return pending.PlanId;
    }
}

/// <summary>
/// The worker cap, refused <b>at the daemon</b> while counting workers that are doing nothing but
/// waiting on a human. Its own class (and therefore its own rig) because it asserts a daemon-global
/// population, which a rig shared with other tests could not give it honestly.
/// </summary>
public sealed class WorkerCapDaemonEnforcementTests : PlanGateIpcTestBase, IClassFixture<PlanGateRig>
{
    public WorkerCapDaemonEnforcementTests(PlanGateRig rig) : base(rig)
    {
    }

    [Fact]
    public async Task TheWorkerCapIsEnforcedAtTheDaemon_CountingWorkersBlockedOnPlanApproval()
    {
        // The whole point: this refusal happens in the daemon's shim handler, on the wire, with no
        // cooperation from the coordinator whatsoever. The population it counts includes workers that are
        // doing nothing but waiting on a human — that is the decided behaviour, and the refusal says so.
        var max = Rig.Limits.MaxActiveWorkers;
        var coordinatorId = await SpawnCoordinatorAsync();

        for (var i = 0; i < max; i++)
        {
            var id = await ShimSpawnAsync(coordinatorId, $"task {i}");
            _ = CallAsync(id, new AgentIpcRequest(
                AgentIpcRequest.PresentPlanOp, PlanJson: PlanJson($"src/{i}.cs"), Title: $"T{i}"));
        }

        await WaitForAsync(() => Rig.Gate.BlockedWorkerCount == max ? "ok" : null);

        var refused = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.SpawnOp, AgentKind: "claude-code", TaskPrompt: "one more", Title: DefaultBrief));

        Assert.False(refused.Ok);
        Assert.Contains($"{max} workers are waiting on human plan approval", refused.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("Let one finish", refused.Error!, StringComparison.Ordinal);
        Assert.Equal(max, Rig.Sessions.List().Count(s => s.Role == AgentRoles.Managed));
    }
}

/// <summary>
/// Shared plumbing: the real socket round-trip and the real spawn path.
///
/// <para>Every agent a test spawns is stopped when that test ends. That is not tidiness — the rig is
/// shared, and <see cref="CoordinatorLimits.MaxActiveWorkers"/> is enforced against the daemon-global
/// Managed population, so leaked workers make a later test hit the worker cap. (It did, first time:
/// an unrelated test failed with "Worker cap reached — 6/6", which is a pleasant way to be reminded
/// that the cap is real.)</para>
/// </summary>
public abstract class PlanGateIpcTestBase : IAsyncLifetime
{
    private readonly List<string> _spawned = new();

    protected PlanGateIpcTestBase(PlanGateRig rig) => Rig = rig;

    protected PlanGateRig Rig { get; }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Registers an agent this test created so it is stopped (and its cap slot freed) at the end.</summary>
    protected void Track(string agentId) => _spawned.Add(agentId);

    public async Task DisposeAsync()
    {
        foreach (var agentId in _spawned)
        {
            try
            {
                await Rig.Spawns.StopAsync(agentId, CancellationToken.None);
            }
            catch
            {
                // Never fail a test from cleanup.
            }
        }
    }

    protected static string PlanJson(string scope) => JsonSerializer.Serialize(new
    {
        scope = new[] { scope },
        approach = "the approach",
        testStrategy = "tests green",
    });

    protected async Task<string> SpawnCoordinatorAsync()
    {
        var id = await Rig.Spawns.SpawnAsync(
            PlanGateRig.RepoHandle, "claude-code", null, AgentRoles.Coordinator, CancellationToken.None);
        _spawned.Add(id);
        return id;
    }

    /// <summary>
    /// A stand-in brief for the tests that are not about the brief. Deliberately a DIFFERENT string from
    /// every task prompt these tests spawn with, so no test here can pass on the two being equal — which
    /// is how the pre-2026-08-29 defect survived a green suite: the daemon derived the brief from the
    /// task, and every test that built its own request agreed with the derivation by construction.
    /// </summary>
    protected const string DefaultBrief = "Plan the auth-module work";

    protected async Task<(string CoordinatorId, string WorkerId)> SpawnCoordinatorAndWorkerAsync(
        string taskPrompt, string title = DefaultBrief)
    {
        var coordinatorId = await SpawnCoordinatorAsync();
        return (coordinatorId, await ShimSpawnAsync(coordinatorId, taskPrompt, title));
    }

    /// <summary>Spawns a worker exactly as a coordinator CLI does — one JSON line on its own socket.</summary>
    protected async Task<string> ShimSpawnAsync(
        string coordinatorId, string taskPrompt, string title = DefaultBrief)
    {
        var response = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.SpawnOp, AgentKind: "claude-code", TaskPrompt: taskPrompt, Title: title));
        Assert.True(response.Ok, response.Error);
        Assert.Equal("AwaitingPlan", response.Status);
        _spawned.Add(response.AgentId!);
        return response.AgentId!;
    }

    /// <summary>One request line in, one response line out — the real socket, no timeout on the reply.</summary>
    protected async Task<AgentIpcResponse> CallAsync(string agentId, AgentIpcRequest request)
    {
        var socketPath = Path.Combine(Rig.Ipc.DirFor(agentId), AgentIpcPaths.SocketFileName);
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        await using var stream = new NetworkStream(client);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(AgentIpcProtocol.SerializeRequest(request) + "\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var line = await reader.ReadLineAsync();
        Assert.NotNull(line);
        return JsonSerializer.Deserialize<AgentIpcResponse>(line!)!;
    }

    protected static async Task<T> WaitForAsync<T>(Func<T?> probe, int attempts = 600) where T : class
    {
        for (var i = 0; i < attempts; i++)
        {
            if (probe() is { } value)
            {
                return value;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("the awaited daemon condition never became true");
    }
}
