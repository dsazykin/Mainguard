using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;

namespace Mainguard.Server.Tests;

/// <summary>
/// A stand-in for a real coding CLI attached to a PTY — one that models the <b>CLI's side</b> of the
/// boundary rather than the PTY's, which is the whole reason it exists.
///
/// <para><b>Why a byte-recording stub was not enough.</b> The previous double was a
/// <see cref="MemoryStream"/> and the assertion was <c>Assert.Equal("prefer the stdlib\n", written)</c>.
/// That proves bytes reached the PTY — the one thing that was never in doubt. It cannot see the defect
/// it was written to catch, because the defect lives one layer further on: the CLI submits on <b>CR</b>,
/// so <c>"\n"</c> put the text in its input box and pressed nothing. Three live prompts accumulated
/// there unsubmitted while that assertion stayed green.</para>
///
/// <para><b>The behaviour modelled here was measured, not assumed</b> — claude-code v2.1.251 driven
/// under a real <c>forkpty</c>, transcripts in
/// <c>docs/design/coordinator-phase-3-decisions.md</c> §17.1:</para>
/// <list type="bullet">
/// <item>the CLI puts the tty in <b>raw mode</b> (ICANON off, and with it ICRNL), so it sees the bytes
/// written to the master verbatim — no line discipline translates anything;</item>
/// <item><b>CR (0x0D) submits</b> the input buffer as one line and clears it;</item>
/// <item><b>LF (0x0A) inserts a newline into the buffer</b> and submits nothing — two prompts sent that
/// way become one two-line buffer, which is exactly what the live run produced;</item>
/// <item>a submit makes the CLI <b>redraw</b>, i.e. produce output. That redraw is the only evidence
/// the daemon can observe, so the double emits it;</item>
/// <item><b>a CR arriving in the same read as a substantial body is PASTED CONTENT, not Enter</b> — a
/// TUI classifies input by the burst it arrives in, so it inserts a newline and submits nothing. That
/// is defect J2, measured in §17.8, and modelling it is why this double had to change.</item>
/// </list>
///
/// <para><b>Why that last rule had to be added.</b> As first written, <see cref="Feed"/> walked the
/// incoming bytes one at a time and acted on every CR regardless of which write it arrived in — a CLI
/// with no paste handling at all, which is a CLI nobody ships. So <c>body + CR</c> in one write
/// submitted here while silently failing against the real binary, and a green suite reported that the
/// coordinator's only steering channel worked while a 139-byte steer sat unsubmitted in a live worker's
/// input box. The double now honours <b>write boundaries</b>, which is the property the whole fix turns
/// on: what matters is not which bytes are sent but how they are grouped.</para>
///
/// <para>Assertions are therefore about <see cref="SubmittedLines"/> — what the CLI <i>received as a
/// submitted line</i> — and <see cref="PendingInput"/>, the text left sitting in its box. Those two
/// tell delivery and submission apart; a byte log cannot.</para>
/// </summary>
internal sealed class RawModeCliDouble : ITerminalSession
{
    private readonly object _gate = new();
    private readonly List<string> _submitted = new();
    private readonly List<(DateTime At, int Bytes)> _writes = new();
    private readonly StringBuilder _pending = new();
    private readonly CliStream _stream;
    private readonly bool _redraws;

    /// <param name="redraws">
    /// Whether the CLI repaints when it accepts a line. False models the case the daemon cannot tell
    /// apart from a swallowed keystroke by watching the PTY alone — a CLI that is silent after Enter.
    /// </param>
    public RawModeCliDouble(bool redraws = true)
    {
        _redraws = redraws;
        _stream = new CliStream(this);
    }

    public Stream IO => _stream;

    public Task<int> ExitCode { get; } = new TaskCompletionSource<int>().Task;

    /// <summary>Every line the CLI has been made to submit, oldest first.</summary>
    public IReadOnlyList<string> SubmittedLines
    {
        get
        {
            lock (_gate)
            {
                return _submitted.ToArray();
            }
        }
    }

    /// <summary>
    /// When each read landed at the CLI, and how big it was — the axis defect J2 lives on.
    ///
    /// <para>Assertions about the <i>gap</i> between the body and the terminator have to be made here
    /// rather than on wall-clock time around the whole call: a caller that waited before writing at all
    /// would satisfy an outer stopwatch while still handing the CLI both halves in one read.</para>
    /// </summary>
    public IReadOnlyList<(DateTime At, int Bytes)> Writes
    {
        get
        {
            lock (_gate)
            {
                return _writes.ToArray();
            }
        }
    }

