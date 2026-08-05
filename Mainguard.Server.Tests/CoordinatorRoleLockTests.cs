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
/// An in-proc daemon with <b>two</b> provisioned repos, so ownership scoping can be tested against the
/// collision it actually has to survive: the same bare agent id living in two repositories. One repo
/// cannot demonstrate that, and a scoping check that is only ever asked about one repo passes whether or
/// not the repo is part of the key — which is how this bug survived three fixes (#281, #284, #286).
/// </summary>
public sealed class RoleLockRig : IDisposable
{
    public const string RepoA = "fake-repo-hash-lock-a";
    public const string RepoB = "fake-repo-hash-lock-b";

    private readonly DaemonFixture _daemon = new();
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;
    private readonly string _root;

    /// <summary>The recorded spawn requests — what each jail was actually asked to mount.</summary>
    internal AgentSessionRepoScopingTests.RecordingEngine Engine { get; }

    public RoleLockRig()
    {
        _root = Path.Combine(Path.GetTempPath(), "mg-rolelock-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "repos", RepoA)); // "provisioned"
        Directory.CreateDirectory(Path.Combine(_root, "repos", RepoB));
        Engine = new AgentSessionRepoScopingTests.RecordingEngine();
        var environment = new AgentSessionRepoScopingTests.FakeAgentEnvironment(_root, Engine);
        _host = _daemon.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<IAgentEnvironment>(environment)));
        _ = _host.Services;
    }

    public AgentSpawnService Spawns => _host.Services.GetRequiredService<AgentSpawnService>();

    public AgentIpcServer Ipc => _host.Services.GetRequiredService<AgentIpcServer>();

    public PlanApprovalService Plans => _host.Services.GetRequiredService<PlanApprovalService>();

    public WorkerPlanGate Gate => _host.Services.GetRequiredService<WorkerPlanGate>();

    public AgentSessionStore Sessions => _host.Services.GetRequiredService<AgentSessionStore>();

    public MergeQueueRegistry Queues => _host.Services.GetRequiredService<MergeQueueRegistry>();

    public void Dispose()
    {
        _host.Dispose();
        _daemon.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// <b>Phase 3 — the role lock.</b> The coordinator contract's §3 surface and §4 denials, proved at the
/// daemon over the real Unix-domain socket an in-jail shim writes to.
///
/// <para><b>Why every assertion here goes through the socket.</b> Contract §5 is the standing instruction:
/// "a system prompt is not a security boundary". MG-12 found role authorization was dead code that failed
/// open, and phase 2 hit the same shape again — a check that passed because an <c>if</c> in the IPC handler
/// quietly did the work while the gate's real check was unreachable from any test. So nothing below asserts
/// on a prompt, a doc comment, or an in-process helper class: each test writes the bytes a coordinator's
/// jail writes and reads the daemon's answer.</para>
///
/// <para><b>Each denial was watched failing.</b> Every test in this file was run against a build with its
/// enforcement removed, and observed to go red before the enforcement was restored — the evidence is in the
/// phase-3 decisions document. A test nobody watched fail is not evidence that anything is enforced.</para>
/// </summary>
public sealed class CoordinatorRoleLockTests : IClassFixture<RoleLockRig>, IAsyncLifetime
{
    private readonly RoleLockRig _rig;
    private readonly List<string> _spawned = new();

    public CoordinatorRoleLockTests(RoleLockRig rig) => _rig = rig;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var id in _spawned)
        {
            try { await _rig.Spawns.StopAsync(id, CancellationToken.None); }
            catch { /* never fail a test from cleanup */ }
        }
    }

    // ---- §3 · the surface is exactly four tools, and the list is exhaustive --------------------

    /// <summary>
    /// Contract §3: the four tools are the whole surface. This pins the set itself, so adding a fifth op
    /// to the protocol is a test failure that has to be argued with — which is what "adding to this list
    /// is a deliberate contract change, reviewed as such" means in practice.
    /// </summary>
    [Fact]
    public void TheCoordinatorSurface_IsExactlyTheContractsFourTools()
    {
        Assert.Equal(
            new[] { "list", "prompt", "spawn", "status", "verify" },
            AgentIpcRequest.CoordinatorOps.OrderBy(o => o, StringComparer.Ordinal).ToArray());

        // `list` and `status` are one tool (get_worker_status) under two spellings, so the surface is the
        // contract's four: spawn_worker, get_worker_status, send_worker_prompt, request_verification.
        Assert.Equal(4, AgentIpcRequest.CoordinatorOps.Count - 1);

        // Neither role can reach the other's operations.
        Assert.Empty(AgentIpcRequest.CoordinatorOps.Intersect(AgentIpcRequest.WorkerOps, StringComparer.Ordinal));
    }

    /// <summary>
    /// §3 "Anything not on this list is denied", and §4's items by name. Every one of these is refused on a
    /// live coordinator endpoint. The §4 RPC spellings are included deliberately: they are the capabilities
    /// the contract forbids, and this asserts there is no back door to them on the one channel a
    /// coordinator can actually reach.
    /// </summary>
    [Theory]
    // §4 — merge power.
    [InlineData("BeginMerge")]
    [InlineData("ConfirmMerge")]
    [InlineData("AbandonMerge")]
    [InlineData("AcknowledgeFlaggedChange")]
    [InlineData("merge")]
    // §4 — plan approval (holding the gate it is denied at merge).
    [InlineData("ApprovePlan")]
    [InlineData("RejectPlan")]
    [InlineData("approve_plan")]
    [InlineData("reject_plan")]
    // §4 — reading other sessions.
    [InlineData("GetScrollback")]
    [InlineData("scrollback")]
    // §4 — the worker's own plan channel is not reachable from a coordinator endpoint.
    [InlineData("brief")]
    [InlineData("present_plan")]
    [InlineData("revise_plan")]
    [InlineData("await_decision")]
    // Anything else at all.
    [InlineData("stop")]
    [InlineData("kill")]
    [InlineData("exec")]
    public async Task EveryOperationOutsideTheContractSurface_IsRefused(string op)
    {
        var coordinator = await SpawnCoordinatorAsync(RoleLockRig.RepoA);

        var response = await CallAsync(coordinator, new AgentIpcRequest(op));

        Assert.False(response.Ok, $"op '{op}' was SERVED to a coordinator — contract §3 says the surface is exhaustive.");
        Assert.Contains("unknown op", response.Error ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty op is refused one layer earlier, by the codec (<c>TryParseRequest</c> requires a non-empty
    /// <c>op</c>), so it never reaches the dispatch table. Asserted separately rather than folded into the
    /// theory above: it is a real refusal, but claiming it comes from the §3 allow-list would be asserting
    /// the wrong cause — and an error that names a cause it never checked is a failure mode this repo has
    /// already paid for more than once.
    /// </summary>
    [Fact]
    public async Task AnEmptyOp_IsRefusedByTheCodec_BeforeTheSurfaceCheck()
    {
        var coordinator = await SpawnCoordinatorAsync(RoleLockRig.RepoA);

        var response = await CallAsync(coordinator, new AgentIpcRequest(string.Empty));

        Assert.False(response.Ok);
        Assert.Contains("malformed request", response.Error ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- §7 · ownership scoping — keyed by (RepoHash, AgentId) --------------------------------

    /// <summary>
    /// <b>The test this whole section exists for.</b> Two repos, two coordinators, and a worker named
    /// <c>pr-7</c> in each — the exact id collision the external-PR intake produces and that has been
    /// fixed three times (#281 AgentSessionStore, #284 SwarmReconciler, #286 TerminalSessionManager).
    ///
    /// <para>Repo A's coordinator must not reach repo B's <c>pr-7</c>. An ownership check keyed on the bare
    /// parent id alone passes every other test in this file and fails only this one, because only here do
    /// two sessions share an id.</para>
    /// </summary>
    [Fact]
    public async Task Ownership_IsKeyedByRepoAndAgentId_NotTheBareAgentId()
    {
        const string CollidingId = "pr-7";

        var coordinatorA = await SpawnCoordinatorAsync(RoleLockRig.RepoA);
        var coordinatorB = await SpawnCoordinatorAsync(RoleLockRig.RepoB);

        // The same bare id, once per repo, each parented to that repo's coordinator.
        await SpawnManagedAsync(RoleLockRig.RepoA, CollidingId, coordinatorA);
        await SpawnManagedAsync(RoleLockRig.RepoB, CollidingId, coordinatorB);

        // Each coordinator sees exactly ONE pr-7 — its own.
        var listA = await CallAsync(coordinatorA, new AgentIpcRequest(AgentIpcRequest.StatusOp));
        Assert.True(listA.Ok, listA.Error);
        Assert.Single(listA.Agents!, r => r.StartsWith(CollidingId + "\t", StringComparison.Ordinal));

        // And naming it resolves within the caller's own repo. If ownership were keyed on the bare parent
        // id, repo A's coordinator would resolve repo B's session here (or an arbitrary one of the two).
        var statusA = await CallAsync(
            coordinatorA, new AgentIpcRequest(AgentIpcRequest.StatusOp, AgentId: CollidingId));
        Assert.True(statusA.Ok, statusA.Error);

        var resolved = _rig.Sessions.Find(RoleLockRig.RepoA, CollidingId);
        Assert.NotNull(resolved);
        Assert.Equal(coordinatorA, resolved!.ParentAgentId);
        Assert.Equal(RoleLockRig.RepoA, resolved.RepoHash);
    }

    /// <summary>§7: a coordinator's status listing contains only workers it spawned.</summary>
    [Fact]
    public async Task Status_ListsOnlyTheCallersOwnWorkers()
    {
        var mine = await SpawnCoordinatorAsync(RoleLockRig.RepoA);
        var theirs = await SpawnCoordinatorAsync(RoleLockRig.RepoA);

        var myWorker = await ShimSpawnAsync(mine, "my task");
        var theirWorker = await ShimSpawnAsync(theirs, "their task");

        var response = await CallAsync(mine, new AgentIpcRequest(AgentIpcRequest.StatusOp));

        Assert.True(response.Ok, response.Error);
        Assert.Contains(response.Agents!, r => r.StartsWith(myWorker + "\t", StringComparison.Ordinal));
        Assert.DoesNotContain(response.Agents!, r => r.StartsWith(theirWorker + "\t", StringComparison.Ordinal));
        // The other coordinator itself is not a child of this one either.
        Assert.DoesNotContain(response.Agents!, r => r.StartsWith(theirs + "\t", StringComparison.Ordinal));
    }

    /// <summary>
    /// §7: "A coordinator must not read, steer, stop, or propose verification for another coordinator's
    /// workers." One test per verb that names an agent — each must refuse.
    /// </summary>
    [Fact]
    public async Task AnotherCoordinatorsWorker_CannotBeRead_Steered_OrProposedForVerification()
    {
        var mine = await SpawnCoordinatorAsync(RoleLockRig.RepoA);
        var theirs = await SpawnCoordinatorAsync(RoleLockRig.RepoA);
        var theirWorker = await ShimSpawnAsync(theirs, "not yours");

        // get_worker_status
        var status = await CallAsync(mine, new AgentIpcRequest(AgentIpcRequest.StatusOp, AgentId: theirWorker));
        Assert.False(status.Ok, "a coordinator READ another coordinator's worker (contract §7).");

        // send_worker_prompt
        var prompt = await CallAsync(
            mine, new AgentIpcRequest(AgentIpcRequest.PromptOp, AgentId: theirWorker, Prompt: "do something else"));
        Assert.False(prompt.Ok, "a coordinator STEERED another coordinator's worker (contract §7).");

        // request_verification
        var verify = await CallAsync(mine, new AgentIpcRequest(AgentIpcRequest.VerifyOp, AgentId: theirWorker));
        Assert.False(verify.Ok, "a coordinator PROPOSED VERIFICATION for another coordinator's worker (contract §7).");
    }

    /// <summary>
    /// The refusal for "not yours" is byte-identical to the refusal for "no such worker". Otherwise the
    /// channel is an existence oracle: a coordinator could enumerate every other coordinator's fan-out by
    /// probing ids and reading which error came back.
    /// </summary>
    [Fact]
    public async Task AStrangersWorker_IsRefusedIdenticallyToAWorkerThatDoesNotExist()
    {
        var mine = await SpawnCoordinatorAsync(RoleLockRig.RepoA);
        var theirs = await SpawnCoordinatorAsync(RoleLockRig.RepoA);
        var theirWorker = await ShimSpawnAsync(theirs, "not yours");

        var stranger = await CallAsync(mine, new AgentIpcRequest(AgentIpcRequest.StatusOp, AgentId: theirWorker));
        var absent = await CallAsync(
            mine, new AgentIpcRequest(AgentIpcRequest.StatusOp, AgentId: "definitely-not-a-real-agent-id"));

        Assert.False(stranger.Ok);
        Assert.False(absent.Ok);
        Assert.Equal(
            absent.Error!.Replace("definitely-not-a-real-agent-id", "<id>", StringComparison.Ordinal),
            stranger.Error!.Replace(theirWorker, "<id>", StringComparison.Ordinal));
    }

    /// <summary>§7: a coordinator may not name ITSELF as a worker (it is nobody's child).</summary>
    [Fact]
    public async Task ACoordinator_CannotNameItselfAsAWorker()
    {
        var coordinator = await SpawnCoordinatorAsync(RoleLockRig.RepoA);

        var status = await CallAsync(
            coordinator, new AgentIpcRequest(AgentIpcRequest.StatusOp, AgentId: coordinator));
        Assert.False(status.Ok);

        var verify = await CallAsync(
            coordinator, new AgentIpcRequest(AgentIpcRequest.VerifyOp, AgentId: coordinator));
        Assert.False(verify.Ok, "a coordinator proposed ITSELF for verification — contract §4.");
    }

    // ---- §4 · declaring its own work merge-ready ----------------------------------------------

    /// <summary>
    /// §4: "Declaring its own work merge-ready." <c>request_verification</c> is refused for a worker with
    /// no approved plan — the coordinator <i>proposes</i>, the daemon decides, and there is nothing to
    /// propose until a human has authorised the work.
    ///
    /// <para><b>What this test deliberately does NOT assert.</b> The companion guard — a coordinator never
    /// acquiring a merge-queue row, since it has no <c>agent/&lt;id&gt;</c> branch — is unobservable here:
    /// the in-proc fake substrate never provisions a merge queue, so <c>MergeQueueRegistry.Resolve</c>
    /// returns null and any assertion over queue membership passes without ever running. That version of
    /// this test was written, caught by mutation testing, and removed rather than left looking like
    /// coverage. The guard itself lives in <c>AgentSpawnService</c> and is recorded as untested in
    /// <c>coordinator-phase-3-decisions.md</c> §6.</para>
    /// </summary>
    [Fact]
    public async Task ACoordinator_CannotProposeVerificationForUnauthorisedWork()
    {
        var coordinator = await SpawnCoordinatorAsync(RoleLockRig.RepoA);
        var worker = await ShimSpawnAsync(coordinator, "some task");
        var verify = await CallAsync(coordinator, new AgentIpcRequest(AgentIpcRequest.VerifyOp, AgentId: worker));
        Assert.False(verify.Ok, "verification was accepted for a worker with no approved plan (contract §2).");
        // Assert the CAUSE, not just the refusal. Mutation testing caught this one asserting nothing
        // useful: with the plan-gate check deleted the call still failed, for an unrelated reason (the
        // fake substrate has no verification command), so `Assert.False` alone was green against a build
        // with the gate removed. An assertion that cannot tell the gate from the substrate is not
        // evidence the gate exists.
        Assert.Contains("no work is authorised", verify.Error ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- §8 step 1 · the jail the role actually gets -------------------------------------------

    /// <summary>
    /// The role lock, proved through the <b>real spawn service</b> rather than the pure spec builder: the
    /// request the daemon hands the sandbox engine for a coordinator carries no worktree, no mirror, no
    /// per-agent git dir and no package cache — and a worker's, from the same service, still carries them.
    ///
    /// <para><c>CoordinatorJailSpecTests</c> proves the builder honours the flag; this proves the daemon
    /// actually <i>sets</i> it for the coordinator role. Both are needed: a correct builder nobody passes
    /// the flag to is the MG-12 shape, a control that looks present and is never reached.</para>
    /// </summary>
    [Fact]
    public async Task TheDaemonSpawnsACoordinatorWithNoRepositoryAccess_AndAWorkerWithIt()
    {
        var coordinator = await SpawnCoordinatorAsync(RoleLockRig.RepoA);
        var worker = await ShimSpawnAsync(coordinator, "a task");

        var coordinatorRequest = Assert.Single(_rig.Engine.Requests, r => r.AgentId == coordinator);
        Assert.True(coordinatorRequest.WithoutRepositoryAccess, "the coordinator jail was NOT role-locked.");
        Assert.True(string.IsNullOrEmpty(coordinatorRequest.WorktreePath), "the coordinator was given a worktree.");
        Assert.Null(coordinatorRequest.BareRepoPath);
        Assert.Null(coordinatorRequest.AgentRepoPath);
        Assert.Null(coordinatorRequest.PackageCachePath);

        // The paired negative control: a worker is untouched by this change.
        var workerRequest = Assert.Single(_rig.Engine.Requests, r => r.AgentId == worker);
        Assert.False(workerRequest.WithoutRepositoryAccess);
        Assert.False(string.IsNullOrEmpty(workerRequest.WorktreePath), "the worker lost its worktree.");
    }

    // ---- the plan gate still binds the new tools ----------------------------------------------

    /// <summary>
    /// Phase-2 decision 2.2 point 3, now applied to the wired tool rather than the un-wired in-process
    /// class: <c>send_worker_prompt</c> is refused for a worker still at the plan gate, so the coordinator
    /// cannot hand over the task the daemon is deliberately withholding.
    /// </summary>
    [Fact]
    public async Task SendWorkerPrompt_IsRefusedForAWorkerStillAtThePlanGate()
    {
        var coordinator = await SpawnCoordinatorAsync(RoleLockRig.RepoA);
        var worker = await ShimSpawnAsync(coordinator, "the withheld task");

        var response = await CallAsync(
            coordinator, new AgentIpcRequest(AgentIpcRequest.PromptOp, AgentId: worker, Prompt: "here is the task"));

        Assert.False(response.Ok, "the coordinator steered a worker the daemon is withholding a task from.");
        // The worker has not presented a plan yet, so the gate's reason is the "nothing authorised" one
        // rather than the "pending your approval" one. Asserting the actual reason keeps the test honest
        // about which state it is in.
        Assert.Contains("no work is authorised", response.Error ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// The paired positive: the refusals above come from the ownership/plan checks, not from the ops being
    /// broken. A worker the caller owns is visible, and its status carries the plan-gate reason. Without
    /// this, every "Assert.False" above would pass on a handler that refused everything.
    /// </summary>
    [Fact]
    public async Task AnOwnedWorker_IsVisibleToItsOwnCoordinator_WithTheGateReason()
    {
        var coordinator = await SpawnCoordinatorAsync(RoleLockRig.RepoA);
        var worker = await ShimSpawnAsync(coordinator, "a task");

        var response = await CallAsync(
            coordinator, new AgentIpcRequest(AgentIpcRequest.StatusOp, AgentId: worker));

        Assert.True(response.Ok, response.Error);
        var row = Assert.Single(response.Agents!);
        Assert.StartsWith(worker + "\t", row, StringComparison.Ordinal);
        // The row explains WHY it is idle, in the same call — so "why is nothing happening" never needs a
        // capability the coordinator does not have.
        Assert.Contains("no work is authorised", row, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<string> SpawnCoordinatorAsync(string repo)
    {
        var id = await _rig.Spawns.SpawnAsync(repo, "claude-code", null, AgentRoles.Coordinator, CancellationToken.None);
        _spawned.Add(id);
        return id;
    }

    private async Task SpawnManagedAsync(string repo, string agentId, string parent)
    {
        var id = await _rig.Spawns.SpawnAsync(
            repo, "claude-code", null, AgentRoles.Managed, CancellationToken.None,
            parentAgentId: parent, agentId: agentId,
            heldTaskTitle: "held", heldTaskPrompt: "held task");
        _spawned.Add(id);
    }

    private async Task<string> ShimSpawnAsync(string coordinatorId, string taskPrompt)
    {
        var response = await CallAsync(coordinatorId, new AgentIpcRequest(
            AgentIpcRequest.SpawnOp, AgentKind: "claude-code", TaskPrompt: taskPrompt));
        Assert.True(response.Ok, response.Error);
        _spawned.Add(response.AgentId!);
        return response.AgentId!;
    }

    /// <summary>One request line in, one response line out — the real socket, the real handler.</summary>
    private async Task<AgentIpcResponse> CallAsync(string agentId, AgentIpcRequest request)
    {
        var socketPath = Path.Combine(_rig.Ipc.DirFor(agentId), AgentIpcPaths.SocketFileName);
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        await using var stream = new NetworkStream(client);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(AgentIpcProtocol.SerializeRequest(request) + "\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var line = await reader.ReadLineAsync();
        Assert.NotNull(line);
        return JsonSerializer.Deserialize<AgentIpcResponse>(line!)!;
    }
}
