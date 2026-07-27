using System;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.UI.Editions;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The one place the human "Merge to Main" action is driven from a surface, so that every outcome — the
/// merge, and each of the ways it can refuse — becomes one visible sentence.
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
            await queue.ConfirmMergeAsync(agentId).ConfigureAwait(false);
            sink($"Merged agent/{agentId} into main.", false);
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

    private static void DefaultReport(string message, bool isWarning)
    {
        // Marshalled to the UI thread: this runs on whatever thread the merge finished on.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ProComposition.ShowShellToast(message, isWarning));
    }
}
