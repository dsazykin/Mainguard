using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Models;
using Mainguard.Git.Review;
using Mainguard.UI.ViewModels;
// The prototype UI plan type (PlanId/Title/Budget/… render fields) — disambiguated from the daemon-side
// Mainguard.Agents.Agents.Orchestrator.TaskPlan (the validated {Scope,Approach,TestStrategy} domain record).
using TaskPlan = Mainguard.Agents.Agents.TaskPlan;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>One viewed-state event for the P2-38 coverage map (emitted now; P2-38 consumes later).</summary>
public sealed record ViewedStateEvent(string Path, int HunkIndex, bool Viewed, DateTimeOffset At);

/// <summary>The composed inputs for one branch's review (keeps the cockpit ctor honest).</summary>
public sealed record ReviewCockpitContext(
    string AgentId,
    string Name,
    string Branch,
    IReadOnlyList<FilePatch> MergeDiff)
{
    /// <summary>Agent-Trace ranges for provenance chips (null/empty → trailer fallback or no chip).</summary>
    public IReadOnlyList<AgentTraceRange>? TraceRanges { get; init; }

    /// <summary>Whole-branch trailer provenance used when no Agent-Trace range covers a hunk.</summary>
    public HunkProvenance? TrailerFallback { get; init; }

    /// <summary>The managed worker's approved plan (F6 scope); null for a plan-less manual run.</summary>
    public TaskPlan? ApprovedPlan { get; init; }

    /// <summary>True for a managed worker (F6 scope comparison applies).</summary>
    public bool Managed { get; init; }

    /// <summary>
    /// Extra flagged items from the semantic lockfile diff (CVE / install-script / advisory-unknown rows).
    ///
    /// <para><b>Local composition only — setting this in the shipped app changes nothing.</b> It is read
    /// exactly once, on the <c>live is null</c> branch of <see cref="ReviewCockpitViewModel"/>'s
    /// constructor, and production always supplies <c>live:</c> (see
    /// <c>ControlCenterViewModel.OpenReviewAsync</c>). So this property reaches the design/render harness
    /// and the pure-composition tests, and nothing else.</para>
    ///
    /// <para><b>The production path is daemon-side and already wired</b>:
    /// <c>MergeQueueProvisioner.ReviewLockfiles</c> reads both full manifests out of the mirror at
    /// verification time and folds the rows into the <see cref="AcknowledgmentStore"/> the merge gate
    /// actually consults; they reach this cockpit through the same daemon projection as every other
    /// flagged item. That is not an alternative to filling this property in — it is the only version that
    /// blocks a merge, because a lockfile is not parseable from a truncated diff hunk and an
    /// acknowledgment addressed to a locally-composed id would clear nothing.</para>
    /// </summary>
    public IReadOnlyList<FlaggedChange>? LockfileFlags { get; init; }

    /// <summary>RT-D2: the branch's resolved test command drifted from the main baseline.</summary>
    public bool ChangedTestCommand { get; init; }

    /// <summary>The test-delta strip data (branch vs latest main baseline).</summary>
    public TestDelta? TestDelta { get; init; }

    /// <summary>The short sha the branch was verified against (freshness at a glance).</summary>
    public string? VerifiedAgainstSha { get; init; }
}

/// <summary>
/// The Review Cockpit (P2-11 / ControlCenterDesign §6) — "where trust is manufactured". Composes the
/// shipped diff stack into a file/hunk list <b>ordered by risk rank</b> (ordering only — nothing hidden,
/// invariant 3), a per-hunk provenance chip (Agent-Trace first, trailers fallback), the pinned flagged-
/// changes gate panel (item-by-item acks), and the test-delta strip. The merge button is bound to the real
/// <see cref="MergeQueue.CanMerge"/>; "bring this branch local" hands the branch back into a T-29 worktree.
/// This View-model renders and composes — every rule (classify/flag/ack) lives in pure Core (invariant 1).
/// </summary>
public partial class ReviewCockpitViewModel : ViewModelBase
{
    private readonly ReviewCockpitContext _ctx;
    private readonly MergeQueue? _queue;
    private readonly Services.IFlaggedChangeSource? _live;
    private readonly Func<string, CancellationToken, Task>? _bringLocal;
    private readonly Action<string>? _onMerge;
    private readonly List<ReviewHunkRowViewModel> _flatHunks = new();

