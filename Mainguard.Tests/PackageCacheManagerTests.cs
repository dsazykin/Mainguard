using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-43 — the filesystem half: creation, the size bound, LRU eviction, and the symlink discipline the
/// daemon needs because the cache is the first agent-WRITABLE tree it walks with delete rights.
///
/// <para>These use a real temp directory rather than a filesystem abstraction, because every property
/// under test is a property of real filesystem behaviour — what a recursive delete does at a symlink,
/// what a size walk counts — and a fake would simply agree with whatever the code already does.</para>
///
/// <para><b>Why the sizes are SPARSE files.</b> The budget floor is 4 GiB and it is a real deployment
/// guard (<see cref="PackageCachePolicy.MinimumBudgetBytes"/> — a smaller budget produces a permanent
/// cache miss), so exercising eviction honestly means multi-gigabyte caches. Adding a
/// "smaller budget, tests only" seam would put a bypass of the guard into shipping code; a sparse file
/// instead reports its full length to the same <c>FileInfo.Length</c> the manager measures while
/// occupying no blocks. The policy is measured at its real numbers and the suite still runs in
/// milliseconds.</para>
/// </summary>
public class PackageCacheManagerTests : IDisposable
{
    /// <summary>3 GiB — bigger than half the 4 GiB floor, so two of them force exactly one eviction.</summary>
    private const long BigCacheBytes = 3L * 1024 * 1024 * 1024;

    private readonly string _vmRoot;

    public PackageCacheManagerTests()
    {
        _vmRoot = Path.Combine(Path.GetTempPath(), "mg-cache-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_vmRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_vmRoot)) Directory.Delete(_vmRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A manager at the real floor budget — the same value a misconfigured deployment would
    /// be held to, so the eviction tests measure the shipped policy rather than a test-only one.</summary>
    private PackageCacheManager NewManager() => new(_vmRoot, PackageCachePolicy.MinimumBudgetBytes);

    // ---- Preparation -------------------------------------------------------------------------------

    [Fact]
    public void Prepare_CreatesTheAgentsOwnDirectory()
    {
        var manager = NewManager();
        manager.Prepare("repo1", "agent-a");
        Assert.True(Directory.Exists(manager.PathFor("repo1", "agent-a")));
    }

    [Fact]
    public void Prepare_WritesTheDaemonOnlyLastUsedMarker()
    {
        var manager = NewManager();
        manager.Prepare("repo1", "agent-a");
        Assert.True(File.Exists(PackageCachePolicy.LastUsedMarkerPath(_vmRoot, "repo1", "agent-a")));
    }

    [Fact]
    public void Prepare_ReportsTheRootsCurrentSize()
    {
        var manager = NewManager();
        manager.Prepare("repo1", "agent-a");
        Fill(manager.PathFor("repo1", "agent-a"), "nuget/packages/blob", 4096);

        var usage = manager.Prepare("repo1", "agent-b");

        // Observability is a requirement of its own: the number is REPORTED, not merely enforced.
        Assert.True(usage.UsedBytes >= 4096, $"expected at least 4096 bytes, saw {usage.UsedBytes}");
        Assert.Equal(PackageCachePolicy.MinimumBudgetBytes, usage.BudgetBytes);
        Assert.Equal(2, usage.Entries.Count);
        Assert.Contains(manager.RootPath, usage.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_IsIdempotent_AndKeepsTheContent()
    {
        // A cache that empties on every relaunch is the permanent-cache-miss failure with extra steps.
        var manager = NewManager();
        manager.Prepare("repo1", "agent-a");
        var blob = Fill(manager.PathFor("repo1", "agent-a"), "nuget/packages/blob", 128);

        manager.Prepare("repo1", "agent-a");

        Assert.True(File.Exists(blob));
    }

    // ---- The ownership grant is applied to the WHOLE chain -------------------------------------------

    [Fact]
    public void Prepare_AppliesTheGrantToTheLeaf()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // The bug this replaced: only the leaf was touched, and only with `g+rwX`, so on a VM root the
        // boot step never provisioned the leaf stayed 0755 owned by the daemon — and a jail that is
        // neither the owner nor in its group (every CI runner) could not write a byte.
        var manager = NewManager();
        manager.Prepare("repo1", "agent-a");

        Assert.Equal(
            PackageCachePolicy.LeafMode(manager.Grant),
            File.GetUnixFileMode(manager.PathFor("repo1", "agent-a")));
    }

    [Fact]
    public void Prepare_AppliesTheGrantToTheCacheRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // The MG-17 invariant is a property of the PARENT, so the chain has to be formed from the root
        // down. Asserted separately from the leaf: creating the leaf correctly while leaving the root at
        // whatever umask produced passes the assertion above and still breaks the setgid propagation.
        var manager = NewManager();
        manager.Prepare("repo1", "agent-a");

        Assert.Equal(PackageCachePolicy.ParentMode, File.GetUnixFileMode(manager.RootPath));
    }

    [Fact]
    public void Prepare_AppliesTheGrantToThePerRepoDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var manager = NewManager();
        manager.Prepare("repo1", "agent-a");

        Assert.Equal(
            PackageCachePolicy.ParentMode,
            File.GetUnixFileMode(Path.GetDirectoryName(manager.PathFor("repo1", "agent-a"))!));
    }

    [Fact]
    public void OnAMachineWithNoJailGroup_TheGrantIsModeOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // A temp root under /tmp is owned by the test process's own group, never gid 101000 — the same
        // position as a CI runner and as the merge-queue end-to-end suite's per-test VM root. The grant
        // has to resolve to the rung that actually reaches the jail there, or nothing does.
        Assert.Equal(PackageCacheGrant.ModeOnly, NewManager().Grant);
    }

