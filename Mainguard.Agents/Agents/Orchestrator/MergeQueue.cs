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

/// <summary>
/// Thrown when a verification command's shell operators survived tokenisation — i.e. the repository
/// wrote a command that needs a shell, on a path that has none.
///
/// <para>Deliberately an <see cref="InvalidOperationException"/> so it lands with the other
/// <b>refusals</b> at the RPC boundary (<c>FailedPrecondition</c>) rather than anywhere near a
/// <see cref="VerificationRecord"/>. That placement is the fix: the failure being prevented is not
/// "the command is wrong", it is "the command is wrong AND the queue reported it as the repository's
/// tests failing".</para>
/// </summary>
public sealed class MalformedVerificationCommandException : InvalidOperationException
{
    public MalformedVerificationCommandException(string message) : base(message) { }
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
/// What one <see cref="MergeQueue.ResumeAfterRestartAsync"/> pass did with the entries a daemon restart
/// left frozen at <c>Verifying</c>. The two lists are the two honest answers, and they are separate
/// because they are not the same fact about the branch.
/// </summary>
/// <param name="ReRun">Entries whose jail is still up, so the interrupted verification was re-executed in
/// it. Each of these reached a real terminal (<c>Verified</c>, or <c>Working</c> on a failure) driven by a
/// daemon-observed container exit — i.e. an actual re-drive.</param>
/// <param name="Stranded">Entries whose jail is <b>gone</b>. These were returned to <c>Working</c> without
/// pretending to verify anything: with no sandbox there is nothing to run the test command in (§3.2 — host
/// execution is a rejection trigger), so "re-drive it" is not available and claiming otherwise would be the
/// same lie in a different state. They need a jail before they can verify again.</param>
public sealed record RestartResumeReport(
    IReadOnlyList<string> ReRun, IReadOnlyList<string> Stranded);

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
    // frozen at Verifying (the daemon restarted mid-run before ResumeAfterRestartAsync could re-drive it),
    // and an action that only worked from AwaitingReview — where Rejected already lives — would not reach a
    // single one of them.
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
    private readonly Dictionary<string, DateTimeOffset> _lastChangedAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> _verifying = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _requeueBlocks = new(StringComparer.Ordinal);
    private string _currentMainSha;

    /// <summary>Audit event a human discard appends (the durable half is the entry's own persisted row).</summary>
    public const string DiscardedEvent = "queue_entry_discarded";
    public const string RejectedEvent = "queue_entry_rejected";

    /// <summary>
    /// Audit event appended when the stale cascade could not reparent a branch, carrying the
    /// <c>reason</c> the entry was returned to <c>Working</c> instead of re-verified.
    ///
    /// <para>This is the record of the one thing the cascade must never do silently: leave a branch it
    /// could not put back on top of main. Re-verifying such a branch produces a fresh-looking
    /// <c>Verified</c> whose merge is then refused by <c>--ff-only</c>, forever, with nothing anywhere
    /// saying why.</para>
    /// </summary>
    public const string RequeueBlockedEvent = "stale_requeue_blocked";

    /// <summary>Audit event appended when a human clears a <c>Verifying</c> state with no run behind it.</summary>
    public const string StalledVerificationClearedEvent = "stalled_verification_cleared";

    /// <summary>
    /// Audit event appended for every entry the restart resume acted on, carrying an <c>outcome</c> field of
    /// <c>rerun</c> or <c>stranded</c>. Deliberately NOT
    /// <see cref="StalledVerificationClearedEvent"/>: that event records a human deciding, and its
    /// <c>by</c> field is a person. This one records the daemon reconciling itself after a restart, and
    /// conflating the two would put an actor's name on something nobody did.
    /// </summary>
    public const string RestartResumeEvent = "verification_restart_resume";

    /// <summary>When true the kill switch has frozen the queue (P2-14): no merge, loudly.</summary>
    public bool IsFrozen { get; set; }

