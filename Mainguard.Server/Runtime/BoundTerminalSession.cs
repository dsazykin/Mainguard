using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Terminal;
using Mainguard.Agents.Terminal.Vterm;
using Mainguard.Protos.V1;
using Mainguard.Server.Terminal;

namespace Mainguard.Server.Runtime;

/// <summary>
/// What the daemon saw while submitting one line — the two halves of the act, kept apart because they
/// are different evidence and because conflating them is how defect J2 hid for so long.
/// </summary>
/// <param name="Echoed">
/// The CLI produced output after the message body and <i>before</i> Enter. It therefore read the body,
/// which is what makes the following CR a separate keystroke rather than the tail of a paste — the
/// runtime detector for a J2 regression.
/// </param>
/// <param name="Reacted">The CLI produced output after Enter.</param>
/// <remarks>
/// <b>Neither field is proof the line became a turn.</b> A CLI already mid-turn emits output
/// continuously and would satisfy both observations without reading anything. The ground truth lives in
/// the CLI's own transcript, which the daemon deliberately does not read. Report; never assert.
/// </remarks>
public readonly record struct SubmitObservation(bool Echoed, bool Reacted);

/// <summary>
/// A long-lived, agent-bound terminal session: the CLI's PTY outlives any single gRPC attach. One
/// continuous <see cref="TerminalStreamer"/> pump drains the PTY into VT-safe frames that are
/// (a) kept in a bounded replay ring — so a re-attach after the client detached/switched agents
/// renders the missed output composed ("Reattached — session continued", ControlCenterDesign §4.5) —
/// and (b) fanned out to every live subscriber. Detaching a client only drops its subscription;
/// killing the CLI is an explicit <see cref="Kill"/> (StopAgent / daemon teardown), never a side
/// effect of closing a terminal document.
///
/// <para><b>P2-18 (libvterm engine):</b> when constructed with
/// <see cref="TerminalEngineKind.Libvterm"/>, the session also owns one <see cref="VtermSession"/>
/// fed the same VT-safe frames on the same 16 ms cadence — the daemon-side authoritative grid.
/// Grid-capable attaches subscribe via <see cref="SubscribeGrid"/> (an atomic full snapshot +
/// live <see cref="GridUpdate"/> deltas); raw attaches, the replay ring, and
/// <see cref="TailText"/> (the death-diagnosis text) are unchanged. OSC 52 copies decoded by the
/// engine fan out as <see cref="ClipboardCopy"/> frames — clipboard queries are never answered.
/// <see cref="Resize"/> resizes the PTY and the vterm screen in the same breath (one authoritative
/// grid size) and pushes a fresh snapshot to grid subscribers.</para>
///
/// <para>The continuous pump also keeps the PTY drained while nobody watches, so a chatty CLI can
/// never block on a full PTY buffer between attaches.</para>
/// </summary>
public sealed class BoundTerminalSession : IDisposable
{
    /// <summary>Replay ring cap — enough to redraw a busy TUI, bounded so daemon memory stays flat.</summary>
    internal const int ReplayCapBytes = 512 * 1024;

    /// <summary>Per-subscriber frame buffer; a stalled attach is completed (it re-attaches + replays).</summary>
    internal const int SubscriberFrameCapacity = 1024;

    private readonly ITerminalSession _session;
    private readonly TerminalStreamer _streamer = new();
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pump;
    private readonly object _gate = new();
    private readonly LinkedList<byte[]> _replay = new();
    private readonly List<Channel<byte[]>> _subscribers = new();
    private readonly List<Channel<TerminalOutput>> _gridSubscribers = new();
    private readonly List<string> _pendingClipboard = new();
    private readonly VtermSession? _vterm;
    private readonly Func<bool>? _isInputLocked;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _replayBytes;
    private bool _completed;
    private bool _inputClaimed;
    private int _disposed;

