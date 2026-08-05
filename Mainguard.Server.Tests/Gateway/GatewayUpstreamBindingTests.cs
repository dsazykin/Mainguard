using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;
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
/// The per-agent upstream binding — the fix for the defect that made the whole P2-08 gateway
/// unreachable in production.
///
/// <para><b>Why the existing custody tests did not catch it.</b> Every one of them builds its request
/// with <c>Host = api.anthropic.com</c>. That shape cannot occur once an agent is actually confined: the
/// jail's CLI is pointed at the gateway by its base-URL variable, so the <c>Host</c> it sends is the
/// GATEWAY's address. The middleware decided "is this model traffic?" by matching that header against a
/// model-host list, which therefore never matched, and every real request fell through to <c>_next</c>
/// unfronted — no key substitution, and no <c>BudgetLedger</c> write. The tests passed because they
/// asserted against a request shape production never produces.</para>
///
/// <para>So every request here is built the way a confined jail actually sends it: <c>Host</c> is the
/// gateway. Routing and attribution both come from the agent's token.</para>
/// </summary>
public sealed class GatewayUpstreamBindingTests
{
    private const string GatewayHost = "172.18.0.1";   // the daemon's gateway bind address, not a provider
    private const string Upstream = "api.anthropic.com";
    private const string RealKey = "sk-ant-REAL-PROVIDER-KEY";
    private const string Agent = "agent-7";

    // The core regression: a request whose Host is the GATEWAY is still fronted, forwarded to the
    // agent's BOUND upstream, and charged to that agent's budget.
    [Fact]
    public async Task ConfinedAgent_RequestToGatewayHost_IsForwardedToItsBoundUpstream()
    {
        var creds = new AgentGatewayCredentials();
        var token = creds.Issue(Agent, RealKey, Upstream);
        var capture = new CapturingHandler();

        var seen = await InvokeAsync(creds, capture, token);

        Assert.Equal(StatusCodes.Status200OK, seen.StatusCode);
        Assert.NotNull(capture.Request);
        // Routed by the agent's binding — NOT by the Host header, which named the gateway.
        Assert.Equal(Upstream, capture.Request!.RequestUri!.Host);
        Assert.Equal("https", capture.Request.RequestUri.Scheme);
        // MG-4: the daemon's key went upstream; the agent's token did not.
        Assert.Equal(RealKey, capture.Request.Headers.GetValues("x-api-key").First());
    }

    // The budget half: the ledger is actually written on the real path. This is the assertion that was
    // unreachable outside tests — AiGateway.Acquire/Settle had no production caller at all.
    [Fact]
    public async Task ConfinedAgent_ChargesTheBudgetLedger()
    {
        var creds = new AgentGatewayCredentials();
        var token = creds.Issue(Agent, RealKey, Upstream);
        var ledger = NewLedger(new BudgetCaps(0, 0, 0, 0));

        await InvokeAsync(creds, new CapturingHandler(), token, ledger);

        // 41 tokens = the usage in the stubbed provider response below.
        Assert.Equal(41, ledger.GetTotals(Agent).Tokens);
    }

    // An over-budget agent is refused with a soft 402 and nothing is forwarded upstream.
    [Fact]
    public async Task OverBudgetAgent_IsRefused_AndNothingReachesTheProvider()
    {
        var creds = new AgentGatewayCredentials();
        var token = creds.Issue(Agent, RealKey, Upstream);
        var ledger = NewLedger(new BudgetCaps(PerAgentTokenCap: 100, 0, 0, 0));
        // Spend the agent past its cap first. A cap is not a pre-flight estimate check — the first
        // request of a fresh agent is always admitted — so "over budget" means settled spend already
        // exceeds the cap, which is exactly the state a long-running agent reaches.
        ledger.Record(Agent, "claude-3-5-haiku", tokens: 500);
        var capture = new CapturingHandler();

        var seen = await InvokeAsync(creds, capture, token, ledger);

        Assert.Equal(StatusCodes.Status402PaymentRequired, seen.StatusCode);
        Assert.Null(capture.Request);
    }

    // THE OAUTH REGRESSION. An interactive-login agent has no key, so it is never issued a gateway
    // token or an upstream binding. Its traffic must pass through untouched — not be fronted, and above
    // all not be refused 401. Breaking this breaks the owner's own agents.
    [Fact]
    public async Task OAuthAgent_HasNoBinding_AndPassesThroughUntouched()
    {
        var creds = new AgentGatewayCredentials();
        // An OAuth CLI presents its OWN bearer token; the daemon issued it nothing.
        var capture = new CapturingHandler();

        var seen = await InvokeAsync(creds, capture, presentedToken: "oauth-session-token-not-ours");

        Assert.True(seen.PassedThrough, "OAuth traffic must reach _next, not be fronted or refused.");
        Assert.NotEqual(StatusCodes.Status401Unauthorized, seen.StatusCode);
        Assert.Null(capture.Request);
    }

    // An unknown token that DOES look like model traffic (the legacy proxy shape) is still refused —
    // the pass-through above must not become an authentication bypass.
    [Fact]
    public async Task UnknownToken_OnLegacyModelHostShape_IsStillRefused()
    {
        var creds = new AgentGatewayCredentials();
        var capture = new CapturingHandler();

        var seen = await InvokeAsync(
            creds, capture, presentedToken: "mg_sess_bogus", requestHost: Upstream);

        Assert.Equal(StatusCodes.Status401Unauthorized, seen.StatusCode);
        Assert.Null(capture.Request);
    }

    // ---- harness ----------------------------------------------------------

    private sealed record Seen(int StatusCode, bool PassedThrough);

    private static BudgetLedger NewLedger(BudgetCaps caps) =>
        new(new InMemorySpendStore(), () => DateTimeOffset.UtcNow, caps);

    private static async Task<Seen> InvokeAsync(
        AgentGatewayCredentials creds,
        CapturingHandler upstream,
        string? presentedToken,
        BudgetLedger? ledger = null,
        int? estimate = null,
        string requestHost = GatewayHost)
    {
        var gateway = new AiGateway(
            TokenBucket.FromKeyHealth(null, () => DateTimeOffset.UtcNow),
            ledger ?? NewLedger(new BudgetCaps(0, 0, 0, 0)),
            NullAgentSupervisor.Instance,
            new InMemoryAuditLog(),
            () => DateTimeOffset.UtcNow);

        var passedThrough = false;
        var middleware = new ModelProxyMiddleware(
            next: _ => { passedThrough = true; return Task.CompletedTask; },
            forwarder: new GatewayForwarder(
                gateway, new HttpMessageInvoker(upstream), delay: (_, _) => Task.CompletedTask),
            portMap: new NullAgentPortMap(),
            modelHosts: ModelHosts.All,
            credentials: creds);

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "http";
        // The production shape: the confined CLI dials the GATEWAY, so this is the gateway's address.
        context.Request.Host = new HostString(requestHost);
        context.Request.Path = "/v1/messages";
        context.Request.Body = new System.IO.MemoryStream(Encoding.UTF8.GetBytes("{}"));
        context.Response.Body = new System.IO.MemoryStream();

        if (presentedToken is not null)
        {
            context.Request.Headers["x-api-key"] = presentedToken;
        }

        if (estimate is not null)
        {
            context.Request.Headers["x-mainguard-token-estimate"] = estimate.Value.ToString();
        }

        await middleware.InvokeAsync(context);
        return new Seen(context.Response.StatusCode, passedThrough);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"model\":\"claude-3-5-haiku\",\"usage\":{\"input_tokens\":20,\"output_tokens\":21}}"),
            });
        }
    }
}
