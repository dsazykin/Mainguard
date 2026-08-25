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

    /// <summary>
    /// The RTT budget of a kill switch that has <b>no control-channel measurement at all</b> — named,
    /// so that "we never measured it" stops being spelled the same way as "we measured a healthy channel".
    ///
    /// <para><b>This is the daemon's real posture today and it is a statement of fact, not a default.</b>
    /// The RTT this class is about terminates at the in-jail supervisor, and P2-09's
    /// <c>IAgentControlChannel</c> has no production transport: <c>SandboxKillTarget.RequestYieldAsync</c>
    /// answers <c>false</c> immediately without a round trip, so there is nothing to time. Feeding
    /// <see cref="TimeSpan.Zero"/> silently made <see cref="RttWouldExceedCeiling"/> a constant
    /// <c>false</c>, which reads on a <see cref="KillReport"/> as "the control channel was fine" — the one
    /// claim nobody was in a position to make. A kill switch built with this reports
    /// <see cref="KillSwitch.MeasuresControlChannelRtt"/> = false and stamps
    /// <c>KillReport.RttMeasured</c>/<c>KillSnapshot.RttMeasured</c> = false, so the emergency-stop record
    /// says <i>unknown</i> instead of <i>fine</i>.</para>
    ///
    /// <para>The RT-D4 arm itself is complete and covered in both directions by <c>KillSwitchTests</c>; it
    /// lights up the moment a transport supplies a real EWMA to pass here instead of this.</para>
    /// </summary>
    public static readonly Func<TimeSpan> UnmeasuredRtt = () => TimeSpan.Zero;

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

    /// <summary>
    /// Releases <b>exactly and only the containment this target itself applied</b> in the kill epoch —
    /// the mirror of <see cref="PauseAsync"/>, and the half that did not exist until ISSUES-LOG #17.
    ///
    /// <para><b>Asymmetric on purpose.</b> The pause is unconditional (MG-39(a)) because containment must
    /// never be negotiable; the release is <i>conditional</i> because waking something the kill switch did
    /// not put to sleep is its own safety failure. A jail already frozen by a human pause or by the
    /// keep-alive rebase's machine hold when the stop fired must still be frozen after Resume — the
    /// implementation records what it actually transitioned and reverses that, rather than blanket-calling
    /// <c>docker unpause</c>. Same rule for the terminal-input sever: a worker whose terminal was locked at
    /// spawn (a role property) keeps its lock; only a lock the kill switch itself took is released.</para>
    ///
    /// <para>A target whose container no longer exists (agent torn down during the freeze) must return
    /// normally — there is nothing left to release, and one dead agent must not abandon the rest of the
    /// fan-out. Throwing means "release unconfirmed, the jail may still be frozen" and is reported as
    /// <see cref="KillResumeOutcome.ResumeFailed"/>.</para>
    /// </summary>
    Task UnpauseAsync(string agentId, CancellationToken ct);

    /// <summary>A point-in-time state word per agent, for the journal snapshot.</summary>
    IReadOnlyDictionary<string, string> CaptureStates();
}

/// <summary>The journal sink the kill switch writes its snapshot to before returning (step 3).</summary>
public interface IKillJournal
{
    void WriteSnapshot(KillSnapshot snapshot);

    /// <summary>Every snapshot this journal holds, oldest first — the record an operator reads AFTER the
    /// stop. Part of the interface rather than of one implementation: a journal that can only be written
    /// to is the defect (see <see cref="JsonKillJournal"/>), not a design choice.</summary>
    IReadOnlyList<KillSnapshot> ReadAll();
}

/// <summary>An in-memory <see cref="IKillJournal"/> for tests. <b>Not a production sink</b> — a kill
/// snapshot that lives in the killed process's heap is gone by the time anyone comes to read it.</summary>
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

    public IReadOnlyList<KillSnapshot> ReadAll() => Snapshots;
}

