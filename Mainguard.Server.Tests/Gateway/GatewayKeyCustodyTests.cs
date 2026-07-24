using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Git.Audit;
using Mainguard.Server.Gateway;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Mainguard.Server.Tests.Gateway;

/// <summary>
/// MG-4 / MG-20 / MG-38 — the gateway's credential-custody boundary.
///
/// <para>MG-4: the agent must present only a Mainguard token; the real provider key is held daemon-side
/// and injected at the network hop, so it never has to exist inside the jail. MG-20: the calling agent's
/// identity must come from that authenticated token, never the client-supplied <c>x-mainguard-agent</c>
/// header (which an agent could set to a victim's id to dodge its own budget or attribute spend/429
/// pauses elsewhere). MG-38: request and response headers are filtered, not relayed verbatim.</para>
/// </summary>
public sealed class GatewayKeyCustodyTests
{
    private const string ModelHost = "api.anthropic.com";
    private const string RealKey = "sk-ant-REAL-PROVIDER-KEY";
    private const string AgentA = "agent-a";
    private const string AgentB = "agent-b";

    [Fact]
    public void Credentials_IssueToken_KeepsProviderKeyDaemonSide()
    {
        var creds = new AgentGatewayCredentials();
        var token = creds.Issue(AgentA, RealKey);

        // What the jail receives is a Mainguard token, and it is NOT the provider key.
        Assert.StartsWith(AgentGatewayCredentials.TokenPrefix, token);
        Assert.DoesNotContain(RealKey, token);

        // The daemon can still resolve identity and recover the key.
        Assert.Equal(AgentA, creds.ResolveAgent(token));
        Assert.Equal(RealKey, creds.ProviderKeyFor(AgentA));
    }

    [Fact]
    public void Credentials_UnknownOrRevokedToken_ResolvesToNothing()
    {
        var creds = new AgentGatewayCredentials();
        var token = creds.Issue(AgentA, RealKey);

        Assert.Null(creds.ResolveAgent("mg_sess_not-a-real-token"));
        Assert.Null(creds.ResolveAgent(null));

        creds.Revoke(AgentA);
        Assert.Null(creds.ResolveAgent(token));           // cannot be replayed after teardown
        Assert.Null(creds.ProviderKeyFor(AgentA));        // key does not outlive the session
    }

    [Fact]
    public void Credentials_Reissue_DoesNotLeaveTheOldTokenValid()
    {
        var creds = new AgentGatewayCredentials();
        var first = creds.Issue(AgentA, RealKey);
        var second = creds.Issue(AgentA, RealKey);

        Assert.NotEqual(first, second);
        Assert.Null(creds.ResolveAgent(first));
        Assert.Equal(AgentA, creds.ResolveAgent(second));
    }

    // MG-4 core: the provider key reaching the provider must be the DAEMON's, and the agent's own
    // credential must never survive the hop.
    [Fact]
    public async Task Upstream_CarriesTheDaemonHeldKey_NotTheAgentsToken()
    {
        var creds = new AgentGatewayCredentials();
        var token = creds.Issue(AgentA, RealKey);
        var capture = new CapturingHandler();

        await InvokeAsync(creds, capture, token, headers: new Dictionary<string, string>());

        var sent = capture.Request!;
        Assert.Equal(RealKey, sent.Headers.GetValues("x-api-key").Single());
        // The agent's Mainguard token did not travel to the provider.
        Assert.DoesNotContain(
            sent.Headers.SelectMany(h => h.Value), v => v.Contains(token, StringComparison.Ordinal));
    }

    // MG-20: a spoofed identity header must not select the agent.
    [Fact]
    public async Task SpoofedAgentHeader_IsIgnored_IdentityComesFromTheToken()
    {
        var creds = new AgentGatewayCredentials();
        var tokenA = creds.Issue(AgentA, RealKey);
        creds.Issue(AgentB, "sk-ant-VICTIM-KEY");
        var capture = new CapturingHandler();

        var seen = await InvokeAsync(
            creds, capture, tokenA,
            headers: new Dictionary<string, string> { ["x-mainguard-agent"] = AgentB });

        // Attribution followed the TOKEN (agent-a), not the header claiming to be agent-b.
        Assert.Equal(AgentA, seen.AgentId);
        // And agent-b's key was not used.
        Assert.Equal(RealKey, capture.Request!.Headers.GetValues("x-api-key").Single());
        // The internal header never reached the provider either (MG-38).
        Assert.False(capture.Request.Headers.Contains("x-mainguard-agent"));
    }

