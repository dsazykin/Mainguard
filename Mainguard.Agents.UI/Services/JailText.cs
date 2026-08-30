using System;
using System.Text;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// Renders text that came out of a sandbox safe to display.
///
/// <para><b>Why this exists on the client.</b> The daemon already refuses to let jail-supplied text reach a
/// log line raw — <c>AgentIpcServer.Echo</c> drops control characters and bounds the length before anything
/// is logged, and <c>AgentCommitMessage</c> rejects a message carrying one outright. <c>GetVerificationLog</c>
/// is the one path that hands jail bytes straight to a human surface: it returns an artifact written by a
/// test runner executing inside the worker's own jail, and that runner emits whatever it likes — ANSI colour
/// from a coloured reporter, carriage-return progress bars that redraw a line, cursor moves, bell characters,
/// and on a corrupt read, NULs. So the same discipline applies at the point the client turns those bytes into
/// something a person reads.</para>
///
/// <para><b>What it keeps.</b> Newlines and tabs, because in a test log they are structure, not noise —
/// exactly the pair <c>AgentCommitMessage</c> permits. Everything else that is a control character becomes a
/// visible <c>.</c>, which is <c>Echo</c>'s substitution: dropping them silently would let a stack trace lose
/// characters without saying so.</para>
///
/// <para><b>Escape sequences are removed as sequences, not as characters.</b> Replacing the ESC of
/// <c>ESC[31m</c> one character at a time leaves <c>.[31m</c> smeared through the output — worse to read than
/// the raw text. The CSI/OSC/two-character forms are consumed whole instead, so a coloured test reporter's
/// output arrives as the plain text underneath it.</para>
///
/// <para><b>It is not a security boundary.</b> Avalonia's <c>TextBlock</c> renders no markup and executes
/// nothing; this is about legibility and about a terminal-control sequence not being able to reorder or
/// blank what a reviewer is reading.</para>
/// </summary>
internal static class JailText
{
    private const char Escape = '\u001B';

    /// <summary>
    /// Sanitizes one blob of jail output. <c>\r\n</c> and lone <c>\r</c> both collapse to <c>\n</c> so a
    /// runner's progress redraws become lines instead of a single unreadable one.
    /// </summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];

            if (c == Escape)
            {
                i = SkipEscapeSequence(raw, i);
                continue;
            }

            if (c == '\r')
            {
                // CRLF is one break, not two; a bare CR (progress redraw) becomes one too.
                if (i + 1 < raw.Length && raw[i + 1] == '\n')
                {
                    i++;
                }

                builder.Append('\n');
                continue;
            }

            builder.Append(char.IsControl(c) && c is not ('\n' or '\t') ? '.' : c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Returns the index of the LAST character of the escape sequence starting at <paramref name="start"/>
    /// (the caller's loop increments past it). Handles the three forms a test runner realistically emits:
    /// CSI (<c>ESC [</c> … final byte <c>@</c>–<c>~</c>), OSC (<c>ESC ]</c> … <c>BEL</c> or <c>ESC \</c>),
    /// and the two-character forms. A truncated sequence at the very end of a TAIL — which is normal, the
    /// daemon cuts mid-stream — consumes the remainder rather than leaving a bare ESC behind.
    /// </summary>
    private static int SkipEscapeSequence(string raw, int start)
    {
        if (start + 1 >= raw.Length)
        {
            return start; // a trailing ESC: dropped, nothing follows it to consume
        }

        var next = raw[start + 1];
        if (next == '[')
        {
            for (var i = start + 2; i < raw.Length; i++)
            {
                if (raw[i] >= '@' && raw[i] <= '~')
                {
                    return i;
                }
            }

            return raw.Length - 1; // unterminated CSI — the tail was cut inside it
        }

        if (next is ']')
        {
            for (var i = start + 2; i < raw.Length; i++)
            {
                if (raw[i] == '\u0007')
                {
                    return i;
                }

                if (raw[i] == Escape && i + 1 < raw.Length && raw[i + 1] == '\\')
                {
                    return i + 1;
                }
            }

            return raw.Length - 1; // unterminated OSC
        }

        return start + 1; // ESC + one final character (e.g. ESC c, ESC =)
    }
}
