using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using Mainguard.Git.Models;

namespace Mainguard.Git.Services;

/// <summary>
/// Background auto-fetch (T-10). Keeps each watched repository's ahead/behind
/// picture fresh by periodically fetching (with prune) off the UI thread. A single
/// <see cref="PeriodicTimer"/> loop drives every watched repo — there is no
/// <c>DispatcherTimer</c> here (Core stays UI-agnostic; rule G-5).
///
/// Invariants:
/// <list type="bullet">
/// <item>Never runs concurrently with itself for the same repo (per-repo in-flight guard).</item>
/// <item>Skips a repo mid merge/rebase (<see cref="CurrentOperation"/> != None) so it never interferes.</item>
/// <item>Skips entirely when <see cref="UserPreferences.AutoFetchMinutes"/> is 0 (disabled).</item>
/// <item>Fetch failures are counted and surfaced via <see cref="FetchFailed"/> — never thrown.</item>
/// <item>Consecutive failures back the repo off exponentially (<see cref="MaxBackoffCycles"/> cap),
/// so a dead remote is retried at a decreasing rate instead of once a minute forever.</item>
/// </list>
///
/// Determinism: the timer cadence and clock are test seams (<see cref="IntervalOverride"/>,
/// <see cref="Clock"/>) and <see cref="RunCycleAsync"/> performs exactly one fetch pass,
/// so the cadence / skip / overlap / error behaviour is asserted without real waiting.
/// </summary>
public sealed class AutoFetchService : IDisposable
{
    private readonly IGitService _git;
    private readonly Func<UserPreferences> _prefs;

