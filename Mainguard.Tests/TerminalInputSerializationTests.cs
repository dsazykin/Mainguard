using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Agents.UI.Services;
using Xunit;
using Proto = Mainguard.Protos.V1;

namespace Mainguard.Tests;

/// <summary>
/// Stress S1 / G5 — the app silently dropped terminal keystrokes. A gRPC
/// <see cref="IClientStreamWriter{T}"/> permits exactly ONE in-flight <c>WriteAsync</c>; a second
/// concurrent caller gets <c>InvalidOperationException: Can't write the message because the previous
/// write is in progress</c>. <see cref="DaemonTerminalGateway"/> has three writers onto one request
/// stream (attach selector, every keystroke, the debounced resize) and the keystroke path is
/// fire-and-forget, so the loser's exception escaped into an unobserved task and the character was
/// gone. Three of those exceptions in the field, three characters missing — NOT the "automation
/// typing too fast" that walkthrough findings #21 and #30 concluded.
///
/// <para><see cref="RacyRequestStream"/> models the real writer's contract exactly (one in flight,
/// otherwise that exception) and holds each write open, so these fail deterministically on the
/// unfixed gateway instead of only when the machine is loaded.</para>
/// </summary>
public sealed class TerminalInputSerializationTests
{
    private const string GrpcConcurrentWriteMessage =
        "Can't write the message because the previous write is in progress.";

    private static DaemonClient UncontactedClient() =>
        new(() => GrpcChannel.ForAddress("http://127.0.0.1:1"), () => "token");

    /// <summary>Two keystrokes inside one write round-trip. Before the serializing queue the second
    /// one threw and the character was lost.</summary>
    [Fact]
    public async Task ConcurrentSends_ShouldAllBeDelivered_NotRaceTheStreamWriter()
    {
        const int keystrokes = 64;
        var stream = new RacyRequestStream(writeLatency: TimeSpan.FromMilliseconds(2));
        using var gateway = Attached(stream, out var attach);

        var sends = Enumerable.Range(0, keystrokes)
            .Select(i => Task.Run(() => gateway.SendInputAsync(new[] { (byte)i })))
            .ToArray();

        await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(stream.Violation);
        Assert.Equal(keystrokes, stream.DataFrames.Count);
        Assert.Equal(
            Enumerable.Range(0, keystrokes).Select(i => (byte)i).OrderBy(b => b),
            stream.DataFrames.Select(f => f[0]).OrderBy(b => b));
        await Detach(gateway, attach);
    }

    /// <summary>Ordering is the enqueue order, not whichever writer the scheduler happens to wake:
    /// keystrokes arriving out of order would turn character loss into character transposition.</summary>
    [Fact]
    public async Task SequentiallyIssuedSends_ShouldArriveInTypingOrder()
    {
        const int keystrokes = 200;
        var stream = new RacyRequestStream(writeLatency: TimeSpan.FromMilliseconds(1));
        using var gateway = Attached(stream, out var attach);

        // Issued from one thread and never awaited — exactly what TerminalViewModel.OnInputAvailable
        // does for every key event on the UI thread.
        var sends = new List<Task>(keystrokes);
        for (var i = 0; i < keystrokes; i++)
        {
            sends.Add(gateway.SendInputAsync(new[] { (byte)i }));
        }

        await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(stream.Violation);
        Assert.Equal(
            Enumerable.Range(0, keystrokes).Select(i => (byte)i),
            stream.DataFrames.Select(f => f[0]));
        await Detach(gateway, attach);
    }

    /// <summary>The attach selector is frame 1 even when a keystroke is already being typed — the
    /// daemon routes on that frame, so a data frame ahead of it would be routed at nothing.</summary>
    [Fact]
    public async Task AttachSelector_ShouldAlwaysBeTheFirstFrame_EvenUnderImmediateTyping()
    {
        var stream = new RacyRequestStream(writeLatency: TimeSpan.FromMilliseconds(2));
        using var gateway = Attached(stream, out var attach);

        var sends = Enumerable.Range(0, 32)
            .Select(i => Task.Run(() => gateway.SendInputAsync(new[] { (byte)i })))
            .ToArray();
        await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("agent-1", stream.Frames[0].AgentId);
        Assert.All(stream.Frames.Skip(1), f => Assert.Equal(
            Proto.TerminalInput.InputOneofCase.Data, f.InputCase));
        await Detach(gateway, attach);
    }

