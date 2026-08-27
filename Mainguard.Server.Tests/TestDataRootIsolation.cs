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
/// <para>The sandbox root stays under <c>%TEMP%</c> wherever a Unix socket can be bound inside it, which
/// matters beyond tidiness: the agent-IPC root is bind-mounted into real jails, and
/// <c>ContainerSpecBuilder</c>'s G-11 gate rejects <c>/mnt/&lt;drive&gt;/</c> and <c>&lt;letter&gt;:\</c>
/// mount sources. A temp path is neither — and neither is the short home-directory fallback
/// <see cref="ResolveSandboxRoot"/> uses when <c>%TEMP%</c> is too long, which is every run on macOS.</para>
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

    /// <summary>
    /// The container used when <c>%TEMP%</c> cannot hold a bindable socket — see
    /// <see cref="ResolveSandboxRoot"/>. Deliberately NOT under <c>~/.mainguard</c>:
    /// <see cref="DataRootIsolationTests"/> fails a root that IS, or sits INSIDE, the real user root.
    /// </summary>
    private const string ShortContainerName = ".mg-server-tests";

    /// <summary>
    /// <c>&lt;root&gt;/agent-ipc/&lt;agentId&gt;/daemon.sock</c> — the longest path the daemon binds
    /// beneath a data root. <c>AgentIpcServer</c> truncates the agent id to 12 characters, so this is a
    /// fixed 35 rather than a guess.
    /// </summary>
    internal const int AgentIpcSocketSuffixLength = 35;

    /// <summary>
    /// <c>sockaddr_un.sun_path</c> holds <b>104</b> bytes on macOS/BSD and <b>108</b> on Linux — a hard
    /// kernel limit, and <see cref="System.Net.Sockets.UnixDomainSocketEndPoint"/> throws rather than
    /// truncating past it.
    /// </summary>
    internal static int MaxUnixSocketPathLength => OperatingSystem.IsMacOS() ? 104 : 108;

    [ModuleInitializer]
    internal static void RedirectDataRootToTempDirectory()
    {
        // The same isolation, for the one piece of daemon state that is NOT under the data root: the live
        // session store's reconcile against the container engine. Docker is machine-wide, and the Mac
        // substrate's mirror root (~/mainguard) is not governed by MAINGUARD_DATA_ROOT — so an in-proc
        // daemon here answers "yes, I host that repository" for a developer's real jails and adopts them
        // into its own store, at which point `ListAgents` in an unrelated test returns somebody's live
        // coordinator. Switched off for the whole assembly, exactly like the data root below and for the
        // same reason; the pass's own coverage is the RequiresDocker tier, which drives it directly
        // against containers it created itself.
        Environment.SetEnvironmentVariable(
            Mainguard.Server.Runtime.AgentSessionReconcilerService.DisableVariable, "1");

        var existing = Environment.GetEnvironmentVariable(MainguardPaths.DataRootOverrideVariable);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            // Someone pinned the root deliberately. Leave it — but the guard test still verifies it is
            // not the real user root, so "pinned" can never quietly mean "pinned at ~/.mainguard".
            return;
        }

        var (container, root) = ResolveSandboxRoot();
        Directory.CreateDirectory(root);
        PruneAbandonedRuns(container, root);

        Environment.SetEnvironmentVariable(MainguardPaths.DataRootOverrideVariable, root);

        // Best effort: a run that is killed hard leaves its directory behind under %TEMP%, which is inert
        // and eventually swept by the OS. Never let cleanup failure fail a test run.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(root);
    }

    /// <summary>
    /// Picks a throwaway root that a Unix-domain socket can still be BOUND inside.
    ///
    /// <para>The obvious root — <c>%TEMP%/mainguard-server-tests/run-&lt;pid&gt;-&lt;guid&gt;</c> — is
    /// unbindable on macOS, and nothing about the failure says so. <c>Path.GetTempPath()</c> there is the
    /// per-user <c>/var/folders/&lt;..&gt;/T/</c>, <b>49 characters before anything is appended</b>; the
    /// agent-IPC socket lands near 149 against a 104-byte <c>sun_path</c>, and every socket-binding test
    /// in the suite dies on <c>ArgumentOutOfRangeException</c> naming a path length, far from the code
    /// that chose the path. On Linux the same root comes to 105 against 108 — it fits, but by three
    /// characters nobody picked, which is why the budget is asserted here and in
    /// <c>DataRootIsolationTests</c> rather than left to hold by luck.</para>
    ///
    /// <para>So: keep the temp root wherever it fits (Linux and Windows are untouched), and fall back to
    /// a short home-directory container when it does not. The fallback is still per-run, still swept, and
    /// still G-11 legal — a home path is neither <c>/mnt/&lt;drive&gt;/</c> nor <c>&lt;letter&gt;:\</c>,
    /// which is the property the class comment above requires of a bind-mount source.</para>
    /// </summary>
    private static (string Container, string Root) ResolveSandboxRoot()
    {
        var budget = MaxUnixSocketPathLength - AgentIpcSocketSuffixLength;

        var tempContainer = Path.Combine(Path.GetTempPath(), ContainerName);
        var tempRoot = Path.Combine(tempContainer, $"run-{Environment.ProcessId}-{Guid.NewGuid():N}");
        if (tempRoot.Length <= budget)
            return (tempContainer, tempRoot);

        // The guid only has to keep concurrent runs apart, and paired with the pid eight hex digits do
        // that as well as thirty-two for a directory that lives minutes.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var shortContainer = Path.Combine(home, ShortContainerName);
        var shortId = Guid.NewGuid().ToString("N")[..8];
        var shortRoot = Path.Combine(shortContainer, $"run-{Environment.ProcessId}-{shortId}");
        if (shortRoot.Length <= budget)
            return (shortContainer, shortRoot);

        throw new InvalidOperationException(
            $"No sandbox data root fits: an agent-IPC socket needs {AgentIpcSocketSuffixLength} characters "
            + $"beneath the root and this platform allows {MaxUnixSocketPathLength} in total, leaving "
            + $"{budget} for the root itself. Tried '{tempRoot}' ({tempRoot.Length}) and '{shortRoot}' "
            + $"({shortRoot.Length}). Set {MainguardPaths.DataRootOverrideVariable} to a shorter absolute "
            + "path to run the suite.");
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
