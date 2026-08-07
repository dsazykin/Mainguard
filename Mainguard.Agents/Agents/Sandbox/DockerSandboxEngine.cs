using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Security;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>Daemon-side config for <see cref="DockerSandboxEngine"/> (network + proxy + userns).</summary>
/// <param name="UsernsMode">MG-17 — the jail's <c>HostConfig.UsernsMode</c>. Defaults to
/// <see cref="UsernsRemapPolicy.InheritDaemonRemap"/>: the daemon is configured with
/// <c>"userns-remap": "mainguard"</c> (<see cref="Mainguard.Agents.Agents.Bootstrap.FirstBootStep"/>),
/// and inheriting it is how a container IS remapped — Docker offers no per-container "definitely remap"
/// value, only the <see cref="UsernsRemapPolicy.OptOutUsernsMode"/> escape, which
/// <see cref="ContainerSpecBuilder"/> refuses. The named constant replaces the bare <c>""</c> the audit
/// found here so the value states its intent instead of looking like an unset field.</param>
/// <param name="ProxyContainerName">The egress proxy whose dnsmasq every jail pins as its resolver
/// (MG-7). Named here rather than hard-coded so a test substrate can point at its own proxy.</param>
/// <param name="SecretWriteTimeout">Bound on each fixed-purpose exec on the SPAWN path — secret
/// delivery and CLI-credential restore (see <see cref="DockerSandboxEngine.DefaultSecretWriteTimeout"/>
/// for the default and the reasoning). Null = the default. Overridable so a deliberately slow substrate
/// can shorten it; it is not a knob for making a wedged endpoint acceptable.</param>
public sealed record SandboxEngineOptions(
    string NetworkName,
    string ProxyUrl,
    string UsernsMode = UsernsRemapPolicy.InheritDaemonRemap,
    string ProxyContainerName = EgressProxyConfigurator.ProxyContainerName,
    TimeSpan? SecretWriteTimeout = null);

/// <summary>
/// The Docker implementation of <see cref="ISandboxEngine"/> (P2-07). Builds the hardened create
/// request with <see cref="ContainerSpecBuilder"/> and drives the persistent-jail lifecycle through
/// Docker.DotNet. Docker is the sole source of truth for liveness (no PID/lock files), and there is
/// <b>no runtime image-build</b> path — toolchains sideload via <c>devbox add</c>
/// (<see cref="ExecAsync"/>) into the static base image (G-16).
///
/// <para>Secrets are written <b>after</b> start via an stdin exec (never <c>Env</c>/argv/disk): the
/// P2-01 credential env-file to the agent-owned 0400 tmpfs, and the OOB key <c>K</c> to the
/// supervisor-owned 0400 tmpfs (G2 control 1). That stdin travels over
/// <see cref="IExecStdinTransport"/> and <b>not</b> over Docker.DotNet, which cannot deliver it at all
/// against a modern engine — see <see cref="DockerSocketExecStdinTransport"/> for the measurements and
/// for why <c>PUT /containers/{id}/archive</c> is not an available replacement on a read-only-rootfs
/// jail whose secret paths are tmpfs.</para>
///
/// <para><b>Every stdin-attached exec on the spawn path is time-bounded</b>
/// (<see cref="DefaultSecretWriteTimeout"/>). A Docker endpoint can accept the attach and then deliver
/// nothing — Docker Desktop's WSL2 socket proxy does exactly this for exec stdin, verified — and an
/// unbounded wait there turned a broken endpoint into a spawn that never returned: no error, no
/// diagnostic, no timeout, forever. Failing loudly in 30 seconds with
/// <see cref="SandboxExecTimeoutException"/> is strictly better than that.</para>
/// </summary>
public sealed class DockerSandboxEngine : ISandboxEngine
{
    /// <summary>
    /// The bound on each fixed-purpose exec on the spawn path (secret write, CLI-credential restore).
    /// These write a few KiB to a LOCAL Docker endpoint and finish in milliseconds; anything past this
    /// is a wedged endpoint, not slow work. Chosen generously so a loaded CI runner never trips it.
    /// </summary>
    public static readonly TimeSpan DefaultSecretWriteTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Bound on the CONTROL-PLANE calls of a general <see cref="ExecAsync"/> (create / attach / inspect).
    /// The command's own runtime is deliberately NOT bounded here — a verification run or a build can
    /// legitimately take many minutes, and the caller's <c>CancellationToken</c> governs that. What is
    /// bounded is the part that must answer promptly regardless of the command: if Docker will not even
    /// create or attach the exec within this, no command is running and waiting longer accomplishes
    /// nothing.
    /// </summary>
    public static readonly TimeSpan ControlPlaneTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Bound on the best-effort cleanup exec that unlinks a half-written secret. Short on
    /// purpose: it runs BECAUSE the endpoint just proved slow, and a cleanup that hangs reintroduces
    /// the very failure it is cleaning up after.</summary>
    private static readonly TimeSpan SecretCleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly IDockerClient _docker;
    private readonly SandboxEngineOptions _options;
    private readonly TimeSpan _secretWriteTimeout;
    private readonly IExecStdinTransport _stdin;

    /// <param name="stdin">How secret bytes reach a jail. Defaults to
    /// <see cref="DockerSocketExecStdinTransport"/> — Docker.DotNet's own stdin path is NOT usable (it
    /// delivers neither the bytes nor the half-close against modern engines; see that class for the
    /// measurements). Injectable so the unit suite can drive the failure paths without a daemon.</param>
    public DockerSandboxEngine(IDockerClient docker, SandboxEngineOptions options, IExecStdinTransport? stdin = null)
    {
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _secretWriteTimeout = options.SecretWriteTimeout ?? DefaultSecretWriteTimeout;
        // Constructed eagerly but RESOLVED lazily: DockerSocketExecStdinTransport.For only captures a
        // callback, so building an engine still needs no live daemon.
        _stdin = stdin ?? DockerSocketExecStdinTransport.For(docker);
    }

    public async Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = ContainerSpecBuilder.ContainerName(request.RepoHash, request.AgentId);

