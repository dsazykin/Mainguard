using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// What the daemon MEASURED about one worktree it parked mid-rebase — the conflict a human is being asked
/// to deal with, as facts rather than as a sentence.
///
/// <para><b>Why this exists.</b> The keep-alive cascade's conflict arm parks the worktree, pauses the jail
/// and blocks the queue entry with a reason that names a required human action
/// (<see cref="MergeQueueProvisioner.RebaseConflictReason"/>). Everything else it knew — <i>where</i> the
/// parked worktree is, <i>which</i> files conflict, <i>when</i> it happened — went into one audit event and
/// one log line, neither of which any surface reads. So the card told a person to resolve a conflict
/// without telling them what conflicted, and offered no operation that could act on it.</para>
///
/// <para><b>Persisted, having originally not been</b> — and the original reasoning is worth stating because
/// it was half right. It said: this is a measurement of one worktree at one instant, a daemon restart
/// re-measures, the durable record is the audit event. The first two clauses are the mistake. Nothing
/// re-measures: the parking is written by the keep-alive cascade's conflict arm, which runs when a rebase
/// conflicts, and a restart is not a rebase. The parked worktree really is still on disk, the jail really
/// is still frozen — and with this record gone, Resolve and Abort both answered "no rebase parked", the
/// hand-back had no permit to grant, and a <c>Stop</c> force-removed the mid-rebase worktree (audit F3).
/// The audit event is a record for a human reading a log, not one any operation can act on.</para>
///
/// <para>What the original note was right about is the <i>content</i>: <see cref="ConflictedPaths"/> is a
/// measurement and can go stale. It is carried across a restart anyway, because a possibly-stale list of
/// conflicted files is strictly more use to the human being asked to resolve them than no list at all, and
/// because the alternative on offer was not a fresh measurement but a card that denied the conflict
/// existed.</para>
/// </summary>
/// <param name="AgentId">The entry whose branch is parked.</param>
/// <param name="WorktreePath">
/// The parked worktree, verbatim — the T-04 handoff address.
///
/// <para>This is a daemon-side filesystem path, which G-14 keeps off client-facing messages, and it is
/// carried anyway for a stated reason rather than by oversight. G-14's rule is about <i>addressing</i>: a
/// handle a client uses to name a resource must not be a path, because a path is meaningless to a client
/// that is not on the daemon's machine. This is not an address — nothing is looked up by it — it is a
/// measured fact about a hand-off to a human, and the identical string is ALREADY carried to a
/// human-facing client verbatim inside <c>AuditService.ReadAudit</c>'s decrypted payload for
/// <see cref="MergeQueueProvisioner.KeepAliveConflictEvent"/>. Withholding it from the card while shipping
/// it in the audit reader would not be a boundary, only an inconvenience.</para>
/// </param>
/// <param name="MainBranch">The mirror branch the rebase was onto.</param>
/// <param name="ConflictedPaths">
/// The repo-relative paths git reports as unmerged (<c>diff --name-only --diff-filter=U</c>), measured at
/// parking time. Empty when git could not be asked — an empty list is "we could not measure it", and the
/// surface says so rather than rendering "no files conflict" over a conflict.
/// </param>
/// <param name="ParkedAt">When the cascade parked it.</param>
public sealed record ParkedRebaseConflict(
    string AgentId,
    string WorktreePath,
    string MainBranch,
    IReadOnlyList<string> ConflictedPaths,
    DateTimeOffset ParkedAt);

/// <summary>
/// The live set of worktrees parked mid-rebase, keyed by <b>(repo handle, agent id)</b>.
///
/// <para>The pair is the key, not the agent id: agent ids are unique per repo and not globally — the
/// external-PR intake names its entries <c>pr-&lt;n&gt;</c>, so two subscribed repositories both hold a
/// <c>pr-7</c> — and answering one repo's conflict from another's parking would point a human at the wrong
/// worktree. The same collision has been fixed repeatedly in this codebase.</para>
/// </summary>
public sealed class RebaseConflictParkingStore
{
    private readonly ConcurrentDictionary<(string Repo, string Agent), ParkedRebaseConflict> _parked =
        new();

    private readonly IAgentRestartLedger _persist;

    /// <summary>The daemon's store, written through to the durable restart ledger.</summary>
    public RebaseConflictParkingStore()
        : this(null)
    {
    }

    /// <param name="persist">The restart store, or null for the process-wide one. The test seam.</param>
    internal RebaseConflictParkingStore(IAgentRestartLedger? persist)
    {
        _persist = persist ?? AgentRestartLedger.Process;
        foreach (var record in _persist.LoadAll())
        {
            foreach (var parked in record.Parked)
            {
                _parked[(parked.RepoHash, record.AgentId)] = new ParkedRebaseConflict(
                    record.AgentId, parked.WorktreePath, parked.MainBranch,
                    parked.ConflictedPaths, parked.ParkedAt);
            }

            foreach (var repo in record.HandedBackRepos)
            {
                _handedBack[(repo, record.AgentId)] = 0;
            }
        }
    }

