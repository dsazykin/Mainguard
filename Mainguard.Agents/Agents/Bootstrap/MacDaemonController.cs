using System;
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

    /// <summary>True when a daemon started from <paramref name="payloadDirectory"/> is running.</summary>
    public async Task<bool> IsRunningAsync(string payloadDirectory, CancellationToken ct)
        => await PgrepAsync(DaemonDllPath(payloadDirectory), ct).ConfigureAwait(false) is not null;

    /// <summary>
    /// Starts the daemon from the payload when it is not already running. Detached — the daemon
    /// outlives this process's UI thread; its own logs land under the data root as always. False
    /// (with no throw) when the payload is absent: the caller's diagnosis names the path.
    /// </summary>
    public async Task<bool> EnsureStartedAsync(string payloadDirectory, CancellationToken ct)
    {
        var dll = DaemonDllPath(payloadDirectory);
        if (!File.Exists(dll)) return false;
        if (await PgrepAsync(dll, ct).ConfigureAwait(false) is not null) return true;

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

    /// <summary>The dotnet muxer: DOTNET_HOST_PATH when set, the standard install, else PATH lookup.</summary>
    internal static string DotnetMuxerPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        const string standard = "/usr/local/share/dotnet/dotnet";
        return File.Exists(standard) ? standard : "dotnet";
    }
}
