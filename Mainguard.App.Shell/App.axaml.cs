using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Mainguard.App.Shell.Editions;
using Mainguard.App.Shell.Services;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.Git;
using Mainguard.Git.Services;
using Mainguard.UI;
using Mainguard.UI.Editions;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Microsoft.EntityFrameworkCore;
namespace Mainguard.App.Shell;

public partial class App : Application
{
    public static ISettingsService Settings { get; private set; } = null!;

    /// <summary>
    /// The edition this process runs as (ADR-0001) — the single composition seam deciding whether the
    /// launch wires the Pro agent platform. Each exe head sets it in its <c>Main</c> BEFORE the app starts
    /// (<c>Mainguard.Client.App</c> → <see cref="ClientManifest"/>; <c>Mainguard.Pro.App</c> → the Pro
    /// manifest). Defaults to Client so a shell built with no head wiring stays reference-clean and safe.
    /// Follows the static-<c>Settings</c> pattern, no DI container.
    /// </summary>
    public static IEditionManifest Edition { get; set; } = new ClientManifest();

    // ---- Pro-launch seams (step 2f): the reference-clean shell holds these as inert delegates; the Pro
    //      head (Mainguard.Pro.App) fills them with the Agents.UI implementations (ProDesktopHost). Under
    //      the Client head they stay null and the shell takes its MainguardOS-free path — so nothing in the
    //      shell ever names the agent platform, the whole point of the split. ----

    /// <summary>The Pro/Cloud desktop launch path (keep-alive, resume-task sweep, VM-stop-on-exit wiring,
    /// OOBE-vs-control-center routing). Set by the Pro head to <c>ProDesktopHost.Start</c>; <c>null</c> under
    /// the Client head, whose launch is deliberately MainguardOS-free.</summary>
    public static Action<IClassicDesktopStyleApplicationLifetime>? ProDesktopStarter { get; set; }

    /// <summary>The Pro visualized shutdown (release keep-alive, optional scoped VM stop behind the
    /// shutdown window, then shut the lifetime down). Set by the Pro head to
    /// <c>ProDesktopHost.RunVisualizedShutdownThenExitAsync</c>; <c>null</c> under the Client head, where a
    /// full exit is a plain <c>desktop.Shutdown()</c> (no VM to tear down).</summary>
    public static Func<IClassicDesktopStyleApplicationLifetime, Task>? VisualizedShutdownAsync { get; set; }

    /// <summary>Runs once at the end of <see cref="Initialize"/> — after <see cref="Settings"/> exists.
    /// The Pro head sets it to inject its composition-root capabilities DOWN into the Pro-UI assembly's
    /// seam (settings, oobe.log sink, shell-toast sink, shell-window factory, sandbox rebuild, host rail
    /// descriptors, daemon version probe). <c>null</c> under the Client head. The shell never names
    /// <c>ProComposition</c> itself — this hook is the one-way inversion the design system already uses for
    /// <c>ThemeManager.PersistKey</c> (the lower/side layer is populated by the head, never reached into).</summary>
    public static Action? AfterInitialize { get; set; }

    /// <summary>Best-effort line into <c>%LocalAppData%\Mainguard\oobe.log</c> — provisioning-lifecycle
    /// breadcrumbs (launch routing, exit-guard) so a misbehaving setup leaves a trace. The Pro side shares
    /// this one sink (wired into <c>ProComposition.LogOobe</c> by the head).</summary>
    public static void LogOobe(string message)
    {
        try
        {
            var dir = MainguardPaths.DataRoot();
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "oobe.log"),
                $"{DateTimeOffset.UtcNow:O} [pid {Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break the flow they diagnose.
        }
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Native macOS typography: put the system face ahead of the bundled Inter. FontUi is
        // consumed via DynamicResource and direct app-resource entries shadow merged-dictionary
        // values, so this one write restyles every window. ".AppleSystemUIFont" is deliberate:
        // SF Pro ships only as that hidden CoreText family — "SF Pro Text" is NOT installed, and
        // Skia's matcher silently substitutes Helvetica for unknown names, so the friendly name
        // would deliver the wrong font, not a graceful skip. The chain ends in Inter so a macOS
        // build where the dot-name ever stops resolving degrades to the cross-platform look.
        if (System.OperatingSystem.IsMacOS())
            Resources["FontUi"] = new Avalonia.Media.FontFamily(
                ".AppleSystemUIFont, Helvetica Neue, Inter, sans-serif");

