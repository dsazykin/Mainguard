using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Mainguard.Server;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Gateway;

/// <summary>
/// That the model gateway is actually ON THE REQUEST PATH of a real daemon — not merely bound.
///
/// <para><b>Why this test exists separately from the bind tests.</b> `GatewayListenerBindTests` proves a
/// port is listening. It cannot prove anything is SERVING on it: before this change the daemon bound the
/// gateway port and had no middleware behind it at all, so the listener answered 404 to every model
/// request and `BudgetLedger` was never written. A bind assertion passes in exactly that world.</para>
///
/// <para>ASP.NET also activates middleware LAZILY, on the first request rather than at startup — so a
/// `ModelProxyMiddleware` whose constructor arguments could not be resolved from DI would start a daemon
/// perfectly happily and only fail once an agent made a call. These tests therefore issue a real HTTP
/// request over the real Kestrel listener.</para>
/// </summary>
public sealed class GatewayPipelineWiringTests
{
    private static string TempToken() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mg-gwpipe-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// An unauthenticated request that DOES look like model traffic must be refused 401 BY THE
    /// MIDDLEWARE. A 404 here means the pipeline has nothing behind the listener — the exact defect
    /// this change fixes — and any transport error means the middleware could not be activated.
    /// </summary>
    [Fact]
    public async Task GatewayPort_ServesTheModelProxyMiddleware_NotAnEmptyPipeline()
    {
        await using var daemon = await TestDaemonHost.StartAsync(new DaemonOptions
        {
            LocalDev = true,
            TokenPath = TempToken(),
            GatewayBindAddress = "127.0.0.1",
        });

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"http://127.0.0.1:{daemon.GatewayPort}/v1/messages")
        {
            Content = new StringContent("{}"),
        };
        // The legacy proxy-shaped request: Host names a model host, and no gateway token is presented.
        request.Headers.Host = "api.anthropic.com";

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The control plane must NOT get this middleware. It is loopback + mutual TLS (MG-19); putting an
    /// unauthenticated HTTP shim in front of it would be a downgrade. The gRPC port rejects a plaintext
    /// HTTP/1.1 call at the TLS handshake, which is the posture we are pinning.
    /// </summary>
    [Fact]
    public async Task ControlPlanePort_IsUnaffectedByTheGatewayMiddleware()
    {
        await using var daemon = await TestDaemonHost.StartAsync(new DaemonOptions
        {
            LocalDev = true,
            TokenPath = TempToken(),
            GatewayBindAddress = "127.0.0.1",
        });

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"http://127.0.0.1:{daemon.Port}/v1/messages");
        request.Headers.Host = "api.anthropic.com";

        // Plaintext against the mutually-authenticated gRPC listener never yields a 401 from the model
        // proxy — it fails at the transport, which is what proves the branch did not leak onto this port.
        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(request));
    }
}
