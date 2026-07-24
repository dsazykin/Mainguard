using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// MG-31 — the <see cref="RoleInterceptor"/>'s input-lock layer must sever input for <b>both</b> attach
/// handshakes. It tracked only the bare <c>agent_id</c> oneof, so a P2-18 grid client (which selects its
/// agent with the <c>Attach</c> oneof) left the tracked id null and every later <c>data</c> frame sailed
/// past the gate — the lock layer was a no-op for those clients.
///
/// <para>These drive <see cref="RoleInterceptor.LockedInputReader"/> <b>directly</b> on purpose: an
/// end-to-end gRPC assertion cannot prove this layer, because <c>TerminalGrpcService</c> re-checks the
/// lock itself (reading both oneofs) and would reject the input either way.</para>
/// </summary>
public sealed class AttachInputLockInterceptorTests
{
    private const string Agent = "locked-agent";

    [Fact]
    public async Task AttachHandshake_ToLockedAgent_DeniesInput()
    {
        var locks = new TerminalLockRegistry();
        locks.Lock(Agent);

        var reader = new RoleInterceptor.LockedInputReader(
            new FakeStream(
                new TerminalInput { Attach = new AttachOptions { AgentId = Agent, Grid = true } },
                new TerminalInput { Data = ByteString.CopyFromUtf8("rm -rf /\n") }),
            locks);

        Assert.True(await reader.MoveNext(CancellationToken.None)); // the Attach handshake passes
        var ex = await Assert.ThrowsAsync<RpcException>(() => reader.MoveNext(CancellationToken.None));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task BareAgentIdHandshake_ToLockedAgent_StillDeniesInput()
    {
        var locks = new TerminalLockRegistry();
        locks.Lock(Agent);

        var reader = new RoleInterceptor.LockedInputReader(
            new FakeStream(
                new TerminalInput { AgentId = Agent },
                new TerminalInput { Data = ByteString.CopyFromUtf8("whoami\n") }),
            locks);

        Assert.True(await reader.MoveNext(CancellationToken.None));
        var ex = await Assert.ThrowsAsync<RpcException>(() => reader.MoveNext(CancellationToken.None));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task AttachHandshake_ToUnlockedAgent_AllowsInput()
    {
        var reader = new RoleInterceptor.LockedInputReader(
            new FakeStream(
                new TerminalInput { Attach = new AttachOptions { AgentId = "free-agent", Grid = true } },
                new TerminalInput { Data = ByteString.CopyFromUtf8("echo hi\n") }),
            new TerminalLockRegistry());

        Assert.True(await reader.MoveNext(CancellationToken.None));
        Assert.True(await reader.MoveNext(CancellationToken.None)); // input flows on an unlocked agent
        Assert.False(await reader.MoveNext(CancellationToken.None));
    }

    // Resize is window geometry, not input — it must keep passing even on a locked session.
    [Fact]
    public async Task AttachHandshake_ToLockedAgent_StillAllowsResize()
    {
        var locks = new TerminalLockRegistry();
        locks.Lock(Agent);

        var reader = new RoleInterceptor.LockedInputReader(
            new FakeStream(
                new TerminalInput { Attach = new AttachOptions { AgentId = Agent, Grid = true } },
                new TerminalInput { Resize = new Resize { Cols = 100, Rows = 40 } }),
            locks);

        Assert.True(await reader.MoveNext(CancellationToken.None));
        Assert.True(await reader.MoveNext(CancellationToken.None));
    }

    private sealed class FakeStream : IAsyncStreamReader<TerminalInput>
    {
        private readonly IReadOnlyList<TerminalInput> _frames;
        private int _index = -1;

        public FakeStream(params TerminalInput[] frames) => _frames = frames;

        public TerminalInput Current => _frames[_index];

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            _index++;
            return Task.FromResult(_index < _frames.Count);
        }
    }
}
