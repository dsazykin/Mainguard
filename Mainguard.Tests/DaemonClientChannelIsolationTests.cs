using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Mainguard.Agents.UI.Services;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Regression for a bug found live on 2026-08-20: a coordinator's attached terminal
/// (<c>TerminalService/Attach</c>, a continuous PTY byte stream) shared one HTTP/2 connection with
/// <c>StreamQueueAsync</c>, and could saturate that connection's flow-control window — even while
/// idling — so a fresh queue entry's push sat unsent for minutes. <see cref="DaemonClient"/> now
/// gives <c>StreamQueueAsync</c> its own connection (<c>StreamChannel()</c>), separate from the one
/// every other RPC — including <c>AttachTerminal</c> — shares (<c>Channel()</c>).
///
/// <para>No live server is needed to prove the isolation: channel creation is a synchronous,
/// factory-driven side effect that happens before any network I/O, so a pre-cancelled token lets each
/// call reach its channel-creation line and then fail fast on cancellation instead of attempting a
/// real connection.</para>
/// </summary>
public sealed class DaemonClientChannelIsolationTests
{
    [Fact]
    public async Task StreamQueueAsync_GetsItsOwnChannel_SeparateFromAttachTerminalAndEveryOtherRpc()
    {
        var created = new List<GrpcChannel>();
        using var client = new DaemonClient(
            () =>
            {
                var channel = GrpcChannel.ForAddress("http://127.0.0.1:1");
                created.Add(channel);
                return channel;
            },
            () => "token");

        // AttachTerminal creates/uses Channel() synchronously, with no I/O required to observe it.
        using (var terminalCall = client.AttachTerminal(CancellationToken.None))
        {
        }

        Assert.Single(created);
        var terminalChannel = created[0];

        // StreamQueueAsync is an iterator method: its body — including the StreamChannel() call —
        // only runs once enumeration starts. A pre-cancelled token reaches that line and then fails
        // fast on cancellation, never attempting a real connection to the bogus address.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using var enumerator = client.StreamQueueAsync("repo-handle", cancelled.Token).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<Grpc.Core.RpcException>(async () => await enumerator.MoveNextAsync());

        Assert.Equal(2, created.Count);
        var queueChannel = created[1];
        Assert.NotSame(terminalChannel, queueChannel);

        // A second, unrelated Channel()-using RPC (also pre-cancelled, same reasoning) proves the
        // shared channel is still cached and reused — only the queue stream was carved out, not every
        // call turned into its own connection.
        using var cancelled2 = new CancellationTokenSource();
        cancelled2.Cancel();
        await Assert.ThrowsAnyAsync<Grpc.Core.RpcException>(
            () => client.RunVerificationAsync("repo-handle", "agent-id", cancelled2.Token));

        Assert.Equal(2, created.Count);
    }
}
