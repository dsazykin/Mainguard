using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Audit;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// RT-D4 safety-timeout constants for the kill switch. The <see cref="Ceiling"/> is a
/// <b>compile-time-visible constant, independent of the measured <c>RttBudget</c> EWMA</b>: the OOB
/// channel's RTT terminates at the untrusted supervisor, so a supervisor-influenced RTT must never
/// stretch the emergency stop. <c>docker pause</c> needs no supervisor cooperation, so the ceiling bounds
/// only <i>how long</i> an agent runs during a kill, not correctness. An RTT that would blow the ceiling
/// feeds the P2-08 A3 <c>Unresponsive</c> signal instead of a longer deadline.
/// </summary>
public static class KillSwitchTiming
{
    /// <summary>The absolute fan-out ceiling. RTT-INDEPENDENT by construction (RT-D4 rejection trigger otherwise).</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    /// <summary>The local-profile floor: the "&lt; 5 s" figure in the master doc is this floor.</summary>
    public static readonly TimeSpan FanOutFloor = TimeSpan.FromSeconds(5);

    /// <summary>The RTT multiplier <c>k</c> in <c>min(ceiling, max(floor, k×RTT))</c>.</summary>
    public const int RttMultiplier = 50;

    /// <summary>The effective fan-out deadline: <c>min(ceiling, max(floor, 50×RTT))</c>.</summary>
    public static TimeSpan FanOutDeadline(TimeSpan rttBudget)
    {
        var scaled = ScaleByRtt(rttBudget);
        var floored = scaled > FanOutFloor ? scaled : FanOutFloor;
        // The ceiling clamp is what denies a supervisor-pumped RTT the ability to stretch the stop.
        return floored < Ceiling ? floored : Ceiling;
    }

    /// <summary>True when <c>50×RTT</c> would have exceeded the ceiling — the A3 <c>Unresponsive</c> trigger.</summary>
    public static bool RttWouldExceedCeiling(TimeSpan rttBudget) => ScaleByRtt(rttBudget) > Ceiling;

    private static TimeSpan ScaleByRtt(TimeSpan rttBudget)
    {
        if (rttBudget <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        // Guard against overflow at absurd EWMA values (a hostile supervisor could report anything).
        try
        {
            return TimeSpan.FromTicks(checked(rttBudget.Ticks * RttMultiplier));
        }
        catch (OverflowException)
        {
            return TimeSpan.MaxValue;
        }
    }
}

/// <summary>
/// The in-proc merge/spawn freeze the kill switch flips <b>first</b> (SA-1/F4). Setting it is synchronous
/// and instant; <c>BeginMerge</c>/<c>ConfirmMerge</c>/spawn consult it and refuse
/// (<see cref="QueueFrozenException"/> / gRPC <c>FAILED_PRECONDITION</c>) while frozen, so no merge slips
/// through the up-to-ceiling fan-out window.
/// </summary>
public sealed class KillSwitchGate
{
    private volatile bool _frozen;

    /// <summary>True while the kill switch holds the queue frozen.</summary>
    public bool IsFrozen => _frozen;

    /// <summary>Freeze the queue + spawn path (instant, in-proc). Idempotent.</summary>
    public void Freeze() => _frozen = true;

    /// <summary>Resume after a kill (the banner's one action). Idempotent.</summary>
    public void Resume() => _frozen = false;

    /// <summary>Throws <see cref="QueueFrozenException"/> when frozen — the guard for merge/spawn RPCs.</summary>
    public void ThrowIfFrozen(string operation)
    {
        if (_frozen)
        {
            throw new QueueFrozenException(operation);
        }
    }
}

/// <summary>Thrown by a merge/spawn path attempted while the kill switch holds the queue frozen (SA-1/F4).</summary>
public sealed class QueueFrozenException : InvalidOperationException
{
    public QueueFrozenException(string operation)
        : base($"The merge queue is frozen (kill switch engaged) — {operation} is refused. Resume first.")
    {
        Operation = operation;
    }

