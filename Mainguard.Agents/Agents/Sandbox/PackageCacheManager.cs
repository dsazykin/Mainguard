using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Mainguard.Git;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>One per-agent package cache, as the manager currently sees it on disk.</summary>
/// <param name="InUse">True while this daemon holds a lease on it (the agent was spawned and has not
/// been torn down) — such a cache is never evicted, because deleting a package folder out from under a
/// restore in progress is not a cache miss.</param>
public sealed record PackageCacheEntry(
    string RepoHash, string AgentId, string Path, long Bytes, DateTimeOffset LastUsed, bool InUse);

/// <summary>
/// What the cache root holds right now — the "make the current size observable" half of the budget.
/// Returned from every <see cref="PackageCacheManager.Prepare"/> and logged by the spawn path, so the
/// size is a fact in the daemon log rather than something a human has to go and measure.
/// </summary>
public sealed record PackageCacheUsage(
    string RootPath,
    long UsedBytes,
    long BudgetBytes,
    IReadOnlyList<PackageCacheEntry> Entries,
    int EvictedCount,
    long EvictedBytes,
    PackageCacheGrant Grant = PackageCacheGrant.SharedJailGroup)
{
    /// <summary>Percent of budget consumed (0 when the budget is somehow zero — never a divide).</summary>
    public double PercentUsed => BudgetBytes <= 0 ? 0 : 100d * UsedBytes / BudgetBytes;

    /// <summary>One log line stating every number this type exists to make visible.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"package cache {RootPath}: {PackageCachePolicy.Bytes(UsedBytes)} of "
        + $"{PackageCachePolicy.Bytes(BudgetBytes)} ({PercentUsed:0.#}%) across {Entries.Count} cache(s); "
        + $"evicted {EvictedCount} ({PackageCachePolicy.Bytes(EvictedBytes)}) this pass; grant {Grant}");
}

/// <summary>
/// MG-43 — the IO half of the daemon-owned package cache: creates each agent's cache directory, keeps
/// the cache root inside its size budget by evicting whole idle caches least-recently-used first, and
/// reports what it holds.
///
/// <para>Everything about <i>why</i> the cache is shaped this way — per-agent rather than shared, on
/// ext4 rather than tmpfs, outside <c>/workspace</c>, group-shared rather than chowned — is in
/// <see cref="PackageCachePolicy"/>. This type only does the filesystem work.</para>
///
/// <para><b>Eviction is whole-cache, never partial.</b> A NuGet global-packages folder records
/// completion per package with a <c>.nupkg.metadata</c> marker and does not re-hash extracted content,
/// so a half-deleted cache is not a cache miss — it is a package that restore reports as already
/// installed and whose assemblies are gone, i.e. a build failure in someone else's branch that looks
/// like their code. The unit of eviction is therefore one agent's entire cache, and the order is
/// least-recently-used by the daemon-written marker (never by directory mtime: package managers touch
/// files deep in the tree and leave the root's timestamp from creation day).</para>
///
/// <para><b>Eviction never touches a leased cache.</b> A lease is taken in <see cref="Prepare"/> and
/// released in <see cref="Release"/> (which is also what deletes the directory, on the ordinary
/// teardown path). So the population eviction actually reaps is the residue: caches whose agents died
/// with the daemon, or were removed without a clean teardown. If the leased caches alone exceed the
/// budget, that is a typed <see cref="PackageCacheOverBudgetException"/> and the spawn fails — the
/// alternative is corrupting a live restore to make room, which trades a loud failure for a quiet
/// wrong answer.</para>
///
/// <para><b>The cost, stated.</b> <see cref="Prepare"/> walks the whole cache root once per spawn — a
/// stat-only pass, no reads. On the measured 2.2 GB this repository produces that is on the order of a
/// second; the budget is enforced from a real measurement rather than from a running total precisely
/// because a running total drifts the moment anything (an eviction that half-failed, a manual cleanup,
/// a daemon restart) touches the tree behind its back, and a budget that silently believes a wrong
/// number is the same class of thing as a control that looks applied and is not. A second pass runs
/// ONLY when the first found the root over budget.</para>
///
/// <para><b>Symlinks are never followed.</b> This is the first agent-WRITABLE tree the daemon walks, so
/// both the size walk and the delete stop at any reparse point and unlink it rather than descending. A
/// naive recursive walk here would let an agent point <c>evil -&gt; /home/mainguard</c> inside its own
/// cache and have the daemon either account for (or, far worse, delete) the daemon's own keyring, tokens
/// and database. .NET's recursive helpers are not documented to guarantee this on every platform, so the
/// walk is explicit rather than trusted.</para>
/// </summary>
public sealed class PackageCacheManager
{
    private readonly string _vmRoot;
    private readonly long _budgetBytes;
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    /// <summary>The caches this daemon currently owns a live jail for; never evicted.</summary>
    private readonly HashSet<string> _leases = new(StringComparer.Ordinal);

