using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents;
using Mainguard.Agents.UI.ViewModels.Agents;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// One activity-bar agent row (P2-13 Row 1, LIFO). Exposes the lifecycle state as the badge
/// geometry key plus a single <see cref="AgentStatus"/> — the View colours the micro-badge through
/// the one <c>AgentStatusBrushConverter</c>, so there is no color and no second status→brush map in
/// the VM (P2-13 invariant #2). Badge forms per ControlCenterDesign.md §9.3.
/// </summary>
public partial class AgentRowViewModel : ViewModelBase
{
    public string AgentId { get; }

    /// <summary>The agent's CLI kind. Kept because the tooltip still reports what the session RUNS,
    /// but never the row's label: four sessions of one CLI produce four identical values.</summary>
    public string Name { get; }

    public string Branch { get; }

    /// <summary>
    /// What the row is labelled: the human's name for this agent, else its brief, else role + short id
    /// (see <see cref="AgentInfo.DisplayName"/>). Observable because all three inputs move while the row
    /// is on screen — a worker presents its first plan, or somebody renames it.
    /// </summary>
    [ObservableProperty] private string _displayName = "";

    /// <summary>True when <see cref="DisplayName"/> is a name the human typed — the one case
    /// "Reset name" has anything to undo, and the only reason the menu item is enabled.</summary>
    [ObservableProperty] private bool _isRenamed;

    /// <summary>
    /// Whether this row is the surface currently being viewed.
    ///
    /// <para>The rail had no such property at all: an agent row was a plain button with nothing bound to
    /// its <c>active</c> class, so selecting an agent could never light its row — while the Coordinator
    /// section row stayed lit, because viewing an agent is routed as "the Coordinator section with a
    /// different panel inside it". A human switching to an agent therefore saw the highlight stay
    /// exactly where it had been.</para>
    /// </summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>True while this agent's jail is paused, so the one menu item can say "Resume" rather
    /// than offering "Pause" on something already frozen.</summary>
    [ObservableProperty] private bool _isPaused;

    /// <summary>"Pause" or "Resume" — the label of the single pause/resume item.</summary>
    [ObservableProperty] private string _pauseMenuLabel = "Pause";

    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _badgeGeometryKey = "AgentWorkingIcon";
    [ObservableProperty] private string _stateWord = "";
    [ObservableProperty] private double _rowOpacity = 1.0;

    /// <summary>The badge status — the single input to the one <c>AgentStatusBrushConverter</c>.
    /// The View binds the micro-badge Foreground to this; no color lives in the VM.</summary>
    [ObservableProperty] private AgentStatus _status = AgentStatus.Working;

    /// <summary>True while this agent needs the human (drives the row's attention affordance).</summary>
    [ObservableProperty] private bool _needsAttention;

    /// <summary>The collapsed rail's tooltip: name — state · current task (E4/TT-1).</summary>
    [ObservableProperty] private string _tooltip = "";

    /// <summary>The row's role label: "coordinator" for the coordinator CLI, "subagent" for a
    /// coordinator-spawned managed worker, empty for a manual agent. Text, not color — the badge
    /// stays the one status channel.</summary>
    [ObservableProperty] private string _roleLabel = "";

    [ObservableProperty] private bool _hasRoleLabel;

    /// <summary>
    /// Who performs this row's menu actions. The row raises intent and owns none of it: pausing,
    /// ending and deleting an agent are daemon conversations with confirmations and toasts attached,
    /// and they already live on the control centre. Null in the render harnesses, where the menu is
    /// laid out but never invoked.
    /// </summary>
    private readonly IAgentRowActions? _actions;

    public AgentRowViewModel(AgentInfo info, IAgentRowActions? actions = null)
    {
        AgentId = info.AgentId;
        Name = info.Name;
        Branch = info.Branch;
        _actions = actions;
        Update(info);
    }

    // ---- the row's context menu ------------------------------------------------------------------
    //
    // Every one of these is a thin dispatch. The row is a projection; it decides nothing about whether
    // an agent may be paused, what a delete destroys, or which surface a review opens on.

    [RelayCommand]
    private void Open() => _actions?.Open(AgentId);

    [RelayCommand]
    private Task PauseOrResumeAsync() => _actions?.PauseOrResumeAsync(AgentId) ?? Task.CompletedTask;

