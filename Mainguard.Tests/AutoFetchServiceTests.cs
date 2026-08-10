using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using Mainguard.Agents.Services;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Xunit;
using Repository = LibGit2Sharp.Repository;

namespace Mainguard.Tests;

// TI-10: AutoFetchService. Cadence, enable/disable, skip-while-operating, no
// self-overlap, and error handling are asserted deterministically via a fake
// IGitService and the internal RunCycleAsync seam — no real waiting, no network.
public class AutoFetchServiceTests
{
    private static UserPreferences Prefs(int minutes) => new() { AutoFetchMinutes = minutes };

    // 7a — fetches, raises the event, records the timestamp.
    [Fact]
    public async Task RunCycle_ShouldFetch_RaiseEvent_AndRecordTimestamp()
    {
        var git = new FakeGit();
        var clock = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        using var svc = new AutoFetchService(git, () => Prefs(10)) { Clock = () => clock };

        var fetched = new List<string>();
        svc.Fetched += fetched.Add;
        svc.Watch("/repo/a");

        await svc.RunCycleAsync();

        Assert.Equal(1, git.FetchCount("/repo/a"));
        Assert.True(git.LastPrune); // auto-fetch always prunes
        Assert.Equal(new[] { "/repo/a" }, fetched);
        Assert.Equal(clock, svc.GetLastFetched("/repo/a"));
    }

    // 7b — disabled (AutoFetchMinutes == 0) -> no fetch at all.
    [Fact]
    public async Task RunCycle_ShouldNoOp_WhenDisabled()
    {
        var git = new FakeGit();
        using var svc = new AutoFetchService(git, () => Prefs(0));
        svc.Watch("/repo/a");

        await svc.RunCycleAsync();

        Assert.Equal(0, git.FetchCount("/repo/a"));
        Assert.Null(svc.GetLastFetched("/repo/a"));
    }

    // 7c — skip while a git operation (merge/rebase) is in progress.
    [Fact]
    public async Task RunCycle_ShouldSkip_WhileOperationInProgress()
    {
        var git = new FakeGit { Operation = CurrentOperation.Merge };
        using var svc = new AutoFetchService(git, () => Prefs(10));
        var fetched = new List<string>();
        svc.Fetched += fetched.Add;
        svc.Watch("/repo/a");

        await svc.RunCycleAsync();

        Assert.Equal(0, git.FetchCount("/repo/a"));
        Assert.Empty(fetched);
        Assert.Null(svc.GetLastFetched("/repo/a"));
    }

    // 7d — never overlaps itself per repo: a slow fetch + a second cycle -> one execution.
    [Fact]
    public async Task RunCycle_ShouldNotOverlapItself_PerRepo()
    {
        var gate = new ManualResetEventSlim(false);
        var git = new FakeGit { FetchGate = gate };
        using var svc = new AutoFetchService(git, () => Prefs(10));
        svc.Watch("/repo/a");

        var first = svc.RunCycleAsync();   // launches the (blocked) fetch, claims the in-flight guard
        // Spin until the fake fetch is actually inside the gate wait.
        for (int i = 0; i < 200 && git.InFetch == 0; i++) Thread.Sleep(5);
        Assert.Equal(1, git.InFetch);

        await svc.RunCycleAsync();         // second cycle: repo already in-flight -> skipped

        gate.Set();
        await first;

        Assert.Equal(1, git.FetchCount("/repo/a")); // exactly one execution
    }

