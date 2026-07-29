using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Logging;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The v1 spawn preflight (field failure 2026-07-17, twice: a fresh MainguardEnv import AND the
/// tier-2 VM upgrade both leave the docker image store empty). BEFORE any worktree/jail is made,
/// <c>SandboxAgentLauncher</c> verifies BOTH jail images and a missing one maps to an actionable
/// <c>FailedPrecondition</c> naming exactly that image — which finally makes the egress-proxy
/// absence (previously an opaque failure inside the egress setup) actionable too. Both-present
/// proceeds to the engine untouched.
/// </summary>
public sealed class SpawnImagePreflightTests : IClassFixture<DaemonFixture>
{
    private const string RepoHandle = "repo-preflight";

    private readonly DaemonFixture _daemon;

    public SpawnImagePreflightTests(DaemonFixture daemon) => _daemon = daemon;

    [Fact]
    public async Task Spawn_BothImagesPresent_ProceedsToTheEngine()
    {
        using var rig = Rig(missingImage: null);

        var agentId = await SpawnAsync(rig);

        Assert.False(string.IsNullOrWhiteSpace(agentId));
        Assert.Equal(1, rig.Environment.Engine.SpawnCalls);

        // The Spawn category records the step sequence: launch begin → preflight ok → jail started.
        var logs = _daemon.CapturedLogs;
        Assert.Contains(logs, l => IsSpawn(l) && l.Contains("launch begin", StringComparison.Ordinal));
        Assert.Contains(logs, l => IsSpawn(l) && l.Contains("preflight ok", StringComparison.Ordinal));
        Assert.Contains(logs, l => IsSpawn(l) && l.Contains("jail started", StringComparison.Ordinal));
    }

    private static bool IsSpawn(string line) =>
        line.Contains("[" + DaemonLogCategories.Spawn + "]", StringComparison.Ordinal);

    [Fact]
    public async Task Spawn_MissingAgentBaseImage_IsFailedPrecondition_NamingItAndTheRepair()
    {
        using var rig = Rig(missingImage: "mainguard-agent-base:latest");

        var ex = await Assert.ThrowsAsync<RpcException>(() => SpawnAsync(rig));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("mainguard-agent-base", ex.Status.Detail);
        Assert.Contains("restart Mainguard", ex.Status.Detail);
        Assert.Contains("docker build", ex.Status.Detail);
        Assert.Equal(0, rig.Environment.Engine.SpawnCalls); // preflight fires before any jail work

        // The Spawn category records the preflight failure naming the missing image.
        Assert.Contains(_daemon.CapturedLogs, l =>
            IsSpawn(l) && l.Contains("preflight failed", StringComparison.Ordinal)
            && l.Contains("mainguard-agent-base", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Spawn_MissingEgressProxyImage_IsFailedPrecondition_NamingIt()
    {
        // The previously NOT-actionable path: the egress image's absence used to fail opaquely
        // inside EgressPolicy.EnsureReadyAsync, not at container-create.
        using var rig = Rig(missingImage: "mainguard-egress-proxy:latest");

        var ex = await Assert.ThrowsAsync<RpcException>(() => SpawnAsync(rig));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("mainguard-egress-proxy", ex.Status.Detail);
        Assert.DoesNotContain("mainguard-agent-base", ex.Status.Detail); // names exactly the absent one
        Assert.Equal(0, rig.Environment.Engine.SpawnCalls);
    }

    [Fact]
    public async Task Spawn_StaleAgentBaseImage_IsFailedPrecondition_NamingItAsOutdated()
    {
        // Present but version-skewed: its mainguard.image.version label ≠ the daemon's expected constant
        // (a Dockerfile change left old bytes under the same tag) — the skew presence alone can't see.
        using var rig = Rig(missingImage: null, staleImage: "mainguard-agent-base:latest");

        var ex = await Assert.ThrowsAsync<RpcException>(() => SpawnAsync(rig));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("mainguard-agent-base", ex.Status.Detail);
        Assert.Contains("outdated", ex.Status.Detail);
        Assert.Equal(0, rig.Environment.Engine.SpawnCalls);

        // The Spawn category records the preflight failure naming the STALE image.
        Assert.Contains(_daemon.CapturedLogs, l =>
            IsSpawn(l) && l.Contains("preflight failed", StringComparison.Ordinal)
            && l.Contains("mainguard-agent-base", StringComparison.Ordinal)
            && l.Contains("stale", StringComparison.Ordinal));
    }

    // ---- MG-42: the per-repo toolchain leg of the same preflight -----------------------------

    /// <summary>
    /// The wiring proof: a repo whose MAIN branch declares a toolchain is jailed from the LAYER, not
    /// from the base image. Counting spawns cannot show this — both cases spawn exactly once — so the
    /// observable is the image ref the engine was handed.
    /// </summary>
    [Fact]
    public async Task Spawn_RepoDeclaringAToolchain_JailsFromTheLayer_NotTheBaseImage()
    {
        var builder = new FakeToolchainBuilder();
        using var rig = Rig(missingImage: null, toolchains: builder, mainToolchain: "dotnet-10");

        await SpawnAsync(rig);

        Assert.Equal(1, rig.Environment.Engine.SpawnCalls);
        Assert.Equal(builder.LastBuiltDigest, rig.Environment.Engine.LastSpawnImageRef);
        Assert.NotEqual("mainguard-agent-base:latest", rig.Environment.Engine.LastSpawnImageRef);
    }

    /// <summary>
    /// The control, and the regression guard that matters most: a repo that declares NOTHING is
    /// unaffected — no layer, no build, still the digest-pinned base. Every repo in existence is in
    /// this state, so a mistake here breaks all of them.
    /// </summary>
    [Fact]
    public async Task Spawn_RepoDeclaringNoToolchain_BuildsNothing_AndJailsFromTheBase()
    {
        var builder = new FakeToolchainBuilder();
        using var rig = Rig(missingImage: null, toolchains: builder, mainToolchain: null);

        await SpawnAsync(rig);

        Assert.Empty(builder.Built);
        Assert.Equal(1, rig.Environment.Engine.SpawnCalls);
    }

    /// <summary>
    /// The loud-failure requirement. A substrate that cannot build layers must REFUSE the spawn for a
    /// repo that declared one — not jail it without the tools and let every subsequent verification
    /// fail in a way that reads like the agent's code is broken.
    /// </summary>
    [Fact]
    public async Task Spawn_DeclaredToolchainThatCannotBeProvisioned_IsFailedPrecondition_AndNeverJails()
    {
        using var rig = Rig(missingImage: null, toolchains: null, mainToolchain: "dotnet-10");

        var ex = await Assert.ThrowsAsync<RpcException>(() => SpawnAsync(rig));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("dotnet-10", ex.Status.Detail, StringComparison.Ordinal);
        Assert.Equal(0, rig.Environment.Engine.SpawnCalls);
    }

    /// <summary>...and it leaves no worktree behind, because it refuses BEFORE any worktree is made.</summary>
    [Fact]
    public async Task Spawn_DeclaredToolchainThatCannotBeProvisioned_LeavesNoWorktreeResidue()
    {
        using var rig = Rig(missingImage: null, toolchains: null, mainToolchain: "dotnet-10");

        await Assert.ThrowsAsync<RpcException>(() => SpawnAsync(rig));

        var worktreeRoot = Path.Combine(rig.TempRoot, "wt");
        Assert.True(
            !Directory.Exists(worktreeRoot) || Directory.GetFileSystemEntries(worktreeRoot).Length == 0,
            "a refused toolchain spawn left a worktree behind");
    }

    /// <summary>
    /// MG-27 continuity on the spawn path: if the base image does not resolve to a content digest,
    /// a toolchain layer is REFUSED rather than built on a mutable tag. Found by accident — the first
    /// version of this rig had a digestless engine and the provisioner refused exactly as designed.
    /// </summary>
    [Fact]
    public async Task Spawn_DeclaredToolchainOnADigestlessEngine_IsRefused_NotBuiltOnAMutableTag()
    {
        var builder = new FakeToolchainBuilder();
        using var rig = Rig(
            missingImage: null, toolchains: builder, mainToolchain: "dotnet-10", digestlessEngine: true);

        var ex = await Assert.ThrowsAsync<RpcException>(() => SpawnAsync(rig));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("digest", ex.Status.Detail, StringComparison.Ordinal);
        Assert.Empty(builder.Built);
        Assert.Equal(0, rig.Environment.Engine.SpawnCalls);
    }

    /// <summary>An uncatalogued id on MAIN is a refusal too — the closed catalog is enforced on the
    /// spawn path, not only in the pure resolver.</summary>
    [Fact]
    public async Task Spawn_MainDeclaringAnUncataloguedToolchain_IsFailedPrecondition()
    {
        using var rig = Rig(
            missingImage: null, toolchains: new FakeToolchainBuilder(), mainToolchain: "cobol-85");

        var ex = await Assert.ThrowsAsync<RpcException>(() => SpawnAsync(rig));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("cobol-85", ex.Status.Detail, StringComparison.Ordinal);
        Assert.Equal(0, rig.Environment.Engine.SpawnCalls);
    }

    /// <summary>An in-memory image store that mints a digest per build (the Docker leg is
    /// <c>ToolchainProvisioningDockerTests</c>; this one is about the LAUNCHER's wiring).</summary>
    private sealed class FakeToolchainBuilder : IToolchainImageBuilder
    {
        private readonly Dictionary<string, Dictionary<string, string>> _images = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<string>> _layers = new(StringComparer.Ordinal);

        public List<string> Built { get; } = new();

        public string? LastBuiltDigest { get; private set; }

        public Task<IReadOnlyDictionary<string, string>?> InspectLabelsAsync(string imageRef, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(
                _images.TryGetValue(imageRef, out var labels) ? labels : null);

        /// <summary>
        /// Models layer inheritance the way a real engine produces it: a built image's chain is the
        /// base's chain plus the new steps. The provisioner proves parentage from this rather than from
        /// the labels, so a fake that answered null here would make the proof vacuous.
        /// </summary>
        public Task<IReadOnlyList<string>?> RootFsLayersAsync(string imageRef, CancellationToken ct = default)
        {
            if (SandboxImageDigest.IsDigest(imageRef) && !_images.ContainsKey(imageRef) && !_layers.ContainsKey(imageRef))
            {
                // The BASE digest: the launcher resolved it off the engine, so it is not one of ours.
                return Task.FromResult<IReadOnlyList<string>?>(new[] { "sha256:base-layer" });
            }

            return Task.FromResult(_layers.TryGetValue(imageRef, out var layers) ? layers : null);
        }

        public Task<string?> ResolveDigestAsync(string imageRef, CancellationToken ct = default)
        {
            if (!_images.ContainsKey(imageRef))
            {
                return Task.FromResult<string?>(null);
            }

            var hex = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(imageRef)))
                .ToLowerInvariant();
            LastBuiltDigest = "sha256:" + hex;
            return Task.FromResult<string?>(LastBuiltDigest);
        }

        public Task BuildAsync(
            string imageRef, string dockerfile, IReadOnlyDictionary<string, string> labels, CancellationToken ct = default)
        {
            Built.Add(imageRef);
            _images[imageRef] = new Dictionary<string, string>(labels, StringComparer.Ordinal);
            var chain = new[] { "sha256:base-layer", "sha256:toolchain-" + imageRef };
            _layers[imageRef] = chain;
            _layers[DigestOf(imageRef)] = chain;
            return Task.CompletedTask;
        }

        private static string DigestOf(string imageRef) =>
            "sha256:" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(imageRef)))
                .ToLowerInvariant();
    }

    // ---- rig (SpawnErrorMappingTests' in-proc pattern, engine scripted per image) -------------

    private async Task<string> SpawnAsync(PreflightRig rig)
    {
        var client = new AgentService.AgentServiceClient(rig.Channel);
        var response = await client.SpawnAgentAsync(new SpawnAgentRequest
        {
            RepoHandle = RepoHandle,
            AgentKind = "claude-code",
            TaskPrompt = "",
            ModelApiKey = "",
            Role = AgentRoles.Coordinator,
        }, rig.Auth, deadline: DateTime.UtcNow.AddSeconds(20));
        return response.AgentId;
    }

    private PreflightRig Rig(
        string? missingImage, string? staleImage = null,
        IToolchainImageBuilder? toolchains = null, string? mainToolchain = null, bool digestlessEngine = false)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "gl-preflight-" + Guid.NewGuid().ToString("N")[..8]);
        var barePath = Path.Combine(tempRoot, "repos", RepoHandle);
        Directory.CreateDirectory(barePath); // "provisioned" → jail path

        if (mainToolchain is not null)
        {
            // MG-42 — a REAL bare mirror carrying a REAL commit on main, because the declaration is read
            // with `git show <mainBranch>:.mainguard/toolchain`. Writing the file to a plain directory
            // would prove nothing: the whole point of reading it out of git is that the value comes from
            // a committed tree in a repository no jail can write.
            SeedBareRepoWithToolchain(tempRoot, barePath, mainToolchain);
        }

        var environment = new PreflightEnvironment(tempRoot, missingImage, staleImage, toolchains, digestlessEngine);
        var host = _daemon.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IAgentEnvironment>(environment)));
        return new PreflightRig(tempRoot, host, environment);
    }

    /// <summary>Builds a bare mirror whose default branch carries <c>.mainguard/toolchain</c>.</summary>
    private static void SeedBareRepoWithToolchain(string tempRoot, string barePath, string toolchain)
    {
        var work = Path.Combine(tempRoot, "seed");
        Directory.CreateDirectory(Path.Combine(work, ".mainguard"));
        File.WriteAllText(Path.Combine(work, ".mainguard", "toolchain"), toolchain + "\n");

        LibGit2Sharp.Repository.Init(work);
        using (var repo = new LibGit2Sharp.Repository(work))
        {
            repo.Config.Set("user.name", "test-user", LibGit2Sharp.ConfigurationLevel.Local);
            repo.Config.Set("user.email", "test@mainguard.local", LibGit2Sharp.ConfigurationLevel.Local);
            LibGit2Sharp.Commands.Stage(repo, ".mainguard/toolchain");
            var sig = new LibGit2Sharp.Signature("test-user", "test@mainguard.local", DateTimeOffset.Now);
            repo.Commit("seed toolchain declaration", sig, sig, new LibGit2Sharp.CommitOptions());
        }

        Directory.Delete(barePath, recursive: true);
        LibGit2Sharp.Repository.Clone(work, barePath, new LibGit2Sharp.CloneOptions { IsBare = true });
    }

    private sealed class PreflightRig : IDisposable
    {
        private readonly string _tempRoot;
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;

        public PreflightRig(
            string tempRoot, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host,
            PreflightEnvironment environment)
        {
            _tempRoot = tempRoot;
            _host = host;
            Environment = environment;
        }

        public PreflightEnvironment Environment { get; }

        /// <summary>The substrate root — the residue check reads the worktree directory under it.</summary>
        public string TempRoot => _tempRoot;

        public GrpcChannel Channel => GrpcChannel.ForAddress(
            _host.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = _host.Server.CreateHandler() });

        public Metadata Auth => new()
        {
            { "authorization", $"bearer {_host.Services.GetRequiredService<SessionTokenFile>().Token}" },
        };

        public void Dispose()
        {
            _host.Dispose();
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>A provisioned-looking substrate whose engine reports one scripted image as absent
    /// (null = both present) and records whether the jail spawn was ever reached.</summary>
    internal sealed class PreflightEnvironment : IAgentEnvironment
    {
        public PreflightEnvironment(
            string root, string? missingImage, string? staleImage = null, IToolchainImageBuilder? toolchains = null,
            bool digestlessEngine = false)
        {
            Engine = new ImageAwareEngine(missingImage, staleImage, digestlessEngine);
            Repos = new StubProvisioner(root);
            Worktrees = new StubWorktrees(root);
            ToolchainImages = toolchains;
        }

        /// <summary>MG-42 — null models a substrate with no image-build capability, which is the
        /// DEFAULT for every hand-rolled IAgentEnvironment double in the tree.</summary>
        public IToolchainImageBuilder? ToolchainImages { get; }

        public ImageAwareEngine Engine { get; }

        public string SubstrateId => "fake";

        public SubstrateCapabilities Capabilities { get; } = new(false, false, "none", "test");

        public IRepoProvisioner Repos { get; }

        public IAgentWorktreeManager Worktrees { get; }

        public ISandboxEngine Sandboxes => Engine;

        public IEgressPolicy Egress { get; } = new StubEgress();

        public SyncRemote ResolveSyncRemote(string repoHash) => new("fake-remote", $"fake://{repoHash}");

        internal sealed class ImageAwareEngine : ISandboxEngine
        {
            private readonly string? _missingImage;
            private readonly string? _staleImage;
            private int _spawnCalls;

            private readonly bool _digestless;

            public ImageAwareEngine(string? missingImage, string? staleImage = null, bool digestless = false)
            {
                _missingImage = missingImage;
                _staleImage = staleImage;
                _digestless = digestless;
            }

            /// <summary>
            /// MG-27/MG-42 — a storeful engine answers a content digest, and the launcher both spawns
            /// from it and builds any toolchain layer ON it. <paramref name="digestless"/> models the
            /// storeless engine (the ISandboxEngine default), which is what a toolchain layer must
            /// REFUSE to be built on: a layer over a mutable tag is a layer whose provenance stops at
            /// a pointer.
            /// </summary>
            public Task<string?> ImageDigestAsync(string imageRef, CancellationToken ct = default)
            {
                if (_digestless)
                {
                    return Task.FromResult<string?>(null);
                }

                var hex = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(imageRef)))
                    .ToLowerInvariant();
                return Task.FromResult<string?>("sha256:" + hex);
            }

            public int SpawnCalls => _spawnCalls;

            public Task<bool> ImageExistsAsync(string imageRef, CancellationToken ct = default) =>
                Task.FromResult(!string.Equals(imageRef, _missingImage, StringComparison.Ordinal));

            // A stale image reports a wrong label; every other present image reports the expected one,
            // so present+current proceeds while a label mismatch is a typed FailedPrecondition.
            public Task<string?> ImageVersionAsync(string imageRef, CancellationToken ct = default) =>
                Task.FromResult(string.Equals(imageRef, _staleImage, StringComparison.Ordinal)
                    ? "an-old-source-hash"
                    : SandboxImageVersions.For(imageRef));

            /// <summary>MG-42 — the image ref the jail was actually created from. Counting spawns
            /// cannot tell a base-image jail from a toolchain-layer jail, and that difference is the
            /// entire point of the per-repo toolchain.</summary>
            public string? LastSpawnImageRef { get; private set; }

            public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default)
            {
                Interlocked.Increment(ref _spawnCalls);
                LastSpawnImageRef = request.ImageRef;
                return Task.FromResult(new SandboxHandle("jail-" + request.AgentId, Reused: false));
            }

            public Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default) =>
                Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));

            public Task PauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task UnpauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task StopAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task RemoveAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
        }

        private sealed class StubProvisioner : IRepoProvisioner
        {
            private readonly string _root;

            public StubProvisioner(string root) => _root = root;

            public ProvisionResult Provision(string windowsRepoPathNormalized) =>
                throw new NotSupportedException("not exercised");

            public string BareRepoPathFor(string repoHash) => Path.Combine(_root, "repos", repoHash);
        }

        private sealed class StubWorktrees : IAgentWorktreeManager
        {
            private readonly string _root;

            public StubWorktrees(string root) => _root = root;

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

        private sealed class StubEgress : IEgressPolicy
        {
            public EgressAllowlist Allowlist { get; } = EgressAllowlist.WithDefaults(new Mainguard.Git.Audit.InMemoryAuditLog());

            public string NetworkName => "fake-net";

            public string ProxyUrl => "http://fake-proxy:3128";

            public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;

            public EgressVerdict Evaluate(string host) => EgressVerdict.Denied;
        }
    }
}
