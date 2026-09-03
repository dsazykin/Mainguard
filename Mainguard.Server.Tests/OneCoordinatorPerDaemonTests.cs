using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// Contract §2.2 (owner decision, 2026-09-03): one live coordinator per daemon. The plan gate streams every
/// coordinator's cards to the one operator surface with no repository on them, so a second live coordinator
/// meant a plan a human could approve from the wrong repository's window. Proved at the spawn service the
/// gRPC and shim paths both go through — with the DEFAULT limits, which is what ships.
/// </summary>
public sealed class OneCoordinatorPerDaemonTests : IDisposable
{
    private const string RepoA = "fake-repo-hash-one-a";
    private const string RepoB = "fake-repo-hash-one-b";

    private readonly DaemonFixture _daemon = new();
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;
    private readonly string _root;

    public OneCoordinatorPerDaemonTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mg-onecoord-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "repos", RepoA));
        Directory.CreateDirectory(Path.Combine(_root, "repos", RepoB));
        var environment = new AgentSessionRepoScopingTests.FakeAgentEnvironment(
            _root, new AgentSessionRepoScopingTests.RecordingEngine());
        _host = _daemon.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<IAgentEnvironment>(environment)));
        _ = _host.Services;
    }

    private AgentSpawnService Spawns => _host.Services.GetRequiredService<AgentSpawnService>();

    [Fact]
    public async Task ASecondCoordinator_IsRefused_NamingTheOneThatIsRunning_UntilItStops()
    {
        var first = await Spawns.SpawnAsync(RepoA, "claude-code", null, AgentRoles.Coordinator, CancellationToken.None);

        // Same repo or another — the rule is per daemon, not per repository.
        var refusal = await Assert.ThrowsAsync<AgentSpawnRefusedException>(() =>
            Spawns.SpawnAsync(RepoB, "claude-code", null, AgentRoles.Coordinator, CancellationToken.None));
        Assert.Contains(first, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("one coordinator per daemon", refusal.Message, StringComparison.Ordinal);

        // A worker is not a coordinator: the cap is on the orchestrator, not on the fan-out.
        var worker = await Spawns.SpawnAsync(RepoA, "claude-code", null, AgentRoles.Managed, CancellationToken.None);
        Assert.NotEqual(first, worker);

        await Spawns.StopAsync(first, CancellationToken.None);
        var second = await Spawns.SpawnAsync(RepoB, "claude-code", null, AgentRoles.Coordinator, CancellationToken.None);
        Assert.NotEqual(first, second);

        await Spawns.StopAsync(worker, CancellationToken.None);
        await Spawns.StopAsync(second, CancellationToken.None);
    }

    public void Dispose()
    {
        _host.Dispose();
        _daemon.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