    /// <summary>Input and the debounced resize share the writer, so they raced each other too — not
    /// only keystroke against keystroke.</summary>
    [Fact]
    public async Task InputAndResize_ShouldNotRaceEachOther()
    {
        var stream = new RacyRequestStream(writeLatency: TimeSpan.FromMilliseconds(2));
        using var gateway = Attached(stream, out var attach);

        var work = new List<Task>();
        for (var i = 0; i < 32; i++)
        {
            var n = i;
            work.Add(Task.Run(() => gateway.SendInputAsync(new[] { (byte)n })));
            work.Add(Task.Run(() => gateway.SendResizeAsync(80 + n, 24)));
        }

        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(stream.Violation);
        Assert.Equal(32, stream.DataFrames.Count);
        Assert.Equal(32, stream.Frames.Count(f => f.InputCase == Proto.TerminalInput.InputOneofCase.Resize));
        await Detach(gateway, attach);
    }

    /// <summary>Teardown: a write queued after the stream closed fails CLEANLY and immediately. It
    /// must not hang waiting on a stream nobody will ever read.</summary>
    [Fact]
    public async Task SendAfterDispose_ShouldFailImmediately_NotHang()
    {
        var stream = new RacyRequestStream(writeLatency: TimeSpan.Zero);
        var gateway = Attached(stream, out var attach);
        gateway.Dispose();

        var send = gateway.SendInputAsync("x"u8.ToArray());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => send.WaitAsync(TimeSpan.FromSeconds(5)));

