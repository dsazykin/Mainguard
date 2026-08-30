using System;
using System.Linq;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Mainguard.Git.Review;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// L4 — <b>the most security-relevant acknowledgment in the product now writes something.</b>
///
/// <para><see cref="ChangedTestCommandGate.Acknowledge"/> recorded the waiver in a plain
/// <c>HashSet&lt;string&gt;</c> and audited nothing, while the neighbouring
/// <see cref="FlaggedChangeGate"/>'s acks did write <c>acknowledged_flagged_change</c> (observed live at
/// audit Seq 800/801). That asymmetry was backwards: the item waived here is <i>"the branch changed the
/// command that verifies it"</i> — a branch cannot be allowed to self-green — so it is the one click that
/// most needs a record, and it was the one click that left none.</para>
///
/// <para>These tests pin more than "an event appears": they pin that it says enough to act on — which
/// item, which config file, from what to what, and who waived it — because a record that says only "a
/// waiver happened" is not materially better than the silence it replaces.</para>
/// </summary>
public class ChangedTestCommandAuditTests
{
    private const string AgentId = "loom-1";
    private const string VerifyPath = ".mainguard/verify";

    private static ChangedTestCommandGate.CommandDrift SelfGreen =>
        new(VerifyPath, FromMain: "dotnet test\n", ToBranch: "exit 0\n");

    [Fact]
    public void Acknowledge_AppendsTheWaiver_NamingWhatChangedAndWho()
    {
        var audit = new InMemoryAuditLog();
        var gate = new ChangedTestCommandGate(audit);
        gate.SetFlagged(AgentId, ChangedTestCommandGate.TestCommandItem, changed: true, SelfGreen);

        Assert.True(gate.Acknowledge(AgentId, "owner@example"));

        var ack = Assert.Single(audit.Read().Where(e => e.Type == "acknowledged_flagged_change"));
        Assert.Equal(AgentId, ack.Fields["agent"]);
        Assert.Equal(ChangedTestCommandGate.TestCommandItem, ack.Fields["item"]);
        Assert.Equal(VerifyPath, ack.Fields["path"]);
        Assert.Equal(FlaggedKind.ChangedTestCommand.ToString(), ack.Fields["kind"]);
        Assert.Equal("owner@example", ack.Fields["by"]);

        // From what, to what — the fact a reader needs. "The test command changed" names a category.
        Assert.Equal("dotnet test", ack.Fields["from"]);
        Assert.Equal("exit 0", ack.Fields["to"]);
        Assert.NotEqual(ack.Fields["from_hash"], ack.Fields["to_hash"]);
        Assert.Equal(64, ack.Fields["to_hash"].Length);
    }

    /// <summary>
    /// The same event TYPE the flagged-change store uses, deliberately: a reader asking "what did a human
    /// wave through on this branch" must get one answer, not two lists they have to remember to union.
    /// <c>kind</c> is what separates them, exactly as it does across <see cref="FlaggedKind"/>.
    /// </summary>
    [Fact]
    public void TheWaiver_UsesTheSameEventTypeAsEveryOtherAcknowledgment()
    {
        var audit = new InMemoryAuditLog();
        var gate = new ChangedTestCommandGate(audit);
        var store = new AcknowledgmentStore(AgentId, audit);
        var item = new FlaggedChange("ci.yml", RiskCategory.CiWorkflow, FlaggedKind.RiskCategory, "h", "");
        store.SetFlagged(new[] { item });

        gate.SetFlagged(AgentId, ChangedTestCommandGate.TestCommandItem, changed: true, SelfGreen);
        gate.Acknowledge(AgentId, "owner@example");
        store.Acknowledge(item.Id);

        var acks = audit.Read().Where(e => e.Type == "acknowledged_flagged_change").ToList();
        Assert.Equal(2, acks.Count);
        Assert.Equal(
            new[] { FlaggedKind.ChangedTestCommand.ToString(), FlaggedKind.RiskCategory.ToString() },
            acks.Select(a => a.Fields["kind"]));
    }

