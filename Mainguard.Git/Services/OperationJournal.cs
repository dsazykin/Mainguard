using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LibGit2Sharp;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Microsoft.EntityFrameworkCore;
using Repository = LibGit2Sharp.Repository;

namespace Mainguard.Git.Services;

/// <summary>
/// SQLite-backed operation journal (T-19). See <see cref="IOperationJournal"/>.
///
/// <para><b>index.lock safety.</b> The pre-snapshot is taken in a short-lived
/// <c>ExecuteWithRepo</c> that fully opens and disposes its native handle <i>before</i>
/// <see cref="BeginOperation"/> returns — i.e. before the mutating method opens its own
/// handle. The post-snapshot runs on scope <c>Dispose</c>, after the mutating method's
/// handle has already closed. The journal therefore never holds a <c>Repository</c>
/// handle that overlaps the mutation's, so it cannot cause an <c>.git/index.lock</c>
/// collision. The repo accessor is a plain, journal-free <see cref="GitService"/> so
/// snapshotting never recurses back into journaling.</para>
/// </summary>
public sealed class OperationJournal : IOperationJournal
{
    private readonly Func<AppDbContext> _contextFactory;
    private readonly IGitService _repo;

    public OperationJournal(Func<AppDbContext>? contextFactory = null, IGitService? repoAccessor = null)
    {
        _contextFactory = contextFactory ?? (() => new AppDbContext());
        // A journal-free GitService used only to open/dispose short-lived handles for
        // snapshots — ExecuteWithRepo does no journaling, so there is no recursion.
        _repo = repoAccessor ?? new GitService();
    }

    // ---- Recording -------------------------------------------------------

    public IDisposable BeginOperation(string repoPath, string kind, string description, string? undoBlockedReason = null)
    {
        RefSnapshot pre;
        try
        {
            pre = Capture(repoPath);
        }
        catch
        {
            // Never let journaling break a real Git operation: if we can't snapshot
            // (bad path, transient error) we simply don't journal this op.
            return NullScope.Instance;
        }
        return new OperationScope(this, repoPath, kind, description, undoBlockedReason, pre);
    }

    // Called once, on scope dispose, after the mutation's own handle has been released.
    private void Complete(string repoPath, string kind, string description, string? reason, RefSnapshot pre)
    {
        try
        {
            var post = Capture(repoPath);
            bool undoable = reason == null;

            var entry = new JournalEntry
            {
                RepoPath = repoPath,
                Kind = kind,
                Description = description,
                WhenUtc = DateTime.UtcNow,
                PreStateJson = Serialize(pre),
                PostStateJson = Serialize(post),
                IsUndoable = undoable,
                UndoBlockedReason = reason,
            };

            using var ctx = _contextFactory();
            // A brand-new operation truncates the redo stack: any entry for this repo that
            // is currently undone can no longer be redone.
            var superseded = ctx.JournalEntries
                .Where(e => e.RepoPath == repoPath && e.IsUndone && !e.IsTruncated)
                .ToList();
            foreach (var e in superseded) e.IsTruncated = true;

            ctx.JournalEntries.Add(entry);
            ctx.SaveChanges();
        }
        catch
        {
            // Best-effort: a journaling failure must never surface as a Git-op failure.
        }
    }

    // ---- History / Undo / Redo ------------------------------------------

    public IReadOnlyList<JournalEntry> GetHistory(string repoPath, int take = 100)
    {
        using var ctx = _contextFactory();
        return ctx.JournalEntries
            .Where(e => e.RepoPath == repoPath)
            .OrderByDescending(e => e.Id)
            .Take(take)
            .ToList();
    }

