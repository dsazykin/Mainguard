using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Protos.V1;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The per-jail ceiling over the real composition root (2026-09-04): Set answers with the clamped value,
/// Get agrees, and — the line that matters — the NEXT spawn is created with it. A setting the launcher
/// never read would be the MG-12 shape again.
/// </summary>
public sealed class JailLimitsRpcTests : IDisposable
{
    private const string Repo = "fake-repo-hash-limits";
    private const long GiB = 1024L * 1024 * 1024;

    private readonly DaemonFixture _daemon = new();
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;
    private readonly AgentSessionRepoScopingTests.RecordingEngine _engine = new();
    private readonly string _root;

    public JailLimitsRpcTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mg-limits-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "repos", Repo));
        var environment = new AgentSessionRepoScopingTests.FakeAgentEnvironment(_root, _engine);
        _host = _daemon.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<IAgentEnvironment>(environment)));
        _ = _host.Services;
    }

    [Fact]
    public async Task Set_IsClampedAndPersisted_GetAgrees_AndTheNextSpawnIsCreatedWithIt()
    {
        var client = new AgentService.AgentServiceClient(_daemon.CreateChannel());
        var headers = _daemon.AuthHeaders();

        var before = await client.GetJailLimitsAsync(new GetJailLimitsRequest(), headers);
        Assert.True(before.IsDefault);
        Assert.Equal(SandboxLimits.Default.MemoryBytes, before.MemoryBytes);

        var set = await client.SetJailLimitsAsync(
            new SetJailLimitsRequest { MemoryBytes = 1 * GiB, Cpus = 0.1 }, headers);
        Assert.Equal(1 * GiB, set.MemoryBytes);
        Assert.Equal(JailLimitsSettings.MinCpus, set.Cpus); // clamped, and answered as persisted
        Assert.False(set.IsDefault);

        var after = await client.GetJailLimitsAsync(new GetJailLimitsRequest(), headers);
        Assert.Equal(set.MemoryBytes, after.MemoryBytes);
        Assert.Equal(set.Cpus, after.Cpus);

        var spawns = _host.Services.GetRequiredService<AgentSpawnService>();
        await spawns.SpawnAsync(Repo, "claude-code", null, AgentRoles.Managed, CancellationToken.None);
        var request = Assert.Single(_engine.Requests);
        Assert.Equal(1 * GiB, request.Limits.MemoryBytes);
        Assert.Equal(JailLimitsSettings.MinCpus, request.Limits.Cpus);
        Assert.Equal(SandboxLimits.Default.Pids, request.Limits.Pids);

        // Persisted beside the session token, where DataRootIsolationTests keeps every daemon store.
        Assert.True(File.Exists(Path.Combine(
            Path.GetDirectoryName(_daemon.Services.GetRequiredService<Mainguard.Server.Auth.SessionTokenFile>().Path)!,
            "mainguard-jail-limits.json")));
    }

    [Fact]
    public async Task ANonPositiveValue_IsRefused_NotClampedIntoAMinimum()
    {
        var client = new AgentService.AgentServiceClient(_daemon.CreateChannel());
        var ex = await Assert.ThrowsAsync<RpcException>(() => client.SetJailLimitsAsync(
            new SetJailLimitsRequest { MemoryBytes = 0, Cpus = 2 }, _daemon.AuthHeaders()).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    public void Dispose()
    {
        _host.Dispose();
        _daemon.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
