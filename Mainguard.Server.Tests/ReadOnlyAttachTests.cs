using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Mainguard.Agents.Agents;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// F64 — what "read-only attach" has to mean, and the two ways it did not.
///
/// <list type="number">
///   <item><b>Resize was forwarded.</b> The bound-session pumps treated a resize as "harmless window
///   geometry" and passed it straight through, so a read-only spectator drove SIGWINCH into a managed
///   worker's CLI and reflowed the daemon's authoritative grid — which every OTHER viewer, including the
///   coordinator mid-turn, then saw repaint under them.</item>
///   <item><b>Every concurrent attach could write.</b> Input was not exclusive, and
///   <c>BoundTerminalSession.WriteInputAsync</c> did not even serialize write+flush on a shared
///   <c>Stream</c>, so two attaches typing at once interleaved at the byte level — splitting escape
///   sequences and UTF-8 codepoints.</item>
/// </list>
///
/// <para>(The third item in the finding — the lock state being captured once per attach — is covered by
/// <see cref="LockAppliedMidAttach_IsHonouredOnTheNextFrame"/>.)</para>
/// </summary>
public sealed class ReadOnlyAttachTests
{
    private const string RepoHash = "repo-f64";
    private const string AgentId = "worker-f64";

    [Fact]
    public async Task LockedAttach_CannotResizeTheManagedWorkersTerminal()
    {
        using var fixture = new DaemonFixture();
        using var cli = new RecordingSession();
        BindBoundSession(fixture, cli);
        fixture.Services.GetRequiredService<TerminalLockRegistry>().Lock(AgentId);

        var client = new TerminalService.TerminalServiceClient(fixture.CreateChannel());
        using var call = client.Attach(fixture.AuthHeaders());

        await call.RequestStream.WriteAsync(new TerminalInput { AgentId = AgentId });
        // The read-only banner proves the output stream is open before we test the input direction.
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Contains("read-only", call.ResponseStream.Current.Raw.ToStringUtf8());

        await call.RequestStream.WriteAsync(new TerminalInput
        {
            Resize = new Resize { Cols = 40, Rows = 10 },
        });

        // The server reads input frames sequentially, so a refusal of the DATA frame that follows is
        // proof the resize before it has already been handled. Draining to completion would not work:
        // a bound attach streams until the client cancels or the CLI exits, which is the point of it.
        await call.RequestStream.WriteAsync(new TerminalInput { Data = ByteString.CopyFromUtf8("x") });
        await call.RequestStream.CompleteAsync();

        var refusal = await Assert.ThrowsAsync<RpcException>(() => DrainAsync(call));
        Assert.Equal(StatusCode.PermissionDenied, refusal.StatusCode);

        Assert.Null(cli.LastResize);
    }

    [Fact]
    public async Task UnlockedAttach_StillResizes()
    {
        using var fixture = new DaemonFixture();
        using var cli = new RecordingSession();
        BindBoundSession(fixture, cli);

        var client = new TerminalService.TerminalServiceClient(fixture.CreateChannel());
        using var call = client.Attach(fixture.AuthHeaders());

        await call.RequestStream.WriteAsync(new TerminalInput { AgentId = AgentId });
        await call.RequestStream.WriteAsync(new TerminalInput
        {
            Resize = new Resize { Cols = 100, Rows = 30 },
        });

        await WaitForAsync(() => cli.LastResize is not null);
        Assert.Equal((100, 30), cli.LastResize);
    }

    /// <summary>
    /// The lock is read LIVE. An attach opened while the worker was free must become read-only the moment
    /// the worker is locked, not at the operator's next reattach — the lock is applied when a worker
    /// becomes managed, which routinely happens while somebody is already watching.
    /// </summary>
    [Fact]
    public async Task LockAppliedMidAttach_IsHonouredOnTheNextFrame()
    {
        using var fixture = new DaemonFixture();
        using var cli = new RecordingSession();
        BindBoundSession(fixture, cli);

        var client = new TerminalService.TerminalServiceClient(fixture.CreateChannel());
        using var call = client.Attach(fixture.AuthHeaders());

        await call.RequestStream.WriteAsync(new TerminalInput { AgentId = AgentId });
        await call.RequestStream.WriteAsync(new TerminalInput
        {
            Resize = new Resize { Cols = 100, Rows = 30 },
        });

        // The worker becomes managed while this attach is open.
        fixture.Services.GetRequiredService<TerminalLockRegistry>().Lock(AgentId);

        await call.RequestStream.WriteAsync(new TerminalInput
        {
            Data = ByteString.CopyFromUtf8("rm -rf /\n"),
        });
        await call.RequestStream.CompleteAsync();

        var error = await Assert.ThrowsAsync<RpcException>(() => DrainAsync(call));
        Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
        Assert.DoesNotContain("rm -rf", cli.WrittenText);
    }

