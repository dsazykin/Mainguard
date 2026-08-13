using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.UI.Services;
using Mainguard.Git.Review;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// The pinned flagged-changes gate panel (ControlCenterDesign §6.3). Renders every must-acknowledge item
/// for the branch and acknowledges them <b>individually</b>; there is no global checkbox (rejection
/// trigger). This View-model only renders + forwards acks — all rule logic lives in pure Core / on the
/// daemon (invariant 1).
///
/// <para>It has two sources, and which one it is built with decides where a checkmark goes.</para>
/// <list type="bullet">
/// <item><b>Local</b> — the branch's <see cref="AcknowledgmentStore"/> plus the in-process
/// <see cref="ChangedTestCommandGate"/>. Correct only where that store IS the gate being cleared (the
/// design/render harness and the pure-Core composition tests).</item>
/// <item><b>Live</b> — an <see cref="IFlaggedChangeSource"/> over the daemon's queue projection.
/// The gate that blocks a merge is daemon-side, so this is the only source whose acknowledgments unblock
/// anything. The panel renders the daemon's own answer: an acknowledgment the gate did not record leaves
/// the row unacknowledged and states why, and when acknowledgments cannot reach the gate at all the
/// controls are disabled and say so rather than appearing to work.</item>
/// </list>
/// </summary>
public partial class FlaggedChangesPanelViewModel : ViewModelBase
{
    private readonly AcknowledgmentStore? _store;
    private readonly ChangedTestCommandGate? _changedGate;
    private readonly IFlaggedChangeSource? _live;
    private readonly string _agentId;
    private readonly bool _changedTestCommand;
    private readonly Action? _onChanged;

    public ObservableCollection<FlaggedItemRowViewModel> Items { get; } = new();

    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private bool _allAcknowledged;
    [ObservableProperty] private string _resetNotice = "";

    /// <summary>True when this panel's acknowledgments travel to the daemon-side merge gate.</summary>
    public bool IsLive => _live is not null;

    /// <summary>Non-empty when no acknowledgment made here could reach the gate — rendered instead of
    /// letting the controls imply otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnavailableNotice))]
    private string _unavailableNotice = "";

    public bool HasUnavailableNotice => UnavailableNotice.Length > 0;

    /// <param name="store">The branch's per-item acknowledgment ledger (already loaded with its flagged set).</param>
    /// <param name="agentId">The branch/agent id (for the RT-D2 gate ack).</param>
    /// <param name="changedGate">The P2-10 changed-test-command gate (RT-D2), or null when not wired.</param>
    /// <param name="changedTestCommand">True when the branch's resolved test command drifted from main (RT-D2).</param>
    /// <param name="onChanged">Invoked after any ack so the cockpit can re-read <c>CanMerge</c>.</param>
    public FlaggedChangesPanelViewModel(
        AcknowledgmentStore store,
        string agentId,
        ChangedTestCommandGate? changedGate = null,
        bool changedTestCommand = false,
        Action? onChanged = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _agentId = agentId ?? "";
        _changedGate = changedGate;
        _changedTestCommand = changedTestCommand;
        _onChanged = onChanged;
        Refresh();
    }

    /// <summary>The live panel: items and acknowledgments both travel through the daemon seam.</summary>
    /// <param name="agentId">The branch/agent id the daemon addresses its items and acks by.</param>
    /// <param name="live">The daemon-backed flagged-item source + ack route.</param>
    /// <param name="onChanged">Invoked after any ack so the cockpit can re-read <c>CanMerge</c>.</param>
    public FlaggedChangesPanelViewModel(string agentId, IFlaggedChangeSource live, Action? onChanged = null)
    {
        _live = live ?? throw new ArgumentNullException(nameof(live));
        _agentId = agentId ?? "";
        _onChanged = onChanged;
        Refresh();
    }

    public void Refresh()
    {
        if (_live is not null)
        {
            RefreshLive();
            return;
        }

        RefreshLocal();
    }

    // ---- live (daemon) ---------------------------------------------------

    private void RefreshLive()
    {
        var canAck = _live!.CanAcknowledge(out var blockedReason);
        UnavailableNotice = canAck ? "" : blockedReason;

        var items = _live.FlaggedFor(_agentId);

        // Sync in place so an in-flight row's own notice is not thrown away by an unrelated queue push.
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (items.All(f => !string.Equals(f.Id, Items[i].ItemId, StringComparison.Ordinal)))
            {
                Items.RemoveAt(i);
            }
        }

