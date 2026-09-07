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
/// F23 / F29 — the gateway's metering is honest under streaming, and the jail cannot lower its own bill.
///
/// <para>The finding these pin: the daemon reserved and settled with the jail's own
/// <c>x-mainguard-token-estimate</c>, and <c>ModelUsageParser</c> ran <c>JsonDocument.Parse</c> over the
/// whole body, which throws for an SSE stream — claude-code's default. Usage came back null, the ledger
/// settled at the estimate, and an estimate of <c>0</c> was accepted, so a confined agent sending
/// <c>stream:true</c> spent the operator's key for free. Honest streaming clients fared no better: they
/// were charged a fixed default, never their actual usage.</para>
/// </summary>
public class GatewayStreamingMeteringTests
{
    private const string ModelHost = "api.anthropic.com";

    /// <summary>A real Anthropic stream: input tokens arrive in <c>message_start</c>, the running output
    /// count in each <c>message_delta</c>. Nothing in it is a parseable JSON document.</summary>
    private const string AnthropicSse =
        "event: message_start\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-sonnet-4-5\",\"usage\":{\"input_tokens\":1200,\"output_tokens\":1}}}\n" +
        "\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"hello\"}}\n" +
        "\n" +
        "event: message_delta\n" +
        "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":340}}\n" +
        "\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    [Fact]
    public void SseStream_UsageIsParsed_NotDiscarded()
    {
        var (tokens, model) = ModelUsageParser.Parse(AnthropicSse);

        // 1200 input + 340 output. The old parser returned (null, "") for this exact body.
        Assert.Equal(1540, tokens);
        Assert.Equal("claude-sonnet-4-5", model);
    }

    [Fact]
    public void OpenAiStream_UsageChunk_IsParsed()
    {
        var body =
            "data: {\"model\":\"gpt-4o\",\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
            "data: {\"model\":\"gpt-4o\",\"choices\":[],\"usage\":{\"prompt_tokens\":30,\"completion_tokens\":70,\"total_tokens\":100}}\n\n" +
            "data: [DONE]\n\n";

        var (tokens, model) = ModelUsageParser.Parse(body);

        Assert.Equal(100, tokens);
        Assert.Equal("gpt-4o", model);
    }

    [Fact]
    public void NonStreamingJson_StillParses()
    {
        var (tokens, model) = ModelUsageParser.Parse(
            "{\"model\":\"gpt-4o\",\"usage\":{\"total_tokens\":321}}");

        Assert.Equal(321, tokens);
        Assert.Equal("gpt-4o", model);
    }

    [Fact]
    public async Task SseResponse_SettlesRealUsage_AndReachesTheAgent()
    {
        var (gateway, _) = BuildGateway();
        var result = await ForwardAsync(gateway, AnthropicSse, "text/event-stream", estimate: null);

        // The charge is the stream's own numbers, not the 1000-token default estimate.
        Assert.Equal(1540, gateway.GetSnapshot().Agents.Single().Tokens);

        // And the body still reached the agent, byte for byte — metering must not consume the stream.
        Assert.Equal(AnthropicSse, result.Body);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task ClientEstimate_CannotLowerTheCharge()
    {
        var (gateway, _) = BuildGateway();

        // The exact bypass: stream:true (unparseable by the old parser) plus an estimate of zero.
        var result = await ForwardAsync(
            gateway, "event: ping\ndata: {\"type\":\"ping\"}\n\n", "text/event-stream", estimate: "0");

        Assert.Equal(200, result.StatusCode);

        // No usage in that stream, so the settle falls back to the estimate — which is FLOORED at the
        // forwarder's own default. Zero is not an accepted answer.
        var charged = gateway.GetSnapshot().Agents.Single().Tokens;
        Assert.Equal(1000, charged);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    public async Task ClientEstimate_BelowTheFloor_IsIgnored(string header)
    {
        var (gateway, _) = BuildGateway();
        var result = await ForwardAsync(gateway, "{\"ok\":true}", "application/json", estimate: header);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(1000, gateway.GetSnapshot().Agents.Single().Tokens);
    }

    [Fact]
    public async Task ClientEstimate_AboveTheFloor_StillRaisesTheReservation()
    {
        var (gateway, _) = BuildGateway();

        // Raising your own estimate is harmless and occasionally honest — that direction still works.
        var result = await ForwardAsync(gateway, "{\"ok\":true}", "application/json", estimate: "5000");

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(5000, gateway.GetSnapshot().Agents.Single().Tokens);
    }

    [Fact]
    public async Task OversizedRequestBody_IsRefusedWith413()
    {
        var (gateway, _) = BuildGateway();
        var result = await ForwardAsync(
            gateway, "{\"ok\":true}", "application/json", estimate: null,
            requestBody: new string('x', 4096), maxRequestBodyBytes: 1024);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, result.StatusCode);

        // Refused at the boundary: nothing was forwarded, so nothing was charged.
        Assert.Empty(gateway.GetSnapshot().Agents);
    }

    [Fact]
    public void ZeroSettle_AgainstALiveReservation_ChargesTheReservation()
    {
        var ledger = new BudgetLedger(
            new InMemorySpendStore(), () => DateTimeOffset.UtcNow, BudgetCaps.Unlimited);

        Assert.Equal(
            BudgetAdmission.Granted,
            ledger.TryReserve("agent-1", 750, out var reservationId, out _));

        // "The response carried no usage" must never mean "the request was free".
        var record = ledger.SettleReservation(reservationId, "agent-1", "claude-sonnet-4-5", actualTokens: 0);

        Assert.Equal(750, record.Tokens);
        Assert.Equal(750, ledger.GetTotals("agent-1").Tokens);
    }

    [Fact]
    public void ZeroSettle_WithNoReservation_KeepsItsOldMeaning()
    {
        var ledger = new BudgetLedger(
            new InMemorySpendStore(), () => DateTimeOffset.UtcNow, BudgetCaps.Unlimited);

        // No admission happened, so there is no reserved amount to fall back to.
        var record = ledger.SettleReservation(reservationId: 0, "agent-1", "m", actualTokens: 0);

        Assert.Equal(0, record.Tokens);
    }

    // ---- harness ----------------------------------------------------------

    private sealed record Relayed(int StatusCode, string Body);

    private static (AiGateway Gateway, BudgetLedger Ledger) BuildGateway()
    {
        Func<DateTimeOffset> clock = () => DateTimeOffset.UtcNow;
        var ledger = new BudgetLedger(new InMemorySpendStore(), clock, BudgetCaps.Unlimited);
        var gateway = new AiGateway(
            TokenBucket.FromKeyHealth(
                new Mainguard.Git.Security.KeyHealth { RequestsPerMinute = 1000, TokensPerMinute = 1_000_000 },
                clock),
            ledger,
            NullAgentSupervisor.Instance,
            new InMemoryAuditLog(),
            clock);
        return (gateway, ledger);
    }

    private static async Task<Relayed> ForwardAsync(
        AiGateway gateway,
        string upstreamBody,
        string contentType,
        string? estimate,
        string requestBody = "{}",
        int maxRequestBodyBytes = GatewayForwarder.DefaultMaxRequestBodyBytes)
    {
        var credentials = new AgentGatewayCredentials();
        var token = credentials.Issue("agent-1", providerApiKey: "sk-real", upstreamHost: ModelHost);

        var forwarder = new GatewayForwarder(
            gateway,
            new HttpMessageInvoker(new ScriptedHandler(upstreamBody, contentType)),
            delay: (_, _) => Task.CompletedTask,
            maxRequestBodyBytes: maxRequestBodyBytes);

        var middleware = new ModelProxyMiddleware(
            next: _ => Task.CompletedTask,
            forwarder: forwarder,
            portMap: new NoPortMap(),
            modelHosts: new[] { ModelHost },
            credentials: credentials);

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(ModelHost);
        context.Request.Path = "/v1/messages";
        context.Request.Body = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(requestBody));
        context.Request.Headers["x-api-key"] = token;
        if (estimate is not null)
        {
            context.Request.Headers["x-mainguard-token-estimate"] = estimate;
        }

        var sink = new System.IO.MemoryStream();
        context.Response.Body = sink;

        await middleware.InvokeAsync(context);

        return new Relayed(context.Response.StatusCode, Encoding.UTF8.GetString(sink.ToArray()));
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly string _contentType;

        public ScriptedHandler(string body, string contentType)
        {
            _body = body;
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new StringContent(_body, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class NoPortMap : IAgentPortMap
    {
        public string? AgentForPort(int port) => null;
    }
}
