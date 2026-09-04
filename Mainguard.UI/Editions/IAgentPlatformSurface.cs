using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Mainguard.UI.ViewModels;

namespace Mainguard.UI.Editions;

/// <summary>
/// The daemon's answer to provisioning a repo, projected to primitives so the reference-clean shell
/// (which must not name the Pro <c>DaemonClient</c>/<c>ProvisionedRepo</c> types) can register the
/// sync remote with its own <c>IGitService</c> after an edition with the agent platform provisioned it
/// (step 2f). Mirrors the daemon's <c>ProvisionRepo</c> response fields verbatim.
/// </summary>
public sealed record RepoSyncBinding(string RepoHandle, string SyncRemoteName, string SyncRemoteUrl);

/// <summary>
/// The outcome of provisioning a repo into the daemon: a successful <see cref="Binding"/>, or the
/// human-readable <see cref="FailureReason"/> the shell surfaces (toast + retry card) so a slow or
/// failed provision is never a silent nothing — for most of this feature's life it was, and the
/// whole agent platform simply looked dead on any repo whose bare clone outlived a 5-second budget.
/// Exactly one of the two is non-null.
/// </summary>
public sealed record RepoProvisionOutcome(RepoSyncBinding? Binding, string? FailureReason)
{
    public static RepoProvisionOutcome Success(RepoSyncBinding binding) => new(binding, null);
    public static RepoProvisionOutcome Failure(string reason) => new(null, reason);
}

/// <summary>
/// The agent-platform surface the shell talks to instead of naming <c>ControlCenterViewModel</c>
/// directly — so <c>MainWindowViewModel.ControlCenter</c> can be <c>null</c> under an edition
/// with no agent platform (1a). It exposes EXACTLY the members the shell references on the control
/// center, and — since step 2d — NO Pro-only concrete View/ViewModel type: the agent rail and the
/// resource monitor are reached only as opaque <c>object</c> and dropped into <c>ContentControl</c>s
/// that resolve their real View through <see cref="ViewLocator"/>. The remaining members are primitives
/// (the rail-section attention/spend adornments the shell binds through the window) plus C#-side hooks.
/// <see cref="ControlCenterViewModel"/> satisfies it; the agent-rail / resource-monitor concrete VMs are
/// reached only as <c>object</c> through it. Extends <see cref="IDisposable"/> because the shell disposes
/// its control center on teardown.
/// </summary>
public interface IAgentPlatformSurface : IDisposable
{
    // ---- rail-section adornments (bound in MainWindow.axaml through the window's ControlCenter) ----

    /// <summary>Coordinator attention badge visibility (the rail's amber dot).</summary>
    bool HasAttention { get; }

    /// <summary>Coordinator attention count (the rail badge number).</summary>
    int AttentionCount { get; }

    /// <summary>Today's token/USD spend, formatted for the Resources rail item.</summary>
    string SpendText { get; }

    // ---- Pro surfaces reached as opaque object (ViewLocator resolves the real View) ----

    /// <summary>The agent rail (worker list + kill switch) as opaque content — concretely an
    /// <c>AgentRailViewModel</c>, resolved to <c>AgentRailView</c> by <see cref="ViewLocator"/>. The shell
    /// holds it as <c>object?</c> (via <c>MainWindowViewModel.AgentRailContent</c>) and drops it into a
    /// <c>ContentControl</c>, so it never names the Pro rail types.</summary>
    object? AgentRailContent { get; }

    /// <summary>Build a task-manager resource monitor over the same backing services, returned as opaque
    /// content (concretely a <c>ResourceMonitorViewModel</c>, resolved to <c>ResourceMonitorView</c> by
    /// <see cref="ViewLocator"/>). The shell holds the result as <c>object?</c> and drops it into a
    /// <c>ContentControl</c>; the owner disposes it.</summary>
    object? CreateResourceMonitor();

    // ---- referenced from MainWindowViewModel (C#) ----

    /// <summary>Raised when the daemon first answers — the shell clears its degraded startup banner on it.</summary>
    event Action? DaemonReachable;

    /// <summary>Live (non-terminal) agent count the exit guard consults before a VM-stopping full exit.</summary>
    int LiveAgentCount { get; }

    /// <summary>Apply a coordinator-surface layout preset (File → Layout).</summary>
    void SetPreset(string preset);

    /// <summary>Propagate the direct-to-agent prompting mode to every open agent document.</summary>
    void SetDirectPrompting(bool allow);

    /// <summary>Make the coordinator conversation the surface's focus.</summary>
    void FocusCoordinator();

    /// <summary>Open (and focus) the given agent's document.</summary>
    void SelectAgent(string agentId);

    /// <summary>Point the live merge-queue projection at the daemon-provisioned repo handle.</summary>
    void SetActiveRepo(string repoHandle);

    /// <summary>
    /// Provision the just-opened repo into the daemon (P2-06). Returns the outcome — a sync-remote
    /// binding on success, a human-readable failure reason otherwise — or <c>null</c> only when this
    /// surface has no daemon at all (the mock/design harness), which the shell treats as "no agent
    /// platform", never as an error. The Pro implementation owns the <c>DaemonClient</c> call; the
    /// reference-clean shell registers the returned remote with its own <c>IGitService</c> and calls
    /// <see cref="SetActiveRepo"/>, so it never names the daemon types (2f). Implementations must clear
    /// any previously active repo BEFORE provisioning, so a failed provision leaves the queue pointed
    /// at nothing rather than at the previously opened repo.
    /// </summary>
    Task<RepoProvisionOutcome?> ProvisionRepoAsync(string repoPath);

    /// <summary>
    /// Stops every live agent through the ordinary Stop path (owner decision 2026-09-04) — the app-exit
    /// leg of "Stop agents and Mainguard OS on exit". On Windows the WSL terminate that follows would
    /// kill the jails anyway, but a clean stop first harvests logins and publishes each branch; on macOS
    /// there is no VM to stop, and before this the setting did nothing there at all — every jail outlived
    /// the app until Docker itself died. Best-effort and bounded by <paramref name="ct"/>: one agent's
    /// refusal must not keep the next from being stopped, or the app from exiting.
    /// </summary>
    Task StopAllAgentsAsync(CancellationToken ct) => Task.CompletedTask;
}
