using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Git.Audit;

/// <summary>
/// P2-15 step 3 (external anchoring): requests an RFC 3161 timestamp token over a chain head hash.
/// The token is a third party's signed statement "this hash existed at this time" — the one thing
/// that defeats the whole-chain rewrite a local attacker with file access could otherwise perform
/// consistently (the chain has no secret; anchoring is what pins it to the outside world).
/// Anchoring is BEST-EFFORT by contract: an unreachable TSA queues and retries, appending never
/// waits (edge row 4 — "anchoring is best-effort, chaining is not").
/// </summary>
public interface IRfc3161TimestampClient
{
    /// <summary>Requests a timestamp token over <paramref name="sha256Hash"/> (32 raw bytes).
    /// Returns the DER-encoded token. Throws on any transport/TSA failure — the queue retries.</summary>
    Task<byte[]> RequestTimestampTokenAsync(byte[] sha256Hash, CancellationToken ct);
}

/// <summary>
/// The real TSA client: POSTs an <c>application/timestamp-query</c> to the configured RFC 3161
/// endpoint and validates the response against the request (nonce + hash echo) before returning
/// the raw token bytes. Network-gated in tests (<c>RequiresNetwork</c>, nightly).
/// </summary>
public sealed class Rfc3161TimestampClient : IRfc3161TimestampClient, IDisposable
{
    private readonly Uri _tsaUrl;
    private readonly HttpClient _http;

    public Rfc3161TimestampClient(Uri tsaUrl, HttpClient? http = null)
    {
        _tsaUrl = tsaUrl ?? throw new ArgumentNullException(nameof(tsaUrl));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<byte[]> RequestTimestampTokenAsync(byte[] sha256Hash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sha256Hash);

        var nonce = RandomNumberGenerator.GetBytes(16);
        var request = Rfc3161TimestampRequest.CreateFromHash(
            sha256Hash, HashAlgorithmName.SHA256, nonce: nonce, requestSignerCertificates: true);

        using var content = new ByteArrayContent(request.Encode());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/timestamp-query");
        using var response = await _http.PostAsync(_tsaUrl, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        // ProcessResponse verifies the token matches THIS request (hash + nonce echo) and throws on
        // a TSA status failure — a token for some other hash can never be stored as our anchor.
        var token = request.ProcessResponse(body, out _);
        return token.AsSignedCms().Encode();
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Pure structural validation of a stored anchor token against the head hash it claims to anchor —
/// what <c>mainguardd audit verify</c> runs offline ("validates anchor tokens when present").
/// Structural = the token decodes and its message imprint IS this hash; full CA-chain trust of the
/// TSA certificate is a policy decision left to the operator's platform trust store.
/// </summary>
public static class Rfc3161AnchorValidation
{
    public static bool Validate(byte[] tokenBytes, string headHashHex, out DateTimeOffset? timestampedAt)
    {
        timestampedAt = null;
        if (tokenBytes is null || string.IsNullOrEmpty(headHashHex))
        {
            return false;
        }

        try
        {
            if (!Rfc3161TimestampToken.TryDecode(tokenBytes, out var token, out _))
            {
                return false;
            }

            var expected = Convert.FromHexString(headHashHex);
            if (!token.TokenInfo.GetMessageHash().Span.SequenceEqual(expected))
            {
                return false;
            }

            timestampedAt = token.TokenInfo.Timestamp;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>
/// The persisted anchor queue (P2-15 step 6): heads are enqueued by policy (every N records /
/// 24 h) and processed best-effort — an unreachable TSA leaves the row pending for the next sweep,
/// and the audit chain never waits on any of this.
/// </summary>
public sealed class AuditAnchorQueue
{
    /// <summary>Anchor when this many records accumulated since the last anchored head…</summary>
    public const long RecordInterval = 1000;

    /// <summary>…or when this long passed since the last anchor, whichever comes first.</summary>
    public static readonly TimeSpan TimeInterval = TimeSpan.FromHours(24);

    private readonly Func<AppDbContext> _dbFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    public AuditAnchorQueue(Func<AppDbContext> dbFactory, Func<DateTimeOffset>? clock = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Enqueues the current head when the policy says one is due (idempotent per head seq).
    /// Returns true when a new pending anchor was enqueued.</summary>
    public bool EnqueueIfDue(IChainedAuditLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var head = log.Head();
        if (head is null)
        {
            return false;
        }

        lock (_gate)
        {
            using var db = _dbFactory();
            if (db.AuditAnchors.Any(a => a.HeadSeq == head.Value.Seq))
            {
                return false; // this exact head is already queued/anchored
            }

            var last = db.AuditAnchors.OrderByDescending(a => a.HeadSeq).FirstOrDefault();
            if (last is not null)
            {
                var lastAt = DateTimeOffset.Parse(last.RequestedAtText, CultureInfo.InvariantCulture);
                var due = head.Value.Seq - last.HeadSeq >= RecordInterval
                    || _clock() - lastAt >= TimeInterval;
                if (!due)
                {
                    return false;
                }
            }

            db.AuditAnchors.Add(new Models.AuditAnchorRow
            {
                HeadSeq = head.Value.Seq,
                HeadHash = head.Value.Hash,
                RequestedAtText = _clock().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            });
            db.SaveChanges();
            return true;
        }
    }

    /// <summary>
    /// Attempts every pending anchor against the TSA. A failure leaves the row pending for the next
    /// sweep (best-effort by contract); a success stores the token. Returns (anchored, stillPending).
    /// </summary>
    public async Task<(int Anchored, int Pending)> ProcessPendingAsync(
        IRfc3161TimestampClient client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        System.Collections.Generic.List<long> pendingIds;
        lock (_gate)
        {
            using var db = _dbFactory();
            pendingIds = db.AuditAnchors.Where(a => a.Token == null).Select(a => a.Id).ToList();
        }

        var anchored = 0;
        foreach (var id in pendingIds)
        {
            ct.ThrowIfCancellationRequested();
            string headHash;
            lock (_gate)
            {
                using var db = _dbFactory();
                headHash = db.AuditAnchors.Single(a => a.Id == id).HeadHash;
            }

            byte[] token;
            try
            {
                token = await client.RequestTimestampTokenAsync(Convert.FromHexString(headHash), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue; // TSA unreachable / refused — stays queued, the log keeps appending
            }

            lock (_gate)
            {
                using var db = _dbFactory();
                var row = db.AuditAnchors.Single(a => a.Id == id);
                row.Token = token;
                row.AnchoredAtText = _clock().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                db.SaveChanges();
            }

            anchored++;
        }

        return (anchored, pendingIds.Count - anchored);
    }

    /// <summary>Validates every stored token against its recorded head hash (the offline half the
    /// verify CLI runs). Returns the ids of anchors whose token fails structural validation.</summary>
    public System.Collections.Generic.IReadOnlyList<long> ValidateStoredAnchors()
    {
        lock (_gate)
        {
            using var db = _dbFactory();
            return db.AuditAnchors
                .Where(a => a.Token != null)
                .AsEnumerable()
                .Where(a => !Rfc3161AnchorValidation.Validate(a.Token!, a.HeadHash, out _))
                .Select(a => a.Id)
                .ToList();
        }
    }
}
