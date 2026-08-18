using System;
using System.IO;
using LibGit2Sharp;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Repository = LibGit2Sharp.Repository;

namespace Mainguard.Git.Services;

public class GitService : IGitService
{
    // Bounded-LRU blame cache (T-11). Keyed by (repoPath, path, revisionSha); a moving
    // HEAD changes the key so cached blame is never stale. One cache per GitService
    // instance — the RepoDashboard holds a single instance for the workspace's lifetime.
    private readonly BlameCache _blameCache = new(capacity: 32);

    // Signing preferences provider (T-15). Optional so the many `new GitService()` call sites
    // (tests, headless harnesses) keep working with signing off; the app injects a live provider
    // reading App.Settings so a preference change takes effect on the next commit without a restart.
    private readonly Func<UserPreferences>? _preferencesProvider;
    private static readonly UserPreferences DefaultPreferences = new();

    private UserPreferences Preferences => _preferencesProvider?.Invoke() ?? DefaultPreferences;

    // Operation journal (T-19). Every mutating method wraps itself in
    // `using var op = _journal.BeginOperation(...)`. Defaults to a no-op so the many
    // `new GitService()` call sites (tests, harnesses) are behavior-preserving and free;
    // the app injects a real OperationJournal so operations become undoable.
    private readonly IOperationJournal _journal;

    public GitService() : this((Func<UserPreferences>?)null, null)
    {
    }

    public GitService(Func<UserPreferences> preferencesProvider) : this(preferencesProvider, null)
    {
    }

    public GitService(Func<UserPreferences>? preferencesProvider, IOperationJournal? journal)
    {
        _preferencesProvider = preferencesProvider;
        _journal = journal ?? NullOperationJournal.Instance;
    }

    // Journal-description helpers: keep operation labels short and single-line.
    private static string Short(string sha) => string.IsNullOrEmpty(sha) || sha.Length <= 7 ? sha : sha.Substring(0, 7);

    private static string Describe(string verb, string message)
    {
        var firstLine = (message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (firstLine.Length > 60) firstLine = firstLine.Substring(0, 57) + "…";
        return string.IsNullOrEmpty(firstLine) ? verb : $"{verb}: {firstLine}";
    }

    public bool IsGitRepository(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.
                Exists(path))
        {
            return false;
        }

        try
        {
            return Repository.IsValid(path);
        }
        catch
        {
            return false;
        }
    }

    public void ExecuteWithRepo(string path, Action<Repository>
        action)
    {
        ExecuteWithRepo<object?>(path, repo =>
        {
            action(repo);
            return null;
        });
    }

