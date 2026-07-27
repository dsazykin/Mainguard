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
            wslConfigFs: new BootstrapFileSystem(),
            removeElevatedComponents: RemoveElevatedComponentsAsync);

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

    /// <summary>
    /// MG-15 inverse: remove <c>%ProgramFiles%\Mainguard</c>, where the elevated helper was relocated to.
    ///
    /// <para>Two attempts, in order. First a direct delete — which succeeds when the uninstaller is
    /// itself running elevated (the ordinary "Uninstall" entry point on Windows runs from an elevated
    /// context when the user is an administrator). If that leaves anything behind, ask the elevated
    /// helper to remove its own install (<c>--uninstall</c>) across a UAC prompt: the helper is the only
    /// component with the rights, and removal is the enumerated inverse of the install action, not a new
    /// capability. If BOTH fail we throw — deliberately, so the uninstall report is not clean and the
    /// user is told exactly which folder is still on their machine. A silent leak here would leave an
    /// administrator-owned Mainguard binary installed on a machine the user believes is clean.</para>
    /// </summary>
    private static Task RemoveElevatedComponentsAsync(CancellationToken ct)
    {
        var protection = ProtectedLocationPolicy.ForHost();
        if (!ElevatedComponentPlan.TryForHost(protection, AppContext.BaseDirectory, out var plan, out var refusal))
        {
            Console.WriteLine($"  elevated components: nothing to remove ({refusal}).");
            return Task.CompletedTask;
        }

        var direct = ElevatedComponentInstaller.Remove(plan);
        if (direct.Clean)
        {
            Console.WriteLine($"  elevated components: {direct.Message}");
            return Task.CompletedTask;
        }

        if (TryElevatedRemoval(out var elevatedError))
        {
            var second = ElevatedComponentInstaller.Remove(plan);
            if (second.Clean)
            {
                Console.WriteLine($"  elevated components: {second.Message}");
                return Task.CompletedTask;
            }

            throw new InvalidOperationException(second.Message);
        }

        throw new InvalidOperationException($"{direct.Message} ({elevatedError})");
    }

    /// <summary>Runs the staged helper's <c>--uninstall</c> across a UAC prompt. The STAGED copy is used
    /// on purpose: a running process cannot delete the directory it was loaded from, so the promoted
    /// copy could not remove itself.</summary>
    private static bool TryElevatedRemoval(out string error)
    {
        var staged = ElevatedComponentLayout.StagedHelperPath(AppContext.BaseDirectory);
        if (!File.Exists(staged))
        {
            staged = Path.Combine(AppContext.BaseDirectory, ElevatedComponentLayout.HelperFileName);
            if (!File.Exists(staged))
            {
                error = "the elevated helper is not present in this install, so the protected folder "
                    + "could not be removed automatically";
                return false;
            }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = staged,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--uninstall",
            };
            using var p = Process.Start(psi);
            p?.WaitForExit();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"the elevated removal could not be started: {ex.Message}";
            return false;
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
