using System;
using System.Globalization;

namespace Mainguard.Git.Exceptions;

/// <summary>
/// The daemon-owned package cache a jail needs could not be prepared, mounted, or proven usable.
///
/// <para><b>Why this is an exception and not a fallback</b> — the same argument
/// <see cref="ToolchainProvisioningException"/> makes, for the same reason. The cache exists because
/// the jail's <c>$HOME</c> is a 256 MiB tmpfs and a real repository's package closure is not (measured:
/// Mainguard's own <c>Mainguard.slnx</c> restores 1.7 GB). If the cache is missing, the verification
/// command does not get a weaker signal — it gets <c>ENOSPC</c> partway through a restore, which the
/// merge queue records as an ordinary failed verification indistinguishable from the agent's code being
/// broken. Worse, a half-populated package folder can make a restore <i>succeed</i> and the build then
/// fail on a missing assembly, or (with NuGet's <c>.nupkg.metadata</c> completion marker present but
/// content absent) succeed against content nobody verified.</para>
///
/// <para>So every path here fails loudly: no silent degrade to the tmpfs home, no "carry on without a
/// cache", no partial eviction. A spawn that cannot get a usable cache does not start.</para>
/// </summary>
public class PackageCacheException : MainguardException
{
    public PackageCacheException(string cacheRoot, string detail)
        : base($"The daemon-owned package cache under '{cacheRoot}' is unusable: {detail}")
    {
        CacheRoot = cacheRoot;
        Detail = detail;
    }

    /// <summary>The cache root the failure is about (<c>&lt;vmRoot&gt;/caches</c>).</summary>
    public string CacheRoot { get; }

    /// <summary>What actually went wrong.</summary>
    public string Detail { get; }
}

/// <summary>
/// The per-agent cache directory could not be created, is not writable by the remapped jail gid, or is
/// not present/writable <i>inside the container</i> once it started.
///
/// <para>The in-container half matters on its own: "the daemon's records say the mount was requested"
/// and "the container really has a writable cache at the mount point" are different facts, and MG-42
/// already paid for assuming the first implies the second. The engine therefore measures it in the real
/// jail after start, and a jail that cannot write its own cache never gets handed to a caller.</para>
/// </summary>
public sealed class PackageCacheUnavailableException : PackageCacheException
{
    public PackageCacheUnavailableException(string cacheRoot, string agentCachePath, string detail)
        : base(cacheRoot, $"the cache for this agent at '{agentCachePath}' is unavailable — {detail}")
        => AgentCachePath = agentCachePath;

    /// <summary>The per-agent cache directory that could not be made usable.</summary>
    public string AgentCachePath { get; }
}

/// <summary>
/// The cache root is over its size budget and eviction could not bring it back under.
///
/// <para>This is reached only when every cache that is <i>not</i> currently mounted into a live jail has
/// already been evicted and the remainder still exceeds the budget — i.e. the in-use caches alone do not
/// fit. Refusing here is deliberate: the alternative is evicting a cache out from under a running jail,
/// and a package folder deleted mid-restore is not a cache miss, it is corruption that presents as a
/// build failure in someone else's branch.</para>
/// </summary>
public sealed class PackageCacheOverBudgetException : PackageCacheException
{
    public PackageCacheOverBudgetException(string cacheRoot, long usedBytes, long budgetBytes, int retainedInUse)
        : base(cacheRoot, string.Create(CultureInfo.InvariantCulture,
            $"it holds {usedBytes:N0} bytes against a {budgetBytes:N0} byte budget, and the {retainedInUse} cache(s) "
            + $"still mounted into live jails cannot be evicted without corrupting a restore in progress. "
            + $"Raise the budget or stop some agents."))
    {
        UsedBytes = usedBytes;
        BudgetBytes = budgetBytes;
        RetainedInUse = retainedInUse;
    }

    /// <summary>Bytes measured across the whole cache root after eviction.</summary>
    public long UsedBytes { get; }

    /// <summary>The budget that was exceeded.</summary>
    public long BudgetBytes { get; }

    /// <summary>How many caches were retained because a live jail still mounts them.</summary>
    public int RetainedInUse { get; }
}
