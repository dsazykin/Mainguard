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
/// <param name="BranchSha">The <c>refs/heads/agent/&lt;id&gt;</c> tip in the mirror the run was measured
/// ON — the branch-side half of the two shas a verdict is only true between.
///
/// <para><b>Its absence was the freeze.</b> A record pinned <c>main@sha</c> and nothing else, so the queue
/// could ask "has main moved under this evidence?" — it does, in <see cref="MergeQueue.CanMerge"/> and in
/// the stale cascade — and structurally could not ask "has the BRANCH moved out from under it?". A worker
/// that pushed three more commits onto a green branch kept the green: verified @ an old tip, footer
/// "ready to merge", Merge enabled, for a tree nobody had ever tested (observed 2026-08-30, agent
/// <c>4c43d17a</c> — verification 50 at 01:35, then <c>commit_work</c> at 01:41, 01:59 and 02:13 with no
/// re-verification of any kind).</para>
///
/// <para>Empty means <b>not measured</b>, never "unchanged": the seeded/synthetic path has no mirror ref
/// to resolve, and every record written before this field existed has none either. Freshness comparisons
/// treat an empty value as unknown and decline to answer, so the branch-side compare can only ever ADD a
/// refusal — the state-machine invalidation (<see cref="MergeQueue.NotifyBranchAdvanced"/>) is the other
/// half, and it does not depend on this field at all.</para></param>
public sealed record VerificationRecord(
    string AgentId,
    string MainSha,
    bool Passed,
    string LogArtifactPath,
    string ResolvedCommand,
    string ConfigHash,
    DateTimeOffset When,
    string BranchSha = "");

/// <summary>
/// A composable merge-gate predicate (P2-10 step 4). The queue owns the staleness gate; P2-11 adds its
/// flagged-change detector, P2-35 its diff-guard — each as an <see cref="IMergeGate"/> the queue ANDs
/// into <see cref="IMergeQueue.CanMerge"/>. No single gate can grant a merge on its own.
/// </summary>
public interface IMergeGate
{
    /// <summary>True iff this gate permits <paramref name="agentId"/> to merge; otherwise sets a reason.</summary>
    bool Allows(string agentId, out string reason);

    /// <summary>
    /// One line stating what this gate had actually established about <paramref name="agentId"/> at the
    /// instant a merge was recorded — the evidence half of <see cref="MergeQueue.MergedEvent"/>. The
    /// default is null, meaning "this gate has nothing to say about that branch".
    ///
    /// <para><b>Why <see cref="Allows"/> returning true is not a record of anything.</b> The question a
    /// reader of the audit chain asks about a merge is <i>what was waived to get here</i>, and "the gates
    /// allowed it" answers it with a tautology — it is equally true of every merge that ever happened,
    /// including the one being investigated. A gate that guards must-acknowledge items says which items,
    /// how many were acknowledged, and the content hash they were bound to, so a later reader can tell a
    /// branch that had nothing to acknowledge apart from one whose flags a human cleared.</para>
    /// </summary>
    string? MergeEvidence(string agentId) => null;
}

/// <summary>
/// Who authorised a merge and through which path — the provenance half of
/// <see cref="MergeQueue.MergedEvent"/>.
///
/// <para>It is a parameter rather than something the queue infers, because the queue genuinely cannot
/// know: the same <c>Merged</c> transition is reached by a human clicking merge in the cockpit, by the
/// RT-D1 boot reconcile replaying a journal for a merge that landed before a crash, by the external-PR
/// dispatch, and by dev seeding. An audit record that could not tell those apart would put a person's
/// name on a daemon's reconciliation — the exact mistake <see cref="MergeQueue.RestartResumeEvent"/>
/// exists as a separate event type to avoid.</para>
/// </summary>
/// <param name="By">The actor. Daemon-derived for a human path (SA-1/F2 — never client-supplied), or a
/// <c>system:</c>-prefixed name for a path no person drove.</param>
/// <param name="Source">Which merge path recorded this — one of the constants below.</param>
/// <param name="LeaseId">The RT-D1 merge lease the merge was performed under; empty where there is none.</param>
public sealed record MergeAuthorization(string By, string Source, string LeaseId = "")
{
    /// <summary>The human-driven <c>ConfirmMerge</c> RPC — a person merged and the daemon-side gates passed.</summary>
    public const string ConfirmRpcSource = "confirm_rpc";

    /// <summary>The RT-D1 boot reconcile synthesizing a confirm for a merge that landed before a crash.</summary>
    public const string BootReconcileSource = "boot_reconcile";

    /// <summary>The P2-12 external-PR merge dispatch.</summary>
    public const string ExternalDispatchSource = "external_dispatch";

    /// <summary>Dev-only queue seeding (<c>docs/design/queue-seeding.md</c>).</summary>
    public const string SeededSource = "seeded";

    /// <summary>A caller that named neither actor nor path (test doubles, and the parameterless overloads).</summary>
    public const string UnattributedSource = "unattributed";

    /// <summary>The record for a merge nobody attributed — it says so rather than guessing a name.</summary>
    public static MergeAuthorization Unattributed { get; } = new("unknown", UnattributedSource);

    /// <summary>A human merge confirmed through the daemon RPC, under <paramref name="leaseId"/>.</summary>
    public static MergeAuthorization ConfirmRpc(string by, string leaseId) =>
        new(string.IsNullOrWhiteSpace(by) ? "unknown" : by, ConfirmRpcSource, leaseId ?? string.Empty);

    /// <summary>The boot reconcile's synthesized confirm. Attributed to the reconciler, never a person.</summary>
    public static MergeAuthorization BootReconcile(string leaseId = "") =>
        new(MergeQueue.ReconcilerActor, BootReconcileSource, leaseId ?? string.Empty);

    /// <summary>The external-PR dispatch's confirm.</summary>
    public static MergeAuthorization ExternalDispatch(string leaseId = "") =>
        new("system:external-pr-dispatch", ExternalDispatchSource, leaseId ?? string.Empty);

    /// <summary>A seeded entry's synthetic merge — labelled, for the same reason a seeded verification is.</summary>
    public static MergeAuthorization Seeded(string by = "") =>
        new(string.IsNullOrWhiteSpace(by) ? "system:seeder" : by, SeededSource);
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
/// What one <see cref="MergeQueue.ReconcileJails"/> pass changed on the <b>jail-liveness axis</b>. Both
/// lists hold agent ids, and both are transitions rather than populations: a pass over a queue whose
/// entries all still agree with the container engine reports nothing.
/// </summary>
/// <param name="Stranded">Entries that were believed jailed and are not — their sandbox is gone.</param>
/// <param name="Recovered">Entries that were believed stranded and have a live sandbox again (a resume,
/// an adopted survivor, or a Docker that was merely unreachable last pass).</param>
public sealed record MergeQueueJailReport(
    IReadOnlyList<string> Stranded, IReadOnlyList<string> Recovered)
{
    /// <summary>The empty pass.</summary>
    public static MergeQueueJailReport Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<string>());

