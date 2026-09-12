using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
    /// <summary>
    /// F29 — the largest request body the daemon will hold in memory for one agent.
    ///
    /// <para>A cap is needed because the body is buffered at all, and it is buffered because a 429 retry
    /// has to replay it. Without a ceiling a jail can push arbitrarily many multi-MB bodies into daemon
    /// memory — the daemon that supervises every OTHER agent — and nothing in the path says no. 8 MiB is
    /// far above any real completion request (a 200k-token prompt is well under 1 MiB of JSON) and far
    /// below anything that threatens the daemon.</para>
    /// </summary>
    public const int DefaultMaxRequestBodyBytes = 8 * 1024 * 1024;

    /// <summary>
    /// How far above the gateway's own default a jail's <c>x-mainguard-token-estimate</c> may raise its
    /// reservation. The header can only ever RAISE the charge (F23), which reads as harmless — but the
    /// reservation comes out of the SHARED per-minute token bucket, so an unbounded raise is a denial of
    /// service against every other agent rather than an act of honesty about this one.
    /// </summary>
    internal const int MaxEstimateMultiple = 4;

    /// <summary>The ceiling that multiple implies, saturating rather than overflowing.</summary>
    internal static int MaxEstimateRaiseFor(int defaultEstimate) =>
        (int)Math.Min(int.MaxValue, (long)Math.Max(0, defaultEstimate) * MaxEstimateMultiple);

    private readonly AiGateway _gateway;
    private readonly HttpMessageInvoker _upstream;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _defaultEstimate;
    private readonly int _maxAttempts;
    private readonly int _maxRequestBodyBytes;

    public GatewayForwarder(
        AiGateway gateway,
        HttpMessageInvoker upstream,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int defaultEstimate = 1000,
        int maxAttempts = 8,
        int maxRequestBodyBytes = DefaultMaxRequestBodyBytes)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
        _delay = delay ?? ((d, ct) => d > TimeSpan.Zero ? Task.Delay(d, ct) : Task.CompletedTask);
        _defaultEstimate = defaultEstimate;
        _maxAttempts = Math.Max(1, maxAttempts);
        _maxRequestBodyBytes = Math.Max(1, maxRequestBodyBytes);
    }

    /// <summary>
    /// The floor every request is reserved at. F23: the jail's own
    /// <c>x-mainguard-token-estimate</c> may only ever RAISE this, never lower it.
    /// </summary>
    public int DefaultEstimate => _defaultEstimate;

    /// <summary>
    /// Forwards one model request for <paramref name="agentId"/>, absorbing any upstream 429s so the
    /// returned response is always the eventual non-429 upstream response (a delayed 200). Throws
    /// <see cref="BudgetExhaustedException"/> if the agent is over budget (caller pauses, never kills).
    ///
    /// <para>This overload BUFFERS the response before returning it. It is kept for callers that want a
    /// complete <see cref="HttpResponseMessage"/> in hand; the production path uses the relay overload
    /// below, which streams.</para>
    /// </summary>
    public Task<HttpResponseMessage> ForwardAsync(
        string agentId, HttpRequestMessage request, int? estimatedTokens, CancellationToken ct) =>
        ForwardAsync(agentId, request, estimatedTokens, relay: null, ct);

    /// <summary>
    /// F23/F29 — forwards one model request and, when <paramref name="relay"/> is supplied, STREAMS the
    /// response body straight through to it while metering the real usage out of the bytes in flight.
    ///
    /// <para><b>Why this is not a performance change.</b> The old path read the whole response into a
    /// string before returning, so an SSE completion — claude-code's default — reached the agent only
    /// after the provider had finished the entire stream, and the token-by-token UX the CLI is built
    /// around did not exist through the gateway. Worse, metering was broken by the same buffering:
    /// <c>JsonDocument.Parse</c> over an <c>event:/data:</c> stream throws, usage came back null, and the
    /// request settled at the estimate — which the jail itself supplied. Streaming and honest metering
    /// are the same fix.</para>
    ///
    /// <para><paramref name="relay"/> is called ONCE, after the terminal (non-429) response's headers are
    /// in hand and before any body byte is read, and returns the stream the body is copied to. Retries
    /// happen before it is ever called, so a 429 cannot leave a half-written response on the wire.</para>
    /// </summary>
    public async Task<HttpResponseMessage> ForwardAsync(
        string agentId,
        HttpRequestMessage request,
        int? estimatedTokens,
        Func<HttpResponseMessage, CancellationToken, Task<Stream>>? relay,
        CancellationToken ct)
    {
        // F23: the estimate is a FLOOR, never a ceiling the jail gets to set. The header used to be
        // taken verbatim, so a confined agent could send `x-mainguard-token-estimate: 0`, reserve
        // nothing, and — with streaming usage unparseable — settle nothing either: the per-agent budget
        // and the shared token bucket were both bypassed by two header bytes. Raising your own
        // reservation is harmless and occasionally honest, so that direction still works — but only up
        // to MaxEstimateMultiple × the default (audit follow-up): the raise is NOT free, because
        // AcquireAsync reserves it out of the SHARED per-minute token bucket, and TokenBucket.Clamp
        // clamps a request to the whole capacity. One jail sending 60000 therefore drained the bucket
        // for a minute and stalled every other agent behind the FIFO queue — a denial of service costing
        // one header. A real request above 4× the default is settled at its real usage anyway; all the
        // ceiling costs an honest client is a slightly optimistic reservation.
        var estimate = Math.Clamp(
            estimatedTokens ?? 0, _defaultEstimate, MaxEstimateRaiseFor(_defaultEstimate));

        // Buffer the request body once so the request can be replayed across retries — bounded, so a
        // jail cannot use the retry buffer as a memory pump (F29).
        var bodyBytes = request.Content is null
            ? null
            : await ReadBoundedAsync(request.Content, _maxRequestBodyBytes, ct).ConfigureAwait(false);
        var contentHeaders = request.Content?.Headers.ToList();

        var lease = await _gateway.AcquireAsync(agentId, estimate, ct).ConfigureAwait(false);

        // MG-24: the lease now carries a provisional budget debit, so EVERY exit from here has to
        // discharge it. Settle does that on the happy path; the finally covers the ones that don't
        // return normally — the upstream send throwing, the client aborting mid-backoff, the retry loop
        // being cancelled. A lease dropped on the floor would charge the agent for a request that never
        // happened, permanently, which is a worse failure than the overshoot the reservation prevents.
        var settled = false;

        // Audit B2 — set the moment a terminal, non-error upstream response is in hand, which is the
        // moment the provider has committed to producing (and billing) a completion. Everything after
        // that point must SETTLE on the way out, never abandon: a jail running
        // `curl -N … | head -c 200` closes its socket mid-stream, the copy throws on RequestAborted, and
        // the old `finally` released the reservation with no SpendRecord at all — a complete, billed
        // completion charged at zero, repeatable at will. The same path under-counted every ordinary
        // claude-code Esc.
        var upstreamCommitted = false;
        ModelUsageSniffer? sniffer = null;
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

                sniffer = new ModelUsageSniffer(ContentTypeOf(response));

                // A 4xx/5xx is not billed by the provider, so an abort while reading one stays a
                // refund; anything the provider answers normally is work it has already done.
                upstreamCommitted = (int)response.StatusCode < 400;

                if (relay is null)
                {
                    // Terminal response: buffer it so we can read usage AND still hand it to the caller
                    // intact. Streaming callers take the branch below instead.
                    await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    sniffer.Append(body);
                }
                else
                {
                    var sink = await relay(response, ct).ConfigureAwait(false);
                    await PumpAsync(response, sink, sniffer, ct).ConfigureAwait(false);
                }

                sniffer.Complete();
                var (tokens, model) = sniffer.Result;
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
                SettleOrAbandonAfterFailure(lease, upstreamCommitted, sniffer, estimate);
            }
        }
    }

    /// <summary>
    /// Audit B2 — discharges a lease whose request did not finish normally.
    ///
    /// <para>Before the upstream committed (the send threw, the retry loop was cancelled, the client went
    /// away during backoff) nothing was produced and nothing is owed: <c>Abandon</c> refunds the bucket
    /// and drops the provisional debit, as before. After it committed — headers in hand, bytes flowing —
    /// the provider has generated and billed a completion whatever the jail then did with the socket, so
    /// the lease must SETTLE. It settles at <c>max(usage seen so far, the reservation)</c>: the sniffer's
    /// result is live per <c>data:</c> frame, so a stream cut at 80% still charges the 80% the provider
    /// actually emitted, and the floor keeps the old "no usage parsed ⇒ charge the estimate" behaviour
    /// for a stream cut before its first usage frame.</para>
    ///
    /// <para>Nothing in here may throw: this runs in a <c>finally</c> while another exception — usually
    /// the client's own cancellation — is in flight, and replacing it would turn a billing detail into a
    /// confusing 500. A settle that fails therefore falls back to the refund.</para>
    /// </summary>
    private void SettleOrAbandonAfterFailure(
        GatewayLease lease, bool upstreamCommitted, ModelUsageSniffer? sniffer, int estimate)
    {
        if (!upstreamCommitted)
        {
            _gateway.Abandon(lease);
            return;
        }

        try
        {
            sniffer?.Complete();
            var (tokens, model) = sniffer?.Result ?? (null, string.Empty);
            _gateway.Settle(lease, Math.Max(tokens ?? 0, estimate), model);
        }
        catch (Exception)
        {
            _gateway.Abandon(lease);
        }
    }

    private static string? ContentTypeOf(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType;

    /// <summary>
    /// Copies the upstream body to <paramref name="sink"/> in bounded chunks, flushing each one so an
    /// SSE event reaches the agent as it arrives, and feeding every byte to the usage sniffer on the way
    /// past. The sniffer keeps only what it needs, so a 100 MB response costs 8 KiB of buffer.
    /// </summary>
    private static async Task PumpAsync(
        HttpResponseMessage response, Stream sink, ModelUsageSniffer sniffer, CancellationToken ct)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                sniffer.Append(buffer.AsSpan(0, read));
                await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                await sink.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads at most <paramref name="limit"/> bytes; one byte more is a refusal, not a truncation —
    /// forwarding a silently truncated body would send the provider a corrupt request under the
    /// operator's real key.
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        await using var source = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var sink = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                if (sink.Length + read > limit)
                {
                    throw new GatewayRequestTooLargeException(limit);
                }

                sink.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return sink.ToArray();
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
/// F29 — the jail sent a request body larger than the gateway will hold. Typed so the middleware can
/// answer 413 rather than letting an OOM-shaped failure surface as a 500.
/// </summary>
public sealed class GatewayRequestTooLargeException : Exception
{
    public GatewayRequestTooLargeException(int limitBytes)
        : base($"Request body exceeds the gateway's {limitBytes}-byte limit.") => LimitBytes = limitBytes;

    public int LimitBytes { get; }
}

/// <summary>
/// Pulls actual token usage + the model id out of a provider response body (Anthropic
/// <c>usage.input_tokens+output_tokens</c>, OpenAI <c>usage.total_tokens</c> /
/// <c>prompt_tokens+completion_tokens</c>). Returns null tokens when the body carries no usage — the
/// caller then settles with the estimate.
///
/// <para><b>F23 — this used to be the whole metering story, and it only worked for non-streaming
/// bodies.</b> <c>JsonDocument.Parse</c> over an SSE stream (<c>event: …\ndata: {…}</c>) throws, the
/// catch returned null, and the request settled at the caller's estimate. claude-code streams by
/// default, so in practice NO confined agent was ever charged its real usage. Streaming is handled by
/// <see cref="ModelUsageSniffer"/>, which reuses the merge rules below; this type stays the
/// whole-document parser and the place the provider dialects are written down.</para>
/// </summary>
public static class ModelUsageParser
{
    public static (int? Tokens, string Model) Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, string.Empty);
        }

        // An SSE body is not a JSON document; route it to the incremental parser rather than throwing
        // it at JsonDocument and calling the resulting null "no usage".
        if (!LooksLikeJsonDocument(body))
        {
            var sniffer = new ModelUsageSniffer("text/event-stream");
            sniffer.Append(body);
            sniffer.Complete();
            return sniffer.Result;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, string.Empty);
            }

            var usage = new UsageAccumulator();
            usage.Merge(doc.RootElement);
            return usage.Result;
        }
        catch (JsonException)
        {
            return (null, string.Empty);
        }
    }

    private static bool LooksLikeJsonDocument(string body)
    {
        foreach (var c in body)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            return c is '{' or '[';
        }

        return false;
    }
}