        // The CLOSED reason, not the queue-full one: "the terminal is not keeping up" would be a lie
        // about a stream that is simply gone, and it is what the operator would be shown.
        Assert.Equal(TerminalWriteQueue.StreamClosedMessage, ex.Message);
        await Detach(gateway, attach);
    }

    /// <summary>Backpressure: a stream slow enough to bank the whole queue refuses further input
    /// loudly rather than growing without bound. The frames already accepted still go out.</summary>
    [Fact]
    public async Task WhenTheQueueIsFull_ShouldRefuseLoudly_RatherThanGrowUnbounded()
    {
        var stream = new RacyRequestStream(writeLatency: TimeSpan.Zero) { Gate = new TaskCompletionSource() };
        using var client = UncontactedClient();
        using var gateway = new DaemonTerminalGateway(client, useGridEngine: false)
        {
            AttachOverride = _ => FakeCall(stream),
            WriteQueueCapacity = 8,
        };
        var attach = gateway.AttachAsync("agent-1", CancellationToken.None);

        // The selector is in flight against the held gate; fill the queue behind it, then overflow.
        var accepted = new List<Task>();
        Exception? refusal = null;
        for (var i = 0; i < 64; i++)
        {
            var send = gateway.SendInputAsync(new[] { (byte)i });
            if (send.IsFaulted)
            {
                refusal = send.Exception?.GetBaseException();
                break;
            }

            accepted.Add(send);
        }

        Assert.NotNull(refusal);
        Assert.IsType<InvalidOperationException>(refusal);
        Assert.Contains("not keeping up", refusal!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(accepted.Count <= 8 + 1, $"queue grew past its bound: {accepted.Count} accepted");

        stream.Gate!.SetResult();
        await Task.WhenAll(accepted).WaitAsync(TimeSpan.FromSeconds(30));
        await Detach(gateway, attach);
    }

    /// <summary>A write the transport genuinely refuses surfaces to the caller. It used to become an
    /// unobserved task, which is how three characters left no trace but a log line.</summary>
    [Fact]
    public async Task WhenTheStreamRefusesTheWrite_ShouldFaultTheCaller_NotSwallowIt()
    {
        var stream = new RacyRequestStream(writeLatency: TimeSpan.Zero)
        {
            ThrowOnData = new RpcException(new Status(StatusCode.Unavailable, "daemon went away")),
        };
        using var gateway = Attached(stream, out var attach);

        await Assert.ThrowsAsync<RpcException>(
            () => gateway.SendInputAsync("a"u8.ToArray()).WaitAsync(TimeSpan.FromSeconds(5)));

        // And the stream stays broken rather than reporting success for frames that go nowhere.
        var after = await Record.ExceptionAsync(
            () => gateway.SendInputAsync("b"u8.ToArray()).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(after);
        await Detach(gateway, attach);
    }

    /// <summary>Input typed at a pane that never attached used to return <c>Task.CompletedTask</c> —
    /// reporting success for a keystroke that went nowhere.</summary>
    [Fact]
    public async Task SendBeforeAttach_ShouldReportUndelivered_NotSilentSuccess()
    {
        using var client = UncontactedClient();
        using var gateway = new DaemonTerminalGateway(client, useGridEngine: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.SendInputAsync("x"u8.ToArray()));
        Assert.Contains("not delivered", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static DaemonTerminalGateway Attached(RacyRequestStream stream, out Task attach)
    {
        var client = UncontactedClient();
        var gateway = new DaemonTerminalGateway(client, useGridEngine: false)
        {
            AttachOverride = _ => FakeCall(stream),
        };
        attach = gateway.AttachAsync("agent-1", CancellationToken.None);
        return gateway;
    }

    private static async Task Detach(DaemonTerminalGateway gateway, Task attach)
    {
        gateway.Dispose();
        await Record.ExceptionAsync(() => attach.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static AsyncDuplexStreamingCall<Proto.TerminalInput, Proto.TerminalOutput> FakeCall(
        RacyRequestStream requests) =>
        new(
            requests,
            new PendingResponseStream(),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

    /// <summary>
    /// A request stream with the real <c>HttpContentClientStreamWriter</c>'s contract: exactly one
    /// write in flight, and a second concurrent one throws the same exception with the same message.
    /// The latency is what makes the race deterministic — a write that completes synchronously never
    /// overlaps anything, which is why the in-proc daemon tier alone could not reproduce this.
    /// </summary>
    private sealed class RacyRequestStream : IClientStreamWriter<Proto.TerminalInput>
    {
        private readonly TimeSpan _latency;
        private int _inFlight;
        private readonly object _sync = new();
        private readonly List<Proto.TerminalInput> _frames = new();

        public RacyRequestStream(TimeSpan writeLatency) => _latency = writeLatency;

        /// <summary>Held open to keep a write in flight (the backpressure test).</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>Thrown by the next data frame's write (the transport-refusal test).</summary>
        public Exception? ThrowOnData { get; set; }

        /// <summary>Set when two writes overlapped — i.e. the defect happened.</summary>
        public Exception? Violation { get; private set; }

        public WriteOptions? WriteOptions { get; set; }

        public IReadOnlyList<Proto.TerminalInput> Frames
        {
            get { lock (_sync) return _frames.ToArray(); }
        }

        public IReadOnlyList<byte[]> DataFrames => Frames
            .Where(f => f.InputCase == Proto.TerminalInput.InputOneofCase.Data)
            .Select(f => f.Data.ToByteArray())
            .ToArray();

        public async Task WriteAsync(Proto.TerminalInput message)
        {
            if (Interlocked.Increment(ref _inFlight) != 1)
            {
                Interlocked.Decrement(ref _inFlight);
                var violation = new InvalidOperationException(GrpcConcurrentWriteMessage);
                Violation ??= violation;
                throw violation;
            }

            try
            {
                if (Gate is { } gate)
                {
                    await gate.Task.ConfigureAwait(false);
                }

                if (_latency > TimeSpan.Zero)
                {
                    await Task.Delay(_latency).ConfigureAwait(false);
                }
                else
                {
                    await Task.Yield();
                }

                if (message.InputCase == Proto.TerminalInput.InputOneofCase.Data
                    && ThrowOnData is { } refusal)
                {
                    throw refusal;
                }

                lock (_sync)
                {
                    _frames.Add(message);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task CompleteAsync() => Task.CompletedTask;
    }

    /// <summary>Stays pending until the call is cancelled — the real server keeps the read side open.</summary>
    private sealed class PendingResponseStream : IAsyncStreamReader<Proto.TerminalOutput>
    {
        public Proto.TerminalOutput Current => throw new InvalidOperationException();

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            return false;
        }
    }
}
