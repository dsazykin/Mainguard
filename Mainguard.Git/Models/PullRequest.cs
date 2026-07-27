using System;
using System.Collections.Generic;

namespace Mainguard.Git.Models;

/// <summary>
/// Lifecycle state of a pull/merge request (T-23), host-agnostic. <see cref="Draft"/> is
/// modelled as a distinct state for the list badge even though most hosts also expose a
/// separate draft flag (see <see cref="PullRequestItem.IsDraft"/>).
/// </summary>
public enum PullRequestState { Open, Closed, Merged, Draft }

/// <summary>How a merge is performed on the host (T-23): a normal merge commit, squash, or rebase.</summary>
public enum PullRequestMergeMethod { Merge, Squash, Rebase }

/// <summary>
/// A pull/merge request as shown in the list (T-23). Host-agnostic projection produced by an
/// <c>IPullRequestProvider</c>; the ViewModel never sees a host's JSON shape.
/// </summary>
public sealed class PullRequestItem
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string SourceBranch { get; init; } = "";   // head ref (friendly)
    public string TargetBranch { get; init; } = "";   // base ref (friendly)
    public PullRequestState State { get; init; }
    public bool IsDraft { get; init; }
    public string Url { get; init; } = "";            // web URL, for "open in browser"

    /// <summary>
    /// The commit the PR head currently points at. This is the external merge's compare-and-swap
    /// old-OID (P2-12): the queue verified one specific head, and the merge must refuse if the PR has
    /// gained commits since — the upstream analogue of <c>git merge --ff-only</c> losing the race.
    /// Empty when the host response carried no head sha (list rows on some hosts).
    /// </summary>
    public string HeadSha { get; init; } = "";

    /// <summary>
    /// The commit the merge produced on the base branch, set only on the item returned by a merge call.
    /// It is the proof the merge is expected to have landed upstream — the external merge refuses to
    /// record anything until this commit is actually reachable from the base branch it fetched.
    /// </summary>
    public string MergeCommitSha { get; init; } = "";
}

/// <summary>Detailed view of a single pull request (T-23): body, mergeability, reviewers, checks.</summary>
public sealed class PullRequestDetail
{
    public PullRequestItem Summary { get; init; } = new();
    public string Body { get; init; } = "";
    public bool Mergeable { get; init; }

    /// <summary>
    /// The host's reason a PR is (or is not) mergeable, verbatim and lower-cased: GitHub's
    /// <c>mergeable_state</c> — <c>clean</c>, <c>dirty</c> (conflicts), <c>blocked</c> (required
    /// reviews/checks not satisfied), <c>behind</c>, <c>unstable</c>, <c>draft</c>, <c>unknown</c>.
    /// <see cref="Mergeable"/> alone collapses "conflicts with the base" and "branch protection says no"
    /// into one false, and those need opposite advice. Empty when the host does not report one.
    /// </summary>
    public string MergeableState { get; init; } = "";
    public IReadOnlyList<string> Reviewers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<(string Name, string State)> Checks { get; init; } = Array.Empty<(string, string)>();
}

/// <summary>The fields needed to open a pull request (T-23).</summary>
public sealed class CreatePullRequest
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string SourceBranch { get; init; } = "";
    public string TargetBranch { get; init; } = "";
    public bool IsDraft { get; init; }
}
