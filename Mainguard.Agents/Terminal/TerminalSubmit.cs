using System;
using System.Text;

namespace Mainguard.Agents.Terminal;

/// <summary>
/// Encodes one line of text as the exact bytes a CLI attached to a PTY must receive to see it as a
/// <b>submitted</b> line — the difference between "the bytes arrived" and "the CLI acted".
///
/// <para><b>The rule: a line is submitted by CR (0x0D), never by LF (0x0A).</b> This is a property of
/// terminals, not of any one vendor's CLI, which is why it lives here (one shared fact) rather than in
/// <c>AdapterManifest</c> beside <c>systemPromptArg</c> / <c>preApprovedCommandArg</c> /
/// <c>initialPromptStyle</c>. Those three are vendor knowledge — only the CLI's author knows how their
/// binary spells a flag. Which byte means Enter is knowledge about the <i>terminal</i>, and both classes
/// of PTY-attached program agree on it:</para>
/// <list type="bullet">
/// <item><b>A TUI puts the tty in raw mode</b> (ICANON off, and with it ICRNL off), so it receives the
/// byte the terminal actually sent. A terminal sends <b>CR</b> for the Enter key. Every such TUI
/// therefore binds CR; an LF is at best a literal newline inserted into its input buffer.</item>
/// <item><b>A line-oriented reader</b> either leaves the tty canonical — where the line discipline's
/// ICRNL (on by default) translates CR to NL and completes the line — or uses a line editor
/// (readline/libedit), which binds CR and LF alike to accept-line.</item>
/// </list>
/// <para>So CR is correct for both classes and LF is correct for only one. A per-adapter field could
/// only ever be set to the one value that always works, while inviting an adapter author to declare
/// <c>"\n"</c> and silently reintroduce the exact defect this type exists to close. Measured, not
/// reasoned — see <c>docs/design/coordinator-phase-3-decisions.md</c> §17.1 for the transcripts.</para>
///
/// <para>The app's human-keystroke path already agrees: <c>TerminalControl.MapKey</c> maps
/// <c>Key.Enter</c> to a bare <c>0x0D</c> for every adapter, consults no manifest, and works.</para>
/// </summary>
public static class TerminalSubmit
{
    /// <summary>The byte that submits a line to a PTY-attached CLI: CR, what Enter sends.</summary>
    public const byte SubmitByte = 0x0D;

    /// <summary>The byte that inserts a newline <i>inside</i> a TUI's input buffer without submitting.</summary>
    public const byte NewLineByte = 0x0A;

    /// <summary>
    /// Builds the bytes for one submitted line, or refuses.
    ///
    /// <para>Two things happen to the text before the terminator is appended, and both are guards:</para>
    /// <list type="number">
    /// <item><b>Every embedded CR becomes LF.</b> A CR inside the text would submit a <i>prefix</i> of
    /// the message as its own turn and leave the remainder sitting in the input box — measured against
    /// the real CLI. A message authored on Windows, or pasted out of a log, carries CRLF routinely.
    /// Embedded LFs are kept: a TUI inserts them as real newlines, so a multi-line message arrives
    /// intact and is submitted once, by the single terminator.</item>
    /// <item><b>Trailing whitespace is trimmed</b>, so a caller that already ended its text with a
    /// newline does not submit a trailing blank line.</item>
    /// </list>
    ///
    /// <para>An empty result is <b>refused rather than sent as a bare CR</b>. A lone CR is not a no-op:
    /// it is Enter, which confirms whatever the CLI currently has focused — a permission dialog's
    /// highlighted option, an autocomplete selection. Sending nothing is the only safe reading of
    /// "there was nothing to say".</para>
    /// </summary>
    /// <param name="text">The message to submit.</param>
    /// <param name="bytes">The UTF-8 bytes to write toward the PTY, terminator included.</param>
    /// <returns>False when there is nothing to submit; <paramref name="bytes"/> is then empty.</returns>
    public static bool TryEncodeLine(string? text, out byte[] bytes)
    {
        var line = NormalizeBody(text);
        if (line.Length == 0)
        {
            bytes = Array.Empty<byte>();
            return false;
        }

        bytes = Encoding.UTF8.GetBytes(line + (char)SubmitByte);
        return true;
    }

    /// <summary>
    /// The body of a submitted line — everything except the terminator. Exposed so a test can assert
    /// the normalisation and the terminator separately.
    /// </summary>
    public static string NormalizeBody(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return normalized.TrimEnd(' ', '\t', '\n');
    }
}
