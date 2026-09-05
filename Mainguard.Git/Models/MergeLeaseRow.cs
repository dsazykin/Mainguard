using System;

namespace Mainguard.Git.Models;

/// <summary>
/// RT-D1 merge lease + idempotency record (P2-10, M7 exit gate). The foreground merge is a two-step
/// daemon conversation: <c>BeginMerge</c> takes a per-repo lease (freezing conflicting queue actions),
/// the Windows-side journaled merge runs, then <c>ConfirmMerge</c> writes the idempotency outcome and
/// releases the lease. A crash between the committed merge and <c>ConfirmMerge</c> is reconciled on
/// daemon boot (journal replay synthesizes the missing confirm) — exactly once or none. One row per
/// repo; the row survives across the merge so the boot reconcile can find an outstanding lease.
/// </summary>
public class MergeLeaseRow
{
    /// <summary>Auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>The repo the lease is held for (P2-06 repo hash). One outstanding lease per repo.</summary>
    public string RepoHash { get; set; } = string.Empty;

    /// <summary>A unique id for this merge attempt (idempotency key).</summary>
    public string LeaseId { get; set; } = string.Empty;

    /// <summary>The agent (branch) being merged under this lease.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>The <c>main@sha</c> the merge was expected to fast-forward from (the verified sha).</summary>
    public string ExpectedMainSha { get; set; } = string.Empty;

    /// <summary>
    /// K3 — the <c>agent/&lt;id&gt;</c> tip the queue's verification was measured ON
    /// (<c>VerificationRecord.BranchSha</c>), read from the daemon's own record at grant time.
    ///
    /// <para><b>The other half of the identity a merge is authorized for.</b>
    /// <see cref="ExpectedMainSha"/> alone says which main this merge may fast-forward; it says nothing
    /// about WHICH COMMITS get fast-forwarded onto it. With only that half recorded, the lease was a
    /// mutex over a repository rather than a claim about a merge: the branch could move between the
    /// grant and the merge, the merge could consume a different ref that happened to carry the same
    /// name, and the confirmed post-merge sha could be anything the client cared to report — and every
    /// one of those still satisfied the lease.</para>
    ///
    /// <para><b>Empty means unknown, and unknown never manufactures a refusal.</b> Rows written before
    /// this field existed, and the seeded/substrate-less paths, carry <c>""</c>; every comparison against
    /// it reads empty as "not measured" and declines to answer rather than blocking a merge on ignorance.
    /// The one exception is the P2-12 external leg, which refuses — see
    /// <c>ExternalPrMergeService</c>: there the sha is the ONLY record of which upstream head was
    /// verified, and re-deriving it at merge time is the defect (K4).</para>
    /// </summary>
    public string ExpectedBranchSha { get; set; } = string.Empty;

    /// <summary>The local main branch the merge lands on (the boot reconcile reads its current tip).</summary>
    public string MainBranch { get; set; } = "main";

    /// <summary>True once <c>ConfirmMerge</c> has recorded the outcome; the lease is then released.</summary>
    public bool Confirmed { get; set; }

    /// <summary>The post-merge <c>main@sha</c> recorded at confirm time (drives <c>NotifyMainMoved</c>).</summary>
    public string? PostMergeSha { get; set; }

    /// <summary>When the lease was taken (UTC).</summary>
    public DateTime BeginUtc { get; set; }
}
