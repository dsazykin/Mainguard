using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.Linq;
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

    public async Task<IReadOnlyList<string>?> RootFsLayersAsync(string imageRef, CancellationToken ct = default)
    {
        try
        {
            var inspect = await _docker.Images.InspectImageAsync(imageRef, ct).ConfigureAwait(false);
            var layers = inspect.RootFS?.Layers;
            return layers is null ? null : layers.ToArray();
        }
        catch (DockerImageNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The facts that would have made the "pull access denied" CI failure a one-read diagnosis: which
    /// engine and image store are speaking, whether the pinned base digest is present <b>as a local
    /// image</b>, and what identity the engine holds for the base by NAME — specifically its
    /// <c>RepoDigests</c>, whose emptiness on a classic image store is the entire reason a
    /// <c>name@sha256:…</c> FROM turns into a registry pull.
    /// </summary>
    public async Task<string> DescribeBuildContextAsync(
        string baseDigest, string baseImageName, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---- engine ----");

        try
        {
            var info = await _docker.System.GetSystemInfoAsync(ct).ConfigureAwait(false);
            var version = await _docker.System.GetVersionAsync(ct).ConfigureAwait(false);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"server={version.Version} api={version.APIVersion} os={version.Os}/{version.Arch} driver={info.Driver}");
        }
        catch (Exception ex)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"server=<unavailable: {ex.GetType().Name}>");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"FROM {baseDigest}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  base-by-digest present={await PresentAsync(baseDigest, ct).ConfigureAwait(false)}");
        sb.Append(await DescribeImageAsync(baseImageName + ":latest", ct).ConfigureAwait(false));
        return sb.ToString();
    }

    private async Task<string> PresentAsync(string imageRef, CancellationToken ct)
    {
        try
        {
            await _docker.Images.InspectImageAsync(imageRef, ct).ConfigureAwait(false);
            return "true";
        }
        catch (DockerImageNotFoundException)
        {
            return "false";
        }
        catch (Exception ex)
        {
            return $"<unknown: {ex.GetType().Name}>";
        }
    }

    private async Task<string> DescribeImageAsync(string imageRef, CancellationToken ct)
    {
        try
        {
            var inspect = await _docker.Images.InspectImageAsync(imageRef, ct).ConfigureAwait(false);
            var repoDigests = inspect.RepoDigests is { Count: > 0 }
                ? string.Join(",", inspect.RepoDigests)
                : "[] (a locally built image has no registry manifest digest — a `name@sha256:` FROM "
                  + "cannot resolve against this and the engine will try to PULL)";
            return $"  base-by-name '{imageRef}' id={inspect.ID}\n"
                   + $"    repoTags={string.Join(",", inspect.RepoTags ?? new List<string>())}\n"
                   + $"    repoDigests={repoDigests}\n";
        }
        catch (DockerImageNotFoundException)
        {
            return $"  base-by-name '{imageRef}' ABSENT from this engine's image store\n";
        }
        catch (Exception ex)
        {
            return $"  base-by-name '{imageRef}' <unknown: {ex.GetType().Name}: {ex.Message}>\n";
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

            // ErrorDetail carries the engine's own code alongside the text; on a resolve failure it is
            // the difference between "denied" and knowing WHICH stage denied it.
            if (message.Error is { } detail && !string.IsNullOrEmpty(detail.Message))
            {
                error ??= detail.Message;
                Append(output, $"[errorDetail{(detail.Code != 0 ? " code=" + detail.Code.ToString(CultureInfo.InvariantCulture) : string.Empty)}] {detail.Message}\n");
            }

            // The CI failure retained ONE line, because a build that dies resolving its FROM emits its
            // step line as `stream` and everything else — the pull attempt, the auth refusal — as
            // `status`/`progress`/`id`. Keeping only `stream` threw away the whole story and left a
            // "build log (tail)" that contained the one line nobody needed.
            if (!string.IsNullOrEmpty(message.Stream))
            {
                Append(output, message.Stream!);
            }

            if (!string.IsNullOrEmpty(message.Status))
            {
                var id = string.IsNullOrEmpty(message.ID) ? string.Empty : message.ID + ": ";
                Append(output, $"[status] {id}{message.Status}\n");
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
        // 4 KiB was tuned for a compile error at the end of a long build. A resolve failure at step 1
        // produces far less than that, and the earlier bug was never truncation — it was that only
        // `stream` messages were retained at all. Keep it generous: the build container is gone by the
        // time anyone reads this, so this text IS the diagnosis.
        const int max = 16000;
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
