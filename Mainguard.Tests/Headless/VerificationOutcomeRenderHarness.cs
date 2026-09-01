using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mainguard.Agents.Agents;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.UI.Theming;
using Xunit;

namespace Mainguard.Tests.Headless;

/// <summary>
/// <b>Red is not never-run.</b> The merge-queue rail rendered in every theme with the three verification
/// outcomes side by side, plus the reader that lets a human find out WHY a run failed without paying for a
/// second one.
///
/// <para>The live run this harness is built from: <c>VerificationRows</c> 47 recorded <c>Passed=0</c>,
/// exit 1, <c>subtract(5,3) !== 2</c>. The row said "not verified yet", the worker pane said "no
/// verification record yet", and the real <c>node test.js</c> output existed only in an artifact file
/// nothing linked to. The daemon half gave the state machine a word for that outcome and put the verdict
/// and the log on the wire; this is the half that has to actually show them.</para>
///
/// <para>Every assertion is a fact a human is supposed to be able to read off the surface, and each one
/// pairs the failure case with the control case — a test that only checks the red row would pass just as
/// well if EVERY row said "Tests failed", which is the same defect mirrored.</para>
/// </summary>
public class VerificationOutcomeRenderHarness
{
    private static readonly string[] ThemeKeys = { "MidnightLoom", "DaylightLoom", "Graphite", "Atelier" };

    private const string Red = "6f2a91c7e0d4-4b8e1c05a7f93d2610be84fa7c9d05312";
    private const string Green = "18cc4b0d7ae2-93af6e1b20c74d85af03e19b7d5c6248";
    private const string NeverRun = "d3719ea5b8c0-c04f7a2e18d3496bb15f0c8ae2947d63";

    private static readonly DateTimeOffset RanAt = new(2026, 8, 30, 0, 41, 0, TimeSpan.Zero);

    /// <summary>The daemon's own sentence for the case this harness reproduces — the one the stale pass
    /// was rendering directly above (<c>MergeQueueProvisioner</c>'s conflict block).</summary>
    private const string ConflictDetail =
        "rebasing this branch onto the new main hit a conflict — the agent is paused with the "
        + "rebase in progress and needs a human to resolve it";

    // ---- the defect, stated as tests -------------------------------------

    /// <summary>
    /// <b>The whole point.</b> A branch whose tests failed and a branch nobody ever tested must not read
    /// the same. Before <c>VerificationFailed</c> and the wire verdict they were literally the same value,
    /// and both rows said "not verified yet".
    /// </summary>
    [AvaloniaFact]
    public void AFailedRun_AndANeverRunEntry_DoNotSayTheSameThing()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var vm = Rail(new StubQueue());

        var red = Row(vm, Red);
        var never = Row(vm, NeverRun);

        // The state word, first channel (E4/N-3).
        Assert.Equal("Tests failed", red.StateWord);
        Assert.Equal("Working", never.StateWord);

        // …and the verdict line under it, which is where the FACTS live.
        Assert.Contains("Tests failed", red.Verification.FactsText, StringComparison.Ordinal);
        Assert.Contains("node test.js", red.Verification.FactsText, StringComparison.Ordinal);
        Assert.Contains("Not verified yet", never.Verification.FactsText, StringComparison.Ordinal);

