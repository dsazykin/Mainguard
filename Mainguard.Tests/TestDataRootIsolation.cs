using System;
using System.IO;
using System.Runtime.CompilerServices;
using Mainguard.Git;

namespace Mainguard.Tests;

/// <summary>
/// Points the ENTIRE test assembly at a throwaway data root before a single test runs.
///
/// <para><b>Why:</b> almost everything Mainguard persists resolves through
/// <see cref="MainguardPaths.DataRoot"/> — <c>mainguard.db</c>, <c>config.json</c> (the user's real
/// preferences), the <c>Keyring</c>, <c>oobe-state.json</c>, <c>daemon.token</c>, workspace layouts,
/// adapter pins. With no override that is the DEVELOPER'S OWN <c>~/.mainguard</c>, so the suite both read
/// real user state and wrote to it. The visible symptom was the headless harnesses: <c>App.Initialize</c>
/// migrates that shared SQLite file under a 20-second watchdog, and the developer's copy carried a stale
/// <c>__EFMigrationsLock</c> row (left by an earlier run killed mid-migration — the sharing is what made
/// the runs able to poison each other), which EF waits on forever. Every <c>[AvaloniaFact]</c> in the
/// assembly therefore timed out: <c>StartupShutdownViewModelTests</c> was 7 failed / 0 passed. The lock was
/// the symptom; running the suite against the user's own database was the defect.</para>
///
/// <para><b>Shape:</b> a <see cref="ModuleInitializerAttribute"/>, deliberately — not a fixture each test
/// class opts into. The runtime executes it before ANY type in this assembly is touched, so there is no
/// ordering to get wrong and no way for a new test to forget and silently fall back to real user data.
/// (<c>TestEditionComposition</c> uses the same mechanism for the same reason.) The environment variable
/// is inherited by child processes the suite spawns, so a head or harness it launches lands in the same
/// sandbox.</para>
///
/// <para>An externally set <c>MAINGUARD_DATA_ROOT</c> is respected — pin it to inspect what a run wrote —
/// and <see cref="DataRootIsolationTests"/> still fails the run if that value is the real user root.</para>
/// </summary>
internal static class TestDataRootIsolation
{
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

        var container = Path.Combine(Path.GetTempPath(), "mainguard-tests");
        var root = Path.Combine(container, $"run-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        PruneAbandonedRuns(container, root);

        Environment.SetEnvironmentVariable(MainguardPaths.DataRootOverrideVariable, root);

        // Best effort: a run that is killed hard leaves its directory behind under %TEMP%/mainguard-tests,
        // which is inert and eventually swept by the OS. Never let cleanup failure fail a test run.
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