    public string Operation { get; }
}

/// <summary>The fan-out target: the live agent set, the yield request, the pause fallback, the snapshot source.</summary>
public interface IKillTarget
{
    /// <summary>The agents currently in scope for the kill (live workers).</summary>
    IReadOnlyList<string> ActiveAgentIds { get; }

    /// <summary>Requests a cooperative yield within <paramref name="timeout"/>; true if the agent yielded in time.
    /// The answer is a courtesy only — it is authored inside the jail and NEVER skips <see cref="PauseAsync"/>.</summary>
    Task<bool> RequestYieldAsync(string agentId, TimeSpan timeout, CancellationToken ct);

    /// <summary>Contain the agent unconditionally: sever its terminal input and <c>docker pause</c> its jail
    /// (neither needs supervisor cooperation). Called for EVERY agent in the fan-out — MG-39(a). Throwing
    /// means "containment unconfirmed" and is reported as <see cref="KillAgentOutcome.PauseFailed"/>.</summary>
    Task PauseAsync(string agentId, CancellationToken ct);

    /// <summary>A point-in-time state word per agent, for the journal snapshot.</summary>
    IReadOnlyDictionary<string, string> CaptureStates();
}

/// <summary>The journal sink the kill switch writes its snapshot to before returning (step 3).</summary>
public interface IKillJournal
{
    void WriteSnapshot(KillSnapshot snapshot);
}

/// <summary>An in-memory <see cref="IKillJournal"/> for tests / the pre-persistence path.</summary>
public sealed class InMemoryKillJournal : IKillJournal
{
    private readonly List<KillSnapshot> _snapshots = new();
    private readonly object _gate = new();

    public IReadOnlyList<KillSnapshot> Snapshots
    {
        get { lock (_gate) return _snapshots.ToList(); }
    }

    public void WriteSnapshot(KillSnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshots.Add(snapshot);
        }
    }
}

/// <summary>
/// How one agent ended the fan-out. <b>Every</b> non-<see cref="PauseFailed"/> outcome means the jail was
/// force-paused: <see cref="Yielded"/> only records that the agent <i>also</i> acknowledged the cooperative
/// request first (a nicer stopping point for the later resume), never that the pause was skipped — see
/// MG-39(a) in <see cref="KillSwitch.FanOutOneAsync"/>.
/// </summary>
public enum KillAgentOutcome { Yielded, Paused, PauseFailed }

/// <summary>One agent's line in the kill snapshot.</summary>
public sealed record KillAgentState(string AgentId, string State, KillAgentOutcome Outcome);

/// <summary>The journal snapshot written before the kill returns: agent list + states + queue-frozen fact.</summary>
public sealed record KillSnapshot(
    string KillEpochId,
    DateTimeOffset At,
    IReadOnlyList<KillAgentState> Agents,
    bool QueueFrozen);

/// <summary>The kill outcome the caller (and tests) assert against.</summary>
public sealed record KillReport(
    string KillEpochId,
    DateTimeOffset FreezeAt,
    TimeSpan Deadline,
    bool RttSpikeDetected,
    IReadOnlyList<KillAgentState> Agents,
    bool QueueFrozen);

