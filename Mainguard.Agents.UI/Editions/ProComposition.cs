using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Git.Services;
using Mainguard.UI.Editions;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.Editions;

/// <summary>
/// The Pro-edition composition seams the moved Pro UI reads, POPULATED BY the Mainguard.App.Shell shell at
/// startup (<c>App.WireProComposition</c>). Step 2e physically split the Pro UI into Mainguard.Agents.UI,
/// which must never reference Mainguard.App.Shell; the handful of App composition-root capabilities the Pro
/// manifest / Pro Tools / control center used to reach through <c>App.*</c> statics are injected DOWN into
/// this static holder instead — the exact inversion the design system already uses for
/// <c>ThemeManager.PersistKey</c> (the lower layer never reaches UP into the app). Unset defaults are
/// inert (no-op sinks, the production orchestrator factory), so a test or harness that constructs a Pro
/// ViewModel without running <c>App.WireProComposition</c> behaves exactly as it did against the old
/// null-guarded <c>App.*</c> statics.
/// </summary>
public static class ProComposition
{
    // ---- orchestration services (was App.OrchestratorServicesFactory / App.CreateOrchestratorServices) ----

    /// <summary>The single composition seam for the control center's orchestration services. Production
    /// resolves the real <see cref="DaemonBackedOrchestrator"/> bundle; the headless design/render harnesses
    /// override this with a scripted mock BEFORE building the shell. The shell keeps a forwarding
    /// <c>App.OrchestratorServicesFactory</c> property over this, so the existing harness seam is unchanged.</summary>
    public static Func<OrchestratorServices> OrchestratorServicesFactory { get; set; } = CreateProduction;

    /// <summary>The shipped control-center services: real DaemonClient-backed, no mock (P2-47).</summary>
    public static OrchestratorServices CreateProduction() => DaemonBackedOrchestrator.CreateBundle();

    /// <summary>The bundle the control center runs on — the factory's current value.</summary>
    public static OrchestratorServices CreateOrchestratorServices() => OrchestratorServicesFactory();

    // ---- external-PR intake configuration (P2-12) ----

    /// <summary>
    /// The seam Settings → PR Intake reads its configuration through. Production resolves the LIVE,
    /// daemon-owned gateway; the headless render harnesses override it with
    /// <see cref="InMemoryPrIntakeGateway"/> before building the page.
    ///
    /// <para>A seam rather than a construction inside the page, for the same reason as the orchestrator
    /// bundle above: the page's whole contract is that it edits daemon state, so the ONE place that
    /// decides what "the daemon" means for a given run belongs here, where a harness can replace it,
    /// and not inside a ViewModel where a fallback would silently become production behaviour.</para>
    /// </summary>
    public static Func<IPrIntakeGateway> PrIntakeGatewayFactory { get; set; } =
        () => new DaemonPrIntakeGateway(SharedIntakeClient.Value);

    /// <summary>The gateway the intake settings page runs on — the factory's current value.</summary>
    public static IPrIntakeGateway CreatePrIntakeGateway() => PrIntakeGatewayFactory();

    /// <summary>The Agent Jails page's seam onto the daemon's per-jail ceiling (2026-09-04) — same shape and
    /// same reason as the intake factory above: the page edits daemon state, and what "the daemon" means
    /// for a run is decided here where a harness can replace it.</summary>
    public static Func<IJailLimitsGateway> JailLimitsGatewayFactory { get; set; } =
        () => new DaemonJailLimitsGateway(SharedIntakeClient.Value);

    public static IJailLimitsGateway CreateJailLimitsGateway() => JailLimitsGatewayFactory();

    /// <summary>
    /// One process-lifetime loopback client for the intake page's unary calls. Deliberately shared and
    /// never disposed: Settings pages are cached per Settings window and nothing disposes them, so a
    /// client per page would leak an mTLS channel every time the window is opened. The channel is
    /// created lazily, on the first activation of this page, so a run that never opens it pays nothing.
    /// </summary>
    private static readonly Lazy<DaemonClient> SharedIntakeClient =
        new(() => DaemonClient.ForLoopback(), isThreadSafe: true);

