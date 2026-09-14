using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// The process holding the daemon's loopback port, identified by the port rather than by a payload
/// directory.
///
/// <para><b>Why the port is the right key.</b> <c>MacDaemonController.IsInstanceLockHeld</c> already
/// records why <c>pgrep -f &lt;dll path&gt;</c> is the wrong one: it "cannot see a daemon started from a
/// DIFFERENT payload directory — which is exactly the second instance that does the damage, since the
/// shared state is the data root, not the payload." F55 moved the LIVENESS answer onto the instance
/// lock and left the STOP path on pgrep, so a daemon launched from a build you have since deleted was
/// invisible to every later build: nothing could find it, and nothing could stop it. One such daemon
/// squatted on 5250 for fifteen days, launched from a git worktree that no longer existed.</para>
///
/// <para>The port is machine-scoped, exactly like the data root it guards, and it is observable without
/// the holder's cooperation — so this also sees daemons built before the instance lock existed, which
/// hold no <c>daemon.lock</c> at all and are therefore invisible to the lock probe too.</para>
/// </summary>
/// <param name="Pid">The listening process id.</param>
/// <param name="CommandLine">Its full command line, as <c>ps</c> reports it.</param>
public sealed record DaemonPortHolder(int Pid, string CommandLine)
{
    /// <summary>The daemon assembly a payload-launched mainguardd is started from.</summary>
    public const string DaemonAssemblyName = "Mainguard.Server.dll";

    /// <summary>The installed daemon's executable name (launchd/systemd unit, no dll on the line).</summary>
    public const string DaemonExecutableName = "mainguardd";

    /// <summary>
    /// True when the command line identifies this as a Mainguard daemon at all. Deliberately broader
    /// than <see cref="PayloadDllPath"/>: knowing "this is one of ours" is what makes it safe to offer
    /// to stop it, and that must not depend on being able to parse a path out of the line.
    /// </summary>
    public bool IsMainguardDaemon =>
        CommandLine.Contains(DaemonAssemblyName, StringComparison.Ordinal)
        || CommandLine.Contains(DaemonExecutableName, StringComparison.Ordinal);

    /// <summary>
    /// The daemon dll this process was launched from, or null when the line does not carry one (an
    /// installed unit) or could not be parsed.
    /// </summary>
    public string? PayloadDllPath => ExtractPayloadDll(CommandLine);

    /// <summary>
    /// True only when this is a Mainguard daemon whose own payload dll is <b>gone from disk</b> — a
    /// daemon orphaned by a deleted build, worktree or checkout. It cannot be restarted, cannot be
    /// updated, and will never be stopped by any later build's payload-scoped stop path, so it is
    /// unambiguously safe to offer to terminate.
    ///
    /// <para>False whenever the path could not be parsed: a parse miss must read as "cannot prove it is
    /// stale", never as "stale". The safe direction is to leave the process alone.</para>
    /// </summary>
    public bool PayloadIsGone => PayloadDllPath is { } dll && !File.Exists(dll);

    /// <summary>
    /// Best-effort extraction of the <c>…/Mainguard.Server.dll</c> argument from a command line.
    ///
    /// <para>Whitespace-delimited, so a payload path containing spaces yields null rather than a wrong
    /// path — which is the direction that fails safe, since the only decision this feeds is
    /// <see cref="PayloadIsGone"/>.</para>
    /// </summary>
    internal static string? ExtractPayloadDll(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var match = Regex.Match(
            commandLine,
            @"(?<path>\S*" + Regex.Escape(DaemonAssemblyName) + @")(?:\s|$)",
            RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            return null;
        }

        var path = match.Groups["path"].Value.Trim('"');
        return Path.IsPathRooted(path) ? path : null;
    }

    /// <summary>
    /// Who is listening on <paramref name="port"/> on loopback, or null when nothing is. Answers from
    /// <c>lsof</c> + <c>ps</c>; any failure of either answers null rather than guessing.
    /// </summary>
    public static async Task<DaemonPortHolder?> FindAsync(int port, CancellationToken ct)
    {
        var pid = await ListeningPidAsync(port, ct).ConfigureAwait(false);
        if (pid is null)
        {
            return null;
        }

        var command = await CommandLineAsync(pid.Value, ct).ConfigureAwait(false);
        return new DaemonPortHolder(pid.Value, command ?? string.Empty);
    }

    private static async Task<int?> ListeningPidAsync(int port, CancellationToken ct)
    {
        // -t: pids only. Scoped to a LISTENing TCP socket on this port, so an outbound connection to it
        // (the app's own gRPC channel) is never mistaken for the server.
        var output = await RunAsync(
            "/usr/sbin/lsof",
            ct,
            "-nP",
            $"-iTCP:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            "-sTCP:LISTEN",
            "-t").ConfigureAwait(false)
            ?? await RunAsync(
                "/usr/bin/lsof",
                ct,
                "-nP",
                $"-iTCP:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                "-sTCP:LISTEN",
                "-t").ConfigureAwait(false);

        if (output is null)
        {
            return null;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(line.Trim(), out var pid) && pid != Environment.ProcessId)
            {
                return pid;
            }
        }

        return null;
    }

    private static async Task<string?> CommandLineAsync(int pid, CancellationToken ct)
    {
        var output = await RunAsync(
            "/bin/ps",
            ct,
            "-o",
            "command=",
            "-p",
            pid.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);

        return output?.Trim();
    }

    private static async Task<string?> RunAsync(string executable, CancellationToken ct, params string[] args)
    {
        if (!File.Exists(executable))
        {
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            // lsof exits 1 when nothing matches, which is an answer ("nobody"), not a failure.
            return string.IsNullOrWhiteSpace(stdout) ? null : stdout;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
