using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Security;

namespace Mainguard.Agents.Agents;

/// <summary>
/// One granted slice of a key's rate budget: a monotonic <paramref name="Ticket"/> (grant order,
/// used by the FIFO fairness assertions) and the <paramref name="EstimatedTokens"/> the caller
/// reserved. The lease is <b>settled with actuals</b> via <see cref="TokenBucket.Release"/> so the
/// estimate→actual difference is conserved back into the bucket.
/// </summary>
public readonly record struct BucketLease(long Ticket, int EstimatedTokens);

/// <summary>
/// P2-08 pure rate limiter: two coupled buckets per key — requests/min and tokens/min — seeded from
/// the P2-01 <see cref="KeyHealth"/> ceilings. Refill is continuous (fractional, by elapsed time);
/// one acquire consumes exactly one request permit and <c>estimatedTokens</c> token permits, so N
/// agents share one key without anyone hitting the provider's own limit. Waiters are granted
/// <b>FIFO within a priority class</b> (a strict queue: the head is served first, so no waiter
/// starves behind a stream of cheaper late arrivals).
///
/// <para><b>Injected clock (rejection trigger otherwise):</b> the class never reads the wall clock
/// inline — time enters only through the <see cref="Func{DateTimeOffset}"/> ctor argument, which is
/// what makes the burst/refill/fairness properties testable on a virtual clock. Time only advances
/// when the injected clock advances; <see cref="Pump"/> (or a <see cref="Release"/>) re-evaluates the
/// waiter queue after that.</para>
/// </summary>
public sealed class TokenBucket
{
    // Conservative fallbacks when a provider returned no rate-limit headers (KeyHealth ceilings null):
    // a light paid tier. Never zero — a zero-capacity bucket would deadlock every acquire.
    internal const int FallbackRequestsPerMinute = 60;
    internal const int FallbackTokensPerMinute = 60_000;

    /// <summary>The env var that overrides the requests/min floor.</summary>
    public const string RequestsPerMinuteVariable = "MAINGUARD_GATEWAY_RPM";

    /// <summary>The env var that overrides the tokens/min floor.</summary>
    public const string TokensPerMinuteVariable = "MAINGUARD_GATEWAY_TPM";

    /// <summary>
    /// F52 — the requests/min the bucket falls back to, now an operator setting rather than a constant.
    ///
    /// <para>These two numbers are the ceiling for the ENTIRE daemon: one bucket is shared by every
    /// agent on the key, so 60 RPM / 60k TPM is what a whole fleet gets, whatever tier the operator
    /// actually pays for. They were literals with no way to change them, so a Tier-4 key was throttled
    /// to a free-tier shape and the only symptom was agents queueing. Read once per process from the
    /// environment — a value that is absent, unparseable or non-positive keeps the conservative
    /// fallback, because the failure direction for a rate limit is "too slow", never "unbounded".</para>
    /// </summary>
    public static int DefaultRequestsPerMinute { get; } =
        ReadPositive(RequestsPerMinuteVariable, FallbackRequestsPerMinute);

    /// <summary>The tokens/min the bucket falls back to. See <see cref="DefaultRequestsPerMinute"/>.</summary>
    public static int DefaultTokensPerMinute { get; } =
        ReadPositive(TokensPerMinuteVariable, FallbackTokensPerMinute);

    internal static int ReadPositive(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out var value) && value > 0
            ? value
            : fallback;

    private readonly double _reqCapacity;
    private readonly double _tokCapacity;
    private readonly double _reqPerSecond;
    private readonly double _tokPerSecond;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _waiters = new();

    private double _reqAvailable;
    private double _tokAvailable;
    private DateTimeOffset _lastRefill;
    private long _nextTicket;

    /// <param name="requestsPerMinute">Requests/min ceiling (coerced to ≥1).</param>
    /// <param name="tokensPerMinute">Tokens/min ceiling (coerced to ≥1).</param>
    /// <param name="clock">The sole time source (injected — no inline wall-clock reads).</param>
    public TokenBucket(int requestsPerMinute, int tokensPerMinute, Func<DateTimeOffset> clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _reqCapacity = Math.Max(1, requestsPerMinute);
        _tokCapacity = Math.Max(1, tokensPerMinute);
        _reqPerSecond = _reqCapacity / 60.0;
        _tokPerSecond = _tokCapacity / 60.0;
        _reqAvailable = _reqCapacity;
        _tokAvailable = _tokCapacity;
        _lastRefill = _clock();
    }

    /// <summary>
    /// Seeds a bucket from the P2-01 key-health ceilings; missing/zero ceilings use the configured
    /// defaults (F52 — <see cref="DefaultRequestsPerMinute"/> / <see cref="DefaultTokensPerMinute"/>,
    /// overridable per deployment). <paramref name="requestsPerMinute"/> /
    /// <paramref name="tokensPerMinute"/> override both, for a caller that has its own configuration
    /// source rather than the environment.
    /// </summary>
    public static TokenBucket FromKeyHealth(
        KeyHealth? health,
        Func<DateTimeOffset> clock,
        int? requestsPerMinute = null,
        int? tokensPerMinute = null)
    {
        var rpm = requestsPerMinute is > 0 ? requestsPerMinute.Value
            : health?.RequestsPerMinute is > 0 ? health.RequestsPerMinute!.Value
            : DefaultRequestsPerMinute;
        var tpm = tokensPerMinute is > 0 ? tokensPerMinute.Value
            : health?.TokensPerMinute is > 0 ? health.TokensPerMinute!.Value
            : DefaultTokensPerMinute;
        return new TokenBucket(rpm, tpm, clock);
    }

