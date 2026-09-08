using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Git.Review;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <see cref="FlaggedChangesPanelViewModel"/> — the must-acknowledge gate panel, and the other Pro
/// ViewModel that had no tests.
///
/// <para>Every property here decides whether a human believes a merge gate has been cleared, so these
/// name the false impression each one prevents. The load-bearing ones: a checkmark may only appear when
/// the GATE recorded it (the call returning is not the evidence); a panel that cannot reach the gate must
/// say so rather than look like it works; and there is no route to acknowledge everything at once — the
/// item-by-item shape is the feature.</para>
/// </summary>
public sealed class FlaggedChangesPanelViewModelTests
{
    private const string AgentId = "agent-a";

    private static FlaggedItem Item(string kind, string path, string fact, bool acknowledged = false) =>
        new($"{kind}|{path}|hash", path, "ExecutableConfig", fact, acknowledged);

    // ---- the live (daemon) panel: the gate's answer, never the client's optimism ----

    /// <summary>
    /// A checkmark appears only because the GATE said so.
    ///
    /// <para>This is the whole point of the live panel. The merge gate is daemon-side and the ack RPC is
    /// addressed by item id; a panel that ticked the row because the call returned would be showing a
    /// cleared gate that no merge consults — which is exactly the defect the seam was built to end.</para>
    /// </summary>
    [Fact]
    public async Task AnAcknowledgmentTheGateRecorded_TicksTheRow()
    {
        var live = new FakeSource { Items = { Item("RiskCategory", ".github/workflows/ci.yml", "workflow edited") } };
        var vm = new FlaggedChangesPanelViewModel(AgentId, live);
        var row = Assert.Single(vm.Items);

        live.NextOutcome = new FlaggedAckOutcome(Acknowledged: true, CanMerge: true, "");
        await row.AcknowledgeCommand.ExecuteAsync(null);

        Assert.True(row.IsAcknowledged);
        Assert.False(row.HasAckNotice);
        Assert.Equal(0, vm.PendingCount);
        Assert.True(vm.AllAcknowledged);
    }

    /// <summary>
    /// An acknowledgment the gate REFUSED leaves the row unticked and states the refusal.
    ///
    /// <para>The refusal arrives as an ordinary successful RPC carrying <c>acknowledged=false</c>, so "no
    /// exception" is not evidence of anything. A row that ticked itself here would tell a reviewer the
    /// gate was cleared while the merge stayed blocked — the worst possible answer on this screen.</para>
    /// </summary>
    [Fact]
    public async Task AnAcknowledgmentTheGateRefused_LeavesTheRowUnticked_AndSaysWhy()
    {
        var live = new FakeSource { Items = { Item("RiskCategory", "Dockerfile", "base image changed") } };
        var vm = new FlaggedChangesPanelViewModel(AgentId, live);
        var row = Assert.Single(vm.Items);

        live.NextOutcome = FlaggedAckOutcome.Refused("the branch moved since this item was raised");
        await row.AcknowledgeCommand.ExecuteAsync(null);

        Assert.False(row.IsAcknowledged);
        Assert.True(row.HasAckNotice);
        Assert.Contains("Not acknowledged", row.AckNotice, StringComparison.Ordinal);
        Assert.Contains("the branch moved", row.AckNotice, StringComparison.Ordinal);
        Assert.Equal(1, vm.PendingCount);
        Assert.False(vm.AllAcknowledged);
    }

    /// <summary>A transport failure is the same class of answer as a refusal, and must read as one — never
    /// as a row stuck mid-flight, and never as a tick.</summary>
    [Fact]
    public async Task AnAcknowledgmentThatThrows_IsReportedAsARefusal_AndNeverStrandsTheRow()
    {
        var live = new FakeSource { Items = { Item("LockfileCve", "package-lock.json", "CVE-2026-1") } };
        live.AckFailure = new InvalidOperationException("the daemon is not reachable");
        var vm = new FlaggedChangesPanelViewModel(AgentId, live);
        var row = Assert.Single(vm.Items);

        await row.AcknowledgeCommand.ExecuteAsync(null);

        Assert.False(row.IsAcknowledged);
        Assert.False(row.IsAcknowledging);   // never left spinning
        Assert.Contains("did not reach the merge gate", row.AckNotice, StringComparison.Ordinal);
        Assert.Contains("the daemon is not reachable", row.AckNotice, StringComparison.Ordinal);
    }

