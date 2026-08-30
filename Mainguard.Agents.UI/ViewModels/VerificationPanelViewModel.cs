using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// One entry's verification <b>verdict, and its output on demand</b> (H4). Composed into both surfaces
/// that were lying about a failed run — the merge-queue row and the worker pane — so the two cannot drift
/// into saying different things about the same record.
///
/// <para><b>Three outcomes, never two.</b> Green, red, and never-run are distinct states here, matching the
/// wire's <c>optional</c> verdict. The defect this exists to fix was exactly the collapse of red into
/// never-run: a branch whose <c>node test.js</c> had just failed rendered as "not verified yet", with the
/// only route to the truth being a second, paid, verification run.</para>
///
/// <para><b>The log is fetched on demand and cached.</b> Never on a queue refresh: the rail re-projects on
/// every daemon event and every entry would then be a file read on the daemon. Expanding is one RPC; the
/// result is held until the verdict itself changes, at which point it is dropped rather than shown against
/// a newer run it did not come from.</para>
///
/// <para><b>Reading is not re-running.</b> This never calls <c>RunVerification</c>. That distinction is the
/// point of the whole feature — a re-run costs minutes of real jail time and can answer differently, so it
/// must never be the price of finding out why something failed.</para>
/// </summary>
public partial class VerificationPanelViewModel : ViewModelBase
{
    private readonly IMergeQueueService _queue;

    /// <summary>The verdict the currently-held <see cref="LogText"/> was fetched for. When the entry's
    /// verdict changes the cached log is dropped: showing a previous run's output under a new verdict is
    /// the same class of lie as the one this change removes.</summary>
    private VerificationVerdict? _loadedFor;

    /// <summary>The verdict the last <see cref="Update"/> reported, so a completed fetch can record which
    /// run its text belongs to.</summary>
    private VerificationVerdict? _verdict;

    private bool _hasLoaded;

    public string AgentId { get; }

    public VerificationPanelViewModel(string agentId, IMergeQueueService queue)
    {
        AgentId = agentId;
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    /// <summary>True when the daemon reported a verdict for this entry at all. False is <b>never run</b> —
    /// materially different from a red one, and the only state with nothing to read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleButtonText))]
    private bool _hasRecord;

    /// <summary>The red verdict. Drives the danger token on the facts line — the row's one honest cue that
    /// this branch is not merely untested.</summary>
    [ObservableProperty] private bool _isFailed;

    /// <summary>The green verdict, kept as its own flag rather than <c>!IsFailed</c> so a never-run entry
    /// cannot render as a pass by omission.</summary>
    [ObservableProperty] private bool _isPassed;

    /// <summary>
    /// The verdict as a sentence: what happened, what command decided it, and when. The command is
    /// provenance and not decoration — a branch that rewrote its own test command produces a green that
    /// means nothing, and a reviewer has to see WHAT ran.
    /// </summary>
    [ObservableProperty] private string _factsText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleButtonText))]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleButtonText))]
    private bool _isLoading;

    /// <summary>The fetched output, already sanitized at the projection boundary.</summary>
    [ObservableProperty] private string _logText = "";

    /// <summary>What the surface must say ABOUT the log rather than in it: that this is the end of a longer
    /// artifact, that the artifact is gone, that the daemon could not be asked, or that the run genuinely
    /// printed nothing. Each of those is a different fact and none may render as any of the others.</summary>
    [ObservableProperty] private string _logNotice = "";

    public string ToggleButtonText => IsLoading ? "Loading…" : IsExpanded ? "Hide test output" : "Show test output";

    /// <summary>
    /// Takes the entry's verdict from the queue projection. Renders the three-way outcome and invalidates a
    /// cached log whose verdict no longer matches.
    /// </summary>
    public void Update(QueueEntry? entry)
    {
        var verdict = entry?.Verification;
        _verdict = verdict;

        if (!Equals(verdict, _loadedFor))
        {
            // A new (or removed) verdict: whatever was on screen described the previous run.
            _hasLoaded = false;
            _loadedFor = null;
            LogText = "";
            LogNotice = "";
            IsExpanded = false;
        }

        HasRecord = verdict is not null;
        IsFailed = verdict is { Passed: false };
        IsPassed = verdict is { Passed: true };

        if (verdict is null)
        {
            // Said plainly, and said as a fact about the ENTRY rather than about the tests. "Not verified
            // yet" was the exact sentence a failed run used to produce, which is why it now has to be
            // unmistakably about the absence of a run.
            FactsText = "Not verified yet — no test run has been recorded for this branch.";
            return;
        }

        var parts = verdict.Passed ? "Tests passed" : "Tests failed";
        if (!string.IsNullOrWhiteSpace(verdict.ResolvedCommand))
        {
            parts += " · " + verdict.ResolvedCommand;
        }

        if (verdict.When is { } when)
        {
            parts += " · " + when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        FactsText = parts;
    }

    /// <summary>
    /// Opens or closes the output, fetching it the first time. <b>Runs no verification</b> — see the class
    /// remarks; a refusal or an unreadable artifact is reported in <see cref="LogNotice"/> rather than
    /// silently leaving an empty box, which would read as a run that printed nothing.
    /// </summary>
    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (IsLoading)
        {
            return;
        }

        if (IsExpanded)
        {
            IsExpanded = false;
            return;
        }

        IsExpanded = true;
        if (_hasLoaded)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var log = await _queue.GetVerificationLogAsync(AgentId).ConfigureAwait(true);
            Apply(log);
            _hasLoaded = true;
            _loadedFor = _verdict;
        }
        catch (Exception ex)
        {
            // Reported, never swallowed. An empty expander is indistinguishable from a suite with no output.
            LogText = "";
            LogNotice = "Couldn't read this run's output — " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Apply(VerificationLog log)
    {
        if (!log.HasRecord)
        {
            // The daemon and the queue projection disagree — possible, since they are separate reads. Say
            // so instead of showing an empty box under a verdict line that claims a run happened.
            LogText = "";
            LogNotice = "The daemon has no verification record for this entry.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(log.UnavailableReason))
        {
            LogText = "";
            LogNotice = Capitalize(log.UnavailableReason) + ".";
            return;
        }

        LogText = log.Text;
        LogNotice = log.Text.Length == 0
            ? "The run produced no output."
            : log.Truncated
                ? "Showing the end of a longer log — the run printed more than is kept."
                : "";
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
