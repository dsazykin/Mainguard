using Mainguard.Agents.Agents;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests.Gateway;

/// <summary>
/// MG-21 — daily budget caps must survive a daemon restart.
///
/// <para>Boot read the persisted budget row and then built
/// <c>new BudgetCaps(stored.TokenCap, stored.UsdMicrosCap, 0, 0)</c>, hardcoding the 3rd/4th arguments
/// (<c>PerDayTokenCap</c>/<c>PerDayUsdMicrosCap</c>) to literal <c>0</c> — which <see cref="BudgetCaps"/>
/// defines as <b>unlimited</b>. So a daily cap the user had set silently stopped being enforced the
/// moment the daemon restarted, while <c>GetBudgets</c> kept reporting the persisted value — the spend
/// ceiling and the reported ceiling disagreed, and only the per-agent lifetime caps survived.
/// <c>SetBudgets</c> at runtime always set all four correctly, so the defect lived exclusively in the
/// boot path, where it is least likely to be noticed.</para>
///
/// <para>These resolve <see cref="BudgetLedger"/> from a <b>freshly built host</b> whose store already
/// holds a persisted budget — i.e. they exercise the real boot composition, which is where the bug was.</para>
/// </summary>
public sealed class DailyBudgetCapBootTests
{
    /// <summary>A store that already holds a daily budget, as it would after a restart.</summary>
    private static IBudgetStore StoreWithPersistedDailyCaps()
    {
        var store = new InMemoryBudgetStore();
        store.Set(usdMicrosCap: 2_000_000, tokenCap: 9_000, usdMicrosCapPerDay: 500_000, tokenCapPerDay: 1_000);
        return store;
    }

    [Fact]
    public void Boot_CarriesThePersistedDailyCaps_IntoTheLiveLedger()
    {
        using var fixture = new DaemonFixture();
        using var restarted = fixture.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton(StoreWithPersistedDailyCaps());
            services.AddSingleton<ISpendStore>(new InMemorySpendStore());
        }));

        var caps = restarted.Services.GetRequiredService<BudgetLedger>().Caps;

        // The per-agent caps always survived...
        Assert.Equal(9_000, caps.PerAgentTokenCap);
        Assert.Equal(2_000_000, caps.PerAgentUsdMicrosCap);

        // ...these two are the regression: previously both came back 0 (= unlimited).
        Assert.Equal(1_000, caps.PerDayTokenCap);
        Assert.Equal(500_000, caps.PerDayUsdMicrosCap);
    }

    [Fact]
    public void Boot_DailyTokenCap_IsActuallyEnforced_AfterRestart()
    {
        using var fixture = new DaemonFixture();
        using var restarted = fixture.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton(StoreWithPersistedDailyCaps());
            services.AddSingleton<ISpendStore>(new InMemorySpendStore());
        }));

        var ledger = restarted.Services.GetRequiredService<BudgetLedger>();

        // Spend the whole 1,000-token day across two different agents (the daily cap is global).
        ledger.Record("agent-a", "claude-sonnet-4", 600);
        Assert.False(ledger.IsExhausted("agent-b", out _)); // still under the daily ceiling
        ledger.Record("agent-b", "claude-sonnet-4", 500);

        // Over the persisted daily cap — enforcement must bite for an agent that has barely spent.
        Assert.True(ledger.IsExhausted("agent-c", out var reason));
        Assert.Contains("Daily", reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Boot_WithNoPersistedBudget_StaysUnlimited()
    {
        using var fixture = new DaemonFixture();
        using var restarted = fixture.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton<IBudgetStore>(new InMemoryBudgetStore()); // never Set
            services.AddSingleton<ISpendStore>(new InMemorySpendStore());
        }));

        var ledger = restarted.Services.GetRequiredService<BudgetLedger>();

        Assert.Equal(0, ledger.Caps.PerDayTokenCap);
        ledger.Record("agent-a", "claude-sonnet-4", 10_000_000);
        Assert.False(ledger.IsExhausted("agent-a", out _)); // unset really does mean unlimited
    }
}
