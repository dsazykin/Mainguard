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
        var result = await ForwardAsync(gateway, "{\"ok\":true}", "application/json", estimate: "3000");

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(3000, gateway.GetSnapshot().Agents.Single().Tokens);
    }

    /// <summary>
    /// Audit follow-up — the raise-only header is not harmless without a ceiling. The reservation is
    /// taken from the SHARED per-minute token bucket and <c>TokenBucket.Clamp</c> clamps a request to the
    /// whole capacity, so one jail sending <c>x-mainguard-token-estimate: 60000</c> drained the bucket
    /// and stalled every other agent behind the FIFO queue — a denial of service costing one header.
    /// </summary>
    [Fact]
    public async Task ClientEstimate_CannotRaiseWithoutLimit()
    {
        var (gateway, _) = BuildGateway();
        var result = await ForwardAsync(gateway, "{\"ok\":true}", "application/json", estimate: "60000");

        Assert.Equal(200, result.StatusCode);

        // Capped at 4× the gateway's own default (1000), not the 60000 the jail asked for.
        Assert.Equal(4000, gateway.GetSnapshot().Agents.Single().Tokens);
        Assert.Equal(4000, GatewayForwarder.MaxEstimateRaiseFor(1000));
    }

    /// <summary>
    /// Gemini's dialect: a top-level <c>usageMetadata</c> repeated per chunk as a running total, and
    /// <c>modelVersion</c> instead of <c>model</c>. Unread, every confined gemini-cli agent settles at
    /// the flat default estimate no matter what it spent.
    /// </summary>
    [Fact]
    public void GeminiStream_UsageMetadata_IsParsed()
    {
        var body =
            "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"hi\"}]}}],\"modelVersion\":\"gemini-2.5-pro\","
            + "\"usageMetadata\":{\"promptTokenCount\":900,\"candidatesTokenCount\":12,\"totalTokenCount\":912}}\n\n"
            + "data: {\"candidates\":[{\"finishReason\":\"STOP\"}],\"modelVersion\":\"gemini-2.5-pro\","
            + "\"usageMetadata\":{\"promptTokenCount\":900,\"candidatesTokenCount\":410,\"totalTokenCount\":1310}}\n\n";

        var (tokens, model) = ModelUsageParser.Parse(body);

        Assert.Equal(1310, tokens);
        Assert.Equal("gemini-2.5-pro", model);
    }

    [Fact]
    public void GeminiNonStreaming_UsageMetadata_IsParsed()
    {
        var (tokens, model) = ModelUsageParser.Parse(
            "{\"modelVersion\":\"gemini-2.5-flash\",\"usageMetadata\":{\"promptTokenCount\":40,"
            + "\"candidatesTokenCount\":60}}");

        Assert.Equal(100, tokens);
        Assert.Equal("gemini-2.5-flash", model);
    }

    // ---- Audit B2: an abort mid-stream is not a free completion ------------------------------------

    /// <summary>
    /// B2 — the zero-charge path. A jail runs <c>curl -N … | head -c 200</c>: the provider streams (and
    /// bills) the completion, the jail closes its socket before <c>message_stop</c>, the copy to the
    /// client throws on <c>RequestAborted</c> — and the old <c>finally</c> ABANDONED the lease, releasing
    /// the reservation with no <c>SpendRecord</c> at all. Repeatable at will, and it also under-counted
    /// every ordinary claude-code Esc. Once the upstream has committed, an exception must SETTLE.
    /// </summary>
    [Fact]
    public async Task ClientAbortMidStream_SettlesWhatTheProviderAlreadyProduced()
    {
        var (gateway, ledger) = BuildGateway();
        var forwarder = new GatewayForwarder(
            gateway,
            new HttpMessageInvoker(new ChunkedHandler(
                new[]
                {
                    // Usage the provider has already reported when the client goes away.
                    "event: message_start\n"
                    + "data: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-sonnet-4-5\","
                    + "\"usage\":{\"input_tokens\":1200,\"output_tokens\":1}}}\n\n",
                    "event: content_block_delta\ndata: {\"type\":\"content_block_delta\"}\n\n",
                    // Never reaches the client — the socket is gone by now.
                    "event: message_delta\ndata: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":340}}\n\n",
                },
                "text/event-stream")),
            delay: (_, _) => Task.CompletedTask);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://" + ModelHost + "/v1/messages")
        {
            Content = new StringContent("{}", Encoding.UTF8),
        };

        var aborting = new AbortingStream(throwOnWrite: 2);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => forwarder.ForwardAsync(
            "agent-1", request, estimatedTokens: null,
            relay: (_, _) => Task.FromResult<System.IO.Stream>(aborting),
            CancellationToken.None));

        // 1200 input + 1 output — what the stream had actually reported when the jail hung up. Not zero,
        // and not the flat estimate either.
        Assert.Equal(1201, ledger.GetTotals("agent-1").Tokens);
    }

    /// <summary>
    /// The floor still applies to an abort: a stream cut before its first usage frame is charged the
    /// reservation, never nothing.
    /// </summary>
    [Fact]
    public async Task ClientAbortBeforeAnyUsageFrame_StillChargesTheReservation()
    {
        var (gateway, ledger) = BuildGateway();
        var forwarder = new GatewayForwarder(
            gateway,
            new HttpMessageInvoker(new ChunkedHandler(
                new[] { "event: ping\ndata: {\"type\":\"ping\"}\n\n", "event: ping\ndata: {}\n\n" },
                "text/event-stream")),
            delay: (_, _) => Task.CompletedTask);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://" + ModelHost + "/v1/messages")
        {
            Content = new StringContent("{}", Encoding.UTF8),
        };

        var aborting = new AbortingStream(throwOnWrite: 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => forwarder.ForwardAsync(
            "agent-1", request, estimatedTokens: null,
            relay: (_, _) => Task.FromResult<System.IO.Stream>(aborting),
            CancellationToken.None));

        Assert.Equal(1000, ledger.GetTotals("agent-1").Tokens);
    }

    /// <summary>
    /// The carve-out, so "settle on the way out" does not become "charge for everything": a failure
    /// BEFORE the upstream committed — the send itself throwing — produced nothing to bill, and still
    /// refunds. Same for an abort while reading a 4xx/5xx, which providers do not bill.
    /// </summary>
    [Fact]
    public async Task UpstreamFailure_BeforeAnyResponse_StillRefunds()
    {
        var (gateway, ledger) = BuildGateway();
        var forwarder = new GatewayForwarder(
            gateway, new HttpMessageInvoker(new ThrowingHandler()), delay: (_, _) => Task.CompletedTask);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://" + ModelHost + "/v1/messages")
        {
            Content = new StringContent("{}", Encoding.UTF8),
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => forwarder.ForwardAsync(
            "agent-1", request, estimatedTokens: null,
            relay: (_, _) => Task.FromResult<System.IO.Stream>(new System.IO.MemoryStream()),
            CancellationToken.None));

        Assert.Equal(0, ledger.GetTotals("agent-1").Tokens);
    }

    [Fact]
    public async Task AbortWhileReadingAnErrorResponse_Refunds()
    {
        var (gateway, ledger) = BuildGateway();
        var forwarder = new GatewayForwarder(
            gateway,
            new HttpMessageInvoker(new ChunkedHandler(
                new[] { "{\"error\":" , "\"overloaded\"}" }, "application/json", HttpStatusCode.InternalServerError)),
            delay: (_, _) => Task.CompletedTask);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://" + ModelHost + "/v1/messages")
        {
            Content = new StringContent("{}", Encoding.UTF8),
        };

        var aborting = new AbortingStream(throwOnWrite: 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => forwarder.ForwardAsync(
            "agent-1", request, estimatedTokens: null,
            relay: (_, _) => Task.FromResult<System.IO.Stream>(aborting),
            CancellationToken.None));

        Assert.Equal(0, ledger.GetTotals("agent-1").Tokens);
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

    /// <summary>An upstream that hands the body over one chunk per read, so a test can cut the client off
    /// BETWEEN frames — the shape of the abort the forwarder has to charge for.</summary>
    private sealed class ChunkedHandler : HttpMessageHandler
    {
        private readonly string[] _chunks;
        private readonly string _contentType;
        private readonly HttpStatusCode _status;

        public ChunkedHandler(string[] chunks, string contentType, HttpStatusCode status = HttpStatusCode.OK)
        {
            _chunks = chunks;
            _contentType = contentType;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new StreamContent(new ChunkStream(_chunks));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);
            return Task.FromResult(new HttpResponseMessage(_status) { Content = content });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("upstream is unreachable"));
    }

    /// <summary>A stream that yields exactly one scripted chunk per <c>Read</c>.</summary>
    private sealed class ChunkStream : System.IO.Stream
    {
        private readonly Queue<byte[]> _chunks;

        public ChunkStream(IEnumerable<string> chunks) =>
            _chunks = new Queue<byte[]>(chunks.Select(c => Encoding.UTF8.GetBytes(c)));

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_chunks.Count == 0)
            {
                return 0;
            }

            var chunk = _chunks.Dequeue();
            chunk.CopyTo(buffer, offset);
            return chunk.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// The client's response body after the jail hangs up: Kestrel's stream throws on the write that
    /// follows an aborted connection. <c>throwOnWrite</c> is 1-based — which write is the one that dies.
    /// </summary>
    private sealed class AbortingStream : System.IO.Stream
    {
        private readonly int _throwOnWrite;
        private int _writes;

        public AbortingStream(int throwOnWrite) => _throwOnWrite = throwOnWrite;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_writes >= _throwOnWrite)
            {
                throw new OperationCanceledException("the client aborted the request");
            }

            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