    public T ExecuteWithRepo<T>(string path, Func<Repository, T> func)
    {
        if (!IsGitRepository(path))
        {
            throw new ArgumentException("Path is not a valid Git repository.", nameof(path));
        }

        // Reliability: another process (a terminal `git`, an IDE plugin, an agent) holding
        // `.git/index.lock` surfaces as LockedFileException. libgit2 raises it when it FAILS TO
        // ACQUIRE the lockfile — the operation has made no changes yet — so retrying with a short
        // exponential backoff is safe and turns the most common transient collision in a
        // multi-tool workflow into a non-event. Each retry re-opens the repository so no state
        // from the failed attempt leaks into the next one. If the lock is still held after the
        // final attempt (a wedged or crashed process), the failure surfaces as a typed
        // GitOperationException that names the lock file and the way out, instead of a raw
        // libgit2 message.
        const int maxAttempts = 4;
        int delayMs = 25;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var repo = new Repository(path);
                return func(repo);
            }
            catch (LockedFileException ex)
            {
                if (attempt >= maxAttempts)
                {
                    throw new GitOperationException(
                        "Another process is using this repository (.git/index.lock is held). " +
                        "Wait for the other Git operation to finish and try again — if no other " +
                        "process is running, a crashed one may have left the lock behind and the " +
                        "lock file can be removed safely.", ex);
                }
                System.Threading.Thread.Sleep(delayMs);
                delayMs *= 2; // 25 → 50 → 100 ms between the four attempts
            }
        }
    }

    public List<GitFileStatus> GetRepositoryStatus(string path)
    {
        return ExecuteWithRepo(path, repo =>
        {
            var results = new List<GitFileStatus>();

            // Query the repository status (includes untracked files)
            var options = new StatusOptions { IncludeUntracked = true };
            var repoStatus = repo.RetrieveStatus(options);

            foreach (var item in repoStatus)
            {
                // We ignore Ignored files (like bin/ obj/ node_modules/) to save performance
                if (item.State == FileStatus.Ignored) continue;

                results.Add(new GitFileStatus
                {
                    FilePath = item.FilePath,
                    State = item.State
                });
            }

            return results;
        });
    }

    public void StageFile(string repoPath, string filePath)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            Commands.Stage(repo, filePath);
        });
    }

    public void UnstageFile(string repoPath, string filePath)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            Commands.Unstage(repo, filePath);
        });
    }

    public void StageFiles(string repoPath, IEnumerable<string> filePaths)
    {
        ExecuteWithRepo(repoPath, repo => Commands.Stage(repo, filePaths));
    }

    public void UnstageFiles(string repoPath, IEnumerable<string> filePaths)
    {
        ExecuteWithRepo(repoPath, repo => Commands.Unstage(repo, filePaths));
    }

    public void DiscardChanges(string repoPath, IEnumerable<string> filePaths)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var paths = filePaths.ToList();
            var status = repo.RetrieveStatus(new StatusOptions { PathSpec = paths.ToArray() });
            var trackedToCheckout = new List<string>();
            var newToRemove = new List<string>();

            foreach (var path in paths)
            {
                var fullPath = System.IO.Path.Combine(repo.Info.WorkingDirectory, path);

                // NEVER touch a directory. Discard operates on the individual file
                // paths surfaced in the status list; if a path resolves to a
                // directory, skip it rather than recursively wiping an untracked
                // tree full of the user's work (a data-loss trap).
                if (System.IO.Directory.Exists(fullPath))
                {
                    continue;
                }

                var entry = status[path];
                bool isNew = entry != null &&
                    (entry.State.HasFlag(FileStatus.NewInWorkdir) || entry.State.HasFlag(FileStatus.NewInIndex));

                if (isNew)
                {
                    newToRemove.Add(path);
                }
                else
                {
                    trackedToCheckout.Add(path);
                }
            }

            foreach (var path in newToRemove)
            {
                // Unstage staged-new files first so they fully disappear from the
                // index instead of lingering as a phantom "deleted" entry.
                var entry = status[path];
                if (entry != null && entry.State.HasFlag(FileStatus.NewInIndex))
                {
                    Commands.Unstage(repo, path);
                }

                var fullPath = System.IO.Path.Combine(repo.Info.WorkingDirectory, path);
                if (System.IO.File.Exists(fullPath))
                {
                    SafeDeleteFile(fullPath);
                }
            }

            if (trackedToCheckout.Count > 0)
            {
                repo.CheckoutPaths(repo.Head.FriendlyName, trackedToCheckout.ToArray(), new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
            }
        });
    }

    // --- Partial (hunk / line) staging -------------------------------------
    // Whole-file staging (StageFile) is not enough for crafting atomic commits.
    // These apply a caller-selected subset of a unified-diff patch to the index
    // or working tree via `git apply`, which is how serious clients implement
    // reliable partial staging. The UI (Category 2.13) builds the patch subset.

    public void StageHunk(string repoPath, string patch)
    {
        // Apply the selected hunk(s) to the index only.
        ApplyPatch(repoPath, patch, "--cached");
    }

    public void UnstageHunk(string repoPath, string patch)
    {
        // Reverse the selected hunk(s) out of the index.
        ApplyPatch(repoPath, patch, "--cached", "--reverse");
    }

    public void DiscardHunk(string repoPath, string patch)
    {
        // Reverse the selected hunk(s) out of the working tree.
        ApplyPatch(repoPath, patch, "--reverse");
    }

    private static void ApplyPatch(string repoPath, string patch, params string[] applyArgs)
    {
        if (string.IsNullOrEmpty(patch)) return;

        // git apply requires the patch to terminate with a newline.
        if (!patch.EndsWith("\n")) patch += "\n";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("apply");
        foreach (var a in applyArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-"); // read the patch from stdin (never a temp file / argv)

        // git apply never needs a prompt; keep it non-interactive so it can't hang.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.Start(psi)
                      ?? throw new GitOperationException(
                          "Failed to launch git. Is Git installed and on the PATH?");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GitOperationException(
                "Failed to launch git. Is Git installed and on the PATH?", ex);
        }

        using (process)
        {
            // Drain both output pipes concurrently BEFORE the blocking stdin write:
            // a large patch could otherwise deadlock, each side blocked on a full
            // pipe (we on stdin, git on an unread stdout/stderr).
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            process.StandardInput.Write(patch);
            process.StandardInput.Close();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                var err = stderrTask.Result;
                throw new GitOperationException(string.IsNullOrWhiteSpace(err)
                    ? $"git apply failed with exit code {process.ExitCode}."
                    : err);
            }
        }
    }

    /// <summary>
    /// Removes a single working-tree file. On Windows it goes to the Recycle Bin and on macOS to
    /// the Finder Trash (with Put Back), so a mis-clicked discard is recoverable; Linux (no
    /// standard trash API) falls back to a hard delete.
    /// </summary>
    private static void SafeDeleteFile(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                fullPath,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        else if (OperatingSystem.IsMacOS() && TryTrashOnMac(fullPath))
        {
            // Sent to the Trash — nothing else to do.
        }
        else
        {
            System.IO.File.Delete(fullPath);
        }
    }

    /// <summary>
    /// macOS ships <c>/usr/bin/trash</c> (Finder-integrated; "Put Back" works). False on any
    /// failure so the caller's hard delete keeps the discard's contract — the file must be gone
    /// either way; the Trash is a courtesy, never a silent no-op.
    /// </summary>
    private static bool TryTrashOnMac(string fullPath)
    {
        try
        {
            if (!System.IO.File.Exists("/usr/bin/trash")) return false;

            var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/trash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(fullPath);
            using var trash = System.Diagnostics.Process.Start(psi);
            if (trash is null) return false;
            trash.WaitForExit(10_000);
            return trash.HasExited && trash.ExitCode == 0 && !System.IO.File.Exists(fullPath);
        }
        catch
        {
            return false;
        }
    }

    public string GetFileDiff(string repoPath, string filePath, bool isStaged)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            Patch patch;
            if (isStaged)
            {
                // Compare HEAD against the Staging Area (Index)
                // If the repo is brand new and has no commits, Head.Tip will be null, which LibGit2Sharp handles gracefully
                Tree? headTree = repo.Head.Tip?.Tree;
                patch = repo.Diff.Compare<Patch>(headTree, DiffTargets.Index, new[] { filePath });
            }
            else
            {
                // Compare the Staging Area (Index) against the Local Filesystem
                patch = repo.Diff.Compare<Patch>(new[] { filePath });
            }

            return patch.Content;
        });
    }

    public string GetFileDiff(string repoPath, string filePath, bool isStaged, bool ignoreWhitespace)
    {
        if (!ignoreWhitespace)
            return GetFileDiff(repoPath, filePath, isStaged);

        // Whitespace-ignored diff (T-13): the CLI's `-w` is the source of truth here (libgit2's
        // IgnoreWhitespace options don't collapse hunks the same way). A whitespace-only change
        // therefore yields zero hunks. `--` guards paths that look like options. Staged view diffs
        // the index against HEAD (`--cached`); unstaged diffs the working tree against the index.
        var args = isStaged
            ? new[] { "diff", "-w", "--cached", "--", filePath }
            : new[] { "diff", "-w", "--", filePath };

        var (code, output, err) = RunGit(repoPath, args);
        if (code != 0 && !string.IsNullOrEmpty(err))
            throw new GitOperationException($"git diff -w failed: {err.Trim()}");
        return output;
    }

    /// <summary>
    /// Builds a committer/author signature from the repo config, or throws
    /// <see cref="GitIdentityMissingException"/> when no identity is configured.
    /// Every mutating operation must route through this instead of calling
    /// <c>repo.Config.BuildSignature</c> directly (which returns null on a fresh
    /// machine and crashes the caller) or falling back to a bogus placeholder
    /// identity (which pollutes history).
    /// </summary>
    private static Signature GetSignature(Repository repo)
    {
        // Capture one timestamp and reuse it so author/committer can't drift.
        var now = DateTimeOffset.Now;

        var sig = repo.Config.BuildSignature(now);
        if (sig != null) return sig;

        var name = repo.Config.Get<string>("user.name")?.Value;
        var email = repo.Config.Get<string>("user.email")?.Value;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
        {
            // Used by commit/pull/merge/rebase/stash/revert/cherry-pick, so keep
            // the message operation-agnostic.
            throw new GitIdentityMissingException(
                "No Git identity configured. Set your user.name and user.email before running Git operations.");
        }

        return new Signature(name, email, now);
    }

    public void Commit(string repoPath, string message, bool amend = false)
    {
        // Refuse the two unsafe amends before anything is journalled or written.
        if (amend) GuardAmend(repoPath);

        using var op = _journal.BeginOperation(repoPath,
            amend ? JournalKinds.AmendCommitMessage : JournalKinds.Commit,
            Describe(amend ? "Amend commit" : "Commit", message));
        // Signing is a git-orchestration concern (gpg/ssh agents, pinentry) that LibGit2Sharp
        // can't drive, so when the preference is on we hand the commit to the git CLI and let it
        // sign from the (locally written) repo config. GIT_TERMINAL_PROMPT=0 is inherited by
        // RunGit, so a bad key fails fast instead of hanging on a pinentry prompt (T-15 invariant).
        if (Preferences.SignCommits)
        {
            // Validate the identity up front so a missing user.name/email throws the same typed
            // error as the unsigned path, before we shell out.
            ExecuteWithRepo(repoPath, repo => GetSignature(repo));
            ApplySigningConfig(repoPath);
            if (amend) RunGitChecked(repoPath, "commit", "--amend", "-m", message);
            else RunGitChecked(repoPath, "commit", "-m", message);
            return;
        }

        ExecuteWithRepo(repoPath, repo =>
        {
            // Resolve the identity from config, throwing a typed error if unset.
            var signature = GetSignature(repo);

            // An amend keeps the ORIGINAL author and only re-stamps the committer — that is what
            // `git commit --amend` does, and therefore what the signing path above already does.
            // Passing `signature` for both would quietly reassign authorship on the unsigned path.
            var author = amend ? repo.Head.Tip!.Author : signature;

            // Commits whatever is currently in the Staging Index
            repo.Commit(message, author, signature, new CommitOptions { AmendPreviousCommit = amend });
        });
    }

    /// <summary>
    /// Pre-flight for <c>Commit(..., amend: true)</c>. Amending the <b>root</b> commit is fine
    /// (a parentless amend simply rewrites the root), but there are two amends we refuse:
    /// an unborn branch has no commit to amend at all, and a HEAD already contained in the
    /// branch's upstream is published — rewriting it diverges the branch and the next push is
    /// rejected, which is exactly the "silently created a diverged branch" outcome the checkbox
    /// must not produce. The upstream ref is the local view of the remote (it can be stale), so
    /// this is a best-effort guard against the common case, not a network round-trip.
    /// </summary>
    private void GuardAmend(string repoPath) => ExecuteWithRepo(repoPath, repo =>
    {
        var tip = repo.Head.Tip;
        if (tip == null)
            throw new GitOperationException(
                "There is nothing to amend — this branch has no commits yet. Clear \"Amend last commit\" and commit normally.");

        var upstream = repo.Head.TrackedBranch;
        if (upstream?.Tip != null && IsAncestorOrSame(repo, tip, upstream.Tip))
        {
            var branchName = repo.Info.IsHeadDetached ? string.Empty : repo.Head.FriendlyName;
            var upstreamName = upstream.FriendlyName;
            throw new AmendPushedCommitException(branchName, upstreamName,
                $"The last commit is already published on '{upstreamName}'. Amending it would rewrite " +
                $"history that has been pushed and leave '{branchName}' diverged from its upstream. " +
                "Commit your changes on top instead, or reset and force-push deliberately.");
        }
    });

    /// <summary>
    /// Writes the signing preferences into the repo's <b>local</b> config (never global) so a
    /// subsequent <c>git commit</c>/<c>git tag</c> signs deterministically (T-15). Only non-empty
    /// preference values overwrite config, so a user who configured signing directly in git keeps
    /// their settings. All writes go through <see cref="ExecuteWithRepo"/>.
    /// </summary>
    private void ApplySigningConfig(string repoPath)
    {
        var prefs = Preferences;
        ExecuteWithRepo(repoPath, repo =>
        {
            repo.Config.Set("commit.gpgsign", true, ConfigurationLevel.Local);
            repo.Config.Set("tag.gpgsign", true, ConfigurationLevel.Local);

            var format = string.IsNullOrWhiteSpace(prefs.GpgFormat) ? "openpgp" : prefs.GpgFormat.Trim();
            repo.Config.Set("gpg.format", format, ConfigurationLevel.Local);

            if (!string.IsNullOrWhiteSpace(prefs.SigningKey))
                repo.Config.Set("user.signingkey", prefs.SigningKey.Trim(), ConfigurationLevel.Local);

            if (!string.IsNullOrWhiteSpace(prefs.GpgProgram))
            {
                // git reads gpg.<format>.program for the format-specific binary; openpgp also
                // honours the legacy gpg.program key. Set both so the override always applies.
                repo.Config.Set($"gpg.{format}.program", prefs.GpgProgram!.Trim(), ConfigurationLevel.Local);
                if (format == "openpgp")
                    repo.Config.Set("gpg.program", prefs.GpgProgram!.Trim(), ConfigurationLevel.Local);
            }
        });
    }


    private string? GetRemoteUrl(string repoPath, string remoteName)
    {
        // Read the RAW configured fetch URL rather than repo.Network.Remotes[..].Url: libgit2 applies
        // any `url.<base>.insteadOf` rewrite to the latter, which would hide the user's real host (e.g.
        // github.com rewritten to a mirror) from host/token detection. The literal config value is what
        // classifies the host; the insteadOf rewrite still applies at transport time in the git CLI.
        return ExecuteWithRepo(repoPath, repo =>
        {
            if (repo.Network.Remotes[remoteName] is null) return null;
            return repo.Config.Get<string>($"remote.{remoteName}.url")?.Value;
        });
    }

    private string? GetTokenForRemote(string repoPath, string remoteName)
        => GetTokenForUrl(GetRemoteUrl(repoPath, remoteName));

    // Resolves a stored token for the host of the given remote URL.
    private static string? GetTokenForUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        var (host, kind) = Mainguard.Git.Security.GitHostDetector.Detect(url);
        var keyring = new Mainguard.Git.Security.SecureKeyring();

        var token = string.IsNullOrEmpty(host)
            ? null
            : keyring.RetrieveSecret(Mainguard.Git.Security.GitHostDetector.TokenKeyForHost(host));

        // Back-compat: fall back to the legacy single "github_token" secret.
        if (string.IsNullOrEmpty(token) && kind == HostKind.GitHub)
        {
            token = keyring.RetrieveSecret("github_token");
        }
        return token;
    }

    public void Push(string repoPath)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.Push, "Push to remote",
            undoBlockedReason: "Pushing to a remote cannot be undone from the journal.");
        // Remote transport goes through the git CLI (RunGitCheckedAuthenticated):
        // libgit2 has no SSH support, and the CLI path handles both HTTPS (token)
        // and SSH (rewritten to HTTPS when a token exists, otherwise SSH keys).
        bool needsUpstream = false;
        string branchName = "";
        string remoteName = "";
        ExecuteWithRepo(repoPath, repo =>
        {
            var branch = repo.Head;
            needsUpstream = branch.TrackedBranch == null;
            branchName = branch.FriendlyName;
            remoteName = ResolveRemoteName(repo);
        });

        if (needsUpstream)
            PushSetUpstream(repoPath, remoteName, branchName);
        else
            RunGitCheckedAuthenticated(repoPath, remoteName, "push");
    }

    // Resolves which remote an operation should target when the caller did not name
    // one: the current branch's upstream remote first, then "origin", then the sole
    // configured remote, else a typed RemoteNotFoundException. This is the single
    // point that used to be a hardcoded "origin" across Push/Pull/Fetch/PushBranch.
    private static string ResolveRemoteName(Repository repo, string? preferred = null)
    {
        if (!string.IsNullOrEmpty(preferred) && repo.Network.Remotes[preferred] != null) return preferred!;

        var tracked = repo.Head.TrackedBranch?.RemoteName;
        if (!string.IsNullOrEmpty(tracked)) return tracked!;

        if (repo.Network.Remotes["origin"] != null) return "origin";

        var remotes = repo.Network.Remotes.ToList();
        if (remotes.Count == 1) return remotes[0].Name;

        throw new RemoteNotFoundException(remotes.Count == 0
            ? "No remote configured for this repository."
            : "Multiple remotes configured and none is tracked — choose a remote explicitly.");
    }

    private string ResolveRemoteName(string repoPath, string? preferred = null)
        => ExecuteWithRepo(repoPath, repo => ResolveRemoteName(repo, preferred));

    public void Pull(string repoPath)
    {
        Pull(repoPath, PullStrategy.Default);
    }

    public void Pull(string repoPath, PullStrategy strategy)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.Pull, "Pull from remote",
            undoBlockedReason: "Pulling from a remote is not undoable; use the branch history to recover.");
        // Rebase strategy: fetch then replay the current branch onto its upstream.
        if (strategy == PullStrategy.Rebase)
        {
            Fetch(repoPath);
            var upstream = ExecuteWithRepo(repoPath, repo => repo.Head.TrackedBranch?.FriendlyName);
            if (string.IsNullOrEmpty(upstream))
                throw new GitOperationException("No upstream branch configured to rebase onto.");
            Rebase(repoPath, upstream);
            return;
        }

        // Remote transport goes through the git CLI (see Push). On conflicts git exits
        // non-zero; the finally converts that into a MergeConflictException the UI knows.
        var remoteName = ResolveRemoteName(repoPath);
        try
        {
            if (strategy == PullStrategy.FastForwardOnly)
                RunGitCheckedAuthenticated(repoPath, remoteName, "pull", "--ff-only");
            else
                RunGitCheckedAuthenticated(repoPath, remoteName, "pull", "--no-rebase");
        }
        finally
        {
            var hasConflicts = ExecuteWithRepo(repoPath, repo => repo.Index.Conflicts.Any());
            if (hasConflicts)
                throw new MergeConflictException(
                    "Pull produced conflicts. Resolve the conflicted files in the Diff Viewer, then commit the merge.");
        }
    }

    public void Fetch(string repoPath, bool prune = false)
        => Fetch(repoPath, ResolveRemoteName(repoPath), prune);

    public void Fetch(string repoPath, string remoteName, bool prune = false)
    {
        // Remote transport goes through the git CLI (see Push). The remote is named
        // explicitly so a repo with several remotes fetches the intended one.
        if (prune)
            RunGitCheckedAuthenticated(repoPath, remoteName, "fetch", remoteName, "--prune");
        else
            RunGitCheckedAuthenticated(repoPath, remoteName, "fetch", remoteName);
    }
    public void Rebase(string repoPath, string targetBranchName)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.Rebase, $"Rebase onto {targetBranchName}");
        try
        {
            ExecuteWithRepo(repoPath, repo =>
            {
                var targetBranch = repo.Branches[targetBranchName];
                if (targetBranch == null) throw new GitOperationException($"Branch {targetBranchName} not found.");

                var signature = GetSignature(repo);
                var identity = new LibGit2Sharp.Identity(signature.Name, signature.Email);
                var rebaseResult = repo.Rebase.Start(repo.Head, targetBranch, null, identity, new RebaseOptions());

                if (rebaseResult.Status != RebaseStatus.Complete)
                {
                    // Do not abort! Leave the repository in the Rebasing state so the user can actually fix it.
                    throw new MergeConflictException($"Merge conflicts detected! Please select the conflicted files in the left staging panel, resolve the conflicts in the Diff Viewer, save the files to stage them, and then click 'Continue Rebase'.");
                }
            });
        }
        catch (LibGit2SharpException)
        {
            // Fallback to CLI if LibGit2Sharp outright fails (e.g., unsupported options)
            RunGitChecked(repoPath, "rebase", targetBranchName);
        }
    }

    // The drag-a-commit-onto-a-branch-label "Rebase <branch> onto here" gesture (#87). LibGit2Sharp's
    // Rebase.Start API only accepts named Branch objects for "onto" — there is no branch to resolve
    // for an arbitrary commit SHA, so this always goes through the CLI (same fallback path Rebase(...)
    // uses when LibGit2Sharp can't do it), matching the "worktree/submodule ops are CLI-driven when
    // libgit2's high-level API can't express them" convention elsewhere in this file.
    public void RebaseOntoCommit(string repoPath, string commitSha)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.Rebase, $"Rebase onto {commitSha}");
        RunGitChecked(repoPath, "rebase", commitSha);
    }

    public void Merge(string repoPath, string sourceBranchName)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.Merge, $"Merge {sourceBranchName}");
        try
        {
            ExecuteWithRepo(repoPath, repo =>
            {
                var sourceBranch = repo.Branches[sourceBranchName];
                if (sourceBranch == null) throw new GitOperationException($"Branch {sourceBranchName} not found.");

                var signature = GetSignature(repo);

                var mergeResult = repo.Merge(sourceBranch, signature, new MergeOptions { CommitOnSuccess = false });

                if (mergeResult.Status == MergeStatus.Conflicts)
                {
                    throw new MergeConflictException($"Merge conflicts detected! Please select the conflicted files in the left staging panel, resolve the conflicts in the Diff Viewer, save the files to stage them, and then commit the merge.");
                }
            });
        }
        catch (LibGit2SharpException)
        {
            RunGitChecked(repoPath, "merge", "--no-commit", sourceBranchName);
        }
    }

    public bool IsMergeInProgress(string repoPath)
    {
        return System.IO.File.Exists(GitDirPath(repoPath, "MERGE_HEAD"));
    }

    public string GetMergeMessage(string repoPath)
    {
        var msgPath = GitDirPath(repoPath, "MERGE_MSG");
        if (System.IO.File.Exists(msgPath))
        {
            return System.IO.File.ReadAllText(msgPath).Trim();
        }
        return "Merge completed";
    }

    // --- Conflict resolution ---
    // Reads/writes the merge index stages (repo.Index.Conflicts) as the single source of truth.
    // Never parses working-tree conflict markers, and never commits on its own.

    public IReadOnlyList<ConflictedFile> GetConflicts(string repoPath) =>
        ExecuteWithRepo(repoPath, repo =>
            repo.Index.Conflicts
                .Select(c => new ConflictedFile
                {
                    // At least one stage is always present; prefer a non-null one for the path.
                    Path = c.Ours?.Path ?? c.Theirs?.Path ?? c.Ancestor!.Path,
                    HasBase = c.Ancestor != null,
                    HasOurs = c.Ours != null,
                    HasTheirs = c.Theirs != null,
                })
                .OrderBy(f => f.Path, StringComparer.Ordinal)   // stable UI ordering
                .ToList());

    public (string BaseText, string OursText, string TheirsText) GetConflictBlobs(string repoPath, string path) =>
        ExecuteWithRepo(repoPath, repo =>
        {
            var c = repo.Index.Conflicts[path]
                ?? throw new GitOperationException($"No conflict recorded for '{path}'.");

            string Read(IndexEntry? e) => e == null ? "" : repo.Lookup<Blob>(e.Id).GetContentText();

            return (Read(c.Ancestor), Read(c.Ours), Read(c.Theirs));
        });

    public void ResolveConflict(string repoPath, string path, string mergedContent) =>
        ExecuteWithRepo(repoPath, repo =>
        {
            var fullPath = System.IO.Path.Combine(repo.Info.WorkingDirectory, path);
            var dir = System.IO.Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);      // subdirectory conflicts must work

            // UTF-8 without BOM (matches Git's expectations; avoids a spurious BOM diff).
            System.IO.File.WriteAllText(fullPath, mergedContent, new System.Text.UTF8Encoding(false));

            // Staging a conflicted path clears its stage-1/2/3 entries in the index.
            Commands.Stage(repo, path);
        });

    public bool HasUnresolvedConflicts(string repoPath) =>
        ExecuteWithRepo(repoPath, repo => repo.Index.Conflicts.Any());

    // Whole-file resolution: check out the chosen side into the working tree, then stage
    // (which clears the conflict entries). CLI checkout via RunGitChecked (no quoting bugs).
    public void ResolveFileWithSide(string repoPath, string path, ConflictSide side) =>
        ExecuteWithRepo(repoPath, repo =>
        {
            RunGitChecked(repoPath, "checkout", side == ConflictSide.Ours ? "--ours" : "--theirs", "--", path);
            Commands.Stage(repo, path);
        });

    // Removes a path from the merge (modify/delete "Delete file" action). git rm stages the deletion.
    public void RemoveFileFromMerge(string repoPath, string path) =>
        RunGitChecked(repoPath, "rm", "--", path);

    public CurrentOperation GetCurrentOperation(string repoPath) =>
        ExecuteWithRepo(repoPath, repo => repo.Info.CurrentOperation);

    public void AbortMerge(string repoPath) => RunGitChecked(repoPath, "merge", "--abort");

    public bool IsRebasing(string repoPath)
    {
        return System.IO.Directory.Exists(GitDirPath(repoPath, "rebase-merge")) ||
               System.IO.Directory.Exists(GitDirPath(repoPath, "rebase-apply"));
    }

    public void ContinueRebase(string repoPath)
    {
        if (System.IO.File.Exists(GitDirPath(repoPath, "rebase-merge", "interactive")))
        {
            // `git rebase --continue` can re-invoke GIT_EDITOR for the reword/squash steps
            // that come after the pause, so we must hand it the SAME message queue used at
            // start (keyed by SHA). Pointing it anywhere else silently loses those messages.
            var env = new System.Collections.Generic.Dictionary<string, string>
            {
                ["GIT_EDITOR"] = $"{GetSelfInvocationPrefix()} --rebase-msg \"{RebaseMsgQueueDir(repoPath)}\""
            };
            var (code, _, errStr) = RunGit(repoPath, env, default, "rebase", "--continue");

            if (code != 0)
            {
                if (ExecuteWithRepo(repoPath, repo => repo.Index.Conflicts.Any()))
                    throw new MergeConflictException("Merge conflicts detected! Resolve the conflicts in the Diff Viewer, save the files to stage them, then click 'Continue Rebase' again.");

                var stoppedShaPath = GitDirPath(repoPath, "rebase-merge", "stopped-sha");
                if (System.IO.File.Exists(stoppedShaPath))
                    throw new GitOperationException($"Rebase paused at {System.IO.File.ReadAllText(stoppedShaPath).Trim()} for editing. Amend your changes, then click 'Continue Rebase'.");

                throw new GitOperationException($"Continue rebase failed: {errStr}");
            }

            // Rebase finished — the queue is no longer needed.
            if (!System.IO.Directory.Exists(GitDirPath(repoPath, "rebase-merge")))
            {
                try { System.IO.Directory.Delete(RebaseMsgQueueDir(repoPath), true); } catch { }
            }
            return;
        }

        ExecuteWithRepo(repoPath, repo =>
        {
            var signature = GetSignature(repo);
            var identity = new LibGit2Sharp.Identity(signature.Name, signature.Email);

            var rebaseResult = repo.Rebase.Continue(identity, new RebaseOptions());

            // If it's Complete or Conflicts, that is a successful operation.
            // Complete = Rebase finished!
            // Conflicts = Successfully committed the previous resolution, but hit a new conflict in a subsequent commit.
            if (rebaseResult.Status != RebaseStatus.Complete && rebaseResult.Status != RebaseStatus.Conflicts)
            {
                throw new GitOperationException($"Rebase stopped with status: {rebaseResult.Status}");
            }
        });
    }

    public void AbortRebase(string repoPath)
    {
        if (System.IO.File.Exists(GitDirPath(repoPath, "rebase-merge", "interactive")))
        {
            var (code, _, errStr) = RunGit(repoPath, "rebase", "--abort");
            // Whether or not abort reports success, the message queue is now stale.
            try { System.IO.Directory.Delete(RebaseMsgQueueDir(repoPath), true); } catch { }
            if (code != 0) throw new GitOperationException($"Abort rebase failed: {errStr}");
            return;
        }

        ExecuteWithRepo(repoPath, repo =>
        {
            repo.Rebase.Abort();
        });
    }

    public void UpdateProject(string repoPath)
    {
        Fetch(repoPath, prune: true);

        // Fast-forward every non-current tracking branch to its upstream (pure ref
        // updates, no working-tree changes), and note whether the checked-out branch
        // also needs updating so it can be pulled through the normal path below.
        bool pullCurrentBranch = ExecuteWithRepo(repoPath, repo =>
        {
            bool needsPull = false;
            var localBranches = repo.Branches.Where(b => !b.IsRemote && b.TrackedBranch != null).ToList();
            foreach (var branch in localBranches)
            {
                try
                {
                    if (branch.IsCurrentRepositoryHead)
                    {
                        // The checked-out branch touches the working tree — defer it to
                        // the CLI Pull path (below), which handles SSH/HTTPS and conflicts.
                        needsPull = true;
                        continue;
                    }

                    var trackingBranch = branch.TrackedBranch;

                    // A just-created local branch or an unborn upstream can have a
                    // null Tip; skip fast-forward evaluation rather than crashing.
                    if (branch.Tip == null || trackingBranch.Tip == null) continue;

                    var baseCommit = repo.ObjectDatabase.FindMergeBase(branch.Tip, trackingBranch.Tip);

                    // If it's a fast-forward (local branch hasn't diverged)
                    if (baseCommit?.Id == branch.Tip.Id && branch.Tip.Id != trackingBranch.Tip.Id)
                    {
                        repo.Refs.UpdateTarget(repo.Refs[branch.CanonicalName], trackingBranch.Tip.Id);
                    }
                }
                catch (LibGit2SharpException ex)
                {
                    // Surface the failure as a typed error (with branch context)
                    // rather than swallowing non-conflict failures silently.
                    throw new GitOperationException($"Failed to update branch '{branch.FriendlyName}': {ex.Message}", ex);
                }
            }
            return needsPull;
        });

        // Pull the checked-out branch via the CLI path so it works for SSH- and
        // HTTPS-cloned remotes alike (and raises MergeConflictException on conflicts).
        if (pullCurrentBranch)
            Pull(repoPath);
    }

    // Single hardened, cross-platform git runner. Replaces the old Windows-only
    // ExecuteGitCli (cmd.exe + "|| pause", which popped a visible terminal, broke
    // on macOS/Linux, and discarded stderr) and the near-duplicate silent runner.
    //
    // Arguments are passed via ArgumentList (never a single command string) so
    // there is no shell quoting/injection surface — each element is one argv slot.
    internal static (int Code, string Out, string Err) RunGit(string repoPath, params string[] args)
        => RunGit(repoPath, null, default, args);

    /// <summary>
    /// Resolves the git directory holding <paramref name="repoPath"/>'s <b>per-worktree</b> state.
    /// <b>Never build one with <c>Path.Combine(repoPath, ".git", …)</c>.</b>
    /// <para>
    /// In the main working tree that is <c>&lt;repo&gt;/.git/</c>, but in a <i>linked</i> worktree
    /// (<c>git worktree add</c> — which both the Worktrees window and the agent platform's
    /// <c>WorktreeManager</c> create) <c>.git</c> is a FILE holding a <c>gitdir:</c> pointer, and
    /// the per-worktree state — <c>HEAD</c>, <c>index</c>, <c>MERGE_HEAD</c>, <c>MERGE_MSG</c>,
    /// <c>rebase-merge/</c>, <c>rebase-apply/</c> — lives under
    /// <c>&lt;main&gt;/.git/worktrees/&lt;name&gt;/</c>. Combining a path against a file silently
    /// yields one that can never exist, so every state check answers "no", forever.
    /// </para>
    /// <c>Repository.Discover</c> performs exactly git's own resolution, including the
    /// <c>.git</c>-file indirection and the bare-repository case.
    /// </summary>
    internal static string ResolveGitDir(string repoPath)
    {
        // Discover returns a trailing-separator path, or null when repoPath is not inside a
        // repository — in which case fall back to the naive layout, so probing a non-repository
        // path behaves exactly as it did before.
        var discovered = Repository.Discover(repoPath);
        return string.IsNullOrEmpty(discovered)
            ? System.IO.Path.Combine(repoPath, ".git")
            : discovered.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Resolves the <b>common</b> git directory shared by every worktree — where <c>refs/</c>,
    /// <c>objects/</c> and <c>packed-refs</c> live. For the main working tree this is the same
    /// directory <see cref="ResolveGitDir"/> returns; for a linked worktree the per-worktree
    /// gitdir carries a <c>commondir</c> file pointing back at <c>&lt;main&gt;/.git</c>.
    /// <para>The distinction matters for anything ref-shaped: a commit made <i>in</i> a worktree
    /// updates <c>refs/heads/&lt;branch&gt;</c> in the common dir, not in the per-worktree one.</para>
    /// </summary>
    internal static string ResolveCommonGitDir(string gitDir)
    {
        var pointer = System.IO.Path.Combine(gitDir, "commondir");
        if (!System.IO.File.Exists(pointer)) return gitDir;

        try
        {
            var target = System.IO.File.ReadAllText(pointer).Trim();
            if (target.Length == 0) return gitDir;
            return System.IO.Path.GetFullPath(
                System.IO.Path.IsPathRooted(target) ? target : System.IO.Path.Combine(gitDir, target));
        }
        catch
        {
            // An unreadable commondir must never take a caller down; the per-worktree dir is
            // still a usable answer for everything that is not ref-shaped.
            return gitDir;
        }
    }

    /// <summary>Path to a per-worktree state file/dir inside the resolved gitdir
    /// (see <see cref="ResolveGitDir"/>).</summary>
    internal static string GitDirPath(string repoPath, params string[] segments)
        => System.IO.Path.Combine(new[] { ResolveGitDir(repoPath) }.Concat(segments).ToArray());

    // Directory (inside the resolved gitdir) where the interactive-rebase message queue lives.
    // It is deliberately inside the gitdir so it survives conflict/edit pauses and is reused by
    // ContinueRebase — the reword/squash messages are keyed by original commit SHA. For a linked
    // worktree that is <main>/.git/worktrees/<name>/, next to the rebase-merge state it pairs with.
    internal static string RebaseMsgQueueDir(string repoPath)
        => GitDirPath(repoPath, "mainguard-rebase-msg");

    // Test seam: when set, this quoted command prefix is used verbatim instead of
    // deriving one from the running process. Integration tests set it to the built
    // the app head (whose --rebase-editor/--rebase-msg argv modes perform the shim
    // copies and exit before Avalonia init); under `dotnet test` the running process
    // is the test host, which knows nothing of those modes. Never set in production.
    internal static string? SelfInvocationOverride;

    // Builds the quoted command prefix that re-invokes THIS application as a git
    // sequence/message editor. Handles the framework-dependent case where the process
    // host is `dotnet` (MainModule points at the runtime, not our app) by expanding to
    // `"dotnet" "<app>.dll"`, and works for the single-file/apphost case on every OS.
    internal static string GetSelfInvocationPrefix()
    {
        if (!string.IsNullOrEmpty(SelfInvocationOverride))
            return SelfInvocationOverride;

        var host = Environment.ProcessPath;
        if (string.IsNullOrEmpty(host))
        {
            try { host = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName; }
            catch { host = null; }
        }

        var entryDll = System.Reflection.Assembly.GetEntryAssembly()?.Location;

        if (!string.IsNullOrEmpty(host))
        {
            var hostName = System.IO.Path.GetFileNameWithoutExtension(host);
            if (string.Equals(hostName, "dotnet", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(entryDll))
            {
                return $"\"{host}\" \"{entryDll}\"";
            }
            return $"\"{host}\"";
        }

        if (!string.IsNullOrEmpty(entryDll))
            return $"\"dotnet\" \"{entryDll}\"";

        // Last-resort sentinel — unreachable for a running process (which always has a ProcessPath or
        // MainModule). Name the running head's OWN assembly (Mainguard.Client.App / Mainguard.Pro.App)
        // rather than hard-code a product exe: the shared Mainguard.Git layer never names a specific head.
        return $"\"{System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "Mainguard"}\"";
    }

    internal static (int Code, string Out, string Err) RunGit(
        string repoPath, IReadOnlyDictionary<string, string>? environment, System.Threading.CancellationToken ct, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Never let git block on an interactive credential/terminal prompt: there
        // is no TTY behind the GUI, so a prompt would hang the operation forever.
        // Set as the default first so an explicit override in `environment` wins.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (environment != null)
        {
            foreach (var kv in environment) psi.Environment[kv.Key] = kv.Value;
        }

        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.Start(psi)
                ?? throw new GitOperationException("Failed to launch git. Is Git installed and on the PATH?");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // git not on PATH (or not executable) surfaces as a Win32Exception —
            // wrap it so callers always receive a typed GitOperationException.
            throw new GitOperationException("Failed to launch git. Is Git installed and on the PATH?", ex);
        }

        using (process)
        using (var reg = ct.Register(() => { try { process.Kill(true); } catch { } }))
        {
            // Close stdin so any prompt that slips past GIT_TERMINAL_PROMPT hits EOF
            // and fails fast instead of waiting for input that will never come.
            process.StandardInput.Close();

            // Read both streams concurrently to avoid a pipe-buffer deadlock.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            ct.ThrowIfCancellationRequested();

            // Stderr flows into typed exceptions and from there into UI error
            // surfaces. When a remote URL carries embedded credentials
            // (https://user:token@host/…), git echoes it verbatim in messages like
            // "fatal: unable to access '<url>'" — mask the userinfo before any
            // caller can leak it (G-4). Stdout is parsed by callers and is left
            // untouched.
            return (process.ExitCode, stdoutTask.Result, RedactUrlCredentials(stderrTask.Result));
        }
    }

    // Masks the userinfo of every URL occurring anywhere in free text
    // ("https://user:token@host/…" → "https://***@host/…"). Applied to git's
    // stderr, whose text reaches exception messages and UI error panels.
    internal static string RedactUrlCredentials(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("://", StringComparison.Ordinal))
            return text;
        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(\w[\w+.-]*://)([^/\s@]+)@",
            "$1***@");
    }

    // Joins args for an error message with any URL-embedded credentials masked,
    // so a token-bearing remote URL never leaks into a UI error surface or log.
    private static string RedactArgs(System.Collections.Generic.IEnumerable<string> args)
        => string.Join(' ', args.Select(RedactArg));

    private static string RedactArg(string arg)
    {
        var schemeIdx = arg.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            var schemeEnd = schemeIdx + 3;
            var at = arg.IndexOf('@', schemeEnd);
            if (at > schemeEnd)
                return string.Concat(arg.AsSpan(0, schemeEnd), "***@", arg.AsSpan(at + 1));
        }
        return arg;
    }

    // Runs git and throws a typed exception (with captured stderr) on failure so
    // the app can show a real error panel instead of a terminal pop-up.
    private void RunGitChecked(string repoPath, params string[] args)
        => RunGitChecked(repoPath, null, args);

    private void RunGitChecked(string repoPath, IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        var (code, _, err) = RunGit(repoPath, environment, default, args);
        if (code == 0) return;

        if (err.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthenticationRequiredException(err);
        }

        var message = string.IsNullOrWhiteSpace(err)
            ? $"git {RedactArgs(args)} failed with exit code {code}."
            : err;
        throw new GitOperationException(message);
    }

    /// <summary>
    /// Turns a refused SSH key exchange into a message the user can act on, by looking at the key
    /// Mainguard would have used and whether a passphrase for it is in the keyring.
    ///
    /// <para>The three cases have three different remedies, and telling them apart is the whole point:
    /// no key at all (generate or add one), an encrypted key with a stored passphrase (the passphrase is
    /// stored but the running <c>ssh</c> never asked Mainguard for it — the key must be loaded into the
    /// agent), and a key with no stored passphrase (it is either locked or not trusted by the host).</para>
    /// </summary>
    private static SshAuthenticationException BuildSshAuthenticationException(Exception inner)
    {
        string? keyPath = null;
        var hasStored = false;
        try
        {
            var ssh = new Mainguard.Git.Security.SshKeyService();
            // Same preference order as CredentialResolver.ResolveDefaultKey — the message must name the
            // key the operation would actually have used.
            var keys = ssh.ListKeys();
            var key = keys.FirstOrDefault(k => k.Name is "id_ed25519")
                ?? keys.FirstOrDefault(k => k.Name is "id_rsa")
                ?? keys.FirstOrDefault();
            if (key is not null)
            {
                keyPath = key.PrivateKeyPath;
                hasStored = !string.IsNullOrEmpty(ssh.GetPassphrase(key.PrivateKeyPath));
            }
        }
        catch
        {
            // Unreadable ~/.ssh or no keyring on this box: fall through to the generic message rather
            // than replacing an auth error with a filesystem one.
        }

        var message = keyPath is null
            ? "SSH authentication failed: no usable SSH key was found in ~/.ssh. "
              + "Open Settings → SSH Keys to generate one and add it to your host."
            : hasStored
                ? $"SSH authentication failed for {System.IO.Path.GetFileName(keyPath)}. Its passphrase is "
                  + "saved in Mainguard, but the system ssh agent was not holding the unlocked key — "
                  + "run ssh-add for it, or check the key is added to your host in Settings → SSH Keys."
                : $"SSH authentication failed for {System.IO.Path.GetFileName(keyPath)}. The key is either "
                  + "passphrase-protected with no saved passphrase, or not accepted by the host. "
                  + "Open Settings → SSH Keys to save its passphrase or copy the public key to your host.";

        return new SshAuthenticationException(message, inner)
        {
            KeyPath = keyPath,
            HasStoredPassphrase = hasStored,
        };
    }

    /// <summary>
    /// Runs a git command against the given remote, injecting a token via git's
    /// credential mechanism when one is available for that remote's host. The
    /// token is passed in the child process ENVIRONMENT and read by an inline
    /// credential helper — it never appears in argv/process listings, shell
    /// history, or the remote URL (the pre-1.7 leak vector).
    /// </summary>
    private void RunGitCheckedAuthenticated(string repoPath, string remoteName, params string[] args)
    {
        var url = GetRemoteUrl(repoPath, remoteName);
        var (host, kind) = Mainguard.Git.Security.GitHostDetector.Detect(url ?? "");
        var token = GetTokenForRemote(repoPath, remoteName);

        if (string.IsNullOrEmpty(token))
        {
            // No stored token: let git use its own credential helpers / prompts,
            // but never block on an interactive prompt in the GUI.
            try
            {
                RunGitChecked(repoPath, new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" }, args);
            }
            catch (AuthenticationRequiredException ex)
            {
                // An SSH-form remote that was refused is NOT a "sign in / store a token" problem, and
                // routing it to the PAT dialog (which is what the generic branch below does) offers a
                // remedy that cannot work. git's stderr for a locked or untrusted key is
                // "Permission denied (publickey)", which the classifier in RunGitChecked matches as a
                // generic auth failure — so every SSH key problem used to arrive at the token dialog.
                if (Mainguard.Git.Security.SshKeyService.IsSshRemote(url))
                {
                    throw BuildSshAuthenticationException(ex);
                }

                // T-14: an unknown-host-no-token failure carries the host so the UI can
                // route to the per-host PAT dialog instead of a generic auth notice.
                throw new AuthenticationRequiredException(ex.Message, string.IsNullOrEmpty(host) ? null : host, ex);
            }
            return;
        }

        var username = Mainguard.Git.Security.GitHostDetector.UsernameForToken(kind);
        // Inline helper echoes credentials from $MAINGUARD_TOKEN; only the helper
        // script text is in argv, never the secret.
        var helper = $"!f() {{ echo \"username={username}\"; echo \"password=$MAINGUARD_TOKEN\"; }}; f";
        var fullArgs = new List<string> { "-c", "credential.helper=", "-c", $"credential.helper={helper}" };

        // libgit2 has no SSH transport, so remote ops run through the git CLI. If the
        // repo was cloned over SSH (git@host:… / ssh://…) but we hold an HTTPS token
        // for that host, transparently rewrite the remote to HTTPS for this one command
        // so token auth works without an SSH key. Scoped via -c insteadOf — the stored
        // remote URL is left untouched. Only done when a token exists, so genuine
        // SSH-key setups (no token) still use SSH via the CLI.
        var httpsUrl = Mainguard.Git.Security.GitHostDetector.ToHttpsUrl(url ?? "");
        if (!string.IsNullOrEmpty(httpsUrl) && !string.Equals(httpsUrl, url, StringComparison.Ordinal))
        {
            fullArgs.Add("-c");
            fullArgs.Add($"url.{httpsUrl}.insteadOf={url}");
        }

        fullArgs.AddRange(args);

        var env = new Dictionary<string, string>
        {
            ["MAINGUARD_TOKEN"] = token,
            ["GIT_TERMINAL_PROMPT"] = "0"
        };
        RunGitChecked(repoPath, env, fullArgs.ToArray());
    }

    public (int? Ahead, int? Behind) GetAheadBehind(string repoPath)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var branch = repo.Head;
            if (branch.TrackedBranch == null) return (null, null);

            return (branch.TrackingDetails.AheadBy, branch.TrackingDetails.BehindBy);
        });
    }

    public IEnumerable<GitCommitItem> GetRecentCommits(string repoPath, int skip, int take, CommitSearchFilter? searchFilter = null)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            IEnumerable<Commit> query;

            if (searchFilter?.FilePaths != null && searchFilter.FilePaths.Count == 1)
            {
                // Single path: LibGit2Sharp's history-following walk is the most
                // efficient option and streams lazily.
                query = repo.Commits.QueryBy(searchFilter.FilePaths.First()).Select(e => e.Commit);
            }
            else
            {
                var filter = new CommitFilter
                {
                    SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time
                };

                if (!string.IsNullOrEmpty(searchFilter?.BranchName))
                {
                    var branch = repo.Branches[searchFilter.BranchName];
                    if (branch != null)
                    {
                        filter.IncludeReachableFrom = branch;
                    }
                }
                else if (searchFilter?.CurrentBranchOnly == true)
                {
                    // Restrict to HEAD (+ its upstream, so the ahead/behind picture stays visible).
                    var includes = new List<object>();
                    if (repo.Head.Tip != null) includes.Add(repo.Head.Tip);
                    var upstreamTip = repo.Head.TrackedBranch?.Tip;
                    if (upstreamTip != null) includes.Add(upstreamTip);
                    if (includes.Count > 0) filter.IncludeReachableFrom = includes;
                }
                query = repo.Commits.QueryBy(filter);

                // Multi-path: run a SINGLE lazy walk (already sorted topo+time)
                // and test each commit's tree changes for membership, instead of
                // materializing the full history of every path and re-sorting it
                // in memory (which broke pagination and desynced the graph fringe).
                if (searchFilter?.FilePaths != null && searchFilter.FilePaths.Count > 1)
                {
                    var paths = new HashSet<string>(searchFilter.FilePaths, StringComparer.Ordinal);
                    query = query.Where(c => CommitTouchesAnyPath(repo, c, paths));
                }
            }

            if (!string.IsNullOrEmpty(searchFilter?.Text))
            {
                var text = searchFilter.Text;
                query = query.Where(c =>
                    c.Message.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    c.Sha.StartsWith(text, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(searchFilter?.Author))
            {
                var author = searchFilter.Author;
                query = query.Where(c =>
                    c.Author.Name.Contains(author, StringComparison.OrdinalIgnoreCase) ||
                    c.Author.Email.Contains(author, StringComparison.OrdinalIgnoreCase));
            }

            if (searchFilter?.DateFrom.HasValue == true)
            {
                query = query.Where(c => c.Author.When.DateTime >= searchFilter.DateFrom.Value);
            }

            if (searchFilter?.DateTo.HasValue == true)
            {
                query = query.Where(c => c.Author.When.DateTime <= searchFilter.DateTo.Value);
            }

            return query.Skip(skip).Take(take).Select(c => new GitCommitItem
            {
                Sha = c.Sha,
                ParentShas = c.Parents.Select(p => p.Sha).ToList(),
                Message = c.Message,
                MessageShort = c.MessageShort,
                AuthorName = c.Author.Name,
                AuthorEmail = c.Author.Email,
                CommitDate = c.Author.When
            }).ToList();
        });
    }

    /// <summary>
    /// True if <paramref name="commit"/> added, modified, deleted or renamed any
    /// of <paramref name="paths"/> relative to its first parent (or the empty
    /// tree for a root commit).
    /// </summary>
    private static bool CommitTouchesAnyPath(Repository repo, Commit commit, HashSet<string> paths)
    {
        var parentTree = commit.Parents.FirstOrDefault()?.Tree;
        var changes = repo.Diff.Compare<TreeChanges>(parentTree, commit.Tree);
        foreach (var change in changes)
        {
            if (paths.Contains(change.Path) ||
                (!string.IsNullOrEmpty(change.OldPath) && paths.Contains(change.OldPath)))
            {
                return true;
            }
        }
        return false;
    }

    public IEnumerable<GitBranchItem> GetBranches(string repoPath)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var branches = new System.Collections.Generic.List<GitBranchItem>();
            foreach (var branch in repo.Branches)
            {
                branches.Add(new GitBranchItem
                {
                    Name = branch.FriendlyName,
                    FriendlyName = branch.FriendlyName,
                    IsRemote = branch.IsRemote,
                    IsCurrentRepositoryHead = branch.IsCurrentRepositoryHead,
                    TipSha = branch.Tip?.Sha ?? string.Empty
                });
            }
            return branches;
        });
    }

    public void CheckoutBranch(string repoPath, string branchName)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.CheckoutBranch, $"Checkout {branchName}");
        ExecuteWithRepo(repoPath, repo =>
        {
            var branch = repo.Branches[branchName];
            if (branch == null) throw new GitOperationException($"Branch {branchName} not found.");

            if (branch.IsRemote)
            {
                // For remote branches, we must create a local tracking branch and check that out instead.
                // Capture the remote branch's canonical name and tip up front — we
                // reassign `branch` to the local branch below and must not re-index
                // the remote afterwards (the original code re-looked it up).
                var remoteCanonicalName = branch.CanonicalName;
                var remoteTip = branch.Tip;
                var localBranchName = branchName.Contains("/") ? branchName.Substring(branchName.IndexOf("/") + 1) : branchName;

                // Check if local branch already exists
                var existingLocal = repo.Branches[localBranchName];
                if (existingLocal != null)
                {
                    branch = existingLocal;
                }
                else
                {
                    var newLocal = repo.CreateBranch(localBranchName, remoteTip);
                    repo.Branches.Update(newLocal, b => b.TrackedBranch = remoteCanonicalName);
                    branch = newLocal;
                }
            }

            Commands.Checkout(repo, branch);
        });
    }

    public void CreateBranch(string repoPath, string branchName, string baseBranchName, bool checkout)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.CreateBranch, $"Create branch {branchName}");
        ExecuteWithRepo(repoPath, repo =>
        {
            var baseBranch = string.IsNullOrEmpty(baseBranchName) ? repo.Head : repo.Branches[baseBranchName];
            if (baseBranch == null) throw new GitOperationException($"Base branch '{baseBranchName}' not found.");
            if (baseBranch.Tip == null)
                throw new GitOperationException("Cannot create a branch from a base that has no commits yet. Make an initial commit first.");

            var newBranch = repo.CreateBranch(branchName, baseBranch.Tip);
            if (checkout)
            {
                Commands.Checkout(repo, newBranch);
            }
        });
    }

    public void RenameBranch(string repoPath, string oldName, string newName)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.RenameBranch, $"Rename branch {oldName} → {newName}");
        ExecuteWithRepo(repoPath, repo =>
        {
            var branch = repo.Branches[oldName];
            if (branch == null) throw new GitOperationException($"Branch '{oldName}' not found.");
            repo.Branches.Rename(branch, newName);
        });
    }

    public void PushBranch(string repoPath, string branchName)
        // Pushes the branch to the resolved remote and sets it as upstream (see Push).
        => PushSetUpstream(repoPath, ResolveRemoteName(repoPath), branchName);

    /// <summary>
    /// Deletes a local or remote branch. <paramref name="force"/> is the <c>git branch -d</c> ⇄
    /// <c>-D</c> switch: without it an unmerged <b>local</b> branch is refused with a typed
    /// <see cref="BranchNotMergedException"/> rather than silently orphaning its commits.
    /// (Remote deletes are a push and have no local merged-check to make.)
    /// </summary>
    public void DeleteBranch(string repoPath, string branchName, bool force = false)
    {
        // Both pre-flight questions — is this a remote branch, and is a local one safe to
        // delete — are answered in one short-lived handle BEFORE the journal scope opens, so a
        // refused delete never records a journal entry for an operation that did not happen.
        var (isRemote, notMergedReason) = ExecuteWithRepo(repoPath, repo =>
        {
            var branch = repo.Branches[branchName];
            if (branch == null) return (false, (string?)null);
            if (branch.IsRemote || force) return (branch.IsRemote, (string?)null);
            return (false, IsSafeToDelete(repo, branch)
                ? null
                : $"Branch '{branchName}' is not fully merged — deleting it would leave its commits " +
                  "unreachable. Merge it first, or delete it anyway to discard the work.");
        });

        if (notMergedReason != null) throw new BranchNotMergedException(branchName, notMergedReason);

        // A local-branch delete is undoable (the branch ref + upstream config is restored);
        // a remote-branch delete pushes the deletion, which the journal cannot reverse.
        using var op = _journal.BeginOperation(repoPath, JournalKinds.DeleteBranch, $"Delete branch {branchName}",
            undoBlockedReason: isRemote ? "Deleting a remote branch cannot be undone from the journal." : null);
        ExecuteWithRepo(repoPath, repo =>
        {
            var branch = repo.Branches[branchName];
            if (branch == null) throw new GitOperationException($"Branch {branchName} not found.");

            if (branch.IsRemote)
            {
                // Fallback to CLI to delete remote branch safely
                var remoteName = branch.RemoteName;
                var remoteBranchName = branchName.Replace($"{remoteName}/", "");
                RunGitCheckedAuthenticated(repoPath, remoteName, "push", remoteName, "--delete", remoteBranchName);
            }
            else
            {
                repo.Branches.Remove(branch);
            }
        });
    }

    /// <summary>
    /// git's own <c>-d</c> rule: a branch may be deleted without force when its tip is already
    /// contained in HEAD, or in the upstream it tracks (published work is not orphaned by
    /// dropping the local ref). An empty branch has nothing to orphan.
    /// </summary>
    private static bool IsSafeToDelete(Repository repo, Branch branch)
    {
        var tip = branch.Tip;
        if (tip == null) return true;

        var upstreamTip = branch.TrackedBranch?.Tip;
        if (upstreamTip != null && IsAncestorOrSame(repo, tip, upstreamTip)) return true;

        var headTip = repo.Head.Tip;
        return headTip != null && IsAncestorOrSame(repo, tip, headTip);
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="descendant"/> or one of
    /// its ancestors — i.e. <paramref name="descendant"/> already contains it.</summary>
    private static bool IsAncestorOrSame(Repository repo, Commit candidate, Commit descendant)
        => candidate.Id == descendant.Id
           || repo.ObjectDatabase.FindMergeBase(candidate, descendant)?.Id == candidate.Id;

    public bool HasUncommittedChanges(string repoPath)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var status = repo.RetrieveStatus();
            return status.IsDirty;
        });
    }

    // ---- Tags (T-05) -------------------------------------------------------
    // CRUD + checkout via LibGit2Sharp; push/delete-remote try libgit2 first and
    // fall back to the git CLI on transport failure (mirrors Push). Local bare
    // remotes push cleanly via libgit2 with no credentials, so the fixture tests
    // need no network and no git CLI.

    public IEnumerable<GitTagItem> GetTags(string repoPath)
    {
        return ExecuteWithRepo(repoPath, repo => repo.Tags.Select(tag =>
        {
            // Peel to the underlying commit: annotated -> PeeledTarget, lightweight -> Target.
            var commit = tag.PeeledTarget as Commit ?? tag.Target as Commit;
            if (commit == null) return null; // tag points at a tree/blob -> skip defensively
            return new GitTagItem
            {
                Name = tag.FriendlyName,
                TargetSha = commit.Sha,
                IsAnnotated = tag.IsAnnotated,
                Message = tag.IsAnnotated ? tag.Annotation.Message : null,
                TaggerName = tag.IsAnnotated ? tag.Annotation.Tagger?.Name : null,
            };
        }).Where(t => t != null).Select(t => t!).ToList());
    }

    public void CreateTag(string repoPath, string name, string targetSha, string? message)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.CreateTag, $"Create tag {name}");
        // Signed annotated tag: only the git CLI can drive gpg/ssh signing. Validate the same way
        // the libgit2 path does (resolving the target's full SHA), close the handle, then shell out
        // to `git tag -s`. A lightweight tag (message == null) has no object to sign, so it always
        // stays on the libgit2 path below.
        if (Preferences.SignCommits && message != null)
        {
            var resolvedSha = ExecuteWithRepo(repoPath, repo =>
            {
                ValidateNewTagName(repo, name);
                var target = repo.Lookup<Commit>(targetSha)
                    ?? throw new GitOperationException($"No commit found for '{targetSha}'.");
                GetSignature(repo); // validate identity up front (typed throw before shelling out)
                return target.Sha;
            });
            ApplySigningConfig(repoPath);
            RunGitChecked(repoPath, "tag", "-s", "-m", message, name, resolvedSha);
            return;
        }

        ExecuteWithRepo(repoPath, repo =>
        {
            ValidateNewTagName(repo, name);
            var target = repo.Lookup<Commit>(targetSha)
                ?? throw new GitOperationException($"No commit found for '{targetSha}'.");

            try
            {
                if (message != null)
                    repo.Tags.Add(name, target, GetSignature(repo), message); // annotated (G-3)
                else
                    repo.Tags.Add(name, target);                              // lightweight
            }
            catch (LibGit2SharpException ex)
            {
                // Never let a raw libgit2 error surface to the UI (rejection trigger).
                throw new GitOperationException($"Failed to create tag '{name}': {ex.Message}");
            }
        });
    }

    // Validates a would-be tag name before any mutation: name-validity -> duplicate.
    // libgit2's IsValidName accepts option-like leading-dash names that git itself rejects,
    // so guard that explicitly.
    private static void ValidateNewTagName(Repository repo, string name)
    {
        if (string.IsNullOrEmpty(name)
            || name.StartsWith('-')
            || !Reference.IsValidName("refs/tags/" + name))
            throw new GitOperationException($"'{name}' is not a valid tag name.");
        if (repo.Tags[name] != null)
            throw new GitOperationException($"A tag named '{name}' already exists.");
    }

    public void DeleteTag(string repoPath, string name)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.DeleteTag, $"Delete tag {name}");
        ExecuteWithRepo(repoPath, repo =>
        {
            if (repo.Tags[name] == null)
                throw new GitOperationException($"No tag named '{name}'.");
            repo.Tags.Remove(name);
        });
    }

    public void PushTag(string repoPath, string remoteName, string name)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var remote = repo.Network.Remotes[remoteName]
                ?? throw new RemoteNotFoundException($"No remote named '{remoteName}'.");
            try
            {
                repo.Network.Push(remote, $"refs/tags/{name}:refs/tags/{name}",
                    BuildRemoteTokenPushOptions(repoPath, remoteName));
            }
            catch (LibGit2SharpException)
            {
                // libgit2 has no SSH; fall back to the CLI (token injected via env).
                RunGitCheckedAuthenticated(repoPath, remoteName, "push", remoteName, $"refs/tags/{name}");
            }
        });
    }

    public void DeleteRemoteTag(string repoPath, string remoteName, string name)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var remote = repo.Network.Remotes[remoteName]
                ?? throw new RemoteNotFoundException($"No remote named '{remoteName}'.");
            try
            {
                // Empty source refspec deletes the remote ref.
                repo.Network.Push(remote, $":refs/tags/{name}",
                    BuildRemoteTokenPushOptions(repoPath, remoteName));
            }
            catch (LibGit2SharpException)
            {
                RunGitCheckedAuthenticated(repoPath, remoteName, "push", remoteName, "--delete", $"refs/tags/{name}");
            }
        });
    }

    public void CheckoutTag(string repoPath, string name)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var tag = repo.Tags[name]
                ?? throw new GitOperationException($"No tag named '{name}'.");
            var commit = (tag.PeeledTarget as Commit) ?? (tag.Target as Commit)
                ?? throw new GitOperationException($"Tag '{name}' does not point to a commit.");
            Commands.Checkout(repo, commit); // detached HEAD at the peeled commit — never a branch
        });
    }

    // PushOptions carrying a credentials provider for the remote's host (T-14). For an
    // HTTP(S) remote this is a token-based UsernamePasswordCredentials; for an SSH-form
    // remote it is SshUserKeyCredentials (key paths + keyring passphrase). For a local
    // bare remote nothing resolves and the callback returns null, so libgit2 pushes over
    // local transport unauthenticated (offline-fixture friendly).
    private PushOptions BuildRemoteTokenPushOptions(string repoPath, string remoteName)
    {
        var url = GetRemoteUrl(repoPath, remoteName);
        return new PushOptions { CredentialsProvider = GetCredentialsProvider(url) };
    }

    /// <summary>
    /// Builds the LibGit2Sharp credentials handler for a remote URL (T-14) via
    /// <see cref="Mainguard.Git.Security.CredentialResolver"/> (single source, so a secret
    /// never enters the URL/argv). Token/HTTP(S) remotes resolve to
    /// <see cref="UsernamePasswordCredentials"/>. SSH-form remotes resolve to
    /// <c>SshUserKeyCredentials</c> — but the pinned libgit2 build has no SSH transport,
    /// so SSH ops run through the git CLI (RunGitCheckedAuthenticated) with the system
    /// ssh/agent; here the SSH branch yields <c>DefaultCredentials</c>. Returns
    /// <c>DefaultCredentials</c> when nothing is available so local/anonymous transport works.
    /// </summary>
    internal LibGit2Sharp.Handlers.CredentialsHandler GetCredentialsProvider(string? url)
        => (_url, _user, _cred) =>
            Mainguard.Git.Security.CredentialResolver.Resolve(
                url,
                new Mainguard.Git.Security.SecureKeyring(),
                new Mainguard.Git.Security.SshKeyService()).Https
            ?? new DefaultCredentials();

    // ---- Remotes (T-10) ----------------------------------------------------
    // CRUD via LibGit2Sharp (repo.Network.Remotes). Names are validated and
    // duplicate/missing throw typed BEFORE any mutation, so the repo config is
    // never left half-edited. The three push options are CLI-driven because
    // libgit2 has no --force-with-lease support.

    public IReadOnlyList<GitRemoteItem> GetRemotes(string repoPath) =>
        ExecuteWithRepo(repoPath, repo => repo.Network.Remotes
            .Select(r => new GitRemoteItem
            {
                Name = r.Name,
                FetchUrl = r.Url,
                // Only surface a distinct push URL; equal push/fetch URLs are the norm.
                PushUrl = string.Equals(r.PushUrl, r.Url, StringComparison.Ordinal) ? null : r.PushUrl
            })
            .ToList());

    public void AddRemote(string repoPath, string name, string url)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            ValidateRemoteName(name);
            if (string.IsNullOrWhiteSpace(url))
                throw new GitOperationException("A remote URL is required.");
            if (repo.Network.Remotes[name] != null)
                throw new GitOperationException($"A remote named '{name}' already exists.");
            repo.Network.Remotes.Add(name, url);
        });
    }

    public void RemoveRemote(string repoPath, string name)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            if (repo.Network.Remotes[name] == null)
                throw new RemoteNotFoundException($"No remote named '{name}'.");
            repo.Network.Remotes.Remove(name);
        });
    }

    public void RenameRemote(string repoPath, string oldName, string newName)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            ValidateRemoteName(newName);
            if (repo.Network.Remotes[oldName] == null)
                throw new RemoteNotFoundException($"No remote named '{oldName}'.");
            if (!string.Equals(oldName, newName, StringComparison.Ordinal) && repo.Network.Remotes[newName] != null)
                throw new GitOperationException($"A remote named '{newName}' already exists.");
            repo.Network.Remotes.Rename(oldName, newName);
        });
    }

    public void SetRemoteUrl(string repoPath, string name, string url)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new GitOperationException("A remote URL is required.");
            if (repo.Network.Remotes[name] == null)
                throw new RemoteNotFoundException($"No remote named '{name}'.");
            repo.Network.Remotes.Update(name, r => r.Url = url);
        });
    }

    public string GetDefaultRemoteName(string repoPath) => ResolveRemoteName(repoPath);

    // Remote name rules: non-empty, no whitespace, no ".." path traversal, and a
    // valid ref component per git. Mirrors the tag/branch pre-mutation validation.
    private static void ValidateRemoteName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Any(char.IsWhiteSpace)
            || name.Contains("..", StringComparison.Ordinal)
            || name.StartsWith('-')
            || !Reference.IsValidName($"refs/remotes/{name}/HEAD"))
            throw new GitOperationException($"'{name}' is not a valid remote name.");
    }

    public void PushForceWithLease(string repoPath, string remoteName, string branchName) =>
        // --force-with-lease (never a bare --force): git refuses the push when the
        // remote advanced past the ref our remote-tracking branch last saw. That
        // stale-info rejection is the safety property T-10 requires.
        RunGitCheckedAuthenticated(repoPath, remoteName, "push", "--force-with-lease", remoteName, branchName);

    public void PushTags(string repoPath, string remoteName) =>
        RunGitCheckedAuthenticated(repoPath, remoteName, "push", remoteName, "--tags");

    public void PushSetUpstream(string repoPath, string remoteName, string branchName) =>
        RunGitCheckedAuthenticated(repoPath, remoteName, "push", "-u", remoteName, branchName);

    public void StashChanges(string repoPath, string message)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var signature = GetSignature(repo);
            repo.Stashes.Add(signature, message, LibGit2Sharp.StashModifiers.Default);
        });
    }

    public IEnumerable<GitStashItem> GetStashes(string repoPath)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var result = new List<GitStashItem>();
            int i = 0;
            foreach (var stash in repo.Stashes)
            {
                result.Add(new GitStashItem { Index = i, Message = stash.Message });
                i++;
            }
            return result;
        });
    }

    public void StashPush(string repoPath, string message)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.StashPush, "Stash changes");
        ExecuteWithRepo(repoPath, repo =>
        {
            var signature = GetSignature(repo);

            repo.Stashes.Add(signature, message, StashModifiers.Default);
        });
    }

    public void StashDrop(string repoPath, int stashIndex)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.StashDrop, $"Drop stash@{{{stashIndex}}}",
            undoBlockedReason: "Dropping a stash discards it and cannot be undone.");
        ExecuteWithRepo(repoPath, repo =>
        {
            repo.Stashes.Remove(stashIndex);
        });
    }

    public void StashPop(string repoPath, int stashIndex)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.StashPop, $"Pop stash@{{{stashIndex}}}",
            undoBlockedReason: "Popping a stash modifies the working tree and cannot be reliably undone.");
        // LibGit2Sharp lacks Apply/Pop natively, fallback to silent CLI
        RunGitChecked(repoPath, "stash", "pop", $"stash@{{{stashIndex}}}");
    }

    public void StashApply(string repoPath, int stashIndex)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.StashApply, $"Apply stash@{{{stashIndex}}}",
            undoBlockedReason: "Applying a stash modifies the working tree and cannot be reliably undone.");
        // LibGit2Sharp lacks Apply/Pop natively, fallback to silent CLI
        RunGitChecked(repoPath, "stash", "apply", $"stash@{{{stashIndex}}}");
    }



    // Worktrees (T-07). CLI-driven via the RunGit family — the libgit2 worktree API is a
    // locked "no" per the policy split (G-7). Parse only the --porcelain format.

    public IReadOnlyList<WorktreeItem> ListWorktrees(string repoPath)
    {
        var (code, output, err) = RunGit(repoPath, "worktree", "list", "--porcelain");
        if (code != 0) throw new GitOperationException($"git worktree list failed: {err}");
        return WorktreePorcelainParser.Parse(output);
    }

    public void AddWorktree(string repoPath, string worktreePath, string branchName, bool createBranch)
    {
        // git worktree add [-b <branch>] <path> [<branch>]
        var args = new List<string> { "worktree", "add" };
        if (createBranch)
        {
            args.Add("-b");
            args.Add(branchName);
            args.Add(worktreePath);
        }
        else
        {
            args.Add(worktreePath);
            args.Add(branchName);
        }
        RunGitChecked(repoPath, args.ToArray());
    }

    public void RemoveWorktree(string repoPath, string worktreePath, bool force)
    {
        var args = new List<string> { "worktree", "remove" };
        if (force) args.Add("--force");
        args.Add(worktreePath);
        RunGitChecked(repoPath, args.ToArray());
    }

    public void PruneWorktrees(string repoPath) => RunGitChecked(repoPath, "worktree", "prune");

    // Check out a PR / branch into a worktree (T-29). Reuses the T-07 AddWorktree; the PR-head fetch
    // goes through the authenticated CLI path (RunGitCheckedAuthenticated — token via env, never argv/URL).
    // The pure ref resolver is static/testable; the libgit2 worktree API stays a locked "no" (G-7).

    /// <summary>The conventional fetch ref for a host's PR/MR head — GitHub <c>pull/{n}/head</c>,
    /// GitLab <c>merge-requests/{n}/head</c>; any other host is a typed "not supported". Pure/testable.</summary>
    public static string PullRequestHeadRef(HostKind host, int number)
    {
        if (number <= 0)
            throw new GitOperationException("A pull request number must be a positive integer.");
        return host switch
        {
            HostKind.GitHub => $"pull/{number}/head",
            HostKind.GitLab => $"merge-requests/{number}/head",
            _ => throw new GitOperationException(
                $"Checking out a pull request locally isn't supported for this host ({host})."),
        };
    }

    // TODO(T-29 human-review): live PR checkout — the fetch + worktree-create mechanics are proven
    // offline over a local file:// fixture remote carrying a synthetic refs/pull/1/head; the only
    // deferred slice is the round-trip against a REAL GitHub PR (incl. a private-repo token fetch that
    // must not leak the secret). See the T-29 manual checklist in the User-Testing Guide.
    public async System.Threading.Tasks.Task<string> CheckoutPullRequestWorktree(
        string repoPath, int prNumber, string remoteName, string worktreePath, System.Threading.CancellationToken ct)
    {
        EnsureWorktreeTargetIsEmpty(worktreePath);

        // Resolve the host from the remote URL so we know the conventional PR-head ref to fetch.
        var url = GetRemoteUrl(repoPath, remoteName);
        var (_, kind) = Mainguard.Git.Security.GitHostDetector.Detect(url ?? "");
        var headRef = PullRequestHeadRef(kind, prNumber);   // throws typed for an unsupported host
        var localBranch = $"pr/{prNumber}";

        return await System.Threading.Tasks.Task.Run(() =>
        {
            // Fetch the PR head into local branch `pr/<n>` (force so a re-checkout refreshes it). The
            // authenticated path injects any stored token via git's credential env — never into argv/URL.
            RunGitCheckedAuthenticated(repoPath, remoteName, "fetch", remoteName, $"+{headRef}:refs/heads/{localBranch}");
            try
            {
                AddWorktree(repoPath, worktreePath, localBranch, createBranch: false);
            }
            catch
            {
                // Best-effort cleanup so a failed add (e.g. the branch is already checked out elsewhere)
                // never leaves a half-made worktree directory behind.
                TryCleanupWorktree(repoPath, worktreePath);
                throw;
            }
            return worktreePath;
        }, ct);
    }

    public string CheckoutBranchWorktree(string repoPath, string branchOrRef, string worktreePath)
    {
        EnsureWorktreeTargetIsEmpty(worktreePath);

        // Resolve the target to a checkout-able local branch. A remote-tracking ref (origin/feature)
        // needs a local tracking branch first — git can't check a remote-tracking ref into a worktree.
        var branchToCheckout = ExecuteWithRepo(repoPath, repo =>
        {
            var branch = repo.Branches[branchOrRef]
                ?? throw new GitOperationException($"Branch '{branchOrRef}' was not found.");

            if (!branch.IsRemote)
                return branchOrRef;

            var localName = LocalNameForRemoteBranch(branch);
            if (repo.Branches[localName] == null)
            {
                var created = repo.CreateBranch(localName, branch.Tip);
                repo.Branches.Update(created, u => u.TrackedBranch = branch.CanonicalName);
            }
            return localName;
        });

        AddWorktree(repoPath, worktreePath, branchToCheckout, createBranch: false);
        return worktreePath;
    }

    // The local branch name for a remote-tracking branch: its friendly name with the "<remote>/" prefix
    // stripped (origin/feature → feature). Falls back to the friendly name if the shape is unexpected.
    private static string LocalNameForRemoteBranch(Branch remoteBranch)
    {
        var friendly = remoteBranch.FriendlyName;
        var remote = remoteBranch.RemoteName;
        if (!string.IsNullOrEmpty(remote) && friendly.StartsWith(remote + "/", StringComparison.Ordinal))
            return friendly.Substring(remote.Length + 1);
        return friendly;
    }

    // A non-empty target is a typed refusal (nothing is created); a missing/empty dir is fine —
    // `git worktree add` creates it.
    private static void EnsureWorktreeTargetIsEmpty(string worktreePath)
    {
        if (Directory.Exists(worktreePath) && Directory.EnumerateFileSystemEntries(worktreePath).Any())
            throw new GitOperationException($"Target directory '{worktreePath}' is not empty.");
    }

    // Best-effort teardown of a partially-created worktree so a failure leaves the repo clean.
    private void TryCleanupWorktree(string repoPath, string worktreePath)
    {
        try { if (Directory.Exists(worktreePath)) RemoveWorktree(repoPath, worktreePath, force: true); } catch { }
        try { PruneWorktrees(repoPath); } catch { }
        try { if (Directory.Exists(worktreePath)) Directory.Delete(worktreePath, recursive: true); } catch { }
    }

    // LFS (T-17) internal seams. LfsService composes GitService so the security-sensitive
    // authenticated CLI path (token via env + redaction — T-14/G-4) lives in ONE audited place
    // rather than being duplicated in a second class. Local LFS ops use the plain checked runner;
    // network ops (lfs pull) resolve the default remote and go through the authenticated path.
    internal void RunGitCheckedForLfs(string repoPath, params string[] args)
        => RunGitChecked(repoPath, args);

    internal void RunGitAuthenticatedForLfs(string repoPath, params string[] args)
        => RunGitCheckedAuthenticated(repoPath, ResolveRemoteName(repoPath), args);

    // Submodules (T-16). Reads go through ExecuteWithRepo (repo.Submodules); every mutation is
    // CLI-driven via the git submodule porcelain — the libgit2 submodule mutation API is a
    // locked "no" per the policy split, exactly like worktrees. The rolled-up status is computed
    // by the pure SubmoduleStatusMapper so the flag semantics are interpreted in one tested place.

    public IReadOnlyList<SubmoduleItem> GetSubmodules(string repoPath)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var items = new List<SubmoduleItem>();
            foreach (var sm in repo.Submodules)
            {
                items.Add(new SubmoduleItem
                {
                    Path = sm.Path,
                    Url = sm.Url ?? "",
                    // The commit the superproject records for the submodule (index/HEAD gitlink).
                    HeadSha = sm.HeadCommitId?.Sha,
                    Status = SubmoduleStatusMapper.Map(sm.RetrieveStatus())
                });
            }
            // Stable, path-ordered so the panel doesn't reshuffle between refreshes.
            items.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return (IReadOnlyList<SubmoduleItem>)items;
        });
    }

    // Initialize + check out all submodules (recursively) to the recorded commits. Safe to run
    // repeatedly; it is the "get me set up" action after a fresh clone.
    public void UpdateSubmodules(string repoPath)
        => RunGitChecked(repoPath, "submodule", "update", "--init", "--recursive");

    // Advance a single submodule to the latest commit on its configured remote branch
    // (`--remote`), leaving the recorded pointer to be committed by the user afterward.
    public void UpdateSubmoduleRemote(string repoPath, string path)
        => RunGitChecked(repoPath, "submodule", "update", "--remote", "--", path);

    // Re-sync each submodule's remote URL from .gitmodules into its own config (after a URL edit).
    public void SyncSubmodules(string repoPath)
        => RunGitChecked(repoPath, "submodule", "sync", "--recursive");

    public string GetDiffAgainstCommit(string repoPath, string commitSha, string? filePath = null)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit == null) throw new GitOperationException($"Commit {commitSha} not found.");

            // filePath == null → whole-tree diff of the working tree against the commit.
            var patch = filePath == null
                ? repo.Diff.Compare<Patch>(commit.Tree, DiffTargets.WorkingDirectory)
                : repo.Diff.Compare<Patch>(commit.Tree, DiffTargets.WorkingDirectory, new[] { filePath });
            return patch?.Content ?? string.Empty;
        });
    }

    public string GetBranchDiffAgainstWorkingTree(string repoPath, string branchName)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var branch = repo.Branches[branchName];
            if (branch == null) throw new GitOperationException($"Branch {branchName} not found.");
            if (branch.Tip == null) throw new GitOperationException($"Branch '{branchName}' has no commits yet.");

            var patch = repo.Diff.Compare<Patch>(branch.Tip.Tree, DiffTargets.WorkingDirectory);
            return patch?.Content ?? string.Empty;
        });
    }

    public IEnumerable<string> GetCommitModifiedFiles(string repoPath, string commitSha)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit == null) return System.Linq.Enumerable.Empty<string>();

            if (!commit.Parents.Any())
            {
                var changes = repo.Diff.Compare<TreeChanges>(null, commit.Tree);
                return changes.Select(c => c.Path).ToList();
            }
            else
            {
                var parentCommit = commit.Parents.First();
                var changes = repo.Diff.Compare<TreeChanges>(parentCommit.Tree, commit.Tree);
                return changes.Select(c => c.Path).ToList();
            }
        });
    }

    public IEnumerable<string> GetBranchesContainingCommit(string repoPath, string commitSha)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit == null) return System.Linq.Enumerable.Empty<string>();

            var branches = new System.Collections.Generic.List<string>();
            foreach (var branch in repo.Branches)
            {
                // Skip unborn branches (no tip) — FindMergeBase would throw on null.
                if (branch.Tip == null) continue;

                var mergeBase = repo.ObjectDatabase.FindMergeBase(commit, branch.Tip);
                if (mergeBase != null && mergeBase.Sha == commit.Sha)
                {
                    branches.Add(branch.FriendlyName);
                }
            }
            return branches;
        });
    }
    public IReadOnlyDictionary<string, CommitSignatureInfo> GetSignatureStatuses(string repoPath, IReadOnlyList<string> shas)
    {
        // No commits selected → no work and, crucially, no `%G?` cost (T-15 invariant: an
        // unsigned repo with the signature column off never pays for verification).
        if (shas == null || shas.Count == 0)
            return new Dictionary<string, CommitSignatureInfo>();

        // Batch-read the whole visible range in one git invocation. `--no-walk` shows each named
        // commit without walking ancestry; the parser keys results by SHA so order is irrelevant.
        // `%G?` runs gpg/ssh verification per commit; GIT_TERMINAL_PROMPT=0 (inherited by RunGit)
        // keeps a missing agent/pinentry from hanging the read.
        var args = new List<string> { "log", "--no-walk", "--format=" + SignatureStatusParser.LogFormat };
        args.AddRange(shas);

        var (code, output, err) = RunGit(repoPath, args.ToArray());
        if (code != 0)
            throw new GitOperationException(string.IsNullOrWhiteSpace(err)
                ? $"git log (signature status) failed with exit code {code}."
                : err.Trim());

        var parsed = SignatureStatusParser.ParseLog(output);

        // Fill in any SHA git didn't report (e.g. an unknown rev) as unsigned so callers get a
        // total map keyed by exactly what they asked for.
        var result = new Dictionary<string, CommitSignatureInfo>(StringComparer.Ordinal);
        foreach (var sha in shas)
            result[sha] = parsed.TryGetValue(sha, out var info) ? info : CommitSignatureInfo.None;
        return result;
    }

    public IReadOnlyList<SigningKeyOption> ListSigningKeys(string gpgFormat)
    {
        // SSH signing keys are just the public keys under ~/.ssh — no external tool needed.
        if (string.Equals(gpgFormat, "ssh", StringComparison.OrdinalIgnoreCase))
            return ListSshPublicKeys();

        return ListGpgSecretKeys();
    }

    private static IReadOnlyList<SigningKeyOption> ListSshPublicKeys()
    {
        var result = new List<SigningKeyOption>();
        // MainguardPaths.HomeDirectory, not GetFolderPath(UserProfile): see MainguardPaths — the default
        // option returns "" for a never-materialized home, silently making this path relative.
        var sshDir = Path.Combine(MainguardPaths.HomeDirectory(), ".ssh");
        if (!Directory.Exists(sshDir)) return result;

        foreach (var pub in Directory.EnumerateFiles(sshDir, "*.pub").OrderBy(p => p, StringComparer.Ordinal))
        {
            // Label with the key comment (3rd field) when present, else the file name.
            string label = Path.GetFileName(pub);
            try
            {
                var firstLine = File.ReadLines(pub).FirstOrDefault();
                var parts = firstLine?.Split(' ', 3);
                if (parts is { Length: 3 } && !string.IsNullOrWhiteSpace(parts[2]))
                    label = $"{Path.GetFileName(pub)} · {parts[2].Trim()}";
            }
            catch { /* an unreadable key just falls back to its file name */ }
            result.Add(new SigningKeyOption(pub, label));
        }
        return result;
    }

    private IReadOnlyList<SigningKeyOption> ListGpgSecretKeys()
    {
        var result = new List<SigningKeyOption>();

        // `--with-colons` is the machine-readable format; parse `sec` (fingerprint follows on the
        // next `fpr`) + the first `uid`. gpg is optional — if it isn't installed the launch throws
        // a typed error we swallow into an empty list (the picker just shows no keys).
        (int Code, string Out, string Err) res;
        try
        {
            res = RunProcess(GpgProgramForListing(), "--list-secret-keys", "--keyid-format", "long", "--with-colons");
        }
        catch (GitOperationException)
        {
            return result;
        }
        if (res.Code != 0) return result;

        string? pendingKeyId = null;
        foreach (var rawLine in res.Out.Split('\n'))
        {
            var fields = rawLine.Split(':');
            switch (fields[0])
            {
                case "sec":
                    // Long key id is field 5 (0-based 4); the uid arrives on a later line.
                    pendingKeyId = fields.Length > 4 ? fields[4] : null;
                    break;
                case "fpr" when pendingKeyId != null:
                    // Prefer the full fingerprint (field 10) as the stable key id.
                    if (fields.Length > 9 && !string.IsNullOrEmpty(fields[9]))
                        pendingKeyId = fields[9];
                    break;
                case "uid" when pendingKeyId != null:
                    var uid = fields.Length > 9 ? fields[9] : string.Empty;
                    result.Add(new SigningKeyOption(pendingKeyId, string.IsNullOrWhiteSpace(uid) ? pendingKeyId : $"{uid} ({Short(pendingKeyId)})"));
                    pendingKeyId = null;
                    break;
            }
        }
        return result;

        static string Short(string id) => id.Length > 16 ? id[^16..] : id;
    }

    private string GpgProgramForListing()
    {
        var prog = Preferences.GpgProgram;
        return string.IsNullOrWhiteSpace(prog) ? "gpg" : prog.Trim();
    }

    // Runs an arbitrary process capturing stdout/stderr, with the same no-TTY discipline as RunGit
    // (stdin closed so nothing blocks on a prompt). Used for the gpg key listing.
    private static (int Code, string Out, string Err) RunProcess(string fileName, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.Start(psi)
                ?? throw new GitOperationException($"Failed to launch '{fileName}'.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GitOperationException($"Failed to launch '{fileName}'. Is it installed and on the PATH?", ex);
        }

        using (process)
        {
            process.StandardInput.Close();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
        }
    }

    public IEnumerable<string> GetAuthors(string repoPath)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            return repo.Commits.Select(c => c.Author.Name).Distinct().OrderBy(a => a).ToList();
        });
    }

    public IEnumerable<string> GetRepositoryPaths(string repoPath)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            return repo.Index.Select(i => i.Path).OrderBy(p => p).ToList();
        });
    }

    public void CheckoutRevision(string repoPath, string commitSha)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit != null)
            {
                Commands.Checkout(repo, commit);
            }
        });
    }

    public void ResetToCommit(string repoPath, string commitSha, LibGit2Sharp.ResetMode mode)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.ResetToCommit, $"Reset ({mode}) to {Short(commitSha)}");
        ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit != null)
            {
                repo.Reset(mode, commit);
            }
        });
    }

    public void RevertCommit(string repoPath, string commitSha)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.RevertCommit, $"Revert {Short(commitSha)}");
        ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit != null)
            {
                var author = GetSignature(repo);
                repo.Revert(commit, author, new RevertOptions { CommitOnSuccess = true });
            }
        });
    }

    public void CherryPick(string repoPath, string commitSha)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.CherryPick, $"Cherry-pick {Short(commitSha)}");
        ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit == null) throw new GitOperationException($"Commit {commitSha} not found.");

            var signature = GetSignature(repo);
            var result = repo.CherryPick(commit, signature, new CherryPickOptions { CommitOnSuccess = false });

            if (result.Status == CherryPickStatus.Conflicts)
            {
                throw new MergeConflictException("Cherry pick resulted in conflicts. Please resolve them and commit manually.");
            }
        });
    }

    public void AmendCommitMessage(string repoPath, string commitSha, string newMessage)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.AmendCommitMessage, Describe("Amend commit", newMessage));
        ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit == null) throw new GitOperationException("Commit not found");

            if (repo.Head.Tip == null)
            {
                throw new GitOperationException("There is no commit to amend (the branch is unborn).");
            }

            if (repo.Head.Tip.Sha != commit.Sha)
            {
                throw new GitOperationException("Can only amend the most recent commit (HEAD).");
            }

            var signature = GetSignature(repo);
            repo.Commit(newMessage, signature, signature, new CommitOptions { AmendPreviousCommit = true });
        });
    }

    public GitHeadState GetHeadState(string repoPath)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            // repo.Info exposes the authoritative HEAD state without materializing branches.
            bool unborn = repo.Info.IsHeadUnborn;
            bool detached = repo.Info.IsHeadDetached;
            return new GitHeadState
            {
                Sha = unborn ? null : repo.Head.Tip?.Sha,
                IsUnborn = unborn,
                IsDetached = detached,
                CurrentBranchName = (unborn || detached) ? null : repo.Head.FriendlyName
            };
        });
    }

    public IReadOnlyList<ReflogItem> GetReflog(string repoPath, string refName = "HEAD", int take = 200)
    {
        if (take < 0) take = 0;
        return ExecuteWithRepo(repoPath, repo =>
        {
            // repo.Refs.Log already yields entries most-recent-first — exactly the viewer's order.
            ReflogCollection log;
            if (string.IsNullOrWhiteSpace(refName) || refName == "HEAD")
            {
                // Valid even on an unborn branch (its reflog is simply empty).
                log = repo.Refs.Log("HEAD");
            }
            else
            {
                // Resolve to a Reference so friendly names ("main") work. The Branches indexer takes
                // friendly OR canonical names and returns null when missing; the Refs indexer only
                // accepts canonical names (it throws on a friendly one), so guard it to "refs/…".
                var reference = repo.Branches[refName]?.Reference;
                if (reference is null && refName.StartsWith("refs/", StringComparison.Ordinal))
                    reference = repo.Refs[refName];
                if (reference is null)
                    throw new GitOperationException($"Reference '{refName}' not found.");
                log = repo.Refs.Log(reference);
            }

            var items = new List<ReflogItem>();
            foreach (var entry in log)
            {
                items.Add(new ReflogItem
                {
                    // From is all-zero for the ref's very first entry (creation); expose the raw sha either way.
                    FromSha = entry.From?.Sha ?? "",
                    ToSha = entry.To?.Sha ?? "",
                    Message = FirstLine(entry.Message),
                    When = entry.Committer?.When ?? default
                });
                if (items.Count >= take) break;
            }

            return (IReadOnlyList<ReflogItem>)items;
        });
    }

    private static string FirstLine(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        int nl = message.IndexOf('\n');
        var line = nl < 0 ? message : message.Substring(0, nl);
        return line.TrimEnd('\r');
    }

    public void CreateBranchAt(string repoPath, string branchName, string commitSha, bool checkout)
    {
        using var op = _journal.BeginOperation(repoPath, JournalKinds.CreateBranchAt, $"Create branch {branchName} at {Short(commitSha)}");
        ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha)
                ?? throw new GitOperationException($"Commit {commitSha} not found.");

            var newBranch = repo.CreateBranch(branchName, commit);
            if (checkout)
            {
                Commands.Checkout(repo, newBranch);
            }
        });
    }

    public IReadOnlyList<BlameLine> GetBlame(string repoPath, string path, string? startingSha = null)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            // Resolve the requested revision to a concrete commit SHA. This is both the
            // blame anchor and the cache key: because it is a pinned SHA, a new commit
            // (HEAD advancing) yields a different key and recomputes — no stale blame.
            Commit startCommit;
            if (string.IsNullOrEmpty(startingSha))
            {
                startCommit = repo.Head.Tip
                    ?? throw new GitOperationException(
                        $"Cannot blame '{path}': the repository has no commits yet.");
            }
            else
            {
                startCommit = repo.Lookup<Commit>(startingSha)
                    ?? throw new GitOperationException(
                        $"Cannot blame '{path}': revision '{startingSha}' was not found.");
            }

            var key = new BlameCache.Key(repoPath, path, startCommit.Sha);
            if (_blameCache.TryGet(key, out var cached))
            {
                return cached;
            }

            // A path that does not exist at the revision is a typed failure that names the
            // path (blame would otherwise throw a raw native error). Checked before blame so
            // the message is precise and independent of the libgit2 error text.
            var entry = startCommit[path];
            if (entry == null)
            {
                throw new GitOperationException(
                    $"Cannot blame '{path}': it does not exist at the requested revision.");
            }

            // Binary blobs carry no meaningful line attribution — return empty rather than
            // throwing (blame on binary must never crash the caller). Empty files likewise
            // yield zero hunks below.
            if (entry.TargetType == TreeEntryTargetType.Blob && entry.Target is Blob blob && blob.IsBinary)
            {
                IReadOnlyList<BlameLine> empty = System.Array.Empty<BlameLine>();
                _blameCache.Set(key, empty);
                return empty;
            }

            var options = new BlameOptions { StartingAt = startCommit.Sha };
            BlameHunkCollection hunks;
            try
            {
                hunks = repo.Blame(path, options);
            }
            catch (LibGit2SharpException ex)
            {
                throw new GitOperationException(
                    $"Cannot blame '{path}' at the requested revision.", ex);
            }

            var lines = new List<BlameLine>();
            foreach (var hunk in hunks)
            {
                var commit = hunk.FinalCommit;
                var sha = commit.Sha;
                var shortSha = sha.Length >= 8 ? sha.Substring(0, 8) : sha;
                var author = commit.Author;
                var summary = commit.MessageShort;

                // libgit2's FinalStartLineNumber is 0-based (pinned by
                // GetBlame_ShouldMapLinesToCommits, which asserts exact line→SHA mapping);
                // +1 normalizes BlameLine.LineNumber to the 1-based current-file line.
                for (int i = 0; i < hunk.LineCount; i++)
                {
                    lines.Add(new BlameLine
                    {
                        LineNumber = hunk.FinalStartLineNumber + i + 1,
                        Sha = sha,
                        ShortSha = shortSha,
                        AuthorName = author.Name,
                        When = author.When,
                        Summary = summary,
                    });
                }
            }

            IReadOnlyList<BlameLine> result = lines;
            _blameCache.Set(key, result);
            return result;
        });
    }

    public void InvalidateBlameCache(string repoPath) => _blameCache.InvalidateRepo(repoPath);

    // ---- File history (T-12) ----------------------------------------------

    public IReadOnlyList<FileVersion> GetFileHistory(string repoPath, string path) =>
        ExecuteWithRepo(repoPath, repo =>
        {
            // QueryBy(path, ...) walks only the commits that touched this file and tracks it
            // across renames for free — LogEntry.Path is the file's name at that revision.
            // Topological | Time keeps the walk newest-first while respecting ancestry.
            var filter = new CommitFilter
            {
                SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time
            };

            var versions = new List<FileVersion>();
            foreach (var entry in repo.Commits.QueryBy(path, filter))
            {
                var commit = entry.Commit;
                versions.Add(ToVersion(commit, entry.Path));
            }

            // QueryBy is anchored at HEAD's tree, so a file that no longer exists at HEAD (deleted,
            // or living only on another branch) yields nothing. `git log -- <path>` still shows such
            // a file's past, so fall back to a manual first-parent walk that collects the revisions
            // where this exact path existed and changed. This fallback does not follow renames
            // (that information is only reconstructable from the tip), which is acceptable for the
            // deleted-file case; the common live-file path above keeps full rename tracking.
            if (versions.Count == 0)
            {
                foreach (var commit in repo.Commits.QueryBy(filter))
                {
                    if (commit[path]?.Target is not Blob blob) continue;   // absent here — nothing to show

                    var parent = commit.Parents.FirstOrDefault();
                    bool changed = parent == null                          // root: introduced the file
                        || parent[path]?.Target is not Blob parentBlob     // added relative to first parent
                        || parentBlob.Sha != blob.Sha;                     // modified
                    if (changed) versions.Add(ToVersion(commit, path));
                }
            }

            return (IReadOnlyList<FileVersion>)versions;
        });

    private static FileVersion ToVersion(Commit commit, string pathAtCommit) => new()
    {
        Sha = commit.Sha,
        PathAtCommit = pathAtCommit,
        MessageShort = commit.MessageShort,
        When = commit.Author.When,
        AuthorName = commit.Author.Name,
    };

    public string GetFileAtCommit(string repoPath, string sha, string path) =>
        ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(sha)
                ?? throw new GitOperationException($"Commit {sha} was not found.");

            // commit[path] resolves the tree entry at the commit; a Blob target is the file content.
            if (commit[path]?.Target is not Blob blob)
            {
                throw new GitOperationException($"'{path}' does not exist at {sha}.");
            }

            // Binary blobs carry no meaningful text — throw typed so the UI shows a placeholder
            // instead of rendering the raw bytes as garbage.
            if (blob.IsBinary)
            {
                throw new GitOperationException($"'{path}' is a binary file at {sha}.");
            }

            return blob.GetContentText();
        });

    public byte[] GetBlobBytesAtCommit(string repoPath, string sha, string path) =>
        ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(sha)
                ?? throw new GitOperationException($"Commit {sha} was not found.");

            if (commit[path]?.Target is not Blob blob)
            {
                throw new GitOperationException($"'{path}' does not exist at {sha}.");
            }

            // Raw bytes — no binary rejection: this feeds the "before" image of the image diff.
            using var stream = blob.GetContentStream();
            using var ms = new System.IO.MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        });

    public string GetFileDiffBetweenCommits(string repoPath, string olderSha, string newerSha, string path) =>
        ExecuteWithRepo(repoPath, repo =>
        {
            var older = repo.Lookup<Commit>(olderSha)
                ?? throw new GitOperationException($"Commit {olderSha} was not found.");
            var newer = repo.Lookup<Commit>(newerSha)
                ?? throw new GitOperationException($"Commit {newerSha} was not found.");

            // Scoped to the single path: equals `git diff olderSha newerSha -- path`.
            return repo.Diff.Compare<Patch>(older.Tree, newer.Tree, new[] { path }).Content;
        });
}