    /// <summary>
    /// When acknowledgments cannot reach the gate at all, the controls are disabled and the panel says so.
    ///
    /// <para>An enabled control that cannot do anything is worse than an absent one here: pressing it would
    /// be read as having cleared the item.</para>
    /// </summary>
    [Fact]
    public void WhenTheGateIsUnreachable_TheControlsAreDisabled_AndThePanelSaysWhy()
    {
        var live = new FakeSource
        {
            Items = { Item("RiskCategory", "Dockerfile", "base image changed") },
            AckBlockedReason = "no repository is active for agents yet",
        };

        var vm = new FlaggedChangesPanelViewModel(AgentId, live);

        Assert.True(vm.HasUnavailableNotice);
        Assert.Contains("no repository is active", vm.UnavailableNotice, StringComparison.Ordinal);

        var row = Assert.Single(vm.Items);
        Assert.False(row.CanAck);
        Assert.False(row.AcknowledgeCommand.CanExecute(null));
        Assert.Contains("no repository is active", row.AckTooltip, StringComparison.Ordinal);
    }

    /// <summary>The panel renders the DAEMON's acknowledged flag, so an item another surface (or an earlier
    /// session) already cleared arrives ticked rather than asking to be cleared twice.</summary>
    [Fact]
    public void AnItemTheDaemonAlreadyHasAcknowledged_ArrivesTicked()
    {
        var live = new FakeSource
        {
            Items =
            {
                Item("RiskCategory", "Dockerfile", "base image changed", acknowledged: true),
                Item("RiskCategory", ".githooks/pre-commit", "hook added"),
            },
        };

        var vm = new FlaggedChangesPanelViewModel(AgentId, live);

        Assert.Equal(2, vm.Items.Count);
        Assert.True(vm.Items.Single(i => i.Path == "Dockerfile").IsAcknowledged);
        Assert.Equal(1, vm.PendingCount);
        Assert.False(vm.AllAcknowledged);
    }

    /// <summary>
    /// A queue push must not throw away a row's own refusal notice.
    ///
    /// <para>The rail re-projects on every daemon event, so <c>Refresh</c> runs constantly. Rebuilding the
    /// rows each time would wipe the sentence explaining why an acknowledgment did not take, typically
    /// within a second of the human reading it.</para>
    /// </summary>
    [Fact]
    public async Task AnUnrelatedRefresh_KeepsARowsOwnRefusalNotice()
    {
        var live = new FakeSource { Items = { Item("RiskCategory", "Dockerfile", "base image changed") } };
        var vm = new FlaggedChangesPanelViewModel(AgentId, live);
        var row = Assert.Single(vm.Items);

        live.NextOutcome = FlaggedAckOutcome.Refused("the branch moved");
        await row.AcknowledgeCommand.ExecuteAsync(null);
        var notice = row.AckNotice;
        Assert.NotEqual("", notice);

        vm.Refresh();   // an unrelated queue push

        Assert.Same(row, Assert.Single(vm.Items));   // reconciled in place, not rebuilt
        Assert.Equal(notice, row.AckNotice);
    }

    /// <summary>Items the daemon has dropped leave the panel; the ones it still reports stay. Rendering a
    /// stale row would ask a human to clear a gate that no longer exists.</summary>
    [Fact]
    public void RowsTheDaemonNoLongerReports_AreRemovedOnRefresh()
    {
        var live = new FakeSource
        {
            Items =
            {
                Item("RiskCategory", "Dockerfile", "base image changed"),
                Item("RiskCategory", ".githooks/pre-commit", "hook added"),
            },
        };
        var vm = new FlaggedChangesPanelViewModel(AgentId, live);
        Assert.Equal(2, vm.Items.Count);

        live.Items.RemoveAll(i => i.Path == ".githooks/pre-commit");
        vm.Refresh();

        Assert.Equal("Dockerfile", Assert.Single(vm.Items).Path);
    }

