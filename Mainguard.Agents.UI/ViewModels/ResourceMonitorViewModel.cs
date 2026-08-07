using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// The task-manager-style resource monitor (revised design 2026-07-11): totals up top,
/// one live row per agent (CPU / RAM / spend / state / task), right-click Pause/Resume and
/// End task (End confirms first — it rejects the work and tears the sandbox down; the branch
/// is kept until teardown, V-5). Readouts tick Still; no accent — telemetry earns silence.
/// </summary>
public partial class ResourceMonitorViewModel : ViewModelBase, IDisposable
{
    private readonly IAgentService _agents;
    private readonly ITelemetryService _telemetry;

    public ObservableCollection<AgentUsageRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _totalsText = "";
    [ObservableProperty] private Points _cpuPoints = new();

    // End-task confirmation (C-1/C-2: the object named, the recoverable stated).
    [ObservableProperty] private bool _isEndConfirmVisible;
    [ObservableProperty] private string _endConfirmTitle = "";
    [ObservableProperty] private string _endConfirmMessage = "";
    private string? _pendingEndAgentId;

    // P2-13 editable per-day budget cap (round-trips through the SetBudgets RPC). The USD field is edited
    // in whole dollars; tokens as an integer. 0 = no cap. The rest of the cap record is preserved on save.
    private SpendBudget _budget = SpendBudget.None;
    [ObservableProperty] private string _perDayUsdCap = "";
    [ObservableProperty] private string _perDayTokenCap = "";
    [ObservableProperty] private string _budgetStatus = "";

    /// <summary>
    /// Whether the cost UI (the per-day cap editor, its Save button, and the <c>spend today $X</c> clause)
    /// means anything right now — true when at least one live agent's spend is actually measurable.
    ///
    /// <para><b>The predicate is metering, not a mode name.</b> "Coordinator mode vs BYOK mode" is the
    /// symptom; the mechanism is that spend is recorded by routing model traffic through Mainguard's
    /// gateway, which requires an API key the daemon can swap for a scoped token. An OAuth-authenticated
    /// agent authenticates <i>past</i> that proxy with a session Mainguard never issued, so its spend is
    /// structurally unmeasurable (<c>docs/design/oauth-budgeting.md</c> records this as an open problem).
    /// BYOK alone is also not sufficient: only <c>claude-code</c> and <c>gemini-cli</c> declare the
    /// <c>baseUrlEnvVar</c>/<c>modelHost</c> pair confinement needs, so a BYOK <c>codex</c>, <c>qwen-code</c>
    /// or <c>opencode</c> agent spends real money unmetered — and the gateway can be switched off entirely.
    /// The daemon already collapses all of those conditions into one fact when it decides whether to issue
    /// a confinement token, so that fact is what travels here rather than a second derivation that could
    /// disagree with the first.</para>
    ///
    /// <para>When false, the cap editor is replaced by a one-line statement rather than silently removed:
    /// an absent control teaches nothing, and the failure this fixes is a UI that quietly implied a
    /// guarantee it did not provide. What must NOT happen is rendering <c>$0.00</c>, which is
    /// indistinguishable from "you have spent nothing".</para>
    /// </summary>
    [ObservableProperty] private bool _isCostVisible;

    /// <summary>
    /// Whether to show the "spend isn't tracked" statement. Deliberately NOT just <c>!IsCostVisible</c>:
    /// with no agents running there are no sessions to make a claim about, and asserting that spend is
    /// not tracked for them would be an unearned statement about nothing — the same species of error as
    /// the <c>$0.00</c> this change removes. Empty means empty; the totals line already says "0 agents".
    /// </summary>
    [ObservableProperty] private bool _isCostNoticeVisible;

    public ResourceMonitorViewModel(IAgentService agents, ITelemetryService telemetry)
    {
        _agents = agents;
        _telemetry = telemetry;
        _telemetry.Sampled += OnSampled;
        Refresh();
        _ = LoadBudgetAsync();
    }