    /// <param name="isInputLocked">
    /// MG-5: evaluated live at OSC 52 fan-out time. When it returns true the session is an
    /// input-locked (managed/view-only) worker, and a copy-out from PTY output is <b>dropped</b>
    /// rather than written to the operator's host clipboard — output must not become a covert write
    /// channel to the host on a terminal the operator is only watching. Null (manual sessions) honors
    /// OSC 52 copies as before.
    /// </param>
    public BoundTerminalSession(
        string agentId,
        ITerminalSession session,
        TerminalEngineConfig? engine = null,
        int cols = 120,
        int rows = 32,
        Func<bool>? isInputLocked = null)
    {
        AgentId = agentId ?? throw new ArgumentNullException(nameof(agentId));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _isInputLocked = isInputLocked;
        if ((engine ?? TerminalEngineConfig.Interim).Engine == TerminalEngineKind.Libvterm)
        {
            _vterm = new VtermSession(cols, rows);
            _vterm.ClipboardCopyRequested += text => _pendingClipboard.Add(text);
        }

        _pump = Task.Run(PumpAsync);
    }

    public string AgentId { get; }

    /// <summary>Whether this session runs the P2-18 libvterm grid engine (grid attaches allowed).</summary>
    public bool GridEnabled => _vterm is not null;

    /// <summary>Completes when the child exits (the binder marks the session state off this).</summary>
    public Task<int> ExitCode => _session.ExitCode;

