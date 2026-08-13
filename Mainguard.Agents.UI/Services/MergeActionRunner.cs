using System;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Services;
using Mainguard.Agents.UI.Editions;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The one place a human merge-queue action — "Merge to Main", and the entry-lifecycle actions beside it
/// — is driven from a surface, so that every outcome, and each of the ways it can refuse, becomes one
/// visible sentence.
///
/// <para><b>Why this exists.</b> Both merge surfaces invoked the merge as fire-and-forget
/// (<c>_ = _queue.ConfirmMergeAsync(id)</c>, and an un-awaited command body), which means every refusal
/// the daemon or the merge itself produced was thrown into an unobserved task and the button simply did
/// nothing. "Nothing happened" is the single worst answer a merge gate can give: it is indistinguishable
/// from a merge that silently succeeded, which is exactly how a queue drifts out of agreement with git.
/// A refusal is information the human asked for by pressing the button.</para>
///
/// <para>The reasons are rendered verbatim (§3.4 vocabulary) — they are the daemon's words, not a
/// re-worded summary — and a refusal is a warning toast, not an error dialog: nothing was damaged, the
/// queue is exactly as it was, and the human can act on the reason and press Merge again.</para>
///
/// <para><b>The confirmation is origin-aware too.</b> Every refusal already said which origin it came
/// from; the success line did not, so an external pull request merged upstream (P2-12) reported
/// <c>Merged agent/pr-7 into main</c> — the LOCAL fast-forward's shape, and a description of something
/// that did not happen. The two origins land a merge in two different places, so they get two
/// sentences.</para>
/// </summary>
public static class MergeActionRunner
{
    /// <summary>
    /// Drives one human merge to a reported conclusion. Never throws: the whole point is that the caller
    /// can invoke it from a command body without an exception disappearing into a dropped task.
    /// </summary>
    /// <param name="queue">The merge-queue seam (the shipped daemon-backed adapter in the real app).</param>
    /// <param name="agentId">The agent whose <c>agent/&lt;id&gt;</c> branch is merged.</param>
    /// <param name="report">(message, isWarning) sink; defaults to the shell's toast stack. Injected by tests.</param>
    public static async Task RunAsync(
        IMergeQueueService queue, string agentId, Action<string, bool>? report = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var sink = report ?? DefaultReport;

        try
        {
            var outcome = await queue.ConfirmMergeAsync(agentId).ConfigureAwait(false);
            sink(Confirmation(outcome), false);
        }
        catch (OperationCanceledException)
        {
            // The app is shutting down (the adapter's own token) — not a merge outcome to report.
        }
        catch (Exception ex)
        {
            // Refused by the gate, lease held elsewhere, lost CAS, not a fast-forward, dirty tree, daemon
            // unreachable — all of them arrive here as a reason, and all of them leave the queue unchanged.
            sink(ex.Message, true);
        }
    }

