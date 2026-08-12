using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mainguard.Git.Models;
using Mainguard.Git.Review;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// Pure flag detector (P2-11 steps 3 + §3.3a). No repo, no IO. Classifies the full merge diff and surfaces
/// the flag-worthy categories, plus (for a managed worker with an approved plan) any file touched
/// <b>outside</b> the approved <c>TaskPlan.Scope</c> as a dedicated <see cref="FlaggedKind.OutOfApprovedScope"/>
/// item (OPS SA-1 / F6). Lockfile CVE/script rows and the RT-D2 changed-test-command item are composed in
/// by the cockpit from their own sources — this detector never guesses them from the diff alone.
/// </summary>
public static class FlaggedChangeDetector
{
    // The categories whose mere presence in a diff is flag-worthy (a Lockfile row is flagged only when the
    // semantic diff says so — that path is composed in separately).
    private static readonly HashSet<RiskCategory> FlagWorthy = new()
    {
        RiskCategory.ExecutableConfig,
        RiskCategory.CiWorkflow,
        RiskCategory.GitHooks,
        RiskCategory.SecuritySensitivePath,
    };

    /// <summary>Contract §2: the flag-worthy (path, category) pairs in the merge diff (one per flagged file).</summary>
    public static IReadOnlyList<(string Path, RiskCategory Category)> Detect(IReadOnlyList<FilePatch> mergeDiff)
    {
        var result = new List<(string, RiskCategory)>();
        if (mergeDiff is null)
        {
            return result;
        }

        foreach (var patch in mergeDiff)
        {
            var path = FilePatchPath.NewPath(patch);
            var category = TopCategory(path, patch, out var isFlagWorthy);
            if (isFlagWorthy)
            {
                result.Add((path, category));
            }
        }

        return result;
    }

    /// <summary>
    /// The full must-acknowledge item set for the branch: flag-worthy risk hunks + (managed, plan-bearing)
    /// out-of-approved-scope files. Each item's content hash binds to the flagged file's hunk content so a
    /// new push resets its acknowledgment.
    /// </summary>
    /// <param name="mergeDiff">The branch's merge diff.</param>
    /// <param name="approvedPlan">The managed worker's approved plan (its <c>Scope</c> is F6-load-bearing); null for a plan-less manual run.</param>
    /// <param name="managed">True for a managed worker (F6 applies); false skips the scope comparison.</param>
    public static IReadOnlyList<FlaggedChange> DetectFlagged(
        IReadOnlyList<FilePatch> mergeDiff,
        TaskPlan? approvedPlan = null,
        bool managed = false)
    {
        var items = new List<FlaggedChange>();
        if (mergeDiff is null)
        {
            return items;
        }

        var applyScope = managed && approvedPlan is not null;

        foreach (var patch in mergeDiff)
        {
            var path = FilePatchPath.NewPath(patch);
            var contentHash = HashPatch(patch);
            var category = TopCategory(path, patch, out var isFlagWorthy);

            if (isFlagWorthy)
            {
                items.Add(new FlaggedChange(
                    path,
                    category,
                    FlaggedKind.RiskCategory,
                    contentHash,
                    RiskDetail(category)));
            }

            // F6: a managed worker touching a file outside its approved scope is its own must-ack item —
            // even when the file's own category is benign (a silent off-scope Source edit still blocks).
            if (applyScope && !ScopeMatcher.IsInScope(path, approvedPlan!.Scope))
            {
                items.Add(new FlaggedChange(
                    path,
                    category,
                    FlaggedKind.OutOfApprovedScope,
                    contentHash,
                    $"outside approved scope — the plan covered {approvedPlan.Scope.Count} path pattern(s), this touches {path}"));
            }
        }

        return items;
    }

