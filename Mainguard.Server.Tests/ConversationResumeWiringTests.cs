using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// <b>Putting the operator back IN the conversation</b> — the half of this feature that answers the
/// report rather than the mechanism. The owner's words were <i>"i think i managed to resume an agent's
/// session, but i cant access the previous claude code conversation"</i>: a persisted transcript nobody
/// opens does not fix that, so the resumed jail's CLI has to be started with the adapter's declared
/// resume verb.
///
/// <para>Two conditions gate it and neither is sufficient alone, so each has its own test with the other
/// held fixed:</para>
/// <list type="bullet">
///   <item><b>the ADOPT path only</b> — an ordinary spawn is new work on a new branch and must start
///   clean, or a fresh agent would be dropped into a stranger's session;</item>
///   <item><b>a transcript must actually exist</b> — a resume flag with no prior session is a WORSE
///   failure than no flag: depending on the vendor the CLI either starts fresh anyway or exits at once,
///   and an agent whose CLI dies at spawn is a dead terminal with nothing saying why.</item>
/// </list>
///
/// <para>Everything is read off the request the SANDBOX ENGINE received — what a real jail would have
/// been given — never off a helper's return value, and the spawn service comes out of the daemon's own
/// container rather than being hand-assembled.</para>
/// </summary>
public sealed class ConversationResumeWiringTests
{
    private const string AgentKind = "probe-cli";
    private const string RepoHandle = "conversation-repo";
    private const string ConversationPath = ".probe/projects";
    private const string CredentialPath = ".probe/auth.json";
    private static readonly string[] ResumeArgs = { "--continue" };

    // ---- the mounts ---------------------------------------------------------------------------

    [Fact]
    public async Task EverySpawn_CarriesTheConversationStore_AsAReadWriteMountOnDaemonOwnedDisk()
    {
        using var rig = ConversationRig.Create();

        await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: string.Empty, CancellationToken.None);

