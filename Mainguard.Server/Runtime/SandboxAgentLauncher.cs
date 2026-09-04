using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mainguard.Server.Runtime;

/// <summary>The real, jailed result of a spawn: the container id + whether a stopped jail was reused, plus the ext4 worktree.</summary>
/// <param name="LaunchCommand">The argv that starts the requested agent CLI inside the jail (from the
/// installed adapter's marker), or null when the kind maps to no installed CLI.</param>
public sealed record SandboxLaunchResult(
    string ContainerId, bool Reused, string WorktreePath, IReadOnlyList<string>? LaunchCommand = null);

/// <summary>
/// The daemon-side spawn chain (P2-06 → P2-07) behind <see cref="Services.AgentGrpcService.SpawnAgent"/>,
/// kept out of the gRPC class so the transport layer stays validation+dispatch only. It provisions the
/// per-agent worktree off the repo's bare mirror (<see cref="IAgentEnvironment.Worktrees"/>), ensures the
/// default-deny egress network + proxy exist (<see cref="IAgentEnvironment.Egress"/>), and starts the
/// hardened container (<see cref="IAgentEnvironment.Sandboxes"/>), returning the real container id.
///
/// <para><b>Graceful degradation (why no throwing stub):</b> when the repo is <i>not</i> provisioned there
/// is no bare mirror to branch a worktree from and nothing to jail, so <see cref="TryLaunchAsync"/> returns
/// <c>null</c> and the caller keeps a session-only record. This is the headless path the in-proc Alpha loop
/// smoke rides (no Docker), while a provisioned repo on a Docker host takes the real jail path — the leg the
/// <c>SandboxSpawnDockerTests</c> RequiresDocker test verifies in CI.</para>
/// </summary>
public sealed class SandboxAgentLauncher
{
    private const int AgentUid = 1000;
    private const int SupervisorUid = 1001;

    /// <summary>
    /// The key variable used when no adapter marker is present at all (unknown kind / dev box without a
    /// catalog). Named once so <see cref="BuildSecrets"/> and the ticket #52 confinement-refusal warnings
    /// cannot name different variables — a warning that reports the wrong variable is worse than none,
    /// since the operator would go looking for a key that is not there.
    /// </summary>
    private const string LegacyApiKeyEnvVar = "ANTHROPIC_API_KEY";

    private readonly IAgentEnvironment _environment;

    /// <summary>This daemon's worktree manager — the one that created the worktrees this launcher's jails
    /// are mounted on. Exposed so an in-daemon caller acting on a LIVE agent's worktree (the worker's
    /// <c>commit_work</c> op) reaches the same instance rather than constructing a second one over the
    /// same directories, which is how two managers come to disagree about a repository's state.</summary>
    public IAgentWorktreeManager Worktrees => _environment.Worktrees;

    private readonly string _imageRef;
    private readonly InstalledAdapterCatalog _adapters;
    private readonly ILogger _log;
    private readonly Gateway.AgentGatewayCredentials? _credentials;
    private readonly Gateway.GatewayConfinementOptions? _gatewayOptions;

    public SandboxAgentLauncher(
        IAgentEnvironment environment, InstalledAdapterCatalog? adapters = null, ILoggerFactory? loggerFactory = null,
        Gateway.AgentGatewayCredentials? credentials = null,
        Gateway.GatewayConfinementOptions? gatewayOptions = null)
    {
        // Optional so the many direct-construction tests (and the RequiresDocker spawn tests) keep
        // compiling unchanged; DI supplies both. Absent ⇒ no confinement ⇒ today's behaviour.
        _credentials = credentials;
        _gatewayOptions = gatewayOptions;
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        // Single source with the provisioner (SandboxImages.AgentBase) so the daemon preflights/spawns
        // exactly the tag the app builds/labels — including a MAINGUARD_AGENT_IMAGE override.
        _imageRef = SandboxImageVersions.AgentBaseRef();
        // The dynamically installed CLIs (the user's OOBE/settings choices), read fresh per spawn so a
        // CLI installed while the daemon runs is immediately launchable.
        _adapters = adapters ?? new InstalledAdapterCatalog();
        // Optional so the RequiresDocker direct-construction tests keep working; DI supplies the real one.
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger(DaemonLogCategories.Spawn);
    }

    /// <summary>
    /// The agent kinds this daemon can actually launch — read fresh, because a CLI installed while the
    /// daemon runs must be spawnable without a restart. Surfaced here rather than by injecting the catalog
    /// a second time into <see cref="AgentSpawnService"/>: one catalog instance means the refusal a
    /// coordinator gets and the jail the launcher would have built cannot be answering different registries.
    /// </summary>
    internal IReadOnlyList<string> InstalledAgentKinds => _adapters.InstalledKinds();