    /// <summary>The text sitting unsubmitted in the CLI's input box.</summary>
    public string PendingInput
    {
        get
        {
            lock (_gate)
            {
                return _pending.ToString();
            }
        }
    }

    public void Resize(int cols, int rows)
    {
    }

    public void Kill() => _stream.CompleteOutput();

    public void Dispose() => _stream.Dispose();

    /// <summary>Waits for the CLI to have submitted at least <paramref name="count"/> lines.</summary>
    public async Task<IReadOnlyList<string>> WaitForSubmittedAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var lines = SubmittedLines;
            if (lines.Count >= count)
            {
                return lines;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return SubmittedLines;
    }

    /// <summary>
    /// Bytes at or above this in a single read make the CLI treat that read as a <b>paste</b>, so a CR
    /// inside it is content rather than Enter. A stand-in for the fast-input heuristics real TUIs use
    /// (burst size, inter-byte timing, bracketed paste). The exact threshold is not the point and no test
    /// leans on its value: they use a body an order of magnitude clear of it on one side and a lone
    /// terminator on the other, which is how a keystroke and a paste actually differ.
    /// </summary>
    internal const int PasteBurstBytes = 16;

    /// <summary>
    /// The raw-mode key handling: CR submits, LF is a newline in the buffer, the rest types — <b>except
    /// inside a paste</b>, where a CR is just another character.
    ///
    /// <para>One call is one read at the CLI, which is what makes this the interesting boundary. A TUI
    /// decides typed-vs-pasted per read burst, so a terminator appended to its own message never reads as
    /// Enter, however correct the byte is. Emitting the echo on <i>every</i> read rather than only on a
    /// submit is deliberate too: a real CLI repaints as text arrives, and that echo is precisely what the
    /// daemon waits for to know the body has been consumed before it presses Enter.</para>
    /// </summary>
    private void Feed(ReadOnlySpan<byte> data)
    {
        var text = Encoding.UTF8.GetString(data);
        if (text.Length == 0)
        {
            return;
        }

        // The defect, modelled: a large enough burst is a paste, and a paste contains no keystrokes.
        var pasted = text.Length >= PasteBurstBytes;

        lock (_gate)
        {
            _writes.Add((DateTime.UtcNow, text.Length));
            foreach (var ch in text)
            {
                if (ch == '\r' && !pasted)
                {
                    _submitted.Add(_pending.ToString());
                    _pending.Clear();
                }
                else if (ch == '\r')
                {
                    // Inside a paste a CR is a line break in the pasted text, NOT Enter. This one branch
                    // is the whole of J2: every byte correct, and nothing submitted.
                    _pending.Append('\n');
                }
                else
                {
                    // '\n' included: a TUI inserts it, it does not act on it.
                    _pending.Append(ch);
                }
            }
        }

        if (_redraws)
        {
            // What a CLI does with input: repaint — while text arrives as well as on accept. The daemon's
            // echo window and its reaction window both watch for exactly this, so the double has to
            // produce it or neither observation would be testable.
            _stream.Emit(Encoding.UTF8.GetBytes("\u001b[2K\rworking\r\n"));
        }
    }

    /// <summary>
    /// The PTY as the daemon sees it: writes go to the CLI's key handler, reads deliver whatever the
    /// CLI has painted (and block until there is some, like a real PTY, rather than reporting EOF).
    /// </summary>
    private sealed class CliStream : Stream
    {
        private readonly RawModeCliDouble _cli;
        private readonly SemaphoreSlim _readable = new(0);
        private readonly Queue<byte[]> _out = new();
        private readonly object _outGate = new();
        private readonly CancellationTokenSource _closed = new();
        private byte[]? _current;
        private int _offset;
        private int _disposed;

        public CliStream(RawModeCliDouble cli) => _cli = cli;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Emit(byte[] frame)
        {
            lock (_outGate)
            {
                _out.Enqueue(frame);
            }

            _readable.Release();
        }

        public void CompleteOutput()
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _closed.Cancel();
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
            if (_current is null)
            {
                try
                {
                    await _readable.WaitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return 0; // torn down: EOF, as a dead PTY reports it
                }

                lock (_outGate)
                {
                    _current = _out.Dequeue();
                }

                _offset = 0;
            }

            var take = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, take).CopyTo(buffer);
            _offset += take;
            if (_offset >= _current.Length)
            {
                _current = null;
            }

            return take;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            _cli.Feed(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => _cli.Feed(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // Idempotent: the bound session disposes the terminal, and the test's own `using` disposes
            // the double again. A second Cancel() on a disposed CTS would throw out of the test body.
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _closed.Cancel();
                _closed.Dispose();
                _readable.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
