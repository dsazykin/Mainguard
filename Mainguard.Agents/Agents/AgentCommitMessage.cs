using System;

namespace Mainguard.Agents.Agents;

/// <summary>
/// The one place a worker-supplied commit message is normalised and judged, before it becomes a commit
/// in the user's history.
///
/// <para><b>Defect G4 — what this replaces.</b> The message used to be flattened into a "subject": every
/// newline replaced by a space, then cut at 200 characters, <i>mid-word</i>, with the caller reporting
/// success. A worker that wrote a subject, a blank line and two body paragraphs got one 200-character
/// run-on line and an empty <c>%b</c>; two of three commits in a stress run were destroyed this way. The
/// comment defending it said a newline "would turn the rest into a body nobody chose" — which is exactly
/// backwards. A commit message IS a subject and a body, separated by a blank line; that structure is
/// git's, every tool downstream splits on it, and it is the durable record of what an agent did and the
/// thing a human reads at review. There is nothing to protect the history from except a message that
/// cannot be honoured.</para>
///
/// <para><b>So the rule is git's own convention, enforced rather than approximated.</b> First line is the
/// subject; if there is more, the second line is blank and everything after it is the body. A message
/// that does not fit that shape is <b>refused, with the reason</b>, and the worker — which is an agent
/// that can read a sentence and try again — rewrites it. Refusing costs a turn. Mangling costs the
/// record, silently, and nobody finds out until a human is reading the log.</para>
///
/// <para><b>What is NOT refused: nothing at all.</b> An absent or blank message still commits under a
/// default naming the agent (<see cref="DefaultFor"/>). That is not a mangle — no structure is being
/// discarded — and §11.2's reasoning stands: refusing there would lose the work, which is the defect the
/// whole <c>commit_work</c> op exists to fix.</para>
/// </summary>
public static class AgentCommitMessage
{
    /// <summary>
    /// The longest subject line accepted, refused past rather than truncated.
    ///
    /// <para>72 is git's own convention — the width <c>git log</c> can show without wrapping, and the
    /// number every commit-message guide states. It is deliberately SMALLER than the 200 it replaces:
    /// the old number was not a limit anyone had chosen, it was the point at which the string was cut,
    /// and raising it would only move where a message gets destroyed. A subject that does not fit in 72
    /// characters is a subject with a body in it, and the refusal says so.</para>
    /// </summary>
    public const int MaxSubjectLength = 72;

    /// <summary>
    /// The longest whole message accepted. Generous — a body is prose and prose is the point — but
    /// bounded, because this string arrives from inside a sandbox and travels the IPC channel, whose own
    /// request ceiling is 64 KiB (<c>AgentIpcPaths.MaxOutboxRequestBytes</c>). A message past this is
    /// refused with its size named, never silently shortened.
    /// </summary>
    public const int MaxLength = 8 * 1024;

    /// <summary>The message recorded when a worker supplies none. Names the agent, so a reader of the
    /// user's history can tell what produced the commit without consulting anything else.</summary>
    public static string DefaultFor(string agentId) => $"wip: work by agent {agentId}";

    /// <summary>
    /// The message as it will be recorded: CRLF folded to LF, trailing whitespace removed from every
    /// line, and leading/trailing blank lines dropped. Nothing else — no reflowing, no collapsing of
    /// blank lines, no truncation. What survives this is what git is asked to record <b>verbatim</b>.
    /// </summary>
    public static string Normalize(string? message)
    {
        var text = (message ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd();
        }

        var first = 0;
        var last = lines.Length - 1;
        while (first <= last && lines[first].Length == 0)
        {
            first++;
        }

        while (last >= first && lines[last].Length == 0)
        {
            last--;
        }

        if (first > last)
        {
            return string.Empty;
        }

        // The SUBJECT loses its leading whitespace too; a body's does not. A headline that starts with
        // spaces is a slip and shows as one in `git log`, while indentation inside a body is the worker's
        // — a code block, a list, a quoted diff — and reflowing it would be exactly the kind of quiet
        // repair this class exists to stop.
        lines[first] = lines[first].TrimStart();
        return string.Join('\n', lines[first..(last + 1)]);
    }

    /// <summary>
    /// Why this message cannot be recorded, or null when it can. Runs on the ALREADY-NORMALISED text, so
    /// a caller cannot be judged for whitespace this class was going to remove anyway.
    ///
    /// <para>Every refusal names the offending value and the rule, because the reader is an agent that
    /// gets one chance to correct itself: a refusal it cannot act on costs the same work a mangle does.</para>
    /// </summary>
    public static string? Refuse(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);

        if (normalized.Length == 0)
        {
            return null; // an absent message is a default, not a refusal — see the class remarks
        }

        if (normalized.Length > MaxLength)
        {
            return $"the commit message is {normalized.Length} characters; the limit is {MaxLength}. "
                 + "Nothing was committed — shorten it rather than losing the end of it.";
        }

        foreach (var c in normalized)
        {
            if (char.IsControl(c) && c is not ('\n' or '\t'))
            {
                return "the commit message contains a control character (U+"
                     + ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)
                     + "). Nothing was committed — send plain text, with newlines and tabs only.";
            }
        }

        var lines = normalized.Split('\n');
        var subject = lines[0];
        if (subject.Length > MaxSubjectLength)
        {
            return $"the first line of a commit message is its SUBJECT and must be at most "
                 + $"{MaxSubjectLength} characters; yours is {subject.Length}. Nothing was committed — "
                 + "shorten the first line and put the detail in a body: a subject, a BLANK line, then "
                 + "as much prose as you like.";
        }

        // Git's structure, enforced rather than assumed. Without the blank line `git log --format=%s`
        // folds every following line into the subject and `%b` is empty — which is silently the same
        // outcome as the flattening this class exists to remove, just produced by git instead.
        if (lines.Length > 1 && lines[1].Length != 0)
        {
            return "the line after the subject must be BLANK — that is what separates a commit's subject "
                 + $"from its body. Line 2 was \"{Ellipsize(lines[1])}\". Nothing was committed.";
        }

        return null;
    }

    private static string Ellipsize(string line) =>
        line.Length <= 40 ? line : line[..40] + "…";
}