    // 7e — network failures are counted (and surfaced) but never thrown; resets on success.
    [Fact]
    public async Task RunCycle_ShouldCountFailures_NeverThrow_AndResetOnSuccess()
    {
        var git = new FakeGit { FailNextFetches = 3 };
        using var svc = new AutoFetchService(git, () => Prefs(10));
        var failureEvents = new List<int>();
        svc.FetchFailed += (_, count) => failureEvents.Add(count);
        svc.Watch("/repo/a");

        await svc.RunCycleAsync();
        await svc.RunCycleAsync();
        await svc.RunCycleAsync();

        Assert.Equal(3, svc.GetFailureCount("/repo/a"));
        Assert.Equal(new[] { 1, 2, 3 }, failureEvents);
        Assert.Null(svc.GetLastFetched("/repo/a")); // never succeeded

        await svc.RunCycleAsync(); // 4th succeeds
        Assert.Equal(0, svc.GetFailureCount("/repo/a"));
        Assert.NotNull(svc.GetLastFetched("/repo/a"));
    }

    // Unwatch stops future cycles from touching a repo.
    [Fact]
    public async Task Unwatch_ShouldStopFetchingRepo()
    {
        var git = new FakeGit();
        using var svc = new AutoFetchService(git, () => Prefs(10));
        svc.Watch("/repo/a");
        svc.Watch("/repo/b");
        svc.Unwatch("/repo/a");

        await svc.RunCycleAsync();

        Assert.Equal(0, git.FetchCount("/repo/a"));
        Assert.Equal(1, git.FetchCount("/repo/b"));
    }

    // ---- Fake IGitService: only Fetch + GetCurrentOperation matter to AutoFetchService.
    private sealed class FakeGit : IGitService
    {
        private readonly Dictionary<string, int> _fetches = new();
        private readonly object _lock = new();

        public CurrentOperation Operation { get; set; } = CurrentOperation.None;
        public ManualResetEventSlim? FetchGate { get; set; }
        public int FailNextFetches { get; set; }
        public bool LastPrune { get; private set; }
        public int InFetch;

        public int FetchCount(string repo)
        {
            lock (_lock) return _fetches.TryGetValue(repo, out var c) ? c : 0;
        }

        public CurrentOperation GetCurrentOperation(string repoPath) => Operation;

        public void Fetch(string repoPath, bool prune = false)
        {
            Interlocked.Increment(ref InFetch);
            try
            {
                FetchGate?.Wait();
                lock (_lock)
                {
                    LastPrune = prune;
                    if (FailNextFetches > 0)
                    {
                        FailNextFetches--;
                        throw new InvalidOperationException("simulated network failure");
                    }
                    _fetches[repoPath] = (_fetches.TryGetValue(repoPath, out var c) ? c : 0) + 1;
                }
            }
            finally
            {
                Interlocked.Decrement(ref InFetch);
            }
        }

        // ---- Unused by AutoFetchService: not exercised in these tests.
        private static T Nope<T>() => throw new NotSupportedException();
        private static void Nope() => throw new NotSupportedException();