        // MG-36: the jail's network and proxy URL come from the request when the caller resolved a
        // per-agent segment (the production launcher always does), and fall back to the engine-wide
        // options for the ad-hoc harnesses that run on `bridge` or on the shared network.
        var networkName = string.IsNullOrWhiteSpace(request.NetworkName) ? _options.NetworkName : request.NetworkName;
        var proxyUrl = string.IsNullOrWhiteSpace(request.ProxyUrl) ? _options.ProxyUrl : request.ProxyUrl;

        // MG-7: the resolver pin is the proxy's CURRENT address on THIS jail's network, so it is
        // resolved per spawn rather than captured at construction — the proxy is recreated on image
        // upgrade and on corpse replacement, and Docker hands out a new address each time (and with
        // MG-36 a different address per segment).
        var dnsServer = await ResolveDnsServerAsync(networkName, ct).ConfigureAwait(false);

        // Built before the reuse check, not just before the create: the reuse check has to know
        // which per-owner secret directories THIS spawn expects (see staleSecretLayout below).
        var credentials = CredTmpfsSpec.Create(request.AgentUid, request.SupervisorUid);

        var existing = await FindByNameAsync(name, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            // Base-image upgrade → recreate. A persistent jail created before the bare-mirror mount
            // existed also recreates (mounts are fixed at container create; without the mirror the
            // worktree's gitdir pointer dangles and every in-jail git command fails).
            var missingBareMount = !string.IsNullOrEmpty(request.BareRepoPath)
                && (existing.Mounts is null || existing.Mounts.All(m => m.Destination != request.BareRepoPath));
            // MG-3: the same reasoning for the per-agent repository. A jail created before MG-3 has its
            // worktree linked off the mirror and carries no agents/ mount, so reusing it would leave the
            // agent committing into the mirror — exactly the write path this change exists to remove.
            var missingAgentRepoMount = !string.IsNullOrEmpty(request.AgentRepoPath)
                && (existing.Mounts is null || existing.Mounts.All(m => m.Destination != request.AgentRepoPath));
            // MG-3 stage 3: a jail created while the mirror mount was still read-write must be recreated,
            // or the MG-3 write path survives a daemon upgrade in every already-running container. Mount
            // options are fixed at create; recreating is the only way to change them.
            var writableMirror = ContainerSpecBuilder.MirrorMountReadOnly
                && !string.IsNullOrEmpty(request.BareRepoPath)
                && existing.Mounts is not null
                && existing.Mounts.Any(m => m.Destination == request.BareRepoPath && m.RW);
            // MG-7: HostConfig.Dns is fixed at create, so a jail that outlived a proxy recreate is
            // pinned to an address that no longer answers — every name in it would fail to resolve.
            // Recreating is the only way to re-pin; the alternative is a jail with no working DNS.
            var stalePin = dnsServer is not null
                && !await PinnedDnsMatchesAsync(existing.ID, dnsServer, ct).ConfigureAwait(false);
            // MG-36: a jail created before segmentation (or on another segment) must move — its network
            // is fixed at create, so an unsegmented jail would otherwise sit on the flat network forever.
            var wrongNetwork = !await AttachedToAsync(existing.ID, networkName, ct).ConfigureAwait(false);
            // MG-43: a jail created before the package cache existed has no cache mount, and mounts are
            // fixed at create. Reusing it would leave every restore filling the 256 MiB tmpfs $HOME —
            // the exact failure this closes — while the daemon logged a healthy cache it was not using.
            var missingCacheMount = !string.IsNullOrEmpty(request.PackageCachePath)
                && (existing.Mounts is null
                    || existing.Mounts.All(m => m.Destination != PackageCachePolicy.SandboxMount));
            // A jail created before the secrets moved into per-owner directories carries the old flat
            // /run/secrets tmpfs, and tmpfs entries — like mounts — are fixed at create. Reusing one
            // would leave the write path execing as a non-root owner into a directory that does not
            // exist and is not writable by it: the very EPERM the per-owner layout removes, resurrected
            // for every container that outlived the upgrade. Caught here rather than at the write,
            // because recreating is the only way to change the tmpfs set.
            //
            // Measured, not theorised: the RequiresDocker secret-delivery test failed on exactly this —
            // a jail left behind by a pre-change run was reused, and the supervisor's key had nowhere to
            // land — then passed once the stale container was gone.
            var staleSecretLayout =
                !await HasOwnedSecretDirsAsync(existing.ID, credentials, ct).ConfigureAwait(false);
            // MG-27: the ref is now a content digest, and Docker's container LIST reports a short image
            // id — compare through the matcher, never with `!=`, or every reuse would look like an
            // upgrade and recreate a perfectly good jail on every spawn.
            if (staleSecretLayout
                || !SandboxImageDigest.SameImage(existing.Image, request.ImageRef)
                || missingBareMount || missingAgentRepoMount || writableMirror || stalePin || wrongNetwork
                || missingCacheMount)
            {
                await _docker.Containers.RemoveContainerAsync(existing.ID,
                    new ContainerRemoveParameters { Force = true }, ct).ConfigureAwait(false);
            }
            else
            {
                if (!string.Equals(existing.State, "running", StringComparison.OrdinalIgnoreCase))
                    await _docker.Containers.StartContainerAsync(existing.ID, new ContainerStartParameters(), ct).ConfigureAwait(false);
                // A restarted jail's tmpfs $HOME came back empty — restore the CLI's saved login
                // state. Write-if-absent, so a still-running jail's fresher tokens are never
                // clobbered by the host keychain's older copy.
                await RestoreCliCredentialsAsync(existing.ID, request, ct).ConfigureAwait(false);
                // Same reason, same write-if-absent rule: a relaunched jail's home came back empty, so
                // the user's approved-command list has to be put back or every command is re-prompted.
                await RestoreCliSettingsAsync(existing.ID, request, ct).ConfigureAwait(false);
                // MG-43: re-prove the cache on the REUSE path too. The mount survived (mounts are fixed
                // at create), but the tree behind it may not have — an eviction, a manual cleanup, or a
                // failed VM boot can leave the bind mount pointing at a directory that is gone, and a
                // reused jail is exactly the path that never re-checks anything.
                await AssertPackageCacheUsableAsync(existing.ID, request, ct).ConfigureAwait(false);
                return new SandboxHandle(existing.ID, Reused: true);
            }
        }

