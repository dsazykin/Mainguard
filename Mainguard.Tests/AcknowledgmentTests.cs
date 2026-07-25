using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Mainguard.Git.Review;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-11 tests 4 (Ack_InvalidationOnHashChange) + 5 (Ack_ItemByItem_GateComposition). Acks bind to the
/// flagged-set content hash (invariant 2); acks are item-by-item, and a single global checkbox is
/// impossible by construction (rejection trigger); each ack emits an event (P2-15 chains it).
/// </summary>
public class AcknowledgmentTests
{
    private static FlaggedChange Item(string path, string content, FlaggedKind kind = FlaggedKind.RiskCategory) =>
        new(path, RiskCategory.ExecutableConfig, kind, AcknowledgmentStore.HashContent(content), $"{path} flagged");

    [Fact]
    public void Ack_InvalidationOnHashChange_ResetsAllAcks()
    {
        var store = new AcknowledgmentStore("loom-1");
        var v1 = new List<FlaggedChange> { Item("package.json", "scripts-v1"), Item(".github/workflows/ci.yml", "ci-v1") };
        store.SetFlagged(v1);

        foreach (var i in v1)
        {
            store.Acknowledge(i.Id);
        }

        Assert.True(store.AllAcknowledged);

        // A new push changes package.json's hunk content → new set hash → every ack invalid.
        var v2 = new List<FlaggedChange> { Item("package.json", "scripts-v2-CHANGED"), Item(".github/workflows/ci.yml", "ci-v1") };
        store.SetFlagged(v2);

        Assert.False(store.AllAcknowledged);
        Assert.Equal(2, store.PendingCount);
        Assert.Equal(2, store.LastResetCount);
    }

    [Fact]
    public void Ack_UnrelatedFileChange_LeavesHashUnchanged_And_KeepsAcks()
    {
        // The hash covers only the FLAGGED set. An unrelated (non-flagged) file changing elsewhere does
        // not alter the flagged set, so the hash — and the acks — are preserved (documented case, test 4).
        var flagged = new List<FlaggedChange> { Item("package.json", "scripts-v1") };
        var store = new AcknowledgmentStore("loom-1");
        store.SetFlagged(flagged);
        store.Acknowledge(flagged[0].Id);

        var hashBefore = store.CurrentHash;
        store.SetFlagged(new List<FlaggedChange> { Item("package.json", "scripts-v1") }); // same flagged content
        Assert.Equal(hashBefore, store.CurrentHash);
        Assert.True(store.AllAcknowledged);
        Assert.Equal(0, store.LastResetCount);
    }

    [Fact]
    public void Ack_ItemByItem_GateComposition_And_EventsEmitted()
    {
        var audit = new InMemoryAuditLog();
        var gate = new FlaggedChangeGate(audit);
        var store = gate.StoreFor("loom-1");

        var items = new List<FlaggedChange> { Item("package.json", "s"), Item(".github/workflows/ci.yml", "c"), Item("src/auth/x.cs", "a") };
        store.SetFlagged(items);

        Assert.False(gate.Allows("loom-1", out _)); // 3 pending

        store.Acknowledge(items[0].Id);
        Assert.False(gate.Allows("loom-1", out _)); // still 2 pending — item-by-item, not global

        store.Acknowledge(items[1].Id);
        store.Acknowledge(items[2].Id);
        Assert.True(gate.Allows("loom-1", out _)); // all acked

        var events = audit.Read().Where(e => e.Type == "acknowledged_flagged_change").ToList();
        Assert.Equal(3, events.Count);
    }

    /// <summary>
    /// MG-40: the gate fails CLOSED. An agent with no acknowledgment store has not been through the
    /// flagged-change review at all — which is not the same thing as having been reviewed and found
    /// clean — so it must not merge. The gate used to return true for any id it did not recognise, so a
    /// review that never ran (or an id that never matched) was indistinguishable from a clean bill of
    /// health.
    /// </summary>
    [Fact]
    public void UnknownAgent_IsDenied_NotWavedThrough()
    {
        var gate = new FlaggedChangeGate();

        Assert.False(gate.Allows("never-reviewed", out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));

        // Registering the branch with an empty flagged set IS a review that came back clean — allowed.
        gate.StoreFor("never-reviewed").SetFlagged(new List<FlaggedChange>());
        Assert.True(gate.Allows("never-reviewed", out _));
    }

    [Fact]
    public void GlobalAcknowledgeAll_IsImpossibleByConstruction()
    {
        // The only way to ack is Acknowledge(string itemId): there is no parameterless / bulk ack method.
        var methods = typeof(AcknowledgmentStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name.StartsWith("Acknowledge", System.StringComparison.Ordinal))
            .ToList();

        Assert.All(methods, m =>
        {
            var ps = m.GetParameters();
            Assert.Single(ps);
            Assert.Equal(typeof(string), ps[0].ParameterType);
        });

        Assert.DoesNotContain(methods, m => m.Name.Contains("All"));
    }

    [Fact]
    public void EmptyFlaggedSet_IsTriviallySatisfied()
    {
        var store = new AcknowledgmentStore("loom-1");
        Assert.True(store.AllAcknowledged);
        Assert.Equal(0, store.PendingCount);
    }
}