    /// <summary>
    /// Turns semantic lockfile delta rows into flagged items (P2-11 §3.6): a script-bearing add or a
    /// CVE-hit row becomes a dedicated must-ack item, and a row whose advisory status could not be
    /// established becomes a <see cref="FlaggedKind.LockfileAdvisoryUnknown"/> one.
    ///
    /// <para>Delegates to <see cref="LockfileReview.ItemsFor"/> — the definition lives beside the semantic
    /// diff in <c>Mainguard.Git</c> because the DAEMON is what arms the gate (see
    /// <c>MergeQueueProvisioner.ArmFlaggedChangeReview</c>), and the daemon's merge path must not have to
    /// reach up into the orchestrator assembly to classify a blob it already holds. This overload stays as
    /// the composed-by-the-cockpit entry point it always was, so the two can never disagree.</para>
    /// </summary>
    public static IReadOnlyList<FlaggedChange> FromLockfileDeltas(
        string lockfilePath, IReadOnlyList<DependencyDelta> deltas, string snapshotReason = "")
        => LockfileReview.ItemsFor(lockfilePath, deltas, snapshotReason);

    // The highest-risk (lowest-rank) category among the file's hunks, and whether it is flag-worthy.
    private static RiskCategory TopCategory(string path, FilePatch patch, out bool isFlagWorthy)
    {
        var best = RiskCategory.Docs;
        var seen = false;
        foreach (var hunk in patch.Hunks)
        {
            var category = RiskClassifier.Classify(path, hunk).Category;
            if (!seen || RiskClassifier.RankOf(category) < RiskClassifier.RankOf(best))
            {
                best = category;
                seen = true;
            }
        }

        if (!seen)
        {
            // A pure header change (mode/rename, no hunks) still classifies by path.
            best = RiskClassifier.Classify(path, new DiffHunk()).Category;
        }

        isFlagWorthy = FlagWorthy.Contains(best);
        return best;
    }

    private static string RiskDetail(RiskCategory category) => category switch
    {
        RiskCategory.ExecutableConfig => "executable config edited (scripts run at install/build)",
        RiskCategory.CiWorkflow => "CI workflow changed (runs with repo credentials)",
        RiskCategory.GitHooks => "git hook changed (runs on local git operations)",
        RiskCategory.SecuritySensitivePath => "security-sensitive path changed",
        _ => category.ToString(),
    };

    private static string HashPatch(FilePatch patch)
    {
        var sb = new StringBuilder();
        sb.Append(FilePatchPath.NewPath(patch)).Append('\n');
        foreach (var hunk in patch.Hunks)
        {
            sb.Append("@@").Append(hunk.OldStart).Append(',').Append(hunk.OldCount)
              .Append(' ').Append(hunk.NewStart).Append(',').Append(hunk.NewCount).Append('\n');
            foreach (var line in hunk.Lines)
            {
                sb.Append((char)line.Kind).Append(line.Text).Append('\n');
            }
        }

        return AcknowledgmentStore.HashContent(sb.ToString());
    }
}

/// <summary>
/// Pure glob matcher for <c>TaskPlan.Scope</c> comparison (F6). Supports <c>**</c> (any characters
/// including <c>/</c>), <c>*</c> (any characters except <c>/</c>), and <c>?</c> (one non-<c>/</c> char).
/// A path is in scope if it matches <b>any</b> scope pattern; an empty scope puts nothing in scope.
/// </summary>
public static class ScopeMatcher
{
    /// <summary>True iff <paramref name="path"/> matches any glob in <paramref name="scope"/>.</summary>
    public static bool IsInScope(string path, IReadOnlyList<string> scope)
    {
        if (scope is null || scope.Count == 0)
        {
            return false;
        }

        var normalized = Normalize(path);
        return scope.Any(pattern => Matches(normalized, Normalize(pattern)));
    }

    private static bool Matches(string path, string pattern)
    {
        if (pattern.Length == 0)
        {
            return false;
        }

        // A bare directory pattern ("src/a" or "src/a/") covers everything under it.
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            return path == pattern
                || path.StartsWith(pattern.TrimEnd('/') + "/", StringComparison.Ordinal);
        }

        var regex = "^" + GlobToRegex(pattern) + "$";
        return Regex.IsMatch(path, regex);
    }

    private static string GlobToRegex(string pattern)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    // "**" (optionally followed by "/") → any characters including path separators.
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        return sb.ToString();
    }

    private static string Normalize(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
}
