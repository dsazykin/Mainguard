using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Server.Gateway;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// <b>A session's identity is (repo, agent id), not the id.</b>
///
/// <para>The external-PR intake names its sessions <c>pr-&lt;n&gt;</c> after the pull request number, and
/// that string is simultaneously the worktree, the <c>agent/pr-&lt;n&gt;</c> branch, the jail's
/// <c>mainguard.agent</c> label, the per-agent package cache and the merge-queue key. All of those are
/// per-repo. <see cref="AgentSessionStore"/> was not: it was daemon-global and keyed by the id alone, so
/// two subscribed repositories that each had a pull request #7 both wanted <c>pr-7</c> and the second was
/// refused by name — its pull request was never intake'd at all.</para>
///
/// <para>These tests prove the behaviour rather than the key's shape: two repos each run <c>pr-7</c>, each
/// gets its own jail, and neither can see, verify in, or stop the other's session. The duplicate guard
/// that makes an overwrite impossible is unchanged — it now fires on the FULL key, which is the only thing
/// that was ever meant by "already live".</para>
/// </summary>
public sealed class AgentSessionRepoScopingTests
{
    private const string RepoA = "aaaaaaaaaaaa1111";
    private const string RepoB = "bbbbbbbbbbbb2222";
    private const string SharedId = "pr-7";

    // ===================== the store =====================

    /// <summary>The defect, at the store: <c>pr-7</c> in two repos is two sessions, each with its own
    /// jail, and each reachable ONLY under its own repo.</summary>
    [Fact]
    public void Spawn_TheSameAgentId_InTwoRepos_YieldsTwoIndependentSessions()
    {
        var store = new AgentSessionStore(new InMemoryAuditLog());

        store.Spawn("external-pr", AgentRoles.Managed, agentId: SharedId, repoHash: RepoA);
        store.Spawn("external-pr", AgentRoles.Managed, agentId: SharedId, repoHash: RepoB);
        store.AttachSandbox(SharedId, "ctr-a", RepoA);
        store.AttachSandbox(SharedId, "ctr-b", RepoB);

        Assert.Equal(2, store.List().Count);
        Assert.Equal("ctr-a", store.Find(RepoA, SharedId)?.ContainerId);
        Assert.Equal("ctr-b", store.Find(RepoB, SharedId)?.ContainerId);

        // Neither repo can see the other's session THROUGH ITS OWN SCOPE — a third repo sees neither.
        Assert.Null(store.Find("cccccccccccc3333", SharedId));
    }

    /// <summary>
    /// The guard that must survive the re-keying. Overwriting a live session would drop the running jail's
    /// container id on the floor, leaking the container, its MG-36 network segment and its package-cache
    /// lease with nothing left able to name them. So a duplicate of the FULL key still throws — while the
    /// same id under a different repo, which is not a duplicate at all, is admitted in the same test.
    /// </summary>
    [Fact]
    public void Spawn_DuplicateOfTheFullKey_StillThrows_AndLeavesTheRunningJailIntact()
    {
        var store = new AgentSessionStore(new InMemoryAuditLog());
        store.Spawn("external-pr", AgentRoles.Managed, agentId: SharedId, repoHash: RepoA);
        store.AttachSandbox(SharedId, "ctr-a", RepoA);

        // Same id, DIFFERENT repo — admitted. (Without this the throw below would be indistinguishable
        // from a store that still refuses the id globally.)
        store.Spawn("external-pr", AgentRoles.Managed, agentId: SharedId, repoHash: RepoB);

        // Same id, SAME repo — refused, loudly.
        var duplicate = Assert.Throws<InvalidOperationException>(
            () => store.Spawn("external-pr", AgentRoles.Managed, agentId: SharedId, repoHash: RepoA));
        Assert.Contains(RepoA, duplicate.Message, StringComparison.Ordinal);

        // …and the refusal did not disturb the record it refused to replace.
        Assert.Equal("ctr-a", store.Find(RepoA, SharedId)?.ContainerId);
        Assert.Equal("Working", store.Find(RepoA, SharedId)?.State);
        Assert.Equal(2, store.List().Count);
    }