    /// <summary>
    /// The kind is read out of the item ID, never out of the category word.
    ///
    /// <para><c>Id</c> is <c>kind|path|contentHash</c>, so the daemon's own <see cref="FlaggedKind"/> is on
    /// the wire verbatim, while <c>Category</c> carries a <see cref="RiskCategory"/> name — a different
    /// enumeration. Parsing the category as a kind succeeded for exactly one value and fell back to
    /// <see cref="FlaggedKind.RiskCategory"/> for the rest, so a daemon-flagged CVE, install script or
    /// unchecked advisory all reached the surface labelled as an ordinary risk hunk.</para>
    /// </summary>
    [Theory]
    [InlineData("LockfileCve", FlaggedKind.LockfileCve)]
    [InlineData("LockfileScript", FlaggedKind.LockfileScript)]
    [InlineData("LockfileAdvisoryUnknown", FlaggedKind.LockfileAdvisoryUnknown)]
    [InlineData("OutOfApprovedScope", FlaggedKind.OutOfApprovedScope)]
    [InlineData("DeclaredDeviation", FlaggedKind.DeclaredDeviation)]
    public void TheRowsKind_ComesFromTheItemId_NotTheCategoryWord(string kindOnTheWire, FlaggedKind expected)
    {
        var live = new FakeSource { Items = { Item(kindOnTheWire, "package-lock.json", "a fact") } };

        var vm = new FlaggedChangesPanelViewModel(AgentId, live);

        Assert.Equal(expected, Assert.Single(vm.Items).Kind);
    }

    /// <summary>The RT-D2 gate item has a well-known id rather than the <c>kind|path|hash</c> shape, and
    /// still has to render as what it is — the daemon accepts acks for it by that exact id.</summary>
    [Fact]
    public void TheChangedTestCommandItem_IsRecognisedByItsWellKnownId()
    {
        var live = new FakeSource
        {
            Items = { new FlaggedItem("changed-test-command", "", "ExecutableConfig",
                "the test command changed on this branch vs main", false) },
        };

        var vm = new FlaggedChangesPanelViewModel(AgentId, live);

        var row = Assert.Single(vm.Items);
        Assert.Equal(FlaggedKind.ChangedTestCommand, row.Kind);
        // An empty path is the verification command, not a file — say which.
        Assert.Equal("(verification command)", row.Path);
    }

    /// <summary>
    /// Worker-declared deviations get their own heading, because they are a different KIND of claim: every
    /// other row is something the daemon detected in the diff, and this is something the worker said about
    /// its own work — the one row here whose truth nothing verifies.
    /// </summary>
    [Fact]
    public void DeclaredDeviations_AreCalledOutByTheirOwnHeading()
    {
        var live = new FakeSource
        {
            Items =
            {
                Item("DeclaredDeviation", "src/Calc.cs", "added validation the approach ruled out"),
                Item("DeclaredDeviation", "src/Parse.cs", "changed a pre-existing signature"),
                Item("RiskCategory", "Dockerfile", "base image changed"),
            },
        };

        var vm = new FlaggedChangesPanelViewModel(AgentId, live);

        Assert.Equal("WORKER-DECLARED DEVIATIONS (2)", vm.DeviationHeading);
    }

    [Fact]
    public void WithNoDeviations_ThereIsNoDeviationHeading()
    {
        var live = new FakeSource { Items = { Item("RiskCategory", "Dockerfile", "base image changed") } };

        var vm = new FlaggedChangesPanelViewModel(AgentId, live);

        Assert.Equal("", vm.DeviationHeading);
        Assert.True(vm.HasItems);
    }

    /// <summary>The gate is item-by-item BY CONSTRUCTION: there is no acknowledge-all command anywhere on
    /// the panel, and a single global checkbox is a documented rejection trigger. Pinned as a reflection
    /// test because the defect it prevents is somebody adding one.</summary>
    [Fact]
    public void ThePanel_ExposesNoAcknowledgeAllCommand()
    {
        var offenders = typeof(FlaggedChangesPanelViewModel).GetMembers()
            .Select(m => m.Name)
            .Where(n => n.Contains("All", StringComparison.Ordinal)
                     && n.Contains("Acknowledge", StringComparison.Ordinal)
                     // AllAcknowledged is a derived FACT the surface reads, not an action it offers —
                     // it and its generated accessors are the one legitimate "…All…Acknowledge…" name.
                     && n != "AllAcknowledged"
                     && n != "get_AllAcknowledged"
                     && n != "set_AllAcknowledged")
            .ToArray();

        Assert.Empty(offenders);
    }

