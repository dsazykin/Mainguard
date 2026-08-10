using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Mainguard.Git.Services;

public class RepositoryWatcher : IDisposable
{
    private readonly string _repoPath;

    // Every directory whose contents count as git METADATA rather than working-tree content,
    // longest path first so the most specific one wins when they nest (a linked worktree's
    // per-worktree gitdir lives inside the common dir). See ResolveGitRoots.
    private readonly string[] _gitRoots;

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Timer _debounceTimer;
    private readonly int _debounceMs;
    private bool _disposed;

    // Never fire more than once per this window, even under continuous writes
    // (e.g. a build churning bin/obj or many agents writing at once).
    private const int MaxRefreshMs = 250;
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    // Cheap prefix denylist so heavy, uninteresting directories don't trigger a
    // status re-read on every write. Mirrors the ignores GetRepositoryStatus
    // already applies, without paying for a full .gitignore evaluation per event.
    private static readonly string[] IgnoredDirSegments =
    {
        "node_modules", "bin", "obj", ".vs", ".idea", "packages", "dist", "target"
    };

    /// <summary>
    /// Event triggered after a debounced delay once changes are detected in HEAD, index, or refs/.
    /// </summary>
    public event Action? RepositoryChanged;

    public RepositoryWatcher(string repoPath, int debounceMs = 300)
    {
        _repoPath = repoPath;
        _debounceMs = debounceMs;
        _gitRoots = ResolveGitRoots(repoPath);

        _debounceTimer = new Timer(OnTimerFired, null, Timeout.
Infinite, Timeout.Infinite);

        StartWatching();
    }

    /// <summary>
    /// The directories holding this repository's git metadata, longest-first.
    /// <para>
    /// For an ordinary working tree that is just <c>&lt;repo&gt;/.git</c>, which lives inside the
    /// watched tree. For a <b>linked worktree</b> it is neither: <c>.git</c> is a FILE, the
    /// per-worktree state (HEAD, index, MERGE_HEAD, rebase-merge/) sits in
    /// <c>&lt;main&gt;/.git/worktrees/&lt;name&gt;/</c> and the shared refs sit in
    /// <c>&lt;main&gt;/.git/</c> — both entirely outside the directory being watched. That is why
    /// a worktree used to see working-tree edits only: a commit, checkout, stage or rebase step
    /// performed by anything else (a terminal, or an agent in its own worktree) changed no file
    /// under the watched root, so the UI never refreshed.
    /// </para>
    /// For a bare repository the gitdir is the repo path itself, which keeps the historical
    /// "everything here is metadata" behaviour.
    /// </summary>
    private static string[] ResolveGitRoots(string repoPath)
    {
        if (string.IsNullOrEmpty(repoPath)) return Array.Empty<string>();

        try
        {
            var gitDir = GitService.ResolveGitDir(repoPath);
            var commonDir = GitService.ResolveCommonGitDir(gitDir);

            return new[] { gitDir, commonDir }
                .Where(Directory.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(p => p.Length)   // most specific root wins in ClassifyGitPath
                .ToArray();
        }
        catch
        {
            // Never let discovery failure stop the watcher from doing the working-tree half.
            return Array.Empty<string>();
        }
    }

    private void StartWatching()
    {
        if (string.IsNullOrEmpty(_repoPath)) return;

        // The working tree, plus any git root that is not already inside it. In an ordinary
        // repo `.git` is under the working tree and this adds nothing; a linked worktree adds
        // the common dir (which contains the per-worktree gitdir, so one watcher covers both).
        var roots = new List<string> { _repoPath };
        foreach (var gitRoot in _gitRoots)
        {
            if (IsUnder(gitRoot, _repoPath)) continue;
            if (roots.Any(existing => IsUnder(gitRoot, existing))) continue;
            roots.Add(gitRoot);
        }

        foreach (var root in roots) TryWatch(root);
    }

    private void TryWatch(string path)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.CreationTime
            };

