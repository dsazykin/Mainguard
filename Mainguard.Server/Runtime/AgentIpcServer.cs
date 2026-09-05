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
/// <para><b>Long-blocking calls are expected here.</b> A worker's plan presentation parks on the socket
/// until a human decides, which may be hours. Each connection is served on its own task, so a parked
/// worker never blocks the accept loop or another agent's request.</para>
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
            }

            var endpoint = new Endpoint(dir, listener, role);
            _ = endpoint.AcceptLoopAsync(agentId, handler);
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