    [Fact]
    public async Task UnauthenticatedCaller_IsRefused_NotDefaultedFromAHeader()
    {
        var creds = new AgentGatewayCredentials();
        creds.Issue(AgentA, RealKey);
        var capture = new CapturingHandler();

        // No token at all, but a header asserting an identity — previously this was accepted.
        var seen = await InvokeAsync(
            creds, capture, presentedToken: null,
            headers: new Dictionary<string, string> { ["x-mainguard-agent"] = AgentA });

        Assert.Equal(StatusCodes.Status401Unauthorized, seen.StatusCode);
        Assert.Null(capture.Request); // nothing was forwarded upstream
    }

    // MG-38: an agent-supplied credential header must be stripped, not relayed alongside the injected key.
    [Fact]
    public async Task AgentSuppliedCredentialHeaders_AreStripped()
    {
        var creds = new AgentGatewayCredentials();
        var token = creds.Issue(AgentA, RealKey);
        var capture = new CapturingHandler();

        await InvokeAsync(creds, capture, token, headers: new Dictionary<string, string>
        {
            ["authorization"] = "Bearer sk-ant-AGENT-SMUGGLED-KEY",
            ["anthropic-api-key"] = "sk-ant-ANOTHER-SMUGGLED",
            ["connection"] = "keep-alive",
        });

        var sent = capture.Request!;
        var all = string.Join("|", sent.Headers.SelectMany(h => h.Value));
        Assert.DoesNotContain("SMUGGLED", all, StringComparison.Ordinal);
        Assert.False(sent.Headers.Contains("connection"));      // hop-by-hop dropped
        Assert.Equal(RealKey, sent.Headers.GetValues("x-api-key").Single());
    }

    // ---- harness ----------------------------------------------------------

    private sealed record Seen(string? AgentId, int StatusCode);

    /// <summary>Drives the middleware over a synthetic request and reports what the gateway attributed.</summary>
    private static async Task<Seen> InvokeAsync(
        AgentGatewayCredentials creds,
        CapturingHandler upstream,
        string? presentedToken,
        IDictionary<string, string> headers)
    {
        string? attributed = null;
        var gateway = new AiGateway(
            TokenBucket.FromKeyHealth(null, () => DateTimeOffset.UtcNow),
            new BudgetLedger(new InMemorySpendStore(), () => DateTimeOffset.UtcNow, new BudgetCaps(0, 0, 0, 0)),
            NullAgentSupervisor.Instance,
            new InMemoryAuditLog(),
            () => DateTimeOffset.UtcNow);

        var forwarder = new GatewayForwarder(
            gateway,
            new HttpMessageInvoker(upstream),
            delay: (_, _) => Task.CompletedTask);

        var middleware = new ModelProxyMiddleware(
            next: _ => Task.CompletedTask,
            forwarder: forwarder,
            portMap: new NullPortMap(),
            modelHosts: new[] { ModelHost },
            credentials: creds);

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(ModelHost);
        context.Request.Path = "/v1/messages";
        context.Request.Body = new System.IO.MemoryStream(Encoding.UTF8.GetBytes("{}"));
        context.Response.Body = new System.IO.MemoryStream();

        if (presentedToken is not null)
        {
            context.Request.Headers["x-api-key"] = presentedToken;
        }

        foreach (var (k, v) in headers)
        {
            context.Request.Headers[k] = v;
        }

        await middleware.InvokeAsync(context);

        attributed = creds.ResolveAgent(presentedToken);
        return new Seen(attributed, context.Response.StatusCode);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}"),
            });
        }
    }

    private sealed class NullPortMap : IAgentPortMap
    {
        public string? AgentForPort(int port) => null;
    }

}
