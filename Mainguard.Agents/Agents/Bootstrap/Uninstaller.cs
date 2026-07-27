using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>User-selectable uninstall choices. Both default to the safe option.</summary>
/// <param name="KeepSettings">Leave <c>%LocalAppData%\Mainguard</c> settings/keyring in place.</param>
/// <param name="RemoveSyncRemote">Also strip the quarantine sync remote from known repos (default off —
/// it never touches repo working trees, only removes the one added remote).</param>
public sealed record UninstallOptions(bool KeepSettings = false, bool RemoveSyncRemote = false);

/// <summary>
/// The in-VM commands the uninstaller's <c>stop-daemon</c> step runs, in order. Pure argument-list
/// builders (the exe only supplies the runner) so the SHAPE is unit-testable without a process — the
/// same discipline as <see cref="WslCommands"/>/<see cref="InstallerCommands"/>.
/// </summary>
public static class UninstallDaemonCommands
{
    /// <summary>
    /// Stop <c>mainguardd</c> before the distro is terminated: ask systemd first, then a last-resort
    /// process kill for a daemon that is running outside its unit (hand-started, or a unit systemd has
    /// already given up on). Both are best-effort — the caller ignores failures, and the distro is
    /// terminated immediately afterwards regardless.
    /// <para><b>Audit MG-34:</b> the kill matches the process NAME exactly (<c>pkill -x mainguardd</c>),
    /// never <c>pkill -f mainguardd</c>. <c>-f</c> matches any process whose full command line merely
    /// CONTAINS the string — an editor with <c>mainguardd.service</c> open, a <c>tail -f</c> on its log,
    /// a <c>grep mainguardd</c> — and this runs as root inside the distro, so a fuzzy match kills the
    /// user's unrelated processes during an uninstall. The presence probes already use the exact form
    /// (<c>pgrep -x</c>, audit fix #10); the payload renames the apphost to <c>mainguardd</c> precisely
    /// so that exact match works.</para>
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> StopSequence() => new[]
    {
        DaemonUpdateCommands.StopUnit(),
        WslCommands.InDistroAsRoot("pkill", "-x", DaemonUpdateCommands.UnitName),
    };
}

/// <summary>The ordered, failure-tolerant uninstall report. <see cref="Clean"/> iff nothing errored.</summary>
public sealed record UninstallReport(
    IReadOnlyList<string> StepsRun,
    IReadOnlyList<Exception> Errors,
    bool DistroUnregistered,
    IReadOnlyList<string> RunningDistrosBefore,
    IReadOnlyList<string> RunningDistrosAfter)
{
    public bool Clean => Errors.Count == 0;

    /// <summary>The personal distros the uninstall must NOT have stopped (G-12): everything running
    /// before, minus our own <c>MainguardEnv</c>. The matrix asserts these all still run afterward.</summary>
    public IReadOnlyList<string> PersonalDistrosBefore =>
        RunningDistrosBefore.Where(d => !string.Equals(d, WslCommands.DistroName, StringComparison.Ordinal)).ToArray();
}

/// <summary>
/// Clean uninstall (P2-22 §J-6). Ordered, each step failure-tolerant (a failure is recorded and the
/// next step still runs — a half-broken machine must always finish cleaning). The lifecycle verbs are
/// scoped to <c>MainguardEnv</c> ONLY via <see cref="WslCommands"/> (G-12: the VM-wide stop verb is never
/// emitted — a personal distro is never touched), and the running-distro diff is captured so the manual
/// matrix can prove it. Windows-specific removal (registry, Scheduled Tasks, appdata) is injected so the
/// full ordering is unit-tested cross-platform with fakes.
/// </summary>
public sealed class Uninstaller
{
    private readonly IWslRunner _wsl;
    private readonly IRegistryCommandRunner _registry;
    private readonly Func<CancellationToken, Task>? _stopDaemon;
    private readonly Func<CancellationToken, Task>? _removeScheduledTasks;
    private readonly Func<bool, CancellationToken, Task>? _removeAppData;
    private readonly Func<CancellationToken, Task>? _removeSyncRemote;
    private readonly Func<CancellationToken, Task>? _removeElevatedComponents;
    private readonly IBootstrapFileSystem? _wslConfigFs;
    private readonly int _terminatePollAttempts;
    private readonly TimeSpan _terminatePollDelay;

    public Uninstaller(
        IWslRunner wsl,
        IRegistryCommandRunner registry,
        Func<CancellationToken, Task>? stopDaemon = null,
        Func<CancellationToken, Task>? removeScheduledTasks = null,
        Func<bool, CancellationToken, Task>? removeAppData = null,
        Func<CancellationToken, Task>? removeSyncRemote = null,
        IBootstrapFileSystem? wslConfigFs = null,
        int terminatePollAttempts = 10,
        TimeSpan? terminatePollDelay = null,
        Func<CancellationToken, Task>? removeElevatedComponents = null)
    {
        _wsl = wsl ?? throw new ArgumentNullException(nameof(wsl));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _stopDaemon = stopDaemon;
        _removeScheduledTasks = removeScheduledTasks;
        _removeAppData = removeAppData;
        _removeSyncRemote = removeSyncRemote;
        _removeElevatedComponents = removeElevatedComponents;
        _wslConfigFs = wslConfigFs;
        _terminatePollAttempts = terminatePollAttempts;
        _terminatePollDelay = terminatePollDelay ?? TimeSpan.FromMilliseconds(500);
    }

