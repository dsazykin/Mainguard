using System;
using System.Collections.Generic;
using GitLoom.Core.Models;
using LibGit2Sharp;
using Repository = LibGit2Sharp.Repository;

namespace GitLoom.Core.Services;

public interface IGitService
{
    /// <summary>
    /// Checks if the specified directory is a valid Git repository.
    /// </summary>
    bool IsGitRepository(string path);

    /// <summary>
    /// Retrieves the current working directory and index status of the repository.
    /// </summary>
    List<GitFileStatus> GetRepositoryStatus(string path);

    /// <summary>
    /// Executes a Git command using LibGit2Sharp, managing the native handle lifecycle strictly.
    /// </summary>
    void ExecuteWithRepo(string path, Action<Repository> action);

    /// <summary>
    /// Executes a Git command using LibGit2Sharp and returns a result, managing the native handle lifecycle strictly.
    /// </summary>
    T ExecuteWithRepo<T>(string path, Func<Repository, T> func);

    void StageFile(string repoPath, string filePath);
    void UnstageFile(string repoPath, string filePath);

    void StageFiles(string repoPath, IEnumerable<string> filePaths);
    void UnstageFiles(string repoPath, IEnumerable<string> filePaths);
    void DiscardChanges(string repoPath, IEnumerable<string> filePaths);

    /// <summary>Stages a subset of changes described by a unified-diff patch (git apply --cached).</summary>
    void StageHunk(string repoPath, string patch);
    /// <summary>Unstages a subset of changes described by a unified-diff patch (git apply --cached --reverse).</summary>
    void UnstageHunk(string repoPath, string patch);
    /// <summary>Discards a subset of working-tree changes described by a unified-diff patch (git apply --reverse).</summary>
    void DiscardHunk(string repoPath, string patch);

    string GetFileDiff(string repoPath, string filePath, bool isStaged);

    /// <summary>
    /// Whitespace-aware diff of <paramref name="filePath"/> (T-13). When
    /// <paramref name="ignoreWhitespace"/> is true it shells out to <c>git diff -w</c>
    /// (staged variant adds <c>--cached</c>) so whitespace-only changes collapse to zero hunks;
    /// when false it is identical to the <see cref="GetFileDiff(string,string,bool)"/> overload.
    /// Partial staging is disabled by the caller in ignore-whitespace mode (offsets differ).
    /// </summary>
    string GetFileDiff(string repoPath, string filePath, bool isStaged, bool ignoreWhitespace);

    /// <summary>
    /// Commits the staging index. With <paramref name="amend"/> the commit <i>replaces</i> HEAD
    /// (<c>git commit --amend</c>) instead of being added on top, keeping the original author.
    /// <para>Two edges are refused rather than surprising the user: an unborn branch (nothing to
    /// amend) throws <see cref="Exceptions.GitOperationException"/>, and a HEAD its branch has
    /// already published throws <see cref="Exceptions.AmendPushedCommitException"/> — rewriting it
    /// would diverge the branch. Amending the root commit is allowed.</para>
    /// </summary>
    void Commit(string repoPath, string message, bool amend = false);

    void Push(string repoPath);
    void Pull(string repoPath);
    void Pull(string repoPath, PullStrategy strategy);
    void Fetch(string repoPath, bool prune = false);
    void UpdateProject(string repoPath);

    // Remotes management (T-10). CRUD via LibGit2Sharp (repo.Network.Remotes);
    // names are validated and duplicate/missing throw typed BEFORE any mutation so
    // the repo config is never left half-edited.
    IReadOnlyList<GitRemoteItem> GetRemotes(string repoPath);
    void AddRemote(string repoPath, string name, string url);
    void RemoveRemote(string repoPath, string name);
    void RenameRemote(string repoPath, string oldName, string newName);
    void SetRemoteUrl(string repoPath, string name, string url);

    /// <summary>Resolves the remote an unqualified push/pull/fetch targets (tracked branch's
    /// remote, else origin, else the sole remote) or throws <c>RemoteNotFoundException</c>.</summary>
    string GetDefaultRemoteName(string repoPath);

    /// <summary>Fetches an explicit remote (T-10). The parameterless <see cref="Fetch(string,bool)"/>
    /// resolves the remote from the tracked branch (else origin, else the sole remote).</summary>
    void Fetch(string repoPath, string remoteName, bool prune = false);

    // Push options (T-10) — CLI-driven; libgit2 has no --force-with-lease support.
    // Never a plain --force: --force-with-lease refuses to clobber remote work the
    // local remote-tracking ref hasn't seen (the safety property).
    void PushForceWithLease(string repoPath, string remoteName, string branchName);
    void PushTags(string repoPath, string remoteName);
    void PushSetUpstream(string repoPath, string remoteName, string branchName);

    void Rebase(string repoPath, string targetBranchName);

