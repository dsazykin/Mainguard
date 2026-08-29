using System.Text;
using Mainguard.Agents.Terminal;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The line-submission encoding, asserted at the byte the CLI actually acts on.
///
/// <para><b>Why this type exists at all.</b> <c>AgentCliBinder.TrySendPromptAsync</c> wrote
/// <c>prompt + "\n"</c> for the whole life of <c>send_worker_prompt</c> — the coordinator's only
/// steering channel — and the tool consequently never worked once. A PTY-attached CLI puts its tty in
/// raw mode, so ICRNL never translates anything and the CLI sees the byte it was sent; Enter on a
/// terminal is <b>CR</b>. LF is at best a newline typed into the input box. Measured against
/// claude-code v2.1.251 under a real forkpty (transcripts in
/// <c>docs/design/coordinator-phase-3-decisions.md</c> §17.1): LF left the text in the box and a second
/// prompt concatenated onto the first; CR submitted and the CLI ran a turn.</para>
/// </summary>
public class TerminalSubmitTests
{
    /// <summary>The terminator is CR. Stated as its own assertion because it is the entire defect.</summary>
    [Fact]
    public void ALineIsTerminatedByCarriageReturn_NotByNewline()
    {
        Assert.True(TerminalSubmit.TryEncodeLine("prefer the stdlib", out var bytes));

        Assert.Equal((byte)0x0D, bytes[^1]);
        Assert.Equal("prefer the stdlib\r", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain((byte)0x0A, bytes);
    }

    /// <summary>
    /// Embedded newlines survive as newlines — a TUI inserts LF into its buffer, so a multi-line steer
    /// arrives intact and is submitted once, by the single terminator.
    /// </summary>
    [Fact]
    public void EmbeddedNewlinesAreKept_SoAMultiLineMessageArrivesIntact()
    {
        Assert.True(TerminalSubmit.TryEncodeLine("first line\nsecond line", out var bytes));

        Assert.Equal("first line\nsecond line\r", Encoding.UTF8.GetString(bytes));
    }

    /// <summary>
    /// An embedded CR is rewritten to LF, because it would otherwise submit a PREFIX of the message as
    /// its own turn and strand the remainder in the input box — measured. CRLF text is the ordinary way
    /// this arrives, not an exotic one.
    /// </summary>
    [Theory]
    [InlineData("do A\r\nthen B", "do A\nthen B")]
    [InlineData("do A\rthen B", "do A\nthen B")]
    [InlineData("a\r\nb\r\nc", "a\nb\nc")]
    public void AnEmbeddedCarriageReturnCannotSplitAMessageIntoTwoTurns(string text, string expectedBody)
    {
        Assert.True(TerminalSubmit.TryEncodeLine(text, out var bytes));

        var encoded = Encoding.UTF8.GetString(bytes);
        Assert.Equal(expectedBody + "\r", encoded);

        // Exactly one CR in the whole payload, and it is the last byte: exactly one submit.
        Assert.Equal(1, encoded.Length - encoded.Replace("\r", string.Empty).Length);
        Assert.EndsWith("\r", encoded, System.StringComparison.Ordinal);
    }

    /// <summary>A caller that already ended its text with a newline does not submit a trailing blank line.</summary>
    [Theory]
    [InlineData("steer it\n", "steer it\r")]
    [InlineData("steer it\r\n", "steer it\r")]
    [InlineData("steer it   ", "steer it\r")]
    [InlineData("steer it\n\n\n", "steer it\r")]
    public void TrailingWhitespaceIsTrimmedBeforeTheTerminator(string text, string expected)
    {
        Assert.True(TerminalSubmit.TryEncodeLine(text, out var bytes));

        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
    }

    /// <summary>
    /// Nothing to say means nothing is written — never a bare CR. A lone CR is Enter, which confirms
    /// whatever the CLI currently has focused: a permission dialog's highlighted option, an autocomplete
    /// row. "Submit an empty message" and "press Enter at whatever is on screen" are not the same act.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("   \t  ")]
    public void AnEmptyMessageIsRefused_RatherThanPressingEnterAtWhateverIsOnScreen(string? text)
    {
        Assert.False(TerminalSubmit.TryEncodeLine(text, out var bytes));

        Assert.Empty(bytes);
    }

    /// <summary>Leading whitespace is the caller's business; only the tail is trimmed.</summary>
    [Fact]
    public void LeadingWhitespaceIsPreserved()
    {
        Assert.Equal("  indented", TerminalSubmit.NormalizeBody("  indented\n"));
    }
}
