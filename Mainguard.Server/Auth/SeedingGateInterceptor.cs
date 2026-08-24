using System;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Mainguard.Server.Auth;

/// <summary>
/// Whether this daemon was STARTED with queue seeding enabled (docs/design/queue-seeding.md §7).
/// Built once in <c>DaemonHost.ConfigureServices</c> from the boot-captured
/// <c>MAINGUARD_ENABLE_QUEUE_SEEDING</c> (or the in-proc test tier's <c>Daemon:EnableQueueSeeding</c>
/// configuration key) and immutable thereafter — the gate is a fact about process startup, never a
/// runtime toggle.
/// </summary>
public sealed record QueueSeedingOptions(bool Enabled);

/// <summary>
/// The BELT behind queue seeding's primary gate (docs/design/queue-seeding.md §7). The primary is
/// that a daemon without the boot flag never maps <c>QueueSeedingService</c> at all — disabled means
/// <c>UNIMPLEMENTED</c>, a service that structurally is not there. This interceptor denies the same
/// method prefix with <see cref="StatusCode.PermissionDenied"/> so that a future refactor which
/// accidentally makes the mapping unconditional still refuses, loudly, instead of quietly shipping a
/// seeding surface. (The coordinator-phase-3 review is explicit that an interceptor-only denial is
/// "defence-in-depth for a token nothing mints" — which is exactly the role this layer plays, and why
/// it is not the primary.)
/// </summary>
public sealed class SeedingGateInterceptor : Interceptor
{
    /// <summary>Every method of the dev-only seeding service starts with this.</summary>
    public const string MethodPrefix = "/mainguard.v1.QueueSeedingService/";

    private readonly QueueSeedingOptions _options;

    public SeedingGateInterceptor(QueueSeedingOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        DenyIfDisabled(context);
        return continuation(request, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        DenyIfDisabled(context);
        return continuation(requestStream, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        DenyIfDisabled(context);
        return continuation(request, responseStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        DenyIfDisabled(context);
        return continuation(requestStream, responseStream, context);
    }

    private void DenyIfDisabled(ServerCallContext context)
    {
        if (!_options.Enabled && context.Method.StartsWith(MethodPrefix, StringComparison.Ordinal))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "Queue seeding is not enabled on this daemon — it must be started with "
                + "MAINGUARD_ENABLE_QUEUE_SEEDING=1 (dev only)."));
        }
    }
}