        var spec = new ContainerSpecRequest(
            request.RepoHash, request.AgentId, request.WorktreePath, request.ImageRef,
            request.Limits, networkName, credentials, proxyUrl, _options.UsernsMode,
            request.AdaptersRootPath, request.IpcDirPath, request.BareRepoPath, dnsServer, request.AgentRepoPath,
            request.PackageCachePath);

        var create = ContainerSpecBuilder.Build(spec);
        var created = await _docker.Containers.CreateContainerAsync(create, ct).ConfigureAwait(false);
        await _docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct).ConfigureAwait(false);

        // Deliver secrets to their per-owner 0400 tmpfs files (content only via stdin — never argv/env).
        var envContent = CredentialInjector.BuildEnvFileContent(request.Secrets.AgentEnv);
        try
        {
            // MG-43: before anything else touches this container, prove the package cache is really
            // there and really writable FROM INSIDE IT. The daemon asked for the mount; that is not
            // evidence about the container (MG-42 paid for that conflation once already). Ordered first
            // so a jail with no usable cache is destroyed on the same path a failed secret write uses,
            // rather than being handed out to run a verification that would die at ENOSPC.
            await AssertPackageCacheUsableAsync(created.ID, request, ct).ConfigureAwait(false);

            await WriteSecretFileAsync(created.ID, credentials.CredentialPath,
                Encoding.UTF8.GetBytes(envContent), credentials.AgentUid, ct).ConfigureAwait(false);
            await WriteSecretFileAsync(created.ID, credentials.OobKeyPath,
                request.Secrets.OobKey, credentials.SupervisorUid, ct).ConfigureAwait(false);
            await RestoreCliCredentialsAsync(created.ID, request, ct).ConfigureAwait(false);
            // The CLI's saved settings — the approved-command list the user built in an earlier jail.
            // Ordered after the credential restore purely so a failure in the security-bearing write is
            // never masked by one in the convenience write; both share the same failure handling below.
            await RestoreCliSettingsAsync(created.ID, request, ct).ConfigureAwait(false);
        }
        catch
        {
            // Secret delivery failed (a timeout on a wedged endpoint, a dead container, anything). Each
            // write already unlinks its own partial file in-jail, but that is best effort against the
            // same endpoint that just failed. THIS container was created moments ago by this call and
            // has never been handed to anyone, so destroying it is available and is the only cleanup
            // that needs nothing from the exec channel: the tmpfs holding every half-written secret goes
            // with it. Never swallow the original failure — that is the whole point of the fix.
            await TryForceRemoveAsync(created.ID).ConfigureAwait(false);
            throw;
        }

        return new SandboxHandle(created.ID, Reused: false);
    }

    public async Task<bool> ImageExistsAsync(string imageRef, CancellationToken ct = default)
    {
        try
        {
            await _docker.Images.InspectImageAsync(imageRef, ct).ConfigureAwait(false);
            return true;
        }
        catch (DockerImageNotFoundException)
        {
            return false;
        }
    }

    public async Task<string?> ImageVersionAsync(string imageRef, CancellationToken ct = default)
    {
        try
        {
            var inspect = await _docker.Images.InspectImageAsync(imageRef, ct).ConfigureAwait(false);
            var labels = inspect.Config?.Labels;
            return labels is not null && labels.TryGetValue(SandboxImageVersions.LabelKey, out var version)
                ? version
                : null;
        }
        catch (DockerImageNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// MG-27 — the immutable content digest behind <paramref name="imageRef"/>. Docker's image inspect
    /// answers <c>Id</c> as <c>sha256:&lt;64 hex&gt;</c>, computed over the image's own config and
    /// layers: unlike the <see cref="SandboxImageVersions.LabelKey"/> label (an arbitrary string the
    /// builder chooses) it cannot be set to a value the image does not have.
    /// </summary>
    public async Task<string?> ImageDigestAsync(string imageRef, CancellationToken ct = default)
    {
        try
        {
            var inspect = await _docker.Images.InspectImageAsync(imageRef, ct).ConfigureAwait(false);
            return SandboxImageDigest.Normalize(inspect.ID);
        }
        catch (DockerImageNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs a command in a live jail. The three CONTROL-PLANE calls are bounded by
    /// <see cref="ControlPlaneTimeout"/>; the output drain in the middle is not, because its duration is
    /// the command's own runtime and that is the caller's <paramref name="ct"/> to govern. Splitting it
    /// this way is deliberate: putting a fixed bound on the drain would kill legitimate long-running
    /// verification runs, while leaving create/attach/inspect unbounded leaves a hang that no command is
    /// even running during.
    /// </summary>
    public async Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
    {
        var exec = await RunBoundedAsync(
            token => _docker.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
            {
                AttachStdout = true,
                AttachStderr = true,
                Cmd = command.ToList(),
            }, token),
            ControlPlaneTimeout, "exec create", containerId, ct).ConfigureAwait(false);

        using var stream = await RunBoundedAsync(
            token => _docker.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false, token),
            ControlPlaneTimeout, "exec attach", containerId, ct).ConfigureAwait(false);

        var (stdout, stderr) = await stream.ReadOutputToEndAsync(ct).ConfigureAwait(false);

        var inspect = await RunBoundedAsync(
            token => _docker.Exec.InspectContainerExecAsync(exec.ID, token),
            ControlPlaneTimeout, "exec inspect", containerId, ct).ConfigureAwait(false);
        return new SandboxExecResult((int)inspect.ExitCode, stdout, stderr);
    }

    public Task PauseAsync(string containerId, CancellationToken ct = default) =>
        _docker.Containers.PauseContainerAsync(containerId, ct);

    public Task UnpauseAsync(string containerId, CancellationToken ct = default) =>
        _docker.Containers.UnpauseContainerAsync(containerId, ct);

    public Task StopAsync(string containerId, CancellationToken ct = default) =>
        _docker.Containers.StopContainerAsync(containerId, new ContainerStopParameters(), ct);

    public Task RemoveAsync(string containerId, CancellationToken ct = default) =>
        _docker.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }, ct);

    /// <summary>
    /// MG-7 — the address of the proxy's dnsmasq on the default-deny network, which every jail there
    /// pins as its ONLY resolver. Returns null (no pin) for engines that are not on that network: the
    /// merge-queue and lifecycle harnesses run jails on <c>bridge</c> with no proxy at all, and pinning
    /// them at an address that does not exist would break them for no security gain. On the agent
    /// network the absence of an address is fatal — <see cref="ContainerSpecBuilder"/> refuses to build
    /// an unpinned spec there, which is the fail-closed half of this control.
    /// </summary>
    private async Task<string?> ResolveDnsServerAsync(string networkName, CancellationToken ct)
    {
        // MG-36: the class of network, not one literal name — see EgressProxyConfigurator.
        if (!EgressProxyConfigurator.IsDefaultDenyAgentNetwork(networkName))
            return null;

        try
        {
            var inspect = await _docker.Containers.InspectContainerAsync(_options.ProxyContainerName, ct).ConfigureAwait(false);
            // The proxy's address ON THIS SEGMENT. A jail cannot reach the proxy's address on another
            // segment — internal networks are isolated from one another, which is the whole point.
            return EgressProxyConfigurator.ProxyAddressOf(inspect, networkName);
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
        }
    }

    /// <summary>MG-36 — true when a reusable jail already sits on <paramref name="networkName"/>. A
    /// container that vanished under us counts as matching so the caller's own recreate path — not this
    /// probe — decides what to do about it (same convention as <see cref="PinnedDnsMatchesAsync"/>).</summary>
    private async Task<bool> AttachedToAsync(string containerId, string networkName, CancellationToken ct)
    {
        try
        {
            var inspect = await _docker.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
            var networks = inspect.NetworkSettings?.Networks;
            return networks is not null && networks.ContainsKey(networkName);
        }
        catch (DockerContainerNotFoundException)
        {
            return true;
        }
    }

    /// <summary>
    /// True when an existing jail already carries a per-owner tmpfs for BOTH secrets — i.e. it was
    /// created by a build that knows secrets are written by their owners rather than chowned into place.
    ///
    /// <para>Read from <c>HostConfig.Tmpfs</c> rather than by looking inside the container: the question
    /// is what the container was CREATED with (tmpfs entries are fixed at create, like mounts), and an
    /// in-jail <c>stat</c> would need an exec — on the very container whose usability is in doubt.</para>
    ///
    /// <para>A container that vanished under us counts as satisfying this, so the caller's own recreate
    /// path — not this probe — decides what to do about it. Same convention as the sibling probes.</para>
    /// </summary>
    private async Task<bool> HasOwnedSecretDirsAsync(string containerId, CredTmpfsSpec credentials, CancellationToken ct)
    {
        try
        {
            var inspect = await _docker.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
            var tmpfs = inspect.HostConfig?.Tmpfs;
            if (tmpfs is null)
            {
                return false;
            }

            // The directory of each ACTUAL secret path, exactly as AssertSecretDirsOwned derives it —
            // so "was this container built for the layout this spawn uses?" is one question, not two
            // that can drift.
            return tmpfs.ContainsKey(CredTmpfsSpec.DirectoryOf(credentials.CredentialPath))
                && tmpfs.ContainsKey(CredTmpfsSpec.DirectoryOf(credentials.OobKeyPath));
        }
        catch (DockerContainerNotFoundException)
        {
            return true;
        }
    }

    /// <summary>True when a reusable jail is already pinned at <paramref name="dnsServer"/>. A container
    /// that vanished under us counts as matching so the caller's own recreate path — not this probe —
    /// decides what to do about it.</summary>
    private async Task<bool> PinnedDnsMatchesAsync(string containerId, string dnsServer, CancellationToken ct)
    {
        try
        {
            var inspect = await _docker.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
            var pinned = inspect.HostConfig?.DNS;
            return pinned is { Count: 1 } && string.Equals(pinned[0], dnsServer, StringComparison.Ordinal);
        }
        catch (DockerContainerNotFoundException)
        {
            return true;
        }
    }

    private async Task<ContainerListResponse?> FindByNameAsync(string name, CancellationToken ct)
    {
        var list = await _docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["name"] = new Dictionary<string, bool> { ["/" + name] = true },
            },
        }, ct).ConfigureAwait(false);

        // The name filter is a substring match; require an exact "/name" entry.
        return list.FirstOrDefault(c => c.Names.Any(n => n == "/" + name));
    }

    /// <summary>
    /// MG-43 — proves, in the started container, that the package cache mount is present and writable by
    /// the agent uid; throws <see cref="PackageCacheUnavailableException"/> otherwise.
    ///
    /// <para><b>Why the request is not evidence.</b> "The daemon put a mount in the create request" and
    /// "the container has a writable directory there" are different facts, and MG-42 is the standing
    /// receipt for what happens when they are conflated: our records said provisioned, the container said
    /// otherwise, and the difference surfaced as an exit-127 that read like the agent's code being
    /// broken. Here the equivalent failure is worse, because it is silent for a while — restore starts
    /// writing into the tmpfs <c>$HOME</c> or the read-only rootfs and dies partway with <c>ENOSPC</c> or
    /// <c>EROFS</c> in a merge-queue log, and the queue records a failed verification.</para>
    ///
    /// <para>A no-cache request (the substrate-less doubles and the ad-hoc harnesses) is not probed: they
    /// have no cache by design, and <see cref="ContainerSpecBuilder"/> has already refused any spec that
    /// claims one without mounting it. This method is deliberately NOT wrapped in a catch-all — an
    /// unusable cache is a spawn failure, never a warning.</para>
    /// </summary>
    private async Task AssertPackageCacheUsableAsync(
        string containerId, SandboxSpawnRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.PackageCachePath))
            return;

        SandboxExecResult probe;
        try
        {
            probe = await ExecAsync(containerId, PackageCachePolicy.WritabilityProbe(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PackageCacheUnavailableException(
                PackageCachePolicy.SandboxMount, request.PackageCachePath,
                $"the in-jail probe could not be run at all: {ex.Message}");
        }

        var failure = PackageCachePolicy.DescribeProbeFailure(probe.Stdout, probe.ExitCode);
        if (failure is not null)
        {
            throw new PackageCacheUnavailableException(
                PackageCachePolicy.SandboxMount, request.PackageCachePath,
                failure + $" (probe exit {probe.ExitCode.ToString(CultureInfo.InvariantCulture)}, "
                + $"stderr: {probe.Stderr.Trim()})");
        }
    }

    /// <summary>
    /// Restores the CLI's saved login state (host-keychain-sourced) into the jail's tmpfs $HOME so
    /// an interactive login survives relaunches. Each file is written as the AGENT uid (so the CLI
    /// can refresh/rewrite it later) over exec stdin, 0600, parents created — and ONLY when the
    /// path does not already exist: a live jail's tokens are always fresher than the host copy.
    /// Paths were validated home-relative upstream (AdapterManifest.IsHomeRelativeFilePath).
    /// </summary>
    private async Task RestoreCliCredentialsAsync(string containerId, SandboxSpawnRequest request, CancellationToken ct)
    {
        if (request.Secrets.CliCredentialFiles is not { Count: > 0 } files)
            return;

        foreach (var file in files)
        {
            if (file.Content is not { Length: > 0 })
                continue;

            var path = ContainerSpecBuilder.AgentHome + "/" + file.HomeRelativePath;
            var length = file.Content.Length.ToString(CultureInfo.InvariantCulture);
            var exec = new ExecStdinRequest(
                containerId,
                request.AgentUid.ToString(CultureInfo.InvariantCulture),
                // path is not secret (argv-safe); the secret content is piped via stdin only.
                new[]
                {
                    "sh", "-c",
                    // The exists-branch still drains stdin so the daemon-side write never races a
                    // finished exec. Everything else is the same stage-then-rename as the secret write
                    // below — and here the write-if-absent guard makes it doubly load-bearing: the target
                    // path may hold the LIVE jail's fresher tokens, so nothing on the failure path is
                    // ever allowed to touch it.
                    "umask 0077\n"
                    + "if [ -e \"$1\" ]; then head -c \"$2\" > /dev/null; exit 0; fi\n"
                    + "mkdir -p \"$(dirname \"$1\")\" || exit 74\n"
                    + "head -c \"$2\" > \"$1.partial\" || exit 74\n"
                    + "[ \"$(wc -c < \"$1.partial\" | tr -d ' ')\" = \"$2\" ] || { rm -f \"$1.partial\"; exit 75; }\n"
                    + "mv \"$1.partial\" \"$1\"\n",
                    "sh", path, length,
                },
                file.Content);

            // Same unbounded-wait bug as the secret write below, and the same fix — this loop is on the
            // SAME spawn path, and it also runs on the REUSE path, where a hang wedged every relaunch of
            // an existing jail.
            await WriteSecretOverExecStdinAsync(path, exec, "CLI credential restore", ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Restores the CLI's saved SETTINGS — the user's approved-command list above all — into the jail's
    /// throwaway trees, so a fresh agent does not re-prompt for everything that was already approved.
    ///
    /// <para>Mechanically identical to the credential restore and for the same three reasons: the write
    /// runs as the AGENT uid (the CLI has to be able to rewrite the file when the user approves
    /// something new), it goes over exec <b>stdin</b> rather than <c>docker cp</c> (which writes
    /// underneath the tmpfs <c>$HOME</c> and reports success while the container sees nothing), and it
    /// is <b>write-if-absent</b> — a live jail's own copy is always fresher than the host's.</para>
    ///
    /// <para>The only difference is the ROOT: an entry may be relative to <c>$HOME</c> or to
    /// <c>/workspace</c>, because a CLI's user-level and project-level settings live in different trees
    /// and it is the PROJECT one that holds "don't ask again" grants. Both are wiped every spawn, which
    /// is why either needs restoring at all. Paths were validated relative upstream
    /// (<see cref="AdapterManifest.IsHomeRelativeFilePath"/>), so neither root can be escaped.</para>
    /// </summary>
    private async Task RestoreCliSettingsAsync(string containerId, SandboxSpawnRequest request, CancellationToken ct)
    {
        if (request.CliSettingsFiles is not { Count: > 0 } files)
            return;

        foreach (var file in files)
        {
            if (file.Content is not { Length: > 0 })
                continue;

            var path = SettingsRootPath(file.Root) + "/" + file.RelativePath;
            var length = file.Content.Length.ToString(CultureInfo.InvariantCulture);
            var exec = new ExecStdinRequest(
                containerId,
                request.AgentUid.ToString(CultureInfo.InvariantCulture),
                // path is argv-safe; the content is piped via stdin only — the same shape as every
                // other in-jail write, so there is one script to reason about rather than two.
                new[]
                {
                    "sh", "-c",
                    "umask 0077\n"
                    + "if [ -e \"$1\" ]; then head -c \"$2\" > /dev/null; exit 0; fi\n"
                    + "mkdir -p \"$(dirname \"$1\")\" || exit 74\n"
                    + "head -c \"$2\" > \"$1.partial\" || exit 74\n"
                    + "[ \"$(wc -c < \"$1.partial\" | tr -d ' ')\" = \"$2\" ] || { rm -f \"$1.partial\"; exit 75; }\n"
                    + "mv \"$1.partial\" \"$1\"\n",
                    "sh", path, length,
                },
                file.Content);

            await WriteSecretOverExecStdinAsync(path, exec, "CLI settings restore", ct).ConfigureAwait(false);
        }

        await ExcludeRestoredSettingsFromGitAsync(containerId, files, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps a restored WORKSPACE settings file out of the agent's commits.
    ///
    /// <para><b>Why this is not optional.</b> <c>/workspace</c> is the agent's git worktree, so a file
    /// restored into it is an UNTRACKED file in the tree the agent commits — and the keep-alive rebase
    /// cycle's dirty-tree path is <c>git add -A &amp;&amp; git commit</c>. Without this, persisting the
    /// user's permission allowlist would start committing it into their repository and merging it to
    /// main: a convenience feature quietly writing to the user's history. The MG-43 package cache was
    /// moved out of the worktree for exactly this reason; this file cannot move, because it is where the
    /// CLI reads it from.</para>
    ///
    /// <para>The entry goes in <c>$GIT_DIR/info/exclude</c> — the repository's LOCAL ignore file, which
    /// lives in the per-agent repo the daemon deletes at teardown. Nothing tracked is touched, the
    /// user's own <c>.gitignore</c> is not edited, and no state outlives the agent.</para>
    ///
    /// <para>Best effort by construction: a worktree that is not a git repository at all (every
    /// substrate-less test jail) resolves no git dir and this exits 0 having done nothing. A failure
    /// here must never fail a spawn — the settings are already in place, which is the user-visible
    /// half.</para>
    /// </summary>
    private async Task ExcludeRestoredSettingsFromGitAsync(
        string containerId, IReadOnlyList<SandboxSettingsFile> files, CancellationToken ct)
    {
        var workspacePaths = files
            .Where(f => f.Root == AdapterSettingsRoot.Workspace && f.Content is { Length: > 0 })
            .Select(f => f.RelativePath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (workspacePaths.Length == 0)
        {
            return;
        }

        var command = new List<string>
        {
            "sh", "-c",
            "cd " + ContainerSpecBuilder.WorkspaceTarget + " 2>/dev/null || exit 0\n"
            + "excl=$(git rev-parse --git-path info/exclude 2>/dev/null) || exit 0\n"
            + "[ -n \"$excl\" ] || exit 0\n"
            + "mkdir -p \"$(dirname \"$excl\")\" 2>/dev/null || exit 0\n"
            + "for p in \"$@\"; do\n"
            + "  grep -qxF \"/$p\" \"$excl\" 2>/dev/null || printf '/%s\\n' \"$p\" >> \"$excl\" || exit 0\n"
            + "done\n",
            "sh",
        };
        command.AddRange(workspacePaths); // positional — never interpolated into script text

        try
        {
            await ExecAsync(containerId, command, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The settings are already restored; failing the spawn over the ignore entry would trade a
            // working feature for a cosmetic one.
        }
    }

    /// <summary>The absolute in-jail directory a settings root names. The ONE mapping — the harvest side
    /// (<c>SandboxAgentLauncher</c>) resolves through this same method, so the two legs of the round trip
    /// cannot drift onto different directories.</summary>
    public static string SettingsRootPath(AdapterSettingsRoot root) => root switch
    {
        AdapterSettingsRoot.Workspace => ContainerSpecBuilder.WorkspaceTarget,
        _ => ContainerSpecBuilder.AgentHome,
    };

    /// <summary>
    /// Writes secret bytes to <paramref name="path"/> inside the container as a mode-0400 file owned by
    /// <paramref name="ownerUid"/>. Content flows over the exec's stdin (never argv/env), and the shell
    /// tightens the umask first.
    ///
    /// <para><b>The exec runs AS THE OWNER, and there is no <c>chown</c>.</b> It used to run as uid 0
    /// and chown the file to its owner afterwards — "root, so chown to the supervisor uid is
    /// permitted". That premise was false for this container's whole life, and it is what broke the
    /// coordinator: a jail is created with a non-root <c>User</c> AND <c>no-new-privileges</c>, and
    /// Docker hands an exec in such a container an EMPTY permitted/effective capability set even when
    /// the exec asks for uid 0. Measured on the shipping engine (Docker 20.10.24): that exec reports
    /// <c>uid=0</c> and <c>CapPrm: 0000000000000000</c> against a bounding set of <c>fb</c>, and its
    /// <c>chown</c> fails with <c>EPERM</c> — <c>chown: changing ownership of
    /// '/run/secrets/agent.env.partial': Operation not permitted</c>. Removing EITHER the non-root user
    /// or no-new-privileges restores <c>CapPrm: fb</c> and the chown succeeds; both are controls we
    /// keep (G-15, G2 control 4), so the chown is what had to go.</para>
    ///
    /// <para><b>Why nothing caught it.</b> The behaviour is engine-specific: the identical container on
    /// Engine 29.4.3 — what CI and Docker Desktop run — gives that exec <c>CapPrm: fb</c> and the chown
    /// succeeded, so the RequiresDocker leg that delivers both secrets end to end was green throughout.
    /// It only ever failed on <c>MainguardEnv</c>'s Docker 20.10.24, and there it was silent until the
    /// exec's exit status started being read. That is why the guard for this lives in the no-Docker
    /// leg (<c>ContainerSpecBuilderTests</c>, <c>SandboxSecretWriteTimeoutTests</c>): it is the only one
    /// that does not depend on which engine happens to be under the runner.</para>
    ///
    /// <para>What replaces it is ownership by construction: <see cref="ContainerSpecBuilder"/> mounts
    /// each secret's directory as a tmpfs that Docker creates — daemon-side, as real root — already
    /// owned by that secret's uid, so the owner simply CREATES its own file and <c>chmod 0400</c>s it
    /// (permitted to a file's owner with no capability at all). Nothing on this path depends on a
    /// capability, on uid 0, or on whether dockerd is userns-remapped.</para>
    ///
    /// <para>The shell <b>stages and renames</b> rather than writing the destination directly, and reads
    /// an exact byte count instead of to EOF. Both changes exist for the timeout: <c>cat &gt; "$1"</c>
    /// treats the socket closing as a successful end of input, so an abandoned write used to leave a
    /// TRUNCATED secret at the real path — chowned and chmodded 0400 by the two commands after it,
    /// therefore readable and indistinguishable from a good one. <c>head -c N</c> is self-terminating, a
    /// short read is detected by comparing the staged size to N, and the destination only ever comes into
    /// existence through an atomic <c>mv</c> of a complete, already-0400 file. A partial secret at the
    /// real path is now structurally impossible rather than something cleanup has to race.</para>
    /// </summary>
    internal Task WriteSecretFileAsync(string containerId, string path, byte[] content, int ownerUid, CancellationToken ct)
    {
        var length = content.Length.ToString(CultureInfo.InvariantCulture);
        var exec = new ExecStdinRequest(
            containerId,
            // The OWNER writes its own secret into its own tmpfs directory, so the file is correctly
            // owned the moment it exists and no privileged step is needed to hand it over.
            // path/length are not secret (argv-safe); the secret content is piped via stdin only.
            ownerUid.ToString(CultureInfo.InvariantCulture),
            new[]
            {
                "sh", "-c",
                "umask 0377\n"
                + "head -c \"$2\" > \"$1.partial\" || exit 74\n"
                // Short read ⇒ the endpoint stopped delivering. Destroy the scratch file and fail; the
                // destination is never created, so there is nothing readable to leak.
                + "[ \"$(wc -c < \"$1.partial\" | tr -d ' ')\" = \"$2\" ] || { rm -f \"$1.partial\"; exit 75; }\n"
                // Redundant against `umask 0377` and deliberately kept: the mode is the G-13 control,
                // and a umask is a property of the shell we happen to get rather than of the file we
                // promised. Owner-chmod needs no capability.
                + "chmod 0400 \"$1.partial\" || { rm -f \"$1.partial\"; exit 74; }\n"
                + "mv \"$1.partial\" \"$1\"\n",
                "sh", path, length,
            },
            content);

        return WriteSecretOverExecStdinAsync(path, exec, "secret-file write", ct);
    }

    /// <summary>
    /// The one bounded transport for every secret that travels over an exec's stdin.
    ///
    /// <para><b>Why a bound at all.</b> stdin here is a hijacked HTTP stream, and an endpoint can accept
    /// the attach and then deliver nothing in either direction — Docker Desktop's WSL2 socket proxy does
    /// exactly that, verified on a live daemon. Unbounded, the read at the end of the sequence never
    /// returns and <see cref="SpawnAsync"/> hangs forever with no error, no log line and no timeout. The
    /// same shape already bounded the egress proxy's config push; this path was simply missed.</para>
    ///
    /// <para><b>What happens to the half-written file — three layers, in order of strength.</b></para>
    ///
    /// <para>1. <b>The destination is never partially written.</b> Both in-jail shells read an exact byte
    /// count into <c>&lt;path&gt;.partial</c>, verify the staged size, and only then <c>mv</c> it into
    /// place. The obvious version of this code (<c>cat &gt; "$1"</c> followed by <c>chown</c>/<c>chmod
    /// 0400</c>) is actively dangerous on a timeout: abandoning the stream closes the socket, <c>cat</c>
    /// reads that as a clean EOF, and the shell runs on to leave a TRUNCATED secret at the real path in
    /// its final readable mode. Staging removes the failure mode instead of racing it — and on the
    /// CLI-restore path it also protects the write-if-absent guarantee, because the destination there may
    /// hold the live jail's fresher tokens and must never be touched by a failure path.</para>
    ///
    /// <para>2. <b>The staging file is unlinked.</b> Best effort, own short budget, own token (the
    /// caller's may already be cancelled — that is one of the reasons we are here). It removes only
    /// <c>&lt;path&gt;.partial</c>, never the destination, so it is safe to run unconditionally.</para>
    ///
    /// <para>3. <b>On the create path <see cref="SpawnAsync"/> destroys the container.</b> That needs
    /// nothing at all from the exec channel that just failed, and takes the whole tmpfs with it.</para>
    /// </summary>
    private async Task WriteSecretOverExecStdinAsync(
        string path, ExecStdinRequest exec, string operation, CancellationToken ct)
    {
        var containerId = exec.ContainerId;
        try
        {
            await RunBoundedAsync(
                async token =>
                {
                    var result = await _stdin.RunAsync(exec, token).ConfigureAwait(false);

                    // The exec's exit status was previously never read, so a short write, a failed
                    // chmod or a full tmpfs all reported success and produced a jail with no credentials
                    // and no explanation. The staged-write shells signal exactly that with 74/75.
                    if (result.ExitCode != 0)
                    {
                        throw new InvalidOperationException(
                            $"The in-jail {operation} of '{path}' exited {result.ExitCode} in container "
                            + $"'{containerId}' — the destination was NOT written (the shell stages to "
                            + "'<path>.partial' and only renames a complete file into place). Exit 75 means "
                            + "the endpoint delivered fewer bytes than promised; 74 means the file could not "
                            + "be created or chmodded — the exec runs as the secret's owner uid in a tmpfs "
                            + "directory Docker mounted owned by that uid, so a permission error here means "
                            + "that mount is missing or owned by someone else."
                            + (string.IsNullOrWhiteSpace(result.Output)
                                ? " The command printed nothing."
                                : $" It printed: {result.Output}"));
                    }

                    return true;
                },
                _secretWriteTimeout,
                $"{operation} of '{path}'",
                containerId,
                ct,
                onTimeout: () => TryUnlinkStagedSecretAsync(containerId, path)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SandboxExecTimeoutException)
        {
            // Not a timeout — a 409, a container that died, a non-zero exit, the caller cancelling. The
            // staging file may still be there in every one of those cases. RunBoundedAsync already ran
            // this for the timeout; this covers every other way out.
            await TryUnlinkStagedSecretAsync(containerId, path).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Unlinks the staging file a failed secret write may have left behind, best effort. It
    /// touches <c>&lt;path&gt;.partial</c> and nothing else — deliberately: on the CLI-restore path the
    /// destination can be the live jail's own, fresher credential file, and a cleanup that removed THAT
    /// would destroy a working login to tidy up after a failure that never reached it. Returns a
    /// human-readable outcome folded into the thrown diagnostic, because "is a partial secret still
    /// sitting in the jail?" must not require a second investigation.</summary>
    private async Task<string> TryUnlinkStagedSecretAsync(string containerId, string path)
    {
        var staged = path + ".partial";
        using var cts = new CancellationTokenSource(SecretCleanupTimeout);
        try
        {
            // No stdin on THIS exec: the failure being cleaned up after is specifically stdin delivery,
            // so the cleanup must not depend on it.
            var exec = await _docker.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
            {
                User = "0",
                AttachStdout = true,
                AttachStderr = true,
                Cmd = new List<string> { "sh", "-c", "rm -f \"$1\"", "sh", staged },
            }, cts.Token).ConfigureAwait(false);

            using var stream = await _docker.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false, cts.Token).ConfigureAwait(false);
            await stream.ReadOutputToEndAsync(cts.Token).ConfigureAwait(false);
            return $" No partial secret was left at '{path}' (it is only ever created by an atomic rename "
                 + $"of a complete file), and the staging file was unlinked (rm -f {staged}).";
        }
        catch (Exception ex)
        {
            return $" No partial secret was left at '{path}' — it is only ever created by an atomic rename "
                 + $"of a complete file — but the staging file '{staged}' could NOT be unlinked "
                 + $"({ex.GetType().Name}: {ex.Message}). On the spawn path the container is destroyed "
                 + "immediately after this, which removes it with the tmpfs.";
        }
    }

    /// <summary>Force-removes a container, treating "already gone" as success. Used only to destroy a
    /// container this call created and never handed out.</summary>
    private async Task TryForceRemoveAsync(string containerId)
    {
        try
        {
            // CancellationToken.None deliberately: the reason we are cleaning up may BE a cancelled
            // token, and a cleanup that skips itself because of that leaves the secret behind.
            await _docker.Containers.RemoveContainerAsync(containerId,
                new ContainerRemoveParameters { Force = true }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best effort — never replace the real failure with a cleanup failure.
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> under a hard wall-clock bound and converts an expiry into a typed,
    /// self-explaining <see cref="SandboxExecTimeoutException"/>.
    ///
    /// <para>The bound is enforced with <see cref="Task.WhenAny(Task, Task)"/> rather than by trusting
    /// <paramref name="body"/> to observe the linked token. That is the entire point: the failure being
    /// bounded is a socket read that may never observe anything, and a "timeout" that itself waits for
    /// cooperation is not a timeout. The linked source is still cancelled first so a well-behaved body
    /// unwinds and releases its stream.</para>
    ///
    /// <para>Caller cancellation is NOT reported as a timeout — it propagates as
    /// <see cref="OperationCanceledException"/>, so "the daemon is shutting down" and "the Docker
    /// endpoint is wedged" never get confused for one another.</para>
    /// </summary>
    /// <param name="onTimeout">Cleanup to run before throwing; its return value is appended to the
    /// diagnostic. Not run when the body completes or the caller cancels.</param>
    internal static async Task<T> RunBoundedAsync<T>(
        Func<CancellationToken, Task<T>> body,
        TimeSpan timeout,
        string operation,
        string containerId,
        CancellationToken ct,
        Func<Task<string>>? onTimeout = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var clock = new CancellationTokenSource();
        var work = body(linked.Token);

        // The delay is cancelled on the fast path so a busy daemon does not accumulate a live timer per
        // exec; its cancellation is observed rather than left to the finalizer.
        var expired = Task.Delay(timeout, clock.Token);
        Observe(expired);

        if (await Task.WhenAny(work, expired).ConfigureAwait(false) == work)
        {
            clock.Cancel();
            return await work.ConfigureAwait(false);
        }

        linked.Cancel();

        // The abandoned task may still fault later; observe it so it never surfaces as an unobserved
        // exception long after the real diagnostic was reported.
        Observe(work);

        // A caller who cancelled at the same moment gets a cancellation, not a "Docker is wedged" story.
        ct.ThrowIfCancellationRequested();

        var detail = onTimeout is null ? string.Empty : await onTimeout().ConfigureAwait(false);
        throw new SandboxExecTimeoutException(operation, containerId, timeout, detail);
    }

    /// <summary>Swallows the eventual outcome of a task nobody is awaiting any more, so an abandoned
    /// wait can never resurface as an <c>UnobservedTaskException</c> minutes after the real failure was
    /// reported.</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}

/// <summary>
/// A Docker exec on the sandbox path did not complete inside its budget.
///
/// <para>Raised instead of waiting forever. The live failure this exists for: a Docker endpoint that
/// accepts an exec attach and never delivers stdin (Docker Desktop's WSL2 socket proxy, verified) —
/// which made <see cref="DockerSandboxEngine.SpawnAsync"/> hang indefinitely with nothing at all to
/// diagnose from. The message names the operation, the container and the budget, and for a secret write
/// it also states whether the half-written file was removed from the jail, because that is the next
/// question anyone reading it will ask.</para>
/// </summary>
public sealed class SandboxExecTimeoutException : TimeoutException
{
    public SandboxExecTimeoutException(string operation, string containerId, TimeSpan timeout, string detail = "")
        : base($"The sandbox docker exec '{operation}' did not complete within {timeout.TotalSeconds:0.###}s "
             + $"against container '{containerId}'. The exec was created/attached but never finished, which "
             + "means the Docker endpoint is not delivering the exec's streams (a socket proxy that drops "
             + "exec stdin does exactly this) — waiting longer would have hung the spawn forever."
             + detail)
    {
        Operation = operation;
        ContainerId = containerId;
        Timeout = timeout;
        Detail = detail;
    }

    /// <summary>What timed out (e.g. <c>secret-file write of '/run/creds/env'</c>).</summary>
    public string Operation { get; }

    /// <summary>The container the exec was against.</summary>
    public string ContainerId { get; }

    /// <summary>The budget that expired.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The cleanup outcome appended to the message (empty when there was nothing to clean up).</summary>
    public string Detail { get; }
}
