using System;
using System.IO;
using System.Threading;
using Mainguard.Agents.Services;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Lane H Part 5 — index.lock resilience (the client-side cousin of Hotspot Register H7's backoff
/// rule). A concurrent `git` process, IDE plugin or agent holding <c>.git/index.lock</c> must turn
/// into (a) a silent bounded retry when the lock clears quickly — the common case — and (b) a
/// typed, actionable <see cref="GitOperationException"/> when it doesn't, never a raw libgit2
/// message. Both paths leave the repository fully usable.
/// </summary>
public class GitServiceIndexLockTests
{
    private static string LockPath(string repoPath) => Path.Combine(repoPath, ".git", "index.lock");

    [Fact]
    public void StageFile_WhenIndexLockClearsDuringBackoff_SucceedsSilently()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "one\n", "seed");
        fx.WriteFile("a.txt", "two\n");

        // Simulate a concurrent tool: the lock exists when we start and clears ~60 ms in — after the
        // first attempt has certainly failed, and well inside the retry window (25+50+100 ms of
        // backoff across four attempts).
        //
        // The releaser is a DEDICATED THREAD, and staging does not begin until it is running. This
        // used to be `Task.Run(async () => { await Task.Delay(60); … })`, which made the release
        // depend on the thread pool: on a loaded CI runner — the whole suite plus several daemon
        // hosts racing for ports — a queued continuation can be starved past the retry's entire
        // 175 ms budget. All four attempts then still see the lock and this test lands on the
        // WEDGED-lock path it is not about, which is exactly how it failed in CI. A dedicated thread
        // is scheduled by the OS rather than by a saturated pool, so 60 ms means 60 ms.
        File.WriteAllText(LockPath(fx.RepoPath), "");

        using var releaserRunning = new ManualResetEventSlim(false);
        var releaser = new Thread(() =>
        {
            releaserRunning.Set();
            Thread.Sleep(60);
            File.Delete(LockPath(fx.RepoPath));
        })
        {
            IsBackground = true,
            Name = "index.lock releaser",
        };
        releaser.Start();
        releaserRunning.Wait();

        var git = new GitService();
        git.StageFile(fx.RepoPath, "a.txt"); // must not throw
        releaser.Join();

        var staged = git.GetRepositoryStatus(fx.RepoPath);
        Assert.Contains(staged, s => s.FilePath == "a.txt");
    }

    [Fact]
    public void StageFile_WhenIndexLockIsWedged_ThrowsTypedActionableError_AndRepoStaysUsable()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "one\n", "seed");
        fx.WriteFile("a.txt", "two\n");

        File.WriteAllText(LockPath(fx.RepoPath), ""); // never released — a crashed process

        var git = new GitService();
        var ex = Assert.Throws<GitOperationException>(() => git.StageFile(fx.RepoPath, "a.txt"));

        // The message must name the lock and the way out — this is what the user sees.
        Assert.Contains("index.lock", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Another process", ex.Message, StringComparison.Ordinal);

        // Nothing was corrupted: clearing the stale lock makes the same operation succeed.
        File.Delete(LockPath(fx.RepoPath));
        git.StageFile(fx.RepoPath, "a.txt");
        Assert.Contains(git.GetRepositoryStatus(fx.RepoPath), s => s.FilePath == "a.txt");
    }

    [Fact]
    public void ReadOnlyOperations_AreNotBlockedByAnIndexLock()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "one\n", "seed");

        File.WriteAllText(LockPath(fx.RepoPath), "");
        try
        {
            // History reads never take the index lock — a held lock must not degrade browsing.
            var git = new GitService();
            var commits = git.GetRecentCommits(fx.RepoPath, 0, 10, new Mainguard.Git.Models.CommitSearchFilter());
            Assert.NotEmpty(commits);
        }
        finally
        {
            File.Delete(LockPath(fx.RepoPath));
        }
    }
}
