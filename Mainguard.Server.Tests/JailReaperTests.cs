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
        var terminals = _host.Services.GetRequiredService<TerminalSessionManager>();
        if (terminals.TryGetBound(new AgentSessionKey(Repo, agentId)) is not null)
        {
            // Where the fixture manages to bind a fake CLI (a PTY is available), the jail is untouchable
            // while it is bound, however long the clock runs — the rule that keeps a working agent's
            // conversation alive. A bound sweep seeds no idle clock, so the timeline below is unaffected.
            Assert.Empty(await Reaper.SweepOnceAsync(t0.AddDays(1)));
            Assert.NotNull(store.Find(new AgentSessionKey(Repo, agentId)));
        }

        // The CLI is gone (exited, or never bound on this platform): from the first sighting the allowance runs.
        terminals.Release(new AgentSessionKey(Repo, agentId));
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

    /// <summary>
    /// <b>Audit F20 — the reaper is not the one who decides a dead agent's work is finished.</b>
    ///
    /// <para>A session the reconciler has marked <c>Unresponsive</c> has no jail left: Docker has no live
    /// container for it, so there is no memory, no CPU and no container to reclaim, which is this sweep's
    /// entire remit. What a reap would still do is delete the agent's worktree — the only place its
    /// uncommitted work exists — putting the decision to discard it in the hands of a 30-minute clock. The
    /// record stays, visible and Stoppable, until a person makes that call.</para>
    /// </summary>
    [Fact]
    public async Task AJailTheReconcilerCallsUnresponsive_IsLeftForAHuman_NotReapedOnATimer()
    {
        var spawns = _host.Services.GetRequiredService<AgentSpawnService>();
        var store = _host.Services.GetRequiredService<AgentSessionStore>();
        var agentId = await spawns.SpawnAsync(Repo, "claude-code", null, AgentRoles.Managed, CancellationToken.None);
        var key = new AgentSessionKey(Repo, agentId);
        var session = store.Find(key);
        Assert.False(string.IsNullOrEmpty(session?.ContainerId), "the fake substrate must produce a jail");

        _host.Services.GetRequiredService<TerminalSessionManager>().Release(key);
        // Exactly what AgentSessionReconciler writes when a session's container is gone.
        store.MarkState(key, AgentSessionReconciler.LostState, AgentSessionReconciler.LostReason);

        var t0 = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        Assert.Empty(await Reaper.SweepOnceAsync(t0));
        Assert.Empty(await Reaper.SweepOnceAsync(t0.AddHours(6)));

        Assert.NotNull(store.Find(key));
        Assert.DoesNotContain(session!.ContainerId!, _environment.RemovedContainers);

        // ...and the human's Stop still works on it — that is what "left for a human" has to mean.
        Assert.True((await spawns.StopAsync(key, CancellationToken.None)).Stopped);
        Assert.Null(store.Find(key));
    }

    /// <summary>
    /// <b>Audit F27 — the sweep is wired.</b> `SandboxSegmentReaper` was complete and tested and reached
    /// nothing: the leak recovery was inert until a caller ran it. The reaper host is that caller, and the
    /// two sweeps it runs are on different axes and different cadences — a jail goes idle in minutes, a
    /// leaked segment matters only once a few dozen have exhausted Docker's address pool.
    ///
    /// <para>The reap is audited by segment name, which is what an operator has to go on: a network that
    /// no longer exists cannot be inspected afterwards.</para>
    /// </summary>
    [Fact]
    public async Task TheSegmentSweep_RunsFromTheReaperHost_AndAuditsWhatItReclaimed()
    {
        var swept = 0;
        var host = new JailReaperHostedService(
            _host.Services.GetRequiredService<AgentSessionStore>(),
            _host.Services.GetRequiredService<TerminalSessionManager>(),
            _host.Services.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.IMergeQueueRegistry>(),
            _host.Services.GetRequiredService<AgentSpawnService>(),
            _host.Services.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.CoordinatorLimits>(),
            _host.Services.GetRequiredService<IAuditLog>(),
            _host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
            sweepSegments: _ =>
            {
                swept++;
                return Task.FromResult<IReadOnlyList<string>>(new[] { "mainguard-agent-deadbeef" });
            });

        var reaped = await host.SweepSegmentsOnceAsync(DateTimeOffset.UtcNow);

        Assert.Equal(1, swept);
        Assert.Equal(new[] { "mainguard-agent-deadbeef" }, reaped);
        var audited = Assert.Single(
            _host.Services.GetRequiredService<IAuditLog>().Read(),
            e => e.Type == JailReaperHostedService.SegmentReapedEvent);
        Assert.Equal("mainguard-agent-deadbeef", audited.Fields["segment"]);
    }

    /// <summary>A segment sweep that throws must not take the jail sweep — the load-bearing half of this
    /// host — down with it. A reaper that throws is a reaper someone disables.</summary>
    [Fact]
    public async Task AThrowingSegmentSweep_IsSwallowed()
    {
        var host = new JailReaperHostedService(
            _host.Services.GetRequiredService<AgentSessionStore>(),
            _host.Services.GetRequiredService<TerminalSessionManager>(),
            _host.Services.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.IMergeQueueRegistry>(),
            _host.Services.GetRequiredService<AgentSpawnService>(),
            _host.Services.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.CoordinatorLimits>(),
            _host.Services.GetRequiredService<IAuditLog>(),
            _host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
            sweepSegments: _ => throw new InvalidOperationException("the engine did not answer"));

        Assert.Empty(await host.SweepSegmentsOnceAsync(DateTimeOffset.UtcNow));
    }

    public void Dispose()
    {
        _host.Dispose();
        _daemon.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
