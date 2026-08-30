using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Git.Audit;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Server.Tests;

/// <summary>
/// <b>Resuming a stranded merge-queue entry</b> — the authorization and refusal half, asserted at the
/// daemon.
///
/// <para>Resume is <i>adoption</i>: a jail spawned onto an agent id that already exists, with its worktree
/// standing on that entry's own <c>agent/&lt;id&gt;</c> branch. That is a more dangerous capability than
/// the merge power contract §4 already denies the coordinator — an agent able to adopt an arbitrary id
/// could take over another agent's entry, write its branch from inside the adopted jail, and have the
/// daemon verify the result under the original entry's identity. So every guard is here, on the daemon,
/// and every one of these tests fails when its guard is removed.</para>
///
/// <para>The end-to-end proof that an adopted jail really carries the branch's commits and can verify in
/// itself needs a container runtime and lives in
/// <c>Mainguard.Server.Tests/Agents/QueueEntryResumeDockerTests.cs</c>. What is asserted here is
/// everything that must be decided BEFORE a jail is built — which is deliberately all of the
/// authorization.</para>
/// </summary>
public sealed class QueueEntryResumeTests
{
    private const string MainSha = "main-sha-0000";
    private const string CoordinatorToken = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    // One handle per test instance: in-proc hosts share a daemon DB, so a constant handle would let one
    // test's rows and leases reach another's.
    private readonly string _repoHandle = "repo-resume-" + Guid.NewGuid().ToString("N");

    // ---- 1. the security boundary ----------------------------------------------------------------

    /// <summary>
    /// Contract §4, extended. The coordinator is denied <c>ResumeAgent</c> at the interceptor, so a
    /// hand-written client holding a coordinator credential cannot walk past it either.
    ///
    /// <para>This is the negative test that matters most: adoption names <i>somebody else's</i> agent id.
    /// A coordinator that could call it would not need merge power — it could attach a writable jail to a
    /// branch it does not own and then ask the daemon to verify whatever it put there.</para>
    /// </summary>
    [Fact]
    public async Task Coordinator_IsDeniedResume_ByTheRoleLayer_AndNothingIsAdopted()
    {
        using var host = new DaemonFixture();
        host.Services.GetRequiredService<ConnectionRoleRegistry>().RegisterCoordinatorToken(CoordinatorToken);
        var queue = SeedQueue(host);
        queue.EnsureEntry("victim", MergeEntryOrigin.Local);

        var client = new AgentService.AgentServiceClient(host.CreateChannel());

        var denied = await Assert.ThrowsAsync<RpcException>(() => client.ResumeAgentAsync(
            new ResumeAgentRequest { RepoHandle = _repoHandle, AgentId = "victim", AgentKind = "claude-code" },
            host.AuthHeaders(CoordinatorToken)).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
        // The ROLE gate, not the bearer layer. Asserting the status code alone would pass for a token that
        // never authenticated at all — which is how MG-12 found role checks that were dead code.
        Assert.Contains("coordinator role", denied.Status.Detail);
        Assert.DoesNotContain("Invalid bearer token", denied.Status.Detail);

        // A denial that still did something would be worse than none: no session was created for the id.
        Assert.Null(host.Services.GetRequiredService<AgentSessionStore>()
            .Find(new AgentSessionKey(_repoHandle, "victim")));
        Assert.Equal(WorkerMergeState.Working, queue.GetState("victim"));
    }

    /// <summary>
    /// The layer BELOW the interceptor. An agent holds no gRPC channel — it reaches the daemon through the
    /// in-jail Unix socket, whose op set is asserted here WHOLE, by reflection, so it cannot grow a resume
    /// by accident. Asserted separately from the interceptor because the interceptor cannot see this
    /// channel at all.
    ///
    /// <para><b>Phases 2 and 3 each widen this set, and the widening is the point of re-reading it.</b> It
    /// was two ops about spawning. Phase 2's worker plan shim adds four — <c>brief</c>,
    /// <c>present_plan</c>, <c>revise_plan</c>, <c>await_decision</c> — which carry a plan through the
    /// human decision and back. Phase 3's role lock adds the rest of the coordinator's four permitted
    /// tools: <c>status</c>, <c>prompt</c>, <c>verify</c> (alongside the existing <c>spawn</c>). The plan-mode
    /// toggle adds <c>task</c> — a second DOOR onto the gate's one exit for the withheld task, gated by
    /// the same <c>MayWork</c> predicate as <c>commit_work</c>, and the op a worker uses when the operator
    /// has turned plan approvals off and there is therefore no <c>present</c> to return one.</para>
    ///
    /// <para>None of the seven names an agent id to adopt or a branch to attach to, and each endpoint is
    /// fixed to ONE role — a worker cannot reach <c>spawn</c>, a coordinator cannot reach the plan ops.
    /// The list grew because the contract deliberately grew; it did not grow an adoption. Pinning the
    /// exact list rather than relaxing to "contains no resume" is what makes that reviewable: a substring
    /// check would pass for an adoption op named anything else, and this assertion has now caught the
    /// surface changing under two separate forward merges, which is precisely what it is for.</para>
    /// </summary>
    [Fact]
    public void AgentIpcSurface_HasNoResumeOp()
    {
        var ops = typeof(Mainguard.Agents.Agents.Ipc.AgentIpcRequest)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "await_decision", "brief", "commit_work", "list", "present_plan", "prompt",
                "revise_plan", "spawn", "status", "task", "verify",
            },
            ops);

