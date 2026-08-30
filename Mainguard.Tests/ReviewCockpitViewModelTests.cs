using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git.Models;
using Mainguard.Git.Review;
using Mainguard.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-11 cockpit ViewModel behavior: risk ordering that reorders but never hides (invariant 3),
/// provenance chips (present + honest absence), the "bring branch local" T-29 round-trip, and
/// review-sprint deferred hunks recorded as unviewed events (test 11). The VM composes pure Core — it
/// contains no rule logic (invariant 1).
/// </summary>
public class ReviewCockpitViewModelTests
{
    private static FilePatch Patch(string path, params DiffHunk[] hunks) => new()
    {
        Header = $"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n",
        Hunks = hunks,
    };

    private static DiffHunk Hunk(int newStart, int newCount, string heading, params (DiffLineKind Kind, string Text)[] lines) => new()
    {
        OldStart = newStart,
        OldCount = newCount,
        NewStart = newStart,
        NewCount = newCount,
        SectionHeading = heading,
        Lines = lines.Select(l => new DiffLine { Kind = l.Kind, Text = l.Text }).ToList(),
    };

    private static DiffHunk Source(int start = 1) => Hunk(start, 1, "", (DiffLineKind.Add, "code"));

    private static DiffHunk Scripts() => Hunk(1, 3, "",
        (DiffLineKind.Context, "  \"scripts\": {"),
        (DiffLineKind.Add, "    \"postinstall\": \"x\","),
        (DiffLineKind.Context, "  },"));

    private static ReviewCockpitContext Context(IReadOnlyList<FilePatch> diff) =>
        new("loom-1", "Loom-1", "fix/x", diff);

    // ---- The approved approach, beside the diff (2026-08-31) ----------------
    //
    // A review is a comparison and this surface only ever had one side of it. The approved `approach`
    // was written by the worker, decided on by the human, and then never rendered anywhere — so a branch
    // that did the opposite of what it proposed looked exactly like one that did it faithfully. Measured:
    // an approved approach said the module had no validation idiom and the plan would keep plain `a / b`;
    // the branch shipped a throwing validation layer that changed three pre-existing functions, with the
    // flagged list EMPTY (the file scope was honoured) and the verification green (the worker wrote the
    // tests). Nothing below judges the diff; it puts the thing it was supposed to match on screen.

    /// <summary>
    /// The approved plan's approach and its identity are rendered, verbatim. Verbatim matters: a
    /// summarised approach is the surface choosing which sentence the reviewer gets to compare against,
    /// and the sentence that gets dropped is the one the diff disagrees with.
    /// </summary>
    [Fact]
    public void TheApprovedApproachIsRenderedBesideTheDiff()
    {
        var vm = new ReviewCockpitViewModel(Context(new[] { Patch("src/calc.js", Source()) }) with
        {
            ApprovedPlanId = "3f2a9c1b7d4e",
            ApprovedPlanTitle = "Add divide() to the calculator",
            ApprovedApproach = "the module has no error-handling or validation idiom anywhere in it, so "
                + "I will keep plain a / b and let the language semantics stand",
            DeviationDeclaration = "None",
        });

        Assert.True(vm.HasApprovedApproach);
        Assert.Contains("keep plain a / b", vm.ApprovedApproachText, StringComparison.Ordinal);
        Assert.Contains("Add divide() to the calculator", vm.ApprovedPlanHeading, StringComparison.Ordinal);
        Assert.Contains("3f2a9c1", vm.ApprovedPlanHeading, StringComparison.Ordinal);
    }

    /// <summary>
    /// No approval, no panel. A manual agent and an external-PR head were never approved against
    /// anything, and an empty "approved approach" card would assert an approval nobody gave — the
    /// fabricated-reassurance failure this whole surface exists to avoid.
    /// </summary>
    [Fact]
    public void NoPanelIsDrawnForABranchThatWasNeverApprovedAgainstAnything()
    {
        var vm = new ReviewCockpitViewModel(Context(new[] { Patch("src/calc.js", Source()) }));

        Assert.False(vm.HasApprovedApproach);
        Assert.Equal("", vm.ApprovedApproachText);
        Assert.Equal("", vm.DeviationDeclarationText);
    }

