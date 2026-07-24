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
///   <item>A committed-but-unrecorded merge (main advanced past the lease's expected sha AND a
///   <c>Merge</c> journal entry exists) → <b>synthesize the <c>ConfirmMerge</c> idempotency record</b>
///   from the journal and fire <c>NotifyMainMoved</c> for the recorded post-merge sha.</item>
///   <item>A never-committed merge (main unchanged) → release the lease and surface the interrupted
///   attempt for the human to retry.</item>
/// </list>
/// The outcome is always <b>exactly once or none</b> — never a double-merge, never a silently
/// half-recorded merge.
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

    /// <summary>Reconciles a single outstanding lease (exposed for the RT-D1 test).</summary>
    public void Reconcile(Mainguard.Git.Models.MergeLeaseRow lease)
    {
        var repoPath = _resolveRepoPath(lease.RepoHash);
        if (string.IsNullOrEmpty(repoPath))
        {
            return; // the repo isn't mounted this boot — leave the lease for a later reconcile.
        }

        var currentMain = RevParse(repoPath!, lease.MainBranch);
        var advanced = !string.IsNullOrEmpty(currentMain)
            && !string.Equals(currentMain, lease.ExpectedMainSha, StringComparison.Ordinal);

        // Evidence in the T-19 journal that a merge op actually ran for this repo.
        var hasMergeEntry = _journal.GetHistory(repoPath!)
            .Any(e => string.Equals(e.Kind, JournalKinds.Merge, StringComparison.Ordinal));

        if (advanced && hasMergeEntry)
        {
            // Committed but not confirmed → synthesize the confirm exactly once, then fire the cascade.
            _leases.Confirm(lease.RepoHash, lease.LeaseId, currentMain);
            _onMerged(lease.RepoHash, lease.AgentId, currentMain);
        }
        else
        {
            // Never committed → release the lease; surface the interrupted attempt for a human retry.
            _leases.Release(lease.RepoHash, lease.LeaseId);
            _onInterrupted?.Invoke(lease.RepoHash,
                $"A merge of agent/{lease.AgentId} was interrupted before it committed; no ref moved. Retry when ready.");
        }
    }

    private static string RevParse(string repoPath, string reference)
    {
        var (code, output, _) = GitService.RunGit(repoPath, "rev-parse", "--verify", reference);
        return code == 0 ? output.Trim() : string.Empty;
    }
}
