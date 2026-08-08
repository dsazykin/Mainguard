using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Sandbox;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The spawn RPC's deadline, measured from the daemon's last sign of life instead of from the start of
/// the call.
///
/// <para><b>The bug this replaces.</b> <c>SpawnAgent</c> carried a flat 5-minute gRPC deadline. A cold
/// first run builds the repository's toolchain image inside that call — <c>dotnet-10</c> is ~2.9 GB and
/// routinely runs longer than five minutes on a fresh machine — so the client hung up on a launch that
/// was working. That is not a cosmetic timeout: the deadline cancels the server call, the daemon's spawn
/// path treats the cancellation as a failed spawn and tears the session and worktree down, and the next
/// attempt starts the same multi-gigabyte build again. Every fresh environment hit it, every time.</para>
///
/// <para><b>Why not simply a bigger number.</b> A bigger constant moves the cliff without removing it:
/// pick 20 minutes and a slow link fails at 20, pick an hour and a genuinely wedged spawn hangs for an
/// hour. The size of the constant was never the problem — measuring the wrong thing was. What the client
/// actually needs to know is not "how long has this taken?" but "has the daemon done anything lately?",
/// and since PR #319/#320 it can answer that: the launcher reports progress, <c>AgentSpawnService</c>
/// turns each line into a state delta on the spawning session, and those arrive on the agent-event
/// stream this client is already reading. So the budget here bounds SILENCE. A build that keeps
/// reporting can run for as long as it needs; a spawn that says nothing for the budget is reported,
/// with what it last said and how long ago.</para>
///
/// <para><b>And an outer bound, still.</b> Silence is not the only failure — a daemon stuck in a loop
/// that keeps emitting lines would never trip this. The caller therefore also passes a hard cap as the
/// gRPC deadline (<see cref="DaemonBackedOrchestrator"/>), so no spawn can wait forever under any
/// combination. A false timeout was traded for a bounded one, never for an infinite wait.</para>
/// </summary>
internal sealed class SpawnProgressWatchdog
{
    private readonly TimeSpan _silenceBudget;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<TimeSpan> _clock;
    private readonly object _sync = new();

    private TimeSpan _lastSignAt;
    private string? _lastLine;
    private bool _trippedOnSilence;

    /// <param name="silenceBudget">How long the daemon may report nothing before the spawn is declared
    /// unresponsive.</param>
    /// <param name="delay">The wait primitive (injectable so a test drives it).</param>
    /// <param name="clock">Elapsed time since this watchdog was created (injectable for the same reason).</param>
    public SpawnProgressWatchdog(
        TimeSpan silenceBudget,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<TimeSpan>? clock = null)
    {
        _silenceBudget = silenceBudget > TimeSpan.Zero ? silenceBudget : TimeSpan.FromMinutes(5);
        _delay = delay ?? Task.Delay;
        var started = Stopwatch.StartNew();
        _clock = clock ?? (() => started.Elapsed);
        _lastSignAt = _clock();
    }

    /// <summary>The last thing the daemon said about this spawn, or null when it has said nothing.</summary>
    public string? LastLine
    {
        get { lock (_sync) return _lastLine; }
    }

    /// <summary>
    /// Records a sign of life from the daemon for the spawn in flight. Called from the agent-event pump,
    /// off the UI thread, for every launch-progress delta.
    /// </summary>
    public void NoteProgress(string line)
    {
        lock (_sync)
        {
            _lastSignAt = _clock();
            if (!string.IsNullOrWhiteSpace(line))
            {
                _lastLine = line.Trim();
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="call"/> under the silence budget. Returns whatever the call returns; throws
    /// <see cref="TimeoutException"/> — carrying the last thing the daemon said and how long it has been
    /// quiet — when the budget expires with the call still outstanding.
    /// </summary>
    /// <exception cref="TimeoutException">The daemon went silent for the whole budget.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled (the user pressed
    /// Stop). Deliberately NOT converted into a timeout: the user's own cancel is not a fault, and the
    /// caller's Stop path owns that messaging.</exception>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var watchdogDone = new CancellationTokenSource();
        var watchdog = WatchAsync(linked, watchdogDone.Token);
        try
        {
            return await call(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The call ended badly. If OUR timer is why, say so in the caller's language rather than
            // letting a bare CANCELLED/DEADLINE_EXCEEDED reach the surface as "the daemon didn't answer".
            bool tripped;
            string? last;
            lock (_sync)
            {
                tripped = _trippedOnSilence;
                last = _lastLine;
            }

            if (!tripped)
            {
                throw;
            }

            var quiet = ToolchainBuildHeartbeat.Describe(_silenceBudget);
            throw new TimeoutException(
                $"the daemon stopped reporting progress for {quiet} while starting the agent. "
                + (last is { Length: > 0 }
                    ? $"The last thing it reported was: {last}"
                    : "It never reported anything at all — check that the daemon is running.")
                + " The partial spawn was torn down; starting again is safe.",
                ex);
        }
        finally
        {
            watchdogDone.Cancel();
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected — the watchdog ends by cancellation once the call has returned.
            }
        }
    }

    private async Task WatchAsync(CancellationTokenSource callCts, CancellationToken done)
    {
        while (!done.IsCancellationRequested)
        {
            TimeSpan remaining;
            lock (_sync)
            {
                remaining = _silenceBudget - (_clock() - _lastSignAt);
            }

            if (remaining <= TimeSpan.Zero)
            {
                lock (_sync)
                {
                    // Re-checked under the lock: a progress line that landed while we were deciding is a
                    // sign of life and must win, or a healthy spawn could be killed by a race.
                    if (_silenceBudget - (_clock() - _lastSignAt) > TimeSpan.Zero)
                    {
                        continue;
                    }

                    _trippedOnSilence = true;
                }

                // Cancelling the call is what makes this a deadline rather than a warning — and it is the
                // same cancellation the daemon already handles: the spawn is torn down, nothing is left
                // half-made, and the user can start again.
                callCts.Cancel();
                return;
            }

            try
            {
                await _delay(remaining, done).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
