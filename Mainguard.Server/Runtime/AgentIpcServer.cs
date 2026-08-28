using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Ipc;

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
/// <para><b>Long-blocking calls are expected here.</b> A worker's plan presentation parks on the socket
/// until a human decides, which may be hours. Each connection is served on its own task, so a parked
/// worker never blocks the accept loop or another agent's request — and a claimed outbox request is
/// renamed out of the way before its handler runs, so a parked file-framed call is never dispatched
/// twice by the next poll pass.</para>
/// </summary>
public sealed class AgentIpcServer : IDisposable
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, Endpoint> _endpoints = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <param name="root">The VM-side base dir for per-agent IPC dirs (ext4, daemon-owned).</param>
    public AgentIpcServer(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("An IPC root directory is required.", nameof(root));
        }

        _root = root;
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

        var endpoint = _endpoints.GetOrAdd(agentId, id => Endpoint.Start(DirFor(id), id, handler, role));
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

    private sealed class Endpoint : IDisposable
    {
        /// <summary>How often the outbox is swept for new requests.</summary>
        private static readonly TimeSpan OutboxPollInterval = TimeSpan.FromMilliseconds(100);

        private readonly Socket _listener;
        private readonly CancellationTokenSource _cts = new();

        public string Dir { get; }

        public AgentIpcEndpointRole Role { get; }

        private Endpoint(string dir, Socket listener, AgentIpcEndpointRole role)
        {
            Dir = dir;
            _listener = listener;
            Role = role;
        }

        public static Endpoint Start(string dir, string agentId, AgentIpcHandler handler, AgentIpcEndpointRole role)
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

            var endpoint = new Endpoint(dir, listener, role);
            _ = endpoint.AcceptLoopAsync(agentId, handler);
            _ = endpoint.OutboxLoopAsync(outbox, agentId, handler);
            return endpoint;
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

                _ = ServeConnectionAsync(connection, agentId, handler, ct);
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

                        _ = ServeOutboxRequestAsync(outbox, ticket, claim, agentId, handler, ct);
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
            string outbox, string ticket, string claim, string agentId, AgentIpcHandler handler, CancellationToken ct)
        {
            AgentIpcResponse response;
            try
            {
                var line = (await File.ReadAllTextAsync(claim, ct).ConfigureAwait(false)).Trim();
                var request = AgentIpcProtocol.TryParseRequest(line);
                response = request is null
                    ? new AgentIpcResponse(Ok: false, Error: "malformed request (expected one JSON line)")
                    : await handler(request, agentId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
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
            Socket connection, string agentId, AgentIpcHandler handler, CancellationToken ct)
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
                    response = request is null
                        ? new AgentIpcResponse(Ok: false, Error: "malformed request (expected one JSON line)")
                        : await handler(request, agentId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
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
