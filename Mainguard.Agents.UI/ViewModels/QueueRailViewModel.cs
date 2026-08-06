using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// The merge-queue rail (ControlCenterDesign.md §3): a projection of the P2-10 state machine —
/// its states, its stale cascade, its CanMerge gate — and nothing the machine can't do.
/// Refresh is event-driven requery (OPS §3.4: events refresh the projection; the gate re-reads).
/// </summary>
public partial class QueueRailViewModel : ViewModelBase
{
    private readonly IMergeQueueService _queue;
    private readonly Action<string> _openReview;

    public ObservableCollection<QueueEntryViewModel> Entries { get; } = new();

    [ObservableProperty] private string _mainShaText = "";
    [ObservableProperty] private string _gateText = "";
    [ObservableProperty] private bool _isEmpty;

    public QueueRailViewModel(IMergeQueueService queue, Action<string> openReview)
    {
        _queue = queue;
        _openReview = openReview;
        Refresh();
    }

    public void Refresh()
    {
        var snapshot = _queue.GetQueue();
        MainShaText = "main " + _queue.MainSha;

        // In-place sync so unchanged rows keep their visuals (no churn, no reflow).
        for (int i = Entries.Count - 1; i >= 0; i--)
            if (snapshot.All(q => q.AgentId != Entries[i].AgentId))
                Entries.RemoveAt(i);

        for (int i = 0; i < snapshot.Count; i++)
        {
            var entry = snapshot[i];
            var existing = Entries.FirstOrDefault(e => e.AgentId == entry.AgentId);
            if (existing is null)
            {
                existing = new QueueEntryViewModel(entry.AgentId, _openReview, _queue);
                Entries.Insert(Math.Min(i, Entries.Count), existing);
            }
            existing.Update(entry, _queue);
        }

        // Keep the rail in the service's order (Verified-fresh first).
        for (int i = 0; i < snapshot.Count; i++)
        {
            var target = Entries.FirstOrDefault(e => e.AgentId == snapshot[i].AgentId);
            var current = Entries.IndexOf(target!);
            if (current != i && current >= 0) Entries.Move(current, i);
        }

        IsEmpty = Entries.Count == 0;

        // The rail's ONE accent: the front-most fresh Verified entry gets the Review CTA.
        var first = Entries.FirstOrDefault(e => e.IsReviewable);
        foreach (var e in Entries) e.ShowReviewAccent = ReferenceEquals(e, first);

        // The gate line mirrors the front entry's CanMerge reason (§3.4).
        if (first is not null && !_queue.CanMerge(first.AgentId, out var reason))
            GateText = reason;
        else if (first is not null)
            GateText = "ready to merge";
        else
            GateText = "";
    }
}

/// <summary>One thread on the rail. State word first (E4/N-3); badge + brush are second channels.</summary>
public partial class QueueEntryViewModel : ViewModelBase
{
    private readonly Action<string> _openReview;
    private readonly IMergeQueueService _queue;

    /// <summary>The last state the queue stream reported, so the Verify affordance can be re-armed
    /// without waiting for a stream update that a refused request never produces.</summary>
    private WorkerMergeState _lastState = WorkerMergeState.Working;

