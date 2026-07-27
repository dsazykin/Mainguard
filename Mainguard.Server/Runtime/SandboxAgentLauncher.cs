using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Adapters;
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

    private readonly IAgentEnvironment _environment;
    private readonly string _imageRef;
    private readonly InstalledAdapterCatalog _adapters;
    private readonly ILogger _log;

    public SandboxAgentLauncher(
        IAgentEnvironment environment, InstalledAdapterCatalog? adapters = null, ILoggerFactory? loggerFactory = null)
    {
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
    /// Provisions the worktree and starts the hardened jail for <paramref name="agentId"/> against the
    /// repo identified by <paramref name="repoHandle"/>. Returns the real container handle, or <c>null</c>
    /// when the repo is not provisioned (session-only path). On a failure <i>after</i> the worktree exists,
    /// the half-made worktree is cleaned up so no residue survives, then the failure propagates.
    /// </summary>
    public async Task<SandboxLaunchResult?> TryLaunchAsync(
        string repoHandle, string agentId, string agentKind, string? modelApiKey,
        string? ipcDirPath = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? extraEnv = null,
        IReadOnlyList<SandboxCredentialFile>? cliCredentials = null)
    {
        _log.LogInformation("launch begin: repo={Repo} kind={Kind}", repoHandle, agentKind);

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

        // agentKind → the CLI the user dynamically installed. Resolved BEFORE the worktree so an
        // unknown kind costs nothing; the jail still spawns without a launch command (the operator
        // gets a shell in a correct sandbox rather than a failed spawn), and the caller surfaces it.
        var adapter = _adapters.TryGet(agentKind);
        var launchCommand = adapter?.Launch;

        var worktreePath = _environment.Worktrees.CreateAgentWorktree(repoHandle, agentId);
        _log.LogInformation("worktree ready: {Path}", worktreePath);
        try
        {
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

            var secrets = BuildSecrets(modelApiKey, adapter, extraEnv, cliCredentials);
            var handle = await _environment.Sandboxes.SpawnAsync(new SandboxSpawnRequest(
                RepoHash: repoHandle,
                AgentId: agentId,
                WorktreePath: worktreePath,
                ImageRef: spawnImageRef,
                Limits: SandboxLimits.Default,
                Secrets: secrets,
                AgentUid: AgentUid,
                SupervisorUid: SupervisorUid,
                // Mount the shared CLI root read-only ONLY when CLIs are actually installed.
                AdaptersRootPath: _adapters.HasAny() ? AdapterPaths.VmRoot : null,
                // Coordinator-role jails only: the daemon-served spawn-channel dir (read-only mount).
                IpcDirPath: ipcDirPath,
                // The bare mirror at its identical VM path so the worktree's gitdir pointer resolves
                // in-jail (field bug 2026-07-23: every in-jail git command died "not a git repository").
                BareRepoPath: barePath,
                // MG-36: this agent's segment, and the proxy's address ON that segment. The address
                // rather than the proxy's NAME because one dnsmasq cannot answer the same name with a
                // different address per segment, and every other segment's address is unreachable.
                NetworkName: segment.NetworkName,
                ProxyUrl: segment.ProxyUrl(EgressProxyConfigurator.ProxyPort)), ct).ConfigureAwait(false);

            _log.LogInformation(
                "jail started: container={Container} reused={Reused} launchCmd={HasLaunch}",
                handle.ContainerId, handle.Reused, launchCommand is { Count: > 0 });
            return new SandboxLaunchResult(handle.ContainerId, handle.Reused, worktreePath, launchCommand);
        }
        catch (Exception ex)
        {
            // Leave no residue: remove the worktree we just created before surfacing the failure.
            _log.LogError(ex, "jail start failed after worktree — cleaning up worktree: repo={Repo}", repoHandle);
            TryRemoveWorktree(repoHandle, agentId);
            throw;
        }
    }

    /// <summary>Best-effort teardown of a launched agent: remove the jail, then its MG-36 network
    /// segment, then its worktree. Never throws.</summary>
    public async Task TeardownAsync(string? repoHash, string agentId, string containerId, CancellationToken ct = default)
    {
        try { await _environment.Sandboxes.RemoveAsync(containerId, ct).ConfigureAwait(false); }
        catch { /* never fail a stop from teardown */ }

        if (!string.IsNullOrEmpty(repoHash))
        {
            // MG-36: reclaim the segment. Docker's local bridge address pool is finite (~32 networks by
            // default), so a segment leaked per agent would eventually make spawning fail with an
            // address-pool exhaustion error that reads like anything but the cause. Ordered AFTER the
            // container removal because Docker refuses to delete a network with a live endpoint.
            try { await _environment.Egress.RemoveAgentSegmentAsync(repoHash, agentId, ct).ConfigureAwait(false); }
            catch { /* never fail a stop from teardown */ }

            TryRemoveWorktree(repoHash, agentId);
        }
    }

    private void TryRemoveWorktree(string repoHash, string agentId)
    {
        try { _environment.Worktrees.RemoveAgentWorktree(repoHash, agentId, force: true); }
        catch { /* best effort */ }
    }

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
            var envVar = adapter is null ? "ANTHROPIC_API_KEY" : adapter.ApiKeyEnvVar;
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
