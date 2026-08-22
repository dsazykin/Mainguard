using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>One CLI login-state file to restore into the jail's tmpfs <c>$HOME</c> at spawn (e.g.
/// <c>.claude/.credentials.json</c>). The path is $HOME-relative and MUST already be validated by
/// <see cref="Adapters.AdapterManifest.IsHomeRelativeFilePath"/> — the engine resolves it under
/// <c>/home/agent</c> without further checks. Content is SECRET: it travels only over exec stdin
/// and lives only in the tmpfs home; the durable copy is the host OS keychain.</summary>
public sealed record SandboxCredentialFile(string HomeRelativePath, byte[] Content);

/// <summary>
/// One CLI SETTINGS file to restore into a jail at spawn — the permission allowlist above all (e.g.
/// <c>.claude/settings.local.json</c> under <see cref="Adapters.AdapterSettingsRoot.Workspace"/>).
///
/// <para>Not a credential and deliberately not carried on <see cref="SandboxSecrets"/>: its durable
/// home is an ordinary per-repository JSON file under the Mainguard data root, not the OS keychain.
/// It still travels over exec <b>stdin</b> like every other file written into a jail, because
/// <c>docker cp</c> writes UNDER the tmpfs <c>$HOME</c> and reports success while the container sees
/// nothing.</para>
///
/// <para><paramref name="Root"/> selects the tree and <paramref name="RelativePath"/> MUST already
/// have passed <see cref="Adapters.AdapterManifest.IsHomeRelativeFilePath"/> — the engine resolves it
/// under the chosen root without further checks.</para>
/// </summary>
public sealed record SandboxSettingsFile(
    Adapters.AdapterSettingsRoot Root, string RelativePath, byte[] Content);

/// <summary>The secrets delivered to a sandbox on spawn — never through <c>Env</c>/argv/disk.</summary>
/// <param name="AgentEnv">The P2-01 credential env-file entries, written to the agent-owned 0400 tmpfs.</param>
/// <param name="OobKey">The OOB session HMAC key <c>K</c>, written to the supervisor-owned 0400 tmpfs.</param>
/// <param name="CliCredentialFiles">The CLI's saved login state to restore into the tmpfs home
/// (write-if-absent, so a live jail's fresher tokens are never clobbered). Null/empty = none.</param>
public sealed record SandboxSecrets(
    IReadOnlyDictionary<string, string> AgentEnv, byte[] OobKey,
    IReadOnlyList<SandboxCredentialFile>? CliCredentialFiles = null);