    public void Undo(string repoPath, long entryId)
    {
        var entry = LoadEntry(entryId)
            ?? throw new UndoBlockedException("That operation is no longer in the journal.");
        if (!entry.IsUndoable)
            throw new UndoBlockedException(entry.UndoBlockedReason ?? "This operation cannot be undone.");
        if (entry.IsUndone)
            throw new UndoBlockedException("This operation has already been undone.");

        var pre = Deserialize(entry.PreStateJson);
        var post = Deserialize(entry.PostStateJson);

        _repo.ExecuteWithRepo(repoPath, repo =>
        {
            var currentTip = repo.Head.Tip?.Sha;
            var targetTip = ResolveHeadTip(repo, pre);
            bool headTipChanges = currentTip != targetTip;

            // Dirty-tree guard: refuse (mutating nothing) if a worktree reset would clobber
            // uncommitted work the user made *after* the operation. If the operation itself
            // left the tree dirty (soft/mixed reset), that dirtiness is expected and allowed.
            if (headTipChanges && !post.TreeDirty && repo.RetrieveStatus().IsDirty)
                throw new UndoBlockedException(
                    "The working tree has uncommitted changes. Commit, stash, or discard them before undoing this operation.");

            RestoreRefs(repo, pre);

            if (headTipChanges && repo.Head.Tip != null)
            {
                // A commit undo is a mixed reset to the parent (changes reappear unstaged);
                // every other worktree-moving op does a hard reset onto the restored tip.
                var mode = (entry.Kind == JournalKinds.Commit || entry.Kind == JournalKinds.AmendCommitMessage)
                    ? ResetMode.Mixed
                    : ResetMode.Hard;
                repo.Reset(mode, repo.Head.Tip);
            }
        });

        SetUndone(entry.Id, true);
    }

    public void Redo(string repoPath, long entryId)
    {
        var entry = LoadEntry(entryId)
            ?? throw new UndoBlockedException("That operation is no longer in the journal.");
        if (!entry.IsUndoable)
            throw new UndoBlockedException(entry.UndoBlockedReason ?? "This operation cannot be redone.");
        if (!entry.IsUndone)
            throw new UndoBlockedException("This operation has not been undone, so there is nothing to redo.");
        if (entry.IsTruncated)
            throw new UndoBlockedException("A newer operation replaced this one; it can no longer be redone.");

        var post = Deserialize(entry.PostStateJson);

        _repo.ExecuteWithRepo(repoPath, repo =>
        {
            var currentTip = repo.Head.Tip?.Sha;
            var targetTip = ResolveHeadTip(repo, post);
            bool headTipChanges = currentTip != targetTip;

            RestoreRefs(repo, post);

            if (headTipChanges && repo.Head.Tip != null)
                repo.Reset(ResetMode.Hard, repo.Head.Tip);
        });

        SetUndone(entry.Id, false);
    }

    // ---- Ref snapshot / restore -----------------------------------------

    // Restores every direct ref + HEAD to the given snapshot. Ref-only work goes through
    // repo.Refs.UpdateTarget/Add/Remove; the caller does any worktree reset afterward.
    private static void RestoreRefs(Repository repo, RefSnapshot target)
    {
        var current = repo.Refs
            .OfType<DirectReference>()
            .ToDictionary(r => r.CanonicalName, r => r.TargetIdentifier, StringComparer.Ordinal);

        // 1. Create/update every ref the snapshot recorded (so any branch HEAD needs exists).
        foreach (var (name, sha) in target.Refs)
        {
            if (current.TryGetValue(name, out var curSha) && curSha == sha) continue;
            var oid = new ObjectId(sha);
            var existing = repo.Refs[name];
            if (existing is DirectReference dref)
                repo.Refs.UpdateTarget(dref, oid);
            else if (existing == null)
                repo.Refs.Add(name, oid);
        }

        // 2. Point HEAD at the recorded target before deleting anything (can't delete the
        //    ref HEAD currently points to). The symbolic overload takes the target Reference
        //    (passing a string would detach HEAD onto the resolved commit).
        if (target.HeadDetached)
        {
            repo.Refs.UpdateTarget(repo.Refs.Head, new ObjectId(target.HeadTarget));
        }
        else
        {
            var headTargetRef = repo.Refs[target.HeadTarget];
            if (headTargetRef != null)
                repo.Refs.UpdateTarget(repo.Refs.Head, headTargetRef);
        }

        // 3. Delete refs present now but absent from the snapshot (e.g. a created branch/tag).
        foreach (var name in current.Keys.Where(k => !target.Refs.ContainsKey(k)).ToList())
        {
            try { repo.Refs.Remove(name); } catch { /* ignore refs we cannot remove */ }
        }

        // 4. Restore local-branch upstream config (branch-delete undo recreates tracking).
        foreach (var (branchRef, upstream) in target.Upstreams)
        {
            var shortName = branchRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? branchRef.Substring("refs/heads/".Length)
                : branchRef;
            var branch = repo.Branches[shortName];
            if (branch == null) continue;

            var parts = upstream.Split('\n');
            var remote = parts[0];
            var merge = parts.Length > 1 ? parts[1] : null;
            if (branch.RemoteName == remote && branch.UpstreamBranchCanonicalName == merge) continue;
            try
            {
                repo.Branches.Update(branch, b => { b.Remote = remote; b.UpstreamBranch = merge; });
            }
            catch { /* upstream restore is best-effort */ }
        }
    }