        foreach (var item in items)
        {
            var existing = Items.FirstOrDefault(r => string.Equals(r.ItemId, item.Id, StringComparison.Ordinal));
            if (existing is null)
            {
                Items.Add(new FlaggedItemRowViewModel(
                    item.Id,
                    string.IsNullOrEmpty(item.Path) ? "(verification command)" : item.Path,
                    item.Category,
                    item.Fact,
                    KindOf(item.Id, item.Category),
                    item.Acknowledged,
                    AcknowledgeLiveAsync)
                {
                    AckBlockedReason = canAck ? "" : blockedReason,
                });
            }
            else
            {
                existing.ApplyGateState(item.Acknowledged, canAck ? "" : blockedReason);
            }
        }

        UpdateTotals();
        ResetNotice = "";
    }

    /// <summary>
    /// Projection only — the daemon owns the classification; this names it for the §9.3 glyph.
    ///
    /// <para><b>The kind is read out of the item ID, not out of the category.</b>
    /// <see cref="FlaggedChange.Id"/> is <c>kind|path|contentHash</c>, so the daemon's own
    /// <see cref="FlaggedKind"/> is on the wire verbatim; <c>Category</c> carries a
    /// <see cref="RiskCategory"/> name (<c>Lockfile</c>, <c>CiWorkflow</c>, …), which is a different
    /// enumeration entirely. Parsing THAT as a kind therefore succeeded for exactly one value and fell back
    /// to <see cref="FlaggedKind.RiskCategory"/> for every other — so a daemon-flagged CVE, install-script
    /// or unchecked-advisory row all arrived at the surface labelled as an ordinary risk hunk. Nothing
    /// rendered from it yet, which is why it went unnoticed; a property that quietly answers the wrong
    /// question is one binding away from being read out loud.</para>
    /// </summary>
    private static FlaggedKind KindOf(string itemId, string category)
    {
        if (string.Equals(itemId, LiveChangedTestCommandItemId, StringComparison.Ordinal))
        {
            return FlaggedKind.ChangedTestCommand;
        }

        var bar = (itemId ?? string.Empty).IndexOf('|');
        if (bar > 0 && Enum.TryParse<FlaggedKind>(itemId![..bar], ignoreCase: false, out var fromId))
        {
            return fromId;
        }

        // A category name is not a kind name, but an id that does not carry one leaves nothing better to
        // try than the historical guess.
        return Enum.TryParse<FlaggedKind>(category, ignoreCase: true, out var parsed)
            ? parsed
            : FlaggedKind.RiskCategory;
    }

    /// <summary>The id the daemon's own <c>AcknowledgeFlaggedChange</c> accepts for the RT-D2 gate item.</summary>
    internal const string LiveChangedTestCommandItemId = "changed-test-command";

    private async Task AcknowledgeLiveAsync(FlaggedItemRowViewModel row)
    {
        row.BeginAcknowledging();
        FlaggedAckOutcome outcome;
        try
        {
            outcome = await _live!.AcknowledgeAsync(_agentId, row.ItemId, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Never leaves a row stuck mid-flight, and never draws a checkmark over a gate that is shut.
            outcome = FlaggedAckOutcome.Refused($"the acknowledgment did not reach the merge gate — {ex.Message}");
        }

        row.EndAcknowledging(outcome);
        UpdateTotals();
        _onChanged?.Invoke();
    }

    // ---- local (in-process store + gate) ---------------------------------

    private void RefreshLocal()
    {
        Items.Clear();

        foreach (var item in _store!.Items)
        {
            Items.Add(new FlaggedItemRowViewModel(
                item.Id,
                item.Path,
                item.Category.ToString(),
                item.Detail,
                item.Kind,
                _store.IsAcknowledged(item.Id),
                AcknowledgeStoreItemAsync));
        }

        // The RT-D2 changed-test-command item lives on its own gate (P2-10 owns it), rendered here.
        if (_changedTestCommand && _changedGate is not null)
        {
            var acked = !_changedGate.IsUnacknowledged(_agentId);
            Items.Add(new FlaggedItemRowViewModel(
                LiveChangedTestCommandItemId,
                "(verification command)",
                RiskCategory.ExecutableConfig.ToString(),
                "the test command changed on this branch vs main — a branch cannot self-green",
                FlaggedKind.ChangedTestCommand,
                acked,
                AcknowledgeChangedTestCommandAsync));
        }

        UpdateTotals();
        ResetNotice = _store.LastResetCount > 0
            ? $"The branch changed since you acknowledged — {_store.LastResetCount} item(s) reset."
            : "";
    }

    private Task AcknowledgeStoreItemAsync(FlaggedItemRowViewModel row)
    {
        _store!.Acknowledge(row.ItemId);
        Refresh();
        _onChanged?.Invoke();
        return Task.CompletedTask;
    }

    private Task AcknowledgeChangedTestCommandAsync(FlaggedItemRowViewModel _)
    {
        _changedGate?.Acknowledge(_agentId);
        Refresh();
        _onChanged?.Invoke();
        return Task.CompletedTask;
    }

    private void UpdateTotals()
    {
        var pending = Items.Count(i => !i.IsAcknowledged);
        PendingCount = pending;
        HasItems = Items.Count > 0;
        AllAcknowledged = pending == 0;
    }
}