    /// <summary>The most recent stale-cascade re-queue work (tests await it; production ignores it).</summary>
    public Task LastCascade { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The most recent <see cref="BeginResumeAfterRestart"/> pass — same posture as
    /// <see cref="LastCascade"/>: tests await it, production fires and forgets. A completed no-op pass
    /// until something starts one, so a caller can always await it.
    /// </summary>
    public Task<RestartResumeReport> LastResume { get; private set; } =
        Task.FromResult(new RestartResumeReport(Array.Empty<string>(), Array.Empty<string>()));

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
    /// <param name="requeue">
    /// P2-09 yield → keep-alive rebase → re-verify re-entry. <b>The default (re-verify only) is not a
    /// lighter version of this — it is the defect.</b> Re-verification moves no branch onto the new main,
    /// so a co-tenant of a merged branch passes its tests, returns to <c>Verified</c> against a main it
    /// does not descend from, and has its <c>--ff-only</c> merge refused; the cascade re-verifies it and
    /// the loop never ends. <see cref="MergeQueueProvisioner"/> supplies the real one.
    /// </param>
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

    /// <summary>
    /// When this entry's row last moved — the same instant that is persisted as
    /// <c>MergeQueueRow.UpdatedUtc</c>, kept in memory and rehydrated on restart so it survives a daemon
    /// bounce. Null for an id the queue has never written a row for.
    ///
    /// <para>This exists because <b>insertion order is not decision order</b>. A terminal entry keeps the
    /// position it was spawned into, so the rail's permanent Merged/Rejected history renders oldest-spawn
    /// first and a brand-new rejection can land at the very bottom of a list of a dozen — which reads, to
    /// the human who just clicked Reject, as the entry having vanished (walkthrough 2026-08-20, ISSUES-LOG
    /// #13, logged as a HIGH data-loss regression when nothing was lost at all). The display order needs a
    /// "when was this decided" to put the newest decision at the top of the history it belongs to.</para>
    /// </summary>
    public DateTimeOffset? LastChangedAt(string agentId)
    {
        lock (_gate)
        {
            return _lastChangedAt.TryGetValue(agentId, out var t) ? t : null;
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

            try
            {
                // Transition into Verifying (legal from Working / StaleVerified / Verifying-resume).
                SetStateLocked(agentId, WorkerMergeState.Verifying);
            }
            catch
            {
                // The entry went terminal (a human discard) between the caller deciding to verify and this
                // lock, so the transition is illegal and this call is over. Undo the in-flight mark on the
                // way out: leaving it would make IsVerificationInFlight answer TRUE forever for an entry
                // with no run — which is the same "the row says an activity that is not happening" defect
                // the restart resume exists to end, and nothing ever clears it because every exit path that
                // removes an id from _verifying is downstream of this throw.
                _verifying.Remove(agentId);
                throw;
            }
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
            // The agent moved on, so whatever the last cascade could not reparent is no longer the story
            // of this branch: the new commits are, and they are verifiable from Working like any other.
            _requeueBlocks.Remove(agentId);
            SetStateLocked(agentId, WorkerMergeState.Working);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// The stale cascade's honest terminus when a branch could NOT be put back on top of main: the entry
    /// returns to <c>Working</c> carrying <paramref name="reason"/>, which <see cref="CanMerge"/> then
    /// renders verbatim instead of the generic "not verified yet".
    ///
    /// <para><b>Why this exists rather than just re-verifying.</b> A staled branch that was not reparented
    /// does not fast-forward onto main, and verification does not care: it runs the test command, passes,
    /// and pins the record to the queue's CURRENT main — so the entry becomes <c>Verified</c>, fresh by
    /// every check <see cref="CanMerge"/> makes, and the <c>--ff-only</c> merge refuses it. The cascade
    /// re-verifies, it passes again, and the loop has no exit. Returning the entry to <c>Working</c> with
    /// the measured reason is the difference between an entry a human can act on and one that lies to
    /// them on every refresh.</para>
    ///
    /// <para>The verification record is cleared with the state, exactly as <see cref="NotifyNewCommits"/>
    /// does: whatever evidence existed was against a main this branch is no longer parented on.</para>
    /// </summary>
    /// <param name="agentId">The entry the cascade could not reparent.</param>
    /// <param name="reason">Render-verbatim explanation (§3.4 vocabulary), e.g. the missing jail or the
    /// rebase conflict. Empty falls back to the generic wording.</param>
    /// <param name="detail">Optional extra field for the audit event (never shown as the gate reason).</param>
    /// <returns>False when the entry is unknown or already terminal — a human discard that landed while
    /// the cascade was running has decided, and this must not walk it back.</returns>
    public bool TryReturnToWorking(string agentId, string reason, string? detail = null)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(agentId, out var from) || IsTerminal(from))
            {
                return false;
            }

            _lastVerification[agentId] = null;
            _verifiedAt[agentId] = null;
            if (!string.IsNullOrWhiteSpace(reason))
            {
                _requeueBlocks[agentId] = reason;
            }

            SetStateLocked(agentId, WorkerMergeState.Working);
        }

        _audit.Append(new AuditEvent(RequeueBlockedEvent, new Dictionary<string, string>
        {
            ["repo"] = _repoHash,
            ["agent"] = agentId,
            ["reason"] = reason ?? string.Empty,
            ["detail"] = detail ?? string.Empty,
            ["main_sha"] = CurrentMainSha,
            ["when"] = _clock().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }));

        Changed?.Invoke();
        return true;
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
    /// restarts mid-run rehydrates <c>Verifying</c> with nothing behind it. Left alone, the entry reports
    /// "verifying" forever, to a human, about a run that does not exist.</para>
    ///
    /// <para><see cref="ResumeAfterRestartAsync"/> now closes that window — <see cref="MergeQueueProvisioner"/>
    /// starts a pass the moment it rebuilds a repo's queue — so a restart-frozen row is transient rather
    /// than permanent. This predicate stays exactly as load-bearing: it is what the resume itself measures
    /// to skip live runs, what <c>CanMerge</c> words its refusal from, and what
    /// <see cref="TryClearStalledVerification"/> refuses on.</para>
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
    /// The review verdict "no" — a human looked at a VERIFIED branch's work and rejected it. Terminal,
    /// exactly as <see cref="WorkerMergeState.Rejected"/> has always been in the pinned transition table;
    /// the walk is Verified → AwaitingReview → Rejected under one lock, mirroring
    /// <c>MarkMergedLocked</c>'s merge walk, so every step is a legal recorded transition.
    ///
    /// <para>Refused for anything not Verified/AwaitingReview: rejecting is a judgment about reviewed
    /// work, and there is nothing to review before a verification ran — un-verified housekeeping is
    /// <see cref="TryDiscard"/>'s job, and the refusal says so. Refused for unknown ids for the same
    /// reason TryDiscard refuses them: SetStateLocked would invent the entry.</para>
    ///
    /// <para>The by/reason/when facts land in the audit event (and the daemon's log); unlike a discard
    /// there is no per-row record column, so a daemon restart keeps the terminal <c>Rejected</c> state
    /// (states persist per transition) but not the prose — the audit log is the durable record.</para>
    /// </summary>
    /// <param name="agentId">The entry to reject.</param>
    /// <param name="rejectedBy">Daemon-derived actor. Never a client-supplied identity (SA-1/F2).</param>
    /// <param name="reason">The reviewer's verbatim reason; may be empty.</param>
    /// <param name="refusal">Render-verbatim reason when this returns false.</param>
    /// <returns>True when the entry moved to <see cref="WorkerMergeState.Rejected"/>.</returns>
    public bool TryReject(string agentId, string rejectedBy, string reason, out string refusal)
    {
        WorkerMergeState from;
        lock (_gate)
        {
            if (!_states.TryGetValue(agentId, out from))
            {
                refusal = "this entry is not in the merge queue";
                return false;
            }

            if (from is not (WorkerMergeState.Verified or WorkerMergeState.AwaitingReview))
            {
                refusal = IsTerminal(from)
                    ? $"this entry is already {from} — a terminal entry cannot be rejected"
                    : $"only a verified branch can be rejected in review — this entry is {from}; discard the entry instead";
                return false;
            }

            if (from == WorkerMergeState.Verified)
            {
                SetStateLocked(agentId, WorkerMergeState.AwaitingReview);
            }

            SetStateLocked(agentId, WorkerMergeState.Rejected);

            // A run that was somehow still in flight is no longer this entry's business (same guard
            // as TryDiscard; the completion path checks for a terminal state before transitioning).
            _verifying.Remove(agentId);
        }

        _audit.Append(new AuditEvent(RejectedEvent, new Dictionary<string, string>
        {
            ["repo"] = _repoHash,
            ["agent"] = agentId,
            ["by"] = string.IsNullOrWhiteSpace(rejectedBy) ? "unknown" : rejectedBy,
            ["reason"] = reason ?? string.Empty,
            ["from_state"] = from.ToString(),
            ["when"] = _clock().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
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
    /// illegal <c>Working → Verified</c> transition. That refusal now also covers a resume's own re-run:
    /// <see cref="ResumeAfterRestartAsync"/> goes through <see cref="RunVerificationAsync"/>, so its runs
    /// are in-flight like any other and a human clicking Clear during one is told to wait.</para>
    ///
    /// <para><b>Still the escape hatch, deliberately.</b> The restart resume handles the case it can
    /// establish — a run interrupted by a restart — and nothing else. A queue whose repo never comes back up
    /// (so its queue is never rebuilt and no resume ever runs), a probe that could not reach the container
    /// runtime, an entry a future path freezes some other way: those still need a human to be able to say
    /// "there is no run behind this". Removing this on the strength of the resume would trade a stuck entry
    /// with a button for a stuck entry without one.</para>
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
            _requeueBlocks.Remove(agentId);
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
    /// Starts a <see cref="ResumeAfterRestartAsync"/> pass in the background and publishes it on
    /// <see cref="LastResume"/>.
    ///
    /// <para><b>Background, not inline, and that is load-bearing.</b> The daemon's only caller
    /// (<see cref="MergeQueueProvisioner.EnsureQueue"/>) runs inside a gRPC handler, and a resume does two
    /// slow things per entry: it asks the container runtime whether the jail is up, and — when it is — it
    /// runs the repo's whole test suite in that jail. Doing that inline would hang <c>ProvisionRepo</c> (and
    /// every spawn) for the length of a test run, which is how a fix for a stuck queue becomes a stuck
    /// daemon.</para>
    /// </summary>
    /// <param name="hasLiveJail">agentId → does this entry still have a running sandbox. See
    /// <see cref="ResumeAfterRestartAsync"/> for why the answer decides the arm.</param>
    /// <param name="log">Optional milestone sink; one line per entry acted on.</param>
    /// <param name="ct">Cancels the pass between entries.</param>
    public Task<RestartResumeReport> BeginResumeAfterRestart(
        Func<string, bool> hasLiveJail, Action<string>? log = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hasLiveJail);
        return LastResume = Task.Run(
            () => ResumeAfterRestartAsync(hasLiveJail, log, ct), CancellationToken.None);
    }

    /// <summary>
    /// Re-drives every <c>Verifying</c> entry this queue hydrated with no run behind it — the shape a
    /// daemon killed mid-verification leaves, because the state is persisted per transition while the
    /// in-flight set (<see cref="IsVerificationInFlight"/>) is memory. Without this the entry reports
    /// "verifying" forever, to a human, about a run that does not exist (edge row 4 — never stuck).
    ///
    /// <para><b>Two arms, because there are two different branches here and one answer cannot be honest
    /// about both.</b></para>
    /// <list type="bullet">
    ///   <item><b>The jail is still up</b> — jails are persistent by design and survive the daemon, so this
    ///   is the common case. The verification is genuinely re-executed in it and reaches a real terminal
    ///   decided by the container-runtime exit. This is the actual re-drive.</item>
    ///   <item><b>The jail is gone</b> — then this entry <i>cannot</i> verify: verification runs in the
    ///   worker's own sandbox and host execution is a rejection trigger (§3.2). Calling
    ///   <see cref="RunVerificationAsync"/> anyway would transition Verifying → Verifying, publish that to
    ///   every observer, fail on the no-jail refusal and settle to <c>Working</c> — the same destination by
    ///   way of a state flap and a "verification failed" shaped event for a verification that never ran. So
    ///   the entry is moved straight to <c>Working</c> and the reason is recorded, which is what the row
    ///   already meant and what a fresh jail can act on. Restoring a jail for it is the human's
    ///   <c>Clear stalled run</c> escape hatch's neighbour, not this method's job.</item>
    /// </list>
    ///
    /// <para>Entries with a run actually in flight are skipped, so this is safe on a live queue as well as a
    /// freshly hydrated one — and it can never collide with <see cref="RunVerificationAsync"/>'s
    /// already-in-flight guard.</para>
    /// </summary>
    /// <param name="hasLiveJail">agentId → does this entry still have a running sandbox. An exception from
    /// the probe counts as "no jail": the daemon's own resolver already answers null for an unreachable
    /// container runtime, and the safe reading of "we could not establish that a jail exists" is the one
    /// that does not then claim to be verifying in it.</param>
    /// <param name="log">Optional milestone sink; one line per entry acted on.</param>
    /// <param name="ct">Cancels the pass between entries.</param>
    public async Task<RestartResumeReport> ResumeAfterRestartAsync(
        Func<string, bool> hasLiveJail, Action<string>? log = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hasLiveJail);

        List<string> interrupted;
        lock (_gate)
        {
            interrupted = _states
                .Where(kv => kv.Value == WorkerMergeState.Verifying && !_verifying.Contains(kv.Key))
                .Select(kv => kv.Key)
                .ToList();
        }

        var reRun = new List<string>();
        var stranded = new List<string>();

        foreach (var agentId in interrupted)
        {
            ct.ThrowIfCancellationRequested();

            if (!ProbeJail(hasLiveJail, agentId))
            {
                if (StrandInterrupted(agentId))
                {
                    stranded.Add(agentId);
                    log?.Invoke(
                        $"restart resume repo={_repoHash} agent={agentId} — the verification interrupted by "
                        + "the restart cannot be re-run (its jail is gone); returned to Working");
                }

                continue;
            }

            // The probe blocks on the container runtime (up to ten seconds in the daemon), which is ample
            // room for a human to discard the entry or the intake to cancel it. Re-read before committing
            // to a run: without this the entry is reported as re-run and RunVerificationAsync throws an
            // illegal-transition exception into the swallow below, so the report would name a run that
            // never happened. Still a check-then-act — RunVerificationAsync takes the lock itself — which
            // is why that method also undoes its in-flight mark when the transition is refused.
            if (!StillInterrupted(agentId))
            {
                continue;
            }

            reRun.Add(agentId);
            log?.Invoke(
                $"restart resume repo={_repoHash} agent={agentId} — re-running the verification the "
                + "restart interrupted, in the agent's own jail");
            Audit(agentId, "rerun");

            try
            {
                await RunVerificationAsync(agentId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // RunVerificationAsync already surfaced the branch back to Working on failure.
            }
        }

        // The restart's OTHER stranded shape (observed live): a rebuilt queue reads the mirror's
        // CURRENT main while its rehydrated Verified entries carry records pinned to an older one —
        // main moved while no queue was alive to see it (the post-merge window, a daemon update, an
        // offline fetch). CanMerge then answers "stale — re-verifying" forever, because the promise
        // in that sentence is the cascade's and no cascade fired: NotifyMainMoved only runs on a
        // LIVE queue's transitions. Fire it now with the sha we already hold — its own walk finds
        // exactly the stale Verified/AwaitingReview entries and routes them through the ordinary
        // yield → rebase → re-verify path; with nothing stale it moves nothing.
        string currentMain;
        lock (_gate)
        {
            currentMain = _currentMainSha;
        }

        NotifyMainMoved(currentMain);

        return new RestartResumeReport(reRun, stranded);
    }

    private static bool ProbeJail(Func<string, bool> hasLiveJail, string agentId)
    {
        try
        {
            return hasLiveJail(agentId);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Is this entry still the thing the pass measured — a Verifying row with no run behind it?
    private bool StillInterrupted(string agentId)
    {
        lock (_gate)
        {
            return GetStateLocked(agentId) == WorkerMergeState.Verifying && !_verifying.Contains(agentId);
        }
    }

    // Moves an interrupted entry off Verifying, re-reading the state under the lock: the probe ran outside
    // it, and a human discard (or the intake's Cancel) landing in that window has decided already.
    private bool StrandInterrupted(string agentId)
    {
        lock (_gate)
        {
            if (GetStateLocked(agentId) != WorkerMergeState.Verifying || _verifying.Contains(agentId))
            {
                return false;
            }

            SetStateLocked(agentId, WorkerMergeState.Working);
        }

        Audit(agentId, "stranded");
        Changed?.Invoke();
        return true;
    }

    private void Audit(string agentId, string outcome) =>
        _audit.Append(new AuditEvent(RestartResumeEvent, new Dictionary<string, string>
        {
            ["repo"] = _repoHash,
            ["agent"] = agentId,
            ["outcome"] = outcome,
            ["when"] = _clock().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }));

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
                // A branch the stale cascade could not reparent is at Working like any other unverified
                // branch, and "not verified yet" would be the second time that fact was reported as
                // something milder than it is. The measured reason replaces it until the entry moves.
                WorkerMergeState.Working when _requeueBlocks.TryGetValue(agentId, out var blocked)
                    => blocked,
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

        // Any move OFF Working retires the cascade's refusal: the entry is verifying, verified or gone,
        // and a stale reason outliving the state it explained is how a fixed branch keeps reading broken.
        if (target != WorkerMergeState.Working)
        {
            _requeueBlocks.Remove(agentId);
        }

        _states[agentId] = target;
        SaveRowLocked(agentId, verifiedAt);
    }

    // Persists the current row for an agent (state + origin) without moving state. Used by EnsureEntry
    // to stamp a first-seen origin, and by SetStateLocked after every legal transition.
    private void SaveRowLocked(string agentId, DateTimeOffset? verifiedAt = null)
    {
        var updatedUtc = _clock().UtcDateTime;
        // Mirrored in memory so LastChangedAt can answer without a store round-trip — the rail's history
        // order is read on every snapshot.
        _lastChangedAt[agentId] = new DateTimeOffset(updatedUtc, TimeSpan.Zero);
        var row = new Mainguard.Git.Models.MergeQueueRow
        {
            RepoHash = _repoHash,
            AgentId = agentId,
            State = GetStateLocked(agentId).ToString(),
            LastVerificationId = _verifications.LastId(_repoHash, agentId),
            UpdatedUtc = updatedUtc,
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

            // Rehydrated, not recomputed: a restart must not restamp every row with "now" and thereby
            // flatten the history order the rail sorts by.
            _lastChangedAt[row.AgentId] = new DateTimeOffset(
                DateTime.SpecifyKind(row.UpdatedUtc, DateTimeKind.Utc), TimeSpan.Zero);

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
