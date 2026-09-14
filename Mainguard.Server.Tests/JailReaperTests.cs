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

        // Scoped to THIS agent: the audit log is db-backed under the suite's shared data root, so every
        // other reap in the assembly lands in the same table and "the only reap event" was never the
        // claim being made.
        var audited = Assert.Single(
            _host.Services.GetRequiredService<IAuditLog>().Read(),
            e => e.Type == JailReaperHostedService.ReapedEvent
                 && string.Equals(e.Fields.GetValueOrDefault("agent"), agentId, StringComparison.Ordinal));
        Assert.Equal(agentId, audited.Fields["agent"]);
        Assert.Equal("IdleWithoutCli", audited.Fields["cause"]);
    }

    /// <summary>
    /// B1, at the service tier: the adoption mark is NOT a blanket reprieve. It excuses a missing terminal
    /// only for a jail whose entry the daemon still expects work from — an adopted jail with no
    /// merge-queue entry at all (a coordinator, a repo with no queue) is the largest part of the
    /// population the reaper exists for and is stopped at the allowance like any other.
    /// </summary>
    [Fact]
    public async Task AnAdoptedJail_WithNoQueueEntry_IsStillReapedAtTheAllowance()
    {
        var spawns = _host.Services.GetRequiredService<AgentSpawnService>();
        var store = _host.Services.GetRequiredService<AgentSessionStore>();
        var agentId = await spawns.SpawnAsync(Repo, "claude-code", null, AgentRoles.Managed, CancellationToken.None);
        var key = new AgentSessionKey(Repo, agentId);
        store.MarkAdopted(key);
        Assert.True(store.WasAdoptedWithoutTerminal(key));

        _host.Services.GetRequiredService<TerminalSessionManager>().Release(key);

        var t0 = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        Assert.Empty(await Reaper.SweepOnceAsync(t0));
        Assert.Equal(new[] { agentId }, await Reaper.SweepOnceAsync(t0.AddMinutes(31)));
        Assert.Null(store.Find(key));
    }

    /// <summary>
    /// ...and the mark is spent the moment the daemon can see a terminal again. The blind spot it records
    /// is "this daemon cannot observe that jail's CLI"; a bound CLI ends it, whether the bind came from a
    /// re-bind on adoption or an ordinary re-spawn into the same jail. Without this the exemption would
    /// outlive its own justification and keep the jail forever.
    /// </summary>
    [Fact]
    public async Task TheAdoptionMark_IsClearedByTheFirstSweepThatSeesABoundCli()
    {
        var spawns = _host.Services.GetRequiredService<AgentSpawnService>();
        var store = _host.Services.GetRequiredService<AgentSessionStore>();
        var terminals = _host.Services.GetRequiredService<TerminalSessionManager>();
        var agentId = await spawns.SpawnAsync(Repo, "claude-code", null, AgentRoles.Managed, CancellationToken.None);
        var key = new AgentSessionKey(Repo, agentId);
        store.MarkAdopted(key);

        terminals.Release(key); // whatever the fixture bound, this test binds its own
        using var cli = new QuietSession();
        terminals.Bind(key, new BoundTerminalSession(agentId, cli));

        Assert.Empty(await Reaper.SweepOnceAsync(new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero)));

        Assert.False(store.WasAdoptedWithoutTerminal(key));
    }

    /// <summary>A CLI that is simply alive: an empty output pipe the bound session's pump reads forever.</summary>
    private sealed class QuietSession : ITerminalSession
    {
        private readonly System.IO.Pipelines.Pipe _output = new();
        private readonly TaskCompletionSource<int> _exit = new();

        public Stream IO => _output.Reader.AsStream();

        public Task<int> ExitCode => _exit.Task;

        public void Resize(int cols, int rows)
        {
        }

        public void Kill() => _exit.TrySetResult(0);

        public void Dispose() => _exit.TrySetResult(0);
    }

    public void Dispose()
    {
        _host.Dispose();
        _daemon.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
