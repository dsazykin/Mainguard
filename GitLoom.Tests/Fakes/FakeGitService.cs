using System;
using System.Collections.Generic;
using System.Linq;
using GitLoom.Core.Models;
using GitLoom.Core.Services;
using LibGit2Sharp;
using Repository = LibGit2Sharp.Repository;

namespace GitLoom.Tests.Fakes;

/// <summary>
/// Hand-rolled configurable <see cref="IGitService"/> fake (TI-00 pattern — no mocking library,
/// keeping with the zero-container philosophy). Members used by a test are backed by settable
/// delegates; everything else throws <see cref="NotSupportedException"/> so an unstubbed call is
/// a loud test error rather than a silent default.
/// </summary>
public sealed class FakeGitService : IGitService
{
    /// <summary>Stub for <see cref="GetBlame"/>. Args: (repoPath, path, startingSha).</summary>
    public Func<string, string, string?, IReadOnlyList<BlameLine>>? GetBlameImpl { get; set; }

    public Action<string>? InvalidateBlameCacheImpl { get; set; }

    public IReadOnlyList<BlameLine> GetBlame(string repoPath, string path, string? startingSha = null)
        => (GetBlameImpl ?? throw new NotSupportedException("GetBlameImpl not set"))(repoPath, path, startingSha);

    public void InvalidateBlameCache(string repoPath) => InvalidateBlameCacheImpl?.Invoke(repoPath);

    // File history (T-12). Args mirror the interface; unstubbed calls throw.
    public Func<string, string, IReadOnlyList<FileVersion>>? GetFileHistoryImpl { get; set; }
    public Func<string, string, string, string>? GetFileAtCommitImpl { get; set; }
    public Func<string, string, string, string, string>? GetFileDiffBetweenCommitsImpl { get; set; }

    public IReadOnlyList<FileVersion> GetFileHistory(string repoPath, string path)
        => (GetFileHistoryImpl ?? throw new NotSupportedException("GetFileHistoryImpl not set"))(repoPath, path);

    public string GetFileAtCommit(string repoPath, string sha, string path)
        => (GetFileAtCommitImpl ?? throw new NotSupportedException("GetFileAtCommitImpl not set"))(repoPath, sha, path);

    /// <summary>Stub for the whitespace-aware diff overload (T-13). Args: (repoPath, path, isStaged, ignoreWhitespace).</summary>
    public Func<string, string, bool, bool, string>? GetFileDiffWhitespaceImpl { get; set; }
    public string GetFileDiff(string repoPath, string filePath, bool isStaged, bool ignoreWhitespace)
        => (GetFileDiffWhitespaceImpl ?? throw new NotSupportedException("GetFileDiffWhitespaceImpl not set"))(repoPath, filePath, isStaged, ignoreWhitespace);

    /// <summary>Stub for raw blob bytes (T-13 image diff). Args: (repoPath, sha, path).</summary>
    public Func<string, string, string, byte[]>? GetBlobBytesAtCommitImpl { get; set; }
    public byte[] GetBlobBytesAtCommit(string repoPath, string sha, string path)
        => (GetBlobBytesAtCommitImpl ?? throw new NotSupportedException("GetBlobBytesAtCommitImpl not set"))(repoPath, sha, path);

    public string GetFileDiffBetweenCommits(string repoPath, string olderSha, string newerSha, string path)
        => (GetFileDiffBetweenCommitsImpl ?? throw new NotSupportedException("GetFileDiffBetweenCommitsImpl not set"))(repoPath, olderSha, newerSha, path);

