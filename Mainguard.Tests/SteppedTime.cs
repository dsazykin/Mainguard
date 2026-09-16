using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Mainguard.Tests;

/// <summary>
/// Simulated time the test steps by hand: the watchdog's wait blocks until the test posts an advance,
/// and <see cref="AdvanceAsync"/> does not return until the watchdog has actually consumed it. Real
/// sleeps would make these tests both slow and racy — and a race here would silently weaken the
/// property, since a watchdog that never gets to run also never trips.
///
/// <para>Shared by <c>SpawnProgressWatchdogTests</c> (the watchdog in isolation) and
/// <c>SpawnWatchdogWiringTests</c> (the same watchdog reached through
/// <c>DaemonBackedOrchestrator</c>). It began as a private helper of the first and moved here when the
/// second needed it: a compressed REAL budget is still a real race, and the keep-alive test in the
/// wiring suite failed on CI because a thread-pool stall outlasted its 300 ms budget, which made the
/// watchdog trip correctly and the test fail anyway. Simulated time is what makes "progress keeps the
/// spawn alive" a statement about the wiring rather than about the runner's scheduling.</para>
/// </summary>
internal sealed class SteppedTime
{
    private readonly Channel<TimeSpan> _pending = Channel.CreateUnbounded<TimeSpan>();
    private readonly Channel<bool> _consumed = Channel.CreateUnbounded<bool>();
    private long _ticks;

    public TimeSpan Now => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public Func<TimeSpan, CancellationToken, Task> Delay => async (_, ct) =>
    {
        var step = await _pending.Reader.ReadAsync(ct);
        Interlocked.Add(ref _ticks, step.Ticks);
        await _consumed.Writer.WriteAsync(true, CancellationToken.None);
    };

    /// <summary>The elapsed-time source to hand the watchdog beside <see cref="Delay"/>.</summary>
    public Func<TimeSpan> Clock => () => Now;

    /// <summary>Post an advance without waiting for it — for the step that is expected to trip the
    /// watchdog, which then never reports back.</summary>
    public void Post(TimeSpan by) => _pending.Writer.TryWrite(by);

    public async Task AdvanceAsync(TimeSpan by, TimeSpan patience)
    {
        Post(by);
        using var cts = new CancellationTokenSource(patience);
        try
        {
            await _consumed.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // The watchdog stopped waiting on time — it gave up on the spawn instead of re-arming.
            // Said plainly here, because as a bare cancellation it reads like a test-harness fault.
            throw new InvalidOperationException(
                "the watchdog never consumed the advance — it stopped watching, which means it "
                + "tripped instead of re-arming on the progress just reported");
        }
    }
}
