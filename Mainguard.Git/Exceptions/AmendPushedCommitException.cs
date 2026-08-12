namespace Mainguard.Git.Exceptions;

/// <summary>
/// Thrown when an amend would rewrite a commit the current branch has already published — the
/// tip is contained in the branch's upstream. Amending it produces a diverged branch whose next
/// push is rejected, so the amend is refused up front instead of leaving the user to discover
/// the divergence later. Deliberate history rewrites still go through reset + force-push.
/// </summary>
public class AmendPushedCommitException : MainguardException
{
    /// <summary>The branch whose tip is already published (empty when HEAD is detached).</summary>
    public string BranchName { get; }

    /// <summary>The upstream that already contains the commit, e.g. <c>origin/main</c>.</summary>
    public string UpstreamName { get; }

    public AmendPushedCommitException(string branchName, string upstreamName, string message)
        : base(message)
    {
        BranchName = branchName;
        UpstreamName = upstreamName;
    }
}
