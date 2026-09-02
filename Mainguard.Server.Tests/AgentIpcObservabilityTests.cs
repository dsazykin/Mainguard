using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Git.Audit;
using Mainguard.Server.Runtime;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// A jail that cannot reach the daemon must leave daemon-side evidence.
///
/// <para><b>The defect.</b> Three consecutive refused IPC connections from inside a live jail produced
/// ZERO entries in the daemon log. <see cref="AgentIpcServer"/> had no logger at all — not one call to
/// any log method in the whole class — so nothing it did was observable: not a malformed request, not a
/// handler that threw, not an oversize outbox file deleted unread, and above all not a channel nobody
/// ever called. From the app the outage was indistinguishable from a model sitting and thinking, which
/// is how it went unnoticed on a real run.</para>
///
/// <para><b>The part that shapes these tests.</b> A REFUSED connection can never be logged at the
/// daemon: <c>connect()</c> to a socket the substrate does not proxy is refused by the jail's own
/// kernel and nothing crosses the boundary. There is no arriving event to instrument. So the evidence
/// for that case has to be the ABSENCE of contact, which is what
/// <see cref="TheSilenceWatch_ReportsAChannelNobodyEverCalled"/> pins — and the negative control beside
/// it is what keeps the mechanism from becoming a periodic false alarm on healthy agents.</para>
///
/// <para>The rejection cap is tested as a SECURITY guard, not as tidiness: the outbox framing is a
/// jail-writable directory, so an uncapped warning-per-rejection would hand a sandbox's occupant a way
/// to flood the daemon log and the audit chain by writing junk files in a loop.</para>
/// </summary>
public sealed class AgentIpcObservabilityTests : IDisposable
{
    /// <summary>
    /// Deliberately short. <c>sockaddr_un.sun_path</c> holds 104 bytes on macOS and
    /// <see cref="AgentIpcServer"/> binds a socket per endpoint even where a test never dials one, so a
    /// chatty root fails this class on path length rather than on anything it means to measure — see
    /// <see cref="TestDataRootIsolation"/>, which budgets the assembly's root for exactly this.
    /// </summary>
    private readonly string _root = Path.Combine(Mainguard.Git.MainguardPaths.DataRoot(), "ipcobs");

    /// <summary>Instructions text for an endpoint under test. The daemon renders the real thing from
    /// its adapter catalog (<c>SandboxAgentLauncher.InstructionsFor</c>); these tests are about the
    /// channel, not the briefing, so they pass a stand-in — but they must pass ONE, because a caller
    /// that could omit it is defect G2.</summary>
    private const string Instructions = "# operating instructions (test)\n";