/// <summary>The request to spawn (or re-start) one agent's hardened jail.</summary>
/// <param name="AdaptersRootPath">The VM-side dynamically-installed agent-CLI root, bind-mounted
/// READ-ONLY into the jail so CLIs installed after provisioning reach agents with no image rebuild.
/// Null when no CLIs are installed.</param>
/// <param name="IpcDirPath">The VM-side per-agent IPC dir (daemon Unix socket + the
/// <c>mainguard-agent</c> spawn shim), bind-mounted READ-ONLY at
/// <see cref="Ipc.AgentIpcPaths.SandboxMount"/>. Coordinator-role jails only; null for workers —
/// they get no spawn channel (least privilege).</param>
/// <param name="BareRepoPath">The VM-side shared mirror, bind-mounted at its identical VM path so the
/// per-agent repo's <c>objects/info/alternates</c> resolves in-jail (see
/// <see cref="ContainerSpecRequest"/>). Null = no mirror mount.</param>
/// <param name="AgentRepoPath">MG-3 — the VM-side per-agent repository backing the worktree,
/// bind-mounted READ-WRITE at its identical VM path so the linked worktree's <c>gitdir:</c> pointer
/// resolves in-jail. Null = no per-agent repo mount.</param>
/// <param name="NetworkName">MG-36 — the per-agent, single-tenant default-deny segment this jail
/// attaches to (<see cref="EgressProxyConfigurator.AgentSegmentName"/>), so agent A has no L2 or L3
/// path to agent B. Null keeps the engine's configured network (the shared <c>mainguard-agents</c>
/// segment) — the pre-segmentation topology, still used by the ad-hoc harnesses.</param>
/// <param name="ProxyUrl">MG-36 — the proxy URL for THIS segment. With one network per agent the
/// proxy holds a different address on each, and the jail's pinned dnsmasq cannot answer the proxy's
/// name differently per client, so the jail is given the address directly. Null keeps the engine's
/// configured URL.</param>
/// <param name="PackageCachePath">MG-43 — this agent's own daemon-owned package cache on ext4,
/// bind-mounted READ-WRITE at <see cref="PackageCachePolicy.SandboxMount"/>: the writable, un-tmpfs'd,
/// out-of-worktree place a real dependency closure can be restored into. Null = no cache mount (the
/// pre-MG-43 behaviour the substrate-less test doubles keep). When it IS supplied,
/// <see cref="DockerSandboxEngine"/> proves in the started container that the mount is really there and
/// really writable before handing the jail back — see <see cref="PackageCachePolicy.WritabilityProbe"/>
/// for why the daemon's own record of the request is not evidence about the container.</param>
/// <param name="CliSettingsFiles">The CLI's saved settings (the adapter's declared
/// <c>settingsPaths</c>) to restore into the jail's throwaway trees, write-if-absent exactly like the
/// credential restore. Null/empty = none, which is what an UNTRUSTED jail always gets: an external
/// pull request's code must never inherit the user's approved-command list.</param>
/// <param name="WorkspaceIgnorePaths">The workspace-rooted settings paths this agent's CLI is
/// DECLARED to use, whether or not anything is being restored into them. They are added to the agent
/// repository's local <c>info/exclude</c>, because <c>/workspace</c> is the tree the agent commits and
/// the CLI writes its approvals there itself — so on a first-ever session, with nothing to restore,
/// the agent's own <c>git add -A</c> would otherwise commit the user's permission allowlist into their
/// repository. Separate from <see cref="CliSettingsFiles"/> precisely because that case has no
/// restore payload to derive it from.</param>
public sealed record SandboxSpawnRequest(
    string RepoHash,
    string AgentId,
    string WorktreePath,
    string ImageRef,
    SandboxLimits Limits,
    SandboxSecrets Secrets,
    int AgentUid,
    int SupervisorUid,
    string? AdaptersRootPath = null,
    string? IpcDirPath = null,
    string? BareRepoPath = null,
    string? NetworkName = null,
    string? ProxyUrl = null,
    string? AgentRepoPath = null,
    string? PackageCachePath = null,
    IReadOnlyList<SandboxSettingsFile>? CliSettingsFiles = null,
    IReadOnlyList<string>? WorkspaceIgnorePaths = null,
    string? ToolchainsRootPath = null,
    IReadOnlyList<string>? ToolchainIds = null,
    string AgentKind = "",
    string AgentRole = "");

/// <summary>A running sandbox handle. <see cref="Reused"/> is true when a stopped persistent jail was re-started rather than recreated.</summary>
public sealed record SandboxHandle(string ContainerId, bool Reused);

