using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.Services;
using Mainguard.Git;
using Mainguard.Git.Services;
namespace Mainguard.Uninstall;

/// <summary>
/// The P2-22 §J-6 uninstaller entry point. Parses the two user choices (<c>--keep-settings</c>,
/// <c>--remove-sync-remote</c>) and drives the Core <see cref="Uninstaller"/> with the real Windows
/// delegates. The ordering, failure-tolerance and G-12 distro scoping all live in Core (unit-tested);
/// this file only supplies the concrete side effects.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = new UninstallOptions(
            KeepSettings: args.Contains("--keep-settings"),
            RemoveSyncRemote: args.Contains("--remove-sync-remote"));

        // Capture the persisted repo list UP FRONT: the appdata-removal step (7) deletes the SQLite DB
        // before the optional sync-remote step (8) runs, so read AppDbContext.Repositories.Path now and
        // let the (default-OFF) delegate close over the captured paths.
        var repoPaths = ReadPersistedRepoPaths();

        var uninstaller = new Uninstaller(
            wsl: new WslRunner(),
            registry: new RegExeRegistryCommandRunner(),
            stopDaemon: StopDaemonAsync,
            removeScheduledTasks: RemoveScheduledTasksAsync,
            removeAppData: RemoveAppDataAsync,
            removeSyncRemote: ct => RemoveSyncRemoteAsync(repoPaths, ct),
            // Fix #12: revert Mainguard's [wsl2] keys in the global .wslconfig (backed up first) so
            // the user's personal distros are not left memory-capped after Mainguard is gone.
            wslConfigFs: new BootstrapFileSystem());

        var report = await uninstaller.RunAsync(options, CancellationToken.None).ConfigureAwait(false);

        // Prove G-12: personal distros that were running before are still running after.
        var stoppedPersonal = report.PersonalDistrosBefore
            .Where(d => !report.RunningDistrosAfter.Contains(d, StringComparer.Ordinal))
            .ToArray();

        Console.WriteLine($"Mainguard uninstall {(report.Clean ? "completed" : "completed with warnings")}.");
        Console.WriteLine($"  Steps: {string.Join(" -> ", report.StepsRun)}");
        Console.WriteLine($"  MainguardEnv unregistered: {report.DistroUnregistered}");
        if (stoppedPersonal.Length > 0)
            Console.Error.WriteLine($"  WARNING (G-12): personal distros stopped: {string.Join(", ", stoppedPersonal)}");
        foreach (var e in report.Errors)
            Console.Error.WriteLine($"  ! {e.Message}");

        return report.Clean ? 0 : 1;
    }

    private static async Task StopDaemonAsync(CancellationToken ct)
    {
        // Best-effort: ask the daemon inside the distro to stop before we terminate the distro. The
        // sequence itself (systemd stop, then an EXACT-name kill — never the fuzzy `pkill -f`, audit
        // MG-34) lives in Core so its shape is unit-tested; this only runs it.
        var wsl = new WslRunner();
        foreach (var command in UninstallDaemonCommands.StopSequence())
        {
            try { await wsl.RunAsync(command, null, ct).ConfigureAwait(false); }
            catch { /* distro may already be gone — every step here is best-effort */ }
        }
    }

    private static async Task RemoveScheduledTasksAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var a in InstallerCommands.UnregisterResumeTask())
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is not null) await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch { /* schtasks is Windows-only; a no-op elsewhere */ }
    }

    // Reads the persisted repo paths from the app DB (AppDbContext.Repositories.Path). Best-effort: if
    // the DB is absent/unreadable (fresh machine, already-cleaned appdata) we return an empty list and
    // the sync-remote step simply finds nothing to strip.
    private static IReadOnlyList<string> ReadPersistedRepoPaths()
    {
        try
        {
            using var db = new AppDbContext();
            return db.Repositories.Select(r => r.Path).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // The optional (default-OFF) sync-remote-removal step. Resolves the ONE substrate-defined sync-remote
    // name via the SC-2 resolver (never a hardcoded "mainguard-vm") and removes it from each known repo
    // through the existing GitService primitive, tolerating missing repos / renamed remotes.
    private static Task RemoveSyncRemoteAsync(IReadOnlyList<string> repoPaths, CancellationToken ct)
    {
        // SC-2: the sync-remote name is substrate-local — ask the WSL2 substrate, don't hardcode it.
        var remoteName = new Wsl2AgentEnvironment().ResolveSyncRemote(string.Empty).Name;

        var git = new GitService();
        var purger = new SyncRemotePurger(repoPaths, remoteName, (path, name) => git.RemoveRemote(path, name));
        purger.Run(ct);
        return Task.CompletedTask;
    }

    private static Task RemoveAppDataAsync(bool keepSettings, CancellationToken ct)
    {
        if (keepSettings) return Task.CompletedTask;
        // MainguardPaths, not GetFolderPath: a "" from the exists-checking default would make this
        // RELATIVE — and a relative path handed to Directory.Delete(recursive) is how uninstalls
        // delete the wrong tree.
        var appData = Mainguard.Git.MainguardPaths.DataRoot();
        try
        {
            if (Directory.Exists(appData)) Directory.Delete(appData, recursive: true);
        }
        catch { /* leave residue rather than fail the uninstall */ }
        return Task.CompletedTask;
    }
}