/// <summary>
/// P2-14 kill switch (contract §2, SA-1/F4 + RT-D4 + RT-D3). One always-visible emergency stop.
///
/// <para>Ordering is binding: step 1 = <b>freeze the queue in-proc, instantly</b> (before any await), so no
/// merge slips the fan-out window; step 2 = yield-all fan-out with the RT-D4 deadline, then an
/// <b>unconditional</b> <c>docker pause</c> + terminal-input sever per agent (MG-39(a): the cooperative
/// ACK comes from inside the jail, so it may never buy an exemption from containment); step 3 = journal
/// snapshot written before returning. Audit is best-effort
/// (freeze-then-audit, RT-D3) — a kill NEVER blocks on audit-store availability; on store recovery
/// <see cref="NotifyAuditStoreRecovered"/> appends the chained <c>killswitch_audit_gap</c> so the carve-out
/// is tamper-evident rather than silent.</para>
/// </summary>
public sealed class KillSwitch
{
    private readonly KillSwitchGate _gate;
    private readonly IKillTarget _target;
    private readonly IKillJournal _journal;
    private readonly IAuditLog _audit;
    private readonly Func<TimeSpan> _rttBudget;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _onRttSpike;
    private readonly object _pendingGate = new();
    private readonly List<(string Epoch, DateTimeOffset At)> _pendingAuditGaps = new();

    /// <param name="gate">The shared in-proc freeze flag merge/spawn paths consult.</param>
    /// <param name="target">The fan-out target (agents + yield + pause + state capture).</param>
    /// <param name="journal">The snapshot sink (written before returning).</param>
    /// <param name="audit">The audit sink (best-effort during the kill — RT-D3).</param>
    /// <param name="rttBudget">The measured control-channel RTT EWMA (never trusted to stretch the stop).</param>
    /// <param name="clock">Injectable clock.</param>
    /// <param name="onRttSpike">Invoked when the RTT would blow the ceiling → feeds P2-08 A3 <c>Unresponsive</c>.</param>
    public KillSwitch(
        KillSwitchGate gate,
        IKillTarget target,
        IKillJournal? journal = null,
        IAuditLog? audit = null,
        Func<TimeSpan>? rttBudget = null,
        Func<DateTimeOffset>? clock = null,
        Action<string>? onRttSpike = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _journal = journal ?? new InMemoryKillJournal();
        _audit = audit ?? new InMemoryAuditLog();
        _rttBudget = rttBudget ?? (() => TimeSpan.Zero);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _onRttSpike = onRttSpike;
    }

    /// <summary>True while the queue is frozen (the kill switch is engaged).</summary>
    public bool IsEngaged => _gate.IsFrozen;

    /// <summary>
    /// Engages the kill switch. FREEZE happens synchronously before the first await (SA-1/F4), so any
    /// <c>BeginMerge</c>/spawn concurrent with the fan-out already sees the frozen gate.
    /// </summary>
    public async Task<KillReport> EngageAsync(CancellationToken ct = default)
    {
        // ---- Step 1: FREEZE FIRST (synchronous, before any await) ----
        _gate.Freeze();
        var freezeAt = _clock();
        var epochId = Guid.NewGuid().ToString("N");

        // ---- RT-D4: the deadline clamps at the fixed ceiling regardless of the measured RTT ----
        var rtt = _rttBudget();
        var deadline = KillSwitchTiming.FanOutDeadline(rtt);
        var rttSpike = KillSwitchTiming.RttWouldExceedCeiling(rtt);
        if (rttSpike)
        {
            // An anomalous RTT feeds A3 Unresponsive rather than only a longer deadline.
            _onRttSpike?.Invoke(epochId);
        }

        // ---- Step 2: yield-all fan-out (timeout → docker pause) ----
        var agentIds = _target.ActiveAgentIds.ToList();
        var results = await Task.WhenAll(agentIds.Select(id => FanOutOneAsync(id, deadline, ct))).ConfigureAwait(false);

        // ---- Step 3: journal snapshot written BEFORE returning ----
        var states = _target.CaptureStates();
        var agentStates = results
            .Select(r => new KillAgentState(r.AgentId, states.TryGetValue(r.AgentId, out var s) ? s : "Unknown", r.Outcome))
            .OrderBy(a => a.AgentId, StringComparer.Ordinal)
            .ToList();

        var snapshot = new KillSnapshot(epochId, _clock(), agentStates, QueueFrozen: _gate.IsFrozen);
        _journal.WriteSnapshot(snapshot);

        // ---- RT-D3: audit best-effort — NEVER blocks the kill on audit-store availability ----
        TryAuditKill(epochId, agentStates.Count);

        return new KillReport(epochId, freezeAt, deadline, rttSpike, agentStates, QueueFrozen: _gate.IsFrozen);
    }

