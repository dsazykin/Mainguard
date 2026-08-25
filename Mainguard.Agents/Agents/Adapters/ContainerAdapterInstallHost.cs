using System;
using System.Collections.Generic;
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
/// <para>The install container runs on Docker's default bridge (registry egress is an
/// install-time, Mainguard-mediated act — the WSL2 substrate's installs likewise ran with the
/// VM's own egress), as the image's non-root <c>agent</c> user, and is removed on exit. The
/// docker CLI itself is the one host dependency; all three engines ship it.</para>
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

    private async Task<WslRunResult> RunInContainerAsync(
        IReadOnlyList<string> command, string? stdin, CancellationToken ct)
    {
        // The mounts must exist host-side first, or the engine creates them with surprising
        // ownership. Created here (not in the ctor) so constructing the host stays I/O-free.
        Directory.CreateDirectory(_hostAdaptersRoot);
        Directory.CreateDirectory(_hostToolchainsRoot);

        var docker = new List<string>
        {
            "docker", "run", "--rm",
            "-v", $"{_hostAdaptersRoot}:{AdapterPaths.VmRoot}",
            "-v", $"{_hostToolchainsRoot}:{Toolchains.ToolchainPaths.VmRoot}",
        };
        if (stdin is not null)
        {
            docker.Add("-i");
        }
        docker.Add(_imageRef);
        docker.AddRange(command);

        return await HostCommandRunner.RunProcessAsync(docker, stdin, ct).ConfigureAwait(false);
    }
}