    private readonly ConcurrentDictionary<string, byte> _watched = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastFetched = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.Ordinal);
    /// <summary>Cycles still to skip for a repo before its next retry (the backoff counter).</summary>
    private readonly ConcurrentDictionary<string, int> _cooldown = new(StringComparer.Ordinal);

    /// <summary>The backoff ceiling, in cycles. At the default 5-minute cadence a repo whose remote is
    /// gone settles at one attempt per ~80 minutes rather than one every five — enough to notice a token
    /// being renewed, cheap enough to leave running all day.</summary>
    internal const int MaxBackoffCycles = 16;

    private readonly object _loopLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public AutoFetchService(IGitService git, Func<UserPreferences> prefs)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _prefs = prefs ?? throw new ArgumentNullException(nameof(prefs));
    }

    /// <summary>Raised on the thread pool after a repo fetches successfully.</summary>
    public event Action<string /*repoPath*/>? Fetched;

    /// <summary>
    /// Raised when a fetch fails, carrying the running consecutive-failure count and the reason, so the
    /// UI can show a subtle warning state (no modal / toast spam).
    ///
    /// <para>Nothing in the app subscribed to this. The one production consumer
    /// (<c>RepoDashboardViewModel</c>) listened to <see cref="Fetched"/> alone, so an expired token or a
    /// deleted remote left "Fetched N min ago" quietly frozen at its last success while the ahead/behind
    /// badge beside it went stale — the user's only signal that their branch is behind, silently wrong,
    /// with the label still saying it was checked recently.</para>
    /// </summary>
    public event Action<string /*repoPath*/, int /*consecutiveFailures*/, string /*reason*/>? FetchFailed;

    /// <summary>Test seam: overrides the derived <c>AutoFetchMinutes</c> cadence. Never set in production.</summary>
    internal TimeSpan? IntervalOverride { get; set; }

    /// <summary>Test seam: the clock used for the last-fetched timestamp.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    public DateTimeOffset? GetLastFetched(string repoPath)
        => _lastFetched.TryGetValue(repoPath, out var t) ? t : null;

    /// <summary>Consecutive fetch failures for a repo (reset to 0 on the next success).</summary>
    public int GetFailureCount(string repoPath)
        => _failures.TryGetValue(repoPath, out var c) ? c : 0;

    /// <summary>Begins periodic fetch for a repo. Idempotent; starts the loop on first watch.</summary>
    public void Watch(string repoPath)
    {
        if (string.IsNullOrEmpty(repoPath)) return;
        _watched[repoPath] = 0;
        EnsureLoopStarted();
    }

    public void Unwatch(string repoPath)
    {
        if (string.IsNullOrEmpty(repoPath)) return;
        _watched.TryRemove(repoPath, out _);
    }

    private void EnsureLoopStarted()
    {
        lock (_loopLock)
        {
            if (_disposed || _loop != null) return;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Recreate the timer each iteration so a preferences change to the cadence
            // takes effect on the next cycle without a restart. Even when auto-fetch is
            // disabled the timer still ticks (cheaply) and the cycle no-ops.
            var interval = IntervalOverride
                ?? TimeSpan.FromMinutes(Math.Max(1, _prefs().AutoFetchMinutes));

            using var timer = new PeriodicTimer(interval);
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) { break; }

            await RunCycleAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Performs exactly one fetch pass over the watched set and completes when this
    /// pass's fetches finish. Repos disabled, mid-operation, or already fetching are
    /// skipped. Exposed for deterministic testing of cadence / skip / overlap / error.
    /// </summary>
    internal async Task RunCycleAsync()
    {
        // Disabled: nothing to do this cycle.
        if (_prefs().AutoFetchMinutes <= 0) return;

        var tasks = new List<Task>();
        foreach (var repoPath in _watched.Keys.ToList())
        {
            var task = TryFetch(repoPath);
            if (task != null) tasks.Add(task);
        }

        if (tasks.Count > 0)
            await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    // Claims the per-repo in-flight guard synchronously (so a concurrent cycle reliably
    // sees it and skips), then runs the fetch off the pool. Returns null when skipped.
    private Task? TryFetch(string repoPath)
    {
        // Skip while a git operation (merge/rebase/etc.) is in progress — never interfere.
        try
        {
            if (_git.GetCurrentOperation(repoPath) != CurrentOperation.None) return null;
        }
        catch
        {
            // Repo vanished / unreadable: treat as a skip, not a crash.
            return null;
        }

        // Failure backoff: a repo that just failed sits out the next few cycles. The counter is
        // consumed here rather than in the catch so one cycle == one decrement regardless of how many
        // repos are watched. Without this, a remote that is simply gone re-ran the same failing fetch
        // (and its process spawn) every single cadence tick for as long as the app stayed open.
        if (_cooldown.TryGetValue(repoPath, out var remaining) && remaining > 0)
        {
            _cooldown[repoPath] = remaining - 1;
            return null;
        }

        // Per-repo overlap guard: only one auto-fetch per repo at a time.
        if (!_inFlight.TryAdd(repoPath, 0)) return null;

        return Task.Run(() =>
        {
            try
            {
                _git.Fetch(repoPath, prune: true);
                _lastFetched[repoPath] = Clock();
                _failures[repoPath] = 0;
                _cooldown[repoPath] = 0;
                Fetched?.Invoke(repoPath);
            }
            catch (Exception ex)
            {
                var count = _failures.AddOrUpdate(repoPath, 1, (_, c) => c + 1);
                _cooldown[repoPath] = BackoffCycles(count);
                FetchFailed?.Invoke(repoPath, count, ex.Message);
            }
            finally
            {
                _inFlight.TryRemove(repoPath, out _);
            }
        });
    }

    /// <summary>Cycles to skip after <paramref name="consecutiveFailures"/> failures: 1, 2, 4, 8, …
    /// capped at <see cref="MaxBackoffCycles"/>. The first failure costs one skipped cycle, so a single
    /// blip barely delays recovery.</summary>
    internal static int BackoffCycles(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0) return 0;
        var cycles = 1;
        for (var i = 1; i < consecutiveFailures && cycles < MaxBackoffCycles; i++) cycles *= 2;
        return Math.Min(cycles, MaxBackoffCycles);
    }

    public void Dispose()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_loopLock)
        {
            if (_disposed) return;
            _disposed = true;
            cts = _cts;
            loop = _loop;
        }

        try { cts?.Cancel(); } catch { }
        try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        cts?.Dispose();
    }
}