    // The commit SHA that HEAD resolves to under a snapshot (its branch tip, or the detached SHA).
    private static string? ResolveHeadTip(Repository repo, RefSnapshot snap)
    {
        if (snap.HeadDetached) return snap.HeadTarget;
        return snap.Refs.TryGetValue(snap.HeadTarget, out var sha) ? sha : null;
    }

    private RefSnapshot Capture(string repoPath) => _repo.ExecuteWithRepo(repoPath, repo =>
    {
        var snap = new RefSnapshot
        {
            TreeDirty = repo.RetrieveStatus().IsDirty,
        };

        foreach (var r in repo.Refs.OfType<DirectReference>())
            snap.Refs[r.CanonicalName] = r.TargetIdentifier;

        var head = repo.Refs.Head;
        if (head is SymbolicReference sym)
        {
            snap.HeadDetached = false;
            snap.HeadTarget = sym.TargetIdentifier;
        }
        else
        {
            snap.HeadDetached = true;
            snap.HeadTarget = head.TargetIdentifier;
        }

        // Record upstream config from local branch config (branch.<x>.remote/merge) directly,
        // so a branch-delete undo can restore tracking even if the remote-tracking ref was
        // pruned or never fetched (b.TrackedBranch would be null in that case).
        foreach (var b in repo.Branches.Where(b => !b.IsRemote))
        {
            if (!string.IsNullOrEmpty(b.RemoteName) || !string.IsNullOrEmpty(b.UpstreamBranchCanonicalName))
                snap.Upstreams[b.CanonicalName] = $"{b.RemoteName}\n{b.UpstreamBranchCanonicalName}";
        }

        return snap;
    });

    // ---- Persistence helpers --------------------------------------------

    private JournalEntry? LoadEntry(long entryId)
    {
        using var ctx = _contextFactory();
        return ctx.JournalEntries.AsNoTracking().FirstOrDefault(e => e.Id == entryId);
    }

