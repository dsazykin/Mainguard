using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LibGit2Sharp;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Repository = LibGit2Sharp.Repository;

namespace Mainguard.Git.Services;

public class InteractiveRebaseService : IInteractiveRebaseService
{
    // Operation journal (T-19): StartInteractiveRebase rewrites history, so it wraps itself
    // in a BeginOperation scope. Defaults to a no-op so existing `new InteractiveRebaseService()`
    // call sites are behavior-preserving.
    private readonly IOperationJournal _journal;

    public InteractiveRebaseService(IOperationJournal? journal = null)
    {
        _journal = journal ?? NullOperationJournal.Instance;
    }

    public IReadOnlyList<RebaseTodoItem> GetRebasePlan(string repoPath, string baseSha)
    {
        using var repo = new Repository(repoPath);

        if (repo.Head.Tip == null)
            throw new GitOperationException("The current branch has no commits yet — nothing to rebase.");

        var baseCommit = repo.Lookup<Commit>(baseSha);
        if (baseCommit == null) throw new GitOperationException($"Base commit {baseSha} not found.");

        var filter = new CommitFilter
        {
            IncludeReachableFrom = repo.Head.Tip,
            ExcludeReachableFrom = baseCommit,
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse
        };

        var commits = repo.Commits.QueryBy(filter).ToList();

        if (commits.Count == 0)
            throw new GitOperationException("There are no commits between the selected commit and HEAD to rebase.");

        // v1 blocks merge commits: `git rebase -i` flattens them by default, which
        // silently drops the second parent's history. Refuse until --rebase-merges lands.
        if (commits.Any(c => c.Parents.Count() > 1))
            throw new GitOperationException("The selected range contains a merge commit, which interactive rebase does not support yet.");

        var plan = new List<RebaseTodoItem>();
        foreach (var c in commits)
        {
            plan.Add(new RebaseTodoItem
            {
                Sha = c.Sha,
                Message = c.MessageShort,
                FullMessage = c.Message,
                Action = RebaseAction.Pick
            });
        }

        return plan;
    }