    // ---- Everything else: not stubbed unless a test needs it.
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
    public void Commit(string repoPath, string message) => Nope();
    public void Push(string repoPath) => Nope();
    public void Pull(string repoPath) => Nope();
    public void Pull(string repoPath, PullStrategy strategy) => Nope();
    public void Fetch(string repoPath, bool prune = false) => Nope();
    public void UpdateProject(string repoPath) => Nope();
    /// <summary>Stub for <see cref="GetRemotes"/> (T-23 host resolution). Unstubbed → throws.</summary>
    public Func<string, IReadOnlyList<GitRemoteItem>>? GetRemotesImpl { get; set; }
    public IReadOnlyList<GitRemoteItem> GetRemotes(string repoPath)
        => (GetRemotesImpl ?? throw new NotSupportedException("GetRemotesImpl not set"))(repoPath);
    public void AddRemote(string repoPath, string name, string url) => Nope();
    public void RemoveRemote(string repoPath, string name) => Nope();
    public void RenameRemote(string repoPath, string oldName, string newName) => Nope();
    public void SetRemoteUrl(string repoPath, string name, string url) => Nope();
    /// <summary>Stub for <see cref="GetDefaultRemoteName"/> (T-29 checkout path). Defaults to "origin" when unset.</summary>
    public Func<string, string>? GetDefaultRemoteNameImpl { get; set; }
    public string GetDefaultRemoteName(string repoPath) => (GetDefaultRemoteNameImpl ?? (_ => "origin"))(repoPath);
    public void Fetch(string repoPath, string remoteName, bool prune = false) => Nope();
    public void PushForceWithLease(string repoPath, string remoteName, string branchName) => Nope();
    public void PushTags(string repoPath, string remoteName) => Nope();
    public void PushSetUpstream(string repoPath, string remoteName, string branchName) => Nope();
    /// <summary>Stub for <see cref="Rebase"/> (branch-menu confirmation tests). Unstubbed → throws.</summary>
    public Action<string, string>? RebaseImpl { get; set; }
    public void Rebase(string repoPath, string targetBranchName)
        => (RebaseImpl ?? ((_, _) => Nope()))(repoPath, targetBranchName);
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
    public CurrentOperation GetCurrentOperation(string repoPath) => Nope<CurrentOperation>();
    public void AbortMerge(string repoPath) => Nope();
    public bool IsRebasing(string repoPath) => Nope<bool>();
    public void ContinueRebase(string repoPath) => Nope();
    public void AbortRebase(string repoPath) => Nope();
    public (int? Ahead, int? Behind) GetAheadBehind(string repoPath) => Nope<(int?, int?)>();
    /// <summary>Stub for <see cref="GetRecentCommits"/> (T-23 create-form title prefill). Unstubbed → throws.</summary>
    public Func<string, int, int, IEnumerable<GitCommitItem>>? GetRecentCommitsImpl { get; set; }
    public IEnumerable<GitCommitItem> GetRecentCommits(string repoPath, int skip, int take, CommitSearchFilter? filter = null)
        => (GetRecentCommitsImpl ?? throw new NotSupportedException("GetRecentCommitsImpl not set"))(repoPath, skip, take);
    /// <summary>Stub for <see cref="GetBranches"/> (T-21 worktree VM). Unstubbed → throws.</summary>
    public Func<string, IEnumerable<GitBranchItem>>? GetBranchesImpl { get; set; }
    public IEnumerable<GitBranchItem> GetBranches(string repoPath)
        => (GetBranchesImpl ?? throw new NotSupportedException("GetBranchesImpl not set"))(repoPath);
    public void CheckoutBranch(string repoPath, string branchName) => Nope();
    public void CreateBranch(string repoPath, string branchName, string baseBranchName, bool checkout) => Nope();
    public void RenameBranch(string repoPath, string oldName, string newName) => Nope();
    public void PushBranch(string repoPath, string branchName) => Nope();
    public void DeleteBranch(string repoPath, string branchName, bool force = false) => Nope();
    public void StashChanges(string repoPath, string message) => Nope();
    public bool HasUncommittedChanges(string repoPath) => Nope<bool>();
    /// <summary>Stub for <see cref="GetTags"/>. Unstubbed → throws.</summary>
    public Func<string, IEnumerable<GitTagItem>>? GetTagsImpl { get; set; }
    public IEnumerable<GitTagItem> GetTags(string repoPath)
        => (GetTagsImpl ?? throw new NotSupportedException("GetTagsImpl not set"))(repoPath);
    public void CreateTag(string repoPath, string name, string targetSha, string? message) => Nope();
    /// <summary>Stub for <see cref="DeleteTag"/> (branch-menu confirmation tests). Unstubbed → throws.</summary>
    public Action<string, string>? DeleteTagImpl { get; set; }
    public void DeleteTag(string repoPath, string name)
        => (DeleteTagImpl ?? ((_, _) => Nope()))(repoPath, name);
    public void PushTag(string repoPath, string remoteName, string name) => Nope();
    /// <summary>Stub for <see cref="DeleteRemoteTag"/> (branch-menu confirmation tests). Unstubbed → throws.</summary>
    public Action<string, string, string>? DeleteRemoteTagImpl { get; set; }
    public void DeleteRemoteTag(string repoPath, string remoteName, string name)
        => (DeleteRemoteTagImpl ?? ((_, _, _) => Nope()))(repoPath, remoteName, name);
    public void CheckoutTag(string repoPath, string name) => Nope();
    public IEnumerable<GitStashItem> GetStashes(string repoPath) => Nope<IEnumerable<GitStashItem>>();
    public void StashPush(string repoPath, string message) => Nope();
    public void StashDrop(string repoPath, int stashIndex) => Nope();
    public void StashPop(string repoPath, int stashIndex) => Nope();
    public void StashApply(string repoPath, int stashIndex) => Nope();
    /// <summary>Stub for <see cref="ListWorktrees"/> (T-21 worktree VM). Unstubbed → throws.</summary>
    public Func<string, IReadOnlyList<WorktreeItem>>? ListWorktreesImpl { get; set; }
    public IReadOnlyList<WorktreeItem> ListWorktrees(string repoPath)
        => (ListWorktreesImpl ?? throw new NotSupportedException("ListWorktreesImpl not set"))(repoPath);
    /// <summary>Records the last <see cref="AddWorktree"/> call so the VM's create path can be asserted.</summary>
    public Action<string, string, string, bool>? AddWorktreeImpl { get; set; }
    public void AddWorktree(string repoPath, string worktreePath, string branchName, bool createBranch)
        => (AddWorktreeImpl ?? throw new NotSupportedException("AddWorktreeImpl not set"))(repoPath, worktreePath, branchName, createBranch);
    public Action<string, string, bool>? RemoveWorktreeImpl { get; set; }
    public void RemoveWorktree(string repoPath, string worktreePath, bool force)
        => (RemoveWorktreeImpl ?? throw new NotSupportedException("RemoveWorktreeImpl not set"))(repoPath, worktreePath, force);
    public Action<string>? PruneWorktreesImpl { get; set; }
    public void PruneWorktrees(string repoPath)
        => (PruneWorktreesImpl ?? throw new NotSupportedException("PruneWorktreesImpl not set"))(repoPath);