    /// <summary>Stopping one repo's <c>pr-7</c> must not remove, or even disturb, the other's.</summary>
    [Fact]
    public void Stop_ScopedToOneRepo_LeavesTheOtherReposSessionRunning()
    {
        var store = new AgentSessionStore(new InMemoryAuditLog());
        store.Spawn("external-pr", AgentRoles.Managed, agentId: SharedId, repoHash: RepoA);
        store.Spawn("external-pr", AgentRoles.Managed, agentId: SharedId, repoHash: RepoB);
        store.AttachSandbox(SharedId, "ctr-a", RepoA);
        store.AttachSandbox(SharedId, "ctr-b", RepoB);

        Assert.True(store.Stop(new AgentSessionKey(RepoA, SharedId)));

        Assert.Null(store.Find(RepoA, SharedId));
        Assert.Equal("ctr-b", store.Find(RepoB, SharedId)?.ContainerId);
        Assert.Equal("Working", store.Find(RepoB, SharedId)?.State);
        Assert.Single(store.List());
    }

    /// <summary>A state word written for one repo's <c>pr-7</c> must not appear on the other's — a
    /// "Dead" or "Paused" that leaks across repos is a lie about a jail that is still running.</summary>
    [Fact]
    public void MarkState_ScopedToOneRepo_DoesNotMoveTheOtherReposState()
    {
        var store = new AgentSessionStore(new InMemoryAuditLog());
        store.Spawn("external-pr", AgentRoles.Managed, agentId: SharedId, repoHash: RepoA);
        store.Spawn("external-pr", AgentRoles.Managed, agentId: SharedId, repoHash: RepoB);
        store.AttachSandbox(SharedId, "ctr-a", RepoA);
        store.AttachSandbox(SharedId, "ctr-b", RepoB);

        store.MarkState(new AgentSessionKey(RepoA, SharedId), "Dead", "the CLI exited");

        Assert.Equal("Dead", store.Find(RepoA, SharedId)?.State);
        Assert.Equal("Working", store.Find(RepoB, SharedId)?.State);
    }

    /// <summary>
    /// The id-only entry points the daemon still has (the <c>StopAgent</c> RPC, the PTY binder's exit
    /// watcher) must refuse an ambiguous id rather than pick one. Guessing would tear down or relabel
    /// whichever repo's jail happened to be enumerated first.
    /// </summary>
    [Fact]
    public void IdOnlyLookups_WhenTwoReposHoldTheId_ResolveNothing_RatherThanGuessing()
    {
        var store = new AgentSessionStore(new InMemoryAuditLog());
        store.Spawn("external-pr", AgentRoles.Managed, agentId: SharedId, repoHash: RepoA);
        store.AttachSandbox(SharedId, "ctr-a", RepoA);

        // Control: with ONE holder the id-only path still resolves — so the nulls below are the ambiguity
        // and not an id-only path that resolves nothing ever.
        Assert.Equal("ctr-a", store.Find(SharedId)?.ContainerId);

        store.Spawn("external-pr", AgentRoles.Managed, agentId: SharedId, repoHash: RepoB);
        store.AttachSandbox(SharedId, "ctr-b", RepoB);

        Assert.Null(store.Find(SharedId));
        store.MarkState(SharedId, "Dead", "ambiguous");
        Assert.Equal("Working", store.Find(RepoA, SharedId)?.State);
        Assert.Equal("Working", store.Find(RepoB, SharedId)?.State);

        Assert.False(store.Stop(SharedId));
        Assert.Equal(2, store.List().Count);

        // FindAll is the honest answer to the same question: both, named by repo.
        Assert.Equal(new[] { RepoA, RepoB }, store.FindAll(SharedId).Select(s => s.RepoHash).ToArray());
    }

    // ===================== the daemon spawn chain =====================

