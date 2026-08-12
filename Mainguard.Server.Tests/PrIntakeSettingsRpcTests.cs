using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git;
using Mainguard.Protos.V1;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using StoredSettings = Mainguard.Agents.Agents.Orchestrator.PrIntakeSettings;
using WireSettings = Mainguard.Protos.V1.PrIntakeSettings;

namespace Mainguard.Server.Tests;

/// <summary>
/// P2-12 — the external-PR-intake settings surface, end to end through a real in-proc daemon.
///
/// <para><b>What these are actually guarding.</b> The App shipped a complete intake settings dialog that
/// nothing opened, and it had nowhere real to write: its ViewModel took the daemon's own
/// <c>IPrIntakeStore</c> and defaulted to an <b>in-process</b> one. Wiring the dialog to that would have
/// produced a settings screen that saves successfully and changes nothing, because the process that
/// polls the host, fetches PR heads and provisions a jail per intake'd pull request is the daemon.</para>
///
/// <para>So no test here asserts that a ViewModel property changed. One requires the write to land in a
/// real SQLite file the daemon owns and to still be there when a <b>fresh store over that file</b> reads
/// it — the next daemon boot's view. The other requires the live poll engine to be reading the same row
/// the RPC writes, which is what makes the two halves structurally unable to drift.</para>
/// </summary>
public class PrIntakeSettingsRpcTests
{
    /// <summary>
    /// The persistence claim: the save reaches the DAEMON'S OWN DURABLE STORE and survives the daemon.
    ///
    /// <para>The host is given a <see cref="DbPrIntakeStore"/> over a SQLite file this test names, using
    /// the same <c>ConfigureTestServices</c> store-isolation idiom <c>GatewaySpendRpcTests</c> uses —
    /// necessary because the in-proc tier shares one daemon database per process, so "a second fixture"
    /// is not a restart. The settings go in over gRPC, the host is disposed, and then a <b>brand-new
    /// store object over the same file</b> — nothing in common with the host but the bytes on disk — has
    /// to answer with them. An implementation that merely held the value in a singleton for the life of
    /// a process (the exact shape of the defect being prevented) passes a same-host round trip and fails
    /// this.</para>
    /// </summary>
    [Fact]
    public async Task UpdatedSettings_ReachTheDaemonsDurableStore_AndSurviveIt()
    {
        var dbPath = FreshDaemonDb();

        using (var host = HostWithStoreAt(dbPath, out var channel, out var headers))
        {
            var client = new PrIntakeService.PrIntakeServiceClient(channel);

            await client.UpdatePrIntakeSettingsAsync(
                new UpdatePrIntakeSettingsRequest
                {
                    Settings = Wire(enabled: false, interval: 900, "renovate[bot]", "dependabot[bot]"),
                },
                headers);

            await client.SubscribePrIntakeSourceAsync(
                new SubscribePrIntakeSourceRequest
                {
                    Source = new PrIntakeSource { Host = "github.com", Owner = "acme", Repo = "app" },
                },
                headers);
        }

        // The next daemon boot's view of the same file: a fresh store, a fresh DbContext, no shared state.
        var afterRestart = new DbPrIntakeStore(() => new AppDbContext(dbPath));
        var settings = afterRestart.GetSettings();

        Assert.False(settings.Enabled);
        Assert.Equal(900, settings.PollIntervalSeconds);
        Assert.Equal(new[] { "renovate[bot]", "dependabot[bot]" }, settings.BotAuthors);
        Assert.Equal("github.com/acme/app", Assert.Single(afterRestart.Subscriptions()).Key);
    }

    /// <summary>
    /// …and the store the SHIPPED daemon graph resolves is that durable one, not an in-memory stand-in.
    /// Without this, the test above proves a durable store works while production could still be wired
    /// to something that forgets on restart.
    /// </summary>
    [Fact]
    public void TheDaemonsOwnIntakeStore_IsTheDurableOne()
    {
        using var fixture = new DaemonFixture();
        Assert.IsType<DbPrIntakeStore>(fixture.Services.GetRequiredService<IPrIntakeStore>());
    }

    /// <summary>
    /// The other half, and the reason persistence alone is not enough: the settings the RPC writes must
    /// be the settings the POLLER reads.
    ///
    /// <para>Asserted against the daemon's own live <see cref="IExternalPrIntake"/> — the very instance
    /// <c>PrIntakeHostedService</c> runs the poll loop on — immediately after a write over the wire, with
    /// no restart and no re-registration. A page that wrote to one place while the engine read its
    /// compiled-in defaults from another would satisfy every persistence assertion above and fail here,
    /// which is exactly the "looks applied but isn't" failure this pair exists for.</para>
    /// </summary>
    [Fact]
    public async Task UpdatedSettings_AreWhatTheLivePollerReads()
    {
        using var host = HostWithStoreAt(FreshDaemonDb(), out var channel, out var headers);
        var client = new PrIntakeService.PrIntakeServiceClient(channel);

        var engine = Assert.IsType<ExternalPrIntake>(host.Services.GetRequiredService<IExternalPrIntake>());
        Assert.Equal(TimeSpan.FromSeconds(60), engine.PollInterval); // the shipped default, before any write

        await client.UpdatePrIntakeSettingsAsync(
            new UpdatePrIntakeSettingsRequest { Settings = Wire(enabled: true, interval: 120, "jenkins[bot]") },
            headers);

        Assert.Equal(TimeSpan.FromSeconds(120), engine.PollInterval);
        Assert.Equal(new[] { "jenkins[bot]" }, engine.AuthorFilters);
        Assert.True(engine.Settings.Enabled);

        // And the off switch reaches it too — the poll loop keeps running, it just stops materializing.
        await client.UpdatePrIntakeSettingsAsync(
            new UpdatePrIntakeSettingsRequest { Settings = Wire(enabled: false, interval: 120, "jenkins[bot]") },
            headers);

        Assert.False(engine.Settings.Enabled);
    }