    // ---- shell capabilities the shell wires at startup (all inert until then) ----

    /// <summary>The app settings service (was <c>App.Settings</c>) — the control center reads/writes its
    /// workspace-layout preset through it. Null until wired (falls back to defaults, as before).</summary>
    public static ISettingsService? Settings { get; set; }

    /// <summary>
    /// The window's live agent surface, set by <see cref="ProManifest.CreateControlCenter"/> so the exit
    /// teardown (<c>ProductionShutdownEnvironment</c>) can stop every live agent through it without the
    /// shell naming Pro types. Null before a window exists and in the tests that never make one.
    /// </summary>
    public static IAgentPlatformSurface? LiveAgentSurface { get; set; }

    /// <summary>The <c>oobe.log</c> breadcrumb sink (was <c>App.LogOobe</c>) — shared with the shell so a
    /// Pro Tools action leaves a trace in the one log. No-op until wired.</summary>
    public static Action<string> LogOobe { get; set; } = static _ => { };

    /// <summary>Show a toast on the shell's main window (was <c>MainWindowViewModel.ShowToast</c> resolved
    /// off the desktop lifetime). No-op until wired / when no shell is present.</summary>
    public static Action<string, bool> ShowShellToast { get; set; } = static (_, _) => { };

    /// <summary>Force-reprovision every sandbox jail image — (log, progress, force). A pure Pro-UI
    /// capability (no shell dependency), so it defaults to <see cref="SandboxImageInstaller.RunAsync"/>
    /// here rather than needing the head to wire it (step 2f).</summary>
    public static Func<Action<string>, IProgress<string>?, bool, Task>? RebuildSandboxImages { get; set; } =
        Mainguard.Agents.UI.Services.SandboxImageInstaller.RunAsync;

    /// <summary>Build the post-setup "Add Repos to Mainguard OS" window VM (was
    /// <c>App.CreateAddReposToOsViewModel</c>), parenting its folder pickers to the given owner. Null until
    /// wired.</summary>
    public static Func<Window, AddReposToOsViewModel>? AddReposToOsFactory { get; set; }

    /// <summary>Register a just-onboarded repo in the shell's ONE repo store (was
    /// <c>RepoCatalog.EnsureRegistered</c>) so a repo copied into Mainguard OS during OOBE / Add-Repos
    /// appears in the sidebar on first launch. Wired by the Pro head (which owns the shell's RepoCatalog —
    /// this Pro-UI assembly must not reference the shell). No-op until wired (step 2f).</summary>
    public static Action<string>? PersistRepo { get; set; }

    /// <summary>Provision a host repo into the daemon (P2-06) and register the returned sync remote — the
    /// OOBE / Add-Repos per-repo pipeline (was <c>App.ProvisionRepoIntoOsAsync</c>). Wired by the Pro head,
    /// which bridges this assembly's <c>DaemonClient</c> to the shell's <c>SyncRemoteRegistrar</c>. A
    /// completed no-op task until wired (step 2f).</summary>
    public static Func<string, System.Threading.CancellationToken, Task>? ProvisionRepoIntoOs { get; set; }

    /// <summary>The shared host-collab rail destinations (Pull requests / Issues / Notifications /
    /// Releases) whose <c>ContentViewModelType</c>s name the shell's own host-collab ViewModels (which
    /// stay in Mainguard.App.Shell and this assembly must NOT reference). The shell owns and injects them (see
    /// <c>EditionManifests</c>' static ctor), so <see cref="ProManifest"/> can compose them into its rail
    /// without naming those App types. Empty until wired.</summary>
    public static IReadOnlyList<RailSectionDescriptor> HostRailSections { get; set; } =
        Array.Empty<RailSectionDescriptor>();

    /// <summary>Build the shell's main window carrying the (optional) startup result — was
    /// <c>new MainWindow { DataContext = new MainWindowViewModel(result) }</c>. The Pro OOBE / startup
    /// loaders (which live here) swap the desktop's <c>MainWindow</c> to it on completion; the shell (which
    /// owns those types) wires this. Null until wired.</summary>
    public static Func<StartupResult?, Window>? CreateShellWindow { get; set; }
}
