using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Merge/rebase state detection and change-watching inside a <b>linked worktree</b>. There,
/// <c>.git</c> is a FILE pointing at <c>&lt;main&gt;/.git/worktrees/&lt;name&gt;/</c>, and the
/// per-worktree state (HEAD, index, MERGE_HEAD, MERGE_MSG, rebase-merge/, rebase-apply/) lives
/// there — not under <c>&lt;worktree&gt;/.git/</c>, which is not a directory at all. Shared
/// state (refs/, objects/) stays in the common dir, <c>&lt;main&gt;/.git/</c>.
///
/// These build a real linked worktree, because mocking one cannot reproduce the defect: the
/// whole bug is that <c>Path.Combine(repoPath, ".git", …)</c> resolves against a file.
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

        var wt = NewWorktreePath();
        Run(_fx.RepoPath, "worktree", "add", wt, "feature");

        File.WriteAllText(Path.Combine(wt, "a.txt"), "feature\n");
        Run(wt, "add", "a.txt");
        Run(wt, "-c", "user.name=test-user", "-c", "user.email=test@mainguard.local", "commit", "-m", "cF");
        return wt;
    }

    private string NewWorktreePath()
    {
        var wt = Path.Combine(Path.GetTempPath(), "MainguardWTState_" + Guid.NewGuid().ToString("N"));
        _worktreePaths.Add(wt);
        return wt;
    }

    /// <summary>The per-worktree gitdir git itself uses: &lt;main&gt;/.git/worktrees/&lt;name&gt;.</summary>
    private string ExpectedGitDir(string worktreePath)
        => Path.Combine(_fx.RepoPath, ".git", "worktrees", Path.GetFileName(worktreePath));

    // ---- State detection ----------------------------------------------------------------

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

        // The measured symptom: mkdir <wt>/.git/mainguard-rebase-msg fails with "Not a directory"
        // because <wt>/.git is a file. Interactive rebase cannot even start from a worktree.
        Directory.CreateDirectory(queueDir);
        Assert.True(Directory.Exists(queueDir));

        // And it must land in the per-worktree gitdir, next to the rebase-merge state it pairs with.
        Assert.StartsWith(
            Path.GetFullPath(ExpectedGitDir(wt)),
            Path.GetFullPath(queueDir),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveGitDir_ShouldReturnPerWorktreeGitDir_AndCommonDirForShared()
    {
        var wt = BuildWorktreeWithDivergedFeature();

        Assert.Equal(
            Path.GetFullPath(ExpectedGitDir(wt)),
            Path.GetFullPath(GitService.ResolveGitDir(wt)));

        // refs/ and objects/ are NOT per-worktree — they live in the common dir.
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_fx.RepoPath, ".git")),
            Path.GetFullPath(GitService.ResolveCommonGitDir(GitService.ResolveGitDir(wt))));
    }

    // ---- Change watching ----------------------------------------------------------------

    [Fact]
    public async Task RepositoryWatcher_ShouldTrigger_OnPerWorktreeHeadChange()
    {
        // HEAD/index/MERGE_HEAD live in the per-worktree gitdir, entirely outside the worktree
        // directory — so a watcher that only ever looks at <worktree>/.git sees none of them.
        var wt = BuildWorktreeWithDivergedFeature();

        var tcs = new TaskCompletionSource<bool>();
        using var watcher = new RepositoryWatcher(wt, debounceMs: 100);
        watcher.RepositoryChanged += () => tcs.TrySetResult(true);

        var headPath = Path.Combine(GitService.ResolveGitDir(wt), "HEAD");
        await File.WriteAllTextAsync(headPath, "ref: refs/heads/feature\n");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        Assert.Same(tcs.Task, completed);
    }

    [Fact]
    public async Task RepositoryWatcher_ShouldTrigger_OnSharedRefsChange_FromAWorktree()
    {
        // A commit made in a worktree updates refs/heads/<branch> in the COMMON dir.
        var wt = BuildWorktreeWithDivergedFeature();

        var tcs = new TaskCompletionSource<bool>();
        using var watcher = new RepositoryWatcher(wt, debounceMs: 100);
        watcher.RepositoryChanged += () => tcs.TrySetResult(true);

        var refPath = Path.Combine(_fx.RepoPath, ".git", "refs", "heads", "watched-from-worktree");
        Directory.CreateDirectory(Path.GetDirectoryName(refPath)!);
        await File.WriteAllTextAsync(refPath, new string('0', 40) + "\n");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        Assert.Same(tcs.Task, completed);
    }

    // ---- Main-repository regression guard -------------------------------------------------

    [Fact]
    public void MainRepositoryStateDetection_ShouldStillWork()
    {
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
