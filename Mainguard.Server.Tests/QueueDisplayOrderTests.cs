using System.Collections.Generic;
using System.Linq;
using Mainguard.Agents.Agents;
using Mainguard.Server.Services;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// Regression coverage for the queue rail's display order, found live on 2026-08-20: a stale
/// Merged/Rejected entry sits at the FRONT of <c>MergeQueue.Agents</c>' stable insertion order
/// forever (they are a permanent record by design), so a freshly spawned, actionable entry always
/// lands at the very BOTTOM of the visible list — which reproduced the "my spawned agent isn't in
/// the queue" complaint exactly (it was there, just scrolled out of view behind older terminal rows).
/// See <see cref="MergeQueueGrpcService.OrderForDisplay"/>.
/// </summary>
public sealed class QueueDisplayOrderTests
{
    [Fact]
    public void ActionableEntries_SortBeforeTerminalOnes_RegardlessOfInsertionOrder()
    {
        var states = new Dictionary<string, WorkerMergeState>
        {
            ["old-merged"] = WorkerMergeState.Merged,
            ["old-rejected"] = WorkerMergeState.Rejected,
            ["fresh-working"] = WorkerMergeState.Working,
        };
        // Insertion order matches what MergeQueue.Agents actually returns: oldest first — the two
        // terminal entries (spawned first, long since resolved) precede the fresh actionable one.
        var insertionOrder = new[] { "old-merged", "old-rejected", "fresh-working" };

        var displayOrder = MergeQueueGrpcService.OrderForDisplay(insertionOrder, id => states[id]).ToArray();

        // The actionable entry must not be buried behind terminal history.
        Assert.Equal("fresh-working", displayOrder[0]);
    }

    [Fact]
    public void WithinEachPartition_RelativeInsertionOrderIsPreserved()
    {
        var states = new Dictionary<string, WorkerMergeState>
        {
            ["a-working"] = WorkerMergeState.Working,
            ["b-verified"] = WorkerMergeState.Verified,
            ["c-merged"] = WorkerMergeState.Merged,
            ["d-rejected"] = WorkerMergeState.Rejected,
        };
        var insertionOrder = new[] { "c-merged", "a-working", "d-rejected", "b-verified" };

        var displayOrder = MergeQueueGrpcService.OrderForDisplay(insertionOrder, id => states[id]).ToArray();

        // Stable partition: actionable group keeps its original relative order (a, b), then the
        // terminal group keeps its original relative order (c, d) — membership/order untouched beyond
        // the single actionable-vs-terminal split.
        Assert.Equal(new[] { "a-working", "b-verified", "c-merged", "d-rejected" }, displayOrder);
    }

    [Fact]
    public void NoTerminalEntries_OrderIsUnchanged()
    {
        var states = new Dictionary<string, WorkerMergeState>
        {
            ["x"] = WorkerMergeState.Working,
            ["y"] = WorkerMergeState.StaleVerified,
            ["z"] = WorkerMergeState.AwaitingReview,
        };
        var insertionOrder = new[] { "z", "x", "y" };

        var displayOrder = MergeQueueGrpcService.OrderForDisplay(insertionOrder, id => states[id]).ToArray();

        Assert.Equal(insertionOrder, displayOrder);
    }
}
