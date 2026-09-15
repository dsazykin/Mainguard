namespace Mainguard.Agents.UI.ViewModels;

/// <summary>Which question the agent-action card is asking.</summary>
public enum AgentActionKind
{
    /// <summary>Type a name for this agent.</summary>
    Rename,

    /// <summary>End the session: sandbox torn down, branch KEPT.</summary>
    End,

    /// <summary>Delete the agent and its work, branch included.</summary>
    Delete,
}

/// <summary>
/// One pending question about one agent, with the sentence a human decides on.
///
/// <para><b>The consequence sentence is a field, not something the view composes.</b> The three
/// actions here differ in exactly the way a human needs told and a dialog is worst at conveying:
/// ending keeps the work, deleting destroys it, renaming touches nothing at all. A shared "are you
/// sure?" over a variable verb would make the destructive one look like the recoverable one — so each
/// carries its own words, written where the difference is known.</para>
/// </summary>
/// <param name="Kind">Which question.</param>
/// <param name="AgentId">The agent it is about.</param>
/// <param name="Label">What that agent is called, for the title line.</param>
/// <param name="Title">The question, as a heading.</param>
/// <param name="Message">What will happen, in full.</param>
/// <param name="ConfirmText">The affirmative button's label — a verb, never "OK", so the button says
/// what it does even when read on its own.</param>
/// <param name="IsDestructive">Whether the confirm button is styled as destructive.</param>
/// <param name="NeedsInput">Whether the card shows a text box (rename only).</param>
public sealed record AgentActionPrompt(
    AgentActionKind Kind,
    string AgentId,
    string Label,
    string Title,
    string Message,
    string ConfirmText,
    bool IsDestructive,
    bool NeedsInput)
{
    public static AgentActionPrompt Rename(string agentId, string label) => new(
        AgentActionKind.Rename,
        agentId,
        label,
        $"Rename {label}",
        "Clearing the box puts the agent back on its own name — the brief it is working to, or its "
        + "role and short id. The name is remembered for this repository only, and only for this agent.",
        "Rename",
        IsDestructive: false,
        NeedsInput: true);

    public static AgentActionPrompt End(string agentId, string label) => new(
        AgentActionKind.End,
        agentId,
        label,
        $"End {label}?",
        // Says what SURVIVES, because that is the half a human is uncertain about and the half that
        // makes this different from Delete.
        "Its work is rejected and its sandbox is torn down, which frees the slot it is holding against "
        + "the worker cap. Its branch is kept, so its commits are still there.",
        "End task",
        IsDestructive: true,
        NeedsInput: false);

    public static AgentActionPrompt Delete(string agentId, string label, string branch) => new(
        AgentActionKind.Delete,
        agentId,
        label,
        $"Delete {label}?",
        // Names the branch, because "its branch" is abstract and `agent/70b21c13` is the thing that is
        // about to stop existing. Ends on the irreversibility rather than opening with it: the reader
        // needs to know what is destroyed before being told it cannot be undone.
        $"Its sandbox is torn down, its merge-queue entry is removed, its worktree is deleted, and the "
        + $"branch {branch} is deleted with it. Any commits on that branch that were never merged are "
        + "lost. This cannot be undone from here.",
        "Delete agent",
        IsDestructive: true,
        NeedsInput: false);
}
