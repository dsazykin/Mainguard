using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The human per-agent Pause/Resume (P2 fix pass) and its arbitration against the keep-alive
/// rebase's yield-pause. The two rules under test are the whole design: a human pause is STICKY
/// (the machine never wakes it), and a human unpause DEFERS to an in-flight machine hold (an
/// honest, self-clearing refusal — a click must never break the rebase's pause open mid-write).
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public sealed class AgentPauseTests
{
    // ---- HumanPauseLedger ------------------------------------------------

    [Fact]
    public void Ledger_TracksHumanPauses_AndNestedMachineHolds()
    {
        var ledger = new HumanPauseLedger();
        Assert.False(ledger.IsHumanPaused("a"));

        ledger.MarkHumanPaused("a");
        Assert.True(ledger.IsHumanPaused("a"));
        Assert.False(ledger.IsHumanPaused("b"));

        var h1 = ledger.HoldForMachine("a");
        var h2 = ledger.HoldForMachine("a");
        Assert.True(ledger.HasMachineHold("a"));

        h1.Dispose();
        Assert.True(ledger.HasMachineHold("a"), "one of two holds released — still held");
        h2.Dispose();
        Assert.False(ledger.HasMachineHold("a"));

        // Double-dispose must not underflow another holder's count.
        h2.Dispose();
        var h3 = ledger.HoldForMachine("a");
        Assert.True(ledger.HasMachineHold("a"));
        h3.Dispose();

        ledger.ClearHumanPaused("a");
        Assert.False(ledger.IsHumanPaused("a"));
    }

    // ---- AgentPauseService over fakes ------------------------------------

    private sealed class FakePausableEngine : ISandboxEngine
    {
        public HashSet<string> Paused { get; } = new(StringComparer.Ordinal);
        public int PauseCalls;
        public int UnpauseCalls;

        public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task PauseAsync(string containerId, CancellationToken ct = default)
        {
            PauseCalls++;
            Paused.Add(containerId);
            return Task.CompletedTask;
        }

        public Task UnpauseAsync(string containerId, CancellationToken ct = default)
        {
            UnpauseCalls++;
            Paused.Remove(containerId);
            return Task.CompletedTask;
        }

        public Task<bool> IsPausedAsync(string containerId, CancellationToken ct = default)
            => Task.FromResult(Paused.Contains(containerId));

        public Task StopAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeEnvironment : IAgentEnvironment
    {
        public FakeEnvironment(ISandboxEngine sandboxes) => Sandboxes = sandboxes;
        public string SubstrateId => "test";
        public SubstrateCapabilities Capabilities => throw new NotSupportedException();
        public IRepoProvisioner Repos => throw new NotSupportedException();
        public IAgentWorktreeManager Worktrees => throw new NotSupportedException();
        public ISandboxEngine Sandboxes { get; }
        public IEgressPolicy Egress => throw new NotSupportedException();
        public SyncRemote ResolveSyncRemote(string repoHash) => throw new NotSupportedException();
    }

    private static (AgentPauseService Service, AgentSessionStore Store, FakePausableEngine Engine,
        HumanPauseLedger Ledger, KillSwitchGate Kill) Build()
    {
        var store = new AgentSessionStore(new InMemoryAuditLog());
        var engine = new FakePausableEngine();
        var ledger = new HumanPauseLedger();
        var kill = new KillSwitchGate();
        var service = new AgentPauseService(
            store, new FakeEnvironment(engine), ledger, kill, NullLogger<AgentPauseService>.Instance);
        return (service, store, engine, ledger, kill);
    }

    [Fact]
    public async Task Pause_FreezesEverySessionBehindTheId_AndMarksPaused()
    {
        var (service, store, engine, ledger, _) = Build();
        // The same id in two repos (the pr-<n> shape) — pausing one and leaving the other running
        // would be a half-true pause.
        store.Spawn("claude-code", agentId: "pr-7", repoHash: "repo-a");
        store.AttachSandbox(new AgentSessionKey("repo-a", "pr-7"), "c-a");
        store.Spawn("claude-code", agentId: "pr-7", repoHash: "repo-b");
        store.AttachSandbox(new AgentSessionKey("repo-b", "pr-7"), "c-b");

        var (done, reason) = await service.PauseAsync("pr-7", CancellationToken.None);

        Assert.True(done, reason);
        Assert.Equal(new[] { "c-a", "c-b" }, engine.Paused.OrderBy(x => x));
        Assert.True(ledger.IsHumanPaused("pr-7"));
        Assert.All(store.FindAll("pr-7"), s => Assert.Equal("Paused", s.State));
    }

    [Fact]
    public async Task Pause_ToleratesAnAlreadyFrozenJail_ByInspectState_NotByErrorText()
    {
        var (service, store, engine, _, _) = Build();
        store.Spawn("claude-code", agentId: "a", repoHash: "repo");
        store.AttachSandbox(new AgentSessionKey("repo", "a"), "c-1");
        engine.Paused.Add("c-1"); // the cascade's yield froze it a moment ago

        var (done, reason) = await service.PauseAsync("a", CancellationToken.None);

        Assert.True(done, reason);
        Assert.Equal(0, engine.PauseCalls); // no second pause fired at the engine
    }

    [Fact]
    public async Task Unpause_IsRefused_WhileTheMachineHoldsTheJail_AndSucceedsAfter()
    {
        var (service, store, engine, ledger, _) = Build();
        store.Spawn("claude-code", agentId: "a", repoHash: "repo");
        store.AttachSandbox(new AgentSessionKey("repo", "a"), "c-1");
        Assert.True((await service.PauseAsync("a", CancellationToken.None)).Done);

        using (ledger.HoldForMachine("a"))
        {
            var (done, reason) = await service.UnpauseAsync("a", CancellationToken.None);
            Assert.False(done);
            Assert.Contains("try again in a moment", reason);
            Assert.True(ledger.IsHumanPaused("a"), "the refusal must not half-clear the human's pause");
        }

        var after = await service.UnpauseAsync("a", CancellationToken.None);
        Assert.True(after.Done, after.Reason);
        Assert.False(ledger.IsHumanPaused("a"));
        Assert.Empty(engine.Paused);
        Assert.Equal("Working", store.Find(new AgentSessionKey("repo", "a"))!.State);
    }

    [Fact]
    public async Task PauseAndUnpause_RefuseUnderTheKillFreeze_AndForAgentsWithNoJail()
    {
        var (service, store, _, _, kill) = Build();

        var noJail = await service.PauseAsync("ghost", CancellationToken.None);
        Assert.False(noJail.Done);
        Assert.Contains("no live jail", noJail.Reason);

        store.Spawn("claude-code", agentId: "a", repoHash: "repo");
        store.AttachSandbox(new AgentSessionKey("repo", "a"), "c-1");
        kill.Freeze();

        Assert.False((await service.PauseAsync("a", CancellationToken.None)).Done);
        Assert.False((await service.UnpauseAsync("a", CancellationToken.None)).Done);
    }

    // ---- The real engine leg (docker pause → inspect .State.Paused → unpause) ----

    [RequiresDockerDaemonFact]
    public async Task RealJail_PausesByInspectState_AndUnpauses()
    {
        using var docker = Mainguard.Agents.Agents.Sandbox.DockerEndpointResolver.CreateClient();
        var engine = new DockerSandboxEngine(docker, new SandboxEngineOptions(string.Empty, string.Empty));

        var created = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = "busybox:latest",
            Name = "mainguard-pause-test-" + Guid.NewGuid().ToString("N")[..8],
            Cmd = new List<string> { "sleep", "120" },
        });
        try
        {
            await docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters());

            Assert.False(await engine.IsPausedAsync(created.ID));
            await engine.PauseAsync(created.ID);
            Assert.True(await engine.IsPausedAsync(created.ID));
            await engine.UnpauseAsync(created.ID);
            Assert.False(await engine.IsPausedAsync(created.ID));
        }
        finally
        {
            try
            {
                await docker.Containers.RemoveContainerAsync(
                    created.ID, new ContainerRemoveParameters { Force = true });
            }
            catch
            {
                // Never fail a test from cleanup.
            }
        }
    }
}
