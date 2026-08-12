using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Mainguard.Agents.UI.Services;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The spawn deadline, measured against the daemon's last sign of life.
///
/// <para><b>What was wrong.</b> <c>SpawnAgent</c> carried a flat 5-minute gRPC deadline. A first run for
/// a repository builds its toolchain image inside that call — ~2.9 GB for <c>dotnet-10</c>, routinely
/// longer than five minutes on a fresh machine — so the client hung up on a launch that was working, the
/// daemon tore the half-made spawn down as a failed one, and the retry started the build over. It was
/// written into the hands-on guide as a known issue, which is a defect documented rather than fixed.</para>
///
/// <para><b>Why not a bigger number.</b> A bigger constant moves the cliff: at 20 minutes a slow link
/// fails at 20, at an hour a wedged spawn hangs for an hour. The constant was never the problem — the
/// question was. "How long has this taken?" cannot separate a healthy build from a dead daemon;
/// "has it said anything lately?" can, and since the launcher reports progress the client can ask it.</para>
///
/// <para>These tests pin both directions, because either one alone is trivially satisfiable: a build
/// that keeps reporting is never cut off (delete the re-arm and the first test fails), and a spawn that
/// goes quiet still fails — legibly, quoting what it last said (delete the timer and the second fails).
/// The trade being refused is "no false timeout" bought with "waits forever".</para>
/// </summary>
public class SpawnProgressWatchdogTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ASpawnThatKeepsReportingProgress_RunsFarPastTheSilenceBudget()
    {
        var time = new SteppedTime();
        var watchdog = new SpawnProgressWatchdog(Budget, time.Delay, () => time.Now);

        var call = new BlockingCall();
        var run = watchdog.RunAsync(call.RunAsync, CancellationToken.None);

        // Ten half-budgets of simulated time — five times the old flat deadline — with the daemon
        // reporting a fresh line each time, exactly as a running toolchain build now does.
        for (var i = 0; i < 10; i++)
        {
            watchdog.NoteProgress($"Still building this repository's toolchain image — beat {i}");
            await time.AdvanceAsync(Budget / 2, Patience);
            Assert.False(run.IsCompleted, $"the spawn was cut off after {i} progress lines");
        }

        call.Complete("agent-7");
        Assert.Equal("agent-7", await run.WaitAsync(Patience));
        Assert.False(call.WasCancelled);
    }

    [Fact]
    public async Task ASpawnThatGoesSILENT_IsCancelled_AndSaysWhatItLastHeard()
    {
        var time = new SteppedTime();
        var watchdog = new SpawnProgressWatchdog(Budget, time.Delay, () => time.Now);

        var call = new BlockingCall();
        var run = watchdog.RunAsync(call.RunAsync, CancellationToken.None);

        watchdog.NoteProgress("Building this repository's toolchain image (dotnet-10).");
        await time.AdvanceAsync(Budget / 2, Patience);   // half the budget — still fine
        Assert.False(run.IsCompleted);

        time.Post(Budget);                               // …and then nothing at all for a full budget
        var failure = Assert.IsType<TimeoutException>(await FailureOfAsync(run));

        Assert.True(call.WasCancelled); // the call really was aborted, not merely reported on
        Assert.Contains("stopped reporting progress for 5m00s", failure.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet-10", failure.Message, StringComparison.Ordinal);
        Assert.Contains("torn down", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A spawn that never reported anything is the ordinary "daemon is not answering" case, and
    /// it must still end — with a message that does not pretend to quote a line that never existed.</summary>
    [Fact]
    public async Task ASpawnThatNeverSaidAnything_StillFails_WithoutInventingALastLine()
    {
        var time = new SteppedTime();
        var watchdog = new SpawnProgressWatchdog(Budget, time.Delay, () => time.Now);

        var call = new BlockingCall();
        var run = watchdog.RunAsync(call.RunAsync, CancellationToken.None);

        time.Post(Budget);
        var failure = Assert.IsType<TimeoutException>(await FailureOfAsync(run));

        Assert.Contains("never reported anything at all", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("The last thing it reported", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The user pressing Stop is not a fault, and must not be dressed as one: the control center's stop
    /// path owns that messaging, and a <see cref="TimeoutException"/> here would render an error the user
    /// caused on purpose.
    /// </summary>
    [Fact]
    public async Task AUserCancel_StaysACancel_NotATimeout()
    {
        var time = new SteppedTime();
        var watchdog = new SpawnProgressWatchdog(Budget, time.Delay, () => time.Now);

        using var cts = new CancellationTokenSource();
        var call = new BlockingCall();
        var run = watchdog.RunAsync(call.RunAsync, cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Patience));
    }

    /// <summary>
    /// The exception a run ended with — asserting FIRST that it ended at all.
    ///
    /// <para>Written this way on purpose: <c>Task.WaitAsync(timeout)</c> throws
    /// <see cref="TimeoutException"/> itself, so <c>Assert.ThrowsAsync&lt;TimeoutException&gt;(() =&gt;
    /// run.WaitAsync(...))</c> would go green for a watchdog that never cancelled anything — the test
    /// would be measuring its own patience. Verified: with the cancel removed, this reports "never
    /// ended" rather than passing.</para>
    /// </summary>
    private static async Task<Exception> FailureOfAsync(Task run)
    {
        var finished = await Task.WhenAny(run, Task.Delay(Patience));
        Assert.True(
            ReferenceEquals(finished, run),
            "the spawn never ended — the watchdog did not cancel the call it was watching");

        var failure = await Record.ExceptionAsync(() => run);
        Assert.NotNull(failure);
        return failure!;
    }

    /// <summary>
    /// Simulated time the test steps by hand: the watchdog's wait blocks until the test posts an advance,
    /// and <see cref="AdvanceAsync"/> does not return until the watchdog has actually consumed it. Real
    /// sleeps would make these tests both slow and racy — and a race here would silently weaken the
    /// property, since a watchdog that never gets to run also never trips.
    /// </summary>
    private sealed class SteppedTime
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

    /// <summary>Stands in for the SpawnAgent RPC: never returns on its own, ends when the caller's token
    /// is cancelled (which is what the real gRPC call does) or when the test completes it.</summary>
    private sealed class BlockingCall
    {
        private readonly TaskCompletionSource<string> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasCancelled { get; private set; }

        public void Complete(string value) => _result.TrySetResult(value);

        public Task<string> RunAsync(CancellationToken ct)
        {
            ct.Register(() =>
            {
                WasCancelled = true;
                _result.TrySetCanceled(ct);
            });
            return _result.Task;
        }
    }
}
