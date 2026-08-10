using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// Merge/rebase state detection inside a <b>linked worktree</b>. In a linked worktree
/// <c>.git</c> is a FILE pointing at <c>&lt;main&gt;/.git/worktrees/&lt;name&gt;/</c>, and every
/// per-worktree state file (MERGE_HEAD, MERGE_MSG, rebase-merge/, rebase-apply/) lives there —
/// not under <c>&lt;worktree&gt;/.git/</c>, which is not a directory at all.
///
/// These tests build a real linked worktree (mocking one would not reproduce the defect: the
/// whole bug is that <c>Path.Combine(repoPath, ".git", …)</c> resolves to a file path) and
/// assert the service agrees with git about the state it is in.
/// </summary>
[Trait("Category", "RequiresGitCli")]
public class GitServiceWorktreeStateTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _git = new();
    private readonly InteractiveRebaseService _rebase = new();
    private readonly List<string> _worktreePaths = new();

    public void Dispose()
    {
        foreach (var path in _worktreePaths)
        {
            try { Run(_fx.RepoPath, "worktree", "remove", "--force", path); } catch { }
            try { ForceDelete(path); } catch { }
        }
        _fx.Dispose();
    }

    /// <summary>
    /// master: c1 → c2 (a.txt = "main"), feature: c1 → cF (a.txt = "feature"), with feature
    /// checked out in a linked worktree. Rebasing feature onto master there always conflicts.
    /// </summary>
    private string BuildWorktreeWithDivergedFeature()
    {
        _fx.CommitFile("a.txt", "base\n", "c1");
        _fx.CreateBranch("feature");
        _fx.CommitFile("a.txt", "main\n", "c2");

        var wt = Path.Combine(Path.GetTempPath(), "GitLoomWTState_" + Guid.NewGuid().ToString("N"));
        _worktreePaths.Add(wt);
        Run(_fx.RepoPath, "worktree", "add", wt, "feature");

        File.WriteAllText(Path.Combine(wt, "a.txt"), "feature\n");
        Run(wt, "add", "a.txt");
        Run(wt, "-c", "user.name=test-user", "-c", "user.email=test@gitloom.local", "commit", "-m", "cF");
        return wt;
    }

    [Fact]
    public void IsRebasing_ShouldBeTrue_ForConflictedRebaseInLinkedWorktree()
    {
        var wt = BuildWorktreeWithDivergedFeature();

        var (code, _, _) = RunRaw(wt, "rebase", "master");
        Assert.NotEqual(0, code);                                  // it conflicted, as designed
        Assert.True(GitReportsRebaseInProgress(wt), "fixture: git itself should report a rebase in progress");

        Assert.True(_git.IsRebasing(wt));
    }

    [Fact]
    public void GetRebaseProgress_ShouldResolve_InLinkedWorktree()
    {
        var wt = BuildWorktreeWithDivergedFeature();
        RunRaw(wt, "rebase", "master");

        Assert.NotNull(_rebase.GetRebaseProgress(wt));
    }

    [Fact]
    public void MergeState_ShouldResolve_InLinkedWorktree()
    {
        var wt = BuildWorktreeWithDivergedFeature();

        var (code, _, _) = RunRaw(wt, "merge", "master");
        Assert.NotEqual(0, code);                                  // conflicted merge

        Assert.True(_git.IsMergeInProgress(wt));
        Assert.Contains("master", _git.GetMergeMessage(wt), StringComparison.Ordinal);
    }

    [Fact]
    public void RebaseMsgQueueDir_ShouldBeCreatable_InLinkedWorktree()
    {
        var wt = BuildWorktreeWithDivergedFeature();

        var queueDir = GitService.RebaseMsgQueueDir(wt);

        // The measured symptom: mkdir <wt>/.git/gitloom-rebase-msg fails with "Not a directory"
        // because <wt>/.git is a file. Interactive rebase cannot even start from a worktree.
        Directory.CreateDirectory(queueDir);
        Assert.True(Directory.Exists(queueDir));

        // And it must land in the per-worktree gitdir, which is where ContinueRebase looks for it.
        var expectedGitDir = Path.Combine(_fx.RepoPath, ".git", "worktrees", Path.GetFileName(wt));
        Assert.StartsWith(
            Path.GetFullPath(expectedGitDir),
            Path.GetFullPath(queueDir),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainRepositoryStateDetection_ShouldStillWork()
    {
        // Regression guard: routing through the resolved gitdir must not change the main repo.
        _fx.CommitFile("a.txt", "base\n", "c1");
        Assert.False(_git.IsRebasing(_fx.RepoPath));
        Assert.False(_git.IsMergeInProgress(_fx.RepoPath));

        _fx.CreateConflict("a.txt", "ours\n", "theirs\n");
        RunRaw(_fx.RepoPath, "merge", "theirs");
        Assert.True(_git.IsMergeInProgress(_fx.RepoPath));

        Assert.StartsWith(
            Path.GetFullPath(Path.Combine(_fx.RepoPath, ".git")),
            Path.GetFullPath(GitService.RebaseMsgQueueDir(_fx.RepoPath)),
            StringComparison.Ordinal);
    }

    private static bool GitReportsRebaseInProgress(string cwd)
    {
        var (_, output, _) = RunRaw(cwd, "status");
        return output.Contains("rebase", StringComparison.OrdinalIgnoreCase);
    }

    private static void Run(string cwd, params string[] args)
    {
        var (code, _, err) = RunRaw(cwd, args);
        if (code != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {err}");
    }

    private static (int Code, string Output, string Error) RunRaw(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start git");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr);
    }

    private static void ForceDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, true);
    }
}