    /// <summary>Rebases HEAD onto an arbitrary commit (not necessarily a branch tip) — the T-09
    /// drag-a-commit-onto-a-branch-label "Rebase &lt;branch&gt; onto here" gesture (#87).</summary>
    void RebaseOntoCommit(string repoPath, string commitSha);
    void Merge(string repoPath, string sourceBranchName);
    bool IsMergeInProgress(string repoPath);
    string GetMergeMessage(string repoPath);

    // Conflict resolution — merge index stages (repo.Index.Conflicts), never working-tree markers.
    IReadOnlyList<ConflictedFile> GetConflicts(string repoPath);
    (string BaseText, string OursText, string TheirsText) GetConflictBlobs(string repoPath, string path);
    void ResolveConflict(string repoPath, string path, string mergedContent);
    bool HasUnresolvedConflicts(string repoPath);
    void ResolveFileWithSide(string repoPath, string path, ConflictSide side);
    void RemoveFileFromMerge(string repoPath, string path);
    CurrentOperation GetCurrentOperation(string repoPath);
    void AbortMerge(string repoPath);

    bool IsRebasing(string repoPath);
    void ContinueRebase(string repoPath);
    void AbortRebase(string repoPath);

    (int? Ahead, int? Behind) GetAheadBehind(string repoPath);

    IEnumerable<GitCommitItem> GetRecentCommits(string repoPath, int skip, int take, CommitSearchFilter? filter = null);

    IEnumerable<GitBranchItem> GetBranches(string repoPath);
    void CheckoutBranch(string repoPath, string branchName);
    void CreateBranch(string repoPath, string branchName, string baseBranchName, bool checkout);
    void RenameBranch(string repoPath, string oldName, string newName);
    void PushBranch(string repoPath, string branchName);
    void DeleteBranch(string repoPath, string branchName, bool force = false);
    void StashChanges(string repoPath, string message);
    bool HasUncommittedChanges(string repoPath);

    // Tags (T-05). CRUD + checkout via LibGit2Sharp; only push/delete-remote may fall
    // back to the git CLI (mirrors the existing Push fallback). Name validation happens
    // before any mutation so the repo is never left with a half-created ref.
    IEnumerable<GitTagItem> GetTags(string repoPath);
    void CreateTag(string repoPath, string name, string targetSha, string? message); // annotated iff message != null
    void DeleteTag(string repoPath, string name);
    void PushTag(string repoPath, string remoteName, string name);
    void DeleteRemoteTag(string repoPath, string remoteName, string name);
    void CheckoutTag(string repoPath, string name);     // detached HEAD at the peeled target

    IEnumerable<GitStashItem> GetStashes(string repoPath);
    void StashPush(string repoPath, string message);
    void StashDrop(string repoPath, int stashIndex);
    void StashPop(string repoPath, int stashIndex);
    void StashApply(string repoPath, int stashIndex);

    // Worktrees (T-07) — CLI porcelain only (libgit2 worktree API is a locked no).
    IReadOnlyList<WorktreeItem> ListWorktrees(string repoPath);
    void AddWorktree(string repoPath, string worktreePath, string branchName, bool createBranch);
    void RemoveWorktree(string repoPath, string worktreePath, bool force);
    void PruneWorktrees(string repoPath);

    // Check out a PR / branch into a worktree (T-29). Reuses the T-07 worktree add; the fetch of a
    // PR head goes through the authenticated CLI path (no secret in argv/URL).
    /// <summary>Fetches a PR head from <paramref name="remoteName"/> into a local branch
    /// <c>pr/&lt;n&gt;</c>, then creates a worktree checked out to it. Returns the created worktree path.
    /// A non-empty <paramref name="worktreePath"/> throws a typed <see cref="Exceptions.GitOperationException"/>
    /// and creates nothing; a failure after the fetch is cleaned up best-effort (no half-made worktree).</summary>
    System.Threading.Tasks.Task<string> CheckoutPullRequestWorktree(
        string repoPath, int prNumber, string remoteName, string worktreePath, System.Threading.CancellationToken ct);

    /// <summary>Creates a worktree checked out to an existing local or remote-tracking branch
    /// (<paramref name="branchOrRef"/>). For a remote-tracking ref (e.g. <c>origin/feature</c>) a local
    /// tracking branch is created first if one doesn't already exist. Returns the worktree path.</summary>
    string CheckoutBranchWorktree(string repoPath, string branchOrRef, string worktreePath);

    // Submodules (T-16). Reads come from `repo.Submodules` (via ExecuteWithRepo); every
    // mutation is CLI-driven through the git submodule porcelain (the policy split — no libgit2
    // submodule mutation). Status is rolled up by the pure SubmoduleStatusMapper.
    IReadOnlyList<SubmoduleItem> GetSubmodules(string repoPath);
    void UpdateSubmodules(string repoPath);                    // submodule update --init --recursive
    void UpdateSubmoduleRemote(string repoPath, string path);  // submodule update --remote <path>
    void SyncSubmodules(string repoPath);                      // submodule sync --recursive

    string GetDiffAgainstCommit(string repoPath, string commitSha, string? filePath = null);

    string GetBranchDiffAgainstWorkingTree(string repoPath, string branchName);