    private void SetUndone(long entryId, bool value)
    {
        using var ctx = _contextFactory();
        var e = ctx.JournalEntries.FirstOrDefault(x => x.Id == entryId);
        if (e == null) return;
        e.IsUndone = value;
        ctx.SaveChanges();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { IncludeFields = false };
    private static string Serialize(RefSnapshot s) => JsonSerializer.Serialize(s, JsonOpts);
    private static RefSnapshot Deserialize(string json) =>
        JsonSerializer.Deserialize<RefSnapshot>(json, JsonOpts) ?? new RefSnapshot();

    /// <summary>
    /// Reads one ref's sha out of a <see cref="JournalEntry"/>'s <c>PreStateJson</c>/<c>PostStateJson</c>.
    ///
    /// <para>Exposed because a journal entry is the only durable record of <b>which refs an operation
    /// moved, and from what to what</b> — and the RT-D1 boot reconcile has to answer exactly that about
    /// one specific merge lease. Reading it here rather than re-parsing the snapshot shape at the call
    /// site keeps the serialized layout in the one file that owns it; <see cref="RefSnapshot"/> stays
    /// internal so nothing outside can grow a second opinion about what a snapshot contains.</para>
    ///
    /// <para>False for a snapshot that cannot be parsed or does not name the ref — an <b>unanswerable</b>
    /// question, which is not the same as "the ref was at nothing". Every caller of this is one that has
    /// to decline rather than guess when it cannot get an answer.</para>
    /// </summary>
    /// <param name="stateJson">A journal entry's serialized pre- or post-operation ref snapshot.</param>
    /// <param name="canonicalRefName">The canonical ref name, e.g. <c>refs/heads/main</c>.</param>
    /// <param name="sha">The ref's sha at snapshot time, when it was recorded.</param>
    public static bool TryReadRef(string? stateJson, string canonicalRefName, out string sha)
    {
        sha = string.Empty;
        if (string.IsNullOrWhiteSpace(stateJson) || string.IsNullOrWhiteSpace(canonicalRefName))
        {
            return false;
        }

        RefSnapshot snapshot;
        try
        {
            snapshot = Deserialize(stateJson!);
        }
        catch (JsonException)
        {
            return false;
        }

        if (!snapshot.Refs.TryGetValue(canonicalRefName, out var value) || string.IsNullOrEmpty(value))
        {
            return false;
        }

        sha = value;
        return true;
    }

    // ---- Scope -----------------------------------------------------------

    private sealed class OperationScope : IDisposable
    {
        private readonly OperationJournal _journal;
        private readonly string _repoPath;
        private readonly string _kind;
        private readonly string _description;
        private readonly string? _reason;
        private readonly RefSnapshot _pre;
        private bool _done;

        public OperationScope(OperationJournal journal, string repoPath, string kind,
            string description, string? reason, RefSnapshot pre)
        {
            _journal = journal;
            _repoPath = repoPath;
            _kind = kind;
            _description = description;
            _reason = reason;
            _pre = pre;
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _journal.Complete(_repoPath, _kind, _description, _reason, _pre);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>Serialized ref state captured on operation begin/end.</summary>
internal sealed class RefSnapshot
{
    /// <summary>Canonical ref name → target SHA, for every direct ref (branches, tags, stash, remotes).</summary>
    public Dictionary<string, string> Refs { get; set; } = new(StringComparer.Ordinal);

    /// <summary>HEAD's symbolic target ("refs/heads/x") or, when detached, its commit SHA.</summary>
    public string HeadTarget { get; set; } = string.Empty;

    public bool HeadDetached { get; set; }

    /// <summary>Whether the working tree was dirty at snapshot time (informs the undo guard).</summary>
    public bool TreeDirty { get; set; }

    /// <summary>Local-branch canonical name → "remote\nupstreamCanonical" (tracking config).</summary>
    public Dictionary<string, string> Upstreams { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Shared kind constants so recording sites and undo logic can't drift.</summary>
public static class JournalKinds
{
    public const string Commit = "Commit";
    public const string AmendCommitMessage = "AmendCommitMessage";
    public const string Merge = "Merge";
    public const string Rebase = "Rebase";
    public const string CheckoutBranch = "CheckoutBranch";
    public const string CreateBranch = "CreateBranch";
    public const string CreateBranchAt = "CreateBranchAt";
    public const string RenameBranch = "RenameBranch";
    public const string DeleteBranch = "DeleteBranch";
    public const string ResetToCommit = "ResetToCommit";
    public const string RevertCommit = "RevertCommit";
    public const string CherryPick = "CherryPick";
    public const string CreateTag = "CreateTag";
    public const string DeleteTag = "DeleteTag";
    public const string StashPush = "StashPush";
    public const string StashPop = "StashPop";
    public const string StashApply = "StashApply";
    public const string StashDrop = "StashDrop";
    public const string InteractiveRebase = "InteractiveRebase";
    public const string Push = "Push";
    public const string Pull = "Pull";
}

/// <summary>No-op journal used when a <see cref="GitService"/> is constructed without one
/// (tests, headless harnesses) so every mutating method's <c>BeginOperation</c> wrapper is
/// behavior-preserving and zero-cost.</summary>
public sealed class NullOperationJournal : IOperationJournal
{
    public static readonly NullOperationJournal Instance = new();
    private sealed class Scope : IDisposable { public static readonly Scope I = new(); public void Dispose() { } }

    public IDisposable BeginOperation(string repoPath, string kind, string description, string? undoBlockedReason = null)
        => Scope.I;
    public IReadOnlyList<JournalEntry> GetHistory(string repoPath, int take = 100) => Array.Empty<JournalEntry>();
    public void Undo(string repoPath, long entryId) { }
    public void Redo(string repoPath, long entryId) { }
}