    // ---- the local (in-process store) panel ------------------------------

    /// <summary>The local panel is the design/harness composition: its acks go to the store it was built
    /// with, and the panel's totals follow the store rather than a local flag.</summary>
    [Fact]
    public async Task TheLocalPanel_AcknowledgesThroughItsStore()
    {
        var store = new AcknowledgmentStore(AgentId);
        store.SetFlagged(new[]
        {
            new FlaggedChange("Dockerfile", RiskCategory.ExecutableConfig, FlaggedKind.RiskCategory,
                "hash-1", "base image changed"),
        });

        var changes = 0;
        var vm = new FlaggedChangesPanelViewModel(store, AgentId, onChanged: () => changes++);
        var row = Assert.Single(vm.Items);
        Assert.False(vm.IsLive);   // acks here clear a store, not the daemon gate

        await row.AcknowledgeCommand.ExecuteAsync(null);

        Assert.True(store.IsAcknowledged(row.ItemId));
        Assert.True(vm.AllAcknowledged);
        Assert.Equal(0, vm.PendingCount);
        Assert.Equal(1, changes);  // the cockpit is told, so it can re-read CanMerge
    }

    /// <summary>A new push invalidates every prior acknowledgment (they bound to the old content hash), and
    /// the panel has to SAY so — an ack silently reset is an ack the human still believes they made.</summary>
    [Fact]
    public void TheLocalPanel_SaysHowManyAcksAPushReset()
    {
        var store = new AcknowledgmentStore(AgentId);
        store.SetFlagged(new[]
        {
            new FlaggedChange("Dockerfile", RiskCategory.ExecutableConfig, FlaggedKind.RiskCategory,
                "hash-1", "base image changed"),
        });
        var vm = new FlaggedChangesPanelViewModel(store, AgentId);
        vm.Items[0].AcknowledgeCommand.Execute(null);

        // A new push: same path, different content hash.
        store.SetFlagged(new[]
        {
            new FlaggedChange("Dockerfile", RiskCategory.ExecutableConfig, FlaggedKind.RiskCategory,
                "hash-2", "base image changed again"),
        });
        vm.Refresh();

        Assert.Contains("1 item(s) reset", vm.ResetNotice, StringComparison.Ordinal);
        Assert.False(vm.Items[0].IsAcknowledged);
        Assert.Equal(1, vm.PendingCount);
    }

    /// <summary>The daemon-backed source, with the gate's answer under the test's control.</summary>
    private sealed class FakeSource : IFlaggedChangeSource
    {
        public List<FlaggedItem> Items { get; } = new();

        public string AckBlockedReason { get; set; } = "";

        public FlaggedAckOutcome NextOutcome { get; set; } =
            new(Acknowledged: true, CanMerge: true, "");

        public Exception? AckFailure { get; set; }

        public IReadOnlyList<FlaggedItem> FlaggedFor(string agentId) => Items.ToArray();

        public bool CanMerge(string agentId, out string reason)
        {
            reason = "";
            return true;
        }

        public bool CanAcknowledge(out string reason)
        {
            reason = AckBlockedReason;
            return AckBlockedReason.Length == 0;
        }

        public Task<FlaggedAckOutcome> AcknowledgeAsync(string agentId, string itemId, CancellationToken ct)
        {
            if (AckFailure is { } ex)
            {
                return Task.FromException<FlaggedAckOutcome>(ex);
            }

            // Model the gate: the daemon's next projection reports what it actually recorded.
            if (NextOutcome.Acknowledged)
            {
                var i = Items.FindIndex(x => x.Id == itemId);
                if (i >= 0) Items[i] = Items[i] with { Acknowledged = true };
            }

            return Task.FromResult(NextOutcome);
        }
    }
}
