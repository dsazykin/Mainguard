using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.Agents.Sandbox;

namespace Mainguard.Agents.Agents.Adapters;

/// <summary>
/// The macos-host <see cref="IAdapterInstallHost"/>: every command runs inside a DISPOSABLE
/// container of the hardened agent-base image, with the daemon-owned adapters and toolchains
/// roots bind-mounted read-write AT THEIR VM PATHS. Mounting at the VM paths is the whole trick:
/// the channels' command shapes, the registry markers' argv, and the spawn path's
/// VmRoot→SandboxMount rewrite all keep working verbatim, while the bytes land in the host trees
/// the daemon catalogs and the jails later mount READ-ONLY. Installing on the macOS host itself
/// would be wrong by construction — the CLIs and toolchains execute in-jail, which is linux.
///
/// <para>The install container is hardened to the same posture as the agent jails
/// (<see cref="HardeningArgs"/>, audit F49): <c>--cap-drop ALL</c> plus the minimal add-backs,
/// <c>no-new-privileges</c>, the shared default-deny <see cref="SeccompProfile"/>, a read-only root
/// filesystem with two named tmpfs, the jail memory/pids/CPU ceilings, and an explicit non-root user
/// pin — and it is removed on exit. It still runs on Docker's default bridge, because reaching the
/// registry IS the job (registry egress is an install-time, Mainguard-mediated act; the WSL2
/// substrate's installs likewise ran with the VM's own egress). The docker CLI is the one host
/// dependency, and it is run from an absolute allow-listed path
/// (<see cref="TrustedDockerBinary"/>), never off the daemon's inherited <c>PATH</c>.</para>
/// </summary>
public sealed class ContainerAdapterInstallHost : IAdapterInstallHost
{
    private readonly string _hostAdaptersRoot;
    private readonly string _hostToolchainsRoot;
    private readonly string _imageRef;

    public ContainerAdapterInstallHost(
        string hostAdaptersRoot, string hostToolchainsRoot, string? imageRef = null)
    {
        _hostAdaptersRoot = hostAdaptersRoot ?? throw new ArgumentNullException(nameof(hostAdaptersRoot));
        _hostToolchainsRoot = hostToolchainsRoot ?? throw new ArgumentNullException(nameof(hostToolchainsRoot));
        _imageRef = imageRef ?? SandboxImageVersions.AgentBaseRef();
    }

    public async Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
    {
        var result = await RunInContainerAsync(command, stdin: null, ct).ConfigureAwait(false);
        return new AdapterCommandResult(result.ExitCode, result.StdOut, result.StdErr);
    }

    public async Task WriteFileAsync(string path, string content, CancellationToken ct)
    {
        var dir = path.Contains('/') ? path[..path.LastIndexOf('/')] : ".";
        await RunInContainerAsync(new[] { "mkdir", "-p", dir }, stdin: null, ct).ConfigureAwait(false);
        var write = await RunInContainerAsync(new[] { "tee", path }, stdin: content, ct).ConfigureAwait(false);
        if (!write.Succeeded)
            throw new AdapterChannelException(AdapterChannelError.InstallFailed, $"Writing config shim '{path}' failed.");
    }

