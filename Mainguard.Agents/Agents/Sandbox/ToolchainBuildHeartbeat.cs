using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// Keeps a running toolchain-image build AUDIBLE: a line every <see cref="DefaultInterval"/> for as long
/// as the build call is outstanding, carrying how long it has been going and what the engine last said.
///
/// <para><b>Why a repeating line and not one "building…" announcement.</b> Everything upstream of the
/// build decides "is this alive or wedged?" from what it has heard recently — the client's spawn
/// watchdog and the control center's connect watchdog both re-arm on a new line. One announcement at
/// the start dates instantly: five minutes later the surface cannot tell a healthy 2.9 GB download from
/// a daemon that died mid-build, and it has to guess. It guessed wrong, in the direction that killed the
/// build.</para>
///
/// <para><b>What the line does and does not claim.</b> It claims the daemon is still inside the build
/// call — that much is a fact about this process, not a report the engine chose to make — and it quotes
/// the engine's own last output when there is any, plus how long ago that was. It deliberately does not
/// claim the engine is doing useful work: <c>dotnet-10</c>'s big step is
/// <c>curl -fsSL … | tar -xzf</c>, which is silent by construction, so "no engine output for 4m" is
/// perfectly normal there and must not be reported as a fault. Gating the heartbeat on engine output
/// would therefore declare the healthiest, longest step dead — which is the original bug wearing a
/// different hat. The bound on a genuinely wedged engine is the caller's hard cap, not this.</para>
/// </summary>
public sealed class ToolchainBuildHeartbeat : IProgress<string>, IAsyncDisposable
{
    /// <summary>How often a running build reports in.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(20);

    private readonly IProgress<string>? _sink;
    private readonly Func<TimeSpan, string?, TimeSpan?, string> _compose;
    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<TimeSpan> _elapsed;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly object _sync = new();

    private string? _lastEngineLine;
    private TimeSpan? _lastEngineAt;

    /// <param name="sink">The user-facing progress sink (the spawn's <c>IProgress&lt;string&gt;</c>), or
    /// null — a null sink makes this an inert observer, exactly as it does everywhere else on this path.</param>
    /// <param name="compose">Builds the reported line from (elapsed, last engine line, how long ago that
    /// line arrived). Injected so the wording lives with the rest of the provisioner's copy.</param>
    /// <param name="interval">Report cadence (default <see cref="DefaultInterval"/>).</param>
    /// <param name="delay">The wait between reports. Injectable so a test drives the cadence instead of
    /// sleeping through it.</param>
    /// <param name="elapsed">Time since the build began. Injectable for the same reason.</param>
    public ToolchainBuildHeartbeat(
        IProgress<string>? sink,
        Func<TimeSpan, string?, TimeSpan?, string> compose,
        TimeSpan? interval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<TimeSpan>? elapsed = null)
    {
        _sink = sink;
        _compose = compose ?? throw new ArgumentNullException(nameof(compose));
        _interval = interval is { } i && i > TimeSpan.Zero ? i : DefaultInterval;
        _delay = delay ?? Task.Delay;
        var started = System.Diagnostics.Stopwatch.StartNew();
        _elapsed = elapsed ?? (() => started.Elapsed);
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    /// <summary>How many lines this heartbeat has reported. Tests only.</summary>
    internal int Reported { get; private set; }

    /// <summary>The engine's build output arrives here (one call per message the builder receives).</summary>
    public void Report(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        lock (_sync)
        {
            _lastEngineLine = Trim(value);
            _lastEngineAt = _elapsed();
        }
    }

    /// <summary>Stops the pump and waits for it, so no line can be reported after the build it describes
    /// has finished — a progress line that arrives after the wait is over is worse than none.</summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected — the pump ends by cancellation.
        }

        _cts.Dispose();
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _delay(_interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            string? line;
            TimeSpan? at;
            lock (_sync)
            {
                line = _lastEngineLine;
                at = _lastEngineAt;
            }

            var now = _elapsed();
            try
            {
                _sink?.Report(_compose(now, line, at is { } stamp ? now - stamp : null));
                Reported++;
            }
            catch (Exception)
            {
                // A progress sink that throws must never fail the build it is describing, and must never
                // end the pump: the next tick reports again.
            }
        }
    }

    /// <summary>One short line of engine output — build streams arrive as multi-line chunks and the
    /// heartbeat has room for a fragment, not a log.</summary>
    private static string Trim(string chunk)
    {
        const int max = 120;
        var lines = chunk.Split(
            new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var text = lines.Length > 0 ? lines[^1] : chunk.Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    /// <summary>A compact human duration ("40s", "6m20s") — the heartbeat's whole job is to be read by a
    /// person watching a loader, and <see cref="TimeSpan.ToString()"/> is not.</summary>
    public static string Describe(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var total = (int)elapsed.TotalSeconds;
        return total < 60
            ? total.ToString(CultureInfo.InvariantCulture) + "s"
            : (total / 60).ToString(CultureInfo.InvariantCulture) + "m"
              + (total % 60).ToString("00", CultureInfo.InvariantCulture) + "s";
    }
}