    /// <summary>
    /// Opens one subscription: the replay tail as already-safe frames, plus a live reader for
    /// everything after it (atomic with the replay — no frame is lost or duplicated in between).
    /// Call <paramref name="unsubscribe"/> on detach; the session itself keeps running.
    /// </summary>
    public (IReadOnlyList<byte[]> Replay, ChannelReader<byte[]> Live) Subscribe(out Action unsubscribe)
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SubscriberFrameCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait, // Wait + TryWrite == "report full", never block
        });

        byte[][] replay;
        lock (_gate)
        {
            replay = _replay.ToArray();
            if (_completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _subscribers.Add(channel);
            }
        }

        unsubscribe = () =>
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }

            channel.Writer.TryComplete();
        };

        return (replay, channel.Reader);
    }

    /// <summary>
    /// Opens one grid subscription (P2-18): a full-grid snapshot taken atomically with enrolment —
    /// no delta is lost or duplicated between the two — plus a live reader of
    /// <see cref="GridUpdate"/> / <see cref="ClipboardCopy"/> frames. Only valid when
    /// <see cref="GridEnabled"/>; a detach unsubscribes only.
    /// </summary>
    public (GridUpdate Snapshot, ChannelReader<TerminalOutput> Live) SubscribeGrid(out Action unsubscribe)
    {
        if (_vterm is null)
        {
            throw new InvalidOperationException("This session does not run the libvterm grid engine.");
        }

        var channel = Channel.CreateBounded<TerminalOutput>(new BoundedChannelOptions(SubscriberFrameCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        GridUpdate snapshot;
        lock (_gate)
        {
            snapshot = GridUpdateBuilder.BuildSnapshot(_vterm.Snapshot());
            if (_completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _gridSubscribers.Add(channel);
            }
        }

        unsubscribe = () =>
        {
            lock (_gate)
            {
                _gridSubscribers.Remove(channel);
            }

            channel.Writer.TryComplete();
        };

        return (snapshot, channel.Reader);
    }

    /// <summary>Writes keystrokes/paste toward the CLI.</summary>
    public async Task WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        // F64: write+flush is one indivisible act. `_session.IO` is a single Stream over the PTY master
        // and Stream is not thread-safe; two attaches typing at once interleaved at the BYTE level, so a
        // multi-byte escape sequence or a UTF-8 codepoint from one writer could be split by the other's
        // keystroke. The gate below costs an uncontended semaphore per keystroke.
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _session.IO.WriteAsync(data, ct).ConfigureAwait(false);
            await _session.IO.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// F64: claims the exclusive right to TYPE into this session, or returns null when another attach
    /// already holds it. Dispose the returned handle to release (the attach's <c>finally</c>).
    ///
    /// <para>Attaches fan out freely — any number of clients may watch — but exactly one may write. Two
    /// concurrent writable attaches to one CLI is not a shared terminal, it is two people's keystrokes
    /// arriving as one stream with no way to tell whose is whose; and on a coordinator-driven worker it
    /// meant an operator could type into a session the coordinator was mid-turn on. The claim is per
    /// attach and released on detach, so the terminal is handed over rather than locked forever.</para>
    ///
    /// <para>The daemon's own prompt delivery (<see cref="WriteInputAndAwaitOutputAsync"/>) does not take
    /// this claim: it is the session's owner acting, not a viewer competing, and it must not be blocked
    /// by whoever happens to have a terminal open. Its writes are still serialized by the gate above.</para>
    /// </summary>
    public IDisposable? TryClaimInput()
    {
        lock (_gate)
        {
            if (_inputClaimed)
            {
                return null;
            }

            _inputClaimed = true;
        }

        return new InputClaim(this);
    }

    /// <summary>Whether some attach currently holds the exclusive write claim.</summary>
    public bool IsInputClaimed
    {
        get { lock (_gate) { return _inputClaimed; } }
    }

    private void ReleaseInputClaim()
    {
        lock (_gate)
        {
            _inputClaimed = false;
        }
    }

    private sealed class InputClaim : IDisposable
    {
        private readonly BoundTerminalSession _owner;
        private int _released;

        public InputClaim(BoundTerminalSession owner) => _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner.ReleaseInputClaim();
            }
        }
    }

    /// <summary>
    /// Writes toward the CLI and then waits up to <paramref name="window"/> for the CLI to produce
    /// output — the only in-band evidence the daemon has that a write was <i>consumed</i> rather than
    /// merely accepted by the PTY master.
    ///
    /// <para><b>What this proves and what it does not.</b> A write to a PTY master succeeds whether or
    /// not the child ever reads it, so "the write returned" is evidence of nothing. A PTY-attached CLI,
    /// by contrast, cannot consume a keystroke silently: it re-renders. So output arriving here means
    /// the child read and reacted. It does <b>not</b> mean the CLI understood the text, and it is
    /// <b>necessary but not sufficient</b> as a submission signal — a CLI already mid-turn emits output
    /// continuously, and that output would satisfy this wait on its own. Its evidential weight is in the
    /// negative direction: an idle CLI that produces nothing at all after a keystroke did not see one.
    /// Callers must report it as an observation, never assert it as proof.</para>
    ///
    /// <para>The subscription is opened <i>before</i> the write, so no reaction can slip through the gap
    /// between the two; it is dropped again on every path.</para>
    /// </summary>
    /// <returns>True when the CLI produced output within the window.</returns>
    public async Task<bool> WriteInputAndAwaitOutputAsync(
        ReadOnlyMemory<byte> data, TimeSpan window, CancellationToken ct)
    {
        var (_, live) = Subscribe(out var unsubscribe);
        try
        {
            await WriteInputAsync(data, ct).ConfigureAwait(false);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(window);
            try
            {
                // False here means the stream COMPLETED (the CLI died) — also "no reaction", correctly.
                return await live.WaitToReadAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested(); // a caller's cancel is not an observation
                return false;
            }
        }
        finally
        {
            unsubscribe();
        }
    }

    /// <summary>
    /// Submits one line to the CLI as a human would: the text first, then Enter as a <b>separate
    /// keystroke</b> — and reports the two things the daemon can actually observe about it.
    ///
    /// <para><b>Why the write is split (defect J2).</b> A TUI decides whether input was typed or pasted
    /// from the read burst it arrives in, and inside a paste a CR is content, not Enter. Sending
    /// <c>body + CR</c> as one buffer therefore submits a 3-byte poke but <i>not</i> a 139-byte steer —
    /// the CR is absorbed into the message. Measured against the real CLI; see
    /// <c>docs/design/coordinator-phase-3-decisions.md</c> §17.8.</para>
    ///
    /// <para><b>How the two writes are kept apart.</b> Preferentially by the CLI's own echo, which is
    /// causal rather than timed: a CLI that has repainted in response to the body has already read those
    /// bytes, so a CR written afterwards cannot arrive in the same read. Only when no echo is seen
    /// within <paramref name="echoWindow"/> does it fall back to waiting
    /// <see cref="TerminalSubmit.TerminatorSeparation"/> — because two back-to-back writes with nothing
    /// between them are coalesced by the PTY into one read, which is the defect again.</para>
    ///
    /// <para><b>What the result proves, and what it does not.</b> <c>Echoed</c> means the CLI consumed
    /// the body and repainted — genuinely stronger than "the write returned", and the runtime detector
    /// for a J2 regression, since it is what establishes that Enter went as its own keystroke.
    /// <c>Reacted</c> means it produced output after Enter. <b>Neither is proof the line became a
    /// turn</b>: a CLI already mid-turn emits output continuously and would satisfy both waits on its
    /// own. Their weight is in the negative direction — an idle CLI silent after a keystroke did not see
    /// one. Callers must report these, never assert on them.</para>
    ///
    /// <para>Each subscription is opened <i>before</i> its write, so no reaction can slip through the
    /// gap; the first is dropped before the second is opened, so the body's own repaint can never be
    /// miscounted as a reaction to Enter.</para>
    /// </summary>
    public async Task<SubmitObservation> SubmitLineAndAwaitOutputAsync(
        ReadOnlyMemory<byte> body,
        ReadOnlyMemory<byte> terminator,
        TimeSpan echoWindow,
        TimeSpan reactionWindow,
        CancellationToken ct)
    {
        var sinceBody = System.Diagnostics.Stopwatch.StartNew();
        var echoed = await WriteInputAndAwaitOutputAsync(body, echoWindow, ct).ConfigureAwait(false);

        // The separation is a FLOOR in every case, echo or no echo. It used to apply only when nothing
        // echoed, on the argument that an echo is causal — a CLI that repainted has read the body. But
        // `echoed` is true on ANY output frame inside the window, and a CLI mid-turn streams output
        // continuously: it satisfies the wait within a millisecond without having read a byte, the CR
        // goes out immediately behind the body, and the PTY can coalesce the two writes into one read
        // — defect J2, on exactly the worker a coordinator most wants to steer (a busy one). The echo
        // stays what it is, an observation reported to the caller; the gap is what keeps the terminator
        // in a read of its own.
        //
        // Measured against the ELAPSED time rather than slept unconditionally: when the echo window
        // lapsed there is already a 250 ms gap and nothing to add, but an instant return — an echo on the
        // first frame, or a CLI whose output stream has completed — arrives here having waited no time at
        // all. The FULL separation, not the remainder: the stopwatch starts marginally before the body
        // actually reaches the PTY, so subtracting its reading would shave the write's own cost off the
        // gap the CLI sees.
        if (sinceBody.Elapsed < TerminalSubmit.TerminatorSeparation)
        {
            await Task.Delay(TerminalSubmit.TerminatorSeparation, ct).ConfigureAwait(false);
        }

        var reacted = await WriteInputAndAwaitOutputAsync(terminator, reactionWindow, ct)
            .ConfigureAwait(false);
        return new SubmitObservation(echoed, reacted);
    }

    /// <summary>
    /// Propagates a resize toward the CLI (SIGWINCH) and, on the libvterm engine, reflows the
    /// vterm screen in the same breath — the PTY, the parser grid, and the rendered grid can never
    /// disagree (the P2-18 one-authoritative-size rule). Grid subscribers receive a fresh snapshot
    /// immediately (an idle CLI produces no output to piggyback on). Invalid sizes are ignored.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0)
        {
            return;
        }

        // MG-22: clamp HERE, before either half of the resize, so the PTY and the vterm grid can never
        // be driven to different sizes — the one-authoritative-size rule. VtermSession clamps again at
        // the native boundary; this is what keeps the two consistent.
        cols = VtermSession.ClampDimension(cols);
        rows = VtermSession.ClampDimension(rows);

        _session.Resize(cols, rows);
        if (_vterm is null)
        {
            return;
        }

        lock (_gate)
        {
            _vterm.Resize(cols, rows);
            if (_vterm.SnapshotPending)
            {
                PublishSnapshotLocked();
            }
        }
    }

    /// <summary>Scrollback rows for the lazy fetch RPC (libvterm engine only; empty otherwise).</summary>
    public ScrollbackReply GetScrollback(long start, int count)
    {
        var reply = new ScrollbackReply();
        if (_vterm is null)
        {
            return reply;
        }

        reply.Total = (ulong)(_vterm.ScrollbackStart + _vterm.ScrollbackCount);
        var rows = _vterm.GetScrollback(start, count);
        reply.Start = rows.Count > 0 ? (uint)rows[0].Index : (uint)start;
        foreach (var (index, cells) in rows)
        {
            reply.Rows.Add(GridUpdateBuilder.BuildRow(index, cells));
        }

        return reply;
    }

    /// <summary>Force-terminates the CLI (StopAgent / teardown). Attaches see the stream complete.</summary>
    public void Kill() => _session.Kill();

    /// <summary>
    /// A human-readable tail of the CLI's most recent output (from the replay ring), for the
    /// death-diagnosis audit: VT escape sequences and control bytes are stripped, whitespace runs
    /// collapsed, and the result capped to the LAST <paramref name="maxChars"/> characters.
    /// </summary>
    public string TailText(int maxChars)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        byte[][] frames;
        lock (_gate)
        {
            frames = _replay.ToArray();
        }

        var raw = System.Text.Encoding.UTF8.GetString(
            frames.SelectMany(f => f).ToArray());

        // Strip CSI/OSC escape sequences — each becomes a SPACE, not nothing: TUI CLIs (Ink —
        // verified against claude-code's real death screen) separate words with cursor-column
        // moves (ESC[9G) instead of literal spaces, so erasing sequences outright welded the words
        // into "Failedtoconnecttoplatform.claude.com" and the egress block-detector then proposed
        // that whole blob as the host to unblock. The \s+ collapse below eats any doubled spaces.
        raw = System.Text.RegularExpressions.Regex.Replace(
            raw, @"\x1B(\[[0-9;?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)?|[@-Z\\-_])", " ");
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            new string(raw.Select(c => char.IsControl(c) ? ' ' : c).ToArray()), @"\s+", " ").Trim();

        return cleaned.Length <= maxChars ? cleaned : cleaned[^maxChars..];
    }

    private async Task PumpAsync()
    {
        try
        {
            await _streamer.RunAsync(_session.IO, (frame, _) =>
            {
                Publish(frame.ToArray());
                return Task.CompletedTask;
            }, flushInterval: null, _pumpCts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // PTY torn down underneath the pump — fall through to completion.
        }
        finally
        {
            CompleteSubscribers();
        }
    }

    private void Publish(byte[] frame)
    {
        lock (_gate)
        {
            _replay.AddLast(frame);
            _replayBytes += frame.Length;
            while (_replayBytes > ReplayCapBytes && _replay.First is { } oldest)
            {
                _replayBytes -= oldest.Value.Length;
                _replay.RemoveFirst();
            }

            for (var i = _subscribers.Count - 1; i >= 0; i--)
            {
                if (!_subscribers[i].Writer.TryWrite(frame))
                {
                    // A stalled attach: complete it (the client re-attaches and replays) rather
                    // than buffering unboundedly or silently dropping frames mid-stream.
                    _subscribers[i].Writer.TryComplete();
                    _subscribers.RemoveAt(i);
                }
            }

            FeedGridLocked(frame);
        }
    }

    /// <summary>Feeds one VT-safe frame to the vterm engine and fans the drained tick out to grid
    /// subscribers. Caller holds the gate (the engine is single-threaded by contract).</summary>
    private void FeedGridLocked(byte[] frame)
    {
        if (_vterm is null)
        {
            return;
        }

        _vterm.Feed(frame);

        if (_vterm.SnapshotPending)
        {
            PublishSnapshotLocked();
        }
        else if (GridUpdateBuilder.BuildDelta(_vterm.DrainDelta()) is { } delta)
        {
            PublishGridLocked(new TerminalOutput { Grid = delta });
        }

        if (_pendingClipboard.Count > 0)
        {
            // MG-5: on an input-locked (view-only) session, drop OSC 52 copy-outs instead of writing
            // the operator's host clipboard — the copy is still consumed here so it never accumulates.
            var suppress = _isInputLocked?.Invoke() == true;
            if (!suppress)
            {
                foreach (var text in _pendingClipboard)
                {
                    PublishGridLocked(new TerminalOutput { Clipboard = new ClipboardCopy { Text = text } });
                }
            }

            _pendingClipboard.Clear();
        }
    }

    /// <summary>
    /// Publishes a full snapshot after draining the pending tick. The drained structural log is
    /// meaningless for the GRID (the snapshot replaces it), but its scrollback pushes/pops are
    /// real ring changes the client must still apply — dropping them would silently desync the
    /// client ring from the daemon's. They ride ahead of the snapshot as a ring-only update.
    /// Caller holds the gate.
    /// </summary>
    private void PublishSnapshotLocked()
    {
        var drained = _vterm!.DrainDelta();
        var ringOnly = new GridUpdate
        {
            Cols = (uint)_vterm.Cols,
            Rows = (uint)_vterm.Rows,
            PushedTruncated = drained.PushedTruncated,
        };
        foreach (var pushed in drained.PushedRows)
        {
            ringOnly.Pushed.Add(GridUpdateBuilder.BuildRow(0, pushed));
        }

        foreach (var op in drained.Ops)
        {
            if (op is VtermGridOp.PopRows pop)
            {
                ringOnly.Ops.Add(new GridOp { PopRows = (uint)pop.Count });
            }
        }

        if (ringOnly.Pushed.Count > 0 || ringOnly.Ops.Count > 0 || ringOnly.PushedTruncated)
        {
            PublishGridLocked(new TerminalOutput { Grid = ringOnly });
        }

        PublishGridLocked(new TerminalOutput { Grid = GridUpdateBuilder.BuildSnapshot(_vterm.Snapshot()) });
    }

    private void PublishGridLocked(TerminalOutput output)
    {
        for (var i = _gridSubscribers.Count - 1; i >= 0; i--)
        {
            if (!_gridSubscribers[i].Writer.TryWrite(output))
            {
                _gridSubscribers[i].Writer.TryComplete();
                _gridSubscribers.RemoveAt(i);
            }
        }
    }

    private void CompleteSubscribers()
    {
        lock (_gate)
        {
            _completed = true;
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryComplete();
            }

            _subscribers.Clear();
            foreach (var subscriber in _gridSubscribers)
            {
                subscriber.Writer.TryComplete();
            }

            _gridSubscribers.Clear();
        }
    }

    /// <summary>
    /// F59: releases the daemon-side half of this session — the pump, the streamer, the vterm and every
    /// subscriber — <b>without killing the CLI</b>.
    ///
    /// <para>Used on daemon shutdown. The jail and the agent's worktree survive a restart by design; the
    /// PTY this daemon opened does not, and cannot be reattached (the Docker API has no re-attach for a
    /// running exec). But "the terminal is gone" and "SIGKILL the agent mid-task" are different acts, and
    /// only the first is a consequence of the daemon stopping. <see cref="Dispose"/> — the explicit
    /// StopAgent / teardown path — still kills, because there the kill is the request.</para>
    ///
    /// <para><b>The bug this used to be.</b> Both halves of teardown ended in
    /// <c>ITerminalSession.Dispose</c>, and the production session is <see cref="PtySession"/>, whose
    /// <c>Dispose</c> is a <c>Kill</c>. So detaching killed exactly what disposing killed and the F59
    /// claim rested on a no-op. Detach now goes through <see cref="ITerminalSession.Release"/>, which
    /// exists to be the difference.</para>
    /// </summary>
    public void Detach()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        TearDownDaemonSide(reapChild: false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _session.Kill();
        }
        catch
        {
            // Best-effort reap.
        }

        TearDownDaemonSide(reapChild: true);
    }

    /// <summary>
    /// The half of teardown that is the daemon's own state, shared by Dispose and Detach.
    /// <paramref name="reapChild"/> chooses which half of the session contract ends the CLI's life:
    /// <c>Dispose</c> reaps the child, <c>Release</c> lets go of it.
    /// </summary>
    private void TearDownDaemonSide(bool reapChild)
    {
        _pumpCts.Cancel();
        try
        {
            _pump.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Pump teardown races with PTY disposal.
        }

        if (reapChild)
        {
            _session.Dispose();
        }
        else
        {
            _session.Release();
        }

        _streamer.Dispose();
        _pumpCts.Dispose();

        // The write gate is deliberately NOT disposed: a keystroke racing teardown would then throw
        // ObjectDisposedException out of WriteInputAsync instead of failing on the torn-down stream it
        // is actually about. SemaphoreSlim only needs disposal when its AvailableWaitHandle was taken,
        // and nothing here takes it.
        lock (_gate)
        {
            _vterm?.Dispose();
        }
    }
}