    public string AgentId { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _branch = "";
    [ObservableProperty] private string _stateWord = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _verifiedAgainst = "";
    [ObservableProperty] private string _badgeGeometryKey = "AgentWorkingIcon";
    [ObservableProperty] private bool _isReviewable;
    [ObservableProperty] private bool _showReviewAccent;

    /// <summary>Whether the human can ask for a verification run on this entry right now. False while one
    /// is already in flight (the daemon rejects a concurrent run) and on the terminal states.</summary>
    [ObservableProperty] private bool _canVerify;

    /// <summary>True only while this row's own request is in flight — keeps the button from being pressed
    /// twice before the queue stream reports <c>Verifying</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VerifyButtonText))]
    private bool _isVerifyRequestInFlight;

    public string VerifyButtonText => IsVerifyRequestInFlight ? "Verifying…" : "Verify";

    /// <summary>The daemon's verbatim answer to the last verification request — a refusal it never ran
    /// ("no live jail", "no verification command"), or a run that happened and failed. Cleared on the
    /// next request. Rendered as a fact line, never swallowed.</summary>
    [ObservableProperty] private string _verifyMessage = "";

    [ObservableProperty] private bool _isNeutral;
    [ObservableProperty] private bool _isMutedState;
    [ObservableProperty] private bool _isInfoState;
    [ObservableProperty] private bool _isWarningState;
    [ObservableProperty] private bool _isSuccessState;
    [ObservableProperty] private bool _isDangerState;

    /// <param name="queue">The seam the Verify command triggers through. <b>Required</b>, deliberately:
    /// an optional queue would let a caller construct a row whose Verify button silently does nothing,
    /// which is precisely the "built but not wired" failure this control was added to fix.</param>
    public QueueEntryViewModel(string agentId, Action<string> openReview, IMergeQueueService queue)
    {
        AgentId = agentId;
        _openReview = openReview;
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        // Decide the affordance up front rather than leaving it on `bool`'s default false: a row that
        // renders before its first Update would otherwise show a dead Verify button.
        RecomputeCanVerify();
    }

    public void Update(QueueEntry entry, IMergeQueueService queue)
    {
        Name = entry.Name;
        Branch = entry.Branch;
        Detail = entry.Detail;
        StateWord = entry.State switch
        {
            WorkerMergeState.StaleVerified => "Stale",
            WorkerMergeState.AwaitingReview => "Awaiting review",
            var s => s.ToString(),
        };
        VerifiedAgainst = entry.Verification is { } v && entry.State is WorkerMergeState.Verified or WorkerMergeState.AwaitingReview
            ? $"main@{v.MainSha}" : "";
        IsReviewable = entry.State is WorkerMergeState.Verified or WorkerMergeState.AwaitingReview;

        // The daemon now owns this row's progress; drop our optimistic in-flight latch.
        if (entry.State == WorkerMergeState.Verifying) IsVerifyRequestInFlight = false;

        _lastState = entry.State;
        RecomputeCanVerify();

        (BadgeGeometryKey, IsNeutral, IsMutedState, IsInfoState, IsWarningState, IsSuccessState, IsDangerState) = entry.State switch
        {
            WorkerMergeState.Working => ("AgentWorkingIcon", true, false, false, false, false, false),
            WorkerMergeState.Verifying => ("AgentVerifyingIcon", false, false, true, false, false, false),
            WorkerMergeState.Verified => ("AgentWaitingIcon", false, false, false, false, true, false),
            WorkerMergeState.AwaitingReview => ("AgentWaitingIcon", false, false, false, false, true, false),
            WorkerMergeState.StaleVerified => ("AgentStaleIcon", false, false, false, true, false, false),
            WorkerMergeState.Merged => ("CheckmarkIcon", false, false, false, false, true, false),
            WorkerMergeState.Rejected => ("DismissIcon", false, false, false, false, false, true),
            _ => ("AgentWorkingIcon", false, true, false, false, false, false),
        };
    }

    [RelayCommand]
    private void OpenReview() => _openReview(AgentId);

    /// <summary>
    /// The human's verification trigger — the Verify button on this row.
    ///
    /// <para><b>Deliberately thin.</b> It makes one call to <see cref="IMergeQueueService.RunVerificationAsync"/>
    /// and renders the answer. It decides nothing: it does not transition the entry, does not judge
    /// pass/fail, and does not touch any merge gate — all of that is the daemon's
    /// <c>MergeQueue.RunVerificationAsync</c>, and the new state arrives back through the queue stream
    /// like every other state change. Keeping the policy on the daemon is what lets phase 2's automatic
    /// caller reuse this path exactly rather than growing a second copy of it beside this one.</para>
    /// </summary>
    [RelayCommand]
    private async Task VerifyAsync()
    {
        if (IsVerifyRequestInFlight) return;

        IsVerifyRequestInFlight = true;
        RecomputeCanVerify();
        VerifyMessage = "";
        try
        {
            var outcome = await _queue.RunVerificationAsync(AgentId).ConfigureAwait(true);
            VerifyMessage = outcome.Reason;
        }
        catch (Exception ex)
        {
            // A verification that could not even be requested is reported, never swallowed into a row
            // that silently stays "not verified yet" — that silence is the defect this button fixes.
            VerifyMessage = "Can't verify — " + ex.Message;
        }
        finally
        {
            // Always re-arm from the last known state. A request the daemon never acted on — no repo
            // bound, daemon unreachable — moves no queue state and therefore pushes no stream update,
            // so a button left disabled here would stay dead for the session: the very "the human is
            // stuck with no way forward" failure this control exists to remove.
            IsVerifyRequestInFlight = false;
            RecomputeCanVerify();
        }
    }

    /// <summary>The single place the Verify affordance is decided, so the command and the stream
    /// refresh can never disagree about it.</summary>
    private void RecomputeCanVerify() =>
        // Offered on every state a run can legally start from — including Verified, because
        // RE-verifying against a moved main is the normal way a stale entry gets fresh again. Withheld
        // while a run is in flight (the daemon refuses a concurrent one) and on the terminal states,
        // where there is nothing left to verify.
        CanVerify = !IsVerifyRequestInFlight && _lastState is not (
            WorkerMergeState.Verifying or WorkerMergeState.Merged or WorkerMergeState.Rejected);
}