        // Load environment variables securely from .env file
        DotNetEnv.Env.TraversePath().Load();

        // Instantiate and load the settings service
        Settings = new SettingsService();

        // Edition-specific composition wiring (step 2f). The Pro head sets AfterInitialize to inject its
        // capabilities DOWN into the Pro-UI assembly's seam (ProComposition) now that Settings exists; the
        // Client head leaves it null. Runs before any window opens and before the Pro manifest's rail is
        // read, exactly where the old shell-side WireProComposition ran.
        AfterInitialize?.Invoke();

        // Seed the ViewLocator's cross-assembly search set from the selected edition (1e, ADR-0001), once
        // and before any View resolves. The Client head lists only the shell; the Pro head additionally
        // lists Mainguard.Agents.UI so its Views resolve. The shell's own assembly is always included
        // (deduped) so in-shell Views resolve even if a manifest ever omitted it.
        ViewLocator.ViewAssemblies = ComposeViewAssemblies(Edition);

        // Ensure SQLite database is created and migrations are applied. This runs before any window
        // exists, so a bare Migrate() that blocks would leave a windowless, dead-looking process (see the
        // single-instance guard in ShellEntryPoint). The block is not hypothetical, and it is not the
        // OS-level lock this comment used to assume: EF Core claims a row in __EFMigrationsLock before it
        // even checks for pending migrations and then polls for it forever, so a head killed mid-migration
        // wedges EVERY later launch with nothing running. The daemon already fixed this on its own database
        // (GatewayServiceRegistration.TryPrepareDatabase); the shell does the same now — clear the orphaned
        // row (safe: we hold the single-instance mutex, so this process is the only writer), then migrate
        // under a watchdog that, if it still fires, LOOKS at the database instead of guessing at a cause.
        var migrationTimeout = TimeSpan.FromSeconds(20);
        var databasePath = "(unresolved)";
        try
        {
            // Inside the try: an unresolvable data root throws with its own named remedy, and that
            // message deserves the same console line as any other migration failure.
            databasePath = Path.Combine(MainguardPaths.DataRoot(), AppDbContext.DatabaseFileName);
            MigrationLock.TryClearOrphanedLock(databasePath);

            var migration = System.Threading.Tasks.Task.Run(() =>
            {
                using var dbContext = new AppDbContext();
                dbContext.Database.Migrate();
            });

            if (!migration.Wait(migrationTimeout))
            {
                throw new TimeoutException(MigrationLock.DescribeStall(databasePath, migrationTimeout));
            }

            // Re-throw any exception the migration task captured on this thread.
            migration.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Better a crash with a reason than a silent, windowless hang. LogToTrace / the console
            // will carry this, and the process exits with a non-zero code instead of lingering.
            Console.Error.WriteLine($"[Mainguard] Fatal: database migration failed. {ex.Message}");
            throw;
        }
    }

    /// <summary>The ViewLocator search set for an edition (1e, ADR-0001): the shell's own assembly first —
    /// always present, so in-shell Views resolve regardless of what a manifest lists — then the edition's
    /// contributed View assemblies, de-duplicated (order-preserving). Trim-honest: only assemblies a manifest
    /// actually names are searched (no <see cref="AppDomain.GetAssemblies"/> scan), so the Client head never
    /// reaches a Pro-only assembly.</summary>
    internal static IReadOnlyList<Assembly> ComposeViewAssemblies(IEditionManifest edition)
    {
        var ordered = new List<Assembly> { typeof(App).Assembly };
        foreach (var asm in edition.ViewAssemblies)
        {
            if (!ordered.Contains(asm))
                ordered.Add(asm);
        }

        return ordered;
    }

    // ---- App lifecycle: tray + full exit (VM-stop-on-exit lives on the Pro side) ----

    /// <summary>True once a FULL exit is underway (tray menu / File > Exit / CloseToTray off) — the
    /// signal MainWindow's close interception uses to let the window actually close.</summary>
    public static bool IsExiting { get; private set; }

    private static int _fullExitStarted;
    private TrayIcon? _trayIcon;

    /// <summary>The one full-exit path: marks the exit (so close-to-tray interception stands down), then
    /// runs the Pro visualized shutdown to completion — or, under the Client head (no VM), a plain lifetime
    /// shutdown. Reentrancy-guarded: a second exit request is ignored so teardown never double-runs.</summary>
    public static void RequestFullExit()
    {
        if (System.Threading.Interlocked.Exchange(ref _fullExitStarted, 1) != 0)
        {
            return;
        }

        IsExiting = true;
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (VisualizedShutdownAsync is { } shutdown)
                _ = shutdown(desktop);
            else
                desktop.Shutdown();
        }
    }

    /// <summary>Live-agent count for the exit guard (wired by MainWindowViewModel; null = unknown = 0).</summary>
    public static Func<int>? LiveAgentCountProvider { get; set; }

    /// <summary>The exit guard's confirmation seam (overridable in tests; defaults to the shared dialog).</summary>
    public static Services.IConfirmationService ExitConfirmation { get; set; } = new Services.DialogConfirmationService();

    /// <summary>
    /// The guarded full exit every user-facing exit path calls (tray Exit, File → Exit, the X with
    /// close-to-tray off): when the exit would stop the VM under live agents
    /// (<see cref="Services.VmExitGuard"/>), it confirms first — the main window is shown so the
    /// dialog has an owner (the tray path may fire with it hidden). Declining leaves everything
    /// running; confirming (or no live agents / VM kept) proceeds to <see cref="RequestFullExit"/>.
    /// </summary>
    public static async Task RequestFullExitGuardedAsync()
    {
        try
        {
            var liveAgents = LiveAgentCountProvider?.Invoke() ?? 0;
            if (Services.VmExitGuard.ShouldConfirm(Settings.Current.StopVmOnExit, liveAgents))
            {
                if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    ShowMainWindow(desktop);
                }

                var confirmed = await ExitConfirmation.ConfirmAsync(
                    Services.VmExitGuard.Title,
                    Services.VmExitGuard.Message(liveAgents),
                    Services.VmExitGuard.ConfirmButtonText);
                if (!confirmed)
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // The guard must never be able to trap the user in the app.
            LogOobe($"exit guard failed (proceeding to exit): {ex.Message}");
        }

        RequestFullExit();
    }

    /// <summary>The always-present tray icon: left-click / "Open Mainguard" re-shows the main window
    /// (the X hides to here when CloseToTray is on); "Exit Mainguard" is the full exit. Shared shell
    /// chrome — set up on BOTH launch paths, before the edition branch.</summary>
    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // A live status row, disabled (informational): refreshed through NativeMenu's own
        // NeedsUpdate — the one hook that fires right before the menu shows, so the count is
        // read exactly when a human is about to look at it, never on a poll.
        var status = new Avalonia.Controls.NativeMenuItem("No agents running") { IsEnabled = false };

        var open = new Avalonia.Controls.NativeMenuItem("Open Mainguard");
        open.Click += (_, _) => ShowMainWindow(desktop);
        var exit = new Avalonia.Controls.NativeMenuItem("Exit Mainguard");
        exit.Click += (_, _) => _ = RequestFullExitGuardedAsync();

        var menu = new Avalonia.Controls.NativeMenu();
        menu.Items.Add(status);
        menu.Items.Add(new Avalonia.Controls.NativeMenuItemSeparator());
        menu.Items.Add(open);
        menu.Items.Add(new Avalonia.Controls.NativeMenuItemSeparator());
        menu.Items.Add(exit);
        menu.NeedsUpdate += (_, _) =>
        {
            var count = LiveAgentCountProvider?.Invoke() ?? 0;
            status.Header = count switch
            {
                0 => "No agents running",
                1 => "1 agent running",
                _ => $"{count} agents running",
            };
        };

        // The brand PNG on macOS/Linux (a status item wants a small raster; the .ico stays for
        // the Windows notification area, where ICO is the native currency).
        var iconUri = OperatingSystem.IsWindows()
            ? new Uri("avares://Mainguard.App.Shell/Assets/avalonia-logo.ico")
            : new Uri("avares://Mainguard.App.Shell/Assets/tray-icon.png");
        _trayIcon = new TrayIcon
        {
            Icon = new Avalonia.Controls.WindowIcon(Avalonia.Platform.AssetLoader.Open(iconUri)),
            ToolTipText = "Mainguard",
            Menu = menu,
        };
        _trayIcon.Clicked += (_, _) => ShowMainWindow(desktop);
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private static void HandleDeepLink(string uri, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var result = Mainguard.Git.Security.DeepLinkParser.Parse(uri);
        System.Diagnostics.Debug.WriteLine($"[Mainguard] deep link {result.Outcome}: {uri} {result.Reason}");
        if (result.Outcome != Mainguard.Git.Security.DeepLinkOutcome.Command) return;

        ShowMainWindow(desktop);
        if (result.Command is Mainguard.Git.Security.DeepLinkCommand.OpenAgent(var agentId)
            && desktop.MainWindow?.DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.ShowAgentCommand.Execute(agentId);
        }
        // OpenRepo/OpenPr: activation only for now — their id semantics live in the agent layer
        // (RepoPathHasher) and PR surfaces; routing them is a follow-up, the secret guard is not.
    }

    private static void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (desktop.MainWindow is { } window)
        {
            window.Show();
            window.WindowState = Avalonia.Controls.WindowState.Normal;
            window.Activate();
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Wire the design system's persistence seam (step 2c): Mainguard.UI's ThemeManager is the base
        // UI layer and must not reach up into App.Settings, so the shell injects the write-back here.
        Mainguard.UI.Theming.ThemeManager.PersistKey = key => Settings.Update(p => p.Theme = key);

        // Apply the persisted theme (or the default) before any window opens.
        Mainguard.UI.Theming.ThemeManager.Initialize(Settings.Current.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The tray icon is shared shell chrome — set up on BOTH paths, before the edition branch.
            SetupTrayIcon(desktop);

            // macOS: the top-of-screen menu bar (no-op elsewhere). Shell-level, edition-agnostic —
            // it dispatches through the same action registry the shortcuts and palette use.
            Services.MacMenuBar.Install(this);

            // macOS deep links: the .app bundle declares mainguard:// (Pro head), LaunchServices
            // routes every link to the ONE running instance, and Avalonia surfaces it here — no
            // named pipe, no registry, unlike the Windows path. Parsing (and the never-a-secret
            // guard) is the same pure DeepLinkParser; unroutable verbs still activate the window,
            // so a clicked link is never a silent nothing.
            if (OperatingSystem.IsMacOS()
                && TryGetFeature(typeof(Avalonia.Controls.ApplicationLifetimes.IActivatableLifetime))
                    is Avalonia.Controls.ApplicationLifetimes.IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e is Avalonia.Controls.ApplicationLifetimes.ProtocolActivatedEventArgs protocol)
                        HandleDeepLink(protocol.Uri.ToString(), desktop);
                };
            }

            // Edition seam (1d, ADR-0001): the Pro/Cloud edition composes the full MainguardOS launch
            // machinery (keep-alive, resume-task sweep, VM-stop-on-exit, the provisioning launch router)
            // through the ProDesktopStarter seam its head wired; the plain Git client takes a deliberately
            // MainguardOS-free path — a client machine must never hold the distro awake or `wsl --terminate
            // MainguardEnv`. The DB migrate already ran in Initialize() and the single-instance guard lives in
            // ShellEntryPoint (both shared, both edition-agnostic).
            if (DecideLaunchComposition(Edition) == LaunchComposition.ProMainguardOs && ProDesktopStarter is { } startPro)
                startPro(desktop);
            else
                StartClientDesktop(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Which top-level launch composition the process runs. <see cref="LaunchComposition.ProMainguardOs"/>
    /// wires the MainguardOS machinery (through the Pro head's <see cref="ProDesktopStarter"/>);
    /// <see cref="LaunchComposition.ClientPlain"/> is the plain Git client's MainguardOS-free path. Kept as a
    /// named seam so 1d's "Client launch is MainguardOS-free" guard is unit-testable without a headless app.</summary>
    internal enum LaunchComposition
    {
        /// <summary>The Pro/Cloud path — keep-alive, resume-task sweep, VM-stop-on-exit, provisioning route.</summary>
        ProMainguardOs,

        /// <summary>The plain Git client — none of the above; the dedicated Clone first-run + shell only.</summary>
        ClientPlain,
    }

    /// <summary>The one edition→launch-composition decision (1d). An edition with the agent platform (Pro)
    /// runs the MainguardOS launch path; the plain client runs MainguardOS-free. Pure over the manifest, and the
    /// ONLY branch that ever reaches keep-alive/resume/VM machinery is the Pro head's ProDesktopStarter — so
    /// asserting this returns <see cref="LaunchComposition.ClientPlain"/> under the Client manifest is the
    /// structural proof that the Client launch touches none of it.</summary>
    internal static LaunchComposition DecideLaunchComposition(IEditionManifest edition)
        => edition.HasAgentPlatform ? LaunchComposition.ProMainguardOs : LaunchComposition.ClientPlain;

    /// <summary>
    /// The Client (plain Git GUI) launch path (1d) — deliberately MainguardOS-free: NO keep-alive, NO
    /// resume-task sweep, and NO framework-Exit VM-stop wiring (a client machine must never hold the distro
    /// awake or `wsl --terminate MainguardEnv`). It keeps only the SHARED shell chrome (the tray icon was set
    /// up before this branch; the DB migrate already ran in Initialize(); single-instance lives in
    /// ShellEntryPoint). First run — an empty repo catalog — opens the dedicated Clone first-run; a
    /// returning client goes straight to the shell.
    /// </summary>
    private static void StartClientDesktop(IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.MainWindow = DecideClientLaunchTarget() == ClientLaunchTarget.FirstRun
            ? CreateClientFirstRunWindow()
            : new MainWindow { DataContext = new MainWindowViewModel(degradedBanner: null) };
    }

    /// <summary>Where the Client edition opens on launch (1d).</summary>
    internal enum ClientLaunchTarget
    {
        /// <summary>Fresh machine (no repositories registered) — the dedicated Clone first-run.</summary>
        FirstRun,

        /// <summary>Returning client (repositories exist) — straight to the shell.</summary>
        Shell,
    }

    /// <summary>The Client first-run gate (1d): first run == the repo catalog is empty (no repositories in
    /// the ONE store the reopen-last-repo card + sidebar consult — <see cref="Services.RepoCatalog"/> over
    /// <c>AppDbContext.Repositories</c>). A returning client with any registered repo skips straight to the
    /// shell (whose own empty-state / "Select a repository to begin" covers the skip case).</summary>
    internal static ClientLaunchTarget DecideClientLaunchTarget()
        => ClientLaunchTargetFor(Services.RepoCatalog.IsEmpty());

    /// <summary>The pure first-run gate mapping (1d): an empty catalog opens the dedicated Clone first-run,
    /// a non-empty one goes straight to the shell. Split from the store read so the routing rule is
    /// unit-tested without a database.</summary>
    internal static ClientLaunchTarget ClientLaunchTargetFor(bool catalogEmpty)
        => catalogEmpty ? ClientLaunchTarget.FirstRun : ClientLaunchTarget.Shell;

    /// <summary>
    /// The Client edition's dedicated "Clone" first-run (1d): a light welcome framing around the REUSED
    /// Clone Dashboard (<see cref="CloneDashboardViewModel"/> / <see cref="Views.CloneDashboardView"/>) —
    /// clone a remote repo OR open a local folder, with the existing multi-host sign-in surfaces
    /// (<see cref="Views.DeviceFlowAuthDialog"/>, <see cref="Views.AccountsWindow"/>, <see cref="Views.SshKeysWindow"/>).
    /// Constructs NONE of the MainguardOS/daemon/bootstrap types. The interactions that need a window owner
    /// (destination picker, device-flow dialog, local-folder open, Accounts/SSH) are wired here where the
    /// window exists — the SAME shape as <see cref="ViewModels.MainWindowViewModel.OpenCloudSync"/>. On a
    /// repo cloned/opened OR an explicit skip, <see cref="Views.ClientFirstRunWindow"/> swaps the app to the
    /// shell.
    /// </summary>
    private static Views.ClientFirstRunWindow CreateClientFirstRunWindow()
    {
        var clone = new CloneDashboardViewModel();
        var vm = new ViewModels.ClientFirstRunViewModel(clone);
        var window = new Views.ClientFirstRunWindow { DataContext = vm };

        // Device-flow sign-in code presented through the SAME dialog the shell's clone flow uses.
        clone.ShowDeviceFlowDialogAction = deviceFlow =>
        {
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var dialog = new Views.DeviceFlowAuthDialog(deviceFlow.VerificationUri, deviceFlow.UserCode);
                clone.CloseDeviceFlowDialogAction = () => Dispatcher.UIThread.Post(dialog.Close);
                dialog.Closed += (_, _) =>
                {
                    if (clone.CancelLoginCommand.CanExecute(null))
                        clone.CancelLoginCommand.Execute(null);
                };
                await dialog.ShowDialog(window);
            });
        };

        // Clone requested → pick a destination → clone with progress → register + proceed to the shell.
        clone.OnCloneRequested = async repo =>
        {
            var folder = await window.StorageProvider.OpenFolderPickerAsync(
                new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Select clone destination", AllowMultiple = false });
            if (folder.Count > 0)
            {
                var target = Path.Combine(folder[0].Path.LocalPath, repo.Name);
                if (await clone.RunCloneAsync(repo.CloneUrl, target))
                    vm.CompleteWithRepo(target);
            }
        };

        // Open a local folder → validate it is a Git repo (the sidebar's IsGitRepository gate) → register + proceed.
        vm.OpenLocalFolderRequested = async () =>
        {
            var folder = await window.StorageProvider.OpenFolderPickerAsync(
                new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Open a local repository", AllowMultiple = false });
            if (folder.Count == 0)
                return;

            var path = folder[0].Path.LocalPath;
            if (new GitService().IsGitRepository(path))
                vm.CompleteWithRepo(path);
            else
                vm.LocalFolderError = "That folder isn't a Git repository. Pick a folder that contains a .git directory.";
        };

        // Multi-host sign-in — the SAME Accounts surface the shell opens (GitHub device-flow + PAT/OAuth hosts).
        vm.ManageAccountsRequested = () =>
        {
            var authContext = new Mainguard.Git.Sync.HostAuthContext
            {
                PresentDeviceCode = device =>
                {
                    var dlg = new Views.DeviceFlowAuthDialog(device.VerificationUri, device.UserCode);
                    _ = dlg.ShowDialog(window);
                    return Task.CompletedTask;
                },
                BrowserOpener = new Services.BrowserOpener(),
                LoopbackChannelFactory = () => new Mainguard.Git.Security.HttpListenerCallbackChannel(),
            };
            var accounts = new Views.AccountsWindow { DataContext = new ViewModels.AccountsViewModel(authContext: authContext) };
            // A host signed in via Accounts should appear in the clone provider selector on return.
            accounts.Closed += (_, _) => clone.RefreshProviders();
            _ = accounts.ShowDialog(window);
        };

        // SSH keys — the SAME dialog the shell uses (SSH clone credentials for the reused clone surface).
        vm.ManageSshKeysRequested = () =>
        {
            var ssh = new Views.SshKeysWindow { DataContext = new ViewModels.SshKeysViewModel() };
            _ = ssh.ShowDialog(window);
        };

        return window;
    }
}
