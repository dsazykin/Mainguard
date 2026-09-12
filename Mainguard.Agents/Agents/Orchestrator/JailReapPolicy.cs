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
    /// <param name="terminalLostToRestart">
    /// This jail was ADOPTED by the reconciler — it outlived the daemon that started it — and no CLI has
    /// bound to it since. That is the one case where "no CLI is bound" is a fact about the daemon rather
    /// than about the jail, and it is the only case the in-flight exemption below applies to.
    /// <b>False for an ordinary jail</b>, including one whose worker finished and is waiting for a human:
    /// that jail's missing CLI means the CLI is missing.
    /// </param>
    public static JailReapVerdict Decide(
        WorkerMergeState? entryState,
        bool hasLiveCli,
        DateTimeOffset? idleSince,
        DateTimeOffset now,
        TimeSpan idleAllowance,
        bool terminalLostToRestart = false)
    {
        if (entryState is WorkerMergeState.Merged or WorkerMergeState.Rejected or WorkerMergeState.Discarded)
        {
            return new JailReapVerdict(true, JailReapCause.EntryTerminal,
                $"its merge-queue entry is {entryState} — the work has left the jail");
        }

        if (!hasLiveCli && idleSince is { } since && now - since >= idleAllowance)
        {
            // F59, SCOPED. "No CLI is bound" is not the same fact as "nothing is happening" — but that is
            // only true of one population, and the first cut of this rule exempted every in-flight entry
            // instead.
            //
            // The population it is true of: a jail the reconciler ADOPTED after a daemon restart. Its PTY
            // belonged to the process that died and Docker has no re-attach for a running exec, so it
            // reads as "no CLI" from the moment the daemon comes back while the agent inside it keeps
            // working. Reaping that at the allowance is the silent mid-task termination F59 removed from
            // the shutdown path, arriving thirty minutes later by another door, and it destroys
            // uncommitted work — a bad trade in a way the reverse is not, since a jail left running costs
            // memory an operator reclaims with Stop.
            //
            // The population it is NOT true of, and which the unscoped rule swallowed whole: the ordinary
            // worker that finished, whose CLI exited, and whose entry sits in AwaitingReview until a human
            // looks at it. Nothing is happening in that jail and nothing will; exempting it kept every
            // finished worker alive forever, which is precisely the twenty-jails-at-2-GiB population this
            // reaper was written against. It reaps at the allowance, as it always did.
            //
            // RESIDUAL, stated rather than hidden: an adopted jail whose CLI genuinely exited while the
            // daemon could not observe it is kept here indefinitely. That is the population re-binding a
            // CLI on adoption fixes — re-binding is what restores the daemon's ability to see the exit and
            // mark the session Dead, at which point this is no longer an adopted-without-terminal jail at
            // all. Until then rule 1 above still reaps it the moment its entry reaches a terminal state.
            if (terminalLostToRestart && IsInFlight(entryState))
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
