using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Git.Security;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>Daemon-side config for <see cref="DockerSandboxEngine"/> (network + proxy + userns).</summary>
/// <param name="ProxyContainerName">The egress proxy whose dnsmasq every jail pins as its resolver
/// (MG-7). Named here rather than hard-coded so a test substrate can point at its own proxy.</param>
public sealed record SandboxEngineOptions(
    string NetworkName,
    string ProxyUrl,
    string UsernsMode = "",
    string ProxyContainerName = EgressProxyConfigurator.ProxyContainerName);

/// <summary>
/// The Docker implementation of <see cref="ISandboxEngine"/> (P2-07). Builds the hardened create
/// request with <see cref="ContainerSpecBuilder"/> and drives the persistent-jail lifecycle through
/// Docker.DotNet. Docker is the sole source of truth for liveness (no PID/lock files), and there is
/// <b>no runtime image-build</b> path — toolchains sideload via <c>devbox add</c>
/// (<see cref="ExecAsync"/>) into the static base image (G-16).
///
/// <para>Secrets are written <b>after</b> start via an stdin exec (never <c>Env</c>/argv/disk): the
/// P2-01 credential env-file to the agent-owned 0400 tmpfs, and the OOB key <c>K</c> to the
/// supervisor-owned 0400 tmpfs (G2 control 1).</para>
/// </summary>
public sealed class DockerSandboxEngine : ISandboxEngine
{
    private readonly IDockerClient _docker;
    private readonly SandboxEngineOptions _options;

    public DockerSandboxEngine(IDockerClient docker, SandboxEngineOptions options)
    {
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = ContainerSpecBuilder.ContainerName(request.RepoHash, request.AgentId);

        // MG-7: the resolver pin is the proxy's CURRENT address on the agent network, so it is resolved
        // per spawn rather than captured at construction — the proxy is recreated on image upgrade and
        // on corpse replacement, and Docker hands out a new address each time.
        var dnsServer = await ResolveDnsServerAsync(ct).ConfigureAwait(false);

        var existing = await FindByNameAsync(name, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            // Base-image upgrade → recreate. A persistent jail created before the bare-mirror mount
            // existed also recreates (mounts are fixed at container create; without the mirror the
            // worktree's gitdir pointer dangles and every in-jail git command fails).
            var missingBareMount = !string.IsNullOrEmpty(request.BareRepoPath)
                && (existing.Mounts is null || existing.Mounts.All(m => m.Destination != request.BareRepoPath));
            // MG-7: HostConfig.Dns is fixed at create, so a jail that outlived a proxy recreate is
            // pinned to an address that no longer answers — every name in it would fail to resolve.
            // Recreating is the only way to re-pin; the alternative is a jail with no working DNS.
            var stalePin = dnsServer is not null
                && !await PinnedDnsMatchesAsync(existing.ID, dnsServer, ct).ConfigureAwait(false);
            if (!string.Equals(existing.Image, request.ImageRef, StringComparison.Ordinal) || missingBareMount || stalePin)
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
                return new SandboxHandle(existing.ID, Reused: true);
            }
        }

        var credentials = CredTmpfsSpec.Create(request.AgentUid, request.SupervisorUid);
        var spec = new ContainerSpecRequest(
            request.RepoHash, request.AgentId, request.WorktreePath, request.ImageRef,
            request.Limits, _options.NetworkName, credentials, _options.ProxyUrl, _options.UsernsMode,
            request.AdaptersRootPath, request.IpcDirPath, request.BareRepoPath, dnsServer);

        var create = ContainerSpecBuilder.Build(spec);
        var created = await _docker.Containers.CreateContainerAsync(create, ct).ConfigureAwait(false);
        await _docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct).ConfigureAwait(false);

        // Deliver secrets to their per-owner 0400 tmpfs files (content only via stdin — never argv/env).
        var envContent = CredentialInjector.BuildEnvFileContent(request.Secrets.AgentEnv);
        await WriteSecretFileAsync(created.ID, credentials.CredentialPath,
            Encoding.UTF8.GetBytes(envContent), credentials.AgentUid, ct).ConfigureAwait(false);
        await WriteSecretFileAsync(created.ID, credentials.OobKeyPath,
            request.Secrets.OobKey, credentials.SupervisorUid, ct).ConfigureAwait(false);
        await RestoreCliCredentialsAsync(created.ID, request, ct).ConfigureAwait(false);

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

    public async Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
    {
        var exec = await _docker.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
        {
            AttachStdout = true,
            AttachStderr = true,
            Cmd = command.ToList(),
        }, ct).ConfigureAwait(false);

        using var stream = await _docker.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false, ct).ConfigureAwait(false);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(ct).ConfigureAwait(false);
        var inspect = await _docker.Exec.InspectContainerExecAsync(exec.ID, ct).ConfigureAwait(false);
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
    private async Task<string?> ResolveDnsServerAsync(CancellationToken ct)
    {
        if (!string.Equals(_options.NetworkName, EgressProxyConfigurator.AgentNetworkName, StringComparison.Ordinal))
            return null;

        try
        {
            var inspect = await _docker.Containers.InspectContainerAsync(_options.ProxyContainerName, ct).ConfigureAwait(false);
            return EgressProxyConfigurator.ProxyAddressOf(inspect);
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
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
            var exec = await _docker.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
            {
                User = request.AgentUid.ToString(CultureInfo.InvariantCulture),
                AttachStdin = true,
                AttachStdout = true,
                AttachStderr = true,
                // path is not secret (argv-safe); the secret content is piped via stdin only.
                Cmd = new List<string>
                {
                    "sh", "-c",
                    // The exists-branch still drains stdin so the daemon-side write never races a
                    // finished exec.
                    "umask 0077; if [ -e \"$1\" ]; then cat > /dev/null; exit 0; fi; mkdir -p \"$(dirname \"$1\")\" && cat > \"$1\"",
                    "sh", path,
                },
            }, ct).ConfigureAwait(false);

            using var stream = await _docker.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false, ct).ConfigureAwait(false);
            await stream.WriteAsync(file.Content, 0, file.Content.Length, ct).ConfigureAwait(false);
            stream.CloseWrite();
            await stream.ReadOutputToEndAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes secret bytes to <paramref name="path"/> inside the container as a mode-0400 file owned by
    /// <paramref name="ownerUid"/>. Content flows over the exec's stdin (never argv/env), the shell
    /// tightens the umask first, and the exec runs as root so it can chown to the supervisor uid.
    /// </summary>
    private async Task WriteSecretFileAsync(string containerId, string path, byte[] content, int ownerUid, CancellationToken ct)
    {
        var uid = ownerUid.ToString(CultureInfo.InvariantCulture);
        var exec = await _docker.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
        {
            User = "0", // root, so chown to the supervisor uid is permitted; the file ends 0400/uid.
            AttachStdin = true,
            AttachStdout = true,
            AttachStderr = true,
            // path is not secret (argv-safe); the secret content is piped via stdin only.
            Cmd = new List<string> { "sh", "-c", "umask 0377; cat > \"$1\"; chown \"$2\" \"$1\"; chmod 0400 \"$1\"", "sh", path, uid },
        }, ct).ConfigureAwait(false);

        using var stream = await _docker.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false, ct).ConfigureAwait(false);
        await stream.WriteAsync(content, 0, content.Length, ct).ConfigureAwait(false);
        stream.CloseWrite();
        await stream.ReadOutputToEndAsync(ct).ConfigureAwait(false);
    }
}