    public ObservableCollection<ReviewFileRowViewModel> Files { get; } = new();
    public FlaggedChangesPanelViewModel FlaggedPanel { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _verifiedText = "";
    [ObservableProperty] private string _testDeltaSummary = "";
    [ObservableProperty] private bool _canMerge;
    [ObservableProperty] private string _mergeReason = "";
    [ObservableProperty] private bool _isEmpty;

    // Review-sprint mode (§3.7).
    [ObservableProperty] private bool _sprintActive;
    [ObservableProperty] private int _sprintIndex = -1;
    [ObservableProperty] private int _sprintRiskBudget;
    [ObservableProperty] private int _sprintViewedCount;
    [ObservableProperty] private string _sprintStatus = "";

    private readonly List<ViewedStateEvent> _viewedEvents = new();

    /// <summary>The viewed-state events emitted this session (unviewed for deferred hunks — P2-38 consumes).</summary>
    public IReadOnlyList<ViewedStateEvent> ViewedEvents => _viewedEvents;

    /// <summary>Total hunks in the merge diff. Equals <see cref="RenderedHunkCount"/> — ordering never hides (invariant 3).</summary>
    public int TotalHunkCount { get; private set; }

    /// <summary>Total hunks actually rendered across all file rows.</summary>
    public int RenderedHunkCount => Files.Sum(f => f.Hunks.Count);

    /// <param name="ctx">The composed review inputs.</param>
    /// <param name="flaggedGate">The P2-11 flagged gate (its per-agent store is loaded here); null builds a private one.</param>
    /// <param name="changedGate">The P2-10 RT-D2 changed-test-command gate (rendered/acked here).</param>
    /// <param name="queue">The in-process merge queue (drives <c>CanMerge</c>); null leaves merge disabled.</param>
    /// <param name="bringLocal">T-29 fetch-into-worktree hand-back (agentId, ct).</param>
    /// <param name="onMerge">The human foreground merge action (agentId).</param>
    /// <param name="live">
    /// The daemon-backed flagged-item source + ack route. <b>When supplied it replaces the local gates
    /// entirely</b> — the flagged panel renders the daemon's items and its acknowledgments go to the
    /// daemon-side gate that actually blocks the merge, and <c>CanMerge</c> is read from the same place.
    /// <para>The overlay used to be built with none of this: no queue, no changed-test-command gate, and a
    /// private in-process <c>AcknowledgmentStore</c>. It surfaced no daemon-flagged item at all, and had it
    /// surfaced one, the checkmark would have cleared a store no merge consults — telling a human they had
    /// unblocked a merge that was still blocked. Leaving <paramref name="live"/> null keeps the local
    /// composition, which is honest only where that store IS the gate (the design/render harness).</para>
    /// </param>
    public ReviewCockpitViewModel(
        ReviewCockpitContext ctx,
        FlaggedChangeGate? flaggedGate = null,
        ChangedTestCommandGate? changedGate = null,
        MergeQueue? queue = null,
        Func<string, CancellationToken, Task>? bringLocal = null,
        Action<string>? onMerge = null,
        Services.IFlaggedChangeSource? live = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _queue = queue;
        _live = live;
        _bringLocal = bringLocal;
        _onMerge = onMerge;

        if (live is not null)
        {
            // No shadow store is built: a second, local ledger of the same items is exactly how a surface
            // ends up disagreeing with the gate it is supposed to be rendering.
            FlaggedPanel = new FlaggedChangesPanelViewModel(ctx.AgentId, live, RefreshGate);
        }
        else
        {
            flaggedGate ??= new FlaggedChangeGate();
            var store = flaggedGate.StoreFor(ctx.AgentId);

            // Compose the flagged set from pure Core: risk hunks + F6 scope + lockfile rows.
            var items = new List<FlaggedChange>(FlaggedChangeDetector.DetectFlagged(ctx.MergeDiff, ctx.ApprovedPlan, ctx.Managed));
            if (ctx.LockfileFlags is { Count: > 0 })
            {
                items.AddRange(ctx.LockfileFlags);
            }

            store.SetFlagged(items);
            changedGate?.SetFlagged(ctx.AgentId, ctx.ChangedTestCommand);

            FlaggedPanel = new FlaggedChangesPanelViewModel(store, ctx.AgentId, changedGate, ctx.ChangedTestCommand, RefreshGate);
        }

        BuildRows();
        BuildHeader();
        RefreshGate();
    }

    /// <summary>
    /// Re-reads the daemon projection (items + their acknowledged flags + the gate's merge answer). The
    /// surface is driven by the queue stream, and an acknowledgment moves no queue state — the daemon
    /// re-pushes the stream after one specifically so this refresh has something to read.
    /// </summary>
    public void RefreshFromQueue()
    {
        if (_live is null)
        {
            return;
        }

        FlaggedPanel.Refresh();
        RefreshGate();
    }

    private void BuildHeader()
    {
        Title = $"Review — {_ctx.Name} · {_ctx.Branch} → main";

        if (_ctx.VerifiedAgainstSha is { Length: > 0 } sha)
        {
            VerifiedText = $"verified @ {Short(sha)}";
        }

        if (_ctx.TestDelta is { } delta)
        {
            var newPart = delta.NewPasses.Count > 0 ? $" (+{delta.NewPasses.Count} new)" : "";
            var cmd = _ctx.ChangedTestCommand
                ? "test command changed on this branch — flagged below"
                : "command unchanged from main";
            TestDeltaSummary = $"{delta.PassedCurrent} green{newPart} · {delta.FailedCurrent} failed · {cmd}";
        }
        else if (_ctx.ChangedTestCommand)
        {
            // The wire carries the changed-command FACT (a flagged item) without the run counts —
            // render the warning alone rather than hiding it behind an absent delta. This is the one
            // header line a reviewer must not miss: the branch's green was measured by a command the
            // branch itself rewrote.
            TestDeltaSummary = "test command changed on this branch — flagged below";
        }
    }

    private void BuildRows()
    {
        Files.Clear();
        _flatHunks.Clear();
        TotalHunkCount = 0;

        var rows = new List<ReviewFileRowViewModel>();
        foreach (var patch in _ctx.MergeDiff)
        {
            var path = FilePatchPath.NewPath(patch);
            var fileRow = new ReviewFileRowViewModel(path);

            RiskCategory topCategory = RiskCategory.Docs;
            var seen = false;
            for (var i = 0; i < patch.Hunks.Count; i++)
            {
                var hunk = patch.Hunks[i];
                var risk = RiskClassifier.Classify(path, hunk);
                if (!seen || risk.Rank < RiskClassifier.RankOf(topCategory))
                {
                    topCategory = risk.Category;
                    seen = true;
                }

                var provenance = ResolveProvenance(path, hunk);
                var hunkRow = new ReviewHunkRowViewModel(path, i, risk.Category, hunk, provenance);
                fileRow.Hunks.Add(hunkRow);
                _flatHunks.Add(hunkRow);
                TotalHunkCount++;
            }

            if (!seen)
            {
                topCategory = RiskClassifier.Classify(path, new DiffHunk()).Category;
            }

            fileRow.SetCategory(topCategory);
            rows.Add(fileRow);
        }

        // Order files by their highest-risk (lowest-rank) hunk — the list IS the review plan (§6.2).
        foreach (var row in rows
                     .OrderBy(r => RiskClassifier.RankOf(r.Category))
                     .ThenBy(r => r.Path, StringComparer.Ordinal))
        {
            Files.Add(row);
        }

        IsEmpty = Files.Count == 0;
    }

    private HunkProvenance? ResolveProvenance(string path, DiffHunk hunk)
    {
        if (_ctx.TraceRanges is { Count: > 0 })
        {
            var fromTrace = ProvenanceReader.ForHunk(_ctx.TraceRanges, path, hunk);
            if (fromTrace is not null)
            {
                return fromTrace;
            }
        }

        return _ctx.TrailerFallback; // may be null → the hunk simply has no chip (honest absence, V-6).
    }

    private void RefreshGate()
    {
        // The daemon's gate is the one that blocks the merge, so when it is bound it is the only answer.
        if (_live is not null)
        {
            CanMerge = _live.CanMerge(_ctx.AgentId, out var liveReason);
            MergeReason = CanMerge ? "ready to merge" : liveReason;
            MergeCommand.NotifyCanExecuteChanged();
            return;
        }

        if (_queue is null)
        {
            CanMerge = false;
            MergeReason = FlaggedPanel.AllAcknowledged ? "no queue bound" : $"{FlaggedPanel.PendingCount} flagged change(s) need acknowledgment";
            MergeCommand.NotifyCanExecuteChanged();
            return;
        }

        CanMerge = _queue.CanMerge(_ctx.AgentId, out var reason);
        MergeReason = CanMerge ? "ready to merge" : reason;
        MergeCommand.NotifyCanExecuteChanged();
    }

    // ---- Commands --------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanMerge))]
    private void Merge() => _onMerge?.Invoke(_ctx.AgentId);

    [RelayCommand]
    private async Task BringBranchLocalAsync(CancellationToken ct)
    {
        if (_bringLocal is not null)
        {
            await _bringLocal(_ctx.AgentId, ct).ConfigureAwait(false);
        }
    }

    // ---- Review-sprint mode (§3.7) --------------------------------------

    /// <summary>Starts a keyboard-only ranked pass with a per-session risk budget (sum of hunk ranks reviewed).</summary>
    public void StartReviewSprint(int riskBudget)
    {
        _viewedEvents.Clear();
        foreach (var hunk in _flatHunks)
        {
            hunk.ResetSprint();
        }

        SprintActive = true;
        SprintRiskBudget = riskBudget;
        SprintViewedCount = 0;
        SprintIndex = _flatHunks.Count > 0 ? 0 : -1;
        UpdateSprintStatus();
    }

    /// <summary>j — advance to the next ranked hunk.</summary>
    public void SprintNext()
    {
        if (SprintActive && SprintIndex < _flatHunks.Count - 1)
        {
            SprintIndex++;
            UpdateSprintStatus();
        }
    }

    /// <summary>k — go to the previous ranked hunk.</summary>
    public void SprintPrev()
    {
        if (SprintActive && SprintIndex > 0)
        {
            SprintIndex--;
            UpdateSprintStatus();
        }
    }

    /// <summary>space — mark the current hunk viewed (consumes its rank cost from the budget).</summary>
    public void SprintMarkViewed()
    {
        if (!SprintActive || SprintIndex < 0 || SprintIndex >= _flatHunks.Count)
        {
            return;
        }

        var hunk = _flatHunks[SprintIndex];
        if (!hunk.Viewed)
        {
            hunk.Viewed = true;
            SprintViewedCount++;
            _viewedEvents.Add(new ViewedStateEvent(hunk.Path, hunk.HunkIndex, true, DateTimeOffset.UtcNow));
        }

        UpdateSprintStatus();
    }

    /// <summary>a — acknowledge the current hunk's flagged item, if any.</summary>
    public void SprintAcknowledge()
    {
        if (!SprintActive || SprintIndex < 0 || SprintIndex >= _flatHunks.Count)
        {
            return;
        }

        var hunk = _flatHunks[SprintIndex];
        var item = FlaggedPanel.Items.FirstOrDefault(i => i.Path == hunk.Path && !i.IsAcknowledged);
        if (item is not null)
        {
            item.AcknowledgeCommand.Execute(null);
        }
    }

    /// <summary>Ends the sprint: every not-viewed hunk is recorded as an <b>unviewed</b> event for P2-38.</summary>
    public void EndReviewSprint()
    {
        foreach (var hunk in _flatHunks.Where(h => !h.Viewed))
        {
            _viewedEvents.Add(new ViewedStateEvent(hunk.Path, hunk.HunkIndex, false, DateTimeOffset.UtcNow));
        }

        SprintActive = false;
        UpdateSprintStatus();
    }

    private void UpdateSprintStatus()
    {
        var spent = _flatHunks.Where(h => h.Viewed).Sum(h => RiskClassifier.RankOf(h.Category) == 0 ? 1 : RiskClassifier.RankOf(h.Category));
        SprintStatus = SprintActive
            ? $"{SprintViewedCount} of {_flatHunks.Count} hunks viewed · budget {Math.Max(0, SprintRiskBudget - spent)} left"
            : $"{_flatHunks.Count(h => h.Viewed)} of {_flatHunks.Count} hunks viewed";
    }

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "—" : (sha.Length > 7 ? sha[..7] : sha);
}

/// <summary>One risk-ranked file row: its top category (severity glyph second channel) + its hunks.</summary>
public partial class ReviewFileRowViewModel : ViewModelBase
{
    public string Path { get; }
    public ObservableCollection<ReviewHunkRowViewModel> Hunks { get; } = new();

