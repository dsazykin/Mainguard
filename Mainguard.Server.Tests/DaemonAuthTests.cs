using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Protos.V1;
using Mainguard.Server;
using Mainguard.Server.Tests.Fixtures;

namespace Mainguard.Server.Tests;

/// <summary>
/// TI-P2-02 §1/§2/§6/§10 + plan §6 rows 1,2,6,6(loopback),7,8. Auth coverage (every RPC the daemon
/// maps, discovered from its own routing table), loopback bind, unimplemented stubs, token-file
/// permissions, port-already-bound.
/// </summary>
public sealed class DaemonAuthTests : IClassFixture<DaemonFixture>
{
    private readonly DaemonFixture _daemon;

    public DaemonAuthTests(DaemonFixture daemon) => _daemon = daemon;

    // TI.1 — authenticated unary call succeeds.
    [Fact]
    public async Task AuthenticatedCall_ShouldSucceed()
    {
        var client = new AgentService.AgentServiceClient(_daemon.CreateChannel());
        var response = await client.ListAgentsAsync(new ListAgentsRequest(), _daemon.AuthHeaders());
        Assert.Empty(response.Agents);
    }

    // TI.1 — wrong token, unary → PermissionDenied.
    [Fact]
    public async Task WrongToken_ShouldReturnPermissionDenied()
    {
        var client = new AgentService.AgentServiceClient(_daemon.CreateChannel());
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            client.ListAgentsAsync(new ListAgentsRequest(), _daemon.WrongTokenHeaders()).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    // TI.1 — missing token → PermissionDenied.
    [Fact]
    public async Task MissingToken_ShouldReturnPermissionDenied()
    {
        var client = new AgentService.AgentServiceClient(_daemon.CreateChannel());
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            client.ListAgentsAsync(new ListAgentsRequest(), new Metadata()).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    // §6.2 — the denial applies to streaming calls too.
    [Fact]
    public async Task WrongToken_ShouldReturnPermissionDenied_OnStreamingCall()
    {
        var client = new AgentService.AgentServiceClient(_daemon.CreateChannel());
        using var call = client.StreamAgentEvents(new StreamAgentEventsRequest(), _daemon.WrongTokenHeaders());
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await foreach (var _ in call.ResponseStream.ReadAllAsync())
            {
            }
        });
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    // TI.1 / §6.6 — AuthCoverage_EveryMethodByReflection. Every RPC the daemon actually MAPS is denied
    // with a wrong token. The set comes from the running host's routing table (MappedGrpcRpcs), not from
    // a list kept here: this test used to reflect over four proto assemblies while DaemonHost mapped
    // NINE services, so merge-queue, plan-approval, kill-switch, coordinator and egress RPCs were never
    // checked at all — and a tenth service could have shipped unauthenticated with this test still green.
    // Driving off the map means coverage cannot drift: mapped is covered, unmapped is unreachable.
    [Fact]
    public async Task AuthCoverage_EveryMappedRpc_WrongToken_Denied()
    {
        var rpcs = MappedGrpcRpcs.Discover(_daemon.Services);
        var invoker = _daemon.CreateChannel().CreateCallInvoker();
        var notDenied = new List<string>();

        foreach (var rpc in rpcs)
        {
            var ex = await Record.ExceptionAsync(() => InvokeDeniedAsync(invoker, rpc, _daemon.WrongTokenHeaders()));
            if (ex is not RpcException { StatusCode: StatusCode.PermissionDenied })
            {
                notDenied.Add($"{rpc} → {(ex as RpcException)?.StatusCode.ToString() ?? ex?.GetType().Name ?? "no exception"}");
            }
        }

        Assert.Empty(notDenied);

        // Every mapped service is represented — a service whose RPCs vanished from the set would
        // otherwise pass this test by not being tested.
        Assert.Equal(
            MappedGrpcRpcs.ServiceTypes(_daemon.Services).Count,
            rpcs.Select(r => r.ServiceName).Distinct().Count());
    }

    // §6.6(loopback) / TI.2 — the real host binds loopback only (127.0.0.1), never AnyIP.
    [Fact]
    public async Task Daemon_ShouldBindLoopbackOnly()
    {
        await using var daemon = await TestDaemonHost.StartAsync(new DaemonOptions
        {
            LocalDev = true,
            TokenPath = TempToken(),
        });

        Assert.NotEmpty(daemon.Urls);
        foreach (var address in daemon.Urls)
        {
            var uri = new Uri(address);
            Assert.True(IPAddress.TryParse(uri.Host, out var ip), $"host not an IP: {uri.Host}");

            // Invariant 2, stated precisely now that MG-4 enables the model gateway by default. The
            // CONTROL PLANE is still loopback-only and unconditionally so — that is where the mTLS
            // session boundary lives. The gateway listener is the one deliberate relaxation (see
            // GatewayBindPolicy): a jail on an Internal network cannot reach loopback, so a gateway
            // there would confine nothing. Relaxed to "private", never to "any": this asserts the
            // policy holds for whatever the auto-resolution picked, so a wildcard or public bind
            // sneaking into the default still fails here.
            if (uri.Port == daemon.Port)
            {
                Assert.True(IPAddress.IsLoopback(ip!), $"control plane is not loopback: {uri.Host}");
                continue;
            }

            Assert.Equal(daemon.GatewayPort, uri.Port);
            Assert.True(
                Mainguard.Server.Gateway.GatewayBindPolicy.IsPermitted(ip, out var reason),
                $"the default gateway bind is impermissible: {reason}");
            Assert.False(
                ip!.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any),
                $"the gateway bound a wildcard: {uri.Host}");
        }
    }

