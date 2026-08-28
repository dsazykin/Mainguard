using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Git.Audit;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Runtime;

/// <summary>Handles one shim request arriving on an agent's IPC socket.</summary>
public delegate Task<AgentIpcResponse> AgentIpcHandler(
    AgentIpcRequest request, string agentId, CancellationToken ct);

/// <summary>
/// The daemon side of the agent→daemon control channel: one Unix-domain socket per agent, served from a
/// daemon-owned ext4 dir that is bind-mounted READ-ONLY into that agent's jail
/// (<see cref="AgentIpcPaths.SandboxMount"/>). The dir also carries the one executable shim that agent's
/// role is allowed, which the launch wrapper puts on PATH:
///
/// <list type="bullet">
/// <item><b>coordinator</b> → <see cref="AgentSpawnShim"/> (<c>mainguard-agent</c>): start workers.</item>
/// <item><b>worker</b> → <see cref="WorkerPlanShim"/> (<c>mainguard-plan</c>): present its own plan and
/// block on the human's decision (phase 2). A worker had no IPC endpoint at all before phase 2 — it has
/// one now because the plan gate is a real block on a real channel, not a prompt.</item>
/// </list>
///
/// <para>The endpoint must exist BEFORE the container is created (it is a mount source), so
/// <see cref="CreateEndpoint"/> runs first in the spawn chain and <see cref="CloseEndpoint"/> is part of
/// teardown.</para>
///
/// <para>Identity is positional: requests arriving on <c>&lt;agentId&gt;/daemon.sock</c> ARE that agent's
/// — only its jail has the mount. Role is likewise a property of the endpoint, not of the request, so a
/// worker cannot reach a coordinator op by naming it (and vice versa). The protocol is one
/// newline-delimited JSON request per connection (<see cref="AgentIpcProtocol"/>); malformed input gets an
/// honest error response, never a dropped connection.</para>
///
/// <para><b>Two framings, one channel.</b> Alongside the socket every endpoint also serves an
/// <c>outbox/</c> directory: the same JSON, the same handler, the same role — framed as request/response
/// FILES the daemon polls. It exists because Docker's macOS file sharing does not proxy AF_UNIX across
/// the host/VM boundary, so on that substrate the bind-mounted socket is an inert inode and every
/// coordinator tool was unreachable (see <see cref="Mainguard.Agents.Agents.Ipc.AgentIpcShimTransport"/>).
/// The directory is created on every platform so this code path is exercised everywhere rather than only
/// where it is load-bearing; whether a jail can WRITE to it is decided by the container spec, which
/// mounts it read-write only on substrates that need it.</para>
///
/// <para><b>What this channel says out loud, and why it had to start saying anything.</b> This class used
/// to have no logger at all, so nothing it did — served, refused, malformed, or never called — produced a
/// single daemon-side line. That was found the hard way: three consecutive refused connections from inside
/// a jail left ZERO entries anywhere, the owner had no way to tell a dead control path from a model
/// sitting idle, and the outage read as the agent thinking. Three observations now exist, and the split
/// between them is the honest part:</para>
/// <list type="bullet">
/// <item><b>A refused connection cannot be logged here, ever.</b> <c>connect()</c> to a bind-mounted
/// socket the substrate does not proxy is refused by the JAIL's kernel; nothing crosses to the daemon and
/// there is nothing to observe. Its only daemon-side shadow is silence — which is why the third bullet
/// exists, and why "just log the failure" was not an available answer.</item>
/// <item><b>A malformed or failing request that DOES arrive</b> (either framing) is logged as a warning
/// and audited, <b>capped per endpoint</b> at <see cref="ChannelObserver.MaxRejectionReports"/> lines and
/// ONE audit event. The cap is not tidiness: the outbox is jail-writable, so an uncapped warning per
/// rejected file would hand a jail's occupant a log-flood and an audit-flood primitive.</item>
/// <item><b>An endpoint nobody ever calls</b> reports itself ONCE, after
/// <see cref="DefaultFirstContactGrace"/> — the shape the original outage actually had. A healthy agent
/// calls within seconds and this never fires; when it does fire and contact arrives later, the arrival is
/// logged too, so the warning is never left standing over a channel that recovered.</item>
/// </list>
/// <para>Everything a healthy channel does is <c>Debug</c>. Nothing at or above <c>Information</c> is
/// emitted per request, and no jail-supplied text is logged unsanitised or unbounded.</para>
///
/// <para><b>Long-blocking calls are expected here.</b> A worker's plan presentation parks on the socket
/// until a human decides, which may be hours. Each connection is served on its own task, so a parked
/// worker never blocks the accept loop or another agent's request — and a claimed outbox request is
/// renamed out of the way before its handler runs, so a parked file-framed call is never dispatched
/// twice by the next poll pass.</para>
/// </summary>
public sealed class AgentIpcServer : IDisposable
{
    /// <summary>
    /// How long after an endpoint is created the daemon waits for that jail's FIRST call before recording
    /// that the control path looks dead. Both roles are instructed to call the shim as their opening move
    /// — a coordinator has nothing else it can do, and a worker cannot even learn its brief without one —
    /// so silence past this is a real signal rather than a slow agent.
    /// </summary>
    public static readonly TimeSpan DefaultFirstContactGrace = TimeSpan.FromSeconds(90);

