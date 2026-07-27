using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;

namespace Mainguard.Git.Hosting;

/// <summary>
/// One audited GitHub REST v3 transport, shared by the GitHub pull-request (T-23) and issue (T-24)
/// providers. Centralizing it means the token-security-critical send path lives in exactly <b>one</b>
/// place (a second copy is a rejection trigger) and every host error is mapped to a typed,
/// token-scrubbed exception identically for both features.
///
/// <para>SECURITY (G-4): the token is written <b>only</b> to the per-request
/// <c>Authorization: Bearer</c> header — never a URL, argv, log, or exception message. Host error text
/// folded into a thrown exception is first scrubbed of the token via <see cref="Redact"/>. The
/// <see cref="HttpClient"/> is shared/injected (never a per-call <c>new</c> — socket exhaustion), so
/// headers are set per-request rather than on <c>DefaultRequestHeaders</c>.</para>
/// </summary>
internal sealed class GitHubApiClient
{
    private readonly HttpClient _http;

    /// <summary>API root (github.com → https://api.github.com; overridable for GHE/tests).</summary>
    public string ApiBase { get; }

    public GitHubApiClient(HttpClient http, string apiBase = "https://api.github.com")
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ApiBase = apiBase.TrimEnd('/');
    }

    /// <summary>
    /// Sends a request with the token in the <c>Authorization</c> header ONLY, returns the success body,
    /// and turns any non-success/host/network failure into a typed, token-scrubbed exception.
    /// </summary>
    public async Task<string> SendAsync(HttpMethod method, string url, string token, string? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        // Token lives here and nowhere else (G-4).
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Mainguard", "1.0"));
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine user cancellation — let it propagate untyped
        }
        catch (Exception ex)
        {
            // Network down / DNS / TLS: typed, never carrying the token. The HostUnreachable flag is
            // additive — the type and the message are unchanged — and only lets a caller tell "we never
            // reached the host" from "the host said no", which need opposite advice (P2-12 merge).
            throw new GitOperationException($"Could not reach GitHub: {Redact(ex.Message, token)}")
            {
                HostUnreachable = true,
            };
        }

        var content = response.Content is null ? "" : await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
            return content;

        throw ToTypedError(response.StatusCode, content, token);
    }

    // Maps a GitHub error response to the right typed exception, scrubbing the token from any host text.
    // Kept host-error-agnostic (no PR- or issue-specific phrasing) so the host's own message surfaces —
    // e.g. a create conflict's "already exists" or a 422 "could not add label" reaches the user verbatim.
    private static Exception ToTypedError(HttpStatusCode status, string body, string token)
    {
        var message = Redact(ExtractMessage(body), token);

        if (status == HttpStatusCode.Unauthorized)
            return new AuthenticationRequiredException(
                $"GitHub rejected the stored token: {message}", "github.com");

        // Both branches below now carry the status alongside the identical message, so a caller can branch
        // on 403/405/409 structurally instead of substring-matching the host's prose. The rate-limit branch
        // keeps its distinct wording — ExternalPrIntake's backoff recognizes it by message, unaffected.
        if (status == HttpStatusCode.Forbidden &&
            message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return new GitOperationException($"GitHub API rate limit reached: {message}")
            {
                HostStatusCode = (int)status,
            };

        return new GitOperationException($"GitHub request failed ({(int)status}): {message}")
        {
            HostStatusCode = (int)status,
        };
    }

    // Pulls GitHub's human-readable text out of an error body: the top-level "message" plus any nested
    // "errors[].message" (where a create-conflict's "already exists" / a bad-label detail actually lives).
    private static string ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "no response body";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return body;

            var parts = new List<string>();
            if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                parts.Add(m.GetString() ?? "");

            if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
            {
                foreach (var err in errs.EnumerateArray())
                    if (err.ValueKind == JsonValueKind.Object &&
                        err.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String)
                        parts.Add(em.GetString() ?? "");
            }

            var joined = string.Join(" — ", parts.Where(p => p.Length > 0));
            return joined.Length > 0 ? joined : body;
        }
        catch (JsonException) { /* not JSON — fall through to the raw body */ }
        return body;
    }

    /// <summary>
    /// Scrubs the token from any host/error text so it can never leak into an exception (G-4).
    /// Delegates to the single sanctioned scrubber in <see cref="Http.RedactionExtensions"/> —
    /// this stays as a thin pass-through so existing call sites (e.g. the GitHub PR provider) don't churn.
    /// </summary>
    public static string Redact(string text, string token) => Http.RedactionExtensions.Redact(text, token);

    /// <summary>Path-segment escape so an owner/repo can never inject query/path syntax into the URL.</summary>
    public static string Esc(string segment) => Uri.EscapeDataString(segment);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
}
