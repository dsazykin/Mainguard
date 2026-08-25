using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Services;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-21 (clone) — <see cref="CloneService"/> against zero-network local fixtures: progress is reported
/// with monotonic <c>ReceivedObjects</c>/<c>Percent</c> and reaches completion; a clone cancelled through
/// the transfer callback throws <see cref="OperationCanceledException"/> and deletes the partial directory;
/// a clone into an existing non-empty directory throws a typed error.
/// </summary>
public class CloneServiceTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly TempRepoFixture _fx = new();

    public CloneServiceTests()
    {
        // A source repo with enough objects that the transfer phase reports progress.
        for (int i = 0; i < 12; i++)
            _fx.CommitFile($"file{i}.txt", $"content number {i}\nsecond line {i}\n", $"commit {i}");
    }

    public void Dispose()
    {
        _fx.Dispose();
        foreach (var d in _tempDirs)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Directory.Delete(d, true);
            }
            catch { }
        }
    }

    private string NewTarget()
    {
        // CanonicalTempRoot, not Path.GetTempPath(): libgit2 reports the clone's real path,
        // and the StartsWith(target, result) assertion below needs both in the same namespace.
        var p = Path.Combine(TempRepoFixture.CanonicalTempRoot, "mainguard-clonetgt-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(p);
        return p;
    }

    /// <summary>A <c>file://</c> URL for the fixture repo — the local transport that reports transfer
    /// progress and honours the transfer-callback cancel (a bare local path clone can skip both).</summary>
    private string SourceUrl => new Uri(_fx.RepoPath).AbsoluteUri;

    /// <summary>Synchronous <see cref="IProgress{T}"/> that records every report on the calling thread
    /// (no SynchronizationContext marshalling) so ordering/monotonicity assertions are deterministic.</summary>
    private sealed class SyncProgress : IProgress<CloneProgress>
    {
        public readonly List<CloneProgress> Reports = new();
        private readonly Action<CloneProgress>? _onReport;
        public SyncProgress(Action<CloneProgress>? onReport = null) => _onReport = onReport;
        public void Report(CloneProgress value) { Reports.Add(value); _onReport?.Invoke(value); }
    }

    [Fact]
    public async Task CloneAsync_LocalRepo_ShouldReportMonotonicProgress_AndComplete()
    {
        var target = NewTarget();
        var progress = new SyncProgress();

        var result = await new CloneService().CloneAsync(SourceUrl, target, progress);

        Assert.True(Directory.Exists(Path.Combine(target, ".git")));
        Assert.True(File.Exists(Path.Combine(target, "file0.txt"))); // working tree checked out
        Assert.StartsWith(target, result); // libgit2 returns the repo (.git) path under the target

        // The transfer phase actually reported received objects (not just the terminal frame).
        Assert.Contains(progress.Reports, r => r.Phase == ClonePhase.Receiving && r.ReceivedObjects > 0);

        // ReceivedObjects and Percent never decrease across the whole run (monotonic bar).
        AssertNonDecreasing(progress.Reports.Select(r => r.ReceivedObjects));
        AssertNonDecreasing(progress.Reports.Select(r => r.Percent));

        // A terminal 100%/Completed report is emitted.
        Assert.Contains(progress.Reports, r => r.Phase == ClonePhase.Completed && r.Percent == 100);
    }

    [Fact]
    public async Task CloneAsync_FromBareRepo_ShouldComplete()
    {
        var bare = _fx.AddBareRemote(); // bare repo with HEAD pushed
        _tempDirs.Add(bare);
        var target = NewTarget();
        var progress = new SyncProgress();

        await new CloneService().CloneAsync(bare, target, progress);

        Assert.True(Directory.Exists(Path.Combine(target, ".git")));
        Assert.Contains(progress.Reports, r => r.Phase == ClonePhase.Completed);
    }

    [Fact]
    public async Task CloneAsync_CancelledViaCallback_ShouldThrow_AndDeletePartialDirectory()
    {
        var target = NewTarget();
        using var cts = new CancellationTokenSource();

        // Cancel from within the first progress report — exercises the transfer-callback cancel path.
        var progress = new SyncProgress(_ => cts.Cancel());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new CloneService().CloneAsync(SourceUrl, target, progress, cts.Token));

        // The partial clone directory must be gone (T-21 invariant).
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task CloneAsync_WithPreCancelledToken_ShouldThrow_AndLeaveNoDirectory()
    {
        var target = NewTarget();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new CloneService().CloneAsync(_fx.RepoPath, target, null, cts.Token));

        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task CloneAsync_IntoExistingNonEmptyDirectory_ShouldThrowTyped()
    {
        var target = NewTarget();
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "existing.txt"), "occupied");

        var ex = await Assert.ThrowsAsync<GitOperationException>(
            () => new CloneService().CloneAsync(_fx.RepoPath, target, null));
        Assert.Contains("not empty", ex.Message);

        // The pre-existing content is untouched (we refused before touching it).
        Assert.True(File.Exists(Path.Combine(target, "existing.txt")));
    }

    [Fact]
    public async Task CloneAsync_IntoFreshEmptyDirectory_ShouldSucceed()
    {
        var target = NewTarget();
        Directory.CreateDirectory(target); // empty dir is allowed

        await new CloneService().CloneAsync(_fx.RepoPath, target, null);
        Assert.True(Directory.Exists(Path.Combine(target, ".git")));
    }

    private static void AssertNonDecreasing(IEnumerable<int> values)
    {
        int prev = int.MinValue;
        foreach (var v in values)
        {
            Assert.True(v >= prev, $"sequence decreased: {prev} -> {v}");
            prev = v;
        }
    }
}
