using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
/// <b>Audit F3 and F6 against a real container engine.</b> The claim under test is the one sentence the
/// audit reduced this whole area to: <i>"the loop survives a daemon restart" is true of the state files
/// and false of the loop.</i>
///
/// <para>Both halves are only honestly provable here. A <c>docker pause</c>d container is a real thing
/// that outlives the process that asked for it, and the question is whether the app can get it back; a
/// <c>docker exec</c> under a daemon-side PTY is a real child process that dies with its parent, and the
/// question is whether the app can start another one in the jail that survived. A fake engine can model
/// the first and cannot model the second at all — the whole defect is about a process tree.</para>
///
/// <para>Every jail here is created by the test and torn down by it; nothing on the developer's machine
/// is read or written, which is the same discipline <see cref="AgentSessionReconcileDockerTests"/>
/// follows.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public sealed class RestartSurvivalDockerTests
{
    private const string Image = "busybox:latest";
    private const string RepoHash = "restartdockerrepohash";

    // ================= F3: a paused jail, a restart, and Resume pressed in the app ==================

    /// <summary>
    /// <b>The acceptance test.</b> A human pauses an agent. The daemon dies. The jail is still there,
    /// frozen — <c>docker inspect</c> says so. A new daemon adopts it and the human presses Resume, and
    /// the jail runs again.
    ///
    /// <para><b>There is no <c>docker unpause</c> in this test outside the app.</b> The only thing that
    /// thaws the container is <see cref="AgentPauseService.UnpauseAsync"/>, which is the body of the
    /// <c>UnpauseAgent</c> RPC — and before the restart ledger existed it answered "this agent isn't
    /// human-paused" here, because the fact that a human had paused it died with the process. The witness
    /// on both sides is Docker's own <c>State.Paused</c>, read straight off the engine rather than through
    /// anything under test.</para>
    /// </summary>
    [RequiresDockerDaemonFact]
    public async Task APausedJail_IsResumedFromInsideTheApp_AfterADaemonRestart()
    {
        using var docker = DockerEndpointResolver.CreateClient();
        using var rig = new RestartRig(docker);
        var containerId = await rig.StartJailAsync("pause").ConfigureAwait(false);

        // ---- daemon #1 ----
        var d1 = rig.NewDaemon();
        d1.Store.Spawn("claude-code", agentId: rig.AgentId, repoHash: RepoHash);
        d1.Store.AttachSandbox(new AgentSessionKey(RepoHash, rig.AgentId), containerId);

        var paused = await d1.Pause.PauseAsync(rig.AgentId, CancellationToken.None).ConfigureAwait(false);
        Assert.True(paused.Done, paused.Reason);
        Assert.True(await rig.IsPausedAsync(containerId).ConfigureAwait(false), "the engine did not freeze it");

        // ---- the daemon dies; the frozen jail does not ----
        var d2 = rig.NewDaemon();
        Assert.Empty(d2.Store.List());

        var report = await rig.Reconciler(d2.Store).ReconcileAsync().ConfigureAwait(false);
        Assert.False(report.Skipped);
        Assert.Contains(rig.AgentId, report.Adopted);
        Assert.Equal(
            AgentSessionReconciler.PausedState, d2.Store.Find(RepoHash, rig.AgentId)!.State);

        // ---- Resume, from inside the app ----
        var resumed = await d2.Pause.UnpauseAsync(rig.AgentId, CancellationToken.None).ConfigureAwait(false);

        Assert.True(resumed.Done, resumed.Reason);
        Assert.False(
            await rig.IsPausedAsync(containerId).ConfigureAwait(false),
            "the jail is still frozen — the only remaining exits are Stop and a raw `docker unpause`");
        Assert.Equal("Working", d2.Store.Find(RepoHash, rig.AgentId)!.State);
    }

    /// <summary>
    /// The emergency-stop shape of the same thing, because "engage the kill switch, then restart" is the
    /// ordinary sequence rather than an edge case. Engage froze the jail; the daemon died holding the only
    /// record of what it had frozen; the new daemon's Resume releases it anyway.
    /// </summary>
    [RequiresDockerDaemonFact]
    public async Task AKillSwitchedJail_IsReleasedByResume_AfterADaemonRestart()
    {
        using var docker = DockerEndpointResolver.CreateClient();
        using var rig = new RestartRig(docker);
        var containerId = await rig.StartJailAsync("kill").ConfigureAwait(false);

        var d1 = rig.NewKillDaemon();
        d1.Store.Spawn("claude-code", agentId: rig.AgentId, repoHash: RepoHash);
        d1.Store.AttachSandbox(new AgentSessionKey(RepoHash, rig.AgentId), containerId);
        await d1.KillSwitch.EngageAsync().ConfigureAwait(false);
        Assert.True(await rig.IsPausedAsync(containerId).ConfigureAwait(false));

        var d2 = rig.NewKillDaemon();
        d2.Store.Spawn("claude-code", agentId: rig.AgentId, repoHash: RepoHash);
        d2.Store.AttachSandbox(new AgentSessionKey(RepoHash, rig.AgentId), containerId);

        var resume = await d2.KillSwitch.ResumeAsync().ConfigureAwait(false);

        Assert.Contains(
            resume.Agents, a => a.AgentId == rig.AgentId && a.Outcome == KillResumeOutcome.Resumed);
        Assert.False(
            await rig.IsPausedAsync(containerId).ConfigureAwait(false),
            "the kill switch's Resume released nothing — its fan-out ledger died with the daemon");
    }

    // ================= F6: the CLI is a real process, and it has to be started again =================

    /// <summary>
    /// <b>A restart with a mid-task agent: the CLI is re-bound and steerable.</b>
    ///
    /// <para>The daemon's PTY child is killed to model exactly what a daemon death does to it — the
    /// exec'd CLI is a <c>docker exec</c> child of the daemon's forkpty, and <c>TerminalSessionManager</c>
    /// disposal (or the process simply ending) takes it with the daemon. Afterwards the jail is still
    /// running and holds the agent's workspace, and the app has no process in it: an adopted coordinator
    /// with four re-bound tools and nothing to call them, every adopted worker answering "no live CLI to
    /// steer".</para>
    ///
    /// <para>The second bind is the fix, driven through the same <see cref="AgentCliBinder.TryBind"/> and
    /// the same production <c>docker exec -i -t</c> PTY factory the spawn path uses. "Steerable" is not
    /// asserted from the write returning — a PTY write succeeds whether or not anything reads it — but
    /// from the new CLI's own output coming back through the bound session.</para>
    /// </summary>
    [RequiresDockerDaemonFact]
    public async Task AMidTaskAgentsCli_IsReBoundAndSteerable_AfterTheDaemonsPtyDies()
    {
        using var docker = DockerEndpointResolver.CreateClient();
        using var rig = new RestartRig(docker);
        var containerId = await rig.StartJailAsync("rebind").ConfigureAwait(false);
        var key = new AgentSessionKey(RepoHash, rig.AgentId);

        // ---- daemon #1: a jail with a live CLI, mid-task ----
        var d1 = rig.NewCliDaemon();
        d1.Store.Spawn("claude-code", agentId: rig.AgentId, repoHash: RepoHash);
        d1.Store.AttachSandbox(key, containerId);
        Assert.True(
            d1.Binder.TryBind(new AgentCliLaunchSpec(rig.AgentId, RepoHash, containerId, new[] { "sh" })),
            "the first bind failed, so this test would prove nothing about the second");
        Assert.True(d1.Binder.IsBound(key));
        Assert.True(
            await SteerAsync(d1.Terminals.TryGetBound(key)!, "FIRST").ConfigureAwait(false),
            "the first CLI never answered");

        // ---- the daemon dies, taking its PTY children with it ----
        d1.Terminals.TryGetBound(key)!.Kill();
        d1.Terminals.Release(key);

        // The jail itself is untouched — that is the whole point of the defect.
        var inspect = await docker.Containers.InspectContainerAsync(containerId).ConfigureAwait(false);
        Assert.True(inspect.State?.Running ?? false, "the jail died with the daemon; there is nothing to adopt");

        // ---- daemon #2: adopt, then re-bind ----
        var d2 = rig.NewCliDaemon();
        var report = await rig.Reconciler(d2.Store).ReconcileAsync().ConfigureAwait(false);
        Assert.Contains(rig.AgentId, report.Adopted);
        Assert.False(d2.Binder.IsBound(key), "a fresh daemon cannot already hold a CLI it never started");

        Assert.True(
            d2.Binder.TryBind(new AgentCliLaunchSpec(rig.AgentId, RepoHash, containerId, new[] { "sh" })),
            "the CLI could not be re-bound into the surviving jail");

        var rebound = d2.Terminals.TryGetBound(key);
        Assert.NotNull(rebound);
        Assert.True(
            await SteerAsync(rebound!, "SECOND").ConfigureAwait(false),
            "the re-bound CLI is not steerable — the agent is adopted, billed for, and mute");

        rebound!.Kill();
        d2.Terminals.Release(key);
    }

    /// <summary>
    /// Writes a command to the bound session and waits for the CLI's own output to come back with the
    /// marker in it. Deliberately NOT "the write returned": a PTY master accepts a write whether or not
    /// the child ever reads it, which is the exact mistake the prompt-delivery code documents at length.
    /// </summary>
    private static async Task<bool> SteerAsync(BoundTerminalSession bound, string marker)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        var command = Encoding.UTF8.GetBytes($"echo MG_{marker}_OK\r");
        while (DateTime.UtcNow < deadline)
        {
            await bound.WriteInputAsync(command, CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(250).ConfigureAwait(false);
            // The replay ring holds what the CLI actually emitted. Two occurrences would be the echo and
            // the result; one is enough to prove the child read the PTY and ran something.
            if (bound.TailText(8192).Contains($"MG_{marker}_OK", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // ================= Harness ======================================================================

    private sealed class RestartRig : IDisposable
    {
        private readonly IDockerClient _docker;
        private readonly List<string> _containers = new();
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "mg-restart-docker-" + Guid.NewGuid().ToString("N")[..12]);

        public RestartRig(IDockerClient docker)
        {
            _docker = docker;
            AgentId = "restart" + Guid.NewGuid().ToString("N")[..12];
            Directory.CreateDirectory(_root);
        }

        public string AgentId { get; }

        /// <summary>A fresh ledger over the SAME file — which is what surviving a restart means.</summary>
        private IAgentRestartLedger Ledger() =>
            new JsonAgentRestartLedger(Path.Combine(_root, AgentRestartLedger.FileName));

        /// <summary>The reconciler, scoped to THIS test's agent id so a developer box with real jails on
        /// it is neither read nor written by the suite.</summary>
        public AgentSessionReconciler Reconciler(AgentSessionStore store) =>
            new(store, async ct =>
            {
                var all = await DockerAgentLister.ListAsync(_docker, ct).ConfigureAwait(false);
                return all.Where(c => string.Equals(c.AgentId, AgentId, StringComparison.Ordinal)).ToList();
            });

        public sealed record PauseDaemon(AgentSessionStore Store, AgentPauseService Pause);

        public PauseDaemon NewDaemon()
        {
            var ledger = Ledger();
            var store = new AgentSessionStore(new InMemoryAuditLog(), ledger);
            return new PauseDaemon(
                store,
                new AgentPauseService(
                    store, new DockerOnlyEnvironment(NewEngine()), new HumanPauseLedger(ledger),
                    new KillSwitchGate(), NullLogger<AgentPauseService>.Instance));
        }

        public sealed record KillDaemon(AgentSessionStore Store, KillSwitch KillSwitch);

        public KillDaemon NewKillDaemon()
        {
            var ledger = Ledger();
            var store = new AgentSessionStore(new InMemoryAuditLog(), ledger);
            var target = new SandboxKillTarget(
                store, NewEngine(),
                new SessionLeader(new LeaderRegistry(Path.Combine(_root, "leader.json"))),
                new Auth.TerminalLockRegistry(), new HumanPauseLedger(ledger),
                NullLoggerFactory.Instance, ledger);
            return new KillDaemon(
                store,
                new KillSwitch(
                    new KillSwitchGate(), target,
                    new JsonKillJournal(Path.Combine(_root, "kills.jsonl")),
                    new InMemoryAuditLog(), null, null, null, ledger));
        }

        public sealed record CliDaemon(
            AgentSessionStore Store, TerminalSessionManager Terminals, AgentCliBinder Binder);

        public CliDaemon NewCliDaemon()
        {
            var ledger = Ledger();
            var store = new AgentSessionStore(new InMemoryAuditLog(), ledger);
            var terminals = new TerminalSessionManager();
            return new CliDaemon(
                store,
                terminals,
                new AgentCliBinder(
                    terminals,
                    new SessionLeader(new LeaderRegistry(Path.Combine(_root, "leader-cli.json"))),
                    store,
                    new InMemoryAuditLog()));
        }

        private static DockerSandboxEngine NewEngine() =>
            new(DockerEndpointResolver.CreateClient(), new SandboxEngineOptions(string.Empty, string.Empty));

        /// <summary>
        /// A trivial jail carrying the P2-07 label set, with <c>/workspace</c> present and writable so the
        /// production <c>docker exec -u 1000 -w /workspace</c> argv works against it. The labels are
        /// written here rather than through <c>ContainerSpecBuilder</c> so the test states the contract
        /// the reconciler reads instead of inheriting it.
        /// </summary>
        public async Task<string> StartJailAsync(string label)
        {
            var created = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = Image,
                Name = $"mainguard-restart-{label}-{Guid.NewGuid().ToString("N")[..8]}",
                Cmd = new List<string>
                {
                    "sh", "-c", "mkdir -p /workspace && chmod 0777 /workspace && sleep 600",
                },
                Tty = false,
                Labels = new Dictionary<string, string>
                {
                    ["mainguard.repo"] = RepoHash,
                    ["mainguard.agent"] = AgentId,
                    ["mainguard.role"] = "agent",
                    [DockerAgentLister.KindLabel] = "claude-code",
                    [DockerAgentLister.AgentRoleLabel] = string.Empty,
                },
            }).ConfigureAwait(false);

            _containers.Add(created.ID);
            await _docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters())
                .ConfigureAwait(false);

            // /workspace is made by the container's own first command; wait for it rather than racing it,
            // since the exec below runs with -w /workspace and would fail outright.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                var state = await _docker.Containers.InspectContainerAsync(created.ID).ConfigureAwait(false);
                if (state.State?.Running == true)
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    return created.ID;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            return created.ID;
        }

        /// <summary>Docker's own answer — the field <c>docker inspect -f '{{.State.Paused}}'</c> prints,
        /// read off the engine rather than through anything under test.</summary>
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
                catch (Exception)
                {
                    // Never fail a test from cleanup — a forced remove works on a paused container too.
                }
            }

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception)
            {
                // Cleanup is a courtesy.
            }
        }
    }

    /// <summary>An <see cref="IAgentEnvironment"/> that supplies nothing but the real sandbox engine —
    /// the only member the pause/kill paths touch.</summary>
    private sealed class DockerOnlyEnvironment : IAgentEnvironment
    {
        public DockerOnlyEnvironment(ISandboxEngine sandboxes) => Sandboxes = sandboxes;

        public string SubstrateId => "docker-test";

        public SubstrateCapabilities Capabilities => throw new NotSupportedException();

        public IRepoProvisioner Repos => throw new NotSupportedException();

        public IAgentWorktreeManager Worktrees => throw new NotSupportedException();

        public ISandboxEngine Sandboxes { get; }

        public IEgressPolicy Egress => throw new NotSupportedException();

        public SyncRemote ResolveSyncRemote(string repoHash) => throw new NotSupportedException();
    }
}
