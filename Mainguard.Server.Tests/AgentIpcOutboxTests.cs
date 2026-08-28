using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Server.Runtime;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The file-framed half of the agent-IPC channel, at the daemon.
///
/// <para><b>Why it exists.</b> The channel was a Unix socket in a directory bind-mounted into the jail,
/// and on macOS that channel is not a channel: the daemon runs natively on the host, jails run inside the
/// container engine's Linux VM, and Docker's file sharing (virtiofs / gRPC-FUSE) does not proxy AF_UNIX
/// across that boundary. The socket bind-mounts in as an inert inode — it stat()s as a socket and every
/// <c>connect()</c> returns ECONNREFUSED with the daemon demonstrably listening (verified with a real
/// jail against a real listener; a FIFO fails the same way, plain files do not). Every coordinator tool
/// of contract §3 and the whole worker plan gate were therefore unreachable on that platform, with the
/// entire suite green — because every test of them dialled the socket from the daemon's own kernel.</para>
///
/// <para>So these tests never dial the socket. They put bytes in the outbox exactly as an in-jail shim
/// does — stage, rename, poll for the answer — and read what comes back. Same daemon, same handler, same
/// role; only the framing differs, which is the point.</para>
/// </summary>
public sealed class AgentIpcOutboxTests
{
    /// <summary>
    /// Kept deliberately short. <c>sockaddr_un.sun_path</c> holds 104 bytes on macOS and
    /// <see cref="AgentIpcServer"/> binds a socket per endpoint even for a test that never dials it, so a
    /// chatty root here fails the class on path length rather than on anything it means to measure — see
    /// <see cref="TestDataRootIsolation"/>, which budgets the assembly's root for exactly this.
    /// </summary>
    private static string NewRoot() => Path.Combine(Mainguard.Git.MainguardPaths.DataRoot(), "ob");

    private static AgentIpcServer NewServer() => new(NewRoot());

    // ---- the channel ---------------------------------------------------------------------------

    [Fact]
    public async Task ARequestDroppedInTheOutbox_IsServedByTheSameHandlerTheSocketWouldReach()
    {
        using var server = NewServer();
        var agentId = NewAgentId();
        var seenAgentId = "";
        var dir = server.CreateEndpoint(agentId, (request, id, _) =>
        {
            seenAgentId = id;
            return Task.FromResult(new AgentIpcResponse(Ok: true, Status: "served:" + request.Op));
        });

        var response = await CallOverOutboxAsync(server, agentId, new AgentIpcRequest(AgentIpcRequest.StatusOp));

        Assert.True(response.Ok, response.Error);
        Assert.Equal("served:status", response.Status);

        // Identity stays POSITIONAL under the new framing: the agent id the handler is told is the one
        // the endpoint was created with, never anything the request carried. Nothing in the outbox names
        // an agent, and that is what keeps one jail's mailbox from being another's.
        Assert.Equal(agentId, seenAgentId);

        // Nothing half-finished is left lying in a directory the jail can also write.
        var leftovers = Directory.GetFiles(AgentIpcPaths.OutboxIn(dir)).Select(Path.GetFileName).ToArray();
        Assert.Empty(leftovers);
    }

    [Fact]
    public void EveryEndpoint_GetsAnOutbox_BesideTheSocketAndTheShim()
    {
        using var server = NewServer();
        var agentId = NewAgentId();
        var dir = server.CreateEndpoint(agentId, (_, _, _) => Task.FromResult(new AgentIpcResponse(Ok: true)));

        // Created on EVERY platform, not only the one that needs it: a code path that runs in one place
        // is a code path nothing else tests, which is the shape of the bug this file exists for.
        Assert.True(Directory.Exists(AgentIpcPaths.OutboxIn(dir)));
        Assert.Equal(server.OutboxFor(agentId), AgentIpcPaths.OutboxIn(dir));
    }

    // ---- the blocking contract, which is the whole plan gate -----------------------------------

    [Fact]
    public async Task AParkedRequest_IsDispatchedExactlyOnce_AndAnswersOnlyWhenItsHandlerReturns()
    {
        using var server = NewServer();
        var agentId = NewAgentId();
        var dispatches = 0;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dir = server.CreateEndpoint(agentId, async (_, _, _) =>
        {
            Interlocked.Increment(ref dispatches);
            await release.Task.ConfigureAwait(false);
            return new AgentIpcResponse(Ok: true, Status: "Approved");
        });
        var outbox = AgentIpcPaths.OutboxIn(dir);
        var ticket = Drop(outbox, new AgentIpcRequest(AgentIpcRequest.PresentPlanOp));

        // A worker's plan presentation parks for as long as the human takes. Many poll passes elapse in
        // that window (the sweep is 100 ms), and a claim that were merely a READ rather than a rename
        // would re-dispatch the same request on every one of them — spawning a worker per pass, or
        // queueing a card per pass, in the production handlers.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Volatile.Read(ref dispatches));
        Assert.False(File.Exists(Path.Combine(outbox, ticket + AgentIpcPaths.OutboxResponseSuffix)),
            "the daemon answered before its handler returned — the gate is not a gate");

        release.SetResult(true);
        var response = await ReadResponseAsync(outbox, ticket);
        Assert.True(response.Ok, response.Error);
        Assert.Equal("Approved", response.Status);
        Assert.Equal(1, Volatile.Read(ref dispatches));
    }