/// <summary>
/// F23 — merges the usage numbers a provider reports, across however many fragments report them.
///
/// <para>Streaming providers report usage in PIECES: Anthropic sends input tokens in
/// <c>message_start</c> and the running output count in each <c>message_delta</c>; OpenAI sends one
/// <c>usage</c> object in a final chunk (with <c>stream_options.include_usage</c>). Taking the last
/// value seen would undercount Anthropic's input; summing every value seen would multiply-count
/// Anthropic's cumulative output. Taking the MAXIMUM of each field is correct for both, because every
/// field either arrives once or arrives as a monotonically growing running total.</para>
/// </summary>
internal sealed class UsageAccumulator
{
    private int _input;
    private int _output;
    private int _total;
    private string _model = string.Empty;

    /// <summary>The merged usage: null tokens when nothing usable was ever reported.</summary>
    public (int? Tokens, string Model) Result
    {
        get
        {
            var tokens = _total > 0 ? _total : _input + _output;
            return (tokens > 0 ? tokens : (int?)null, _model);
        }
    }

    /// <summary>
    /// Merges one JSON fragment — a whole response body, or one SSE <c>data:</c> payload. The
    /// <c>usage</c> object is looked for at the root and one level down under <c>message</c>, which is
    /// where Anthropic's <c>message_start</c> event puts both it and the model id.
    /// </summary>
    public void Merge(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        MergeModel(element);
        MergeUsage(element);

        if (element.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            MergeModel(message);
            MergeUsage(message);
        }
    }