    /// <summary>
    /// One event per ITEM waived, not one per click. The click clears every armed item at once (by
    /// design — a second, separately-acknowledged gate would let a human clear one and merge while the
    /// other went unread), but what was waived is the items, and a single event would make "the command
    /// changed" and "the toolchain changed" indistinguishable in the chain.
    /// </summary>
    [Fact]
    public void TwoArmedItems_ProduceTwoRecords()
    {
        var audit = new InMemoryAuditLog();
        var gate = new ChangedTestCommandGate(audit);
        gate.SetFlagged(AgentId, ChangedTestCommandGate.TestCommandItem, changed: true, SelfGreen);
        gate.SetFlagged(
            AgentId, ChangedTestCommandGate.ToolchainItem, changed: true,
            new ChangedTestCommandGate.CommandDrift(".mainguard/toolchain", "dotnet:10", "node:20"));

        gate.Acknowledge(AgentId, "owner@example");

        var acks = audit.Read().Where(e => e.Type == "acknowledged_flagged_change").ToList();
        Assert.Equal(2, acks.Count);
        Assert.Contains(acks, a => a.Fields["item"] == ChangedTestCommandGate.TestCommandItem);
        Assert.Contains(acks, a => a.Fields["item"] == ChangedTestCommandGate.ToolchainItem
                                   && a.Fields["to"] == "node:20");
    }

    /// <summary>
    /// Idempotent. A cockpit that refreshes twice must not inflate the record of how often a human waived
    /// something — the count in the chain is read as "how many times did someone decide this".
    /// </summary>
    [Fact]
    public void AcknowledgingTwice_AppendsOnce()
    {
        var audit = new InMemoryAuditLog();
        var gate = new ChangedTestCommandGate(audit);
        gate.SetFlagged(AgentId, ChangedTestCommandGate.TestCommandItem, changed: true, SelfGreen);

        Assert.True(gate.Acknowledge(AgentId, "owner@example"));
        Assert.False(gate.Acknowledge(AgentId, "owner@example"));

        Assert.Single(audit.Read().Where(e => e.Type == "acknowledged_flagged_change"));
    }

    /// <summary>
    /// Acknowledging an agent with nothing flagged is a no-op and must audit NOTHING. A record here would
    /// be worse than the old silence: it would put "a human waived a self-greening branch" in the chain
    /// for a branch that never changed its command.
    /// </summary>
    [Fact]
    public void AcknowledgingAnUnflaggedAgent_AppendsNothing()
    {
        var audit = new InMemoryAuditLog();
        var gate = new ChangedTestCommandGate(audit);

        Assert.False(gate.Acknowledge("never-flagged", "owner@example"));
        Assert.Empty(audit.Read());
    }

    /// <summary>
    /// A NEW drift re-arms the gate, and the second waiver is its own record. Otherwise "I already
    /// acknowledged this branch once" would cover a later, different rewrite of the test command — and the
    /// chain would show one waiver for two.
    /// </summary>
    [Fact]
    public void ANewDrift_ReArmsTheGate_AndTheSecondWaiverIsItsOwnRecord()
    {
        var audit = new InMemoryAuditLog();
        var gate = new ChangedTestCommandGate(audit);
        gate.SetFlagged(AgentId, ChangedTestCommandGate.TestCommandItem, changed: true, SelfGreen);
        gate.Acknowledge(AgentId, "owner@example");

        // The branch re-verifies against a new tip whose command drifted differently.
        gate.SetFlagged(AgentId, ChangedTestCommandGate.TestCommandItem, changed: false);
        gate.SetFlagged(
            AgentId, ChangedTestCommandGate.TestCommandItem, changed: true,
            new ChangedTestCommandGate.CommandDrift(VerifyPath, "dotnet test", "true"));

        Assert.True(gate.IsUnacknowledged(AgentId));
        Assert.True(gate.Acknowledge(AgentId, "owner@example"));

        var acks = audit.Read().Where(e => e.Type == "acknowledged_flagged_change").ToList();
        Assert.Equal(2, acks.Count);
        Assert.Equal("exit 0", acks[0].Fields["to"]);
        // The SECOND record describes the second diff. Keeping the first run's baseline would make the
        // record describe a change the human never saw.
        Assert.Equal("true", acks[1].Fields["to"]);
    }

