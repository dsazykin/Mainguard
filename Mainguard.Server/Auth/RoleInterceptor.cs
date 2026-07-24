using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Mainguard.Protos.V1;

namespace Mainguard.Server.Auth;

/// <summary>
/// P2-14 role + terminal-lock enforcement, <b>daemon-side, at the gRPC layer</b> (never UI-only —
/// convention is not enforcement, plan §7 rejection trigger).
///
/// <para><b>Role (test 6):</b> a connection whose bearer token is a <see cref="ConnectionRole.Coordinator"/>
/// credential is denied the merge RPCs (<c>BeginMerge</c>/<c>ConfirmMerge</c>) and the human-only
/// plan-approval RPCs (<c>ApprovePlan</c>/<c>RejectPlan</c>) with <see cref="StatusCode.PermissionDenied"/>.
/// The coordinator has no merge power and cannot approve its own plans.</para>
///
/// <para><b>Terminal input lock (test 5):</b> for <c>TerminalService.Attach</c> the request (INPUT) stream
/// is wrapped so a <c>data</c> frame toward a <see cref="TerminalLockRegistry"/>-locked agent is rejected
/// server-side — the input stream is severed here, at the interceptor, while the output (read) stream flows
/// untouched. A hand-crafted raw client cannot bypass it.</para>
/// </summary>
public sealed class RoleInterceptor : Interceptor
{
    private const string HeaderKey = "authorization";
    private const string Scheme = "bearer ";

    // The RPCs a coordinator credential may never call (interceptor-enforced role, not convention).
    private static readonly HashSet<string> CoordinatorDeniedMethods = new(StringComparer.Ordinal)
    {
        "/mainguard.v1.MergeQueueService/BeginMerge",
        "/mainguard.v1.MergeQueueService/ConfirmMerge",
        "/mainguard.v1.PlanApprovalService/ApprovePlan",
        "/mainguard.v1.PlanApprovalService/RejectPlan",
        // MG-30: GetScrollback serves any agent's daemon-side scrollback ring (up to 1000 rows per
        // page) with no ownership scoping — a coordinator could read a worker's whole session, which
        // is exactly the read the coordinator surface is not supposed to have. The operator token
        // legitimately reads every agent (it drives the UI), so the boundary is the role, and this is
        // the same gate the merge/plan RPCs use. Now genuinely enforced: MG-12 (same change) makes a
        // coordinator token authenticate, so this check is reached instead of being dead code.
        // Per-agent ownership scoping (one connection ↔ its own agents) remains a separate concern.
        "/mainguard.v1.TerminalService/GetScrollback",
    };

    private const string AttachMethod = "/mainguard.v1.TerminalService/Attach";

    private readonly ConnectionRoleRegistry _roles;
    private readonly TerminalLockRegistry _locks;
    private readonly SessionTokenFile _tokenFile;

    public RoleInterceptor(ConnectionRoleRegistry roles, TerminalLockRegistry locks, SessionTokenFile tokenFile)
    {
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _tokenFile = tokenFile ?? throw new ArgumentNullException(nameof(tokenFile));
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        DenyIfCoordinatorForbidden(context);
        return continuation(request, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        DenyIfCoordinatorForbidden(context);
        return continuation(requestStream, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        DenyIfCoordinatorForbidden(context);
        return continuation(request, responseStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        DenyIfCoordinatorForbidden(context);

        // Terminal input lock: wrap the Attach INPUT stream so a data frame to a locked agent is rejected
        // at the interceptor — output (read) still flows.
        if (context.Method == AttachMethod && requestStream is IAsyncStreamReader<TerminalInput> terminalInput)
        {
            var filtered = new LockedInputReader(terminalInput, _locks);
            return continuation((IAsyncStreamReader<TRequest>)(object)filtered, responseStream, context);
        }

        return continuation(requestStream, responseStream, context);
    }

    private void DenyIfCoordinatorForbidden(ServerCallContext context)
    {
        if (!CoordinatorDeniedMethods.Contains(context.Method))
        {
            return;
        }

        var token = ExtractBearer(context);
        if (_roles.Resolve(token, _tokenFile.Token) == ConnectionRole.Coordinator)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "The coordinator role cannot invoke merge or plan-approval RPCs — chat + capped tools only."));
        }
    }

    private static string? ExtractBearer(ServerCallContext context)
    {
        var header = context.RequestHeaders.GetValue(HeaderKey);
        return header is not null && header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            ? header[Scheme.Length..]
            : null;
    }

    /// <summary>
    /// Wraps the <c>Attach</c> request stream: the first <c>agent_id</c> frame selects the agent, and any
    /// subsequent <c>data</c> (input) frame toward a locked agent throws <see cref="StatusCode.PermissionDenied"/>.
    /// Resize frames are harmless (window geometry) and pass through; the output stream is untouched.
    /// </summary>
    // internal (not private) so the MG-31 regression test can drive this reader directly. A gRPC-level
    // test cannot prove this layer: TerminalGrpcService re-checks the lock, so an end-to-end assertion
    // passes whether or not the interceptor tracks the Attach oneof.
    internal sealed class LockedInputReader : IAsyncStreamReader<TerminalInput>
    {
        private readonly IAsyncStreamReader<TerminalInput> _inner;
        private readonly TerminalLockRegistry _locks;
        private string? _agentId;

        public LockedInputReader(IAsyncStreamReader<TerminalInput> inner, TerminalLockRegistry locks)
        {
            _inner = inner;
            _locks = locks;
        }

        public TerminalInput Current => _inner.Current;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            var moved = await _inner.MoveNext(cancellationToken).ConfigureAwait(false);
            if (!moved)
            {
                return false;
            }

            var frame = _inner.Current;
            if (frame.InputCase == TerminalInput.InputOneofCase.AgentId)
            {
                _agentId = frame.AgentId;
            }
            else if (frame.InputCase == TerminalInput.InputOneofCase.Attach)
            {
                // MG-31: a P2-18 grid-capable client selects its agent with the Attach handshake
                // instead of the bare agent_id frame. Tracking only AgentId left `_agentId` null for
                // those clients, so every later Data frame sailed past this gate and the input-lock
                // layer was a no-op for them. TerminalGrpcService re-checks the lock (it reads both
                // oneofs), so this was defense-in-depth rather than a live bypass — but the
                // interceptor is the layer that is supposed to sever input, so it must see both.
                _agentId = frame.Attach.AgentId;
            }
            else if (frame.InputCase == TerminalInput.InputOneofCase.Data
                     && _agentId is not null && _locks.IsLocked(_agentId))
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied,
                    "This terminal is locked (managed worker) — input is denied. The read stream stays open."));
            }

            return true;
        }
    }
}