    private void MergeModel(JsonElement element)
    {
        if (_model.Length != 0)
        {
            return;
        }

        // Anthropic/OpenAI say `model`; Gemini answers with `modelVersion` and no `model` at all, and a
        // spend row priced against an empty model id is a spend row nobody can audit.
        if ((element.TryGetProperty("model", out var m) || element.TryGetProperty("modelVersion", out m))
            && m.ValueKind == JsonValueKind.String
            && m.GetString() is { Length: > 0 } name)
        {
            _model = name;
        }
    }

    private void MergeUsage(JsonElement element)
    {
        MergeGeminiUsage(element);

        if (!element.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        _total = Math.Max(_total, Read(usage, "total_tokens"));
        // Anthropic names them input/output; OpenAI names them prompt/completion. Both dialects are read
        // because one gateway fronts both, and a field this code does not know about is worth more as a
        // zero than as an exception.
        _input = Math.Max(_input, Math.Max(Read(usage, "input_tokens"), Read(usage, "prompt_tokens")));
        _output = Math.Max(_output, Math.Max(Read(usage, "output_tokens"), Read(usage, "completion_tokens")));
    }

    /// <summary>
    /// Gemini's third dialect: a top-level <c>usageMetadata</c> object with its own field names, repeated
    /// on every streamed chunk as a running total (so the max-merge rule above is right for it too).
    ///
    /// <para>Read here rather than "when gemini-cli lands" because the confinement of gemini-cli and this
    /// parser are separate PRs: without it every confined Gemini agent settles at the flat default
    /// estimate no matter what it actually spent, which is the same silent under-metering F23 fixed for
    /// SSE.</para>
    /// </summary>
    private void MergeGeminiUsage(JsonElement element)
    {
        if (!element.TryGetProperty("usageMetadata", out var meta) || meta.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        _total = Math.Max(_total, Read(meta, "totalTokenCount"));
        _input = Math.Max(_input, Read(meta, "promptTokenCount"));
        _output = Math.Max(_output, Read(meta, "candidatesTokenCount"));
    }

    private static int Read(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var v) && v > 0
            ? v
            : 0;
}

/// <summary>
/// F23/F29 — reads token usage out of a response body <b>as it streams past</b>, in constant memory.
///
/// <para>Two modes, chosen from the response's content type and confirmed by the first non-whitespace
/// byte. An <c>application/json</c> body is accumulated up to <see cref="JsonCaptureLimit"/> and parsed
/// once at the end (usage can be anywhere in it). An <c>text/event-stream</c> body is parsed line by
/// line: each <c>data:</c> payload is merged as it completes and then discarded, so a completion of any
/// length costs one line buffer. The line buffer itself is capped — a provider that never sends a
/// newline must not be able to grow it without bound.</para>
/// </summary>
internal sealed class ModelUsageSniffer
{
    /// <summary>How much of a non-streaming body is kept for parsing. Above this the body is not a
    /// completion response, and holding more of it in daemon memory buys nothing.</summary>
    internal const int JsonCaptureLimit = 1024 * 1024;

    /// <summary>The longest single SSE line that will be buffered.</summary>
    internal const int MaxLineBytes = 256 * 1024;

    private readonly UsageAccumulator _usage = new();
    private readonly StringBuilder _line = new();
    private readonly StringBuilder? _json;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private bool _modeDecided;
    private bool _eventStream;
    private bool _overflowed;

    public ModelUsageSniffer(string? contentType)
    {
        _eventStream = contentType is not null
            && contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase);
        _modeDecided = _eventStream;
        _json = new StringBuilder();
    }

    /// <summary>The merged usage seen so far. Call <see cref="Complete"/> first for a final answer.</summary>
    public (int? Tokens, string Model) Result { get; private set; }

    public void Append(string text) => Append(Encoding.UTF8.GetBytes(text));

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        var chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(bytes.Length));
        try
        {
            var count = _decoder.GetChars(bytes, chars.AsSpan(), flush: false);
            Consume(chars.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    /// <summary>Flushes the trailing partial line / whole JSON document and publishes the final result.</summary>
    public void Complete()
    {
        if (_line.Length > 0)
        {
            EndLine();
        }

        if (!_eventStream && _json is { Length: > 0 })
        {
            TryMergeJson(_json.ToString());
        }

        Result = _usage.Result;
    }

    private void Consume(ReadOnlySpan<char> chars)
    {
        foreach (var c in chars)
        {
            if (!_modeDecided && !char.IsWhiteSpace(c))
            {
                // The content type is a claim; the first byte is evidence. A provider that labels an SSE
                // stream `application/json` (or the reverse) is metered correctly either way.
                _eventStream = c is not '{' and not '[';
                _modeDecided = true;
            }

            if (!_eventStream)
            {
                AppendJson(c);
                continue;
            }

            if (c == '\n')
            {
                EndLine();
                continue;
            }

            if (c == '\r')
            {
                continue;
            }

            if (_line.Length < MaxLineBytes)
            {
                _line.Append(c);
            }
            else
            {
                _overflowed = true;
            }
        }
    }

    private void AppendJson(char c)
    {
        if (_json is null)
        {
            return;
        }

        if (_json.Length < JsonCaptureLimit)
        {
            _json.Append(c);
        }
    }

    private void EndLine()
    {
        var line = _line.ToString();
        _line.Clear();
        var overflowed = _overflowed;
        _overflowed = false;

        // A line we could not hold in full cannot be parsed, and half a JSON object parsed as a whole
        // one is worse than no reading at all.
        if (overflowed)
        {
            return;
        }

        var trimmed = line.AsSpan().Trim();
        if (!trimmed.StartsWith("data:", StringComparison.Ordinal))
        {
            return;
        }

        var payload = trimmed[5..].Trim();
        if (payload.Length == 0 || payload.SequenceEqual("[DONE]"))
        {
            return;
        }

        TryMergeJson(payload.ToString());
    }

    private void TryMergeJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            _usage.Merge(doc.RootElement);
            Result = _usage.Result;
        }
        catch (JsonException)
        {
            // One unparseable frame in a stream is not a reason to give up on the rest of it.
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
            // F29: the response is RELAYED, not buffered. The forwarder hands us the terminal response's
            // headers, we write them, and it then copies the body straight to the client while metering
            // the usage out of the bytes going past — so an SSE completion arrives token by token
            // instead of after the provider has finished, and a multi-MB body never lands in daemon
            // memory at all.
            using var upstream = await _forwarder.ForwardAsync(
                agentId, request, estimate,
                (response, _) =>
                {
                    WriteResponseHead(context, response);
                    return Task.FromResult(context.Response.Body);
                },
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (BudgetExhaustedException)
        {
            // The agent is paused with a typed reason (never killed); the CLI receives a soft 402.
            context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
        }
        catch (GatewayRequestTooLargeException)
        {
            // F29: refused at the boundary, before the body was buffered or the lease taken.
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
    }

    private bool IsModelHost(string host) =>
        _modelHosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>Credential-bearing headers the agent may present. They are always DROPPED and replaced
    /// by the daemon-held provider key — the jail's copy is only ever a Mainguard token.</summary>
    private static readonly string[] CredentialHeaders =
    {
        "authorization", "x-api-key", "api-key", "anthropic-api-key", "openai-api-key",
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

    /// <summary>
    /// The jail's declared token estimate — advisory ONLY.
    ///
    /// <para>F23: this used to be the reservation and, whenever usage could not be parsed (i.e. every
    /// streaming response), the settlement too. A confined agent sending <c>0</c> therefore reserved
    /// nothing and was charged nothing, bypassing both its own budget and the shared token bucket.
    /// <see cref="GatewayForwarder"/> now floors whatever comes back from here at its own default, so
    /// the header can only ever RAISE the charge. A zero, a negative, or an unparseable value is
    /// discarded rather than honoured.</para>
    /// </summary>
    private static int? TryReadEstimate(HttpContext context) =>
        int.TryParse(context.Request.Headers["x-mainguard-token-estimate"].FirstOrDefault(), out var v) && v > 0
            ? v
            : null;

    /// <summary>
    /// Writes the upstream status + headers to the client, leaving the BODY to be streamed by the
    /// forwarder's pump. Called once, by the relay, before the first body byte is read.
    /// </summary>
    private static void WriteResponseHead(HttpContext context, HttpResponseMessage upstream)
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

        // Kestrel must not hold the body back to fill a buffer: an SSE event that arrives now has to
        // leave now, or "streaming through the gateway" is streaming in name only. Content-Length is
        // dropped for the same reason — the relayed length is the upstream's claim about a body we are
        // forwarding chunk by chunk, and a mismatch would truncate the response.
        context.Response.Headers.ContentLength = null;
        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
    }
}