    [Fact]
    public void TheGrant_IsReportedInTheUsageLine()
        // A fallback that is taken quietly is the failure class this codebase keeps finding. It has to
        // be in the line the spawn path logs, so a production daemon sitting on the wrong rung says so.
        => Assert.Contains(
            NewManager().Prepare("repo1", "agent-a").Grant.ToString(),
            NewManager().Prepare("repo1", "agent-b").Describe(),
            StringComparison.Ordinal);

    // ---- Release ------------------------------------------------------------------------------------

    [Fact]
    public void Release_RemovesTheDirectoryAndTheMarker()
    {
        var manager = NewManager();
        manager.Prepare("repo1", "agent-a");
        Fill(manager.PathFor("repo1", "agent-a"), "blob", 64);

        manager.Release("repo1", "agent-a");

        Assert.False(Directory.Exists(manager.PathFor("repo1", "agent-a")));
        Assert.False(File.Exists(PackageCachePolicy.LastUsedMarkerPath(_vmRoot, "repo1", "agent-a")));
    }

    [Fact]
    public void Release_OfAnUnknownAgent_IsSilent()
        // Teardown runs on failure paths; it must not throw over an agent that never had a cache.
        => NewManager().Release("repo1", "never-existed");

    [Fact]
    public void Release_OfAMalformedAgentId_IsSilent()
        => NewManager().Release("repo1", "../../escape");

    // ---- The budget and eviction ---------------------------------------------------------------------

    [Fact]
    public void UnderBudget_NothingIsEvicted()
    {
        var manager = NewManager();
        Idle(manager, "repo1", "old", BigCacheBytes, agedDays: 9);

        var usage = manager.Prepare("repo1", "fresh");

        Assert.Equal(0, usage.EvictedCount);
        Assert.True(Directory.Exists(manager.PathFor("repo1", "old")));
    }

    [Fact]
    public void OverBudget_AnIdleCacheIsEvicted()
    {
        var manager = NewManager();
        Idle(manager, "repo1", "old", BigCacheBytes, agedDays: 9);
        Idle(manager, "repo1", "recent", BigCacheBytes, agedDays: 1);

        var usage = manager.Prepare("repo1", "fresh");

        Assert.True(usage.EvictedCount >= 1, "6 GiB against a 4 GiB budget evicted nothing");
    }

    [Fact]
    public void OverBudget_TheEvictedOneIsTheLeastRecentlyUsed()
    {
        // Asserted separately from "something was evicted": a test that stops at the count says nothing
        // about the ORDER, and evicting the newest cache first is a working eviction policy that
        // maximises re-downloads.
        var manager = NewManager();
        Idle(manager, "repo1", "old", BigCacheBytes, agedDays: 9);
        Idle(manager, "repo1", "recent", BigCacheBytes, agedDays: 1);

        manager.Prepare("repo1", "fresh");

        Assert.False(Directory.Exists(manager.PathFor("repo1", "old")), "the OLDEST idle cache survived");
    }

    [Fact]
    public void OverBudget_EvictionStopsAsSoonAsItFits()
    {
        // The other half of the same behaviour, and a separate assertion because a policy that evicts
        // EVERYTHING whenever it is over budget also passes both tests above while throwing away every
        // cache on the box.
        var manager = NewManager();
        Idle(manager, "repo1", "old", BigCacheBytes, agedDays: 9);
        Idle(manager, "repo1", "recent", BigCacheBytes, agedDays: 1);

        manager.Prepare("repo1", "fresh");

        Assert.True(Directory.Exists(manager.PathFor("repo1", "recent")),
            "eviction kept going after the root already fitted the budget");
    }