        public bool IsGitRepository(string path) => Nope<bool>();
        public List<GitFileStatus> GetRepositoryStatus(string path) => Nope<List<GitFileStatus>>();
        public void ExecuteWithRepo(string path, Action<Repository> action) => Nope();
        public T ExecuteWithRepo<T>(string path, Func<Repository, T> func) => Nope<T>();
        public void StageFile(string repoPath, string filePath) => Nope();
        public void UnstageFile(string repoPath, string filePath) => Nope();
        public void StageFiles(string repoPath, IEnumerable<string> filePaths) => Nope();
        public void UnstageFiles(string repoPath, IEnumerable<string> filePaths) => Nope();
        public void DiscardChanges(string repoPath, IEnumerable<string> filePaths) => Nope();
        public void StageHunk(string repoPath, string patch) => Nope();
        public void UnstageHunk(string repoPath, string patch) => Nope();
        public void DiscardHunk(string repoPath, string patch) => Nope();
        public string GetFileDiff(string repoPath, string filePath, bool isStaged) => Nope<string>();
        public string GetFileDiff(string repoPath, string filePath, bool isStaged, bool ignoreWhitespace) => Nope<string>();
        public void Commit(string repoPath, string message, bool amend = false) => Nope();
        public void Push(string repoPath) => Nope();
        public void Pull(string repoPath) => Nope();
        public void Pull(string repoPath, PullStrategy strategy) => Nope();
        public void UpdateProject(string repoPath) => Nope();
        public IReadOnlyList<GitRemoteItem> GetRemotes(string repoPath) => Nope<IReadOnlyList<GitRemoteItem>>();
        public void AddRemote(string repoPath, string name, string url) => Nope();
        public void RemoveRemote(string repoPath, string name) => Nope();
        public void RenameRemote(string repoPath, string oldName, string newName) => Nope();
        public void SetRemoteUrl(string repoPath, string name, string url) => Nope();
        public string GetDefaultRemoteName(string repoPath) => Nope<string>();
        public void Fetch(string repoPath, string remoteName, bool prune = false) => Nope();
        public void PushForceWithLease(string repoPath, string remoteName, string branchName) => Nope();
        public void PushTags(string repoPath, string remoteName) => Nope();
        public void PushSetUpstream(string repoPath, string remoteName, string branchName) => Nope();
        public void Rebase(string repoPath, string targetBranchName) => Nope();
        public void RebaseOntoCommit(string repoPath, string commitSha) => Nope();
        public void Merge(string repoPath, string sourceBranchName) => Nope();
        public bool IsMergeInProgress(string repoPath) => Nope<bool>();
        public string GetMergeMessage(string repoPath) => Nope<string>();
        public IReadOnlyList<ConflictedFile> GetConflicts(string repoPath) => Nope<IReadOnlyList<ConflictedFile>>();
        public (string BaseText, string OursText, string TheirsText) GetConflictBlobs(string repoPath, string path) => Nope<(string, string, string)>();
        public void ResolveConflict(string repoPath, string path, string mergedContent) => Nope();
        public bool HasUnresolvedConflicts(string repoPath) => Nope<bool>();
        public void ResolveFileWithSide(string repoPath, string path, ConflictSide side) => Nope();
        public void RemoveFileFromMerge(string repoPath, string path) => Nope();
        public void AbortMerge(string repoPath) => Nope();
        public bool IsRebasing(string repoPath) => Nope<bool>();
        public void ContinueRebase(string repoPath) => Nope();
        public void AbortRebase(string repoPath) => Nope();
        public (int? Ahead, int? Behind) GetAheadBehind(string repoPath) => Nope<(int?, int?)>();
        public IEnumerable<GitCommitItem> GetRecentCommits(string repoPath, int skip, int take, CommitSearchFilter? filter = null) => Nope<IEnumerable<GitCommitItem>>();
        public IEnumerable<GitBranchItem> GetBranches(string repoPath) => Nope<IEnumerable<GitBranchItem>>();
        public void CheckoutBranch(string repoPath, string branchName) => Nope();
        public void CreateBranch(string repoPath, string branchName, string baseBranchName, bool checkout) => Nope();
        public void RenameBranch(string repoPath, string oldName, string newName) => Nope();
        public void PushBranch(string repoPath, string branchName) => Nope();
        public void DeleteBranch(string repoPath, string branchName, bool force = false) => Nope();
        public void StashChanges(string repoPath, string message) => Nope();
        public bool HasUncommittedChanges(string repoPath) => Nope<bool>();
        public IEnumerable<GitTagItem> GetTags(string repoPath) => Nope<IEnumerable<GitTagItem>>();
        public void CreateTag(string repoPath, string name, string targetSha, string? message) => Nope();
        public void DeleteTag(string repoPath, string name) => Nope();
        public void PushTag(string repoPath, string remoteName, string name) => Nope();
        public void DeleteRemoteTag(string repoPath, string remoteName, string name) => Nope();
        public void CheckoutTag(string repoPath, string name) => Nope();
        public IEnumerable<GitStashItem> GetStashes(string repoPath) => Nope<IEnumerable<GitStashItem>>();
        public void StashPush(string repoPath, string message) => Nope();
        public void StashDrop(string repoPath, int stashIndex) => Nope();
        public void StashPop(string repoPath, int stashIndex) => Nope();
        public void StashApply(string repoPath, int stashIndex) => Nope();
        public IReadOnlyList<WorktreeItem> ListWorktrees(string repoPath) => Nope<IReadOnlyList<WorktreeItem>>();
        public void AddWorktree(string repoPath, string worktreePath, string branchName, bool createBranch) => Nope();
        public void RemoveWorktree(string repoPath, string worktreePath, bool force) => Nope();
        public void PruneWorktrees(string repoPath) => Nope();
        public System.Threading.Tasks.Task<string> CheckoutPullRequestWorktree(string repoPath, int prNumber, string remoteName, string worktreePath, System.Threading.CancellationToken ct) => Nope<System.Threading.Tasks.Task<string>>();
        public string CheckoutBranchWorktree(string repoPath, string branchOrRef, string worktreePath) => Nope<string>();
        public IReadOnlyList<SubmoduleItem> GetSubmodules(string repoPath) => Nope<IReadOnlyList<SubmoduleItem>>();
        public void UpdateSubmodules(string repoPath) => Nope();
        public void UpdateSubmoduleRemote(string repoPath, string path) => Nope();
        public void SyncSubmodules(string repoPath) => Nope();
        public string GetDiffAgainstCommit(string repoPath, string commitSha, string? filePath = null) => Nope<string>();
        public string GetBranchDiffAgainstWorkingTree(string repoPath, string branchName) => Nope<string>();
        public IEnumerable<string> GetCommitModifiedFiles(string repoPath, string commitSha) => Nope<IEnumerable<string>>();
        public IEnumerable<string> GetBranchesContainingCommit(string repoPath, string commitSha) => Nope<IEnumerable<string>>();
        public IReadOnlyDictionary<string, CommitSignatureInfo> GetSignatureStatuses(string repoPath, IReadOnlyList<string> shas) => Nope<IReadOnlyDictionary<string, CommitSignatureInfo>>();
        public IReadOnlyList<SigningKeyOption> ListSigningKeys(string gpgFormat) => Nope<IReadOnlyList<SigningKeyOption>>();
        public IEnumerable<string> GetAuthors(string repoPath) => Nope<IEnumerable<string>>();
        public IEnumerable<string> GetRepositoryPaths(string repoPath) => Nope<IEnumerable<string>>();
        public void CheckoutRevision(string repoPath, string commitSha) => Nope();
        public void ResetToCommit(string repoPath, string commitSha, ResetMode mode) => Nope();
        public void RevertCommit(string repoPath, string commitSha) => Nope();
        public void AmendCommitMessage(string repoPath, string commitSha, string newMessage) => Nope();
        public void CherryPick(string repoPath, string commitSha) => Nope();
        public GitHeadState GetHeadState(string repoPath) => Nope<GitHeadState>();
        public IReadOnlyList<ReflogItem> GetReflog(string repoPath, string refName = "HEAD", int take = 200) => Nope<IReadOnlyList<ReflogItem>>();
        public void CreateBranchAt(string repoPath, string branchName, string commitSha, bool checkout) => Nope();
        public IReadOnlyList<BlameLine> GetBlame(string repoPath, string path, string? startingSha = null) => Nope<IReadOnlyList<BlameLine>>();
        public void InvalidateBlameCache(string repoPath) => Nope();
        public IReadOnlyList<FileVersion> GetFileHistory(string repoPath, string path) => Nope<IReadOnlyList<FileVersion>>();
        public string GetFileAtCommit(string repoPath, string sha, string path) => Nope<string>();
        public byte[] GetBlobBytesAtCommit(string repoPath, string sha, string path) => Nope<byte[]>();
        public string GetFileDiffBetweenCommits(string repoPath, string olderSha, string newerSha, string path) => Nope<string>();
    }
}
