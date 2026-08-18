using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Docker.DotNet;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// Resolves the Docker endpoint the daemon should talk to, so the sandbox layer works against
/// whichever engine the machine actually runs — Docker Desktop, OrbStack, Colima — instead of
/// only the one that happens to symlink <c>/var/run/docker.sock</c>. Resolution order mirrors
/// the docker CLI's own: <c>DOCKER_HOST</c> wins; then the CLI's current context (read straight
/// from <c>~/.docker</c>, no process spawned); then the well-known per-engine socket paths; then
/// the library default. Windows is deliberately left on the library default (named pipe) — the
/// WSL2 substrate's behavior there is proven and must not change under a stray DOCKER_HOST.
/// </summary>
public static class DockerEndpointResolver
{
    /// <summary>A client for the resolved endpoint (the one-line form the composition sites use).</summary>
    public static IDockerClient CreateClient() => CreateConfiguration().CreateClient();

    public static DockerClientConfiguration CreateConfiguration()
    {
        var (uri, _) = Resolve();
        return uri is null ? new DockerClientConfiguration() : new DockerClientConfiguration(uri);
    }

    /// <summary>
    /// The resolved endpoint and a human-readable source ("DOCKER_HOST", "docker context 'x'",
    /// "socket probe", "library default") for bootstrap diagnostics. A null uri means "use the
    /// library default".
    /// </summary>
    public static (Uri? Uri, string Source) Resolve() =>
        OperatingSystem.IsWindows()
            ? (null, "library default")
            // MainguardPaths.HomeDirectory, not GetFolderPath: the repo-wide guard bans direct
            // GetFolderPath calls (it returns "" for a not-yet-materialized Unix home).
            : ResolveCore(
                Environment.GetEnvironmentVariable,
                Path.Combine(Mainguard.Git.MainguardPaths.HomeDirectory(), ".docker"),
                File.Exists,
                Mainguard.Git.MainguardPaths.HomeDirectory());

    internal static (Uri? Uri, string Source) ResolveCore(
        Func<string, string?> getEnv, string dockerConfigDir, Func<string, bool> fileExists, string homeDir)
    {
        var dockerHost = getEnv("DOCKER_HOST");
        if (!string.IsNullOrWhiteSpace(dockerHost)
            && Uri.TryCreate(dockerHost, UriKind.Absolute, out var fromEnv))
        {
            return (fromEnv, "DOCKER_HOST");
        }

        var fromContext = TryReadCurrentContextEndpoint(dockerConfigDir, fileExists);
        if (fromContext is { } ctx)
        {
            return (ctx.Uri, $"docker context '{ctx.Name}'");
        }

        // Well-known engine sockets, most standard first. Existence is the probe — a stale
        // socket file with no daemon behind it fails later with the endpoint named, which is
        // a better error than silently picking a different engine than the CLI would.
        string[] candidates =
        {
            "/var/run/docker.sock",
            Path.Combine(dockerConfigDir, "run", "docker.sock"),   // Docker Desktop (user-scoped)
            Path.Combine(homeDir, ".orbstack", "run", "docker.sock"),
            Path.Combine(homeDir, ".colima", "default", "docker.sock"),
        };
        foreach (var candidate in candidates)
        {
            if (fileExists(candidate))
            {
                return (new Uri("unix://" + candidate), "socket probe");
            }
        }

        return (null, "library default");
    }

    private static (Uri Uri, string Name)? TryReadCurrentContextEndpoint(
        string dockerConfigDir, Func<string, bool> fileExists)
    {
        try
        {
            var configPath = Path.Combine(dockerConfigDir, "config.json");
            if (!fileExists(configPath)) return null;

            using var config = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!config.RootElement.TryGetProperty("currentContext", out var ctx)) return null;
            var name = ctx.GetString();
            if (string.IsNullOrWhiteSpace(name) || name == "default") return null;

            // The CLI stores context metadata under meta/<sha256(name)>/meta.json.
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))
                .ToLowerInvariant();
            var metaPath = Path.Combine(dockerConfigDir, "contexts", "meta", digest, "meta.json");
            if (!fileExists(metaPath)) return null;

            using var meta = JsonDocument.Parse(File.ReadAllText(metaPath));
            if (!meta.RootElement.TryGetProperty("Endpoints", out var endpoints)
                || !endpoints.TryGetProperty("docker", out var dockerEndpoint)
                || !dockerEndpoint.TryGetProperty("Host", out var host))
            {
                return null;
            }

            var hostValue = host.GetString();
            if (string.IsNullOrWhiteSpace(hostValue)
                || !Uri.TryCreate(hostValue, UriKind.Absolute, out var uri))
            {
                return null;
            }
            return (uri, name!);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable CLI config must never stop the daemon — fall through to the probes.
            return null;
        }
    }
}