    public async Task<UninstallReport> RunAsync(UninstallOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var steps = new List<string>();
        var errors = new List<Exception>();

        var runningBefore = await SafeListRunning(errors, ct).ConfigureAwait(false);

        // 1. Stop the daemon (best-effort).
        await RunStep(steps, errors, "stop-daemon", ct => _stopDaemon?.Invoke(ct) ?? Task.CompletedTask, ct).ConfigureAwait(false);

        // 2. Terminate OUR distro only (G-12 — never the VM-wide stop verb).
        await RunStep(steps, errors, "terminate-distro",
            async c => { await _wsl.RunAsync(WslCommands.Terminate(), null, c).ConfigureAwait(false); }, ct).ConfigureAwait(false);

        // 3. Poll until MainguardEnv is no longer running before unregistering.
        await RunStep(steps, errors, "poll-stopped", PollStoppedAsync, ct).ConfigureAwait(false);

        // 4. Unregister OUR distro (removes the VM + its provisioned repos — the mirror lives inside it).
        var unregistered = false;
        await RunStep(steps, errors, "unregister-distro", async c =>
        {
            var r = await _wsl.RunAsync(WslCommands.Unregister(), null, c).ConfigureAwait(false);
            unregistered = r.Succeeded;
        }, ct).ConfigureAwait(false);

        // 5. Remove registry integration (context menu + protocol handler).
        await RunStep(steps, errors, "remove-registry", async c =>
        {
            foreach (var cmd in WindowsIntegration.UninstallCommands())
                await _registry.RunAsync(cmd, c).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // 6. Remove Scheduled Tasks (the resume task, any others).
        await RunStep(steps, errors, "remove-scheduled-tasks",
            c => _removeScheduledTasks?.Invoke(c) ?? Task.CompletedTask, ct).ConfigureAwait(false);

        // 6b. MG-15: remove the elevated components from the administrator-owned install root. This
        // step exists BECAUSE of the relocation: moving the elevated helper out of %LocalAppData% put
        // it somewhere the per-user uninstall cannot reach, and a relocation that leaks an
        // administrator-owned directory on uninstall is a bug — the user believes Mainguard is gone
        // while a Mainguard binary is still installed under Program Files. Failure-tolerant like every
        // other step, but the delegate REPORTS what it could not remove rather than swallowing it.
        await RunStep(steps, errors, "remove-elevated-components",
            c => _removeElevatedComponents?.Invoke(c) ?? Task.CompletedTask, ct).ConfigureAwait(false);

        // 7. Revert Mainguard's [wsl2] keys in the user's GLOBAL .wslconfig (audit fix #12): the
        // memory cap applies to EVERY WSL2 distro on the machine, so leaving it behind kept the
        // user's personal distros capped after Mainguard was gone. Conservative (a hand-tuned value
        // survives — see WslConfigMerger.RemoveMainguardKeys) and backed up before the write.
        if (_wslConfigFs is { } fs)
        {
            await RunStep(steps, errors, "revert-wslconfig", _ =>
            {
                var existing = fs.ReadWslConfig();
                var reverted = WslConfigMerger.RemoveMainguardKeys(existing);
                if (existing is not null && !string.Equals(reverted, existing, StringComparison.Ordinal))
                {
                    fs.BackupWslConfig();
                    fs.WriteWslConfig(reverted);
                }

                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);
        }

        // 8. Remove appdata unless the user chose keep-settings.
        await RunStep(steps, errors, "remove-appdata",
            c => _removeAppData?.Invoke(options.KeepSettings, c) ?? Task.CompletedTask, ct).ConfigureAwait(false);

        // 9. Optional: strip the quarantine sync remote from known repos (default off).
        if (options.RemoveSyncRemote)
            await RunStep(steps, errors, "remove-sync-remote",
                c => _removeSyncRemote?.Invoke(c) ?? Task.CompletedTask, ct).ConfigureAwait(false);

        var runningAfter = await SafeListRunning(errors, ct).ConfigureAwait(false);
        return new UninstallReport(steps, errors, unregistered, runningBefore, runningAfter);
    }

    private async Task PollStoppedAsync(CancellationToken ct)
    {
        for (var i = 0; i < _terminatePollAttempts; i++)
        {
            var running = await ListRunning(ct).ConfigureAwait(false);
            if (!running.Contains(WslCommands.DistroName, StringComparer.Ordinal))
                return;
            await Task.Delay(_terminatePollDelay, ct).ConfigureAwait(false);
        }
        // Not fatal: unregister will still proceed (a stuck distro is force-unregistered).
    }

    private async Task<IReadOnlyList<string>> ListRunning(CancellationToken ct)
    {
        var r = await _wsl.RunAsync(WslCommands.ListRunning(), null, ct).ConfigureAwait(false);
        return WslRunner.ParseDistroList(r.StdOut);
    }

    private async Task<IReadOnlyList<string>> SafeListRunning(List<Exception> errors, CancellationToken ct)
    {
        try { return await ListRunning(ct).ConfigureAwait(false); }
        catch (Exception ex) { errors.Add(ex); return Array.Empty<string>(); }
    }

    private static async Task RunStep(List<string> steps, List<Exception> errors, string name, Func<CancellationToken, Task> step, CancellationToken ct)
    {
        steps.Add(name);
        try
        {
            await step(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errors.Add(new InvalidOperationException($"Uninstall step '{name}' failed: {ex.Message}", ex));
        }
    }
}
