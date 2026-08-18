using System;
using Mainguard.Git.Services;

namespace Mainguard.Agents.Services;

/// <summary>The outcome of a bring-local: <see cref="Done"/> with the created/advanced
/// <see cref="LocalBranch"/>, or a render-verbatim <see cref="Reason"/>. Never both.</summary>
public sealed record BringLocalResult(bool Done, string? LocalBranch, string Reason)
{
    public static BringLocalResult Success(string branch) => new(true, branch, "");
    public static BringLocalResult Refused(string reason) => new(false, null, reason);
}

/// <summary>
/// The review cockpit's <b>Bring local</b>: fetch an agent's <c>agent/&lt;id&gt;</c> branch from the
/// sync remote into the user's own checkout as a LOCAL branch, so a reviewer can open the work in
/// their editor while the agent keeps working. This is the handoff the cockpit button always claimed
/// to perform — for the whole of its life before this class the button was bound to a null delegate
/// and did nothing, silently, which is the exact failure <c>MergeActionRunner</c>'s doc names.
///
/// <para><b>Same discipline as <see cref="ForegroundMergeService"/></b>: every git exit code is read,
/// every refusal is a phrased reason, and the ref move is journaled (T-19, <see cref="JournalKinds.CreateBranch"/>)
/// so a mistaken bring-local is undoable like any branch operation. The local update is a NON-forced
/// <c>git fetch . remote-ref:local-ref</c>: it creates a missing branch, fast-forwards an existing
/// one, and REFUSES a diverged or checked-out one with git's own stderr — bringing a branch local
/// must never rewrite local work that happens to share its name.</para>
/// </summary>
public sealed class BringLocalService
{
    private readonly IOperationJournal _journal;

    public BringLocalService(IOperationJournal journal)
        => _journal = journal ?? throw new ArgumentNullException(nameof(journal));

    /// <param name="repoPath">The user's checkout (the same path the foreground merge lands on).</param>
    /// <param name="syncRemoteName">The SC-2-resolved sync remote registered on that checkout.</param>
    /// <param name="agentId">The agent whose <c>agent/&lt;id&gt;</c> branch is brought local.</param>
    public BringLocalResult BringLocal(string repoPath, string syncRemoteName, string agentId)
    {
        var branch = $"agent/{agentId}";

        // (1) Fetch exactly this branch — an explicit refspec, so an unrelated fetch failure elsewhere
        // in the remote can't be mistaken for this branch being absent.
        var (fetchCode, _, fetchErr) = GitService.RunGit(
            repoPath, "fetch", syncRemoteName, $"+refs/heads/{branch}:refs/remotes/{syncRemoteName}/{branch}");
        if (fetchCode != 0)
        {
            // An explicit refspec for a branch the remote doesn't have fails the whole fetch — that
            // case is "the agent hasn't published yet", not a transport error, and the reason says so.
            return BringLocalResult.Refused(
                (fetchErr ?? "").Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase)
                    ? $"'{branch}' isn't on '{syncRemoteName}' yet — the agent hasn't published it"
                    : $"couldn't fetch '{branch}' from '{syncRemoteName}' ({FirstLine(fetchErr)})");
        }

        // (2) The remote-tracking ref must exist — a fetch of a nonexistent branch can exit 0.
        var (verifyCode, _, _) = GitService.RunGit(
            repoPath, "rev-parse", "--verify", "--quiet", $"refs/remotes/{syncRemoteName}/{branch}");
        if (verifyCode != 0)
        {
            return BringLocalResult.Refused(
                $"'{branch}' isn't on '{syncRemoteName}' yet — the agent hasn't published it");
        }

        // (3) The journaled, NON-forced local ref update. `git fetch . src:dst` (no leading +) creates
        // dst or fast-forwards it, and refuses anything else — a diverged local branch, or the branch
        // being checked out in a worktree — with a reason on stderr worth rendering verbatim.
        int code;
        string err;
        using (_journal.BeginOperation(repoPath, JournalKinds.CreateBranch, $"Bring {branch} local"))
        {
            (code, _, err) = GitService.RunGit(
                repoPath, "fetch", ".", $"refs/remotes/{syncRemoteName}/{branch}:refs/heads/{branch}");
        }

        if (code != 0)
        {
            return BringLocalResult.Refused(
                $"couldn't update local '{branch}' ({FirstLine(err)})");
        }

        return BringLocalResult.Success(branch);
    }

    private static string FirstLine(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        var nl = trimmed.IndexOf('\n');
        return nl < 0 ? trimmed : trimmed[..nl].Trim();
    }
}
