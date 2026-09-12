using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Docker.DotNet.Models;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// Resource ceilings for one agent container (P2-07 §3.1).
///
/// <para>MG-26 — the jail used to bound only RAM and pids, which leaves two uncapped denial-of-service
/// surfaces that a prompt-injected agent reaches with a one-liner: a busy loop per pid starves every
/// OTHER agent (and the daemon) of CPU on the shared VM, and a descriptor leak exhausts the kernel's
/// per-process file table long before the pids ceiling is anywhere near. Both are now ceilings on the
/// create request, not conventions.</para>
/// </summary>
/// <param name="Cpus">The CPU ceiling in whole cores, applied as <c>NanoCPUs</c> (cgroup
/// cpu.max). Fractional values are allowed — 1.5 is a legitimate ceiling.</param>
/// <param name="NoFile">The <c>RLIMIT_NOFILE</c> ceiling (soft = hard). Per-process, so it is a true
/// bound on one runaway CLI without any cross-container coupling.</param>
/// <param name="NProc">The <c>RLIMIT_NPROC</c> ceiling (soft = hard). Deliberately set ABOVE
/// <paramref name="Pids"/>: Docker's nproc ulimit is enforced by the kernel per <b>real uid</b>, and
/// with userns-remap every jail shares one host uid — so a value at or below the pids ceiling would
/// make the FIRST agent's processes count against the SECOND agent's fork budget. The per-container
/// bound that actually binds is the cgroup <c>PidsLimit</c>; nproc is the outer fork-bomb backstop
/// that survives a cgroup misconfiguration.</param>
public sealed record SandboxLimits(
    long MemoryBytes,
    long Pids,
    double Cpus = SandboxLimits.DefaultCpus,
    long NoFile = SandboxLimits.DefaultNoFile,
    long NProc = SandboxLimits.DefaultNProc)
{
    /// <summary>2 cores: enough for a parallel build, never the whole VM.</summary>
    public const double DefaultCpus = 2.0;

    /// <summary>4096 descriptors — well above a node/go toolchain's working set, well below exhaustion.</summary>
    public const long DefaultNoFile = 4096;

    /// <summary>See <see cref="NProc"/>: strictly above the default pids ceiling, on purpose.</summary>
    public const long DefaultNProc = 4096;

    /// <summary>A conservative default: 2 GiB RAM, 512 pids, 2 CPUs, 4096 nofile/nproc.</summary>
    public static SandboxLimits Default { get; } = new(2L * 1024 * 1024 * 1024, 512);
}

/// <summary>
/// The two secret tmpfs files under <c>/run/secrets</c> (P2-07 §3.2 + G2 control 1). Both are
/// mode <c>0400</c>; crucially the agent credential file is owned by the <b>agent uid</b> while the
/// OOB session key <c>K</c> is owned by a <b>dedicated supervisor uid ≠ the agent uid</b> — so the
/// prompt-injected agent cannot read <c>K</c> from the file (the memory path is closed by the
/// seccomp denylist + no <c>CAP_SYS_PTRACE</c>). Contents are written after start via an stdin exec,
/// never through <c>Env</c>/argv/persistent disk.
///
/// <para><b>Each secret lives in its OWNER'S OWN directory, and that is load-bearing rather than
/// tidy.</b> The files used to sit side by side in a single root-owned <c>0711</c> directory, which
/// meant only root could create them and the writer therefore had to <c>chown</c> each one to its
/// owner afterwards. That <c>chown</c> could never work: the jail is created with a non-root
/// <c>User</c> AND <c>no-new-privileges</c>, and Docker gives an exec in such a container an EMPTY
/// permitted/effective capability set even when the exec asks for uid 0 — so the "root" exec had no
/// <c>CAP_CHOWN</c> and every secret write died with <c>EPERM</c>. Measured on the shipping engine
/// (Docker 20.10.24): with <c>--user 1000 --security-opt no-new-privileges</c> an exec as uid 0
/// reports <c>CapPrm: 0000000000000000</c> against a bounding set of <c>fb</c>; drop EITHER the
/// non-root user or no-new-privileges and the same exec reports <c>CapPrm: fb</c> and the chown
/// succeeds. Both of those are non-negotiable controls (G-15, G2), so the CHOWN had to go.</para>
///
/// <para>Docker mounts a tmpfs daemon-side, as real root, and honours <c>uid=</c>/<c>gid=</c> — so a
/// per-owner directory arrives already owned by the right uid and the secret is simply CREATED by
/// its owner. No capability is required anywhere on the path, which is why it now works with or
/// without a daemon-level userns remap: the ids are container-relative either way. The posture is
/// also strictly tighter than the flat layout it replaces — the agent uid can no longer even
/// <c>stat</c> the supervisor's directory, where before it could traverse to <c>oob.key</c> and was
/// stopped only by the file's own mode.</para>
/// </summary>
public sealed record CredTmpfsSpec(
    string CredentialPath,
    string OobKeyPath,
    int Mode,
    int AgentUid,
    int SupervisorUid)
{
    /// <summary>The traversable-but-not-listable parent of both per-owner secret directories. Stays
    /// root-owned <c>0711</c>: nothing is written here, so nobody needs to write here.</summary>
    public const string SecretsRoot = "/run/secrets";

    /// <summary>The agent uid's own <c>0700</c> secret directory.</summary>
    public const string AgentSecretsDir = SecretsRoot + "/agent";

    /// <summary>The supervisor uid's own <c>0700</c> secret directory. The agent uid cannot open it,
    /// list it, or create anything in it.</summary>
    public const string SupervisorSecretsDir = SecretsRoot + "/supervisor";

    /// <summary>The conventional per-agent credential file (P2-01 injector content).</summary>
    public const string DefaultCredentialPath = AgentSecretsDir + "/agent.env";

    /// <summary>The OOB session-HMAC-key file, owned by the supervisor uid.</summary>
    public const string DefaultOobKeyPath = SupervisorSecretsDir + "/oob.key";

    /// <summary>Secret files are read-only to their owner and no one else (G-13).</summary>
    public const int SecretMode = 0b100_000_000; // 0400 octal

    /// <summary>The mode every per-owner secret directory is mounted with: the owner may traverse,
    /// list and create; nobody else has any access at all.</summary>
    public const string OwnedDirMode = "0700";

    /// <summary>The directory a secret path lives in — the thing that has to be owned by the writer.
    /// Hand-rolled rather than <c>Path.GetDirectoryName</c>, which yields backslashes when the daemon
    /// build runs on Windows and would silently stop matching the container-side tmpfs key.</summary>
    public static string DirectoryOf(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var cut = path.LastIndexOf('/');
        if (cut <= 0)
            throw new SandboxSpecException($"Secret path '{path}' has no container-absolute parent directory.");
        return path[..cut];
    }

    /// <summary>
    /// Builds the spec from the two distinct uids, enforcing G2 control 1 (supervisor uid ≠ agent
    /// uid) at construction — a shared uid would let the agent read <c>K</c> from its own file.
    /// </summary>
    public static CredTmpfsSpec Create(int agentUid, int supervisorUid)
    {
        if (agentUid == supervisorUid)
            throw new SandboxSpecException(
                $"G2 control 1: the OOB key custody uid ({supervisorUid}) must differ from the agent-CLI uid ({agentUid}); a shared uid lets the agent read K.");
        if (agentUid <= 0 || supervisorUid <= 0)
            throw new SandboxSpecException("Both the agent uid and the supervisor uid must be non-root, positive uids.");

        return new CredTmpfsSpec(DefaultCredentialPath, DefaultOobKeyPath, SecretMode, agentUid, supervisorUid);
    }
}

