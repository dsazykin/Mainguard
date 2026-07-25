using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Git.Audit;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// P2-10 immutable verification record tied to a specific <c>main@sha</c> (contract §2). Re-verification
/// creates a NEW record — a row is never updated (invariant 2). Pass/fail is the daemon-observed
/// container-runtime exit code (OPS SA-1), never a supervisor-reported <c>VerifyResult</c> frame.
/// <para>RT-D2 provenance: <see cref="ResolvedCommand"/> + <see cref="ConfigHash"/> pin what actually
/// ran, so a branch that rewrites its own test command is flagged before a merge is possible.</para>
/// </summary>
public sealed record VerificationRecord(
    string AgentId,
    string MainSha,
    bool Passed,
    string LogArtifactPath,
    string ResolvedCommand,
    string ConfigHash,
    DateTimeOffset When);

/// <summary>
/// A composable merge-gate predicate (P2-10 step 4). The queue owns the staleness gate; P2-11 adds its
/// flagged-change detector, P2-35 its diff-guard — each as an <see cref="IMergeGate"/> the queue ANDs
/// into <see cref="IMergeQueue.CanMerge"/>. No single gate can grant a merge on its own.
/// </summary>
public interface IMergeGate
{
    /// <summary>True iff this gate permits <paramref name="agentId"/> to merge; otherwise sets a reason.</summary>
    bool Allows(string agentId, out string reason);
}

/// <summary>
/// The P2-10 merge queue (contract §2 — the product spine). A branch-keyed state machine deciding when
/// work is safe to merge: every branch is verified against a specific <c>main@sha</c>; any merge to main
/// invalidates every other <c>Verified</c> branch and auto re-queues it. <b>No auto-merge, ever</b> — the
/// only path to <see cref="WorkerMergeState.Merged"/> is the human foreground merge, which is NOT on this
/// interface (see <see cref="MergeQueue.ConfirmHumanMerge"/>).
/// </summary>
public interface IMergeQueue
{
    /// <summary>The branch's current merge-eligibility state.</summary>
    WorkerMergeState GetState(string agentId);

    /// <summary>Runs the project's test command in the agent's own sandbox and records the immutable result.</summary>
    Task<VerificationRecord> RunVerificationAsync(string agentId, CancellationToken ct);

    /// <summary>Main moved: flip every fresh <c>Verified</c> branch to <c>StaleVerified</c> and auto re-queue it.</summary>
    void NotifyMainMoved(string newMainSha);

    /// <summary>False when stale/unverified or a gate blocks; the reason renders verbatim (§3.4 vocabulary).</summary>
    bool CanMerge(string agentId, out string reason);
}

/// <summary>Thrown on an illegal <see cref="WorkerMergeState"/> transition (the state machine is exhaustive).</summary>
public sealed class InvalidMergeStateTransitionException : InvalidOperationException
{
    public InvalidMergeStateTransitionException(WorkerMergeState from, WorkerMergeState to)
        : base($"Illegal merge-state transition {from} → {to}.")
    {
        From = from;
        To = to;
    }

    public WorkerMergeState From { get; }
    public WorkerMergeState To { get; }
}

/// <summary>Thrown when a repo has no configured verification command and no override is set (edge row 5).</summary>
public sealed class NoVerificationCommandException : InvalidOperationException
{
    public NoVerificationCommandException(string message) : base(message) { }
}

/// <summary>The persistence seam for merge-queue state (daemon SQLite; in-memory in tests).</summary>
public interface IMergeQueueStore
{
    /// <summary>All persisted rows for a repo (used to resume queue state on daemon restart).</summary>
    IReadOnlyList<Mainguard.Git.Models.MergeQueueRow> LoadAll(string repoHash);

    /// <summary>Upserts a row (keyed by repo + agent) inside one transaction — the transition and its persistence are atomic.</summary>
    void Save(Mainguard.Git.Models.MergeQueueRow row);