    IEnumerable<string> GetCommitModifiedFiles(string repoPath, string commitSha);
    IEnumerable<string> GetBranchesContainingCommit(string repoPath, string commitSha);

    // Commit/tag signing (T-15). Signature verification for the given commits, batch-read via
    // `git log --no-walk --format=%H|%G?|%GS`. Returns a SHA → CommitSignatureInfo map (missing/unsigned
    // commits map to CommitSignatureInfo.None). Empty input → empty result, no git invocation.
    IReadOnlyDictionary<string, CommitSignatureInfo> GetSignatureStatuses(string repoPath, IReadOnlyList<string> shas);

    // Enumerates signing keys available for the current GpgFormat: gpg secret keys ("openpgp")
    // or `~/.ssh/*.pub` public keys ("ssh"). Used by the signing preferences key picker.
    IReadOnlyList<SigningKeyOption> ListSigningKeys(string gpgFormat);

    IEnumerable<string> GetAuthors(string repoPath);
    IEnumerable<string> GetRepositoryPaths(string repoPath);

    void CheckoutRevision(string repoPath, string commitSha);
    void ResetToCommit(string repoPath, string commitSha, LibGit2Sharp.ResetMode mode);
    void RevertCommit(string repoPath, string commitSha);
    void AmendCommitMessage(string repoPath, string commitSha, string newMessage);
    void CherryPick(string repoPath, string commitSha);

    /// <summary>Snapshot of HEAD (attached/detached/unborn + tip SHA) used to drive graph context-menu rules (T-09).</summary>
    GitHeadState GetHeadState(string repoPath);

    /// <summary>
    /// Reflog entries for <paramref name="refName"/> (T-20) — newest-first, capped at
    /// <paramref name="take"/> — read straight from Git's reflog via <c>repo.Refs.Log(...)</c>.
    /// <paramref name="refName"/> accepts <c>"HEAD"</c>, a friendly branch name (<c>"main"</c>), or a
    /// canonical ref (<c>"refs/heads/main"</c>). A ref that does not exist throws a typed
    /// <see cref="Exceptions.GitOperationException"/>; a ref that exists but has no reflog (a branch
    /// created without one) returns an empty list. The reflog is the data source for the recovery UI:
    /// its per-entry restore (hard reset) and "create branch here" recovery route through the existing
    /// journaled <see cref="ResetToCommit"/> / <see cref="CreateBranchAt"/> so they stay undoable (T-19).
    /// </summary>
    IReadOnlyList<ReflogItem> GetReflog(string repoPath, string refName = "HEAD", int take = 200);

    /// <summary>
    /// Per-line blame for the current version of <paramref name="path"/> at <paramref name="startingSha"/>
    /// (HEAD when null), rename-following through history (T-11). Result is bounded-LRU cached keyed by the
    /// resolved revision SHA, so a new commit misses and recomputes. A path missing at the revision throws a
    /// typed <see cref="Exceptions.GitOperationException"/> naming the path; a binary or empty file returns an
    /// empty list rather than throwing.
    /// </summary>
    IReadOnlyList<BlameLine> GetBlame(string repoPath, string path, string? startingSha = null);

    /// <summary>Drops cached blame for <paramref name="repoPath"/> (wired to <c>RepositoryWatcher.RepositoryChanged</c>, T-11).</summary>
    void InvalidateBlameCache(string repoPath);

    /// <summary>Creates a branch pointing at an arbitrary commit ("create branch here" from the graph, T-09).</summary>
    void CreateBranchAt(string repoPath, string branchName, string commitSha, bool checkout);

    // File history (T-12). Rename-following log of a single file, blob-at-revision, and the
    // adjacent-version diff — the reads behind the dedicated file-history view.

    /// <summary>Commits that touched <paramref name="path"/>, newest-first, following renames
    /// (<see cref="Models.FileVersion.PathAtCommit"/> is the file's historical name at each revision).</summary>
    IReadOnlyList<Models.FileVersion> GetFileHistory(string repoPath, string path);

    /// <summary>Text of the blob for <paramref name="path"/> at <paramref name="sha"/>. Throws a typed
    /// <see cref="Exceptions.GitOperationException"/> if the path is absent at that commit or the blob is
    /// binary (the UI shows a placeholder rather than rendering garbage).</summary>
    string GetFileAtCommit(string repoPath, string sha, string path);

    /// <summary>Raw bytes of the blob for <paramref name="path"/> at <paramref name="sha"/> (T-13
    /// image diff). Unlike <see cref="GetFileAtCommit"/> this does not reject binary blobs — it is
    /// the "before" image source. Throws a typed <see cref="Exceptions.GitOperationException"/> only
    /// when the path is absent at that commit.</summary>
    byte[] GetBlobBytesAtCommit(string repoPath, string sha, string path);

    /// <summary>Unified diff of <paramref name="path"/> between two commits — equals
    /// <c>git diff olderSha newerSha -- path</c>. Drives the selected-version-vs-predecessor pane.</summary>
    string GetFileDiffBetweenCommits(string repoPath, string olderSha, string newerSha, string path);
}
