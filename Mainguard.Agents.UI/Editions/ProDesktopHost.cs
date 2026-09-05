using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Mainguard.Agents;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.Git;
using Mainguard.Git.Services;
using Mainguard.UI.Editions;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;

namespace Mainguard.Agents.UI.Editions;

/// <summary>
/// The Pro/Cloud desktop launch machinery, physically moved out of the (now reference-clean) shell's
/// <c>App</c> in step 2f. Everything here names the agent platform (VM keep-alive, the OOBE wizard, the
/// startup/shutdown sequences, the provisioning launch router) — which is exactly why it lives in the
/// Pro-UI assembly and NOT in the shell. The Pro exe head (Mainguard.Pro.App) hooks the shell's
/// <c>App.ProDesktopStarter</c> / <c>App.VisualizedShutdownAsync</c> seams to these methods; under the
/// Client head those seams stay null and none of this is ever reached.
///
/// <para>It reaches shell capabilities (settings, oobe.log, the repo store, the sync-remote registrar,
/// the shell window) ONLY through the <see cref="ProComposition"/> seams the Pro head wires — never by
/// referencing the shell, the one-way boundary the split exists to hold.</para>
/// </summary>
public static class ProDesktopHost
{
    // ---- VM keep-alive (was App._vmKeepAlive) ----

    /// <summary>Holds MainguardEnv awake while the app runs (<see cref="VmKeepAlive"/>); released on exit
    /// before the optional VM stop. Static (one host) so both exit paths — the visualized shutdown and the
    /// framework Exit backstop — reach the same holder.</summary>
    private static VmKeepAlive? _vmKeepAlive;
    private static int _vmStopRan;

    /// <summary>True once the user picked "Later" on this run's VM upgrade offer — don't nag again this
    /// session (in-memory only; the next launch re-offers).</summary>
    private static bool _vmUpgradeDeclinedThisSession;

    private static void EnsureKeepAlive()
    {
        // macos-host: no MainguardEnv to hold awake — a holder would just spawn a missing
        // wsl.exe in a backoff loop forever. The daemon's own MacSleepAssertion covers sleep.
        if (OperatingSystem.IsMacOS()) return;
        _vmKeepAlive ??= new VmKeepAlive();
    }

    private static void ReleaseKeepAlive()
    {
        _vmKeepAlive?.Dispose();
        _vmKeepAlive = null;
    }

    /// <summary>
    /// The Pro/Cloud launch path — TODAY'S behavior verbatim (1d), minus the tray icon, which is now shared
    /// shell chrome set up before the edition branch: hold MainguardEnv awake, wire the framework Exit
    /// backstop's release + scoped VM stop, sweep the resume task, then route OOBE-vs-control-center.
    /// </summary>
    public static void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Hold MainguardEnv awake for the app's lifetime. WSL idle-terminates the distro seconds after the
        // last wsl.exe client exits (gRPC connections don't count), taking mainguardd down between RPCs.
        // Started from the FIRST moment on BOTH routes: the OOBE wizard's own gRPC steps (repo onboarding)
        // need the VM held just as much as the control center; before the distro exists the holder just
        // retries quietly. The control-center route's startup sequence re-ensures this (idempotent).
        EnsureKeepAlive();

        // Full exit (tray Exit, File > Exit, X with CloseToTray off) is the visualized shutdown
        // (App.RequestFullExit → App.VisualizedShutdownAsync → RunVisualizedShutdownThenExitAsync); this
        // framework Exit hook is the BACKSTOP for a shutdown that bypassed it (OS logoff). Both release the
        // keep-alive FIRST so the stop isn't fighting a holder that would reboot it; both are
        // idempotent-guarded, so whichever ran first wins and this never double-runs.
        desktop.Exit += (_, _) =>
        {
            ReleaseKeepAlive();
            StopVmOnExitBestEffort();
        };

