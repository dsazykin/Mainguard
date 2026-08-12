using System;
using LibGit2Sharp;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <c>DeleteBranch(force)</c> semantics. The GUI used to be permanently equivalent to
/// <c>git branch -D</c>: <c>force</c> was accepted and never read, so deleting an unmerged
/// branch silently orphaned its commits with no warning. These assert the <c>git branch -d</c>
/// contract instead — refuse an unmerged branch unless the caller explicitly forces.
/// </summary>
public class GitServiceBranchDeleteTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _git = new();

    public void Dispose() => _fx.Dispose();

    /// <summary>master keeps c1; "feature" gets a commit of its own that master never sees.</summary>
    private void BuildUnmergedFeature()
    {
        _fx.CommitFile("a.txt", "1\n", "c1");
        _fx.CreateBranch("feature");
        _fx.Checkout("feature");
        _fx.CommitFile("b.txt", "feature work\n", "cF");
        _fx.Checkout("master");
    }

    [Fact]
    public void DeleteBranch_WithoutForce_ShouldRefuseUnmergedBranch()
    {
        BuildUnmergedFeature();

        var ex = Assert.Throws<BranchNotMergedException>(
            () => _git.DeleteBranch(_fx.RepoPath, "feature"));

        Assert.Equal("feature", ex.BranchName);
        Assert.Contains("feature", ex.Message, StringComparison.Ordinal);

        using var repo = new Repository(_fx.RepoPath);
        Assert.NotNull(repo.Branches["feature"]);   // the work is still reachable
    }

    [Fact]
    public void DeleteBranch_WithForce_ShouldDeleteUnmergedBranch()
    {
        BuildUnmergedFeature();

        _git.DeleteBranch(_fx.RepoPath, "feature", force: true);

        using var repo = new Repository(_fx.RepoPath);
        Assert.Null(repo.Branches["feature"]);
    }

    [Fact]
    public void DeleteBranch_WithoutForce_ShouldDeleteMergedBranch()
    {
        _fx.CommitFile("a.txt", "1\n", "c1");
        _fx.CreateBranch("merged");     // same tip as master — fully merged
        _fx.CommitFile("a.txt", "2\n", "c2");

        _git.DeleteBranch(_fx.RepoPath, "merged");

        using var repo = new Repository(_fx.RepoPath);
        Assert.Null(repo.Branches["merged"]);
    }

    [Fact]
    public void DeleteBranch_WithoutForce_ShouldAllowBranchContainedInItsUpstream()
    {
        // git branch -d also accepts a branch whose tip its upstream already has, even when
        // HEAD has never seen it — the commits are published, so nothing is orphaned.
        _fx.CommitFile("a.txt", "1\n", "c1");
        _fx.AddBareRemote("origin");    // real remote config, so upstream resolution works
        _fx.CreateBranch("published");
        _fx.Checkout("published");
        var tip = _fx.CommitFile("b.txt", "published work\n", "cP");
        _fx.Checkout("master");

        using (var repo = new Repository(_fx.RepoPath))
        {
            repo.Refs.Add("refs/remotes/origin/published", tip);
            var branch = repo.Branches["published"]!;
            repo.Branches.Update(branch,
                b => b.Remote = "origin",
                b => b.UpstreamBranch = "refs/heads/published");
        }

        _git.DeleteBranch(_fx.RepoPath, "published");

        using (var repo = new Repository(_fx.RepoPath))
            Assert.Null(repo.Branches["published"]);
    }

    [Fact]
    public void DeleteBranch_ShouldStillReportMissingBranch()
    {
        _fx.CommitFile("a.txt", "1\n", "c1");

        var ex = Assert.Throws<GitOperationException>(
            () => _git.DeleteBranch(_fx.RepoPath, "no-such-branch"));
        Assert.Contains("no-such-branch", ex.Message, StringComparison.Ordinal);
    }
}
