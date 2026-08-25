using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// Read-only tail of the in-VM daemon logs for the App's "Daemon logs" settings panel, over the same
/// <see cref="IWslRunner"/> seam <c>WslDaemonHealthProbe</c> already uses for the OOBE error card. Two
/// sources: the unified systemd journal (<c>journalctl -u mainguardd</c>, all subsystems interleaved) and
/// the per-subsystem rolling files under <c>~/.mainguard/logs/&lt;subsystem&gt;.log</c>.
///
/// <para><b>Never throws.</b> A non-zero exit or a WSL failure returns an empty string, so a read
/// surface renders "nothing to show" rather than faulting — diagnostics must never break the surface
/// that reads them.</para>
/// </summary>
public sealed class DaemonLogReader
{
    /// <summary>The VM service user's logs directory: <c>mainguardd</c> runs as uid 1000 with
    /// <c>HOME=/home/mainguard</c>, so its logs sit at <c>/home/mainguard/.mainguard/logs</c> regardless of
    /// the Windows-side data root the App uses.</summary>
    public const string VmLogsDir = "/home/mainguard/.mainguard/logs";

    private readonly IWslRunner _wsl;

    public DaemonLogReader(IWslRunner wsl) => _wsl = wsl ?? throw new ArgumentNullException(nameof(wsl));

    /// <summary>The unified daemon journal tail (every subsystem interleaved), oldest→newest.
    /// macos-host has no journal — the daemon is a host process writing rolling files under the
    /// data root — so the unified view merges every subsystem file's tail by the ISO timestamp
    /// each line starts with (lexicographic == chronological for that format).</summary>
    public Task<string> ReadRecentAsync(int lines, CancellationToken ct = default) =>
        OperatingSystem.IsMacOS()
            ? Task.FromResult(ReadMacUnified(Clamp(lines)))
            : RunAsync(WslCommands.InDistroAsRoot(
                "journalctl", "-u", "mainguardd", "--no-pager", "-n", Clamp(lines).ToString(), "-o", "cat"), ct);

    /// <summary>One subsystem's rolling file tail (e.g. <c>spawn.log</c>), oldest→newest.</summary>
    public Task<string> ReadSubsystemAsync(string subsystem, int lines, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subsystem))
            return Task.FromResult(string.Empty);

        if (OperatingSystem.IsMacOS())
        {
            return Task.FromResult(ReadMacFileTail(
                System.IO.Path.Combine(MacLogsDir(), SanitizeSubsystem(subsystem) + ".log"), Clamp(lines)));
        }

        var file = $"{VmLogsDir}/{SanitizeSubsystem(subsystem)}.log";
        return RunAsync(WslCommands.InDistroAsRoot("tail", "-n", Clamp(lines).ToString(), file), ct);
    }

    /// <summary>The macos-host daemon's own logs directory (a host path — same data root).</summary>
    private static string MacLogsDir() =>
        System.IO.Path.Combine(Mainguard.Git.MainguardPaths.DataRoot(), "logs");

    private static string ReadMacUnified(int lines)
    {
        try
        {
            var dir = MacLogsDir();
            if (!System.IO.Directory.Exists(dir)) return string.Empty;

            var merged = new List<string>();
            foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.log"))
            {
                merged.AddRange(TailLines(file, lines));
            }
            merged.Sort(StringComparer.Ordinal); // ISO-timestamp-prefixed lines sort chronologically
            var skip = merged.Count > lines ? merged.Count - lines : 0;
            return string.Join('\n', merged.GetRange(skip, merged.Count - skip));
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string ReadMacFileTail(string file, int lines)
    {
        try
        {
            return System.IO.File.Exists(file) ? string.Join('\n', TailLines(file, lines)) : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static List<string> TailLines(string file, int lines)
    {
        // Share-tolerant read: the daemon appends to these files while we tail them.
        using var stream = new System.IO.FileStream(
            file, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
        using var reader = new System.IO.StreamReader(stream);
        var buffer = new Queue<string>(lines);
        while (reader.ReadLine() is { } line)
        {
            if (buffer.Count == lines) buffer.Dequeue();
            buffer.Enqueue(line);
        }
        return new List<string>(buffer);
    }

    private async Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            var result = await _wsl.RunAsync(args, stdin: null, ct).ConfigureAwait(false);
            // A missing file (tail: no such file) or a stopped VM is "nothing to show", not an error.
            return result.Succeeded ? result.StdOut : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static int Clamp(int lines) => lines < 1 ? 1 : (lines > 5000 ? 5000 : lines);

    // The subsystem name comes from our own fixed list, but never let a stray path separator or space
    // compose a different argument: keep only lowercased alphanumerics (the canonical names are exactly
    // that). Empty input degrades to the always-present lifecycle log.
    private static string SanitizeSubsystem(string subsystem)
    {
        var sb = new StringBuilder(subsystem.Length);
        foreach (var c in subsystem)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }

        return sb.Length > 0 ? sb.ToString() : "lifecycle";
    }
}
