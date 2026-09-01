using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Models;
using Mainguard.Git.Review;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The worker-declared-deviation mechanism (2026-08-31), which closes the half of an approval nothing
/// was measuring.
///
/// <para><b>The defect, as it actually happened.</b> A human approved a plan whose <c>approach</c> said
/// the module had no error-handling or validation idiom anywhere in it, so the worker would keep plain
/// <c>a / b</c> and let the language semantics stand. The worker then shipped <c>divide()</c> throwing
/// <c>RangeError</c> on zero, plus a <c>checkOperands</c>/<c>checkResult</c> layer throwing
/// <c>TypeError</c> on EVERY helper — silently changing the behaviour of pre-existing <c>add</c> /
/// <c>subtract</c> / <c>square</c> (<c>add('2',3)</c> began throwing instead of returning <c>'23'</c>).
/// Nothing caught it, and nothing was broken:</para>
/// <list type="bullet">
/// <item>the plan's <c>scope</c> WAS honoured, so the out-of-scope arm saw nothing and
/// <c>FlaggedItems</c> was empty;</item>
/// <item><c>CanMerge</c> was true and the state was <c>Verified</c>, because the worker also authored
/// the tests asserting its own new behaviour. <b>A worker that owns its tests can always turn its
/// divergence green</b> — verification proves a diff is self-consistent, never that it matches what was
/// approved;</item>
/// <item>nothing anywhere compared the approved approach against the produced diff, and nothing here
/// does either. What changed is that the worker must ANSWER, that the answer is attributable, and that
/// silence is not one of the available answers.</item>
/// </list>
/// </summary>
public class WorkerDeviationDeclarationTests
{
    private const string Worker = "loom-1";
    private const string DiffHash = "deadbeef";

    // ---- The three outcomes, which is the whole design ---------------------