    /// <summary>Stub for <see cref="CheckoutPullRequestWorktree"/> (T-29 VM). Args: (repoPath, prNumber, remoteName, worktreePath). Returns the created path.</summary>
    public Func<string, int, string, string, string>? CheckoutPullRequestWorktreeImpl { get; set; }
    public System.Threading.Tasks.Task<string> CheckoutPullRequestWorktree(
        string repoPath, int prNumber, string remoteName, string worktreePath, System.Threading.CancellationToken ct)
        => System.Threading.Tasks.Task.FromResult(
            (CheckoutPullRequestWorktreeImpl ?? throw new NotSupportedException("CheckoutPullRequestWorktreeImpl not set"))
                (repoPath, prNumber, remoteName, worktreePath));

    /// <summary>Stub for <see cref="CheckoutBranchWorktree"/> (T-29). Args: (repoPath, branchOrRef, worktreePath). Returns the created path.</summary>
    public Func<string, string, string, string>? CheckoutBranchWorktreeImpl { get; set; }
    public string CheckoutBranchWorktree(string repoPath, string branchOrRef, string worktreePath)
        => (CheckoutBranchWorktreeImpl ?? throw new NotSupportedException("CheckoutBranchWorktreeImpl not set"))
            (repoPath, branchOrRef, worktreePath);

