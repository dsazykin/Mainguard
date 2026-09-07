using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// Lifecycle of the LOCAL mainguardd on the macos-host substrate: the daemon is an ordinary child
/// process of this machine (no VM, no systemd), started from the app-bundled payload and found
/// again later by its command line. Start is idempotent; stop is SIGTERM-then-wait. The daemon is
/// launched through the dotnet muxer (<c>dotnet Mainguard.Server.dll</c>) rather than the payload
/// apphost, because current macOS pins an executable name to its first-run location and SIGKILLs
/// a same-named apphost anywhere else — the notarized muxer plus a dll is immune (the same rule
/// the test suite's SelfInvocation helper encodes).
/// </summary>
public sealed class MacDaemonController
{
    /// <summary>The marker this controller finds its daemon by (`pgrep -f`): the payload dll path.</summary>
    private static string DaemonDllPath(string payloadDirectory) =>
        Path.Combine(payloadDirectory, "Mainguard.Server.dll");

    /// <summary>Where the packaged app ships the daemon payload (same layout as WSL2's tier-1).</summary>
    public static string DefaultPayloadDirectory() => DaemonUpdater.DefaultPayloadDirectory();

    /// <summary>
    /// The daemon's single-instance lock file, beside <c>daemon.token</c> under the data root.
    ///
    /// <para>Mirrors <c>Mainguard.Server.Runtime.DaemonInstanceLock.FileName</c>, which this assembly
    /// cannot reference (the daemon references the agent platform, not the other way round). A test in
    /// <c>Mainguard.Server.Tests</c> — which references both — pins the two strings equal, so the
    /// duplication cannot drift.</para>
    /// </summary>
    internal const string InstanceLockFileName = "daemon.lock";