        var mount = Assert.Single(rig.Engine.LastSpawn!.ConversationMounts!);
        Assert.Equal(ConversationPath, mount.HomeRelativePath);
        Assert.Equal(ContainerSpecBuilder.AgentHome + "/" + ConversationPath, mount.SandboxTarget);
        // The source is under the daemon's own conversations/ tree — not the worktree, not $HOME.
        Assert.Equal(
            Path.Combine(rig.Root, "conversations", RepoHandle, rig.LastAgentId, ".probe", "projects"),
            mount.HostPath);
        Assert.True(Directory.Exists(mount.HostPath),
            "the store must exist BEFORE the container is created — mounts are fixed at create");
    }

    [Fact]
    public async Task AnAdapterThatDeclaresNoConversationPaths_GetsNoMounts()
    {
        // Four of the five bundled adapters are in this state on purpose. It must be a supported,
        // silent outcome — not an error, and not a guessed path.
        using var rig = ConversationRig.Create(declareConversationPaths: false, declareResumeArgs: false);

        await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: string.Empty, CancellationToken.None);

        Assert.True(rig.Engine.LastSpawn!.ConversationMounts is null or { Count: 0 });
    }

    // ---- the resume verb ----------------------------------------------------------------------

    [Fact]
    public async Task AnOrdinarySpawn_NeverCarriesTheResumeVerb_EvenWithATranscriptOnDisk()
    {
        // The transcript is deliberately planted first, so a green here can only be the ADOPT gate and
        // never "there was nothing to resume".
        using var rig = ConversationRig.Create();
        rig.PlantTranscript("agent-with-history");

        await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: string.Empty, CancellationToken.None,
            agentId: "agent-with-history");

        Assert.Equal(ConversationRig.LaunchArgv, rig.Binder.LastLaunch!.Launch);
    }

    [Fact]
    public async Task AResumeWithNoSurvivingTranscript_StartsTheCLIClean()
    {
        // The store directory EXISTS by the time the decision is made — Prepare creates it on every
        // spawn — so this is precisely the case a directory-existence guard would get wrong, handing a
        // first-ever resume a flag with nothing behind it.
        using var rig = ConversationRig.Create();

        await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: string.Empty, CancellationToken.None,
            agentId: "agent-without-history", adoptExistingBranch: true);

        Assert.Equal(ConversationRig.LaunchArgv, rig.Binder.LastLaunch!.Launch);
    }

    [Fact]
    public async Task AResumeWithASurvivingTranscript_StartsTheCLIBackInTheConversation()
    {
        // THE test for the owner's report.
        using var rig = ConversationRig.Create();
        rig.PlantTranscript("agent-resumed");

        await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: string.Empty, CancellationToken.None,
            agentId: "agent-resumed", adoptExistingBranch: true);

        Assert.Equal(
            ConversationRig.LaunchArgv.Concat(ResumeArgs).ToArray(),
            rig.Binder.LastLaunch!.Launch);
    }

    [Fact]
    public async Task AResumeOfAnAdapterWithNoResumeVerb_StartsTheCLIUnchanged()
    {
        // Absent resumeArgs is a STATEMENT (this CLI cannot be told to resume), exactly like an absent
        // baseUrlEnvVar. The transcripts are still mounted; nothing is invented to "use" them.
        using var rig = ConversationRig.Create(declareResumeArgs: false);
        rig.PlantTranscript("agent-no-verb");

        await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: string.Empty, CancellationToken.None,
            agentId: "agent-no-verb", adoptExistingBranch: true);

        Assert.Equal(ConversationRig.LaunchArgv, rig.Binder.LastLaunch!.Launch);
        Assert.Single(rig.Engine.LastSpawn!.ConversationMounts!);
    }

    [Fact]
    public async Task ATranscriptFromANOTHERAgent_DoesNotResumeThisOne()
    {
        // The (repo, agent) scoping trap, on the path where it would be most confusing: one agent's
        // session must never be opened in another's jail.
        using var rig = ConversationRig.Create();
        rig.PlantTranscript("somebody-else");

        await rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: string.Empty, CancellationToken.None,
            agentId: "agent-resumed", adoptExistingBranch: true);

        Assert.Equal(ConversationRig.LaunchArgv, rig.Binder.LastLaunch!.Launch);
    }

    // ---- the invariant, at the spawn ----------------------------------------------------------

    [Fact]
    public async Task AMarkerWhoseConversationPathCouldHoldACredential_FailsTheSpawn_AndBuildsNoJail()
    {
        // The manifest parser refuses this too, but the daemon does not spawn from the manifest — it
        // spawns from an install marker in a user-writable VM path, possibly written by an older build.
        // So the gate has to hold HERE, and it has to be a typed failure rather than a filtered path.
        using var rig = ConversationRig.Create(conversationPaths: new[] { ".probe" });

        var ex = await Assert.ThrowsAsync<ConversationStoreOverlapException>(() => rig.Spawns.SpawnAsync(
            RepoHandle, AgentKind, modelApiKey: null, role: string.Empty, CancellationToken.None));

        Assert.Equal(".probe", ex.ConversationPath);
        Assert.Equal(CredentialPath, ex.CredentialPath);
        Assert.Null(rig.Engine.LastSpawn);
    }

    // ================= the rig =================

    /// <summary>
    /// An in-proc daemon over a fake substrate, with a REAL <see cref="ConversationStoreManager"/> over a
    /// temp VM root — the store is a filesystem feature, and faking it would only prove the fake works.
    /// The sandbox engine records every spawn request, and the CLI binder records the argv it was asked
    /// to run, so both halves of the decision are observed where they would really take effect.
    /// </summary>
    private sealed class ConversationRig : IDisposable
    {
        public static readonly string[] LaunchArgv = { "/opt/mainguard/adapters/bin/probe" };

        private ConversationRig(string root) => Root = root;

        public string Root { get; }

        public required WebApplicationFactory<Program> Host { get; init; }

        public required RecordingSandboxEngine Engine { get; init; }

        public required RecordingBinder Binder { get; init; }

        public AgentSpawnService Spawns => Host.Services.GetRequiredService<AgentSpawnService>();

        /// <summary>The id of the most recent spawn (ids are minted unless the caller names one).</summary>
        public string LastAgentId => Engine.LastSpawn!.AgentId;

        /// <param name="conversationPaths">Overrides the declared paths; null keeps the rig's default
        /// <see cref="ConversationPath"/>. Use <paramref name="declareConversationPaths"/> to declare
        /// NONE — a null here cannot mean both "use the default" and "declare nothing".</param>
        public static ConversationRig Create(
            IReadOnlyList<string>? conversationPaths = null,
            bool declareConversationPaths = true,
            bool declareResumeArgs = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "mg-conv-rig-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(root, "repos", RepoHandle)); // "provisioned"

            var registry = Path.Combine(root, "registry");
            Directory.CreateDirectory(registry);
            File.WriteAllText(
                Path.Combine(registry, AgentKind + ".json"),
                InstalledAdapterMarker.Serialize(new InstalledAdapterMarker(
                    AgentKind, "1.0.0", LaunchArgv,
                    CredentialPaths: new[] { CredentialPath },
                    ConversationPaths: declareConversationPaths
                        ? conversationPaths ?? new[] { ConversationPath }
                        : null,
                    ResumeArgs: declareResumeArgs ? ResumeArgs : null)));

            var engine = new RecordingSandboxEngine();
            var binder = new RecordingBinder();
            var host = new DaemonFixture().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentEnvironment>(new FakeEnvironment(root, engine));
                services.AddSingleton(new InstalledAdapterCatalog(registry));
                services.AddSingleton(sp => new AgentCliBinder(
                    sp.GetRequiredService<TerminalSessionManager>(),
                    sp.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.SessionLeader>(),
                    sp.GetRequiredService<AgentSessionStore>(),
                    sp.GetRequiredService<Mainguard.Git.Audit.IAuditLog>(),
                    binder.Observe));
            }));

            return new ConversationRig(root) { Host = host, Engine = engine, Binder = binder };
        }

        /// <summary>Writes a transcript into an agent's store exactly where a previous jail's CLI would
        /// have: <c>&lt;store&gt;/-workspace/&lt;uuid&gt;.jsonl</c>, the escaped-cwd layout the shipped
        /// CLI uses (and identical across jails, because every jail's WorkingDir is /workspace).</summary>
        public void PlantTranscript(string agentId)
        {
            var dir = Path.Combine(
                Root, "conversations", RepoHandle, agentId, ".probe", "projects", "-workspace");
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, Guid.NewGuid().ToString("D") + ".jsonl"),
                "{\"type\":\"user\",\"text\":\"where were we\"}\n");
        }

        public void Dispose()
        {
            Host.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch { /* never fail a test from cleanup */ }
        }

        /// <summary>Records the argv each CLI bind was asked to run — the observable half of the resume
        /// decision, taken where the daemon really starts the CLI.</summary>
        public sealed class RecordingBinder
        {
            private readonly ConcurrentQueue<AgentCliLaunchSpec> _launches = new();

            public AgentCliLaunchSpec? LastLaunch => _launches.LastOrDefault();

            public ITerminalSession Observe(AgentCliLaunchSpec spec)
            {
                _launches.Enqueue(spec);
                return new InertTerminalSession();
            }

            private sealed class InertTerminalSession : ITerminalSession
            {
                private readonly TaskCompletionSource<int> _exit =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);

                public Stream IO { get; } = new MemoryStream();

                public Task<int> ExitCode => _exit.Task;

                public void Resize(int cols, int rows) { }

                public void Kill() => _exit.TrySetResult(0);

                public void Dispose() => Kill();
            }
        }

        public sealed class RecordingSandboxEngine : ISandboxEngine
        {
            private readonly ConcurrentQueue<SandboxSpawnRequest> _spawns = new();

            public SandboxSpawnRequest? LastSpawn => _spawns.LastOrDefault();

            public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default)
            {
                _spawns.Enqueue(request);
                return Task.FromResult(new SandboxHandle($"ctr-{request.AgentId}", Reused: false));
            }

            public Task<SandboxExecResult> ExecAsync(
                string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
                => Task.FromResult(new SandboxExecResult(1, string.Empty, string.Empty));

            public Task PauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task UnpauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task StopAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task RemoveAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
        }

        private sealed class FakeEnvironment : IAgentEnvironment
        {
            public FakeEnvironment(string root, ISandboxEngine sandboxes)
            {
                Sandboxes = sandboxes;
                Repos = new FakeProvisioner(root);
                Worktrees = new FakeWorktrees(root);
                // The REAL manager, over this rig's VM root: what is under test is whether a store on
                // disk survives and is found again, which a stub could only assert about itself.
                ConversationStores = new ConversationStoreManager(root);
            }

            public string SubstrateId => "fake";

            public SubstrateCapabilities Capabilities { get; } = new(false, false, "none", "test");

            public ISandboxEngine Sandboxes { get; }

            public IRepoProvisioner Repos { get; }

            public IAgentWorktreeManager Worktrees { get; }

            public IEgressPolicy Egress { get; } = new FakeEgress();

            public ConversationStoreManager? ConversationStores { get; }

            public SyncRemote ResolveSyncRemote(string repoHash) => new("fake-remote", $"fake://{repoHash}");

            private sealed class FakeEgress : IEgressPolicy
            {
                public EgressAllowlist Allowlist { get; } =
                    EgressAllowlist.WithDefaults(new Mainguard.Git.Audit.InMemoryAuditLog());

                public string NetworkName => "fake-net";

                public string ProxyUrl => "http://fake-proxy:3128";

                public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;

                public EgressVerdict Evaluate(string host) => EgressVerdict.Denied;
            }

            private sealed class FakeProvisioner : IRepoProvisioner
            {
                private readonly string _root;

                public FakeProvisioner(string root) => _root = root;

                public ProvisionResult Provision(string windowsRepoPathNormalized) =>
                    throw new NotSupportedException("not exercised");

                public string BareRepoPathFor(string repoHash) => Path.Combine(_root, "repos", repoHash);
            }

            /// <summary>Adopt and create both hand back a directory here: which branch a worktree stands
            /// on is <c>WorktreeManager</c>'s own contract and is covered by its tests. What this rig has
            /// to reproduce is only that the ADOPT path was taken.</summary>
            private sealed class FakeWorktrees : IAgentWorktreeManager
            {
                private readonly string _root;

                public FakeWorktrees(string root) => _root = root;

                public string CreateAgentWorktree(string repoHash, string agentId) => Make(repoHash, agentId);

                public string AdoptAgentWorktree(string repoHash, string agentId) => Make(repoHash, agentId);

                public void RemoveAgentWorktree(string repoHash, string agentId, bool force)
                {
                    try { Directory.Delete(Path.Combine(_root, "wt", repoHash, agentId), recursive: true); }
                    catch (DirectoryNotFoundException) { }
                }

                public void Prune(string repoHash) { }

                public IReadOnlyList<Mainguard.Git.Models.WorktreeItem> List(string repoHash) =>
                    Array.Empty<Mainguard.Git.Models.WorktreeItem>();

                private string Make(string repoHash, string agentId)
                {
                    var path = Path.Combine(_root, "wt", repoHash, agentId);
                    Directory.CreateDirectory(path);
                    return path;
                }
            }
        }
    }
}
