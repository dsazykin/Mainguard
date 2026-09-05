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
}
