using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Models;
using Mainguard.Git.Review;
using Xunit;
using TaskPlan = Mainguard.Agents.Agents.TaskPlan;
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Tests;

/// <summary>
/// P2-11 flagged-change detection + gate composition: the flag-worthy category set, the F6
/// out-of-approved-scope dedicated item (test 9), and the RT-D2 changed-test-command gate half (test 10).
/// </summary>
public class FlaggedChangeDetectorTests
{
    private static FilePatch Patch(string path, params DiffHunk[] hunks) => new()
    {
        Header = $"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n",
        Hunks = hunks,
    };

    private static DiffHunk Hunk(string heading, params (DiffLineKind Kind, string Text)[] lines) => new()
    {
        OldStart = 1,
        OldCount = lines.Length,
        NewStart = 1,
        NewCount = lines.Length,
        SectionHeading = heading,
        Lines = lines.Select(l => new DiffLine { Kind = l.Kind, Text = l.Text }).ToList(),
    };

    private static readonly DiffHunk SourceEdit = Hunk("", (DiffLineKind.Add, "var x = 1;"));

    private static readonly DiffHunk ScriptsEdit = Hunk("",
        (DiffLineKind.Context, "  \"scripts\": {"),
        (DiffLineKind.Add, "    \"postinstall\": \"node evil.js\","),
        (DiffLineKind.Context, "  },"));

    [Fact]
    public void Detect_FlagsTheFourFlagWorthyCategories_NotBenign()
    {
        var diff = new List<FilePatch>
        {
            Patch("package.json", ScriptsEdit),                       // ExecutableConfig
            Patch(".github/workflows/ci.yml", SourceEdit),            // CiWorkflow
            Patch(".husky/pre-commit", SourceEdit),                   // GitHooks
            Patch("src/auth/Login.cs", SourceEdit),                  // SecuritySensitivePath
            Patch("src/Plain.cs", SourceEdit),                       // Source (benign)
            Patch("docs/notes.md", SourceEdit),                      // Docs (benign)
        };

        var flagged = FlaggedChangeDetector.Detect(diff);
        var categories = flagged.Select(f => f.Category).ToHashSet();

        Assert.Contains(RiskCategory.ExecutableConfig, categories);
        Assert.Contains(RiskCategory.CiWorkflow, categories);
        Assert.Contains(RiskCategory.GitHooks, categories);
        Assert.Contains(RiskCategory.SecuritySensitivePath, categories);
        Assert.DoesNotContain(RiskCategory.Source, categories);
        Assert.DoesNotContain(RiskCategory.Docs, categories);
        Assert.Equal(4, flagged.Count);
    }

    [Fact]
    public void OutOfScopeDiff_ShouldBeDedicatedFlaggedItem()
    {
        var plan = new TaskPlan("plan-1", "Fix a", new[] { "src/a/**" }, "approach", "tests", 5m, DateTimeOffset.UtcNow);
        var diff = new List<FilePatch>
        {
            Patch("src/a/InScope.cs", SourceEdit),   // inside scope
            Patch("src/b/x.cs", SourceEdit),         // OUTSIDE scope
        };

        var managed = FlaggedChangeDetector.DetectFlagged(diff, plan, managed: true);
        var scopeItems = managed.Where(i => i.Kind == FlaggedKind.OutOfApprovedScope).ToList();

        Assert.Single(scopeItems);
        Assert.Equal("src/b/x.cs", scopeItems[0].Path);

        // Plan-less manual run: scope comparison skipped entirely.
        var planless = FlaggedChangeDetector.DetectFlagged(diff, approvedPlan: null, managed: false);
        Assert.DoesNotContain(planless, i => i.Kind == FlaggedKind.OutOfApprovedScope);
    }

    [Fact]
    public void OutOfScope_GateFalseUntilAcked()
    {
        var plan = new TaskPlan("plan-1", "Fix a", new[] { "src/a/**" }, "approach", "tests", 5m, DateTimeOffset.UtcNow);
        var diff = new List<FilePatch> { Patch("src/b/x.cs", SourceEdit) };
        var items = FlaggedChangeDetector.DetectFlagged(diff, plan, managed: true);

        var gate = new FlaggedChangeGate();
        var store = gate.StoreFor("loom-1");
        store.SetFlagged(items);

        Assert.False(gate.Allows("loom-1", out var reason));
        Assert.Contains("acknowledgment", reason);

        foreach (var item in items)
        {
            store.Acknowledge(item.Id);
        }

        Assert.True(gate.Allows("loom-1", out _));
    }

    [Fact]
    public async Task ChangedTestCommand_ShouldBlockCanMergeUntilAcked()
    {
        // RT-D2 panel half: the ChangedTestCommandGate (owned by P2-10) is wired beside the flagged gate;
        // the cockpit acknowledges it per item. A verified branch cannot merge while the flag is unacked.
        var changedGate = new ChangedTestCommandGate();
        MergeQueue queue = null!;
        Func<string, CancellationToken, Task<VerificationRecord>> run = (id, ct) =>
            Task.FromResult(new VerificationRecord(id, queue.CurrentMainSha, true, "log", "npm test", "hash-branch", DateTimeOffset.UtcNow));
        queue = new MergeQueue("repo", "main1", new InMemoryMergeQueueStore(), new InMemoryVerificationStore(), run,
            requeue: (_, _) => Task.CompletedTask, gates: new IMergeGate[] { changedGate });

        await queue.RunVerificationAsync("loom-1", CancellationToken.None);
        Assert.True(queue.CanMerge("loom-1", out _)); // verified, no flag yet

        // A resolution showed the branch changed the test command vs main.
        var resolution = VerificationCommandResolver.Resolve(
            branchConfigContent: "exit 0",
            mainConfigContent: "npm test");
        Assert.True(resolution.ChangedVsMain);
        changedGate.SetFlagged("loom-1", resolution.ChangedVsMain);

        Assert.False(queue.CanMerge("loom-1", out var reason));
        Assert.Contains("test command changed", reason);

        changedGate.Acknowledge("loom-1", ChangedTestCommandGate.TestCommandItem, null);
        Assert.True(queue.CanMerge("loom-1", out _));
    }

    [Theory]
    [InlineData("src/a/x.cs", "src/a/**", true)]
    [InlineData("src/a/deep/y.cs", "src/a/**", true)]
    [InlineData("src/b/x.cs", "src/a/**", false)]
    [InlineData("src/a/x.cs", "src/a", true)]
    [InlineData("README.md", "*.md", true)]
    [InlineData("docs/README.md", "*.md", false)]
    public void ScopeMatcher_GlobRules(string path, string pattern, bool inScope)
    {
        Assert.Equal(inScope, ScopeMatcher.IsInScope(path, new[] { pattern }));
    }
}