    /// <summary>Records (or replaces) the parking for one entry.</summary>
    public void Park(string repoHandle, ParkedRebaseConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        var repo = repoHandle ?? string.Empty;
        _parked[(repo, conflict.AgentId)] = conflict;
        _persist.Update(conflict.AgentId, record => record with
        {
            Parked = record.Parked
                .Where(p => !string.Equals(p.RepoHash, repo, StringComparison.Ordinal))
                .Append(new RestartParkedConflict(
                    repo, conflict.WorktreePath, conflict.MainBranch,
                    conflict.ConflictedPaths.ToList(), conflict.ParkedAt))
                .ToList(),
        });
    }

    /// <summary>The parking for one entry, or null when this entry is not parked mid-rebase.</summary>
    public ParkedRebaseConflict? Find(string repoHandle, string agentId) =>
        _parked.TryGetValue((repoHandle ?? string.Empty, agentId ?? string.Empty), out var parked)
            ? parked
            : null;

    /// <summary>
    /// Forgets the parking for one entry. True when there was one.
    ///
    /// <para>Called when the conflict stops being one — the rebase was aborted, or the agent was handed it
    /// back to finish. It is deliberately NOT called on a successful later rebase cycle: that cycle parks
    /// or clears through the same two entry points, and a third writer is how a stale record survives.</para>
    /// </summary>
    public bool Clear(string repoHandle, string agentId)
    {
        var repo = repoHandle ?? string.Empty;
        var id = agentId ?? string.Empty;
        if (!_parked.TryRemove((repo, id), out _))
        {
            return false;
        }

        _persist.Update(id, record => record with
        {
            Parked = record.Parked
                .Where(p => !string.Equals(p.RepoHash, repo, StringComparison.Ordinal))
                .ToList(),
        });
        return true;
    }

    // ---- the hand-back mark ----------------------------------------------------------------------
    //
    // "Let the agent resolve" unpauses the worker and tells it to finish the rebase. A finished rebase is
    // a rewrite of history the mirror already holds, and the ref mediator's rule 2 refuses exactly that —
    // so without this mark the handed-back branch was refused on every sweep, forever, and the card's
    // promise of automatic re-verification was false. The mark is the human's authorisation for ONE such
    // rewrite: set by the hand-back, consumed by the first publish it lets through, keyed like the parking.
    private readonly ConcurrentDictionary<(string Repo, string Agent), byte> _handedBack = new();

    /// <summary>Records that a human handed this entry's conflict back to its agent to finish the rebase.
    ///
    /// <para>Durable: the permit authorises ONE history rewrite that the ref mediator's rule 2 otherwise
    /// refuses, and the rewrite it authorises arrives whenever the agent finishes — minutes later, across
    /// a restart as easily as not. A permit lost with the daemon leaves the handed-back branch refused on
    /// every sweep, forever, which is precisely the failure the permit was introduced to fix.</para></summary>
    public void MarkHandedBack(string repoHandle, string agentId)
    {
        var repo = repoHandle ?? string.Empty;
        var id = agentId ?? string.Empty;
        _handedBack[(repo, id)] = 0;
        _persist.Update(id, record => record.HandedBackRepos.Contains(repo, StringComparer.Ordinal)
            ? record
            : record with { HandedBackRepos = record.HandedBackRepos.Append(repo).ToList() });
    }

    /// <summary>True while a hand-back is outstanding — the mediator may accept one rewrite of this branch.</summary>
    public bool IsHandedBack(string repoHandle, string agentId) =>
        _handedBack.ContainsKey((repoHandle ?? string.Empty, agentId ?? string.Empty));

    /// <summary>Consumes the mark: the rewrite it authorised has reached the mirror (or the entry is gone).</summary>
    public bool ClearHandedBack(string repoHandle, string agentId)
    {
        var repo = repoHandle ?? string.Empty;
        var id = agentId ?? string.Empty;
        if (!_handedBack.TryRemove((repo, id), out _))
        {
            return false;
        }

        _persist.Update(id, record => record with
        {
            HandedBackRepos = record.HandedBackRepos
                .Where(r => !string.Equals(r, repo, StringComparison.Ordinal))
                .ToList(),
        });
        return true;
    }
}

/// <summary>
/// What one conflict action did, as a result rather than an exception — the same posture
/// <c>AgentResumeResult</c> takes, and for the same reason: every way these can decline is a state of the
/// world a human has to read ("this entry is not parked mid-rebase", "its jail is gone"), not a fault.
/// </summary>
/// <param name="Done">True when the action actually changed something.</param>
/// <param name="Reason">Render-verbatim explanation. Empty on success.</param>
public sealed record ConflictActionResult(bool Done, string Reason)
{
    /// <summary>The refusal shape, so a caller never has to remember that Done and Reason are exclusive.</summary>
    public static ConflictActionResult Refused(string reason) => new(false, reason);

    /// <summary>The success shape.</summary>
    public static ConflictActionResult Ok() => new(true, string.Empty);
}
