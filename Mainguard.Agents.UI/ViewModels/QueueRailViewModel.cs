using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents;
using Mainguard.Agents.UI.Services;
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
    private readonly Action<string, bool>? _report;
    private readonly Func<string?>? _resumeAgentKind;

    public ObservableCollection<QueueEntryViewModel> Entries { get; } = new();

    [ObservableProperty] private string _mainShaText = "";
    [ObservableProperty] private string _gateText = "";
    [ObservableProperty] private bool _isEmpty;

    /// <param name="report">(message, isWarning) sink for the lifecycle actions' outcomes; null uses the
    /// shell's toast stack. Injected by tests — a discard's refusal is the sentence the human reads, so it
    /// has to be observable somewhere other than a toast.</param>
    /// <param name="resumeAgentKind">
    /// Which CLI a resume should run in the new jail, read at press time. It is a callback rather than a
    /// value because the answer is the human's current choice on the surface that owns the CLI picker, and
    /// a value captured at rail construction would be whatever was selected when the repo opened. Null (the
    /// render harness, unit tests) simply resolves to no kind, and the daemon refuses with a reason.
    /// </param>
    public QueueRailViewModel(
        IMergeQueueService queue, Action<string> openReview, Action<string, bool>? report = null,
        Func<string?>? resumeAgentKind = null)
    {
        _queue = queue;
        _openReview = openReview;
        _report = report;
        _resumeAgentKind = resumeAgentKind;
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
                existing = new QueueEntryViewModel(
                    entry.AgentId, _openReview, _queue, _report, _resumeAgentKind);
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

/// <summary>
/// One thread on the rail. State word first (E4/N-3); badge + brush are second channels.
///
/// <para>Beyond Review it carries the entry's <b>lifecycle</b> actions — the ones an entry whose agent is
/// gone previously had none of. Every one of them is a thin drive of a daemon RPC: the transition, the
/// record and the refusal all live in the daemon, and this class can neither remove a row from the queue
/// nor invent an outcome. That direction is deliberate — a "remove from list" implemented here would
/// clear the rail until the next <c>StreamQueue</c> snapshot silently put the entry back.</para>
/// </summary>
public partial class QueueEntryViewModel : ViewModelBase
{
    private readonly Action<string> _openReview;
    private readonly IMergeQueueService _queue;
    private readonly Action<string, bool>? _report;
    private readonly Func<string?>? _resumeAgentKind;

    /// <summary>The last state the queue stream reported, so the Verify affordance can be re-armed
    /// without waiting for a stream update that a refused request never produces.</summary>
    private WorkerMergeState _lastState = WorkerMergeState.Working;

    /// <summary>
    /// The daemon's last word on whether this entry still has a jail — <b>three-valued</b>. Null is "the
    /// projection did not say", and it must not be read as "no jail": that would offer to resume every
    /// entry served by a daemon that predates the fact, including the ones that are running.
    /// </summary>
    private bool? _hasLiveSandbox;

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

    /// <summary>True when a discard is a legal thing to ask for — i.e. the entry is not already terminal.
    /// The daemon refuses a terminal discard anyway; this only keeps the rail from offering it.</summary>
    [ObservableProperty] private bool _canDiscard;

    /// <summary>
    /// True only for an entry whose row says <c>Verifying</c> while the DAEMON reports no run in flight.
    /// Never inferred from the state alone: that inference is wrong for exactly the entries this action
    /// exists for.
    /// </summary>
    [ObservableProperty] private bool _isVerificationStalled;

    /// <summary>The two-step guard on the destructive action: the row asks before it drops anything.</summary>
    [ObservableProperty] private bool _isConfirmingDiscard;

    /// <summary>
    /// True for a <b>stranded</b> entry: non-terminal, and the daemon positively reports that it has no
    /// jail. Both halves matter. Verification runs only in the worker's own sandbox, so an entry without
    /// one cannot be verified, cannot become <c>Verified</c>, and cannot merge — until now its only
    /// offered action was to throw the work away. And "positively reports": a projection that cannot
    /// answer leaves this false, so Resume is never offered on a guess.
    /// </summary>
    [ObservableProperty] private bool _canResume;

    /// <summary>True only while this row's own resume is in flight. A resume builds a jail, which takes
    /// tens of seconds, so the button has to say so or it reads as a control that did nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResumeButtonText))]
    private bool _isResumeRequestInFlight;

    public string ResumeButtonText => IsResumeRequestInFlight ? "Resuming…" : "Resume";

    /// <summary>
    /// True while this row's own lifecycle request is in flight — the same latch <see cref="VerifyAsync"/>
    /// keeps, for the same reason. Without it a double-press fires two RPCs, and the second comes back as
    /// "this entry was already discarded": a warning toast reporting a failure for the action the human
    /// just successfully performed.
    /// </summary>
    private bool _lifecycleRequestInFlight;

    [ObservableProperty] private bool _isNeutral;
    [ObservableProperty] private bool _isMutedState;
    [ObservableProperty] private bool _isInfoState;
    [ObservableProperty] private bool _isWarningState;
    [ObservableProperty] private bool _isSuccessState;
    [ObservableProperty] private bool _isDangerState;

    /// <param name="queue">The seam the Verify and entry-lifecycle commands trigger through.
    /// <b>Required</b>, deliberately: an optional queue would let a caller construct a row whose buttons
    /// silently do nothing, which is precisely the "built but not wired" failure these controls were
    /// added to fix.</param>
    /// <param name="report">(message, isWarning) sink for the entry-lifecycle outcomes; null uses the
    /// shell's toast stack. Optional because it has a real default, unlike the queue.</param>
    /// <param name="resumeAgentKind">Which CLI a resume runs in the new jail, read at press time. See
    /// <see cref="QueueRailViewModel"/>'s constructor.</param>
    public QueueEntryViewModel(
        string agentId,
        Action<string> openReview,
        IMergeQueueService queue,
        Action<string, bool>? report = null,
        Func<string?>? resumeAgentKind = null)
    {
        AgentId = agentId;
        _openReview = openReview;
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _report = report;
        _resumeAgentKind = resumeAgentKind;
        // Decide the affordance up front rather than leaving it on `bool`'s default false: a row that
        // renders before its first Update would otherwise show a dead Verify button.
        RecomputeCanVerify();
    }

    public void Update(QueueEntry entry, IMergeQueueService queue)
    {
        Name = entry.Name;
        Branch = entry.Branch;
        Detail = entry.Detail;

        // A Verifying row with no run behind it is NOT verifying, and the rail must not say it is. The
        // daemon supplies the in-flight fact; the state alone cannot tell these apart, which is precisely
        // how such an entry ends up reading as permanently busy.
        IsVerificationStalled = entry.State == WorkerMergeState.Verifying && !entry.VerificationInFlight;

        StateWord = entry.State switch
        {
            WorkerMergeState.Verifying when IsVerificationStalled => "Stalled",
            WorkerMergeState.StaleVerified => "Stale",
            WorkerMergeState.AwaitingReview => "Awaiting review",
            var s => s.ToString(),
        };
        // The wire carries the sha as its own field (VerifiedMainSha) — deliberately NOT wrapped in a
        // VerificationRecord, which would fabricate a pass/fail verdict the daemon didn't send. The old
        // read looked only at Verification, which the daemon projection never populates, so the stamp
        // could never render in the shipped app.
        var verifiedSha = entry.VerifiedMainSha is { Length: > 0 } wireSha ? wireSha : entry.Verification?.MainSha;
        VerifiedAgainst = verifiedSha is { Length: > 0 } sha
            && entry.State is WorkerMergeState.Verified or WorkerMergeState.AwaitingReview
            ? $"main@{Shorten(sha)}" : "";
        IsReviewable = entry.State is WorkerMergeState.Verified or WorkerMergeState.AwaitingReview;

        // The daemon now owns this row's progress; drop our optimistic in-flight latch.
        if (entry.State == WorkerMergeState.Verifying) IsVerifyRequestInFlight = false;

        _lastState = entry.State;
        _hasLiveSandbox = entry.HasLiveSandbox;
        RecomputeCanVerify();

        CanDiscard = entry.State
            is not (WorkerMergeState.Merged or WorkerMergeState.Rejected or WorkerMergeState.Discarded);
        if (!CanDiscard)
        {
            IsConfirmingDiscard = false;
        }

        // Stranded: a non-terminal entry the daemon says has no jail. `== false` rather than `!= true` on
        // purpose — an unknown answer offers nothing, because offering a resume for a live agent would
        // spend a minute building a jail only for the daemon to refuse it.
        CanResume = CanDiscard && _hasLiveSandbox == false;

        (BadgeGeometryKey, IsNeutral, IsMutedState, IsInfoState, IsWarningState, IsSuccessState, IsDangerState) = entry.State switch
        {
            WorkerMergeState.Working => ("AgentWorkingIcon", true, false, false, false, false, false),
            // A stalled Verifying reads as a warning, not as the info-blue "work in progress" it is not.
            WorkerMergeState.Verifying when IsVerificationStalled => ("AgentStaleIcon", false, false, false, true, false, false),
            WorkerMergeState.Verifying => ("AgentVerifyingIcon", false, false, true, false, false, false),
            WorkerMergeState.Verified => ("AgentWaitingIcon", false, false, false, false, true, false),
            WorkerMergeState.AwaitingReview => ("AgentWaitingIcon", false, false, false, false, true, false),
            WorkerMergeState.StaleVerified => ("AgentStaleIcon", false, false, false, true, false, false),
            WorkerMergeState.Merged => ("CheckmarkIcon", false, false, false, false, true, false),
            WorkerMergeState.Rejected => ("DismissIcon", false, false, false, false, false, true),
            // Discarded entries leave the daemon's live queue, so this maps a state the rail should not
            // normally receive. It is spelled out rather than left to the fallback so that if one ever DOES
            // arrive (a mock, a future projection) it reads as the muted, finished, not-merged thing it is
            // — never borrowing Merged's success green.
            WorkerMergeState.Discarded => ("DismissIcon", false, true, false, false, false, false),
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
        //
        // …and withheld when the daemon says this entry has NO JAIL. Verification runs in the worker's own
        // sandbox and never on the host, so pressing Verify there can only ever produce "Agent 'x' has no
        // live sandbox" — an enabled button whose entire behaviour is an error message. It stays enabled on
        // an UNKNOWN answer (`!= false`, not `== true`): withholding the one action an entry has on the
        // strength of a fact the projection never supplied would be the worse mistake.
        CanVerify = !IsVerifyRequestInFlight
            && _hasLiveSandbox != false
            && _lastState is not (
                WorkerMergeState.Verifying or WorkerMergeState.Merged or WorkerMergeState.Rejected
                or WorkerMergeState.Discarded);

    /// <summary>Arms the destructive action. Nothing has been asked of the daemon yet.</summary>
    [RelayCommand]
    private void BeginDiscard() => IsConfirmingDiscard = true;

    /// <summary>Disarms it.</summary>
    [RelayCommand]
    private void CancelDiscard() => IsConfirmingDiscard = false;

    /// <summary>
    /// The confirmed discard. The reason recorded is the state the entry was in when the human dropped it
    /// — a fact this surface actually has, rather than a prompt for prose nobody types.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmDiscardAsync()
    {
        IsConfirmingDiscard = false;
        if (_lifecycleRequestInFlight) return;

        _lifecycleRequestInFlight = true;
        try
        {
            await MergeActionRunner
                .DiscardAsync(_queue, AgentId, $"dropped from the queue while {StateWord}", _report)
                .ConfigureAwait(true);
        }
        finally
        {
            _lifecycleRequestInFlight = false;
        }
    }

    /// <summary>
    /// Asks the daemon to give this stranded entry a live jail again, standing on its own branch.
    ///
    /// <para><b>Not confirmed first, unlike Discard.</b> A discard is destructive and asks; a resume adds a
    /// sandbox and changes nothing that cannot be undone by stopping the agent — an extra step there would
    /// be ceremony on the recovery action while the destructive one it sits next to is one press away.</para>
    ///
    /// <para>It decides nothing. Whether the entry exists in this repo's queue, whether its branch
    /// survived, whether the id already has a session and whether a merge is open are all the daemon's
    /// answers; this hands over the repo's active handle and the CLI the human picked.</para>
    /// </summary>
    [RelayCommand]
    private async Task ResumeAsync()
    {
        if (_lifecycleRequestInFlight || IsResumeRequestInFlight) return;

        _lifecycleRequestInFlight = true;
        IsResumeRequestInFlight = true;
        try
        {
            await MergeActionRunner
                .ResumeAsync(_queue, AgentId, _resumeAgentKind?.Invoke() ?? string.Empty, _report)
                .ConfigureAwait(true);
        }
        finally
        {
            // Re-armed from here rather than from the next stream update, for the same reason Verify is: a
            // resume the daemon refused moves no queue state, so it pushes no snapshot, so a button left
            // latched would stay dead for the session.
            IsResumeRequestInFlight = false;
            _lifecycleRequestInFlight = false;
        }
    }

    /// <summary>Asks the daemon to clear a Verifying state it is not actually running.</summary>
    [RelayCommand]
    private async Task ClearStalledVerificationAsync()
    {
        if (_lifecycleRequestInFlight) return;

        _lifecycleRequestInFlight = true;
        try
        {
            await MergeActionRunner
                .ClearStalledVerificationAsync(_queue, AgentId, _report)
                .ConfigureAwait(true);
        }
        finally
        {
            _lifecycleRequestInFlight = false;
        }
    }

    private static string Shorten(string sha) => sha.Length > 10 ? sha[..10] : sha;
}