/// <summary>The complete input to <see cref="ContainerSpecBuilder"/> (P2-07 §3.1).</summary>
/// <param name="AdaptersRootPath">The VM-side dynamically-installed-CLI root
/// (<see cref="Adapters.AdapterPaths.VmRoot"/>), bind-mounted READ-ONLY at
/// <see cref="Adapters.AdapterPaths.SandboxMount"/>. Null/empty when no CLIs are installed — the
/// jail simply carries no adapters mount.</param>
/// <param name="IpcDirPath">The VM-side per-agent IPC dir (coordinator jails only), bind-mounted
/// READ-ONLY at <see cref="Ipc.AgentIpcPaths.SandboxMount"/>; same G-11 ext4-only rejection as every
/// other mount. Null/empty = no IPC mount (workers).</param>
/// <param name="IpcOutboxPath">The <c>outbox/</c> child of <paramref name="IpcDirPath"/>, bind-mounted
/// READ-WRITE at <see cref="Ipc.AgentIpcPaths.SandboxOutboxPath"/> — nested inside the read-only IPC
/// mount, which leaves the shim and the instructions unwritable while giving the jail a mailbox.
/// <para>Supplied ONLY where the substrate cannot carry a Unix socket across the container mount
/// boundary — which today is macOS, where the daemon runs on the host and jails run in the engine's
/// Linux VM, and Docker's file sharing does not proxy AF_UNIX: the socket bind-mounts in as an inert
/// inode and every <c>connect()</c> fails ECONNREFUSED. Null/empty everywhere else, and then the jail
/// has no writable bind mount at all, exactly as before.</para></param>
/// <param name="BareRepoPath">The VM-side <b>shared mirror</b>, bind-mounted at its <b>identical</b> VM
/// path so the per-agent repo's <c>objects/info/alternates</c> (an absolute VM path into
/// <c>&lt;bare&gt;/objects</c>) resolves inside the jail — without it in-jail git cannot read a single
/// object of the repo's history. Null/empty = no mirror mount (session-only paths and pre-P2-18 tests).
/// <para><b>MG-3:</b> mounted READ-WRITE until stage 3, then read-only. It is a shared surface — every
/// jail for this repo mounts the same directory — and the deny-non-fast-forward / deny-delete quarantine
/// on it only ever constrained <c>receive-pack</c>, so a writable mount let any agent rewrite
/// <c>refs/heads/main</c> by editing a 41-byte file.</para></param>
/// <param name="AgentRepoPath">MG-3 — the VM-side per-agent repository
/// (<c>&lt;vmRoot&gt;/agents/&lt;hash&gt;/&lt;agentId&gt;.git</c>), bind-mounted READ-WRITE at its
/// <b>identical</b> VM path so the worktree's <c>.git</c> <c>gitdir:</c> pointer resolves in-jail. This
/// is the ONLY git directory the jail may write, and exactly one jail ever mounts it. Null/empty = no
/// per-agent repo (session-only paths and the pre-MG-3 test doubles).</param>
/// <param name="DnsServerAddress">MG-7 — the IPv4 address of the egress proxy's dnsmasq, pinned as the
/// jail's ONLY resolver (<c>HostConfig.Dns</c>). Without it Docker hands the container its embedded
/// resolver at <c>127.0.0.11</c>, which forwards to the VM's upstream DNS: the NXDOMAIN-pinned dnsmasq
/// is then rendered into the proxy container and never consulted by anything, so the "DNS exfiltration
/// is blocked" control is a no-op. Mandatory whenever the jail sits on the default-deny agent network
/// (see <see cref="EgressProxyConfigurator.AgentNetworkName"/>); null only for the ad-hoc engines that
/// run outside that network (merge-queue/lifecycle harnesses on <c>bridge</c>).</param>
/// <param name="PackageCachePath">MG-43 — this agent's own daemon-owned package cache
/// (<c>&lt;vmRoot&gt;/caches/&lt;repoHash&gt;/&lt;agentId&gt;</c>), bind-mounted READ-WRITE at
/// <see cref="PackageCachePolicy.SandboxMount"/> — on ext4, outside <c>/workspace</c>, and outside the
/// 256 MiB tmpfs <c>$HOME</c>. It is what lets a real dependency closure (1.7 GB for this repository)
/// be restored at all, without putting a gigabyte of untracked files inside the tree that verification
/// measures. One cache, one jail: see <see cref="PackageCachePolicy"/> for why it is never shared.
/// Null/empty = no cache mount, and then the cache environment is not set either — the two travel
/// together by construction, because an environment that names a mount the container has not got is
/// exactly the silent fall-through this feature must not have.</param>
public sealed record ContainerSpecRequest(
    string RepoHash,
    string AgentId,
    string WorktreePath,
    string ImageRef,
    SandboxLimits Limits,
    string NetworkName,
    CredTmpfsSpec Credentials,
    string ProxyUrl,
    string UsernsMode = UsernsRemapPolicy.InheritDaemonRemap,
    string? AdaptersRootPath = null,
    string? IpcDirPath = null,
    string? IpcOutboxPath = null,
    string? BareRepoPath = null,
    string? DnsServerAddress = null,
    string? AgentRepoPath = null,
    string? PackageCachePath = null,
    bool WithoutRepositoryAccess = false,
    string? ToolchainsRootPath = null,
    IReadOnlyList<string>? ToolchainIds = null,
    // ESC-I1: the substrate's daemon-owned roots; when supplied, every bind-mount source must sit
    // under one of them (see SandboxEngineOptions.AllowedMountRoots).
    IReadOnlyList<string>? AllowedMountRoots = null,
    // Stamped onto the container as mainguard.kind / mainguard.agent.role so a daemon that restarts can
    // adopt this jail back as the agent it actually is. Default "" keeps every existing caller (and the
    // ad-hoc harnesses) compiling and simply yields an unlabelled jail, which adopts as a role-less worker.
    string AgentKind = "",
    string AgentRole = "",
    // The spawning coordinator, stamped as mainguard.agent.parent for the same reason as the role: after a
    // restart the label is the only record of whose fan-out this worker belongs to.
    string AgentParentId = "");

/// <summary>
/// The pure, unit-testable heart of P2-07: turns an agent request into a hardened Docker
/// <see cref="CreateContainerParameters"/>. It performs <b>no</b> I/O and holds no Docker client;
/// the engine passes the result to <c>CreateContainerAsync</c>.
///
/// <para>Every hardening control is set <b>and re-asserted</b> here (G-11/G-15 + the G2 per-container
/// quartet): a Windows/UNC mount source, a missing seccomp denylist, a present <c>CAP_SYS_PTRACE</c>,
/// or a secret in the environment is a <see cref="SandboxSpecException"/> at construction — the
/// container is never created. <c>kernel.yama.ptrace_scope</c> (G2 control 2) is deliberately
/// <b>not</b> set here: it is a non-namespaced VM-wide sysctl provisioned by the P2-05 bootstrapper
/// (<see cref="Mainguard.Agents.Agents.Bootstrap.FirstBootStep"/>).</para>
/// </summary>
public static class ContainerSpecBuilder
{
    /// <summary>Capabilities dropped-then-re-added: a minimal set for dev tooling that never
    /// includes <c>SYS_PTRACE</c> (G2 control 4). We drop <c>ALL</c> and add only these back.</summary>
    private static readonly string[] MinimalCaps =
    {
        "CHOWN", "DAC_OVERRIDE", "FOWNER", "FSETID", "SETGID", "SETUID", "KILL",
    };