    /// <summary>Removes the row for a (repo, agent) — the P2-12 cancel path when an intake'd PR closes upstream (entry gone, not a terminal state).</summary>
    void Delete(string repoHash, string agentId);
}

/// <summary>An in-memory <see cref="IMergeQueueStore"/> for tests and the pre-persistence path.</summary>
public sealed class InMemoryMergeQueueStore : IMergeQueueStore
{
    private readonly object _gate = new();
    private readonly List<Mainguard.Git.Models.MergeQueueRow> _rows = new();
    private long _nextId;

    public IReadOnlyList<Mainguard.Git.Models.MergeQueueRow> LoadAll(string repoHash)
    {
        lock (_gate)
        {
            return _rows.Where(r => r.RepoHash == repoHash).Select(Clone).ToList();
        }
    }

    public void Save(Mainguard.Git.Models.MergeQueueRow row)
    {
        lock (_gate)
        {
            var existing = _rows.FirstOrDefault(r => r.RepoHash == row.RepoHash && r.AgentId == row.AgentId);
            if (existing is null)
            {
                row.Id = ++_nextId;
                _rows.Add(Clone(row));
            }
            else
            {
                existing.State = row.State;
                existing.LastVerificationId = row.LastVerificationId;
                existing.UpdatedUtc = row.UpdatedUtc;
                existing.VerifiedAtUtc = row.VerifiedAtUtc;
                existing.Origin = row.Origin;
                row.Id = existing.Id;
            }
        }
    }

    public void Delete(string repoHash, string agentId)
    {
        lock (_gate)
        {
            _rows.RemoveAll(r => r.RepoHash == repoHash && r.AgentId == agentId);
        }
    }

    private static Mainguard.Git.Models.MergeQueueRow Clone(Mainguard.Git.Models.MergeQueueRow r) => new()
    {
        Id = r.Id,
        RepoHash = r.RepoHash,
        AgentId = r.AgentId,
        State = r.State,
        LastVerificationId = r.LastVerificationId,
        UpdatedUtc = r.UpdatedUtc,
        VerifiedAtUtc = r.VerifiedAtUtc,
        Origin = r.Origin,
    };
}

/// <summary>
/// The concrete P2-10 merge queue: an exhaustive, persisted state machine over one repo's agent
/// branches. Every legal transition is enumerated; every illegal transition throws
/// <see cref="InvalidMergeStateTransitionException"/>. Each transition is persisted in the same
/// transaction, so a daemon restart resumes queue state and an interrupted <c>Verifying</c> resumes
/// (never stuck).
/// </summary>
public sealed class MergeQueue : IMergeQueue
{
    // Legal transitions (contract §3.1). Anything not listed throws. "Working" is reachable from every
    // non-terminal state (new commits from the agent invalidate). Merged/Rejected are terminal.
    private static readonly IReadOnlyDictionary<WorkerMergeState, WorkerMergeState[]> Legal =
        new Dictionary<WorkerMergeState, WorkerMergeState[]>
        {
            [WorkerMergeState.Working] = new[] { WorkerMergeState.Verifying, WorkerMergeState.Working },
            [WorkerMergeState.Verifying] = new[] { WorkerMergeState.Verified, WorkerMergeState.Working, WorkerMergeState.Verifying },
            [WorkerMergeState.Verified] = new[] { WorkerMergeState.StaleVerified, WorkerMergeState.AwaitingReview, WorkerMergeState.Working },
            [WorkerMergeState.StaleVerified] = new[] { WorkerMergeState.Verifying, WorkerMergeState.Working },
            [WorkerMergeState.AwaitingReview] = new[] { WorkerMergeState.Merged, WorkerMergeState.Rejected, WorkerMergeState.StaleVerified, WorkerMergeState.Working },
            [WorkerMergeState.Merged] = Array.Empty<WorkerMergeState>(),
            [WorkerMergeState.Rejected] = Array.Empty<WorkerMergeState>(),
        };