    [RelayCommand]
    private void Rename() => _actions?.BeginRename(AgentId);

    /// <summary>Puts the row back on its derived name. Enabled only while there is a typed name to
    /// drop — otherwise it would claim to undo something that never happened.</summary>
    [RelayCommand(CanExecute = nameof(IsRenamed))]
    private void ResetName() => _actions?.ResetName(AgentId);

    [RelayCommand]
    private void Review() => _actions?.Review(AgentId);

    [RelayCommand]
    private Task VerifyAsync() => _actions?.VerifyAsync(AgentId) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ViewVerificationLogAsync() =>
        _actions?.ViewVerificationLogAsync(AgentId) ?? Task.CompletedTask;

    /// <summary>The FULL id, not the shortened one the row shows: the short form is for reading, and a
    /// human copying an id is about to paste it somewhere that needs all of it.</summary>
    [RelayCommand]
    private Task CopyAgentIdAsync() => _actions?.CopyAsync(AgentId) ?? Task.CompletedTask;

    [RelayCommand]
    private Task CopyBranchAsync() => _actions?.CopyAsync(Branch) ?? Task.CompletedTask;

    [RelayCommand]
    private void End() => _actions?.ConfirmEnd(AgentId);

    [RelayCommand]
    private void Delete() => _actions?.ConfirmDelete(AgentId);

    /// <summary>Keeps "Reset name" enabled exactly while there is a name to reset.</summary>
    partial void OnIsRenamedChanged(bool value) => ResetNameCommand.NotifyCanExecuteChanged();

    public void Update(AgentInfo info)
    {
        Detail = info.Detail;
        DisplayName = info.DisplayName;
        IsRenamed = info.IsRenamed;
        StateWord = info.State.ToString();
        IsPaused = info.State == AgentLifecycleState.Paused;
        PauseMenuLabel = IsPaused ? "Resume" : "Pause";
        RowOpacity = info.State == AgentLifecycleState.ReviewHibernated ? 0.60 : 1.0;
        RoleLabel = info.Role switch
        {
            AgentRoles.Coordinator => "coordinator",
            AgentRoles.Managed => "subagent",
            _ => "",
        };
        HasRoleLabel = RoleLabel.Length > 0;
        // Leads with the row's LABEL and still carries the CLI kind, because the two answer different
        // questions — which agent this is, and what it is running — and the row only has space for the
        // first.
        Tooltip = HasRoleLabel
            ? $"{DisplayName} ({RoleLabel}, {Name}) — {StateWord} · {info.Detail} · {Branch}"
            : $"{DisplayName} ({Name}) — {StateWord} · {info.Detail} · {Branch}";
        Status = AgentStatusMap.FromLifecycle(info.State);
        NeedsAttention = AttentionPolicy.IsAttentionRequired(Status);

        // The glyph carries the state (shape is the primary channel, E1/E2); colour is the second
        // channel and comes from the converter via Status.
        BadgeGeometryKey = info.State switch
        {
            AgentLifecycleState.Working => "AgentWorkingIcon",
            AgentLifecycleState.Provisioning => "AgentProvisioningIcon",
            AgentLifecycleState.Yielding => "AgentVerifyingIcon",
            AgentLifecycleState.Paused => "AgentPausedIcon",
            AgentLifecycleState.ReviewHibernated => "AgentPausedIcon",
            AgentLifecycleState.RateLimited => "AgentThrottledIcon",
            AgentLifecycleState.Unresponsive => "AgentUnresponsiveIcon",
            AgentLifecycleState.PlanPending => "AgentWaitingIcon",
            AgentLifecycleState.AwaitingReview => "AgentWaitingIcon",
            AgentLifecycleState.Merged => "CheckmarkIcon",
            AgentLifecycleState.Rejected => "DismissIcon",
            AgentLifecycleState.Dead => "DismissIcon",
            _ => "AgentWorkingIcon",
        };
    }

    /// <summary>Re-raise <see cref="Status"/> so the badge binding re-runs the converter after a
    /// live theme switch (the converter resolves against the active theme variant).</summary>
    public void RefreshBadgeBrush() => OnPropertyChanged(nameof(Status));
}
