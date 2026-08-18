using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

// The engine-agnostic Docker endpoint resolution (macos-host substrate): DOCKER_HOST wins,
// then the docker CLI's current context read straight from disk, then the well-known engine
// sockets in order, then the library default. Pure logic through ResolveCore — no live engine,
// no environment mutation.
public class DockerEndpointResolverTests : IDisposable
{
    private readonly string _configDir = Path.Combine(
        Path.GetTempPath(), "mainguard-dockercfg-" + Guid.NewGuid().ToString("N"));
    private readonly string _homeDir = Path.Combine(
        Path.GetTempPath(), "mainguard-dockerhome-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var dir in new[] { _configDir, _homeDir })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    private static string? NoEnv(string _) => null;

    [Fact]
    public void DockerHost_ShouldWin_OverEverythingElse()
    {
        var (uri, source) = DockerEndpointResolver.ResolveCore(
            name => name == "DOCKER_HOST" ? "unix:///custom/docker.sock" : null,
            _configDir, _ => true, _homeDir);

        Assert.Equal(new Uri("unix:///custom/docker.sock"), uri);
        Assert.Equal("DOCKER_HOST", source);
    }

    [Fact]
    public void DockerHost_TcpForm_ShouldResolve()
    {
        var (uri, _) = DockerEndpointResolver.ResolveCore(
            name => name == "DOCKER_HOST" ? "tcp://127.0.0.1:2375" : null,
            _configDir, _ => false, _homeDir);

        Assert.Equal(new Uri("tcp://127.0.0.1:2375"), uri);
    }

    [Fact]
    public void CurrentContext_ShouldBeReadFromCliMetadata()
    {
        WriteContext("orbstack", "unix:///Users/x/.orbstack/run/docker.sock");

        var (uri, source) = DockerEndpointResolver.ResolveCore(
            NoEnv, _configDir, File.Exists, _homeDir);

        Assert.Equal(new Uri("unix:///Users/x/.orbstack/run/docker.sock"), uri);
        Assert.Equal("docker context 'orbstack'", source);
    }

    [Fact]
    public void DefaultContext_ShouldFallThrough_ToSocketProbes()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(Path.Combine(_configDir, "config.json"), "{\"currentContext\":\"default\"}");
        var orbstack = Path.Combine(_homeDir, ".orbstack", "run", "docker.sock");

        var (uri, source) = DockerEndpointResolver.ResolveCore(
            NoEnv, _configDir, path => path == orbstack, _homeDir);

        Assert.Equal(new Uri("unix://" + orbstack), uri);
        Assert.Equal("socket probe", source);
    }

    [Fact]
    public void SocketProbe_ShouldPreferTheStandardSocket()
    {
        var (uri, _) = DockerEndpointResolver.ResolveCore(
            NoEnv, _configDir, _ => true, _homeDir);

        Assert.Equal(new Uri("unix:///var/run/docker.sock"), uri);
    }

    [Fact]
    public void NothingFound_ShouldFallBack_ToLibraryDefault()
    {
        var (uri, source) = DockerEndpointResolver.ResolveCore(
            NoEnv, _configDir, _ => false, _homeDir);

        Assert.Null(uri);
        Assert.Equal("library default", source);
    }

    [Fact]
    public void MalformedCliConfig_ShouldFallThrough_NotThrow()
    {
        Directory.CreateDirectory(_configDir);
        var configPath = Path.Combine(_configDir, "config.json");
        File.WriteAllText(configPath, "{not json");

        // Only the malformed config "exists" — the machine's real engine sockets must not
        // leak into the assertion (this box genuinely has /var/run/docker.sock).
        var (uri, source) = DockerEndpointResolver.ResolveCore(
            NoEnv, _configDir, path => path == configPath, _homeDir);

        Assert.Null(uri);
        Assert.Equal("library default", source);
    }

    private void WriteContext(string name, string host)
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(Path.Combine(_configDir, "config.json"),
            $"{{\"currentContext\":\"{name}\"}}");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();
        var metaDir = Path.Combine(_configDir, "contexts", "meta", digest);
        Directory.CreateDirectory(metaDir);
        File.WriteAllText(Path.Combine(metaDir, "meta.json"),
            $"{{\"Name\":\"{name}\",\"Endpoints\":{{\"docker\":{{\"Host\":\"{host}\"}}}}}}");
    }
}
