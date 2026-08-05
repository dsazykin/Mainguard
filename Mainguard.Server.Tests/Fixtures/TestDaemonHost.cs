using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Server.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>
/// Starts REAL Kestrel daemons for the out-of-proc test tier (MG-19 transport, reconnect, gateway bind),
/// on a port from <see cref="TestPorts"/>, retrying the bind on a fresh port when the address turns out
/// to be taken.
///
/// <para>The retry lives HERE, once, rather than at each call site — the four duplicated
/// <c>FreePort()</c> helpers this replaces are exactly how the problem drifted in the first place. It is
/// not redundant with <see cref="TestPorts"/>: the allocator guarantees this process never issues a port
/// twice, but an OS-assigned port is released before the daemon binds it, so a <i>foreign</i> process on
/// the runner can still win the race. Only a retry survives that.</para>
///
/// <para>Retries are bounded and narrow: <see cref="MaxStartAttempts"/> attempts, and only when
/// <see cref="TestPorts.IsAddressInUse"/> recognises the failure. Any other startup failure is rethrown
/// on the first attempt, which is what keeps the gateway tests' "must fail loudly" assertions honest.</para>
///
/// <para>This tier cannot be replaced by <see cref="DaemonFixture"/>: <c>WebApplicationFactory</c> swaps
/// Kestrel for a <c>TestServer</c>, so the transport never runs and a transport regression is invisible.</para>
/// </summary>
internal static class TestDaemonHost
{
    /// <summary>Bind attempts before giving up. Bounded so a genuinely wedged runner fails fast and loudly.</summary>
    internal const int MaxStartAttempts = 4;

    /// <summary>A throwaway session-token path (its directory also holds the transport credentials).</summary>
    public static string TempTokenPath(string prefix = "mainguard-tok")
        => Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"), "daemon.token");

    /// <summary>
    /// Starts a daemon from <paramref name="template"/> with a freshly leased control port (and, when the
    /// gateway is enabled, a freshly leased gateway port), retrying on address-in-use.
    /// </summary>
    public static Task<RunningDaemon> StartAsync(
        DaemonOptions template, CancellationToken ct = default,
        Action<IServiceCollection>? configureServices = null)
        => StartAsync(template, TestPorts.Lease, ct, configureServices);

    /// <summary>
    /// The port-source seam. Only the allocator's own tests pass <paramref name="leasePort"/> — it is how
    /// they force the first attempt onto a port that is already bound and then observe that startup still
    /// succeeds on the next one.
    /// </summary>
    internal static async Task<RunningDaemon> StartAsync(
        DaemonOptions template, Func<int> leasePort, CancellationToken ct = default,
        Action<IServiceCollection>? configureServices = null)
    {
        var failures = new List<Exception>();

        for (var attempt = 1; attempt <= MaxStartAttempts; attempt++)
        {
            var options = template with { Port = leasePort() };
            if (!string.IsNullOrWhiteSpace(options.GatewayBindAddress))
            {
                options = options with { GatewayPort = leasePort() };
            }

            try
            {
                var app = await DaemonHost.StartAsync(options, ct, configureServices);
                return new RunningDaemon(app, options, attempt);
            }
            catch (Exception ex) when (TestPorts.IsAddressInUse(ex))
            {
                // Someone else on the box took the port between the lease and the bind. Try another.
                failures.Add(ex);
            }
        }

        throw new AggregateException(
            $"A real daemon host could not bind a free loopback port in {MaxStartAttempts} attempts; " +
            "every attempt lost the port to another process.",
            failures);
    }

    /// <summary>
    /// A started real daemon plus the facts its tests need: the port it actually bound (which is NOT the
    /// one the caller asked for — the caller does not choose), its session token, and how many bind
    /// attempts it took (so a retry test can prove the retry happened rather than the first try working).
    /// </summary>
    internal sealed class RunningDaemon : IAsyncDisposable
    {
        private readonly DaemonOptions _options;

        internal RunningDaemon(WebApplication app, DaemonOptions options, int startAttempts)
        {
            App = app;
            _options = options;
            StartAttempts = startAttempts;
        }

        /// <summary>The running host.</summary>
        public WebApplication App { get; }

        /// <summary>The loopback port the gRPC control plane actually bound.</summary>
        public int Port => _options.Port;

        /// <summary>The model-gateway port (meaningful only when the gateway was enabled).</summary>
        public int GatewayPort => _options.GatewayPort;

        /// <summary>The session-token file this daemon minted its token and transport credentials into.</summary>
        public string TokenPath => _options.TokenPath!;

        /// <summary>How many bind attempts it took to come up. 1 on an uncontended runner.</summary>
        public int StartAttempts { get; }

        /// <summary>The daemon's live session token.</summary>
        public string Token => App.Services.GetRequiredService<SessionTokenFile>().Token;

        /// <summary>The host's service provider.</summary>
        public IServiceProvider Services => App.Services;

        /// <summary>The addresses Kestrel actually bound (the loopback-only assertions read this).</summary>
        public IReadOnlyList<string> Urls => App.Urls.ToList();

        /// <summary>Stops the host without disposing it — a deliberate mid-test "the daemon died".</summary>
        public Task StopAsync() => App.StopAsync();

        public async ValueTask DisposeAsync()
        {
            try
            {
                await App.StopAsync();
            }
            catch
            {
                // Already stopped by the test — disposal must not mask the real assertion.
            }

            await App.DisposeAsync();
        }
    }
}