    // Submodules (T-16). Stubbable so VM/render tests can supply a canned list without a repo.
    public Func<string, IReadOnlyList<SubmoduleItem>>? GetSubmodulesImpl { get; set; }
    public Action<string>? UpdateSubmodulesImpl { get; set; }
    public Action<string, string>? UpdateSubmoduleRemoteImpl { get; set; }
    public Action<string>? SyncSubmodulesImpl { get; set; }
    public IReadOnlyList<SubmoduleItem> GetSubmodules(string repoPath)
        => (GetSubmodulesImpl ?? throw new NotSupportedException("GetSubmodulesImpl not set"))(repoPath);
    public void UpdateSubmodules(string repoPath) => (UpdateSubmodulesImpl ?? (_ => Nope()))(repoPath);
    public void UpdateSubmoduleRemote(string repoPath, string path) => (UpdateSubmoduleRemoteImpl ?? ((_, _) => Nope()))(repoPath, path);
    public void SyncSubmodules(string repoPath) => (SyncSubmodulesImpl ?? (_ => Nope()))(repoPath);
    public string GetDiffAgainstCommit(string repoPath, string commitSha, string? filePath = null) => Nope<string>();
    public string GetBranchDiffAgainstWorkingTree(string repoPath, string branchName) => Nope<string>();
    public IEnumerable<string> GetCommitModifiedFiles(string repoPath, string commitSha) => Nope<IEnumerable<string>>();
    public IEnumerable<string> GetBranchesContainingCommit(string repoPath, string commitSha) => Nope<IEnumerable<string>>();

    // Signing (T-15). Overridable so VM tests can drive badge state; defaults to "all unsigned"
    // and "no keys" so a plain timeline VM test never has to configure them.
    public Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, CommitSignatureInfo>>? GetSignatureStatusesImpl;
    public IReadOnlyDictionary<string, CommitSignatureInfo> GetSignatureStatuses(string repoPath, IReadOnlyList<string> shas)
        => GetSignatureStatusesImpl?.Invoke(repoPath, shas)
           ?? shas.ToDictionary(s => s, _ => CommitSignatureInfo.None);
    public IReadOnlyList<SigningKeyOption> ListSigningKeys(string gpgFormat) => System.Array.Empty<SigningKeyOption>();

    public IEnumerable<string> GetAuthors(string repoPath) => Nope<IEnumerable<string>>();
    public IEnumerable<string> GetRepositoryPaths(string repoPath) => Nope<IEnumerable<string>>();
    public void CheckoutRevision(string repoPath, string commitSha) => Nope();
    /// <summary>Records the last ResetToCommit call (T-20 restore + T-09) so a VM test can assert it fired.</summary>
    public (string RepoPath, string CommitSha, ResetMode Mode)? LastResetToCommit { get; private set; }
    public Action<string, string, ResetMode>? ResetToCommitImpl { get; set; }
    public void ResetToCommit(string repoPath, string commitSha, ResetMode mode)
    {
        LastResetToCommit = (repoPath, commitSha, mode);
        ResetToCommitImpl?.Invoke(repoPath, commitSha, mode);
    }
    public void RevertCommit(string repoPath, string commitSha) => Nope();
    public void AmendCommitMessage(string repoPath, string commitSha, string newMessage) => Nope();
    public void CherryPick(string repoPath, string commitSha) => Nope();
    /// <summary>Stub for <see cref="GetHeadState"/> (T-23 create-form gating). Unstubbed → throws.</summary>
    public Func<string, GitHeadState>? GetHeadStateImpl { get; set; }
    public GitHeadState GetHeadState(string repoPath)
        => (GetHeadStateImpl ?? throw new NotSupportedException("GetHeadStateImpl not set"))(repoPath);

    /// <summary>Stub for <see cref="GetReflog"/> (T-20). Args: (repoPath, refName, take).</summary>
    public Func<string, string, int, IReadOnlyList<ReflogItem>>? GetReflogImpl { get; set; }
    public IReadOnlyList<ReflogItem> GetReflog(string repoPath, string refName = "HEAD", int take = 200)
        => (GetReflogImpl ?? throw new NotSupportedException("GetReflogImpl not set"))(repoPath, refName, take);

    /// <summary>Records the last CreateBranchAt call (T-20 recovery + T-09) so a VM test can assert it fired.</summary>
    public (string RepoPath, string BranchName, string CommitSha, bool Checkout)? LastCreateBranchAt { get; private set; }
    public Action<string, string, string, bool>? CreateBranchAtImpl { get; set; }
    public void CreateBranchAt(string repoPath, string branchName, string commitSha, bool checkout)
    {
        LastCreateBranchAt = (repoPath, branchName, commitSha, checkout);
        CreateBranchAtImpl?.Invoke(repoPath, branchName, commitSha, checkout);
    }
}