    [Fact]
    public void EvictionIsWholeCache_NeverPartial()
    {
        // A half-deleted NuGet global-packages folder is not a cache miss: restore reads the surviving
        // .nupkg.metadata markers as "already installed" and the build then fails on missing assemblies.
        var manager = NewManager();
        Idle(manager, "repo1", "old", BigCacheBytes, agedDays: 9, extraFiles: 3);
        Idle(manager, "repo1", "recent", BigCacheBytes, agedDays: 1);

        manager.Prepare("repo1", "fresh");

        Assert.False(Directory.Exists(manager.PathFor("repo1", "old")));
    }

    [Fact]
    public void ALeasedCache_IsNeverEvicted_EvenWhenItIsTheOldest()
    {
        // The corruption guard: a leased cache is mounted into a live jail and may be mid-restore.
        var manager = NewManager();
        manager.Prepare("repo1", "leased");                       // leases it
        Fill(manager.PathFor("repo1", "leased"), "blob", BigCacheBytes);
        Age("repo1", "leased", TimeSpan.FromDays(30));            // and it is by far the oldest
        Idle(manager, "repo1", "idle", BigCacheBytes, agedDays: 1);

        manager.Prepare("repo1", "fresh");

        Assert.True(Directory.Exists(manager.PathFor("repo1", "leased")),
            "a cache mounted into a live jail was evicted");
    }

    [Fact]
    public void ALeasedCacheBeingProtected_StillEvictsTheIdleOne()
    {
        // Inverted separately: "the leased one survived" is also satisfied by evicting NOTHING, which
        // would silently turn the budget off.
        var manager = NewManager();
        manager.Prepare("repo1", "leased");
        Fill(manager.PathFor("repo1", "leased"), "blob", BigCacheBytes);
        Age("repo1", "leased", TimeSpan.FromDays(30));
        Idle(manager, "repo1", "idle", BigCacheBytes, agedDays: 1);

        manager.Prepare("repo1", "fresh");

        Assert.False(Directory.Exists(manager.PathFor("repo1", "idle")));
    }

    [Fact]
    public void WhenOnlyLeasedCachesRemain_AndTheyExceedTheBudget_ItIsATypedRefusal()
    {
        // The loud failure: never "carry on and hope", never evict a live cache to make room.
        var manager = NewManager();
        manager.Prepare("repo1", "leased-a");
        Fill(manager.PathFor("repo1", "leased-a"), "blob", BigCacheBytes);
        manager.Prepare("repo1", "leased-b");
        Fill(manager.PathFor("repo1", "leased-b"), "blob", BigCacheBytes);

        var ex = Assert.Throws<PackageCacheOverBudgetException>(() => manager.Prepare("repo1", "leased-a"));

        Assert.True(ex.UsedBytes > ex.BudgetBytes);
    }

    [Fact]
    public void TheOverBudgetRefusal_NamesHowManyCachesItCouldNotTouch()
    {
        // The message has to point at the fix ("stop some agents"), which needs the count. Separate
        // assertion because the throw above passes with RetainedInUse left at zero.
        var manager = NewManager();
        manager.Prepare("repo1", "leased-a");
        Fill(manager.PathFor("repo1", "leased-a"), "blob", BigCacheBytes);
        manager.Prepare("repo1", "leased-b");
        Fill(manager.PathFor("repo1", "leased-b"), "blob", BigCacheBytes);

        var ex = Assert.Throws<PackageCacheOverBudgetException>(() => manager.Prepare("repo1", "leased-a"));

        Assert.Equal(2, ex.RetainedInUse);
    }

    [Fact]
    public void AReleasedCache_BecomesEvictableAgain()
    {
        var manager = NewManager();
        manager.Prepare("repo1", "a");
        Fill(manager.PathFor("repo1", "a"), "blob", BigCacheBytes);
        manager.Prepare("repo1", "b");
        Fill(manager.PathFor("repo1", "b"), "blob", BigCacheBytes);
        Assert.Throws<PackageCacheOverBudgetException>(() => manager.Prepare("repo1", "a"));

        manager.Release("repo1", "b");

        // Release both deletes and un-leases, so the same call now succeeds.
        var usage = manager.Prepare("repo1", "a");
        Assert.True(usage.UsedBytes <= usage.BudgetBytes);
    }

    // ---- Symlink discipline ---------------------------------------------------------------------------

    [Fact]
    public void ASymlinkInsideACache_IsNotFollowedWhenMeasuring()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var outside = Path.Combine(_vmRoot, "not-a-cache");
        Fill(outside, "big", 500_000);

        var manager = NewManager();
        manager.Prepare("repo1", "agent-a");
        Directory.CreateSymbolicLink(Path.Combine(manager.PathFor("repo1", "agent-a"), "escape"), outside);

