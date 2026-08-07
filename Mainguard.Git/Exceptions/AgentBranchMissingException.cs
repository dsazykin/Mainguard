namespace Mainguard.Git.Exceptions;

/// <summary>
/// A resume was asked to adopt an agent's existing work and there is no
/// <c>refs/heads/agent/&lt;id&gt;</c> in the repository's mirror to adopt.
///
/// <para><b>This is a refusal, never a fallback.</b> The one thing an adoption must not do when the
/// branch is gone is quietly create a fresh branch off the default branch: the caller asked to resume a
/// specific entry's committed work, and handing back an empty branch under the same name would report
/// success for an operation that lost everything it was invoked to recover. The commits are the asset —
/// without them there is nothing to resume, and the honest answer names that.</para>
/// </summary>
public class AgentBranchMissingException : MainguardException
{
    public AgentBranchMissingException(string repoHash, string agentId, string branch)
        : base($"Branch '{branch}' no longer exists in this repository's mirror, so agent '{agentId}' "
               + "has no committed work to resume. Its commits are gone (the branch is deleted when an "
               + "agent is stopped) — discard the entry instead.")
    {
        RepoHash = repoHash;
        AgentId = agentId;
        Branch = branch;
    }

    /// <summary>The repo whose mirror was asked for the branch. Agent ids are unique per repo, not
    /// globally, so the pair is the identity — never the id alone.</summary>
    public string RepoHash { get; }

    /// <summary>The agent whose branch is absent.</summary>
    public string AgentId { get; }

    /// <summary>The ref that was looked for (<c>agent/&lt;id&gt;</c>).</summary>
    public string Branch { get; }
}