        // No property here may name a resume. `AgentId` is deliberately NOT in this check any more, and
        // the change is worth stating rather than burying.
        //
        // This clause used to read "…|| p.Name == "AgentId"", on the reasoning that a request carrying no
        // agent id structurally cannot ask to adopt one. Phase 3 ends that: `get_worker_status`,
        // `send_worker_prompt` and `request_verification` each name the worker they act on, so the field
        // exists and must. The structural proof is therefore GONE, and deleting the clause without
        // replacing it would have quietly retired the guarantee rather than the mechanism.
        //
        // What replaces it is behavioural and lives where it can actually be exercised:
        // CoordinatorRoleLockTests.ShimSpawn_IgnoresAnyAgentIdTheRequestCarries drives a real `spawn`
        // over the real socket with a hostile AgentId and proves the daemon mints its own instead. That
        // is a stronger assertion than field-absence ever was — it survives the field existing.
        Assert.DoesNotContain(
            typeof(Mainguard.Agents.Agents.Ipc.AgentIpcRequest).GetProperties(),
            p => p.Name.Contains("Resume", StringComparison.OrdinalIgnoreCase));
    }

    // ---- 2. scoping: the (repo, agent) pair, never the id alone ----------------------------------

    /// <summary>
    /// An id that names no entry in <b>this</b> repository's queue is refused, even when another
    /// repository has an entry of the same name.
    ///
    /// <para>Agent ids are unique per repo and not globally — the external-PR intake names entries
    /// <c>pr-&lt;n&gt;</c>, so two subscribed repositories both hold a <c>pr-7</c> — and that exact
    /// collision class has been fixed four separate times here (#281, #284, #286, #292). An adoption keyed
    /// on the id alone would let one repository's resume attach a jail to the other's branch.</para>
    /// </summary>
    [Fact]
    public async Task Resume_IsScopedToTheRepo_SoAnotherReposEntryOfTheSameNameIsNotAdopted()
    {
        using var host = new DaemonFixture();
        var mine = SeedQueue(host);
        var otherHandle = "repo-resume-other-" + Guid.NewGuid().ToString("N");
        var theirs = SeedQueue(host, otherHandle);
        theirs.EnsureEntry("pr-7", MergeEntryOrigin.External);

        // My repo's queue is live and does NOT hold pr-7; the other repo's does.
        Assert.DoesNotContain("pr-7", mine.Agents);
        Assert.Contains("pr-7", theirs.Agents);

        var response = await ResumeAsync(host, "pr-7");

        Assert.False(response.Resumed);
        Assert.Contains("not in this repository's merge queue", response.Reason);
        // The other repo's entry was neither adopted nor disturbed.
        Assert.Equal(WorkerMergeState.Working, theirs.GetState("pr-7"));
        Assert.Null(host.Services.GetRequiredService<AgentSessionStore>()
            .Find(new AgentSessionKey(otherHandle, "pr-7")));
    }

    // ---- 3. the refusals, each with its own sentence ---------------------------------------------

    /// <summary>
    /// A discarded entry is finished, not stranded. Resuming it would undo a decision a human made
    /// deliberately and recorded — a different act, which this refuses by name rather than by silently
    /// treating "absent from the live queue" as "unknown".
    /// </summary>
    [Fact]
    public async Task Resume_RefusesADiscardedEntry_ByName()
    {
        using var host = new DaemonFixture();
        var queue = SeedQueue(host);
        queue.EnsureEntry("dropped", MergeEntryOrigin.Local);
        Assert.True(queue.TryDiscard("dropped", "uid:1000", "done with it", out _));

        var response = await ResumeAsync(host, "dropped");

        Assert.False(response.Resumed);
        Assert.Contains("discarded", response.Reason);
        Assert.Equal(WorkerMergeState.Discarded.ToString(), response.State);
        Assert.Equal(WorkerMergeState.Discarded, queue.GetState("dropped"));
    }

    /// <summary>A terminal entry has nothing left to resume, and says which terminal it is in.</summary>
    [Fact]
    public async Task Resume_RefusesATerminalEntry()
    {
        using var host = new DaemonFixture();
        var queue = SeedQueue(host);
        queue.EnsureEntry("done", MergeEntryOrigin.Local);
        await queue.RunVerificationAsync("done", CancellationToken.None);
        queue.RequestReview("done");
        queue.Reject("done");
        Assert.Equal(WorkerMergeState.Rejected, queue.GetState("done"));

        var response = await ResumeAsync(host, "done");

        Assert.False(response.Resumed);
        Assert.Contains("already Rejected", response.Reason);
    }

    /// <summary>
    /// An entry with a live session is not stranded, so there is nothing to resume — and spawning a second
    /// jail onto the same id would collide with the running one over the same worktree and branch.
    /// </summary>
    [Fact]
    public async Task Resume_RefusesAnEntryThatAlreadyHasALiveAgent()
    {
        using var host = new DaemonFixture();
        var queue = SeedQueue(host);
        queue.EnsureEntry("running", MergeEntryOrigin.Local);
        // A session for THIS repo and this id — the shape a live agent leaves in the daemon's store.
        host.Services.GetRequiredService<AgentSessionStore>()
            .Spawn("claude-code", role: "", parentAgentId: null, agentId: "running", repoHash: _repoHandle);

        var response = await ResumeAsync(host, "running");

        Assert.False(response.Resumed);
        Assert.Contains("already has a live agent", response.Reason);
    }

    /// <summary>
    /// Never inside the <c>BeginMerge</c> → <c>ConfirmMerge</c> window. A merge is already executing
    /// against this branch on the user's checkout, and attaching a writable jail to it mid-merge is how
    /// the queue ends up disagreeing with git — the one outcome the whole subsystem exists to prevent.
    /// </summary>
    [Fact]
    public async Task Resume_RefusesWhileThisEntryHoldsTheReposMergeLease()
    {
        using var host = new DaemonFixture();
        var queue = SeedQueue(host);
        queue.EnsureEntry("merging", MergeEntryOrigin.Local);
        var lease = host.Services.GetRequiredService<IMergeLeaseStore>()
            .TryBegin(_repoHandle, "lease-1", "merging", MainSha, "main");
        Assert.NotNull(lease);

        var response = await ResumeAsync(host, "merging");

        Assert.False(response.Resumed);
        Assert.Contains("merge is in progress", response.Reason);
    }

    /// <summary>
    /// A verification that is really running has a jail, so the entry is not stranded — and the honest
    /// answer is "wait", not a race between a live run and a jail being rebuilt underneath it.
    /// </summary>
    [Fact]
    public async Task Resume_RefusesWhileAVerificationIsGenuinelyInFlight()
    {
        using var host = new DaemonFixture();
        using var release = new SemaphoreSlim(0);
        var queue = SeedQueue(host, runVerification: async (id, ct, q) =>
        {
            await release.WaitAsync(ct).ConfigureAwait(false);
            return new VerificationRecord(
                id, q.CurrentMainSha, Passed: true, LogArtifactPath: "", ResolvedCommand: "npm test",
                ConfigHash: "cfg", When: DateTimeOffset.UtcNow);
        });
        queue.EnsureEntry("busy", MergeEntryOrigin.Local);

        var run = queue.RunVerificationAsync("busy", CancellationToken.None);
        Assert.True(await WaitUntilAsync(() => queue.IsVerificationInFlight("busy")));

        var response = await ResumeAsync(host, "busy");

        Assert.False(response.Resumed);
        // THIS guard's sentence, not the stalled-clear's. An in-flight run implies the state is
        // `Verifying`, so the clear one step later refuses this case too — and an assertion on wording
        // they share passes with this guard deleted, which is exactly what it did before this line named
        // the resume's own reason.
        Assert.Contains("nothing to resume", response.Reason);
        Assert.Contains("has a live sandbox", response.Reason);
        Assert.Equal(WorkerMergeState.Verifying, queue.GetState("busy"));
        // The live run is untouched and still completes normally.
        release.Release();
        var record = await run;
        Assert.True(record.Passed);
        Assert.Equal(WorkerMergeState.Verified, queue.GetState("busy"));
    }

    /// <summary>
    /// A resume refused BEFORE the spawn must leave no trace: no session, no state move, no audit event.
    /// A refusal that half-happened is exactly the "it looked applied and was not" failure this repository
    /// keeps producing.
    /// </summary>
    [Fact]
    public async Task ARefusedResume_LeavesNoSession_NoStateChange_AndNoAuditEvent()
    {
        using var host = new DaemonFixture();
        var audit = host.Services.GetRequiredService<IAuditLog>();
        var queue = SeedQueue(host);
        queue.EnsureEntry("running", MergeEntryOrigin.Local);
        var sessions = host.Services.GetRequiredService<AgentSessionStore>();
        sessions.Spawn("claude-code", role: "", parentAgentId: null, agentId: "running", repoHash: _repoHandle);
        var before = sessions.List().Count;

        var response = await ResumeAsync(host, "running");

        Assert.False(response.Resumed);
        Assert.Equal(before, sessions.List().Count);
        Assert.Equal(WorkerMergeState.Working, queue.GetState("running"));
        // Repo-scoped: since P2-15 the audit log persists in the run-shared daemon DB, so an
        // unscoped DoesNotContain would trip on OTHER tests' (legitimate) resume events.
        Assert.DoesNotContain(audit.Read(),
            e => e.Type == AgentResumeService.ResumedEvent && e.Fields["repo"] == _repoHandle);
    }

    /// <summary>
    /// <b>The stale "verifying" claim is retracted, and never left standing for a jail that is gone.</b>
    ///
    /// <para>The state is persisted per transition while the in-flight set is daemon memory, so a restart
    /// mid-run rehydrates <c>Verifying</c> with nothing executing — which is what constructing the queue
    /// over a store already holding that row reproduces exactly. A resume walks it back to <c>Working</c>
    /// through the same method the standalone clear uses, so there is one implementation of that transition
    /// rather than two that can drift.</para>
    ///
    /// <para>And because the retraction is a state change the human did not directly ask for, a refusal
    /// that happens afterwards has to <b>say</b> it — a refusal reads as "nothing happened", and a mutation
    /// hidden inside one is the silent side effect this codebase keeps finding.</para>
    /// </summary>
    [Fact]
    public async Task Resume_RetractsAStaleVerifyingClaim_WithoutDiscardingTheEvidence_AndSaysSo()
    {
        using var host = new DaemonFixture();
        var verifications = new InMemoryVerificationStore();
        var record = new VerificationRecord(
            "frozen", MainSha, Passed: true, LogArtifactPath: "/artifacts/frozen.log",
            ResolvedCommand: "npm test", ConfigHash: "cfg", When: DateTimeOffset.UtcNow);
        verifications.Insert(_repoHandle, record);

        // The row a daemon restart rehydrates: Verifying, persisted, with no run behind it anywhere.
        var store = new InMemoryMergeQueueStore();
        store.Save(new Mainguard.Git.Models.MergeQueueRow
        {
            RepoHash = _repoHandle,
            AgentId = "frozen",
            State = WorkerMergeState.Verifying.ToString(),
            Origin = MergeEntryOrigin.Local.ToString(),
            UpdatedUtc = DateTime.UtcNow,
        });

        var queue = SeedQueue(host, store: store, verifications: verifications);
        Assert.Equal(WorkerMergeState.Verifying, queue.GetState("frozen"));
        Assert.False(queue.IsVerificationInFlight("frozen"));

        // This repo has no mirror on disk, so the spawn cannot produce a jail — which makes this the
        // refusal-after-retraction path, the one where silence would be the defect.
        var response = await ResumeAsync(host, "frozen");

        Assert.False(response.Resumed);
        Assert.True(response.ClearedStalledVerification);
        Assert.Contains("no sandbox", response.Reason);
        Assert.Contains("stalled verification was cleared", response.Reason);

        // The claim is gone; the evidence is not. The immutable record of the run that really happened is
        // untouched, and the entry is not carrying a resurrected "verified" state for a jail that is gone.
        Assert.Equal(WorkerMergeState.Working, queue.GetState("frozen"));
        Assert.NotNull(verifications.Latest(_repoHandle, "frozen"));
        Assert.Equal(record.LogArtifactPath, verifications.Latest(_repoHandle, "frozen")!.LogArtifactPath);
        Assert.False(queue.CanMerge("frozen", out var gate));
        Assert.Equal("not verified yet", gate);
    }

    /// <summary>
    /// The daemon derives the resuming identity from the connection. The request message deliberately
    /// carries no actor field, so there is nothing for a token-holder to forge — the same rule a plan
    /// approval and a discard follow.
    /// </summary>
    [Fact]
    public void ResumeRequest_CarriesNoActorField()
    {
        var fields = typeof(ResumeAgentRequest).GetProperties()
            .Select(p => p.Name)
            .ToArray();

        Assert.DoesNotContain(fields, n =>
            n.Contains("Actor", StringComparison.OrdinalIgnoreCase)
            || n.Contains("ResumedBy", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Identity", StringComparison.OrdinalIgnoreCase));

        // …and no role either, so a resume structurally cannot mint a coordinator or a managed session.
        Assert.DoesNotContain(fields, n => n.Equals("Role", StringComparison.Ordinal));
    }

    // ---- 4. the liveness fact the surface reads ---------------------------------------------------

    /// <summary>
    /// <c>has_live_sandbox</c> is what lets a client tell a stranded entry from a working one, and it is
    /// keyed on <c>(repo, agent)</c>. Answering it from another repository's session of the same name
    /// would report a stranded entry as healthy — which is precisely what keeps it stranded.
    /// </summary>
    [Fact]
    public async Task TheQueueStream_ReportsLivenessPerEntry_ScopedToTheRepo()
    {
        using var host = new DaemonFixture();
        var queue = SeedQueue(host);
        queue.EnsureEntry("stranded", MergeEntryOrigin.Local);
        queue.EnsureEntry("alive", MergeEntryOrigin.Local);

        var sessions = host.Services.GetRequiredService<AgentSessionStore>();
        var live = sessions.Spawn("claude-code", role: "", parentAgentId: null, agentId: "alive", repoHash: _repoHandle);
        sessions.AttachSandbox(live.Key, "container-alive");
        // The same id in ANOTHER repo, with a live jail. It must not make this repo's entry look healthy.
        var other = sessions.Spawn(
            "claude-code", role: "", parentAgentId: null, agentId: "stranded",
            repoHash: "repo-elsewhere-" + Guid.NewGuid().ToString("N"));
        sessions.AttachSandbox(other.Key, "container-elsewhere");

        var snapshot = await SnapshotAsync(host);

        var stranded = snapshot.Entries.Single(e => e.AgentId == "stranded");
        var alive = snapshot.Entries.Single(e => e.AgentId == "alive");
        Assert.True(stranded.HasHasLiveSandbox, "liveness must be stated, not left to proto3's default");
        Assert.False(stranded.HasLiveSandbox);
        Assert.True(alive.HasLiveSandbox);
    }

    // ---- 5. the shipped rail, through the shipped adapter, into a real daemon --------------------

    /// <summary>
    /// The rail's Resume button drives the <b>shipped</b> <c>QueueRailViewModel</c> through the
    /// <b>shipped</b> <c>DaemonBackedOrchestrator</c> into a real in-proc daemon — and the daemon's refusal
    /// reaches the human verbatim.
    ///
    /// <para>The daemon answers a refused resume with an ordinary successful RPC carrying
    /// <c>resumed=false</c>, so "no exception" is not evidence a jail exists. Both halves of the path are
    /// asserted here: the adapter has to turn that into a throw, and the runner has to turn the throw into
    /// a warning. Without both, a refused resume would look exactly like a successful one — a row that
    /// stays stranded while the surface says nothing.</para>
    ///
    /// <para>It also proves the row offers Resume for the right reason: the entry has no session, so the
    /// daemon's <c>has_live_sandbox</c> is false, and that fact travels the whole way to the button.</para>
    /// </summary>
    [Fact]
    public async Task TheRailsResume_ReachesTheDaemon_AndAResusalIsReportedVerbatim()
    {
        using var host = new DaemonFixture();
        var queue = SeedQueue(host);
        queue.EnsureEntry("stranded", MergeEntryOrigin.Local);

        using var client = new DaemonClient(host.CreateChannel, () => host.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.SetActiveRepo(_repoHandle, localRepoPath: null, syncRemoteName: null);
        Assert.True(await WaitUntilAsync(() => adapter.GetQueue().Any(q => q.AgentId == "stranded")),
            "the queue projection never carried the stranded entry");

        var reported = new List<(string Message, bool IsWarning)>();
        var rail = new QueueRailViewModel(
            adapter, _ => { }, (m, w) => reported.Add((m, w)), resumeAgentKind: () => "claude-code");
        adapter.Changed += rail.Refresh;
        rail.Refresh();

        var row = rail.Entries.Single(e => e.AgentId == "stranded");
        Assert.True(row.CanResume, "an entry the daemon says has no sandbox must offer Resume");

        await row.ResumeCommand.ExecuteAsync(null);

        var (message, isWarning) = Assert.Single(reported);
        Assert.True(isWarning, "a refused resume must read as a warning, not as a success");
        // The daemon's own words, not a client-invented sentence.
        Assert.Contains("produced no sandbox", message);
        // …and nothing moved: the entry is still there, still stranded, still Working.
        Assert.Equal(WorkerMergeState.Working, queue.GetState("stranded"));
        Assert.Contains("stranded", queue.Agents);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private async Task<ResumeAgentResponse> ResumeAsync(DaemonFixture host, string agentId)
    {
        var client = new AgentService.AgentServiceClient(host.CreateChannel());
        using var cts = new CancellationTokenSource(Timeout);
        return await client.ResumeAgentAsync(
            new ResumeAgentRequest
            {
                RepoHandle = _repoHandle,
                AgentId = agentId,
                AgentKind = "claude-code",
            },
            host.AuthHeaders(), cancellationToken: cts.Token);
    }

    private async Task<QueueUpdate> SnapshotAsync(DaemonFixture host)
    {
        var client = new MergeQueueService.MergeQueueServiceClient(host.CreateChannel());
        using var cts = new CancellationTokenSource(Timeout);
        using var call = client.StreamQueue(
            new StreamQueueRequest { RepoHandle = _repoHandle }, host.AuthHeaders(), cancellationToken: cts.Token);
        Assert.True(await call.ResponseStream.MoveNext(cts.Token), "the queue stream produced no snapshot");
        return call.ResponseStream.Current;
    }

    /// <summary>
    /// Registers a live queue for a handle, built with the daemon's own lease store and audit sink so the
    /// checks under test are the real ones. The repo has no mirror on disk, which is exactly right for this
    /// suite: every assertion here is about a decision taken BEFORE any jail could be built.
    /// </summary>
    private MergeQueue SeedQueue(
        DaemonFixture host, string? handle = null,
        Func<string, CancellationToken, MergeQueue, Task<VerificationRecord>>? runVerification = null,
        IMergeQueueStore? store = null,
        IVerificationStore? verifications = null)
    {
        var repoHandle = handle ?? _repoHandle;
        var registry = host.Services.GetRequiredService<MergeQueueRegistry>();
        var leases = host.Services.GetRequiredService<IMergeLeaseStore>();

        MergeQueue queue = null!;
        queue = new MergeQueue(
            repoHash: repoHandle,
            currentMainSha: MainSha,
            // Supplied by the caller for the one case that needs it: a store already holding a row is how
            // a daemon restart's rehydration is reproduced, since MergeQueue hydrates in its constructor.
            store: store ?? new InMemoryMergeQueueStore(),
            verifications: verifications ?? new InMemoryVerificationStore(),
            runVerification: (id, ct) => runVerification is not null
                ? runVerification(id, ct, queue)
                : Task.FromResult(new VerificationRecord(
                    id, queue.CurrentMainSha, Passed: true, LogArtifactPath: "", ResolvedCommand: "npm test",
                    ConfigHash: "cfg", When: DateTimeOffset.UtcNow)),
            requeue: (_, _) => Task.CompletedTask,
            audit: host.Services.GetRequiredService<IAuditLog>());

        registry.Register(repoHandle, new MergeQueueContext(queue, leases));
        return queue;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return predicate();
    }
}