    /// <summary>Resolved once; see <see cref="Grant"/>.</summary>
    private PackageCacheGrant? _grant;

    /// <param name="vmRoot">The VM base directory, shared with the provisioner and worktree manager.</param>
    /// <param name="budgetBytes">The ceiling for the whole cache root. Refused below
    /// <see cref="PackageCachePolicy.MinimumBudgetBytes"/> — see that constant for why a too-small
    /// budget is worse than none.</param>
    /// <param name="log">Optional milestone sink (the daemon's sandbox log category).</param>
    public PackageCacheManager(
        string? vmRoot = null,
        long budgetBytes = PackageCachePolicy.DefaultBudgetBytes,
        Action<string>? log = null)
    {
        _vmRoot = vmRoot ?? Path.Combine(MainguardPaths.HomeDirectory(), "mainguard");
        if (budgetBytes < PackageCachePolicy.MinimumBudgetBytes)
        {
            throw new PackageCacheException(
                PackageCachePolicy.CacheRoot(_vmRoot),
                $"a budget of {PackageCachePolicy.Bytes(budgetBytes)} is below the "
                + $"{PackageCachePolicy.Bytes(PackageCachePolicy.MinimumBudgetBytes)} minimum; a budget that "
                + "cannot hold two dependency closures evicts what the previous spawn just downloaded, so "
                + "every verification re-downloads everything and the cache is worse than absent.");
        }

        _budgetBytes = budgetBytes;
        _log = log;
    }

    /// <summary>The cache root the budget is measured over.</summary>
    public string RootPath => PackageCachePolicy.CacheRoot(_vmRoot);

    private string CacheRootPath => RootPath;

    /// <summary>The budget in force.</summary>
    public long BudgetBytes => _budgetBytes;

    /// <summary>This agent's cache directory (whether or not it exists yet).</summary>
    public string PathFor(string repoHash, string agentId)
        => PackageCachePolicy.AgentCachePath(_vmRoot, repoHash, agentId);

    /// <summary>
    /// Makes this agent's cache directory exist, usable and leased, brings the root back inside budget,
    /// and reports what the root now holds. Called on the spawn path <b>before</b> the container is
    /// created; every failure is typed and stops the spawn.
    /// </summary>
    public PackageCacheUsage Prepare(string repoHash, string agentId)
    {
        var path = PathFor(repoHash, agentId);

        lock (_gate)
        {
            _leases.Add(Key(repoHash, agentId));

            try
            {
                // The WHOLE chain, each level shared as it is created. The MG-17 invariant is a property
                // of the PARENT — a setgid parent hands its group to everything made underneath — so
                // creating the leaf alone and sharing only that leaves the chain unformed on any VM root
                // the boot step never provisioned. That is not hypothetical: it is exactly how this
                // failed against the merge-queue end-to-end suite, which builds a fresh VM root under
                // /tmp per test, where the daemon's umask 022 left the leaf 0755 owned by the test user
                // and the jail — a different uid, in no shared group — could not write it.
                ApplyGrantLocked(Directory.CreateDirectory(CacheRootPath), PackageCachePolicy.ParentMode);
                ApplyGrantLocked(
                    Directory.CreateDirectory(PackageCachePolicy.RepoCacheDirectory(_vmRoot, repoHash)),
                    PackageCachePolicy.ParentMode);
                ApplyGrantLocked(Directory.CreateDirectory(path), PackageCachePolicy.LeafMode(Grant));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PackageCacheUnavailableException(RootPath, path, $"it could not be created: {ex.Message}");
            }

            // NOTE: this checks the DAEMON's own access, and it is deliberately not the guard for the
            // jail's. The daemon owns the directory, so on any machine it passes regardless of whether
            // the jail can write a byte — that is precisely the "green for the wrong reason" shape MG-3
            // was burned by. It is here only to turn a broken disk / read-only mount into a failure the
            // daemon can name. The jail's access is proven where it can actually be observed: by
            // DockerSandboxEngine's probe, INSIDE the started container.
            if (!IsWritableDirectory(path))
            {
                throw new PackageCacheUnavailableException(RootPath, path,
                    "even the daemon, which owns it, cannot write to it — the filesystem is read-only, "
                    + "full, or the mode was changed underneath us");
            }

            MarkUsed(repoHash, agentId);
            return EnforceBudgetLocked();
        }
    }

