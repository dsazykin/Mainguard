using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Git.Audit;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The jail reaper over the real composition root (2026-09-04): a jail with no CLI bound to it survives
/// the idle allowance and not a minute more, and the reap is the ordinary Stop — the session is gone and
/// the engine was asked to remove the container. Driven by the caller's clock, so no allowance is waited out.
/// </summary>
public sealed class JailReaperTests : IDisposable
{
    private const string Repo = "fake-repo-hash-reaper";

    private readonly DaemonFixture _daemon = new();
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;
    private readonly AgentSessionRepoScopingTests.FakeAgentEnvironment _environment;
    private readonly string _root;

    public JailReaperTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mg-reaper-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "repos", Repo));
        _environment = new AgentSessionRepoScopingTests.FakeAgentEnvironment(
            _root, new AgentSessionRepoScopingTests.RecordingEngine());
        _host = _daemon.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<IAgentEnvironment>(_environment)));
        _ = _host.Services;
    }

    private JailReaperHostedService Reaper => Assert.Single(
        _host.Services.GetServices<IHostedService>().OfType<JailReaperHostedService>());

    [Fact]
    public async Task ABoundJailIsKept_AndOnceItsCliIsGone_ItIsStoppedAfterTheIdleAllowance_NotBefore()
    {
        var spawns = _host.Services.GetRequiredService<AgentSpawnService>();
        var store = _host.Services.GetRequiredService<AgentSessionStore>();
        var agentId = await spawns.SpawnAsync(Repo, "claude-code", null, AgentRoles.Managed, CancellationToken.None);
        var session = store.Find(new AgentSessionKey(Repo, agentId));
        Assert.False(string.IsNullOrEmpty(session?.ContainerId), "the fake substrate must produce a jail");

        var t0 = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        // The fixture binds a fake CLI to every spawn; while it is bound the jail is untouchable, however
        // long the clock runs — that is the rule that keeps a working agent's conversation alive.
        Assert.Empty(await Reaper.SweepOnceAsync(t0.AddDays(1)));
        Assert.NotNull(store.Find(new AgentSessionKey(Repo, agentId)));

        // The CLI exits: the binder's exit watcher releases the bound session. From here the allowance runs.
        _host.Services.GetRequiredService<TerminalSessionManager>().Release(new AgentSessionKey(Repo, agentId));
        Assert.Empty(await Reaper.SweepOnceAsync(t0));                     // first sighting: the clock starts
        Assert.Empty(await Reaper.SweepOnceAsync(t0.AddMinutes(29)));      // inside the allowance: kept
        Assert.NotNull(store.Find(new AgentSessionKey(Repo, agentId)));

        var reaped = await Reaper.SweepOnceAsync(t0.AddMinutes(31));
        Assert.Equal(new[] { agentId }, reaped);
        Assert.Null(store.Find(new AgentSessionKey(Repo, agentId)));
        Assert.Contains(session!.ContainerId!, _environment.RemovedContainers);

        var audited = Assert.Single(
            _host.Services.GetRequiredService<IAuditLog>().Read(), e => e.Type == JailReaperHostedService.ReapedEvent);
        Assert.Equal(agentId, audited.Fields["agent"]);
        Assert.Equal("IdleWithoutCli", audited.Fields["cause"]);
    }

    public void Dispose()
    {
        _host.Dispose();
        _daemon.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