    private readonly string _root;
    private readonly ILogger? _log;
    private readonly IAuditLog? _audit;
    private readonly TimeSpan _firstContactGrace;
    private readonly ConcurrentDictionary<string, Endpoint> _endpoints = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <param name="root">The VM-side base dir for per-agent IPC dirs (ext4, daemon-owned).</param>
    /// <param name="log">Where this channel's failures go. Optional so a test can construct a bare
    /// server, but in the daemon it is always present — see the class remarks on why the channel used to
    /// have none.</param>
    /// <param name="audit">The durable record, alongside <c>ipc_endpoint_failed</c>, which
    /// <c>AgentSpawnService</c> already writes for the other half of this subsystem's failures.</param>
    /// <param name="firstContactGrace">Overrides <see cref="DefaultFirstContactGrace"/> (tests).</param>
    public AgentIpcServer(
        string root, ILogger? log = null, IAuditLog? audit = null, TimeSpan? firstContactGrace = null)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("An IPC root directory is required.", nameof(root));
        }

        _root = root;
        _log = log;
        _audit = audit;
        _firstContactGrace = firstContactGrace ?? DefaultFirstContactGrace;
    }

    /// <summary>The per-agent IPC dir (the container mount source). The dir name is the agent id's
    /// 12-char prefix — Unix socket paths have a hard ~104-byte limit, and live-session prefix
    /// collisions are not a real risk.</summary>
    public string DirFor(string agentId) =>
        Path.Combine(_root, agentId.Length > 12 ? agentId[..12] : agentId);

    /// <summary>
    /// Materializes the agent's IPC dir (role's shim written 0755, socket bound + listening) and returns
    /// the dir path to bind-mount. Idempotent per agent id.
    /// </summary>
    public string CreateEndpoint(
        string agentId, AgentIpcHandler handler, AgentIpcEndpointRole role = AgentIpcEndpointRole.Coordinator)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("An agent id is required.", nameof(agentId));
        }

        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var endpoint = _endpoints.GetOrAdd(agentId, id => Endpoint.Start(
            DirFor(id), id, handler, role, new ChannelObserver(_log, _audit, id, role), _firstContactGrace));
        return endpoint.Dir;
    }

    /// <summary>The agent's outbox dir — the file-framed channel's directory, and the ONE read-write
    /// mount source a coordinator jail is ever given.</summary>
    public string OutboxFor(string agentId) => AgentIpcPaths.OutboxIn(DirFor(agentId));

    /// <summary>The role an existing endpoint was created with (null when there is no such endpoint).</summary>
    public AgentIpcEndpointRole? RoleOf(string agentId) =>
        agentId is not null && _endpoints.TryGetValue(agentId, out var endpoint) ? endpoint.Role : null;

    /// <summary>Stops the agent's listener and removes its IPC dir. Idempotent.</summary>
    public void CloseEndpoint(string agentId)
    {
        if (agentId is not null && _endpoints.TryRemove(agentId, out var endpoint))
        {
            endpoint.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var agentId in _endpoints.Keys)
        {
            CloseEndpoint(agentId);
        }
    }

    /// <summary>The shim file name + script for one endpoint role (least privilege: exactly one shim).</summary>
    private static (string FileName, string Script) ShimFor(AgentIpcEndpointRole role) => role switch
    {
        AgentIpcEndpointRole.Worker => (AgentIpcPaths.PlanShimFileName, WorkerPlanShim.Script),
        _ => (AgentIpcPaths.SpawnShimFileName, AgentSpawnShim.Script),
    };

    /// <summary>
    /// One endpoint's evidence: the single place a served call, a rejected one, or a channel nobody ever
    /// used turns into a daemon-side line. Per-endpoint so every cap and every once-only flag is scoped
    /// to one agent — a noisy jail can neither drown out another agent's signal nor consume its budget.
    /// </summary>
    internal sealed class ChannelObserver
    {
        /// <summary>
        /// How many rejected requests one endpoint may report before this observer goes quiet about them.
        ///
        /// <para>A cap rather than a rate limit because the thing being bounded is a CAPABILITY, not a
        /// volume: the outbox framing is a jail-WRITABLE directory, so without a ceiling the occupant of
        /// a sandbox could drive unbounded daemon log lines and unbounded audit-chain entries by writing
        /// junk files in a loop. Five is enough to characterise a real fault (they carry the framing and
        /// the reason) and small enough that flooding buys nothing.</para>
        /// </summary>
        internal const int MaxRejectionReports = 5;

        /// <summary>The longest slice of jail-supplied text that ever reaches a log line. An <c>op</c>
        /// arrives from inside the sandbox and is bounded only by the outbox request cap (64 KiB), so it
        /// is truncated as well as stripped of control characters — a log field is not a place to
        /// faithfully reproduce whatever a jail sent.</summary>
        private const int MaxEchoedChars = 40;

        private readonly ILogger? _log;
        private readonly IAuditLog? _audit;
        private readonly string _agentId;
        private readonly AgentIpcEndpointRole _role;
        private int _served;
        private int _rejections;
        private int _silenceReported;

        public ChannelObserver(ILogger? log, IAuditLog? audit, string agentId, AgentIpcEndpointRole role)
        {
            _log = log;
            _audit = audit;
            _agentId = agentId;
            _role = role;
        }

        /// <summary>Whether this endpoint has ever served a request on EITHER framing.</summary>
        public bool HasServed => Volatile.Read(ref _served) > 0;

        /// <summary>How many rejections have been reported (the cap's observable side, for tests).</summary>
        public int RejectionsSeen => Volatile.Read(ref _rejections);

        public void EndpointReady(string dir) =>
            _log?.LogInformation(
                "agent-ipc: {Role} endpoint ready for agent={Agent} at {Dir} (socket + outbox framings)",
                _role, _agentId, dir);

        /// <summary>A request the daemon actually served — Debug, because this is what health looks like
        /// and a per-request Information line would drown the log a real fault has to be found in.</summary>
        public void Served(string framing, string? op, bool ok)
        {
            var first = Interlocked.Increment(ref _served) == 1;
            _log?.LogDebug(
                "agent-ipc: agent={Agent} role={Role} framing={Framing} op={Op} ok={Ok}",
                _agentId, _role, framing, Echo(op), ok);

            // Only ever emitted after the silence warning fired: an owner told the control path looked
            // dead must also be told when it came back, or the last word on a recovered channel stays
            // "dead".
            if (first && Volatile.Read(ref _silenceReported) == 1)
            {
                _log?.LogInformation(
                    "agent-ipc: agent={Agent} role={Role} reached the daemon after it was reported silent "
                    + "— the control path is alive (first call: {Framing}/{Op})",
                    _agentId, _role, framing, Echo(op));
            }
        }

        /// <summary>A request that arrived and could not be served — malformed, oversize, or one whose
        /// handler threw. Capped; see <see cref="MaxRejectionReports"/>.</summary>
        public void Rejected(string framing, string reason, Exception? error = null)
        {
            var n = Interlocked.Increment(ref _rejections);
            if (n > MaxRejectionReports)
            {
                return;
            }

            _log?.LogWarning(
                error,
                "agent-ipc: agent={Agent} role={Role} framing={Framing} REJECTED a request: {Reason}",
                _agentId, _role, framing, reason);

            // One audit event per endpoint, not per rejection — the audit chain is the durable record and
            // must not be writable-by-volume from inside a sandbox. The log lines above carry the detail.
            if (n == 1)
            {
                _audit?.Append(new AuditEvent("ipc_request_rejected", new Dictionary<string, string>
                {
                    ["agent_id"] = _agentId,
                    ["role"] = _role.ToString(),
                    ["framing"] = framing,
                    ["reason"] = reason,
                }));
            }

            if (n == MaxRejectionReports)
            {
                _log?.LogWarning(
                    "agent-ipc: agent={Agent} has now had {Count} requests rejected — further rejections "
                    + "from this agent will not be logged (the outbox is jail-writable; this cap is what "
                    + "stops one sandbox flooding the daemon log)",
                    _agentId, MaxRejectionReports);
            }
        }

        /// <summary>
        /// The endpoint has been up for the whole grace period without a single call on either framing.
        /// Fires at most once, and never for an endpoint that has been used.
        ///
        /// <para>This is the ONLY daemon-side shadow a refused connection casts. A jail whose kernel
        /// refuses <c>connect()</c> — Docker's macOS file sharing not proxying AF_UNIX, a daemon that is
        /// down, a mount that never arrived — sends nothing at all, so the absence of contact is the
        /// evidence, and this turns that absence into a line naming the agent, its role, and both
        /// framings it could have used.</para>
        /// </summary>
        public void Silent(string dir, TimeSpan grace)
        {
            if (HasServed || Interlocked.Exchange(ref _silenceReported, 1) == 1)
            {
                return;
            }

            _log?.LogWarning(
                "agent-ipc: agent={Agent} role={Role} has not called the daemon once in {Grace}s. Its CLI "
                + "is instructed to run {Shim} as its first action, so the control path is probably not "
                + "reachable from inside the jail (a refused connect never reaches the daemon, which is "
                + "why this is reported as silence). Endpoint: {Dir} — socket {Socket}, outbox {Outbox}.",
                _agentId, _role, (int)grace.TotalSeconds, AgentIpcPaths.SandboxShimPath(_role), dir,
                AgentIpcPaths.SandboxSocketPath, AgentIpcPaths.SandboxOutboxPath);

            _audit?.Append(new AuditEvent("ipc_channel_silent", new Dictionary<string, string>
            {
                ["agent_id"] = _agentId,
                ["role"] = _role.ToString(),
                ["grace_seconds"] = ((int)grace.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
        }

        public void EndpointClosed() =>
            _log?.LogInformation(
                "agent-ipc: endpoint closed for agent={Agent} role={Role} after {Served} served, "
                + "{Rejected} rejected",
                _agentId, _role, Volatile.Read(ref _served), Volatile.Read(ref _rejections));

        /// <summary>Renders jail-supplied text safe to log: control characters dropped, length bounded.
        /// Nothing that came out of a sandbox reaches a log line without passing through here.</summary>
        private static string Echo(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "(none)";
            }

            var builder = new StringBuilder(Math.Min(value.Length, MaxEchoedChars));
            foreach (var c in value)
            {
                if (builder.Length == MaxEchoedChars)
                {
                    builder.Append('…');
                    break;
                }

                builder.Append(char.IsControl(c) ? '.' : c);
            }

            return builder.ToString();
        }
    }

    /// <summary>The two framings, as they are named in every log line and audit entry — one spelling
    /// each, so a search for either finds all of it.</summary>
    private const string SocketFraming = "socket";

    private const string OutboxFraming = "outbox";

    private sealed class Endpoint : IDisposable
    {
        /// <summary>How often the outbox is swept for new requests.</summary>
        private static readonly TimeSpan OutboxPollInterval = TimeSpan.FromMilliseconds(100);

        private readonly Socket _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly ChannelObserver _observer;

        public string Dir { get; }

        public AgentIpcEndpointRole Role { get; }

        private Endpoint(string dir, Socket listener, AgentIpcEndpointRole role, ChannelObserver observer)
        {
            Dir = dir;
            _listener = listener;
            Role = role;
            _observer = observer;
        }

        public static Endpoint Start(
            string dir, string agentId, AgentIpcHandler handler, AgentIpcEndpointRole role,
            ChannelObserver observer, TimeSpan firstContactGrace)
        {
            Directory.CreateDirectory(dir);

            var (shimName, shimScript) = ShimFor(role);
            var shimPath = Path.Combine(dir, shimName);
            File.WriteAllText(shimPath, shimScript.Replace("\r\n", "\n"));

            // A shim is useless to a CLI that was never told it exists. Phase 3 §1.2 recorded this as
            // "the prompt was never delivered", which understated it — nothing ran at spawn at all, for
            // either role. Written beside the shim so the two cannot be staged independently.
            File.WriteAllText(
                Path.Combine(dir, AgentIpcPaths.InstructionsFileName),
                AgentOperatingInstructions.For(role, AgentIpcPaths.SandboxShimPath(role)).Replace("\r\n", "\n"));

            // The outbox: the file-framed form of this same channel. Created before the listener so
            // the directory exists by the time the container is created (it is a mount source too).
            var outbox = Path.Combine(dir, AgentIpcPaths.OutboxDirName);
            Directory.CreateDirectory(outbox);

            var socketPath = Path.Combine(dir, AgentIpcPaths.SocketFileName);
            File.Delete(socketPath); // a stale socket from a crashed daemon blocks bind

            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(backlog: 8);

            if (!OperatingSystem.IsWindows())
            {
                // The jail's agent uid must traverse the dir, exec the shim, and connect to the
                // socket (connect needs write on the socket inode). The mount itself is read-only.
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.SetUnixFileMode(shimPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
                // World-writable, and it has to be: the jail's agent uid is not the daemon's, and on the
                // macOS substrate the two are not even in the same kernel. Nothing is shared — one jail
                // mounts this directory and no other — so the reach of the mode is one agent's own
                // mailbox. Same reasoning as the socket's mode directly above.
                File.SetUnixFileMode(outbox, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            }

            var endpoint = new Endpoint(dir, listener, role, observer);
            observer.EndpointReady(dir);
            _ = endpoint.AcceptLoopAsync(agentId, handler);
            _ = endpoint.OutboxLoopAsync(outbox, agentId, handler);
            _ = endpoint.FirstContactWatchAsync(firstContactGrace);
            return endpoint;
        }

        /// <summary>
        /// Waits out the grace period and, if this endpoint has still served nothing, reports it once.
        ///
        /// <para>Cancelled by teardown, deliberately: an endpoint stopped inside the grace window says
        /// nothing. The owner who stopped that agent already knows, and a warning about a channel whose
        /// jail no longer exists is the noise this whole design is trying not to add.</para>
        /// </summary>
        private async Task FirstContactWatchAsync(TimeSpan grace)
        {
            try
            {
                await Task.Delay(grace, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _observer.Silent(Dir, grace);
        }

        private async Task AcceptLoopAsync(string agentId, AgentIpcHandler handler)
        {
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested)
            {
                Socket connection;
                try
                {
                    connection = await _listener.AcceptAsync(ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return; // listener closed (teardown) — the loop's normal end
                }

                _ = ServeConnectionAsync(connection, agentId, handler, _observer, ct);
            }
        }

        /// <summary>
        /// The outbox poll loop: claim every request file that has appeared, serve each on its own task.
        /// Polling rather than a filesystem watcher on purpose — on the substrate that needs the outbox
        /// the directory is a virtiofs/gRPC-FUSE share, and change notifications do not cross that
        /// boundary reliably. A 100 ms sweep of a directory that is empty almost always is not a cost
        /// worth trading correctness for.
        /// </summary>
        private async Task OutboxLoopAsync(string outbox, string agentId, AgentIpcHandler handler)
        {
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    foreach (var path in Directory.EnumerateFiles(outbox, "*" + AgentIpcPaths.OutboxRequestSuffix))
                    {
                        var ticket = Path.GetFileNameWithoutExtension(path);
                        if (string.IsNullOrEmpty(ticket))
                        {
                            continue;
                        }

                        // The bound on the one capability a writable mount grants: a jail can put bytes in
                        // the daemon's data root. A request line is a few hundred of them; anything past
                        // the cap is deleted unread rather than read into the daemon's memory.
                        if (new FileInfo(path).Length > AgentIpcPaths.MaxOutboxRequestBytes)
                        {
                            TryDelete(path);
                            _observer.Rejected(
                                OutboxFraming,
                                $"outbox request over the {AgentIpcPaths.MaxOutboxRequestBytes}-byte cap, deleted unread");
                            continue;
                        }

                        // Claim by rename: the request stops matching the poll's filter the instant it is
                        // claimed, so a handler parked on a human for hours is never re-dispatched.
                        var claim = Path.Combine(outbox, ticket + AgentIpcPaths.OutboxClaimSuffix);
                        try
                        {
                            File.Move(path, claim, overwrite: false);
                        }
                        catch (IOException)
                        {
                            continue; // already claimed, or gone
                        }

                        _ = ServeOutboxRequestAsync(outbox, ticket, claim, agentId, handler, _observer, ct);
                    }
                }
                catch (Exception)
                {
                    // The directory is gone (teardown) or momentarily unreadable — the delay below and the
                    // cancellation check are this loop's exit; a poll pass is never worth throwing on.
                }

                try
                {
                    await Task.Delay(OutboxPollInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>Serves one claimed outbox request — the exact <see cref="ServeConnectionAsync"/>
        /// contract, with files where that one has a stream.</summary>
        private static async Task ServeOutboxRequestAsync(
            string outbox, string ticket, string claim, string agentId, AgentIpcHandler handler,
            ChannelObserver observer, CancellationToken ct)
        {
            AgentIpcResponse response;
            try
            {
                var line = (await File.ReadAllTextAsync(claim, ct).ConfigureAwait(false)).Trim();
                var request = AgentIpcProtocol.TryParseRequest(line);
                if (request is null)
                {
                    observer.Rejected(OutboxFraming, "malformed request (expected one JSON line)");
                    response = new AgentIpcResponse(Ok: false, Error: "malformed request (expected one JSON line)");
                }
                else
                {
                    response = await handler(request, agentId, ct).ConfigureAwait(false);
                    observer.Served(OutboxFraming, request.Op, response.Ok);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                observer.Rejected(OutboxFraming, "the handler threw: " + ex.GetType().Name, ex);
                response = new AgentIpcResponse(Ok: false, Error: ex.Message);
            }

            try
            {
                // Staged then renamed, under a suffix the shim never writes: the shim polls for the final
                // name, so it can only ever observe a response that is complete.
                var staged = Path.Combine(outbox, ticket + AgentIpcPaths.OutboxResponseStagingSuffix);
                var final = Path.Combine(outbox, ticket + AgentIpcPaths.OutboxResponseSuffix);
                await File.WriteAllTextAsync(
                    staged, AgentIpcProtocol.SerializeResponse(response) + "\n", ct).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite
                        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
                }

                File.Move(staged, final, overwrite: true);
            }
            catch (Exception)
            {
                // The client is gone, or the endpoint is being torn down — nothing to salvage, exactly as
                // when a socket client hangs up mid-response.
            }
            finally
            {
                TryDelete(claim);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // Best effort; the whole directory goes at teardown.
            }
        }

        private static async Task ServeConnectionAsync(
            Socket connection, string agentId, AgentIpcHandler handler, ChannelObserver observer,
            CancellationToken ct)
        {
            using (connection)
            await using (var stream = new NetworkStream(connection, ownsSocket: false))
            {
                AgentIpcResponse response;
                try
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    var request = line is null ? null : AgentIpcProtocol.TryParseRequest(line);
                    if (request is null)
                    {
                        observer.Rejected(SocketFraming, "malformed request (expected one JSON line)");
                        response = new AgentIpcResponse(Ok: false, Error: "malformed request (expected one JSON line)");
                    }
                    else
                    {
                        response = await handler(request, agentId, ct).ConfigureAwait(false);
                        observer.Served(SocketFraming, request.Op, response.Ok);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    observer.Rejected(SocketFraming, "the handler threw: " + ex.GetType().Name, ex);
                    response = new AgentIpcResponse(Ok: false, Error: ex.Message);
                }

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(AgentIpcProtocol.SerializeResponse(response) + "\n");
                    await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Client hung up mid-response — nothing to salvage.
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _observer.EndpointClosed();
            try
            {
                _listener.Dispose();
            }
            catch
            {
                // Already closed.
            }

            try
            {
                Directory.Delete(Dir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; the dir may be mount-busy until the jail is removed.
            }

            _cts.Dispose();
        }
    }
}
