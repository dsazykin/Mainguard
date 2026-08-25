using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// <b>Resuming a stranded entry, against a real jail.</b>
///
/// <para>The reported state, in the owner's words: <i>"since i previously did work with agents their work
/// still shows up in the merge queue but i cant access it at all."</i> The commits are on
/// <c>agent/&lt;id&gt;</c>; the jail is gone; verification runs only in the worker's own sandbox, so the
/// entry can never become <c>Verified</c> and can never merge. Until this change the single offered action
/// threw the work away.</para>
///
/// <para><b>Nothing about the decisive test is simulated except how the jail died.</b> The daemon is the
/// real in-proc composition root, the queue is the one <c>MergeQueueProvisioner</c> builds on
/// <c>ProvisionRepo</c>, the jail is spawned by the shipped chain, the commits are real commits in the real
/// worktree, and the verification is a real <c>docker exec</c> of the command read out of
/// <c>.mainguard/verify</c>. Every assertion is read off the <b>daemon's</b> state — the session store, the
/// queue's own state machine, the mirror's refs — never off a ViewModel and never off "the RPC returned
/// true", which is exactly what a resume faked in the client would also produce.</para>
///
/// <para><b>How the stranding is constructed.</b> The container is removed through the engine and the
/// daemon's session record is dropped, which is the state a VM stop or a daemon crash leaves: no jail, no
/// session, worktree and <c>agent/&lt;id&gt;</c> still on disk, queue entry still persisted. Deliberately
/// NOT the <c>StopAgent</c> RPC — a clean stop deletes the branch, so it produces a different (and
/// genuinely unrecoverable) state, which the branch-is-gone case asserts separately.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public sealed class QueueEntryResumeDockerTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ProjectionWait = TimeSpan.FromSeconds(30);

    private readonly List<string> _containers = new();
    private readonly List<(string RepoHash, string AgentId)> _segments = new();
    private readonly List<string> _dirs = new();

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// <b>The decisive case.</b> A stranded entry — jail destroyed, branch intact — is resumed, gets a live
    /// sandbox, verifies IN THAT JAIL, and becomes mergeable. The entry keeps its identity throughout: same
    /// agent id, same branch, same row.
    /// </summary>
    [RequiresDockerFact]
    public async Task AStrandedEntry_IsResumed_VerifiesInItsNewJail_AndBecomesMergeable()
    {
        await using var world = await ResumeWorld.BuildAsync(this);

        // ---- a real agent does real work ----
        var agent = await world.SpawnJailedAgentAsync();
        var firstContainer = world.ContainerFor(agent.Id);
        var tip = world.Commit(agent, "src/calc.js",
            FixtureRepo.CalcJs + "exports.sub = (a, b) => a - b;\n", "feat: subtraction");
        await world.WaitForQueueProjectionAsync(agent.Id);

        // ---- …and then its jail dies ----
        await world.StrandAsync(agent.Id);

        // The entry is exactly the dead end the screenshot showed: present, Working, no sandbox, and a
        // Verify that can only ever answer with the refusal.
        var stranded = (await world.SnapshotAsync()).Entries.Single(e => e.AgentId == agent.Id);
        Assert.Equal(WorkerMergeState.Working.ToString(), stranded.State);
        Assert.True(stranded.HasHasLiveSandbox);
        Assert.False(stranded.HasLiveSandbox);
        Assert.False(stranded.CanMerge);

        var refused = await Assert.ThrowsAsync<RpcException>(() => world.RunVerificationAsync(agent.Id));
        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("no live sandbox", refused.Status.Detail);

        // ---- resume ----
        var resumed = await world.ResumeAsync(agent.Id);
        Assert.True(resumed.Resumed, resumed.Reason);
        // ADOPTION, not re-creation: the same id and the same branch, never a newly minted entry.
        Assert.Equal(agent.Id, resumed.AgentId);
        Assert.Equal("agent/" + agent.Id, resumed.Branch);
        Assert.False(resumed.ClearedStalledVerification); // it was Working; nothing to retract

        // The daemon — not a ViewModel — now holds a session with a REAL and DIFFERENT container for this
        // (repo, agent). A resume that reported success without building a jail fails here.
        var secondContainer = world.ContainerFor(agent.Id);
        Assert.False(string.IsNullOrEmpty(secondContainer));
        Assert.NotEqual(firstContainer, secondContainer);

        // …and the new jail's worktree is standing on the entry's own branch, at the commit the dead jail
        // made. `worktree add -b` (a fresh branch off main) would fail every line of this.
        Assert.Equal("agent/" + agent.Id, world.Git(agent.WorktreePath, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal(tip, world.Git(agent.WorktreePath, "rev-parse", "HEAD"));
        Assert.Contains("exports.sub", File.ReadAllText(Path.Combine(agent.WorktreePath, "src", "calc.js")));
        Assert.Equal(tip, world.Git(world.MirrorPath, "rev-parse", "refs/heads/agent/" + agent.Id));

        // ---- and now it can do the thing it could not do while stranded ----
        var live = (await world.SnapshotAsync()).Entries.Single(e => e.AgentId == agent.Id);
        Assert.True(live.HasLiveSandbox);

        var verified = await world.RunVerificationAsync(agent.Id);
        Assert.True(verified.Passed, "the resumed jail must be able to run the repo's real verify command");
        Assert.Equal(FixtureRepo.VerifyCommand, verified.ResolvedCommand);
        Assert.Equal(WorkerMergeState.Verified.ToString(), verified.State);

        // Mergeable, asserted on the DAEMON's gate rather than on any client projection.
        var canMerge = await world.CanMergeAsync(agent.Id);
        Assert.True(canMerge.CanMerge, canMerge.Reason);

        // The action is audited: who resumed what, out of which state.
        var audited = Assert.Single(
            world.Audit, e => e.Type == AgentResumeService.ResumedEvent && e.Fields["agent"] == agent.Id);
        Assert.Equal(world.RepoHandle, audited.Fields["repo"]);
        Assert.Equal(WorkerMergeState.Working.ToString(), audited.Fields["from_state"]);
        Assert.Equal("agent/" + agent.Id, audited.Fields["branch"]);
        Assert.False(string.IsNullOrWhiteSpace(audited.Fields["by"]));
    }

    /// <summary>
    /// The branch-is-gone case, honestly. A clean <c>StopAgent</c> deletes <c>agent/&lt;id&gt;</c> with the
    /// worktree, so there is nothing left to resume — and the one behaviour that would be worse than the
    /// refusal is a jail started on a fresh empty branch under the old name, reported as success.
    /// </summary>
    [RequiresDockerFact]
    public async Task WhenTheBranchIsGoneToo_ResumeRefusesAndNamesIt_AndBuildsNoJail()
    {
        await using var world = await ResumeWorld.BuildAsync(this);

        var agent = await world.SpawnJailedAgentAsync();
        world.Commit(agent, "src/calc.js",
            FixtureRepo.CalcJs + "exports.sub = (a, b) => a - b;\n", "feat: subtraction");
        await world.WaitForQueueProjectionAsync(agent.Id);

        // The clean stop: jail, worktree AND branch. The queue entry survives it, as it always has — which
        // is the whole reason a stranded entry exists to be acted on at all.
        await world.AgentRpc.StopAgentAsync(new StopAgentRequest { AgentId = agent.Id }, world.Headers);
        Assert.Contains(agent.Id, world.Queue.Agents);
        Assert.NotEqual(0, world.GitCode(world.MirrorPath, "rev-parse", "--verify", "--quiet",
            "refs/heads/agent/" + agent.Id));

        var response = await world.ResumeAsync(agent.Id);

        Assert.False(response.Resumed);
        Assert.Contains("no longer exists", response.Reason);
        Assert.Contains("discard the entry instead", response.Reason);

        // Nothing was built and nothing was invented: no session, no jail, and no branch conjured out of
        // main under the entry's name.
        Assert.Null(world.SessionFor(agent.Id));
        Assert.False(Directory.Exists(agent.WorktreePath));
        Assert.NotEqual(0, world.GitCode(world.MirrorPath, "rev-parse", "--verify", "--quiet",
            "refs/heads/agent/" + agent.Id));
    }

    // ================= the world =================

    /// <summary>
    /// A real daemon over an isolated VM root, a real provisioned repo, and real jails. Deliberately its
    /// own small world rather than a knob on the end-to-end merge suite: the state under test is one that
    /// suite never constructs (a jail that died without its entry being torn down), and threading it in
    /// there would make a 1200-line file answer two questions.
    /// </summary>
    private sealed class ResumeWorld : IAsyncDisposable
    {
        private readonly QueueEntryResumeDockerTests _owner;
        private WebApplicationFactory<Program> _host = null!;

        private ResumeWorld(QueueEntryResumeDockerTests owner) => _owner = owner;

        public string VmRoot { get; private set; } = "";
        public string Checkout { get; private set; } = "";
        public string RepoHandle { get; private set; } = "";
        public string MirrorPath { get; private set; } = "";
        public Metadata Headers { get; private set; } = null!;
        public AgentService.AgentServiceClient AgentRpc { get; private set; } = null!;
        public MergeQueueService.MergeQueueServiceClient Merge { get; private set; } = null!;
        public RepoSyncService.RepoSyncServiceClient Sync { get; private set; } = null!;

        /// <summary>The daemon's OWN queue for this repo — built by its <c>MergeQueueProvisioner</c> on
        /// <c>ProvisionRepo</c>, never hand-registered here.</summary>
        public MergeQueue Queue => _host.Services.GetRequiredService<MergeQueueRegistry>()
            .Resolve(RepoHandle)!.Queue;

        public IReadOnlyList<AuditEvent> Audit => _host.Services.GetRequiredService<IAuditLog>().Read();

        public static async Task<ResumeWorld> BuildAsync(QueueEntryResumeDockerTests owner)
        {
            var world = new ResumeWorld(owner);
            await world.BuildAsync();
            return world;
        }

        private async Task BuildAsync()
        {
            VmRoot = _owner.NewDir("mg-resume-vm-");
            Checkout = _owner.NewDir("mg-resume-checkout-");

            AgentTestGit.RunChecked(Checkout, "-c", "init.defaultBranch=main", "init");
            AgentTestGit.RunChecked(Checkout, "config", "user.name", "T");
            AgentTestGit.RunChecked(Checkout, "config", "user.email", "t@mainguard.local");
            AgentTestGit.RunChecked(Checkout, "config", "commit.gpgsign", "false");
            FixtureRepo.Seed(Checkout);
            AgentTestGit.RunChecked(Checkout, "add", "-A");
            AgentTestGit.RunChecked(Checkout, "commit", "-m", "seed: node fixture project");
            AgentTestGit.RunChecked(Checkout, "branch", "-M", "main");

            // The real composition root, with ONE override: an isolated VM root so mirrors and worktrees
            // never touch the developer's ~/mainguard.
            _host = new DaemonFixture().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
                services.AddSingleton<IAgentEnvironment>(new Wsl2AgentEnvironment(vmRoot: VmRoot))));

            var channel = GrpcChannel.ForAddress(_host.Server.BaseAddress,
                new GrpcChannelOptions { HttpHandler = _host.Server.CreateHandler() });
            Headers = new Metadata
            {
                { "authorization", $"bearer {_host.Services.GetRequiredService<SessionTokenFile>().Token}" },
            };
            AgentRpc = new AgentService.AgentServiceClient(channel);
            Merge = new MergeQueueService.MergeQueueServiceClient(channel);
            Sync = new RepoSyncService.RepoSyncServiceClient(channel);

            var provisioned = await Sync.ProvisionRepoAsync(
                new ProvisionRepoRequest { OriginUrl = Checkout }, Headers);
            RepoHandle = provisioned.RepoHandle;
            MirrorPath = _host.Services.GetRequiredService<IAgentEnvironment>().Repos.BareRepoPathFor(RepoHandle);
        }

        // ---- agents ----

        public async Task<JailedAgent> SpawnJailedAgentAsync()
        {
            var spawned = await AgentRpc.SpawnAgentAsync(new SpawnAgentRequest
            {
                RepoHandle = RepoHandle,
                AgentKind = "worker",
                ModelApiKey = "sk-test-not-a-real-key",
                TaskPrompt = "resume fixture",
            }, Headers);

            var session = SessionFor(spawned.AgentId);
            Assert.NotNull(session);
            Assert.False(string.IsNullOrEmpty(session!.ContainerId),
                "the spawn must produce a real jail — verification runs in the worker's own sandbox");
            _owner._containers.Add(session.ContainerId!);
            _owner._segments.Add((RepoHandle, spawned.AgentId));

            return new JailedAgent(spawned.AgentId, Path.Combine(VmRoot, "worktrees", RepoHandle, spawned.AgentId));
        }

        public AgentSession? SessionFor(string agentId)
            => _host.Services.GetRequiredService<AgentSessionStore>()
                .Find(new AgentSessionKey(RepoHandle, agentId));

        public string? ContainerFor(string agentId) => SessionFor(agentId)?.ContainerId;

        /// <summary>
        /// Destroys the jail and forgets the session, leaving the worktree, the per-agent repository and
        /// <c>agent/&lt;id&gt;</c> exactly where they are — a VM stop or a daemon crash, not a stop.
        /// </summary>
        public async Task StrandAsync(string agentId)
        {
            var containerId = ContainerFor(agentId);
            Assert.False(string.IsNullOrEmpty(containerId));
            await _host.Services.GetRequiredService<IAgentEnvironment>().Sandboxes
                .RemoveAsync(containerId!, CancellationToken.None);
            _host.Services.GetRequiredService<AgentSessionStore>().Stop(new AgentSessionKey(RepoHandle, agentId));

            Assert.Null(SessionFor(agentId));
            // …and the two things a resume needs are still there.
            Assert.True(Directory.Exists(Path.Combine(VmRoot, "worktrees", RepoHandle, agentId)));
            Assert.Equal(0, GitCode(MirrorPath, "rev-parse", "--verify", "--quiet", "refs/heads/agent/" + agentId));
        }

        // ---- drive ----

        public Task<RunVerificationResponse> RunVerificationAsync(string agentId)
            => Merge.RunVerificationAsync(
                new RunVerificationRequest { RepoHandle = RepoHandle, AgentId = agentId }, Headers).ResponseAsync;

        public Task<CanMergeResponse> CanMergeAsync(string agentId)
            => Merge.CanMergeAsync(
                new CanMergeRequest { RepoHandle = RepoHandle, AgentId = agentId }, Headers).ResponseAsync;

        public Task<ResumeAgentResponse> ResumeAsync(string agentId)
            => AgentRpc.ResumeAgentAsync(new ResumeAgentRequest
            {
                RepoHandle = RepoHandle,
                AgentId = agentId,
                AgentKind = "worker",
                ModelApiKey = "sk-test-not-a-real-key",
            }, Headers).ResponseAsync;

        public async Task<QueueUpdate> SnapshotAsync()
        {
            using var cts = new CancellationTokenSource(ProjectionWait);
            using var call = Merge.StreamQueue(
                new StreamQueueRequest { RepoHandle = RepoHandle }, Headers, cancellationToken: cts.Token);
            Assert.True(await call.ResponseStream.MoveNext(cts.Token), "the queue stream produced no snapshot");
            return call.ResponseStream.Current;
        }

        public async Task WaitForQueueProjectionAsync(string agentId)
        {
            var deadline = DateTime.UtcNow + ProjectionWait;
            while (DateTime.UtcNow < deadline)
            {
                if (Queue.Agents.Contains(agentId))
                {
                    return;
                }

                await Task.Delay(100);
            }

            Assert.Contains(agentId, Queue.Agents);
        }

        /// <summary>Commits in the agent's worktree the way its CLI would, and returns the new tip.</summary>
        public string Commit(JailedAgent agent, string relPath, string content, string message)
        {
            var full = Path.Combine(agent.WorktreePath, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            AgentTestGit.RunChecked(agent.WorktreePath, "add", "-A");
            AgentTestGit.RunChecked(agent.WorktreePath,
                "-c", "user.name=agent", "-c", "user.email=agent@mainguard.local", "-c", "commit.gpgsign=false",
                "commit", "-m", message);
            return Git(agent.WorktreePath, "rev-parse", "HEAD");
        }

        public string Git(string repo, params string[] args) => AgentTestGit.RunChecked(repo, args).Trim();

        public int GitCode(string repo, params string[] args) => AgentTestGit.Run(repo, args).Code;

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            try { _host?.Dispose(); } catch { /* never fail a test from cleanup */ }
        }
    }

    private sealed record JailedAgent(string Id, string WorktreePath);

    // ================= helpers =================

    private string NewDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _dirs.Add(path);
        return path;
    }

    public async Task DisposeAsync()
    {
        using var docker = Mainguard.Agents.Agents.Sandbox.DockerEndpointResolver.CreateClient();
        foreach (var id in _containers)
        {
            try { await docker.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }); }
            catch { /* never fail a test from cleanup */ }
        }

        // MG-36: Docker's default local bridge pool is only ~32 networks deep, so a suite that leaks one
        // segment per agent fails EVERY later spawn on the same box with an address-pool error that has
        // nothing to do with the test that hits it.
        var egress = new EgressProxyConfigurator(docker, EgressAllowlist.WithDefaults(new InMemoryAuditLog()));
        foreach (var (repoHash, agentId) in _segments)
        {
            try { await egress.RemoveAgentSegmentAsync(repoHash, agentId); }
            catch { /* never fail a test from cleanup */ }
        }

        // A resume creates a SECOND jail for the same (repo, agent), so the by-name sweep above cannot be
        // the only reclamation — sweep every empty agent segment this box has left over.
        try
        {
            foreach (var net in await docker.Networks.ListNetworksAsync())
            {
                if (net.Name is null
                    || !net.Name.StartsWith(EgressProxyConfigurator.AgentSegmentPrefix, StringComparison.Ordinal)
                    || net.Containers is { Count: > 0 })
                {
                    continue;
                }

                try { await docker.Networks.DeleteNetworkAsync(net.ID); }
                catch { /* another suite's live segment — leave it alone */ }
            }
        }
        catch { /* never fail a test from cleanup */ }

        foreach (var dir in _dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(dir, recursive: true);
            }
            catch { /* never fail a test from cleanup */ }
        }
    }
}