    [ObservableProperty] private RiskCategory _category;
    [ObservableProperty] private string _categoryWord = "";
    [ObservableProperty] private string _severityGlyphKey = "SeverityInfoIcon";
    [ObservableProperty] private bool _isMustAck;
    [ObservableProperty] private bool _isRisky;
    [ObservableProperty] private bool _isInformational;

    public ReviewFileRowViewModel(string path) => Path = path;

    public void SetCategory(RiskCategory category)
    {
        Category = category;
        CategoryWord = SeverityVocabulary.Word(category);
        (SeverityGlyphKey, IsMustAck, IsRisky, IsInformational) = SeverityVocabulary.Glyph(category);
    }
}

/// <summary>One hunk row: its risk category, provenance chip text, and sprint viewed-state.</summary>
public partial class ReviewHunkRowViewModel : ViewModelBase
{
    public string Path { get; }
    public int HunkIndex { get; }
    public RiskCategory Category { get; }
    public string CategoryWord { get; }
    public DiffHunk Hunk { get; }

    /// <summary>The provenance chip text (⑂ agent · task · sha), or empty when no provenance (no chip, V-6).</summary>
    public string ProvenanceChip { get; }

    /// <summary>Tooltip that marks a trailer-sourced chip's origin (TT-3 — provenance of the provenance).</summary>
    public string ProvenanceTooltip { get; }

