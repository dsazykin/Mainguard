using System;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Xunit;

namespace Mainguard.Tests;

/// <summary>The reaper's two rules (2026-09-04), and the one thing it must never do: touch a jail with a live CLI.</summary>
public sealed class JailReapPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Allowance = TimeSpan.FromMinutes(30);

    [Theory]
    [InlineData(WorkerMergeState.Merged)]
    [InlineData(WorkerMergeState.Rejected)]
    [InlineData(WorkerMergeState.Discarded)]
    public void ATerminalEntry_IsReaped_EvenWithALiveCli(WorkerMergeState state)
    {
        var verdict = JailReapPolicy.Decide(state, hasLiveCli: true, idleSince: null, T0, Allowance);
        Assert.True(verdict.Reap);
        Assert.Equal(JailReapCause.EntryTerminal, verdict.Cause);
        Assert.Contains(state.ToString(), verdict.Reason);
    }

    [Theory]
    [InlineData(WorkerMergeState.Working)]
    [InlineData(WorkerMergeState.Verified)]
    [InlineData(WorkerMergeState.StaleVerified)]
    [InlineData(WorkerMergeState.VerificationFailed)]
    [InlineData(null)]
    public void ALiveCli_IsNeverReaped_WhateverTheEntrySays(WorkerMergeState? state)
    {
        Assert.False(JailReapPolicy.Decide(state, hasLiveCli: true, idleSince: null, T0.AddDays(1), Allowance).Reap);
    }

    [Fact]
    public void NoCli_IsKeptUntilTheAllowance_ThenReaped()
    {
        Assert.False(JailReapPolicy.Decide(null, false, T0, T0.AddMinutes(29), Allowance).Reap);
        var verdict = JailReapPolicy.Decide(null, false, T0, T0.AddMinutes(30), Allowance);
        Assert.True(verdict.Reap);
        Assert.Equal(JailReapCause.IdleWithoutCli, verdict.Cause);
        Assert.Contains("30 min", verdict.Reason);
    }

    [Fact]
    public void NoCli_ButNeverObservedIdle_IsKept()
    {
        Assert.False(JailReapPolicy.Decide(WorkerMergeState.Working, false, idleSince: null, T0.AddDays(1), Allowance).Reap);
    }

    /// <summary>
    /// F59: the interaction the daemon-restart fix creates. An adopted jail has no bound CLI — its PTY
    /// belonged to the previous daemon process and Docker cannot re-attach a running exec — so it starts
    /// accruing idle time the moment the daemon comes back, while the agent inside it keeps working.
    /// Reaping it at the allowance would be the silent mid-task termination F59 removed from the shutdown
    /// path, arriving thirty minutes later by another door.
    /// </summary>
    [Theory]
    [InlineData(WorkerMergeState.Working)]
    [InlineData(WorkerMergeState.Verifying)]
    [InlineData(WorkerMergeState.Verified)]
    [InlineData(WorkerMergeState.StaleVerified)]
    [InlineData(WorkerMergeState.AwaitingReview)]
    [InlineData(WorkerMergeState.VerificationFailed)]
    public void AnAdoptedJail_WithWorkInFlight_IsNotReapedJustForHavingNoTerminal(WorkerMergeState state)
    {
        var verdict = JailReapPolicy.Decide(
            state, hasLiveCli: false, idleSince: T0, T0.AddDays(1), Allowance,
            terminalLostToRestart: true);
        Assert.False(verdict.Reap);
        Assert.Equal(JailReapCause.None, verdict.Cause);
    }

    /// <summary>
    /// The scope of that exemption, and the regression it was the first time. The very same entry states
    /// on a jail this daemon started itself are reaped at the allowance: there the missing CLI is a fact
    /// about the jail (its CLI exited), not about the daemon. AwaitingReview is the one that matters most
    /// — a worker that finished and is waiting for a human is the ordinary end state of every worker, and
    /// exempting it meant no finished jail was ever reclaimed again.
    /// </summary>
    [Theory]
    [InlineData(WorkerMergeState.Working)]
    [InlineData(WorkerMergeState.Verifying)]
    [InlineData(WorkerMergeState.Verified)]
    [InlineData(WorkerMergeState.StaleVerified)]
    [InlineData(WorkerMergeState.AwaitingReview)]
    [InlineData(WorkerMergeState.VerificationFailed)]
    public void AJailThisDaemonStarted_IsStillReapedWhenItsCliIsGone(WorkerMergeState state)
    {
        var verdict = JailReapPolicy.Decide(
            state, hasLiveCli: false, idleSince: T0, T0.AddMinutes(30), Allowance,
            terminalLostToRestart: false);
        Assert.True(verdict.Reap);
        Assert.Equal(JailReapCause.IdleWithoutCli, verdict.Cause);
    }

    /// <summary>The exemption is about the terminal, not about the clock: an adopted jail inside the
    /// allowance is kept by the ordinary idle rule either way.</summary>
    [Fact]
    public void AnAdoptedJail_WithALiveCli_IsTreatedLikeAnyOther()
    {
        Assert.False(JailReapPolicy.Decide(
            WorkerMergeState.Working, hasLiveCli: true, idleSince: null, T0.AddDays(1), Allowance,
            terminalLostToRestart: true).Reap);
    }

    /// <summary>
    /// ...and the other side of the trade, which the idle rule exists for: a jail the daemon expects
    /// nothing further from — a coordinator, or a repo with no queue — is still reaped at the allowance.
    /// A fix that kept everything would have re-created the 26 GB of abandoned jails the rule was written
    /// against.
    /// </summary>
    [Fact]
    public void AJailWithNoEntry_IsStillReapedAtTheAllowance()
    {
        var verdict = JailReapPolicy.Decide(null, hasLiveCli: false, idleSince: T0, T0.AddMinutes(30), Allowance);
        Assert.True(verdict.Reap);
        Assert.Equal(JailReapCause.IdleWithoutCli, verdict.Cause);
    }

    /// <summary>A terminal entry is reaped by rule 1 regardless — the in-flight guard never reaches it.</summary>
    [Theory]
    [InlineData(WorkerMergeState.Merged)]
    [InlineData(WorkerMergeState.Rejected)]
    [InlineData(WorkerMergeState.Discarded)]
    public void ATerminalEntry_WithNoCli_IsStillReaped(WorkerMergeState state)
    {
        var verdict = JailReapPolicy.Decide(state, hasLiveCli: false, idleSince: T0, T0.AddMinutes(30), Allowance);
        Assert.True(verdict.Reap);
        Assert.Equal(JailReapCause.EntryTerminal, verdict.Cause);
    }
}
