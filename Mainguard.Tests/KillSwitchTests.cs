using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-14 kill-switch governance (tests 7, 9, 10, 13). Freeze-first ordering (SA-1/F4), the RT-D4
/// hard-ceiling fan-out timing independent of the measured RTT, the journal snapshot, and the RT-D3
/// non-blocking-during-audit-outage + recovery gap marker.
/// </summary>
public class KillSwitchTests
{
    // ---- Test 7 — KillSwitch_FanOutUnder5s (+ journal snapshot + queue frozen) ----

    [Fact]
    public async Task KillSwitch_FanOutUnder5s_AllStopped_QueueFrozen_SnapshotWritten()
    {
        var gate = new KillSwitchGate();
        var journal = new InMemoryKillJournal();
        var audit = new InMemoryAuditLog();
        // 3 agents; "c" ignores the yield (returns false) so it takes the docker-pause fallback.
        var target = new FakeKillTarget(new[] { "a", "b", "c" }, yieldsFor: new[] { "a", "b" });
        var kill = new KillSwitch(gate, target, journal, audit, rttBudget: () => TimeSpan.Zero);

        var sw = Stopwatch.StartNew();
        var report = await kill.EngageAsync();
        sw.Stop();

        // Under the 5 s local profile (virtualized — the fake short-circuits the deadline wait).
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"fan-out took {sw.Elapsed}");

        // Every agent is stopped: a/b yielded, c paused. Nothing left running.
        Assert.Equal(KillAgentOutcome.Yielded, report.Agents.Single(x => x.AgentId == "a").Outcome);
        Assert.Equal(KillAgentOutcome.Yielded, report.Agents.Single(x => x.AgentId == "b").Outcome);
        Assert.Equal(KillAgentOutcome.Paused, report.Agents.Single(x => x.AgentId == "c").Outcome);
        Assert.Contains("c", target.Paused);

        // Queue frozen.
        Assert.True(gate.IsFrozen);
        Assert.True(report.QueueFrozen);

        // Journal snapshot written before returning, with all three agents + the frozen fact.
        var snapshot = Assert.Single(journal.Snapshots);
        Assert.Equal(3, snapshot.Agents.Count);
        Assert.True(snapshot.QueueFrozen);
        Assert.Equal(report.KillEpochId, snapshot.KillEpochId);