    // §6.7 / TI.10 — RepoSync is implemented (P2-06): it now validates rather than stubbing
    // Unimplemented (an empty ProvisionRepo request → InvalidArgument). Gateway is implemented (P2-08):
    // GetBudgets now returns a budget rather than the old Unimplemented stub.
    [Fact]
    public async Task RepoSync_ValidatesRequest_And_Gateway_IsImplemented()
    {
        var repoSync = new RepoSyncService.RepoSyncServiceClient(_daemon.CreateChannel());
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            repoSync.ProvisionRepoAsync(new ProvisionRepoRequest(), _daemon.AuthHeaders()).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);

        var gateway = new GatewayService.GatewayServiceClient(_daemon.CreateChannel());
        var budgets = await gateway.GetBudgetsAsync(new GetBudgetsRequest(), _daemon.AuthHeaders());
        Assert.NotNull(budgets.Budget); // P2-08: implemented, no longer an Unimplemented stub
    }

    // §6.8 / TI — token-file permissions. Linux: mode 0600 (no group/other). Windows: skip.
    [Fact]
    public void TokenFile_Permissions_ShouldBeUserOnly()
    {
        var tokenFile = Mainguard.Server.Auth.SessionTokenFile.Create(TempToken());
        Assert.True(System.IO.File.Exists(tokenFile.Path));

        if (!OperatingSystem.IsWindows())
        {
            var mode = System.IO.File.GetUnixFileMode(tokenFile.Path);
            const System.IO.UnixFileMode groupOther =
                System.IO.UnixFileMode.GroupRead | System.IO.UnixFileMode.GroupWrite | System.IO.UnixFileMode.GroupExecute
                | System.IO.UnixFileMode.OtherRead | System.IO.UnixFileMode.OtherWrite | System.IO.UnixFileMode.OtherExecute;
            Assert.Equal(default, mode & groupOther);
        }
    }

    // TI.7 / edge row 4 — token file deleted while running: existing channels keep working
    // (the interceptor holds the token in memory; the file is only read at bootstrap).
    [Fact]
    public async Task TokenFileDeletedWhileRunning_ShouldNotBreakExistingChannels()
    {
        var tokenPath = TempToken();
        await using var daemon = await TestDaemonHost.StartAsync(new DaemonOptions { LocalDev = true, TokenPath = tokenPath });
        var token = daemon.Token;

        // MG-19: the control plane is mutually-authenticated TLS, so this goes through the real
        // pinned transport. The credentials are loaded BEFORE the token file is deleted below —
        // exactly like a live client that connected while the daemon was healthy.
        using var channel = Fixtures.PinnedDaemonChannel.Pinned(daemon.Port, tokenPath);
        var client = new AgentService.AgentServiceClient(channel);
        var headers = new Metadata { { "authorization", $"bearer {token}" } };

        await client.ListAgentsAsync(new ListAgentsRequest(), headers, deadline: DateTime.UtcNow.AddSeconds(10));

        // Delete the on-disk token — the running daemon must keep honoring live channels.
        System.IO.File.Delete(tokenPath);
        Assert.False(System.IO.File.Exists(tokenPath));

        var afterDelete = await client.ListAgentsAsync(new ListAgentsRequest(), headers, deadline: DateTime.UtcNow.AddSeconds(10));
        Assert.Empty(afterDelete.Agents);
    }

    // §6 edge row 3 / TI.6 — port already bound → typed startup failure naming the port.
    //
    // The ONE test here that wants a specific port, and wants it OCCUPIED. It therefore takes a listener
    // that is already bound (TestPorts.LeaseBoundListener never releases the socket, so unlike a leased
    // port number there is no window for another process to steal it) and calls DaemonHost.StartAsync
    // DIRECTLY — deliberately bypassing TestDaemonHost, whose whole job is to retry past exactly the
    // failure this test is asserting on.
    [Fact]
    public async Task PortAlreadyBound_ShouldFailTypedNamingPort()
    {
        var blocker = Fixtures.TestPorts.LeaseBoundListener();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
        try
        {
            var ex = await Assert.ThrowsAsync<DaemonStartupException>(() =>
                DaemonHost.StartAsync(new DaemonOptions { Port = port, LocalDev = true, TokenPath = TempToken() }));
            Assert.Equal(port, ex.Port);
            Assert.Contains(port.ToString(), ex.Message);
        }
        finally
        {
            blocker.Stop();
        }
    }

    private static readonly MethodInfo GenericInvoke = typeof(DaemonAuthTests)
        .GetMethod(nameof(InvokeDeniedGeneric), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Task InvokeDeniedAsync(CallInvoker invoker, MappedRpc rpc, Metadata headers)
    {
        var generic = GenericInvoke.MakeGenericMethod(rpc.RequestType, rpc.ResponseType);
        return (Task)generic.Invoke(null, new object[]
        {
            invoker, rpc.ServiceName, rpc.MethodName, rpc.MethodType, headers,
        })!;
    }

    private static async Task InvokeDeniedGeneric<TReq, TResp>(
        CallInvoker invoker, string service, string methodName, MethodType type, Metadata headers)
        where TReq : class, IMessage<TReq>, new()
        where TResp : class, IMessage<TResp>, new()
    {
        var reqParser = new MessageParser<TReq>(() => new TReq());
        var respParser = new MessageParser<TResp>(() => new TResp());
        var method = new Method<TReq, TResp>(type, service, methodName,
            Marshallers.Create(m => m.ToByteArray(), reqParser.ParseFrom),
            Marshallers.Create(m => m.ToByteArray(), respParser.ParseFrom));
        var options = new CallOptions(headers);
        var request = new TReq();

        switch (type)
        {
            case MethodType.Unary:
                await invoker.AsyncUnaryCall(method, null, options, request).ResponseAsync;
                break;
            case MethodType.ServerStreaming:
                using (var call = invoker.AsyncServerStreamingCall(method, null, options, request))
                {
                    await call.ResponseStream.MoveNext(CancellationToken.None);
                }

                break;
            case MethodType.ClientStreaming:
                using (var call = invoker.AsyncClientStreamingCall(method, null, options))
                {
                    await call.ResponseAsync;
                }

                break;
            default:
                using (var call = invoker.AsyncDuplexStreamingCall(method, null, options))
                {
                    await call.ResponseStream.MoveNext(CancellationToken.None);
                }

                break;
        }
    }

    private static string TempToken()
        => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mainguard-tok-" + Guid.NewGuid().ToString("N"), "daemon.token");
}
