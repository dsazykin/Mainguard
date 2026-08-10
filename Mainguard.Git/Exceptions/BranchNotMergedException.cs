namespace Mainguard.Git.Exceptions;

/// <summary>
/// Thrown when a branch delete is asked for <b>without</b> force and the branch is not fully
/// merged — neither into HEAD nor into its own upstream — so removing the ref would leave its
/// commits unreferenced. This is the <c>git branch -d</c> refusal, surfaced as a type (rather
/// than a message) so the UI can offer an explicit "delete anyway" instead of guessing.
/// </summary>
public class BranchNotMergedException : MainguardException
{
    /// <summary>The branch that was refused, so callers never have to parse the message.</summary>
    public string BranchName { get; }

    public BranchNotMergedException(string branchName, string message) : base(message)
        => BranchName = branchName;
}
