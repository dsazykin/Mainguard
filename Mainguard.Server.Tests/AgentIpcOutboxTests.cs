using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Git.Audit;
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
    /// <summary>Instructions text for an endpoint under test. The daemon renders the real thing from
    /// its adapter catalog (<c>SandboxAgentLauncher.InstructionsFor</c>); these tests are about the
    /// channel, not the briefing, so they pass a stand-in — but they must pass ONE, because a caller
    /// that could omit it is defect G2.</summary>
    private const string Instructions = "# operating instructions (test)\n";

    private static string NewRoot() => Path.Combine(Mainguard.Git.MainguardPaths.DataRoot(), "ob");

    private static AgentIpcServer NewServer() => new(NewRoot());

    /// <summary>
    /// A server whose refusals are readable. Every guard below is a security boundary, and a boundary
    /// that refuses silently is one nobody can tell from a boundary that is not there — so each of these
    /// tests asserts the REASON as well as the effect, through the same capped
    /// <c>ipc_request_rejected</c> path <c>ChannelObserver</c> already owns (one event per endpoint; the
    /// cap is what stops a jail-writable directory being an audit-flood primitive).
    /// </summary>
    private static AgentIpcServer NewAuditedServer(out InMemoryAuditLog audit)
    {
        audit = new InMemoryAuditLog();
        return new AgentIpcServer(NewRoot(), log: null, audit: audit);
    }

    private static async Task<string> RefusalReasonAsync(InMemoryAuditLog audit)
    {
        await WaitUntilAsync(
            () => audit.Read().Any(e => e.Type == "ipc_request_rejected"), "no rejection was ever audited");
        return audit.Read().First(e => e.Type == "ipc_request_rejected").Fields["reason"];
    }

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
        }, AgentIpcEndpointRole.Coordinator, Instructions);

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
        var dir = server.CreateEndpoint(agentId, (_, _, _) => Task.FromResult(new AgentIpcResponse(Ok: true)), AgentIpcEndpointRole.Coordinator, Instructions);

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
        }, AgentIpcEndpointRole.Coordinator, Instructions);
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
        }, AgentIpcEndpointRole.Coordinator, Instructions);
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
        }, AgentIpcEndpointRole.Coordinator, Instructions);
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

    // ---- the directory is jail-controlled, and it is not allowed to hurt the daemon --------------

    /// <summary>
    /// One <c>ln -s /dev/zero x.req</c> from inside a jail used to kill the daemon.
    ///
    /// <para><b>Reproduced before it was fixed</b>, running the daemon's own sequence by hand:
    /// <c>FileInfo("x.req").Length</c> is <b>9</b> — the length of the string "/dev/zero", not of what it
    /// points at — so the 64 KiB cap passed it; <c>File.Move</c> renamed the LINK, because rename does
    /// not follow; and <c>File.ReadAllText</c> on the claim followed it and read until the process died.
    /// Measured: 4.2 GB resident and still climbing when it was killed. That is not one agent's channel
    /// — the daemon serves every running agent's control plane out of the same process.</para>
    ///
    /// <para>The link here points at an ordinary large file rather than at <c>/dev/zero</c> so that this
    /// test can be watched failing without costing the machine several gigabytes: 16 MiB is already 256
    /// times the cap the daemon claims to enforce, which is all the assertion needs.</para>
    /// </summary>
    [Fact]
    public async Task ASymlinkedRequest_IsRefusedUnread_RatherThanFollowed()
    {
        using var server = NewAuditedServer(out var audit);
        var agentId = NewAgentId();
        var reachedHandler = false;
        var dir = server.CreateEndpoint(agentId, (_, _, _) =>
        {
            reachedHandler = true;
            return Task.FromResult(new AgentIpcResponse(Ok: true));
        }, AgentIpcEndpointRole.Coordinator, Instructions);
        var outbox = AgentIpcPaths.OutboxIn(dir);

        var target = Path.Combine(outbox, "..", "big.bin");
        using (var big = new FileStream(target, FileMode.Create))
        {
            big.SetLength(16L * 1024 * 1024); // sparse: costs no disk, stats as 16 MiB
        }

        var ticket = Guid.NewGuid().ToString("N");
        var request = Path.Combine(outbox, ticket + AgentIpcPaths.OutboxRequestSuffix);
        File.CreateSymbolicLink(request, target);

        // The cap the daemon states is a cap on what it READS. A link is refused before that question
        // is even asked, so the request goes unanswered rather than being followed off the end of it.
        await WaitUntilAsync(() => !File.Exists(request), "the symlinked request was never removed");
        await Task.Delay(300);
        Assert.False(reachedHandler, "the daemon followed a symlink into something that was not a request");
        Assert.False(File.Exists(Path.Combine(outbox, ticket + AgentIpcPaths.OutboxResponseSuffix)));
        Assert.True(File.Exists(target), "the refusal deleted the link, and must not touch its target");

        // Refused AS A SYMLINK, and audited as one. Without this the ceiling below would also have
        // stopped it — defence in depth is the point, but a test that cannot tell which guard fired
        // cannot show that this one exists.
        Assert.Contains("SYMLINK", await RefusalReasonAsync(audit), StringComparison.Ordinal);

        // And the endpoint is still an endpoint.
        var response = await CallOverOutboxAsync(server, agentId, new AgentIpcRequest(AgentIpcRequest.StatusOp));
        Assert.True(response.Ok, response.Error);
    }

    /// <summary>
    /// A jail needs no capability at all to <c>mkfifo</c> in a directory it can write, and a FIFO is
    /// indistinguishable from a regular file through every managed API — measured: <c>Attributes</c> is
    /// <c>Normal</c>, <c>LinkTarget</c> is null, <c>Length</c> is 0.
    ///
    /// <para>Opening one blocks until a writer appears, and the daemon's open happened on the POLL LOOP's
    /// own thread: <c>File.ReadAllTextAsync</c> opens synchronously before it goes anywhere near the
    /// await. So a single FIFO parked that agent's whole file-framed channel for good — which is what the
    /// second half of this test measures, by requiring a perfectly ordinary call to still be served
    /// afterwards.</para>
    /// </summary>
    [Fact]
    public async Task ANonRegularRequest_IsRefused_AndCannotParkThePollLoop()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // no FIFOs in the filesystem namespace, and no jail writes this dir on Windows
        }

        using var server = NewAuditedServer(out var audit);
        var agentId = NewAgentId();
        var reachedHandler = false;
        var dir = server.CreateEndpoint(agentId, (_, _, _) =>
        {
            reachedHandler = true;
            return Task.FromResult(new AgentIpcResponse(Ok: true, Status: "alive"));
        }, AgentIpcEndpointRole.Coordinator, Instructions);
        var outbox = AgentIpcPaths.OutboxIn(dir);

        var ticket = Guid.NewGuid().ToString("N");
        var request = Path.Combine(outbox, ticket + AgentIpcPaths.OutboxRequestSuffix);
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", request))
        {
            await mkfifo!.WaitForExitAsync();
            Assert.Equal(0, mkfifo.ExitCode);
        }

        Assert.Null(new FileInfo(request).LinkTarget); // nothing managed distinguishes this from a file

        await WaitUntilAsync(() => !File.Exists(request), "the FIFO request was never removed");
        Assert.False(reachedHandler);
        Assert.Contains("not a regular file", await RefusalReasonAsync(audit), StringComparison.Ordinal);

        // The assertion that matters: the loop is still sweeping. On the unhardened daemon this call
        // never returns, because the poll loop is parked in open() on the FIFO above.
        var response = await CallOverOutboxAsync(server, agentId, new AgentIpcRequest(AgentIpcRequest.StatusOp));
        Assert.True(response.Ok, response.Error);
        Assert.Equal("alive", response.Status);
    }

    /// <summary>
    /// <c>MaxOutboxRequestBytes</c> bounds ONE request. Nothing bounded the number of them, so a jail
    /// could fill the host's disk inside <c>~/.mainguard</c> 64 KiB at a time and make its own 100 ms
    /// sweep walk an ever-growing directory while it did.
    ///
    /// <para>The breach is answered by reclaiming the directory and CONTINUING to poll. Stopping the
    /// endpoint would let a jail switch off a control plane the human depends on too; leaving the files
    /// is the defect. The recovery half of this test is the half that matters — a burst must cost a
    /// retry, never the channel.</para>
    /// </summary>
    [Fact]
    public async Task AnOutboxPastItsAggregateBound_IsReclaimed_AndTheChannelRecovers()
    {
        using var server = NewAuditedServer(out var audit);
        var agentId = NewAgentId();
        var served = 0;
        var dir = server.CreateEndpoint(agentId, (_, _, _) =>
        {
            Interlocked.Increment(ref served);
            return Task.FromResult(new AgentIpcResponse(Ok: true, Status: "alive"));
        }, AgentIpcEndpointRole.Coordinator, Instructions);
        var outbox = AgentIpcPaths.OutboxIn(dir);

        var flood = (AgentIpcPaths.MaxOutboxFiles * 2) + 1;
        for (var i = 0; i < flood; i++)
        {
            File.WriteAllText(
                Path.Combine(outbox, Guid.NewGuid().ToString("N") + AgentIpcPaths.OutboxRequestSuffix),
                AgentIpcProtocol.SerializeRequest(new AgentIpcRequest(AgentIpcRequest.StatusOp)) + "\n");
        }

        await WaitUntilAsync(
            () => Directory.GetFiles(outbox).Length == 0, "the over-quota outbox was never reclaimed");

        // Not served, and not answered: past the bound the daemon stops reading the directory rather than
        // working through it.
        Assert.Equal(0, Volatile.Read(ref served));
        Assert.Contains("the outbox held more than", await RefusalReasonAsync(audit), StringComparison.Ordinal);

        var response = await CallOverOutboxAsync(server, agentId, new AgentIpcRequest(AgentIpcRequest.StatusOp));
        Assert.True(response.Ok, response.Error);
        Assert.Equal("alive", response.Status);
        Assert.Equal(1, Volatile.Read(ref served));
    }

    /// <summary>
    /// The claim leaves the jail's directory. That single structural fact is what every other check in
    /// the reader is allowed to rest on: after this rename the entry is inside the READ-ONLY IPC mount
    /// and outside the read-write one, so there is no second writer between the stat and the read.
    /// </summary>
    [Fact]
    public async Task AClaimedRequest_IsRenamedOutOfTheDirectoryTheJailCanWrite()
    {
        using var server = NewServer();
        var agentId = NewAgentId();
        var claimedIn = "";
        var inflight = AgentIpcPaths.InflightIn(server.DirFor(agentId));
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dir = server.CreateEndpoint(agentId, async (_, _, _) =>
        {
            claimedIn = string.Join(",", Directory.GetFiles(inflight).Select(Path.GetFileName));
            await release.Task.ConfigureAwait(false);
            return new AgentIpcResponse(Ok: true);
        }, AgentIpcEndpointRole.Coordinator, Instructions);
        var outbox = AgentIpcPaths.OutboxIn(dir);
        var ticket = Drop(outbox, new AgentIpcRequest(AgentIpcRequest.StatusOp));

        await WaitUntilAsync(() => claimedIn.Length > 0, "the request was never dispatched");
        Assert.Equal(ticket + AgentIpcPaths.OutboxClaimSuffix, claimedIn);
        Assert.Empty(Directory.GetFiles(outbox)); // nothing of the claim is left where the jail can reach

        release.SetResult(true);
        var response = await ReadResponseAsync(outbox, ticket);
        Assert.True(response.Ok, response.Error);
    }

    /// <summary>
    /// A claim whose handler never returned is swept when the endpoint comes UP, not on a timer.
    ///
    /// <para>Age cannot be the signal: a worker's plan presentation parks on its claim for as long as the
    /// human takes, and a TTL that swept "stale" claims would be a timer on the human. But at the moment
    /// an endpoint is created nothing can be in flight by construction, so anything left in either
    /// directory belongs to a daemon that died mid-call and goes.</para>
    /// </summary>
    [Fact]
    public void AnEndpointComingUp_ClearsWhatADaemonThatDiedMidCallLeftBehind()
    {
        using var server = NewServer();
        var agentId = NewAgentId();

        var dir = server.DirFor(agentId);
        Directory.CreateDirectory(AgentIpcPaths.OutboxIn(dir));
        Directory.CreateDirectory(AgentIpcPaths.InflightIn(dir));
        var staleClaim = Path.Combine(AgentIpcPaths.InflightIn(dir), "old" + AgentIpcPaths.OutboxClaimSuffix);
        var staleResponse = Path.Combine(AgentIpcPaths.OutboxIn(dir), "old" + AgentIpcPaths.OutboxResponseSuffix);
        File.WriteAllText(staleClaim, "{}\n");
        File.WriteAllText(staleResponse, "{}\n");

        server.CreateEndpoint(agentId, (_, _, _) => Task.FromResult(new AgentIpcResponse(Ok: true)), AgentIpcEndpointRole.Coordinator, Instructions);

        Assert.False(File.Exists(staleClaim), "a claim from a dead daemon outlived the endpoint that made it");
        Assert.False(File.Exists(staleResponse));
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
