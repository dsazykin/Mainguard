using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Mainguard.Agents.UI.Services;
using Mainguard.Protos.V1;
using Mainguard.Server.Tests.Fixtures;

namespace Mainguard.Server.Tests;

/// <summary>
/// Stress S1 / G5 — terminal keystrokes were silently dropped while typing at an agent. Three
/// <c>InvalidOperationException: Can't write the message because the previous write is in progress</c>
/// at <c>DaemonTerminalGateway.SendInputAsync</c>, matching EXACTLY the three characters lost. The
/// cause is a gRPC duplex-stream constraint (one in-flight <c>WriteAsync</c> per
/// <see cref="IClientStreamWriter{T}"/>) meeting a fire-and-forget caller, not automation typing too
/// fast (which is what walkthrough findings #21 and #30 concluded — both wrong).
///
/// <para>These run the REAL client against the REAL in-proc daemon over real Grpc.Net.Client, so the
/// constraint being defended against is the shipped one and not a test double's idea of it.</para>
/// </summary>
public sealed class TerminalInputSerializationRpcTests : IClassFixture<DaemonFixture>
{
    private readonly DaemonFixture _daemon;

    public TerminalInputSerializationRpcTests(DaemonFixture daemon) => _daemon = daemon;

    private DaemonClient Client() => new(() => _daemon.CreateChannel(), () => _daemon.Token);

    /// <summary>
    /// The constraint itself, pinned on the real stack: two concurrent writes to one request stream
    /// throw. If gRPC ever serializes internally this test goes green-for-the-wrong-reason, so it
    /// asserts the exception rather than merely tolerating it — a change here is a signal, not noise.
    ///
    /// <para><b>It drains the response stream, and it has a deadline (2026-09-12).</b> It did neither,
    /// and that is what hung CI for six hours: <c>Attach</c> is a DUPLEX stream and the daemon echoes
    /// every input back, so 64 unread 64 KiB echoes filled the response pipe, the server's write blocked
    /// on <c>ResponseBodyPipeWriter.FlushAsync</c>, it stopped draining the REQUEST stream, and the
    /// client's surviving <c>WriteAsync</c> blocked on back-pressure — both ends parked with no deadline
    /// on either. Whether it wedged was a race between "a second write overlaps and throws" and "enough
    /// bytes land to fill the pipe", so it passed in 9 s most of the time and, when it lost, took the
    /// whole <c>Mainguard.Server.Tests</c> run with it: the collection runner waits for it forever, so
    /// every later test in the assembly is simply never reported (run 34259331113 — 367 of 1011 results,
    /// then nothing until the 6 h job timeout cancelled the job). Draining is also the honest shape: a
    /// real client reads what it is sent. The deadline is the belt — a future regression here must FAIL,
    /// not hang.</para>
    /// </summary>
    [Fact]
    public async Task RawRequestStream_ConcurrentWrites_ThrowTheOneInFlightWriteConstraint()
    {
        var client = new TerminalService.TerminalServiceClient(_daemon.CreateChannel());
        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var call = client.Attach(
            _daemon.AuthHeaders(), deadline: DateTime.UtcNow.AddSeconds(60), cancellationToken: drain.Token);

        // The reader. Without it the daemon's echo back-pressures the request stream and every writer
        // below blocks forever; with it the constraint under test is still what decides the outcome.
        var reader = Task.Run(async () =>
        {
            try
            {
                while (await call.ResponseStream.MoveNext(drain.Token))
                {
                }
            }
            catch (OperationCanceledException) when (drain.IsCancellationRequested)
            {
                // The only expected end: the test finished and cancelled the drain itself.
            }
            catch (RpcException rpc) when (rpc.StatusCode == StatusCode.Cancelled && drain.IsCancellationRequested)
            {
                // Same ending, surfaced through gRPC because the call was torn down under it.
            }
        });

        await call.RequestStream.WriteAsync(new TerminalInput { AgentId = "agent-race" });

        var start = new SemaphoreSlim(0, 64);
        var failures = new ConcurrentBag<Exception>();
        var writers = Enumerable.Range(0, 64).Select(i => Task.Run(async () =>
        {
            await start.WaitAsync();
            try
            {
                await call.RequestStream.WriteAsync(new TerminalInput
                {
                    Data = ByteString.CopyFrom(new byte[64 * 1024]),
                });
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        })).ToArray();

        start.Release(64);
        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Contains(failures, ex =>
            ex is InvalidOperationException && ex.Message.Contains("previous write is in progress"));

        drain.Cancel();

        // Assert the DRAIN's outcome too. Swallowing every exception here let a broken Attach/echo path
        // look like a clean drain: the writer raises "previous write is in progress" on the client side
        // all by itself, so the assertion above would still pass while the back-pressure protection this
        // test exists to hold was never exercised. Only the test's own cancellation is an expected end;
        // anything else now fails the test rather than being discarded.
        await reader.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The defect as the operator met it, on the real stack: input handed to the gateway concurrently
    /// (the ViewModel forwards every frame fire-and-forget) must ALL arrive, in order, with nothing
    /// thrown. Before the serializing write queue this failed with the constraint above and the
    /// losing frames were simply gone.
    ///
    /// <para>The frames are paste-sized rather than single keystrokes for the same reason the test
    /// above needs them: over the in-proc transport a small write completes before the next call
    /// starts, so nothing overlaps. On a real socket a single keystroke is enough — which is why the
    /// deterministic version of this lives in <c>Mainguard.Tests</c>
    /// (<c>TerminalInputSerializationTests</c>) against a stream that models the writer's contract
    /// directly.</para>
    /// </summary>
    [Fact]
    public async Task Gateway_ConcurrentInput_AllArrivesInOrder_AndNothingIsDropped()
    {
        const int frames = 24;
        const int frameSize = 64 * 1024;

        using var client = Client();
        using var gateway = new DaemonTerminalGateway(client, useGridEngine: false);

        var echoed = new List<byte>();
        var allEchoed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.OutputReceived += bytes =>
        {
            lock (echoed)
            {
                echoed.AddRange(bytes.ToArray());
                if (echoed.Count >= frames * frameSize) allEchoed.TrySetResult();
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var pump = gateway.AttachAsync("agent-race", cts.Token);

        // Issued from the caller's thread in order (as the UI thread does), each one unawaited (as
        // TerminalViewModel.OnInputAvailable does) — so every write overlaps the one before it.
        var sends = new List<Task>(frames);
        for (var i = 0; i < frames; i++)
        {
            var chunk = new byte[frameSize];
            Array.Fill(chunk, (byte)i);
            sends.Add(gateway.SendInputAsync(chunk));
        }

        await Task.WhenAll(sends); // a fault here is a frame the app would have thrown away

        await allEchoed.Task.WaitAsync(TimeSpan.FromSeconds(60));
        lock (echoed)
        {
            // Byte-exact and in order: every frame arrived, none interleaved with another.
            for (var i = 0; i < frames; i++)
            {
                Assert.Equal((byte)i, echoed[i * frameSize]);
                Assert.Equal((byte)i, echoed[(i * frameSize) + frameSize - 1]);
            }

            Assert.Equal(frames * frameSize, echoed.Count);
        }

        cts.Cancel();
        await pump.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