        // Audit shows the killswitch event.
        Assert.Contains("killswitch", audit.Read().Select(e => e.Type));
    }

    // ---- Test 9 — KillSwitchBound_HardCeiling_IndependentOfRtt (PR-BLOCKING, M7 exit) ----

    [Fact]
    public async Task KillSwitchBound_HardCeiling_IndependentOfRtt()
    {
        // A hostile/anomalous RTT EWMA: 50 × 10 s = 500 s — must NOT stretch the emergency stop.
        var inflatedRtt = TimeSpan.FromSeconds(10);

        // The formula clamps at the fixed, compile-time-visible ceiling — independent of the RTT.
        Assert.Equal(KillSwitchTiming.Ceiling, KillSwitchTiming.FanOutDeadline(inflatedRtt));
        Assert.True(KillSwitchTiming.RttWouldExceedCeiling(inflatedRtt));
        // The ceiling is not a function of the measured RTT — an even larger RTT gives the same ceiling.
        Assert.Equal(KillSwitchTiming.Ceiling, KillSwitchTiming.FanOutDeadline(TimeSpan.FromHours(1)));

        var gate = new KillSwitchGate();
        var spikeEpochs = new List<string>();
        var target = new FakeKillTarget(new[] { "a" }, yieldsFor: new[] { "a" });
        var kill = new KillSwitch(gate, target, rttBudget: () => inflatedRtt,
            onRttSpike: epoch => spikeEpochs.Add(epoch));

        var report = await kill.EngageAsync();

        // The effective deadline is clamped at the ceiling, not 50×RTT.
        Assert.Equal(KillSwitchTiming.Ceiling, report.Deadline);
        Assert.True(report.RttSpikeDetected);
        // The RTT spike fed the A3 Unresponsive signal (rather than only a longer deadline).
        Assert.Contains(report.KillEpochId, spikeEpochs);
    }

    // ---- Test 10 — KillSwitch_FreezesQueueBeforeFanOut (OPS SA-1/F4) ----

    [Fact]
    public async Task KillSwitch_FreezesQueueBeforeFanOut()
    {
        var gate = new KillSwitchGate();
        var fanOutStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The target blocks inside the fan-out so we can inspect the timeline while it is in progress.
        var target = new GatedKillTarget(new[] { "a" }, onYieldStart: () => fanOutStarted.TrySetResult(), release: release.Task);
        var kill = new KillSwitch(gate, target, rttBudget: () => TimeSpan.Zero);

        var killTask = kill.EngageAsync();

        // Wait until the fan-out has actually started — we are now inside the up-to-ceiling window.
        await fanOutStarted.Task;

        // The queue was frozen FIRST: a BeginMerge issued in the fan-out window is refused.
        Assert.True(gate.IsFrozen);
        Assert.Throws<QueueFrozenException>(() => gate.ThrowIfFrozen("BeginMerge"));

        // Let the fan-out complete.
        release.TrySetResult();
        var report = await killTask;
        Assert.True(report.QueueFrozen);
    }

    // ---- Test 13 — KillSwitchDuringAuditOutage_ShouldMarkGapOnRecovery (RT-D3) ----

    [Fact]
    public async Task KillSwitchDuringAuditOutage_ShouldMarkGapOnRecovery()
    {
        var gate = new KillSwitchGate();
        var audit = new FaultableAuditLog { Down = true };
        var target = new FakeKillTarget(new[] { "a" }, yieldsFor: new[] { "a" });
        var kill = new KillSwitch(gate, target, audit: audit, rttBudget: () => TimeSpan.Zero);

        // The kill fires while the audit store is DOWN — it must NOT block or fail.
        var report = await kill.EngageAsync();
        Assert.True(report.QueueFrozen);

        // No killswitch event landed (store was down) — the absence is about to be made tamper-evident.
        Assert.DoesNotContain("killswitch", audit.Read().Select(e => e.Type));

        // Store recovers → a chained killswitch_audit_gap marks the kill that fired during the outage.
        audit.Down = false;
        kill.NotifyAuditStoreRecovered();

        var gap = Assert.Single(audit.Read(), e => e.Type == "killswitch_audit_gap");
        Assert.Equal(report.KillEpochId, gap.Fields["kill_epoch_id"]);
        Assert.True(gap.Fields.ContainsKey("observed_at"));
    }

    // ---- MG-39(a) — a cooperative ACK must not buy an exemption from docker pause ----

    /// <summary>
    /// The <c>[IPC_UPDATE_READY]</c> ACK is written by the worker's own wrapper INSIDE the jail — by exactly
    /// the party the kill switch exists to stop. Treating it as "already stopped, no pause needed" was a
    /// one-line self-exemption: answer ready, skip the freezer cgroup, keep executing. The pause is now
    /// unconditional; the ACK only records a cleaner stopping point.
    /// </summary>
    [Fact]
    public async Task KillSwitch_AgentThatFakesTheYieldAck_IsStillDockerPaused()
    {
        var gate = new KillSwitchGate();
        // Every agent "cooperates" — the exact shape a hostile worker would forge.
        var target = new FakeKillTarget(new[] { "a", "b" }, yieldsFor: new[] { "a", "b" });
        var kill = new KillSwitch(gate, target, rttBudget: () => TimeSpan.Zero);

        var report = await kill.EngageAsync();

        // Containment is unconditional: BOTH jails were paused despite both ACKing.
        Assert.Contains("a", target.Paused);
        Assert.Contains("b", target.Paused);
        // The ACK is still reported (a nicer stopping point), it just never gated the pause.
        Assert.All(report.Agents, a => Assert.Equal(KillAgentOutcome.Yielded, a.Outcome));
    }

    /// <summary>A jail that ACKed but could NOT be frozen is not contained — the report must say so.</summary>
    [Fact]
    public async Task KillSwitch_YieldAckedButPauseFails_ReportsPauseFailed()
    {
        var gate = new KillSwitchGate();
        var target = new FakeKillTarget(new[] { "a" }, yieldsFor: new[] { "a" }) { FailPauses = true };
        var kill = new KillSwitch(gate, target, rttBudget: () => TimeSpan.Zero);

        var report = await kill.EngageAsync();

        Assert.Equal(KillAgentOutcome.PauseFailed, report.Agents.Single().Outcome);
        Assert.True(report.QueueFrozen); // the kill still completes
    }

    // ---- ISSUES-LOG #17 — Resume must actually reverse the kill, not just clear a flag ----

    /// <summary>
    /// The bug, stated as a test: Engage <c>docker pause</c>d every jail and Resume un-paused none of them,
    /// because <c>Resume()</c> was <c>_gate.Resume()</c> and <see cref="IKillTarget"/> had no release member
    /// at all. The emergency stop was one-way — the app's own Resource Monitor called the state
    /// "(recoverable)" while only a raw <c>docker unpause</c> from outside the app could recover it.
    /// </summary>
    [Fact]
    public async Task Resume_UnpausesEveryAgentTheKillPaused_NotJustTheQueueFlag()
    {
        var gate = new KillSwitchGate();
        var target = new FakeKillTarget(new[] { "a", "b", "c" }, yieldsFor: new[] { "a" });
        var kill = new KillSwitch(gate, target, rttBudget: () => TimeSpan.Zero);

        await kill.EngageAsync();
        Assert.Equal(new[] { "a", "b", "c" }, target.Paused.OrderBy(x => x, StringComparer.Ordinal));

        var report = await kill.ResumeAsync();

        // The half that did not exist: every paused jail released.
        Assert.Equal(new[] { "a", "b", "c" }, target.Unpaused.OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(report.Agents, a => Assert.Equal(KillResumeOutcome.Resumed, a.Outcome));

        // ...and the half that always worked, still working.
        Assert.False(gate.IsFrozen);
        Assert.False(report.QueueFrozen);
        Assert.True(kill.IsEngaged == false);
    }

    /// <summary>Resume is idempotent: a second press must not re-issue an unpause at an agent that is
    /// already running (nor throw), the same way Engage's freeze is idempotent.</summary>
    [Fact]
    public async Task Resume_Twice_ReleasesOnce()
    {
        var gate = new KillSwitchGate();
        var target = new FakeKillTarget(new[] { "a" }, yieldsFor: Array.Empty<string>());
        var kill = new KillSwitch(gate, target, rttBudget: () => TimeSpan.Zero);

        await kill.EngageAsync();
        await kill.ResumeAsync();
        var second = await kill.ResumeAsync();

        Assert.Equal(new[] { "a" }, target.Unpaused);
        Assert.Empty(second.Agents);
        Assert.False(gate.IsFrozen);
    }

    /// <summary>
    /// A jail the engine refuses to wake must (a) never be reported as resumed, (b) never abandon the rest
    /// of the fan-out, (c) never leave the operator stuck behind a frozen queue with no way out, and
    /// (d) stay in the ledger so pressing Resume again retries exactly it.
    /// </summary>
    [Fact]
    public async Task Resume_WhenUnpauseFails_ClearsTheFreezeAnyway_AndRetriesThatAgentOnTheNextPress()
    {
        var gate = new KillSwitchGate();
        var target = new FakeKillTarget(new[] { "a" }, yieldsFor: Array.Empty<string>());
        var kill = new KillSwitch(gate, target, rttBudget: () => TimeSpan.Zero);

        await kill.EngageAsync();
        target.FailUnpauses = true;
        var failedRun = await kill.ResumeAsync();

        Assert.Equal(KillResumeOutcome.ResumeFailed, Assert.Single(failedRun.Agents).Outcome);
        Assert.Empty(target.Unpaused);
        // The queue is NOT held hostage by an engine that would not co-operate.
        Assert.False(gate.IsFrozen);

        // The engine comes back; the operator presses Resume again and the jail this time is released.
        target.FailUnpauses = false;
        var retry = await kill.ResumeAsync();
        Assert.Equal(KillResumeOutcome.Resumed, Assert.Single(retry.Agents).Outcome);
        Assert.Equal(new[] { "a" }, target.Unpaused);
    }

    /// <summary>The release is audited like the stop is — an emergency stop's END is as much of an
    /// operational fact as its beginning, and RT-D3's "never block on the audit store" applies to both.</summary>
    [Fact]
    public async Task Resume_IsAudited_AndNeverBlocksOnTheAuditStore()
    {
        var gate = new KillSwitchGate();
        var audit = new FaultableAuditLog();
        var target = new FakeKillTarget(new[] { "a" }, yieldsFor: Array.Empty<string>());
        var kill = new KillSwitch(gate, target, audit: audit, rttBudget: () => TimeSpan.Zero);

        await kill.EngageAsync();
        audit.Down = true;
        var duringOutage = await kill.ResumeAsync();
        // The store being down did not stop the jail from being released.
        Assert.Equal(new[] { "a" }, target.Unpaused);
        Assert.Equal(KillResumeOutcome.Resumed, Assert.Single(duringOutage.Agents).Outcome);

        audit.Down = false;
        await kill.EngageAsync();
        await kill.ResumeAsync();
        var resume = Assert.Single(audit.Read(), e => e.Type == "killswitch_resume");
        Assert.Equal("1", resume.Fields["resumed"]);
        Assert.Equal("0", resume.Fields["resume_failed"]);
    }

    // ---- fakes ----

    private sealed class FakeKillTarget : IKillTarget
    {
        private readonly HashSet<string> _yields;
        public ConcurrentBag<string> Paused { get; } = new();

        /// <summary>Agents this fake released, in the order Resume asked for them.</summary>
        public ConcurrentBag<string> Unpaused { get; } = new();

        public FakeKillTarget(IReadOnlyList<string> ids, IReadOnlyList<string> yieldsFor)
        {
            ActiveAgentIds = ids;
            _yields = new HashSet<string>(yieldsFor, StringComparer.Ordinal);
        }

        public IReadOnlyList<string> ActiveAgentIds { get; }

        /// <summary>Models a jail the engine could not freeze (MG-39(a) honesty path).</summary>
        public bool FailPauses { get; init; }

        /// <summary>Models a jail the engine could not wake — the release-side honesty path.</summary>
        public bool FailUnpauses { get; set; }

        // The fake short-circuits the deadline wait (virtualized timing): an ignoring agent returns false
        // immediately rather than consuming the wall-clock deadline.
        public Task<bool> RequestYieldAsync(string agentId, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(_yields.Contains(agentId));

        public Task PauseAsync(string agentId, CancellationToken ct)
        {
            if (FailPauses)
            {
                throw new InvalidOperationException("docker daemon unreachable");
            }

            Paused.Add(agentId);
            return Task.CompletedTask;
        }

        public Task UnpauseAsync(string agentId, CancellationToken ct)
        {
            if (FailUnpauses)
            {
                throw new InvalidOperationException("docker daemon unreachable");
            }

            Unpaused.Add(agentId);
            return Task.CompletedTask;
        }

        public IReadOnlyDictionary<string, string> CaptureStates() =>
            ActiveAgentIds.ToDictionary(id => id, _ => "Stopped", StringComparer.Ordinal);
    }

    private sealed class GatedKillTarget : IKillTarget
    {
        private readonly Action _onYieldStart;
        private readonly Task _release;

        public GatedKillTarget(IReadOnlyList<string> ids, Action onYieldStart, Task release)
        {
            ActiveAgentIds = ids;
            _onYieldStart = onYieldStart;
            _release = release;
        }

        public IReadOnlyList<string> ActiveAgentIds { get; }

        public async Task<bool> RequestYieldAsync(string agentId, TimeSpan timeout, CancellationToken ct)
        {
            _onYieldStart();
            await _release;
            return true;
        }

        public Task PauseAsync(string agentId, CancellationToken ct) => Task.CompletedTask;

        public Task UnpauseAsync(string agentId, CancellationToken ct) => Task.CompletedTask;

        public IReadOnlyDictionary<string, string> CaptureStates() =>
            ActiveAgentIds.ToDictionary(id => id, _ => "Yielded", StringComparer.Ordinal);
    }

    /// <summary>An audit log that throws on Append while <see cref="Down"/> — models a store outage (RT-D3).</summary>
    private sealed class FaultableAuditLog : IAuditLog
    {
        private readonly List<AuditEvent> _events = new();
        private readonly object _gate = new();
        public bool Down { get; set; }

        public void Append(AuditEvent auditEvent)
        {
            if (Down)
            {
                throw new IOException("audit store unavailable");
            }

            lock (_gate)
            {
                _events.Add(auditEvent);
            }
        }

        public IReadOnlyList<AuditEvent> Read()
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }
}