    /// <summary>
    /// <b>The three declarations read as three different things.</b> This is the assertion that keeps
    /// the mechanism from becoming a rubber stamp at the last layer: "the worker says it matches" and
    /// "nobody ever asked" must not produce the same sentence, and the one that is an unanswered
    /// question must not read as reassurance.
    /// </summary>
    [Theory]
    [InlineData("None", false, "declared no deviation")]
    [InlineData("Declared", true, "declared it departed")]
    [InlineData("NotDeclared", true, "No deviation declaration is on record")]
    public void TheThreeDeclarationsAreRenderedAsThreeDifferentSentences(
        string declaration, bool expectedAttention, string expectedText)
    {
        var vm = new ReviewCockpitViewModel(Context(new[] { Patch("src/calc.js", Source()) }) with
        {
            ApprovedPlanTitle = "Add divide()",
            ApprovedApproach = "keep plain a / b",
            DeviationDeclaration = declaration,
        });

        Assert.Contains(expectedText, vm.DeviationDeclarationText, StringComparison.Ordinal);
        Assert.Equal(expectedAttention, vm.DeviationNeedsAttention);
    }

    /// <summary>
    /// A "None" declaration is rendered as the CLAIM it is, not as a verification. The worker's tests
    /// are the worker's own, so a line reading like a check would be the surface manufacturing the exact
    /// reassurance the defect ran on.
    /// </summary>
    [Fact]
    public void AnAssertedNoneIsRenderedAsAClaim_NotAsACheck()
    {
        var vm = new ReviewCockpitViewModel(Context(new[] { Patch("src/calc.js", Source()) }) with
        {
            ApprovedApproach = "keep plain a / b",
            DeviationDeclaration = "None",
        });

        Assert.Contains("its claim, not a check", vm.DeviationDeclarationText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A daemon that sends no declaration at all (one predating the field) gets no sentence — this
    /// surface does not pick one of the three answers on the daemon's behalf.
    /// </summary>
    [Fact]
    public void ADaemonThatSaysNothingAboutDeviations_GetsNoSentenceInventedForIt()
    {
        var vm = new ReviewCockpitViewModel(Context(new[] { Patch("src/calc.js", Source()) }) with
        {
            ApprovedApproach = "keep plain a / b",
            DeviationDeclaration = null,
        });

        Assert.True(vm.HasApprovedApproach);
        Assert.Equal("", vm.DeviationDeclarationText);
    }

    /// <summary>
    /// The flagged panel names worker-declared rows as their own group. They are a different KIND of
    /// claim from everything else in that panel — the rest is what the daemon found in the diff, this is
    /// what the worker said about its own work — and the reviewer has to read it against the approach
    /// above rather than against the file the row names.
    /// </summary>
    [Fact]
    public void DeclaredDeviationsAreNamedAsTheirOwnGroupInTheFlaggedPanel()
    {
        var live = new FakeFlaggedSource { MergeAllowed = false, MergeReason = "1 flagged change" };
        live.Items.Add(new FlaggedItem(
            $"{FlaggedKind.DeclaredDeviation}|(worker-declared deviation)|abc",
            "(worker-declared deviation)", "Source",
            "the worker declared it departed from the approved approach — \"added RangeError\"", false));

        var vm = new ReviewCockpitViewModel(
            Context(new[] { Patch("src/calc.js", Source()) }), live: live);

        Assert.Equal("WORKER-DECLARED DEVIATIONS (1)", vm.FlaggedPanel.DeviationHeading);
    }

    [Fact]
    public void RiskOrdering_Reorders_ButNeverHides()
    {
        // Files supplied in benign→dangerous order; the cockpit must surface the dangerous one first,
        // and every hunk must remain rendered (nothing collapsed by rank).
        var diff = new List<FilePatch>
        {
            Patch("docs/notes.md", Source(), Source(3)),                 // Docs (rank 7), 2 hunks
            Patch("src/Plain.cs", Source()),                            // Source (rank 6), 1 hunk
            Patch("package.json", Scripts()),                          // ExecutableConfig (rank 0), 1 hunk
        };

        var vm = new ReviewCockpitViewModel(Context(diff));

        Assert.Equal(RiskCategory.ExecutableConfig, vm.Files[0].Category);   // dangerous first
        Assert.Equal(RiskCategory.Docs, vm.Files[^1].Category);             // docs last
        Assert.Equal(4, vm.TotalHunkCount);
        Assert.Equal(vm.TotalHunkCount, vm.RenderedHunkCount);              // never hides (invariant 3)
    }

    [Fact]
    public void Provenance_FromTrace_RendersChip_AndAbsenceIsHonest()
    {
        var diff = new List<FilePatch> { Patch("src/Auth.cs", Hunk(12, 3, "", (DiffLineKind.Add, "x"))) };
        var ranges = ProvenanceReader.ParseTraceRanges(
            """{ "entries": [ { "file": "src/Auth.cs", "startLine": 10, "endLine": 20, "agent": "Loom-3", "task": "7", "sha": "a1b2c3d4" } ] }""");

        var withTrace = new ReviewCockpitViewModel(Context(diff) with { TraceRanges = ranges });
        Assert.True(withTrace.Files[0].Hunks[0].HasProvenance);
        Assert.Contains("Loom-3", withTrace.Files[0].Hunks[0].ProvenanceChip);

        // No trace, no trailers → no chip, no crash (V-6).
        var without = new ReviewCockpitViewModel(Context(diff));
        Assert.False(without.Files[0].Hunks[0].HasProvenance);
    }

    [Fact]
    public async Task BringBranchLocal_InvokesT29Callback()
    {
        var diff = new List<FilePatch> { Patch("src/Plain.cs", Source()) };
        string? broughtAgent = null;
        var vm = new ReviewCockpitViewModel(
            Context(diff),
            bringLocal: (agentId, ct) => { broughtAgent = agentId; return Task.CompletedTask; });

        await vm.BringBranchLocalCommand.ExecuteAsync(null);
        Assert.Equal("loom-1", broughtAgent);
    }

    [Fact]
    public void ReviewSprint_DeferredHunksRecordedUnviewed()
    {
        var diff = new List<FilePatch>
        {
            Patch("package.json", Scripts()),
            Patch("src/A.cs", Source()),
            Patch("src/B.cs", Source()),
        };
        var vm = new ReviewCockpitViewModel(Context(diff));

        vm.StartReviewSprint(riskBudget: 10);
        vm.SprintMarkViewed();   // view the first (highest-risk) hunk
        vm.SprintNext();
        vm.SprintMarkViewed();   // view the second
        // Third hunk deferred (never marked viewed).
        vm.EndReviewSprint();

        var unviewed = vm.ViewedEvents.Where(e => !e.Viewed).ToList();
        Assert.Single(unviewed);                       // exactly one deferred hunk
        Assert.Equal(2, vm.ViewedEvents.Count(e => e.Viewed));
    }

    [Fact]
    public void FlaggedGate_BlocksMerge_UntilAllItemsAcked()
    {
        var diff = new List<FilePatch> { Patch("package.json", Scripts()), Patch(".github/workflows/ci.yml", Source()) };
        var gate = new FlaggedChangeGate();
        var vm = new ReviewCockpitViewModel(Context(diff), flaggedGate: gate);

        Assert.True(vm.FlaggedPanel.HasItems);
        Assert.Equal(2, vm.FlaggedPanel.PendingCount);

        foreach (var item in vm.FlaggedPanel.Items.ToList())
        {
            item.AcknowledgeCommand.Execute(null);
        }

        Assert.True(vm.FlaggedPanel.AllAcknowledged);
        Assert.True(gate.Allows("loom-1", out _));
    }

    // ---- the live (daemon-backed) overlay panel --------------------------
    //
    // The overlay's items and acks both travel through IFlaggedChangeSource. These pin the two ways it
    // must refuse to look like it worked; the end-to-end proof that a real acknowledgment reaches the real
    // daemon gate (blocked → ack on the overlay → merge permitted) is
    // Mainguard.Server.Tests.ReviewCockpitOverlayAckTests.

    [Fact]
    public void LivePanel_RendersTheDaemonsItems_AndTheDaemonsMergeAnswer()
    {
        var source = new FakeFlaggedSource
        {
            Items = { new FlaggedItem("changed-test-command", "(verification command)", "ExecutableConfig", "the test command changed on this branch vs main", false) },
            MergeAllowed = false,
            MergeReason = "test command changed on this branch",
        };

        var vm = new ReviewCockpitViewModel(Context(new List<FilePatch>()), live: source);

        Assert.True(vm.FlaggedPanel.IsLive);
        Assert.True(vm.FlaggedPanel.HasItems);
        Assert.Equal("changed-test-command", Assert.Single(vm.FlaggedPanel.Items).ItemId);
        Assert.False(vm.CanMerge);
        Assert.Equal("test command changed on this branch", vm.MergeReason);
    }

    [Fact]
    public void LivePanel_WhenTheGateIsUnreachable_SaysSo_AndOffersNoAckControl()
    {
        var source = new FakeFlaggedSource
        {
            Items = { new FlaggedItem("changed-test-command", "(verification command)", "ExecutableConfig", "drifted", false) },
            AckAvailable = false,
            AckUnavailableReason = "no repository is open in the daemon — acknowledging here would clear nothing.",
        };

        var vm = new ReviewCockpitViewModel(Context(new List<FilePatch>()), live: source);
        var row = Assert.Single(vm.FlaggedPanel.Items);

        Assert.True(vm.FlaggedPanel.HasUnavailableNotice);
        Assert.Contains("no repository", vm.FlaggedPanel.UnavailableNotice, StringComparison.OrdinalIgnoreCase);
        Assert.False(row.CanAck);
        Assert.False(row.AcknowledgeCommand.CanExecute(null));
        Assert.Contains("no repository", row.AckTooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LivePanel_WhenTheGateRefusesTheAck_TheRowStaysUnacknowledged_AndSaysWhy()
    {
        // The call succeeds; the GATE does not record it. "The RPC was invoked" must never read as done.
        var source = new FakeFlaggedSource
        {
            Items = { new FlaggedItem("changed-test-command", "(verification command)", "ExecutableConfig", "drifted", false) },
            Outcome = FlaggedAckOutcome.Refused("the daemon did not record this acknowledgment"),
        };

        var vm = new ReviewCockpitViewModel(Context(new List<FilePatch>()), live: source);
        var row = Assert.Single(vm.FlaggedPanel.Items);

        await row.AcknowledgeCommand.ExecuteAsync(null);

        Assert.False(row.IsAcknowledged);
        Assert.Equal("Acknowledge", row.AckLabel);
        Assert.True(row.HasAckNotice);
        Assert.Contains("Not acknowledged", row.AckNotice, StringComparison.Ordinal);
        Assert.False(vm.FlaggedPanel.AllAcknowledged);
    }

    /// <summary>A source the panel can be driven against without a daemon (the daemon-backed proof is in
    /// Mainguard.Server.Tests). It records the id it was asked to acknowledge, so a per-item ack that
    /// addressed the wrong row would be visible here too.</summary>
    private sealed class FakeFlaggedSource : Mainguard.Agents.UI.Services.IFlaggedChangeSource
    {
        public List<FlaggedItem> Items { get; } = new();
        public bool AckAvailable { get; init; } = true;
        public string AckUnavailableReason { get; init; } = "";
        public bool MergeAllowed { get; init; }
        public string MergeReason { get; init; } = "";
        public FlaggedAckOutcome Outcome { get; init; } = new(true, true, "");
        public List<string> Acknowledged { get; } = new();

        public IReadOnlyList<FlaggedItem> FlaggedFor(string agentId) => Items;

        public bool CanMerge(string agentId, out string reason)
        {
            reason = MergeReason;
            return MergeAllowed;
        }

        public bool CanAcknowledge(out string reason)
        {
            reason = AckUnavailableReason;
            return AckAvailable;
        }

        public Task<FlaggedAckOutcome> AcknowledgeAsync(string agentId, string itemId, CancellationToken ct)
        {
            Acknowledged.Add(itemId);
            return Task.FromResult(Outcome);
        }
    }
}
