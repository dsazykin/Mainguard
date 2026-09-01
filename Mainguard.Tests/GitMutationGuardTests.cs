using System;
using System.Collections.Generic;
using System.IO;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-P2-09 tests 3, 4, 6 (pure): the mutation guard's mid-rebase / detached-HEAD / in-progress-merge
/// verdicts and the <c>index.lock</c> exponential-backoff-then-typed-failure retry — the unit-testable
/// heart of the cooperative-yield contract. No process spawn, no Docker.
/// </summary>
public sealed class GitMutationGuardTests : IDisposable
{
    private readonly string _root;

    public GitMutationGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mainguard-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        // A well-formed symbolic HEAD unless a test overrides it.
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/agent/a1\n");
    }

    [Fact]
    public void Guard_MidRebase_Skips_ZeroMutations()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "rebase-merge"));

        var verdict = GitMutationGuard.CanMutate(GitMutationGuard.Inspect(_root));

        Assert.False(verdict.CanMutate);
        Assert.Equal(MutationVerdictKind.SkipRebaseInProgress, verdict.Kind);
    }

    [Fact]
    public void Guard_RebaseApplyDir_AlsoSkips()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "rebase-apply"));

        Assert.Equal(MutationVerdictKind.SkipRebaseInProgress,
            GitMutationGuard.CanMutate(GitMutationGuard.Inspect(_root)).Kind);
    }

    [Fact]
    public void Guard_DetachedHead_Skips()
    {
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0\n");

        var verdict = GitMutationGuard.CanMutate(GitMutationGuard.Inspect(_root));

        Assert.False(verdict.CanMutate);
        Assert.Equal(MutationVerdictKind.SkipDetachedHead, verdict.Kind);
    }

    [Fact]
    public void Guard_InProgressMerge_Skips()
    {
        File.WriteAllText(Path.Combine(_root, ".git", "MERGE_HEAD"), "deadbeef\n");

        Assert.Equal(MutationVerdictKind.SkipMergeInProgress,
            GitMutationGuard.CanMutate(GitMutationGuard.Inspect(_root)).Kind);
    }

    [Fact]
    public void Guard_CleanWorktree_Allows()
    {
        var verdict = GitMutationGuard.CanMutate(GitMutationGuard.Inspect(_root));

        Assert.True(verdict.CanMutate);
        Assert.Equal(MutationVerdictKind.Allow, verdict.Kind);
    }

    [Fact]
    public void RunGuarded_LockReleasedOnThirdTry_Succeeds()
    {
        var attempts = 0;
        var sleeps = new List<TimeSpan>();
        var actionRuns = 0;

        // Held on attempts 1 and 2, clear on attempt 3.
        bool IsLockHeld() => ++attempts <= 2;

        var result = GitMutationGuard.RunGuarded(
            new ActiveToken(),
            IsLockHeld,
            () => { actionRuns++; return 42; },
            sleep: sleeps.Add);

        Assert.Equal(42, result);
        Assert.Equal(1, actionRuns);
        Assert.Equal(2, sleeps.Count); // backed off twice before the third probe cleared
    }

    [Fact]
    public void RunGuarded_LockNeverReleases_ThrowsTypedAfterCap()
    {
        var actionRuns = 0;

        var ex = Assert.Throws<GitMutationLockException>(() => GitMutationGuard.RunGuarded(
            new ActiveToken(),
            isLockHeld: () => true,
            action: () => { actionRuns++; return 0; },
            sleep: _ => { }));

        Assert.Equal(0, actionRuns); // the action never ran under a persistent lock
        Assert.Contains("index.lock", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunGuarded_WithoutActiveToken_Refuses()
    {
        // Invariant 2 (API shape): a resumed/inactive token cannot reach a worktree mutation.
        Assert.Throws<InvalidOperationException>(() => GitMutationGuard.RunGuarded(
            new ActiveToken(active: false),
            isLockHeld: () => false,
            action: () => 0,
            sleep: _ => { }));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Never fail a test from cleanup.
        }
    }

    /// <summary>A minimal <see cref="IYieldToken"/> stand-in for the guard's API-shape check.</summary>
    private sealed class ActiveToken : IYieldToken
    {
        private readonly bool _active;

        public ActiveToken(bool active = true) => _active = active;

        public string AgentId => "a1";

        public bool IsActive => _active;

        public YieldOutcome Outcome => YieldOutcome.ByReady;

        public void Resume() { }

        public void ReleaseWithoutResuming() { }

        public void Dispose() { }
    }
}
