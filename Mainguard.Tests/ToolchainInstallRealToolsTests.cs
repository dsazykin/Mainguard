using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Toolchains;
using Mainguard.Tests.TestTools;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The toolchain install path against <b>real programs on a real filesystem</b> — no scripted host
/// deciding what each command returns.
///
/// <para><b>Why this file exists.</b> Settings → Toolchains shipped an install that fetched its payload
/// by shelling <c>curl</c> into the MainguardEnv VM. The VM has no <c>curl</c> and no <c>wget</c>:
/// <c>build/mainguardos/packages.pinned.txt</c> pins neither, and probing a live distro finds neither.
/// Every install a user attempted died with <c>curl: command not found</c> at exit 127, and it stayed
/// invisible for the life of the feature because every test substituted a fake VM that answered
/// <c>curl</c> with exit 0. The suite was measuring a VM that does not exist — this repo's recurring
/// defect: the thing tested sitting one layer away from the thing that matters.</para>
///
/// <para>A scripted host can be taught to refuse <c>curl</c> (and now is — see
/// <see cref="MainguardEnvFacts"/>), but that is still a fake agreeing with a comment. So these run the
/// shipped channel's own commands as real processes: real <c>mkdir</c>/<c>rm</c>/<c>mv</c>, real
/// <c>tar</c> unpacking a real gzipped tarball, real <c>tee</c> and real <c>base64 -d</c> doing the
/// staging transfer exactly the way <see cref="WslAdapterInstallHost"/> does it, and a real executable
/// answering the probe. The only substitution left is the network — an
/// <see cref="IToolchainPayloadSource"/> hands over bytes instead of fetching them — which is the one
/// thing that genuinely cannot be a fact about this machine.</para>
///
/// <para>Linux-only because the argv is POSIX (the production host runs it inside a Linux VM). The
/// Docker/Linux CI leg is the authoritative run.</para>
/// </summary>
public class ToolchainInstallRealToolsTests
{
    private const string PayloadUrl = "https://example.invalid/toolx-1.2.3.tar.gz";

