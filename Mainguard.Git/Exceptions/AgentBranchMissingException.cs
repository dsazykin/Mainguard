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

/// <summary>
/// A resume's rescue publish — the last chance to carry commits out of the previous jail's per-agent
/// repository before that repository is deleted — failed for a <b>transient</b> reason (git itself fell
/// over: unreadable repo, a race, a full disk).
///
/// <para><b>Why this refuses the adoption instead of proceeding.</b> The next step of the adoption
/// deletes that repository, and it is the only copy of any commits the mirror never saw. Proceeding is
/// unrecoverable; refusing costs one retry once the transient condition clears. The outcome used to be
/// discarded entirely, so the deletion happened anyway and the loss was silent.</para>
///
/// <para>Deliberately narrow: a <i>refused</i> publish (non-fast-forward, or a target outside the agent's
/// own namespace) is a permanent property of those commits, so refusing on it would strand the agent on
/// every future attempt rather than on one. Those outcomes stay logged and audited instead.</para>
/// </summary>
public class AgentBranchRescueFailedException : MainguardException
{
    public AgentBranchRescueFailedException(string repoHash, string agentId, string? reason)
        : base($"Could not rescue agent '{agentId}'s commits out of its previous repository before "
               + $"resuming it in repo '{repoHash}': {reason ?? "git reported a failure"}. The resume was "
               + "refused rather than completed, because completing it deletes the only copy of any work "
               + "the mirror has not seen. Retry once the underlying condition (disk, a concurrent "
               + "operation, an unreadable repository) has cleared.")
    {
        RepoHash = repoHash;
        AgentId = agentId;
        Reason = reason;
    }

    /// <summary>The repo the resume was for.</summary>
    public string RepoHash { get; }

    /// <summary>The agent whose commits could not be carried to the mirror.</summary>
    public string AgentId { get; }

    /// <summary>Git's reported reason, verbatim, so the operator sees the measurement.</summary>
    public string? Reason { get; }
}
