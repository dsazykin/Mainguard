using System;
using System.Collections.Generic;
using System.Linq;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>One RPC the daemon actually serves, read off the running host's routing table.
/// <paramref name="RequestType"/>/<paramref name="ResponseType"/> come from the concrete
/// <c>Method&lt;TRequest, TResponse&gt;</c> the endpoint carries, so a caller can build a client-side
/// invocation for it without knowing the proto by name.</summary>
public sealed record MappedRpc(
    Type ServiceType,
    string ServiceName,
    string MethodName,
    MethodType MethodType,
    Type RequestType,
    Type ResponseType)
{
    public override string ToString() => $"{ServiceName}/{MethodName}";
}

/// <summary>
/// The daemon's gRPC surface as the daemon itself defines it: every endpoint <c>DaemonHost.MapServices</c>
/// mapped, discovered from the built host's <see cref="EndpointDataSource"/>s rather than from a list a
/// test keeps by hand.
///
/// <para>This exists because a hand-kept list is a test that quietly stops testing. <c>DaemonAuthTests</c>
/// reflected over four proto assemblies while nine services were mapped, so five services' RPCs — merge
/// queue, plan approval, kill switch, coordinator, egress — were never checked for authentication, and
/// adding a tenth service would not have changed that. Discovery from the routing table cannot drift: a
/// service that is mapped is covered, and one that is not mapped is not reachable.</para>
/// </summary>
public static class MappedGrpcRpcs
{
    /// <summary>Every mapped RPC on a built daemon host.</summary>
    public static IReadOnlyList<MappedRpc> Discover(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var rpcs = services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .Select(endpoint => endpoint.Metadata.GetMetadata<GrpcMethodMetadata>())
            .Where(metadata => metadata is not null)
            .Select(metadata =>
            {
                // The endpoint's IMethod is a closed Method<TRequest, TResponse>; its type arguments are
                // the only place the CLR message types are recorded on the routing table.
                var messageTypes = metadata!.Method.GetType().GetGenericArguments();
                return new MappedRpc(
                    metadata.ServiceType,
                    metadata.Method.ServiceName,
                    metadata.Method.Name,
                    metadata.Method.Type,
                    messageTypes[0],
                    messageTypes[1]);
            })
            .DistinctBy(rpc => (rpc.ServiceName, rpc.MethodName))
            .OrderBy(rpc => rpc.ServiceName, StringComparer.Ordinal)
            .ThenBy(rpc => rpc.MethodName, StringComparer.Ordinal)
            .ToList();

        // A discovery that silently returns nothing would make every caller vacuously green — the exact
        // failure mode this type exists to end.
        if (rpcs.Count == 0)
        {
            throw new InvalidOperationException(
                "No gRPC endpoints were discovered on the daemon host — endpoint discovery is broken, " +
                "and any test driven from it would be vacuous.");
        }

        return rpcs;
    }

    /// <summary>The distinct service implementation types behind those RPCs (the `MapGrpcService&lt;T&gt;`
    /// arguments, recovered from the routing table).</summary>
    public static IReadOnlyList<Type> ServiceTypes(IServiceProvider services)
        => Discover(services)
            .Select(rpc => rpc.ServiceType)
            .Distinct()
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();
}
