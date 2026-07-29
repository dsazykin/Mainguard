using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// The Docker implementation of <see cref="IToolchainImageBuilder"/>: builds a per-repo toolchain layer
/// through the engine API, from an in-memory build context that contains exactly one file — the
/// generated Dockerfile.
///
/// <para>A one-file context is not a shortcut, it is a control. A build context is an implicit input to
/// every <c>COPY</c>/<c>ADD</c> in a Dockerfile, so handing the daemon a directory would mean the layer
/// could, in principle, absorb bytes from wherever that directory pointed. The generated Dockerfile
/// contains no <c>COPY</c> at all and the context contains nothing to copy, so the layer's inputs are
/// the pinned base image plus the catalogued recipes' checksum-verified downloads, and nothing else.</para>
///
/// <para>The build's own network is the VM's, which is where the base image is built too — no jail
/// network, no agent allowlist, no change to either.</para>
/// </summary>
public sealed class DockerToolchainImageBuilder : IToolchainImageBuilder
{
    private readonly IDockerClient _docker;

    public DockerToolchainImageBuilder(IDockerClient docker) =>
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));

    public async Task<IReadOnlyDictionary<string, string>?> InspectLabelsAsync(
        string imageRef, CancellationToken ct = default)
    {
        try
        {
            var inspect = await _docker.Images.InspectImageAsync(imageRef, ct).ConfigureAwait(false);
            var labels = inspect.Config?.Labels;
            return labels is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(labels, StringComparer.Ordinal);
        }
        catch (DockerImageNotFoundException)
        {
            return null;
        }
    }

    public async Task<string?> ResolveDigestAsync(string imageRef, CancellationToken ct = default)
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

    public async Task BuildAsync(
        string imageRef, string dockerfile, IReadOnlyDictionary<string, string> labels, CancellationToken ct = default)
    {
        await using var context = BuildContext(dockerfile);

        // The daemon streams build output as newline-delimited JSON. The `error` field is the ONLY
        // place a failed RUN surfaces: BuildImageFromDockerfileAsync completes normally on a failed
        // build, so a caller that does not read this stream gets "success" and then a missing image.
        var output = new StringBuilder();
        string? error = null;

        var progress = new Progress<JSONMessage>(message =>
        {
            if (!string.IsNullOrEmpty(message.ErrorMessage))
            {
                error ??= message.ErrorMessage;
            }

            if (!string.IsNullOrEmpty(message.Stream))
            {
                Append(output, message.Stream!);
            }
        });

        await _docker.Images.BuildImageFromDockerfileAsync(
            new ImageBuildParameters
            {
                Dockerfile = "Dockerfile",
                Tags = new List<string> { imageRef },
                Labels = new Dictionary<string, string>(labels, StringComparer.Ordinal),
                // The FROM is a LOCAL, digest-pinned image; nothing here consults a registry (Docker
                // only pulls a parent when explicitly asked to, and this build never asks).
                Remove = true,
                ForceRemove = true,
            },
            context,
            authConfigs: null,
            headers: null,
            progress,
            ct).ConfigureAwait(false);

        if (error is not null)
        {
            throw new DockerToolchainBuildException(imageRef, error, Tail(output.ToString()));
        }
    }

    /// <summary>A tar stream holding just the generated Dockerfile — the whole build context.</summary>
    private static Stream BuildContext(string dockerfile)
    {
        var bytes = Encoding.UTF8.GetBytes(dockerfile);
        var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "Dockerfile")
            {
                DataStream = new MemoryStream(bytes),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            };
            writer.WriteEntry(entry);
        }

        tar.Position = 0;
        return tar;
    }

    private static void Append(StringBuilder sb, string chunk)
    {
        // Bound the retained output: a nix realisation prints tens of thousands of lines and the only
        // part anyone reads on a failure is the end.
        const int cap = 64 * 1024;
        sb.Append(chunk);
        if (sb.Length > cap * 2)
        {
            sb.Remove(0, sb.Length - cap);
        }
    }

    private static string Tail(string text)
    {
        const int max = 4000;
        return text.Length <= max ? text : text[^max..];
    }
}

/// <summary>A <c>docker build</c> of a toolchain layer failed; carries the engine's error and the tail
/// of the build log, which is the only diagnosis available after the build container is gone.</summary>
public sealed class DockerToolchainBuildException : Exception
{
    public DockerToolchainBuildException(string imageRef, string error, string logTail)
        : base($"docker build of '{imageRef}' failed: {error}\n---- build log (tail) ----\n{logTail}")
    {
        ImageRef = imageRef;
        Error = error;
        LogTail = logTail;
    }

    public string ImageRef { get; }

    public string Error { get; }

    public string LogTail { get; }
}