    /// <summary>Same base64-over-stdin staging discipline as the WSL host — the runner's stdin is
    /// text-only, and installing from the staged file is what makes the sha256 pin real.</summary>
    public async Task<string> StagePayloadAsync(string fileName, byte[] content, CancellationToken ct)
    {
        var safeName = string.Concat(fileName.Select(c =>
            char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-'));
        var b64Path = $"{AdapterPaths.VmStageDir}/{safeName}.b64";
        var finalPath = $"{AdapterPaths.VmStageDir}/{safeName}";

        await RunInContainerAsync(new[] { "mkdir", "-p", AdapterPaths.VmStageDir }, stdin: null, ct).ConfigureAwait(false);

        var upload = await RunInContainerAsync(
            new[] { "bash", "-c", $"tee '{b64Path}' > /dev/null" },
            stdin: Convert.ToBase64String(content), ct).ConfigureAwait(false);
        if (!upload.Succeeded)
            throw new AdapterChannelException(AdapterChannelError.InstallFailed,
                $"Staging the verified payload failed (tee exit {upload.ExitCode}): {upload.StdErr}".Trim());

        var decode = await RunInContainerAsync(
            new[] { "bash", "-c", $"base64 -d < '{b64Path}' > '{finalPath}' && rm -f '{b64Path}'" },
            stdin: null, ct).ConfigureAwait(false);
        if (!decode.Succeeded)
            throw new AdapterChannelException(AdapterChannelError.InstallFailed,
                $"Decoding the staged payload failed (exit {decode.ExitCode}): {decode.StdErr}".Trim());

        return finalPath;
    }

    /// <summary>
    /// The uid the agent-base image's non-root <c>agent</c> user has, and the uid the jails run as
    /// (<see cref="ContainerSpecBuilder"/>'s <c>User</c>). Pinned explicitly rather than left to the
    /// image's default so a rebuilt image that changed its <c>USER</c> line cannot silently promote this
    /// container to root — the mounts it writes are the ones every jail later reads.
    /// </summary>
    internal const int AgentUid = 1000;

    /// <summary>
    /// The install container's hardening, as an argv fragment (audit F49).
    ///
    /// <para><b>What went wrong.</b> This container ran with Docker's stock everything: default bridge,
    /// full default capability set, no <c>no-new-privileges</c>, the stock seccomp profile, a writable
    /// root filesystem, no memory/pid/CPU ceiling, and no user pin — while doing the one thing on this
    /// substrate that combines a full-network <c>npm install</c> (arbitrary upstream package code
    /// resolving and unpacking) with a host read-WRITE bind mount of the very tree every agent jail
    /// later mounts. The product hardens the jails that merely <i>run</i> those binaries and left the
    /// container that <i>writes</i> them unconfined, which is the wrong way round.</para>
    ///
    /// <para><b>What this mirrors, and what it deliberately does not.</b> Everything here is copied from
    /// <see cref="ContainerSpecBuilder"/>'s posture so there is one hardening story, not two: capability
    /// set (<c>--cap-drop ALL</c> plus the same minimal add-backs npm needs to unpack a tarball with
    /// ownership), <c>no-new-privileges</c>, the same default-deny <see cref="SeccompProfile"/>,
    /// <see cref="SandboxLimits.Default"/>'s memory/pids/CPU ceilings and rlimits, and the non-root user
    /// pin. The two deliberate differences are stated rather than inherited: the root filesystem is
    /// read-only <i>except</i> the two bind mounts and a small writable tmpfs for npm's cache and
    /// temporary unpack area (npm cannot install onto a wholly read-only rootfs), and the network stays
    /// on the default bridge because reaching the registry IS the job — an install with no egress
    /// installs nothing. Both are the minimum the task requires, and neither is left implicit.</para>
    /// </summary>
    internal static IReadOnlyList<string> HardeningArgs(string seccompProfilePath)
    {
        var limits = SandboxLimits.Default;
        return new List<string>
        {
            // G-15: no privilege escalation, and the default-deny profile — never seccomp=unconfined.
            "--security-opt", "no-new-privileges",
            "--security-opt", $"seccomp={seccompProfilePath}",

            // Drop everything, then add back only what unpacking a tarball as a non-root user needs.
            // Same list as the jails: a capability the agent-base image does not need here either.
            "--cap-drop", "ALL",
            "--cap-add", "CHOWN",
            "--cap-add", "DAC_OVERRIDE",
            "--cap-add", "FOWNER",
            "--cap-add", "FSETID",
            "--cap-add", "SETGID",
            "--cap-add", "SETUID",

            // The image's non-root user, pinned. The bytes this container writes are what every jail
            // executes; writing them as root would also make them root-owned in the host tree.
            "--user", AgentUid.ToString(CultureInfo.InvariantCulture),

            // Read-only rootfs, with the writable surface named explicitly: npm needs a cache and a
            // temp dir, and both are throwaway, so they are tmpfs rather than image layers. nosuid/nodev
            // so nothing unpacked into them can be turned into a privilege primitive.
            "--read-only",
            "--tmpfs", "/tmp:rw,nosuid,nodev,size=1g",
            "--tmpfs", "/home/agent/.npm:rw,nosuid,nodev,size=1g",

            // The same ceilings a jail gets. Without them a hostile postinstall (or an honest but
            // enormous dependency tree) can exhaust the host: this runs on the user's Mac, not in a VM.
            "--memory", limits.MemoryBytes.ToString(CultureInfo.InvariantCulture),
            "--pids-limit", limits.Pids.ToString(CultureInfo.InvariantCulture),
            "--cpus", limits.Cpus.ToString(CultureInfo.InvariantCulture),
            "--ulimit", $"nofile={limits.NoFile}:{limits.NoFile}",
            "--ulimit", $"nproc={limits.NProc}:{limits.NProc}",
        };
    }

    /// <summary>
    /// Materialises the embedded seccomp profile next to the adapters root so the docker CLI can be
    /// pointed at it. The CLI's <c>--security-opt seccomp=</c> takes a FILE, unlike the engine API's
    /// inline JSON that <see cref="ContainerSpecBuilder"/> uses — same profile, different transport.
    /// Rewritten each run (it is small) so a stale or tampered copy can never be what the container gets.
    /// </summary>
    private string EnsureSeccompProfileFile()
    {
        var directory = Path.Combine(_hostAdaptersRoot, ".mainguard");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "install-seccomp.json");
        File.WriteAllText(path, SeccompProfile.Json);
        return path;
    }

    private async Task<WslRunResult> RunInContainerAsync(
        IReadOnlyList<string> command, string? stdin, CancellationToken ct)
    {
        // The mounts must exist host-side first, or the engine creates them with surprising
        // ownership. Created here (not in the ctor) so constructing the host stays I/O-free.
        Directory.CreateDirectory(_hostAdaptersRoot);
        Directory.CreateDirectory(_hostToolchainsRoot);

        var docker = new List<string>
        {
            // Audit F54: the absolute, allow-listed docker path — never a bare "docker" resolved out of
            // whatever PATH the daemon happened to inherit.
            TrustedDockerBinary.Resolve(), "run", "--rm",
        };
        docker.AddRange(HardeningArgs(EnsureSeccompProfileFile()));
        docker.AddRange(new[]
        {
            "-v", $"{_hostAdaptersRoot}:{AdapterPaths.VmRoot}",
            "-v", $"{_hostToolchainsRoot}:{Toolchains.ToolchainPaths.VmRoot}",
        });
        if (stdin is not null)
        {
            docker.Add("-i");
        }
        docker.Add(_imageRef);
        docker.AddRange(command);

        return await HostCommandRunner.RunProcessAsync(docker, stdin, ct).ConfigureAwait(false);
    }
}
