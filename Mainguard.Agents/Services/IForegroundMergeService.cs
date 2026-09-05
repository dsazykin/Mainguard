using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Models;

namespace Mainguard.Agents.Services;

/// <summary>The request for the human-gated foreground merge (P2-10 §3.5).</summary>
/// <param name="RepoPath">The Windows-side working repo.</param>
/// <param name="RepoHash">The P2-06 repo hash (drives <c>ResolveSyncRemote</c> and the merge lease).</param>
/// <param name="AgentId">The agent whose <c>agent/&lt;id&gt;</c> branch is merged.</param>
/// <param name="ExpectedMainSha">The <c>main@sha</c> the verification ran against (the A5 CAS old-OID).</param>
/// <param name="MainBranch">The local main branch the merge lands on.</param>
/// <param name="AllowStaleOverride">Loud, separate override path: merge a stale/unverified branch anyway (journaled + audited).</param>
/// <param name="OverrideReason">Why the override was used (recorded in the audit event).</param>
/// <param name="ExpectedBranchSha">
/// K3/K2 — the <c>agent/&lt;id&gt;</c> tip the queue's verification was measured ON, carried from the
/// lease the daemon granted. It is what makes the merge source identifiable: the ref this repository
/// happens to hold under that name is a NAME, and a name is not an identity. Empty means the daemon has
/// no measured branch sha for this entry, in which case the merge falls back to preferring the ref it
/// just fetched — never to trusting a stale local copy.
/// </param>
public sealed record ForegroundMergeRequest(
    string RepoPath,
    string RepoHash,
    string AgentId,
    string ExpectedMainSha,
    string MainBranch = "main",
    bool AllowStaleOverride = false,
    string? OverrideReason = null,
    string ExpectedBranchSha = "");

/// <summary>The outcome of a foreground merge attempt.</summary>
/// <param name="Merged">True iff the merge landed on main.</param>
/// <param name="NewMainSha">The post-merge <c>main@sha</c> when merged.</param>
/// <param name="CasLost">True when the A5 ref-level compare-and-swap lost (main moved) — no merge happened.</param>
/// <param name="Reason">A human-readable reason when not merged.</param>
public sealed record ForegroundMergeResult(bool Merged, string? NewMainSha, bool CasLost, string? Reason);

/// <summary>
/// The Windows-side, human-gated "Merge to Main" (P2-10 §3.5). Fetches the SC-2-resolved sync remote,
/// then merges <c>agent/&lt;id&gt;</c> onto main under a ref-level compare-and-swap on
/// <c>refs/heads/main</c> (A5) — journaled via T-19 so it is undoable. The merge is the RT-D1 two-step
/// daemon conversation (<c>BeginMerge</c> lease → journaled merge → <c>ConfirmMerge</c>). There is no
/// auto-merge: this is the sole path to <c>Merged</c>.
/// </summary>
public interface IForegroundMergeService
{
    /// <summary>Runs the full begin → merge → confirm conversation and returns the outcome.</summary>
    ForegroundMergeResult MergeAgentBranch(ForegroundMergeRequest request);
}

/// <summary>
/// <b>The middle leg of the RT-D1 conversation on its own</b> (P2-10 §3.7: <c>BeginMerge</c> → <i>Windows-side
/// journaled merge</i> → <c>ConfirmMerge</c>), for the caller that already holds the repo's merge lease.
///
/// <para>This seam exists because the human merge is driven from the <b>Windows GUI</b> while the lease,
/// the <c>CanMerge</c> gate and the queue live in the <b>daemon</b>. The GUI takes the lease over the
/// <c>BeginMerge</c> RPC — so it must NOT take a second one — then runs the real
/// <c>git merge --ff-only</c> against the user's own checkout through this interface, then records the
/// outcome over <c>ConfirmMerge</c> (or hands the lease back over <c>AbandonMerge</c>). Exposing only this
/// method keeps the one-outstanding-merge-per-repo invariant intact: there is exactly one
/// <see cref="IMergeLeaseStore"/> in the system, daemon-side, and this leg never touches it (MG-23).</para>
/// </summary>
public interface IJournaledMergeExecutor
{
    /// <summary>
    /// Fetches the SC-2 sync remote, then merges <c>agent/&lt;id&gt;</c> onto main under the A5 ref-level
    /// CAS, inside one T-19 journal operation. Takes, confirms and releases <b>nothing</b> — the caller
    /// owns <paramref name="lease"/> and owns recording its outcome.
    /// </summary>
    ForegroundMergeResult PerformJournaledMerge(ForegroundMergeRequest request, MergeLeaseRow lease);
}
