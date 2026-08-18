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
    string? BareRepoPath = null,
    string? DnsServerAddress = null,
    string? AgentRepoPath = null,
    string? PackageCachePath = null,
    string? ToolchainsRootPath = null,
    IReadOnlyList<string>? ToolchainIds = null,
    // ESC-I1: the substrate's daemon-owned roots; when supplied, every bind-mount source must sit
    // under one of them (see SandboxEngineOptions.AllowedMountRoots).
    IReadOnlyList<string>? AllowedMountRoots = null);

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

    /// <summary>True when <paramref name="path"/> equals a root or sits inside one (textual, in the
    /// daemon's own namespace — mount sources are daemon-produced paths, never user input).</summary>
    private static bool IsUnderAnyRoot(string path, IReadOnlyList<string> roots)
    {
        var full = Path.GetFullPath(path);
        foreach (var root in roots)
        {
            var fullRoot = Path.GetFullPath(root);
            if (string.Equals(full, fullRoot, Mainguard.Git.Services.FileSystemPaths.Comparison)) return true;
            var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, Mainguard.Git.Services.FileSystemPaths.Comparison)) return true;
        }
        return false;
    }

    /// <summary>Builds the hardened create request; throws typed on any invariant violation.</summary>
    public static CreateContainerParameters Build(ContainerSpecRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // G-11 enforced at CONSTRUCTION: the ONLY mount source is an ext4 worktree path. A drvfs
        // (/mnt/c/...), UNC (\\wsl.localhost\...), or drive-letter (C:\...) source is rejected here
        // so the container is never created.
        RejectNonExt4Source(request.WorktreePath);

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
            Tmpfs = new Dictionary<string, string>
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
            },

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
            Labels = new Dictionary<string, string>
            {
                ["mainguard.repo"] = request.RepoHash,
                ["mainguard.agent"] = request.AgentId,
                ["mainguard.role"] = "agent",
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
        // toolchain honours the proxy; NO_PROXY carries loopback + the internal git proxy host.
        return new List<string>
        {
            $"HTTP_PROXY={proxyUrl}",
            $"HTTPS_PROXY={proxyUrl}",
            $"http_proxy={proxyUrl}",
            $"https_proxy={proxyUrl}",
            "NO_PROXY=localhost,127.0.0.1,::1,git.mainguard.internal",
            "no_proxy=localhost,127.0.0.1,::1,git.mainguard.internal",
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
    private static long NanoCpus(double cpus) => (long)Math.Round(cpus * 1_000_000_000d, MidpointRounding.AwayFromZero);

    private static void AssertResourceCeilings(CreateContainerParameters create)
    {
        // MG-26: re-assert on the finished request, in the same style as the G2 quartet — an unbounded
        // axis is a builder error, not a warning. A jail with no CPU or descriptor ceiling is a
        // one-liner away from taking the whole VM (and every other agent) down.
        var host = create.HostConfig;
        if (host.Memory <= 0)
            throw new SandboxSpecException("MG-26: the agent jail must carry a memory ceiling.");
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

    private static void AssertNoSecretsInEnv(CreateContainerParameters create)
    {
        // G-13: the environment carries proxy routing ONLY. Any KEY/TOKEN/SECRET/PASSWORD-shaped var
        // is a leak — the credential path is the 0400 tmpfs, never Env.
        foreach (var entry in create.Env ?? new List<string>())
        {
            var name = entry.Split('=', 2)[0];
            var upper = name.ToUpperInvariant();
            var isProxy = upper is "HTTP_PROXY" or "HTTPS_PROXY" or "NO_PROXY";
            if (isProxy) continue;
            if (upper.Contains("KEY") || upper.Contains("TOKEN") || upper.Contains("SECRET")
                || upper.Contains("PASSWORD") || upper.Contains("CREDENTIAL"))
                throw new SandboxSpecException($"G-13: environment variable '{name}' looks like a secret; secrets go on the 0400 tmpfs, never Env.");
        }
    }
}
