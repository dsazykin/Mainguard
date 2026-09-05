using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Mainguard.Protos.V1;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The ViewModel-facing seam onto the daemon terminal stream. Keeping the gRPC bidi call behind an
/// interface lets the <see cref="ViewModels.TerminalViewModel"/> be tested with a fake (no daemon),
/// and keeps the App's only daemon touch-point the <see cref="DaemonClient"/> (G-18).
/// </summary>
public interface ITerminalGateway : IDisposable
{
    /// <summary>Raised for each <c>raw</c> output frame the daemon streams from the PTY.</summary>
    event Action<ReadOnlyMemory<byte>>? OutputReceived;

    /// <summary>Attaches to <paramref name="agentId"/> and begins pumping output until cancelled.</summary>
    Task AttachAsync(string agentId, CancellationToken ct);

    /// <summary>
    /// Sends keystrokes/paste toward the PTY. The returned task completes only once the bytes have
    /// ACTUALLY been written to the stream, and FAULTS when they could not be — a caller that drops
    /// it on the floor is dropping the operator's keystroke silently, which is the bug this contract
    /// exists to prevent.
    /// </summary>
    Task SendInputAsync(ReadOnlyMemory<byte> data);

    /// <summary>Sends a terminal resize (SIGWINCH) toward the PTY.</summary>
    Task SendResizeAsync(int cols, int rows);
}

/// <summary>
/// <see cref="ITerminalGateway"/> over the daemon's <c>TerminalService.Attach</c> bidi stream via
/// <see cref="DaemonClient"/>. Writes the first selector frame, then forwards input/resize frames
/// and raises <see cref="OutputReceived"/> for each output frame.
///
/// <para>P2-18: with the grid engine selected, the first frame is <c>AttachOptions(grid: true)</c>
/// and grid/clipboard frames are forwarded as serialized <see cref="TerminalOutput"/> envelopes
/// through the SAME byte event — the ViewModel shuttles opaque bytes either way (zero VM change),
/// and the engine control on the other end knows which encoding it subscribed for. <c>raw</c>
/// frames keep their P2-03 byte semantics untouched.</para>
///
/// <para><b>Every</b> frame — selector, input, resize — leaves through one
/// <see cref="TerminalWriteQueue"/>. gRPC permits a single in-flight <c>WriteAsync</c> per request
/// stream and this class has three concurrent writers, so before the queue a keystroke landing
/// inside another frame's write round-trip threw <c>Can't write the message because the previous
/// write is in progress</c> into a fire-and-forget task and simply vanished (stress S1 / G5 — three
/// exceptions, three characters lost from what the operator typed at a jailed CLI). The queue makes
/// the second writer wait its turn instead of losing.</para>
/// </summary>
public sealed class DaemonTerminalGateway : ITerminalGateway
{
    internal const string NotAttachedMessage =
        "The agent terminal is not connected — that input was not delivered.";

    private readonly DaemonClient _client;
    private readonly bool _grid;
    private readonly object _gate = new();
    private Grpc.Core.AsyncDuplexStreamingCall<TerminalInput, TerminalOutput>? _call;
    private TerminalWriteQueue? _writes;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public DaemonTerminalGateway(DaemonClient client, bool? useGridEngine = null)
    {
        _client = client;
        _grid = useGridEngine ?? TerminalEngineSelection.UseGridEngine;
    }

    /// <summary>
    /// Test seam: replaces the attach call this gateway writes through, so the concurrency, ordering
    /// and teardown behaviour can be driven against a fake request stream with no daemon. Never set
    /// in production (where it is <see cref="DaemonClient.AttachTerminal"/>) — the same shape as
    /// <c>DaemonBackedOrchestrator.AttachTerminalOverride</c>.
    /// </summary>
    internal Func<CancellationToken, Grpc.Core.AsyncDuplexStreamingCall<TerminalInput, TerminalOutput>>?
        AttachOverride
    { get; set; }