    private async Task LoadBudgetAsync()
    {
        try
        {
            _budget = await _telemetry.GetSpendBudgetAsync();
        }
        catch
        {
            _budget = SpendBudget.None; // daemon unreachable — show empty caps, editing still works once up.
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            PerDayUsdCap = _budget.PerDayUsdMicrosCap > 0
                ? (_budget.PerDayUsdMicrosCap / 1_000_000m).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                : "";
            PerDayTokenCap = _budget.PerDayTokenCap > 0
                ? _budget.PerDayTokenCap.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "";
        });
    }

    [RelayCommand]
    private async Task SaveBudgetAsync()
    {
        long usdMicros = 0;
        if (!string.IsNullOrWhiteSpace(PerDayUsdCap))
        {
            if (!decimal.TryParse(PerDayUsdCap, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var dollars) || dollars < 0)
            {
                BudgetStatus = "Enter a dollar amount (or blank for no cap).";
                return;
            }

            usdMicros = (long)(dollars * 1_000_000m);
        }

        long tokens = 0;
        if (!string.IsNullOrWhiteSpace(PerDayTokenCap))
        {
            if (!long.TryParse(PerDayTokenCap, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out tokens) || tokens < 0)
            {
                BudgetStatus = "Enter a whole number of tokens (or blank for no cap).";
                return;
            }
        }

        // Preserve the per-agent caps; only the per-day caps are edited here.
        var next = _budget with { PerDayUsdMicrosCap = usdMicros, PerDayTokenCap = tokens };
        try
        {
            await _telemetry.SetSpendBudgetAsync(next);
            _budget = next;
            BudgetStatus = "Saved.";
        }
        catch
        {
            BudgetStatus = "Couldn't save — the daemon is unreachable.";
        }
    }

    private void OnSampled() => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        var usage = _telemetry.GetAgentUsage();

        for (int i = Rows.Count - 1; i >= 0; i--)
            if (usage.All(u => u.AgentId != Rows[i].AgentId))
                Rows.RemoveAt(i);
        // Stable order (insertion order, new agents append): reordering live rows would
        // yank an open context menu shut and make targets jump under the cursor.
        foreach (var u in usage)
        {
            var row = Rows.FirstOrDefault(r => r.AgentId == u.AgentId);
            if (row is null) Rows.Add(row = new AgentUsageRowViewModel(u.AgentId, this));
            row.Update(u);
        }

        // Cost UI is shown only where cost is actually measured. The predicate is per-agent metering, not
        // a UI mode name: spend is recorded by routing model traffic through the gateway, which needs an
        // API key to swap for a scoped token. See IsCostVisible.
        IsCostVisible = usage.Any(u => u.IsMetered);
        IsCostNoticeVisible = usage.Count > 0 && !IsCostVisible;

        var total = _telemetry.Current;
        var cpuPart = total.CpuPercent is { } c ? FormattableString.Invariant($"CPU {c:0}%") : $"CPU {AgentUsageRowViewModel.Unknown}";
        var ramPart = total.RamGb is { } r ? FormattableString.Invariant($"RAM {r:0.0} GB") : $"RAM {AgentUsageRowViewModel.Unknown}";
        var parts = new List<string> { cpuPart, ramPart };
        // The spend clause is omitted entirely when nothing is metered — printing "spend today $0.00"
        // there is the false-reassurance shape: indistinguishable from "you have spent nothing".
        if (total.SpendTodayUsd is { } spend)
            parts.Add(FormattableString.Invariant($"spend today ${spend:0.00}"));
        parts.Add(FormattableString.Invariant($"{Rows.Count} agents"));
        TotalsText = string.Join("   ·   ", parts);

        var history = _telemetry.History;
        var points = new Points();
        int n = Math.Min(60, history.Count);
        for (int i = 0; i < n; i++)
        {
            var s = history[history.Count - n + i];
            // An unmeasured point is skipped rather than plotted at the baseline: a gap in the line is
            // honest about missing data, whereas drawing it at 0 invents an idle period.
            if (s.CpuPercent is not { } cpu) continue;
            points.Add(new Point(i * (240.0 / Math.Max(1, n - 1)), 20 - cpu / 100.0 * 20));
        }
        CpuPoints = points;
    }

    // ---- row actions (invoked from the context menu) ----

    public async Task PauseOrResumeAsync(string agentId, bool isPaused)
    {
        if (isPaused) await _agents.ResumeAgentAsync(agentId);
        else await _agents.PauseAgentAsync(agentId);
        Refresh();
    }

    public void RequestEnd(string agentId, string name)
    {
        _pendingEndAgentId = agentId;
        EndConfirmTitle = $"End {name}?";
        EndConfirmMessage = $"{name}'s work is rejected and its sandbox is torn down. " +
                            "Its branch is kept until teardown, so nothing is silently lost.";
        IsEndConfirmVisible = true;
    }

    [RelayCommand]
    private async Task ConfirmEndAsync()
    {
        IsEndConfirmVisible = false;
        if (_pendingEndAgentId is { } id)
        {
            _pendingEndAgentId = null;
            await _agents.EndAgentAsync(id);
            Refresh();
        }
    }

    [RelayCommand]
    private void CancelEnd()
    {
        IsEndConfirmVisible = false;
        _pendingEndAgentId = null;
    }

    public void Dispose() => _telemetry.Sampled -= OnSampled;
}

