using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Agents.Agents;
using Mainguard.Server.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>
/// TI-P2-00 §A.4.1 — the shared daemon in-proc fixture. Hosts <c>Mainguard.Server</c> via
/// <see cref="WebApplicationFactory{TEntryPoint}"/>, isolates the session-token file to a
/// temp path, exposes the token, a correct/authenticated channel, a wrong-token channel,
/// and a log-capture sink for the G-13 field-mask assertions. Every daemon in-proc test
/// uses this — hand-rolled hosts are a bug. Call <see cref="StartNew"/> for an
/// independent second host (the daemon-restart / reconnect scenarios).
/// </summary>
public sealed class DaemonFixture : WebApplicationFactory<Program>
{
    private readonly string _tokenPath;
    private readonly CapturingLoggerProvider _logs = new();

    public DaemonFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mainguard-daemon-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tokenPath = Path.Combine(dir, "daemon.token");
    }

    /// <summary>
    /// Replaces the daemon's container-stats source. Set before the host is built (i.e. before touching
    /// <see cref="WebApplicationFactory{T}.Services"/>). Lets a test drive the resource stream with KNOWN
    /// CPU/RAM values, so the daemon→gRPC→client→ViewModel wire can be asserted on real numbers rather
    /// than on a formatter — the whole point being that this feature previously had no data source at all.
    /// </summary>
    public Mainguard.Agents.Agents.Sandbox.IContainerResourceSampler? ResourceSampler { get; init; }

    /// <summary>
    /// Starts THIS host with dev-only queue seeding enabled (docs/design/queue-seeding.md §7) by
    /// replacing the boot-captured <c>QueueSeedingOptions</c> singleton in ConfigureTestServices —
    /// the same seam the AdmissionController pin below uses, and the only one that can differ
    /// between the enabled and disabled daemons one test process hosts side by side (a process-wide
    /// env var cannot, and a <c>UseSetting</c> configuration key measurably never reaches
    /// <c>builder.Configuration</c> during the daemon's ConfigureServices under this minimal-hosting
    /// factory). Set before the host is built. The default (false) daemon never maps
    /// <c>QueueSeedingService</c>, which is itself an assertion surface: seeding must be
    /// UNIMPLEMENTED there.
    /// </summary>
    public bool EnableQueueSeeding { get; init; }

    /// <summary>An independent, freshly-started in-proc daemon (own token + host).</summary>
    public static DaemonFixture StartNew()
    {
        var host = new DaemonFixture();
        _ = host.Services; // force host build
        return host;
    }

    /// <summary>The session token the running host authenticates against.</summary>
    public string Token => Services.GetRequiredService<SessionTokenFile>().Token;

    /// <summary>Formatted log lines captured from the daemon's logging pipeline.</summary>
    public IReadOnlyList<string> CapturedLogs => _logs.Lines;

    /// <summary>A gRPC channel over the in-proc test handler (attach metadata per call).</summary>
    public GrpcChannel CreateChannel()
        => GrpcChannel.ForAddress(Server.BaseAddress, new GrpcChannelOptions { HttpHandler = Server.CreateHandler() });

    /// <summary>Bearer metadata carrying the correct token (or an override for negatives).</summary>
    public Metadata AuthHeaders(string? token = null)
        => new() { { "authorization", $"bearer {token ?? Token}" } };

    /// <summary>Bearer metadata carrying a wrong token — the "wrong-token channel" factory.</summary>
    public Metadata WrongTokenHeaders()
        => new() { { "authorization", "bearer 0000000000000000000000000000000000000000000000000000000000000000" } };

    /// <summary>
    /// The memory reading every in-proc daemon test runs against. Matches
    /// <c>CoordinatorSpawnGateTests.Roomy()</c> — ~15 GB total with ~12 GB free, i.e. 25% used, far
    /// below <see cref="AdmissionController.DefaultUsedThreshold"/>. The <c>MemTotalKb</c> value doubles
    /// as a sentinel: no real machine reports exactly this, so a test can assert that the graph is
    /// reading THIS and not <c>/proc/meminfo</c>.
    /// </summary>
    public static MemorySample RoomySample => new(MemTotalKb: 16_000_000, MemAvailableKb: 12_000_000);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Isolate the on-disk token to a temp path so tests never touch the real
        // ~/.mainguard/daemon.token, and capture the daemon's logs for the mask test.
        builder.UseSetting("Daemon:TokenPath", _tokenPath);
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(_logs);
            logging.SetMinimumLevel(LogLevel.Trace);
        });

        builder.ConfigureTestServices(services =>
        {
            // MG-37 flake: the in-proc tier must not gate on the BOX's live memory.
            //
            // The production graph wires AdmissionController with its DEFAULT sampler, which reads
            // /proc/meminfo (GatewayServiceRegistration) — and the wired shim-spawn path consults it on
            // every spawn (AgentSpawnService -> CoordinatorSpawnGate.Evaluate -> CanSpawn). So on Linux
            // every `HandleShimRequestAsync("spawn")` in this tier was gated on however much memory the
            // machine happened to have free at that instant. A full `dotnet test` runs two test
            // assemblies alongside the MSBuild nodes that just built them; cross the 85% threshold for
            // one 5-second sample window (the controller caches that long) and exactly one shim spawn
            // comes back `Ok:false` with the honest "free memory or stop an agent" refusal, while the
            // re-run — a few seconds later, under no load — passes. That is the whole flake: not a race
            // inside the code under test, but an uncontrolled machine input reaching it.
            //
            // Pinned here rather than per-test because the exposure is the TIER's, not one test's, and
            // a per-test fix leaves the next shim-spawn test to rediscover it. Admission itself keeps
            // its deterministic coverage in CoordinatorSpawnGateTests, which injects its own sampler —
            // this replacement is registered before WiringRig's, so a test that wants to exercise
            // pressure can still override it.
            // Dev-only queue seeding (see EnableQueueSeeding): the singleton replacement IS the
            // in-proc boot flag — MapServices reads this instance to decide whether the seeding
            // service is mapped at all.
            if (EnableQueueSeeding)
            {
                services.Replace(ServiceDescriptor.Singleton(new QueueSeedingOptions(Enabled: true)));
            }

            // The plan-mode toggle gets a PER-HOST, in-memory store, for the same class of reason as
            // the admission sampler above: the tier must not share it, and the shared thing here is on
            // disk.
            //
            // `UseSetting("Daemon:TokenPath")` measurably does not reach `builder.Configuration` during
            // this factory's ConfigureServices (see EnableQueueSeeding's remarks), so every daemon store
            // path falls back to MainguardPaths.DataRoot() — which TestDataRootIsolation gives the WHOLE
            // assembly as one directory. That is already documented for the plan store (phase 2 §3: every
            // DaemonFixture rehydrates from the same one). For plan mode it would be worse than shared
            // state: one test turning the human approval gate OFF would turn it off for every host built
            // afterwards, in parallel, and the tests it broke would be the ones asserting that the gate
            // holds. Replaced here rather than per test, because the exposure is the tier's.
            //
            // A test that wants a specific starting state sets it on this instance; a test that wants to
            // assert the PERSISTENCE uses JsonPlanModeStore directly (PlanModeToggleTests).
            services.Replace(ServiceDescriptor.Singleton(
                new Mainguard.Agents.Agents.Orchestrator.PlanModeSwitch(
                    new Mainguard.Agents.Agents.Orchestrator.InMemoryPlanModeStore())));

            // The per-jail ceiling (2026-09-04) is the same shape: a file under the shared data root that
            // one test's Set would hand to every host built afterwards. Per-host, in memory; the JSON
            // persistence has its own test (JailLimitsSettingsTests).
            services.Replace(ServiceDescriptor.Singleton(
                new Mainguard.Agents.Agents.Sandbox.JailLimitsSettings(
                    new Mainguard.Agents.Agents.Sandbox.InMemoryJailLimitsStore())));

            services.Replace(ServiceDescriptor.Singleton(new AdmissionController(
                sampler: () => RoomySample,
                runningAgentCount: () => 0)));

            // Container-stats source override (see ResourceSampler). Left alone when unset, so the
            // default in-proc daemon keeps the production wiring.
            if (ResourceSampler is { } sampler)
            {
                services.Replace(ServiceDescriptor.Singleton(sampler));
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try
            {
                var dir = Path.GetDirectoryName(_tokenPath);
                if (dir is not null && Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // Never fail a test from cleanup.
            }
        }
    }

    /// <summary>A minimal in-memory logger provider — the G-13 field-mask log sink. Each captured line
    /// is prefixed with its <c>[category]</c> so the daemon-logging tests can assert which subsystem a
    /// line belongs to (e.g. <c>[mainguardd.Rpc]</c>); the mask assertions use <c>Contains</c>, so the
    /// prefix is transparent to them.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => _lines.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_lines, categoryName);

        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly ConcurrentQueue<string> _lines;
            private readonly string _category;

            public CapturingLogger(ConcurrentQueue<string> lines, string category)
            {
                _lines = lines;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _lines.Enqueue($"[{_category}] {formatter(state, exception)}");
            }
        }
    }
}