    /// <summary>
    /// Drives one human discard to a reported conclusion. Same contract as <see cref="RunAsync"/>: never
    /// throws, and the daemon's refusal is the sentence the human reads.
    ///
    /// <para>The daemon answers a refused discard with an ordinary successful RPC carrying
    /// <c>discarded=false</c>, so "no exception" is not evidence anything was removed — the adapter turns
    /// that into a throw and this turns the throw into a warning. Without both halves a refused discard
    /// would look exactly like a successful one until the next queue snapshot put the entry back.</para>
    /// </summary>
    public static async Task DiscardAsync(
        IMergeQueueService queue, string agentId, string reason, Action<string, bool>? report = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var sink = report ?? DefaultReport;

        try
        {
            var outcome = await queue.DiscardEntryAsync(agentId, reason ?? string.Empty).ConfigureAwait(false);
            sink(DiscardConfirmation(outcome), false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down — not an outcome to report.
        }
        catch (Exception ex)
        {
            sink(ex.Message, true);
        }
    }

    /// <summary>Drives one "clear the stalled verification" to a reported conclusion. Never throws.</summary>
    public static async Task ClearStalledVerificationAsync(
        IMergeQueueService queue, string agentId, Action<string, bool>? report = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var sink = report ?? DefaultReport;

        try
        {
            await queue.ClearStalledVerificationAsync(agentId).ConfigureAwait(false);
            sink($"Cleared the stalled verification on agent/{agentId} — it can be verified again.", false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            sink(ex.Message, true);
        }
    }

    /// <summary>
    /// Drives one human resume to a reported conclusion. Never throws; the daemon's refusal — "the branch
    /// no longer exists", "this entry already has a live agent", "a merge is in progress" — is the sentence
    /// the human reads, verbatim.
    /// </summary>
    public static async Task ResumeAsync(
        IMergeQueueService queue, string agentId, string agentKind, Action<string, bool>? report = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var sink = report ?? DefaultReport;

        try
        {
            var outcome = await queue.ResumeEntryAsync(agentId, agentKind ?? string.Empty).ConfigureAwait(false);
            sink(ResumeConfirmation(outcome), false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            sink(ex.Message, true);
        }
    }

    /// <summary>
    /// The one visible sentence a resume produces. It says what actually changed: the entry kept its
    /// identity and its branch, and it now has a sandbox again — never "restarted the agent", which would
    /// describe a new agent picking up where none left off.
    ///
    /// <para>A cleared stalled verification is stated in the same breath. It is a state change the human did
    /// not directly ask for, and an entry that silently stopped saying "Verifying" is precisely the kind of
    /// unexplained transition that makes a queue untrustworthy.</para>
    /// </summary>
    internal static string ResumeConfirmation(QueueEntryResumeOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var cleared = outcome.ClearedStalledVerification
            ? " Its stalled verification was cleared, so it can be verified again."
            : "";
        return $"Resumed {outcome.Branch} in a new sandbox — the entry keeps its id and its commits, "
            + $"and is {outcome.State}.{cleared}";
    }

    /// <summary>
    /// The one visible sentence a discard produces. It says <b>dropped from the merge queue</b> and
    /// nothing more — the branch and its commits are untouched, and a sentence like "removed the work"
    /// would describe something that did not happen. The attributed actor is included because that is what
    /// was recorded.
    /// </summary>
    internal static string DiscardConfirmation(QueueEntryDiscardOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var by = string.IsNullOrWhiteSpace(outcome.DiscardedBy) ? "" : $" (recorded as {outcome.DiscardedBy})";
        return $"Dropped agent/{outcome.AgentId} from the merge queue{by}. "
            + "The branch and its commits are untouched.";
    }

    /// <summary>
    /// The one visible sentence a landed merge produces, in the terms of the origin that landed it.
    ///
    /// <para><b>Local</b> — "Merged agent/x into main." The user's own <c>refs/heads/main</c> was
    /// fast-forwarded onto the agent branch, which is exactly what the sentence describes.</para>
    ///
    /// <para><b>External</b> — "Merged pull request #7 upstream — main fast-forwarded onto a1b2c3d." Both
    /// halves are said because both halves are what P2-12 guarantees, and neither alone is the whole truth:
    /// the merge was performed by the HOST (this app never fast-forwards a bot PR's mirrored branch into
    /// main and calls it merged), and the checkout then converged onto it. It is stated as fact only
    /// because nothing reaches here otherwise — the executor records a merge only once the host named a
    /// merge commit, that commit was proven reachable from the host remote's main by
    /// <c>merge-base --is-ancestor</c> after a fetch, and local main fast-forwarded onto it. Upstream is
    /// authoritative, not trusted; <paramref name="outcome"/>'s sha is the one main really moved to.</para>
    /// </summary>
    internal static string Confirmation(MergeOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        // External is the case that must never wear the local sentence, so it is the case named
        // explicitly — the same shape MergeDispatch routes the transports with.
        if (outcome.Origin == MergeEntryOrigin.External)
        {
            // The number IS the pull request (the queue key and the PR it merges cannot disagree). An id
            // that names none can't reach here — the external executor refuses it before calling the host —
            // but the fallback stays inside the external sentence rather than borrowing the local one.
            var pullRequest = ExternalPrMergeService.PrNumberFor(outcome.AgentId) is int number
                ? $"pull request #{number}"
                : "the pull request";

            return $"Merged {pullRequest} upstream — {outcome.MainBranch} fast-forwarded onto "
                + $"{ShortSha(outcome.NewMainSha)}.";
        }

        return $"Merged agent/{outcome.AgentId} into {outcome.MainBranch}.";
    }

    /// <summary>7-char sha, the display form every other Mainguard surface uses.</summary>
    private static string ShortSha(string sha) =>
        string.IsNullOrEmpty(sha) || sha.Length <= 7 ? sha : sha[..7];

    private static void DefaultReport(string message, bool isWarning)
    {
        // Marshalled to the UI thread: this runs on whatever thread the merge finished on.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ProComposition.ShowShellToast(message, isWarning));
    }
}
