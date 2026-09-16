using System.Threading.Tasks;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// What an agent row's context menu can ask for. Implemented by the control centre, which owns the
/// daemon seams, the confirmations and the toast stack.
///
/// <para><b>Why a seam rather than the row calling the services.</b> Half of these actions are not
/// single calls: ending and deleting need a human's confirmation first, and a confirmation is a piece
/// of surface state that belongs to the panel, not to a list item that can be recycled out from under
/// it. Keeping the row a pure projection also keeps the render harnesses able to lay the menu out
/// without a daemon — they pass no implementation at all.</para>
///
/// <para>Nothing here returns a result. Every outcome a human needs to see (a refusal, a deleted
/// branch's sha, a copied value) is reported by the implementation through the surfaces that already
/// carry such things, so a row never has to decide how to phrase a failure.</para>
/// </summary>
public interface IAgentRowActions
{
    /// <summary>Show this agent's workspace — the same thing clicking the row does, offered in the
    /// menu because a right-click that cannot do the obvious thing reads as a broken menu.</summary>
    void Open(string agentId);

    /// <summary>Pause a running agent's jail, or unpause a paused one. Which of the two is decided
    /// from live state by the implementation, never from the label the row happened to render.</summary>
    Task PauseOrResumeAsync(string agentId);

    /// <summary>Open the rename prompt for this agent (the typing happens on the panel).</summary>
    void BeginRename(string agentId);

    /// <summary>Drop the human's name, returning the row to its derived one.</summary>
    void ResetName(string agentId);

    /// <summary>Open this agent's branch-vs-integration diff in the review cockpit — which is where
    /// the Merge button lives, and was previously reachable only from the merge-queue rail.</summary>
    void Review(string agentId);

    /// <summary>Ask the daemon to verify this agent's branch now, in its own jail.</summary>
    Task VerifyAsync(string agentId);

    /// <summary>Show the output of the last verification, without paying for another run.</summary>
    Task ViewVerificationLogAsync(string agentId);

    /// <summary>Put <paramref name="value"/> on the system clipboard.</summary>
    Task CopyAsync(string value);

    /// <summary>Ask the human whether to END this agent: its sandbox is torn down, its branch KEPT.</summary>
    void ConfirmEnd(string agentId);

    /// <summary>Ask the human whether to DELETE this agent: its sandbox, its queue entry, its worktree
    /// AND its branch. The implementation's confirmation must say that the commits go — this is the one
    /// action on the menu that destroys work.</summary>
    void ConfirmDelete(string agentId);
}
