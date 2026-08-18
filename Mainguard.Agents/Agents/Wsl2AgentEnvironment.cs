using System;
using System.Collections.Generic;
using System.Linq;
using Docker.DotNet;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;

namespace Mainguard.Agents.Agents;

/// <summary>
/// The WSL2 substrate implementation of <see cref="IAgentEnvironment"/>. Holds the real
/// P2-06 provisioner and worktree manager and resolves the host-side sync remote to a
/// <c>\\wsl.localhost\...</c> UNC handle. The <c>"mainguard-vm"</c> sync-remote name appears
/// in this method and NOWHERE else in the codebase (SC-2): every other layer registers
/// whatever <see cref="ResolveSyncRemote"/> returns, so the P2-25 cloud substrate can
/// resolve <c>mainguard-cloud</c> through the same seam.
/// </summary>
public sealed class Wsl2AgentEnvironment : IAgentEnvironment
{
    /// <summary>The default WSL2 sync-remote name (SC-2). Substrate-local by design.</summary>
    private const string Wsl2SyncRemoteName = "mainguard-vm";

    private readonly string _uncPrefix;

    /// <param name="vmRoot">The daemon-side ext4 base dir for mirrors/worktrees (defaults to <c>~/mainguard</c>).</param>
    /// <param name="userName">The Linux user whose home holds <c>mainguard/</c> (defaults to <c>USER</c>/<c>USERNAME</c>).</param>
    /// <param name="distroName">The WSL distro name in the UNC path (defaults to <c>MainguardEnv</c>).</param>
    /// <param name="dockerClient">The daemon-side Docker client (defaults to the local socket; connects lazily).</param>
    /// <param name="auditLog">Audit sink for allowlist-change events (defaults to the in-memory journal).</param>
    /// <param name="gatewayEndpoint">
    /// MG-4 — the daemon's model-gateway <c>host:port</c>, or null when the gateway is disabled (the
    /// default, and byte-identical to the pre-gateway behaviour). Handed to the egress proxy so the
    /// rendered tinyproxy filter PERMITS the gateway's own address: a confined jail reaches the gateway
    /// through the proxy it already routes through, and without this entry that request is refused by
    /// Mainguard's own default-deny filter. Passing it here rather than deriving it inside the
    /// configurator keeps the daemon the single source of the address it actually bound.
    /// </param>
    public Wsl2AgentEnvironment(
        string? vmRoot = null, string? userName = null, string? distroName = null,
        IDockerClient? dockerClient = null, IAuditLog? auditLog = null,
        string? gatewayEndpoint = null)
    {
        var user = string.IsNullOrEmpty(userName)
            ? Environment.GetEnvironmentVariable("USER") ?? Environment.GetEnvironmentVariable("USERNAME") ?? "mainguard"
            : userName;
        var distro = string.IsNullOrEmpty(distroName) ? "MainguardEnv" : distroName;

        // The Windows-facing UNC root of the VM's ~/<user>/mainguard/repos directory.
        _uncPrefix = $@"\\wsl.localhost\{distro}\home\{user}\mainguard\repos";

        // The substrate-neutral collaborators (provisioner, worktrees, caches, egress, sandbox
        // engine, toolchain images) — shared with MacHostAgentEnvironment; the invariants live on
        // AgentEnvironmentComposition.Compose. The provisioner's Windows-facing handle for a hash
        // IS the resolved sync-remote URL.
        var parts = AgentEnvironmentComposition.Compose(
            vmRoot, dockerClient, auditLog, gatewayEndpoint, hash => ResolveSyncRemote(hash).Url);
        Repos = parts.Repos;
        PackageCaches = parts.PackageCaches;
        Worktrees = parts.Worktrees;
        Egress = parts.Egress;
        Sandboxes = parts.Sandboxes;
        ToolchainImages = parts.ToolchainImages;

        // The user-managed toolchain channel installs INTO the VM over the same hardened WSL runner the
        // agent-CLI channel uses — one way to run a command in MainguardEnv, not two. Constructing it
        // needs no live VM (the runner shells out lazily), so this is safe in construction and tests.
        //
        // No payload source is passed, and that IS the production wiring: the channel defaults to
        // HttpsToolchainPayloadSource, so the payload is fetched here on the host rather than by a
        // `curl` inside a VM that has none. Omitting the argument yields the strong path — it is not a
        // control that can be silently dropped at a composition root (see the ctor's own note).
        Toolchains = new Toolchains.ToolchainChannel(
            new WslAdapterInstallHost(new Bootstrap.WslRunner()));
    }

    public string SubstrateId => "wsl2";

    public SubstrateCapabilities Capabilities { get; } =
        new(SupportsMaxIsolationBackend: false, SupportsWarmPoolPrestart: false,
            FilesystemTransport: "9p", LifecycleDialect: "wsl");

    public IRepoProvisioner Repos { get; }

    public IAgentWorktreeManager Worktrees { get; }

    public ISandboxEngine Sandboxes { get; }

    public IEgressPolicy Egress { get; }

    public IToolchainImageBuilder? ToolchainImages { get; }

    public Toolchains.ToolchainChannel? Toolchains { get; }

    public PackageCacheManager? PackageCaches { get; }

    public SyncRemote ResolveSyncRemote(string repoHash)
        => new(Wsl2SyncRemoteName, $@"{_uncPrefix}\{repoHash}.git");
}
