using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Headless.XUnit;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The timeline's SHOW view-option toggles and the "Current Branch" highlight.
///
/// <para>The SHOW checkboxes were persisted and reloaded but read by <i>nothing</i> — a control
/// that reports a setting the timeline never applies. The two that survived now have to
/// demonstrably change what the timeline renders. "Current Branch" highlighted
/// <c>LaneIndex == 0</c>, a rendering artifact, rather than HEAD.</para>
/// </summary>
public class CommitTimelineViewOptionsTests : IDisposable
{
    private readonly List<SqliteConnection> _connections = new();

    public void Dispose()
    {
        foreach (var c in _connections) c.Dispose();
        GC.SuppressFinalize(this);
    }

    private IPinnedRefService InMemoryPins()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        _connections.Add(conn);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        using (var ctx = new AppDbContext(options)) ctx.Database.EnsureCreated();
        return new PinnedRefService(() => new AppDbContext(options));
    }

    private CommitTimelineViewModel NewVm(GitService git, string repoPath)
        => new(git, repoPath, null, null, InMemoryPins());

    // ---- SHOW → Tag Names ------------------------------------------------------------

    /// <summary>
    /// Turning "Tag Names" off has to actually remove the tag chips (and leave branch chips alone).
    /// Before the fix the checkbox moved a persisted bool and the chips never changed.
    /// </summary>
    [AvaloniaFact]
    public void TagNames_ShouldControlWhetherTagChipsAreRendered()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var head = fx.CommitFile("a.txt", "1\n", "c1");
        git.CreateTag(fx.RepoPath, "v1.0.0", head, null);

        var settings = Mainguard.App.Shell.App.Settings;
        var originalTagNames = settings.Current.TagNames;
        try
        {
            var vm = NewVm(git, fx.RepoPath);
            vm.TagNames = true;
            vm.LoadInitialCommits();

            var headRow = vm.Commits.Single(r => r.Commit.Sha == head);
            Assert.Contains(headRow.RefLabels, l => l.IsTag && l.RefName == "v1.0.0");
            var branchChipsWithTags = headRow.RefLabels.Count(l => !l.IsTag);

            vm.TagNames = false;

            headRow = vm.Commits.Single(r => r.Commit.Sha == head);
            Assert.DoesNotContain(headRow.RefLabels, l => l.IsTag);
            // Branch chips are untouched — the toggle is about tags only.
            Assert.Equal(branchChipsWithTags, headRow.RefLabels.Count(l => !l.IsTag));

            vm.TagNames = true;
            headRow = vm.Commits.Single(r => r.Commit.Sha == head);
            Assert.Contains(headRow.RefLabels, l => l.IsTag && l.RefName == "v1.0.0");
        }
        finally
        {
            settings.Update(p => p.TagNames = originalTagNames);
        }
    }

    // ---- SHOW → Commit Timestamp -----------------------------------------------------

    /// <summary>
    /// The date column binds two alternative TextBlocks to <c>CommitTimestamp</c> and its inverse,
    /// so the inverse has to track the toggle (and raise change notification) or the column would
    /// render both formats at once, or neither.
    /// </summary>
    [AvaloniaFact]
    public void CommitTimestamp_ShouldDriveItsInverse_AndNotify()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        fx.CommitFile("a.txt", "1\n", "c1");

        var settings = Mainguard.App.Shell.App.Settings;
        var original = settings.Current.CommitTimestamp;
        try
        {
            var vm = NewVm(git, fx.RepoPath);
            var notified = new List<string?>();
            vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

            vm.CommitTimestamp = true;
            Assert.False(vm.HideCommitTimestamp);

            notified.Clear();
            vm.CommitTimestamp = false;

            Assert.True(vm.HideCommitTimestamp);
            Assert.Contains(nameof(CommitTimelineViewModel.HideCommitTimestamp), notified);
        }
        finally
        {
            settings.Update(p => p.CommitTimestamp = original);
        }
    }

    // ---- HIGHLIGHT → Current Branch --------------------------------------------------

    private static CommitRowViewModel Row(string sha, string[] parents, params RefLabelViewModel[] labels)
    {
        var row = new CommitRowViewModel
        {
            Commit = new GitCommitItem { Sha = sha, ParentShas = parents.ToList() },
        };
        foreach (var l in labels) row.RefLabels.Add(l);
        return row;
    }

    private static RefLabelViewModel HeadChip(string name, string sha)
        => new() { RefName = name, DisplayName = name, Sha = sha, IsCurrentHead = true };

    private static RefLabelViewModel BranchChip(string name, string sha)
        => new() { RefName = name, DisplayName = name, Sha = sha };

    /// <summary>
    /// HEAD's tip and its ancestors are "the current branch"; a sibling branch's commits are not —
    /// no matter which lane the router drew them in.
    /// </summary>
    [Fact]
    public void ComputeCurrentBranchShas_ShouldFollowHead_NotLaneZero()
    {
        // base <- onCurrent (HEAD, feature) and base <- onOther (main)
        var rows = new[]
        {
            Row("onCurrent", new[] { "base" }, HeadChip("feature", "onCurrent")),
            Row("onOther", new[] { "base" }, BranchChip("main", "onOther")),
            Row("base", Array.Empty<string>()),
        };

        var onBranch = CommitTimelineViewModel.ComputeCurrentBranchShas(rows);

        Assert.Contains("onCurrent", onBranch);
        Assert.Contains("base", onBranch);
        Assert.DoesNotContain("onOther", onBranch);
    }

    [Fact]
    public void ComputeCurrentBranchShas_WhenHeadIsNotLoaded_ShouldHighlightNothing()
    {
        // Detached HEAD, or a filter that excludes HEAD's tip: no chip is marked IsCurrentHead.
        var rows = new[]
        {
            Row("a", new[] { "b" }, BranchChip("main", "a")),
            Row("b", Array.Empty<string>()),
        };

        Assert.Empty(CommitTimelineViewModel.ComputeCurrentBranchShas(rows));
    }

    [Fact]
    public void ComputeCurrentBranchShas_ShouldIgnoreTagChips()
    {
        // A tag chip must never be mistaken for HEAD's branch.
        var rows = new[]
        {
            Row("tagged", Array.Empty<string>(),
                new RefLabelViewModel { RefName = "v1", DisplayName = "v1", Sha = "tagged", IsTag = true, IsCurrentHead = true }),
        };

        Assert.Empty(CommitTimelineViewModel.ComputeCurrentBranchShas(rows));
    }

    [Fact]
    public void ComputeCurrentBranchShas_ShouldWalkBothParentsOfAMerge()
    {
        var rows = new[]
        {
            Row("merge", new[] { "left", "right" }, HeadChip("main", "merge")),
            Row("left", new[] { "base" }),
            Row("right", new[] { "base" }),
            Row("base", Array.Empty<string>()),
            Row("unrelated", Array.Empty<string>(), BranchChip("other", "unrelated")),
        };

        var onBranch = CommitTimelineViewModel.ComputeCurrentBranchShas(rows);

        Assert.Equal(new[] { "base", "left", "merge", "right" }, onBranch.OrderBy(s => s, StringComparer.Ordinal));
    }
}
