using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// The P2-10 Merge Queue Rail bound to the <b>real</b> <see cref="MergeQueue"/> state machine
/// (ControlCenterDesign.md §3). Each row names the <c>main@sha</c> its verification ran against, the
/// stale cascade is shown as a re-verification wave (never a silent reorder), and the merge button is
/// bound to <see cref="MergeQueue.CanMerge"/> with the reason surfaced verbatim. The override sits
/// behind a confirm + loud warning and is a SEPARATE path — the merge button stays disabled.
/// Design tokens / component classes only; no raw colors; renders in all five themes.
/// </summary>
public partial class MergeQueueViewModel : ViewModelBase, IDisposable
{
    private readonly MergeQueue _queue;
    private readonly Action<string> _onMerge;
    private readonly Action<string, string> _onOverride;
    private readonly Func<string, string> _displayName;

    public ObservableCollection<MergeQueueRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _mainShaText = "";
    [ObservableProperty] private bool _isEmpty = true;

    /// <param name="queue">The live P2-10 merge queue.</param>
    /// <param name="onMerge">The human "Merge to Main" action (agentId) — the only path to Merged.</param>
    /// <param name="onOverride">The loud stale-override action (agentId, reason) — separate from CanMerge.</param>
    /// <param name="displayName">Maps an agent id to its working name (defaults to the id).</param>
    public MergeQueueViewModel(
        MergeQueue queue,
        Action<string>? onMerge = null,
        Action<string, string>? onOverride = null,
        Func<string, string>? displayName = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _onMerge = onMerge ?? (_ => { });
        _onOverride = onOverride ?? ((_, _) => { });
        _displayName = displayName ?? (id => id);
        _queue.Changed += OnQueueChanged;
        Refresh();
    }

    private void OnQueueChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Refresh();
        }
        else
        {
            Dispatcher.UIThread.Post(Refresh);
        }
    }

    public void Refresh()
    {
        MainShaText = "main@" + Short(_queue.CurrentMainSha);
        var agents = _queue.Agents.OrderBy(RailOrder).ThenBy(a => a, StringComparer.Ordinal).ToList();

        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!agents.Contains(Rows[i].AgentId))
            {
                Rows.RemoveAt(i);
            }
        }

        for (var i = 0; i < agents.Count; i++)
        {
            var agentId = agents[i];
            var row = Rows.FirstOrDefault(r => r.AgentId == agentId);
            if (row is null)
            {
                row = new MergeQueueRowViewModel(agentId, _displayName(agentId), _onMerge, _onOverride);
                Rows.Insert(Math.Min(i, Rows.Count), row);
            }

            row.Update(_queue);
        }

        for (var i = 0; i < agents.Count; i++)
        {
            var target = Rows.FirstOrDefault(r => r.AgentId == agents[i]);
            var current = target is null ? -1 : Rows.IndexOf(target);
            if (current >= 0 && current != i)
            {
                Rows.Move(current, i);
            }
        }

        IsEmpty = Rows.Count == 0;
    }

    private int RailOrder(string agentId) => _queue.GetState(agentId) switch
    {
        WorkerMergeState.Verified => 0,
        WorkerMergeState.AwaitingReview => 0,
        WorkerMergeState.Verifying => 1,
        WorkerMergeState.Working => 2,
        WorkerMergeState.StaleVerified => 3,
        WorkerMergeState.Merged => 4,
        _ => 5,
    };

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "—" : (sha.Length > 8 ? sha[..8] : sha);

    public void Dispose() => _queue.Changed -= OnQueueChanged;
}

/// <summary>One row on the merge-queue rail. State word first (E4); badge + brush are second channels.</summary>
public partial class MergeQueueRowViewModel : ViewModelBase
{
    private readonly Action<string> _onMerge;
    private readonly Action<string, string> _onOverride;

    public string AgentId { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _stateWord = "";
    [ObservableProperty] private string _verifiedAgainst = "";
    [ObservableProperty] private bool _canMerge;
    [ObservableProperty] private string _gateReason = "";
    [ObservableProperty] private bool _canOverride;
    [ObservableProperty] private string _badgeGeometryKey = "AgentWorkingIcon";

    [ObservableProperty] private bool _isNeutral;
    [ObservableProperty] private bool _isMutedState;
    [ObservableProperty] private bool _isInfoState;
    [ObservableProperty] private bool _isWarningState;
    [ObservableProperty] private bool _isSuccessState;
    [ObservableProperty] private bool _isDangerState;

    public MergeQueueRowViewModel(string agentId, string name, Action<string> onMerge, Action<string, string> onOverride)
    {
        AgentId = agentId;
        _name = name;
        _onMerge = onMerge;
        _onOverride = onOverride;
    }

    public void Update(MergeQueue queue)
    {
        var state = queue.GetState(AgentId);
        StateWord = state switch
        {
            WorkerMergeState.StaleVerified => "Stale — re-verifying",
            WorkerMergeState.AwaitingReview => "Awaiting review",
            _ => state.ToString(),
        };

        CanMerge = queue.CanMerge(AgentId, out var reason);
        GateReason = CanMerge ? "ready to merge" : reason;

        // Verified/AwaitingReview rows show the main@sha they were verified against (freshness at a glance).
        VerifiedAgainst = state is WorkerMergeState.Verified or WorkerMergeState.AwaitingReview
            ? "verified against main@" + Short(queue.CurrentMainSha)
            : "";

        // The override affordance exists only for a stale branch — and merging via it is a separate path
        // (CanMerge stays false). It sits behind a confirm + warning in the view.
        CanOverride = state == WorkerMergeState.StaleVerified;

        (BadgeGeometryKey, IsNeutral, IsMutedState, IsInfoState, IsWarningState, IsSuccessState, IsDangerState) = state switch
        {
            WorkerMergeState.Working => ("AgentWorkingIcon", true, false, false, false, false, false),
            WorkerMergeState.Verifying => ("AgentVerifyingIcon", false, false, true, false, false, false),
            WorkerMergeState.Verified => ("AgentWaitingIcon", false, false, false, false, true, false),
            WorkerMergeState.AwaitingReview => ("AgentWaitingIcon", false, false, false, false, true, false),
            WorkerMergeState.StaleVerified => ("AgentStaleIcon", false, false, false, true, false, false),
            WorkerMergeState.Merged => ("CheckmarkIcon", false, false, false, false, true, false),
            WorkerMergeState.Rejected => ("DismissIcon", false, false, false, false, false, true),
            // Finished and NOT merged: muted, and pointedly not the success green Merged wears.
            WorkerMergeState.Discarded => ("DismissIcon", false, true, false, false, false, false),
            _ => ("AgentWorkingIcon", false, true, false, false, false, false),
        };
    }

    [RelayCommand]
    private void Merge()
    {
        if (CanMerge)
        {
            _onMerge(AgentId);
        }
    }

    [RelayCommand]
    private void Override() => _onOverride(AgentId, "human stale-merge override");

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "—" : (sha.Length > 8 ? sha[..8] : sha);
}