    /// <summary>True when the pass moved something (the audit/publish threshold).</summary>
    public bool Changed => Stranded.Count + Recovered.Count > 0;
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
    // non-terminal state (new commits from the agent invalidate). Merged/Rejected/Discarded are terminal.
    //
    // Discarded is reachable from EVERY non-terminal state, and that breadth is the point: the entries this
    // exists for are stranded at Working (their agent was stopped and nothing ever removed the entry) or
    // frozen at Verifying (the daemon restarted mid-run before ResumeAfterRestartAsync could re-drive it),
    // and an action that only worked from AwaitingReview — where Rejected already lives — would not reach a
    // single one of them.
    //
    // VerificationFailed (H2) is reachable ONLY from Verifying, and only from the arm that has a red
    // VerificationRecord in hand. It is deliberately NOT reachable from the refusal path: a run that could
    // not start (no jail, a drifted branch, a malformed verify command) writes no record and settles to
    // Working, and routing it here would turn "we could not run your tests" into "your tests failed" —
    // the one distinction the merge decision rests on.
    private static readonly IReadOnlyDictionary<WorkerMergeState, WorkerMergeState[]> Legal =
        new Dictionary<WorkerMergeState, WorkerMergeState[]>
        {
            [WorkerMergeState.Working] = new[] { WorkerMergeState.Verifying, WorkerMergeState.Working, WorkerMergeState.Discarded },
            [WorkerMergeState.Verifying] = new[] { WorkerMergeState.Verified, WorkerMergeState.VerificationFailed, WorkerMergeState.Working, WorkerMergeState.Verifying, WorkerMergeState.Discarded },
            [WorkerMergeState.Verified] = new[] { WorkerMergeState.StaleVerified, WorkerMergeState.AwaitingReview, WorkerMergeState.Working, WorkerMergeState.Discarded },
            [WorkerMergeState.StaleVerified] = new[] { WorkerMergeState.Verifying, WorkerMergeState.Working, WorkerMergeState.Discarded },
            [WorkerMergeState.AwaitingReview] = new[] { WorkerMergeState.Merged, WorkerMergeState.Rejected, WorkerMergeState.StaleVerified, WorkerMergeState.Working, WorkerMergeState.Discarded },
            // The three honest moves out of a red verification: retry it, let the agent's fix reset it, or
            // let the human drop it. No edge to Merged (there is no passing record) and none to Rejected
            // (that is a verdict on reviewed work, and nothing here has been reviewed).
            [WorkerMergeState.VerificationFailed] = new[] { WorkerMergeState.Verifying, WorkerMergeState.Working, WorkerMergeState.VerificationFailed, WorkerMergeState.Discarded },
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
    private readonly Action<string, WorkerMergeState>? _onStateChanged;

    private readonly Dictionary<string, WorkerMergeState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VerificationRecord?> _lastVerification = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset?> _verifiedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MergeEntryOrigin> _origins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QueueEntryDiscard> _discards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastChangedAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> _verifying = new(StringComparer.Ordinal);

    /// <summary>
    /// The render-verbatim reason an entry is sitting at <c>Working</c> when "not verified yet" would
    /// understate it. Two writers, and they are the same fact told twice: the stale cascade could not
    /// reparent the branch (<see cref="TryReturnToWorking"/>), or the branch moved out from under its own
    /// verification (<see cref="NotifyBranchAdvanced"/> and the settle in
    /// <see cref="RunVerificationAsync"/>). Cleared by any move OFF <c>Working</c>.
    /// </summary>
    private readonly Dictionary<string, WorkingReason> _workingReasons = new(StringComparer.Ordinal);

    /// <summary>
    /// A measured refusal for a <c>Working</c> entry, and whether it already accounts for that entry's
    /// sandbox being gone.
    ///
    /// <para><b>Why the flag exists.</b> <see cref="CanMergeLocked"/> has two candidate sentences for a
    /// stranded <c>Working</c> entry — this measured one and the generic <see cref="StrandedReason"/> —
    /// and neither is right in both directions. The cascade's no-jail terminus ("this branch needs
    /// rebasing onto the new main and its agent has no live sandbox — resume the agent") says everything
    /// <see cref="StrandedReason"/> says <i>and</i> why the branch is back at <c>Working</c>, so letting
    /// the generic line win discards the only sentence naming both facts — which is exactly what happened
    /// live on 2026-08-30 to the three co-tenants of the merge that moved main. But the cascade's OTHER
    /// termini ("the agent is paused with the rebase in progress", "the keep-alive rebase was skipped")
    /// were every one of them measured with a live jail in hand; if the sandbox has since gone, they
    /// instruct a human to act inside a container that no longer exists. So the measured reason outranks
    /// the generic one exactly when it was itself derived from the missing sandbox, and never otherwise.</para>
    ///
    /// <para>Not persisted, deliberately, for the reason <see cref="_branchTip"/> is not: it is a
    /// MEASUREMENT of one cascade attempt at one instant, and a measurement written to SQLite outlives its
    /// own truth. After a restart the reason is gone and <see cref="_stranded"/> — which
    /// <see cref="ReconcileJails"/> re-measures against the live container runtime — is what answers, which
    /// is the correct ordering of a remembered claim and a fresh one.</para>
    /// </summary>
    /// <param name="Reason">The sentence <see cref="CanMerge"/> renders verbatim.</param>
    /// <param name="AccountsForMissingSandbox">True when the reason was produced BY observing that this
    /// entry has no live sandbox, so <see cref="StrandedReason"/> would only restate it less precisely.</param>
    private readonly record struct WorkingReason(string Reason, bool AccountsForMissingSandbox);

    /// <summary>
    /// The newest <c>refs/heads/agent/&lt;id&gt;</c> tip this queue has been told about, per entry — the
    /// branch-side counterpart of <see cref="_currentMainSha"/>.
    ///
    /// <para>Two writers, both monotone in mirror time: the ref watcher's sweep, through
    /// <see cref="NotifyBranchAdvanced"/> (a mediated agent-ref publish is fast-forward-only), and the
    /// settle in <see cref="RunVerificationAsync"/>, which advances it to the tip the run was actually
    /// measured on. The second writer is not redundant — the watcher announces only publishes IT
    /// performed, and the verification path re-publishes the agent's ref itself (MG-3), so the sweep that
    /// follows sees <c>Unchanged</c> and stays silent about a tip the queue would otherwise never learn.
    /// Without it a legitimately fresh verification could be compared against an older observed tip and
    /// refused.</para>
    ///
    /// <para>Deliberately NOT persisted, for the same reason the jail-liveness axis is not: it is a
    /// MEASUREMENT of the mirror rather than a decision this queue made, and a restart re-learns it from
    /// the watcher's first sweep. Its absence is honest — an unknown tip declines to answer the freshness
    /// question rather than manufacturing a "fresh".</para>
    /// </summary>
    private readonly Dictionary<string, string> _branchTip = new(StringComparer.Ordinal);

    /// <summary>
    /// Per entry, a tip observed WHILE a verification of that entry was in flight — the agent committing
    /// during its own test run. Cleared when a run starts and again when it settles, so its presence means
    /// exactly "something moved between those two instants" and nothing else.
    ///
    /// <para>It is a separate dictionary rather than a comparison against <see cref="_branchTip"/>, and
    /// that distinction is load-bearing: <see cref="_branchTip"/> is only as current as the last thing that
    /// told the queue, and the queue is told by a watcher that does not observe every mover. A rebase (the
    /// stale cascade) or a commit made while nothing was watching moves the branch without any
    /// announcement, so the re-verification that follows legitimately measures a tip <see cref="_branchTip"/>
    /// has never heard of. Reading that difference as "the branch moved mid-run" demoted every re-verified
    /// entry straight back to <c>Working</c> — a cascade that could never finish, which is precisely the
    /// unbreakable loop the cascade's own design notes warn about. Two of the provisioner's tests caught
    /// it. Only an advance the queue was told about DURING the run is evidence of a mid-run move.</para>
    /// </summary>
    private readonly Dictionary<string, string> _tipDuringRun = new(StringComparer.Ordinal);
    private string _currentMainSha;

    // ---- The jail-liveness axis (ISSUES-LOG #24) ------------------------------------------------------
    //
    // Deliberately NOT persisted, and that is the design rather than an omission. Liveness is a MEASUREMENT
    // of the container engine, not a decision this queue made, and a measurement written to SQLite is a
    // measurement that outlives its own truth: the daemon that wrote "stranded" three days ago has no idea
    // whether the jail came back, and the row would keep asserting it after a resume. It is re-derived from
    // Docker on every reconcile pass instead, which is why nothing here needs an EF migration.
    private readonly HashSet<string> _stranded = new(StringComparer.Ordinal);

    // The ids a pass has actually got an answer FOR. Per entry and not per queue, because the two are
    // different facts and the difference is exactly where a pass declines to answer: a probe that threw,
    // or an entry skipped because a run is genuinely in flight. A queue-wide "measured" flag would let
    // absence from _stranded read as "alive" for those, i.e. would manufacture a confident `true` out of a
    // question nobody asked. The wire contract is three-valued for this reason (see
    // MergeQueueGrpcService's HasLiveSandbox) and it is only worth anything if `null` is honest.
    private readonly HashSet<string> _jailMeasured = new(StringComparer.Ordinal);

    /// <summary>Audit event a human discard appends (the durable half is the entry's own persisted row).</summary>
    public const string DiscardedEvent = "queue_entry_discarded";
    public const string RejectedEvent = "queue_entry_rejected";

    /// <summary>
    /// The audit event for the one action this whole product exists to make safe: a branch reached
    /// <see cref="WorkerMergeState.Merged"/> and the user's main moved.
    ///
    /// <para><b>It had no event at all until now.</b> The chain recorded the act of DISCARDING an entry
    /// (<see cref="DiscardedEvent"/>) and not the act of merging one — so the single most consequential
    /// thing the product does, the one that rewrites the user's main branch, was the only one that left
    /// no tamper-evident artifact. G-17 exists precisely so a consequential pass leaves one.</para>
    ///
    /// <para><b>Emitted from here and not from the RPC, deliberately.</b> Four paths reach
    /// <c>Merged</c> — the <c>ConfirmMerge</c> RPC, the RT-D1 boot reconcile, the external-PR dispatch and
    /// dev seeding — and an event wired to only the first would leave a crash-recovered merge exactly as
    /// unrecorded as every merge is today. Both confirm entry points funnel through
    /// <see cref="MarkMergedLocked"/>, which is why the invariant "no transition to Merged without exactly
    /// one <c>queue_entry_merged</c>" is enforceable at all. <see cref="MergeAuthorization.Source"/> says
    /// which path it was.</para>
    ///
    /// <para><b>The append is allowed to throw, and that is the point.</b> The chained log throws when it
    /// cannot store (see <c>IAuditLog.Append</c>); here that surfaces as a failed <c>ConfirmMerge</c> with
    /// the lease still outstanding, so the merge that really landed is picked up by the next boot's RT-D1
    /// reconcile. An audit outage therefore delays the record rather than silently losing it.</para>
    /// </summary>
    public const string MergedEvent = "queue_entry_merged";

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

    /// <summary>
    /// Audit event appended when <see cref="ReconcileJails"/> moves an entry on the jail-liveness axis,
    /// carrying an <c>outcome</c> of <c>stranded</c> or <c>recovered</c>.
    ///
    /// <para>Its <c>by</c> is <see cref="ReconcilerActor"/> and never a person's name, for the same reason
    /// <see cref="RestartResumeEvent"/> is not <see cref="StalledVerificationClearedEvent"/>: this records
    /// the daemon noticing a fact about Docker, and putting an actor's name on it would attribute to a
    /// human a decision nobody made.</para>
    /// </summary>
    public const string JailReconciledEvent = "queue_entry_jail_reconciled";

    /// <summary>The actor every <see cref="ReconcileJails"/> audit event is attributed to. Prefixed
    /// <c>system:</c> so it can never be confused with a client-supplied identity (SA-1/F2).</summary>
    public const string ReconcilerActor = "system:reconciler";

    /// <summary>
    /// What <see cref="CanMerge"/> says about an entry whose sandbox is gone, in place of the generic
    /// "not verified yet".
    ///
    /// <para>"Not verified yet" is a sentence about a branch that might still get there under its own
    /// steam. This one cannot: verification runs in the worker's own jail and never on the host (§3.2), so
    /// the entry is not waiting on anything — it is waiting on a person. The wording names the one action
    /// that actually moves it (<c>AgentResumeService</c>'s adoption), which is the difference between a
    /// row a human can act on and a row that reports progress forever.</para>
    /// </summary>
    public const string StrandedReason =
        "the agent's sandbox is gone — resume the entry to give it one, or discard it";

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
    /// <param name="onStateChanged">
    /// Called with <c>(agentId, newState)</c> after every REAL transition — the seam the daemon reports a
    /// branch's merge state back onto its agent session through.
    ///
    /// <para><b>Why the queue has to say it and the session cannot infer it.</b> An agent session's state
    /// word is a liveness word: the store writes <c>Working</c> when the sandbox attaches and nothing in
    /// the session's own world ever learns that the branch verified. So a coordinator asking
    /// <c>get_worker_status</c> after a green verification was told <c>Working</c> — permanently, and
    /// while the queue said <c>Verified</c> — which makes a coordinator structurally unable to report the
    /// completion of its own fan-out (coordinator contract §3). It is deliberately a notification and not
    /// a second state machine: the words are <see cref="WorkerMergeState"/>'s own, and this queue stays
    /// the only thing that decides them.</para>
    ///
    /// <para>Null in every test that does not care. Never allowed to fail a transition — see the guard at
    /// the call site.</para>
    /// </param>
    public MergeQueue(
        string repoHash,
        string currentMainSha,
        IMergeQueueStore store,
        IVerificationStore verifications,
        Func<string, CancellationToken, Task<VerificationRecord>> runVerification,
        Func<string, CancellationToken, Task>? requeue = null,
        IReadOnlyList<IMergeGate>? gates = null,
        IAuditLog? audit = null,
        Func<DateTimeOffset>? clock = null,
        Action<string, WorkerMergeState>? onStateChanged = null)
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
        _onStateChanged = onStateChanged;

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

        // The measured reason this entry was resting at Working BEFORE anyone asked for a run — captured
        // here because the transition below is about to delete it (SetStateLocked retires the cascade's
        // refusal on any move OFF Working, correctly, for a run that actually happens). A run that is
        // REFUSED never happens, and it must not be able to erase the only sentence naming why the branch
        // needs a person. See the catch below for the defect this closes.
        WorkingReason? reasonBeforeRun;

        lock (_gate)
        {
            if (!_verifying.Add(agentId))
            {
                throw new InvalidOperationException($"A verification for '{agentId}' is already in flight.");
            }

            reasonBeforeRun = _workingReasons.TryGetValue(agentId, out var measuredBefore)
                ? measuredBefore
                : null;

            try
            {
                // Transition into Verifying (legal from Working / StaleVerified / Verifying-resume).
                SetStateLocked(agentId, WorkerMergeState.Verifying);

                // Opens the mid-run window. Anything the queue is TOLD about this branch from here until
                // the settle below happened while its own tests were running.
                _tipDuringRun.Remove(agentId);
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
        catch (Exception ex)
        {
            lock (_gate)
            {
                _verifying.Remove(agentId);
                // A failed run surfaces the branch back to Working (not silently retried — edge row 2).
                SettleAfterVerificationLocked(agentId, WorkerMergeState.Working);
                RestoreWorkingReasonAfterRefusedRunLocked(agentId, reasonBeforeRun, ex);
            }
            Changed?.Invoke();
            throw;
        }

        lock (_gate)
        {
            _verifying.Remove(agentId);
            _lastVerification[agentId] = record;

            // Did the branch move while its own tests were running? Answered ONLY from what the queue was
            // told inside the run window — see _tipDuringRun for why a comparison against the last known
            // tip is not the same question and gets the cascade wrong.
            var movedMidRun = _tipDuringRun.TryGetValue(agentId, out var during)
                && !string.Equals(during, record.BranchSha, StringComparison.Ordinal);
            _tipDuringRun.Remove(agentId);

            // The queue's knowledge of the branch advances to the newest tip it can justify: the one the
            // run measured, or the one that overtook it. This writer is not redundant with the watcher —
            // the verification path re-publishes the agent's ref itself (MG-3), so the sweep that follows
            // reports `Unchanged` and never announces the tip it just verified, and a freshness compare
            // against an older observed tip would then refuse a genuinely fresh run.
            if (movedMidRun)
            {
                _branchTip[agentId] = during;
            }
            else if (!string.IsNullOrEmpty(record.BranchSha))
            {
                _branchTip[agentId] = record.BranchSha;
            }

            if (record.Passed && movedMidRun)
            {
                // The agent committed while its own tests were running. The verdict is true and it is
                // about a tree nobody is going to merge, so it must not become a green: settling Verified
                // here is the freeze all over again, one run later and with a fresher timestamp on it.
                // Working is where an entry with no evidence about its current tip belongs, and the
                // readiness trigger picks the new tip up on its next sweep.
                //
                // The RECORD is still inserted and still returned — it is immutable history and the
                // caller asked for it. What is refused is promoting it to this entry's standing evidence.
                _lastVerification[agentId] = null;
                _verifiedAt[agentId] = null;
                // Not sandbox-aware: nothing here probed the jail. A stranded entry carrying this reason
                // gets StrandedReason instead — see WorkingReason.
                _workingReasons[agentId] = new WorkingReason(BranchMovedReason, AccountsForMissingSandbox: false);
                SettleAfterVerificationLocked(agentId, WorkerMergeState.Working);
            }
            else if (record.Passed)
            {
                _verifiedAt[agentId] = record.When;
                SettleAfterVerificationLocked(agentId, WorkerMergeState.Verified, verifiedAt: record.When);
            }
            else
            {
                // H2 — a FAILED verification is its own outcome and gets its own state. This used to settle
                // to Working, which is where an entry that has NEVER been verified sits, so a red run was
                // indistinguishable from no run at all: the rail and the worker pane both said "not
                // verified yet" about a branch whose tests had just failed, Verify was still offered, and
                // the only way to see the failure was to pay for a second run. Failure surfaced, not
                // silently retried (edge row 2) — and now surfaced as itself.
                SettleAfterVerificationLocked(agentId, WorkerMergeState.VerificationFailed);
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
    /// <param name="agentId">The entry whose branch landed.</param>
    /// <param name="newMainSha">The post-merge <c>main@sha</c>.</param>
    /// <param name="authorization">Who authorised it and by which path — carried into
    /// <see cref="MergedEvent"/>. Null records the merge as
    /// <see cref="MergeAuthorization.Unattributed"/> rather than inventing an actor.</param>
    public void ConfirmHumanMerge(string agentId, string newMainSha, MergeAuthorization? authorization = null)
    {
        Dictionary<string, string> merged;
        lock (_gate)
        {
            merged = BuildMergedPayloadLocked(agentId, newMainSha, authorization);
            MarkMergedLocked(agentId);
        }

        NotifyMainMoved(newMainSha);
        _audit.Append(new AuditEvent(MergedEvent, merged));
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
    /// <param name="authorization">Who authorised it and by which path — carried into
    /// <see cref="MergedEvent"/>. Null records the merge as
    /// <see cref="MergeAuthorization.Unattributed"/> rather than inventing an actor.</param>
    public bool TryConfirmHumanMerge(
        string agentId, string newMainSha, string? expectedMainSha, out string reason,
        MergeAuthorization? authorization = null)
    {
        Dictionary<string, string> merged;
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

            // Built BEFORE the transition, under the same lock that decided it: the pre-merge main, the
            // state the entry merged FROM, and the gate evidence are all facts about the instant the gates
            // passed. Read after MarkMergedLocked they would describe the world the merge had already
            // changed — an audit record of its own effect.
            merged = BuildMergedPayloadLocked(agentId, newMainSha, authorization);
            MarkMergedLocked(agentId);
        }

        // Outside the lock: the cascade re-queues co-tenants and raises Changed.
        NotifyMainMoved(newMainSha);
        _audit.Append(new AuditEvent(MergedEvent, merged));
        reason = "";
        return true;
    }

    /// <summary>
    /// The <see cref="MergedEvent"/> payload for a merge that is about to be recorded. Caller holds
    /// <c>_gate</c>; the append itself happens outside it (the chained log does I/O and may throw).
    ///
    /// <para>What it has to answer, because these are the questions someone reading the chain after a bad
    /// merge actually asks: <b>who</b> authorised it and through which path, <b>which branch</b> and under
    /// <b>which lease</b>, <b>which shas</b> main moved between, <b>which verification record</b> the
    /// merge rode on (and whether that record was even measured against the main it merged into), and
    /// <b>what the gates had established</b> — i.e. which flagged items a human waived to get here.</para>
    /// </summary>
    private Dictionary<string, string> BuildMergedPayloadLocked(
        string agentId, string newMainSha, MergeAuthorization? authorization)
    {
        var auth = authorization ?? MergeAuthorization.Unattributed;
        var fields = new Dictionary<string, string>
        {
            ["repo"] = _repoHash,
            ["agent"] = agentId,
            ["by"] = string.IsNullOrWhiteSpace(auth.By) ? "unknown" : auth.By,
            ["source"] = auth.Source,
            ["lease"] = auth.LeaseId,
            ["from_state"] = GetStateLocked(agentId).ToString(),
            ["pre_main_sha"] = _currentMainSha,
            ["post_main_sha"] = newMainSha ?? string.Empty,
            ["when"] = _clock().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        };

        // The verification this merge relied on. Its ABSENCE is a real and important state — the boot
        // reconcile records merges for entries this queue may never have verified — so it is stated as
        // such rather than rendered as a row of empty strings that reads like a verification with blank
        // fields.
        if (_lastVerification.TryGetValue(agentId, out var record) && record is not null)
        {
            fields["verification_main_sha"] = record.MainSha ?? string.Empty;
            fields["verification_branch_sha"] = record.BranchSha ?? string.Empty;
            fields["verification_passed"] = record.Passed ? "true" : "false";
            fields["verification_command"] = record.ResolvedCommand ?? string.Empty;
            fields["verification_config_hash"] = record.ConfigHash ?? string.Empty;
            fields["verification_when"] =
                record.When.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            fields["verification"] = "none recorded";
        }

        // Per gate, in the order the queue ANDs them, so the line reads the same way twice running. A gate
        // with nothing to say is omitted rather than padded with "(none)" — an empty evidence list is
        // itself the honest statement that this queue had no must-acknowledge gates wired.
        var evidence = new List<string>();
        foreach (var gate in _gates)
        {
            var line = gate.MergeEvidence(agentId);
            if (!string.IsNullOrWhiteSpace(line))
            {
                evidence.Add(line!);
            }
        }

        fields["gates"] = evidence.Count == 0 ? "no gates wired" : string.Join("; ", evidence);
        return fields;
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
            _workingReasons.Remove(agentId);
            SetStateLocked(agentId, WorkerMergeState.Working);
        }

        Changed?.Invoke();
    }

    /// <summary>The render-verbatim gate reason for an entry whose branch moved out from under its own
    /// verification. Public so the surfaces and the tests name the same sentence.</summary>
    public const string BranchMovedReason =
        "the branch has new commits since it was verified — re-verifying";

    /// <summary>
    /// The mirror's <c>refs/heads/agent/&lt;id&gt;</c> for this entry advanced to <paramref name="newSha"/>.
    /// Records the tip, and — for the two states in which stale evidence would GRANT a merge — invalidates
    /// the verification the way <see cref="NotifyNewCommits"/> does.
    ///
    /// <h3>Why this exists, and why it is not a new state</h3>
    ///
    /// <para><b>The defect.</b> <c>Verified</c> was a trap door. Nothing walked a locally-spawned agent's
    /// entry out of it for the agent's OWN new commits: <see cref="NotifyNewCommits"/> had exactly two
    /// callers — <c>ExternalPrIntake</c> (an upstream PR head moved) and the dev queue seeder — and neither
    /// fires for a worker in a jail. <c>WorkerReadinessTrigger</c> only starts runs from
    /// <c>Working</c>/<c>StaleVerified</c>/<c>VerificationFailed</c>, so nothing re-verified; the human
    /// Verify button was still offered and threw <c>Verified → Verifying</c> every time; and
    /// <c>ArmFlaggedChangeReview</c> runs only inside a verification, so the F6 out-of-scope gate stayed
    /// armed against the diff of two commits ago. The cockpit therefore offered <b>Merge</b> for a tip
    /// carrying an out-of-scope change and arithmetic that fails the repo's own tests, under the word
    /// "verified" (2026-08-30, agent <c>4c43d17a</c>).
    ///
    /// <para><b>Why <c>Working</c> and not <c>StaleVerified</c>.</b> They look like siblings and they are
    /// not. <c>StaleVerified</c> says <i>the evidence is still ABOUT this tree, but was measured against an
    /// old main</i>: the branch's bytes are unchanged, so every acknowledgment still binds to the same
    /// flagged-set hash, the F6 scope verdict still holds, and the honest remedy is mechanical — rebase
    /// onto the new main and re-run (which is exactly what the cascade does, and why it KEEPS the record).
    /// The agent's own commits invalidate something else entirely: the evidence is about a <b>tree that no
    /// longer exists</b>. Its diff is different, its flagged set is different, every ack is void, and its
    /// scope classification was computed from bytes nobody will merge. There is nothing to rebase and
    /// nothing to salvage — there is only "verify the new thing". Routing it through <c>StaleVerified</c>
    /// would put it in the cascade, retain a <see cref="VerificationRecord"/> and a
    /// <c>VerifiedAtUtc</c> that assert a verdict about a vanished tree, and let the rail keep reading
    /// "was green, just needs a refresh" about work nobody has ever tested.</para>
    ///
    /// <para>The state that already means <i>there is no evidence about this branch</i> is <c>Working</c>,
    /// <c>Verified → Working</c> is <b>already</b> a legal edge documented as "new commits from the agent
    /// invalidate", and <see cref="NotifyNewCommits"/> already implements it exactly. So this change adds no
    /// state and no edge. It adds the missing CALLER — which is what the defect always was.</para>
    ///
    /// <h3>Which states it acts on</h3>
    ///
    /// <para>State is moved for <c>Verified</c> and <c>AwaitingReview</c> only: they are precisely the two
    /// <see cref="CanMergeLocked"/> admits, i.e. the two where stale evidence is not merely wrong but
    /// dangerous. <c>StaleVerified</c> and <c>VerificationFailed</c> already refuse the merge AND are
    /// already in the trigger's auto-verify whitelist, so they need nothing here — and demoting
    /// <c>VerificationFailed</c> would erase the red verdict a human is reading and replace it with "not
    /// verified yet", the exact conflation H2 exists to end. <c>Verifying</c> is left alone because the run
    /// owns the entry; the settle in <see cref="RunVerificationAsync"/> compares the tip it measured
    /// against the tip recorded here and refuses to hand a green to a tree that moved mid-run.</para>
    /// </summary>
    /// <param name="agentId">The entry whose branch moved.</param>
    /// <param name="newSha">The mirror's new tip. Empty is ignored — an unknown tip must never be written
    /// over a known one, or the freshness compare would start declining to answer.</param>
    /// <returns>True when this call invalidated a verification (i.e. moved the entry to <c>Working</c>).</returns>
    public bool NotifyBranchAdvanced(string agentId, string newSha)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(newSha))
        {
            return false;
        }

        bool invalidated;
        lock (_gate)
        {
            var state = GetStateLocked(agentId);
            if (IsTerminal(state))
            {
                return false;
            }

            var previous = _branchTip.TryGetValue(agentId, out var t) ? t : string.Empty;
            _branchTip[agentId] = newSha;

            // A run owns this entry: the state must not move under it (the settle would then throw
            // Working → Verified out of a background completion nobody is awaiting). Record the move
            // instead, and let RunVerificationAsync's settle refuse to promote a verdict the branch has
            // already overtaken.
            if (state == WorkerMergeState.Verifying)
            {
                _tipDuringRun[agentId] = newSha;
                return false;
            }

            // Nothing to invalidate: either this is the tip the entry already stands on (announced twice,
            // or announced back to the queue after its own verification measured it), or the entry is in a
            // state where stale evidence cannot grant a merge — see the note above for why that set is
            // exactly the two states CanMerge admits.
            invalidated = state is WorkerMergeState.Verified or WorkerMergeState.AwaitingReview
                && !string.Equals(previous, newSha, StringComparison.Ordinal)
                && !string.Equals(VerifiedBranchShaLocked(agentId), newSha, StringComparison.Ordinal);

            if (!invalidated)
            {
                return false;
            }

            _lastVerification[agentId] = null;
            _verifiedAt[agentId] = null;
            // Said out loud rather than left to fall into "not verified yet": the human was one click from
            // merging this entry a moment ago, and the reason it is no longer offered is a fact about
            // THEIR branch, not a generic absence.
            // Not sandbox-aware: this was decided from a ref moving in the mirror, with no question asked
            // about the agent's jail. A stranded entry carrying this reason gets StrandedReason instead.
            _workingReasons[agentId] = new WorkingReason(BranchMovedReason, AccountsForMissingSandbox: false);
            SetStateLocked(agentId, WorkerMergeState.Working);
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>The branch tip the entry's last verification was measured on, or empty when there is no
    /// record or the record predates the field. Caller holds <c>_gate</c>.</summary>
    private string VerifiedBranchShaLocked(string agentId) =>
        _lastVerification.TryGetValue(agentId, out var r) && r is not null ? r.BranchSha : string.Empty;

    /// <summary>
    /// The newest branch tip this queue has been told about for an entry, or null when it has never been
    /// told one. Exposed so the daemon's projection and the tests read the same value the merge gate does.
    /// </summary>
    public string? ObservedBranchTip(string agentId)
    {
        lock (_gate)
        {
            return _branchTip.TryGetValue(agentId, out var tip) ? tip : null;
        }
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
    /// <para><b>The verification RECORD is kept</b> — and this is the half that was wrong. The
    /// <c>VerifiedAtUtc</c> stamp goes, because the entry is no longer verified against anything it could
    /// merge into; the record itself stays, because it is still evidence about THIS tree. That is exactly
    /// the distinction <c>StaleVerified</c> is built on (§19.3: "the evidence is still ABOUT this tree,
    /// measured against an old main"), and this method is reached only from entries the cascade just moved
    /// through <c>StaleVerified</c> — the branch's bytes did not change, only its parentage. Clearing it
    /// erased the one thing the row could still honestly say. Observed live on 2026-08-30: three
    /// co-tenants of a merge, each holding a PASSING <c>VerificationRow</c> (ids 49, 50, 52), rendered
    /// "Not verified yet — no test run has been recorded for this branch."</para>
    ///
    /// <para><b>And it cannot leak into a merge.</b> <see cref="CanMergeLocked"/> reads the record only for
    /// <c>Verified</c>/<c>AwaitingReview</c>; the only edge out of <c>Working</c> that is not terminal is
    /// <c>Verifying</c>, whose settle overwrites the record before it can reach either. A retained record
    /// is readable history and never standing evidence.</para>
    /// </summary>
    /// <param name="agentId">The entry the cascade could not reparent.</param>
    /// <param name="reason">Render-verbatim explanation (§3.4 vocabulary), e.g. the missing jail or the
    /// rebase conflict. Empty falls back to the generic wording.</param>
    /// <param name="detail">Optional extra field for the audit event (never shown as the gate reason).</param>
    /// <param name="sandboxIsGone">True when <paramref name="reason"/> was produced BY establishing that
    /// this entry has no live sandbox. It makes the reason outrank <see cref="StrandedReason"/>, which
    /// would otherwise replace the cascade's own measurement with a strictly less informative restatement
    /// of half of it. Leave false for every reason measured with a live jail in hand — see
    /// <see cref="WorkingReason"/>.</param>
    /// <returns>False when the entry is unknown or already terminal — a human discard that landed while
    /// the cascade was running has decided, and this must not walk it back.</returns>
    public bool TryReturnToWorking(
        string agentId, string reason, string? detail = null, bool sandboxIsGone = false)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(agentId, out var from) || IsTerminal(from))
            {
                return false;
            }

            _verifiedAt[agentId] = null;
            if (!string.IsNullOrWhiteSpace(reason))
            {
                _workingReasons[agentId] = new WorkingReason(reason, sandboxIsGone);
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
            _workingReasons.Remove(agentId);
            _branchTip.Remove(agentId);
            _tipDuringRun.Remove(agentId);
            _stranded.Remove(agentId);
            _jailMeasured.Remove(agentId);
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

    // ---- Jail reconcile (ISSUES-LOG #24) ---------------------------------

    /// <summary>
    /// Whether this entry has a live sandbox, as of the last <see cref="ReconcileJails"/> pass.
    /// <c>null</c> means <b>not measured</b> — no pass has run — and is a materially different answer from
    /// <c>false</c>: the surface withholds Verify on <c>false</c> and offers it on <c>null</c>, because
    /// removing the only action an entry has on the strength of a fact nobody established is the worse
    /// mistake. Answers <c>null</c> for an id this queue does not track.
    /// </summary>
    public bool? HasLiveJail(string agentId)
    {
        lock (_gate)
        {
            if (!_jailMeasured.Contains(agentId) || !_states.ContainsKey(agentId))
            {
                return null;
            }

            return !_stranded.Contains(agentId);
        }
    }

    /// <summary>
    /// ISSUES-LOG #24 — reconciles every non-terminal entry's <b>jail-liveness</b> against
    /// <paramref name="hasLiveJail"/>, the same probe the restart resume uses.
    ///
    /// <para><b>The gap this closes.</b> A queue entry's state is push-only, exactly as
    /// <c>AgentSession.State</c> was before <c>AgentSessionReconciler</c>: something calls a transition, or
    /// the row never moves. Stopping an agent is not a queue transition and neither is a jail dying —
    /// <c>docker rm</c> run by hand, an OOM kill, an engine restart, a daemon restart — so an entry keeps
    /// reporting <c>Working</c> about an agent that has not existed for days, with Verify offered on it.
    /// Found live on 2026-08-22: 15 <c>Working</c> rows three days stale, against exactly ONE real
    /// container on the machine.</para>
    ///
    /// <para><b>It moves no merge state, and that is the decision, not a shortcut.</b> The obvious fix — walk
    /// a jail-less entry to <c>Discarded</c> — would destroy the affordance this product just built:
    /// <c>AgentResumeService</c> exists precisely to give a stranded entry a live jail again on its own
    /// branch, with its commits and its verification history intact, "so it can be verified and merged
    /// instead of only discarded". <c>Discarded</c> is terminal with no path back and <c>EnsureEntry</c>
    /// cannot resurrect the id, so an automatic discard would silently convert every recoverable entry into
    /// an unrecoverable one — a reconcile pass reaping user work while the user is looking the other way,
    /// which is the failure this area has already paid for once. What was wrong was never the state word;
    /// it was that liveness was asserted from a store nothing corrected. So liveness is what gets
    /// corrected, the human keeps both Resume and Discard, and nothing is thrown away.</para>
    ///
    /// <para><b>Terminal entries are skipped</b> — a Merged branch's jail being gone is not news — and so is
    /// an entry with a verification genuinely in flight, which by construction has a jail it is running
    /// in.</para>
    /// </summary>
    /// <param name="hasLiveJail">agentId → does this entry have a live sandbox right now. Allowed to throw;
    /// a probe that fails counts as "not established", which leaves the entry exactly where it is rather
    /// than stranding it on a Docker hiccup.</param>
    /// <returns>The entries that moved, in each direction. Never throws.</returns>
    public MergeQueueJailReport ReconcileJails(Func<string, bool> hasLiveJail)
    {
        ArgumentNullException.ThrowIfNull(hasLiveJail);

        List<string> candidates;
        lock (_gate)
        {
            candidates = _states
                .Where(kv => !IsTerminal(kv.Value) && !_verifying.Contains(kv.Key))
                .Select(kv => kv.Key)
                .ToList();
        }

        var stranded = new List<string>();
        var recovered = new List<string>();

        foreach (var agentId in candidates)
        {
            bool live;
            try
            {
                live = hasLiveJail(agentId);
            }
            catch (Exception)
            {
                // Not "no jail" — "no answer". Stranding an entry because a probe threw would make an
                // unreachable container engine read as the whole queue losing its agents at once, which is
                // the mass-mismarking AgentSessionReconciler's own lister is written to avoid.
                continue;
            }

            lock (_gate)
            {
                // Re-read under the lock: the probe runs outside it and a human discard (or a cancel) that
                // landed in that window has decided already.
                if (!_states.TryGetValue(agentId, out var state) || IsTerminal(state))
                {
                    _stranded.Remove(agentId);
                    _jailMeasured.Remove(agentId);
                    continue;
                }

                _jailMeasured.Add(agentId);
                if (live)
                {
                    if (!_stranded.Remove(agentId))
                    {
                        continue;
                    }

                    recovered.Add(agentId);
                }
                else
                {
                    if (!_stranded.Add(agentId))
                    {
                        continue;
                    }

                    stranded.Add(agentId);
                }
            }
        }

        lock (_gate)
        {
            // Ids that left the queue entirely (Cancel) must not keep a mark that outlives them.
            _stranded.RemoveWhere(id => !_states.ContainsKey(id));
            _jailMeasured.RemoveWhere(id => !_states.ContainsKey(id));
        }

        var report = new MergeQueueJailReport(stranded, recovered);
        if (!report.Changed)
        {
            return report;
        }

        foreach (var agentId in stranded)
        {
            AuditJail(agentId, "stranded");
        }

        foreach (var agentId in recovered)
        {
            AuditJail(agentId, "recovered");
        }

        // The republish is the other half of the fix and is why this is NotifyGateChanged rather than
        // nothing: no merge state moved, so the queue stream — which re-pushes only on Changed — would
        // otherwise go on serving the liveness the client last heard. A rail rendering Verify for an entry
        // the daemon now knows has no jail is the whole user-visible symptom.
        NotifyGateChanged();
        return report;
    }

    private void AuditJail(string agentId, string outcome) =>
        _audit.Append(new AuditEvent(JailReconciledEvent, new Dictionary<string, string>
        {
            ["repo"] = _repoHash,
            ["agent"] = agentId,
            ["by"] = ReconcilerActor,
            ["outcome"] = outcome,
            ["state"] = GetState(agentId).ToString(),
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
                // FIRST, and ahead of the jail-liveness arm below: a measured reason that was itself
                // derived from the missing sandbox. It says everything StrandedReason says and also why
                // the branch is at Working — "this branch needs rebasing onto the new main AND its agent
                // has no live sandbox — resume the agent". Ordering StrandedReason above it replaced the
                // cascade's own measurement with a less informative restatement of half of it, which is
                // what the three co-tenants of the 2026-08-30 merge were shown while the daemon log
                // carried the honest sentence, and which contradicted MergeQueueProvisioner.Block's own
                // comment ("rendered verbatim as the CanMerge reason"). See WorkingReason for why this is
                // a flag on the reason and not a blanket reorder: a reason measured with a LIVE jail in
                // hand ("the agent is paused with the rebase in progress") would, on an entry whose
                // sandbox has since gone, send a human into a container that no longer exists.
                WorkerMergeState.Working
                    when _workingReasons.TryGetValue(agentId, out var measured)
                        && (measured.AccountsForMissingSandbox || !_stranded.Contains(agentId))
                    => measured.Reason,
                // ISSUES-LOG #24 — the jail-liveness axis outranks both of these, because both of them
                // promise something that cannot happen without a sandbox. "Re-verifying" is the cascade's
                // promise and the cascade needs a jail to keep it; "not verified yet" is a sentence about a
                // branch still on its way, and this one is not on its way anywhere until a person acts.
                // Deliberately not applied to Verifying, whose "verification stalled — no run in progress"
                // already says the true thing about that row, nor to the terminal words, which are final
                // whatever the container engine reports.
                WorkerMergeState.StaleVerified or WorkerMergeState.Working
                    when _stranded.Contains(agentId) => StrandedReason,
                WorkerMergeState.StaleVerified => "verification is stale — re-verifying",
                // "verifying" is only true while something is actually running. Saying it about a row
                // that merely persists Verifying — the shape a daemon restart leaves behind — reports an
                // activity to the human that no longer exists, and it is the reason those entries read as
                // permanently busy instead of as needing a hand.
                WorkerMergeState.Verifying => _verifying.Contains(agentId)
                    ? "verifying"
                    : "verification stalled — no run in progress",
                // H2 — the red verdict, said out loud and with the command that produced it. The whole
                // reason this state exists is that this sentence used to be "not verified yet", which is a
                // statement about a branch nobody has tested. The gate reason is rendered verbatim, so it
                // is the one place a human reliably sees the outcome without paying for a second run; it
                // names the command so the failure is attributable, and points at the output the entry's
                // own verification log now carries (H4).
                WorkerMergeState.VerificationFailed => VerificationFailedReason(agentId),
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

        // The BRANCH-side half of freshness. The compare above proves the evidence was measured against
        // the main this branch will merge INTO; this one proves it was measured on the branch that will
        // merge. Without it "fresh" was one-sided, and a Verified entry whose worker pushed three more
        // commits was still, by every check the daemon made, ready to merge.
        //
        // This is a belt, not the mechanism: NotifyBranchAdvanced has already walked such an entry out of
        // Verified, so a queue whose invalidation is wired reaches here with the two shas equal. It stays
        // because the invalidation depends on a ref watcher — an observation that can be missed, delayed,
        // or absent on a substrate that has no watcher at all — and the failure it guards is a human being
        // shown a green Merge button for untested code. A gate that only holds while an event fires is not
        // a gate.
        //
        // Both sides must be KNOWN to refuse: an empty BranchSha is a record from the seeded path or from
        // before the field existed, and an absent observed tip is a queue nobody has told. Neither is
        // evidence of drift, and inventing a refusal from ignorance would make every pre-existing record
        // unmergeable forever.
        if (!string.IsNullOrEmpty(record.BranchSha)
            && _branchTip.TryGetValue(agentId, out var tip)
            && !string.IsNullOrEmpty(tip)
            && !string.Equals(record.BranchSha, tip, StringComparison.Ordinal))
        {
            reason = BranchMovedReason;
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

    /// <summary>
    /// The verbatim gate reason for a <see cref="WorkerMergeState.VerificationFailed"/> entry. Caller
    /// holds <c>_gate</c>.
    ///
    /// <para>The command is included when the record has one and omitted when it does not, rather than
    /// substituted: a reason that names a command the queue cannot actually evidence would be the same
    /// class of fabrication as the "not verified yet" it replaces. The fallback wording still says the one
    /// load-bearing thing — the tests FAILED — because that fact comes from the state, which only the
    /// record-carrying arm of <see cref="RunVerificationAsync"/> can set.</para>
    /// </summary>
    private string VerificationFailedReason(string agentId)
    {
        var record = _lastVerification.TryGetValue(agentId, out var r) ? r : null;
        var command = record?.ResolvedCommand;
        return string.IsNullOrWhiteSpace(command)
            ? "the verification FAILED — read the run output, then push a fix or discard the entry"
            : $"the verification FAILED ({command}) — read the run output, then push a fix or discard the entry";
    }

    /// <summary>
    /// The most recent verification this queue settled for an entry — <b>pass or fail</b> — or null when
    /// it has never produced one.
    ///
    /// <para>H4: the daemon held this record and nothing carried it out. The wire's <c>QueueEntry</c> had
    /// no verification field at all and the client's projection hardcoded <c>Verification: null</c>, so a
    /// human looking at a failed branch was shown a one-line gate reason and had no route of any kind to
    /// the stdout/stderr the run actually produced — which sat in an artifact file on the daemon's disk
    /// that nothing linked to. This is the accessor that lets the transport carry it.</para>
    ///
    /// <para>Deliberately the queue's own settled record rather than a store lookup: it is the exact
    /// record the state was decided from, so what a human reads can never disagree with the state word
    /// they are reading it under.</para>
    /// </summary>
    public VerificationRecord? LastVerification(string agentId)
    {
        lock (_gate)
        {
            return _lastVerification.TryGetValue(agentId, out var r) ? r : null;
        }
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

    /// <summary>
    /// Puts a <c>Working</c> entry's render-verbatim reason back after a verification that was
    /// <b>refused</b> — and, when there was none to put back, records the refusal's own words instead.
    ///
    /// <para><b>The defect.</b> Pressing Verify on an entry the stale cascade had blocked used to make
    /// the surface say LESS than it did before the press. <see cref="RunVerificationAsync"/> transitions
    /// to <c>Verifying</c> first, <see cref="SetStateLocked"/> drops the measured reason on any move off
    /// <c>Working</c>, and then the run refuses (no jail, no verification command, a worktree parked on a
    /// detached HEAD mid-rebase) and settles the entry straight back to <c>Working</c> — now with nothing
    /// to say. The accurate refusal went to the daemon log and to gRPC's <c>FailedPrecondition</c>, and
    /// the entry's own gate line fell back to the generic "not verified yet": the human's one available
    /// action deleted the actionable sentence it was offered next to. Observed live on a rebase-conflict
    /// entry, whose card went from "the agent is paused with the rebase in progress and needs a human to
    /// resolve it" to "not verified yet".</para>
    ///
    /// <para><b>Why the prior reason wins over the refusal's.</b> Nothing ran, so nothing about the world
    /// changed: the cascade's measurement is still the most specific true statement about this entry, and
    /// it is the one written for a human to act on. The refusal is the second-best answer and is used only
    /// when there was no measurement to keep — which is the ordinary case for an entry that had simply
    /// never been verified.</para>
    ///
    /// <para><b>And only for a TYPED refusal.</b> The set mirrors <c>MergeQueueGrpcService.RunVerification</c>'s
    /// own catch filter exactly: those are the messages the daemon already puts on the wire verbatim, so
    /// recording one here exposes nothing new. Anything else — an IO fault, a cancellation — keeps its
    /// message off a render-verbatim surface rather than leaking whatever a stack happened to contain.</para>
    /// </summary>
    private void RestoreWorkingReasonAfterRefusedRunLocked(
        string agentId, WorkingReason? reasonBeforeRun, Exception failure)
    {
        // The settle above is a no-op for an entry that went terminal mid-run (a human discard). Writing a
        // Working reason onto a Discarded row would put a "this branch needs…" sentence under a decision
        // that is already made.
        if (GetStateLocked(agentId) != WorkerMergeState.Working)
        {
            return;
        }

        if (reasonBeforeRun is { } measured)
        {
            _workingReasons[agentId] = measured;
            return;
        }

        var quotable = failure is NoVerificationCommandException
            or MalformedVerificationCommandException
            or Mainguard.Git.Exceptions.ToolchainProvisioningException
            or InvalidOperationException;
        if (quotable && !string.IsNullOrWhiteSpace(failure.Message))
        {
            // Not sandbox-aware: this reason was not derived from probing the jail, so a stranded entry
            // still gets StrandedReason — see WorkingReason.
            _workingReasons[agentId] = new WorkingReason(failure.Message, AccountsForMissingSandbox: false);
        }
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
            _workingReasons.Remove(agentId);
        }

        _states[agentId] = target;
        SaveRowLocked(agentId, verifiedAt);

        // AFTER the row is persisted, so nothing can be told a state the store does not yet hold — and
        // only for a REAL move, because a notification for a transition that did not happen is how a
        // reporting seam starts describing something other than the state machine.
        if (from != target)
        {
            NotifyStateChangedLocked(agentId, target);
        }
    }

    /// <summary>
    /// Reports one transition to <c>onStateChanged</c>, swallowing anything it throws.
    ///
    /// <para><b>Reporting a state may never fail a transition.</b> The row is already written when this
    /// runs; letting an exception out would abort the caller mid-move and leave the persisted state and
    /// the in-memory state disagreeing about a branch's merge eligibility. Same posture, for the same
    /// reason, as <c>MergeQueueProvisioner.MarkRunState</c>.</para>
    ///
    /// <para>Called under <c>_gate</c>, deliberately. The alternative — deferring to the ~15 sites that
    /// raise <see cref="Changed"/> outside the lock — is a second, hand-maintained list of transition
    /// points, and the one that gets forgotten is the one that stops reporting. The sink is a bounded,
    /// non-blocking in-memory write (<c>AgentSessionStore.MarkState</c> → <c>TryWrite</c>), strictly
    /// cheaper than the SQLite <c>Save</c> this method already performs under the same lock, and nothing
    /// in the session store ever calls back into a queue.</para>
    /// </summary>
    private void NotifyStateChangedLocked(string agentId, WorkerMergeState target)
    {
        if (_onStateChanged is null)
        {
            return;
        }

        try
        {
            _onStateChanged(agentId, target);
        }
        catch (Exception)
        {
            // Deliberately swallowed and deliberately not logged from here: this type has no log sink,
            // and the only caller that supplies a sink (MergeQueueProvisioner) logs its own failures.
        }
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
