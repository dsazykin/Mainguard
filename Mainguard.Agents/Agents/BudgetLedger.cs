using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Git.Models;

namespace Mainguard.Agents.Agents;

/// <summary>Per-agent and per-day token + cost caps. A zero or negative cap means "unlimited".</summary>
/// <param name="PerAgentTokenCap">Max lifetime tokens for a single agent (0 = unlimited).</param>
/// <param name="PerAgentUsdMicrosCap">Max lifetime cost (USD micros) for a single agent (0 = unlimited).</param>
/// <param name="PerDayTokenCap">Max tokens across all agents in a UTC day (0 = unlimited).</param>
/// <param name="PerDayUsdMicrosCap">Max cost (USD micros) across all agents in a UTC day (0 = unlimited).</param>
public sealed record BudgetCaps(
    long PerAgentTokenCap,
    long PerAgentUsdMicrosCap,
    long PerDayTokenCap,
    long PerDayUsdMicrosCap)
{
    /// <summary>No caps — every request is admitted (the default until the user sets budgets).</summary>
    public static BudgetCaps Unlimited { get; } = new(0, 0, 0, 0);
}

/// <summary>The accumulated spend used by snapshots and the cost-per-merged-change join (P2-10).</summary>
public readonly record struct SpendTotals(long Tokens, long UsdMicros);

/// <summary>
/// The verdict of <see cref="BudgetLedger.TryReserve"/> (MG-24). The two refusals are deliberately
/// distinct: settled spend at the cap is a <b>durable</b> condition that pauses the worker, while a
/// refusal caused only by other requests still in flight is <b>transient</b> back-pressure — the
/// request is still refused (that is the cap), but pausing the worker for spend that has not happened
/// yet, and may never settle, would be a lie the user cannot clear.
/// </summary>
public enum BudgetAdmission
{
    /// <summary>There is room: the provisional debit is held until settle/release.</summary>
    Granted,

    /// <summary>Settled spend alone is at/over a cap — pause the agent (P2-08 typed pause).</summary>
    Exhausted,

    /// <summary>Only the in-flight reservations push the agent over — refuse, but do not pause.</summary>
    InFlight,
}

/// <summary>
/// The static per-model price table (documented in code). Prices are USD micro-dollars per 1M tokens
/// — a blended input/output figure adequate for budget accounting, not billing. Unknown models fall
/// back to a conservative default so an unlisted model still costs something.
/// </summary>
public static class ModelPriceTable
{
    // USD micros per 1,000,000 tokens (i.e. $3.00 → 3_000_000). Blended input+output.
    private static readonly IReadOnlyDictionary<string, long> Prices = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-3-5-haiku"] = 1_000_000,     // ~$1.00 / Mtok blended
        ["claude-3-5-sonnet"] = 6_000_000,    // ~$6.00 / Mtok blended
        ["claude-3-7-sonnet"] = 6_000_000,
        ["claude-3-opus"] = 30_000_000,       // ~$30.00 / Mtok blended
        ["gpt-4o"] = 5_000_000,
        ["gpt-4o-mini"] = 400_000,
        ["gpt-4-turbo"] = 20_000_000,
    };

    /// <summary>Conservative fallback for an unlisted model ($5.00 / Mtok).</summary>
    public const long DefaultUsdMicrosPerMillionTokens = 5_000_000;

    /// <summary>USD micro-dollars for <paramref name="tokens"/> of <paramref name="model"/> (prefix match).</summary>
    public static long CostMicros(string model, long tokens)
    {
        var rate = RateFor(model);
        // micros = tokens * rate / 1_000_000, computed in 128-bit to avoid overflow on large runs.
        return (long)((System.Numerics.BigInteger)tokens * rate / 1_000_000);
    }

    private static long RateFor(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return DefaultUsdMicrosPerMillionTokens;
        }

        if (Prices.TryGetValue(model, out var exact))
        {
            return exact;
        }

        // Longest-prefix match so "claude-3-5-sonnet-20241022" maps to "claude-3-5-sonnet".
        var match = Prices.Keys
            .Where(k => model.StartsWith(k, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => k.Length)
            .FirstOrDefault();
        return match is not null ? Prices[match] : DefaultUsdMicrosPerMillionTokens;
    }
}

/// <summary>The persistence seam for spend rows — SQLite in the daemon, in-memory in tests.</summary>
public interface ISpendStore
{
    /// <summary>Appends one settled spend row (assigns its <see cref="SpendRecord.Id"/>).</summary>
    void Append(SpendRecord record);