    private static readonly TimeSpan ShortGrace = TimeSpan.FromMilliseconds(250);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // Best effort — the endpoint's own teardown removes its dir.
        }
    }

    // ---- the silence watch: the only daemon-side shadow a refused connection casts ----------------

    /// <summary>
    /// An endpoint that is never called reports itself — once — naming the agent, its role, and both
    /// framings. This is the reported outage's exact shape: the jail dialled, was refused by its own
    /// kernel, and the daemon saw nothing arrive.
    /// </summary>
    [Fact]
    public async Task TheSilenceWatch_ReportsAChannelNobodyEverCalled()
    {
        var (logs, factory, audit, server) = NewServer();
        using (factory)
        using (server)
        {
            server.CreateEndpoint("sil1", NeverCalled, AgentIpcEndpointRole.Coordinator, Instructions);

            await WaitForAsync(() => audit.Read().Any(e => e.Type == "ipc_channel_silent"));

            var warning = Assert.Single(
                logs.Entries,
                e => e.Level == LogLevel.Warning && e.Message.Contains("has not called", StringComparison.Ordinal));
            Assert.Contains("sil1", warning.Message, StringComparison.Ordinal);
            Assert.Contains(AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Coordinator), warning.Message, StringComparison.Ordinal);
            Assert.Contains(AgentIpcPaths.SandboxSocketPath, warning.Message, StringComparison.Ordinal);
            Assert.Contains(AgentIpcPaths.SandboxOutboxPath, warning.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The negative control, and it is what stops this being a warning generator. An agent that USES its
    /// channel is never reported silent, however long it then runs — otherwise every healthy coordinator
    /// would emit the same warning as the dead one and the signal would be worth nothing.
    /// </summary>
    [Fact]
    public async Task AnAgentThatCallsTheDaemon_IsNeverReportedSilent()
    {
        var (logs, factory, audit, server) = NewServer();
        using (factory)
        using (server)
        {
            var dir = server.CreateEndpoint("bsy1", Ok, AgentIpcEndpointRole.Coordinator, Instructions);
            Assert.Contains("\"ok\":true", await SocketRoundTripAsync(dir, """{"op":"status"}"""), StringComparison.Ordinal);

            // Well past the grace window: the watch has fired by now if it is ever going to.
            await Task.Delay(ShortGrace + ShortGrace);

            Assert.DoesNotContain(audit.Read(), e => e.Type == "ipc_channel_silent");
            Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("has not called", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// An endpoint torn down inside the grace window says nothing. The owner who stopped that agent
    /// already knows it is gone, and a warning about a channel whose jail no longer exists is exactly
    /// the noise a healthy system must not produce.
    /// </summary>
    [Fact]
    public async Task AnEndpointClosedInsideTheGraceWindow_IsNotReportedSilent()
    {
        var (logs, factory, audit, server) = NewServer();
        using (factory)
        using (server)
        {
            server.CreateEndpoint("brf1", NeverCalled, AgentIpcEndpointRole.Coordinator, Instructions);
            server.CloseEndpoint("brf1");

            await Task.Delay(ShortGrace + ShortGrace);

            Assert.DoesNotContain(audit.Read(), e => e.Type == "ipc_channel_silent");
            Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("has not called", StringComparison.Ordinal));
        }
    }

    // ---- attempts that DO arrive and fail --------------------------------------------------------

    /// <summary>A malformed request is answered honestly on the wire AND recorded daemon-side. Before
    /// this, the caller got its error and the daemon's operator got nothing.</summary>
    [Fact]
    public async Task AMalformedRequest_IsLoggedAndAudited()
    {
        var (logs, factory, audit, server) = NewServer();
        using (factory)
        using (server)
        {
            var dir = server.CreateEndpoint("bad1", Ok, AgentIpcEndpointRole.Coordinator, Instructions);

            var response = await SocketRoundTripAsync(dir, "this is not json");

            Assert.Contains("\"ok\":false", response, StringComparison.Ordinal);
            var rejected = Assert.Single(audit.Read(), e => e.Type == "ipc_request_rejected");
            Assert.Equal("bad1", rejected.Fields["agent_id"]);
            Assert.Equal("socket", rejected.Fields["framing"]);
            Assert.Contains(logs.Entries, e =>
                e.Level == LogLevel.Warning && e.Message.Contains("REJECTED", StringComparison.Ordinal));
        }
    }

    /// <summary>The outbox framing is instrumented identically — it is the framing macOS actually uses,
    /// so an observability gap there would be a gap on the substrate that has the problem.</summary>
    [Fact]
    public async Task AMalformedOutboxRequest_IsLoggedAndAuditedToo()
    {
        var (logs, factory, audit, server) = NewServer();
        using (factory)
        using (server)
        {
            server.CreateEndpoint("bad2", Ok, AgentIpcEndpointRole.Coordinator, Instructions);
            WriteOutboxRequest(server.OutboxFor("bad2"), "ticket-1", "{ not json");

            await WaitForAsync(() => audit.Read().Any(e => e.Type == "ipc_request_rejected"));

            var rejected = Assert.Single(audit.Read(), e => e.Type == "ipc_request_rejected");
            Assert.Equal("outbox", rejected.Fields["framing"]);
            Assert.Contains(logs.Entries, e =>
                e.Level == LogLevel.Warning && e.Message.Contains("framing=outbox", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// THE GUARD. The outbox is jail-writable, so the reporting has to be capped or the sandbox's
    /// occupant owns the daemon's log volume and the audit chain's growth. Warnings stop at
    /// <c>MaxRejectionReports</c> and the audit chain takes exactly ONE event however many junk requests
    /// arrive.
    /// </summary>
    [Fact]
    public async Task AFloodOfBadRequests_IsCapped_SoAJailCannotDriveTheDaemonLog()
    {
        const int flood = 30;
        var (logs, factory, audit, server) = NewServer();
        using (factory)
        using (server)
        {
            var dir = server.CreateEndpoint("fld1", Ok, AgentIpcEndpointRole.Coordinator, Instructions);
            for (var i = 0; i < flood; i++)
            {
                await SocketRoundTripAsync(dir, "junk " + i);
            }

            var rejections = logs.Entries.Count(e =>
                e.Level == LogLevel.Warning && e.Message.Contains("REJECTED", StringComparison.Ordinal));
            Assert.Equal(AgentIpcServer.ChannelObserver.MaxRejectionReports, rejections);
            Assert.Single(audit.Read(), e => e.Type == "ipc_request_rejected");

            // And the cap announces itself once, so a reader is never left thinking the flood stopped.
            Assert.Single(
                logs.Entries, e => e.Message.Contains("will not be logged", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Text that came out of a sandbox is never reproduced into a log line whole. An <c>op</c> is
    /// jail-supplied and bounded only by the 64 KiB outbox cap, so it is truncated and stripped of
    /// control characters — a log field is not a rendering surface for whatever a jail chose to send.
    /// </summary>
    [Fact]
    public async Task JailSuppliedText_IsTruncatedAndStrippedBeforeItReachesALogLine()
    {
        var (logs, factory, _, server) = NewServer();
        using (factory)
        using (server)
        {
            var dir = server.CreateEndpoint("ech1", Ok, AgentIpcEndpointRole.Coordinator, Instructions);
            var hostile = new string('A', 400) + "\nFAKE LOG LINE";

            await SocketRoundTripAsync(dir, "{\"op\":\"" + hostile.Replace("\n", "\\n") + "\"}");

            var line = Assert.Single(
                logs.Entries,
                e => e.Message.Contains("op=", StringComparison.Ordinal)
                     && e.Message.Contains("AAA", StringComparison.Ordinal));
            Assert.DoesNotContain("\n", line.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("FAKE LOG LINE", line.Message, StringComparison.Ordinal);
            Assert.True(line.Message.Length < 400, "the whole jail-supplied op reached the log line");
        }
    }

    /// <summary>
    /// Health is <see cref="LogLevel.Debug"/> and stays there. Without this a served request could be
    /// promoted to Information "for visibility" and the channel would spam every operator watching at
    /// the level the failures above are reported at — which is the same mistake in the other direction.
    /// </summary>
    [Fact]
    public async Task AHealthyCall_ProducesNothingAboveDebug()
    {
        var (logs, factory, audit, server) = NewServer();
        using (factory)
        using (server)
        {
            var dir = server.CreateEndpoint("qui1", Ok, AgentIpcEndpointRole.Coordinator, Instructions);
            for (var i = 0; i < 5; i++)
            {
                await SocketRoundTripAsync(dir, """{"op":"status"}""");
            }

            Assert.DoesNotContain(
                logs.Entries,
                e => e.Level >= LogLevel.Warning
                     || (e.Level == LogLevel.Information && e.Message.Contains("op=", StringComparison.Ordinal)));
            Assert.Empty(audit.Read());
        }
    }

    // ---- the socket framing's bounds: the outbox's guards, on the framing that had none -------------

    /// <summary>
    /// The G1 defect on the other framing. The socket read was <c>StreamReader.ReadLineAsync</c>, which
    /// accumulates until a newline with no ceiling — so a jail that wrote bytes with no newline in them
    /// grew the daemon, and with it every agent's control plane, until it died. The read now stops at the
    /// same cap the outbox enforces and refuses; the handler is never reached; the endpoint still serves.
    /// </summary>
    [Fact]
    public async Task ASocketLineOverTheCap_IsRefusedUnread_AndTheChannelSurvives()
    {
        var (_, factory, audit, server) = NewServer();
        using (factory)
        using (server)
        {
            var reached = false;
            var dir = server.CreateEndpoint("big1", (r, a, c) =>
            {
                reached = true;
                return Ok(r, a, c);
            }, AgentIpcEndpointRole.Coordinator, Instructions);

            var response = await SocketRoundTripAsync(
                dir, new string('a', AgentIpcPaths.MaxOutboxRequestBytes + 4096));

            Assert.Contains("\"ok\":false", response, StringComparison.Ordinal);
            Assert.Contains("over the", response, StringComparison.Ordinal);
            Assert.False(reached, "the handler ran on a request the daemon should have refused unread");
            var rejected = Assert.Single(audit.Read(), e => e.Type == "ipc_request_rejected");
            Assert.Contains("cap", rejected.Fields["reason"], StringComparison.Ordinal);

            Assert.Contains("\"ok\":true", await SocketRoundTripAsync(dir, """{"op":"status"}"""), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Connections are bounded per endpoint, like the outbox's file count: every accepted connection is a
    /// parked handler task, and a jail that opens sockets without ever finishing a call would otherwise
    /// grow the daemon without limit. Past the cap a connection is answered with a refusal and closed;
    /// once handlers complete the endpoint accepts again.
    /// </summary>
    [Fact]
    public async Task ConnectionsPastTheInFlightCap_AreRefused_AndAcceptedAgainOnceHandlersComplete()
    {
        var (_, factory, audit, server) = NewServer();
        using (factory)
        using (server)
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dir = server.CreateEndpoint("cap1", async (_, _, _) =>
            {
                await release.Task;
                return new AgentIpcResponse(Ok: true, Status: "released");
            }, AgentIpcEndpointRole.Coordinator, Instructions);

            var parked = new List<(Socket Client, NetworkStream Stream)>();
            try
            {
                for (var i = 0; i < AgentIpcPaths.MaxInFlightConnections; i++)
                {
                    var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    // The listener's backlog is 8, so a burst of connects can be refused by the kernel
                    // before the accept loop drains it — that is the kernel's bound, not the one under
                    // test, so a refused connect is simply retried.
                    for (var attempt = 0; ; attempt++)
                    {
                        try
                        {
                            await client.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(dir, AgentIpcPaths.SocketFileName)));
                            break;
                        }
                        catch (SocketException) when (attempt < 50)
                        {
                            await Task.Delay(20);
                        }
                    }

                    var stream = new NetworkStream(client, ownsSocket: false);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"op\":\"status\"}\n"));
                    parked.Add((client, stream));
                }

                // The (cap + 1)th caller is refused, promptly, with a reason — not parked, not ignored.
                var refused = await SocketRoundTripAsync(dir, """{"op":"status"}""").WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Contains("\"ok\":false", refused, StringComparison.Ordinal);
                Assert.Contains("in flight", refused, StringComparison.Ordinal);
                var rejected = Assert.Single(audit.Read(), e => e.Type == "ipc_request_rejected");
                Assert.Contains("in flight", rejected.Fields["reason"], StringComparison.Ordinal);

                release.SetResult(true);
                foreach (var (_, stream) in parked)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    Assert.Contains("released", (await reader.ReadLineAsync())!, StringComparison.Ordinal);
                }
            }
            finally
            {
                foreach (var (client, stream) in parked)
                {
                    await stream.DisposeAsync();
                    client.Dispose();
                }
            }

            // Recovery is automatic: with the parked handlers gone, the next call is served.
            await WaitForAsync(() => SocketRoundTripAsync(dir, """{"op":"status"}""").GetAwaiter().GetResult().Contains("\"ok\":true", StringComparison.Ordinal));
        }
    }

    // ---- rig ---------------------------------------------------------------------------------------

    private (CapturingProvider Logs, ILoggerFactory Factory, InMemoryAuditLog Audit, AgentIpcServer Server) NewServer()
    {
        var logs = new CapturingProvider();
        var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var audit = new InMemoryAuditLog();
        var server = new AgentIpcServer(
            Path.Combine(_root, Guid.NewGuid().ToString("N")[..4]),
            factory.CreateLogger("test"),
            audit,
            ShortGrace);
        return (logs, factory, audit, server);
    }

    // ---- the directory outlives the process; the listener is re-bound at the same path -------------

    /// <summary>
    /// The jail bind-mounts the endpoint directory by inode and survives a daemon restart. Deleting that
    /// directory on shutdown — which the server used to do — orphaned the mount: the daemon came back,
    /// adopted the jail, created a NEW directory the container could not see, and every shim call from
    /// the adopted agent failed with "cannot reach the Mainguard daemon". So shutdown keeps the directory
    /// (shim, instructions and outbox included), a new server re-binds at exactly that path and serves,
    /// and only the STOP path removes it.
    /// </summary>
    [Fact]
    public async Task ADaemonShutdown_KeepsTheEndpointDirectory_AndARestartRebindsAtTheSamePath()
    {
        var first = new AgentIpcServer(_root);
        var dir = first.CreateEndpoint("agent-r", Ok, AgentIpcEndpointRole.Coordinator, Instructions);
        var instructions = Path.Combine(dir, AgentIpcPaths.InstructionsFileName);
        Assert.True(File.Exists(instructions));

        first.Dispose(); // the process going away — the jail is still running with `dir` mounted

        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(instructions));
        Assert.True(Directory.Exists(Path.Combine(dir, AgentIpcPaths.OutboxDirName)));

        var second = new AgentIpcServer(_root);
        var rebound = second.CreateEndpoint("agent-r", Ok, AgentIpcEndpointRole.Coordinator, Instructions);
        Assert.Equal(dir, rebound);
        var reply = await SocketRoundTripAsync(rebound, "{\"op\":\"status\"}");
        Assert.Contains("served", reply, StringComparison.Ordinal);

        second.CloseEndpoint("agent-r"); // the agent was stopped — now the mailbox goes
        Assert.False(Directory.Exists(dir));
        second.Dispose();
    }

    private static Task<AgentIpcResponse> Ok(AgentIpcRequest request, string agentId, CancellationToken ct) =>
        Task.FromResult(new AgentIpcResponse(Ok: true, Status: "served"));

    private static Task<AgentIpcResponse> NeverCalled(AgentIpcRequest request, string agentId, CancellationToken ct) =>
        throw new InvalidOperationException("this endpoint's handler must never run");

    /// <summary>One line in, one line out over the endpoint's real socket — the bytes a shim writes.</summary>
    private static async Task<string> SocketRoundTripAsync(string dir, string requestLine)
    {
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(dir, AgentIpcPaths.SocketFileName)));
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var bytes = Encoding.UTF8.GetBytes(requestLine + "\n");
        await stream.WriteAsync(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadLineAsync() ?? string.Empty;
    }

    /// <summary>Stage-then-rename, exactly as the shim's outbox transport does.</summary>
    private static void WriteOutboxRequest(string outbox, string ticket, string body)
    {
        var staged = Path.Combine(outbox, ticket + AgentIpcPaths.OutboxStagingSuffix);
        File.WriteAllText(staged, body + "\n");
        File.Move(staged, Path.Combine(outbox, ticket + AgentIpcPaths.OutboxRequestSuffix));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("the expected daemon-side evidence never appeared");
    }

    private sealed record Entry(LogLevel Level, string Message);

    private sealed class CapturingProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<Entry> _entries = new();

        public IReadOnlyList<Entry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new Sink(_entries);

        public void Dispose()
        {
        }

        private sealed class Sink : ILogger
        {
            private readonly ConcurrentQueue<Entry> _entries;

            public Sink(ConcurrentQueue<Entry> entries) => _entries = entries;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                _entries.Enqueue(new Entry(logLevel, formatter(state, exception)));
        }
    }
}
