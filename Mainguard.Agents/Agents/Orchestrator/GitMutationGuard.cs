using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>Why <see cref="GitMutationGuard.CanMutate"/> allowed or refused a keep-alive mutation.</summary>
public enum MutationVerdictKind
{
    /// <summary>The worktree is quiescent — the daemon may mutate it.</summary>
    Allow,

    /// <summary>A <c>rebase-merge</c>/<c>rebase-apply</c> dir is present: the agent is mid-rebase of its own branch. Skip this cycle (edge row 2).</summary>
    SkipRebaseInProgress,

    /// <summary>HEAD is detached — refuse to mutate (a keep-alive commit/rebase would orphan work).</summary>
    SkipDetachedHead,

    /// <summary>A <c>MERGE_HEAD</c> is present (an in-progress merge) — refuse to mutate.</summary>
    SkipMergeInProgress,
}

/// <summary>The three <c>.git</c>-dir preconditions the guard reads (pure snapshot; no process spawn).</summary>
public sealed record GitDirState(bool RebaseInProgress, bool DetachedHead, bool MergeInProgress);

/// <summary>The guard's decision for one worktree.</summary>
public sealed record MutationVerdict(MutationVerdictKind Kind, string? Reason)
{
    /// <summary>True only when the worktree is safe to mutate.</summary>
    public bool CanMutate => Kind == MutationVerdictKind.Allow;

    /// <summary>The single allow instance.</summary>
    public static readonly MutationVerdict Allowed = new(MutationVerdictKind.Allow, null);
}

/// <summary>
/// P2-09 pure preconditions for touching an agent worktree — the unit-testable heart of the
/// cooperative-yield contract. <see cref="CanMutate"/> is a pure function of a
/// <see cref="GitDirState"/>; <see cref="Inspect"/> reads that state off disk (resolving the
/// per-worktree gitdir); <see cref="RunGuarded{T}"/> wraps one runner call with
/// <c>.git/index.lock</c> detection and bounded exponential backoff, and requires an active
/// <see cref="IYieldToken"/> so mutation code cannot reach a worktree without a completed yield
/// (invariant 2). This is <b>not</b> a git runner — it spawns nothing; the caller's
/// <paramref name="action"/> is the one that goes through the shared audited primitive.
/// </summary>
public static class GitMutationGuard
{
    /// <summary>Bounded retry cap for a transient <c>index.lock</c> (the agent is yielded/paused, so it should clear fast).</summary>
    public const int MaxLockAttempts = 5;

    /// <summary>Base backoff delay; doubles each attempt (100 ms, 200 ms, 400 ms, …).</summary>
    public static readonly TimeSpan BaseLockDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Pure decision: refuse if the worktree is mid-rebase, detached, or mid-merge; otherwise allow.</summary>
    public static MutationVerdict CanMutate(GitDirState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.RebaseInProgress)
        {
            return new MutationVerdict(MutationVerdictKind.SkipRebaseInProgress,
                "Worktree is mid-rebase (agent rebasing its own branch); skip this keep-alive cycle and retry next.");
        }

        if (state.DetachedHead)
        {
            return new MutationVerdict(MutationVerdictKind.SkipDetachedHead,
                "Worktree HEAD is detached; refuse to commit/rebase against it.");
        }

        if (state.MergeInProgress)
        {
            return new MutationVerdict(MutationVerdictKind.SkipMergeInProgress,
                "Worktree has an in-progress merge (MERGE_HEAD present); refuse to mutate.");
        }