    /// <summary>
    /// <b>The proof.</b> Two subscribed repositories, each with pull request #7, both intake'd through the
    /// production <see cref="ExternalPrWorkerHost"/> over the production
    /// <see cref="AgentSpawnService"/> spawn chain. Before this both wanted the id <c>pr-7</c>; the first
    /// took it and the second came back <c>Failed("… already taken by repo …")</c> forever.
    ///
    /// <para>Then the isolation, which is the half a key-shape assertion would miss: each session resolves
    /// only under its own repo, each jail is a DIFFERENT container, and releasing repo A's pull request
    /// leaves repo B's session and jail untouched — not stopped, not torn down.</para>
    /// </summary>
    [Fact]
    public async Task TwoRepos_EachWithPr7_BothGetTheirOwnJail_AndNeitherCanStopTheOther()
    {
        using var rig = new ScopingRig();

        var a = await rig.Workers.EnsureWorkerAsync(RepoA, SharedId, 7, CancellationToken.None);
        var b = await rig.Workers.EnsureWorkerAsync(RepoB, SharedId, 7, CancellationToken.None);

        // BOTH spawn. Neither is refused, and neither adopts the other.
        Assert.Equal(PrWorkerOutcome.Spawned, a.Outcome);
        Assert.Equal(PrWorkerOutcome.Spawned, b.Outcome);

        var sessionA = rig.Store.Find(RepoA, SharedId);
        var sessionB = rig.Store.Find(RepoB, SharedId);
        Assert.NotNull(sessionA);
        Assert.NotNull(sessionB);
        Assert.Equal($"ctr-{RepoA}-{SharedId}", sessionA!.ContainerId);
        Assert.Equal($"ctr-{RepoB}-{SharedId}", sessionB!.ContainerId);
        Assert.NotEqual(sessionA.ContainerId, sessionB.ContainerId);
        Assert.Equal(2, rig.Store.List().Count);

        // The jails really are two: the substrate was asked to start one per (repo, agent).
        Assert.Equal(
            new[] { (RepoA, SharedId), (RepoB, SharedId) },
            rig.Engine.Spawns.OrderBy(s => s.Repo, StringComparer.Ordinal).ToArray());

        // Repo A's pull request closes upstream. Its worker is released — and ONLY its worker.
        await rig.Workers.ReleaseWorkerAsync(RepoA, SharedId, CancellationToken.None);

        Assert.Null(rig.Store.Find(RepoA, SharedId));
        Assert.Contains($"ctr-{RepoA}-{SharedId}", rig.Environment.RemovedContainers);
        Assert.DoesNotContain($"ctr-{RepoB}-{SharedId}", rig.Environment.RemovedContainers);

        var survivor = rig.Store.Find(RepoB, SharedId);
        Assert.NotNull(survivor);
        Assert.Equal($"ctr-{RepoB}-{SharedId}", survivor!.ContainerId);
        Assert.Equal("Working", survivor.State);

        // …and repo B's terminal input lock (P2-14, keyed by agent id alone) was NOT released by repo A's
        // stop: an id-keyed teardown that fired on any stop would have handed somebody else's untrusted
        // pull-request jail a typeable terminal.
        Assert.True(rig.Locks.IsLocked(SharedId));
    }

    /// <summary>
    /// The idempotence leg, per repo. A repeat poll of repo A's still-open pull request adopts repo A's
    /// live worker and spawns nothing — while repo B, which has no worker, still gets one. Under the old
    /// global key the second repo's poll could only ever answer Failed.
    /// </summary>
    [Fact]
    public async Task EnsureWorker_IsIdempotentPerRepo_NotPerId()
    {
        using var rig = new ScopingRig();

        Assert.Equal(
            PrWorkerOutcome.Spawned,
            (await rig.Workers.EnsureWorkerAsync(RepoA, SharedId, 7, CancellationToken.None)).Outcome);
        Assert.Equal(
            PrWorkerOutcome.AlreadyLive,
            (await rig.Workers.EnsureWorkerAsync(RepoA, SharedId, 7, CancellationToken.None)).Outcome);

        // The other repo's identical id is a different worker, and it is spawned rather than "adopted".
        Assert.Equal(
            PrWorkerOutcome.Spawned,
            (await rig.Workers.EnsureWorkerAsync(RepoB, SharedId, 7, CancellationToken.None)).Outcome);
        Assert.Equal(2, rig.Engine.Spawns.Count);
    }