    /// <summary>The request/min and tokens/min capacities (the burst ceiling of each bucket).</summary>
    public (double Requests, double Tokens) Capacity => (_reqCapacity, _tokCapacity);

    /// <summary>Currently available permits (post-refill) — exposed for the property tests.</summary>
    public (double Requests, double Tokens) Available
    {
        get { lock (_gate) { Refill(); return (_reqAvailable, _tokAvailable); } }
    }

    /// <summary>Number of queued waiters not yet granted.</summary>
    public int QueueDepth
    {
        get { lock (_gate) { return _waiters.Count; } }
    }

    /// <summary>
    /// Non-blocking acquire honoring FIFO: succeeds only when no waiter is queued ahead <b>and</b> both
    /// buckets have capacity. Returns false rather than jumping the queue.
    /// </summary>
    public bool TryAcquire(int estimatedTokens, out BucketLease lease)
    {
        lock (_gate)
        {
            return TryAcquireLocked(Clamp(estimatedTokens), out lease);
        }
    }

    /// <summary>
    /// FIFO acquire. Completes immediately when grantable now; otherwise enqueues a waiter served in
    /// arrival order as capacity refills (advance the clock, then <see cref="Pump"/>).
    /// </summary>
    public Task<BucketLease> AcquireAsync(int estimatedTokens, CancellationToken ct)
    {
        var tokens = Clamp(estimatedTokens);
        lock (_gate)
        {
            if (ct.IsCancellationRequested)
            {
                return Task.FromCanceled<BucketLease>(ct);
            }

            if (TryAcquireLocked(tokens, out var lease))
            {
                return Task.FromResult(lease);
            }

            var waiter = new Waiter(tokens);
            var node = _waiters.AddLast(waiter);
            waiter.Registration = ct.Register(() =>
            {
                lock (_gate)
                {
                    if (node.List is not null)
                    {
                        _waiters.Remove(node);
                    }
                }

                waiter.Completion.TrySetCanceled(ct);
            });
            return waiter.Completion.Task;
        }
    }

    /// <summary>
    /// Re-evaluates the waiter queue after the clock advanced, granting the FIFO head(s) that now fit.
    /// Returns the number granted. The gateway's pump loop calls this on a cadence.
    /// </summary>
    public int Pump()
    {
        List<Waiter>? granted = null;
        lock (_gate)
        {
            Refill();
            while (_waiters.First is { Value: var waiter })
            {
                if (_reqAvailable < 1 || _tokAvailable < waiter.Tokens)
                {
                    break; // head does not fit yet → everyone behind waits (FIFO, no starvation).
                }

                _reqAvailable -= 1;
                _tokAvailable -= waiter.Tokens;
                waiter.Lease = new BucketLease(++_nextTicket, waiter.Tokens);
                _waiters.RemoveFirst();
                waiter.Registration.Dispose();
                (granted ??= new List<Waiter>()).Add(waiter);
            }
        }

        if (granted is null)
        {
            return 0;
        }

        // Complete outside the lock so a synchronous continuation cannot re-enter the bucket under it.
        foreach (var waiter in granted)
        {
            waiter.Completion.TrySetResult(waiter.Lease);
        }

        return granted.Count;
    }

    /// <summary>
    /// Settles a lease with the actual token usage: refunds (or claws back) the estimate→actual
    /// difference so the net token permits consumed equal <paramref name="actualTokens"/>. The one
    /// request permit is already spent (a request was made) and is not refunded. Conserves tokens.
    /// </summary>
    public void Release(BucketLease lease, int actualTokens)
    {
        lock (_gate)
        {
            Refill();
            var delta = lease.EstimatedTokens - Clamp(actualTokens); // >0 over-estimated → refund
            _tokAvailable = Math.Max(-_tokCapacity, Math.Min(_tokCapacity, _tokAvailable + delta));
        }

        Pump();
    }

    private bool TryAcquireLocked(int tokens, out BucketLease lease)
    {
        Refill();
        if (_waiters.Count == 0 && _reqAvailable >= 1 && _tokAvailable >= tokens)
        {
            _reqAvailable -= 1;
            _tokAvailable -= tokens;
            lease = new BucketLease(++_nextTicket, tokens);
            return true;
        }

        lease = default;
        return false;
    }

    private void Refill()
    {
        var now = _clock();
        var elapsed = (now - _lastRefill).TotalSeconds;
        if (elapsed <= 0)
        {
            return; // clock did not advance (or a non-monotonic sample) — nothing to add.
        }

        _reqAvailable = Math.Min(_reqCapacity, _reqAvailable + (elapsed * _reqPerSecond));
        _tokAvailable = Math.Min(_tokCapacity, _tokAvailable + (elapsed * _tokPerSecond));
        _lastRefill = now;
    }

    // A single acquire can never need more than a full bucket; clamp so a huge estimate cannot deadlock.
    private int Clamp(int tokens) => Math.Max(0, Math.Min(tokens, (int)_tokCapacity));

    private sealed class Waiter
    {
        public Waiter(int tokens) => Tokens = tokens;

        public int Tokens { get; }

        public TaskCompletionSource<BucketLease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Registration { get; set; }

        public BucketLease Lease { get; set; }
    }
}