    private readonly object _gate = new();
    private readonly string _repoHash;
    private readonly IMergeQueueStore _store;
    private readonly IVerificationStore _verifications;
    private readonly Func<string, CancellationToken, Task<VerificationRecord>> _runVerification;
    private readonly Func<string, CancellationToken, Task>? _requeue;
    private readonly IReadOnlyList<IMergeGate> _gates;
    private readonly IAuditLog _audit;
    private readonly Func<DateTimeOffset> _clock;

    private readonly Dictionary<string, WorkerMergeState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VerificationRecord?> _lastVerification = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset?> _verifiedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MergeEntryOrigin> _origins = new(StringComparer.Ordinal);
    private readonly HashSet<string> _verifying = new(StringComparer.Ordinal);
    private string _currentMainSha;

    /// <summary>When true the kill switch has frozen the queue (P2-14): no merge, loudly.</summary>
    public bool IsFrozen { get; set; }

    /// <summary>The most recent stale-cascade re-queue work (tests await it; production ignores it).</summary>
    public Task LastCascade { get; private set; } = Task.CompletedTask;

    /// <summary>Raised (off any lock) after any state change so the gRPC stream / UI can re-read.</summary>
    public event Action? Changed;

    /// <summary>Every agent this queue currently tracks (for stream snapshots).</summary>
    public IReadOnlyList<string> Agents
    {
        get { lock (_gate) return _states.Keys.ToList(); }
    }

    /// <param name="repoHash">The repo this queue governs.</param>
    /// <param name="currentMainSha">The current <c>main@sha</c> verifications are compared against.</param>
    /// <param name="store">Persisted queue-state store (SQLite in the daemon).</param>
    /// <param name="verifications">The immutable verification-record store.</param>
    /// <param name="runVerification">Runs the test command in the agent sandbox and returns the daemon-observed record.</param>
    /// <param name="requeue">P2-09 yield → rebase → re-verify re-entry (default: re-verify only).</param>
    /// <param name="gates">Composable merge gates ANDed into <see cref="CanMerge"/> (P2-11/P2-35 hooks).</param>
    /// <param name="audit">Audit sink for the loud override path (<c>stale_override_used</c>).</param>
    /// <param name="clock">Injectable clock (tests use a virtual one).</param>
    public MergeQueue(
        string repoHash,
        string currentMainSha,
        IMergeQueueStore store,
        IVerificationStore verifications,
        Func<string, CancellationToken, Task<VerificationRecord>> runVerification,
        Func<string, CancellationToken, Task>? requeue = null,
        IReadOnlyList<IMergeGate>? gates = null,
        IAuditLog? audit = null,
        Func<DateTimeOffset>? clock = null)
    {
        _repoHash = repoHash ?? throw new ArgumentNullException(nameof(repoHash));
        _currentMainSha = currentMainSha ?? string.Empty;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _verifications = verifications ?? throw new ArgumentNullException(nameof(verifications));
        _runVerification = runVerification ?? throw new ArgumentNullException(nameof(runVerification));
        _requeue = requeue;
        _gates = gates ?? Array.Empty<IMergeGate>();
        _audit = audit ?? new InMemoryAuditLog();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        Hydrate();
    }

    /// <summary>The current <c>main@sha</c> this queue verifies against.</summary>
    public string CurrentMainSha { get { lock (_gate) return _currentMainSha; } }

    // ---- IMergeQueue -----------------------------------------------------

    public WorkerMergeState GetState(string agentId)
    {
        lock (_gate)
        {
            return _states.TryGetValue(agentId, out var s) ? s : WorkerMergeState.Working;
        }
    }

    public async Task<VerificationRecord> RunVerificationAsync(string agentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("agentId is required.", nameof(agentId));
        }

        lock (_gate)
        {
            if (!_verifying.Add(agentId))
            {
                throw new InvalidOperationException($"A verification for '{agentId}' is already in flight.");
            }

            // Transition into Verifying (legal from Working / StaleVerified / Verifying-resume).
            SetStateLocked(agentId, WorkerMergeState.Verifying);
        }