/// <summary>One agent's live usage row; the context menu drives pause/resume/end.</summary>
public partial class AgentUsageRowViewModel : ViewModelBase
{
    private readonly ResourceMonitorViewModel _owner;

    public string AgentId { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _stateWord = "";
    [ObservableProperty] private string _cpuText = "";
    [ObservableProperty] private string _ramText = "";
    [ObservableProperty] private string _spendText = "";
    /// <summary>Whether this agent's spend is measurable; drives whether the row shows a figure at all.</summary>
    [ObservableProperty] private bool _isMetered;
    /// <summary>Why there is no spend figure. Null for a metered agent (nothing to explain).</summary>
    [ObservableProperty] private string? _spendTooltip;
    [ObservableProperty] private string _task = "";
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _pauseMenuLabel = "Pause";

    /// <summary>The one rendering of "not measured". Shared so a row and the totals line cannot drift
    /// into two different vocabularies for the same fact.</summary>
    public const string Unknown = "—";

    public AgentUsageRowViewModel(string agentId, ResourceMonitorViewModel owner)
    {
        AgentId = agentId;
        _owner = owner;
    }

    public void Update(AgentResourceUsage usage)
    {
        Name = usage.Name;
        StateWord = usage.StateWord;
        // Unknown renders as an em dash, never as 0 — "0%" is a measurement, "—" is the absence of one.
        CpuText = usage.CpuPercent is { } cpu ? FormattableString.Invariant($"{cpu:0}%") : Unknown;
        RamText = usage.RamGb is { } ram ? FormattableString.Invariant($"{ram:0.0} GB") : Unknown;
        IsMetered = usage.IsMetered;
        SpendText = usage.SpendUsd is { } spend ? FormattableString.Invariant($"${spend:0.00}") : Unknown;
        SpendTooltip = usage.IsMetered
            ? null
            : "Spend isn't tracked for this session — metering needs an API key routed through Mainguard's "
              + "model gateway. An OAuth login authenticates directly with the provider, so Mainguard "
              + "never sees the cost.";
        Task = usage.Task;
        IsPaused = usage.IsPaused;
        PauseMenuLabel = usage.IsPaused ? "Resume" : "Pause";
    }

    [RelayCommand]
    private System.Threading.Tasks.Task PauseOrResumeAsync() => _owner.PauseOrResumeAsync(AgentId, IsPaused);

    [RelayCommand]
    private void EndTask() => _owner.RequestEnd(AgentId, Name);
}
