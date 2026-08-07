using System;

namespace Mainguard.Git.Models;

/// <summary>
/// One persisted merge-queue state row (P2-10). Written inside the same SQLite transaction as every
/// state-machine transition, so a daemon restart resumes queue state exactly (edge row 4 — an
/// interrupted <c>Verifying</c> resumes, never stuck). One row per (repo, agent).
/// </summary>
public class MergeQueueRow
{
    /// <summary>Auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>The repo this queue entry belongs to (P2-06 repo hash).</summary>
    public string RepoHash { get; set; } = string.Empty;

    /// <summary>The agent (branch) whose merge eligibility this row tracks.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>The current <c>WorkerMergeState</c> name (persisted as its enum name).</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>The id of the last <see cref="VerificationRow"/> this agent produced (null before its first run).</summary>
    public long? LastVerificationId { get; set; }

    /// <summary>When this state was last written (UTC), used for FIFO re-queue ordering.</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>The original verification time (UTC) used to order the stale-cascade re-queue FIFO.</summary>
    public DateTime? VerifiedAtUtc { get; set; }

    /// <summary>
    /// The entry's origin (P2-12) as the <see cref="Agents.MergeEntryOrigin"/> enum name — <c>Local</c>
    /// (foreground merge) or <c>External</c> (intake'd bot PR, host-API merge). Persisted so the merge
    /// dispatch routes correctly after a daemon restart. Defaults to <c>Local</c>.
    /// </summary>
    public string Origin { get; set; } = "Local";

    /// <summary>
    /// Who dropped this entry from the queue, for a row whose <see cref="State"/> is <c>Discarded</c>;
    /// null for every other row. Daemon-derived at the RPC (the same
    /// <c>IApproverIdentityResolver</c> a plan approval uses) — the discard request carries no identity
    /// field, so a client cannot assert one.
    ///
    /// <para>Read the honest limits of this value on <c>IApproverIdentityResolver</c>: over loopback TCP
    /// the daemon has no peer credential, so this attributes the discard to the HOST SESSION the daemon
    /// runs as and cannot tell two local callers apart. It is a record of <i>where</i> the discard came
    /// from, not proof of which human pressed the button.</para>
    /// </summary>
    public string? DiscardedBy { get; set; }

    /// <summary>When the entry was discarded (UTC); null unless <see cref="State"/> is <c>Discarded</c>.</summary>
    public DateTime? DiscardedAtUtc { get; set; }

    /// <summary>
    /// The human's stated reason for the discard, verbatim; empty when they gave none. Kept on the row
    /// rather than only in the audit log because the row is what survives in the daemon DB and what the
    /// queue rehydrates from — an audit sink that is in-memory today (see <c>DaemonHost</c>) would
    /// otherwise lose the whole record on the next daemon restart.
    /// </summary>
    public string? DiscardReason { get; set; }
}