    /// <summary>
    /// <c>CoordinatorSpawnGate</c> counts ACTIVE MANAGED WORKERS against <c>MaxActiveWorkers</c>, a
    /// box-wide allowance. Re-keying must not change what that population is: two repos' <c>pr-7</c> are
    /// two workers on the box and must consume two slots. (Before, the second could not be recorded at
    /// all, so it was invisible to the cap even when its jail was refused for a different reason.)
    /// </summary>
    [Fact]
    public async Task TheWorkerCap_CountsBothReposPr7_AsTwoWorkers()
    {
        using var rig = new ScopingRig(maxActiveWorkers: 2);

        Assert.Equal(
            PrWorkerOutcome.Spawned,
            (await rig.Workers.EnsureWorkerAsync(RepoA, SharedId, 7, CancellationToken.None)).Outcome);
        Assert.Equal(
            PrWorkerOutcome.Spawned,
            (await rig.Workers.EnsureWorkerAsync(RepoB, SharedId, 7, CancellationToken.None)).Outcome);
        Assert.Equal(2, rig.Store.List().Count(s => s.Role == AgentRoles.Managed));

        // The pool is now full — of two sessions that share an id. A third repo's pull request #7 is
        // refused BY THE CAP, which is only possible if both of the first two were counted.
        var third = await rig.Workers.EnsureWorkerAsync("cccccccccccc3333", SharedId, 7, CancellationToken.None);
        Assert.Equal(PrWorkerOutcome.Refused, third.Outcome);
        Assert.Contains("cap", third.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, rig.Engine.Spawns.Count); // and nothing was started for it
    }

    // ===================== verification resolution =====================

    /// <summary>
    /// The verify leg, through the daemon's OWN resolver — the function
    /// <c>MergeQueueProvisioner.resolveContainerId</c> is wired to. Each repo's queue resolves its own
    /// <c>pr-7</c>'s jail and NOTHING for the other's, which is the guard whose comment ("so one repo's
    /// queue can never reach into another's container even if agent ids ever collide") anticipated exactly
    /// this collision. A null answer is not a soft failure: the queue turns it into a refusal to verify,
    /// because host execution is a rejection trigger.
    /// </summary>
    [Fact]
    public void VerificationJail_ResolvesEachReposOwnContainer_AndNeverTheOthers()
    {
        var store = new AgentSessionStore(new InMemoryAuditLog());
        store.Spawn("external-pr", AgentRoles.Managed, agentId: SharedId, repoHash: RepoA);
        store.AttachSandbox(SharedId, "ctr-a", RepoA);

        // Only repo A has a session so far: repo B's queue must NOT be handed repo A's container.
        Assert.Equal("ctr-a", GatewayServiceRegistration.ResolveVerificationJail(store, RepoA, SharedId));
        Assert.Null(GatewayServiceRegistration.ResolveVerificationJail(store, RepoB, SharedId));

        store.Spawn("external-pr", AgentRoles.Managed, agentId: SharedId, repoHash: RepoB);
        store.AttachSandbox(SharedId, "ctr-b", RepoB);

        // Both live: each resolves its own, and they are different containers.
        Assert.Equal("ctr-a", GatewayServiceRegistration.ResolveVerificationJail(store, RepoA, SharedId));
        Assert.Equal("ctr-b", GatewayServiceRegistration.ResolveVerificationJail(store, RepoB, SharedId));
    }

    // ===================== the already-repo-scoped substrate =====================

