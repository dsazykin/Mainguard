using System;
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

    /// <summary>
    /// ISSUES-LOG #13: the stable partition alone puts a JUST-rejected entry behind every terminal row
    /// spawned before it, i.e. dead last on a rail with any history — which the human who just clicked
    /// Reject reads as the entry having vanished. Insertion order is spawn order; history has to be
    /// ordered by when it was decided.
    /// </summary>
    [Fact]
    public void TerminalHistory_SortsNewestDecisionFirst()
    {
        var states = new Dictionary<string, WorkerMergeState>
        {
            ["old-merged"] = WorkerMergeState.Merged,
            ["old-rejected"] = WorkerMergeState.Rejected,
            ["working"] = WorkerMergeState.Working,
            ["just-rejected"] = WorkerMergeState.Rejected,
        };
        var decided = new Dictionary<string, DateTimeOffset?>
        {
            ["old-merged"] = new DateTimeOffset(2026, 8, 18, 20, 45, 0, TimeSpan.Zero),
            ["old-rejected"] = new DateTimeOffset(2026, 8, 18, 21, 21, 0, TimeSpan.Zero),
            ["working"] = new DateTimeOffset(2026, 8, 20, 11, 18, 0, TimeSpan.Zero),
            ["just-rejected"] = new DateTimeOffset(2026, 8, 22, 14, 17, 0, TimeSpan.Zero),
        };
        // Spawn order: the fresh rejection was spawned in the middle, so insertion order buries it.
        var insertionOrder = new[] { "old-merged", "old-rejected", "working", "just-rejected" };

        var displayOrder = MergeQueueGrpcService.OrderForDisplay(
            insertionOrder, id => states[id], id => decided[id]).ToArray();

        Assert.Equal(
            new[] { "working", "just-rejected", "old-rejected", "old-merged" }, displayOrder);
    }

    /// <summary>A row whose decision time is unknown (a daemon that predates the field) must still be
    /// PRESENT — it sorts to the back of history, it is never dropped.</summary>
    [Fact]
    public void TerminalEntriesWithNoDecisionTime_KeepInsertionOrderAtTheBackAndAreNeverDropped()
    {
        var states = new Dictionary<string, WorkerMergeState>
        {
            ["no-stamp-a"] = WorkerMergeState.Merged,
            ["no-stamp-b"] = WorkerMergeState.Rejected,
            ["stamped"] = WorkerMergeState.Rejected,
        };
        var insertionOrder = new[] { "no-stamp-a", "no-stamp-b", "stamped" };

        var displayOrder = MergeQueueGrpcService.OrderForDisplay(
            insertionOrder,
            id => states[id],
            id => id == "stamped" ? new DateTimeOffset(2026, 8, 22, 14, 17, 0, TimeSpan.Zero) : null)
            .ToArray();

        Assert.Equal(new[] { "stamped", "no-stamp-a", "no-stamp-b" }, displayOrder);
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