/// <summary>The outcome of an in-sandbox exec (e.g. <c>devbox add</c>).</summary>
public sealed record SandboxExecResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// The engine-agnostic sandbox lifecycle seam (P2-07). Deliberately carries <b>no</b> Docker.DotNet
/// types in its signature so an optional future <c>SbxSandboxEngine</c> (microVM) can implement it
/// without sbx becoming a hard dependency. The Docker implementation is
/// <see cref="DockerSandboxEngine"/>. Docker is the sole source of truth for liveness — there are no
/// PID/lock files.
/// </summary>
public interface ISandboxEngine
{
    /// <summary>Create-or-start the persistent jail keyed by repo hash + agent id (a stopped container is <c>docker start</c>ed; a base-image upgrade recreates).</summary>
    Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default);

    /// <summary>
    /// True when <paramref name="imageRef"/> is present in the engine's image store — the spawn
    /// preflight's probe (field failure 2026-07-17: a fresh/upgraded VM has an empty docker store,
    /// so both jail images are absent and the spawn fails opaquely). The default answers true — an
    /// engine (or test fake) with no separate image store has nothing to preflight;
    /// <see cref="DockerSandboxEngine"/> overrides with a real image inspect.
    /// </summary>
    Task<bool> ImageExistsAsync(string imageRef, CancellationToken ct = default) => Task.FromResult(true);

    /// <summary>
    /// The installed <see cref="SandboxImageVersions.LabelKey"/> label of <paramref name="imageRef"/>.
    /// The spawn preflight compares it to the expected <see cref="SandboxImageVersions"/> constant to
    /// catch a STALE image (right name, old bytes) — the skew class a presence check alone cannot see;
    /// a real <see cref="DockerSandboxEngine"/> answers the actual Docker label, or <c>null</c> when the
    /// image is absent OR carries no such label (an old, pre-versioning image ⇒ stale).
    /// <para>The DEFAULT answers the EXPECTED version (<see cref="SandboxImageVersions.For"/>), so a
    /// storeless engine / test fake — which already reports every image present via the
    /// <see cref="ImageExistsAsync"/> default — passes the version check too (it has no real label
    /// store to be stale against); a fake that wants to exercise the stale path overrides this.</para>
    /// </summary>
    Task<string?> ImageVersionAsync(string imageRef, CancellationToken ct = default) =>
        Task.FromResult(SandboxImageVersions.For(imageRef));

    /// <summary>
    /// MG-27 — the immutable content digest (<c>sha256:&lt;64 hex&gt;</c>) the mutable ref
    /// <paramref name="imageRef"/> currently resolves to, or <c>null</c> when the image is absent.
    ///
    /// <para>This is what turns the spawn preflight from "the thing behind <c>:latest</c> looked right
    /// a moment ago" into a pin: the launcher resolves the ref ONCE, checks that digest, and then
    /// creates the container from the digest, so a tag re-pointed between the check and the create
    /// cannot change which bytes run. The default returns <c>null</c> — an engine (or test fake) with
    /// no image store has no digest to offer and stays on its ref, exactly as before.</para>
    /// </summary>
    Task<string?> ImageDigestAsync(string imageRef, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    /// <summary>Run a command inside a live sandbox (e.g. <c>devbox add jq</c>) and return its exit + output.</summary>
    Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default);

    /// <summary>
    /// Freeze every process in the jail (<c>docker pause</c> — SIGSTOP via the freezer cgroup) so the
    /// daemon may safely touch the worktree. This is the P2-09 cooperative-yield <b>timeout</b> path: a
    /// silent agent that never answers <c>[IPC_UPDATE_READY]</c> is paused before any Git mutation.
    /// </summary>
    Task PauseAsync(string containerId, CancellationToken ct = default);

    /// <summary>Resume a paused jail (<c>docker unpause</c>). Called through the yield token on resume.</summary>
    Task UnpauseAsync(string containerId, CancellationToken ct = default);

    /// <summary>Whether the jail is currently frozen (<c>docker inspect .State.Paused</c>). Callers that
    /// must tolerate "already paused" classify BY THIS STATE, never by error-message substring — engine
    /// wordings differ per version. Default false: an engine that cannot answer lets the pause/unpause
    /// call itself be the honest arbiter.</summary>
    Task<bool> IsPausedAsync(string containerId, CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>Stop the jail without removing it (the persistent jail can be re-started later).</summary>
    Task StopAsync(string containerId, CancellationToken ct = default);

    /// <summary>Remove the jail entirely.</summary>
    Task RemoveAsync(string containerId, CancellationToken ct = default);
}