        VerificationRecord record;
        try
        {
            // The runner launches the test command via the container runtime and reads the
            // daemon-observed exit code (OPS SA-1). This queue never inspects a supervisor frame.
            record = await _runVerification(agentId, ct).ConfigureAwait(false);
            _verifications.Insert(_repoHash, record);
        }
        catch
        {
            lock (_gate)
            {
                _verifying.Remove(agentId);
                // A failed run surfaces the branch back to Working (not silently retried — edge row 2).
                SetStateLocked(agentId, WorkerMergeState.Working);
            }
            Changed?.Invoke();
            throw;
        }

        lock (_gate)
        {
            _verifying.Remove(agentId);
            _lastVerification[agentId] = record;
            if (record.Passed)
            {
                _verifiedAt[agentId] = record.When;
                SetStateLocked(agentId, WorkerMergeState.Verified, verifiedAt: record.When);
            }
            else
            {
                // Failure surfaced, not silently retried (edge row 2).
                SetStateLocked(agentId, WorkerMergeState.Working);
            }
        }

        Changed?.Invoke();
        return record;
    }

    public void NotifyMainMoved(string newMainSha)
    {
        List<string> staleFifo;
        lock (_gate)
        {
            _currentMainSha = newMainSha ?? string.Empty;

            // Every Verified — and every AwaitingReview whose verification is now against an old main —
            // flips to StaleVerified. FIFO by original verification time (contract §3.3).
            staleFifo = _states
                .Where(kv => kv.Value is WorkerMergeState.Verified or WorkerMergeState.AwaitingReview)
                .Where(kv => IsVerificationStaleLocked(kv.Key))
                .OrderBy(kv => _verifiedAt.TryGetValue(kv.Key, out var t) && t.HasValue ? t.Value : DateTimeOffset.MaxValue)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var agentId in staleFifo)
            {
                SetStateLocked(agentId, WorkerMergeState.StaleVerified);
            }
        }

        // Auto re-queue each stale branch: P2-09 yield → keep-alive rebase → re-verify. One verification
        // per agent at a time; FIFO order preserved. Kept as an awaitable so tests can drain it.
        if (staleFifo.Count > 0)
        {
            LastCascade = RequeueAllAsync(staleFifo);
        }

        Changed?.Invoke();
    }

    public bool CanMerge(string agentId, out string reason)
    {
        lock (_gate)
        {
            return CanMergeLocked(agentId, out reason);
        }
    }

    // ---- Human-gated transitions (NOT on IMergeQueue — no auto-merge path) ----

    /// <summary>Opens review for a fresh <c>Verified</c> branch (Verified → AwaitingReview).</summary>
    public void RequestReview(string agentId)
    {
        lock (_gate)
        {
            SetStateLocked(agentId, WorkerMergeState.AwaitingReview);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Records the human foreground merge outcome (the ONLY path to <see cref="WorkerMergeState.Merged"/>).
    /// Never reachable through <see cref="IMergeQueue"/>. Fires the stale cascade for the new main sha.
    ///
    /// <para><b>Unconditional by design — this is the RECONCILE entry point.</b> It records a merge that has
    /// already landed on a ref (the RT-D1 boot replay finds main advanced past the lease's expected sha with
    /// a T-19 <c>Merge</c> journal entry, and must reflect that fact whatever the queue currently believes).
    /// Refusing there would leave the queue permanently disagreeing with git. Every path where the merge has
    /// NOT yet landed — i.e. every path that is still ASKING for permission — must call
    /// <see cref="TryConfirmHumanMerge"/> instead (MG-11).</para>
    /// </summary>
    public void ConfirmHumanMerge(string agentId, string newMainSha)
    {
        lock (_gate)
        {
            MarkMergedLocked(agentId);
        }

        NotifyMainMoved(newMainSha);
    }

    /// <summary>
    /// MG-11 — the <b>gated</b> human-merge confirmation: the merge gate and the <c>Merged</c> transition
    /// are evaluated and applied under ONE hold of the queue lock, so nothing can move main between the
    /// check and the commit.
    ///
    /// <para>The old <c>ConfirmMerge</c> RPC called <see cref="ConfirmHumanMerge"/> directly, which meant the
    /// daemon enforced <i>nothing</i>: no <see cref="CanMerge"/>, no freshness compare, and no gate — a
    /// branch that was <c>Verified@old</c>, or whose flagged/RT-D2 items were unacknowledged, went straight
    /// to <c>Merged</c> because every one of those checks lived in the client cockpit. A cockpit is a
    /// renderer; a hand-written client that skips it (or a cockpit racing a co-tenant's merge) simply
    /// bypassed the entire merge contract. The gates now decide here, daemon-side, or the merge is refused.</para>
    ///
    /// <para><paramref name="expectedMainSha"/> is the caller's compare-and-swap old-OID: the main this
    /// merge was authorized against (the lease's <c>ExpectedMainSha</c>). It is compared to the queue's
    /// authoritative current main <i>inside</i> the lock — a co-tenant merge that landed since
    /// <c>BeginMerge</c> is exactly the stale cascade this refusal exists to respect. Pass null to skip only
    /// that compare (the record-vs-main staleness check inside <see cref="CanMerge"/> still applies).</para>
    /// </summary>
    /// <returns>True when the branch moved to <see cref="WorkerMergeState.Merged"/>; false with a
    /// render-verbatim <paramref name="reason"/> (§3.4 vocabulary) when the gate refused.</returns>
    public bool TryConfirmHumanMerge(string agentId, string newMainSha, string? expectedMainSha, out string reason)
    {
        lock (_gate)
        {
            // The CAS old-OID compare comes first so a lost race reports "main moved" rather than whatever
            // downstream symptom (stale record, re-queued state) the cascade has already produced.
            if (!string.IsNullOrEmpty(expectedMainSha)
                && !string.Equals(expectedMainSha, _currentMainSha, StringComparison.Ordinal))
            {
                reason = "verification is stale — main moved; re-verifying";
                return false;
            }

            // Freeze + state + record-vs-main freshness + every composable gate, in one place.
            if (!CanMergeLocked(agentId, out reason))
            {
                return false;
            }

            MarkMergedLocked(agentId);
        }

        // Outside the lock: the cascade re-queues co-tenants and raises Changed.
        NotifyMainMoved(newMainSha);
        reason = "";
        return true;
    }

    // The Verified → AwaitingReview → Merged walk shared by both confirm entry points. Caller holds _gate.
    private void MarkMergedLocked(string agentId)
    {
        // Allow the human merge from a fresh Verified or an opened AwaitingReview.
        if (GetStateLocked(agentId) == WorkerMergeState.Verified)
        {
            SetStateLocked(agentId, WorkerMergeState.AwaitingReview);
        }

        SetStateLocked(agentId, WorkerMergeState.Merged);
    }

    /// <summary>Rejects a branch (AwaitingReview → Rejected); teardown follows per policy.</summary>
    public void Reject(string agentId)
    {
        lock (_gate)
        {
            SetStateLocked(agentId, WorkerMergeState.Rejected);
        }

        Changed?.Invoke();
    }

    /// <summary>New commits from the agent invalidate any verification (any non-terminal → Working).</summary>
    public void NotifyNewCommits(string agentId)
    {
        lock (_gate)
        {
            var current = GetStateLocked(agentId);
            if (current is WorkerMergeState.Merged or WorkerMergeState.Rejected)
            {
                return; // terminal — a new branch/agent id would be a fresh row.
            }

            _lastVerification[agentId] = null;
            _verifiedAt[agentId] = null;
            SetStateLocked(agentId, WorkerMergeState.Working);
        }

        Changed?.Invoke();
    }

    // ---- P2-12 external-PR intake (entry origin + cancel) ----------------

    /// <summary>The origin of an entry (defaults to <see cref="MergeEntryOrigin.Local"/> for an unknown/local agent).</summary>
    public MergeEntryOrigin GetOrigin(string agentId)
    {
        lock (_gate)
        {
            return _origins.TryGetValue(agentId, out var o) ? o : MergeEntryOrigin.Local;
        }
    }

    /// <summary>
    /// Ensures a queue entry exists for <paramref name="agentId"/> at <c>Working</c> with the given
    /// <paramref name="origin"/> (P2-12). Idempotent: a re-materialize of an already-tracked PR only
    /// (re)stamps the origin — it does not reset a branch that is mid-verification or already verified.
    /// </summary>
    public void EnsureEntry(string agentId, MergeEntryOrigin origin)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("agentId is required.", nameof(agentId));
        }

        lock (_gate)
        {
            _origins[agentId] = origin;
            if (!_states.ContainsKey(agentId))
            {
                // A brand-new entry starts at Working (self-transition persists the row + origin).
                SetStateLocked(agentId, WorkerMergeState.Working);
            }
            else
            {
                // Already tracked — just persist the (possibly first-seen) origin without moving state.
                SaveRowLocked(agentId);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Cancels and forgets an entry (P2-12 closed-PR cleanup): the entry is <b>gone</b>, not a terminal
    /// state. The caller prunes the worktree + branch; this drops all in-memory tracking and the
    /// persisted row. Safe to call for an unknown agent (no-op).
    /// </summary>
    public void Cancel(string agentId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _states.Remove(agentId);
            _origins.Remove(agentId);
            _lastVerification.Remove(agentId);
            _verifiedAt.Remove(agentId);
            _verifying.Remove(agentId);
            _store.Delete(_repoHash, agentId);
        }

        if (removed)
        {
            Changed?.Invoke();
        }
    }

    // ---- Override path (loud, separate, journaled+audited; CanMerge stays false) ----

    /// <summary>
    /// The stale-merge override (P2-10 step 4). This is a SEPARATE path from <see cref="CanMerge"/> —
    /// <see cref="CanMerge"/> still returns false. The caller (the Windows foreground merge) invokes this
    /// only behind an explicit, loudly-labeled confirmation; it emits the <c>stale_override_used</c> audit
    /// event (the journal row is written by the merge itself via T-19).
    /// </summary>
    public void RecordStaleOverrideUse(string agentId, string reason)
    {
        _audit.Append(new AuditEvent("stale_override_used", new Dictionary<string, string>
        {
            ["repo"] = _repoHash,
            ["agent"] = agentId,
            ["reason"] = reason,
            ["main_sha"] = CurrentMainSha,
        }));
    }

    // ---- Restart resume --------------------------------------------------

    /// <summary>
    /// Resumes any interrupted <c>Verifying</c> run after a daemon restart (edge row 4 — never stuck).
    /// Each interrupted run is re-executed; the terminal state is always reached.
    /// </summary>
    public async Task ResumeAfterRestartAsync(CancellationToken ct = default)
    {
        List<string> interrupted;
        lock (_gate)
        {
            interrupted = _states.Where(kv => kv.Value == WorkerMergeState.Verifying).Select(kv => kv.Key).ToList();
        }

        foreach (var agentId in interrupted)
        {
            try
            {
                await RunVerificationAsync(agentId, ct).ConfigureAwait(false);
            }
            catch
            {
                // RunVerificationAsync already surfaced the branch back to Working on failure.
            }
        }
    }

    // ---- Internals -------------------------------------------------------

    private Task RequeueAllAsync(IReadOnlyList<string> staleFifo)
    {
        var requeue = _requeue ?? ((id, token) => RunVerificationAsync(id, token));
        return Task.Run(async () =>
        {
            foreach (var agentId in staleFifo)
            {
                try
                {
                    await requeue(agentId, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // A re-verify failure surfaces to Working via RunVerificationAsync; never crash the cascade.
                }
            }
        });
    }

    private bool CanMergeLocked(string agentId, out string reason)
    {
        if (IsFrozen)
        {
            reason = "the queue is frozen — resume first";
            return false;
        }

        var state = GetStateLocked(agentId);
        if (state is not (WorkerMergeState.Verified or WorkerMergeState.AwaitingReview))
        {
            reason = state == WorkerMergeState.StaleVerified
                ? "verification is stale — re-verifying"
                : state == WorkerMergeState.Verifying
                    ? "verifying"
                    : "not verified yet";
            return false;
        }

        var record = _lastVerification.TryGetValue(agentId, out var r) ? r : null;
        if (record is null || !record.Passed || !string.Equals(record.MainSha, _currentMainSha, StringComparison.Ordinal))
        {
            reason = "verification is stale — re-verifying";
            return false;
        }

        // Composable gates (P2-11 flagged-change detector, P2-35 diff guard, RT-D2 changed-test-command).
        foreach (var gate in _gates)
        {
            if (!gate.Allows(agentId, out var gateReason))
            {
                reason = gateReason;
                return false;
            }
        }

        reason = "";
        return true;
    }

    private bool IsVerificationStaleLocked(string agentId)
    {
        var record = _lastVerification.TryGetValue(agentId, out var r) ? r : null;
        return record is null || !string.Equals(record.MainSha, _currentMainSha, StringComparison.Ordinal);
    }

    private WorkerMergeState GetStateLocked(string agentId) =>
        _states.TryGetValue(agentId, out var s) ? s : WorkerMergeState.Working;

    private void SetStateLocked(string agentId, WorkerMergeState target, DateTimeOffset? verifiedAt = null)
    {
        var from = GetStateLocked(agentId);
        if (from != target)
        {
            if (!Legal.TryGetValue(from, out var allowed) || !allowed.Contains(target))
            {
                throw new InvalidMergeStateTransitionException(from, target);
            }
        }

        _states[agentId] = target;
        SaveRowLocked(agentId, verifiedAt);
    }

    // Persists the current row for an agent (state + origin) without moving state. Used by EnsureEntry
    // to stamp a first-seen origin, and by SetStateLocked after every legal transition.
    private void SaveRowLocked(string agentId, DateTimeOffset? verifiedAt = null)
    {
        var row = new Mainguard.Git.Models.MergeQueueRow
        {
            RepoHash = _repoHash,
            AgentId = agentId,
            State = GetStateLocked(agentId).ToString(),
            LastVerificationId = _verifications.LastId(_repoHash, agentId),
            UpdatedUtc = _clock().UtcDateTime,
            VerifiedAtUtc = verifiedAt?.UtcDateTime
                ?? (_verifiedAt.TryGetValue(agentId, out var t) ? t?.UtcDateTime : null),
            Origin = (_origins.TryGetValue(agentId, out var o) ? o : MergeEntryOrigin.Local).ToString(),
        };
        // The transition and its persistence are one transaction (Save == one SQLite SaveChanges).
        _store.Save(row);
    }

    // Rebuild in-memory state from the store on construction (daemon restart resume).
    private void Hydrate()
    {
        foreach (var row in _store.LoadAll(_repoHash))
        {
            if (Enum.TryParse<WorkerMergeState>(row.State, out var state))
            {
                _states[row.AgentId] = state;
            }

            if (Enum.TryParse<MergeEntryOrigin>(row.Origin, out var origin))
            {
                _origins[row.AgentId] = origin;
            }

            if (row.VerifiedAtUtc.HasValue)
            {
                _verifiedAt[row.AgentId] = new DateTimeOffset(row.VerifiedAtUtc.Value, TimeSpan.Zero);
            }

            var record = _verifications.Latest(_repoHash, row.AgentId);
            if (record is not null)
            {
                _lastVerification[row.AgentId] = record;
            }
        }
    }
}
