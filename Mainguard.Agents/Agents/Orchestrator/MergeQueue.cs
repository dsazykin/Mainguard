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
                // Kept in step with DbMergeQueueStore.Save: the in-memory store is what every test and the
                // pre-persistence path reads back, so a field it silently drops is a field whose
                // persistence is never actually exercised.
                existing.DiscardedBy = row.DiscardedBy;
                existing.DiscardedAtUtc = row.DiscardedAtUtc;
                existing.DiscardReason = row.DiscardReason;
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
        DiscardedBy = r.DiscardedBy,
        DiscardedAtUtc = r.DiscardedAtUtc,
        DiscardReason = r.DiscardReason,
    };
}

/// <summary>
/// The record a <see cref="WorkerMergeState.Discarded"/> entry leaves behind: who dropped it, when, and
/// why. Persisted on the entry's own row, so it survives a daemon restart even though the audit sink is
/// in-memory today.
/// </summary>
/// <param name="By">Daemon-derived actor (see <c>MergeQueueRow.DiscardedBy</c> for what it does and does
/// not prove).</param>
/// <param name="At">When the discard was recorded.</param>
/// <param name="Reason">The human's verbatim reason; empty when they gave none.</param>
/// <param name="FromState">The state the entry was in when it was discarded — the fact that says whether
/// verified work was thrown away or an idle entry was tidied up. <b>Null on a record rehydrated from the
/// store after a daemon restart:</b> the row persists where the entry ENDED, not where it came from, and
/// the audit event is where that fact lives. Null means "not known here", never a state.</param>
public sealed record QueueEntryDiscard(
    string By, DateTimeOffset At, string Reason, WorkerMergeState? FromState);

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
    // non-terminal state (new commits from the agent invalidate). Merged/Rejected/Discarded are terminal.
    //
    // Discarded is reachable from EVERY non-terminal state, and that breadth is the point: the entries this
    // exists for are stranded at Working (their agent was stopped and nothing ever removed the entry) or
    // frozen at Verifying (the daemon restarted mid-run and nothing resumes it), and an action that only
    // worked from AwaitingReview — where Rejected already lives — would not reach a single one of them.
    private static readonly IReadOnlyDictionary<WorkerMergeState, WorkerMergeState[]> Legal =
        new Dictionary<WorkerMergeState, WorkerMergeState[]>
        {
            [WorkerMergeState.Working] = new[] { WorkerMergeState.Verifying, WorkerMergeState.Working, WorkerMergeState.Discarded },
            [WorkerMergeState.Verifying] = new[] { WorkerMergeState.Verified, WorkerMergeState.Working, WorkerMergeState.Verifying, WorkerMergeState.Discarded },
            [WorkerMergeState.Verified] = new[] { WorkerMergeState.StaleVerified, WorkerMergeState.AwaitingReview, WorkerMergeState.Working, WorkerMergeState.Discarded },
            [WorkerMergeState.StaleVerified] = new[] { WorkerMergeState.Verifying, WorkerMergeState.Working, WorkerMergeState.Discarded },
            [WorkerMergeState.AwaitingReview] = new[] { WorkerMergeState.Merged, WorkerMergeState.Rejected, WorkerMergeState.StaleVerified, WorkerMergeState.Working, WorkerMergeState.Discarded },
            [WorkerMergeState.Merged] = Array.Empty<WorkerMergeState>(),
            [WorkerMergeState.Rejected] = Array.Empty<WorkerMergeState>(),
            [WorkerMergeState.Discarded] = Array.Empty<WorkerMergeState>(),
        };

    /// <summary>The states nothing leaves — a discard is refused from all of them, in both directions.</summary>
    private static bool IsTerminal(WorkerMergeState state) =>
        state is WorkerMergeState.Merged or WorkerMergeState.Rejected or WorkerMergeState.Discarded;

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
    private readonly Dictionary<string, QueueEntryDiscard> _discards = new(StringComparer.Ordinal);
    private readonly HashSet<string> _verifying = new(StringComparer.Ordinal);
    private string _currentMainSha;

    /// <summary>Audit event a human discard appends (the durable half is the entry's own persisted row).</summary>
    public const string DiscardedEvent = "queue_entry_discarded";

    /// <summary>Audit event appended when a human clears a <c>Verifying</c> state with no run behind it.</summary>
    public const string StalledVerificationClearedEvent = "stalled_verification_cleared";

    /// <summary>When true the kill switch has frozen the queue (P2-14): no merge, loudly.</summary>
    public bool IsFrozen { get; set; }

    /// <summary>The most recent stale-cascade re-queue work (tests await it; production ignores it).</summary>
    public Task LastCascade { get; private set; } = Task.CompletedTask;

    /// <summary>Raised (off any lock) after any state change so the gRPC stream / UI can re-read.</summary>
    public event Action? Changed;

    /// <summary>
    /// Republishes the queue to every observer <b>without moving any state</b> — for the case where the
    /// gate's ANSWER changed and the state machine did not.
    ///
    /// <para>A human acknowledging a flagged item flips <see cref="CanMerge"/> from false to true, empties
    /// the gate reason, and marks that item acknowledged. All three reach the client only on the queue
    /// stream, and the stream re-pushes only on <see cref="Changed"/> — which an acknowledgment does not
    /// raise, because it is not a transition. So the ack landed daemon-side while the review surface went
    /// on rendering a blocked branch and a disabled Merge button until some unrelated transition happened
    /// to fire: the human acknowledged, and nothing they could see changed.</para>
    /// </summary>
    public void NotifyGateChanged() => Changed?.Invoke();

    /// <summary>
    /// Every agent this queue currently tracks (for stream snapshots).
    ///
    /// <para>A <see cref="WorkerMergeState.Discarded"/> entry is deliberately NOT here. Discarding is the
    /// human saying "take this off my queue", and an entry that stays on the rail wearing a Discarded chip
    /// has not been taken off anything — the complaint this action answers is a queue that accumulates
    /// entries forever. Nothing is erased by the omission: the row survives in the daemon DB carrying the
    /// state, the actor, the timestamp and the reason (<see cref="GetDiscard"/>), <see cref="GetState"/>
    /// still answers <c>Discarded</c>, an audit event was appended, and <see cref="EnsureEntry"/> cannot
    /// resurrect the id. It leaves the LIVE queue, not the record.</para>
    /// </summary>
    public IReadOnlyList<string> Agents
    {
        get
        {
            lock (_gate)
            {
                return _states.Where(kv => kv.Value != WorkerMergeState.Discarded).Select(kv => kv.Key).ToList();
            }
        }
    }

    /// <summary>Every agent this queue holds a discard record for (the record the rail no longer shows).</summary>
    public IReadOnlyList<string> DiscardedAgents
    {
        get
        {
            lock (_gate)
            {
                return _states.Where(kv => kv.Value == WorkerMergeState.Discarded).Select(kv => kv.Key).ToList();
            }
        }
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
                SettleAfterVerificationLocked(agentId, WorkerMergeState.Working);
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
                SettleAfterVerificationLocked(agentId, WorkerMergeState.Verified, verifiedAt: record.When);
            }
            else
            {
                // Failure surfaced, not silently retried (edge row 2).
                SettleAfterVerificationLocked(agentId, WorkerMergeState.Working);
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
            // Terminal — a new branch/agent id would be a fresh row. Asked through IsTerminal rather than
            // by listing the states: this guard named Merged and Rejected explicitly, so adding Discarded
            // to the enum silently turned "the human dropped this entry, then its branch moved" into an
            // illegal Discarded → Working transition. That throws out of ExternalPrIntake's per-PR poll,
            // which swallows it and never reaches SetSeenHead — so the same head is re-fetched and the
            // same exception is thrown on every poll, forever.
            if (IsTerminal(GetStateLocked(agentId)))
            {
                return;
            }

            _lastVerification[agentId] = null;
            _verifiedAt[agentId] = null;
            SetStateLocked(agentId, WorkerMergeState.Working);
        }

        Changed?.Invoke();
    }

    // ---- Human entry lifecycle (NOT on IMergeQueue — see the note in TryDiscard) ----

    /// <summary>The discard record for an entry, or null when it was never discarded.</summary>
    public QueueEntryDiscard? GetDiscard(string agentId)
    {
        lock (_gate)
        {
            return _discards.TryGetValue(agentId, out var d) ? d : null;
        }
    }

    /// <summary>
    /// Whether a verification run is ACTUALLY executing for this entry right now.
    ///
    /// <para>This is the fact that separates a branch being verified from a branch whose row merely SAYS
    /// <c>Verifying</c>. The two are indistinguishable from the state alone, and they come apart routinely:
    /// the state is persisted per transition while the in-flight set is in-memory, so a daemon that
    /// restarts mid-run rehydrates <c>Verifying</c> with nothing behind it, and
    /// <see cref="ResumeAfterRestartAsync"/> — which would have re-driven it — has no production caller.
    /// The entry then reports "verifying" forever, to a human, about a run that does not exist.</para>
    /// </summary>
    public bool IsVerificationInFlight(string agentId)
    {
        lock (_gate)
        {
            return _verifying.Contains(agentId);
        }
    }

    /// <summary>
    /// The human drops an entry from the queue: a terminal <see cref="WorkerMergeState.Discarded"/>
    /// transition carrying who, when and why.
    ///
    /// <para><b>Not on <see cref="IMergeQueue"/>, for the same reason <see cref="ConfirmHumanMerge"/> is
    /// not.</b> That interface is the surface the orchestration machinery holds, and an agent-reachable
    /// discard is a way to erase evidence — a branch that flagged an unacknowledged executable-config
    /// change could simply delete its own queue entry and the flag with it. The daemon denies the
    /// coordinator role the discard RPC at the interceptor, and keeping the method off the shared
    /// interface means nothing inside the orchestrator can reach it either.</para>
    ///
    /// <para><b>It is not a merge, and cannot be mistaken for one.</b> No lease is taken or confirmed, no
    /// <c>NotifyMainMoved</c> cascade fires, no T-19 journal entry is written, no
    /// <see cref="MergeOutcome"/> exists, and the persisted state string is <c>Discarded</c> — a distinct
    /// enum member from <see cref="WorkerMergeState.Merged"/> that no projection maps onto it.</para>
    /// </summary>
    /// <param name="agentId">The entry to drop.</param>
    /// <param name="discardedBy">Daemon-derived actor. Never a client-supplied identity (SA-1/F2).</param>
    /// <param name="reason">The human's verbatim reason; may be empty.</param>
    /// <param name="refusal">Render-verbatim reason when this returns false.</param>
    /// <returns>True when the entry moved to <see cref="WorkerMergeState.Discarded"/>.</returns>
    public bool TryDiscard(string agentId, string discardedBy, string reason, out string refusal)
    {
        QueueEntryDiscard record;
        WorkerMergeState from;
        lock (_gate)
        {
            // An unknown id must NOT be discardable. SetStateLocked would happily invent the entry
            // (GetStateLocked defaults every unknown agent to Working), so a typo'd or replayed id would
            // manufacture a Discarded row for a branch this queue never tracked.
            if (!_states.TryGetValue(agentId, out from))
            {
                refusal = "this entry is not in the merge queue";
                return false;
            }

            if (IsTerminal(from))
            {
                refusal = from == WorkerMergeState.Discarded
                    ? "this entry was already discarded"
                    : $"this entry is already {from} — a terminal entry cannot be discarded";
                return false;
            }

            record = new QueueEntryDiscard(
                string.IsNullOrWhiteSpace(discardedBy) ? "unknown" : discardedBy,
                _clock(),
                reason ?? string.Empty,
                from);
            _discards[agentId] = record;

            // The transition persists the row — state AND the discard record — in one Save.
            SetStateLocked(agentId, WorkerMergeState.Discarded);

            // A run that was in flight is no longer this entry's business. RunVerificationAsync's
            // completion path checks for a terminal state before transitioning, so the run finishing
            // afterwards cannot walk a discarded entry back to Verified.
            _verifying.Remove(agentId);
        }

        _audit.Append(new AuditEvent(DiscardedEvent, new Dictionary<string, string>
        {
            ["repo"] = _repoHash,
            ["agent"] = agentId,
            ["by"] = record.By,
            ["reason"] = record.Reason,
            ["from_state"] = from.ToString(),
            ["when"] = record.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }));

        Changed?.Invoke();
        refusal = "";
        return true;
    }

    /// <summary>
    /// Clears a <c>Verifying</c> state that has no run behind it, returning the entry to <c>Working</c> so
    /// it can be verified again.
    ///
    /// <para>This means exactly one thing and refuses in every other case: if a verification really is
    /// executing (<see cref="IsVerificationInFlight"/>) the call is refused, because the honest answer is
    /// "wait", and because walking a live run's entry to <c>Working</c> would make its own completion an
    /// illegal <c>Working → Verified</c> transition.</para>
    /// </summary>
    /// <returns>True when the entry moved back to <see cref="WorkerMergeState.Working"/>.</returns>
    public bool TryClearStalledVerification(string agentId, string clearedBy, out string refusal)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(agentId, out var from))
            {
                refusal = "this entry is not in the merge queue";
                return false;
            }

            if (from != WorkerMergeState.Verifying)
            {
                refusal = $"this entry is {from}, not stuck verifying — there is nothing to clear";
                return false;
            }

            if (_verifying.Contains(agentId))
            {
                refusal = "a verification is running for this entry right now — wait for it to finish";
                return false;
            }

            SetStateLocked(agentId, WorkerMergeState.Working);
        }

        _audit.Append(new AuditEvent(StalledVerificationClearedEvent, new Dictionary<string, string>
        {
            ["repo"] = _repoHash,
            ["agent"] = agentId,
            ["by"] = string.IsNullOrWhiteSpace(clearedBy) ? "unknown" : clearedBy,
            ["when"] = _clock().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }));

        Changed?.Invoke();
        refusal = "";
        return true;
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
            _discards.Remove(agentId);
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
            reason = state switch
            {
                WorkerMergeState.StaleVerified => "verification is stale — re-verifying",
                // "verifying" is only true while something is actually running. Saying it about a row
                // that merely persists Verifying — the shape a daemon restart leaves behind — reports an
                // activity to the human that no longer exists, and it is the reason those entries read as
                // permanently busy instead of as needing a hand.
                WorkerMergeState.Verifying => _verifying.Contains(agentId)
                    ? "verifying"
                    : "verification stalled — no run in progress",
                // Terminal states get their own words. They used to fall into "not verified yet", which is
                // a statement about a branch that might still get there.
                WorkerMergeState.Merged => "already merged",
                WorkerMergeState.Rejected => "rejected in review",
                WorkerMergeState.Discarded => "discarded — this entry was dropped from the queue",
                _ => "not verified yet",
            };
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

    /// <summary>
    /// Applies a finished verification's outcome, unless the entry became terminal while the run was in
    /// flight. A human who discards an entry mid-verification has decided; the run that was already
    /// executing must not walk it back out of <c>Discarded</c> — which the state machine would refuse
    /// anyway, by throwing an <see cref="InvalidMergeStateTransitionException"/> out of a background
    /// completion where nobody is waiting to catch it.
    /// </summary>
    private void SettleAfterVerificationLocked(
        string agentId, WorkerMergeState target, DateTimeOffset? verifiedAt = null)
    {
        if (IsTerminal(GetStateLocked(agentId)))
        {
            return;
        }

        SetStateLocked(agentId, target, verifiedAt);
    }

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

        // The discard record rides the SAME row write as the Discarded transition, so there is no window
        // in which the store holds a discarded entry with no record of who discarded it.
        if (_discards.TryGetValue(agentId, out var discard))
        {
            row.DiscardedBy = discard.By;
            row.DiscardedAtUtc = discard.At.UtcDateTime;
            row.DiscardReason = discard.Reason;
        }
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

            // A discarded entry is rehydrated INTO _states even though it never reaches the live queue
            // again. That is what makes the discard survive a restart as a decision rather than as a
            // deletion: EnsureEntry only creates an entry for an id _states does not already hold, so the
            // next spawn/intake carrying this id cannot resurrect it, and GetState still answers Discarded.
            if (row.DiscardedAtUtc.HasValue)
            {
                _discards[row.AgentId] = new QueueEntryDiscard(
                    row.DiscardedBy ?? "unknown",
                    new DateTimeOffset(row.DiscardedAtUtc.Value, TimeSpan.Zero),
                    row.DiscardReason ?? string.Empty,
                    // Not persisted, so not claimed. See QueueEntryDiscard.FromState.
                    FromState: null);
            }

            var record = _verifications.Latest(_repoHash, row.AgentId);
            if (record is not null)
            {
                _lastVerification[row.AgentId] = record;
            }
        }
    }
}
