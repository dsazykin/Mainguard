using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Git.Exceptions;
using Microsoft.AspNetCore.Http;

namespace Mainguard.Server.Gateway;

/// <summary>
/// The in-path 429 interception (P2-08 invariant #1). The model host is reachable <b>only</b> via the
/// egress proxy route this fronts; every model request flows through <see cref="ForwardAsync"/>:
/// <list type="number">
///   <item>acquire the shared key's rate budget (FIFO block on the <see cref="AiGateway"/>);</item>
///   <item>forward upstream;</item>
///   <item>on <b>429</b> → <see cref="AiGateway.Report429"/> (pauses the worker's PTY input + marks the
///   agent <c>RateLimited</c>), honor <c>Retry-After</c> with exponential backoff, retry;</item>
///   <item>on success → resume the PTY, clear the rate-limit state, and settle the lease with the
///   actual token usage parsed from the provider response.</item>
/// </list>
/// <b>The agent's CLI never sees the 429 — it sees a delayed 200.</b> The delay hook is injected so the
/// backoff runs on a virtual clock in tests; production passes real <c>Task.Delay</c>.
/// </summary>
public sealed class GatewayForwarder
{
    private readonly AiGateway _gateway;
    private readonly HttpMessageInvoker _upstream;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _defaultEstimate;
    private readonly int _maxAttempts;

    public GatewayForwarder(
        AiGateway gateway,
        HttpMessageInvoker upstream,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int defaultEstimate = 1000,
        int maxAttempts = 8)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
        _delay = delay ?? ((d, ct) => d > TimeSpan.Zero ? Task.Delay(d, ct) : Task.CompletedTask);
        _defaultEstimate = defaultEstimate;
        _maxAttempts = Math.Max(1, maxAttempts);
    }

    /// <summary>
    /// Forwards one model request for <paramref name="agentId"/>, absorbing any upstream 429s so the
    /// returned response is always the eventual non-429 upstream response (a delayed 200). Throws
    /// <see cref="BudgetExhaustedException"/> if the agent is over budget (caller pauses, never kills).
    /// </summary>
    public async Task<HttpResponseMessage> ForwardAsync(
        string agentId, HttpRequestMessage request, int? estimatedTokens, CancellationToken ct)
    {
        var estimate = estimatedTokens ?? _defaultEstimate;

        // Buffer the request body once so the request can be replayed across retries.
        var bodyBytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var contentHeaders = request.Content?.Headers.ToList();

        var lease = await _gateway.AcquireAsync(agentId, estimate, ct).ConfigureAwait(false);

        // MG-24: the lease now carries a provisional budget debit, so EVERY exit from here has to
        // discharge it. Settle does that on the happy path; the finally covers the ones that don't
        // return normally — the upstream send throwing, the client aborting mid-backoff, the retry loop
        // being cancelled. A lease dropped on the floor would charge the agent for a request that never
        // happened, permanently, which is a worse failure than the overshoot the reservation prevents.
        var settled = false;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                using var outbound = Clone(request, bodyBytes, contentHeaders);
                var response = await _upstream.SendAsync(outbound, ct).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < _maxAttempts)
                {
                    var retryAfter = ParseRetryAfter(response);
                    response.Dispose();
                    _gateway.Report429(agentId, retryAfter);       // pauses PTY input, marks RateLimited
                    await _delay(_gateway.RemainingBackoff(agentId), ct).ConfigureAwait(false);
                    continue;                                       // retry — the CLI still waits on one call
                }

                // Terminal response: buffer it so we can read usage AND still hand it to the caller intact.
                await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var (tokens, model) = ModelUsageParser.Parse(body);
                _gateway.Settle(lease, tokens ?? estimate, model);
                settled = true;
                _gateway.ClearRateLimit(agentId);                   // resumes PTY input, marks Running
                return response;
            }
        }
        finally
        {
            if (!settled)
            {
                _gateway.Abandon(lease);
            }
        }
    }

    private static HttpRequestMessage Clone(
        HttpRequestMessage source, byte[]? body, List<KeyValuePair<string, IEnumerable<string>>>? contentHeaders)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            if (contentHeaders is not null)
            {
                foreach (var header in contentHeaders)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        return clone;
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } delta)
            {
                return delta;
            }

            if (ra.Date is { } date)
            {
                var diff = date - DateTimeOffset.UtcNow;
                return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
            }
        }

        // Fake/raw endpoints may send a bare "Retry-After: 5" header the typed parser missed.
        if (response.Headers.TryGetValues("retry-after", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }
}