    /// <summary>
    /// Nothing downstream of the session was relying on the id being globally unique to keep two repos
    /// apart: the container name, the MG-36 network segment derived from it and the per-agent package
    /// cache are all functions of (repoHash, agentId) already. Asserted against the SHIPPED naming
    /// functions, so a future change that drops the repo from any of them fails here.
    /// </summary>
    [Fact]
    public void TheSubstratesNames_AreAlreadyPerRepo_ForOneSharedAgentId()
    {
        var containerA = ContainerSpecBuilder.ContainerName(RepoA, SharedId);
        var containerB = ContainerSpecBuilder.ContainerName(RepoB, SharedId);
        Assert.NotEqual(containerA, containerB);
        Assert.Contains(SharedId, containerA, StringComparison.Ordinal); // the id is still in the name…
        Assert.DoesNotContain(RepoB[..12], containerA, StringComparison.Ordinal); // …but so is the repo

        Assert.NotEqual(
            EgressProxyConfigurator.AgentSegmentName(RepoA, SharedId),
            EgressProxyConfigurator.AgentSegmentName(RepoB, SharedId));

        const string VmRoot = "/home/mainguard/mainguard";
        Assert.NotEqual(
            PackageCachePolicy.AgentCachePath(VmRoot, RepoA, SharedId),
            PackageCachePolicy.AgentCachePath(VmRoot, RepoB, SharedId));
    }

    // ===================== rig =====================

    /// <summary>
    /// The production <see cref="AgentSpawnService"/> / <see cref="SandboxAgentLauncher"/> /
    /// <see cref="ExternalPrWorkerHost"/> over a substrate whose repos are temp directories and whose
    /// jails are recorded strings — the whole intake spawn chain, minus Docker. Both repos are
    /// "provisioned" (a bare-repo directory exists), which is what makes the launcher take its real jail
    /// path rather than degrading to a session-only record.
    /// </summary>
    private sealed class ScopingRig : IDisposable
    {
        private readonly DaemonFixture _daemon = new();
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;
        private readonly string _root;

        public ScopingRig(int maxActiveWorkers = 8)
        {
            _root = Path.Combine(Path.GetTempPath(), "mg-scoping-" + Guid.NewGuid().ToString("N")[..8]);
            foreach (var repo in new[] { RepoA, RepoB, "cccccccccccc3333" })
            {
                Directory.CreateDirectory(Path.Combine(_root, "repos", repo)); // "provisioned"
            }

            Engine = new RecordingEngine();
            Environment = new FakeAgentEnvironment(_root, Engine);
            _host = _daemon.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<IAgentEnvironment>(Environment)));