    /// <summary>
    /// <b>Silence and "I checked, none" are different facts, and only one of them is reassuring.</b>
    /// This is the property the entire mechanism rests on: an optional declaration would be absent on
    /// exactly the runs that needed it, and an absent must-acknowledge item is an ACKNOWLEDGED one (an
    /// empty flagged set is <see cref="AcknowledgmentStore.AllAcknowledged"/>). So a branch nobody ever
    /// asked must flag, and one whose worker asserted it followed the approach must not.
    ///
    /// <para>Collapsing the two — returning no item for <see cref="DeviationDeclaration.NotDeclared"/> —
    /// makes this red, and is the mutation that turns the whole feature into a field nobody fills in.
    /// Same call <c>FlaggedKind.LockfileAdvisoryUnknown</c> and <c>WorkerPlanGate.MergeEvidence</c>
    /// already make.</para>
    /// </summary>
    [Fact]
    public void NoDeclarationAtAll_IsFlagged_WhileAnExplicitNoneIsNot()
    {
        var silent = DeviationReview.ItemsFor(DeviationDeclaration.NotDeclared, null, DiffHash);
        var asserted = DeviationReview.ItemsFor(DeviationDeclaration.None, null, DiffHash);

        var missing = Assert.Single(silent);
        Assert.Equal(FlaggedKind.DeviationDeclarationMissing, missing.Kind);
        Assert.Contains("no deviation declaration is on record", missing.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(asserted);
    }

    /// <summary>
    /// A declared departure becomes its own must-acknowledge row carrying the worker's own words. The
    /// words are the point — "the worker deviated" is not something a human can weigh against an
    /// approved approach, and a row that summarised it would be the surface deciding what the reviewer
    /// gets to see.
    /// </summary>
    [Fact]
    public void EachDeclaredDeviation_BecomesItsOwnRow_CarryingTheWorkersWords()
    {
        var items = DeviationReview.ItemsFor(
            DeviationDeclaration.Declared,
            new[]
            {
                "added RangeError on divide-by-zero; the approach said keep plain a / b",
                "added checkOperands to every helper, changing add('2',3)",
            },
            DiffHash);

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(FlaggedKind.DeclaredDeviation, i.Kind));
        Assert.Contains(items, i => i.Detail.Contains("keep plain a / b", StringComparison.Ordinal));
        Assert.Contains(items, i => i.Detail.Contains("changing add('2',3)", StringComparison.Ordinal));

        // Distinct ids, or the second departure would silently ride the first one's acknowledgment.
        Assert.Equal(2, items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Invariant 2 for a branch-level row: a new push invalidates the acknowledgment. Per-file items get
    /// this for free because their content hash is the file's; a row about the WHOLE branch would not,
    /// so the branch's diff hash is folded into it. Without that, "yes, I read that deviation" would
    /// survive a push that rewrote every line it was granted for.
    /// </summary>
    [Fact]
    public void ADeviationsAcknowledgmentDoesNotSurviveTheNextPush()
    {
        var before = DeviationReview.ItemsFor(
            DeviationDeclaration.Declared, new[] { "same words" }, "diff-hash-before");
        var after = DeviationReview.ItemsFor(
            DeviationDeclaration.Declared, new[] { "same words" }, "diff-hash-after");

        Assert.NotEqual(before[0].Id, after[0].Id);

        var store = new AcknowledgmentStore(Worker);
        store.SetFlagged(before);
        Assert.True(store.Acknowledge(before[0].Id));
        Assert.True(store.AllAcknowledged);

        store.SetFlagged(after);
        Assert.False(store.AllAcknowledged);
    }

    /// <summary>
    /// The diff hash is over CONTENT, not over the file list — a branch that rewrites a line without
    /// adding a file has still changed the thing the human acknowledged. This is what makes the test
    /// above about pushes rather than about renames.
    /// </summary>
    [Fact]
    public void TheBranchDiffHash_ChangesWhenAnyLineChanges()
    {
        var one = FlaggedChangeDetector.HashDiff(new[] { Patch("src/a.js", "const x = 1;") });
        var two = FlaggedChangeDetector.HashDiff(new[] { Patch("src/a.js", "const x = 2;") });
        var same = FlaggedChangeDetector.HashDiff(new[] { Patch("src/a.js", "const x = 1;") });

        Assert.NotEqual(one, two);
        Assert.Equal(one, same);
    }

    // ---- The record: where a declaration is written, and what it cannot do --

    /// <summary>
    /// The declaration lands on the plan that AUTHORISED the work — the same record
    /// <see cref="PlanApprovalService.ApprovedForWorker"/> resolves for the F6 scope comparison — so the
    /// approach shown at review and the deviations shown beside it can never describe two different
    /// plans.
    /// </summary>
    [Fact]
    public void ADeclarationIsRecordedOnTheApprovedPlan_AndReadBackAsApprovedWork()
    {
        var plans = new PlanApprovalService();
        ApprovePlanFor(plans, Worker);

        var recorded = plans.DeclareDeviations(Worker, new[] { "kept the helper synchronous" });

        Assert.True(recorded.IsRecorded);
        var work = plans.ApprovedWorkFor(Worker)!;
        Assert.Equal(DeviationDeclaration.Declared, work.Declaration);
        Assert.Equal(new[] { "kept the helper synchronous" }, work.Deviations);
        Assert.Equal("keep plain a / b", work.Plan.Approach);
    }

    /// <summary>An empty declaration is the explicit "none" assertion, not the absence of one.</summary>
    [Fact]
    public void AnEmptyDeclarationRecordsTheAssertion_NotSilence()
    {
        var plans = new PlanApprovalService();
        ApprovePlanFor(plans, Worker);

        Assert.Equal(DeviationDeclaration.NotDeclared, plans.ApprovedWorkFor(Worker)!.Declaration);
        plans.DeclareDeviations(Worker, deviations: null);
        Assert.Equal(DeviationDeclaration.None, plans.ApprovedWorkFor(Worker)!.Declaration);
    }

    /// <summary>
    /// <b>A declaration cannot be walked back.</b> A worker commits several times; if any of those
    /// commits declared a departure then the branch has departed, whatever the last one says. The
    /// opposite rule would let a final <c>--no-deviations</c> erase the disclosure made three commits
    /// ago — which is the rubber stamp this mechanism must not become, and it would be reachable by
    /// accident rather than by malice.
    /// </summary>
    [Fact]
    public void ALaterNoneCannotClearAnEarlierDeclaredDeviation()
    {
        var plans = new PlanApprovalService();
        ApprovePlanFor(plans, Worker);

        plans.DeclareDeviations(Worker, new[] { "added validation the approach said not to add" });
        plans.DeclareDeviations(Worker, deviations: null);

        var work = plans.ApprovedWorkFor(Worker)!;
        Assert.Equal(DeviationDeclaration.Declared, work.Declaration);
        Assert.Equal(new[] { "added validation the approach said not to add" }, work.Deviations);
    }

    /// <summary>
    /// Later commits ADD to the record rather than replacing it, and the same text twice is one row.
    ///
    /// <para>The two declarations here name DIFFERENT departures on purpose. An earlier version of this
    /// test declared <c>[first]</c> then <c>[first, second]</c>, and a replace-not-accumulate
    /// implementation passes that identically — the second call already listed both. Mutation M6 caught
    /// it staying green. A worker that discloses one thing on commit 1 and a different thing on commit 3
    /// has disclosed two, and only this shape says so.</para>
    /// </summary>
    [Fact]
    public void DeviationsFromSuccessiveCommitsAccumulate_AndDoNotDuplicate()
    {
        var plans = new PlanApprovalService();
        ApprovePlanFor(plans, Worker);

        plans.DeclareDeviations(Worker, new[] { "first departure" });
        plans.DeclareDeviations(Worker, new[] { "second departure" });
        plans.DeclareDeviations(Worker, new[] { "second departure" }); // re-stated, not a third row

        Assert.Equal(
            new[] { "first departure", "second departure" },
            plans.ApprovedWorkFor(Worker)!.Deviations);
    }

    /// <summary>
    /// <b>The record is bounded, and what it drops it declares.</b> <c>commit_work</c> may be called any
    /// number of times and records on a clean tree too, so accumulation is an agent-controlled growth
    /// path through a file the daemon rewrites on every save — and this was the ONE agent-authored field
    /// with no oversized guard, while <c>TaskPlanSchema</c> bounds every sibling (<c>MaxScopeFiles</c>,
    /// <c>MaxFieldLength</c>, <c>MaxPlanBytes</c>).
    ///
    /// <para>Refusing the commit at the cap would be worse than the growth: a worker that hit it could
    /// never commit again, and its work dies with the jail. So the excess is recorded as a stated
    /// overflow the human reads, never as silence — and the notice is recomputed rather than
    /// accumulated, so a second overflowing round does not leave two of them.</para>
    /// </summary>
    [Fact]
    public void TheRecordIsCapped_AndSaysWhatItCouldNotHold()
    {
        var plans = new PlanApprovalService();
        ApprovePlanFor(plans, Worker);

        plans.DeclareDeviations(
            Worker,
            Enumerable.Range(0, PlanApprovalService.MaxDeclaredDeviations + 5)
                .Select(i => $"departure {i}").ToList());

        var first = plans.ApprovedWorkFor(Worker)!.Deviations;
        Assert.Equal(PlanApprovalService.MaxDeclaredDeviations + 1, first.Count); // the cap, plus the notice
        Assert.Equal("departure 0", first[0]);
        Assert.Contains("5 further declared deviation(s)", first[^1], StringComparison.Ordinal);

        // A second overflowing round re-states the overflow rather than stacking a second notice.
        plans.DeclareDeviations(Worker, new[] { "one more" });
        var second = plans.ApprovedWorkFor(Worker)!.Deviations;
        Assert.Equal(PlanApprovalService.MaxDeclaredDeviations + 1, second.Count);
        Assert.Single(second, d => d.Contains("further declared deviation(s)", StringComparison.Ordinal));
    }

    /// <summary>
    /// One over-long deviation is truncated with the cut MARKED, not refused. Refusing would block a
    /// commit over prose; an unmarked cut is the one way truncation is worse than either.
    /// </summary>
    [Fact]
    public void AnOverLongDeviationIsTruncated_AndSaysThatItWas()
    {
        var plans = new PlanApprovalService();
        ApprovePlanFor(plans, Worker);

        plans.DeclareDeviations(Worker, new[] { new string('x', TaskPlanSchema.MaxFieldLength + 500) });

        var recorded = Assert.Single(plans.ApprovedWorkFor(Worker)!.Deviations);
        Assert.EndsWith("…[truncated]", recorded, StringComparison.Ordinal);
        Assert.True(
            recorded.Length < TaskPlanSchema.MaxFieldLength + 50,
            $"an over-long deviation was stored at {recorded.Length} characters");
    }

    /// <summary>
    /// A worker with no approved plan has no approved approach, so there is nothing to deviate from and
    /// nowhere to record it. Refused with that reason rather than written somewhere — a declaration
    /// against no approval is a claim about nothing.
    /// </summary>
    [Fact]
    public void AWorkerWithNoApprovedPlan_HasNothingToDeclareAgainst()
    {
        var plans = new PlanApprovalService();

        var result = plans.DeclareDeviations(Worker, new[] { "anything" });

        Assert.False(result.IsRecorded);
        Assert.Equal(DeviationDeclarationOutcome.NoApprovedPlan, result.Outcome);
        Assert.Null(plans.ApprovedWorkFor(Worker));
    }

    /// <summary>
    /// The declaration survives a daemon restart, because it is persisted on the plan record. It has to:
    /// a worker declares at COMMIT time and the row is armed at VERIFICATION time, so a restart between
    /// the two would otherwise turn "the worker asserted it followed the approach" back into "nobody
    /// ever asked" — a must-acknowledge row for a question that was answered.
    /// </summary>
    [Fact]
    public void ADeclarationSurvivesADaemonRestart()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"mg-plans-{Guid.NewGuid():N}.json");
        try
        {
            var first = new PlanApprovalService(new JsonPlanApprovalStore(path));
            ApprovePlanFor(first, Worker);
            first.DeclareDeviations(Worker, new[] { "swapped the technique" });

            var afterRestart = new PlanApprovalService(new JsonPlanApprovalStore(path));

            var work = afterRestart.ApprovedWorkFor(Worker)!;
            Assert.Equal(DeviationDeclaration.Declared, work.Declaration);
            Assert.Equal(new[] { "swapped the technique" }, work.Deviations);
        }
        finally
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }

    /// <summary>
    /// A store file written before this field existed genuinely holds no answer, and rehydrates as
    /// <see cref="DeviationDeclaration.NotDeclared"/> — the fail-closed direction. Reading it as "None"
    /// would manufacture, for every plan already on disk, exactly the assertion this mechanism exists to
    /// stop anyone assuming.
    /// </summary>
    [Fact]
    public void APlanRecordFromBeforeThisFieldExisted_RehydratesAsNotDeclared()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"mg-plans-{Guid.NewGuid():N}.json");
        try
        {
            var first = new PlanApprovalService(new JsonPlanApprovalStore(path));
            ApprovePlanFor(first, Worker);
            first.DeclareDeviations(Worker, deviations: null);

            // Strip the field exactly as an older daemon's file would have lacked it.
            var stripped = System.Text.RegularExpressions.Regex.Replace(
                System.IO.File.ReadAllText(path), "\"Deviation\":\"[A-Za-z]*\",?", "");
            System.IO.File.WriteAllText(path, stripped);

            var afterRestart = new PlanApprovalService(new JsonPlanApprovalStore(path));
            Assert.Equal(
                DeviationDeclaration.NotDeclared, afterRestart.ApprovedWorkFor(Worker)!.Declaration);
        }
        finally
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }

    // ---- The shim: the worker's half of the CLI ----------------------------

    /// <summary>
    /// <b>The declaration reaches the wire, and the two forms stay distinguishable on it.</b> Driven
    /// through the shim's real <c>main()</c> under python3, for the reason §13.6 wrote down: a correct
    /// parser the dispatch does not route through is the shape of the defect, not the absence of one.
    ///
    /// <para>The two fields are sent only when actually given, so "answered none" and "said nothing"
    /// are different bytes — the daemon's refusal depends on being able to tell them apart, and an
    /// encoding that could not would make the refusal unimplementable.</para>
    /// </summary>
    [Fact]
    public void TheShimsCommit_SendsTheDeclarationItWasGiven_AndNothingItWasNot()
    {
        var none = RunShim("commit", "feat: work", "--no-deviations");
        if (none is null)
        {
            return; // no python3 on this box — nothing measured, nothing claimed
        }

        Assert.Null(none.Value.Refusal);
        using (var request = System.Text.Json.JsonDocument.Parse(none.Value.RequestJson!))
        {
            Assert.True(request.RootElement.GetProperty("noDeviations").GetBoolean());
            Assert.False(request.RootElement.TryGetProperty("deviations", out _));
        }

        var declared = RunShim(
            "commit", "feat: work", "--deviated", "added validation", "--deviated", "changed add()")!;
        Assert.Null(declared.Value.Refusal);
        using (var request = System.Text.Json.JsonDocument.Parse(declared.Value.RequestJson!))
        {
            Assert.Equal(
                new[] { "added validation", "changed add()" },
                request.RootElement.GetProperty("deviations")
                    .EnumerateArray().Select(e => e.GetString()).ToArray());
            Assert.False(request.RootElement.TryGetProperty("noDeviations", out _));
        }

        // Neither flag: sent as-is. The shim cannot know whether THIS worker owes a declaration (that
        // depends on holding an approved plan, which only the daemon knows), so refusing here would deny
        // an ungated worker a commit it is entitled to make. The daemon refuses it, with the form.
        var bare = RunShim("commit", "feat: work")!;
        Assert.Null(bare.Value.Refusal);
        using (var request = System.Text.Json.JsonDocument.Parse(bare.Value.RequestJson!))
        {
            Assert.False(request.RootElement.TryGetProperty("noDeviations", out _));
            Assert.False(request.RootElement.TryGetProperty("deviations", out _));
        }
    }

    /// <summary>
    /// The malformed forms are refused BEFORE any round trip, and nothing is sent — asserted as a null
    /// request, because "refused" and "sent something the daemon then refused" are different facts and
    /// only the second one commits anything.
    ///
    /// <para>Both flags at once is refused rather than resolved by precedence: they say opposite things
    /// about the same work, and a rule about which one wins would be invisible at the call site.</para>
    /// </summary>
    [Theory]
    [InlineData(new[] { "commit", "m", "--no-deviations", "--deviated", "x" }, "opposite things")]
    [InlineData(new[] { "commit", "m", "--deviated" }, "--deviated needs the deviation itself")]
    [InlineData(new[] { "commit", "m", "--no-deviation" }, "unknown option")]
    public void TheShimsCommit_RefusesAContradictoryOrIncompleteDeclaration_WithoutSendingAnything(
        string[] args, string expectedRefusal)
    {
        var run = RunShim(args);
        if (run is null)
        {
            return; // no python3 on this box
        }

        Assert.Null(run.Value.RequestJson);
        Assert.Contains(expectedRefusal, run.Value.Refusal!, StringComparison.Ordinal);
    }

    // ---- The instructions: what the worker is actually told -----------------

    /// <summary>
    /// A gated worker is taught the exact form the shim parses, single-sourced — two spellings of one
    /// command is how they come to disagree, and this one has flags a paraphrase would drop.
    /// </summary>
    [Fact]
    public void AGatedWorkerIsTaughtTheDeclarationFormTheShimActuallyParses()
    {
        var text = AgentOperatingInstructions.Worker(WorkerPlanMode.Gated);

        Assert.Contains(WorkerPlanShim.CommitUsage, text, StringComparison.Ordinal);
        Assert.Contains(WorkerPlanShim.CommitUsage, WorkerPlanShim.Script, StringComparison.Ordinal);
        Assert.Contains("--deviated", text, StringComparison.Ordinal);

        // ...and WHY, which is the half that changes behaviour: a worker told only the syntax has no
        // reason to think its own green tests are not already the answer to the question being asked.
        Assert.Contains("nothing compares anything against it", text, StringComparison.Ordinal);
        Assert.Contains("is an assertion, not a default", text, StringComparison.Ordinal);

        // The section is markdown a CLI reads, and it is spliced into a raw string literal — so its
        // heading has to START a line. An indented `### ` is a fenced code block in markdown, which would
        // render the whole section as literal text rather than as instructions. Cheap to get wrong
        // (change the closing delimiter's indentation and the splice silently gains eight spaces) and
        // invisible to every other assertion here, which only look for substrings.
        Assert.Contains("\n### Say whether you departed", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An UNGATED worker is told none of it. With plan mode off there is no approved approach, so the
    /// daemon neither requires nor records a declaration — and instructions that describe a mechanism
    /// the daemon is not applying to this jail are the MG-12 shape one layer up: a worker following them
    /// would send a flag whose only possible answer is "not recorded".
    /// </summary>
    [Fact]
    public void AnUngatedWorkerIsNotAskedToDeclareAnythingItCannotHaveDepartedFrom()
    {
        var text = AgentOperatingInstructions.Worker(WorkerPlanMode.Ungated);

        Assert.DoesNotContain("--no-deviations", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--deviated", text, StringComparison.Ordinal);

        // …but it is still told to commit, which is the unconditional half.
        Assert.Contains("commit \"<message>\"", text, StringComparison.Ordinal);
    }

    // ---- helpers ------------------------------------------------------------

    private static void ApprovePlanFor(PlanApprovalService plans, string workerAgentId)
    {
        var presented = plans.Present(
            workerAgentId,
            coordinatorId: "coord-1",
            title: "Add divide() to the calculator",
            new TaskPlanFields(
                new[] { "src/calc.js" },
                "keep plain a / b",
                "node test.js"),
            taskPrompt: "",
            budgetUsd: 1m);
        plans.Approve(presented.PlanId!, "uid:1000");
    }

    private static FilePatch Patch(string path, string addedLine) => new()
    {
        Header = $"diff --git a/{path} b/{path}\n",
        Hunks = new[]
        {
            new DiffHunk
            {
                OldStart = 1, OldCount = 1, NewStart = 1, NewCount = 1,
                Lines = new List<DiffLine> { new() { Kind = DiffLineKind.Add, Text = addedLine } },
            },
        },
    };

    /// <summary>Runs the shim's real <c>main()</c>; null when python3 is not installed on this box.</summary>
    private static (string? RequestJson, string? Refusal)? RunShim(params string[] args)
        => AgentIpcProtocolTests.RunPlanShimRequestForTests(args);
}