    // ---- honest failures -----------------------------------------------------------------------

    [Fact]
    public async Task AMalformedRequest_IsAnsweredHonestly_RatherThanSilentlyDropped()
    {
        using var server = NewServer();
        var agentId = NewAgentId();
        var reachedHandler = false;
        var dir = server.CreateEndpoint(agentId, (_, _, _) =>
        {
            reachedHandler = true;
            return Task.FromResult(new AgentIpcResponse(Ok: true));
        });
        var outbox = AgentIpcPaths.OutboxIn(dir);

        var ticket = DropRaw(outbox, "this is not json\n");
        var response = await ReadResponseAsync(outbox, ticket);

        // Same contract as the socket's: an error response, never a dropped connection — a shim that got
        // nothing back would block on its poll until its deadline and report a dead daemon.
        Assert.False(response.Ok);
        Assert.Contains("malformed request", response.Error ?? "", StringComparison.Ordinal);
        Assert.False(reachedHandler);
    }

    [Fact]
    public async Task AnOversizeRequest_IsDeletedUnread()
    {
        using var server = NewServer();
        var agentId = NewAgentId();
        var reachedHandler = false;
        var dir = server.CreateEndpoint(agentId, (_, _, _) =>
        {
            reachedHandler = true;
            return Task.FromResult(new AgentIpcResponse(Ok: true));
        });
        var outbox = AgentIpcPaths.OutboxIn(dir);

        // The outbox is the one thing a jail can WRITE into the daemon's data root, so the size of what
        // the daemon will read off it is the bound on that capability. A request line is a few hundred
        // bytes; this is a megabyte.
        var ticket = DropRaw(outbox, new string('x', AgentIpcPaths.MaxOutboxRequestBytes + 1) + "\n");
        var request = Path.Combine(outbox, ticket + AgentIpcPaths.OutboxRequestSuffix);

        await WaitUntilAsync(() => !File.Exists(request), "the oversize request was never removed");
        Assert.False(File.Exists(Path.Combine(outbox, ticket + AgentIpcPaths.OutboxResponseSuffix)));
        Assert.False(reachedHandler);
    }

    // ---- the shim's side of the protocol, exactly ----------------------------------------------

    private static string NewAgentId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Writes a request the way the shim does: staged under a name the daemon's poll does not match,
    /// then RENAMED into place. Returns the ticket.
    /// </summary>
    private static string Drop(string outbox, AgentIpcRequest request)
        => DropRaw(outbox, AgentIpcProtocol.SerializeRequest(request) + "\n");

    private static string DropRaw(string outbox, string line)
    {
        var ticket = Guid.NewGuid().ToString("N");
        var staged = Path.Combine(outbox, ticket + AgentIpcPaths.OutboxStagingSuffix);
        File.WriteAllText(staged, line);
        File.Move(staged, Path.Combine(outbox, ticket + AgentIpcPaths.OutboxRequestSuffix));
        return ticket;
    }

    private static async Task<AgentIpcResponse> CallOverOutboxAsync(
        AgentIpcServer server, string agentId, AgentIpcRequest request)
    {
        var outbox = server.OutboxFor(agentId);
        var ticket = Drop(outbox, request);
        var response = await ReadResponseAsync(outbox, ticket);
        File.Delete(Path.Combine(outbox, ticket + AgentIpcPaths.OutboxResponseSuffix)); // the shim's cleanup
        return response;
    }

    private static async Task<AgentIpcResponse> ReadResponseAsync(string outbox, string ticket)
    {
        var path = Path.Combine(outbox, ticket + AgentIpcPaths.OutboxResponseSuffix);
        await WaitUntilAsync(() => File.Exists(path), $"no response file appeared for ticket {ticket}");
        var line = File.ReadAllText(path);
        Assert.EndsWith("\n", line, StringComparison.Ordinal);
        return JsonSerializer.Deserialize<AgentIpcResponse>(line)!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because, int attempts = 300)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail(because);
    }
}
