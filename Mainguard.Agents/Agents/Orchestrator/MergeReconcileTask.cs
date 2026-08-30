using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// RT-D1 crash-mid-merge reconciliation (P2-10 §3.7, M7 exit gate). Replaces the boot placeholder in
/// the <c>merge-reconcile</c> slot, which runs FIRST in the <c>DaemonBootSequence</c> — <b>before</b> the
/// swarm reconciler and before admission accepts any new <c>BeginMerge</c>.
///
/// <para>For every repo with an outstanding (unconfirmed) merge lease at boot, it replays the
/// <see cref="IForegroundMergeService"/> T-19 Windows-side journal:</para>
/// <list type="bullet">
///   <item>A committed-but-unrecorded merge — <b>proved by identity</b>, see
///   <see cref="ReconcileVerdict"/> → <b>synthesize the <c>ConfirmMerge</c> idempotency record</b> and
///   fire <c>NotifyMainMoved</c> for the sha main really holds.</item>
///   <item>A never-committed merge (main unchanged) → release the lease and surface the interrupted
///   attempt for the human to retry.</item>
///   <item>A merge the daemon <b>cannot decide about</b> → release the lease, record NOTHING, and say so.
///   K1: the destructive act here is the synthesized terminal <c>Merged</c>, so an unanswerable question
///   is a "no" — the <c>AgentBranchReapVerdict.Undecidable</c> precedent.</item>
/// </list>
/// The outcome is always <b>exactly once or none</b> — never a double-merge, never a silently
/// half-recorded merge.
///
/// <para><b>K1 — what this used to do, and why it was the worst of the stale-evidence set.</b> The old
/// predicate was <c>currentMain != lease.ExpectedMainSha</c> <b>AND</b> "the repo's journal contains
/// <i>any</i> entry of kind <c>Merge</c>" — unfiltered by lease, agent, branch, sha or time. A
/// <c>git pull</c>, a co-tenant's merge or a hand commit satisfies the first; a merge somebody performed
/// last month satisfies the second. Two facts about a repository, neither of them about <i>this lease</i>,
/// were enough to fabricate a terminal <c>Merged</c> for an entry that had never merged and to fire the
/// whole stale cascade at the shas of a merge that never happened. Coincidence is not evidence, and
/// evidence that does not record what it is evidence FOR is coincidence.</para>
/// </summary>
public sealed class MergeReconcileTask : IBootTask
{
    private readonly IMergeLeaseStore _leases;
    private readonly IOperationJournal _journal;
    private readonly Func<string, string?> _resolveRepoPath;
    private readonly Action<string, string, string> _onMerged;
    private readonly Action<string, string>? _onInterrupted;

    /// <param name="leases">The RT-D1 lease store (outstanding leases are the reconcile input).</param>
    /// <param name="journal">The T-19 journal, replayed to detect a committed-but-unrecorded merge.</param>
    /// <param name="resolveRepoPath">Maps a repo hash → Windows repo path (null → cannot reconcile that repo yet).</param>
    /// <param name="onMerged">Fired for a synthesized confirm: <b>(repoHash, agentId, postMergeSha)</b> →
    /// <c>ConfirmHumanMerge</c>/<c>NotifyMainMoved</c>. MG-29: the repo hash is part of the callback because
    /// the cascade must be fired on the queue that actually OWNS this agent; without it the daemon had no
    /// way to resolve the owning queue and the wiring degenerated into a hardcoded no-op.</param>
    /// <param name="onInterrupted">Fired for a never-committed attempt: (repoHash, reason) → surfaced to the UI.</param>
    public MergeReconcileTask(
        IMergeLeaseStore leases,
        IOperationJournal journal,
        Func<string, string?> resolveRepoPath,
        Action<string, string, string> onMerged,
        Action<string, string>? onInterrupted = null)
    {
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _resolveRepoPath = resolveRepoPath ?? throw new ArgumentNullException(nameof(resolveRepoPath));
        _onMerged = onMerged ?? throw new ArgumentNullException(nameof(onMerged));
        _onInterrupted = onInterrupted;
    }

