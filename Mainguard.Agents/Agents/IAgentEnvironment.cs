using Mainguard.Agents.Agents.Sandbox;

namespace Mainguard.Agents.Agents;

/// <summary>The one host-side remote that fetches agent branches. <paramref name="Name"/> is
/// substrate-defined (SC-2) — "mainguard-vm" on WSL2; <paramref name="Url"/> is an opaque handle
/// (G-14) whose concrete form (UNC path, https endpoint, unix path) is platform-defined.</summary>
public sealed record SyncRemote(string Name, string Url);

/// <summary>Declares which MAY-vary features a substrate implementation offers (§3). Opaque labels.</summary>
/// <param name="SupportsBindMountedUnixSockets">
/// Whether a Unix-domain socket the daemon binds is REACHABLE from inside a jail that bind-mounts it.
/// True wherever the daemon and the containers share a kernel (WSL2: the daemon runs inside the VM, so
/// the mount source is a native path on both sides). <b>False on macOS</b>, where the daemon runs on the
/// host and jails run in the container engine's Linux VM: Docker's file sharing (virtiofs / gRPC-FUSE)
/// does not proxy AF_UNIX across that boundary, so the socket bind-mounts in as an inert inode — it
/// stat()s as a socket and every <c>connect()</c> to it fails ECONNREFUSED with the daemon demonstrably
/// listening. Measured on a live jail, not inferred, and it is why the agent-IPC channel also ships a
/// file-framed form (<c>AgentIpcPaths</c>' outbox) that this flag selects.
/// <para>Defaulted true because it is the property every substrate had until macOS arrived, and a test
/// double that does not think about sockets should describe the world it is standing in.</para>
/// </param>
public sealed record SubstrateCapabilities(
    bool SupportsMaxIsolationBackend,
    bool SupportsWarmPoolPrestart,
    string FilesystemTransport,
    string LifecycleDialect,
    bool SupportsBindMountedUnixSockets = true);

/// <summary>
/// The per-platform substrate facade (ESC §1). Exactly one conforming implementation per platform;
/// it <b>composes</b> the P2-06 daemon services (holds them as members — it does not re-declare
/// their methods) and adds sync-remote resolution. It is the single object the daemon resolves
/// once per platform at startup.
///
/// <para><b>Minimal seam by design — realized incrementally.</b> Only the members P2-06 can truly
/// implement live here. The following are deferred and ADDED by their owning tasks (additive growth,
/// no throwing stubs):</para>
/// <list type="bullet">
///   <item><description><c>Sandboxes</c> / <c>Egress</c> → added by <b>P2-07</b>, which owns
///   <c>ISandboxEngine</c> / <c>IEgressPolicy</c>.</description></item>
///   <item><description><c>HealthCheckAsync</c> / <c>UpgradeAsync</c> / <c>TeardownAsync</c> →
///   daemon-side lifecycle, added by a <b>future task</b> (P2-07 teardown / P2-09 lifecycle).
///   These MAY reuse P2-05's already-shipped bootstrap/health-probe logic
///   (<c>Mainguard.Agents/Agents/Bootstrap/</c>), but P2-05 is a <i>client-side</i> state machine and
///   does not itself implement these daemon-side facade methods.</description></item>
/// </list>
///
/// <para>UI-free and daemon-side (P2-06 invariant 3; ESC-I2): never referenced from
/// <c>Mainguard.App.Shell</c>, which reaches the substrate only through the gRPC surface.</para>
/// </summary>
public interface IAgentEnvironment
{
    /// <summary>Substrate identity, e.g. "wsl2".</summary>
    string SubstrateId { get; }

    /// <summary>The MAY-vary capabilities this implementation offers.</summary>
    SubstrateCapabilities Capabilities { get; }

    /// <summary>P2-06 provisioner (held, not re-declared).</summary>
    IRepoProvisioner Repos { get; }

    /// <summary>P2-06 worktree manager (held, not re-declared).</summary>
    IAgentWorktreeManager Worktrees { get; }

    /// <summary>P2-07 hardened sandbox engine (held, not re-declared). Added by P2-07 per ESC §1.2.</summary>
    ISandboxEngine Sandboxes { get; }

    /// <summary>P2-07 default-deny egress policy (allowlist + proxy posture). Added by P2-07 per ESC §1.2.</summary>
    IEgressPolicy Egress { get; }

    /// <summary>
    /// Builds the per-repo verification-toolchain layer a repository's <c>.mainguard/toolchain</c>
    /// declares (see <see cref="ToolchainProvisioner"/>). Defaulted to <c>null</c> — a substrate with
    /// no image-build capability simply cannot provision toolchains, and the spawn path then refuses
    /// LOUDLY for a repo that declares one rather than jailing it without the tools. The many
    /// hand-rolled <see cref="IAgentEnvironment"/> doubles in the test tree inherit the default and
    /// keep their existing behaviour exactly (no declaration ⇒ nothing changes for them).
    /// </summary>
    IToolchainImageBuilder? ToolchainImages => null;

    /// <summary>
    /// MG-43 — the daemon-owned package cache (<see cref="PackageCacheManager"/>) whose per-agent
    /// directory is bind-mounted into each jail on ext4, outside the worktree and outside the 256 MiB
    /// tmpfs <c>$HOME</c>. Defaulted to <c>null</c> for the same reason
    /// <see cref="ToolchainImages"/> is: a substrate with no daemon-side filesystem has no cache to
    /// hand out, and the many hand-rolled test doubles keep their existing behaviour exactly (no cache
    /// mount, no cache environment — a combination <see cref="ContainerSpecBuilder"/> accepts and
    /// asserts is self-consistent).
    /// </summary>
    PackageCacheManager? PackageCaches => null;

    /// <summary>
    /// The user-managed toolchain channel (<see cref="Toolchains.ToolchainChannel"/>): the curated,
    /// pinned toolchains a HUMAN has installed into this environment, which reach every jail as a
    /// read-only bind mount rather than as an image layer.
    ///
    /// <para>Defaulted to <c>null</c> for the same reason <see cref="ToolchainImages"/> is — a substrate
    /// with no VM to install into has none — and the spawn path treats <c>null</c> as "nothing is
    /// installed", which is honest rather than optimistic: a repository declaring a runtime-mount
    /// toolchain on such a substrate gets a typed refusal naming what is missing, never a jail without
    /// it.</para>
    /// </summary>
    Toolchains.ToolchainChannel? Toolchains => null;

    /// <summary>The VM directory holding installed toolchains, bind-mounted READ-ONLY into every jail.
    /// Null when this substrate has none.</summary>
    string? ToolchainsRootPath => Toolchains is null ? null : Agents.Toolchains.ToolchainPaths.VmRoot;

    /// <summary>Resolve the ONE host-side sync remote for a provisioned repo (name + opaque URL handle).</summary>
    SyncRemote ResolveSyncRemote(string repoHash);
}
