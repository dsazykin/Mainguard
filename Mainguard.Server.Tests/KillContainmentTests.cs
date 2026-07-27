using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Server.Auth;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// MG-8 — the kill switch must CONTAIN, not relabel. The wired <see cref="IKillTarget"/> previously only
/// wrote <c>MarkState(agentId, "Paused")</c>: engaging the emergency stop froze the merge queue and changed
/// a word in the UI while the worker's processes kept running and its terminal stayed typeable. These tests
/// drive the real <see cref="KillSwitch"/> over the real <see cref="SandboxKillTarget"/> and assert the two
/// facts a state string cannot provide — the jail is <c>docker pause</c>d and terminal input is severed —
/// plus the honesty rule for a pause that fails, and that the composition root actually wires this target.
/// </summary>
public sealed class KillContainmentTests
{
    [Fact]
    public async Task Engage_PausesTheJail_AndSeversTerminalInput_NotJustTheStateWord()
    {
        using var rig = new KillRig();
        var agentId = rig.AddLiveAgent("ctr-a");

        var report = await rig.KillSwitch.EngageAsync();

        // The containment facts (these are what MG-8 was missing entirely).
        Assert.Contains("ctr-a", rig.Engine.Paused);
        Assert.True(rig.Locks.IsLocked(agentId), "terminal input was not severed by the kill switch");
        Assert.True(rig.Leader.IsPaused(agentId), "the leader's PTY input gate was not closed by the kill switch");

        // The state word is still reported (kept from the old target — visibility is still wanted).
        Assert.Equal("Paused", rig.Store.Find(agentId)!.State);
        Assert.Equal(KillAgentOutcome.Paused, report.Agents.Single(a => a.AgentId == agentId).Outcome);
        Assert.True(report.QueueFrozen);
    }

    [Fact]
    public async Task Engage_WhenDockerPauseFails_ReportsPauseFailed_AndNeverClaimsPaused()
    {
        using var rig = new KillRig();
        rig.Engine.FailPauses = true;
        var agentId = rig.AddLiveAgent("ctr-a");

        var report = await rig.KillSwitch.EngageAsync();

        // An uncontained worker must not project as "Paused" — that relabel-without-containment IS the bug.
        Assert.Equal(KillAgentOutcome.PauseFailed, report.Agents.Single(a => a.AgentId == agentId).Outcome);
        Assert.NotEqual("Paused", rig.Store.Find(agentId)!.State);
        Assert.Equal("Unresponsive", rig.Store.Find(agentId)!.State);

        // The input sever runs BEFORE the Docker round-trip, so it survives an engine failure.
        Assert.True(rig.Locks.IsLocked(agentId), "terminal input must be severed even when docker pause fails");

        // The kill still completed and still froze the queue (a pause failure is recorded, never thrown).
        Assert.True(report.QueueFrozen);
    }

    /// <summary>A session that never got a jail (degraded spawn) has nothing to freeze — the input sever is
    /// the whole containment, and it must not be reported as a failure or it would bury the real ones.</summary>
    [Fact]
    public async Task Engage_SessionWithoutAJail_SeversInput_AndIsNotReportedAsAFailure()
    {
        using var rig = new KillRig();
        var agentId = rig.Store.Spawn("claude").Id; // no AttachSandbox → no container id

        var report = await rig.KillSwitch.EngageAsync();

        Assert.Empty(rig.Engine.Paused);
        Assert.True(rig.Locks.IsLocked(agentId));
        Assert.Equal(KillAgentOutcome.Paused, report.Agents.Single(a => a.AgentId == agentId).Outcome);
    }

    /// <summary>The wiring half of MG-8: a containing target that is not the one the daemon resolves fixes
    /// nothing, so assert the composition root itself.</summary>
    [Fact]
    public void CompositionRoot_WiresTheContainingKillTarget()
    {
        using var daemon = new DaemonFixture();
        var target = daemon.Services.GetRequiredService<IKillTarget>();
        Assert.IsType<SandboxKillTarget>(target);
        // The KillSwitch the RPC dispatches to must be driving that same target instance.
        Assert.Same(target, daemon.Services.GetRequiredService<IKillTarget>());
        Assert.NotNull(daemon.Services.GetRequiredService<KillSwitch>());
    }

    /// <summary>The real store + leader + lock registry + a fake sandbox engine behind the real KillSwitch.</summary>
    private sealed class KillRig : IDisposable
    {
        private readonly string _registryPath =
            Path.Combine(Path.GetTempPath(), "mg-kill-" + Guid.NewGuid().ToString("N"), "leader.json");

        public KillRig()
        {
            Store = new AgentSessionStore(new InMemoryAuditLog());
            Engine = new RecordingSandboxEngine();
            Directory.CreateDirectory(Path.GetDirectoryName(_registryPath)!);
            Leader = new SessionLeader(new LeaderRegistry(_registryPath));
            Locks = new TerminalLockRegistry();
            Target = new SandboxKillTarget(Store, Engine, Leader, Locks, NullLoggerFactory.Instance);
            KillSwitch = new KillSwitch(new KillSwitchGate(), Target, rttBudget: () => TimeSpan.Zero);
        }

        public AgentSessionStore Store { get; }

        public RecordingSandboxEngine Engine { get; }

        public SessionLeader Leader { get; }

        public TerminalLockRegistry Locks { get; }

        public SandboxKillTarget Target { get; }

        public KillSwitch KillSwitch { get; }

        /// <summary>A spawned session with a jail attached and a leader-owned PTY — a live worker.</summary>
        public string AddLiveAgent(string containerId)
        {
            var id = Store.Spawn("claude").Id;
            Store.AttachSandbox(id, containerId, "repohash");
            Leader.Register(new LeaderSession(id, "repohash", containerId, 80, 24, SocketPath: string.Empty));
            return id;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(_registryPath)!, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    /// <summary>Records pause/unpause by container id; <see cref="FailPauses"/> models an unreachable engine.</summary>
    private sealed class RecordingSandboxEngine : ISandboxEngine
    {
        public ConcurrentBag<string> Paused { get; } = new();

        public bool FailPauses { get; set; }

        public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default) =>
            Task.FromResult(new SandboxHandle($"ctr-{request.AgentId}", Reused: false));

        public Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));

        public Task PauseAsync(string containerId, CancellationToken ct = default)
        {
            if (FailPauses)
            {
                throw new InvalidOperationException("docker daemon unreachable");
            }

            Paused.Add(containerId);
            return Task.CompletedTask;
        }

        public Task UnpauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task StopAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
