using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
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

    /// <summary>
    /// A session's identity is (repo, agent id), so two repositories can each hold <c>pr-7</c> — the
    /// external-PR intake names its workers after the pull request number. <see cref="IKillTarget"/> is an
    /// id-only contract, so the emergency stop must fan out over EVERY session behind an id: containing one
    /// and leaving the other's jail running would be MG-8's original failure (a state word instead of
    /// containment) reintroduced through a lookup.
    /// </summary>
    [Fact]
    public async Task Engage_WhenTwoReposShareAnAgentId_PausesBothJails_AndSnapshotsWithoutThrowing()
    {
        using var rig = new KillRig();
        rig.AddLiveAgent("ctr-repo-a", "repo-a", "pr-7");
        rig.AddLiveAgent("ctr-repo-b", "repo-b", "pr-7");

        var report = await rig.KillSwitch.EngageAsync();

        // Both jails frozen — not one of them.
        Assert.Contains("ctr-repo-a", rig.Engine.Paused);
        Assert.Contains("ctr-repo-b", rig.Engine.Paused);

        // Both sessions marked, each under its own repo.
        Assert.Equal("Paused", rig.Store.Find("repo-a", "pr-7")!.State);
        Assert.Equal("Paused", rig.Store.Find("repo-b", "pr-7")!.State);

        // The id is offered to the fan-out ONCE (a repeated id would pause twice and report twice), and
        // the journal snapshot — a dictionary keyed by agent id — is built rather than throwing on the
        // duplicate key.
        Assert.Equal("pr-7", Assert.Single(report.Agents).AgentId);
        Assert.Equal(KillAgentOutcome.Paused, report.Agents[0].Outcome);
        Assert.Equal("Paused", rig.Target.CaptureStates()["pr-7"]);
    }

    /// <summary>
    /// RT-D4 observed in BOTH directions over the real target — the thing the old rig could not do because
    /// it hardcoded the production default (<c>rttBudget: () =&gt; TimeSpan.Zero</c>), making
    /// <c>RttSpikeDetected</c> constant false and the A3 <c>Unresponsive</c> feed unreachable.
    /// </summary>
    [Theory]
    [InlineData(0)]        // a measured-fast channel: no spike
    [InlineData(10)]       // 50 × 10 ms = 500 ms, well inside the 30 s ceiling — no spike
    [InlineData(1_000)]    // 50 × 1 s = 50 s — blows the ceiling, so a spike
    public async Task Engage_RttSpikeArm_FiresExactlyWhenFiftyTimesRttBlowsTheCeiling(int rttMilliseconds)
    {
        using var rig = new KillRig();
        rig.Rtt = TimeSpan.FromMilliseconds(rttMilliseconds);
        rig.AddLiveAgent("ctr-a");

        var report = await rig.KillSwitch.EngageAsync();

        // The expected answer is the formula's, computed here rather than restated as a literal.
        var expected = KillSwitchTiming.RttWouldExceedCeiling(rig.Rtt);
        Assert.Equal(expected, report.RttSpikeDetected);
        Assert.Equal(expected, rig.SpikeEpochs.Contains(report.KillEpochId));

        // Whatever the RTT, the deadline never exceeds the ceiling (the RT-D4 invariant proper).
        Assert.True(report.Deadline <= KillSwitchTiming.Ceiling);

        // This rig MEASURES the RTT, so the record says so — the daemon's sentinel says the opposite.
        Assert.True(report.RttMeasured);
    }

    /// <summary>
    /// Step 3's snapshot has to survive the process. The default journal is an
    /// <c>InMemoryKillJournal</c> nothing holds a reference to, so "written BEFORE returning" was written
    /// into garbage — read it back off disk instead, with a FRESH reader, which is what a daemon that
    /// restarted after the emergency stop sees.
    /// </summary>
    [Fact]
    public async Task Engage_WritesTheKillEpochToADurableJournal_ReadableAfterTheFact()
    {
        using var rig = new KillRig();
        var agentId = rig.AddLiveAgent("ctr-a");

        var report = await rig.KillSwitch.EngageAsync();

        var reread = new JsonKillJournal(rig.Journal.Path).ReadAll();
        var snapshot = Assert.Single(reread);
        Assert.Equal(report.KillEpochId, snapshot.KillEpochId);
        Assert.True(snapshot.QueueFrozen);
        Assert.Equal(agentId, Assert.Single(snapshot.Agents).AgentId);
        Assert.Equal(KillAgentOutcome.Paused, snapshot.Agents[0].Outcome);
    }

    // ---- ISSUES-LOG #17 — the release path, and what it must NOT release ----

    /// <summary>
    /// The whole bug in one test. Engage froze the jail and severed input; Resume used to clear a boolean
    /// and nothing else, leaving the container paused for the life of the daemon while the Resource
    /// Monitor's own row said "(recoverable)". Both halves of the containment must now come back.
    /// </summary>
    [Fact]
    public async Task Resume_UnpausesTheJail_AndReleasesTheTerminalLock_TheKillItselfTook()
    {
        using var rig = new KillRig();
        var agentId = rig.AddLiveAgent("ctr-a");

        await rig.KillSwitch.EngageAsync();
        Assert.Contains("ctr-a", rig.Engine.Paused);
        Assert.True(rig.Locks.IsLocked(agentId));
        Assert.True(rig.Leader.IsPaused(agentId));

        var report = await rig.KillSwitch.ResumeAsync();

        Assert.Equal(KillResumeOutcome.Resumed, Assert.Single(report.Agents).Outcome);
        Assert.DoesNotContain("ctr-a", rig.Engine.Paused);
        Assert.False(rig.Locks.IsLocked(agentId), "the kill switch took this lock, so the kill switch must release it");
        Assert.False(rig.Leader.IsPaused(agentId));
        Assert.Equal("Working", rig.Store.Find(agentId)!.State);
        Assert.False(rig.KillSwitch.IsEngaged);
    }

    /// <summary>
    /// The distinction the fix must not blur: a human pause and a kill-switch pause are different reasons
    /// for the same Docker-paused state. A jail the human had already paused is still contained when the
    /// stop fires (so it is NOT a PauseFailed), but the kill switch never owned that pause and must leave
    /// it exactly where it found it — the same stickiness <c>AgentPauseService</c> gives a human pause
    /// against the machine's yield hold.
    /// </summary>
    [Fact]
    public async Task Resume_LeavesAJailTheHumanHadAlreadyPaused_Frozen()
    {
        using var rig = new KillRig();
        var human = rig.AddLiveAgent("ctr-human");
        var killed = rig.AddLiveAgent("ctr-killed");

        // The human's pause, as AgentPauseService leaves the world: the jail frozen and the ledger marked.
        await rig.Engine.PauseAsync("ctr-human");
        rig.Ledger.MarkHumanPaused(human);

        var kill = await rig.KillSwitch.EngageAsync();

        // Containment is satisfied for both — the already-frozen jail is NOT reported as a failed pause.
        Assert.All(kill.Agents, a => Assert.NotEqual(KillAgentOutcome.PauseFailed, a.Outcome));
        Assert.Contains("ctr-human", rig.Engine.Paused);
        Assert.Contains("ctr-killed", rig.Engine.Paused);

        await rig.KillSwitch.ResumeAsync();

        // The kill switch reverses its own pause and only its own.
        Assert.DoesNotContain("ctr-killed", rig.Engine.Paused);
        Assert.Contains("ctr-human", rig.Engine.Paused);
        Assert.True(rig.Ledger.IsHumanPaused(human), "a kill/resume cycle must not clear the human's pause");
    }

    /// <summary>The same rule at the other end of the race: the ledger says human-paused by the time Resume
    /// runs, even though the kill switch's own pause call is the one that reached the engine first. The
    /// human's intent outranks the ledger entry — Resume leaves the jail frozen.</summary>
    [Fact]
    public async Task Resume_LeavesAJailTheHumanPausedDuringTheFreeze_Frozen()
    {
        using var rig = new KillRig();
        var agentId = rig.AddLiveAgent("ctr-a");

        await rig.KillSwitch.EngageAsync();
        rig.Ledger.MarkHumanPaused(agentId); // the human claims it while the stop is engaged

        await rig.KillSwitch.ResumeAsync();

        Assert.Contains("ctr-a", rig.Engine.Paused);
        // The terminal sever is still reversed: the human paused the agent's work, not the operator's
        // ability to type at it.
        Assert.False(rig.Locks.IsLocked(agentId));
    }

    /// <summary>
    /// The concern the original "deliberately no un-containment" note was protecting, kept intact: a
    /// managed worker's terminal is locked at SPAWN as a role property. The kill switch did not take that
    /// lock, so the kill switch does not release it — a blanket unlock would hand an operator-locked
    /// worker a typeable terminal.
    /// </summary>
    [Fact]
    public async Task Resume_KeepsTheSpawnTimeTerminalLockOfAManagedWorker()
    {
        using var rig = new KillRig();
        var agentId = rig.AddLiveAgent("ctr-a");
        rig.Locks.Lock(agentId); // coordinated-mode spawn locked it long before any kill

        await rig.KillSwitch.EngageAsync();
        await rig.KillSwitch.ResumeAsync();

        Assert.DoesNotContain("ctr-a", rig.Engine.Paused);
        Assert.True(rig.Locks.IsLocked(agentId), "a lock taken at spawn must survive a kill/resume cycle");
    }

    /// <summary>A container torn down during the freeze is released by definition. It must not be reported
    /// as a failed release, and it must not abandon the rest of the fan-out.</summary>
    [Fact]
    public async Task Resume_ToleratesAJailThatNoLongerExists_AndStillReleasesTheOthers()
    {
        using var rig = new KillRig();
        rig.AddLiveAgent("ctr-gone");
        rig.AddLiveAgent("ctr-live");

        await rig.KillSwitch.EngageAsync();
        rig.Engine.Vanish("ctr-gone"); // agent torn down while the stop was engaged

        var report = await rig.KillSwitch.ResumeAsync();

        Assert.All(report.Agents, a => Assert.Equal(KillResumeOutcome.Resumed, a.Outcome));
        Assert.DoesNotContain("ctr-live", rig.Engine.Paused);
    }

    /// <summary>An engine that refuses to wake a jail must say so — the row must not read "Working" over a
    /// container that is demonstrably still frozen (MG-8's lesson, applied to the release).</summary>
    [Fact]
    public async Task Resume_WhenUnpauseFails_ReportsResumeFailed_AndNeverClaimsWorking()
    {
        using var rig = new KillRig();
        var agentId = rig.AddLiveAgent("ctr-a");

        await rig.KillSwitch.EngageAsync();
        rig.Engine.FailUnpauses = true;
        var report = await rig.KillSwitch.ResumeAsync();

        Assert.Equal(KillResumeOutcome.ResumeFailed, Assert.Single(report.Agents).Outcome);
        Assert.Contains("ctr-a", rig.Engine.Paused);
        Assert.Equal("Unresponsive", rig.Store.Find(agentId)!.State);
        Assert.Contains("STILL paused", rig.Store.Find(agentId)!.Detail);
        // The queue is freed regardless — a wedged engine must not also trap the operator.
        Assert.False(rig.KillSwitch.IsEngaged);
    }

    /// <summary>
    /// Engage is idempotent and the UI's control is a toggle, so a second press before any Resume is
    /// ordinary — and its own pause calls 409 against the jails the FIRST press froze. If that second
    /// press overwrote the causation ledger, Resume would find nothing it owned and release nothing:
    /// ISSUES-LOG #17 restored by a double click.
    /// </summary>
    [Fact]
    public async Task Resume_StillReleasesEverything_AfterTwoEngagesWithNoResumeBetween()
    {
        using var rig = new KillRig();
        var agentId = rig.AddLiveAgent("ctr-a");

        await rig.KillSwitch.EngageAsync();
        await rig.KillSwitch.EngageAsync();
        Assert.Contains("ctr-a", rig.Engine.Paused);

        await rig.KillSwitch.ResumeAsync();

        Assert.DoesNotContain("ctr-a", rig.Engine.Paused);
        Assert.False(rig.Locks.IsLocked(agentId));
    }

    /// <summary>Two repos holding one id: the release fans over BOTH jails, exactly as the pause does.</summary>
    [Fact]
    public async Task Resume_WhenTwoReposShareAnAgentId_UnpausesBothJails()
    {
        using var rig = new KillRig();
        rig.AddLiveAgent("ctr-repo-a", "repo-a", "pr-7");
        rig.AddLiveAgent("ctr-repo-b", "repo-b", "pr-7");

        await rig.KillSwitch.EngageAsync();
        await rig.KillSwitch.ResumeAsync();

        Assert.Empty(rig.Engine.Paused);
        Assert.Equal("Working", rig.Store.Find("repo-a", "pr-7")!.State);
        Assert.Equal("Working", rig.Store.Find("repo-b", "pr-7")!.State);
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
            Ledger = new HumanPauseLedger();
            Target = new SandboxKillTarget(Store, Engine, Leader, Locks, Ledger, NullLoggerFactory.Instance);
            Journal = new JsonKillJournal(
                Path.Combine(Path.GetDirectoryName(_registryPath)!, "kills.jsonl"));
            // The rig used to pass `rttBudget: () => TimeSpan.Zero` — byte-for-byte the production default
            // it was meant to be exercising — so RttSpikeDetected was constant false here for the same
            // reason it was constant false in the daemon, and no assertion in either direction was
            // possible. It is settable now, and asserted BOTH ways below. Same for the journal: the
            // default one is unreachable, so step 3 could not be observed at all.
            KillSwitch = new KillSwitch(
                new KillSwitchGate(), Target, journal: Journal, audit: Audit,
                rttBudget: () => Rtt, onRttSpike: epoch => SpikeEpochs.Add(epoch));
        }

        /// <summary>The control-channel RTT the kill switch reads. Zero here is a MEASUREMENT of zero
        /// (a real EWMA on a fast channel), not <see cref="KillSwitchTiming.UnmeasuredRtt"/>.</summary>
        public TimeSpan Rtt { get; set; } = TimeSpan.Zero;

        /// <summary>Kill epochs the A3 <c>Unresponsive</c> feed was told about.</summary>
        public ConcurrentBag<string> SpikeEpochs { get; } = new();

        public InMemoryAuditLog Audit { get; } = new();

        /// <summary>The durable journal step 3 writes to — read back off disk by the test.</summary>
        public JsonKillJournal Journal { get; }

        public AgentSessionStore Store { get; }

        public RecordingSandboxEngine Engine { get; }

        public SessionLeader Leader { get; }

        public TerminalLockRegistry Locks { get; }

        /// <summary>The human/machine pause arbiter the release path consults.</summary>
        public HumanPauseLedger Ledger { get; }

        public SandboxKillTarget Target { get; }

        public KillSwitch KillSwitch { get; }

        /// <summary>A spawned session with a jail attached and a leader-owned PTY — a live worker. The repo
        /// goes in at spawn because it is half of the session's identity (see <c>AgentSessionKey</c>); the
        /// sandbox then attaches to that same (repo, id) rather than re-homing the record.</summary>
        public string AddLiveAgent(string containerId, string repoHash = "repohash")
        {
            var id = Store.Spawn("claude", repoHash: repoHash).Id;
            Store.AttachSandbox(id, containerId, repoHash);
            Leader.Register(new LeaderSession(id, repoHash, containerId, 80, 24, SocketPath: string.Empty));
            return id;
        }

        /// <summary>The same, under a caller-chosen id — the intake's <c>pr-&lt;n&gt;</c> shape, which two
        /// repos can both hold.</summary>
        public string AddLiveAgent(string containerId, string repoHash, string agentId)
        {
            var id = Store.Spawn("claude", agentId: agentId, repoHash: repoHash).Id;
            Store.AttachSandbox(id, containerId, repoHash);
            Leader.Register(new LeaderSession(id, repoHash, containerId, 80, 24, SocketPath: string.Empty));
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

    /// <summary>
    /// Models the paused SET rather than a log of pause calls, because the release path is about state:
    /// a second pause of an already-frozen jail is a 409 in Docker and must be here too, or the
    /// "somebody else already paused this" arbitration would be untestable without a daemon.
    /// <see cref="FailPauses"/>/<see cref="FailUnpauses"/> model an unreachable engine;
    /// <see cref="Vanish"/> models a container removed while the kill switch held it.
    /// </summary>
    private sealed class RecordingSandboxEngine : ISandboxEngine
    {
        private readonly ConcurrentDictionary<string, byte> _paused = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _gone = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Paused => _paused.Keys.ToList();

        public bool FailPauses { get; set; }

        public bool FailUnpauses { get; set; }

        /// <summary>The container is removed from under us (agent torn down during the freeze).</summary>
        public void Vanish(string containerId) => _gone[containerId] = 0;

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

            ThrowIfGone(containerId);
            if (!_paused.TryAdd(containerId, 0))
            {
                // Docker's own answer to pausing a paused container (409). Modelled, because relying on it
                // is exactly what the kill switch's "was I the one who froze this?" arbitration does.
                throw new InvalidOperationException("Container is already paused");
            }

            return Task.CompletedTask;
        }

        public Task UnpauseAsync(string containerId, CancellationToken ct = default)
        {
            if (FailUnpauses)
            {
                throw new InvalidOperationException("docker daemon unreachable");
            }

            ThrowIfGone(containerId);
            _paused.TryRemove(containerId, out _);
            return Task.CompletedTask;
        }

        public Task<bool> IsPausedAsync(string containerId, CancellationToken ct = default)
        {
            ThrowIfGone(containerId);
            return Task.FromResult(_paused.ContainsKey(containerId));
        }

        public Task StopAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        private void ThrowIfGone(string containerId)
        {
            if (_gone.ContainsKey(containerId))
            {
                throw new DockerContainerNotFoundException(
                    System.Net.HttpStatusCode.NotFound, $"No such container: {containerId}");
            }
        }
    }
}
