using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.UI.Services;
using Proto = Mainguard.Protos.V1;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The wire between the daemon's launch progress and the client's spawn deadline.
///
/// <para><see cref="SpawnProgressWatchdogTests"/> pins the watchdog's behaviour in isolation; this pins
/// that the orchestrator actually FEEDS it — from the one place the evidence arrives, the agent-event
/// stream. That join is where this fix can silently become inert: the watchdog would still be correct,
/// the daemon would still be reporting, and a healthy 2.9 GB build would still be cancelled at five
/// minutes, exactly as before. So the assertion is made through the real
/// <see cref="DaemonBackedOrchestrator.ApplyAgentEvent"/> with real <c>AgentEvent</c> messages carrying
/// the daemon's own wire vocabulary ("Starting"), not through a stub.</para>
/// </summary>
public class SpawnWatchdogWiringTests : IDisposable
{
    private readonly TimeSpan _previousBudget = DaemonBackedOrchestrator.SpawnSilenceBudget;

    public void Dispose() => DaemonBackedOrchestrator.SpawnSilenceBudget = _previousBudget;

    private static DaemonBackedOrchestrator NewOrchestrator() =>
        // Never started and never called: the pumps are what would touch the daemon, and this test drives
        // ApplyAgentEvent directly, which is where a live pump would deliver.
        new(DaemonClient.ForLoopback(), ownsClient: false);

    private static Proto.AgentEvent LaunchProgress(string agentId, string reason) => new()
    {
        AgentId = agentId,
        State = new Proto.StateChange { State = "Starting", Reason = reason },
    };

    [Fact]
    public async Task LaunchProgressDeltas_KeepASpawnAliveWellPastTheSilenceBudget()
    {
        DaemonBackedOrchestrator.SpawnSilenceBudget = TimeSpan.FromMilliseconds(300);
        var orchestrator = NewOrchestrator();

        var call = new NeverEndingCall();
        var spawn = orchestrator.SpawnUnderWatchdogAsync<string>(
            (token, _) => call.RunAsync(token), CancellationToken.None);

        // Two seconds — nearly seven budgets — of the daemon reporting a running toolchain build.
        var stop = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        var beat = 0;
        while (DateTime.UtcNow < stop)
        {
            orchestrator.ApplyAgentEvent(LaunchProgress(
                "coordinator-1", $"Still building this repository's toolchain image (dotnet-10) — beat {beat++}"));
            await Task.Delay(40);
            Assert.False(spawn.IsCompleted, $"the spawn was cancelled after {beat} progress lines");
        }

        Assert.True(beat > 5); // the loop really did report repeatedly
        call.Complete("agent-1");
        Assert.Equal("agent-1", await spawn.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// The same wiring, with the daemon quiet — the half that keeps the test above from being a test of
    /// nothing. Removing the timer entirely would pass the first test and fail this one.
    /// </summary>
    [Fact]
    public async Task WithNoLaunchProgress_TheSpawnStillFails_AndSaysSo()
    {
        DaemonBackedOrchestrator.SpawnSilenceBudget = TimeSpan.FromMilliseconds(300);
        var orchestrator = NewOrchestrator();

        var call = new NeverEndingCall();
        var spawn = orchestrator.SpawnUnderWatchdogAsync<string>(
            (token, _) => call.RunAsync(token), CancellationToken.None);

        var failure = Assert.IsType<TimeoutException>(await FailureOfAsync(spawn));
        Assert.Contains("stopped reporting progress", failure.Message, StringComparison.Ordinal);
        Assert.True(call.WasCancelled);
    }

    /// <summary>
    /// Only a PROVISIONING delta is evidence about a spawn. A running agent's ordinary state chatter must
    /// not vouch for one — otherwise a busy fleet would keep a genuinely wedged launch alive forever, and
    /// the bounded wait this change promises would quietly become an unbounded one.
    /// </summary>
    [Fact]
    public async Task ARunningAgentsChatter_DoesNotCountAsSpawnProgress()
    {
        DaemonBackedOrchestrator.SpawnSilenceBudget = TimeSpan.FromMilliseconds(300);
        var orchestrator = NewOrchestrator();

        var call = new NeverEndingCall();
        var spawn = orchestrator.SpawnUnderWatchdogAsync<string>(
            (token, _) => call.RunAsync(token), CancellationToken.None);

        // A different, already-running agent talking away the whole time.
        using var chatter = new CancellationTokenSource();
        var noise = Task.Run(async () =>
        {
            var i = 0;
            while (!chatter.IsCancellationRequested)
            {
                orchestrator.ApplyAgentEvent(new Proto.AgentEvent
                {
                    AgentId = "worker-9",
                    State = new Proto.StateChange { State = "Working", Reason = $"rate limited {i++}" },
                });
                await Task.Delay(20);
            }
        });

        var failure = Assert.IsType<TimeoutException>(await FailureOfAsync(spawn));
        Assert.Contains("stopped reporting progress", failure.Message, StringComparison.Ordinal);

        chatter.Cancel();
        await noise;
    }

    /// <summary>
    /// The exception a spawn ended with, asserting FIRST that it ended. Not
    /// <c>Assert.ThrowsAsync&lt;TimeoutException&gt;(() =&gt; task.WaitAsync(budget))</c>: that form
    /// throws its OWN TimeoutException when the task never finishes, so it goes green for a watchdog
    /// that cancels nothing — a test measuring its own patience.
    /// </summary>
    private static async Task<Exception> FailureOfAsync(Task spawn)
    {
        var finished = await Task.WhenAny(spawn, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(
            ReferenceEquals(finished, spawn),
            "the spawn never ended — nothing bounded the wait");

        var failure = await Record.ExceptionAsync(() => spawn);
        Assert.NotNull(failure);
        return failure!;
    }

    private sealed class NeverEndingCall
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