/// <summary>
/// The durable <see cref="IKillJournal"/>: one JSON object per kill epoch, appended to a daemon-owned
/// file (same posture as <c>JsonPlanApprovalStore</c> next to the session token).
///
/// <para><b>Why this type had to exist.</b> The kill switch's step 3 is "journal snapshot written BEFORE
/// returning" and the daemon passed no journal at all, so the constructor's <c>?? new
/// InMemoryKillJournal()</c> built a sink nothing held a reference to. The snapshot was written, correctly
/// and on time, into an object that became garbage on the next collection — and the whole point of writing
/// it before returning is that it survives what happens next. After an emergency stop and a daemon restart
/// there was no record of which agents the kill epoch covered or what state each was in.</para>
///
/// <para>Append-only JSON Lines, so a torn write costs the last line rather than the file, and
/// <see cref="ReadAll"/> skips lines it cannot parse for the same reason. Every failure is swallowed:
/// RT-D3's rule that a kill never blocks on a store applies to this store too — an unwritable journal must
/// not keep an agent running.</para>
/// </summary>
public sealed class JsonKillJournal : IKillJournal
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly object _gate = new();

    /// <param name="path">The JSONL file kill snapshots are appended to.</param>
    public JsonKillJournal(string path)
        => _path = string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("path is required.", nameof(path)) : path;

    /// <summary>The file this journal appends to (surfaced so an operator can be told where to look).</summary>
    public string Path => _path;

    public void WriteSnapshot(KillSnapshot snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                var dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }

                System.IO.File.AppendAllText(
                    _path, System.Text.Json.JsonSerializer.Serialize(snapshot, Json) + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // RT-D3 posture: a kill NEVER blocks or fails on a store being unavailable.
        }
    }

    public IReadOnlyList<KillSnapshot> ReadAll()
    {
        try
        {
            lock (_gate)
            {
                if (!System.IO.File.Exists(_path))
                {
                    return Array.Empty<KillSnapshot>();
                }

                var snapshots = new List<KillSnapshot>();
                foreach (var line in System.IO.File.ReadAllLines(_path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var snapshot = System.Text.Json.JsonSerializer.Deserialize<KillSnapshot>(line, Json);
                        if (snapshot is not null)
                        {
                            snapshots.Add(snapshot);
                        }
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // A torn final line from a process that died mid-append. Skip it; the rest is intact.
                    }
                }

                return snapshots;
            }
        }
        catch (Exception)
        {
            return Array.Empty<KillSnapshot>();
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

/// <summary>
/// How one agent ended the RESUME fan-out. <see cref="Resumed"/> means the target reversed the containment
/// it had applied — including the honest no-op case where the kill switch had nothing of its own to
/// reverse for that agent (already human-paused when the stop fired, or torn down during the freeze).
/// <see cref="ResumeFailed"/> is the only word that means "this jail may still be frozen".
/// </summary>
public enum KillResumeOutcome { Resumed, ResumeFailed }

/// <summary>One agent's line in the resume report.</summary>
public sealed record KillAgentResume(string AgentId, KillResumeOutcome Outcome);

/// <summary>
/// The outcome of <see cref="KillSwitch.ResumeAsync"/> — the emergency stop's release, reported per agent.
/// </summary>
/// <param name="KillEpochId">The kill epoch this resume released, or null if nothing was engaged.</param>
/// <param name="QueueFrozen">The gate AFTER the resume. Always false: the freeze is cleared whatever the
/// unpause fan-out did, so a jail that refuses to wake can never also trap the operator's queue.</param>
public sealed record KillResumeReport(
    string? KillEpochId,
    DateTimeOffset At,
    IReadOnlyList<KillAgentResume> Agents,
    bool QueueFrozen);

/// <summary>The journal snapshot written before the kill returns: agent list + states + queue-frozen fact.</summary>
/// <param name="RttMeasured">Whether a control-channel RTT was actually measured for this epoch. False
/// means <see cref="RttSpikeDetected"/>-style reasoning does not apply to this record at all — see
/// <see cref="KillSwitchTiming.UnmeasuredRtt"/>.</param>
public sealed record KillSnapshot(
    string KillEpochId,
    DateTimeOffset At,
    IReadOnlyList<KillAgentState> Agents,
    bool QueueFrozen,
    bool RttMeasured = false);

/// <summary>The kill outcome the caller (and tests) assert against.</summary>
/// <param name="RttSpikeDetected">True when <c>50×RTT</c> would have blown the RT-D4 ceiling. Only
/// meaningful when <paramref name="RttMeasured"/> is true — with no measurement it is <c>false</c> because
/// nothing was observed, NOT because the channel was healthy.</param>
/// <param name="RttMeasured">Whether the kill switch had a real control-channel RTT to reason about.</param>
public sealed record KillReport(
    string KillEpochId,
    DateTimeOffset FreezeAt,
    TimeSpan Deadline,
    bool RttSpikeDetected,
    IReadOnlyList<KillAgentState> Agents,
    bool QueueFrozen,
    bool RttMeasured = false);

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
///
/// <para><b>And it is reversible</b> (<see cref="ResumeAsync"/>): the epoch's fan-out set is remembered, so
/// the release un-pauses exactly the jails this kill switch froze and nothing else. Until ISSUES-LOG #17
/// there was no unpause path at all and every killed agent stayed frozen for the life of the daemon.</para>
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

    // ---- The containment ledger: who the LAST kill epoch fanned out to (ISSUES-LOG #17) ----
    //
    // Resume used to be `_gate.Resume()` and nothing else, so the emergency stop was one-way: every jail
    // it froze stayed frozen forever, recoverable only by a raw `docker unpause` from outside the app.
    // The fix needs to know WHICH agents to release, and the only honest answer is "the ones this kill
    // switch actually fanned out to", not "everything Docker currently has paused".
    private readonly object _epochGate = new();
    private string? _engagedEpochId;
    private List<string> _fannedOutTo = new();

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
        _rttBudget = rttBudget ?? KillSwitchTiming.UnmeasuredRtt;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _onRttSpike = onRttSpike;

        // Reference identity, not "is it zero": a real EWMA that happens to read zero on a fast channel is
        // a measurement, and the sentinel is the absence of one.
        MeasuresControlChannelRtt = !ReferenceEquals(_rttBudget, KillSwitchTiming.UnmeasuredRtt);

        // Same discipline as MergeQueueProvisioner.WiredOptionalControls, for the same measured reason: the
        // daemon passes only gate/target/audit, and deleting `audit:` from DaemonHost left the whole
        // Kill-filtered suite green (11 passed) because nothing asserted this composition at all.
        var wired = new SortedSet<string>(StringComparer.Ordinal);
        if (journal is not null) { wired.Add(nameof(journal)); }
        if (audit is not null) { wired.Add(nameof(audit)); }
        if (rttBudget is not null) { wired.Add(nameof(rttBudget)); }
        if (clock is not null) { wired.Add(nameof(clock)); }
        if (onRttSpike is not null) { wired.Add(nameof(onRttSpike)); }
        WiredOptionalControls = wired;
    }

    /// <summary>True while the queue is frozen (the kill switch is engaged).</summary>
    public bool IsEngaged => _gate.IsFrozen;

    /// <summary>
    /// Every optional constructor argument this kill switch was actually given, by parameter name — the
    /// composition-root assertion surface.
    ///
    /// <para>Each default here substitutes a weaker behaviour silently: <c>journal</c> becomes an
    /// <see cref="InMemoryKillJournal"/> nobody holds a reference to (step 3 writes nowhere durable),
    /// <c>audit</c> becomes a throwaway <see cref="InMemoryAuditLog"/> that detaches the
    /// <c>killswitch</c>/<c>killswitch_audit_gap</c> events from the daemon's sink, and <c>rttBudget</c>
    /// becomes <see cref="KillSwitchTiming.UnmeasuredRtt"/>. So the daemon states its whole tail and a test
    /// pins it, rather than three of the five being invisible.</para>
    /// </summary>
    public IReadOnlySet<string> WiredOptionalControls { get; }

    /// <summary>
    /// Whether this kill switch has a real control-channel RTT to reason about, as opposed to
    /// <see cref="KillSwitchTiming.UnmeasuredRtt"/>. False in the shipped daemon — there is no production
    /// transport behind P2-09's <c>IAgentControlChannel</c> — and reported on every
    /// <see cref="KillReport"/>/<see cref="KillSnapshot"/> so the record says <i>unknown</i> rather than
    /// implying a healthy channel.
    /// </summary>
    public bool MeasuresControlChannelRtt { get; }

    /// <summary>Whether an RTT spike has anywhere to go (the A3 <c>Unresponsive</c> feed).</summary>
    public bool ReportsRttSpike => _onRttSpike is not null;

    /// <summary>The journal step 3's snapshot is written to — durable in the daemon, so a kill epoch
    /// survives the restart that follows an emergency stop.</summary>
    public IKillJournal Journal => _journal;

    /// <summary>The audit sink the <c>killswitch</c> event lands in (best-effort, RT-D3).</summary>
    public IAuditLog AuditLog => _audit;

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

        // Record the epoch's fan-out set BEFORE returning, so ResumeAsync has something to reverse even if
        // the caller never reads the report. Every id the fan-out touched is recorded, including the
        // PauseFailed ones: a failure is per-agent and can be partial (one repo's jail frozen, another's
        // refused behind the same id), and the terminal-input sever ran for all of them. What each agent
        // actually gets released is the TARGET's call — it is the only layer that knows, per container,
        // whether this kill switch was the party that froze it.
        lock (_epochGate)
        {
            _engagedEpochId = epochId;
            // A UNION, not a replacement: the ledger means "contained and not yet released". Engaging
            // twice without a Resume in between is ordinary (the control is a toggle), and so is engaging
            // again after a Resume that could not wake one jail — in both cases an agent already in the
            // ledger must stay there, or it would be silently abandoned in the paused state.
            _fannedOutTo = _fannedOutTo
                .Union(results.Select(r => r.AgentId), StringComparer.Ordinal)
                .ToList();
        }

        // ---- Step 3: journal snapshot written BEFORE returning ----
        var states = _target.CaptureStates();
        var agentStates = results
            .Select(r => new KillAgentState(r.AgentId, states.TryGetValue(r.AgentId, out var s) ? s : "Unknown", r.Outcome))
            .OrderBy(a => a.AgentId, StringComparer.Ordinal)
            .ToList();

        var snapshot = new KillSnapshot(
            epochId, _clock(), agentStates, QueueFrozen: _gate.IsFrozen, RttMeasured: MeasuresControlChannelRtt);
        _journal.WriteSnapshot(snapshot);

        // ---- RT-D3: audit best-effort — NEVER blocks the kill on audit-store availability ----
        TryAuditKill(epochId, agentStates.Count);

        return new KillReport(
            epochId, freezeAt, deadline, rttSpike, agentStates,
            QueueFrozen: _gate.IsFrozen, RttMeasured: MeasuresControlChannelRtt);
    }

    /// <summary>
    /// Resumes after a kill (the banner's one action) — the real mirror of <see cref="EngageAsync"/>.
    ///
    /// <para><b>ISSUES-LOG #17.</b> This used to be <c>_gate.Resume()</c> and nothing else. Engage
    /// <c>docker pause</c>d every live jail; Resume cleared the merge/spawn freeze flag and left every one
    /// of them frozen, while the Resource Monitor's own row said the state was "(recoverable)". It was not:
    /// the per-agent Unpause RPC correctly refuses a jail it did not human-pause, so nothing inside the app
    /// could bring a killed agent back — only a raw <c>docker unpause</c> from outside it. An emergency stop
    /// you cannot come back from is a worse control than one you hesitate to press.</para>
    ///
    /// <para><b>Ordering, and why the freeze clears last but unconditionally.</b> The unpause fan-out runs
    /// first, under the same RT-D4 deadline as the kill, so the queue is not accepting merges while jails
    /// are still frozen. The gate is then cleared in a <c>finally</c>: a container engine that refuses to
    /// wake one jail must never also leave the operator with a permanently frozen queue and no way out.
    /// Agents whose release could not be confirmed come back as
    /// <see cref="KillResumeOutcome.ResumeFailed"/> and STAY in the ledger, so pressing Resume again
    /// retries exactly them.</para>
    /// </summary>
    public async Task<KillResumeReport> ResumeAsync(CancellationToken ct = default)
    {
        string? epochId;
        List<string> agentIds;
        lock (_epochGate)
        {
            epochId = _engagedEpochId;
            agentIds = _fannedOutTo.ToList();
        }

        var deadline = KillSwitchTiming.FanOutDeadline(_rttBudget());
        var results = new List<KillAgentResume>();
        try
        {
            results.AddRange(
                await Task.WhenAll(agentIds.Select(id => ResumeOneAsync(id, deadline, ct))).ConfigureAwait(false));
        }
        finally
        {
            // Unconditional: the freeze flag is the operator's control, and failing to clear it would make
            // Resume strictly worse than the broken behaviour this replaces.
            _gate.Resume();
        }

        // Keep only what still needs releasing, so a second press retries the failures and is otherwise a
        // no-op (idempotent, like Engage's freeze).
        var stillHeld = results
            .Where(r => r.Outcome == KillResumeOutcome.ResumeFailed)
            .Select(r => r.AgentId)
            .ToList();
        lock (_epochGate)
        {
            _fannedOutTo = stillHeld;
            _engagedEpochId = stillHeld.Count > 0 ? epochId : null;
        }

        TryAuditResume(epochId, results);

        return new KillResumeReport(
            epochId, _clock(),
            results.OrderBy(r => r.AgentId, StringComparer.Ordinal).ToList(),
            QueueFrozen: _gate.IsFrozen);
    }

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

    private async Task<KillAgentResume> ResumeOneAsync(string agentId, TimeSpan deadline, CancellationToken ct)
    {
        try
        {
            // Bounded like the kill's own pause, and for the same reason: an unreachable engine must not
            // hold the release open indefinitely. It reports ResumeFailed instead — the honest word for
            // "this jail may still be frozen" — which keeps the agent in the retry ledger.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(deadline);
            await _target.UnpauseAsync(agentId, cts.Token).ConfigureAwait(false);
            return new KillAgentResume(agentId, KillResumeOutcome.Resumed);
        }
        catch (Exception)
        {
            // Never thrown onward: one wedged agent must not abandon the rest of the fan-out (the failure
            // mode that would leave a fleet half-woken with no record of which half).
            return new KillAgentResume(agentId, KillResumeOutcome.ResumeFailed);
        }
    }

    private void TryAuditResume(string? epochId, IReadOnlyList<KillAgentResume> results)
    {
        try
        {
            _audit.Append(new AuditEvent("killswitch_resume", new Dictionary<string, string>
            {
                ["kill_epoch_id"] = epochId ?? string.Empty,
                ["resumed"] = results.Count(r => r.Outcome == KillResumeOutcome.Resumed).ToString(),
                ["resume_failed"] = results.Count(r => r.Outcome == KillResumeOutcome.ResumeFailed).ToString(),
            }));
        }
        catch (Exception)
        {
            // Same RT-D3 posture as the kill: the release never blocks on the audit store being up.
            lock (_pendingGate)
            {
                if (epochId is not null)
                {
                    _pendingAuditGaps.Add((epochId, _clock()));
                }
            }
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