    /// <summary>
    /// A drift that changes WHILE the item stays armed is recorded in its latest form. Every
    /// re-verification re-arms this item against a new branch tip, so keeping the first run's baseline
    /// would make the eventual waiver describe a diff the human never saw — a record that is worse than
    /// none, because it looks specific.
    /// </summary>
    [Fact]
    public void ADriftThatChangesWhileArmed_IsRecordedAsItsLatestForm()
    {
        var audit = new InMemoryAuditLog();
        var gate = new ChangedTestCommandGate(audit);

        gate.SetFlagged(AgentId, ChangedTestCommandGate.TestCommandItem, changed: true, SelfGreen);
        // The branch pushes again; the command still differs from main, differently. The item never left
        // the flagged set, so nothing cleared the first run's detail on the way through.
        gate.SetFlagged(
            AgentId, ChangedTestCommandGate.TestCommandItem, changed: true,
            new ChangedTestCommandGate.CommandDrift(VerifyPath, "dotnet test\n", "true\n"));

        gate.Acknowledge(AgentId, "owner@example");

        var ack = Assert.Single(audit.Read(), e => e.Type == "acknowledged_flagged_change");
        Assert.Equal("true", ack.Fields["to"]);
    }

    /// <summary>
    /// Three distinct answers about a side of the drift, kept apart: not recorded, absent on that side,
    /// and here is the content. Collapsing the first two would render "we did not capture the baseline"
    /// as "this branch invented a verification command out of nothing" — the exact class of truthful-
    /// looking statement that means something else.
    /// </summary>
    [Fact]
    public void AnUnrecordedDrift_AndAnAbsentFile_ReadDifferently()
    {
        var audit = new InMemoryAuditLog();
        var gate = new ChangedTestCommandGate(audit);

        gate.SetFlagged("no-detail", ChangedTestCommandGate.TestCommandItem, changed: true);
        gate.SetFlagged(
            "absent-on-main", ChangedTestCommandGate.TestCommandItem, changed: true,
            new ChangedTestCommandGate.CommandDrift(VerifyPath, FromMain: null, ToBranch: "exit 0"));

        gate.Acknowledge("no-detail", "owner@example");
        gate.Acknowledge("absent-on-main", "owner@example");

        var acks = audit.Read().Where(e => e.Type == "acknowledged_flagged_change").ToList();
        var undetailed = Assert.Single(acks, a => a.Fields["agent"] == "no-detail");
        Assert.Equal("(not recorded)", undetailed.Fields["from"]);
        Assert.Equal("(not recorded)", undetailed.Fields["from_hash"]);

        var absent = Assert.Single(acks, a => a.Fields["agent"] == "absent-on-main");
        Assert.Equal("(absent)", absent.Fields["from"]);
        Assert.Equal("exit 0", absent.Fields["to"]);
    }

    /// <summary>
    /// A pathological config cannot decide the size of an audit payload. The excerpt is bounded and says
    /// it was cut; the hash beside it still pins the full content, so nothing is lost.
    /// </summary>
    [Fact]
    public void AHugeCommand_IsExcerptedButStillHashedInFull()
    {
        var audit = new InMemoryAuditLog();
        var gate = new ChangedTestCommandGate(audit);
        var huge = new string('x', ChangedTestCommandGate.DriftExcerptChars * 4);
        gate.SetFlagged(
            AgentId, ChangedTestCommandGate.TestCommandItem, changed: true,
            new ChangedTestCommandGate.CommandDrift(VerifyPath, "dotnet test", huge));

        gate.Acknowledge(AgentId, "owner@example");

        var ack = Assert.Single(audit.Read().Where(e => e.Type == "acknowledged_flagged_change"));
        Assert.EndsWith("…(truncated)", ack.Fields["to"], StringComparison.Ordinal);
        Assert.True(ack.Fields["to"].Length < huge.Length);
        Assert.Equal(64, ack.Fields["to_hash"].Length);
    }

    /// <summary>
    /// Nobody named means "unknown", never a borrowed name. The client-side mirror of this gate has no
    /// daemon identity to offer, and an audit chain that quietly assigned it one would be lying exactly
    /// where nobody looks.
    /// </summary>
    [Fact]
    public void AnUnattributedWaiver_SaysUnknown()
    {
        var audit = new InMemoryAuditLog();
        var gate = new ChangedTestCommandGate(audit);
        gate.SetFlagged(AgentId, ChangedTestCommandGate.TestCommandItem, changed: true, SelfGreen);

        gate.Acknowledge(AgentId);

        var ack = Assert.Single(audit.Read().Where(e => e.Type == "acknowledged_flagged_change"));
        Assert.Equal("unknown", ack.Fields["by"]);
    }
}