    /// <summary>
    /// Drops this agent's cache and its lease. Called from the ordinary teardown
    /// (<c>RemoveAgentWorktree</c>), so a cleanly-retired agent reclaims its own space immediately and
    /// eviction is left to deal only with residue.
    /// </summary>
    public void Release(string repoHash, string agentId)
    {
        string path;
        string marker;
        try
        {
            path = PathFor(repoHash, agentId);
            marker = PackageCachePolicy.LastUsedMarkerPath(_vmRoot, repoHash, agentId);
        }
        catch (RepoProvisioningException)
        {
            // An id that cannot name a path never had a cache. Teardown must not throw over it.
            return;
        }

        lock (_gate)
        {
            _leases.Remove(Key(repoHash, agentId));
            DeleteTree(path);
            TryDeleteFile(marker);
        }
    }

    /// <summary>Measures the cache root without changing anything — the observability entry point.</summary>
    public PackageCacheUsage Measure()
    {
        lock (_gate)
        {
            var entries = ReadEntriesLocked();
            return new PackageCacheUsage(RootPath, entries.Sum(e => e.Bytes), _budgetBytes, entries, 0, 0, Grant);
        }
    }

    // ---- Budget ----------------------------------------------------------------------------------

    private PackageCacheUsage EnforceBudgetLocked()
    {
        var entries = ReadEntriesLocked();
        var used = entries.Sum(e => e.Bytes);
        if (used <= _budgetBytes)
        {
            return new PackageCacheUsage(RootPath, used, _budgetBytes, entries, 0, 0, Grant);
        }

        var evicted = 0;
        long evictedBytes = 0;

        // Least-recently-used first, and only ever a cache this daemon does not hold a lease on.
        foreach (var candidate in entries.Where(e => !e.InUse).OrderBy(e => e.LastUsed))
        {
            if (used <= _budgetBytes)
            {
                break;
            }

            DeleteTree(candidate.Path);
            TryDeleteFile(candidate.Path + PackageCachePolicy.LastUsedMarkerSuffix);
            used -= candidate.Bytes;
            evictedBytes += candidate.Bytes;
            evicted++;
            _log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"package cache evicted {candidate.RepoHash}/{candidate.AgentId} "
                + $"({PackageCachePolicy.Bytes(candidate.Bytes)}, last used {candidate.LastUsed:u})"));
        }

        var remaining = ReadEntriesLocked();
        var remainingBytes = remaining.Sum(e => e.Bytes);
        if (remainingBytes > _budgetBytes)
        {
            throw new PackageCacheOverBudgetException(
                RootPath, remainingBytes, _budgetBytes, remaining.Count(e => e.InUse));
        }

        return new PackageCacheUsage(RootPath, remainingBytes, _budgetBytes, remaining, evicted, evictedBytes, Grant);
    }

    private IReadOnlyList<PackageCacheEntry> ReadEntriesLocked()
    {
        var root = RootPath;
        if (!Directory.Exists(root))
        {
            return Array.Empty<PackageCacheEntry>();
        }

        var entries = new List<PackageCacheEntry>();
        foreach (var repoDir in SafeChildDirectories(root))
        {
            var repoHash = Path.GetFileName(repoDir)!;
            foreach (var agentDir in SafeChildDirectories(repoDir))
            {
                var agentId = Path.GetFileName(agentDir)!;
                entries.Add(new PackageCacheEntry(
                    repoHash, agentId, agentDir,
                    MeasureTree(agentDir),
                    ReadMarker(agentDir + PackageCachePolicy.LastUsedMarkerSuffix),
                    _leases.Contains(Key(repoHash, agentId))));
            }
        }

        return entries;
    }

    private void MarkUsed(string repoHash, string agentId)
    {
        var marker = PackageCachePolicy.LastUsedMarkerPath(_vmRoot, repoHash, agentId);
        try
        {
            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing marker reads as DateTimeOffset.MinValue, i.e. "evict me first" — the safe
            // direction. Never fail a spawn over bookkeeping.
        }
    }

    private static DateTimeOffset ReadMarker(string markerPath)
    {
        try
        {
            return File.Exists(markerPath)
                && DateTimeOffset.TryParse(
                    File.ReadAllText(markerPath), CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var when)
                ? when
                : DateTimeOffset.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static string Key(string repoHash, string agentId) => repoHash + " " + agentId;

    // ---- Symlink-safe filesystem primitives -------------------------------------------------------

    /// <summary>Child directories that are really directories — a symlink is skipped, never followed.</summary>
    private static IEnumerable<string> SafeChildDirectories(string parent)
    {
        string[] children;
        try
        {
            children = Directory.GetDirectories(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            if (!IsLink(child))
            {
                yield return child;
            }
        }
    }

    /// <summary>True for a symlink/reparse point. <see cref="File.ResolveLinkTarget(string,bool)"/>
    /// answers non-null exactly for a link, and unlike a <c>FileAttributes</c> test it needs no
    /// platform-specific flag handling.</summary>
    private static bool IsLink(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: false) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unknown ⇒ treat as a link, i.e. do not descend and do not account. The conservative
            // direction: an under-count delays an eviction, a wrong descent walks out of the tree.
            return true;
        }
    }

    /// <summary>Bytes held under <paramref name="root"/>, descending only into real directories and
    /// counting only real files (a symlink contributes nothing — its target is somebody else's bytes).</summary>
    private static long MeasureTree(string root)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (IsLink(file))
                {
                    continue;
                }

                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            foreach (var child in SafeChildDirectories(dir))
            {
                stack.Push(child);
            }
        }

        return total;
    }

    /// <summary>
    /// Removes a whole cache tree. The directory is RENAMED out of the way first, so the removal is
    /// visible to anything using it as a single atomic step rather than as a slow dissolve — a restore
    /// racing an eviction then fails on a path that is simply gone, instead of running on a tree that is
    /// half there. Best effort by contract: a cache that cannot be deleted is reported by the next
    /// measurement, and a teardown must not throw over disk residue.
    /// </summary>
    private void DeleteTree(string path)
    {
        if (!Directory.Exists(path) || IsLink(path))
        {
            TryDeleteFile(path);
            return;
        }

        var staged = path + ".evicting-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.Move(path, staged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            staged = path;
        }

        try
        {
            DeleteTreeRecursive(staged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"package cache could not fully remove '{staged}': {ex.Message}");
        }
    }

    /// <summary>Recursive delete that unlinks symlinks instead of descending through them.</summary>
    private static void DeleteTreeRecursive(string dir)
    {
        foreach (var file in Directory.GetFiles(dir))
        {
            TryDeleteFile(file);
        }

        foreach (var child in Directory.GetDirectories(dir))
        {
            if (IsLink(child))
            {
                TryDeleteFile(child);
            }
            else
            {
                DeleteTreeRecursive(child);
            }
        }

        try
        {
            Directory.Delete(dir, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsWritableDirectory(string path)
    {
        var probe = Path.Combine(path, ".mgcachewrite-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Applies <paramref name="mode"/> to one directory in the cache chain. Deliberately NOT recursive
    /// and deliberately not a chown: the daemon runs unprivileged and cannot chown into the remapped
    /// range, and the CONTENT below is written by the jail — as its own uid, under the group the setgid
    /// bit hands down — so it is already correct without the daemon touching it. Re-applied on every
    /// spawn, so a mode changed underneath the daemon heals rather than festering.
    /// </summary>
    private void ApplyGrantLocked(DirectoryInfo directory, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(directory.FullName, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log?.Invoke($"package cache could not set mode on '{directory.FullName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Which grant this cache root actually supports — resolved ONCE per manager and then reported on
    /// every spawn (see <see cref="PackageCacheGrant"/> for what each rung means and why the fallback
    /// does not weaken the cross-tenant property).
    ///
    /// <para>Resolved from the cache root's group id, which is the one observable that answers the
    /// question, and read with <c>stat -c %g</c> — the same tool, on the same fact, that
    /// <see cref="UsernsRemapPolicy.MountOwnershipScript"/> already guards its own recursive pass with.
    /// .NET exposes a file's MODE but not its owning gid, so there is no managed alternative short of a
    /// libc P/Invoke whose symbol name is not stable across the distributions this ships to.</para>
    ///
    /// <para>Cached because it cannot change under a running daemon: the only thing that sets it is the
    /// boot step, which runs before the daemon starts and restarts it when it changes the group.</para>
    /// </summary>
    public PackageCacheGrant Grant => _grant ??= ResolveGrant();

    private PackageCacheGrant ResolveGrant()
    {
        var gid = ReadOwningGroupId(CacheRootPath);
        var grant = PackageCachePolicy.DecideGrant(gid);
        _log?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"package cache grant for '{CacheRootPath}': {grant} (root gid {gid}, jail gid "
            + $"{UsernsRemapPolicy.AgentHostGid})"));
        return grant;
    }

    /// <summary>The directory's owning gid, or -1 when it cannot be read (Windows, no <c>stat</c>, an
    /// error). -1 is a value <see cref="PackageCachePolicy.DecideGrant"/> handles explicitly.
    ///
    /// <para><c>internal</c> rather than private because <see cref="ConversationStoreManager"/> asks the
    /// SAME question about ITS root and must not get a different answer: the grant is a property of the
    /// machine (did the boot step provision the jail group?), so two copies of this could only ever
    /// disagree by being wrong.</para></summary>
    internal static int ReadOwningGroupId(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return -1;
        }

        try
        {
            Directory.CreateDirectory(path);
            var psi = new System.Diagnostics.ProcessStartInfo("stat")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("%g");
            psi.ArgumentList.Add(path);

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return -1;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return int.TryParse(output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var gid)
                ? gid
                : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }
}