    /// <summary>
    /// Provisions the worktree and starts the hardened jail for <paramref name="agentId"/> against the
    /// repo identified by <paramref name="repoHandle"/>. Returns the real container handle, or <c>null</c>
    /// when the repo is not provisioned (session-only path). On a failure <i>after</i> the worktree exists,
    /// the half-made worktree is cleaned up so no residue survives, then the failure propagates.
    /// </summary>
    /// <param name="withoutRepositoryAccess">
    /// Coordinator contract §2/§8 — the role lock. A coordinator is <i>only</i> an orchestrator: it gets no
    /// worktree, no per-agent git repo, no mirror mount and no package cache, because "the coordinator has
    /// no worktree, no git credentials and no view of repository contents". Set for
    /// <see cref="AgentRoles.Coordinator"/> and nothing else; <see cref="ContainerSpecBuilder"/> re-asserts
    /// it fail-closed, so a caller that later passes a repository path alongside this flag gets a typed
    /// spawn failure rather than silently regaining the capability.
    ///
    /// <para>Outranks <paramref name="adoptExistingBranch"/>: a coordinator has no branch to resume onto,
    /// so the two set together is a caller error rather than a third behaviour, and the role lock is the
    /// safe way to resolve it.</para>
    /// </param>
    /// <param name="adoptExistingBranch">
    /// <b>Resume.</b> When true the worktree is ADOPTED onto this id's existing <c>agent/&lt;id&gt;</c>
    /// branch rather than created on a new one, so a jail started for a stranded queue entry begins on the
    /// commits the dead jail left behind. Two things change with it, both of them load-bearing: an absent
    /// branch is a typed refusal instead of a fresh branch off main, and the post-failure cleanup preserves
    /// the branch instead of deleting it — a rollback that ran the ordinary teardown would destroy the one
    /// copy of the work the resume exists to recover.
    /// </param>
    public async Task<SandboxLaunchResult?> TryLaunchAsync(
        string repoHandle, string agentId, string agentKind, string? modelApiKey,
        string? ipcDirPath = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? extraEnv = null,
        IReadOnlyList<SandboxCredentialFile>? cliCredentials = null,
        bool withoutRepositoryAccess = false,
        IProgress<string>? progress = null,
        bool adoptExistingBranch = false,
        IReadOnlyList<SandboxSettingsFile>? cliSettings = null,
        string agentRole = "",
        Mainguard.Agents.Agents.Orchestrator.WorkerPlanMode planMode =
            Mainguard.Agents.Agents.Orchestrator.WorkerPlanMode.Gated,
        string agentParentId = "")
    {
        _log.LogInformation("launch begin: repo={Repo} kind={Kind}", repoHandle, agentKind);

        // The file-framed half of the agent-IPC channel, mounted read-write ONLY where the socket half
        // cannot work. Derived here rather than passed in: the caller supplies the endpoint dir, and the
        // outbox is a fixed child of it (AgentIpcPaths.OutboxIn) that the spec builder re-derives and
        // checks — so the read-write mount cannot be pointed anywhere else by a caller bug.
        var ipcOutboxPath = string.IsNullOrEmpty(ipcDirPath)
            || _environment.Capabilities.SupportsBindMountedUnixSockets
                ? null
                : Mainguard.Agents.Agents.Ipc.AgentIpcPaths.OutboxIn(ipcDirPath);

        var barePath = _environment.Repos.BareRepoPathFor(repoHandle);
        if (!Directory.Exists(barePath))
        {
            // Repo not provisioned — nothing to branch a worktree from, nothing to jail. The caller keeps
            // a session-only record (the daemon still tracks/streams/stops it) rather than fabricating a jail.
            _log.LogInformation("repo not provisioned — session-only (no jail): repo={Repo}", repoHandle);
            return null;
        }

        // Spawn preflight (field failure 2026-07-17, twice): a fresh MainguardEnv import AND the
        // tier-2 VM upgrade both leave the docker image store empty (it lives outside /home/mainguard,
        // so the migration correctly skips it). Verify BOTH jail images BEFORE any worktree/jail is
        // made — present AND current (the mainguard.image.version label == the expected constant) — so
        // the failure is one typed, actionable error naming the missing/outdated image instead of a
        // DockerImageNotFoundException at container-create (agent-base), an opaque create failure
        // inside Egress.EnsureReadyAsync (egress-proxy), or a silently-stale image running old bytes.
        var problems = new List<SandboxImagePreflightProblem>();
        string? pinnedImageRef = null;
        foreach (var imageRef in new[] { _imageRef, EgressProxyConfigurator.DefaultImageRef })
        {
            if (!await _environment.Sandboxes.ImageExistsAsync(imageRef, ct).ConfigureAwait(false))
            {
                problems.Add(new SandboxImagePreflightProblem(imageRef, Stale: false));
                continue;
            }

            // MG-27: resolve the MUTABLE ref to the image's immutable content digest, once, HERE — and
            // spawn from that digest below. `:latest` is a pointer: without this the preflight verifies
            // whatever the tag happened to name at check time and the create then re-resolves the tag,
            // so nothing ties the image that was checked to the image that runs. A digest cannot be
            // re-pointed, and unlike the mainguard.image.version label it cannot be chosen by whoever
            // built the image. An engine with no image store answers null and stays on its ref.
            var digest = await _environment.Sandboxes.ImageDigestAsync(imageRef, ct).ConfigureAwait(false);
            if (string.Equals(imageRef, _imageRef, StringComparison.Ordinal))
            {
                pinnedImageRef = digest;
            }

            // An image we don't version (a fully-renamed MAINGUARD_AGENT_IMAGE override) is
            // presence-only — we have no expected hash to compare against. It is still digest-pinned:
            // the pin is about "the bytes we checked are the bytes that run", which holds regardless of
            // whether we can say anything about WHICH bytes they ought to be.
            var expected = SandboxImageVersions.For(imageRef);
            if (expected is null)
            {
                continue;
            }

            var installed = await _environment.Sandboxes.ImageVersionAsync(imageRef, ct).ConfigureAwait(false);
            if (!string.Equals(installed, expected, StringComparison.Ordinal))
            {
                problems.Add(new SandboxImagePreflightProblem(imageRef, Stale: true));
            }
        }

        if (problems.Count > 0)
        {
            _log.LogError("preflight failed: sandbox image(s) need provisioning: {Images}",
                string.Join(", ", problems.Select(p => $"{p.ImageRef} ({(p.Stale ? "stale" : "missing")})")));
            throw new SandboxImageMissingException(problems);
        }

        // The ref the jail is actually created from: the resolved digest when one is available, the
        // original ref otherwise (a storeless engine / test fake).
        var spawnImageRef = pinnedImageRef ?? _imageRef;
        _log.LogInformation("preflight ok: jail images present and current; pinned image={Image}", spawnImageRef);

        // Per-repo toolchain (MG-42). The repo's MAIN-side .mainguard/toolchain — never a branch's —
        // decides what the verification jail carries beyond the curated base image. Resolved and built
        // HERE, before the container exists, because a live jail's image cannot be changed underneath
        // it (and because a docker build during a live session severs the agent PTY, G-16).
        //
        // A failure is loud and stops the spawn. The alternative is a jail that quietly lacks the tools
        // the repo's verify command names, whose every verification then fails at exit 127 in a way
        // that reads like the agent's code is broken.
        // `progress` is threaded ONLY here. This build is the one step of a launch that runs for
        // minutes, and it does so inside the spawn RPC with nothing on the wire saying so — which is
        // why the UI could only conclude "not responding".
        var toolchain = await EnsureToolchainAsync(repoHandle, barePath, pinnedImageRef, progress, ct).ConfigureAwait(false);
        if (toolchain is not null)
        {
            spawnImageRef = toolchain.ImageRef;
            _log.LogInformation(
                "toolchain layer ready: repo={Repo} ids={Ids} image={Image} base={Base}",
                repoHandle, string.Join(",", toolchain.Ids), toolchain.ImageRef, toolchain.BaseDigest);
        }

        // The runtime-mount half of the same declaration: toolchains a HUMAN installed into this
        // environment, which reach the jail as a read-only bind mount rather than as an image layer.
        // Checked HERE, before the container exists, for the same reason the layer is built here — and
        // refused loudly for the reason stated above, which applies identically to a mount that is not
        // there.
        var mountedToolchainIds = await EnsureMountedToolchainsAsync(repoHandle, barePath, ct).ConfigureAwait(false);

        // agentKind → the CLI the user dynamically installed. Resolved BEFORE the worktree so an
        // unknown kind costs nothing; the jail still spawns without a launch command (the operator
        // gets a shell in a correct sandbox rather than a failed spawn), and the caller surfaces it.
        //
        // That CLI-less jail is a real, wanted outcome of the OPERATOR path and only of that path — there
        // is a human on its PTY, which is the entire reason a bare sandbox is worth having. It is never a
        // wanted outcome of a COORDINATOR's spawn, where the worker's terminal is daemon-locked read-only
        // and nobody can ever type into it; a real coordinator spawned `coder`, got exactly this, and was
        // told the spawn succeeded. That refusal belongs to the coordinator's channel and lives in
        // AgentSpawnService.SpawnWorkerAsync, so this path keeps the behaviour it wants.
        var adapter = _adapters.TryGet(agentKind);
        var launchCommand = adapter?.Launch;

        // The role's operating instructions, rendered once and delivered two ways — neither redundant.
        //
        // The FLAG is the only delivery that reaches a coordinator: the role lock gives it an empty tmpfs
        // at /workspace, so there is no host side on which to pre-place a file. The FILE is what a CLI
        // opens unprompted, and is written into the worktree below for the roles that have one. An
        // adapter declaring neither spawns exactly as before, silently: a CLI with no instruction channel
        // is a limitation of that CLI, not a spawn failure.
        var instructionsRole = string.Equals(agentRole, AgentRoles.Coordinator, StringComparison.Ordinal)
            ? AgentIpcEndpointRole.Coordinator
            : AgentIpcEndpointRole.Worker;
        var instructions = InstructionsFor(instructionsRole, planMode);

        // The launch line the jail's CLI is actually started with: the first turn, the instructions, and
        // the one pre-approved command, assembled in ONE place because their ORDER is load-bearing (see
        // BuildLaunchArgv).
        launchCommand = BuildLaunchArgv(
            launchCommand, adapter, ipcDirPath, instructionsRole, instructions, planMode);

        // THREE cases, and the order is the point — phase 3's role lock is asked FIRST.
        //
        // Coordinator contract §2: a coordinator has no worktree. Note this skips CREATING one, not just
        // mounting it — provisioning a worktree the jail can never see would leave a branch and a
        // directory per coordinator for no reason, and a later change that started mounting it would find
        // the content already there.
        //
        // Then resume: adopting this id's existing agent/<id> vs creating a new one. The two are
        // deliberately different methods rather than one with a flag deep inside: creating refuses when
        // the branch exists, adopting refuses when it does not, and each refusal is the other's success.
        //
        // Why the role lock outranks the resume flag rather than the reverse: a coordinator is never
        // resumed onto a branch — it has no branch — so `withoutRepositoryAccess && adoptExistingBranch`
        // is a caller error, not a third behaviour. Asking the lock first means such a call gets a
        // repository-less jail (and ContainerSpecBuilder's fail-closed re-assertion) instead of quietly
        // adopting a worktree the role forbids.
        var worktreePath = withoutRepositoryAccess
            ? string.Empty
            : adoptExistingBranch
                ? _environment.Worktrees.AdoptAgentWorktree(repoHandle, agentId)
                : _environment.Worktrees.CreateAgentWorktree(repoHandle, agentId);
        // MG-3: the per-agent repository the worktree is linked off — the ONE git dir this jail may
        // write. Resolved AFTER creation so an implementation that has no per-agent repo (the test
        // doubles' default) simply reports none and the jail carries no such mount.
        var agentRepoPath = withoutRepositoryAccess
            ? null
            : _environment.Worktrees.AgentRepoPathFor(repoHandle, agentId);
        _log.LogInformation(
            withoutRepositoryAccess
                ? "repository-less jail (coordinator role): no worktree, no agent repo, no mirror, no cache"
                : "worktree ready: {Path} agentRepo={AgentRepo}",
            worktreePath, agentRepoPath);

        // The file half, where the working directory is a real host path. This is what a CLI opens on its
        // own — the copy staged in the IPC dir sits at a path nothing reads unprompted — so an adapter
        // that names no instructions file simply has no file-side delivery and relies on the flag.
        // Best-effort: an agent that starts without its briefing is worse off, not broken, and a spawn
        // that fails because a markdown file could not be written would be the worse trade.
        if (!withoutRepositoryAccess && worktreePath is { Length: > 0 })
        {
            TryStageInstructionsFile(worktreePath, adapter, instructions, agentId);
        }
        // Hoisted out of the try so the rollback can see it: a throw AFTER the container exists (the
        // ref watch, a post-start probe) used to clean up the worktree and leave the jail running, for
        // good — one of the ways the engine came to hold a day's worth of jails nobody had stopped.
        SandboxHandle? handle = null;
        try
        {
            // MG-43: this agent's own package cache on ext4, prepared BEFORE the container exists (its
            // mounts are fixed at create). A failure here is typed and stops the spawn — deliberately
            // NOT caught and degraded, because the alternative is a jail whose restore fills the 256 MiB
            // tmpfs $HOME and dies at ENOSPC, which the merge queue records as an ordinary failed
            // verification indistinguishable from the agent's code being broken.
            string? packageCachePath = null;
            // A coordinator builds nothing, so it has no use for a package cache — and a cache is a
            // read-write bind mount, which is exactly the kind of capability the role lock removes.
            if (!withoutRepositoryAccess && _environment.PackageCaches is { } caches)
            {
                var usage = caches.Prepare(repoHandle, agentId);
                packageCachePath = caches.PathFor(repoHandle, agentId);
                _log.LogInformation("package cache ready: {Path} — {Usage}", packageCachePath, usage.Describe());
            }

            // The default-deny network + allowlist proxy must exist before the jail joins the network.
            await _environment.Egress.EnsureReadyAsync(ct).ConfigureAwait(false);
            _log.LogInformation("egress ready (default-deny network + proxy)");

            // MG-36: this agent's OWN default-deny segment — an internal network whose only other member
            // is the shared proxy. Before this, every jail sat on one flat `mainguard-agents` network,
            // so agent A could dial agent B's container IP and ports directly; there was no east-west
            // control at all. Attaching the proxy to a new segment is additive (it keeps running, and
            // its existing legs keep their addresses and MACs), so segmenting does not re-introduce the
            // "recreating the proxy strands running jails" problem.
            var segment = await _environment.Egress
                .EnsureAgentSegmentAsync(repoHandle, agentId, ct).ConfigureAwait(false);
            _log.LogInformation(
                "egress segment ready: network={Network} proxy={Proxy}", segment.NetworkName, segment.ProxyAddress);

            // MG-4: mint this agent's gateway confinement — a Mainguard session token for the jail, the
            // real provider key kept daemon-side, and the agent's UPSTREAM BINDING recorded so the
            // gateway knows which provider to forward its traffic to. Null when the gateway is disabled,
            // when the CLI cannot be redirected, or when the proxy cannot reach the gateway — and
            // BuildSecrets then keeps the pre-gateway behaviour exactly. This argument is what was
            // missing in #298: without it the confinement machinery below was complete but never
            // invoked, so every BYOK jail received the raw provider key.
            var confinement = await TryConfineToGatewayAsync(agentId, modelApiKey, adapter, ct)
                .ConfigureAwait(false);
            var secrets = BuildSecrets(modelApiKey, adapter, extraEnv, cliCredentials, confinement);
            handle = await _environment.Sandboxes.SpawnAsync(new SandboxSpawnRequest(
                RepoHash: repoHandle,
                AgentId: agentId,
                WorktreePath: worktreePath,
                ImageRef: spawnImageRef,
                Limits: SandboxLimits.Default,
                Secrets: secrets,
                AgentUid: AgentUid,
                SupervisorUid: SupervisorUid,
                // Mount the shared CLI root read-only ONLY when CLIs are actually installed — and mount
                // THIS CATALOG'S root, not the fixed VM path. The catalog is injectable while the mount
                // source was hardcoded, so the two could describe different directories: the daemon
                // would answer "claude-code is installed" from one location and hand the jail another.
                // Identical in production (the default catalog's root IS AdapterPaths.VmRoot); the
                // difference only shows up where they were already inconsistent, as a container-create
                // failure on a bind source that does not exist.
                AdaptersRootPath: _adapters.HasAny() ? _adapters.Root : null,
                // Coordinator-role jails only: the daemon-served spawn-channel dir (read-only mount).
                IpcDirPath: ipcDirPath,
                // ...and, ONLY where the substrate cannot carry a Unix socket across the mount boundary,
                // that dir's outbox read-write so the jail has a channel at all. On macOS the daemon runs
                // on the host while jails run in the engine's Linux VM, and Docker's file sharing does not
                // proxy AF_UNIX: the socket mounts in as an inert inode and every coordinator tool was
                // unreachable. Decided here, from the substrate's own declaration, rather than from an
                // OS check — the property that matters is "can a bind-mounted socket be dialled", and the
                // substrate is the only thing that knows it.
                IpcOutboxPath: ipcOutboxPath,
                // The shared mirror at its identical VM path so the per-agent repo's alternates pointer
                // resolves in-jail (field bug 2026-07-23: every in-jail git command died "not a git
                // repository"). MG-3 stage 3 makes this mount read-only.
                // Coordinator contract §2: repository-less jails get no mirror either — read-only is
                // still a "view of repository contents", which is precisely what the role lock denies.
                BareRepoPath: withoutRepositoryAccess ? null : barePath,
                // MG-3: the per-agent repository, at its identical VM path so the worktree's gitdir
                // pointer resolves. Read-write, and mounted into exactly this one jail.
                AgentRepoPath: string.IsNullOrEmpty(agentRepoPath) ? null : agentRepoPath,
                // MG-36: this agent's segment, and the proxy's address ON that segment. The address
                // rather than the proxy's NAME because one dnsmasq cannot answer the same name with a
                // different address per segment, and every other segment's address is unreachable.
                NetworkName: segment.NetworkName,
                ProxyUrl: segment.ProxyUrl(EgressProxyConfigurator.ProxyPort),
                // MG-43: the daemon-owned package cache for THIS agent, read-write at
                // /var/cache/mainguard — on ext4, outside the worktree, outside the tmpfs $HOME.
                PackageCachePath: packageCachePath,
                // The role lock itself, re-asserted inside the pure spec builder (fail-closed).
                WithoutRepositoryAccess: withoutRepositoryAccess,
                // The user's saved CLI settings — the approved-command list. Filtered to what THIS
                // adapter declares, exactly as the credential files are, because the client names the
                // paths on the wire. An untrusted spawn never reaches here with any (the caller passes
                // none), so this filter is the second gate, not the only one.
                CliSettingsFiles: FilterCliSettings(cliSettings, adapter),
                // EVERYTHING Mainguard writes into /workspace: the DECLARED workspace settings paths, sent
                // whether or not anything is being restored into them (on a first-ever session the CLI
                // creates its own settings file there the moment the user approves something, and that
                // session has no restore payload to infer the path from), AND the instructions file
                // staged a few dozen lines above. /workspace is the tree the agent commits; nothing
                // Mainguard put there may reach the user's history.
                WorkspaceIgnorePaths: DeclaredWorkspaceIgnorePaths(adapter),
                ToolchainsRootPath: mountedToolchainIds.Count > 0 ? _environment.ToolchainsRootPath : null,
                ToolchainIds: mountedToolchainIds,
                // Stamped onto the jail's labels. The daemon's live session store is in-memory, so after a
                // restart the container labels are the ONLY record of what this agent is — without these
                // two, a surviving coordinator is adopted back as an unnamed, role-less worker and the
                // Coordinator surface reports no coordinator for a repo that plainly has one.
                AgentKind: agentKind,
                AgentRole: agentRole,
                AgentParentId: agentParentId), ct).ConfigureAwait(false);

            // MG-3 (design §7, "fetch trigger: both"): from here on the daemon watches this agent's own
            // refs/heads/agent/<id> and publishes it into the mirror the moment it moves. Started only
            // after the jail is up, so a failed spawn leaves no watcher behind; the pre-verification
            // re-fetch is the other half and neither replaces the other.
            // A repository-less jail has no refs/heads/agent/<id> to watch — it has no git dir at all.
            if (!withoutRepositoryAccess)
            {
                _environment.Worktrees.WatchAgentRef(repoHandle, agentId);
            }

            _log.LogInformation(
                "jail started: container={Container} reused={Reused} launchCmd={HasLaunch}",
                handle.ContainerId, handle.Reused, launchCommand is { Count: > 0 });
            return new SandboxLaunchResult(handle.ContainerId, handle.Reused, worktreePath, launchCommand);
        }
        catch (Exception ex)
        {
            // Leave no residue: remove the worktree we just created before surfacing the failure.
            // A repository-less jail never created one, so there is nothing to remove.
            //
            // On the RESUME path the cleanup must keep agent/<id>. The ordinary teardown ends in
            // `branch -D`, which here would delete the only surviving copy of the commits this launch was
            // invoked to recover — turning a failed resume into data loss. The branch-preserving clear can
            // leave the worktree behind if it cannot remove it; the next resume clears that residue, and
            // residue is a strictly better failure than deleted work.
            //
            // Mirrors the three-way choice above, in the same order and for the same reason: no worktree
            // was created for a role-locked coordinator, so neither teardown applies to it.
            _log.LogError(ex,
                "jail start failed after worktree — cleaning up worktree: repo={Repo} adopt={Adopt}",
                repoHandle, adoptExistingBranch);
            if (handle is not null)
            {
                // The jail is real and about to be forgotten by everything that could stop it later.
                try { await _environment.Sandboxes.RemoveAsync(handle.ContainerId, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception removeEx) { _log.LogWarning(removeEx, "rollback: could not remove jail {Container}", handle.ContainerId); }
            }

            if (withoutRepositoryAccess)
            {
                // Nothing was created; nothing to remove.
            }
            else if (adoptExistingBranch)
            {
                TryRemoveWorktreeKeepingBranch(repoHandle, agentId);
            }
            else
            {
                TryRemoveWorktree(repoHandle, agentId);
            }

            throw;
        }
    }

    /// <summary>
    /// Resolves the repo's MAIN-side toolchain declaration and ensures its layer exists, returning what
    /// to jail from — or <c>null</c> when the repo declares nothing (the overwhelming common case: the
    /// base image is the answer and this costs one <c>git show</c> that finds no file).
    ///
    /// <para>The substrate having no image-build capability is <b>not</b> a reason to continue: a repo
    /// that declared a toolchain and got a jail without it has no verification signal, only a
    /// convincing-looking failed one.</para>
    /// </summary>
    private async Task<ProvisionedToolchain?> EnsureToolchainAsync(
        string repoHandle, string barePath, string? baseDigest, IProgress<string>? progress, CancellationToken ct)
    {
        var declaration = RepoToolchainConfig.ReadMainBaseline(barePath, repoHandle);
        if (declaration.IsEmpty)
        {
            return null;
        }

        // Only an IMAGE-LAYER toolchain needs a builder. A declaration of nothing but runtime-mount
        // toolchains (`python-3`) builds no layer at all, so demanding an image builder for it would
        // refuse a perfectly satisfiable repository — and refuse it with the WRONG REASON, which is
        // worse: "this substrate cannot build toolchain layers" sends the reader after an image-build
        // capability that was never required. Found by the spawn test below, which drives a Python-only
        // repository through a substrate that has no builder.
        var needsLayer = declaration.Ids
            .Select(ToolchainCatalog.TryGet)
            .Any(r => r is { Delivery: ToolchainDelivery.ImageLayer });

        if (!needsLayer)
        {
            return null;
        }

        var builder = _environment.ToolchainImages;
        if (builder is null)
        {
            throw new ToolchainProvisioningException(repoHandle, declaration.Ids,
                $"this substrate ('{_environment.SubstrateId}') cannot build toolchain layers, so the jail "
                + "would start without the tools the repository's verification command needs.");
        }

        return await new ToolchainProvisioner(builder, m => _log.LogInformation("{Message}", m), progress)
            .EnsureAsync(repoHandle, declaration, baseDigest, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The declared toolchains that are delivered as a read-only mount, checked to be actually installed
    /// in this environment. Returns the ids the jail should carry; an empty list means the repo declared
    /// none of them and no toolchain mount is attached.
    ///
    /// <para><b>Why this refuses instead of continuing.</b> The failure being prevented is the one the
    /// owner hit: a repository declares Python, the jail starts without it, and the verify command dies
    /// with <c>No module named pytest</c> — which reads like the agent wrote a broken test, not like the
    /// environment is missing a toolchain. Verification is the gate that decides whether work may enter
    /// the merge queue, so a jail that cannot run the repository's tests must fail as a PROVISIONING
    /// problem, by name, with the action that fixes it. It must never produce a red verification.</para>
    ///
    /// <para>Note what is NOT here: an auto-install. A repository's declaration is not permission to
    /// install software — it names a toolchain and a human decides whether this environment has it.
    /// Installing on a repo's say-so would hand a repo-writable file the install-time execution the
    /// closed catalog exists to deny it.</para>
    /// </summary>
    private async Task<IReadOnlyList<string>> EnsureMountedToolchainsAsync(
        string repoHandle, string barePath, CancellationToken ct)
    {
        var declaration = RepoToolchainConfig.ReadMainBaseline(barePath, repoHandle);
        if (declaration.IsEmpty)
        {
            return Array.Empty<string>();
        }

        var wanted = declaration.Ids
            .Select(id => ToolchainCatalog.TryGet(id))
            .Where(r => r is { Delivery: ToolchainDelivery.RuntimeMount })
            .Select(r => r!.Id)
            .ToList();

        if (wanted.Count == 0)
        {
            return Array.Empty<string>();
        }

        var channel = _environment.Toolchains;
        if (channel is null)
        {
            throw new ToolchainProvisioningException(repoHandle, declaration.Ids,
                $"this substrate ('{_environment.SubstrateId}') cannot install toolchains, so the jail would "
                + $"start without {string.Join(" and ", wanted)} — the tools this repository's verification "
                + "command needs.");
        }

        var missing = new List<string>();
        var unknown = new List<string>();
        foreach (var id in wanted)
        {
            var entry = channel.Manifest.TryGet(id);
            if (entry is null)
            {
                missing.Add(id);
                continue;
            }

            var status = await channel.StatusAsync(entry, ct).ConfigureAwait(false);
            if (status.CouldNotCheck)
            {
                unknown.Add($"{entry.DisplayName} ({entry.Id}) — {status.Detail}");
            }
            else if (!status.IsInstalled)
            {
                missing.Add($"{entry.DisplayName} ({entry.Id}) — {status.Detail}");
            }
        }

        // Reported FIRST, and as its own failure. "We could not look" is not "it is not there": sending
        // someone whose environment is unreachable to Settings → Toolchains points them at a button that
        // will fail the same way, for a reason nothing on screen mentions. A wrong-but-confident
        // diagnosis is worse than the raw error it replaced.
        if (unknown.Count > 0)
        {
            throw new ToolchainProvisioningException(repoHandle, declaration.Ids,
                $"this repository declares {string.Join(", ", unknown)}. Mainguard could not reach its "
                + "environment to check, so whether the toolchain is installed is UNKNOWN — this is not a "
                + "report that it is missing. Check that MainguardEnv is running, then start the agent "
                + "again. The jail was NOT started.");
        }

        if (missing.Count > 0)
        {
            throw new ToolchainProvisioningException(repoHandle, declaration.Ids,
                $"this repository declares {string.Join(", ", missing)}, which is not installed in this "
                + "Mainguard environment. Install it in Settings → Toolchains and start the agent again. "
                + "The jail was NOT started: a jail without the toolchain cannot run this repository's "
                + "tests, and a verification that fails for that reason would look like failing code.");
        }

        _log.LogInformation(
            "mounted toolchains ready: repo={Repo} ids={Ids} root={Root}",
            repoHandle, string.Join(",", wanted), _environment.ToolchainsRootPath);
        return wanted;
    }

    /// <summary>Best-effort teardown of a launched agent: remove the jail, then its MG-36 network
    /// segment, then its worktree. Never throws.</summary>
    public async Task TeardownAsync(string? repoHash, string agentId, string containerId, CancellationToken ct = default)
    {
        try { await _environment.Sandboxes.RemoveAsync(containerId, ct).ConfigureAwait(false); }
        catch { /* never fail a stop from teardown */ }

        if (!string.IsNullOrEmpty(repoHash))
        {
            // MG-3: a LAST publish before anything is removed, then stop watching. Without it the work
            // an agent committed between the final verification and the stop would be lost with its
            // repository — the agent's own repo is deleted a few lines below. The OUTCOME is read, not
            // discarded: a publish the mediator refuses (a non-fast-forward tip after an amend or a
            // rebase) leaves the mirror at the old tip, and the agent's repository is then the only copy
            // of the rewritten commits, so the removal below must keep it.
            var publish = Mainguard.Agents.Agents.AgentRefPublishOutcome.NothingToPublish;
            try { publish = _environment.Worktrees.PublishAgentBranchOutcome(repoHash, agentId); }
            catch { /* never fail a stop from housekeeping */ }
            try { _environment.Worktrees.UnwatchAgentRef(repoHash, agentId); }
            catch { /* never fail a stop from housekeeping */ }

            // MG-36: reclaim the segment. Docker's local bridge address pool is finite (~32 networks by
            // default), so a segment leaked per agent would eventually make spawning fail with an
            // address-pool exhaustion error that reads like anything but the cause. Ordered AFTER the
            // container removal because Docker refuses to delete a network with a live endpoint.
            try { await _environment.Egress.RemoveAgentSegmentAsync(repoHash, agentId, ct).ConfigureAwait(false); }
            catch { /* never fail a stop from teardown */ }

            if (publish is Mainguard.Agents.Agents.AgentRefPublishOutcome.RefusedNonFastForward
                or Mainguard.Agents.Agents.AgentRefPublishOutcome.RefusedTarget)
            {
                _log.LogWarning(
                    "teardown: the last publish of agent/{Agent} in repo={Repo} was refused ({Outcome}) — "
                    + "keeping the agent's repository so the unpublished commits are not deleted with it",
                    agentId, repoHash, publish);
                TryRemoveWorktreeKeepingRepository(repoHash, agentId, $"the last publish was refused ({publish})");
            }
            else
            {
                TryRemoveWorktree(repoHash, agentId);
            }
        }
    }

    /// <summary>The teardown after a REFUSED publish: clear the worktree, keep the branch and the agent's
    /// repository. Best effort, and deliberately never falling back to the deleting removal.</summary>
    private void TryRemoveWorktreeKeepingRepository(string repoHash, string agentId, string reason)
    {
        try { _environment.Worktrees.RemoveAgentWorktreeKeepingRepository(repoHash, agentId, reason); }
        catch { /* best effort — residue is strictly better than the only copy of the work */ }
    }

    private void TryRemoveWorktree(string repoHash, string agentId)
    {
        try { _environment.Worktrees.RemoveAgentWorktree(repoHash, agentId, force: true); }
        catch { /* best effort */ }
    }

    /// <summary>The resume path's rollback: clear the worktree, keep <c>agent/&lt;id&gt;</c>. A manager
    /// that cannot do that throws rather than falling back — and this swallows the throw, so the outcome
    /// is residue, never a deleted branch.</summary>
    private void TryRemoveWorktreeKeepingBranch(string repoHash, string agentId)
    {
        try { _environment.Worktrees.RemoveAgentWorktreeKeepingBranch(repoHash, agentId); }
        catch { /* best effort — and deliberately NOT falling back to the branch-deleting removal */ }
    }

    /// <summary>
    /// Mints the MG-4 gateway confinement for one spawn, or null to leave the spawn exactly as it was.
    ///
    /// <para>ALL of the following must hold, and each null return is a deliberate refusal rather than a
    /// degradation:</para>
    /// <list type="bullet">
    ///   <item>the daemon actually bound a gateway (on by default; <c>--gateway-bind off</c> disables)
    ///   — otherwise the CLI would be pointed at nothing and a working BYOK agent would break;</item>
    ///   <item>that gateway is REACHABLE from this jail's egress proxy, measured rather than assumed —
    ///   the jail's segment is <c>Internal</c>, so the proxy is its only route to the daemon;</item>
    ///   <item>a provider key was supplied — an interactive-login (OAuth) agent has no key to confine,
    ///   holds no credential worth stealing, and must keep its direct route untouched;</item>
    ///   <item>the CLI declares BOTH a base-URL variable (it can be redirected) and a model host (we know
    ///   where to forward) — a CLI missing either cannot be fronted without breaking it.</item>
    /// </list>
    /// </summary>
    private async Task<GatewayConfinement?> TryConfineToGatewayAsync(
        string agentId, string? modelApiKey, InstalledAdapterMarker? adapter, CancellationToken ct)
    {
        // No key at all — an interactive-login (OAuth) agent. Nothing is confined and nothing is at
        // risk, so this is the one refusal that stays quiet: warning here would train the operator to
        // ignore the warnings that DO mean a key is sitting in a container.
        if (string.IsNullOrWhiteSpace(modelApiKey))
        {
            return null;
        }

        // Ticket #52 — from here down a BYOK key EXISTS, so every remaining refusal ends with
        // BuildSecrets writing the raw provider key into the jail. That outcome used to be reached in
        // total silence, which made a confined agent and an unconfined one indistinguishable in the
        // daemon log: the only loud path was the unreachable-gateway one below. An operator could not
        // answer "is this agent's key in its container?" from any evidence the daemon produced.
        //
        // Each refusal now names ITSELF and its cause, because the two have different remedies: the
        // gateway being off is an operator setting, while an adapter that cannot be redirected is a
        // vendor fact no configuration change will fix (see the verified table in adapters.starter.json
        // — codex, qwen-code and opencode expose no base-URL environment variable at all).
        if (_credentials is null || _gatewayOptions is not { } gateway || !gateway.CanConfine)
        {
            _log.LogWarning(
                "gateway confinement OFF: agent={Agent} adapter={Adapter} supplied a BYOK provider key, but "
                + "the model gateway is disabled, so THE RAW KEY IS INJECTED INTO THE JAIL under {KeyVar}. "
                + "MG-4 is not in effect for this agent and its model spend is not metered.",
                agentId, adapter?.Id ?? "<unknown>", adapter?.ApiKeyEnvVar ?? LegacyApiKeyEnvVar);
            return null;
        }

        if (adapter?.BaseUrlEnvVar is not { Length: > 0 }
            || adapter.ModelHost is not { Length: > 0 } upstreamHost)
        {
            _log.LogWarning(
                "gateway confinement IMPOSSIBLE: agent={Agent} adapter={Adapter} declares no "
                + "baseUrlEnvVar/modelHost pair, so its CLI cannot be pointed at the gateway and THE RAW "
                + "KEY IS INJECTED INTO THE JAIL under {KeyVar}. This is a property of the vendor's CLI, "
                + "not a misconfiguration — MG-4 cannot be applied to this agent, and its model spend is "
                + "not metered.",
                agentId, adapter?.Id ?? "<unknown>", adapter?.ApiKeyEnvVar ?? LegacyApiKeyEnvVar);
            return null;
        }

        // The gateway must be REACHABLE from this jail's egress proxy before we point the CLI at it.
        // A confined jail has no other route (its segment is Internal), so confining against an
        // address the proxy cannot dial does not degrade the agent, it breaks it — and the failure is a
        // proxy error that reads like a provider outage. Refusing here falls back to the exact
        // behaviour the agent had before the gateway existed, which works. This is what makes it safe
        // to have the gateway enabled without the operator having verified the address by hand.
        var endpoint = GatewayEndpointOf(gateway.BaseUrl!);
        if (!await _environment.Egress.CanProxyReachAsync(endpoint, ct).ConfigureAwait(false))
        {
            _log.LogWarning(
                "gateway confinement SKIPPED: agent={Agent} endpoint={Endpoint} is not reachable from the "
                + "egress proxy, so the jail keeps its direct provider route (and its own key). MG-4 is "
                + "not in effect for this agent.",
                agentId, endpoint);
            return null;
        }

        // The one lookup that answers BOTH "who is calling" and "where does it go": the token the jail
        // receives resolves, gateway-side, to this agent id, its budget, and this upstream host.
        var token = _credentials.Issue(agentId, modelApiKey, upstreamHost);
        _log.LogInformation(
            "gateway confinement issued: agent={Agent} upstream={Upstream} baseUrlVar={Var}",
            agentId, upstreamHost, adapter.BaseUrlEnvVar);
        return new GatewayConfinement(gateway.BaseUrl!, token);
    }

    /// <summary>The <c>host:port</c> inside a gateway base URL — the shape the egress probe wants.</summary>
    internal static string GatewayEndpointOf(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? $"{uri.Host}:{uri.Port}" : baseUrl;

    /// <summary>
    /// The credential env-file entries (P2-01), written to the agent-owned 0400 tmpfs — never
    /// Env/argv/disk. The variable NAME comes from the installed adapter's marker (audit fix #13 —
    /// a hardcoded <c>ANTHROPIC_API_KEY</c> meant codex/opencode never saw their keys):
    /// <list type="bullet">
    ///   <item>marker declares <c>apiKeyEnvVar</c> → the key is injected under that name;</item>
    ///   <item>marker declares NONE → the CLI authenticates interactively; no key is injected;</item>
    ///   <item>no marker at all (unknown kind / dev box without a catalog) → the legacy
    ///   <c>ANTHROPIC_API_KEY</c> fallback keeps local-dev flows working.</item>
    /// </list>
    /// The user's custom <paramref name="extraEnv"/> entries (llm_env_* — multi-provider CLIs like
    /// opencode) ride the same env-file; on a name collision the adapter's declared key wins.
    /// </summary>
    internal static SandboxSecrets BuildSecrets(
        string? modelApiKey, InstalledAdapterMarker? adapter,
        IReadOnlyDictionary<string, string>? extraEnv = null,
        IReadOnlyList<SandboxCredentialFile>? cliCredentials = null,
        GatewayConfinement? gateway = null)
    {
        var agentEnv = new Dictionary<string, string>(StringComparer.Ordinal);
        if (extraEnv is not null)
        {
            foreach (var (name, value) in extraEnv)
            {
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrEmpty(value))
                {
                    agentEnv[name] = value;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(modelApiKey))
        {
            var envVar = adapter is null ? LegacyApiKeyEnvVar : adapter.ApiKeyEnvVar;
            if (envVar is { Length: > 0 })
            {
                // MG-4: when the gateway is available AND this CLI declares a base-URL variable, the
                // jail receives the Mainguard SESSION TOKEN and is pointed at the gateway — the real
                // provider key never enters the container and is injected daemon-side at the network
                // hop. Both conditions are required: without a base-URL variable the CLI would still
                // call the provider directly, and handing it a token there would simply break it.
                if (gateway is not null && adapter?.BaseUrlEnvVar is { Length: > 0 } baseUrlVar)
                {
                    agentEnv[envVar] = gateway.SessionToken;
                    agentEnv[baseUrlVar] = gateway.BaseUrl;
                }
                else
                {
                    // No gateway (the default) or a CLI that cannot be redirected — unchanged
                    // behaviour: the real key goes into the jail, as documented by MG-4.
                    agentEnv[envVar] = modelApiKey;
                }
            }
        }

        var oobKey = new byte[32];
        RandomNumberGenerator.Fill(oobKey);
        return new SandboxSecrets(agentEnv, oobKey, FilterCliCredentials(cliCredentials, adapter));
    }

    /// <summary>
    /// The ONLY credential files that reach the jail: client-supplied entries whose path exactly
    /// matches one the installed adapter DECLARES (its marker's <c>credentialPaths</c>) and passes
    /// the home-relative shape gate. The client names paths on the wire, so without this filter a
    /// compromised client could write arbitrary agent-home files at spawn; with it the surface is
    /// exactly the vendor-declared login files. No marker / no declared paths ⇒ nothing is restored.
    /// </summary>
    internal static IReadOnlyList<SandboxCredentialFile>? FilterCliCredentials(
        IReadOnlyList<SandboxCredentialFile>? supplied, InstalledAdapterMarker? adapter)
    {
        if (supplied is not { Count: > 0 } || adapter?.CredentialPaths is not { Count: > 0 } declared)
        {
            return null;
        }

        var allowed = new HashSet<string>(
            declared.Where(AdapterManifest.IsHomeRelativeFilePath), StringComparer.Ordinal);
        var kept = supplied
            .Where(f => f.Content is { Length: > 0 } && allowed.Contains(f.HomeRelativePath))
            .ToArray();
        return kept.Length > 0 ? kept : null;
    }

    /// <summary>
    /// The ONLY settings files that reach the jail: client-supplied entries whose (root, path) pair
    /// exactly matches one the installed adapter DECLARES (its marker's <c>settingsPaths</c>) and
    /// passes the relative-path shape gate. Same reasoning as
    /// <see cref="FilterCliCredentials"/> — the client names paths on the wire — but the stakes are
    /// different in kind: these files carry a PERMISSION ALLOWLIST, so an unfiltered path would let a
    /// compromised client plant a pre-approved-command file anywhere in the agent's home or checkout.
    /// No marker / no declared settings paths ⇒ nothing is restored.
    ///
    /// <para><b>D5b — and the grant a stored file must not carry in.</b> Every kept file is put through
    /// <see cref="CliSettingsGrantScrub"/>, which removes any rule naming the daemon-owned IPC mount. A
    /// per-repo store that predates this carries exactly such a rule on this machine
    /// (<c>Bash(/opt/mainguard/ipc/mainguard-agent *)</c>, harvested from an attended coordinator), and
    /// restoring it hands one role's tool grant to every later jail of that repository — workers included.
    /// Scrubbing on the way IN is what makes an already-poisoned store harmless with no migration; the
    /// harvest side (see <see cref="HarvestCliSettingsAsync"/>) is what stops it re-acquiring one.</para>
    /// </summary>
    internal static IReadOnlyList<SandboxSettingsFile>? FilterCliSettings(
        IReadOnlyList<SandboxSettingsFile>? supplied, InstalledAdapterMarker? adapter)
    {
        if (supplied is not { Count: > 0 } || adapter?.SettingsPaths is not { Count: > 0 } declared)
        {
            return null;
        }

        var allowed = new HashSet<(AdapterSettingsRoot Root, string Path)>(
            declared.Where(d => d is not null && d.IsWellFormed()).Select(d => (d.ParsedRoot, d.Path)));
        var kept = supplied
            .Where(f => f.Content is { Length: > 0 }
                        && f.Content.Length <= AdapterSettingsPolicy.MaxFileBytes
                        && allowed.Contains((f.Root, f.RelativePath)))
            .Select(f => CliSettingsGrantScrub.Scrub(f.Content) is { Length: > 0 } clean
                ? f with { Content = clean }
                : null)
            .Where(f => f is not null)
            .Select(f => f!)
            .ToArray();
        return kept.Length > 0 ? kept : null;
    }

    /// <summary>
    /// The WORKSPACE-rooted settings paths this adapter declares — the files the jail must keep out of
    /// the agent's commits. Derived from the marker rather than from whatever is being restored,
    /// because the session that most needs the ignore is the FIRST one, which restores nothing and
    /// whose CLI writes the file itself. Empty when the adapter declares no workspace settings.
    /// </summary>
    internal static IReadOnlyList<string> DeclaredWorkspaceSettingsPaths(InstalledAdapterMarker? adapter) =>
        adapter?.SettingsPaths is not { Count: > 0 } declared
            ? Array.Empty<string>()
            : declared
                .Where(d => d is not null && d.IsWellFormed() && d.ParsedRoot == AdapterSettingsRoot.Workspace)
                .Select(d => d.Path)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    /// <summary>
    /// <b>Everything Mainguard itself writes into <c>/workspace</c></b>, and therefore everything the
    /// jail's local <c>info/exclude</c> must carry: the adapter's declared workspace settings paths
    /// <i>and</i> its <see cref="InstalledAdapterMarker.InstructionsFile"/>.
    ///
    /// <para><b>The defect this closes.</b> The instructions file was delivered and never ignored.
    /// Measured in a live worker jail: <c>info/exclude</c> held only <c>/.claude/settings.local.json</c>,
    /// <c>git check-ignore CLAUDE.md</c> answered rc=1, and every worker's <c>git status</c> showed
    /// <c>?? CLAUDE.md</c> — so the agent's own <c>git add -A</c> would commit MAINGUARD'S OWN briefing
    /// into the user's branch, in every repository. The worker in that run noticed unprompted and said so
    /// in its report, which is the shape of a defect the user finds first.</para>
    ///
    /// <para><b>Why the DECLARED name rather than <c>CLAUDE.md</c>.</b> The filename is vendor knowledge
    /// carried per adapter (<c>instructionsFile</c>), and the daemon writes whatever that field says. An
    /// exclusion hardcoded to today's value would keep passing its tests and silently stop covering the
    /// next CLI Mainguard ships — the "a description that outlived the thing it described" failure (MG-12)
    /// this codebase keeps re-finding. One field decides both what is written and what is ignored.</para>
    ///
    /// <para>Filtered through <see cref="AdapterManifest.IsHomeRelativeFilePath"/> for the same reason the
    /// settings half is: this list is written verbatim into a git ignore file, and an installed marker is
    /// a JSON file on disk that no manifest parse re-validates.</para>
    /// </summary>
    internal static IReadOnlyList<string> DeclaredWorkspaceIgnorePaths(InstalledAdapterMarker? adapter) =>
        DeclaredWorkspaceSettingsPaths(adapter)
            .Concat(AdapterManifest.IsHomeRelativeFilePath(adapter?.InstructionsFile)
                ? new[] { adapter!.InstructionsFile! }
                : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Writes the role's operating instructions to the adapter's declared file at the worktree root —
    /// <b>and only where there is nothing of the user's to destroy</b>. Returns the relative path written,
    /// or null when nothing was written (which is not a failure).
    ///
    /// <para><b>Why it refuses to overwrite, measured rather than reasoned.</b> The obvious reading of
    /// "keep it out of the user's history" is the <c>info/exclude</c> entry — and a git exclude does not
    /// apply to a TRACKED file. Probed in a container against real git: in a repository that tracks
    /// <c>CLAUDE.md</c>, with <c>/CLAUDE.md</c> present in <c>info/exclude</c>,
    /// <c>git check-ignore CLAUDE.md</c> still answers rc=1, <c>git status</c> reports <c>M CLAUDE.md</c>,
    /// and <c>git add -A</c> stages the daemon's text over the user's own instructions as an ordinary
    /// modification. So for exactly the repositories most likely to declare one — anything with a
    /// <c>CLAUDE.md</c> at its root, this one included — the ignore is inert and the write is destructive.
    /// A briefing is worth writing into an empty slot; it is not worth silently replacing a file the user
    /// wrote and tracks.</para>
    ///
    /// <para><b>File.Exists, not "is it tracked".</b> On a freshly created worktree the two are the same
    /// question, and that is the case that destroys something. On an ADOPTED (resumed) worktree the file
    /// may instead be this daemon's own dropping from the previous spawn — already excluded, and already
    /// carrying this text — so skipping it costs nothing, while for claude-code the launch flag delivers
    /// the current text on every start regardless. One rule, no git process, and the destructive case is
    /// the one it is right about.</para>
    ///
    /// <para><b>The path is re-validated here</b> even though the manifest parser now refuses a malformed
    /// <c>instructionsFile</c>: an <see cref="InstalledAdapterMarker"/> is a JSON file on disk written at
    /// install time and read back at spawn, so nothing re-parses it through the manifest. Without this,
    /// <c>Path.Combine</c> with a rooted or <c>..</c>-bearing name writes OUTSIDE the worktree.</para>
    /// </summary>
    internal string? TryStageInstructionsFile(
        string worktreePath, InstalledAdapterMarker? adapter, string instructions, string agentId)
    {
        if (adapter?.InstructionsFile is not { Length: > 0 } instructionsFile)
        {
            return null;
        }

        if (!AdapterManifest.IsHomeRelativeFilePath(instructionsFile))
        {
            _log.LogWarning(
                "adapter {Adapter} declares instructionsFile '{File}', which is not a plain relative path — "
                + "nothing was written for agent={Agent}", adapter.Id, instructionsFile, agentId);
            return null;
        }

        var target = Path.Combine(worktreePath, instructionsFile);
        if (File.Exists(target))
        {
            _log.LogInformation(
                "not staging {File} for agent={Agent}: the worktree already has one, and a git exclude "
                + "does not cover a tracked file — overwriting would put Mainguard's text into the user's "
                + "own history", instructionsFile, agentId);
            return null;
        }

        try
        {
            var parent = Path.GetDirectoryName(target);
            if (parent is { Length: > 0 })
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllText(target, instructions.Replace("\r\n", "\n"));
            return instructionsFile;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(
                ex, "could not stage {File} into the worktree for agent={Agent}", instructionsFile, agentId);
            return null;
        }
    }

    /// <summary>
    /// The operating instructions a jail of this role is handed, <b>bound to this daemon's catalog</b> —
    /// and the one place the daemon renders them. Everything that DELIVERS the text asks for it here: the
    /// launch flag below, the file <c>AgentIpcServer</c> writes beside the shim (via
    /// <c>AgentSpawnService</c>, which creates the endpoint before the jail exists), and the copy staged
    /// into a worker's worktree.
    ///
    /// <para>An instance method rather than a static over a caller-supplied catalog, because the
    /// interesting failure is not "the text can name kinds" — it is "the text names the kinds THIS daemon
    /// has". Defect G2 was that shape with the argument omitted altogether: the launcher bound its catalog
    /// and the IPC server bound nothing, so one jail got two different texts.</para>
    /// </summary>
    internal string InstructionsFor(
        AgentIpcEndpointRole role,
        Mainguard.Agents.Agents.Orchestrator.WorkerPlanMode planMode =
            Mainguard.Agents.Agents.Orchestrator.WorkerPlanMode.Gated) =>
        AgentOperatingInstructions.For(role, _adapters, planMode);

    /// <summary>
    /// Assembles the complete launch line a jailed CLI is started with — the first user turn, the role's
    /// operating instructions, and the one pre-approved command — and is the <b>single place that knows
    /// their order</b>, because the order is what makes the first turn arrive at all.
    ///
    /// <para><b>The turn goes FIRST, before every flag the daemon appends.</b> Measured against a real
    /// claude-code 2.1.250, not assumed: appended last — the position every other field on this line uses
    /// — the turn never reached the model, because <c>--allowedTools</c> is variadic
    /// (<c>&lt;tools...&gt;</c>) and swallows every positional that follows it. The CLI idled at an empty
    /// input box for the full probe, indistinguishable from having no turn at all. Placed first, the same
    /// text ran the shim as its first action. Appending it here — the obvious implementation, and the one
    /// this file's three neighbouring fields would have suggested — would therefore have shipped the fix
    /// and kept the bug.</para>
    ///
    /// <para>Everything stays optional and silent: an adapter that declares no channel launches
    /// byte-for-byte as it did before, and a CLI with no instruction or first-turn surface is a limitation
    /// of that CLI rather than a spawn failure.</para>
    /// </summary>
    internal static IReadOnlyList<string>? BuildLaunchArgv(
        IReadOnlyList<string>? launchCommand,
        InstalledAdapterMarker? adapter,
        string? ipcDirPath,
        AgentIpcEndpointRole role,
        string instructions,
        Mainguard.Agents.Agents.Orchestrator.WorkerPlanMode planMode =
            Mainguard.Agents.Agents.Orchestrator.WorkerPlanMode.Gated)
    {
        launchCommand = ApplyInitialPrompt(launchCommand, adapter, ipcDirPath, role, planMode);

        if (launchCommand is { Count: > 0 } && adapter?.SystemPromptArg is { Length: > 0 } promptArg)
        {
            launchCommand = launchCommand.Append(promptArg).Append(instructions).ToList();
        }

        // Telling a CLI its shim exists is not the same as letting it run one. A real coordinator followed
        // the instructions above exactly, ran its shim as its first action, and got "This command requires
        // approval" — in a jail with no human to answer. Every tool the role has is that one command, so
        // the whole feature stalled on its first action. This grants that command, and only that command.
        return ApplyShimPreApproval(launchCommand, adapter, ipcDirPath, role);
    }

    /// <summary>
    /// Places the daemon's FIRST USER TURN on the launch line, as this CLI declares it takes one
    /// (<see cref="InstalledAdapterMarker.InitialPromptStyle"/>). Returns
    /// <paramref name="launchCommand"/> untouched whenever any part of that is absent.
    ///
    /// <para><b>The deadlock this closes.</b> A vendor CLI does not act on a system prompt — it needs a
    /// user turn. A worker jail launched with only <c>--append-system-prompt</c> drew its banner and
    /// waited: six minutes, empty outbox, no transcript, <c>mainguard-plan</c> never run. And no other
    /// mechanism could start it, because the only writer to a worker's CLI is the coordinator's
    /// <c>send_worker_prompt</c>, which <see cref="Mainguard.Agents.Agents.Orchestrator.WorkerPlanGate"/>
    /// refuses until that worker has an approved plan. No first turn, no plan; no plan, no first turn.</para>
    ///
    /// <para><b>This does not hand the worker its task.</b> The text comes from
    /// <see cref="AgentKickoffPrompt"/>, a pure function of the role and the shim path — the task, the
    /// title and the agent id are not parameters of it and are not in scope where it is built, so it
    /// cannot carry the work even by accident. What it says is "ask the daemon what you are here to plan",
    /// i.e. run the <c>brief</c> op, which is precisely what phase 2 gives a worker up front. Every gate
    /// still answers no afterwards: the task is released only by <c>TryReleaseTask</c> on an approved
    /// plan, steering and verification are still refused, and the plan gate is still ANDed into the merge
    /// queue.</para>
    ///
    /// <para><b><paramref name="ipcDirPath"/> is the gate, for the same reason it gates the pre-approval.</b>
    /// A jail with no IPC dir has no <c>mainguard-plan</c> in it, so a turn telling its CLI to run one
    /// would spend a request to produce "command not found" and an agent with no idea what to do next.
    /// That is every external-PR head and every manually spawned worker — none of which the plan gate
    /// holds, and none of which is deadlocked, because nothing is being withheld from them.</para>
    /// </summary>
    internal static IReadOnlyList<string>? ApplyInitialPrompt(
        IReadOnlyList<string>? launchCommand,
        InstalledAdapterMarker? adapter,
        string? ipcDirPath,
        AgentIpcEndpointRole role,
        Mainguard.Agents.Agents.Orchestrator.WorkerPlanMode planMode =
            Mainguard.Agents.Agents.Orchestrator.WorkerPlanMode.Gated)
    {
        if (launchCommand is not { Count: > 0 }
            || string.IsNullOrEmpty(ipcDirPath)
            || adapter?.InitialPromptDelivery != AdapterInitialPromptStyle.FirstPositional)
        {
            return launchCommand;
        }

        var turn = AgentKickoffPrompt.For(role, AgentIpcPaths.SandboxShimPath(role), planMode);
        return turn is null ? launchCommand : launchCommand.Append(turn).ToList();
    }

    /// <summary>
    /// Appends the ONE pre-approval this jail gets: the absolute in-jail path of the shim THIS agent's
    /// role was actually given, spelled the way <paramref name="adapter"/> declares
    /// (<see cref="InstalledAdapterMarker.PreApprovedCommandArg"/> +
    /// <see cref="InstalledAdapterMarker.PreApprovedCommandFormat"/>). Returns
    /// <paramref name="launchCommand"/> untouched whenever any part of that is absent.
    ///
    /// <para><b>What is being granted, stated plainly.</b> This is a capability grant inside a sandbox
    /// whose point is least privilege, so it is written to be readable as one. It widens exactly one
    /// thing: the CLI may run <c>/opt/mainguard/ipc/&lt;its own shim&gt;</c> without asking a human. It
    /// grants no other command, no wildcard, no directory, and no second shim — a coordinator gets
    /// <c>mainguard-agent</c> and a worker gets <c>mainguard-plan</c>, because
    /// <see cref="AgentIpcPaths.SandboxShimPath"/> is the same function that decides which shim the
    /// daemon WROTE into that jail. Neither role can be given the other's grant without changing which
    /// shim it has.</para>
    ///
    /// <para><b><paramref name="ipcDirPath"/> is the gate, and it is the load-bearing argument.</b> A
    /// session with no IPC dir has no shim at all: that is every external-PR head and every manually
    /// spawned worker the plan gate is not holding. Deriving the grant from the ROLE STRING alone would
    /// have handed those jails a standing pre-approval for a path that does not exist in them — harmless
    /// today, and exactly the kind of latent grant that stops being harmless the day something else is
    /// mounted at that path. No dir, no shim, no grant.</para>
    /// </summary>
    internal static IReadOnlyList<string>? ApplyShimPreApproval(
        IReadOnlyList<string>? launchCommand,
        InstalledAdapterMarker? adapter,
        string? ipcDirPath,
        AgentIpcEndpointRole role)
    {
        if (launchCommand is not { Count: > 0 }
            || string.IsNullOrEmpty(ipcDirPath)
            || adapter?.PreApprovedCommandArg is not { Length: > 0 } arg)
        {
            return launchCommand;
        }

        var grant = AdapterManifest.RenderPreApproval(
            adapter.PreApprovedCommandFormat, AgentIpcPaths.SandboxShimPath(role));
        return grant is null ? launchCommand : launchCommand.Append(arg).Append(grant).ToList();
    }

    /// <summary>
    /// Harvests the CLI's SETTINGS files (the installed adapter's declared <c>settingsPaths</c>) out of
    /// the jail, so the approvals a user gave in this session survive into the next agent.
    ///
    /// <para><b>This is the direction that can escalate, and the caller — not this method — decides
    /// whether it may run.</b> The files are agent-writable by construction (the CLI has to be able to
    /// record a new approval), so what comes back is "whatever is in the file", not "what a human
    /// approved". <see cref="AgentSpawnService"/> therefore calls this only for a human-attended,
    /// trusted session; see the design note for the full argument.</para>
    ///
    /// <para>Mechanically the twin of <see cref="HarvestCliCredentialsAsync"/>: base64 over the exec
    /// pipe, a missing file skipped, any exec failure yielding an empty result — harvesting must never
    /// block a stop. Files over <see cref="AdapterSettingsPolicy.MaxFileBytes"/> are refused rather
    /// than truncated: a settings file is kilobytes, and the ceiling bounds what a jail's occupant can
    /// push into a host-side store that later jails read.</para>
    /// </summary>
    public async Task<IReadOnlyList<SandboxSettingsFile>> HarvestCliSettingsAsync(
        string containerId, string agentKind, CancellationToken ct = default)
    {
        var declared = _adapters.TryGet(agentKind)?.SettingsPaths;
        if (declared is not { Count: > 0 })
        {
            return Array.Empty<SandboxSettingsFile>();
        }

        if (await IsFrozenAsync(containerId, ct).ConfigureAwait(false))
        {
            LogHarvestSkippedForFrozenJail("settings", agentKind, containerId);
            return Array.Empty<SandboxSettingsFile>();
        }

        var harvested = new List<SandboxSettingsFile>();
        foreach (var entry in declared)
        {
            if (entry is null || !entry.IsWellFormed())
            {
                continue;
            }

            var root = entry.ParsedRoot;
            try
            {
                // Runs as the container's default user — the agent uid. Path is positional ("$1"),
                // never interpolated into script text. The size check happens in the shell so an
                // oversized file is never read into the daemon's memory at all.
                var result = await _environment.Sandboxes.ExecAsync(containerId, new[]
                {
                    "sh", "-c",
                    "[ -f \"$1\" ] || exit 1\n"
                    + "[ \"$(wc -c < \"$1\" | tr -d ' ')\" -le \"$2\" ] || exit 2\n"
                    + "base64 \"$1\"\n",
                    "sh",
                    DockerSandboxEngine.SettingsRootPath(root) + "/" + entry.Path,
                    AdapterSettingsPolicy.MaxFileBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }, ct).ConfigureAwait(false);

                if (result.ExitCode == 2)
                {
                    _log.LogWarning(
                        "cli settings harvest refused (over {Max} bytes): kind={Kind} root={Root} path={Path}",
                        AdapterSettingsPolicy.MaxFileBytes, agentKind, AdapterSettingsPath.SpellRoot(root), entry.Path);
                    continue;
                }

                if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
                {
                    continue; // no settings written yet — nothing to persist
                }

                var content = Convert.FromBase64String(
                    string.Concat(result.Stdout.Where(c => !char.IsWhiteSpace(c))));

                // D5b: a rule naming the daemon-owned IPC mount does not leave the jail. Those grants are
                // issued per jail and per role at launch, so nothing a jail records about them means
                // anything in the next one — and a coordinator's `Bash(<its shim> *)`, persisted per REPO,
                // is one role's tool grant queued up for every later jail of that repository. An
                // unparseable file that names the mount is dropped whole rather than carried unread.
                var scrubbed = CliSettingsGrantScrub.Scrub(content);
                if (scrubbed is null)
                {
                    _log.LogWarning(
                        "cli settings harvest refused (names {Mount} and could not be scrubbed): kind={Kind} root={Root} path={Path}",
                        CliSettingsGrantScrub.DaemonOwnedPathPrefix, agentKind,
                        AdapterSettingsPath.SpellRoot(root), entry.Path);
                    continue;
                }

                if (scrubbed.Length != content.Length)
                {
                    _log.LogInformation(
                        "cli settings harvest scrubbed a role-scoped grant for {Mount}: kind={Kind} root={Root} path={Path}",
                        CliSettingsGrantScrub.DaemonOwnedPathPrefix, agentKind,
                        AdapterSettingsPath.SpellRoot(root), entry.Path);
                }

                content = scrubbed;
                if (content.Length is > 0 and <= AdapterSettingsPolicy.MaxFileBytes)
                {
                    harvested.Add(new SandboxSettingsFile(root, entry.Path, content));
                }
            }
            catch (Exception ex)
            {
                // A dead container / malformed pipe output loses this file's harvest, never the stop.
                _log.LogWarning(ex, "cli settings harvest failed: kind={Kind} path={Path}", agentKind, entry.Path);
            }
        }

        return harvested;
    }

    /// <summary>
    /// Harvests the CLI's login-state files (the installed adapter's declared
    /// <c>credentialPaths</c>) out of the jail's tmpfs $HOME — called just before teardown, because
    /// teardown is the moment the tmpfs (and with it any in-terminal login the user performed)
    /// would otherwise evaporate. Files come out base64 over the exec pipe (binary-safe through the
    /// string plumbing); a missing file is skipped, and any exec failure yields an empty result —
    /// harvesting must never block a stop.
    /// </summary>
    public async Task<IReadOnlyList<SandboxCredentialFile>> HarvestCliCredentialsAsync(
        string containerId, string agentKind, CancellationToken ct = default)
    {
        var declared = _adapters.TryGet(agentKind)?.CredentialPaths;
        if (declared is not { Count: > 0 })
        {
            return Array.Empty<SandboxCredentialFile>();
        }

        if (await IsFrozenAsync(containerId, ct).ConfigureAwait(false))
        {
            LogHarvestSkippedForFrozenJail("credential", agentKind, containerId);
            return Array.Empty<SandboxCredentialFile>();
        }

        var harvested = new List<SandboxCredentialFile>();
        foreach (var relative in declared.Where(AdapterManifest.IsHomeRelativeFilePath))
        {
            try
            {
                // Runs as the container's default user — the agent uid — so its own 0600 files read
                // fine. Path is positional ("$1"), never interpolated into script text.
                var result = await _environment.Sandboxes.ExecAsync(containerId, new[]
                {
                    "sh", "-c", "[ -f \"$1\" ] && base64 \"$1\"", "sh",
                    ContainerSpecBuilder.AgentHome + "/" + relative,
                }, ct).ConfigureAwait(false);

                if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
                {
                    continue; // not logged in yet / file absent — nothing to persist
                }

                var content = Convert.FromBase64String(
                    string.Concat(result.Stdout.Where(c => !char.IsWhiteSpace(c))));
                if (content.Length > 0)
                {
                    harvested.Add(new SandboxCredentialFile(relative, content));
                }
            }
            catch (Exception ex)
            {
                // A dead container / malformed pipe output loses this file's harvest, never the stop.
                _log.LogWarning(ex, "cli credential harvest failed: kind={Kind} path={Path}", agentKind, relative);
            }
        }

        return harvested;
    }

    /// <summary>
    /// Whether this jail is frozen (<c>docker pause</c>) — asked BEFORE either harvest, because
    /// <c>docker exec</c> into a paused container is not a failure the harvest can learn anything from:
    /// the engine refuses it outright with <c>Conflict</c>, once per declared path.
    ///
    /// <para><b>The defect this closes.</b> A conflicted keep-alive rebase leaves the worker paused, and
    /// the client's harvest sweep runs against every agent — so a stop or a sweep in that window logged a
    /// raw <c>Docker.DotNet.DockerApiException … status code=Conflict</c> stack trace per file (four in
    /// one observed session). Nothing was wrong that an operator could act on, and a warning-with-stack
    /// that means "as expected" is how the ones that mean something stop being read.</para>
    ///
    /// <para>An engine that cannot answer is treated as NOT frozen, deliberately: the harvest then runs
    /// and its own error is the honest report. Guessing "frozen" from ignorance would silently skip a
    /// harvest that would have succeeded, and a skipped credential harvest costs the user their login.</para>
    /// </summary>
    private async Task<bool> IsFrozenAsync(string containerId, CancellationToken ct)
    {
        try
        {
            return await _environment.Sandboxes.IsPausedAsync(containerId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(
                ex, "cli harvest: could not read pause state, harvesting anyway: container={Container}",
                containerId);
            return false;
        }
    }

    /// <summary>
    /// The skip, said once and said as a fact rather than as a failure — Information, no exception. It
    /// names what was NOT done and that nothing was lost, so an operator reading the log after a
    /// conflicted merge is not left wondering whether a login was dropped.
    /// </summary>
    private void LogHarvestSkippedForFrozenJail(string what, string agentKind, string containerId) =>
        _log.LogInformation(
            "cli {What} harvest skipped: kind={Kind} container={Container} — the jail is paused and "
            + "`docker exec` into a frozen container is refused by the engine. Nothing was harvested and "
            + "nothing was lost; the files are still in its $HOME and harvest again once it is resumed.",
            what, agentKind, containerId);
}

/// <summary>
/// MG-4 — what the jail is given INSTEAD of the real provider key when the model gateway is
/// available: an opaque Mainguard session token, plus the gateway's base URL to send requests to.
///
/// <para>The gateway maps the token back to the agent, swaps in the real provider key it holds
/// daemon-side, and forwards upstream — so the key never has to exist inside the container.</para>
///
/// <para>This applies to the BYOK/api-key path ONLY. A CLI that authenticates by interactive OAuth
/// owns its own login and refresh lifecycle, so its token files must remain in the jail; those are
/// handled by the credentialPaths restore/harvest round-trip instead.</para>
/// </summary>
/// <param name="BaseUrl">The gateway URL the CLI's base-URL variable is set to.</param>
/// <param name="SessionToken">The per-agent <c>mg_sess_</c> token that replaces the provider key.</param>
internal sealed record GatewayConfinement(string BaseUrl, string SessionToken);
