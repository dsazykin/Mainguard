using System;
using System.Collections.Generic;
using System.IO;

namespace Mainguard.App.Shell.Services;

/// <summary>
/// One repository the sidebar's auto-detect scan found: where it is, the label the sidebar shows,
/// and the grouping folder whose name becomes its workspace category (<c>null</c> = the default
/// category). <see cref="Path"/> is always a repository's WORKING directory, never a <c>.git</c> dir.
/// </summary>
internal readonly record struct AutoDetectedRepo(string Path, string DisplayName, string? CategoryName);

/// <summary>
/// The pure directory walk behind <c>MainWindowViewModel.ScanAutoDetectFolderAsync</c> (the sidebar's
/// "auto-detect repositories" folder browse), split out of the ViewModel so the walk is unit-pinned
/// while the ViewModel keeps only the persistence around it.
/// <para>
/// Shape: the chosen root when the root is ITSELF a repository, otherwise the root's immediate
/// subdirectories plus one further level down (a "workspaces" folder of grouping folders, each
/// grouping folder's name becoming a category). The root-is-a-repository case is not an extra
/// convenience but a correctness requirement — descending into a repository walks its own
/// <c>.git</c>/<c>.mainguard</c> internals, and a raw <c>.git</c> directory satisfies libgit2's
/// repository signature, so the scan used to add the git internals as a repository literally named
/// ".git" (walkthrough bug W3). <see cref="Mainguard.Git.Services.IGitService.IsGitRepository"/>
/// now rejects a <c>.git</c> directory as well, so the two guards are independent.
/// </para>
/// Unreadable directories are skipped, never thrown — a denied subtree must not abort the scan.
/// </summary>
internal static class AutoDetectScan
{
    internal static IReadOnlyList<AutoDetectedRepo> Scan(string rootPath, Func<string, bool> isGitRepository)
    {
        var found = new List<AutoDetectedRepo>();
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return found;
        }

        var root = Path.TrimEndingDirectorySeparator(rootPath);

        // The chosen root is the repository — one result, under the default category, and NO walk
        // of its children (see the type remarks: that walk is what produced the ".git" entry).
        if (isGitRepository(root))
        {
            found.Add(new AutoDetectedRepo(root, NameOf(root), null));
            return found;
        }

        foreach (var dir in SafeGetDirectories(root))
        {
            if (isGitRepository(dir))
            {
                found.Add(new AutoDetectedRepo(dir, NameOf(dir), null));
                continue;
            }

            // One extra level down: a grouping folder (a client/org) whose children are the
            // repositories; its own name becomes their workspace category.
            foreach (var sub in SafeGetDirectories(dir))
            {
                if (isGitRepository(sub))
                {
                    found.Add(new AutoDetectedRepo(sub, NameOf(sub), NameOf(dir)));
                }
            }
        }

        return found;
    }

    /// <summary>The sidebar label for a path: its own folder name, with a trailing separator (which a
    /// folder picker's path can carry) trimmed first so the name is never empty.</summary>
    private static string NameOf(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private static string[] SafeGetDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch
        {
            // Access denied / reparse-point trouble: skip this branch, keep scanning the rest.
            return Array.Empty<string>();
        }
    }
}