        // FIRST action, before any route decision: resume-task hygiene. A `--resume` launch means the
        // elevated ONLOGON task just fired us — its purpose is served and we are the elevated instance,
        // the one place its deletion can never be denied.
        var launchedByResumeTask = Environment.GetCommandLineArgs().Contains("--resume");
        if (!OperatingSystem.IsMacOS())  // resume tasks are schtasks — Windows machinery only
            SweepResumeTaskAtStartup(launchedByResumeTask);

        var route = DecideLaunchRoute();

        // OOBE is its own sequence (the wizard); the control-center route runs the startup sequence behind
        // the loading screen — VM wake, daemon reachable, tier-1 refresh, and the consented tier-2 upgrade
        // all complete (or degrade with a banner) BEFORE the shell opens.
        desktop.MainWindow = route != LaunchRoute.Oobe
            ? CreateStartupWindow()
            : OperatingSystem.IsMacOS()
                ? CreateMacOobeWindow(desktop)
                : new OobeWizardView { DataContext = CreateOobeWizardViewModel() };
    }

    /// <summary>The macos-host first-run window; completion hands off to the same startup-window
    /// path the ControlCenter route takes, so post-OOBE behavior is one code path.</summary>
    private static MacOobeWindow CreateMacOobeWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        MacOobeWindow? window = null;
        window = new MacOobeWindow
        {
            DataContext = new MacOobeViewModel(onCompleted: () =>
            {
                var startup = CreateStartupWindow();
                desktop.MainWindow = startup;
                startup.Show();
                window?.Close();
            }),
        };
        return window;
    }

    /// <summary>Shows the shutdown window, runs <see cref="AppShutdownSequence"/> (release keep-alive, and —
    /// when StopVmOnExit is on — stop Mainguard OS with completion), then shuts the lifetime down. Wired to
    /// the shell's <c>App.VisualizedShutdownAsync</c> seam. The framework Exit hook remains a backstop; its
    /// release/stop are no-ops here (both idempotent-guarded), so nothing double-runs.</summary>
    public static async Task RunVisualizedShutdownThenExitAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var vm = new ShutdownWindowViewModel();
            var window = new ShutdownWindow { DataContext = vm };
            desktop.MainWindow = window;
            window.Show();

            var env = new ProductionShutdownEnvironment(ReleaseKeepAlive, StopVmScopedAsync, ProComposition.LogOobe);
            // 60 s, not 15: the sequence now stops every live agent first, and each stop harvests logins
            // and publishes a branch before the jail goes.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await new AppShutdownSequence(env).RunAsync(vm, cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ProComposition.LogOobe($"visualized shutdown failed (proceeding to exit): {ex.Message}");
        }
        finally
        {
            desktop.Shutdown();
        }
    }

    /// <summary>Best-effort, once-only scoped stop of the MainguardEnv VM (`wsl --terminate MainguardEnv` ONLY —
    /// never the VM-wide shutdown verb, G-12). The guard is what lets the visualized shutdown and the
    /// framework Exit backstop both call this without the terminate ever running twice.</summary>
    private static async Task StopVmScopedAsync(CancellationToken ct)
    {
        // macos-host: there is no MainguardEnv VM — the Docker engine manages its own lifetime.
        if (OperatingSystem.IsMacOS())
            return;

        if (System.Threading.Interlocked.Exchange(ref _vmStopRan, 1) != 0)
            return;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await new WslRunner().RunAsync(WslCommands.Terminate(), stdin: null, timeout.Token).ConfigureAwait(false);
            ProComposition.LogOobe("terminated MainguardEnv on exit (StopVmOnExit)");
        }
        catch (Exception ex)
        {
            ProComposition.LogOobe($"stop-VM-on-exit failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>The framework Exit backstop's synchronous VM stop: honors StopVmOnExit, then runs the
    /// guarded scoped terminate. After the visualized shutdown already ran, the guard makes this a no-op.</summary>
    private static void StopVmOnExitBestEffort()
    {
        if (ProComposition.Settings?.Current.StopVmOnExit != true)
            return;

        // Same veto the visualized shutdown applies (AppShutdownSequence): never terminate the distro
        // while a sandbox-image build is running in it. Repeated here because this backstop can fire on
        // a path that never reaches the sequence (OS logoff), and it is the terminate — not which code
        // called it — that kills the build.
        if (SandboxImageProvisioningTracker.Shared.IsProvisioning)
        {
            ProComposition.LogOobe("exit backstop: a sandbox image build is still running — leaving "
                + "MainguardEnv up (StopVmOnExit skipped)");
            return;
        }

        StopVmScopedAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>The startup resume-task sweep (best-effort; failures logged, never fatal to launch).</summary>
    private static void SweepResumeTaskAtStartup(bool launchedByResumeTask)
    {
        try
        {
            var stage = new OobeStateMachine(new JsonOobeStateStore(JsonOobeStateStore.DefaultPath())).CurrentStage;
            new ResumeTaskGuard(log: ProComposition.LogOobe).Sweep(stage, launchedByResumeTask, ResumeTargetExePath());
        }
        catch (Exception ex)
        {
            ProComposition.LogOobe($"startup resume-task sweep failed: {ex}");
        }
    }

    // ---- launch routing (was App.DecideLaunchRoute / ProvisioningProbeFactory / CreateProvisioningProbe) ----

    /// <summary>P2-48 launch-routing seam: the provisioning probe the launch consults to decide
    /// OOBE-vs-control-center. Defaults to the real WSL/daemon probe; overridable (tests/dev).</summary>
    public static Func<IProvisioningProbe> ProvisioningProbeFactory { get; set; } = CreateProvisioningProbe;

    /// <summary>The shipped provisioning probe (audit fix #8): MainguardEnv registered AND no OOBE run
    /// mid-flight — an installed-state question that never cold-boots the VM inside the startup budget.</summary>
    public static IProvisioningProbe CreateProvisioningProbe()
        => new InstalledStateProbe(
            new WslRunner(),
            static () => new OobeStateMachine(new JsonOobeStateStore(JsonOobeStateStore.DefaultPath())).CurrentStage);

    private static LaunchRoute DecideLaunchRoute()
    {
        // macos-host: its own first-run flow (engine detection, file-sharing canary, jail images,
        // daemon, CLI picker — MacOobeWindow). The completed marker is the whole state machine on
        // this substrate (no reboot-resume, no elevation); the skip flags below are honored first.
        if (OperatingSystem.IsMacOS())
        {
            var skip = string.Equals(Environment.GetEnvironmentVariable("MAINGUARD_SKIP_OOBE"), "1", StringComparison.Ordinal)
                || Environment.GetCommandLineArgs().Any(a => a is "--control-center" or "--no-oobe");
            return skip || MacOobeState.IsCompleted() ? LaunchRoute.ControlCenter : LaunchRoute.Oobe;
        }

        // Phase-4: MAINGUARD_SKIP_OOBE is the current name; MAINGUARD_SKIP_OOBE stays honored as a
        // read-fallback for one release (a CI script / shell profile may still set the old name).
        if (string.Equals(
                Environment.GetEnvironmentVariable("MAINGUARD_SKIP_OOBE")
                    ?? Environment.GetEnvironmentVariable("MAINGUARD_SKIP_OOBE"), "1", StringComparison.Ordinal)
            || Environment.GetCommandLineArgs().Any(a => a is "--control-center" or "--no-oobe"))
            return LaunchRoute.ControlCenter;

        try
        {
            return System.Threading.Tasks.Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                return await LaunchRouter.DecideAsync(ProvisioningProbeFactory(), cts.Token).ConfigureAwait(false);
            }).GetAwaiter().GetResult();
        }
        catch
        {
            return LaunchRoute.Oobe;
        }
    }

    // ---- control-center loading screen (was App.CreateStartupWindow / RunStartupSequenceAsync) ----

    private static StartupWindow CreateStartupWindow()
    {
        var vm = new StartupWindowViewModel();
        // The per-substrate startup environment: WSL legs on Windows, the host-local daemon +
        // Docker-engine legs on macOS (MacStartupEnvironment). Same AppStartupSequence over both.
        IAppStartupEnvironment env = OperatingSystem.IsMacOS()
            ? new MacStartupEnvironment(ProComposition.LogOobe)
            {
                Host = vm,
                VmUpgradeDeclinedThisSession = _vmUpgradeDeclinedThisSession,
            }
            : new ProductionStartupEnvironment(EnsureKeepAlive, ProComposition.LogOobe)
            {
                Host = vm,
                VmUpgradeDeclinedThisSession = _vmUpgradeDeclinedThisSession,
            };
        var sequence = new AppStartupSequence(env);
        vm.SequenceRunner = (progress, ct) => RunStartupSequenceAsync(sequence, env, progress, ct);
        return new StartupWindow { DataContext = vm };
    }

    private static async Task<StartupResult> RunStartupSequenceAsync(
        AppStartupSequence sequence,
        IAppStartupEnvironment env,
        IProgress<StartupProgress> progress,
        CancellationToken ct)
    {
        var result = await sequence.RunAsync(progress, ct).ConfigureAwait(false);
        // Carry the "Later" choice across the loading screen so a later re-entry doesn't re-nag.
        _vmUpgradeDeclinedThisSession = env.VmUpgradeDeclinedThisSession;
        KickAgentCliUpdateCheck();
        return result;
    }

    // ---- launch-time agent-CLI update check ----

    private static int _cliUpdateCheckRan;

    /// <summary>
    /// The launch-time sweep of the Mainguard-managed CLI updater: once per process, in the
    /// background, it asks the npm registry whether any INSTALLED agent CLI has a newer release and
    /// — if so — surfaces a shell toast pointing at Tools → Agent CLIs, where the user decides
    /// (update per CLI, or revert a previous update). Nothing is ever installed from here; the check
    /// is best-effort and silent on any failure, exactly like the tier-1 daemon refresh toast.
    /// </summary>
    private static void KickAgentCliUpdateCheck()
    {
        // macos-host: skipped for now — the installed-CLI listing runs a version probe per CLI,
        // which on this substrate spawns a container each; that is fine behind the Tools page's
        // explicit click and too heavy for a silent every-launch sweep. Revisit with a cheaper
        // marker-only listing.
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        if (Interlocked.Exchange(ref _cliUpdateCheckRan, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var wsl = new WslRunner();
                var installer = Mainguard.Agents.Agents.Adapters.AgentCliInstaller.CreateDefault(wsl);
                var updater = Mainguard.Agents.Agents.Adapters.AgentCliUpdateService.CreateDefault(wsl);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var installed = (await installer.ListAsync(cts.Token).ConfigureAwait(false))
                    .Where(o => o.IsInstalled)
                    .ToDictionary(o => o.Id, o => o.InstalledVersion!, StringComparer.Ordinal);
                if (installed.Count == 0)
                {
                    return;
                }

                var updates = (await updater.CheckForUpdatesAsync(cts.Token).ConfigureAwait(false))
                    .Where(u => installed.ContainsKey(u.Id))
                    .ToList();
                if (updates.Count == 0)
                {
                    return;
                }

                // Use the PROBED installed version, not AgentCliUpdate.InstalledVersion (the pin this
                // update supersedes) — the two drift apart whenever the disk copy is ahead of the pin
                // (W6), and announcing the pin here would tell a user already on 2.1.223 that they are
                // "still on 2.1.218".
                var summary = string.Join(", ",
                    updates.Select(u => $"{u.DisplayName} {installed[u.Id]} → {u.LatestVersion}"));
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ProComposition.ShowShellToast(
                    $"Agent CLI update{(updates.Count > 1 ? "s" : "")} available: {summary}. "
                    + "Update (or later revert) from Tools → Agent CLIs.", false));
            }
            catch (Exception ex)
            {
                ProComposition.LogOobe($"agent-cli update check skipped: {ex.Message}");
            }
        });
    }

    // ---- OOBE wizard (was App.CreateOobeWizardViewModel) ----

    /// <summary>Builds the in-app OOBE wizard VM over P2-21's tested machinery. The elevated helper +
    /// payload are resolved from the app's own directory, where the packaged build co-locates them. The
    /// shell-owned callbacks (persist a repo into the store, provision it into the daemon) come through the
    /// <see cref="ProComposition"/> seams the Pro head wired.</summary>
    public static OobeWizardViewModel CreateOobeWizardViewModel()
    {
        var wsl = new WslRunner();
        var store = new JsonOobeStateStore(JsonOobeStateStore.DefaultPath());
        var machine = new OobeStateMachine(store);
        var diagnostics = new SystemDiagnostics(new WindowsSystemProbe(), new WslStatusProbe(wsl));

        var appDir = AppContext.BaseDirectory;
        var resumeTarget = ResumeTargetExePath();
        var helperExe = Path.Combine(appDir, "Mainguard.Installer.Elevated.exe");
        var dataRoot = MainguardPaths.DataRoot();
        var resultPath = Path.Combine(dataRoot, "elevated-result.json");
        var launcher = new RunAsElevationLauncher(helperExe, resumeTarget, resultPath);

        var options = new BootstrapOptions(
            InstallDir: Path.Combine(dataRoot, "vm"),
            TarballPath: Path.Combine(appDir, "payload", "MainguardOS.tar.gz"));
        var ctx = new BootstrapContext(wsl, new BootstrapFileSystem(), new EndToEndDaemonHealthProbe(wsl), options);
        var bootstrapper = MainguardOsBootstrapper.Create(ctx);

        var guard = new ResumeTaskGuard(log: ProComposition.LogOobe);
        void Sweep() => guard.Sweep(machine.CurrentStage, launchedByResumeTask: false, resumeTarget);

        async Task<bool> VmIsRegistered(CancellationToken ct)
        {
            var list = await wsl.RunAsync(WslCommands.ListQuiet(), stdin: null, ct).ConfigureAwait(false);
            return WslRunner.ParseDistroList(list.StdOut)
                .Any(d => string.Equals(d, WslCommands.DistroName, StringComparison.OrdinalIgnoreCase));
        }

        return new OobeWizardViewModel(
            machine, diagnostics, launcher, bootstrapper,
            resumeTaskSweep: Sweep,
            instanceLockFactory: static () => OobeInstanceLock.TryAcquire(),
            cliInstaller: Mainguard.Agents.Agents.Adapters.AgentCliInstaller.CreateDefault(wsl),
            vmIsRegistered: VmIsRegistered,
            rebootHasCompleted: static (since, _) => Task.FromResult(SystemRebootEvidence.RebootedSince(since)),
            repoDiscovery: new RepoDiscoveryService(new GitService()),
            pickRepoRootFolder: static () => PickOobeFolderAsync("Select the folder that contains your repositories"),
            pickIndividualRepoFolders: static () => PickOobeFoldersAsync("Select the repositories to copy into Mainguard OS"),
            provisionRepo: static (path, ct) => ProComposition.ProvisionRepoIntoOs?.Invoke(path, ct) ?? Task.CompletedTask,
            persistRepo: static path => ProComposition.PersistRepo?.Invoke(path),
            settingsService: ProComposition.Settings);
    }

    /// <summary>
    /// Builds the post-setup "Add Repos to Mainguard OS" window VM (Tools → Add Repos to Mainguard OS…) —
    /// the SAME onboarding engine and per-repo pipeline the OOBE repo step drives. Wired to
    /// <c>ProComposition.AddReposToOsFactory</c>; the folder pickers parent to the given owner (its owner is
    /// modal-disabled while it shows). The persist + provision callbacks reuse the shell-wired seams.
    /// </summary>
    public static AddReposToOsViewModel CreateAddReposToOsViewModel(Window pickerOwner)
        => new(
            new RepoDiscoveryService(new GitService()),
            () => PickFolderAsync(pickerOwner, "Select the folder that contains your repositories"),
            () => PickFoldersAsync(pickerOwner, "Select the repositories to copy into Mainguard OS"),
            (path, ct) => ProComposition.ProvisionRepoIntoOs?.Invoke(path, ct) ?? Task.CompletedTask,
            ProComposition.PersistRepo,
            ProComposition.Settings);

    /// <summary>The exe the resume Scheduled Task must point at — the running app itself (the Pro head).</summary>
    private static string ResumeTargetExePath()
        => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mainguard.Pro.App.exe");

    // ---- folder pickers (was App.Pick*Async) ----

    private static async Task<string?> PickOobeFolderAsync(string title)
    {
        var picked = await PickOobeFoldersAsync(title, allowMultiple: false);
        return picked.Count > 0 ? picked[0] : null;
    }

    private static Task<IReadOnlyList<string>> PickOobeFoldersAsync(string title)
        => PickOobeFoldersAsync(title, allowMultiple: true);

    private static Task<IReadOnlyList<string>> PickOobeFoldersAsync(string title, bool allowMultiple)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        return PickFoldersCoreAsync(desktop.MainWindow, title, allowMultiple);
    }

    private static async Task<string?> PickFolderAsync(Window owner, string title)
    {
        var picked = await PickFoldersCoreAsync(owner, title, allowMultiple: false);
        return picked.Count > 0 ? picked[0] : null;
    }

    private static Task<IReadOnlyList<string>> PickFoldersAsync(Window owner, string title)
        => PickFoldersCoreAsync(owner, title, allowMultiple: true);

    private static async Task<IReadOnlyList<string>> PickFoldersCoreAsync(Window owner, string title, bool allowMultiple)
    {
        var result = await owner.StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = title, AllowMultiple = allowMultiple });
        return result.Select(f => f.Path.LocalPath).ToList();
    }

    // ---- Settings "About / versions" daemon probe (wired into ShellVersionProbe.Query by the Pro head) ----

    /// <summary>The loopback <c>GetDaemonInfo</c> probe the Settings versions footer runs, mapped to the
    /// shell's edition-agnostic <see cref="DaemonVersionSnapshot"/> (step 2f). Same contract the shell's
    /// VersionsViewModel expects: a snapshot names the versions; a <c>null</c> RESULT means the daemon
    /// answered but predates version reporting (Unimplemented); a THROW means unreachable (the shell maps
    /// it to honest "unreachable" text).</summary>
    public static async Task<DaemonVersionSnapshot?> QueryDaemonVersionsAsync(CancellationToken ct)
    {
        using var daemon = DaemonClient.ForLoopback();
        try
        {
            var info = await daemon.GetDaemonInfoAsync(ct, deadline: TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            return info is null ? null : new DaemonVersionSnapshot(info.DaemonVersion, info.PayloadVersion);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unimplemented)
        {
            return null; // a pre-GetDaemonInfo daemon — reachable but versionless
        }
    }

    // ---- Pro-only chrome the shell's App.axaml intentionally omits (Dock is a Pro dependency) ----

    /// <summary>Injects the Dock.Avalonia tab/split theme the per-agent workspaces need (step 2f) into the
    /// running app's styles — inserted BEFORE the design-system include so the "DesignSystem overrides win"
    /// ordering the shell's App.axaml documents is preserved. The reference-clean shell must not name Dock
    /// (an avares include forces an assembly reference), so the Pro head calls this once at startup.</summary>
    public static void InjectProChrome()
    {
        if (Application.Current is not { } app)
            return;

        var dock = new StyleInclude(new Uri("avares://Mainguard.Agents.UI/"))
        {
            Source = new Uri("avares://Dock.Avalonia/Themes/DockFluentTheme.axaml"),
        };

        var styles = app.Styles;
        var insertAt = styles.Count;
        for (var i = 0; i < styles.Count; i++)
        {
            if (styles[i] is StyleInclude si && si.Source?.ToString().Contains("DesignSystem", StringComparison.Ordinal) == true)
            {
                insertAt = i;
                break;
            }
        }

        styles.Insert(insertAt, dock);
    }
}
