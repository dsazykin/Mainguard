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
    /// <summary>
    /// A steer of the length a coordinator actually sends — the exact message from the live run that
    /// exposed defect J2 (139 bytes). Every encoding assertion below runs against THIS rather than a
    /// three-word literal, because that is the difference the defect turns on: <c>body + CR</c> in one
    /// write submits a 3-byte poke and does not submit this. Short fixtures are how a broken encoder
    /// stayed green through a live failure.
    /// </summary>
    private const string RealisticSteer =
        "Add one more assertion to test.js covering the empty-input case, then re-run the suite and "
        + "record the result in your mainguard-plan commit.";

    /// <summary>Encodes, asserting the call succeeded, and hands back both halves.</summary>
    private static (byte[] Body, byte[] Terminator) Encode(string? text)
    {
        Assert.True(TerminalSubmit.TryEncodeSubmission(text, out var body, out var terminator));
        return (body, terminator);
    }

    /// <summary>The terminator is CR. Stated as its own assertion because it is half the defect.</summary>
    [Fact]
    public void ALineIsTerminatedByCarriageReturn_NotByNewline()
    {
        var (body, terminator) = Encode(RealisticSteer);

        Assert.Equal(new[] { (byte)0x0D }, terminator);
        Assert.Equal(RealisticSteer, Encoding.UTF8.GetString(body));
        Assert.DoesNotContain((byte)0x0A, body);
    }

    /// <summary>
    /// The other half, and the one a correct byte alone did not buy: the terminator comes back as its
    /// <b>own buffer</b>, and the body does not end with it.
    ///
    /// <para>A TUI classifies input as typed or pasted by the read burst it arrives in, so a CR appended
    /// to a realistic message is read as pasted content — a newline — and submits nothing. Measured
    /// against claude-code v2.1.251: this exact 139-byte string plus CR in one write left the text in the
    /// input box; the same bytes with the CR written separately submitted every time (§17.8). If this
    /// ever fails by producing one concatenated buffer, <c>send_worker_prompt</c> is inert again for
    /// every message longer than a poke.</para>
    /// </summary>
    [Fact]
    public void TheTerminatorIsASeparateBuffer_SoItCannotBeReadAsPastedContent()
    {
        var (body, terminator) = Encode(RealisticSteer);

        // Realistic length is the whole point: at 3 bytes the shipped encoder worked.
        Assert.True(body.Length > 100, $"fixture must be realistic; was {body.Length} bytes");

        Assert.Single(terminator);
        Assert.Equal(TerminalSubmit.SubmitByte, terminator[0]);

        // The body carries NO terminator of its own — nothing to coalesce.
        Assert.DoesNotContain(TerminalSubmit.SubmitByte, body);
        Assert.NotEqual(TerminalSubmit.SubmitByte, body[^1]);
    }

    /// <summary>
    /// A poke and a realistic steer encode to the <b>same shape</b>. The defect was precisely that they
    /// behaved differently — `go` submitted instantly, 139 bytes never did — so length must not change
    /// anything about the encoding.
    /// </summary>
    [Theory]
    [InlineData("go")]
    [InlineData("narrow the try block")]
    [InlineData(RealisticSteer)]
    public void LengthChangesNothingAboutTheEncoding(string text)
    {
        var (body, terminator) = Encode(text);

        Assert.Equal(text, Encoding.UTF8.GetString(body));
        Assert.Equal(new[] { TerminalSubmit.SubmitByte }, terminator);
    }

    /// <summary>
    /// Embedded newlines survive as newlines — a TUI inserts LF into its buffer, so a multi-line steer
    /// arrives intact and is submitted once, by the single terminator.
    /// </summary>
    [Fact]
    public void EmbeddedNewlinesAreKept_SoAMultiLineMessageArrivesIntact()
    {
        var multiLine = RealisticSteer + "\n" + RealisticSteer;
        var (body, terminator) = Encode(multiLine);

        Assert.Equal(multiLine, Encoding.UTF8.GetString(body));
        Assert.Equal(new[] { TerminalSubmit.SubmitByte }, terminator);
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
        var (body, terminator) = Encode(text);

        Assert.Equal(expectedBody, Encoding.UTF8.GetString(body));

        // Exactly one CR anywhere in what will be written, and it is the standalone terminator: one
        // submit, at the end, of the whole message.
        Assert.DoesNotContain(TerminalSubmit.SubmitByte, body);
        Assert.Equal(new[] { TerminalSubmit.SubmitByte }, terminator);
    }

    /// <summary>A caller that already ended its text with a newline does not submit a trailing blank line.</summary>
    [Theory]
    [InlineData(RealisticSteer + "\n")]
    [InlineData(RealisticSteer + "\r\n")]
    [InlineData(RealisticSteer + "   ")]
    [InlineData(RealisticSteer + "\n\n\n")]
    [InlineData(RealisticSteer + " \t\r\n")]
    public void TrailingWhitespaceIsTrimmedBeforeTheTerminator(string text)
    {
        var (body, terminator) = Encode(text);

        Assert.Equal(RealisticSteer, Encoding.UTF8.GetString(body));
        Assert.Equal(new[] { TerminalSubmit.SubmitByte }, terminator);
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
        Assert.False(TerminalSubmit.TryEncodeSubmission(text, out var body, out var terminator));

        // BOTH empty. A terminator handed back here would be a bare Enter with nothing said.
        Assert.Empty(body);
        Assert.Empty(terminator);
    }

    /// <summary>Leading whitespace is the caller's business; only the tail is trimmed.</summary>
    [Fact]
    public void LeadingWhitespaceIsPreserved()
    {
        Assert.Equal("  indented", TerminalSubmit.NormalizeBody("  indented\n"));
    }
}
