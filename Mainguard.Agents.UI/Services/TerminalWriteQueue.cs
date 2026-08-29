using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Protos.V1;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// Serializes every frame written to ONE <c>TerminalService.Attach</c> request stream.
///
/// <para><b>Why this exists.</b> A gRPC <see cref="IClientStreamWriter{T}"/> permits exactly one
/// in-flight <c>WriteAsync</c>; a second concurrent call throws
/// <c>InvalidOperationException: Can't write the message because the previous write is in
/// progress</c>. <see cref="DaemonTerminalGateway"/> has three writers onto the same stream — the
/// attach selector frame, every keystroke/paste/mouse report, and the debounced resize — and the
/// keystroke path is fire-and-forget (<c>TerminalViewModel.OnInputAvailable</c>), so two keys
/// pressed inside one write round-trip raced. The loser's exception escaped into an unobserved
/// task and the character was silently gone. Found live (stress S1 / G5): three of those
/// exceptions, three characters missing from what the operator typed at a jailed CLI.</para>
///
/// <para><b>Ordering.</b> Frames leave in the order <see cref="EnqueueAsync"/> was CALLED, not the
/// order writers happen to be scheduled: the enqueue is a synchronous <c>TryWrite</c> under
/// <see cref="_gate"/>, so a caller's position is fixed before it ever awaits. Keystrokes must
/// arrive in typing order or the character loss becomes character transposition, which is worse.</para>
///
/// <para><b>Backpressure.</b> The queue is BOUNDED (<see cref="DefaultCapacity"/> frames). A stream
/// slow enough to bank that many frames is not coming back, so the enqueue fails loudly instead of
/// growing without limit — the caller sees a faulted task and the operator sees a banner.</para>
///
/// <para><b>Teardown.</b> <see cref="Close"/> fails everything still queued and every later enqueue
/// immediately. A write posted after detach must not hang waiting on a stream nobody will read.</para>
///
/// <para>Once one write fails the request stream is finished — gRPC gives no way to resume it — so
/// the fault is remembered and every subsequent frame fails with it. That is deliberate: it keeps
/// "your input did not arrive" true for every keystroke after the break rather than reporting
/// success for frames that go nowhere.</para>
/// </summary>
internal sealed class TerminalWriteQueue
{
    /// <summary>Frames that may be waiting to go out before an enqueue is refused.</summary>
    internal const int DefaultCapacity = 4096;

    internal const string StreamClosedMessage =
        "The terminal stream is closed — that input was not delivered.";

    private readonly IClientStreamWriter<TerminalInput> _writer;
    private readonly Channel<Pending> _queue;
    private readonly int _capacity;
    private readonly object _gate = new();

    private bool _closed;
    private Exception? _fault;

    public TerminalWriteQueue(IClientStreamWriter<TerminalInput> writer, int capacity = DefaultCapacity)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _capacity = capacity;
        _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait, // never reached: only TryWrite is used
        });

        Pump = Task.Run(PumpAsync);
    }

    /// <summary>The single writer loop. Exposed so teardown/tests can await a quiesced queue.</summary>
    internal Task Pump { get; }

    /// <summary>
    /// Queues <paramref name="frame"/> behind everything already queued. The returned task completes
    /// when the frame has ACTUALLY been written to the stream, and faults when it could not be —
    /// never before, and never silently.
    /// </summary>
    public Task EnqueueAsync(TerminalInput frame)
    {
        var pending = new Pending(frame);

        lock (_gate)
        {
            if (_closed || _fault is not null)
            {
                return Task.FromException(Closed(_fault));
            }

            if (!_queue.Writer.TryWrite(pending))
            {
                return Task.FromException(new InvalidOperationException(
                    $"The agent terminal is not keeping up — {_capacity} frames are still waiting to be sent, "
                    + "so this input was not delivered."));
            }
        }

        return pending.Completion.Task;
    }

    /// <summary>
    /// Stops the queue: everything still waiting fails, and so does every later enqueue. Cheap and
    /// synchronous — the caller is usually <c>Dispose</c> on the UI thread.
    /// </summary>
    public void Close()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _fault ??= new InvalidOperationException(StreamClosedMessage);
        }

        _queue.Writer.TryComplete();
    }

    private async Task PumpAsync()
    {
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (_queue.Reader.TryRead(out var pending))
            {
                Exception? fault;
                lock (_gate)
                {
                    fault = _fault;
                }

                if (fault is not null)
                {
                    // The stream already broke (or was closed under us): report, do not write.
                    pending.Completion.TrySetException(Closed(fault));
                    continue;
                }

                try
                {
                    await _writer.WriteAsync(pending.Frame).ConfigureAwait(false);
                    pending.Completion.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        _fault ??= ex;
                    }

                    pending.Completion.TrySetException(ex);
                }
            }
        }
    }

    private static Exception Closed(Exception? fault) => fault switch
    {
        null => new InvalidOperationException(StreamClosedMessage),
        // Re-throwing the captured instance would graft this caller's stack onto another caller's
        // exception; wrap so both the reason and the reporter stay honest.
        _ => new InvalidOperationException(StreamClosedMessage, fault),
    };

    private sealed class Pending(TerminalInput frame)
    {
        public TerminalInput Frame { get; } = frame;

        // RunContinuationsAsynchronously: a waiting caller's continuation must never run inline on
        // the pump thread, or one slow consumer stalls every other queued frame.
        public TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