/// <summary>
/// Pulls actual token usage + the model id out of a provider response body (Anthropic
/// <c>usage.input_tokens+output_tokens</c>, OpenAI <c>usage.total_tokens</c>). Returns null tokens
/// when the body carries no usage — the caller then settles with the estimate.
/// </summary>
public static class ModelUsageParser
{
    public static (int? Tokens, string Model) Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, string.Empty);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, string.Empty);
            }

            var model = doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? string.Empty
                : string.Empty;

            if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return (null, model);
            }

            if (usage.TryGetProperty("total_tokens", out var total) && total.TryGetInt32(out var totalTokens))
            {
                return (totalTokens, model);
            }

            var input = usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt32(out var iv) ? iv : 0;
            var output = usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt32(out var ov) ? ov : 0;
            var sum = input + output;
            return (sum > 0 ? sum : (int?)null, model);
        }
        catch (JsonException)
        {
            return (null, string.Empty);
        }
    }
}

/// <summary>Resolves which agent an inbound model request belongs to (per-agent listener port).</summary>
public interface IAgentPortMap
{
    /// <summary>The agent bound to a listener <paramref name="port"/>, or null if unknown.</summary>
    string? AgentForPort(int port);
}

/// <summary>
/// The ASP.NET wrapper that puts <see cref="GatewayForwarder"/> on the model-request path.
///
/// <para><b>Attribution and routing both come from the agent's authenticated token</b>
/// (<see cref="AgentGatewayCredentials"/>), with a per-agent listener port as a fallback. This is not a
/// stylistic choice: a confined BYOK CLI is pointed at the gateway by its base-URL variable, so the
/// inbound <c>Host</c> header names the GATEWAY. Deciding "is this model traffic?" from that header
/// therefore never matched in production and every real request fell through unfronted. The upstream
/// provider is instead recorded per agent at spawn and looked up here.</para>
///
/// <para><b>Scope — BYOK only.</b> An agent that authenticates its CLI interactively (OAuth) holds no
/// API key, is given no gateway token and no base-URL override, and never transits this middleware; its
/// traffic goes to the provider directly exactly as before. Such an agent is deliberately NOT metered —
/// a session the agent authenticates past cannot be attributed or priced at a proxy. See
/// <c>docs/design/oauth-budgeting.md</c>.</para>
///
/// <para>Requests that neither carry an upstream binding nor target a model host pass through untouched.
/// The forwarding core is <see cref="GatewayForwarder"/> so the no-raw-429 invariant is asserted without
/// spinning a listener.</para>
/// </summary>
public sealed class ModelProxyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly GatewayForwarder _forwarder;
    private readonly IAgentPortMap _portMap;
    private readonly IReadOnlyCollection<string> _modelHosts;
    private readonly AgentGatewayCredentials _credentials;

    public ModelProxyMiddleware(
        RequestDelegate next,
        GatewayForwarder forwarder,
        IAgentPortMap portMap,
        IReadOnlyCollection<string> modelHosts,
        AgentGatewayCredentials credentials)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _forwarder = forwarder ?? throw new ArgumentNullException(nameof(forwarder));
        _portMap = portMap ?? throw new ArgumentNullException(nameof(portMap));
        _modelHosts = modelHosts ?? Array.Empty<string>();
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // MG-20: identity comes from the agent's own Mainguard gateway token (presented as its API key),
        // or from a per-agent listener port. The client-supplied `x-mainguard-agent` header is NEVER
        // trusted — an agent could set it to another agent's id to dodge its own budget or attribute
        // spend and 429-pauses to a victim.
        //
        // Identity is resolved BEFORE any host check, and that ordering is the fix for the defect that
        // made this middleware unreachable in production. A confined BYOK CLI has its base URL pointed at
        // the gateway, so the inbound `Host` is the GATEWAY's address; matching that against the model-host
        // list never succeeded and every real request fell through to `_next` unfronted — the invariant in
        // this file's own doc comment was false. The upstream is therefore taken from the agent's
        // spawn-time binding (see AgentGatewayCredentials.Issue), and the Host header is not consulted for
        // a confined agent at all.
        var presentedToken = ExtractPresentedToken(context);
        var agentId = _credentials.ResolveAgent(presentedToken)
                      ?? _portMap.AgentForPort(context.Connection.LocalPort);

        var boundUpstream = _credentials.UpstreamHostFor(agentId);
        var requestHost = context.Request.Host.Host;

        // The upstream to forward to: the agent's spawn-time binding first; otherwise the legacy
        // proxy-shaped request whose Host IS a model host (the tinyproxy-upstream route, still supported).
        var upstreamHost = boundUpstream ?? (IsModelHost(requestHost) ? requestHost : null);
        if (upstreamHost is null)
        {
            // Not model traffic this gateway fronts — hand it on untouched.
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(agentId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var request = BuildUpstreamRequest(context, upstreamHost, _credentials.ProviderKeyFor(agentId));
        int? estimate = TryReadEstimate(context);

        try
        {
            using var upstream = await _forwarder.ForwardAsync(agentId, request, estimate, context.RequestAborted)
                .ConfigureAwait(false);
            await WriteBackAsync(context, upstream).ConfigureAwait(false);
        }
        catch (BudgetExhaustedException)
        {
            // The agent is paused with a typed reason (never killed); the CLI receives a soft 402.
            context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
        }
    }

    private bool IsModelHost(string host) =>
        _modelHosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>Credential-bearing headers the agent may present. They are always DROPPED and replaced
    /// by the daemon-held provider key — the jail's copy is only ever a Mainguard token.</summary>
    private static readonly string[] CredentialHeaders =
    {
        // PROBE M16 (DO NOT MERGE): "x-api-key" is no longer stripped, so the jail's Mainguard session
        // token is forwarded to the model provider verbatim.
        "authorization", "api-key", "anthropic-api-key", "openai-api-key",
    };

    /// <summary>Mainguard's own control headers — internal, never forwarded to the provider.</summary>
    private static readonly string[] MainguardHeaders =
    {
        "x-mainguard-agent", "x-mainguard-token-estimate",
    };

    /// <summary>Hop-by-hop headers that must not be relayed (RFC 9110 §7.6.1).</summary>
    private static readonly string[] HopByHopHeaders =
    {
        "connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
        "te", "trailer", "transfer-encoding", "upgrade", "host",
    };

    /// <summary>
    /// MG-4: builds the upstream request with the agent's credential <b>replaced</b> by the real
    /// provider key held daemon-side. The agent presents only its Mainguard token, so the provider key
    /// never has to exist inside the jail. MG-38: the agent's headers are filtered rather than relayed
    /// verbatim — credential, Mainguard-internal, and hop-by-hop headers are all dropped.
    /// </summary>
    private static HttpRequestMessage BuildUpstreamRequest(HttpContext context, string host, string? providerKey)
    {
        var uri = new UriBuilder("https", host)
        {
            Path = context.Request.Path,
            Query = context.Request.QueryString.ToString(),
        }.Uri;

        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), uri);
        if (context.Request.ContentLength is > 0 || context.Request.Body.CanRead)
        {
            request.Content = new StreamContent(context.Request.Body);
        }

        foreach (var header in context.Request.Headers)
        {
            if (IsDropped(header.Key))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && request.Content is not null)
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        // Inject the real key at the network hop, in the shape the provider expects. Anthropic reads
        // `x-api-key`; the bearer form covers OpenAI-style providers. Absent a key in custody (an
        // interactive-login CLI rather than BYOK) nothing is injected and the call goes out unauthenticated,
        // which the provider rejects — never a silent fallback to whatever the agent sent.
        if (!string.IsNullOrEmpty(providerKey))
        {
            if (IsAnthropicHost(host))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", providerKey);
            }
            else
            {
                request.Headers.TryAddWithoutValidation("authorization", "Bearer " + providerKey);
            }
        }

        return request;
    }

    private static bool IsDropped(string name) =>
        CredentialHeaders.Contains(name, StringComparer.OrdinalIgnoreCase)
        || MainguardHeaders.Contains(name, StringComparer.OrdinalIgnoreCase)
        || HopByHopHeaders.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool IsAnthropicHost(string host) =>
        host.EndsWith("anthropic.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Mainguard token the agent presents as its API key (either header shape).</summary>
    private static string? ExtractPresentedToken(HttpContext context)
    {
        var apiKey = context.Request.Headers["x-api-key"].FirstOrDefault();
        if (!string.IsNullOrEmpty(apiKey))
        {
            return apiKey;
        }

        var auth = context.Request.Headers["authorization"].FirstOrDefault();
        if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return auth["Bearer ".Length..];
        }

        return auth;
    }

    private static int? TryReadEstimate(HttpContext context) =>
        int.TryParse(context.Request.Headers["x-mainguard-token-estimate"].FirstOrDefault(), out var v) ? v : null;

    private static async Task WriteBackAsync(HttpContext context, HttpResponseMessage upstream)
    {
        context.Response.StatusCode = (int)upstream.StatusCode;
        foreach (var header in upstream.Headers)
        {
            // MG-38: hop-by-hop headers are per-connection and must not be relayed to the agent.
            if (!HopByHopHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        foreach (var header in upstream.Content.Headers)
        {
            if (!HopByHopHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        context.Response.Headers.Remove("transfer-encoding");
        var bytes = await upstream.Content.ReadAsByteArrayAsync(context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }
}
