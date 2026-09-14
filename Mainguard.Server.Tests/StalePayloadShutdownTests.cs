using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Server.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The daemon reaping itself once the build it was launched from is deleted.
///
/// <para><b>The incident.</b> A daemon launched on 2026-08-30 from the git worktree
/// <c>~/mg-work/sim</c> was still running fifteen days later, re-parented to init, holding loopback
/// 5250 against every later daemon; that worktree had been deleted on 2026-09-05. The daemon is started
/// DETACHED on purpose — it must outlive the UI — so nothing owns its lifetime once the launcher exits.
/// That is correct for an installed daemon and a leak for one running out of a deleted build tree.</para>
/// </summary>
public class StalePayloadShutdownTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public int Stops;
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stops++;
    }

    private static StalePayloadShutdownHostedService Watcher(string? path, out FakeLifetime lifetime)
    {
        lifetime = new FakeLifetime();
        return new StalePayloadShutdownHostedService(
            lifetime,
            NullLogger<StalePayloadShutdownHostedService>.Instance,
            path,
            TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public void APresentPayload_NeverAsksForShutdown()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mg-{Guid.NewGuid():N}.dll");
        File.WriteAllText(file, "");
        try
        {
            var watcher = Watcher(file, out _);
            for (var i = 0; i < StalePayloadShutdownHostedService.RequiredConsecutiveMisses * 3; i++)
            {
                Assert.False(watcher.ObserveOnce());
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// A deleted build tree stops the daemon — but only after the full run of misses, so a payload swap
    /// (MacDaemonUpdater replaces the dll underneath a running daemon) can never trip it.
    /// </summary>
    [Fact]
    public void AMissingPayload_StopsTheDaemon_ButOnlyAfterTheFullRunOfMisses()
    {
        var watcher = Watcher(Path.Combine(Path.GetTempPath(), $"mg-never-{Guid.NewGuid():N}.dll"), out _);

        for (var i = 1; i < StalePayloadShutdownHostedService.RequiredConsecutiveMisses; i++)
        {
            Assert.False(watcher.ObserveOnce());
        }

        Assert.True(watcher.ObserveOnce());
    }

    /// <summary>
    /// The swap case, directly: the dll vanishes, reappears mid-run, and the count starts over. Without
    /// the reset a slow tier-1 refresh would shut the daemon down in the middle of updating itself.
    /// </summary>
    [Fact]
    public void APayloadThatComesBack_ResetsTheCount()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mg-{Guid.NewGuid():N}.dll");
        var watcher = Watcher(file, out _);

        for (var i = 1; i < StalePayloadShutdownHostedService.RequiredConsecutiveMisses; i++)
        {
            Assert.False(watcher.ObserveOnce());
        }

        File.WriteAllText(file, ""); // the swap completes
        try
        {
            Assert.False(watcher.ObserveOnce());

            File.Delete(file);
            // Back to zero: one more miss must not be enough.
            Assert.False(watcher.ObserveOnce());
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    /// <summary>A host that reports no assembly location must do nothing rather than guess a path.</summary>
    [Fact]
    public async Task AnUnknownPayloadPath_DisablesTheWatchEntirely()
    {
        var watcher = Watcher(null, out var lifetime);

        Assert.False(watcher.ObserveOnce());
        await watcher.StartAsync(CancellationToken.None);
        await watcher.StopAsync(CancellationToken.None);

        Assert.Equal(0, lifetime.Stops);
    }

    /// <summary>
    /// The live loop, end to end: a payload that is gone reaches StopApplication on its own, with no
    /// launcher involved — which is the whole point, since the launcher is usually long gone.
    /// </summary>
    [Fact]
    public async Task TheRunningWatch_StopsTheApplicationOnceThePayloadStaysGone()
    {
        var watcher = Watcher(Path.Combine(Path.GetTempPath(), $"mg-never-{Guid.NewGuid():N}.dll"), out var lifetime);

        await watcher.StartAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (lifetime.Stops == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        await watcher.StopAsync(CancellationToken.None);
        Assert.Equal(1, lifetime.Stops);
    }
}