    public string Name => "merge-reconcile";

    public Task RunAsync(CancellationToken ct)
    {
        foreach (var lease in _leases.AllOutstanding())
        {
            ct.ThrowIfCancellationRequested();
            Reconcile(lease);
        }

        return Task.CompletedTask;
    }

    /// <summary>What the boot reconcile concluded about one outstanding lease.</summary>
    public enum ReconcileVerdict
    {
        /// <summary>The merge this lease authorized demonstrably landed. Synthesize the confirm.</summary>
        Merged,

        /// <summary>Main is still the sha the lease expected: nothing landed. Release and surface it.</summary>
        NeverCommitted,

        /// <summary>
        /// Main moved, but nothing ties the move to <b>this</b> lease. Record nothing — the destructive act
        /// is the synthesized <c>Merged</c>, and an unanswerable question gating a destructive act is a "no".
        /// </summary>
        Undecidable,
    }

    /// <summary>Reconciles a single outstanding lease (exposed for the RT-D1 test).</summary>
    public void Reconcile(Mainguard.Git.Models.MergeLeaseRow lease)
    {
        var repoPath = _resolveRepoPath(lease.RepoHash);
        if (string.IsNullOrEmpty(repoPath))
        {
            return; // the repo isn't mounted this boot — leave the lease for a later reconcile.
        }

        var currentMain = RevParse(repoPath!, lease.MainBranch);
        switch (Classify(repoPath!, lease, currentMain))
        {
            case ReconcileVerdict.Merged:
                // Committed but not confirmed → synthesize the confirm exactly once, then fire the cascade.
                _leases.Confirm(lease.RepoHash, lease.LeaseId, currentMain);
                _onMerged(lease.RepoHash, lease.AgentId, currentMain);
                break;

            case ReconcileVerdict.NeverCommitted:
                // Never committed → release the lease; surface the interrupted attempt for a human retry.
                _leases.Release(lease.RepoHash, lease.LeaseId);
                _onInterrupted?.Invoke(lease.RepoHash,
                    $"A merge of agent/{lease.AgentId} was interrupted before it committed; no ref moved. "
                    + "Retry when ready.");
                break;

            default:
                // The lease is still handed back — holding it would block every future merge on this repo
                // until the next restart, which is the RT-D1 strand this task exists to sweep. What is NOT
                // done is the only irreversible half: nothing is marked Merged and no cascade fires. The
                // sentence names the ambiguity rather than restating one horn of it as a fact.
                _leases.Release(lease.RepoHash, lease.LeaseId);
                _onInterrupted?.Invoke(lease.RepoHash, UndecidableReason(lease, currentMain));
                break;
        }
    }

    /// <summary>
    /// What the human is told when the daemon cannot decide. The two ways of not deciding are genuinely
    /// different situations and get different sentences: a main it could not READ is a repository problem,
    /// a main that moved for reasons unconnected to this lease is a history problem. Collapsing them would
    /// send someone looking in the wrong place — and the whole point of this verdict is that the daemon
    /// says what it does not know instead of picking a horn.
    /// </summary>
    private static string UndecidableReason(Mainguard.Git.Models.MergeLeaseRow lease, string currentMain)
        => string.IsNullOrEmpty(currentMain)
            ? $"A merge of agent/{lease.AgentId} was interrupted, and '{lease.MainBranch}' could not be "
              + "read, so whether it landed cannot be established — nothing was recorded. Check the "
              + "repository, then merge again if the work is still needed."
            : $"A merge of agent/{lease.AgentId} was interrupted, and '{lease.MainBranch}' has since moved "
              + "for some other reason — nothing shows this merge landed, so it was NOT recorded. Check "
              + $"'{lease.MainBranch}', then merge again if the work is still needed.";

