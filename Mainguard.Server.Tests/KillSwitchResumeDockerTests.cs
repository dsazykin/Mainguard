using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Server.Auth;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// ISSUES-LOG #17, end to end against a real container engine: <b>the emergency stop must be
/// reversible</b>.
///
/// <para>Every other kill-switch test in this repo ran over a fake <c>ISandboxEngine</c>, which is exactly
/// why the defect survived: engaging the kill switch really did <c>docker pause</c> every jail, and Resume
/// really did nothing but clear the merge/spawn freeze flag, so the containers stayed frozen for the life
/// of the daemon while the Resource Monitor's own row called the state "(recoverable)". Only a raw
/// <c>docker unpause</c> from outside the app — or a daemon restart, which then orphans the jail — could
/// bring a killed agent back. Reproduced twice live (RPC harness, then real UI clicks against a real
/// claude-code coordinator) before it was fixed.</para>
///
/// <para>So these tests assert the claim itself, with Docker as the only witness: engage, read
/// <c>.State.Paused</c> off the daemon, resume, read it again. The second test asserts the half that must
/// NOT happen — a jail a human paused is still paused after a whole kill/resume cycle, because a
/// human pause and a kill-switch pause are different reasons for the same frozen state and the kill switch
/// only ever reverses its own.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public sealed class KillSwitchResumeDockerTests
{
    /// <summary>A trivial jail — the assertions are about pause/unpause, not about the agent image.</summary>
    private const string Image = "busybox:latest";

    [RequiresDockerDaemonFact]
    public async Task Engage_PausesTheRealJail_AndResume_ActuallyUnpausesIt()
    {
        using var docker = DockerEndpointResolver.CreateClient();
        using var rig = new LiveKillRig(docker);
        var containerId = await rig.StartJailAsync("resume").ConfigureAwait(false);
        var agentId = rig.Register(containerId);

        Assert.False(await rig.IsPausedAsync(containerId).ConfigureAwait(false));

        // ---- Engage: the half that always worked ----
        var kill = await rig.KillSwitch.EngageAsync().ConfigureAwait(false);
        Assert.Equal(KillAgentOutcome.Paused, Assert.Single(kill.Agents).Outcome);
        Assert.True(kill.QueueFrozen);
        Assert.True(await rig.IsPausedAsync(containerId).ConfigureAwait(false),
            "engage must docker-pause the jail (this half was never broken)");

        // ---- Resume: the half that did not exist ----
        var resume = await rig.KillSwitch.ResumeAsync().ConfigureAwait(false);

        Assert.Equal(KillResumeOutcome.Resumed, Assert.Single(resume.Agents).Outcome);
        Assert.False(resume.QueueFrozen);
        Assert.False(await rig.IsPausedAsync(containerId).ConfigureAwait(false),
            "Resume left the jail docker-paused — ISSUES-LOG #17, the exact regression this test exists for");

        // The containment's other half comes back too: the terminal lock this kill took is released, and
        // the row no longer claims a paused agent.
        Assert.False(rig.Locks.IsLocked(agentId));
        Assert.False(rig.Leader.IsPaused(agentId));
        Assert.Equal("Working", rig.Store.Find(agentId)!.State);
    }

    /// <summary>
    /// The arbitration, against the real engine: the human's pause is sticky through the whole cycle. The
    /// kill switch finds that jail already frozen (Docker answers 409 to the second pause), reports
    /// containment rather than a failure, never records the pause as its own — and therefore leaves it
    /// paused on Resume, while the jail it did freeze comes back.
    /// </summary>
    [RequiresDockerDaemonFact]
    public async Task Resume_LeavesAHumanPausedJailFrozen_WhileReleasingTheOneTheKillSwitchFroze()
    {
        using var docker = DockerEndpointResolver.CreateClient();
        using var rig = new LiveKillRig(docker);

        var humanContainer = await rig.StartJailAsync("human").ConfigureAwait(false);
        var humanAgent = rig.Register(humanContainer);
        var killedContainer = await rig.StartJailAsync("killed").ConfigureAwait(false);
        rig.Register(killedContainer);

        // The human pause, exactly as AgentPauseService leaves it: the jail frozen + the ledger marked.
        await rig.Engine.PauseAsync(humanContainer).ConfigureAwait(false);
        rig.Ledger.MarkHumanPaused(humanAgent);

        var kill = await rig.KillSwitch.EngageAsync().ConfigureAwait(false);
        Assert.All(kill.Agents, a => Assert.NotEqual(KillAgentOutcome.PauseFailed, a.Outcome));
        Assert.True(await rig.IsPausedAsync(humanContainer).ConfigureAwait(false));
        Assert.True(await rig.IsPausedAsync(killedContainer).ConfigureAwait(false));

        await rig.KillSwitch.ResumeAsync().ConfigureAwait(false);

        Assert.False(await rig.IsPausedAsync(killedContainer).ConfigureAwait(false),
            "the jail the kill switch froze must be released");
        Assert.True(await rig.IsPausedAsync(humanContainer).ConfigureAwait(false),
            "a human pause must survive a kill/resume cycle — the kill switch never owned it");
        Assert.True(rig.Ledger.IsHumanPaused(humanAgent));
    }

    /// <summary>The real daemon pieces — store, leader, lock registry, human-pause ledger and a REAL
    /// <see cref="DockerSandboxEngine"/> — behind the real <see cref="KillSwitch"/>. Only the jails are
    /// trivial.</summary>
    private sealed class LiveKillRig : IDisposable
    {
        private readonly IDockerClient _docker;
        private readonly List<string> _containers = new();
        private readonly string _stateDir =
            Path.Combine(Path.GetTempPath(), "mg-kill-live-" + Guid.NewGuid().ToString("N"));

        public LiveKillRig(IDockerClient docker)
        {
            _docker = docker;
            Directory.CreateDirectory(_stateDir);
            Store = new AgentSessionStore(new InMemoryAuditLog());
            Engine = new DockerSandboxEngine(docker, new SandboxEngineOptions(string.Empty, string.Empty));
            Leader = new SessionLeader(new LeaderRegistry(Path.Combine(_stateDir, "leader.json")));
            Locks = new TerminalLockRegistry();
            Ledger = new HumanPauseLedger();
            Target = new SandboxKillTarget(Store, Engine, Leader, Locks, Ledger, NullLoggerFactory.Instance);
            KillSwitch = new KillSwitch(
                new KillSwitchGate(), Target,
                journal: new JsonKillJournal(Path.Combine(_stateDir, "kills.jsonl")),
                audit: new InMemoryAuditLog());
        }

        public AgentSessionStore Store { get; }

        public DockerSandboxEngine Engine { get; }

        public SessionLeader Leader { get; }

        public TerminalLockRegistry Locks { get; }

        public HumanPauseLedger Ledger { get; }

        public SandboxKillTarget Target { get; }

        public KillSwitch KillSwitch { get; }

        /// <summary>Creates and starts a throwaway jail, remembering it for teardown.</summary>
        public async Task<string> StartJailAsync(string label)
        {
            var created = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = Image,
                Name = $"mainguard-killresume-{label}-{Guid.NewGuid().ToString("N")[..8]}",
                Cmd = new List<string> { "sleep", "300" },
            }).ConfigureAwait(false);

            _containers.Add(created.ID);
            await _docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters())
                .ConfigureAwait(false);
            return created.ID;
        }

        /// <summary>Registers the jail as a live agent session with a leader-owned PTY — what the kill
        /// switch fans out over.</summary>
        public string Register(string containerId, string repoHash = "repohash")
        {
            var id = Store.Spawn("claude", repoHash: repoHash).Id;
            Store.AttachSandbox(id, containerId, repoHash);
            Leader.Register(new LeaderSession(id, repoHash, containerId, 80, 24, SocketPath: string.Empty));
            return id;
        }

        /// <summary>Docker's own answer — the same field <c>docker inspect -f '{{.State.Paused}}'</c>
        /// prints, read straight off the daemon rather than through anything under test.</summary>
        public async Task<bool> IsPausedAsync(string containerId)
        {
            var inspect = await _docker.Containers.InspectContainerAsync(containerId).ConfigureAwait(false);
            return inspect.State?.Paused ?? false;
        }

        public void Dispose()
        {
            foreach (var containerId in _containers)
            {
                try
                {
                    _docker.Containers.RemoveContainerAsync(
                        containerId, new ContainerRemoveParameters { Force = true }).GetAwaiter().GetResult();
                }
                catch
                {
                    // Never fail a test from cleanup — a forced remove works on a paused container too.
                }
            }

            try
            {
                Directory.Delete(_stateDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
