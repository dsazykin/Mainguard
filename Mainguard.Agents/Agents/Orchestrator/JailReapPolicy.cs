using System;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>Why a jail is being reaped — the audit event's vocabulary.</summary>
public enum JailReapCause
{
    /// <summary>Not reaped.</summary>
    None,

    /// <summary>The merge-queue entry is terminal (Merged / Rejected / Discarded): the work has left the jail.</summary>
    EntryTerminal,

    /// <summary>No CLI has been bound to the jail for longer than the idle allowance.</summary>
    IdleWithoutCli,
}

/// <summary>The reaper's answer for one jail.</summary>
public sealed record JailReapVerdict(bool Reap, JailReapCause Cause, string Reason)
{
    public static JailReapVerdict Keep { get; } = new(false, JailReapCause.None, string.Empty);
}

/// <summary>
/// Which jails the daemon stops on its own (owner decision, 2026-09-04). Pure, so the rule is testable
/// without a daemon: every input is a fact the reaper already holds.
///
/// <para><b>Why this exists.</b> A jail was only ever removed by a human pressing Stop. Orphans adopted
/// after a daemon restart, workers whose entry had merged, agents whose CLI had exited, and every jail
/// left behind when the app closed on macOS all ran until Docker itself died — twenty of them at 2 GiB
/// each is the 26 GB an owner measured. Two rules, and only two: the work has provably left the jail, or
/// nothing has been able to type into it for a long time. A jail with a live CLI is never touched here,
/// whatever it is doing, because stopping one kills the conversation inside it.</para>
/// </summary>
public static class JailReapPolicy
{
    /// <param name="entryState">The jail's merge-queue entry state, or null when it has no entry (a coordinator, a
    /// repo with no queue).</param>
    /// <param name="hasLiveCli">A CLI is bound to the jail's PTY and has not exited.</param>
    /// <param name="idleSince">When the reaper first saw this jail with no live CLI; null while it has one.</param>
    /// <param name="now">The reaper's clock.</param>
    /// <param name="idleAllowance"><see cref="CoordinatorLimits.IdleJailReapMinutes"/> as a span.</param>
    public static JailReapVerdict Decide(
        WorkerMergeState? entryState, bool hasLiveCli, DateTimeOffset? idleSince, DateTimeOffset now, TimeSpan idleAllowance)
    {
        if (entryState is WorkerMergeState.Merged or WorkerMergeState.Rejected or WorkerMergeState.Discarded)
        {
            return new JailReapVerdict(true, JailReapCause.EntryTerminal,
                $"its merge-queue entry is {entryState} — the work has left the jail");
        }

        if (!hasLiveCli && idleSince is { } since && now - since >= idleAllowance)
        {
            return new JailReapVerdict(true, JailReapCause.IdleWithoutCli,
                $"no CLI has been bound to it for {(int)(now - since).TotalMinutes} min");
        }

        return JailReapVerdict.Keep;
    }
}
