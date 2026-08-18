using System;
using System.IO;
using Docker.DotNet;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;

namespace Mainguard.Agents.Agents;

/// <summary>
/// The macos-host substrate implementation of <see cref="IAgentEnvironment"/>: mainguardd runs
/// natively on the Mac, daemon state lives under <c>~/mainguard</c> on the host filesystem, and
/// sandboxes run through whichever Docker engine the machine has (Docker Desktop / OrbStack /
/// Colima — resolved by <see cref="DockerEndpointResolver"/>, engine-agnostic by requirement).
/// This deviates from the ESC's deferred B4 "macos-vm" topology (daemon inside a Linux VM); the
/// deviation and its compensating controls are recorded in the substrate ADR/B-doc. The
/// <c>"mainguard-local"</c> sync-remote name appears in <see cref="ResolveSyncRemote"/> and
/// NOWHERE else in the codebase (SC-2), exactly like the WSL2 substrate's "mainguard-vm".
///
/// <para><see cref="IAgentEnvironment.Toolchains"/> is intentionally null for now: toolchains and
/// agent CLIs execute IN-JAIL (linux-arm64), so installing them on the macOS host would be wrong
/// by construction — the spawn path already refuses with a typed, substrate-naming error. A
/// container-backed install host is the follow-up.</para>
/// </summary>
public sealed class MacHostAgentEnvironment : IAgentEnvironment
{
    /// <summary>The macos-host sync-remote name (SC-2). Substrate-local by design.</summary>
    private const string MacHostSyncRemoteName = "mainguard-local";

    private readonly string _reposRoot;

    /// <param name="vmRoot">The substrate base dir for mirrors/worktrees (defaults to <c>~/mainguard</c>).</param>
    /// <param name="dockerClient">The Docker client (defaults to the resolved engine endpoint; connects lazily).</param>
    /// <param name="auditLog">Audit sink for allowlist-change events (defaults to the in-memory journal).</param>
    /// <param name="gatewayEndpoint">MG-4 — the daemon's model-gateway <c>host:port</c>, or null when the
    /// gateway is disabled. Same contract as the WSL2 substrate's parameter; see
    /// <see cref="AgentEnvironmentComposition"/> for why the egress proxy must PERMIT it.</param>
    public MacHostAgentEnvironment(
        string? vmRoot = null,
        IDockerClient? dockerClient = null, IAuditLog? auditLog = null,
        string? gatewayEndpoint = null)
    {
        // The substrate-neutral collaborators, shared with Wsl2AgentEnvironment; every invariant
        // is documented on AgentEnvironmentComposition.Compose. The daemon and the client are the
        // same machine here, so the client-facing sync-remote handle IS the local bare path.
        var parts = AgentEnvironmentComposition.Compose(
            vmRoot, dockerClient, auditLog, gatewayEndpoint, hash => ResolveSyncRemote(hash).Url,
            // ESC-I1 made structural on this substrate: bind sources may only name the substrate
            // root (repos/worktrees/agents/caches) or the daemon's data root (IPC socket dir) —
            // never a user repo or anything else on the host.
            allowedMountRoots: root => new[] { root, Mainguard.Git.MainguardPaths.DataRoot() });
        _reposRoot = Path.Combine(parts.Root, "repos");
        Repos = parts.Repos;
        PackageCaches = parts.PackageCaches;
        Worktrees = parts.Worktrees;
        Egress = parts.Egress;
        Sandboxes = parts.Sandboxes;
        ToolchainImages = parts.ToolchainImages;
    }

    public string SubstrateId => "macos-host";

    /// <summary>
    /// Filesystem transport is the engine's host file sharing (virtiofs on every current macOS
    /// engine); lifecycle dialect is plain docker — there is no VM of ours to import, terminate
    /// or unregister, the engine manages its own.
    /// </summary>
    public SubstrateCapabilities Capabilities { get; } =
        new(SupportsMaxIsolationBackend: false, SupportsWarmPoolPrestart: false,
            FilesystemTransport: "virtiofs", LifecycleDialect: "docker");

    public IRepoProvisioner Repos { get; }

    public IAgentWorktreeManager Worktrees { get; }

    public ISandboxEngine Sandboxes { get; }

    public IEgressPolicy Egress { get; }

    public IToolchainImageBuilder? ToolchainImages { get; }

    public PackageCacheManager? PackageCaches { get; }

    public SyncRemote ResolveSyncRemote(string repoHash)
        => new(MacHostSyncRemoteName, Path.Combine(_reposRoot, repoHash + ".git"));
}