            Store = _host.Services.GetRequiredService<AgentSessionStore>();
            Locks = _host.Services.GetRequiredService<Mainguard.Server.Auth.TerminalLockRegistry>();
            Workers = new ExternalPrWorkerHost(
                spawns: _host.Services.GetRequiredService<AgentSpawnService>(),
                sessions: Store,
                launcher: _host.Services.GetRequiredService<SandboxAgentLauncher>(),
                admission: _host.Services.GetRequiredService<AdmissionController>(),
                limits: new CoordinatorLimits(MaxActiveWorkers: maxActiveWorkers),
                // No container runtime in this tier: every "already live" answer must come from the
                // session store, so the store is what these tests actually measure.
                resolveRunningJail: (_, _) => null,
                worktrees: Environment.Worktrees,
                audit: _host.Services.GetRequiredService<IAuditLog>(),
                loggerFactory: NullLoggerFactory.Instance);
        }

        public AgentSessionStore Store { get; }

        public Mainguard.Server.Auth.TerminalLockRegistry Locks { get; }

        public ExternalPrWorkerHost Workers { get; }

        public RecordingEngine Engine { get; }

        public FakeAgentEnvironment Environment { get; }

        public void Dispose()
        {
            _host.Dispose();
            _daemon.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Containers are strings named after the (repo, agent) they belong to — so "these are two
    /// different jails" is a fact this fake cannot fake away.</summary>
    internal sealed class RecordingEngine : ISandboxEngine
    {
        private readonly List<(string Repo, string Agent)> _spawns = new();
        private readonly List<SandboxSpawnRequest> _requests = new();
        private readonly List<string> _removed = new();

        public IReadOnlyList<(string Repo, string Agent)> Spawns
        {
            get { lock (_spawns) return _spawns.ToList(); }
        }

        /// <summary>
        /// The full spawn requests, so a test can assert what the jail was actually asked for rather than
        /// only that a spawn happened. Phase 3 needs this: the coordinator role lock is expressed entirely
        /// in the request's mount fields, and "a coordinator was spawned" says nothing about whether it was
        /// handed a worktree.
        /// </summary>
        public IReadOnlyList<SandboxSpawnRequest> Requests
        {
            get { lock (_spawns) return _requests.ToList(); }
        }

        public IReadOnlyList<string> Removed
        {
            get { lock (_removed) return _removed.ToList(); }
        }

        public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default)
        {
            lock (_spawns)
            {
                _spawns.Add((request.RepoHash, request.AgentId));
                _requests.Add(request);
            }

            return Task.FromResult(new SandboxHandle($"ctr-{request.RepoHash}-{request.AgentId}", Reused: false));
        }

        public Task<SandboxExecResult> ExecAsync(
            string containerId, IReadOnlyList<string> command, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));

        public Task PauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnpauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task StopAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string containerId, CancellationToken ct = default)
        {
            lock (_removed)
            {
                _removed.Add(containerId);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>A substrate whose repos/worktrees are temp dirs and whose jail is a recorded no-op.</summary>
    internal sealed class FakeAgentEnvironment : IAgentEnvironment
    {
        private readonly RecordingEngine _engine;

        public FakeAgentEnvironment(string root, RecordingEngine engine)
        {
            _engine = engine;
            Sandboxes = engine;
            Repos = new FakeProvisioner(root);
            Worktrees = new FakeWorktrees(root);
        }

        public string SubstrateId => "fake";

        public SubstrateCapabilities Capabilities { get; } = new(false, false, "none", "test");

        public IRepoProvisioner Repos { get; }

        public IAgentWorktreeManager Worktrees { get; }

        public ISandboxEngine Sandboxes { get; }

        public IEgressPolicy Egress { get; } = new FakeEgress();

        /// <summary>The containers the teardown path actually removed — the observable behind "repo A's
        /// release tore down repo A's jail and only repo A's".</summary>
        public IReadOnlyList<string> RemovedContainers => _engine.Removed;

        public SyncRemote ResolveSyncRemote(string repoHash) => new("fake-remote", $"fake://{repoHash}");

        private sealed class FakeProvisioner : IRepoProvisioner
        {
            private readonly string _root;

            public FakeProvisioner(string root) => _root = root;

            public ProvisionResult Provision(string windowsRepoPathNormalized) =>
                throw new NotSupportedException("not exercised");

            public string BareRepoPathFor(string repoHash) => Path.Combine(_root, "repos", repoHash);
        }

        private sealed class FakeWorktrees : IAgentWorktreeManager
        {
            private readonly string _root;

            public FakeWorktrees(string root) => _root = root;

            public string CreateAgentWorktree(string repoHash, string agentId)
            {
                var path = Path.Combine(_root, "wt", repoHash, agentId);
                Directory.CreateDirectory(path);
                return path;
            }

            public void RemoveAgentWorktree(string repoHash, string agentId, bool force)
            {
                try
                {
                    Directory.Delete(Path.Combine(_root, "wt", repoHash, agentId), recursive: true);
                }
                catch (DirectoryNotFoundException)
                {
                }
            }

            public void Prune(string repoHash)
            {
            }

            public IReadOnlyList<Mainguard.Git.Models.WorktreeItem> List(string repoHash) =>
                Array.Empty<Mainguard.Git.Models.WorktreeItem>();
        }

        private sealed class FakeEgress : IEgressPolicy
        {
            public EgressAllowlist Allowlist { get; } = EgressAllowlist.WithDefaults(new InMemoryAuditLog());

            public string NetworkName => "fake-net";

            public string ProxyUrl => "http://fake-proxy:3128";

            public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;

            public EgressVerdict Evaluate(string host) => EgressVerdict.Denied;
        }
    }
}
