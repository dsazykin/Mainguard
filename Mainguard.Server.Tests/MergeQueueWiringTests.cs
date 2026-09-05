using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using LibGit2Sharp;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// MG-10 — the merge queue is actually INSTANTIATED by the running daemon.
///
/// <para><c>new MergeQueue(...)</c>, <c>new MergeQueueContext(...)</c> and <c>registry.Register(...)</c>
/// appeared only in the test projects. The daemon registered an empty <see cref="IMergeQueueRegistry"/> and
/// nothing ever wrote to it, so <c>MergeQueueGrpcService.Resolve</c> threw <c>NOT_FOUND</c> for every handle
/// for the daemon's whole lifetime — and the client's queue pump swallowed it and retried against an empty
/// projection, which is why nobody noticed. The P2-10 merge guarantees were therefore neither enforced nor
/// bypassable: they were not running at all.</para>
///
/// <para>These run through the REAL composition root (<see cref="DaemonFixture"/> = the whole
/// <c>DaemonHost</c> graph) against a REAL provisioned bare mirror, and drive the shipped RPCs. Nothing is
/// hand-registered into the registry: if the daemon does not build the queue itself, they fail with the
/// exact <c>NOT_FOUND</c> the finding describes.</para>
/// </summary>
public sealed class MergeQueueWiringTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ProvisionedRepo_YieldsANonNotFoundStreamQueue_ThroughTheRealCompositionRoot()
    {
        using var repos = new TempRepos();
        using var host = NewHost(repos.VmRoot);
        var (client, sync, headers) = NewClients(host);

        // The ONLY thing this test does to make the repo active is call the shipped ProvisionRepo RPC.
        var provisioned = await sync.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = repos.Source }, headers);
        Assert.False(string.IsNullOrWhiteSpace(provisioned.RepoHandle));

        using var cts = new CancellationTokenSource(Timeout);
        using var stream = client.StreamQueue(
            new StreamQueueRequest { RepoHandle = provisioned.RepoHandle }, headers, cancellationToken: cts.Token);

        // Pre-fix this MoveNext threw RpcException/NotFound: "No active merge queue for repo handle '…'".
        Assert.True(await stream.ResponseStream.MoveNext(cts.Token));
        var snapshot = stream.ResponseStream.Current;

        // A live queue reports the mirror's real main sha — not an empty projection.
        Assert.False(string.IsNullOrWhiteSpace(snapshot.MainSha));
        Assert.Equal(repos.MainSha, snapshot.MainSha);
    }

    [Fact]
    public async Task CreateWorktree_PutsTheAgentIntoTheLiveQueue()
    {
        using var repos = new TempRepos();
        using var host = NewHost(repos.VmRoot);
        var (client, sync, headers) = NewClients(host);

        var provisioned = await sync.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = repos.Source }, headers);
        await sync.CreateWorktreeAsync(new CreateWorktreeRequest
        {
            RepoHandle = provisioned.RepoHandle,
            AgentId = "loom-7",
        }, headers);

        using var cts = new CancellationTokenSource(Timeout);
        using var stream = client.StreamQueue(
            new StreamQueueRequest { RepoHandle = provisioned.RepoHandle }, headers, cancellationToken: cts.Token);
        Assert.True(await stream.ResponseStream.MoveNext(cts.Token));

        // The queue only reports agents it tracks; without the EnsureEntry wiring the repo has branches but
        // an empty queue, which renders as "no work in flight" while work is very much in flight.
        var entry = Assert.Single(stream.ResponseStream.Current.Entries);
        Assert.Equal("loom-7", entry.AgentId);
        Assert.Equal(WorkerMergeState.Working.ToString(), entry.State);
        Assert.False(entry.CanMerge); // unverified — the gate is live, not a default-true placeholder.
    }

    /// <summary>
    /// MG-29's boot merge-reconcile cascade resolves its queue out of this registry, so it only becomes live
    /// once something populates it. This asserts the interaction end to end: the daemon-built queue for a
    /// provisioned repo is reachable through the same <c>Resolve(repoHash)</c> the reconcile callback uses.
    /// </summary>
    [Fact]
    public async Task TheDaemonBuiltQueue_IsReachableByTheBootReconcileCallbackShape()
    {
        using var repos = new TempRepos();
        using var host = NewHost(repos.VmRoot);
        var (_, sync, headers) = NewClients(host);

        var provisioned = await sync.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = repos.Source }, headers);

        var registry = host.Services.GetRequiredService<IMergeQueueRegistry>();
        var resolved = registry.Resolve(provisioned.RepoHandle);

        Assert.NotNull(resolved);
        Assert.Equal(repos.MainSha, resolved!.Queue.CurrentMainSha);
        // The lease store MUST be the daemon's shared singleton, or cross-origin merge serialization
        // silently reverts: BeginMerge and the foreground/external merges would contend on different rows.
        Assert.Same(host.Services.GetRequiredService<IMergeLeaseStore>(), resolved.Leases);
    }

    // ---- G1: a queue row is a claim on human attention, and requires an approved plan ----------------

    /// <summary>
    /// Defect G1, end to end through the REAL composition root: a coordinator-spawned worker that has
    /// presented no plan gets NO merge-queue row — and gets one the moment a human approves its plan.
    ///
    /// <para>Both halves have to be here rather than only in the provisioner's unit tests, because the
    /// second half depends on a SUBSCRIPTION (<c>PlanApprovalService.PlanApproved</c> →
    /// <c>MergeQueueProvisioner.AdmitDeferredEntries</c>) that lives in the composition root and nowhere
    /// else. A unit test would pass with that line deleted, and the result would be strictly worse than the
    /// defect: every legitimately approved worker silently missing from the queue. This test fails if the
    /// gate is removed (the row appears too early) AND if the subscription is removed (it never appears).</para>
    /// </summary>
    [Fact]
    public async Task AWorkerWithNoApprovedPlan_GetsNoRow_UntilItsPlanIsApproved()
    {
        using var repos = new TempRepos();
        using var host = NewHost(repos.VmRoot);
        var (client, sync, headers) = NewClients(host);

        var provisioned = await sync.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = repos.Source }, headers);

        // The daemon holds this worker's task exactly as the coordinator's spawn path does: it has a
        // branch and a brief, and no authorisation to do anything.
        const string worker = "g1-worker";
        var gate = host.Services.GetRequiredService<WorkerPlanGate>();
        var plans = host.Services.GetRequiredService<
            Mainguard.Agents.Agents.Orchestrator.PlanApprovalService>();
        gate.Hold(worker, "coord-1", "Fix the clock", "the actual work to do", 1m);

        await sync.CreateWorktreeAsync(new CreateWorktreeRequest
        {
            RepoHandle = provisioned.RepoHandle,
            AgentId = worker,
        }, headers);

        // No row. This is the assertion the live daemon failed: three scripted probes with zero plan calls
        // each had one.
        Assert.Empty(await SnapshotAsync(client, headers, provisioned.RepoHandle));

        // The worker presents, and a human approves. Approval is the moment the gate's answer changes.
        var planId = plans.Present(
            worker, "coord-1", "Fix the clock",
            new TaskPlanFields(new[] { "README.md" }, "how", "tests"), "", 1m).PlanId!;
        plans.Approve(planId, "tester");

        var entry = Assert.Single(await SnapshotAsync(client, headers, provisioned.RepoHandle));
        Assert.Equal(worker, entry.AgentId);
        Assert.Equal(WorkerMergeState.Working.ToString(), entry.State);
    }

    /// <summary>
    /// The conflict card's facts have to reach the wire, through the real composition root.
    ///
    /// <para>The daemon has always known where a conflicted worktree is parked and which files conflict —
    /// it writes both into an audit event and a log line — and <c>QueueEntry</c> carried neither, so the
    /// one row on the rail that asks for human judgment reached the client with a sentence naming a
    /// required action and no evidence at all. This is the mapping from the provisioner's parking store to
    /// <c>rebase_conflict</c>, asserted where it actually runs: <c>MergeQueueGrpcService</c> holds the
    /// provisioner as an OPTIONAL dependency, so a composition root that stopped passing it would leave
    /// every conflict card blank again with nothing failing.</para>
    /// </summary>
    [Fact]
    public async Task AParkedConflict_ReachesTheWire_WithItsWorktreeAndItsConflictingFiles()
    {
        using var repos = new TempRepos();
        using var host = NewHost(repos.VmRoot);
        var (client, sync, headers) = NewClients(host);

        var provisioned = await sync.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = repos.Source }, headers);
        await sync.CreateWorktreeAsync(new CreateWorktreeRequest
        {
            RepoHandle = provisioned.RepoHandle,
            AgentId = "loom-7",
        }, headers);

        // The parking the keep-alive cascade's conflict arm writes. Supplied directly rather than driven
        // through a real conflicting rebase: that path has its own end-to-end coverage in
        // MergeQueueProvisionerTests over real git, and what is under test HERE is the seam between the
        // daemon's store and the wire.
        var parkedAt = new DateTimeOffset(2026, 8, 31, 9, 15, 0, TimeSpan.Zero);
        host.Services.GetRequiredService<MergeQueueProvisioner>().ParkedConflicts.Park(
            provisioned.RepoHandle,
            new ParkedRebaseConflict(
                "loom-7", "/srv/mainguard/agents/9f2c/loom-7/worktree", "main",
                new[] { "src/Shared.cs" }, parkedAt));

        var entry = Assert.Single(await SnapshotAsync(client, headers, provisioned.RepoHandle));
        Assert.NotNull(entry.RebaseConflict);
        Assert.Equal("/srv/mainguard/agents/9f2c/loom-7/worktree", entry.RebaseConflict.Worktree);
        Assert.Equal("main", entry.RebaseConflict.MainBranch);
        Assert.Equal(new[] { "src/Shared.cs" }, entry.RebaseConflict.Paths);
        Assert.Equal(parkedAt.ToString("O"), entry.RebaseConflict.ParkedAt);
    }

    /// <summary>
    /// The other half, and the one that decides whether the two conflict controls appear on rows that have
    /// no conflict: an entry with nothing parked carries NO <c>rebase_conflict</c> at all. A message field
    /// filled with defaults would light "Abort rebase" on every branch in the queue — a control whose whole
    /// behaviour, on a branch that is not rebasing, is an error message.
    /// </summary>
    /// <summary>
    /// The hand-back's rewrite grant is wired from the real composition root: the mediator inside the
    /// worktree manager asks the provisioner's parking store, and a mark set by "let the agent resolve"
    /// lets exactly one non-fast-forward publish through. Asserted on the composition root because the
    /// mediator's policy defaults to null — a correct grant nobody installs is rule 2 forever, which is
    /// the defect this closes.
    /// </summary>
    [Fact]
    public void TheHandBackRewriteGrant_IsWiredFromTheParkingStore()
    {
        using var repos = new TempRepos();
        using var host = NewHost(repos.VmRoot);

        var provisioner = host.Services.GetRequiredService<MergeQueueProvisioner>();
        var worktrees = Assert.IsType<WorktreeManager>(host.Services.GetRequiredService<IAgentEnvironment>().Worktrees);
        Assert.True(worktrees.HasHandedBackRewritePolicy);

        // The predicate the mediator holds IS the store's mark, not a copy of it.
        provisioner.ParkedConflicts.MarkHandedBack("repo-x", "agent-x");
        Assert.True(provisioner.ParkedConflicts.IsHandedBack("repo-x", "agent-x"));
        Assert.True(provisioner.ParkedConflicts.ClearHandedBack("repo-x", "agent-x"));
        Assert.False(provisioner.ParkedConflicts.IsHandedBack("repo-x", "agent-x"));
    }

    /// <summary>
    /// The mirror-freshness sweep (2026-09-04) is a hosted service the host itself starts, its one pass
    /// stamps the queue, and the stamp reaches the wire — so "refreshed N min ago" on the rail is a fact
    /// the daemon produced, not a number the client made up.
    /// </summary>
    [Fact]
    public async Task TheMirrorRefreshSweep_IsHostedAtBoot_AndItsStampReachesTheQueueStream()
    {
        using var repos = new TempRepos();
        using var host = NewHost(repos.VmRoot);
        var (client, sync, headers) = NewClients(host);

        var provisioned = await sync.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = repos.Source }, headers);

        var sweep = Assert.Single(host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<MirrorMainRefreshHostedService>());
        var provisioner = host.Services.GetRequiredService<MergeQueueProvisioner>();
        Assert.Null(provisioner.LastMainRefresh(provisioned.RepoHandle));

        sweep.SweepOnce();

        var refresh = provisioner.LastMainRefresh(provisioned.RepoHandle);
        Assert.NotNull(refresh);
        Assert.Null(refresh!.Error);

        using var cts = new CancellationTokenSource(Timeout);
        using var stream = client.StreamQueue(
            new StreamQueueRequest { RepoHandle = provisioned.RepoHandle }, headers, cancellationToken: cts.Token);
        Assert.True(await stream.ResponseStream.MoveNext(cts.Token));
        var update = stream.ResponseStream.Current;
        Assert.Equal(refresh.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture), update.MirrorMainRefreshedAt);
        Assert.Equal(string.Empty, update.MirrorMainRefreshError);

        // The on-demand RPC is the same call, and answers with what the mirror holds.
        var state = await client.RefreshMirrorMainAsync(
            new RefreshMirrorMainRequest { RepoHandle = provisioned.RepoHandle }, headers);
        Assert.Equal(update.MainSha, state.MainSha);
        Assert.Equal(string.Empty, state.Error);
        Assert.False(state.Moved);
    }

    [Fact]
    public async Task AnOrdinaryEntry_CarriesNoConflictOnTheWire()
    {
        using var repos = new TempRepos();
        using var host = NewHost(repos.VmRoot);
        var (client, sync, headers) = NewClients(host);

        var provisioned = await sync.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = repos.Source }, headers);
        await sync.CreateWorktreeAsync(new CreateWorktreeRequest
        {
            RepoHandle = provisioned.RepoHandle,
            AgentId = "loom-7",
        }, headers);

        var entry = Assert.Single(await SnapshotAsync(client, headers, provisioned.RepoHandle));
        Assert.Null(entry.RebaseConflict);
    }

    /// <summary>
    /// Both conflict RPCs refuse an entry that is not parked, as an ordinary answer carrying its reason —
    /// never a fault and never a silent success. A refusal a client cannot read is indistinguishable from
    /// a button that did nothing, which is the failure these controls exist to remove.
    /// </summary>
    [Fact]
    public async Task BothConflictRpcs_RefuseAnUnparkedEntry_WithAReadableReason()
    {
        using var repos = new TempRepos();
        using var host = NewHost(repos.VmRoot);
        var (client, sync, headers) = NewClients(host);

        var provisioned = await sync.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = repos.Source }, headers);

        var handBack = await client.ResolveConflictWithAgentAsync(
            new ResolveConflictWithAgentRequest { RepoHandle = provisioned.RepoHandle, AgentId = "loom-7" },
            headers);
        Assert.False(handBack.HandedBack);
        Assert.Contains("no rebase parked", handBack.Reason);

        var abort = await client.AbortRebaseAsync(
            new AbortRebaseRequest { RepoHandle = provisioned.RepoHandle, AgentId = "loom-7" },
            headers);
        Assert.False(abort.Aborted);
        Assert.Contains("no rebase parked", abort.Reason);
    }

    /// <summary>
    /// <b>The human's Verify button must not start a run inside a frozen jail.</b>
    ///
    /// <para>A sibling fix closed this on the COORDINATOR's <c>request_verification</c> op, in
    /// <c>AgentSpawnService</c>. The human's Verify reaches the same merge queue by a different path —
    /// this RPC — and was still unguarded, so pressing it on a conflicted entry started a run whose
    /// <c>docker exec</c> answers "Container ... is paused, unpause the container before exec". That
    /// arrives as a provisioning failure, on the one screen where "we could not run your tests" and "your
    /// tests failed" is the distinction the merge decision rests on.</para>
    ///
    /// <para>The predicate is <see cref="FrozenJailPolicy"/>'s, shared with the coordinator's guard on
    /// purpose: two spellings of "is this jail frozen" is how one of them stops agreeing with the state
    /// word the surface renders. <c>Conflict</c> is asserted specifically because it is the state a parked
    /// keep-alive rebase writes — and the one <c>HumanPauseLedger.IsHumanPaused</c> answers FALSE for,
    /// which is why that ledger is the wrong predicate here.</para>
    /// </summary>
    [Theory]
    [InlineData("Conflict")]
    [InlineData("Paused")]
    public async Task Verify_OnAFrozenJail_IsRefusedWithTheReasonAndTheWayOut(string state)
    {
        using var repos = new TempRepos();
        using var host = NewHost(repos.VmRoot);
        var (client, sync, headers) = NewClients(host);

        var provisioned = await sync.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = repos.Source }, headers);
        await sync.CreateWorktreeAsync(new CreateWorktreeRequest
        {
            RepoHandle = provisioned.RepoHandle,
            AgentId = "loom-7",
        }, headers);

        var sessions = host.Services.GetRequiredService<AgentSessionStore>();
        sessions.Spawn("claude-code", agentId: "loom-7", repoHash: provisioned.RepoHandle);
        sessions.MarkState(
            new AgentSessionKey(provisioned.RepoHandle, "loom-7"), state, "frozen for the test");

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            client.RunVerificationAsync(
                new RunVerificationRequest { RepoHandle = provisioned.RepoHandle, AgentId = "loom-7" },
                headers).ResponseAsync);

        // A typed refusal, never an opaque fault and never a Passed=false response.
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("frozen", ex.Status.Detail);
        // …and it names the way out, which is the difference between a refusal and a dead end. Both of
        // this branch's conflict controls are reachable from the same card.
        Assert.Contains("abort the rebase", ex.Status.Detail);
        Assert.Contains("resume the agent", ex.Status.Detail);
        // It must not read as a test result.
        Assert.DoesNotContain("failed", ex.Status.Detail);
    }

    /// <summary>
    /// <b>The cross-seam half of the same composition.</b> The "let the agent resolve" control moves the
    /// session off the frozen word — to <see cref="AgentRunState.Rebasing"/> — and only then delivers its
    /// instruction, so that a state-word guard on the delivery path cannot refuse the very control that
    /// unfroze the jail. The ordering is pinned in <c>MergeQueueProvisionerTests</c>, which cannot see this
    /// assembly; this is the other side of it, holding the real policy to the word that control writes.
    ///
    /// <para>It fails if <see cref="FrozenJailPolicy"/> is ever widened to count <c>Rebasing</c> as frozen
    /// — which would be a defensible-looking change (a rebase IS a moment not to type into) that silently
    /// breaks the hand-back, and breaks it in the worst way: the button reports success and delivers
    /// nothing.</para>
    /// </summary>
    [Fact]
    public void TheHandBacksStateWord_IsOneTheFrozenJailGuardsLetThrough()
    {
        // What the hand-back leaves on the session at delivery time.
        Assert.False(FrozenJailPolicy.IsFrozen(nameof(AgentRunState.Rebasing)));

        // ...and the two words it must never be confused with, which the same policy still refuses. The
        // second is the one HumanPauseLedger.IsHumanPaused answers false for, which is why the policy
        // keys on the state word instead.
        Assert.True(FrozenJailPolicy.IsFrozen(AgentSessionReconciler.PausedState));
        Assert.True(FrozenJailPolicy.IsFrozen(nameof(AgentRunState.Conflict)));

        // An unknown/absent word is NOT frozen: the guards must not refuse from ignorance.
        Assert.False(FrozenJailPolicy.IsFrozen(null));
        Assert.False(FrozenJailPolicy.IsFrozen("Working"));
    }

    /// <summary>
    /// The control that keeps the guard from being a blanket refusal: a RUNNING jail still verifies, and
    /// so does an entry the session store knows nothing about (a seeded row, an entry whose session died
    /// with a previous daemon). Refusing from ignorance would strand every such entry with the one
    /// message that sounds like it has a live, frozen jail.
    /// </summary>
    [Fact]
    public async Task Verify_OnARunningOrUnknownJail_IsNotRefusedAsFrozen()
    {
        using var repos = new TempRepos();
        using var host = NewHost(repos.VmRoot);
        var (client, sync, headers) = NewClients(host);

        var provisioned = await sync.ProvisionRepoAsync(
            new ProvisionRepoRequest { OriginUrl = repos.Source }, headers);
        await sync.CreateWorktreeAsync(new CreateWorktreeRequest
        {
            RepoHandle = provisioned.RepoHandle,
            AgentId = "loom-7",
        }, headers);

        // No session at all — the store cannot answer, and "cannot answer" is not "frozen".
        var unknown = await Assert.ThrowsAsync<RpcException>(() =>
            client.RunVerificationAsync(
                new RunVerificationRequest { RepoHandle = provisioned.RepoHandle, AgentId = "loom-7" },
                headers).ResponseAsync);
        Assert.DoesNotContain("frozen", unknown.Status.Detail);

        // A live, working session: same answer. (Both fall through to the ordinary no-jail refusal, which
        // is the honest one for a worktree with no container behind it.)
        var sessions = host.Services.GetRequiredService<AgentSessionStore>();
        sessions.Spawn("claude-code", agentId: "loom-7", repoHash: provisioned.RepoHandle);
        sessions.MarkState(
            new AgentSessionKey(provisioned.RepoHandle, "loom-7"), "Working", null);

        var working = await Assert.ThrowsAsync<RpcException>(() =>
            client.RunVerificationAsync(
                new RunVerificationRequest { RepoHandle = provisioned.RepoHandle, AgentId = "loom-7" },
                headers).ResponseAsync);
        Assert.DoesNotContain("frozen", working.Status.Detail);
    }

    private static async Task<System.Collections.Generic.IReadOnlyList<Mainguard.Protos.V1.QueueEntry>>
        SnapshotAsync(MergeQueueService.MergeQueueServiceClient client, Metadata headers, string repoHandle)
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var stream = client.StreamQueue(
            new StreamQueueRequest { RepoHandle = repoHandle }, headers, cancellationToken: cts.Token);
        Assert.True(await stream.ResponseStream.MoveNext(cts.Token));
        return stream.ResponseStream.Current.Entries.ToList();
    }

    // ---- helpers ---------------------------------------------------------

    // The real composition root with ONE override: the substrate points at an isolated temp VM root so
    // provisioning never writes to the developer's ~/mainguard. Everything else — the registry, the
    // provisioner, the gRPC services — is the shipped graph.
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> NewHost(string vmRoot)
        => new DaemonFixture().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<IAgentEnvironment>(new Wsl2AgentEnvironment(vmRoot: vmRoot))));

    private static (MergeQueueService.MergeQueueServiceClient Merge,
        RepoSyncService.RepoSyncServiceClient Sync, Metadata Headers) NewClients(
            Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
    {
        var channel = GrpcChannel.ForAddress(host.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = host.Server.CreateHandler() });
        var headers = new Metadata
        {
            { "authorization", $"bearer {host.Services.GetRequiredService<SessionTokenFile>().Token}" },
        };
        return (new MergeQueueService.MergeQueueServiceClient(channel),
            new RepoSyncService.RepoSyncServiceClient(channel), headers);
    }

    /// <summary>A seeded source repo plus an isolated VM root, so provisioning never touches ~/mainguard.</summary>
    private sealed class TempRepos : IDisposable
    {
        public string VmRoot { get; }
        public string Source { get; }
        public string MainSha { get; }

        public TempRepos()
        {
            VmRoot = NewDir("mainguard-mqwire-vm-");
            Source = NewDir("mainguard-mqwire-src-");

            Repository.Init(Source);
            using var repo = new Repository(Source);
            repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
            repo.Config.Set("user.email", "test@mainguard.local", ConfigurationLevel.Local);
            repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
            File.WriteAllText(Path.Combine(Source, "README.md"), "seed\n");
            Commands.Stage(repo, "README.md");
            var sig = new Signature("test-user", "test@mainguard.local", DateTimeOffset.Now);
            MainSha = repo.Commit("seed commit", sig, sig).Sha;
        }

        public void Dispose()
        {
            TryDelete(VmRoot);
            TryDelete(Source);
        }

        private static string NewDir(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(path, recursive: true);
            }
            catch { /* never fail a test from cleanup */ }
        }
    }
}
