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
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Sandbox;
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
/// <b>The conversation survives a jail that dies without a clean stop.</b>
///
/// <para>This is the whole feature, and the reason it is a bind mount rather than a harvest-on-stop
/// round trip like <c>credentialPaths</c>. The event that makes an operator need the conversation back
/// is the jail dying WITHOUT a clean stop — a VM crash, a <c>docker rm</c>, a WSL restart — which is the
/// definition of a stranded queue entry, which is what resume exists for. Harvest runs inside
/// <c>StopAgent</c>, so in the crash case it never runs at all: a harvest-based design would pass every
/// test that stops the agent and fail in every situation a user actually hits.</para>
///
/// <para><b>So the decisive test models the crash, not the stop.</b> The container is removed through the
/// engine and the daemon's session record is dropped — no <c>StopAgent</c> RPC anywhere in it — exactly
/// as <c>QueueEntryResumeDockerTests</c> constructs the same state. A test that tore the agent down
/// politely would be testing the one path this design does not depend on.</para>
///
/// <para><b>Why Docker and not a fake.</b> Three facts here are only observable against a real engine and
/// each has silently broken a neighbouring feature before: the jail's <c>$HOME</c> is a 256 MiB tmpfs
/// mounted over the image's home, the store is a bind mount applied UNDERNEATH that tmpfs at a deeper
/// path, and the writes come from the agent uid through a userns remap. A fake engine would assert the
/// shape of the mount while proving nothing about whether the CLI's bytes reach ext4.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public sealed class ConversationPersistenceDockerTests : IAsyncLifetime
{
    /// <summary>The adapter kind whose marker declares what is persisted. A probe CLI rather than
    /// claude-code: the daemon must persist whatever an adapter DECLARES, and pinning the real vendor's
    /// paths here would make this test about one CLI's layout instead of about the mechanism.</summary>
    private const string AgentKind = "probe-cli";

    /// <summary>The declared conversation directory. Nested, so the store's <c>mkdir -p</c> of the
    /// intermediate directory is exercised rather than assumed.</summary>
    private const string ConversationPath = ".probe/projects";

    /// <summary>The declared credential file — deliberately a SIBLING of the conversation directory, so
    /// the overlap rule is being satisfied by a real declaration rather than by there being nothing to
    /// overlap with.</summary>
    private const string CredentialPath = ".probe/auth.json";

    private static readonly TimeSpan ProjectionWait = TimeSpan.FromSeconds(30);

    private readonly List<string> _containers = new();
    private readonly List<(string RepoHash, string AgentId)> _segments = new();
    private readonly List<string> _dirs = new();

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// <b>The decisive case.</b> A CLI writes its transcript inside a real jail; the jail is destroyed the
    /// way a crash destroys it; the transcript is still on daemon-owned disk afterwards, and the resumed
    /// jail can read it back byte for byte at the same path.
    /// </summary>
    [RequiresDockerFact]
    public async Task ATranscriptWrittenInAJail_SurvivesAJailThatDiesWithoutACleanStop_AndIsReadableAfterResume()
    {
        await using var world = await ConversationWorld.BuildAsync(this);

        // A per-run nonce so neither a leftover container nor a file that happened to already be there
        // can satisfy the final assertion.
        var transcript = "{\"type\":\"user\",\"text\":\"" + Guid.NewGuid().ToString("N") + "\"}";

        // ---- a real agent does real work, and its CLI writes a real transcript ----------------------
        var agent = await world.SpawnJailedAgentAsync();
        var firstContainer = world.ContainerFor(agent.Id)!;
        world.Commit(agent, "src/calc.js",
            FixtureRepo.CalcJs + "exports.sub = (a, b) => a - b;\n", "feat: subtraction");
        await world.WaitForQueueProjectionAsync(agent.Id);

        // Written from INSIDE the jail, as the agent uid, at the exact path the CLI uses: the escaped-cwd
        // directory under the declared conversation path. `/workspace` escapes to `-workspace`, and it is
        // the same in every jail for this agent — which is what makes a remount (rather than a copy)
        // enough to line the history up again.
        var wrote = await world.ExecAsync(firstContainer,
            "sh", "-c",
            $"mkdir -p '{JailDir}' && printf '%s' '{transcript}' > '{JailFile}'");
        Assert.True(wrote.ExitCode == 0,
            $"the jail must be able to write its own transcript: exit={wrote.ExitCode} stderr={wrote.Stderr}");

        // ---- …and then its jail dies. NO StopAgent anywhere. ----------------------------------------
        await world.StrandAsync(agent.Id);

        // The bytes are on the daemon's ext4, with nothing having run at teardown — because nothing had
        // to. This single assertion is what a harvest-on-stop design could not make.
        var hostFile = Path.Combine(
            world.VmRoot, "conversations", world.RepoHandle, agent.Id, ".probe", "projects",
            "-workspace", "session.jsonl");
        Assert.True(File.Exists(hostFile),
            $"the transcript must outlive the container on daemon-owned disk; nothing was at '{hostFile}'");
        Assert.Equal(transcript, File.ReadAllText(hostFile));

        // ---- resume: a NEW container, standing on the same branch AND the same conversation ----------
        var resumed = await world.ResumeAsync(agent.Id);
        Assert.True(resumed.Resumed, resumed.Reason);

        var secondContainer = world.ContainerFor(agent.Id)!;
        Assert.NotEqual(firstContainer, secondContainer);

        // Read back through the NEW jail, framed rather than substring-matched: an exec that failed to
        // run prints no frame at all and would otherwise be indistinguishable from a mismatch.
        var read = await world.ExecAsync(secondContainer,
            "sh", "-c", $"printf 'BEGIN['; cat '{JailFile}' 2>/dev/null; printf ']END'");
        Assert.Equal(0, read.ExitCode);
        Assert.Equal($"BEGIN[{transcript}]END", read.Stdout.Trim());
    }

    /// <summary>
    /// The store is per (repo, agent) and reachable by exactly one jail. A second agent in the same
    /// repository must not see the first one's conversation — a transcript is the most sensitive thing an
    /// agent produces (the repository's code, the operator's prompts, everything the CLI read), so a
    /// shared store would be a cross-tenant read of all of it.
    /// </summary>
    [RequiresDockerFact]
    public async Task OneAgentsConversation_IsNotVisibleInsideAnotherAgentsJail()
    {
        await using var world = await ConversationWorld.BuildAsync(this);

        var first = await world.SpawnJailedAgentAsync();
        var wrote = await world.ExecAsync(world.ContainerFor(first.Id)!,
            "sh", "-c", $"mkdir -p '{JailDir}' && printf 'private' > '{JailFile}'");
        Assert.Equal(0, wrote.ExitCode);

        var second = await world.SpawnJailedAgentAsync();
        var read = await world.ExecAsync(world.ContainerFor(second.Id)!,
            "sh", "-c", $"printf 'BEGIN['; cat '{JailFile}' 2>/dev/null; printf ']END'");

        Assert.Equal("BEGIN[]END", read.Stdout.Trim());
    }

    /// <summary>
    /// The lifecycle decision, measured rather than described: a clean <c>StopAgent</c> deletes the
    /// agent's branch, and the conversation goes with it.
    ///
    /// <para>Keeping it would not preserve continuity — the work the conversation is ABOUT is gone — it
    /// would build a trap. Agent ids are unique per repo and not globally, and the external-PR intake's
    /// <c>pr-&lt;n&gt;</c> ids RECUR, so a later <c>pr-7</c> for a different pull request would mount, and
    /// resume into, the previous author's session: a wrong answer and a disclosure at once.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task ACleanStop_TakesTheConversationWithTheBranch()
    {
        await using var world = await ConversationWorld.BuildAsync(this);

        var agent = await world.SpawnJailedAgentAsync();
        var wrote = await world.ExecAsync(world.ContainerFor(agent.Id)!,
            "sh", "-c", $"mkdir -p '{JailDir}' && printf 'finished work' > '{JailFile}'");
        Assert.Equal(0, wrote.ExitCode);

        var storeDir = Path.Combine(world.VmRoot, "conversations", world.RepoHandle, agent.Id);
        Assert.True(Directory.Exists(storeDir), "the store must exist before the stop, or this proves nothing");

        await world.AgentRpc.StopAgentAsync(new StopAgentRequest { AgentId = agent.Id }, world.Headers);

        Assert.False(Directory.Exists(storeDir),
            "a clean stop deletes agent/<id>; the conversation about that branch goes with it, or a later "
            + "agent reusing the id would resume into a stranger's session");
    }

    private static string JailDir =>
        ContainerSpecBuilder.AgentHome + "/" + ConversationPath + "/-workspace";

    private static string JailFile => JailDir + "/session.jsonl";

    // ================= the world =================

    /// <summary>
    /// A real daemon over an isolated VM root, a real provisioned repo, real jails — the same shape as
    /// <c>QueueEntryResumeDockerTests.ResumeWorld</c>, with ONE addition: an adapter registry whose
    /// marker declares a conversation path, because the daemon persists what an adapter DECLARES and a
    /// kind with no marker declares nothing.
    /// </summary>
    private sealed class ConversationWorld : IAsyncDisposable
    {
        private readonly ConversationPersistenceDockerTests _owner;
        private WebApplicationFactory<Program> _host = null!;

        private ConversationWorld(ConversationPersistenceDockerTests owner) => _owner = owner;

        public string VmRoot { get; private set; } = "";

        public string Checkout { get; private set; } = "";

        public string RepoHandle { get; private set; } = "";

        public string MirrorPath { get; private set; } = "";

        public Metadata Headers { get; private set; } = null!;

        public AgentService.AgentServiceClient AgentRpc { get; private set; } = null!;

        public MergeQueueService.MergeQueueServiceClient Merge { get; private set; } = null!;

        public RepoSyncService.RepoSyncServiceClient Sync { get; private set; } = null!;

        public Mainguard.Agents.Agents.Orchestrator.MergeQueue Queue =>
            _host.Services.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.MergeQueueRegistry>()
                .Resolve(RepoHandle)!.Queue;

        public static async Task<ConversationWorld> BuildAsync(ConversationPersistenceDockerTests owner)
        {
            var world = new ConversationWorld(owner);
            await world.BuildAsync();
            return world;
        }

        private async Task BuildAsync()
        {
            VmRoot = _owner.NewDir("mg-conv-vm-");
            Checkout = _owner.NewDir("mg-conv-checkout-");

            AgentTestGit.RunChecked(Checkout, "-c", "init.defaultBranch=main", "init");
            AgentTestGit.RunChecked(Checkout, "config", "user.name", "T");
            AgentTestGit.RunChecked(Checkout, "config", "user.email", "t@mainguard.local");
            AgentTestGit.RunChecked(Checkout, "config", "commit.gpgsign", "false");
            FixtureRepo.Seed(Checkout);
            AgentTestGit.RunChecked(Checkout, "add", "-A");
            AgentTestGit.RunChecked(Checkout, "commit", "-m", "seed: node fixture project");
            AgentTestGit.RunChecked(Checkout, "branch", "-M", "main");

            // The adapter registry: `<dir>/registry/<kind>.json`, so the catalog's derived Root is a real
            // directory the spawn path can bind-mount read-only (it mounts the catalog's OWN root, not a
            // fixed VM path).
            var adaptersRoot = _owner.NewDir("mg-conv-adapters-");
            var registry = Path.Combine(adaptersRoot, "registry");
            Directory.CreateDirectory(registry);
            File.WriteAllText(
                Path.Combine(registry, AgentKind + ".json"),
                InstalledAdapterMarker.Serialize(new InstalledAdapterMarker(
                    AgentKind, "1.0.0",
                    // Long-lived, so the jail's CLI does not exit the instant it is bound.
                    new[] { "/bin/sh", "-c", "sleep 3600" },
                    CredentialPaths: new[] { CredentialPath },
                    ConversationPaths: new[] { ConversationPath },
                    ResumeArgs: new[] { "--continue" })));

            // The real composition root, with TWO overrides: an isolated VM root (so stores, mirrors and
            // worktrees never touch the developer's ~/mainguard) and this registry.
            _host = new DaemonFixture().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentEnvironment>(new Wsl2AgentEnvironment(vmRoot: VmRoot));
                services.AddSingleton(new InstalledAdapterCatalog(registry));
            }));

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

        public async Task<JailedAgent> SpawnJailedAgentAsync()
        {
            var spawned = await AgentRpc.SpawnAgentAsync(new SpawnAgentRequest
            {
                RepoHandle = RepoHandle,
                AgentKind = AgentKind,
                ModelApiKey = "sk-test-not-a-real-key",
                TaskPrompt = "conversation persistence fixture",
            }, Headers);

            var session = SessionFor(spawned.AgentId);
            Assert.NotNull(session);
            Assert.False(string.IsNullOrEmpty(session!.ContainerId),
                "the spawn must produce a real jail — the store only exists as a mount inside one");
            _owner._containers.Add(session.ContainerId!);
            _owner._segments.Add((RepoHandle, spawned.AgentId));

            return new JailedAgent(
                spawned.AgentId, Path.Combine(VmRoot, "worktrees", RepoHandle, spawned.AgentId));
        }

        public AgentSession? SessionFor(string agentId)
            => _host.Services.GetRequiredService<AgentSessionStore>()
                .Find(new AgentSessionKey(RepoHandle, agentId));

        public string? ContainerFor(string agentId) => SessionFor(agentId)?.ContainerId;

        public Task<SandboxExecResult> ExecAsync(string containerId, params string[] command)
            => _host.Services.GetRequiredService<IAgentEnvironment>().Sandboxes
                .ExecAsync(containerId, command, CancellationToken.None);

        /// <summary>
        /// Destroys the jail and forgets the session, leaving the worktree, the per-agent repository,
        /// <c>agent/&lt;id&gt;</c> AND the conversation store exactly where they are — a VM stop or a
        /// daemon crash, deliberately NOT a stop. This is the state the whole design turns on.
        /// </summary>
        public async Task StrandAsync(string agentId)
        {
            var containerId = ContainerFor(agentId);
            Assert.False(string.IsNullOrEmpty(containerId));
            await _host.Services.GetRequiredService<IAgentEnvironment>().Sandboxes
                .RemoveAsync(containerId!, CancellationToken.None);
            _host.Services.GetRequiredService<AgentSessionStore>()
                .Stop(new AgentSessionKey(RepoHandle, agentId));

            Assert.Null(SessionFor(agentId));
            Assert.Equal(0, GitCode(MirrorPath, "rev-parse", "--verify", "--quiet", "refs/heads/agent/" + agentId));
        }

        public Task<ResumeAgentResponse> ResumeAsync(string agentId)
            => AgentRpc.ResumeAgentAsync(new ResumeAgentRequest
            {
                RepoHandle = RepoHandle,
                AgentId = agentId,
                AgentKind = AgentKind,
                ModelApiKey = "sk-test-not-a-real-key",
            }, Headers).ResponseAsync;

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

        public string Commit(JailedAgent agent, string relPath, string content, string message)
        {
            var full = Path.Combine(agent.WorktreePath, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            AgentTestGit.RunChecked(agent.WorktreePath, "add", "-A");
            AgentTestGit.RunChecked(agent.WorktreePath,
                "-c", "user.name=agent", "-c", "user.email=agent@mainguard.local", "-c", "commit.gpgsign=false",
                "commit", "-m", message);
            return AgentTestGit.RunChecked(agent.WorktreePath, "rev-parse", "HEAD").Trim();
        }

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
        using var docker = new DockerClientConfiguration().CreateClient();
        foreach (var id in _containers)
        {
            try { await docker.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }); }
            catch { /* never fail a test from cleanup */ }
        }

        // MG-36: Docker's default local bridge pool is only ~32 networks deep, so a suite that leaks one
        // segment per agent fails EVERY later spawn on the same box.
        var egress = new EgressProxyConfigurator(
            docker, EgressAllowlist.WithDefaults(new Mainguard.Git.Audit.InMemoryAuditLog()));
        foreach (var (repoHash, agentId) in _segments)
        {
            try { await egress.RemoveAgentSegmentAsync(repoHash, agentId); }
            catch { /* never fail a test from cleanup */ }
        }

        // A resume creates a SECOND jail for the same (repo, agent), so the by-name sweep cannot be the
        // only reclamation — sweep every empty agent segment left over.
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
