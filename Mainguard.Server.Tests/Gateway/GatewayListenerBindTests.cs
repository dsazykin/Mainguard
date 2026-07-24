using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Mainguard.Server;
using Xunit;

namespace Mainguard.Server.Tests.Gateway;

/// <summary>
/// MG-13 — the model-gateway listener, and the invariant it is NOT allowed to break.
///
/// <para>The daemon's gRPC control plane binds loopback only, and that is the trust boundary
/// (MG-19). The gateway needs to be reachable from the agent jail, which sits on an
/// <c>Internal=true</c> Docker network where <c>127.0.0.1</c> is the container itself — so it gets its
/// own listener. These tests pin that the exception stays narrow: the gateway is OFF unless
/// configured, an impermissible bind fails startup loudly rather than quietly exposing the daemon or
/// silently falling back to an unreachable loopback, and the control plane is untouched either way.</para>
/// </summary>
public sealed class GatewayListenerBindTests
{
    private static string TempToken() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mg-gwtok-" + Guid.NewGuid().ToString("N"));

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // The default must remain exactly today's posture: loopback only, no gateway.
    [Fact]
    public async Task GatewayDisabledByDefault_DaemonStaysLoopbackOnly()
    {
        await using var app = await DaemonHost.StartAsync(new DaemonOptions
        {
            Port = FreePort(),
            LocalDev = true,
            TokenPath = TempToken(),
            GatewayBindAddress = null, // default
        });

        Assert.NotEmpty(app.Urls);
        foreach (var url in app.Urls)
        {
            var host = new Uri(url).Host;
            Assert.True(IPAddress.TryParse(host, out var ip), $"host not an IP: {host}");
            Assert.True(IPAddress.IsLoopback(ip!), $"not loopback: {host}");
        }
    }

    // A wildcard bind would put the gateway on every interface the host is on. Startup must FAIL —
    // not fall back, not warn — because a silent fallback is how an exposure ships unnoticed.
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("8.8.8.8")]
    public async Task ImpermissibleGatewayBind_FailsStartupLoudly(string address)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var app = await DaemonHost.StartAsync(new DaemonOptions
            {
                Port = FreePort(),
                LocalDev = true,
                TokenPath = TempToken(),
                GatewayBindAddress = address,
                GatewayPort = FreePort(),
            });
        });

        Assert.Contains("gateway", Flatten(ex), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedGatewayBind_FailsStartupLoudly()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var app = await DaemonHost.StartAsync(new DaemonOptions
            {
                Port = FreePort(),
                LocalDev = true,
                TokenPath = TempToken(),
                GatewayBindAddress = "not-an-ip",
                GatewayPort = FreePort(),
            });
        });

        Assert.Contains("not a valid IP", Flatten(ex), StringComparison.OrdinalIgnoreCase);
    }

    // Loopback is permitted (it is what CI and local-dev can actually bind), and enabling the gateway
    // must not move the CONTROL plane off loopback.
    [Fact]
    public async Task PermittedGatewayBind_Starts_AndControlPlaneStaysLoopback()
    {
        var gatewayPort = FreePort();
        await using var app = await DaemonHost.StartAsync(new DaemonOptions
        {
            Port = FreePort(),
            LocalDev = true,
            TokenPath = TempToken(),
            GatewayBindAddress = "127.0.0.1",
            GatewayPort = gatewayPort,
        });

        // Every bound address is still loopback here, and the gateway port is among them.
        foreach (var url in app.Urls)
        {
            Assert.True(IPAddress.IsLoopback(IPAddress.Parse(new Uri(url).Host)));
        }

        Assert.Contains(app.Urls, u => new Uri(u).Port == gatewayPort);
    }

    private static string Flatten(Exception ex)
    {
        var text = ex.Message;
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            text += " | " + inner.Message;
        }

        return text;
    }
}