    /// <summary>
    /// Input is exclusive: the second attach to type is refused while the first holds the claim, and the
    /// claim is released when that attach ends so the terminal can be handed over rather than wedged.
    /// </summary>
    [Fact]
    public void InputClaim_IsExclusive_AndReleasedOnDispose()
    {
        using var cli = new RecordingSession();
        using var bound = new BoundTerminalSession(AgentId, cli);

        var first = bound.TryClaimInput();
        Assert.NotNull(first);
        Assert.True(bound.IsInputClaimed);
        Assert.Null(bound.TryClaimInput());

        first!.Dispose();
        Assert.False(bound.IsInputClaimed);

        using var second = bound.TryClaimInput();
        Assert.NotNull(second);
    }

    /// <summary>
    /// Concurrent writes are serialized end to end. Each write here is a distinct multi-byte payload; if
    /// write+flush were not one indivisible act the bytes of one could appear inside another.
    /// </summary>
    [Fact]
    public async Task ConcurrentWrites_AreNotInterleaved()
    {
        using var cli = new RecordingSession();
        using var bound = new BoundTerminalSession(AgentId, cli);

        const int writers = 8;
        const int perWriter = 25;
        var payloads = new string[writers];
        for (var i = 0; i < writers; i++)
        {
            payloads[i] = new string((char)('a' + i), 16);
        }

        var tasks = new Task[writers];
        for (var i = 0; i < writers; i++)
        {
            var payload = System.Text.Encoding.UTF8.GetBytes(payloads[i]);
            tasks[i] = Task.Run(async () =>
            {
                for (var n = 0; n < perWriter; n++)
                {
                    await bound.WriteInputAsync(payload, CancellationToken.None);
                }
            });
        }

        await Task.WhenAll(tasks);

        var written = cli.WrittenText;
        Assert.Equal(writers * perWriter * 16, written.Length);
        // Every 16-byte block must be one writer's payload, unbroken.
        for (var offset = 0; offset < written.Length; offset += 16)
        {
            var block = written.Substring(offset, 16);
            Assert.Contains(block, payloads);
        }
    }

    private static void BindBoundSession(DaemonFixture fixture, ITerminalSession cli)
        => fixture.Services.GetRequiredService<TerminalSessionManager>()
            .Bind(new AgentSessionKey(RepoHash, AgentId), new BoundTerminalSession(AgentId, cli));

    /// <summary>Polls a condition the server satisfies asynchronously, with a bounded wait.</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the daemon never applied the expected change.");
            await Task.Delay(25);
        }
    }

    private static async Task DrainAsync(AsyncDuplexStreamingCall<TerminalInput, TerminalOutput> call)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await call.ResponseStream.MoveNext(deadline.Token))
        {
            // Frames are irrelevant here; reaching the end of the stream is what proves the server
            // consumed every input frame we sent.
        }
    }

    /// <summary>
    /// A CLI double: output is an empty pipe the session's pump can read from forever, input is captured,
    /// and resizes are recorded rather than applied.
    /// </summary>
    private sealed class RecordingSession : ITerminalSession
    {
        private readonly Pipe _output = new();
        private readonly MemoryStream _input = new();
        private readonly TaskCompletionSource<int> _exit = new();
        private readonly DuplexStream _io;

        public RecordingSession() => _io = new DuplexStream(_output.Reader.AsStream(), _input);

        public Stream IO => _io;

        public Task<int> ExitCode => _exit.Task;

        public (int Cols, int Rows)? LastResize { get; private set; }

        public bool Killed { get; private set; }

        public string WrittenText
        {
            get { lock (_input) { return System.Text.Encoding.UTF8.GetString(_input.ToArray()); } }
        }

        public void Resize(int cols, int rows) => LastResize = (cols, rows);

        public void Kill()
        {
            Killed = true;
            _exit.TrySetResult(0);
        }

        public void Dispose() => _exit.TrySetResult(0);

        /// <summary>Reads come from the PTY-output pipe; writes go to the captured input buffer.</summary>
        private sealed class DuplexStream(Stream reader, MemoryStream writer) : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public override int Read(byte[] buffer, int offset, int count) =>
                reader.Read(buffer, offset, count);

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                reader.ReadAsync(buffer, cancellationToken);

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                lock (writer)
                {
                    writer.Write(buffer, offset, count);
                }
            }

            public override ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                lock (writer)
                {
                    writer.Write(buffer.Span);
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