    /// <summary>The lock file's absolute path for this user.</summary>
    internal static string InstanceLockPath() => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(Mainguard.Agents.Daemon.DaemonPaths.TokenFilePath()))!,
        InstanceLockFileName);

    /// <summary>
    /// Whether a daemon holds the single-instance lock. <b>F55:</b> this — not <c>pgrep</c> — is the
    /// authoritative liveness answer.
    ///
    /// <para><c>pgrep -f &lt;dll path&gt;</c> is a check-then-act with a multi-second window (container
    /// start, EF migration, port bind all happen after the process exists), it matches on a command line
    /// rather than on ownership of anything, and it cannot see a daemon started from a DIFFERENT payload
    /// directory — which is exactly the second instance that does the damage, since the shared state is
    /// the data root, not the payload. An exclusive open of the lock file asks the kernel who owns the
    /// data root, which is the question that matters, and the answer cannot be stale: the OS releases the
    /// lock when the holder dies, however it dies.</para>
    /// </summary>
    public static bool IsInstanceLockHeld()
    {
        var path = InstanceLockPath();
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(
                path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            return false; // we took it, so nobody held it
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// True when a Mainguard daemon is running for this user. Answers from the instance lock, and falls
    /// back to the payload-scoped <c>pgrep</c> only when no lock file exists at all — which is the
    /// pre-F55 daemon, i.e. an upgrade in progress.
    /// </summary>
    public async Task<bool> IsRunningAsync(string payloadDirectory, CancellationToken ct)
        => IsInstanceLockHeld()
           || await PgrepAsync(DaemonDllPath(payloadDirectory), ct).ConfigureAwait(false) is not null;

    /// <summary>
    /// Starts the daemon from the payload when it is not already running. Detached — the daemon
    /// outlives this process's UI thread; its own logs land under the data root as always. False
    /// (with no throw) when the payload is absent: the caller's diagnosis names the path.
    ///
    /// <para><b>F55.</b> The pre-start check reads the instance lock rather than a process listing, and
    /// the residual race (two callers both seeing "not running") is now harmless rather than damaging:
    /// the loser refuses itself inside <c>DaemonHost</c> before minting or writing anything. This method
    /// is a fast path and a courtesy, not the guard.</para>
    /// </summary>
    public async Task<bool> EnsureStartedAsync(string payloadDirectory, CancellationToken ct)
    {
        var dll = DaemonDllPath(payloadDirectory);
        if (!File.Exists(dll)) return false;
        if (await IsRunningAsync(payloadDirectory, ct).ConfigureAwait(false)) return true;

        var psi = new ProcessStartInfo(DotnetMuxerPath())
        {
            WorkingDirectory = payloadDirectory,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        _ = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start mainguardd.");
        return true;
    }

    /// <summary>SIGTERM the running daemon (if any), wait up to ~10 s, then SIGKILL as the last
    /// resort — graceful first so Kestrel and SQLite close cleanly, but never wedged behind a
    /// hung process.</summary>
    public async Task StopAsync(string payloadDirectory, CancellationToken ct)
    {
        var dll = DaemonDllPath(payloadDirectory);
        var pid = await PgrepAsync(dll, ct).ConfigureAwait(false);
        if (pid is null) return;

        await SignalAsync(pid.Value, "-TERM", ct).ConfigureAwait(false);
        for (var i = 0; i < 20 && await PgrepAsync(dll, ct).ConfigureAwait(false) is not null; i++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        if (await PgrepAsync(dll, ct).ConfigureAwait(false) is { } survivor)
        {
            await SignalAsync(survivor, "-KILL", ct).ConfigureAwait(false);
        }
    }

    private static async Task SignalAsync(int pid, string signal, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("/bin/kill") { UseShellExecute = false };
        psi.ArgumentList.Add(signal);
        psi.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var kill = Process.Start(psi);
        if (kill is not null) await kill.WaitForExitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The daemon's pid, found by its payload-dll command line, or null.</summary>
    private static async Task<int?> PgrepAsync(string marker, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("/usr/bin/pgrep")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(marker);

        using var pgrep = Process.Start(psi);
        if (pgrep is null) return null;
        var output = await pgrep.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await pgrep.WaitForExitAsync(ct).ConfigureAwait(false);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(line.Trim(), out var pid) && pid != Environment.ProcessId)
            {
                return pid;
            }
        }
        return null;
    }

    /// <summary>
    /// The dotnet muxer as an ABSOLUTE path, or null when none of the known locations holds one.
    ///
    /// <para><b>F63(a).</b> The old resolver ended in a bare <c>"dotnet"</c>, which is fine for a child of
    /// an interactive process (it inherits the user's <c>PATH</c>) and fatal for a launchd job, whose
    /// <c>PATH</c> is <c>/usr/bin:/bin:/usr/sbin:/sbin</c> — no <c>/usr/local/share/dotnet</c>, no
    /// Homebrew. A plist built with the bare name produced a job that could not exec, exited immediately,
    /// and (with the unconditional <c>KeepAlive</c> this file's sibling wrote) was respawned by launchd
    /// forever. Callers that need a path for a plist use THIS and refuse to write one when it is null;
    /// <see cref="DotnetMuxerPath"/> keeps the lenient fallback for the interactive case.</para>
    ///
    /// <para>The candidate list is ordered by how authoritative each source is: the host's own
    /// <c>DOTNET_HOST_PATH</c> (set by the muxer that launched us, so it names the exact runtime this
    /// build is running on), then <c>DOTNET_ROOT</c>, then the official installer's location, then the
    /// two Homebrew prefixes (arm64 and x86_64), then a per-user install. Symlinks are resolved so the
    /// plist records a path that does not depend on a Homebrew shim staying put.</para>
    /// </summary>
    internal static string? TryResolveAbsoluteMuxerPath()
    {
        foreach (var candidate in MuxerCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
            {
                continue;
            }

            try
            {
                // A Homebrew `dotnet` is a symlink into the Cellar; record the target so a `brew
                // unlink` cannot silently break the login job.
                var resolved = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                return resolved is not null && File.Exists(resolved) ? resolved : Path.GetFullPath(candidate);
            }
            catch (IOException)
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string?> MuxerCandidates()
    {
        yield return Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");

        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            yield return Path.Combine(root, "dotnet");
        }

        yield return "/usr/local/share/dotnet/dotnet";   // official installer (x86_64 + arm64 layout)
        yield return "/usr/local/share/dotnet/x64/dotnet";
        yield return "/opt/homebrew/bin/dotnet";          // Homebrew, Apple silicon
        yield return "/usr/local/bin/dotnet";             // Homebrew, Intel
        yield return "/opt/homebrew/opt/dotnet/bin/dotnet";
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
    }

    /// <summary>
    /// The dotnet muxer for an INTERACTIVE child of this process: the absolute path when one is known,
    /// otherwise the bare name so the user's own <c>PATH</c> still resolves it. Never use this for a
    /// launchd plist — see <see cref="TryResolveAbsoluteMuxerPath"/>.
    /// </summary>
    internal static string DotnetMuxerPath() => TryResolveAbsoluteMuxerPath() ?? "dotnet";
}
