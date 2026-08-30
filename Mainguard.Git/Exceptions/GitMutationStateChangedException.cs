namespace Mainguard.Git.Exceptions;

/// <summary>
/// K6 — a P2-09 keep-alive Git mutation was abandoned because the worktree stopped being the worktree the
/// guard decided about.
///
/// <para><c>GitMutationGuard.CanMutate</c> reads a <c>GitDirState</c> snapshot — is the agent mid-rebase,
/// is HEAD detached, is there a <c>MERGE_HEAD</c> — and then <c>RunGuarded</c> waits out up to five
/// <c>index.lock</c> backoff attempts before running the action. That wait re-checked <b>only the lock</b>.
/// The three preconditions the verdict was actually made of were never looked at again, and they are
/// exactly the states an agent enters by starting work: the whole reason the lock is held during the
/// backoff is usually that git is busy establishing one of them. A decision that was correct when it was
/// made was then acted on against a worktree it was no longer true of, which is the same disease as every
/// other stale-evidence defect on the merge spine — evidence that does not record what it is evidence
/// FOR.</para>
///
/// <para>Distinct from <see cref="GitMutationLockException"/>: that one means the lock never cleared and
/// nothing was attempted; this one means the lock DID clear and the worktree underneath had moved on. Both
/// leave the action unrun, and the keep-alive cycle skips rather than failing — the next cycle retries,
/// which is what the cooperative-yield contract's "skip and retry" arm has always meant.</para>
/// </summary>
public sealed class GitMutationStateChangedException : MainguardException
{
    public GitMutationStateChangedException(string message) : base(message) { }

    public GitMutationStateChangedException(string message, System.Exception inner) : base(message, inner) { }
}
