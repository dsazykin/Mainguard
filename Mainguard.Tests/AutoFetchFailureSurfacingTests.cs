using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Tests.Fixtures;
using Xunit;
using Repository = Mainguard.Git.Models.Repository;

namespace Mainguard.Tests;

/// <summary>
/// Auto-fetch failures used to be counted, raised on <c>FetchFailed</c>, and observed by nobody: the one
/// production consumer subscribed to <c>Fetched</c> alone. The consequence is not a missing log line —
/// it is that the ahead/behind badge stops being true the moment fetching stops, while the freshness
/// label beside it keeps counting up from the last SUCCESS. "Fetched 4 min ago" next to "0 ahead,
/// 0 behind" reads as "checked recently, you're up to date" when in fact nothing has been checked since
/// the token expired.
///
/// <para>These tests drive a REAL failing fetch through the REAL service into the REAL dashboard and
/// assert what the user is shown, not that a private field moved.</para>
/// </summary>
public class AutoFetchFailureSurfacingTests
{
    private static async Task PumpAsync()
    {
        // The handler marshals through Dispatcher.UIThread.Post; give the loop a chance to drain.
        for (var i = 0; i < 50; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// FAILS BEFORE / PASSES AFTER. A remote that no longer exists (the deleted-remote / expired-token
    /// shape) must produce an error toast AND a persistent, legible warning on the freshness label.
    /// Before the fix the dashboard subscribed to <c>Fetched</c> only, so this cycle produced nothing
    /// whatsoever: no toast, no label change, ahead/behind silently frozen.
    /// </summary>
    [AvaloniaFact]
    public async Task FailingAutoFetch_TellsTheUser_AndMarksTheFreshnessLabel()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("readme.md", "# demo\n", "chore: seed");
        var barePath = fx.AddBareRemote();

        // The remote is gone — exactly what a deleted remote or an unreachable host looks like locally.
        Directory.Delete(barePath, recursive: true);

        using var dash = new RepoDashboardViewModel(new Repository { Path = fx.RepoPath, DisplayName = "demo" });
        await PumpAsync();
        dash.Toasts.Clear(); // ignore anything the initial load surfaced

        await dash.AutoFetch.RunCycleAsync();
        await PumpAsync();

        Assert.True(dash.IsAutoFetchFailing, "a failing background fetch left the dashboard looking healthy");

        var toast = Assert.Single(dash.Toasts);
        Assert.True(toast.IsError);
        Assert.Contains("ahead/behind", toast.Message, StringComparison.OrdinalIgnoreCase);

        // The label must SAY it, not merely change colour — colour alone is not a message.
        Assert.Contains("Fetch failing", dash.LastFetchedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The warning is honest in both directions: a fetch that starts working again clears the state and
    /// the label goes back to plain freshness. Otherwise the first blip would leave a permanent warning
    /// and the surface would stop meaning anything.
    /// </summary>
    [AvaloniaFact]
    public async Task RecoveredAutoFetch_ClearsTheWarning()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("readme.md", "# demo\n", "chore: seed");
        var barePath = fx.AddBareRemote();
        fx.SetUpstream("origin"); // the recovery path refreshes ahead/behind, which needs an upstream
        var stash = barePath + "-moved";
        Directory.Move(barePath, stash);

        using var dash = new RepoDashboardViewModel(new Repository { Path = fx.RepoPath, DisplayName = "demo" });
        await PumpAsync();
        dash.Toasts.Clear();

        await dash.AutoFetch.RunCycleAsync();
        await PumpAsync();
        Assert.True(dash.IsAutoFetchFailing);

        // The remote is reachable again. Pumped in a loop because the failure also armed the retry
        // backoff, so the very next cycle is deliberately a skip.
        Directory.Move(stash, barePath);
        for (var i = 0; i < 6 && dash.IsAutoFetchFailing; i++)
        {
            await dash.AutoFetch.RunCycleAsync();
            await PumpAsync();
        }

        Assert.False(dash.IsAutoFetchFailing);
        Assert.DoesNotContain("Fetch failing", dash.LastFetchedText, StringComparison.Ordinal);

        // A SUCCESSFUL fetch also kicks off a background ahead/behind refresh. Let it finish before the
        // fixture deletes the repo out from under it, or the teardown race fails the test for the wrong
        // reason.
        await SettleAsync(dash);
    }

    /// <summary>Drains the dashboard's fire-and-forget post-fetch refresh so nothing is still reading the
    /// repository when the fixture deletes it.</summary>
    private static async Task SettleAsync(RepoDashboardViewModel dash)
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(10);
            await PumpAsync();
        }
    }

    /// <summary>
    /// One toast per outage. The original design note refused toast spam and was right to — it just
    /// refused it by saying nothing at all. Repeated failures must not stack toasts; the label carries
    /// the ongoing state.
    /// </summary>
    [AvaloniaFact]
    public async Task RepeatedFailures_DoNotSpamToasts()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("readme.md", "# demo\n", "chore: seed");
        Directory.Delete(fx.AddBareRemote(), recursive: true);

        using var dash = new RepoDashboardViewModel(new Repository { Path = fx.RepoPath, DisplayName = "demo" });
        await PumpAsync();
        dash.Toasts.Clear();

        // Enough cycles to clear the backoff several times over.
        for (var i = 0; i < 12; i++)
        {
            await dash.AutoFetch.RunCycleAsync();
            await PumpAsync();
        }

        Assert.Single(dash.Toasts);
        Assert.True(dash.IsAutoFetchFailing);
    }
}
