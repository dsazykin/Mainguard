using System.Threading.Tasks;
using Avalonia.Controls;

namespace Mainguard.UI.Editions;

/// <summary>
/// The Settings-page surface the SHARED shell (<c>SettingsViewModel</c>) talks to instead of naming the
/// Pro agent-platform types directly (step 1c) — so Settings carries ZERO reference to
/// <c>Mainguard.Agents.Agents.*</c> or any Pro-only View. It exposes EXACTLY the agent-platform pages
/// (AI Providers, Agent CLIs, Toolchains, Mainguard OS, Daemon Logs) plus the one rebuild action, as
/// opaque <c>object</c> content (concretely Pro ViewModels, resolved to their View by
/// <c>ViewLocator</c> — the same <c>object?</c>-through-ViewLocator pattern as
/// <see cref="IAgentPlatformSurface.AgentRailContent"/>/<c>CreateResourceMonitor</c>), so no Pro-only
/// concrete type crosses the seam. <c>ProToolsSurface</c> satisfies it under the Pro edition; the Client
/// manifest returns <c>null</c> for <see cref="IEditionManifest.ProTools"/>, so the Settings sidebar
/// simply omits these pages (gated by <c>MainWindowViewModel.HasAgentPlatform</c>).
///
/// <para>These used to be five dialog-opening methods (<c>Task ManageXAsync(Window owner)</c>) — that
/// shape no longer fits once each is a Settings page instead of its own dialog. Only Mainguard OS still
/// needs a <see cref="Window"/>: its folder-picker dialogs need a real <see cref="TopLevel"/> owner (the
/// Settings window itself now, not a throwaway dialog).</para>
/// </summary>
public interface IProToolsSurface
{
    /// <summary>Settings → AI Providers: the API-key vault page content.</summary>
    object CreateAiProvidersPage();

    /// <summary>Settings → Agent CLIs: the agent-CLI install/manage page content.</summary>
    object CreateAgentClisPage();

    /// <summary>Settings → Toolchains: the curated language-toolchain install/remove page content —
    /// the human half of the user-managed toolchain channel (a repository may name a toolchain id;
    /// only a person can put that toolchain on this machine).</summary>
    object CreateToolchainsPage();

    /// <summary>Settings → Daemon Logs: the read-only recent-daemon-logs page content.</summary>
    object CreateDaemonLogsPage();

    /// <summary>Settings → PR Intake: the external-PR-intake configuration page content — the poll
    /// cadence, the shared bot-author list and the subscribed repositories.
    ///
    /// <para>All of it is DAEMON state, edited over gRPC: the daemon runs the poll loop and provisions a
    /// sandbox per intake'd pull request, so it owns the configuration and the App is its client. That is
    /// also why the page shipped unreachable — it had a complete dialog and nowhere real to write.</para></summary>
    object CreatePrIntakePage();

    /// <summary>Settings → Mainguard OS: the repo-onboarding + rebuild-sandbox-images page content,
    /// parented to <paramref name="owner"/> for its folder-picker dialogs. <c>null</c> if the factory
    /// seam isn't wired (mirrors the pre-migration null-tolerant behavior).</summary>
    object? CreateMainguardOsPage(Window owner);

    /// <summary>Settings → Mainguard OS's "Rebuild sandbox images" action: force-reprovision every jail
    /// image (fire-and-forget, with the shell toast + oobe.log trace). Self-resolves its shell/toast
    /// target, so it needs no owner.</summary>
    Task RebuildSandboxImagesAsync();
}
