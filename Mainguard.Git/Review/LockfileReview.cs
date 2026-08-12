using System;
using System.Collections.Generic;
using System.Linq;

namespace Mainguard.Git.Review;

/// <summary>
/// Turns a lockfile's two committed blobs into the must-acknowledge items the merge gate blocks on
/// (P2-11 §3.6). Pure: no repo, no IO, <b>no network</b> — the caller supplies both texts and the offline
/// <see cref="OsvSnapshot"/>.
///
/// <para><b>Why this exists as its own type.</b> <see cref="LockfileSemanticDiff"/> answers "what moved"
/// and <see cref="FlaggedChange"/> is "what a human must sign off"; the mapping between them is a policy
/// with three decisions in it — which paths are lockfiles at all, which rows are worth a human's attention,
/// and what a row whose advisory status could not be established renders as — and that policy was
/// previously nowhere. <see cref="FlaggedChangeDetector"/> could not host it: it lives above this assembly,
/// so the daemon's merge path would have had to reach up into the orchestrator to classify a blob it had
/// already read.</para>
///
/// <para><b>Every exit is fail-closed.</b> A lockfile that cannot be read, is too large to parse, or is
/// checked against a snapshot that cannot answer produces an <see cref="FlaggedKind.LockfileAdvisoryUnknown"/>
/// item — never silence. Silence here is indistinguishable from "reviewed and clean", and the whole point
/// of the semantic diff is that a path-level "lockfile changed" flag cannot tell a patch bump from an added
/// transitive with a postinstall script.</para>
/// </summary>
public static class LockfileReview
{
    /// <summary>
    /// The largest blob either side may be before parsing is refused.
    ///
    /// <para>A bound rather than a timeout because the cost is dominated by document size, and a size is
    /// knowable before any work is done — the merge path must not be able to be stalled by committing a
    /// pathological manifest. 8 MB is roughly an order of magnitude above the largest real
    /// <c>package-lock.json</c> in circulation (a ~5,000-package lock is ~2 MB); exceeding it reports the
    /// refusal as an unknown rather than skipping quietly.</para>
    /// </summary>
    public const int MaxManifestBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The manifest format this path is parsed as, or null when it is not a dependency manifest at all.
    ///
    /// <para>Deliberately narrower than <c>RiskClassifier</c>'s lockfile <i>category</i>, which flags any
    /// <c>*.lock</c> by name. A format with no parser here (<c>yarn.lock</c>, <c>Cargo.lock</c>,
    /// <c>composer.lock</c>) must not be handed to a parser that would read zero dependencies out of it and
    /// report a clean semantic diff — that is a confident wrong answer where the honest one is the
    /// path-level flag the classifier already raises.</para>
    /// </summary>
    public static LockfileKind? KindFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var name = path.Replace('\\', '/').TrimEnd('/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        if (name.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("npm-shrinkwrap.json", StringComparison.OrdinalIgnoreCase))
        {
            return LockfileKind.NpmPackageLock;
        }

        if (name.Equals("pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase))
        {
            return LockfileKind.PnpmLock;
        }

        if (name.Equals("poetry.lock", StringComparison.OrdinalIgnoreCase))
        {
            return LockfileKind.PoetryLock;
        }

        return name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? LockfileKind.CsprojPackageReference
            : null;
    }

    /// <summary>
    /// The must-acknowledge items for one lockfile's change.
    /// </summary>
    /// <param name="path">The lockfile's path in the branch tree (carried onto every item).</param>
    /// <param name="kind">Its manifest format (from <see cref="KindFor"/>).</param>
    /// <param name="baseText">The base side's full blob; null/empty when the branch adds the file.</param>
    /// <param name="branchText">The branch side's full blob; null/empty when the branch removes it.</param>
    /// <param name="osv">The offline advisory snapshot (<see cref="OsvSnapshot.Default"/> when null).</param>
    /// <param name="asOf">The instant the snapshot's age is judged against (defaults to now).</param>
    /// <param name="unreadable">
    /// True when the caller could not obtain the blobs at all (git refused, the mirror does not carry the
    /// ref). Distinct from "both sides are empty", which is a legitimately empty manifest — the caller knows
    /// which it had and this must not guess.
    /// </param>
    public static IReadOnlyList<FlaggedChange> Review(
        string path,
        LockfileKind kind,
        string? baseText,
        string? branchText,
        OsvSnapshot? osv = null,
        DateTimeOffset? asOf = null,
        bool unreadable = false)
    {
        path ??= "(lockfile)";

        if (unreadable)
        {
            return new[] { Unknown(path, "its contents could not be read out of the mirror", "unreadable") };
        }

        var oldText = baseText ?? string.Empty;
        var newText = branchText ?? string.Empty;

        if (oldText.Length > MaxManifestBytes || newText.Length > MaxManifestBytes)
        {
            return new[]
            {
                Unknown(
                    path,
                    $"it is larger than the {MaxManifestBytes / (1024 * 1024)} MB the semantic review parses, "
                    + "so what it added was never determined",
                    "oversize"),
            };
        }

        // Resolved once: the snapshot that answers the CVE column and the snapshot whose refusal is quoted
        // to the reviewer have to be the same object, or the item could explain a check that never ran.
        var snapshot = osv ?? OsvSnapshot.Default;
        var deltas = LockfileSemanticDiff.Parse(oldText, newText, kind, snapshot, asOf);
        snapshot.CanAnswerAt(asOf ?? DateTimeOffset.UtcNow, out var snapshotReason);
        return ItemsFor(path, deltas, snapshotReason);
    }

    /// <summary>
    /// The items for an already-computed set of delta rows — the shared body behind
    /// <c>FlaggedChangeDetector.FromLockfileDeltas</c>, so there is exactly one definition of which row
    /// becomes which item.
    /// </summary>
    /// <param name="path">The lockfile's path.</param>
    /// <param name="deltas">The semantic rows.</param>
    /// <param name="snapshotReason">
    /// Why the advisory snapshot could not answer, when it could not. Empty means it could; rows still
    /// carrying <see cref="DependencyDelta.AdvisoryStatusUnknown"/> then fall back to a generic sentence
    /// rather than being dropped, because a row that says it was not checked must not be silenced by a
    /// caller that forgot to say why.
    /// </param>
    public static IReadOnlyList<FlaggedChange> ItemsFor(
        string path, IReadOnlyList<DependencyDelta>? deltas, string snapshotReason = "")
    {
        var items = new List<FlaggedChange>();
        if (deltas is null)
        {
            return items;
        }

        foreach (var delta in deltas)
        {
            if (delta.CveIds.Count > 0)
            {
                items.Add(new FlaggedChange(
                    path,
                    RiskCategory.Lockfile,
                    FlaggedKind.LockfileCve,
                    AcknowledgmentStore.HashContent(
                        $"{path}|{delta.Name}|{delta.NewVersion}|{string.Join(",", delta.CveIds)}"),
                    $"{delta.Name} {delta.NewVersion} — known CVE: {string.Join(", ", delta.CveIds)}"));
            }
            else if (delta.InstallScripts)
            {
                items.Add(new FlaggedChange(
                    path,
                    RiskCategory.Lockfile,
                    FlaggedKind.LockfileScript,
                    AcknowledgmentStore.HashContent($"{path}|{delta.Name}|{delta.NewVersion}|install-scripts"),
                    $"{delta.Name} {delta.NewVersion} declares install scripts (code runs at install)"));
            }
        }

        // ONE unknown item per lockfile rather than one per dependency. A snapshot that cannot answer cannot
        // answer for anything, so a row per package would turn a single fact into hundreds of identical
        // must-ack rows — which is how a gate stops being read.
        var unverified = deltas.Where(d => d.AdvisoryStatusUnknown).ToList();
        if (unverified.Count > 0)
        {
            var one = unverified.Count == 1;
            var why = snapshotReason.Length > 0
                ? snapshotReason
                : "the offline advisory snapshot could not answer";

            items.Add(Unknown(
                path,
                $"{unverified.Count} added or updated dependenc{(one ? "y" : "ies")} ({Sample(unverified)}) "
                + $"{(one ? "was" : "were")} NOT checked for known advisories — {why}",
                // The seed names every unchecked package, so landing a different dependency produces a
                // different hash and drops the acknowledgment that covered the old set (invariant 2).
                "snapshot|" + string.Join(",", unverified.Select(d => $"{d.Name}@{d.NewVersion}"))));
        }

        return items;
    }

    /// <summary>
    /// The one shape an unknown advisory status takes: a dedicated must-acknowledge item that says what was
    /// not established and why. Never an omission — an omitted item is
    /// <see cref="AcknowledgmentStore.AllAcknowledged"/>, i.e. it renders as "reviewed and clean".
    /// </summary>
    private static FlaggedChange Unknown(string path, string detail, string hashSeed) => new(
        path,
        RiskCategory.Lockfile,
        FlaggedKind.LockfileAdvisoryUnknown,
        AcknowledgmentStore.HashContent($"{path}|advisory-unknown|{hashSeed}"),
        $"{path} changed and {detail}");

    // The first few names, so the item is actionable without listing 400 packages in a gate row.
    private static string Sample(IReadOnlyList<DependencyDelta> rows)
    {
        const int max = 3;
        var names = rows.Take(max).Select(r => $"{r.Name} {r.NewVersion}".Trim());
        var text = string.Join(", ", names);
        return rows.Count > max ? $"{text}, +{rows.Count - max} more" : text;
    }
}