    /// <summary>Bounded depth of the serialized write queue. Settable so the backpressure test can
    /// reach the limit in milliseconds; it changes the CAPACITY only, never whether it is enforced.</summary>
    internal int WriteQueueCapacity { get; set; } = TerminalWriteQueue.DefaultCapacity;

    public event Action<ReadOnlyMemory<byte>>? OutputReceived;

    public async Task AttachAsync(string agentId, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var call = (AttachOverride ?? _client.AttachTerminal)(cts.Token);
        var writes = new TerminalWriteQueue(call.RequestStream, WriteQueueCapacity);

        var first = _grid
            ? new TerminalInput { Attach = new AttachOptions { AgentId = agentId, Grid = true } }
            : new TerminalInput { AgentId = agentId };

        // Queued BEFORE the queue is published, so the selector frame can never be overtaken by a
        // keystroke arriving while this method is still setting up. The daemon reads frame 1 as the
        // agent selector; a data frame ahead of it would be routed at nothing.
        var selector = writes.EnqueueAsync(first);

        bool disposed;
        lock (_gate)
        {
            disposed = _disposed;
            if (!disposed)
            {
                _cts = cts;
                _call = call;
                _writes = writes;
            }
        }

        if (disposed)
        {
            // Disposed out from under the attach — tear the half-built call down, don't leak it.
            writes.Close();
            cts.Cancel();
            call.Dispose();
            cts.Dispose();
            return;
        }

        await selector.ConfigureAwait(false);

        try
        {
            await foreach (var output in call.ResponseStream.ReadAllAsync(cts.Token))
            {
                switch (output.FrameCase)
                {
                    case TerminalOutput.FrameOneofCase.Raw:
                        OutputReceived?.Invoke(output.Raw.Memory);
                        break;
                    case TerminalOutput.FrameOneofCase.Grid:
                    case TerminalOutput.FrameOneofCase.Clipboard:
                        OutputReceived?.Invoke(output.ToByteArray());
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Detached — normal teardown.
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            // The same teardown, as the transport spells it: cancelling the linked token surfaces
            // here as a Cancelled RpcException, not an OperationCanceledException.
        }
    }

    public Task SendInputAsync(ReadOnlyMemory<byte> data)
    {
        if (Queue() is not { } writes)
        {
            // Was a silent Task.CompletedTask — a keystroke typed at an unattached pane reported
            // success and went nowhere. Say so instead; the ViewModel surfaces it.
            return Task.FromException(new InvalidOperationException(NotAttachedMessage));
        }

        return writes.EnqueueAsync(new TerminalInput { Data = ByteString.CopyFrom(data.Span) });
    }

    public Task SendResizeAsync(int cols, int rows)
    {
        if (Queue() is not { } writes)
        {
            return Task.FromException(new InvalidOperationException(NotAttachedMessage));
        }

        return writes.EnqueueAsync(new TerminalInput
        {
            Resize = new Resize { Cols = (uint)cols, Rows = (uint)rows },
        });
    }

    public void Dispose()
    {
        TerminalWriteQueue? writes;
        CancellationTokenSource? cts;
        Grpc.Core.AsyncDuplexStreamingCall<TerminalInput, TerminalOutput>? call;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            writes = _writes;
            cts = _cts;
            call = _call;

            // _writes is deliberately KEPT: a closed queue is the honest reporter for input typed
            // after a detach ("the terminal stream is closed"), where nulling it would fall back to
            // the not-connected message and describe the wrong thing.
        }

        // Order matters: fail the queue FIRST, so a frame enqueued mid-teardown reports "closed"
        // instead of being handed to a request stream that is about to be disposed under it.
        writes?.Close();
        cts?.Cancel();
        call?.Dispose();
        cts?.Dispose();
    }

    private TerminalWriteQueue? Queue()
    {
        lock (_gate)
        {
            return _writes;
        }
    }
}
