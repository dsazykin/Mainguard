using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// What one acknowledgment actually did, as answered by the gate that blocks the merge — not by the fact
/// that a call was made. <paramref name="Acknowledged"/> false with a <paramref name="Reason"/> is the
/// honest outcome a surface must render instead of a checkmark.
/// </summary>
/// <param name="Acknowledged">True only when the gate recorded this item as acknowledged.</param>
/// <param name="CanMerge">The gate's post-acknowledgment answer for the branch.</param>
/// <param name="Reason">The gate's words (or the refusal, when nothing reached it).</param>
public sealed record FlaggedAckOutcome(bool Acknowledged, bool CanMerge, string Reason)
{
    /// <summary>Nothing reached the gate: no repo, no daemon, or the RPC failed.</summary>
    public static FlaggedAckOutcome Refused(string reason) => new(false, CanMerge: false, reason);
}

/// <summary>
/// The seam a review surface reads its must-acknowledge items from and routes acknowledgments through.
///
/// <para><b>Why this exists.</b> The review cockpit overlay built its own in-process
/// <c>AcknowledgmentStore</c>, so a human's checkmark cleared a gate that no merge consults. The gate that
/// blocks the merge lives on the daemon, the acknowledgment RPC is addressed by item id, and the daemon's
/// queue projection is the only place those ids come from — so items and acknowledgments have to come from
/// and go to the same place, which is what this seam is.</para>
/// </summary>
public interface IFlaggedChangeSource
{
    /// <summary>The daemon's must-acknowledge items for a branch (empty when it is not in the queue).</summary>
    IReadOnlyList<FlaggedItem> FlaggedFor(string agentId);

    /// <summary>The gate's merge answer for the branch, verbatim.</summary>
    bool CanMerge(string agentId, out string reason);

    /// <summary>False → an acknowledgment made now cannot reach the gate; <paramref name="reason"/> says why.</summary>
    bool CanAcknowledge(out string reason);

    /// <summary>Acknowledge one item BY ID and report what the gate did about it.</summary>
    Task<FlaggedAckOutcome> AcknowledgeAsync(string agentId, string itemId, CancellationToken ct);
}

/// <summary>
/// The <see cref="IFlaggedChangeSource"/> over the live merge-queue seam — the shipped
/// <see cref="DaemonBackedOrchestrator"/> in the real app. Items come off the queue projection the daemon
/// streams (<c>QueueEntry.FlaggedItems</c>), acknowledgments go out over the daemon's ack RPC, and the
/// outcome is the daemon's own answer rather than the local optimism of having called it.
/// </summary>
public sealed class DaemonFlaggedChangeSource : IFlaggedChangeSource
{
    /// <summary>The daemon's well-known flagged-item id for "this branch changed its own test
    /// command" (mirrors <c>MergeQueueGrpcService.ChangedTestCommandItemId</c> — the id is part of
    /// the wire vocabulary, not an internal detail; the cockpit header keys off it).</summary>
    public const string ChangedTestCommandItemId = "changed-test-command";

    private readonly IMergeQueueService _queue;

    public DaemonFlaggedChangeSource(IMergeQueueService queue)
        => _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    public IReadOnlyList<FlaggedItem> FlaggedFor(string agentId)
        => _queue.GetQueue().FirstOrDefault(e => string.Equals(e.AgentId, agentId, StringComparison.Ordinal))
               ?.FlaggedItems
           ?? Array.Empty<FlaggedItem>();

    public bool CanMerge(string agentId, out string reason) => _queue.CanMerge(agentId, out reason);

    public bool CanAcknowledge(out string reason)
    {
        if (_queue is DaemonBackedOrchestrator daemon)
        {
            return daemon.CanAcknowledgeFlaggedChange(out reason);
        }

        reason = "";
        return true;
    }

    public async Task<FlaggedAckOutcome> AcknowledgeAsync(string agentId, string itemId, CancellationToken ct)
    {
        // The daemon-backed adapter answers from the RPC response itself, which is authoritative and
        // synchronous — the queue stream's re-push arrives afterwards and would race a read-back.
        if (_queue is DaemonBackedOrchestrator daemon)
        {
            return await daemon.AcknowledgeFlaggedChangeReportedAsync(agentId, itemId, ct).ConfigureAwait(false);
        }

        // Any other implementation of the seam (an in-process queue, a test double) is asked, then READ
        // BACK: the item's own acknowledged flag is the evidence, never the fact that the call returned.
        try
        {
            await _queue.AcknowledgeFlaggedChangeAsync(agentId, itemId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return FlaggedAckOutcome.Refused($"the acknowledgment did not reach the merge gate — {ex.Message}");
        }

        var item = FlaggedFor(agentId).FirstOrDefault(f => string.Equals(f.Id, itemId, StringComparison.Ordinal));
        var canMerge = CanMerge(agentId, out var reason);

        // The item disappearing from the flagged set is the gate saying it no longer blocks; the item
        // still present and unacknowledged is the gate saying the acknowledgment did not take.
        var acknowledged = item is null || item.Acknowledged;
        return acknowledged
            ? new FlaggedAckOutcome(true, canMerge, reason)
            : FlaggedAckOutcome.Refused("the merge gate did not record this acknowledgment.");
    }
}
