using System;
using System.Collections.Concurrent;
using Mainguard.Git.Audit;
using Mainguard.Git.Review;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// The P2-11 flagged-changes merge gate: a composable <see cref="IMergeGate"/> the queue ANDs into
/// <see cref="IMergeQueue.CanMerge"/>. It holds a per-agent <see cref="AcknowledgmentStore"/>; a branch may
/// merge only once every flagged item for its <b>current</b> flagged-set hash is acknowledged item-by-item
/// (a global ack is impossible — the store exposes no "ack all"). This is the security boundary the review
/// cockpit renders and drives: hostile agent code lands in a flag-worthy category (ExecutableConfig, CI,
/// hooks, security paths, F6 out-of-scope, RT-D2), and the human must acknowledge each before merge.
///
/// <para><b>Fails closed (MG-40):</b> an agent with no store yet is DENIED — see <see cref="Allows"/>.</para>
/// </summary>
public sealed class FlaggedChangeGate : IMergeGate
{
    private readonly ConcurrentDictionary<string, AcknowledgmentStore> _stores = new(StringComparer.Ordinal);
    private readonly IAuditLog _audit;

    /// <param name="audit">Audit sink threaded into every per-agent store (for <c>acknowledged_flagged_change</c>).</param>
    public FlaggedChangeGate(IAuditLog? audit = null) => _audit = audit ?? new InMemoryAuditLog();

    /// <summary>The acknowledgment store for an agent (created on first use). The cockpit sets its flagged set and acks items.</summary>
    public AcknowledgmentStore StoreFor(string agentId) =>
        _stores.GetOrAdd(agentId ?? string.Empty, id => new AcknowledgmentStore(id, _audit));

    public bool Allows(string agentId, out string reason)
    {
        // Default-DENY on an agent this gate has never seen. The old code treated "no ack store" as
        // "nothing to acknowledge" and allowed the merge — but the two states are indistinguishable from
        // here: an agent with no store is one whose diff was never run through the flagged-change review,
        // not one that was reviewed and came back clean. A gate that answers "allow" for a branch it has
        // never inspected is not a gate; a typo'd/renamed agent id, or a review path that failed before
        // it could publish its findings, would silently wave hostile code straight through the one
        // control that exists to catch it. Everything that legitimately reaches a merge decision calls
        // StoreFor first (the review cockpit does it in its constructor), so the honest path is unchanged.
        if (!_stores.TryGetValue(agentId ?? string.Empty, out var store))
        {
            reason = "flagged-change review has not run for this branch (no acknowledgment record)";
            return false;
        }

        if (store.AllAcknowledged)
        {
            reason = "";
            return true;
        }

        var pending = store.PendingCount;
        reason = pending == 1
            ? "1 flagged change needs acknowledgment"
            : $"{pending} flagged changes need acknowledgment";
        return false;
    }
}