    /// <summary>All rows, insertion order.</summary>
    IReadOnlyList<SpendRecord> All();
}

/// <summary>
/// P2-08 budget ledger. Records settled spend (tokens + cost) per agent, enforces per-agent and
/// per-day caps, and raises <see cref="SpendRecorded"/> so the daemon can stream rows over
/// <c>GatewayService.StreamSpend</c>. Budget exhaustion is a <b>typed pause</b> signal — the caller
/// pauses the agent, it is never killed (rejection trigger). Rows carry <c>agentId</c> and
/// <see cref="GetSpendSince"/> exposes the cost-per-merged-change join for P2-10.
///
/// <para><b>MG-24 — reservations close the check-then-act race.</b> Settled spend is only known after
/// the upstream round-trip, so a ledger that is <i>only</i> consulted with <see cref="IsExhausted"/>
/// before forwarding lets N concurrent requests for the same agent all read "under cap" and blow
/// straight through it; the overshoot was bounded by nothing but the shared token bucket. So a request
/// now <see cref="TryReserve"/>s its estimate up front — check and provisional debit inside ONE lock —
/// and the (N+1)th request sees the spend that is already in flight. Every reservation MUST reach
/// <see cref="SettleReservation"/> (the request completed) or <see cref="ReleaseReservation"/> (it did
/// not); a leaked reservation would permanently exhaust an agent's budget with spend that never
/// happened, which is a strictly worse bug than the overshoot it prevents.</para>
/// </summary>
public sealed class BudgetLedger
{
    private readonly ISpendStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    // Provisional debits for requests that have been admitted but not yet settled (MG-24). Keyed by an
    // opaque monotonic id handed back to the caller, which is the only way to discharge one.
    private readonly Dictionary<long, Reservation> _reservations = new();
    private long _nextReservationId;

    private BudgetCaps _caps;

    public BudgetLedger(ISpendStore store, Func<DateTimeOffset> clock, BudgetCaps? caps = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _caps = caps ?? BudgetCaps.Unlimited;
    }

    /// <summary>Raised (outside the lock) for each appended row so the daemon can stream it live.</summary>
    public event Action<SpendRecord>? SpendRecorded;

    /// <summary>The current caps (get/set persisted by the gRPC budgets endpoints).</summary>
    public BudgetCaps Caps
    {
        get { lock (_gate) { return _caps; } }
        set { lock (_gate) { _caps = value ?? BudgetCaps.Unlimited; } }
    }

    /// <summary>
    /// True when <paramref name="agentId"/> is at/over any cap and no further request may be admitted.
    /// The <paramref name="reason"/> names the cap and its value (honest, user-facing). Counts settled
    /// spend <b>plus</b> the reservations still in flight (MG-24), so it answers the only question the
    /// caller actually has: "would one more request overshoot?".
    /// </summary>
    public bool IsExhausted(string agentId, out string reason)
    {
        lock (_gate)
        {
            return IsOverCapLocked(agentId, includeReservations: true, out reason);
        }
    }

    /// <summary>
    /// MG-24: atomically re-checks the caps and, when there is room, holds a <b>provisional debit</b>
    /// for one in-flight request. The check and the debit share one lock, so concurrent callers for the
    /// same agent serialize and only as many as fit under the cap are admitted.
    ///
    /// <para>On <see cref="BudgetAdmission.Granted"/> the caller owns <paramref name="reservationId"/>
    /// and MUST discharge it exactly once — <see cref="SettleReservation"/> on completion, or
    /// <see cref="ReleaseReservation"/> on <i>every</i> other exit (failure, cancellation, exception).
    /// On either refusal <paramref name="reservationId"/> is 0 and nothing is held.</para>
    /// </summary>
    /// <param name="agentId">The agent the request is attributed to.</param>
    /// <param name="estimatedTokens">The request's token estimate — what gets provisionally debited.</param>
    /// <param name="reservationId">The reservation handle (0 when not granted).</param>
    /// <param name="reason">The user-facing refusal reason (empty when granted).</param>
    public BudgetAdmission TryReserve(string agentId, long estimatedTokens, out long reservationId, out string reason)
    {
        lock (_gate)
        {
            reservationId = 0;

            // Settled spend alone is over a cap → the durable exhaustion the caller pauses on.
            if (IsOverCapLocked(agentId, includeReservations: false, out reason))
            {
                return BudgetAdmission.Exhausted;
            }

            // Only the in-flight reservations push it over → refuse this request without pausing.
            if (IsOverCapLocked(agentId, includeReservations: true, out reason))
            {
                return BudgetAdmission.InFlight;
            }

            var tokens = Math.Max(0, estimatedTokens);
            reservationId = ++_nextReservationId;
            _reservations[reservationId] = new Reservation(
                agentId ?? string.Empty,
                tokens,
                // The model id is not known until the provider answers, so the estimate is priced at the
                // table's conservative unlisted-model rate. SettleReservation re-prices with the real
                // model, so the approximation only ever affects the in-flight window.
                ModelPriceTable.CostMicros(string.Empty, tokens),
                _clock().UtcDateTime);
            return BudgetAdmission.Granted;
        }
    }

