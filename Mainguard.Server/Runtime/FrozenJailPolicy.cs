using System;
using Mainguard.Agents.Agents.Orchestrator;

namespace Mainguard.Server.Runtime;

/// <summary>
/// Whether a worker's jail is FROZEN, and therefore whether anything the coordinator asks the daemon to
/// do <i>inside</i> it can possibly happen.
///
/// <para><b>The defect.</b> A merge whose auto-rebase conflicts leaves the worker <c>docker pause</c>d
/// with the rebase in progress (<c>KeepAliveRebaser</c> deliberately does not resume that jail). Every
/// other guard on <c>send_worker_prompt</c> — kill switch, ownership, empty text, the plan gate — kept
/// answering yes, so the prompt was typed into a channel inside a SIGSTOPped process that cannot read
/// it, the call returned <c>Ok</c>, and the coordinator sat polling a worker that could never answer.
/// The same hole was on <c>request_verification</c>, which runs the test command in that same jail.</para>
///
/// <para><b>The predicate is the state word, not the human-pause ledger.</b>
/// <see cref="HumanPauseLedger.IsHumanPaused"/> answers a narrower question — did a PERSON press pause —
/// and the conflict pause is the daemon's own, so that ledger says no for exactly the case this exists
/// for. The session's state word is what <c>AgentSpawnService.Row</c> and <c>ListAgents</c> already
/// project, so the refusal a coordinator gets and the <c>state=Paused</c> it can see agree by
/// construction rather than by coincidence. <see cref="AgentRunState.Conflict"/> counts too: it is
/// written by the one path that freezes a jail and leaves it frozen, and it is written seconds before
/// the reconciler's drift pass gets round to spelling the same fact <c>Paused</c>.</para>
/// </summary>
public static class FrozenJailPolicy
{
    /// <summary>The state word a conflicted keep-alive rebase leaves on the session.</summary>
    public static readonly string ConflictState = nameof(AgentRunState.Conflict);

    /// <summary>True when this session's jail is frozen and nothing delivered into it will run.</summary>
    public static bool IsFrozen(string? state) =>
        string.Equals(state, AgentSessionReconciler.PausedState, StringComparison.Ordinal)
        || string.Equals(state, ConflictState, StringComparison.Ordinal);

    /// <summary>Why it is frozen, in the words that tell the reader what has to happen next.</summary>
    private static string Why(string? state) =>
        string.Equals(state, ConflictState, StringComparison.Ordinal)
            ? "its keep-alive rebase onto the new main conflicted, so the daemon froze the jail with the "
              + "rebase still in progress and a human has to resolve it"
            : "the jail is frozen (a human paused it, or a conflicted keep-alive rebase did) and only a "
              + "human can resume it";

    /// <summary>
    /// The <c>send_worker_prompt</c> refusal — written, like the plan gate's, for the agent that receives
    /// it to read and act on in one turn. It says nothing was sent, why, and what the human must do; and
    /// it explicitly closes the loop the defect produced, which was a coordinator polling forever.
    /// </summary>
    public static string RefusePrompt(string workerId, string? state) =>
        $"{workerId} is paused, so nothing was sent: {Why(state)}. A frozen jail reads nothing — a prompt "
        + "delivered now would sit unread in a channel and this worker would never answer it. Report this "
        + $"to the human and move on: do not prompt {workerId} again and do not keep polling it until they "
        + "have resumed it.";

    /// <summary>The <c>request_verification</c> refusal — the same fact, about the op that would have run
    /// the test command inside that frozen jail.</summary>
    public static string RefuseVerification(string workerId, string? state) =>
        $"{workerId} is paused, so its branch cannot be verified: {Why(state)}. Verification runs the test "
        + "command inside that jail and a frozen jail runs nothing. Report this to the human rather than "
        + "asking again — verification becomes possible when they resume it.";

    /// <summary>
    /// The guard as every caller should ask it: the state word <b>or</b> the session store's pause axis.
    /// The word alone reopened the hole this policy closes — the merge queue rewrites it on every
    /// transition — so a frozen jail is frozen if EITHER says so (see <c>AgentSessionStore.MarkFrozen</c>).
    /// </summary>
    public static bool IsFrozen(string? state, string? frozenReason) =>
        IsFrozen(state) || !string.IsNullOrEmpty(frozenReason);

    private static string Why(string? state, string? frozenReason) =>
        !string.IsNullOrEmpty(frozenReason) ? frozenReason! : Why(state);

    /// <summary>The prompt refusal, with the pause axis's own reason when it has one.</summary>
    public static string RefusePrompt(string workerId, string? state, string? frozenReason) =>
        $"{workerId} is paused, so nothing was sent: {Why(state, frozenReason)}. A frozen jail reads nothing — a prompt "
        + "delivered now would sit unread in a channel and this worker would never answer it. Report this "
        + $"to the human and move on: do not prompt {workerId} again and do not keep polling it until they "
        + "have resumed it.";

    /// <summary>The verification refusal, with the pause axis's own reason when it has one.</summary>
    public static string RefuseVerification(string workerId, string? state, string? frozenReason) =>
        $"{workerId} is paused, so its branch cannot be verified: {Why(state, frozenReason)}. Verification runs the test "
        + "command inside that jail and a frozen jail runs nothing. Report this to the human rather than "
        + "asking again — verification becomes possible when they resume it.";
}