        return MutationVerdict.Allowed;
    }

    /// <summary>Reads the three preconditions off <paramref name="worktreePath"/>'s resolved gitdir.</summary>
    public static GitDirState Inspect(string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath))
        {
            throw new ArgumentException("A worktree path is required.", nameof(worktreePath));
        }

        var gitDir = ResolveGitDir(worktreePath);
        var rebase =
            Directory.Exists(Path.Combine(gitDir, "rebase-merge")) ||
            Directory.Exists(Path.Combine(gitDir, "rebase-apply"));
        var merge = File.Exists(Path.Combine(gitDir, "MERGE_HEAD"));
        var detached = IsDetachedHead(gitDir);
        return new GitDirState(rebase, detached, merge);
    }

    /// <summary>True iff the worktree's <c>.git/index.lock</c> currently exists.</summary>
    public static bool IsIndexLockHeld(string worktreePath) =>
        File.Exists(Path.Combine(ResolveGitDir(worktreePath), "index.lock"));

    /// <summary>The backoff schedule (base × 2^n) used by <see cref="RunGuarded{T}"/>.</summary>
    public static IReadOnlyList<TimeSpan> BackoffDelays(int attempts = MaxLockAttempts, TimeSpan? baseDelay = null)
    {
        var b = baseDelay ?? BaseLockDelay;
        var list = new List<TimeSpan>(attempts);
        for (var i = 0; i < attempts; i++)
        {
            list.Add(TimeSpan.FromTicks(b.Ticks * (1L << i)));
        }

        return list;
    }

    /// <summary>
    /// Runs <paramref name="action"/> once the worktree lock is clear, retrying on a held
    /// <c>index.lock</c> with the exponential backoff <paramref name="delays"/>. Requires an active
    /// <paramref name="token"/> (invariant 2). A lock that never clears within the cap raises a typed
    /// <see cref="GitMutationLockException"/> — <paramref name="action"/> never runs in that case.
    /// <paramref name="isLockHeld"/> and <paramref name="sleep"/> are injected so the backoff is
    /// deterministically testable without wall-clock timing.
    ///
    /// <para><b>K6 — <paramref name="recheck"/> is the state the decision was made from, re-read at the
    /// moment of action.</b> The caller reaches here having already run <see cref="CanMutate"/> over an
    /// <see cref="Inspect"/> snapshot. This method then waits — up to the full backoff schedule, ~1.5 s by
    /// default — and used to re-check <b>only the lock</b>, which is the one precondition that was never
    /// part of the verdict. The three that WERE (mid-rebase, detached HEAD, an in-progress merge) went
    /// unlooked-at, and they are precisely the states a worktree enters while a lock is held: the usual
    /// reason the backoff is running at all is that git is busy establishing one of them. So the guard's
    /// answer was measured at one instant and acted on at another, and an agent that started its own
    /// rebase during the wait got a <c>git add -A; git commit</c> and a <c>git rebase main</c> run against
    /// it anyway.</para>
    ///
    /// <para>Re-reading the state is <b>required</b> rather than optional in spirit: the parameter is
    /// nullable only because a caller with no worktree to inspect (the pure-backoff tests) must still be
    /// able to exercise the lock loop, and a re-check that cannot be performed must not fabricate a
    /// refusal. A refusing verdict raises <see cref="GitMutationStateChangedException"/> and
    /// <paramref name="action"/> never runs.</para>
    /// </summary>
    /// <param name="recheck">Re-reads the guard verdict immediately before the action. Evaluated once the
    /// lock is clear, so it observes the worktree as the action will find it.</param>
    public static T RunGuarded<T>(
        IYieldToken token,
        Func<bool> isLockHeld,
        Func<T> action,
        IReadOnlyList<TimeSpan>? delays = null,
        Action<TimeSpan>? sleep = null,
        Func<MutationVerdict>? recheck = null)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(isLockHeld);
        ArgumentNullException.ThrowIfNull(action);

        if (!token.IsActive)
        {
            throw new InvalidOperationException(
                "A completed, active yield token is required before any worktree mutation (P2-09 invariant 2).");
        }

        delays ??= BackoffDelays();
        sleep ??= d => Thread.Sleep(d);
        var cap = Math.Max(1, delays.Count);

        for (var attempt = 0; attempt < cap; attempt++)
        {
            if (!isLockHeld())
            {
                // The last thing before the mutation, and after the last thing that could have delayed it.
                var verdict = recheck?.Invoke() ?? MutationVerdict.Allowed;
                if (!verdict.CanMutate)
                {
                    throw new GitMutationStateChangedException(
                        "The worktree changed while this mutation waited for index.lock, and is no longer "
                        + $"the worktree the guard allowed: {verdict.Reason}");
                }

                return action();
            }

            if (attempt < cap - 1)
            {
                sleep(delays[attempt]);
            }
        }

        throw new GitMutationLockException(
            $"index.lock stayed held across {cap} attempts while the agent was yielded/paused; refusing to force it.");
    }

    /// <summary>
    /// Resolves a worktree's real gitdir. A linked worktree's <c>.git</c> is a file
    /// (<c>gitdir: &lt;path&gt;</c>) pointing at <c>&lt;bare&gt;/worktrees/&lt;name&gt;</c>; a normal
    /// repo's is the <c>.git</c> directory itself. Falls back to the worktree path when neither is present.
    /// </summary>
    internal static string ResolveGitDir(string worktreePath)
    {
        var dotGit = Path.Combine(worktreePath, ".git");
        if (Directory.Exists(dotGit))
        {
            return dotGit;
        }

        if (File.Exists(dotGit))
        {
            foreach (var line in File.ReadAllLines(dotGit))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    var target = trimmed["gitdir:".Length..].Trim();
                    if (target.Length > 0)
                    {
                        return Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(worktreePath, target));
                    }
                }
            }
        }

        // A bare repo passed directly, or an unusual layout: treat the path itself as the gitdir.
        return worktreePath;
    }

    private static bool IsDetachedHead(string gitDir)
    {
        var head = Path.Combine(gitDir, "HEAD");
        if (!File.Exists(head))
        {
            return false;
        }

        var content = File.ReadAllText(head).Trim();
        // A symbolic HEAD is "ref: refs/heads/...". Anything else (a raw SHA) is detached.
        return !content.StartsWith("ref:", StringComparison.Ordinal);
    }
}
