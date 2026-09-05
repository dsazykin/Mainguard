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
        var environment = new AgentSessionRepoScopingTests.FakeAgentEnvironment(
            _root, new AgentSessionRepoScopingTests.RecordingEngine());
        _host = _daemon.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<IAgentEnvironment>(environment)));
        _ = _host.Services; // build once, here, not inside the first test
    }

    public AgentSpawnService Spawns => _host.Services.GetRequiredService<AgentSpawnService>();

    public AgentIpcServer Ipc => _host.Services.GetRequiredService<AgentIpcServer>();

    public PlanApprovalService Plans => _host.Services.GetRequiredService<PlanApprovalService>();

    public WorkerPlanGate Gate => _host.Services.GetRequiredService<WorkerPlanGate>();

    public CoordinatorLimits Limits => _host.Services.GetRequiredService<CoordinatorLimits>();

    public AgentSessionStore Sessions => _host.Services.GetRequiredService<AgentSessionStore>();

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

    [Fact]
    public async Task ACoordinatorSpawnedWorker_GetsAPlanShimAndABrief_ButNotItsTask()
    {
        var (_, workerId) = await SpawnCoordinatorAndWorkerAsync("fix the token clock");

        // The worker's jail carries mainguard-plan and NOT the coordinator's spawn shim.
        var dir = Rig.Ipc.DirFor(workerId);
        Assert.True(File.Exists(Path.Combine(dir, AgentIpcPaths.PlanShimFileName)));
        Assert.False(File.Exists(Path.Combine(dir, AgentIpcPaths.SpawnShimFileName)));
        Assert.Equal(AgentIpcEndpointRole.Worker, Rig.Ipc.RoleOf(workerId));

        // `brief` tells it what to plan about — and carries no task prompt.
        var brief = await CallAsync(workerId, new AgentIpcRequest(AgentIpcRequest.BriefOp));
        Assert.True(brief.Ok);
        Assert.Equal("fix the token clock", brief.Brief);
        Assert.True(string.IsNullOrEmpty(brief.TaskPrompt));
        Assert.Equal(Rig.Limits.MaxPlanRevisions, brief.MaxRevisions);
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
            AgentIpcRequest.SpawnOp, AgentKind: "claude-code", TaskPrompt: "one more"));

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

    protected async Task<(string CoordinatorId, string WorkerId)> SpawnCoordinatorAndWorkerAsync(string taskPrompt)
    {
        var coordinatorId = await SpawnCoordinatorAsync();
        return (coordinatorId, await ShimSpawnAsync(coordinatorId, taskPrompt));
    }

    /// <summary>Spawns a worker exactly as a coordinator CLI does — one JSON line on its own socket.</summary>
    protected async Task<string> ShimSpawnAsync(string coordinatorId, string taskPrompt)
    {
        var response = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.SpawnOp, AgentKind: "claude-code", TaskPrompt: taskPrompt));
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