    /// <summary>
    /// Discharges a reservation by replacing it with the real, model-priced row. The drop and the
    /// append happen in ONE lock so a concurrent <see cref="IsExhausted"/> can never observe a window
    /// where the request is charged to neither the reservation nor the ledger. A
    /// <paramref name="reservationId"/> of 0 (or one already discharged) simply records the row — the
    /// direct <see cref="Record"/> path stays available.
    /// </summary>
    public SpendRecord SettleReservation(long reservationId, string agentId, string model, long actualTokens)
    {
        var record = new SpendRecord
        {
            AgentId = agentId,
            Model = model ?? string.Empty,
            Tokens = actualTokens,
            UsdMicros = ModelPriceTable.CostMicros(model ?? string.Empty, actualTokens),
            WhenUtc = _clock().UtcDateTime,
        };

        lock (_gate)
        {
            _reservations.Remove(reservationId);
            _store.Append(record);
        }

        SpendRecorded?.Invoke(record);
        return record;
    }

    /// <summary>
    /// Drops a reservation WITHOUT recording spend — the request never happened (refused downstream,
    /// cancelled, or it threw). Returns true when a reservation was actually held. This is the half of
    /// the MG-24 contract that must be unmissable: a reservation that is never released permanently
    /// consumes budget the agent never spent.
    /// </summary>
    public bool ReleaseReservation(long reservationId)
    {
        lock (_gate)
        {
            return _reservations.Remove(reservationId);
        }
    }

    /// <summary>Provisional (unsettled) debit currently held for one agent — the MG-24 leak canary.</summary>
    public SpendTotals GetReserved(string agentId)
    {
        lock (_gate)
        {
            return ReservedForAgentLocked(agentId);
        }
    }

    /// <summary>How many reservations are outstanding across all agents (0 when nothing is in flight).</summary>
    public int OutstandingReservations
    {
        get { lock (_gate) { return _reservations.Count; } }
    }

    /// <summary>
    /// Records actual settled spend for a request and returns the persisted row. Fires
    /// <see cref="SpendRecorded"/> so the daemon streams it. Recording is always allowed (the request
    /// already happened); the cap gate is <see cref="IsExhausted"/>, consulted before the next request.
    /// </summary>
    public SpendRecord Record(string agentId, string model, long tokens)
    {
        var record = new SpendRecord
        {
            AgentId = agentId,
            Model = model ?? string.Empty,
            Tokens = tokens,
            UsdMicros = ModelPriceTable.CostMicros(model ?? string.Empty, tokens),
            WhenUtc = _clock().UtcDateTime,
        };

        lock (_gate)
        {
            _store.Append(record);
        }

        SpendRecorded?.Invoke(record);
        return record;
    }

    /// <summary>Lifetime totals for one agent.</summary>
    public SpendTotals GetTotals(string agentId)
    {
        lock (_gate)
        {
            return TotalsForAgentLocked(agentId);
        }
    }

    /// <summary>
    /// The cost-per-merged-change join hook (P2-10): spend for <paramref name="agentId"/> at or after
    /// <paramref name="since"/>.
    /// </summary>
    public SpendTotals GetSpendSince(string agentId, DateTimeOffset since)
    {
        lock (_gate)
        {
            long tokens = 0, usd = 0;
            foreach (var r in _store.All())
            {
                if (string.Equals(r.AgentId, agentId, StringComparison.Ordinal) && r.WhenUtc >= since.UtcDateTime)
                {
                    tokens += r.Tokens;
                    usd += r.UsdMicros;
                }
            }

            return new SpendTotals(tokens, usd);
        }
    }

    /// <summary>All persisted rows (snapshot for <c>StreamSpend</c> replay and snapshot totals).</summary>
    public IReadOnlyList<SpendRecord> AllRows()
    {
        lock (_gate)
        {
            return _store.All();
        }
    }

