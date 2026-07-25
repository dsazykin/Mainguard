using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Git.Audit;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Security;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-08 test contract #4/#8 — budgets, spend telemetry, and the typed-pause-not-kill invariant.
/// </summary>
public class BudgetLedgerTests
{
    private static Func<DateTimeOffset> FrozenClock(DateTimeOffset at) => () => at;

    [Fact]
    public void ModelPriceTable_PricesKnownAndUnknownModels()
    {
        // 1,000,000 haiku tokens ≈ $1.00 = 1,000,000 micros.
        Assert.Equal(1_000_000, ModelPriceTable.CostMicros("claude-3-5-haiku", 1_000_000));
        // Prefix match: dated model id maps to its family price.
        Assert.Equal(6_000_000, ModelPriceTable.CostMicros("claude-3-5-sonnet-20241022", 1_000_000));
        // Unknown model → conservative default rate.
        Assert.Equal(ModelPriceTable.DefaultUsdMicrosPerMillionTokens, ModelPriceTable.CostMicros("mystery-model", 1_000_000));
    }

    [Fact]
    public void IsExhausted_TripsOnPerAgentTokenCap_WithHonestReason()
    {
        var ledger = new BudgetLedger(new InMemorySpendStore(), FrozenClock(DateTimeOffset.UtcNow),
            new BudgetCaps(PerAgentTokenCap: 1000, 0, 0, 0));

        ledger.Record("agent-1", "claude-3-5-haiku", 600);
        Assert.False(ledger.IsExhausted("agent-1", out _));

        ledger.Record("agent-1", "claude-3-5-haiku", 500); // 1100 ≥ 1000
        Assert.True(ledger.IsExhausted("agent-1", out var reason));
        Assert.Contains("1,000", reason); // states the cap
        // Another agent is unaffected.
        Assert.False(ledger.IsExhausted("agent-2", out _));
    }

    [Fact]
    public void GetSpendSince_FiltersByAgentAndTime()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = t0;
        var ledger = new BudgetLedger(new InMemorySpendStore(), () => now, BudgetCaps.Unlimited);

        ledger.Record("a", "gpt-4o", 100);
        now = t0.AddHours(2);
        var since = now;
        ledger.Record("a", "gpt-4o", 250);
        ledger.Record("b", "gpt-4o", 999);

