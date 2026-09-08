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
            // F59: "no CLI is bound" is not the same fact as "nothing is happening", and conflating them
            // became dangerous the moment a daemon restart stopped killing the agents.
            //
            // Before F59, container disposal killed every bound CLI on shutdown, so a jail the daemon
            // reattached to after a restart really was dead weight and reaping it at the idle allowance
            // was right. Now the CLI survives — the jail keeps working — while the PTY it was started
            // with belonged to the old daemon process and cannot be reattached (Docker has no re-attach
            // for a running exec). So every adopted jail reads as "no CLI" from the moment the daemon
            // comes back, and the reaper would stop a mid-task agent half an hour later, destroying
            // uncommitted work: the same silent termination F59 removed from the shutdown path, arriving
            // 30 minutes later by another door.
            //
            // The daemon's own belief about the entry is the check that separates the two. An entry it
            // considers in flight is one it expects output from; killing that jail because the daemon
            // lost its terminal is a bad trade in a way the reverse is not — a jail left running costs
            // memory the operator reclaims with Stop, while a jail reaped mid-task costs work nobody can
            // get back.
            //
            // RESIDUAL, stated rather than hidden: a jail whose CLI genuinely exited while the daemon
            // could not observe it (again, the restart case — the exit watcher died with the PTY) keeps
            // an in-flight entry forever and is never reaped here. That is the population the CLI
            // re-bind-on-adoption work fixes, because re-binding is what restores the daemon's ability
            // to see the CLI exit and mark the session Dead. Until then it is the deliberate side of the
            // trade, and rule 1 above still reaps the entry the moment it reaches a terminal state.
            if (IsInFlight(entryState))
            {
                return JailReapVerdict.Keep;
            }

            return new JailReapVerdict(true, JailReapCause.IdleWithoutCli,
                $"no CLI has been bound to it for {(int)(now - since).TotalMinutes} min");
        }

        return JailReapVerdict.Keep;
    }

    /// <summary>
    /// Whether the daemon still expects this entry to produce work. <c>null</c> — a coordinator, or a
    /// repo with no queue — is NOT in flight: those hold no branch and no unmerged work, and they are a
    /// large part of the population the idle rule exists for.
    /// </summary>
    private static bool IsInFlight(WorkerMergeState? entryState) => entryState is
        WorkerMergeState.Working
        or WorkerMergeState.Verifying
        or WorkerMergeState.Verified
        or WorkerMergeState.StaleVerified
        or WorkerMergeState.AwaitingReview
        or WorkerMergeState.VerificationFailed;
}