    public void StartInteractiveRebase(string repoPath, string baseSha, IReadOnlyList<RebaseTodoItem> plan, CancellationToken ct = default)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.InteractiveRebase, "Interactive rebase");
        using (var repo = new Repository(repoPath))
        {
            if (repo.RetrieveStatus().IsDirty)
            {
                throw new GitOperationException("Working tree is dirty. Stash or discard your changes before rebasing.");
            }

            // Per-worktree state — resolve the gitdir, never <repoPath>/.git (a FILE in a
            // linked worktree). See GitService.ResolveGitDir.
            if (Directory.Exists(GitService.GitDirPath(repoPath, "rebase-merge")) ||
                Directory.Exists(GitService.GitDirPath(repoPath, "rebase-apply")))
            {
                throw new GitOperationException("A rebase is already in progress.");
            }
        }

        var firstKept = plan.FirstOrDefault(p => p.Action != RebaseAction.Drop);
        if (firstKept == null)
            throw new GitOperationException("The plan drops every commit — nothing would remain to rebase.");
        if (firstKept.Action == RebaseAction.Squash || firstKept.Action == RebaseAction.Fixup)
        {
            throw new GitOperationException("Cannot squash or fixup without a previous kept commit.");
        }

        // Message queue: files keyed by the ORIGINAL commit SHA. The --rebase-msg editor
        // shim reads .git/rebase-merge/done to learn which commit git is currently editing
        // and copies the matching message in. SHA-keying (rather than an ordinal queue) is
        // what makes squash chains correct: git invokes the editor once per squash *chain*,
        // so an ordinal queue would desync after the first multi-squash group.
        //
        // The dir lives inside the resolved gitdir so it survives conflict/edit pauses and is reused verbatim
        // by ContinueRebase — the temp-dir-deleted-on-pause bug is gone.
        var msgDir = GitService.RebaseMsgQueueDir(repoPath);
        try { if (Directory.Exists(msgDir)) Directory.Delete(msgDir, true); } catch { }
        Directory.CreateDirectory(msgDir);

        var todoLines = new List<string>();
        foreach (var item in plan)
        {
            if (item.Action == RebaseAction.Drop) continue;

            var actionStr = item.Action.ToString().ToLowerInvariant();
            var msgSafe = item.Message.Replace('\n', ' ').Replace('\r', ' ');
            todoLines.Add($"{actionStr} {item.Sha} {msgSafe}");

            // Only stage a custom message when the user actually supplied one. When null,
            // git keeps its own default (the full original message for reword, the combined
            // message for squash) — this is what preserves multi-line bodies.
            if ((item.Action == RebaseAction.Reword || item.Action == RebaseAction.Squash)
                && !string.IsNullOrEmpty(item.NewMessage))
            {
                File.WriteAllText(Path.Combine(msgDir, item.Sha + ".msg"), item.NewMessage);
            }
        }

        var todoPath = Path.Combine(Path.GetTempPath(), "mainguard-todo-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllLines(todoPath, todoLines);

        // Invariant 5: the generated todo is logged for diagnosability. The applied
        // payload is logged from the --rebase-editor shim (Program.cs) as git runs it.
        System.Diagnostics.Debug.WriteLine(
            "[Mainguard] Interactive rebase generated todo:\n" + string.Join('\n', todoLines));

        var self = GitService.GetSelfInvocationPrefix();
        var env = new Dictionary<string, string>
        {
            ["GIT_SEQUENCE_EDITOR"] = $"{self} --rebase-editor \"{todoPath}\"",
            ["GIT_EDITOR"] = $"{self} --rebase-msg \"{msgDir}\""
        };

        int code; string errStr;
        try
        {
            (code, _, errStr) = GitService.RunGit(repoPath, env, ct, "rebase", "-i", baseSha);
        }
        finally
        {
            // The sequence editor only runs once, at the very start, so the todo temp file
            // is safe to remove now. The message queue (msgDir) must NOT be removed here —
            // remaining reword/squash steps after a pause still need it on --continue.
            try { File.Delete(todoPath); } catch { }
        }

        if (code == 0)
        {
            // Completed in one shot — no further editor invocations will happen.
            try { Directory.Delete(msgDir, true); } catch { }
            return;
        }

        using (var repo = new Repository(repoPath))
        {
            if (repo.Index.Conflicts.Any())
            {
                throw new MergeConflictException("Merge conflicts detected! Please select the conflicted files in the left staging panel, resolve the conflicts in the Diff Viewer, save the files to stage them, and then click 'Continue Rebase'.");
            }
        }

        var stoppedShaPath = GitService.GitDirPath(repoPath, "rebase-merge", "stopped-sha");
        if (File.Exists(stoppedShaPath))
        {
            var sha = File.ReadAllText(stoppedShaPath).Trim();
            throw new GitOperationException($"Rebase paused at {sha} for editing. Amend your changes, then click 'Continue Rebase'.");
        }

        // Genuine failure that did not leave a resumable rebase — clean the queue up.
        if (!Directory.Exists(GitService.GitDirPath(repoPath, "rebase-merge")))
        {
            try { Directory.Delete(msgDir, true); } catch { }
        }
        throw new GitOperationException($"Rebase failed: {errStr}");
    }

    public (int Step, int Total)? GetRebaseProgress(string repoPath)
    {
        var msgnumPath = GitService.GitDirPath(repoPath, "rebase-merge", "msgnum");
        var endPath = GitService.GitDirPath(repoPath, "rebase-merge", "end");

        if (File.Exists(msgnumPath) && File.Exists(endPath)
            && int.TryParse(File.ReadAllText(msgnumPath).Trim(), out var step)
            && int.TryParse(File.ReadAllText(endPath).Trim(), out var total))
        {
            return (step, total);
        }

        return null;
    }
}