/// <summary>One flagged item row: severity-first (§9.3 octagon = must-acknowledge), the fact, its own ack.</summary>
public partial class FlaggedItemRowViewModel : ViewModelBase
{
    private readonly Func<FlaggedItemRowViewModel, Task> _onAcknowledge;

    public string ItemId { get; }
    public string Path { get; }
    public string CategoryWord { get; }
    public string Detail { get; }
    public FlaggedKind Kind { get; }

    /// <summary>All flagged items are must-acknowledge → the octagon severity glyph (§9.3 E-family).</summary>
    public string SeverityGlyphKey => "SeverityBlockerIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AckLabel))]
    [NotifyPropertyChangedFor(nameof(CanAck))]
    private bool _isAcknowledged;

    /// <summary>True while the acknowledgment is in flight to the gate (the control is not yet a fact).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AckLabel))]
    [NotifyPropertyChangedFor(nameof(CanAck))]
    private bool _isAcknowledging;

    /// <summary>Non-empty when acknowledging is impossible right now (no repo / no daemon). Panel-wide by
    /// construction, so the panel states it once; the row keeps it for its own gate and tooltip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAck))]
    [NotifyPropertyChangedFor(nameof(AckTooltip))]
    private string _ackBlockedReason = "";

    /// <summary>Non-empty when an acknowledgment was made and the gate did not record it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AckNotice))]
    [NotifyPropertyChangedFor(nameof(HasAckNotice))]
    [NotifyPropertyChangedFor(nameof(AckTooltip))]
    private string _ackProblem = "";

    /// <summary>The ack button label (E4 — a state word, not a bare checkbox).</summary>
    public string AckLabel => IsAcknowledged ? "Acknowledged" : IsAcknowledging ? "Acknowledging…" : "Acknowledge";

    /// <summary>The single gate on the control — computed here, never in the View (rejection trigger).</summary>
    public bool CanAck => !IsAcknowledged && !IsAcknowledging && AckBlockedReason.Length == 0;

    /// <summary>The row's own line: what went wrong with THIS item's acknowledgment.</summary>
    public string AckNotice => AckProblem;

    public bool HasAckNotice => AckNotice.Length > 0;

    /// <summary>What the disabled/enabled control says on hover — the refusal, or why it is unusable.</summary>
    public string AckTooltip => AckProblem.Length > 0 ? AckProblem : AckBlockedReason;

    public FlaggedItemRowViewModel(
        string itemId,
        string path,
        string categoryWord,
        string detail,
        FlaggedKind kind,
        bool isAcknowledged,
        Func<FlaggedItemRowViewModel, Task> onAcknowledge)
    {
        ItemId = itemId;
        Path = path;
        CategoryWord = categoryWord;
        Detail = detail;
        Kind = kind;
        _isAcknowledged = isAcknowledged;
        _onAcknowledge = onAcknowledge;
    }

    internal void BeginAcknowledging()
    {
        AckProblem = "";
        IsAcknowledging = true;
    }

    /// <summary>Applies the gate's OWN answer: acknowledged only when the gate says so, the refusal
    /// otherwise. This is the whole point of the live panel — the call returning is not the evidence.</summary>
    internal void EndAcknowledging(FlaggedAckOutcome outcome)
    {
        IsAcknowledging = false;
        IsAcknowledged = outcome.Acknowledged;
        AckProblem = outcome.Acknowledged ? "" : Refusal(outcome.Reason);
    }

    private static string Refusal(string reason)
        => reason.Length > 0
            ? $"Not acknowledged — {reason}"
            : "Not acknowledged — the merge gate did not record it.";

    /// <summary>Re-applies daemon state to an existing row without discarding an unrelated notice.</summary>
    internal void ApplyGateState(bool acknowledged, string blockedReason)
    {
        AckBlockedReason = blockedReason;
        if (acknowledged)
        {
            IsAcknowledged = true;
            AckProblem = "";
        }
        else if (!IsAcknowledging)
        {
            IsAcknowledged = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAck))]
    private Task AcknowledgeAsync() => _onAcknowledge(this);

    partial void OnIsAcknowledgedChanged(bool value) => AcknowledgeCommand.NotifyCanExecuteChanged();

    partial void OnIsAcknowledgingChanged(bool value) => AcknowledgeCommand.NotifyCanExecuteChanged();

    partial void OnAckBlockedReasonChanged(string value) => AcknowledgeCommand.NotifyCanExecuteChanged();
}