    /// <summary>
    /// Whether main's current sha is the effect of <b>this lease's</b> merge.
    ///
    /// <para><b>The identity a merge decision is bound to</b> is the triple (agent branch, the main it was
    /// authorized to fast-forward, the main it produced). The lease records the first two; git holds the
    /// third. So the question is asked of git wherever git can answer it — the §22
    /// <c>BranchDescendsFromMain</c> precedent — and of the journal, filtered to this lease, where it
    /// cannot.</para>
    ///
    /// <list type="number">
    ///   <item><b>Main unchanged</b> ⇒ <see cref="ReconcileVerdict.NeverCommitted"/>. Unambiguous, and the
    ///   one arm that was always right.</item>
    ///   <item><b>Main unreadable</b> ⇒ undecidable. It is not "unchanged": a repo that cannot be read
    ///   cannot be said to be at its old sha either.</item>
    ///   <item><b>Main moved and does not even descend from the sha the lease expected</b> ⇒ undecidable.
    ///   A fast-forward merge only ever moves main FORWARD from its old-OID; a main that went sideways or
    ///   backwards was moved by something that is not this merge.</item>
    ///   <item><b>Git proof</b>: main now CONTAINS <c>agent/&lt;id&gt;</c>'s tip. That is what
    ///   <c>merge --ff-only agent/&lt;id&gt;</c> means, it is measured rather than reported, and it is
    ///   false for every unrelated reason main might have moved.</item>
    ///   <item><b>Journal proof</b>, when the branch ref is gone (deleted after the merge, or never local
    ///   in this checkout): a <c>Merge</c> entry, at or after this lease began, whose own snapshots record
    ///   main moving FROM the lease's expected sha TO the sha main holds now. That is a record of this
    ///   merge, not of a merge.</item>
    ///   <item><b>Neither can answer</b> ⇒ undecidable.</item>
    /// </list>
    /// </summary>
    internal ReconcileVerdict Classify(
        string repoPath, Mainguard.Git.Models.MergeLeaseRow lease, string currentMain)
    {
        if (string.IsNullOrEmpty(currentMain))
        {
            return ReconcileVerdict.Undecidable;
        }

        if (string.Equals(currentMain, lease.ExpectedMainSha, StringComparison.Ordinal))
        {
            return ReconcileVerdict.NeverCommitted;
        }

        // A lease with no expected sha (a substrate-less double, or a row from before the field carried
        // one) cannot support the descent compare, and inventing one from ignorance would mark unrelated
        // work Merged — the exact defect. Everything below still has to prove the merge independently.
        if (!string.IsNullOrEmpty(lease.ExpectedMainSha)
            && !IsAncestor(repoPath, lease.ExpectedMainSha, currentMain))
        {
            return ReconcileVerdict.Undecidable;
        }

        // (4) Ask git. The branch is looked for locally first and then through any remote-tracking form,
        // because the user's checkout may only ever have seen agent/<id> over the sync remote.
        var branchRef = ResolveAgentRef(repoPath, lease.AgentId);
        if (branchRef is not null)
        {
            return IsAncestor(repoPath, branchRef, currentMain)
                ? ReconcileVerdict.Merged
                : ReconcileVerdict.Undecidable;
        }

        // (5) The branch ref is gone. The journal is the only remaining witness, and it is used ONLY in
        // its identity-bound form.
        return HasThisLeasesMergeEntry(repoPath, lease, currentMain)
            ? ReconcileVerdict.Merged
            : ReconcileVerdict.Undecidable;
    }

