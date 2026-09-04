using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.UI.Editions;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.Editions;

/// <summary>
/// The Pro edition (the shipped default) — the full agent platform. Its <see cref="CreateControlCenter"/>
/// routes through <c>ProComposition.CreateOrchestratorServices</c> (NOT <c>DaemonBackedOrchestrator.CreateBundle</c>
/// directly) so the headless render harnesses, which override <see cref="ProComposition.OrchestratorServicesFactory"/>
/// with a scripted <c>MockOrchestrator</c> before building the shell (through the shell's forwarding
/// <c>App.OrchestratorServicesFactory</c> property), still inject their mock — the green-keeping contract
/// that keeps behavior under this edition identical to today. Step 2e moved this manifest into the Pro-only
/// <c>Mainguard.Agents.UI</c> assembly; the contract types stay in the shared base (Mainguard.UI), and the
/// few App composition-root capabilities it needs are injected down through <see cref="ProComposition"/>.
/// </summary>
public sealed class ProManifest : IEditionManifest
{
    public string ProductName => "Mainguard Pro";

    public bool HasAgentPlatform => true;

    public EditionFirstRun FirstRun => EditionFirstRun.MainguardOsProvisioning;

    public bool ShowsAgentRail => true;

    public IAgentPlatformSurface? CreateControlCenter()
    {
        var surface = new ControlCenterViewModel(ProComposition.CreateOrchestratorServices());
        ProComposition.LiveAgentSurface = surface; // the exit teardown stops live agents through this
        return surface;
    }

    // The Pro Tools surface (step 1c) — a single stateless instance holding the five moved command
    // bodies (and, with them, the Mainguard.Agents.Agents + Pro-View references the shared hub no longer carries).
    public IProToolsSurface? ProTools { get; } = new ProToolsSurface();

    // The Pro rail: three Pro-only destinations up front, then the shared host-collab tabs. The host tabs'
    // descriptors name the shell's own host ViewModels (PullRequests/Issues/Notifications/Releases), which
    // STAY in Mainguard.App.Shell — a type this Pro-only assembly must not reference — so the shell injects them via
    // ProComposition.HostRailSections (wired in EditionManifests' static ctor, before any Sections read).
    // The labels/icons mirror MainWindow.axaml; the hard-coded rail rendered these until 1b's data-driven rail.
    //
    // ContentViewModelType (1f): the four host tabs carry their ViewModel type so 1f's manifest-completeness
    // test proves every one resolves to a real View (through the shell's ViewLocator over the composed
    // shell+Pro assembly set). Repo/Coordinator/Resources stay null ON PURPOSE: they are special direct-panel
    // content, NOT ViewLocator-routed (the repo workspace, the coordinator surface, and the lazily-built
    // resource monitor are bound directly, not via the …ViewModel→…View convention). Phase 2 populates the
    // remaining three when section content routing converges on the ContentControl+ViewLocator path.
    public IReadOnlyList<RailSectionDescriptor> Sections =>
        new List<RailSectionDescriptor>
        {
            new("Repo",        "Repo viewer", "CommitIcon",          false, RailAdornmentKind.None,      null),
            new("Coordinator", "Coordinator", "DiscussionIcon",      false, RailAdornmentKind.Attention, null),
            new("Resources",   "Resources",   "ResourceMonitorIcon", false, RailAdornmentKind.Spend,     null),
        }
        .Concat(ProComposition.HostRailSections)
        .ToList();

    public IReadOnlyList<SettingsPageDescriptor> SettingsPages { get; } = Array.Empty<SettingsPageDescriptor>();

    // 1e/2e: the shell's ViewLocator resolves this edition's Pro Views from THIS assembly
    // (Mainguard.Agents.UI). App.ComposeViewAssemblies always prepends the shell's own assembly (where the
    // git/host Views live), so the effective search set is shell + Pro — resolving both without this
    // assembly ever naming Mainguard.App.Shell.
    public IReadOnlyList<Assembly> ViewAssemblies { get; } = new[] { typeof(ProManifest).Assembly };
}
