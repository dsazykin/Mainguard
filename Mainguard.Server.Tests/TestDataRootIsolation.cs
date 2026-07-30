using System;
using System.IO;
using System.Runtime.CompilerServices;
using Mainguard.Git;

namespace Mainguard.Server.Tests;

/// <summary>
/// Points the ENTIRE daemon test assembly at a throwaway data root before a single test runs — the
/// <c>Mainguard.Server.Tests</c> twin of <c>Mainguard.Tests/TestDataRootIsolation.cs</c> (PR #287).
///
/// <para><b>Why this suite needed its own.</b> PR #287 fixed the desktop suite and flagged this one as
/// "likely" affected without proving it. It is affected, and the mechanism is specific:
/// <see cref="DaemonOptions.TokenPath"/> IS temp-isolated by the in-proc host fixtures, and every
/// <c>DaemonHost.Resolve*</c> store path prefers "next to the token" — but each one FALLS BACK to
/// <see cref="MainguardPaths.DataRoot"/> when no token path is supplied, and enough of the suite
/// constructs hosts that way that the fallback fires constantly. Measured on a clean
/// <c>Category!=RequiresDocker</c> run against the developer's box, one pass rewrote
/// <c>~/.mainguard/daemon.token</c>, <c>daemon-client.pfx</c> and <c>daemon-server.cer</c> (the real
/// mTLS identity), touched <c>mainguard-daemon.db</c> while holding it open with a live <c>-wal</c>/
/// <c>-shm</c> pair, appended to the real <c>mainguard-plans.json</c> and the <c>logs/</c> sinks, and
/// left 13 fresh <c>agent-ipc/&lt;id&gt;/</c> directories — each with a bound Unix socket and a spawn
/// shim — in the user's data root. The 1163 such directories that had accumulated there are the same
/// leak, counted over months.</para>
///
/// <para><b>Shape:</b> a <see cref="ModuleInitializerAttribute"/>, deliberately — not a fixture each
/// test class opts into. A module initializer is per-ASSEMBLY by construction (which is why this file
/// exists alongside the desktop suite's rather than being shared), and the runtime runs it before ANY
/// type here is touched, so there is no ordering to get wrong and no way for a new daemon test to
/// forget and silently fall back to real user data. The relocation seam itself is not duplicated: this
/// sets the same <see cref="MainguardPaths.DataRootOverrideVariable"/> that PR #287 added, and because
/// it is an environment variable the spawned daemons, harnesses and jails inherit the sandbox too.</para>
///
/// <para>The sandbox root stays under <c>%TEMP%</c>, which matters here beyond tidiness: the agent-IPC
/// root is bind-mounted into real jails, and <c>ContainerSpecBuilder</c>'s G-11 gate rejects
/// <c>/mnt/&lt;drive&gt;/</c> and <c>&lt;letter&gt;:\</c> mount sources. A temp path is neither.</para>
///
/// <para>An externally set <c>MAINGUARD_DATA_ROOT</c> is respected — pin it to inspect what a run wrote
/// — and <see cref="DataRootIsolationTests"/> still fails the run if that value is the real user
/// root.</para>
/// </summary>
internal static class TestDataRootIsolation
{
    /// <summary>
    /// The container for this suite's throwaway roots. Deliberately NOT the desktop suite's
    /// <c>mainguard-tests</c>: the two assemblies run concurrently in CI, and separate containers keep
    /// one suite's abandoned-run sweep from ever walking the other's live directories.
    /// </summary>
    private const string ContainerName = "mainguard-server-tests";

    [ModuleInitializer]
    internal static void RedirectDataRootToTempDirectory()
    {
        var existing = Environment.GetEnvironmentVariable(MainguardPaths.DataRootOverrideVariable);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            // Someone pinned the root deliberately. Leave it — but the guard test still verifies it is
            // not the real user root, so "pinned" can never quietly mean "pinned at ~/.mainguard".
            return;
        }

        var container = Path.Combine(Path.GetTempPath(), ContainerName);
        var root = Path.Combine(container, $"run-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        PruneAbandonedRuns(container, root);

        Environment.SetEnvironmentVariable(MainguardPaths.DataRootOverrideVariable, root);

        // Best effort: a run that is killed hard leaves its directory behind under %TEMP%, which is inert
        // and eventually swept by the OS. Never let cleanup failure fail a test run.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(root);
    }

    /// <summary>
    /// Sweeps roots left by runs that were KILLED (a hung suite, a cancelled CI job) — <c>ProcessExit</c>
    /// never fires for those, so without this they accumulate. Only directories untouched for a day are
    /// removed, which cannot race a run happening right now, and never this run's own root.
    /// </summary>
    private static void PruneAbandonedRuns(string container, string ownRoot)
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (var stale in Directory.EnumerateDirectories(container, "run-*"))
            {
                if (string.Equals(stale, ownRoot, StringComparison.Ordinal))
                    continue;
                if (Directory.GetLastWriteTimeUtc(stale) < cutoff)
                    TryDelete(stale);
            }
        }
        catch (Exception)
        {
            // Housekeeping must never fail a test run.
        }
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (Exception)
        {
            // Cleanup is a courtesy, not a contract.
        }
    }
}