            watcher.Changed += OnFileSystemEvent;
            watcher.Created += OnFileSystemEvent;
            watcher.Deleted += OnFileSystemEvent;
            watcher.Renamed += OnFileSystemEvent;

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch
        {
            // Fail gracefully if directory accessibility issues occur
            watcher?.Dispose();
        }
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or sits inside it.</summary>
    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root);
        if (string.Equals(full, fullRoot, StringComparison.Ordinal)) return true;

        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.Ordinal);
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        // Metadata first: an event may arrive from the working-tree watcher (ordinary repo,
        // where .git is inside it) or from a git-root watcher (linked worktree, where it is not).
        var gitRelative = ClassifyGitPath(e.FullPath);
        if (gitRelative != null)
        {
            // Ignore lock-file churn (index.lock, refs/**/*.lock, HEAD.lock, ...).
            // These are written and deleted mid-operation and would otherwise
            // trigger a refresh in the middle of a commit/merge/rebase.
            if (gitRelative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Match changes affecting only critical references and state files
            bool isHead = gitRelative.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
            bool isIndex = gitRelative.Equals("index", StringComparison.OrdinalIgnoreCase);
            bool isRefs = gitRelative.StartsWith("refs/", StringComparison.OrdinalIgnoreCase);

            if (isHead || isIndex || isRefs)
            {
                _debounceTimer.Change(_debounceMs, Timeout.Infinite);
            }
            return;
        }

        var relativePath = Path.GetRelativePath(_repoPath, e.FullPath);

        // Normalize path separators for comparison
        relativePath = relativePath.Replace('\\', '/');

        // A bare touch of the .git entry itself (mtime bump, or the pointer FILE in a linked
        // worktree) is not actionable and, without this guard, would fall through to the
        // working tree branch and fire a refresh.
        if (relativePath.Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Working tree change. Skip heavy build/dependency directories so a
        // build or dependency install doesn't hammer status re-reads.
        if (IsIgnoredWorkingTreePath(relativePath))
        {
            return;
        }

        _debounceTimer.Change(_debounceMs, Timeout.Infinite);
    }

    /// <summary>
    /// Returns the path of <paramref name="fullPath"/> relative to whichever git root contains
    /// it (separators normalized to <c>/</c>), or null when it is working-tree content.
    /// <see cref="_gitRoots"/> is ordered longest-first, so a linked worktree's per-worktree
    /// gitdir is matched before the common dir that contains it — otherwise its <c>HEAD</c>
    /// would present as <c>worktrees/&lt;name&gt;/HEAD</c> and be ignored.
    /// </summary>
    private string? ClassifyGitPath(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        foreach (var root in _gitRoots)
        {
            if (string.Equals(full, root, StringComparison.Ordinal)) return string.Empty;

            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.Ordinal))
            {
                return full.Substring(prefix.Length).Replace('\\', '/');
            }
        }
        return null;
    }

    private static bool IsIgnoredWorkingTreePath(string relativePath)
    {
        foreach (var segment in relativePath.Split('/'))
        {
            foreach (var ignored in IgnoredDirSegments)
            {
                if (segment.Equals(ignored, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void OnTimerFired(object? state)
    {
        // Rate cap: if we refreshed very recently, defer this fire to the end of
        // the cap window instead of refreshing again immediately.
        var now = DateTime.UtcNow;
        var sinceLast = (now - _lastRefreshUtc).TotalMilliseconds;
        if (sinceLast < MaxRefreshMs)
        {
            _debounceTimer.Change(MaxRefreshMs - (int)sinceLast, Timeout.Infinite);
            return;
        }

        _lastRefreshUtc = now;
        RepositoryChanged?.Invoke();
    }

    public void ForceRefresh()
    {
        RepositoryChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnFileSystemEvent;
            watcher.Created -= OnFileSystemEvent;
            watcher.Deleted -= OnFileSystemEvent;
            watcher.Renamed -= OnFileSystemEvent;
            watcher.Dispose();
        }
        _watchers.Clear();

        _debounceTimer.Dispose();
    }
}