    /// <summary>
    /// The premise, measured rather than asserted from memory — the same treatment
    /// <c>PythonToolchainDockerTests</c> gives the base image's pip-less interpreter.
    ///
    /// <para><b>If this ever fails</b>, someone has added a downloader to the MainguardOS image. That is
    /// allowed, but it changes the payload hash (P2-21 invariant 2) and it makes the reasoning in
    /// <see cref="ToolchainChannel"/> stale — so it is a decision to re-take deliberately, not a fact to
    /// discover from a user whose install exits 127.</para>
    /// </summary>
    [Fact]
    public void TheMainguardOsImage_PinsNoDownloader_WhichIsWhyPayloadsAreFetchedHostSide()
    {
        var pinned = File.ReadAllLines(RepoFile("build/mainguardos/packages.pinned.txt"))
            .Select(l => l.Split('#')[0].Trim())
            .Where(l => l.Length > 0)
            .Select(l => l.Split('=')[0].Trim())
            .ToArray();

        Assert.NotEmpty(pinned);

        foreach (var downloader in MainguardEnvFacts.AbsentBinaries)
        {
            Assert.DoesNotContain(downloader, pinned, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The whole install, executed: fetch (substituted) → hash on the host → transfer into the
    /// environment through <c>base64</c> → unpack with <c>tar</c> → swap into place → RUN it → marker.
    /// Every step after the fetch is a real program.
    /// </summary>
    [LinuxOnlyFact("the channel's argv is POSIX (tar/mv/rm/base64), as it is inside the VM")]
    public async Task AnInstall_CompletesAgainstRealTools_WithNoDownloaderInTheEnvironment()
    {
        using var sandbox = new TempDir();
        var payload = BuildToolchainTarball(sandbox.Path, reportsVersion: "1.2.3");
        var root = Path.Combine(sandbox.Path, "toolchains");

        var host = new LocalVmHost(Path.Combine(sandbox.Path, "stage"));
        var channel = new ToolchainChannel(
            host, ManifestPinning(payload), vmRoot: root, payloads: new InMemoryPayloadSource(payload));

        var status = await channel.InstallAsync("tool-x");

        Assert.True(status.IsInstalled, status.Detail);
        Assert.Contains("1.2.3", status.Detail, StringComparison.Ordinal);

        // The toolchain is really on disk, at the path a jail would bind-mount, and really runs.
        var installed = Path.Combine(root, "tool-x", "bin", "toolx");
        Assert.True(File.Exists(installed), $"nothing was unpacked to {installed}");

        // The marker is written last and only a version-matched probe earns it.
        Assert.True(File.Exists(ToolchainPaths.RegistryMarkerPath("tool-x", root)));

        // Not one downloader was needed — this is the assertion the old suite could not make, because
        // its VM was a dictionary of canned answers rather than a machine.
        foreach (var absent in MainguardEnvFacts.AbsentBinaries)
        {
            Assert.DoesNotContain(host.Commands, c => c.Any(a =>
                a.Split('/')[^1].Equals(absent, StringComparison.Ordinal)));
        }

        // The staged payload is cleaned up on success — a ~100 MiB tarball per install, left behind,
        // fills the VM's disk one install at a time.
        Assert.Empty(Directory.GetFiles(Path.Combine(sandbox.Path, "stage")));
    }

    /// <summary>
    /// A payload that does not match the pin must never reach the environment. Against real tools this
    /// is a filesystem fact rather than an assertion about a mock: the staging directory is empty.
    /// </summary>
    [LinuxOnlyFact("the channel's argv is POSIX (tar/mv/rm/base64), as it is inside the VM")]
    public async Task APayloadThatFailsThePin_NeverReachesTheFilesystem()
    {
        using var sandbox = new TempDir();
        var payload = BuildToolchainTarball(sandbox.Path, reportsVersion: "1.2.3");
        var root = Path.Combine(sandbox.Path, "toolchains");
        var stage = Path.Combine(sandbox.Path, "stage");

        // Same manifest pin, different bytes — exactly the substitution the checksum exists to catch.
        var tampered = payload.Concat(Encoding.UTF8.GetBytes("tampered")).ToArray();
        var host = new LocalVmHost(stage);
        var channel = new ToolchainChannel(
            host, ManifestPinning(payload), vmRoot: root, payloads: new InMemoryPayloadSource(tampered));

        var ex = await Assert.ThrowsAsync<ToolchainChannelException>(() => channel.InstallAsync("tool-x"));

        Assert.Equal(ToolchainChannelError.HashMismatch, ex.Error);
        Assert.False(Directory.Exists(stage) && Directory.GetFiles(stage).Length > 0,
            "unverified bytes were written into the environment before the pin was checked");
        Assert.False(Directory.Exists(Path.Combine(root, "tool-x")), "an unverified payload was unpacked");
        Assert.False(File.Exists(ToolchainPaths.RegistryMarkerPath("tool-x", root)));
    }

    // ---- fixtures ---------------------------------------------------------------------------------

    /// <summary>A manifest whose pin is the real SHA-256 of <paramref name="payload"/>.</summary>
    private static ToolchainManifest ManifestPinning(byte[] payload)
    {
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        return ToolchainManifest.Parse($$"""
            {
              "toolchains": [
                {
                  "id": "tool-x",
                  "displayName": "Tool X",
                  "summary": "A curated toolchain.",
                  "version": "1.2.3",
                  "payloadUrl": "{{PayloadUrl}}",
                  "sha256": "{{sha}}",
                  "stripComponents": 1,
                  "pathEntries": ["{toolchain}/bin"],
                  "probe": {
                    "command": ["{toolchain}/bin/toolx", "--version"],
                    "expectedVersionSubstring": "1.2.3"
                  }
                }
              ]
            }
            """);
    }

    /// <summary>
    /// A genuine gzipped tarball with one leading directory (so <c>stripComponents: 1</c> is exercised
    /// rather than assumed) containing an executable that prints its version — the smallest thing that
    /// is really a toolchain: it unpacks, it is executable after unpacking, and it answers a probe.
    /// </summary>
    private static byte[] BuildToolchainTarball(string scratch, string reportsVersion)
    {
        var stagingRoot = Path.Combine(scratch, "src");
        var bin = Path.Combine(stagingRoot, "toolx-1.2.3", "bin");
        Directory.CreateDirectory(bin);

        var exe = Path.Combine(bin, "toolx");
        File.WriteAllText(exe, $"#!/bin/sh\necho '{reportsVersion} ok'\n");

        // The executable bit is the point — a payload that unpacks to a non-executable file is exactly
        // the "a file arrived, so it must be installed" failure the probe exists to catch. Guarded only
        // because the callers are [LinuxOnlyFact] and the analyser cannot see that.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                exe,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var tarball = Path.Combine(scratch, "payload.tar.gz");
        Run("tar", new[] { "-czf", tarball, "-C", stagingRoot, "toolx-1.2.3" });

        var bytes = File.ReadAllBytes(tarball);
        Directory.Delete(stagingRoot, recursive: true);
        File.Delete(tarball);
        return bytes;
    }

    private static void Run(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        Assert.True(process.ExitCode == 0, $"{exe} {string.Join(' ', args)} exited {process.ExitCode}: {stderr}");
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mainguard.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private sealed class InMemoryPayloadSource : IToolchainPayloadSource
    {
        private readonly byte[] _bytes;

        public InMemoryPayloadSource(byte[] bytes) => _bytes = bytes;

        public Task<byte[]> FetchAsync(Uri url, CancellationToken ct)
        {
            PinnedPayloadTransport.RequireHttps(url);
            return Task.FromResult(_bytes);
        }
    }

    /// <summary>
    /// The channel's commands, run as real processes on this machine, and staging done <b>the way
    /// <see cref="WslAdapterInstallHost"/> does it</b>: base64 over a pipe into <c>tee</c>, decoded by
    /// the environment's own <c>base64 -d</c>. That detail is not decoration — it is the mechanism the
    /// whole fix rests on, and running it through the real <c>base64</c> is what turns "the VM has
    /// base64" from a claim into a measurement.
    /// </summary>
    private sealed class LocalVmHost : IAdapterInstallHost
    {
        private readonly string _stageDir;

        public LocalVmHost(string stageDir) => _stageDir = stageDir;

        public List<IReadOnlyList<string>> Commands { get; } = new();

        public async Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
        {
            Commands.Add(command);
            return await ExecAsync(command, stdin: null, ct);
        }

        public async Task WriteFileAsync(string path, string content, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, ct);
        }

        public async Task<string> StagePayloadAsync(string fileName, byte[] content, CancellationToken ct)
        {
            var safeName = string.Concat(fileName.Select(c =>
                char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-'));
            var b64Path = Path.Combine(_stageDir, safeName + ".b64");
            var finalPath = Path.Combine(_stageDir, safeName);

            Directory.CreateDirectory(_stageDir);

            var upload = await ExecAsync(
                new[] { "tee", b64Path }, Convert.ToBase64String(content), ct);
            Assert.True(upload.Succeeded, $"tee exited {upload.ExitCode}: {upload.Stderr}");

            var decode = await ExecAsync(
                new[] { "bash", "-c", $"base64 -d '{b64Path}' > '{finalPath}' && rm -f '{b64Path}'" },
                stdin: null, ct);
            Assert.True(decode.Succeeded, $"base64 -d exited {decode.ExitCode}: {decode.Stderr}");

            return finalPath;
        }

        private static async Task<AdapterCommandResult> ExecAsync(
            IReadOnlyList<string> command, string? stdin, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(command[0])
            {
                RedirectStandardInput = stdin is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            for (var i = 1; i < command.Count; i++)
            {
                psi.ArgumentList.Add(command[i]);
            }

            Process process;
            try
            {
                process = Process.Start(psi)!;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // The production host shells into the VM, so a missing binary comes back as a non-zero
                // exit rather than a throw. 127 is the shell's "command not found" — matching the real
                // host's SHAPE is the point.
                return new AdapterCommandResult(127, string.Empty, ex.Message);
            }

            using (process)
            {
                if (stdin is not null)
                {
                    await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
                    process.StandardInput.Close();
                }

                var stdout = await process.StandardOutput.ReadToEndAsync(ct);
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                return new AdapterCommandResult(process.ExitCode, stdout, stderr);
            }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            // Under the test binary's own tree, never /tmp: /tmp is a tmpfs on the machines this repo is
            // developed on, and filling RAM with a payload is a documented way to kill the WSL VM.
            Path = System.IO.Path.Combine(
                AppContext.BaseDirectory, "toolchain-real-tools", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