    /// <summary>
    /// The daemon answers with what it STORED, never with what it was handed. A handler that echoed the
    /// request would show a cadence of zero while the poller ran at ten seconds — a smaller version of
    /// the same lie, and the one most likely to be reintroduced by a later "simplify".
    /// </summary>
    [Fact]
    public async Task UpdateSettings_EchoesWhatWasPersisted_NotWhatWasRequested()
    {
        using var host = HostWithStoreAt(FreshDaemonDb(), out var channel, out var headers);
        var client = new PrIntakeService.PrIntakeServiceClient(channel);

        var response = await client.UpdatePrIntakeSettingsAsync(
            new UpdatePrIntakeSettingsRequest { Settings = Wire(enabled: true, interval: 0) },
            headers);

        Assert.Equal(StoredSettings.MinPollIntervalSeconds, response.Settings.PollIntervalSeconds);
        // An empty author list is a mis-save far more often than a deliberate "poll this repo and ignore
        // every pull request on it", so the daemon substitutes its own list rather than matching nothing.
        Assert.Equal(ExternalPrIntake.DefaultBotAuthors, response.Settings.BotAuthors);
    }

    /// <summary>A repeat subscribe is idempotent and reported as such — never an error, never a second row.</summary>
    [Fact]
    public async Task SubscribeSource_IsIdempotent()
    {
        using var host = HostWithStoreAt(FreshDaemonDb(), out var channel, out var headers);
        var client = new PrIntakeService.PrIntakeServiceClient(channel);
        var request = new SubscribePrIntakeSourceRequest
        {
            Source = new PrIntakeSource { Host = "github.com", Owner = "acme", Repo = "app" },
        };

        var first = await client.SubscribePrIntakeSourceAsync(request, headers);
        var second = await client.SubscribePrIntakeSourceAsync(request, headers);

        Assert.True(first.Added);
        Assert.False(second.Added);
        Assert.Single(second.Sources);
    }

    /// <summary>An incomplete source is refused rather than persisted: a row with no repository can never
    /// resolve to anything and would be skipped silently on every poll for as long as the daemon lives.</summary>
    [Fact]
    public async Task SubscribeSource_WithoutARepository_IsRefused()
    {
        using var host = HostWithStoreAt(FreshDaemonDb(), out var channel, out var headers);
        var client = new PrIntakeService.PrIntakeServiceClient(channel);

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.SubscribePrIntakeSourceAsync(
            new SubscribePrIntakeSourceRequest { Source = new PrIntakeSource { Host = "github.com" } },
            headers).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    // ---- harness -------------------------------------------------------------

    /// <summary>
    /// An in-proc daemon whose intake store is the DURABLE one, over a database this test names.
    ///
    /// <para>The replacement is the tier's documented store-isolation idiom (see
    /// <c>GatewaySpendRpcTests</c>): every <see cref="DaemonFixture"/> in a process resolves to the SAME
    /// daemon database, so without it these tests would see each other's subscriptions. It replaces only
    /// the file, not the implementation — the store is still <see cref="DbPrIntakeStore"/>, which is what
    /// keeps the persistence assertions about real SQLite rather than about a test double.</para>
    /// </summary>
    private static IntakeHost HostWithStoreAt(string dbPath, out GrpcChannel channel, out Metadata headers)
    {
        var fixture = new DaemonFixture();
        var host = fixture.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<IPrIntakeStore>(new DbPrIntakeStore(() => new AppDbContext(dbPath)))));

        channel = GrpcChannel.ForAddress(
            host.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = host.Server.CreateHandler() });
        var token = host.Services.GetRequiredService<Mainguard.Server.Auth.SessionTokenFile>().Token;
        headers = new Metadata { { "authorization", $"bearer {token}" } };
        return new IntakeHost(fixture, host);
    }

    /// <summary>The base fixture and the store-overridden host it produced, disposed together — disposing
    /// only the derived one leaks the base fixture's temp token directory.</summary>
    private sealed class IntakeHost : IDisposable
    {
        private readonly DaemonFixture _fixture;
        private readonly WebApplicationFactory<Program> _host;

        public IntakeHost(DaemonFixture fixture, WebApplicationFactory<Program> host)
        {
            _fixture = fixture;
            _host = host;
        }

        public IServiceProvider Services => _host.Services;

        public void Dispose()
        {
            _host.Dispose();
            _fixture.Dispose();
        }
    }

    /// <summary>A migrated, empty daemon database inside the run's isolated data root — never the
    /// developer's real one (see <see cref="TestDataRootIsolation"/>), and unique per call.</summary>
    private static string FreshDaemonDb()
    {
        var dir = Path.Combine(MainguardPaths.DataRoot(), "pr-intake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "mainguard-daemon.db");
        using var db = new AppDbContext(dbPath);
        db.Database.Migrate();
        return dbPath;
    }

    private static WireSettings Wire(bool enabled, int interval, params string[] authors)
    {
        var settings = new WireSettings { Enabled = enabled, PollIntervalSeconds = interval };
        settings.BotAuthors.AddRange(authors);
        return settings;
    }
}