    // The single cap evaluation, shared by IsExhausted and TryReserve so the two can never drift.
    // includeReservations decides whether in-flight provisional debits count toward the caps; the
    // reason text says which, because "1,000 of 1,000 used" reads as a lie when 400 of it is in flight.
    private bool IsOverCapLocked(string agentId, bool includeReservations, out string reason)
    {
        var caps = _caps;
        var today = _clock().UtcDateTime.Date;
        var agent = TotalsForAgentLocked(agentId);
        var day = TotalsForDayLocked(today);
        if (includeReservations)
        {
            agent = Add(agent, ReservedForAgentLocked(agentId));
            day = Add(day, ReservedForDayLocked(today));
        }

        var scope = includeReservations ? " (including requests in flight)" : "";

        if (caps.PerAgentTokenCap > 0 && agent.Tokens >= caps.PerAgentTokenCap)
        {
            reason = $"Agent budget reached: {Tokens(agent.Tokens)} of {Tokens(caps.PerAgentTokenCap)} tokens used{scope}.";
            return true;
        }

        if (caps.PerAgentUsdMicrosCap > 0 && agent.UsdMicros >= caps.PerAgentUsdMicrosCap)
        {
            reason = $"Agent budget reached: {FormatUsd(agent.UsdMicros)} of {FormatUsd(caps.PerAgentUsdMicrosCap)} spent{scope}.";
            return true;
        }

        if (caps.PerDayTokenCap > 0 && day.Tokens >= caps.PerDayTokenCap)
        {
            reason = $"Daily budget reached: {Tokens(day.Tokens)} of {Tokens(caps.PerDayTokenCap)} tokens used today{scope}.";
            return true;
        }

        if (caps.PerDayUsdMicrosCap > 0 && day.UsdMicros >= caps.PerDayUsdMicrosCap)
        {
            reason = $"Daily budget reached: {FormatUsd(day.UsdMicros)} of {FormatUsd(caps.PerDayUsdMicrosCap)} spent today{scope}.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static SpendTotals Add(SpendTotals a, SpendTotals b) =>
        new(a.Tokens + b.Tokens, a.UsdMicros + b.UsdMicros);

    private SpendTotals ReservedForAgentLocked(string agentId)
    {
        long tokens = 0, usd = 0;
        foreach (var r in _reservations.Values)
        {
            if (string.Equals(r.AgentId, agentId, StringComparison.Ordinal))
            {
                tokens += r.Tokens;
                usd += r.UsdMicros;
            }
        }

        return new SpendTotals(tokens, usd);
    }

    private SpendTotals ReservedForDayLocked(DateTime utcDay)
    {
        long tokens = 0, usd = 0;
        foreach (var r in _reservations.Values)
        {
            if (r.WhenUtc.Date == utcDay)
            {
                tokens += r.Tokens;
                usd += r.UsdMicros;
            }
        }

        return new SpendTotals(tokens, usd);
    }

    private SpendTotals TotalsForAgentLocked(string agentId)
    {
        long tokens = 0, usd = 0;
        foreach (var r in _store.All())
        {
            if (string.Equals(r.AgentId, agentId, StringComparison.Ordinal))
            {
                tokens += r.Tokens;
                usd += r.UsdMicros;
            }
        }

        return new SpendTotals(tokens, usd);
    }

    private SpendTotals TotalsForDayLocked(DateTime utcDay)
    {
        long tokens = 0, usd = 0;
        foreach (var r in _store.All())
        {
            if (r.WhenUtc.Date == utcDay)
            {
                tokens += r.Tokens;
                usd += r.UsdMicros;
            }
        }

        return new SpendTotals(tokens, usd);
    }

    private static string Tokens(long count) =>
        count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatUsd(long micros) =>
        (micros / 1_000_000.0).ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    /// <summary>One provisional debit held between <see cref="TryReserve"/> and its discharge (MG-24).</summary>
    private readonly record struct Reservation(string AgentId, long Tokens, long UsdMicros, DateTime WhenUtc);
}

/// <summary>An in-memory <see cref="ISpendStore"/> for tests and the pre-persistence daemon path.</summary>
public sealed class InMemorySpendStore : ISpendStore
{
    private readonly object _gate = new();
    private readonly List<SpendRecord> _rows = new();
    private long _nextId;

    public void Append(SpendRecord record)
    {
        lock (_gate)
        {
            record.Id = ++_nextId;
            _rows.Add(record);
        }
    }

    public IReadOnlyList<SpendRecord> All()
    {
        lock (_gate)
        {
            return _rows.ToArray();
        }
    }
}