        Assert.NotEqual(red.Verification.FactsText, never.Verification.FactsText);
        Assert.True(red.Verification.IsFailed);
        Assert.False(never.Verification.IsFailed);
        Assert.False(never.Verification.HasRecord);
    }

    /// <summary>
    /// A green record is a third thing, not "the absence of red". Kept as its own flag so a never-run entry
    /// cannot render as a pass by omission — the inverse of the defect, and the more dangerous direction.
    /// </summary>
    [AvaloniaFact]
    public void APassingRun_IsItsOwnOutcome_WithTheCommandThatProducedIt()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var vm = Rail(new StubQueue());

        var green = Row(vm, Green).Verification;
        Assert.True(green.IsPassed);
        Assert.False(green.IsFailed);
        Assert.Contains("Tests passed", green.FactsText, StringComparison.Ordinal);
        // Provenance: a branch that rewrote its own test command produces a green that means nothing.
        Assert.Contains("dotnet test", green.FactsText, StringComparison.Ordinal);

        Assert.False(Row(vm, NeverRun).Verification.IsPassed);
    }

    /// <summary>
    /// <b>A pass the branch has moved out from under must not read as a current green.</b>
    ///
    /// <para>The live defect: an entry whose keep-alive rebase onto the new main had CONFLICTED showed
    /// <c>Tests passed · node test.js · &lt;timestamp&gt;</c> immediately above "rebasing this branch onto
    /// the new main hit a conflict…". Both sentences were true of different moments and the pass was the
    /// one that read as the answer. The record is still a pass — it is not rewritten into a failure — but
    /// it is now stated as what it is: a result about the branch as it WAS.</para>
    ///
    /// <para>The control is the same verdict, same command, same timestamp, on a <c>Verified</c> entry:
    /// without it this would pass just as well if every row were marked stale, which is the same defect
    /// mirrored — and a surface that cries stale everywhere teaches people to ignore the word.</para>
    /// </summary>
    [AvaloniaFact]
    public void AVerdictTheBranchHasMovedOutFromUnder_ReadsAsStale_NotAsACurrentResult()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        var current = Row(Rail(new StubQueue()), Green).Verification;
        Assert.True(current.IsPassed);
        Assert.False(current.IsStale);
        Assert.DoesNotContain("stale", current.FactsText, StringComparison.OrdinalIgnoreCase);

        var conflicted = new StubQueue { GreenBranchConflictedOnRebase = true };
        var stale = Row(Rail(conflicted), Green).Verification;

        // Still a pass: the record is qualified, never rewritten. Turning a green into a red would be a
        // second lie in the other direction.
        Assert.True(stale.IsPassed);
        Assert.False(stale.IsFailed);
        Assert.True(stale.IsStale);

        Assert.Contains("Tests passed", stale.FactsText, StringComparison.Ordinal);
        Assert.Contains("stale", stale.FactsText, StringComparison.OrdinalIgnoreCase);
        // The qualifier is in the verdict clause, not appended after the provenance — a reader who stops
        // at the first line of the row has already been told.
        Assert.Contains("not for the branch as it now stands", stale.FactsText, StringComparison.Ordinal);
        Assert.EndsWith(
            "stale · dotnet test · " + RanAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            stale.FactsText,
            StringComparison.Ordinal);

        // Presentation, not authorisation: the merge was correctly blocked before this change and still is.
        Assert.False(conflicted.CanMerge(Green, out var why));
        Assert.Contains("hit a conflict", why, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second channel for it, in every theme: a stale verdict resolves the theme's own
    /// <c>WarningBrush</c> — and the current pass beside it does not, which is what makes the colour carry
    /// information rather than decorate the row. Daylight Loom is LIGHT; the token is what is compared, so
    /// this holds there too.
    /// </summary>
    [AvaloniaFact]
    public void TheStaleVerdictReadsAsWarning_InEveryTheme_AndOnlyIt()
    {
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);

            var warning = Resource("WarningBrush");
            var muted = Resource("TextMuted");
            var danger = Resource("DangerBrush");
            Assert.NotEqual(warning, muted);  // the theme itself must distinguish them, or this measures nothing
            Assert.NotEqual(warning, danger);

            var currentView = new QueueRailView { DataContext = Rail(new StubQueue()) };
            var currentWin = HostWindow(currentView);
            currentWin.Show();
            Settle();
            Assert.Equal(muted, VerdictColor(currentView, Green));
            HarnessHygiene.Teardown(currentWin);

            var staleView = new QueueRailView
            {
                DataContext = Rail(new StubQueue { GreenBranchConflictedOnRebase = true }),
            };
            var staleWin = HostWindow(staleView);
            staleWin.Show();
            Settle();
            Assert.Equal(warning, VerdictColor(staleView, Green));
            // The other two rows are untouched: a stale marker that repainted the whole rail would be the
            // same failure to distinguish, one layer along.
            Assert.Equal(danger, VerdictColor(staleView, Red));
            Assert.Equal(muted, VerdictColor(staleView, NeverRun));
            HarnessHygiene.Teardown(staleWin);
        }

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    /// <summary>
    /// The second channel, in every theme: the failed verdict resolves the theme's own <c>DangerBrush</c>,
    /// and the other two do not. Checked against the resolved token rather than a hex value, so it holds in
    /// Daylight Loom (which is LIGHT) as well as the dark themes.
    /// </summary>
    [AvaloniaFact]
    public void TheFailedVerdictReadsAsDanger_InEveryTheme_AndOnlyIt()
    {
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);
            var vm = Rail(new StubQueue());
            var view = new QueueRailView { DataContext = vm };
            var win = HostWindow(view);
            win.Show();
            Settle();

            var danger = Resource("DangerBrush");
            var muted = Resource("TextMuted");
            Assert.NotEqual(danger, muted); // the theme itself must distinguish them, or this measures nothing

            Assert.Equal(danger, VerdictColor(view, Red));
            Assert.Equal(muted, VerdictColor(view, Green));
            Assert.Equal(muted, VerdictColor(view, NeverRun));

            HarnessHygiene.Teardown(win);
        }

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    // ---- reading the failure, without re-running it ----------------------

    /// <summary>
    /// <b>Reading is not re-running.</b> Expanding the output must reach <c>GetVerificationLog</c> and must
    /// NOT touch <c>RunVerification</c>: a re-run costs minutes of real jail time and can answer
    /// differently, and charging a human that to find out why something failed is the defect this feature
    /// removes, not a workaround for it.
    /// </summary>
    [AvaloniaFact]
    public async Task ShowingTheOutput_ReadsTheRecordedRun_AndNeverStartsANewOne()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var stub = new StubQueue();
        var vm = Rail(stub);
        var panel = Row(vm, Red).Verification;

        Assert.False(panel.IsExpanded);
        Assert.Empty(stub.LogsRead);

        await panel.ToggleCommand.ExecuteAsync(null);

        Assert.True(panel.IsExpanded);
        Assert.Equal(new[] { Red }, stub.LogsRead);
        Assert.Empty(stub.VerificationsRun);
        Assert.Contains("subtract(5,3) !== 2", panel.LogText, StringComparison.Ordinal);

        // Collapsing and re-opening does not re-ask the daemon: the log is a per-entry file read there.
        await panel.ToggleCommand.ExecuteAsync(null);
        await panel.ToggleCommand.ExecuteAsync(null);
        Assert.Equal(new[] { Red }, stub.LogsRead);
    }

    /// <summary>The expanded log is REACHABLE — rendered and readable — not merely bound. Same lesson as
    /// the lifecycle harness: a control the layout never realizes measures nothing.</summary>
    [AvaloniaFact]
    public async Task TheExpandedLog_IsActuallyOnScreen_AndMonospaced()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var vm = Rail(new StubQueue());
        var view = new QueueRailView { DataContext = vm };
        var win = HostWindow(view);
        win.Show();
        Settle();

        var toggle = Button(view, Red, "Show test output");
        Assert.True(toggle.IsEffectivelyVisible, "the failed row offers no way to read its output");
        Assert.True(toggle.IsEffectivelyEnabled, "the reader is rendered but disabled");
        // Not a second accent — the rail's one emphasized action stays the Review CTA.
        Assert.DoesNotContain("Accent", toggle.Classes);

        await Row(vm, Red).Verification.ToggleCommand.ExecuteAsync(null);
        Settle();

        var log = RowOf(view, Red).GetVisualDescendants().OfType<SelectableTextBlock>()
            .SingleOrDefault(t => t.IsEffectivelyVisible);
        Assert.NotNull(log);
        Assert.Contains("subtract(5,3) !== 2", log!.Text ?? "", StringComparison.Ordinal);
        // Test output is column-aligned; the mono face is what keeps a diff or a stack readable.
        Assert.True(Application.Current!.TryGetResource("FontMono", null, out var mono));
        Assert.Equal(mono, log.FontFamily);

        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// An entry with no record offers no reader. A button whose only possible answer is "there is no
    /// record" is the enabled-control-that-only-errors shape this rail has already shipped once.
    /// </summary>
    [AvaloniaFact]
    public void ANeverRunEntry_OffersNoOutputToRead()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var vm = Rail(new StubQueue());
        var view = new QueueRailView { DataContext = vm };
        var win = HostWindow(view);
        win.Show();
        Settle();

        Assert.DoesNotContain(
            RowOf(view, NeverRun).GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && Equals(b.Content, "Show test output"));
        // The control: the rows that DO have a record keep theirs, so the assertion above is about the
        // record and not about the button never rendering anywhere.
        Assert.True(Button(view, Red, "Show test output").IsEffectivelyVisible);
        Assert.True(Button(view, Green, "Show test output").IsEffectivelyVisible);

        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// The three answers the daemon keeps apart stay apart on the surface. A truncated tail says so; a
    /// missing artifact says so; neither may render as a run that printed nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task ATruncatedTail_AndAMissingArtifact_AreBothSaidOutLoud()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        var truncating = new StubQueue { Truncated = true };
        var truncated = Row(Rail(truncating), Red).Verification;
        await truncated.ToggleCommand.ExecuteAsync(null);
        Assert.Contains("end of a longer log", truncated.LogNotice, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("", truncated.LogText);

        var gone = new StubQueue { UnavailableReason = "the run's output artifact is no longer on disk" };
        var missing = Row(Rail(gone), Red).Verification;
        await missing.ToggleCommand.ExecuteAsync(null);
        Assert.Equal("", missing.LogText);
        Assert.Contains("no longer on disk", missing.LogNotice, StringComparison.Ordinal);
        // The verdict is untouched by the artifact being unreadable — it is still a recorded failure.
        Assert.True(missing.IsFailed);

        var silent = new StubQueue { Log = "" };
        var empty = Row(Rail(silent), Red).Verification;
        await empty.ToggleCommand.ExecuteAsync(null);
        Assert.Contains("no output", empty.LogNotice, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A cached log belongs to the verdict it was fetched for. When the entry verifies again the old output
    /// is dropped rather than left on screen under a newer result — showing a previous run's failure beside
    /// a fresh pass is the same class of lie, one layer along.
    /// </summary>
    [AvaloniaFact]
    public async Task ANewVerdict_DropsTheOutputTheOldOneWasReadFor()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var stub = new StubQueue();
        var vm = Rail(stub);
        var row = Row(vm, Red);

        await row.Verification.ToggleCommand.ExecuteAsync(null);
        Assert.NotEqual("", row.Verification.LogText);

        // The same entry, verified again and now green.
        stub.Repass(Red);
        vm.Refresh();

        Assert.False(row.Verification.IsExpanded);
        Assert.Equal("", row.Verification.LogText);
        Assert.True(row.Verification.IsPassed);
    }

    // ---- the worker pane, the other surface that was lying ---------------

    /// <summary>
    /// The worker pane said "no verification record yet" about the same red branch, and printed
    /// <c>{TestsPassed}/{TestsTotal} tests green</c> for a verified one — counts nothing measures. It now
    /// composes the SAME panel as the row, so the two cannot drift into different answers again.
    /// </summary>
    [AvaloniaFact]
    public void TheWorkerPane_ReadsTheSameVerdictAsTheRow()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        // The pane resolves its agent from the agent service, so the failing entry is pointed at an id
        // the mock actually has — otherwise Refresh returns early and this would assert on a pane that
        // never ran.
        var mock = new Mainguard.Agents.Agents.Mock.MockOrchestrator();
        const string agentId = "loom-1";
        var stub = new StubQueue(redId: agentId);
        var doc = new AgentDocumentViewModel(agentId, mock, stub, mock);
        var row = Row(Rail(stub), agentId);

        Assert.Equal(row.Verification.FactsText, doc.Verification.FactsText);
        Assert.True(doc.Verification.IsFailed);
        Assert.True(doc.Verification.HasRecord);

        // The sha survived the removal of the invented counts beside it — it is the one real fact that
        // line carried, and it is what says whether a verdict was measured against today's main.
        Assert.Contains("a1b2c3d4e5", doc.VerifiedAgainstText, StringComparison.Ordinal);
    }

    // ---- captures --------------------------------------------------------

    /// <summary>The frames to judge this on: the three outcomes at rest and with the failure's output open,
    /// in every theme. Daylight Loom is LIGHT — a failure has to read as a failure there too.</summary>
    [AvaloniaFact]
    public async Task Capture_VerificationOutcomes_AllThemes()
    {
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);

            var vm = Rail(new StubQueue());
            var view = new QueueRailView { DataContext = vm };
            var win = HostWindow(view);
            win.Show();
            Settle();
            Assert.Equal(3, vm.Entries.Count);
            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"verification_outcomes_{theme}.png"));

            await Row(vm, Red).Verification.ToggleCommand.ExecuteAsync(null);
            Settle();
            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"verification_log_open_{theme}.png"));

            HarnessHygiene.Teardown(win);
        }

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    // ---- helpers ---------------------------------------------------------

    private static QueueRailViewModel Rail(StubQueue queue) => new(queue, _ => { });

    private static QueueEntryViewModel Row(QueueRailViewModel vm, string agentId) =>
        vm.Entries.Single(e => e.AgentId == agentId);

    private static Border RowOf(Control view, string agentId) =>
        view.GetVisualDescendants().OfType<Border>()
            .First(b => b.DataContext is QueueEntryViewModel e && e.AgentId == agentId);

    private static Button Button(Control view, string agentId, string content) =>
        RowOf(view, agentId).GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, content));

    /// <summary>The resolved colour of a row's verdict line — the class-driven token, not an inline value.</summary>
    private static Color? VerdictColor(Control view, string agentId)
    {
        var text = RowOf(view, agentId).GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Classes.Contains("verdict"));
        return (text.Foreground as ISolidColorBrush)?.Color;
    }

    private static Color? Resource(string key) =>
        Application.Current!.TryGetResource(key, null, out var v) && v is ISolidColorBrush b ? b.Color : null;

    private static Window HostWindow(Control content)
    {
        var win = new Window { Width = 460, Height = 900, Content = content };
        if (Application.Current!.TryGetResource("SurfaceWindow", null, out var bg) && bg is IBrush brush)
        {
            win.Background = brush;
        }

        return win;
    }

    private static void Settle()
    {
        for (var i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }

    /// <summary>
    /// The three outcomes as the daemon projects them. Not the real <c>MergeQueue</c>: the verdict's
    /// presence is the one fact this rendering turns on, and the stub has to be able to set "failed",
    /// "passed" and "no record at all" independently — which is exactly the distinction under test.
    /// </summary>
    private sealed class StubQueue : IMergeQueueService
    {
        public event Action? Changed;

        /// <summary>Every agent whose recorded output was READ. The pair with <see cref="VerificationsRun"/>
        /// is what proves reading a failure never costs a run.</summary>
        public List<string> LogsRead { get; } = new();

        public List<string> VerificationsRun { get; } = new();

        public string Log { get; set; } =
            "$ node test.js\nAssertionError: subtract(5,3) !== 2\n    at Object.<anonymous> (test.js:14:9)\n";

        public bool Truncated { get; set; }

        public string UnavailableReason { get; set; } = "";

        private bool _redPassedNow;

        /// <summary>Re-verifies the failing entry, green this time — a NEW verdict for the same entry.</summary>
        public void Repass(string agentId) => _redPassedNow = agentId == _red;

        /// <summary>
        /// Puts the GREEN entry where the live defect found it: main moved, the keep-alive rebase onto it
        /// conflicted, and the stale cascade parked the entry back at <c>Working</c> with the conflict as
        /// its reason. The verdict record is untouched — that is the point. Off by default so the
        /// entry count and every existing assertion here stay exactly as they were.
        /// </summary>
        public bool GreenBranchConflictedOnRebase { get; set; }

        public string MainSha => "a1b2c3d4e5";

        /// <summary>Which id carries the failed record. Overridable so the worker-pane test can point it at
        /// an agent that actually exists in the agent service beside it.</summary>
        private readonly string _red;

        public StubQueue(string? redId = null) => _red = redId ?? Red;

        public IReadOnlyList<QueueEntry> GetQueue() => new[]
        {
            GreenBranchConflictedOnRebase
                ? Entry(Green, WorkerMergeState.Working, ConflictDetail,
                    new VerificationVerdict(true, "dotnet test", RanAt))
                : Entry(Green, WorkerMergeState.Verified, "ready to merge",
                    new VerificationVerdict(true, "dotnet test", RanAt)),
            _redPassedNow
                ? Entry(_red, WorkerMergeState.Verified, "ready to merge",
                    new VerificationVerdict(true, "node test.js", RanAt.AddMinutes(9)))
                // The live run, verbatim: a red record, its command, and a gate reason that names it.
                : Entry(_red, WorkerMergeState.VerificationFailed, "tests failed — node test.js",
                    new VerificationVerdict(false, "node test.js", RanAt)),
            // Never verified: no record at all, which is a different answer from a failure and must render
            // as one.
            Entry(NeverRun, WorkerMergeState.Working, "not verified yet", verdict: null),
        };

        private static QueueEntry Entry(
            string id, WorkerMergeState state, string detail, VerificationVerdict? verdict)
            => new(id, id, "agent/" + id, state, detail, verdict,
                FlaggedItems: Array.Empty<FlaggedItem>(), VerificationInFlight: false,
                HasLiveSandbox: true, VerifiedMainSha: verdict is null ? null : "a1b2c3d4e5");

        public bool CanMerge(string agentId, out string reason)
        {
            // The conflicted entry is NOT mergeable, and never was — the authorisation was right all
            // along, which is exactly why the green sentence above it was the whole defect.
            if (agentId == Green && GreenBranchConflictedOnRebase)
            {
                reason = ConflictDetail;
                return false;
            }

            reason = agentId == Green ? "" : "not verified";
            return agentId == Green;
        }

        public Task<VerificationOutcome> RunVerificationAsync(string agentId)
        {
            VerificationsRun.Add(agentId);
            return Task.FromResult(new VerificationOutcome(true, false, "tests failed — node test.js"));
        }

        public Task<VerificationLog> GetVerificationLogAsync(string agentId)
        {
            LogsRead.Add(agentId);
            var entry = GetQueue().Single(e => e.AgentId == agentId);
            if (entry.Verification is not { } v)
            {
                return Task.FromResult(new VerificationLog(false, false, "", "", null, "", false, ""));
            }

            return Task.FromResult(new VerificationLog(
                HasRecord: true, Passed: v.Passed, ResolvedCommand: v.ResolvedCommand, MainSha: MainSha,
                When: v.When, Text: UnavailableReason.Length > 0 ? "" : Log,
                Truncated: Truncated, UnavailableReason: UnavailableReason));
        }

        public Task<MergeOutcome> ConfirmMergeAsync(string agentId) => throw new NotSupportedException();

        public Task AcknowledgeFlaggedChangeAsync(string agentId, string itemId) => Task.CompletedTask;

        public Task<QueueEntryDiscardOutcome> DiscardEntryAsync(string agentId, string reason) =>
            throw new NotSupportedException();

        public Task<QueueEntryRejectOutcome> RejectEntryAsync(string agentId, string reason) =>
            throw new NotSupportedException();

        public Task ClearStalledVerificationAsync(string agentId) => Task.CompletedTask;

        public Task<QueueEntryResumeOutcome> ResumeEntryAsync(string agentId, string agentKind) =>
            throw new NotSupportedException();
    }
}