        Assert.True(manager.Measure().UsedBytes < 500_000,
            "the size walk followed a symlink out of the cache — and eviction deletes what that walk finds");
    }

    [Fact]
    public void EvictingACacheWithAnEscapingSymlink_LeavesTheTargetsSubdirectoriesIntact()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // The attack: an agent can write inside its OWN cache, so it can plant `evil -> /home/mainguard`
        // and let the daemon's eviction delete the daemon's keyring, tokens and SQLite. Nothing else in
        // the product walks an agent-writable tree with delete rights.
        //
        // It asserts on a SUBDIRECTORY of the target, not on the target itself. Measured: with the
        // symlink guard inverted, "the target directory still exists" stayed GREEN while every file
        // inside it was destroyed — .NET unlinks a symlink-to-directory at the final rmdir rather than
        // removing what it points at, so the link's own target survives as an empty husk. An assertion
        // that cannot fail against the break it names is not a test, and this suite has been burned by
        // twelve of those.
        var outside = PlantEscapeAndEvict();
        Assert.True(Directory.Exists(Path.Combine(outside, "keyring.d")),
            "eviction followed a symlink OUT of the cache and removed a directory it points at");
    }

    [Fact]
    public void EvictingACacheWithAnEscapingSymlink_LeavesTheTargetsFilesIntact()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Inverted separately from the directory check: a delete that emptied the target but left the
        // (now empty) directory standing passes the assertion above and has still destroyed the daemon.
        var outside = PlantEscapeAndEvict();
        Assert.True(File.Exists(Path.Combine(outside, "keyring")),
            "eviction deleted a FILE outside the cache tree through a symlink");
    }

    [Fact]
    public void EvictingACacheWithAnEscapingSymlink_StillRemovesTheCacheItself()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // And the third inversion: refusing to delete anything at all also leaves the target intact.
        PlantEscapeAndEvict();
        Assert.False(Directory.Exists(Path.Combine(_vmRoot, "caches", "repo1", "idle")),
            "the cache carrying the symlink was not evicted, so the two assertions above proved nothing");
    }

    [Fact]
    public void ASymlinkedCacheDirectory_IsNotEnumeratedAsACache()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var outside = Path.Combine(_vmRoot, "elsewhere");
        Fill(outside, "blob", 64);

        var manager = NewManager();
        manager.Prepare("repo1", "real");
        Directory.CreateSymbolicLink(
            Path.Combine(Path.GetDirectoryName(manager.PathFor("repo1", "real"))!, "linked"), outside);

        Assert.DoesNotContain(manager.Measure().Entries, e => e.AgentId == "linked");
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    /// <summary>Plants an escaping symlink in an idle, over-budget cache and forces an eviction pass.</summary>
    private string PlantEscapeAndEvict()
    {
        var outside = Path.Combine(_vmRoot, "daemon-state");
        Fill(outside, "keyring", 32);
        Fill(outside, "keyring.d/session", 32);

        var manager = NewManager();
        Idle(manager, "repo1", "idle", BigCacheBytes, agedDays: 9);
        Directory.CreateSymbolicLink(Path.Combine(manager.PathFor("repo1", "idle"), "escape"), outside);
        Idle(manager, "repo1", "other", BigCacheBytes, agedDays: 1);

        manager.Prepare("repo1", "fresh");
        return outside;
    }

    /// <summary>An UNLEASED cache: created directly (never through <c>Prepare</c>, which would lease it),
    /// filled, and back-dated so LRU order is deterministic.</summary>
    private void Idle(PackageCacheManager manager, string repoHash, string agentId, long bytes, int agedDays, int extraFiles = 0)
    {
        var path = manager.PathFor(repoHash, agentId);
        Directory.CreateDirectory(path);
        Fill(path, "blob", bytes);
        for (var i = 0; i < extraFiles; i++)
        {
            Fill(path, $"nuget/packages/pkg{i.ToString(CultureInfo.InvariantCulture)}/.nupkg.metadata", 16);
        }

        Age(repoHash, agentId, TimeSpan.FromDays(agedDays));
    }

    /// <summary>Writes a SPARSE file of <paramref name="bytes"/> logical length (see the class remarks).</summary>
    private static string Fill(string directory, string relativePath, long bytes)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.SetLength(bytes);
        return path;
    }

    private void Age(string repoHash, string agentId, TimeSpan ago)
        => File.WriteAllText(
            PackageCachePolicy.LastUsedMarkerPath(_vmRoot, repoHash, agentId),
            (DateTimeOffset.UtcNow - ago).ToString("O", CultureInfo.InvariantCulture));
}
