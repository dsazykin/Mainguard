using System;
using System.Linq;
using LibGit2Sharp;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <c>IGitService.Commit(..., amend)</c>. The interface had no amend parameter at all, so the
/// staging panel's "Amend last commit" checkbox could only ever produce a brand-new commit.
/// These pin the amend contract, including the two edges: the root commit (no parent) and a
/// commit the branch has already published.
/// </summary>
public class GitServiceAmendTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _git = new();

    public void Dispose() => _fx.Dispose();

    private int CommitCount()
    {
        using var repo = new Repository(_fx.RepoPath);
        return repo.Head.Tip == null ? 0 : repo.Commits.Count();
    }

    private (string Sha, string Message) Head()
    {
        using var repo = new Repository(_fx.RepoPath);
        return (repo.Head.Tip!.Sha, repo.Head.Tip.Message.Trim());
    }

    [Fact]
    public void Commit_WithAmend_ShouldReplaceHeadInsteadOfAddingACommit()
    {
        _fx.CommitFile("a.txt", "1\n", "c1");
        _fx.CommitFile("b.txt", "2\n", "c2");
        var before = Head().Sha;

        _fx.WriteFile("c.txt", "forgotten\n");
        _git.StageFiles(_fx.RepoPath, new[] { "c.txt" });
        _git.Commit(_fx.RepoPath, "c2 (amended)", amend: true);

        Assert.Equal(2, CommitCount());                          // no third commit
        var (sha, message) = Head();
        Assert.NotEqual(before, sha);
        Assert.Equal("c2 (amended)", message);

        using var repo = new Repository(_fx.RepoPath);
        Assert.Contains("c.txt", repo.Head.Tip!.Tree.Select(e => e.Name));  // the amend carried the staged file
    }

    [Fact]
    public void Commit_WithoutAmend_ShouldStillAddACommit()
    {
        _fx.CommitFile("a.txt", "1\n", "c1");

        _fx.WriteFile("b.txt", "2\n");
        _git.StageFiles(_fx.RepoPath, new[] { "b.txt" });
        _git.Commit(_fx.RepoPath, "c2");

        Assert.Equal(2, CommitCount());
    }

    [Fact]
    public void Commit_WithAmend_ShouldRewriteTheRootCommit()
    {
        // Edge 1: the root commit has no parent. Amending it is allowed — it rewrites the root
        // in place and the repository is left with exactly one (parentless) commit.
        _fx.CommitFile("a.txt", "1\n", "root");

        _fx.WriteFile("b.txt", "also\n");
        _git.StageFiles(_fx.RepoPath, new[] { "b.txt" });
        _git.Commit(_fx.RepoPath, "root (amended)", amend: true);

        Assert.Equal(1, CommitCount());
        using var repo = new Repository(_fx.RepoPath);
        Assert.Empty(repo.Head.Tip!.Parents);
        Assert.Equal("root (amended)", repo.Head.Tip.Message.Trim());
    }

    [Fact]
    public void Commit_WithAmend_ShouldPreserveTheOriginalAuthor()
    {
        // `git commit --amend` keeps the original author (only the committer changes). The
        // signing path shells out to git and does exactly that, so the libgit2 path must match.
        _fx.CommitFile("a.txt", "1\n", "c1", "Original Author", "original@example.com",
            new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero));

        _fx.WriteFile("b.txt", "2\n");
        _git.StageFiles(_fx.RepoPath, new[] { "b.txt" });
        _git.Commit(_fx.RepoPath, "c1 (amended)", amend: true);

        using var repo = new Repository(_fx.RepoPath);
        Assert.Equal("Original Author", repo.Head.Tip!.Author.Name);
        Assert.Equal("original@example.com", repo.Head.Tip.Author.Email);
    }

    [Fact]
    public void Commit_WithAmend_ShouldRefuseACommitTheBranchHasAlreadyPushed()
    {
        // Edge 2: amending published history diverges the branch and the next push is rejected.
        // Refuse up front rather than let the UI create the divergence silently.
        _fx.CommitFile("a.txt", "1\n", "c1");
        _fx.AddBareRemote("origin");    // pushes HEAD, creating refs/remotes/origin/master
        string tip;
        using (var repo = new Repository(_fx.RepoPath))
        {
            tip = repo.Head.Tip!.Sha;
            Assert.NotNull(repo.Refs["refs/remotes/origin/master"]);
            repo.Branches.Update(repo.Head,
                b => b.Remote = "origin",
                b => b.UpstreamBranch = "refs/heads/master");
        }

        _fx.WriteFile("b.txt", "2\n");
        _git.StageFiles(_fx.RepoPath, new[] { "b.txt" });

        var ex = Assert.Throws<AmendPushedCommitException>(
            () => _git.Commit(_fx.RepoPath, "c1 (amended)", amend: true));

        Assert.Equal("master", ex.BranchName);
        Assert.Equal("origin/master", ex.UpstreamName);
        Assert.Equal(tip, Head().Sha);                           // nothing was rewritten
        Assert.Equal(1, CommitCount());
    }

    [Fact]
    public void Commit_WithAmend_ShouldRefuseOnAnUnbornBranch()
    {
        _fx.WriteFile("a.txt", "1\n");
        _git.StageFiles(_fx.RepoPath, new[] { "a.txt" });

        var ex = Assert.Throws<GitOperationException>(
            () => _git.Commit(_fx.RepoPath, "nothing to amend", amend: true));
        Assert.Contains("amend", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, CommitCount());
    }
}