    /// <summary>
    /// A T-19 <c>Merge</c> entry that records <b>this lease's</b> merge: at or after the lease was taken,
    /// naming this lease's branch, and whose pre/post snapshots show <c>refs/heads/&lt;main&gt;</c> moving
    /// from the lease's expected sha to the sha main holds now.
    ///
    /// <para>Both snapshot reads must SUCCEED. A journal entry whose snapshot does not name main is an
    /// entry that cannot say what this merge did, and treating a missing answer as a match is how "any
    /// <c>Merge</c> entry anywhere" got written in the first place.</para>
    ///
    /// <para><b>Why this is the fallback and not the check.</b> The description is the merge service's own
    /// <i>self-report</i> of what it was merging — the one kind of evidence §22 says to prefer git over.
    /// It is used only when the branch ref is gone and git therefore has nothing to be asked, and only
    /// under the three independent constraints above; on its own it would prove nothing.</para>
    /// </summary>
    private bool HasThisLeasesMergeEntry(
        string repoPath, Mainguard.Git.Models.MergeLeaseRow lease, string currentMain)
    {
        if (string.IsNullOrEmpty(lease.ExpectedMainSha))
        {
            return false; // nothing to compare the pre-state against — this cannot be identified.
        }

        var mainRef = $"refs/heads/{lease.MainBranch}";
        return _journal.GetHistory(repoPath)
            .Any(e =>
                string.Equals(e.Kind, JournalKinds.Merge, StringComparison.Ordinal)
                && e.WhenUtc >= lease.BeginUtc
                && NamesThisBranch(e.Description, lease.AgentId)
                && OperationJournal.TryReadRef(e.PreStateJson, mainRef, out var before)
                && string.Equals(before, lease.ExpectedMainSha, StringComparison.Ordinal)
                && OperationJournal.TryReadRef(e.PostStateJson, mainRef, out var after)
                && string.Equals(after, currentMain, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether a journal entry's description names this lease's branch. Both merge legs write it: the
    /// local one as <c>Merge agent/&lt;id&gt;</c>, the P2-12 external one as
    /// <c>Merge pull request #&lt;n&gt;</c> for the <c>pr-&lt;n&gt;</c> agent id the intake mints.
    /// </summary>
    private static bool NamesThisBranch(string? description, string agentId)
    {
        if (string.IsNullOrEmpty(description) || string.IsNullOrEmpty(agentId))
        {
            return false;
        }

        if (description!.Contains($"agent/{agentId}", StringComparison.Ordinal))
        {
            return true;
        }

        return Mainguard.Agents.Services.ExternalPrMergeService.PrNumberFor(agentId) is int number
            && description.Contains($"pull request #{number}", StringComparison.Ordinal);
    }

    /// <summary>
    /// The ref this lease's <c>agent/&lt;id&gt;</c> is reachable as here — <b>unique-or-nothing</b>, or null
    /// when it is gone or ambiguous.
    ///
    /// <para>The local <c>refs/heads/</c> form wins when it exists: that is the ref the foreground merge
    /// consumed. Otherwise the branch is looked up by shape across every remote-tracking namespace, because
    /// the reconcile has no substrate facade and so does not know the SC-2 sync remote's name. If two
    /// remotes hold that branch at DIFFERENT shas, this returns nothing rather than whichever git listed
    /// first — following the <c>WorkerPlanGate.ResolveKeyLocked</c> precedent, since the caller reads "no
    /// ref" as "ask the journal instead" and an arbitrary pick could answer the containment question about
    /// a copy of the branch that is not the one that merged.</para>
    /// </summary>
    private static string? ResolveAgentRef(string repoPath, string agentId)
    {
        var branch = $"agent/{agentId}";
        if (RevParse(repoPath, $"refs/heads/{branch}").Length > 0)
        {
            return $"refs/heads/{branch}";
        }

        var (code, output, _) = GitService.RunGit(
            repoPath, "for-each-ref", "--format=%(objectname) %(refname)", $"refs/remotes/*/{branch}");
        if (code != 0)
        {
            return null;
        }

        var rows = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2))
            .Where(parts => parts.Length == 2)
            .ToList();

        if (rows.Count == 0)
        {
            return null;
        }

        return rows.Select(parts => parts[0]).Distinct(StringComparer.Ordinal).Count() == 1
            ? rows[0][1]
            : null; // two remotes, two different shas — refuse to guess which one merged.
    }

    /// <summary>True only when git ANSWERS that <paramref name="ancestor"/> is contained in <paramref name="descendant"/>.</summary>
    private static bool IsAncestor(string repoPath, string ancestor, string descendant)
    {
        var (code, _, _) = GitService.RunGit(
            repoPath, "merge-base", "--is-ancestor", ancestor, descendant);
        return code == 0;
    }

    private static string RevParse(string repoPath, string reference)
    {
        var (code, output, _) = GitService.RunGit(repoPath, "rev-parse", "--verify", reference);
        return code == 0 ? output.Trim() : string.Empty;
    }
}
