using System;
using System.Text;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Mainguard.Server.Auth;

/// <summary>
/// Authenticates EVERY RPC (unary, client-stream, server-stream, duplex) by
/// requiring an <c>authorization: bearer &lt;token&gt;</c> metadata header that
/// matches the session token via a constant-time compare
/// (<see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals"/>).
/// There is deliberately <b>no allowlist of "public" methods</b> (invariant 1): a new
/// RPC is authenticated automatically, and the reflection coverage test proves it.
/// Anything missing/wrong/malformed → <see cref="StatusCode.PermissionDenied"/>.
/// </summary>
public sealed class BearerTokenInterceptor : Interceptor
{
    private const string HeaderKey = "authorization";
    private const string Scheme = "bearer ";

    private readonly byte[] _expected;
    private readonly ConnectionRoleRegistry _roles;

    public BearerTokenInterceptor(SessionTokenFile tokenFile, ConnectionRoleRegistry roles)
    {
        _expected = Encoding.UTF8.GetBytes(tokenFile.Token);
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(request, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(requestStream, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(request, responseStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authenticate(context);
        return continuation(requestStream, responseStream, context);
    }

    private void Authenticate(ServerCallContext context)
    {
        var header = context.RequestHeaders.GetValue(HeaderKey);
        if (header is null || !header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Missing or malformed bearer token."));
        }

        var token = header[Scheme.Length..];
        var presented = Encoding.UTF8.GetBytes(token);
        // FixedTimeEquals already short-circuits on length mismatch without leaking timing.
        if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(presented, _expected))
        {
            return;
        }

        // MG-12: a role-scoped coordinator credential is a VALID authentication — it simply carries
        // fewer privileges, which RoleInterceptor enforces next. Previously only the operator token
        // authenticated, so a coordinator token died here and the role layer was unreachable dead
        // code (and the role tests passed for the wrong reason, asserting this interceptor's
        // PermissionDenied rather than the role gate's).
        if (_roles.IsCoordinatorToken(token))
        {
            return;
        }

        throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid bearer token."));
    }
}