    public bool HasProvenance => ProvenanceChip.Length > 0;

    [ObservableProperty] private bool _viewed;

    public ReviewHunkRowViewModel(string path, int hunkIndex, RiskCategory category, DiffHunk hunk, HunkProvenance? provenance)
    {
        Path = path;
        HunkIndex = hunkIndex;
        Category = category;
        CategoryWord = SeverityVocabulary.Word(category);
        Hunk = hunk;

        if (provenance is not null && (provenance.Agent is not null || provenance.Task is not null || provenance.Plan is not null))
        {
            var parts = new List<string>();
            if (provenance.Agent is { Length: > 0 } a) parts.Add(a);
            if (provenance.Task is { Length: > 0 } t) parts.Add("task " + t);
            if (provenance.Sha is { Length: > 0 } s) parts.Add(s.Length > 7 ? s[..7] : s);
            ProvenanceChip = "⑂ " + string.Join(" · ", parts);
            ProvenanceTooltip = provenance.Source == "trailer"
                ? "provenance from commit trailers"
                : "provenance from Agent Trace";
        }
        else
        {
            ProvenanceChip = "";
            ProvenanceTooltip = "";
        }
    }

    public void ResetSprint() => Viewed = false;
}

/// <summary>The §9.3 severity vocabulary mapping (rendering-only projection of the pure risk category).</summary>
internal static class SeverityVocabulary
{
    public static string Word(RiskCategory category) => category switch
    {
        RiskCategory.ExecutableConfig => "Executable config",
        RiskCategory.CiWorkflow => "CI workflow",
        RiskCategory.GitHooks => "Git hook",
        RiskCategory.EditorConfig => "Editor config",
        RiskCategory.SecuritySensitivePath => "Security-sensitive",
        RiskCategory.Lockfile => "Dependencies",
        RiskCategory.Source => "Source",
        RiskCategory.Docs => "Docs",
        _ => category.ToString(),
    };

    // octagon (SeverityBlockerIcon) = must-ack, triangle (WarningIcon) = risky, circle (SeverityInfoIcon) = informational.
    public static (string Glyph, bool MustAck, bool Risky, bool Info) Glyph(RiskCategory category) => category switch
    {
        RiskCategory.ExecutableConfig or RiskCategory.CiWorkflow or RiskCategory.GitHooks or RiskCategory.SecuritySensitivePath
            => ("SeverityBlockerIcon", true, false, false),
        RiskCategory.Lockfile or RiskCategory.EditorConfig
            => ("WarningIcon", false, true, false),
        _ => ("SeverityInfoIcon", false, false, true),
    };
}
