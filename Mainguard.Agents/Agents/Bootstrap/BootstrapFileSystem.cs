using System;
using System.IO;

using Mainguard.Git;
namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// Default <see cref="IBootstrapFileSystem"/> over the real disk: <c>%UserProfile%\.wslconfig</c>,
/// timestamped backups written before any write, and host-RAM detection. The one place P2-05 touches
/// the filesystem, so the steps stay pure/testable.
/// </summary>
public sealed class BootstrapFileSystem : IBootstrapFileSystem
{
    public BootstrapFileSystem(string? userProfileDir = null)
    {
        // MainguardPaths.HomeDirectory, not GetFolderPath: the default-option GetFolderPath verifies
        // the directory exists and returns "" when it doesn't, silently making this path relative.
        var profile = userProfileDir ?? MainguardPaths.HomeDirectory();
        WslConfigPath = Path.Combine(profile, ".wslconfig");
    }

    public string WslConfigPath { get; }

    public string? ReadWslConfig() =>
        File.Exists(WslConfigPath) ? File.ReadAllText(WslConfigPath) : null;

    public void BackupWslConfig()
    {
        if (!File.Exists(WslConfigPath))
            return;

        // Timestamped so an existing backup is never clobbered (invariant §5.4). Second-resolution
        // stamps can collide on rapid re-runs (audit fix #14) — uniquify instead of throwing.
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var backup = WslConfigPath + $".mainguard.{stamp}.bak";
        for (var n = 1; File.Exists(backup); n++)
            backup = WslConfigPath + $".mainguard.{stamp}-{n}.bak";
        File.Copy(WslConfigPath, backup, overwrite: false);
    }

    /// <summary>
    /// Writes the merged <c>.wslconfig</c> <b>atomically</b>: a sibling temp file is written, flushed to
    /// disk, and then swapped over the target in one filesystem operation (audit MG-32).
    /// </summary>
    public void WriteWslConfig(string content)
    {
        // .wslconfig is GLOBAL — it configures EVERY WSL2 distro on the machine — so a torn write here
        // does not merely break Mainguard, it breaks the user's other distros. File.WriteAllText (what
        // this used to be) truncates the target in place and then streams into it, so any interruption
        // between those two moments — a crash, a full disk, power loss, another process reading — leaves
        // a half-written or empty global config. Writing a sibling temp file and swapping it in means an
        // observer sees either the whole old file or the whole new one, never a truncated one.
        //
        // NOTE — still true after this fix: the read-modify-write around this call (WslConfigMergeStep
        // and the uninstaller's revert step both read, merge, then write) is last-writer-wins across
        // processes, so a concurrent editor's change can still be lost. That is a lost update, not a
        // corrupt file, and the timestamped backup taken immediately before every write keeps it
        // recoverable; closing it fully would need a cross-process lock spanning the read AND the write.
        var directory = Path.GetDirectoryName(WslConfigPath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException($"'{WslConfigPath}' has no parent directory to write into.");

        // The temp MUST live in the target's own directory: the swap is only atomic within one volume
        // (across volumes it degrades to a copy — exactly the non-atomic behaviour being fixed). The
        // GUID keeps two concurrent writers off each other's temp file.
        var temp = Path.Combine(directory, $".wslconfig.mainguard-{Guid.NewGuid():N}.tmp");
        try
        {
            // UTF-8 without BOM, matching what File.WriteAllText produced — a BOM prepended to
            // %UserProfile%\.wslconfig is not something WSL's INI parser should have to tolerate.
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                writer.Write(content);
                writer.Flush();
                // Durable BEFORE the swap: a swap that publishes a file still sitting in the OS write
                // cache would reintroduce "valid name, garbage content" after an unclean shutdown.
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(WslConfigPath))
                // Replace, not Move: on Windows only ReplaceFile carries the original file's attributes
                // and ACLs across, and a plain Move refuses an existing destination outright.
                File.Replace(temp, WslConfigPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temp, WslConfigPath);
        }
        finally
        {
            // The temp only survives when the swap threw — never leave litter in %UserProfile%.
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                /* best-effort cleanup; the real write outcome is what matters */
            }
        }
    }

    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public bool FileExists(string path) => File.Exists(path);

    // GC.GetGCMemoryInfo().TotalAvailableMemoryBytes is the plan-sanctioned GlobalMemoryStatusEx
    // stand-in for the memory= default computation.
    public long TotalPhysicalMemoryBytes => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
}