    // Windows/WSL mount sources that MUST NEVER be bind-mounted into an agent (G-11).
    private static readonly Regex WslDrvfsMount = new(@"^/mnt/[a-z]/", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WindowsDrive = new(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);

    /// <summary>The container mount point of the agent worktree.</summary>
    public const string WorkspaceTarget = "/workspace";

    /// <summary>
    /// MG-3 — whether the shared mirror's bind mount denies writes from inside the jail.
    ///
    /// <para>This is the single bit that closes MG-3, and it is a named constant so that "is the mirror
    /// writable from an agent?" is one greppable answer rather than an inference from a mount literal
    /// buried in a list. It is only correct while the agent has somewhere else to write: the per-agent
    /// repository (<see cref="ContainerSpecRequest.AgentRepoPath"/>) borrows this mirror's objects
    /// through <c>objects/info/alternates</c> and owns the refs, HEAD, index and new objects, so a
    /// <c>git commit</c> in the jail touches nothing here.</para>
    ///
    /// <para><b>Measured, not assumed.</b> With this false and everything else in MG-3 already landed,
    /// <c>MirrorReadOnlyDockerTests</c> writes <c>&lt;bare&gt;/refs/heads/main</c> from inside a real
    /// production jail and succeeds — on a box with no userns remap the container's uid 1000 IS the
    /// daemon's, and <c>core.sharedRepository=group</c> makes it group-writable besides. With it true
    /// the same write is refused by the bind mount, whoever the writer is. That is the whole of MG-3:
    /// the deny-non-fast-forward / deny-delete settings only ever governed <c>receive-pack</c>, and
    /// nothing above went anywhere near <c>receive-pack</c>.</para>
    /// </summary>
    public const bool MirrorMountReadOnly = true;

    /// <summary>The agent user's home inside the jail — a tmpfs (wiped every relaunch) by design;
    /// the ONE path the CLI login round-trip (restore at spawn / harvest at stop) resolves under.</summary>
    public const string AgentHome = "/home/agent";

    /// <summary>
    /// The mount list: the ext4 worktree, plus the read-only adapters root when one is supplied.
    /// The adapters mount source is an ext4 VM path and goes through the same G-11 rejection.
    /// </summary>
    private static List<Mount> BuildMounts(ContainerSpecRequest request)
    {
        // Coordinator contract §2/§8: "The coordinator has no worktree, no git credentials and no view of
        // repository contents." A coordinator is only an orchestrator, so its jail carries NO path into the
        // repository at all — not the worktree, not the shared mirror, not its own git dir, not the package
        // cache (nothing in it builds anything). /workspace becomes an empty tmpfs so the CLI still has a
        // working directory to exec into.
        //
        // This is fail-closed, and deliberately so. §5 is the reason the whole contract exists: a system
        // prompt is not a security boundary, and the shipped coordinator's prompt ("you never write code,
        // touch a worktree, or merge") was never even delivered to the in-jail CLI. Refusing the paths here
        // — rather than trusting a caller to pass null — means a future caller that starts supplying a
        // worktree for a coordinator gets a typed spawn failure instead of silently handing back the
        // capability the role-lock removed.
        if (request.WithoutRepositoryAccess)
        {
            foreach (var (name, path) in new[]
                     {
                         (nameof(request.WorktreePath), request.WorktreePath),
                         (nameof(request.BareRepoPath), request.BareRepoPath),
                         (nameof(request.AgentRepoPath), request.AgentRepoPath),
                         (nameof(request.PackageCachePath), request.PackageCachePath),
                     })
            {
                if (!string.IsNullOrEmpty(path))
                {
                    throw new SandboxSpecException(
                        $"Coordinator contract §3: a repository-less jail was asked to mount {name} ('{path}'). "
                        + "A coordinator has no worktree, no git credentials and no view of repository contents — "
                        + "it orchestrates and nothing else. Refusing to create the container.");
                }
            }

            // Only the read-only capability mounts survive: the adapters root (the CLI it runs) and the
            // IPC dir (its four tools). Both are added below.
            return BuildCapabilityOnlyMounts(request);
        }

        var mounts = new List<Mount>
        {
            new() { Type = "bind", Source = request.WorktreePath, Target = WorkspaceTarget, ReadOnly = false },
        };

        if (!string.IsNullOrEmpty(request.BareRepoPath))
        {
            RejectNonExt4Source(request.BareRepoPath);
            mounts.Add(new Mount
            {
                Type = "bind",
                // Target == Source: the per-agent repo's objects/info/alternates names this absolute VM
                // path; any other target leaves every object lookup dangling and in-jail git dead.
                Source = request.BareRepoPath,
                Target = request.BareRepoPath,
                ReadOnly = MirrorMountReadOnly,
            });
        }

        if (!string.IsNullOrEmpty(request.AgentRepoPath))
        {
            RejectNonExt4Source(request.AgentRepoPath);
            mounts.Add(new Mount
            {
                Type = "bind",
                // Target == Source: the worktree's `.git` file names this absolute VM path; any other
                // target leaves the gitdir pointer dangling and in-jail git dead.
                Source = request.AgentRepoPath,
                Target = request.AgentRepoPath,
                // MG-3: the ONE git directory the agent may write. Exactly one jail mounts it, so a
                // write here cannot reach another agent, and the shared mirror is not writable at all.
                ReadOnly = false,
            });
        }

        if (!string.IsNullOrEmpty(request.AdaptersRootPath))
        {
            RejectNonExt4Source(request.AdaptersRootPath);
            mounts.Add(new Mount
            {
                Type = "bind",
                Source = request.AdaptersRootPath,
                Target = Adapters.AdapterPaths.SandboxMount,
                // READ-ONLY: agents run the shared CLIs but can never modify what other agents execute.
                ReadOnly = true,
            });
        }

        if (!string.IsNullOrEmpty(request.ToolchainsRootPath))
        {
            RejectNonExt4Source(request.ToolchainsRootPath);
            mounts.Add(new Mount
            {
                Type = "bind",
                Source = request.ToolchainsRootPath,
                Target = Toolchains.ToolchainPaths.SandboxMount,
                // READ-ONLY, and this is the property that lets one toolchain tree be SHARED by every
                // jail on the machine. A writable share would let agent A replace the interpreter that
                // agent B's verification runs under — the merge gate decided by another tenant, which is
                // the same reasoning that makes package caches per-agent instead. Toolchains may be
                // shared precisely because nothing in a jail can write them.
                ReadOnly = true,
            });
        }

        if (!string.IsNullOrEmpty(request.IpcDirPath))
        {
            RejectNonExt4Source(request.IpcDirPath);
            mounts.Add(new Mount
            {
                Type = "bind",
                Source = request.IpcDirPath,
                Target = Ipc.AgentIpcPaths.SandboxMount,
                // READ-ONLY: the coordinator can dial the daemon's socket (connect() is not a
                // filesystem write) and run the shim, but can never replace either.
                ReadOnly = true,
            });
        }

        AddIpcOutboxMount(request, mounts);

        if (!string.IsNullOrEmpty(request.PackageCachePath))
        {
            RejectNonExt4Source(request.PackageCachePath);

            // MG-43 + MG-3: the ONE structural fact this pure builder can check about a writable
            // daemon-side mount is WHICH tree it names. A package-cache mount may only ever name
            // something inside a `caches/` tree — so this mount can never be edited into a second
            // writable path at the mirror, the per-agent git dir, or anywhere else under the daemon's
            // home. That is the guard that keeps MG-3 closed while adding a read-write mount.
            if (!PackageCachePolicy.IsInsideACacheTree(request.PackageCachePath))
                throw new SandboxSpecException(
                    $"MG-43: refusing '{request.PackageCachePath}' as a package cache source. A package cache mount "
                    + $"is READ-WRITE, so it may only ever name a path inside a '{PackageCachePolicy.CachesDirectoryName}/' "
                    + "tree; any other source would be a second writable path into daemon-owned state (MG-3).");

            mounts.Add(new Mount
            {
                Type = "bind",
                Source = request.PackageCachePath,
                // A FIXED target, not the source path: unlike the mirror and the per-agent repo (whose
                // in-jail paths are named by git metadata and must therefore match the VM's), nothing
                // refers to the cache by absolute VM path — the package managers are told where it is
                // through the environment. A fixed target is what lets that environment be a constant.
                Target = PackageCachePolicy.SandboxMount,
                // READ-WRITE by definition: a package manager that cannot write its cache is worse off
                // than one with no cache at all, because it fails halfway instead of at the start.
                ReadOnly = false,
            });
        }

        // ESC-I1 made structural: when the substrate declared its daemon-owned roots, every bind
        // source must sit under one of them. The per-source guards above reject known-bad SHAPES
        // (drvfs/UNC/drive-letter, a cache outside a caches/ tree); this one rejects everything
        // that is not known-good — a user repo, the host home, any path a future caller bug names.
        if (request.AllowedMountRoots is { Count: > 0 } roots)
        {
            foreach (var mount in mounts)
            {
                if (mount.Type == "bind" && !IsUnderAnyRoot(mount.Source, roots))
                    throw new SandboxSpecException(
                        $"ESC-I1: refusing bind source '{mount.Source}' — it is outside every daemon-owned "
                        + $"substrate root ({string.Join(", ", roots)}). Only substrate-owned state may be "
                        + "mounted into a jail; user repos and the host filesystem never are.");
            }
        }

        return mounts;
    }

    /// <summary>
    /// The mounts a repository-less (coordinator) jail gets: the read-only adapters root and the
    /// read-only IPC dir, and <b>nothing else</b>. Both are read-only, so this jail has no writable bind
    /// mount at all — its only writable storage is its own tmpfs, which dies with the container.
    /// </summary>
    private static List<Mount> BuildCapabilityOnlyMounts(ContainerSpecRequest request)
    {
        var mounts = new List<Mount>();

        if (!string.IsNullOrEmpty(request.AdaptersRootPath))
        {
            RejectNonExt4Source(request.AdaptersRootPath);
            mounts.Add(new Mount
            {
                Type = "bind",
                Source = request.AdaptersRootPath,
                Target = Adapters.AdapterPaths.SandboxMount,
                ReadOnly = true,
            });
        }

        if (!string.IsNullOrEmpty(request.IpcDirPath))
        {
            RejectNonExt4Source(request.IpcDirPath);
            mounts.Add(new Mount
            {
                Type = "bind",
                Source = request.IpcDirPath,
                Target = Ipc.AgentIpcPaths.SandboxMount,
                ReadOnly = true,
            });
        }

        AddIpcOutboxMount(request, mounts);

        return mounts;
    }

    /// <summary>
    /// The read-write outbox, nested inside the read-only IPC mount. On the substrates that need it this
    /// is the coordinator jail's ONLY writable bind mount, which is why the guard is exact rather than
    /// shaped: the source must be the <c>outbox/</c> child of THIS request's IPC dir, so the field can
    /// never be edited into a second writable path at the mirror, the per-agent git dir, or anywhere else
    /// under the daemon's home (MG-3, and the same reasoning as the package cache's <c>caches/</c> gate).
    ///
    /// <para>Nested rather than a separate top-level target so the read-only mount keeps covering the
    /// shim and the operating instructions: an agent that could rewrite its own shim would be harming
    /// only itself, but "the thing the CLI executes is the daemon's file" is worth keeping structural.
    /// The engine sorts mounts by target depth, so the nesting resolves regardless of list order.</para>
    /// </summary>
    private static void AddIpcOutboxMount(ContainerSpecRequest request, List<Mount> mounts)
    {
        if (string.IsNullOrEmpty(request.IpcOutboxPath))
        {
            return;
        }

        RejectNonExt4Source(request.IpcOutboxPath);

        var expected = string.IsNullOrEmpty(request.IpcDirPath)
            ? null
            : Ipc.AgentIpcPaths.OutboxIn(request.IpcDirPath);
        if (expected is null || !SamePath(request.IpcOutboxPath, expected))
        {
            throw new SandboxSpecException(
                $"Refusing '{request.IpcOutboxPath}' as an IPC outbox source. The outbox mount is "
                + "READ-WRITE, so it may only ever name the '" + Ipc.AgentIpcPaths.OutboxDirName
                + "' directory inside THIS agent's own IPC dir"
                + (expected is null ? " — and this request names no IPC dir at all." : $" ('{expected}').")
                + " Any other source would be a second writable path into daemon-owned state (MG-3).");
        }

        mounts.Add(new Mount
        {
            Type = "bind",
            Source = request.IpcOutboxPath,
            Target = Ipc.AgentIpcPaths.SandboxOutboxPath,
            // READ-WRITE by definition: this IS the channel on a substrate whose mount cannot carry a
            // socket. The jail drops a request file here and polls for its answer.
            ReadOnly = false,
        });
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            Mainguard.Git.Services.FileSystemPaths.Comparison);

    /// <summary>
    /// The writable surfaces, all tmpfs (the rootfs is read-only and every bind mount that is not the
    /// worktree/agent-repo/cache is read-only).
    /// </summary>
    private static Dictionary<string, string> BuildTmpfs(ContainerSpecRequest request)
    {
        var tmpfs = new Dictionary<string, string>
        {
            ["/dev/shm"] = "",
            ["/tmp"] = "size=256m,mode=1777",
            // uid/gid MUST name the agent: a tmpfs without them is created root-owned, and mode
            // 0700 then locks the agent out of its OWN $HOME — every agent CLI that writes state
            // under ~/.local or ~/.config (verified: opencode) dies with EACCES on first run.
            // (Same class as the /run/secrets 0711 note below; unhit until a CLI actually ran.)
            [AgentHome] = $"size=256m,mode=0700,uid={request.Credentials.AgentUid},gid={request.Credentials.AgentUid}",
            // 0711 (traverse-only, not listable): each uid can reach its OWN secret directory
            // below, and nothing is ever created directly here — so this stays root-owned with
            // no write bit for anyone but root, and no exec ever needs to write it.
            [CredTmpfsSpec.SecretsRoot] = "size=1m,mode=0711",

            // One tmpfs per secret owner, mounted BY THE DAEMON (real root) already owned by the
            // uid that will write into it. This is what removes the impossible chown from the
            // secret-write path — see the CredTmpfsSpec remarks for the measurement. `uid=`/`gid=`
            // on a tmpfs are interpreted in the container's user namespace, so these are correct
            // whether or not dockerd is userns-remapped, exactly as $HOME above already relies on.
            //
            // The DIRECTORIES are the structural constants while the UIDS come from the request,
            // and that split is deliberate: AssertSecretDirsOwned below re-derives the directory
            // from each secret's actual path in the spec, so the mount list and the secret list are
            // two independent statements that have to agree. Deriving both from the same expression
            // would make the assertion true by construction and prove nothing.
            [CredTmpfsSpec.AgentSecretsDir] = OwnedSecretDirOptions(request.Credentials.AgentUid),
            [CredTmpfsSpec.SupervisorSecretsDir] = OwnedSecretDirOptions(request.Credentials.SupervisorUid),
        };

        if (request.WithoutRepositoryAccess)
        {
            // A coordinator has no worktree to mount at /workspace, but `WorkingDir` is still
            // /workspace and `docker exec -w /workspace` must land somewhere — an absent working
            // directory fails the exec outright. An empty, agent-owned tmpfs gives the CLI a cwd that
            // contains no repository content and does not survive the container.
            tmpfs[WorkspaceTarget] =
                $"size=64m,mode=0700,uid={request.Credentials.AgentUid},gid={request.Credentials.AgentUid}";
        }

        return tmpfs;
    }

    /// <summary>
    /// True when <paramref name="path"/> equals a root or sits inside one — compared on the REAL paths,
    /// not the spelled ones.
    ///
    /// <para><b>Audit F33.</b> This used to be pure textual prefix matching, and the docker daemon does
    /// not bind what a path spells — it binds what the path RESOLVES to. So a symlink anywhere under a
    /// daemon-owned root pointed anywhere else passed a containment check the bind then ignored, and
    /// this is exactly the shape ESC-I1 exists to make structural. The specific reachable case on the
    /// shipping substrate is macOS's own <c>/var → /private/var</c> (and <c>/tmp</c>), which every
    /// fixture that crosses the boundary already has to canonicalize by hand.</para>
    ///
    /// <para>Both sides are resolved, because a root spelled through a symlink is just as wrong as a
    /// source spelled through one: resolving only the source would start REFUSING every legitimate mount
    /// on a machine whose data root happens to sit under <c>/var</c>. Resolution is best-effort on a path
    /// that does not exist yet — a not-yet-created cache directory is a legitimate source, so an
    /// unresolvable path falls back to its normalized form and is compared as before rather than being
    /// refused for not existing.</para>
    /// </summary>
    private static bool IsUnderAnyRoot(string path, IReadOnlyList<string> roots)
    {
        var full = RealPath(path);
        foreach (var root in roots)
        {
            var fullRoot = RealPath(root);
            if (string.Equals(full, fullRoot, Mainguard.Git.Services.FileSystemPaths.Comparison)) return true;
            var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, Mainguard.Git.Services.FileSystemPaths.Comparison)) return true;
        }
        return false;
    }

    /// <summary>
    /// <paramref name="path"/> with every symlink on it resolved, normalized. Falls back to
    /// <see cref="Path.GetFullPath(string)"/> for a path that does not exist or cannot be resolved —
    /// see <see cref="IsUnderAnyRoot"/> for why that fallback is the safe direction here.
    ///
    /// <para>Resolved COMPONENT BY COMPONENT, from the root down, which is the part that matters: the
    /// escape shape is an intermediate link (<c>/var</c> → <c>/private/var</c>), and asking only whether
    /// the leaf is a link would miss every one of them. A component that does not exist yet is simply
    /// appended — a not-yet-created cache directory under a resolved parent is still contained by that
    /// parent. <c>ResolveLinkTarget(returnFinalTarget: true)</c> is the framework's own "follow the whole
    /// chain", and answers null for a path that is not a link, which is the ordinary case.</para>
    /// </summary>
    internal static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            var current = full;
            // Each pass substitutes the FIRST link it meets and starts again, because a link's target
            // may itself be spelled through links (macOS: /tmp → /private/tmp, and /var → /private/var
            // under it). Bounded so a link cycle terminates instead of spinning; a path that has not
            // settled in this many substitutions is one we decline to have an opinion about.
            for (var hops = 0; hops < 64; hops++)
            {
                var next = SubstituteFirstLink(current);
                if (next is null)
                {
                    return Path.GetFullPath(current);
                }

                current = next;
            }

            return full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return full;
        }
    }

    /// <summary>The path with its first symlinked component replaced by that link's target (and the rest
    /// of the path re-appended), or null when it contains no link left to substitute.</summary>
    private static string? SubstituteFirstLink(string full)
    {
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        var segments = full[root.Length..]
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

        var walked = root;
        for (var i = 0; i < segments.Length; i++)
        {
            walked = Path.Combine(walked, segments[i]);

            // A component that does not exist cannot be a link, and a not-yet-created leaf under a
            // resolved parent is a legitimate mount source — so absence is simply walked past.
            var target = Directory.Exists(walked)
                ? new DirectoryInfo(walked).ResolveLinkTarget(returnFinalTarget: false)?.FullName
                : File.Exists(walked)
                    ? new FileInfo(walked).ResolveLinkTarget(returnFinalTarget: false)?.FullName
                    : null;
            if (string.IsNullOrEmpty(target))
            {
                continue;
            }

            var tail = segments.Skip(i + 1).ToArray();
            return tail.Length == 0
                ? target
                : Path.GetFullPath(Path.Combine(new[] { target }.Concat(tail).ToArray()));
        }

        return null;
    }

    /// <summary>Builds the hardened create request; throws typed on any invariant violation.</summary>
    public static CreateContainerParameters Build(ContainerSpecRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // G-11 enforced at CONSTRUCTION: the ONLY mount source is an ext4 worktree path. A drvfs
        // (/mnt/c/...), UNC (\\wsl.localhost\...), or drive-letter (C:\...) source is rejected here
        // so the container is never created.
        //
        // A repository-less (coordinator) jail has NO worktree at all, so there is no source to vet —
        // BuildMounts refuses a non-empty WorktreePath for that role instead, which is the stricter
        // check of the two. Running this one first would reject the role lock with "an ext4 worktree
        // path is required", i.e. demand the very capability the lock removes.
        if (!request.WithoutRepositoryAccess)
        {
            RejectNonExt4Source(request.WorktreePath);
        }

        // G2 control 1 is enforced inside CredTmpfsSpec.Create; assert again defensively in case a
        // spec was constructed directly.
        if (request.Credentials.AgentUid == request.Credentials.SupervisorUid)
            throw new SandboxSpecException("G2 control 1: supervisor uid must differ from the agent uid.");

        var env = BuildProxyEnv(request.ProxyUrl);

        // MG-43: the cache environment travels WITH the cache mount and never without it. Telling a
        // package manager to put 1.7 GB at /var/cache/mainguard when nothing is mounted there points it
        // at the read-only rootfs — a confusing mid-restore failure — so the two are set together here
        // and re-asserted together by AssertPackageCache below.
        if (!string.IsNullOrEmpty(request.PackageCachePath))
        {
            env.AddRange(PackageCachePolicy.EnvironmentList());
        }

        env.AddRange(BuildToolchainEnv(request));

        var dns = ResolveDnsPinning(request);

        var hostConfig = new HostConfig
        {
            // G-15: no privilege escalation, plus the default-deny G2 seccomp profile. NEVER seccomp=unconfined.
            SecurityOpt = new List<string> { "no-new-privileges", SeccompProfile.SecurityOptValue },

            // G2 control 4: drop ALL capabilities and add back a minimal set with no SYS_PTRACE.
            CapDrop = new List<string> { "ALL" },
            CapAdd = MinimalCaps.ToList(),

            // MG-17: inherit the daemon's userns-remap. Docker's per-container knob has no value meaning
            // "definitely remap" — "" is "whatever dockerd does" and "host" is an explicit OPT-OUT — so
            // the empty string is the correct value here and AssertUsernsRemapped below refuses the
            // opt-out. The daemon-level fact is asserted at boot (FirstBootStep, UsernsRemapPolicy).
            UsernsMode = request.UsernsMode,

            Memory = request.Limits.MemoryBytes,

            // F28 follow-up: stated EXPLICITLY rather than left to dockerd's implicit default, because
            // the update endpoint cannot be given one without the other. moby's adaptContainerSettings
            // fills an unset MemorySwap with Memory*2 and persists THAT, so a container created without
            // this line still reports 2x — but `UpdateContainerAsync` validates the new Memory against
            // the swap total it is being sent, and a Memory-only update raising the ceiling above the
            // OLD swap total is rejected (409). Recording the value here is what lets RetightenCeiling
            // send the matching pair; the created posture itself is byte-for-byte what it always was.
            MemorySwap = MemorySwapFor(request.Limits.MemoryBytes),

            PidsLimit = request.Limits.Pids,

            // MG-26: a CPU ceiling (cgroup cpu.max) — without it one `while :; do :; done` per pid
            // starves every other jail AND the daemon on the shared VM. Memory+pids alone bound the
            // wrong axis: a busy loop allocates nothing and forks nothing.
            NanoCPUs = NanoCpus(request.Limits.Cpus),

            // MG-26: kernel rlimits, the ceilings cgroups do NOT cover. nofile is the descriptor-leak
            // bound (hit long before 512 pids); nproc is the outer fork-bomb backstop that survives a
            // cgroup misconfiguration — see SandboxLimits.NProc for why it sits ABOVE PidsLimit.
            Ulimits = new List<Ulimit>
            {
                new() { Name = "nofile", Soft = request.Limits.NoFile, Hard = request.Limits.NoFile },
                new() { Name = "nproc", Soft = request.Limits.NProc, Hard = request.Limits.NProc },
            },

            // MG-7: pin the jail's resolver to the proxy's NXDOMAIN-default dnsmasq. Left unset, Docker
            // injects its embedded resolver (127.0.0.11) which forwards to the VM's upstream DNS — every
            // name resolves and the pinned-DNS control never sits in the path at all.
            DNS = dns,

            // Read-only rootfs; writable surfaces are tmpfs only.
            ReadonlyRootfs = true,

            // The ext4 worktree at /workspace, plus (when the VM has dynamically installed agent CLIs)
            // the shared adapters root mounted READ-ONLY. The read-only adapters mount is what makes
            // CLI installs DYNAMIC: a CLI installed after provisioning reaches every new sandbox with
            // no image rebuild, while the agent can never tamper with the shared binaries.
            Mounts = BuildMounts(request),

            // Writable scratch + the secrets tmpfs (contents written post-start, never here).
            Tmpfs = BuildTmpfs(request),

            NetworkMode = request.NetworkName,

            // Belt-and-braces: never privileged (rejection trigger if ever flipped).
            Privileged = false,
        };

        var create = new CreateContainerParameters
        {
            Name = ContainerName(request.RepoHash, request.AgentId),
            Hostname = "agent",
            Image = request.ImageRef,
            User = request.Credentials.AgentUid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            WorkingDir = WorkspaceTarget,
            Env = env,
            // The jail's own identity card. It is the ONLY thing that survives a daemon restart — the live
            // session store is in-memory — so everything the daemon needs to adopt this jail back into a
            // real, correctly-typed session has to be on it. Before the kind/role pair was stamped here, a
            // restart could recover THAT a jail existed but not WHAT it was, and a surviving coordinator
            // came back as an anonymous worker.
            Labels = new Dictionary<string, string>
            {
                ["mainguard.repo"] = request.RepoHash,
                ["mainguard.agent"] = request.AgentId,
                ["mainguard.role"] = "agent",
                [DockerAgentLister.KindLabel] = request.AgentKind,
                [DockerAgentLister.AgentRoleLabel] = request.AgentRole,
                [DockerAgentLister.AgentParentLabel] = request.AgentParentId,
            },
            HostConfig = hostConfig,
        };

        // Re-assert the G2 per-container controls on the finished request. Dropping any is a typed
        // builder error, not a warning (rejection trigger: shipping fewer than all four G2 controls).
        AssertG2Controls(create, request.Credentials);
        AssertSecretDirsOwned(create, request.Credentials);
        AssertNoSecretsInEnv(create);
        AssertResourceCeilings(create);
        AssertDnsPinned(create, request);
        AssertUsernsRemapped(create);
        AssertPackageCache(create, request);

        return create;
    }

    /// <summary>
    /// MG-43 — re-asserts the package cache's shape on the finished request, in the same style as the G2
    /// quartet. Four separate properties, each individually a way for the feature to be quietly wrong:
    ///
    /// <list type="number">
    ///   <item><b>The cache is not inside the verified worktree.</b> A cache under
    ///   <see cref="WorkspaceTarget"/> puts gigabytes of untracked files in the tree an agent commits
    ///   from and the merge queue verifies — one <c>git add -A</c> from being in a reviewed diff. This is
    ///   the explicitly-rejected non-solution, so it is a typed builder error rather than a convention.</item>
    ///   <item><b>The cache is not inside <see cref="AgentHome"/>.</b> <c>$HOME</c> is the 256 MiB tmpfs
    ///   whose exhaustion is the entire reason this exists; a target under it would leave the feature
    ///   looking wired up and changing nothing.</item>
    ///   <item><b>The mount is read-write.</b> A read-only package cache fails a restore halfway with a
    ///   permission error rather than at the start with a clear one.</item>
    ///   <item><b>Environment and mount agree, in both directions.</b> Environment naming a cache that is
    ///   not mounted points a package manager at the read-only rootfs; a mount with no environment is
    ///   1.7 GB of bind mount that nothing uses while the tmpfs fills up anyway. Either one is a change
    ///   that looks applied and is not — the recurring bug class here — so both directions are checked.</item>
    /// </list>
    /// </summary>
    private static void AssertPackageCache(CreateContainerParameters create, ContainerSpecRequest request)
    {
        var mounts = create.HostConfig.Mounts ?? new List<Mount>();
        var cacheMount = mounts.FirstOrDefault(m =>
            string.Equals(m.Target, PackageCachePolicy.SandboxMount, StringComparison.Ordinal));
        var env = create.Env ?? new List<string>();
        var envNames = PackageCachePolicy.EnvironmentNames();
        var envPresent = envNames.Any(name =>
            env.Any(e => e.StartsWith(name + "=", StringComparison.Ordinal)));

        if (string.IsNullOrEmpty(request.PackageCachePath))
        {
            // No cache requested: nothing may claim there is one.
            if (cacheMount is not null)
                throw new SandboxSpecException(
                    $"MG-43: no package cache was requested, but a mount targets '{PackageCachePolicy.SandboxMount}'.");
            if (envPresent)
                throw new SandboxSpecException(
                    "MG-43: no package cache was requested, but the environment names one — a package manager "
                    + $"pointed at '{PackageCachePolicy.SandboxMount}' with nothing mounted there writes into the "
                    + "read-only rootfs and fails mid-restore.");
            return;
        }

        if (cacheMount is null)
            throw new SandboxSpecException(
                $"MG-43: a package cache at '{request.PackageCachePath}' was requested but no mount targets "
                + $"'{PackageCachePolicy.SandboxMount}'.");

        if (IsWithin(cacheMount.Target, WorkspaceTarget))
            throw new SandboxSpecException(
                $"MG-43: the package cache is mounted at '{cacheMount.Target}', inside the verified worktree "
                + $"'{WorkspaceTarget}'. That is the rejected non-solution: it puts the dependency closure in the "
                + "tree the agent commits from and the merge queue verifies.");

        if (IsWithin(cacheMount.Target, AgentHome))
            throw new SandboxSpecException(
                $"MG-43: the package cache is mounted at '{cacheMount.Target}', inside the tmpfs $HOME "
                + $"'{AgentHome}' whose 256 MiB ceiling is the reason the cache exists.");

        if (cacheMount.ReadOnly)
            throw new SandboxSpecException(
                "MG-43: the package cache mount is read-only; a package manager that cannot write its cache "
                + "fails partway through a restore instead of at the start.");

        foreach (var name in envNames)
        {
            if (!env.Any(e => e.StartsWith(name + "=", StringComparison.Ordinal)))
                throw new SandboxSpecException(
                    $"MG-43: the package cache is mounted but '{name}' is not in the environment, so that package "
                    + "manager still fills the 256 MiB tmpfs $HOME and the cache changes nothing for it.");
        }
    }

    /// <summary>Whole-segment containment for container paths: <c>/workspace-cache</c> is not inside
    /// <c>/workspace</c>, while <c>/workspace</c> and <c>/workspace/x</c> both are.</summary>
    private static bool IsWithin(string? path, string ancestor)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var trimmed = path.TrimEnd('/');
        var root = ancestor.TrimEnd('/');
        return string.Equals(trimmed, root, StringComparison.Ordinal)
               || trimmed.StartsWith(root + "/", StringComparison.Ordinal);
    }

    /// <summary>The stable per-repo/per-agent container name (drives the persistent-jail lookup).</summary>
    public static string ContainerName(string repoHash, string agentId)
    {
        var shortHash = repoHash.Length > 12 ? repoHash[..12] : repoHash;
        var safeAgent = new string(agentId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return $"mainguard-{shortHash}-{safeAgent}";
    }

    /// <summary>
    /// The <c>PATH</c> the agent base image bakes, verbatim from its final <c>ENV PATH=</c> line.
    ///
    /// <para><b>Why this is duplicated here.</b> A runtime-mount toolchain has to go on <c>PATH</c>
    /// AHEAD of the base image's curated tools — a repository that declares Python must get the pinned
    /// interpreter, not the incidental <c>/opt/toolchain/bin/python3</c> that has no pip. Docker's
    /// <c>Env</c> does no shell expansion, so there is no <c>$PATH</c> to prepend to: the value handed to
    /// <c>CreateContainerAsync</c> must be complete. That makes this constant a copy, and a copy that
    /// drifts is a jail whose PATH silently loses the adapters mount or the nix profile — so
    /// <c>ContainerSpecBuilderTests.BaseImagePath_MatchesTheAgentBaseImage</c> reads the Dockerfile and
    /// fails if the two ever disagree, the same guard the catalog's nixpkgs revision already carries.</para>
    /// </summary>
    public const string BaseImagePath =
        "/opt/mainguard/adapters/bin:/opt/toolchain/bin:/nix/var/nix/profiles/default/bin:/usr/local/bin:/usr/bin:/bin";

    /// <summary>
    /// The environment a jail needs in order to USE the toolchains its repository declared: their bin
    /// directories at the front of <c>PATH</c>, plus whatever each one needs to find itself.
    ///
    /// <para>Only <see cref="ToolchainDelivery.RuntimeMount"/> toolchains appear here. An image-layer
    /// toolchain baked its own <c>ENV PATH</c> and <c>ENV</c> lines into the layer at build time
    /// (<see cref="ToolchainProvisioner.RenderDockerfile"/>), and setting them again here would override
    /// the image's own with a value computed from a different source.</para>
    ///
    /// <para>An id that is declared but whose toolchain is not installed contributes nothing and is NOT
    /// an error here — this builder is pure and cannot see the VM's filesystem. The check that the
    /// toolchain is actually present belongs where it can be observed, and it is made there: by the
    /// spawn path before the jail is created, and again by the verification path inside the live jail.</para>
    /// </summary>
    internal static List<string> BuildToolchainEnv(ContainerSpecRequest request)
    {
        var env = new List<string>();
        if (request.ToolchainIds is not { Count: > 0 } || string.IsNullOrEmpty(request.ToolchainsRootPath))
        {
            return env;
        }

        var recipes = request.ToolchainIds
            .Select(ToolchainCatalog.TryGet)
            .Where(r => r is { Delivery: ToolchainDelivery.RuntimeMount })
            .Select(r => r!)
            .ToList();

        if (recipes.Count == 0)
        {
            return env;
        }

        var pathEntries = recipes.SelectMany(r => r.PathEntries).ToList();
        env.Add("PATH=" + string.Join(':', pathEntries) + ":" + BaseImagePath);

        foreach (var (name, value) in recipes.SelectMany(r => r.Environment))
        {
            env.Add($"{name}={value}");
        }

        return env;
    }

    private static List<string> BuildProxyEnv(string proxyUrl)
    {
        // Only proxy routing — NEVER a secret (G-13). Both upper- and lower-case forms so every
        // toolchain honours the proxy; NO_PROXY carries loopback and nothing else.
        //
        // AUDIT F33: `git.mainguard.internal` used to be in this list. Nothing anywhere resolves that
        // name — no DNS record on any segment, no `url.insteadOf` rewriting to it, and the A6
        // DaemonGitProxy it was reserved for has only test callers — so the entry described a route that
        // does not exist. That is worse than useless in a default-deny design: it is a standing
        // pre-authorisation to BYPASS the proxy for a hostname, sitting in every jail's environment,
        // waiting for the day something makes the name resolve. Re-add it in the same commit that gives
        // the name an address, not before.
        return new List<string>
        {
            $"HTTP_PROXY={proxyUrl}",
            $"HTTPS_PROXY={proxyUrl}",
            $"http_proxy={proxyUrl}",
            $"https_proxy={proxyUrl}",
            "NO_PROXY=localhost,127.0.0.1,::1",
            "no_proxy=localhost,127.0.0.1,::1",
            // CLIs must not self-update: versions are pinned by the adapter channel (sha256-verified
            // installs into a mount the jail sees READ-ONLY), so an in-CLI updater can only fail —
            // claude-code's footer showed a permanent "Auto-update failed" until this was set.
            "DISABLE_AUTOUPDATER=1",
        };
    }

    /// <summary>
    /// MG-7 — validates the requested resolver pin and turns it into the <c>HostConfig.Dns</c> list.
    ///
    /// <para>Two rules, both fail-closed. (1) A jail on the default-deny agent network MUST carry a pin:
    /// that network exists so the proxy is the only route out, and an unpinned resolver hands the jail
    /// Docker's embedded <c>127.0.0.11</c> — which resolves EVERY name (the rendered NXDOMAIN dnsmasq is
    /// simply never asked), so DNS-tunnelled exfiltration walks straight out of the "default-deny"
    /// network. (2) The pin itself must be a real IPv4 literal and must not be a loopback address:
    /// <c>127.0.0.11</c> is exactly the resolver we are replacing, and any 127/8 address inside the
    /// jail's own netns points at the jail, not at the proxy.</para>
    /// </summary>
    private static List<string>? ResolveDnsPinning(ContainerSpecRequest request)
    {
        var address = request.DnsServerAddress?.Trim();
        // MG-36: the gate keys on the CLASS of network, not on one literal name. It used to compare
        // against `mainguard-agents` alone, so the moment a jail moved onto its own per-agent segment
        // this fail-closed check would have quietly stopped applying — an unpinned jail with Docker's
        // 127.0.0.11 resolver, and no error anywhere.
        var onDefaultDenyNetwork = EgressProxyConfigurator.IsDefaultDenyAgentNetwork(request.NetworkName);

        if (string.IsNullOrEmpty(address))
        {
            if (onDefaultDenyNetwork)
                throw new SandboxSpecException(
                    $"MG-7: a jail on the default-deny network '{request.NetworkName}' must pin its resolver to the "
                    + "egress proxy's dnsmasq; with no HostConfig.Dns Docker injects its embedded 127.0.0.11 resolver and the pinned-DNS "
                    + "control never sits in the resolution path.");
            return null;
        }

        if (!System.Net.IPAddress.TryParse(address, out var parsed)
            || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new SandboxSpecException($"MG-7: the pinned DNS server '{address}' is not an IPv4 literal; Docker's Dns list takes addresses, not names.");

        if (System.Net.IPAddress.IsLoopback(parsed))
            throw new SandboxSpecException(
                $"MG-7: refusing to pin DNS at loopback '{address}'. Inside the jail's netns 127/8 is the jail itself, and 127.0.0.11 is "
                + "precisely Docker's embedded resolver this pin exists to replace.");

        return new List<string> { address };
    }

    /// <summary>Whole cores → Docker's <c>NanoCPUs</c> (1e9 nanoCPU = 1 core).</summary>
    internal static long NanoCpus(double cpus) => (long)Math.Round(cpus * 1_000_000_000d, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Docker's <c>MemorySwap</c> for a memory ceiling: the memory+swap TOTAL, not the swap allowance.
    /// <c>Memory * 2</c> is exactly the value moby's <c>adaptContainerSettings</c> writes when the field
    /// is left unset with a memory limit present, so naming it changes no jail's posture — it makes the
    /// number available to the update endpoint, which needs the pair or it rejects the raise.
    /// </summary>
    internal static long MemorySwapFor(long memoryBytes) => memoryBytes * 2;

    /// <summary>
    /// What a jail we are about to REUSE is missing, relative to the posture this builder would create
    /// it with today (audit F28).
    /// </summary>
    /// <param name="Recreate">Reasons that can only be fixed by creating a new container: every G2/G-15
    /// hardening control, and the MG-26 rlimits, are fixed at create time.</param>
    /// <param name="Ceiling">Reasons the engine CAN change on a live container: memory, CPU and pids are
    /// all writable through Docker's container-update endpoint.</param>
    public sealed record JailPostureVerdict(
        IReadOnlyList<string> Recreate, IReadOnlyList<string> Ceiling)
    {
        /// <summary>The jail must be destroyed and rebuilt — its posture cannot be repaired in place.</summary>
        public bool MustRecreate => Recreate.Count > 0;

        /// <summary>The jail's resource ceiling has drifted and can be re-applied without recreating it.</summary>
        public bool MustRetighten => Ceiling.Count > 0;

        public string Describe() => string.Join("; ", Recreate.Concat(Ceiling));

        internal static readonly JailPostureVerdict Clean =
            new(Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>
    /// <b>Audit F28 — does this already-running jail still match the posture we would create today?</b>
    ///
    /// <para>The reuse path in <see cref="DockerSandboxEngine"/> re-checks mounts, DNS, network and the
    /// secret layout, and every one of those questions is "was this container created by a build that
    /// knows about X?". Two things it never asked were the ones an operator can change from the UI and
    /// the ones a Mainguard upgrade tightens: the per-jail ceiling, and the hardening set. So lowering
    /// the ceiling reached no jail already running, and a jail created before MG-26 was reused
    /// indefinitely with no CPU cap at all — hardened everywhere except the axis a prompt-injected agent
    /// reaches with <c>while :; do :; done</c>.</para>
    ///
    /// <para><b>The split between the two lists is the whole design.</b> Hardening is fixed at create,
    /// so drift there can only be answered by recreating — the same answer every other reuse check
    /// gives. A ceiling is not: Docker's update endpoint writes memory, CPU and pids to a live cgroup,
    /// so the ceiling is re-applied IN PLACE and a running agent keeps its session. Recreating for a
    /// ceiling change would mean an operator moving a slider killed every live jail, which is a worse
    /// product than the bug.</para>
    ///
    /// <para>Pure, and it takes the engine's <see cref="HostConfig"/> as an argument, so every drift
    /// shape is unit-assertable with no Docker daemon. A null <paramref name="actual"/> reports NO drift:
    /// the convention across every sibling probe is that an unanswerable question is not a reason to
    /// destroy a container.</para>
    /// </summary>
    public static JailPostureVerdict InspectPosture(HostConfig? actual, SandboxLimits expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (actual is null)
        {
            return JailPostureVerdict.Clean;
        }

        var recreate = new List<string>();
        var ceiling = new List<string>();

        if (actual.Privileged)
            recreate.Add("the jail is privileged");
        if (!actual.ReadonlyRootfs)
            recreate.Add("the rootfs is writable (created before ReadonlyRootfs)");

        var capDrop = actual.CapDrop ?? new List<string>();
        if (!capDrop.Any(c => string.Equals(c, "ALL", StringComparison.OrdinalIgnoreCase)))
            recreate.Add("G2 control 4: capabilities were not dropped (no CapDrop ALL)");

        var capAdd = actual.CapAdd ?? new List<string>();
        if (capAdd.Any(c => c.Contains("SYS_PTRACE", StringComparison.OrdinalIgnoreCase)))
            recreate.Add("G2 control 4: CAP_SYS_PTRACE is in the effective set");

        var securityOpt = actual.SecurityOpt ?? new List<string>();
        if (!securityOpt.Any(o => o.Contains("no-new-privileges", StringComparison.OrdinalIgnoreCase)))
            recreate.Add("G-15: no-new-privileges is missing");

        var seccomp = securityOpt.FirstOrDefault(o => o.StartsWith("seccomp=", StringComparison.Ordinal));
        if (seccomp is null)
            recreate.Add("G2 control 3: no seccomp profile");
        else if (seccomp.Contains("unconfined", StringComparison.OrdinalIgnoreCase))
            recreate.Add("G2 control 3: seccomp=unconfined");

        if (string.Equals(actual.UsernsMode, UsernsRemapPolicy.OptOutUsernsMode, StringComparison.OrdinalIgnoreCase))
            recreate.Add("MG-17: the jail opts OUT of the daemon's userns remap");

        // MG-26 rlimits. Docker's update endpoint does not write ulimits, so an absent or slack one is a
        // recreate, not a retighten. Checked by presence-and-value: these are compiled constants, so the
        // only way they differ is a Mainguard upgrade — exactly when a recreate is the right answer.
        var ulimits = actual.Ulimits ?? new List<Ulimit>();
        foreach (var (name, want) in new[] { ("nofile", expected.NoFile), ("nproc", expected.NProc) })
        {
            var found = ulimits.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.Ordinal));
            if (found is null)
                recreate.Add($"MG-26: no '{name}' ulimit");
            else if (found.Hard != want || found.Soft != want)
                recreate.Add($"MG-26: '{name}' ulimit is {found.Soft}/{found.Hard}, expected {want}/{want}");
        }

        if (actual.Memory != expected.MemoryBytes)
            ceiling.Add($"memory is {actual.Memory}, expected {expected.MemoryBytes}");

        // The memory+swap TOTAL, and it is not decoration: an update that raises Memory past the swap
        // total the container currently carries is REFUSED by the engine, so a ceiling raise that did
        // not also move this one could never apply. Drift here is by definition drift in memory too
        // (both are a pure function of MemoryBytes), so it costs no extra recreate — it is listed so the
        // retighten is told what to send and the reason names the axis that would otherwise 409.
        var wantMemorySwap = MemorySwapFor(expected.MemoryBytes);
        if (actual.MemorySwap != wantMemorySwap)
            ceiling.Add($"memory+swap total is {actual.MemorySwap}, expected {wantMemorySwap}");

        var wantNanoCpus = NanoCpus(expected.Cpus);
        if (actual.NanoCPUs != wantNanoCpus)
        {
            ceiling.Add(actual.NanoCPUs <= 0
                ? $"no CPU ceiling at all (created before MG-26), expected {wantNanoCpus} NanoCPUs"
                : $"CPU ceiling is {actual.NanoCPUs} NanoCPUs, expected {wantNanoCpus}");
        }

        if ((actual.PidsLimit ?? 0) != expected.Pids)
            ceiling.Add($"pids ceiling is {actual.PidsLimit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unset"}, expected {expected.Pids}");

        return new JailPostureVerdict(recreate, ceiling);
    }

    private static void AssertResourceCeilings(CreateContainerParameters create)
    {
        // MG-26: re-assert on the finished request, in the same style as the G2 quartet — an unbounded
        // axis is a builder error, not a warning. A jail with no CPU or descriptor ceiling is a
        // one-liner away from taking the whole VM (and every other agent) down.
        var host = create.HostConfig;
        if (host.Memory <= 0)
            throw new SandboxSpecException("MG-26: the agent jail must carry a memory ceiling.");
        // A memory ceiling with unbounded swap (MemorySwap = -1) is not a memory ceiling: the jail
        // simply spills past it onto disk. Asserted rather than assumed, because the field is now set
        // explicitly and an explicit field is one a future edit can get wrong.
        if (host.MemorySwap < host.Memory)
            throw new SandboxSpecException(
                "MG-26: the agent jail's memory+swap total must be at least its memory ceiling; "
                + $"got MemorySwap={host.MemorySwap} for Memory={host.Memory} "
                + "(-1 or 0 would leave swap unbounded, which defeats the ceiling).");
        if (host.PidsLimit is null or <= 0)
            throw new SandboxSpecException("MG-26: the agent jail must carry a pids ceiling.");
        if (host.NanoCPUs <= 0)
            throw new SandboxSpecException("MG-26: the agent jail must carry a CPU ceiling (NanoCPUs); memory+pids do not bound a busy loop.");

        var ulimits = host.Ulimits ?? new List<Ulimit>();
        foreach (var required in new[] { "nofile", "nproc" })
        {
            var limit = ulimits.FirstOrDefault(u => string.Equals(u.Name, required, StringComparison.Ordinal));
            if (limit is null || limit.Hard <= 0 || limit.Soft <= 0)
                throw new SandboxSpecException($"MG-26: the agent jail must carry a positive '{required}' ulimit.");
        }
    }

    /// <summary>
    /// MG-17 — the jail must not opt OUT of the daemon's user-namespace remap.
    ///
    /// <para>Docker's <c>UsernsMode</c> is asymmetric: <c>""</c> means "inherit whatever dockerd does"
    /// and there is no value that means "definitely remap", but <c>"host"</c> is a hard opt-out that puts
    /// the container back on host uids — container root becomes host root — while every other flag on
    /// this request still reads as fully hardened. That is precisely the shape of regression that ships
    /// unnoticed, so it is a typed builder error here, in the same style as the G2 quartet. Anything that
    /// is neither empty nor a recognised mode is refused too: Docker would reject it at create, and a
    /// spec whose isolation posture nobody can name must not reach the daemon.</para>
    /// </summary>
    private static void AssertUsernsRemapped(CreateContainerParameters create)
    {
        var mode = create.HostConfig.UsernsMode ?? string.Empty;
        if (mode.Length == 0)
            return;

        throw new SandboxSpecException(
            $"MG-17: HostConfig.UsernsMode is '{mode}'. The agent jail must inherit the daemon's userns-remap "
            + $"(UsernsMode = UsernsRemapPolicy.InheritDaemonRemap); '{UsernsRemapPolicy.OptOutUsernsMode}' opts the "
            + "container OUT of it, which restores host uids (container root = host root) and makes every write "
            + "through a bind mount land as the VM's own service uid again.");
    }

    private static void AssertDnsPinned(CreateContainerParameters create, ContainerSpecRequest request)
    {
        // MG-7 re-assert: the pin survived onto the request the daemon is about to POST. A future edit
        // that drops HostConfig.Dns silently restores 127.0.0.11 and un-does pinned DNS wholesale, and
        // no egress test would notice — the existing exfil probe used a name that NXDOMAINs everywhere.
        if (!EgressProxyConfigurator.IsDefaultDenyAgentNetwork(request.NetworkName))
            return;

        var dns = create.HostConfig.DNS;
        if (dns is null || dns.Count != 1 || string.IsNullOrWhiteSpace(dns[0]))
            throw new SandboxSpecException(
                "MG-7: the create request for a default-deny jail must pin exactly one resolver (the egress proxy's dnsmasq).");
    }

    private static void RejectNonExt4Source(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new SandboxSpecException("The worktree mount source is empty; an ext4 worktree path is required (G-11).");

        // Any backslash means a Windows/UNC path — an ext4 path never contains one.
        if (source.Contains('\\') || source.StartsWith("//", StringComparison.Ordinal))
            throw new SandboxSpecException($"Refusing UNC/Windows mount source '{source}': only ext4 worktree paths may be mounted (G-11).");

        if (WslDrvfsMount.IsMatch(source))
            throw new SandboxSpecException($"Refusing drvfs mount source '{source}': /mnt/<drive> is a Windows filesystem (G-11).");

        if (WindowsDrive.IsMatch(source))
            throw new SandboxSpecException($"Refusing Windows drive mount source '{source}' (G-11).");
    }

    private static void AssertG2Controls(CreateContainerParameters create, CredTmpfsSpec creds)
    {
        var securityOpt = create.HostConfig.SecurityOpt ?? new List<string>();

        // Control 3: the default-deny seccomp profile is present and NOT unconfined.
        var seccomp = securityOpt.FirstOrDefault(o => o.StartsWith("seccomp=", StringComparison.Ordinal));
        if (seccomp is null)
            throw new SandboxSpecException("G2 control 3: the seccomp denylist is missing from SecurityOpt.");
        if (seccomp.Contains("unconfined", StringComparison.OrdinalIgnoreCase))
            throw new SandboxSpecException("G2 control 3: seccomp=unconfined is forbidden.");

        // Read the profile's RULES, never its text. This loop used to be a substring search over the
        // whole `seccomp=<json>` blob — it only checked that the NAME `ptrace` appeared somewhere — and
        // stock moby's profile carries all three names in its ALLOW group. The guard for this profile's
        // sole hardening delta was therefore one the un-hardened upstream profile also passes.
        var gap = SeccompProfile.DescribeDenialGap(seccomp["seccomp=".Length..]);
        if (gap is not null)
            throw new SandboxSpecException(gap);

        if (!securityOpt.Contains("no-new-privileges"))
            throw new SandboxSpecException("G-15: no-new-privileges is missing from SecurityOpt.");

        // Control 4: no CAP_SYS_PTRACE in the effective set (= what CapAdd restores after dropping ALL).
        var capAdd = create.HostConfig.CapAdd ?? new List<string>();
        if (capAdd.Any(c => c.Contains("SYS_PTRACE", StringComparison.OrdinalIgnoreCase)))
            throw new SandboxSpecException("G2 control 4: CAP_SYS_PTRACE must not be in the agent capability set.");
        var capDrop = create.HostConfig.CapDrop ?? new List<string>();
        if (!capDrop.Any(c => string.Equals(c, "ALL", StringComparison.OrdinalIgnoreCase)))
            throw new SandboxSpecException("G2 control 4: capabilities must be dropped (CapDrop ALL) before any are added back.");

        // Control 1: the supervisor-uid ownership of the K/credential tmpfs is expressed in the spec.
        if (creds.SupervisorUid == creds.AgentUid)
            throw new SandboxSpecException("G2 control 1: supervisor uid must differ from the agent uid.");
        if (creds.Mode != CredTmpfsSpec.SecretMode)
            throw new SandboxSpecException("G2 control 1: the secret tmpfs files must be mode 0400.");

        // Control 2 (ptrace_scope) is VM-wide (P2-05); it MUST NOT appear on the create request.
        AssertNoPtraceScopeSysctl(create);
    }

    /// <summary>The tmpfs options that make a directory the private property of one container uid.</summary>
    private static string OwnedSecretDirOptions(int uid)
    {
        var id = uid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"size=1m,mode={CredTmpfsSpec.OwnedDirMode},uid={id},gid={id}";
    }

    /// <summary>
    /// Every secret is written into a tmpfs directory ALREADY OWNED by the uid that writes it.
    ///
    /// <para>This is the structural half of the fix for the in-jail <c>chown</c> that could never
    /// succeed. The write path no longer has a fallback: it execs as the owner and does not chown, so
    /// if a future edit drops one of these mounts, moves a secret into a shared directory, or points
    /// two owners at the same directory, the write fails at runtime with an <c>EPERM</c> inside a jail
    /// — the exact failure this whole change exists to remove. Asserting it here turns that into a
    /// typed builder error before the container is ever created.</para>
    ///
    /// <para>Deliberately checks the directory of the ACTUAL path in the spec rather than the
    /// <see cref="CredTmpfsSpec"/> constants: the record is constructible with custom paths, and a
    /// check that reads the constants would pass while the container was built from something else.</para>
    /// </summary>
    private static void AssertSecretDirsOwned(CreateContainerParameters create, CredTmpfsSpec creds)
    {
        var tmpfs = create.HostConfig.Tmpfs ?? new Dictionary<string, string>();

        AssertOwnedBy(creds.CredentialPath, creds.AgentUid, "the agent credential file");
        AssertOwnedBy(creds.OobKeyPath, creds.SupervisorUid, "the OOB session key K");

        // G2 control 1 restated as a property of the LAYOUT: sharing one directory would put both
        // secrets back under a single owner and hand the agent uid write access to K's directory.
        if (string.Equals(
                CredTmpfsSpec.DirectoryOf(creds.CredentialPath),
                CredTmpfsSpec.DirectoryOf(creds.OobKeyPath), StringComparison.Ordinal))
        {
            throw new SandboxSpecException(
                "G2 control 1: the agent credential file and the OOB session key must live in DIFFERENT "
                + $"per-owner directories; both are in '{CredTmpfsSpec.DirectoryOf(creds.OobKeyPath)}'.");
        }

        void AssertOwnedBy(string path, int uid, string what)
        {
            var dir = CredTmpfsSpec.DirectoryOf(path);
            if (!tmpfs.TryGetValue(dir, out var options))
            {
                throw new SandboxSpecException(
                    $"The directory '{dir}' holding {what} ('{path}') is not a tmpfs on the create request, so "
                    + $"the in-jail write would have to create the file somewhere uid {uid} cannot write. "
                    + $"Mounted tmpfs: {string.Join(", ", tmpfs.Keys)}.");
            }

            var expected = OwnedSecretDirOptions(uid);
            if (!string.Equals(options, expected, StringComparison.Ordinal))
            {
                throw new SandboxSpecException(
                    $"The tmpfs at '{dir}' holding {what} must be mounted '{expected}' so uid {uid} OWNS it and "
                    + $"can create the secret without a chown (which no exec in this container can perform — "
                    + $"non-root User plus no-new-privileges leaves even a uid-0 exec with no CAP_CHOWN). "
                    + $"It is mounted '{options}'.");
            }
        }
    }

    private static void AssertNoPtraceScopeSysctl(CreateContainerParameters create)
    {
        // Defensive: Docker.DotNet's HostConfig.Sysctls would carry a per-container sysctl. We never
        // set kernel.yama.ptrace_scope (it is non-namespaced — P2-05's VM-boot job). If a future edit
        // adds it here, fail loudly.
        var sysctls = create.HostConfig.Sysctls;
        if (sysctls is not null && sysctls.Keys.Any(k => k.Contains("ptrace_scope", StringComparison.OrdinalIgnoreCase)))
            throw new SandboxSpecException("kernel.yama.ptrace_scope is VM-wide (P2-05); it must not be set on the container create request.");
    }

    /// <summary>
    /// G-13 — the create request's environment carries proxy routing and toolchain PATH only; a
    /// credential reaches a jail through the 0400 tmpfs and nowhere else.
    ///
    /// <para><b>Audit F33: this was name-shaped only.</b> An anonymously-named variable holding a real
    /// token — <c>ANTHROPIC_AUTH=sk-ant-…</c>, <c>GH=ghp_…</c> — passed a check built entirely out of
    /// KEY/TOKEN/SECRET substrings, which is precisely the shape a mistake takes: nobody writes
    /// <c>MY_SECRET_TOKEN=</c> by accident. So the VALUE is now checked too, against the same rule
    /// catalog the pre-commit scanner uses (<see cref="Mainguard.Git.Safety.SecretPatterns"/>) — the
    /// single place in this codebase where "does this text look like a credential" is written down, and
    /// one whose public surface is a bool by construction, so a refusal here can never echo the value it
    /// refused.</para>
    ///
    /// <para>The name rule is kept alongside it, not replaced: a variable NAMED like a secret is worth
    /// refusing even when its value is a placeholder, because the next edit fills it in.</para>
    /// </summary>
    /// <summary>The G-13 guard, reachable by the suite so a planted credential can be driven through it
    /// on a request the builder has already accepted — <see cref="Build"/> runs it on the way out, so
    /// there is otherwise no way to add an env entry and then ask.</summary>
    internal static void AssertNoSecretsInEnvForTests(CreateContainerParameters create) =>
        AssertNoSecretsInEnv(create);

    private static void AssertNoSecretsInEnv(CreateContainerParameters create)
    {
        foreach (var entry in create.Env ?? new List<string>())
        {
            var split = entry.Split('=', 2);
            var name = split[0];
            var value = split.Length > 1 ? split[1] : string.Empty;
            var upper = name.ToUpperInvariant();

            // The proxy variables carry a URL that is allowed to look like anything; nothing else is
            // exempt from either half of the check.
            if (upper is "HTTP_PROXY" or "HTTPS_PROXY" or "NO_PROXY") continue;

            if (upper.Contains("KEY") || upper.Contains("TOKEN") || upper.Contains("SECRET")
                || upper.Contains("PASSWORD") || upper.Contains("CREDENTIAL"))
                throw new SandboxSpecException($"G-13: environment variable '{name}' looks like a secret; secrets go on the 0400 tmpfs, never Env.");

            foreach (var rule in Mainguard.Git.Safety.SecretPatterns.All)
            {
                // The rule NAME and the variable NAME; never the value. The catalog's own invariant is
                // that a match cannot hand back what it matched, and this message keeps that true.
                if (rule.IsMatch(value))
                    throw new SandboxSpecException(
                        $"G-13: environment variable '{name}' carries a value matching the "
                        + $"'{rule.DisplayName}' rule; secrets go on the 0400 tmpfs, never Env.");
            }
        }
    }
}