    /// <summary>Resumes after a kill (the banner action). Clears the freeze; agents are unpaused by their tokens.</summary>
    public void Resume() => _gate.Resume();

    /// <summary>
    /// RT-D3: on audit-store recovery, append a chained <c>killswitch_audit_gap{killEpochId, observedAt}</c>
    /// for every kill that fired while the store was down — making the freeze-then-audit carve-out
    /// tamper-evident rather than a silent absence.
    /// </summary>
    public void NotifyAuditStoreRecovered()
    {
        List<(string Epoch, DateTimeOffset At)> gaps;
        lock (_pendingGate)
        {
            gaps = _pendingAuditGaps.ToList();
            _pendingAuditGaps.Clear();
        }

        foreach (var (epoch, at) in gaps)
        {
            _audit.Append(new AuditEvent("killswitch_audit_gap", new Dictionary<string, string>
            {
                ["kill_epoch_id"] = epoch,
                ["observed_at"] = at.ToString("O"),
            }));
        }
    }

    private async Task<KillAgentState> FanOutOneAsync(string agentId, TimeSpan deadline, CancellationToken ct)
    {
        var yielded = false;
        try
        {
            yielded = await _target.RequestYieldAsync(agentId, deadline, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A yield failure is not fatal to the kill — the unconditional pause below still runs.
        }

        // MG-39(a): the pause is UNCONDITIONAL — it is NOT a timeout fallback.
        //
        // The cooperative ACK ([IPC_UPDATE_READY]) is authored by the worker's own wrapper inside the
        // jail, i.e. by exactly the party a kill switch exists to stop. Treating that ACK as "already
        // stopped, no pause needed" handed the target a one-line self-exemption: answer ready, skip
        // `docker pause`, keep executing. The freezer cgroup needs no cooperation at all, so paying for
        // it on every agent costs nothing but closes the bypass. A cooperative ACK now only buys a
        // cleaner stopping point (recorded as Yielded), never an exemption from containment.
        try
        {
            // The RT-D4 ceiling must bound the WHOLE fan-out, not just the yield window: an unreachable
            // or wedged container engine must not stretch the emergency stop past the ceiling. A pause
            // that blows the deadline is cancelled and reported as PauseFailed — the honest word for
            // "containment unconfirmed" — rather than silently holding the kill open.
            using var pauseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pauseCts.CancelAfter(deadline);
            await _target.PauseAsync(agentId, pauseCts.Token).ConfigureAwait(false);

            return yielded
                ? new KillAgentState(agentId, "Yielded", KillAgentOutcome.Yielded)
                : new KillAgentState(agentId, "Paused", KillAgentOutcome.Paused);
        }
        catch (Exception)
        {
            // Even a pause failure is recorded, not thrown — the kill must always complete + snapshot.
            // Note this outranks a cooperative ACK: an agent that said "ready" but whose jail could not
            // be frozen is NOT contained, and the report must not claim otherwise.
            return new KillAgentState(agentId, "PauseFailed", KillAgentOutcome.PauseFailed);
        }
    }

    private void TryAuditKill(string epochId, int agentCount)
    {
        try
        {
            _audit.Append(new AuditEvent("killswitch", new Dictionary<string, string>
            {
                ["kill_epoch_id"] = epochId,
                ["agents"] = agentCount.ToString(),
            }));
        }
        catch (Exception)
        {
            // RT-D3: the audit store is down — record the gap, DO NOT block or fail the kill.
            lock (_pendingGate)
            {
                _pendingAuditGaps.Add((epochId, _clock()));
            }
        }
    }
}