        var spend = ledger.GetSpendSince("a", since);
        Assert.Equal(250, spend.Tokens); // the earlier 100 and agent b are excluded
    }

    [Fact]
    public async Task Budget_ExhaustionPausesTyped_NotKilled_AndAudits()
    {
        var audit = new InMemoryAuditLog();
        var supervisor = new FakeAgentSupervisor();
        var gateway = AiGateway.Create(
            new KeyHealth { RequestsPerMinute = 100, TokensPerMinute = 100_000 },
            FrozenClock(DateTimeOffset.UtcNow),
            supervisor,
            audit,
            new BudgetCaps(PerAgentTokenCap: 1000, 0, 0, 0));

        // Accrue spend to the cap (as a settled request would).
        gateway.Ledger.Record("agent-1", "claude-3-5-haiku", 1000);

        // The next acquire is refused with a typed reason — and the agent is PAUSED, not killed.
        var ex = await Assert.ThrowsAsync<BudgetExhaustedException>(
            () => gateway.AcquireAsync("agent-1", 500, CancellationToken.None));
        Assert.Equal("agent-1", ex.AgentId);

        Assert.Contains("agent-1", supervisor.Paused);
        Assert.Empty(supervisor.Resumed);                       // never resumed → still paused
        Assert.Equal("BudgetExhausted", supervisor.LastState("agent-1"));

        // AuditProbe-equivalent: exactly one budget_exceeded event carrying the agent id.
        var events = audit.Read().Where(e => e.Type == "budget_exceeded").ToArray();
        Assert.Single(events);
        Assert.Equal("agent-1", events[0].Fields["agent_id"]);
    }

    [Fact]
    public void Snapshot_ReportsPerAgentSpend_AndTotalsMatchRows()
    {
        var gateway = AiGateway.Create(
            new KeyHealth { RequestsPerMinute = 100, TokensPerMinute = 100_000 },
            FrozenClock(DateTimeOffset.UtcNow));

        gateway.Ledger.Record("a", "gpt-4o", 100);
        gateway.Ledger.Record("a", "gpt-4o", 50);
        gateway.Ledger.Record("b", "gpt-4o-mini", 200);

        var snapshot = gateway.GetSnapshot();
        var a = snapshot.Agents.Single(x => x.AgentId == "a");
        var b = snapshot.Agents.Single(x => x.AgentId == "b");

        Assert.Equal(150, a.Tokens);
        Assert.Equal(200, b.Tokens);

        // Snapshot totals reconcile with the raw ledger rows.
        var rows = gateway.Ledger.AllRows();
        Assert.Equal(rows.Where(r => r.AgentId == "a").Sum(r => r.Tokens), a.Tokens);
        Assert.Equal(rows.Sum(r => r.UsdMicros), snapshot.Agents.Sum(x => x.UsdMicros));
    }

    [Fact]
    public async Task Snapshot_ReportsQueueDepth_ForWaitingAgent()
    {
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var bucket = new TokenBucket(1, 1000, () => clock);
        var gateway = new AiGateway(bucket, new BudgetLedger(new InMemorySpendStore(), () => clock));

        // Drain the single request permit, then leave one acquire queued.
        var first = await gateway.AcquireAsync("a", 500, CancellationToken.None);
        Assert.NotNull(first);
        using var cts = new CancellationTokenSource();
        var queued = gateway.AcquireAsync("a", 500, cts.Token);

        var snapshot = gateway.GetSnapshot();
        Assert.Equal(1, snapshot.TotalQueueDepth);
        Assert.Equal(1, snapshot.Agents.Single(x => x.AgentId == "a").QueueDepth);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
    }

    // ---- MG-24: the cap is enforced against spend in flight, not just spend already settled ----

    /// <summary>A gateway with a bucket far too large to be the thing that limits anything — so the only
    /// gate under test is the budget one.</summary>
    private static AiGateway UnthrottledGateway(BudgetCaps caps, out BudgetLedger ledger)
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ledger = new BudgetLedger(new InMemorySpendStore(), FrozenClock(at), caps);
        return new AiGateway(new TokenBucket(1_000_000, 1_000_000_000, FrozenClock(at)), ledger);
    }

    /// <summary>
    /// MG-24 (the race itself). N genuinely parallel acquires for one agent at the cap boundary. The old
    /// gate read <c>IsExhausted</c> and only debited in <c>Settle</c> — after the upstream round-trip —
    /// so every one of the N read "under cap" and every one was admitted; the cap was enforced against
    /// spend that had already been committed, never against spend in flight, and the only thing bounding
    /// the overshoot was the shared 60 req/min bucket. With the estimate reserved at acquire time the
    /// admitted count is exactly what fits: 1000-token cap / 100-token estimate = 10, and the other 40
    /// are refused.
    /// </summary>
    [Fact]
    public async Task AcquireAsync_ConcurrentRequestsAtTheCap_CannotOvershoot()
    {
        const int cap = 1000, estimate = 100, parallel = 50;
        var gateway = UnthrottledGateway(new BudgetCaps(PerAgentTokenCap: cap, 0, 0, 0), out var ledger);

        // All 50 threads pile into the gate at once — the window the old check-then-act code lost.
        using var start = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, parallel).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            try
            {
                return await gateway.AcquireAsync("agent-1", estimate, CancellationToken.None);
            }
            catch (BudgetExhaustedException)
            {
                return null;
            }
        })).ToArray();

        start.Set();
        var leases = await Task.WhenAll(attempts);

        var granted = leases.Where(l => l is not null).ToArray();
        Assert.Equal(cap / estimate, granted.Length);                   // exactly what fits — no overshoot
        Assert.Equal(cap, ledger.GetReserved("agent-1").Tokens);        // all of it held as provisional debit
        Assert.Equal(granted.Length, ledger.OutstandingReservations);

        // The reservations are estimates, not charges: settling them for less frees the difference and
        // the agent can spend again. A reservation that stuck would be a permanent phantom debit.
        foreach (var lease in granted)
        {
            gateway.Settle(lease!, actualTokens: 50, "claude-3-5-haiku");
        }

        Assert.Equal(0, ledger.OutstandingReservations);
        Assert.Equal(granted.Length * 50, ledger.GetTotals("agent-1").Tokens);
        Assert.False(ledger.IsExhausted("agent-1", out _));
        Assert.NotNull(await gateway.AcquireAsync("agent-1", estimate, CancellationToken.None));
    }

    /// <summary>
    /// MG-24 (the failure mode the fix must not introduce). A reservation that survives a failed request
    /// is worse than the overshoot it prevents: it charges an agent forever for spend that never
    /// happened. Every non-settling exit — a cancelled bucket wait, an abandoned lease — hands it back,
    /// and a hundred failed requests against a ten-request budget leave the agent untouched.
    /// </summary>
    [Fact]
    public async Task AcquireAsync_ReleasesTheReservation_OnEveryFailedExitPath()
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ledger = new BudgetLedger(new InMemorySpendStore(), FrozenClock(at),
            new BudgetCaps(PerAgentTokenCap: 1000, 0, 0, 0));

        // A one-request bucket so the second acquire is forced to queue, then cancelled while waiting.
        var gateway = new AiGateway(new TokenBucket(1, 1_000_000, FrozenClock(at)), ledger);

        var first = await gateway.AcquireAsync("agent-1", 100, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var queued = gateway.AcquireAsync("agent-1", 100, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        // The cancelled acquire left nothing behind; only the granted lease still holds its estimate.
        Assert.Equal(1, ledger.OutstandingReservations);
        Assert.Equal(100, ledger.GetReserved("agent-1").Tokens);

        gateway.Abandon(first);
        Assert.Equal(0, ledger.OutstandingReservations);
        Assert.Equal(0, ledger.GetReserved("agent-1").Tokens);
        Assert.Empty(ledger.AllRows());                                  // abandoning records no spend

        // 100 failed requests against a budget that fits 10 — with a leak, the agent is dead after 10.
        var wide = new AiGateway(new TokenBucket(1_000_000, 1_000_000_000, FrozenClock(at)), ledger);
        for (var i = 0; i < 100; i++)
        {
            wide.Abandon(await wide.AcquireAsync("agent-1", 100, CancellationToken.None));
        }

        Assert.Equal(0, ledger.OutstandingReservations);
        Assert.False(ledger.IsExhausted("agent-1", out _));
        Assert.NotNull(await wide.AcquireAsync("agent-1", 100, CancellationToken.None));
    }

    /// <summary>
    /// MG-24 (reconciliation). Settling swaps the provisional debit for the real, model-priced row in one
    /// step: the agent is charged the ACTUAL usage, never the estimate, and the ledger's committed totals
    /// stay exactly the sum of its rows (what the snapshot and the cost-per-merged-change join read).
    /// </summary>
    [Fact]
    public async Task Settle_ReconcilesTheReservation_ToActualUsage()
    {
        var gateway = UnthrottledGateway(BudgetCaps.Unlimited, out var ledger);

        var lease = await gateway.AcquireAsync("agent-1", estimatedTokens: 5000, CancellationToken.None);
        Assert.Equal(5000, ledger.GetReserved("agent-1").Tokens);
        Assert.Equal(0, ledger.GetTotals("agent-1").Tokens);              // nothing charged while in flight

        var totals = gateway.Settle(lease, actualTokens: 12, "claude-3-5-haiku");

        Assert.Equal(12, totals.Tokens);                                   // actuals, not the estimate
        Assert.Equal(0, ledger.GetReserved("agent-1").Tokens);
        Assert.Equal(ledger.AllRows().Sum(r => r.Tokens), ledger.GetTotals("agent-1").Tokens);
    }

    /// <summary>
    /// MG-24 (honest pausing). Settled spend at the cap is durable — it pauses the worker and audits, as
    /// P2-08 requires. A refusal caused only by requests still in flight is transient back-pressure: the
    /// request is refused, but the worker is NOT paused for spend that has not happened and may never
    /// settle, because nothing would clear that pause if the estimates came back smaller.
    /// </summary>
    [Fact]
    public async Task InFlightRefusal_DoesNotPauseTheWorker_ButSettledExhaustionDoes()
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var supervisor = new FakeAgentSupervisor();
        var audit = new InMemoryAuditLog();
        var ledger = new BudgetLedger(new InMemorySpendStore(), FrozenClock(at),
            new BudgetCaps(PerAgentTokenCap: 1000, 0, 0, 0));
        var gateway = new AiGateway(
            new TokenBucket(1_000_000, 1_000_000_000, FrozenClock(at)), ledger, supervisor, audit, FrozenClock(at));

        // Fill the cap with reservations only — no settled spend at all.
        var held = new List<GatewayLease>();
        for (var i = 0; i < 10; i++)
        {
            held.Add(await gateway.AcquireAsync("agent-1", 100, CancellationToken.None));
        }

        await Assert.ThrowsAsync<BudgetExhaustedException>(
            () => gateway.AcquireAsync("agent-1", 100, CancellationToken.None));
        Assert.Empty(supervisor.Paused);                                   // transient — no pause, no audit
        Assert.Empty(audit.Read().Where(e => e.Type == "budget_exceeded").ToArray());

        // Now let them settle at full estimate: the spend is real, so the next refusal IS a pause.
        foreach (var lease in held)
        {
            gateway.Settle(lease, actualTokens: 100, "claude-3-5-haiku");
        }

        await Assert.ThrowsAsync<BudgetExhaustedException>(
            () => gateway.AcquireAsync("agent-1", 100, CancellationToken.None));
        Assert.Contains("agent-1", supervisor.Paused);
        Assert.Single(audit.Read().Where(e => e.Type == "budget_exceeded").ToArray());
    }
}
