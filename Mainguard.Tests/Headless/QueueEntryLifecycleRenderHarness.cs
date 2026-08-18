using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// The merge-queue rail's <b>entry lifecycle actions</b>, rendered in every theme.
///
/// <para>The reported defect: entries left behind by agents that are gone "just sit there" with nothing
/// the human can do to them. The rail offered exactly one control — Review — shown only for the
/// front-most <c>Verified</c> entry, so a stranded <c>Working</c> row had no affordance at all.</para>
///
/// <para>Every assertion here is about a control being <b>reachable</b>, not merely present: this
/// repository has already shipped a test that asserted a button's visibility when the defect was that it
/// was disabled, so each one checks <c>IsEffectivelyVisible</c> AND <c>IsEffectivelyEnabled</c>. The
/// design constraint is asserted too — the rail's ONE accent stays the Review CTA, so the destructive
/// action must read destructive without becoming a second accent.</para>
/// </summary>
public class QueueEntryLifecycleRenderHarness
{
    private static readonly string[] ThemeKeys = { "MidnightLoom", "DaylightLoom", "Graphite", "Atelier" };

    // Production-length ids: DaemonBackedOrchestrator projects Name = AgentId and Branch = agent/<id>,
    // and the owner's own entries look like this.
    private const string Stranded = "1deb19131adb-ef9fe0bd3390433193896eca5e46145e";
    private const string Frozen = "7c41a9f0e2b3-11d8ac6e5f9042bb1e7c3a8d6042f19b";
    private const string Live = "b2e7d1c4a8f6-33ea9b0d7c1245fe8a2b6d4c9e30f57a";
    private const string Ready = "e9f3b7a1c0d2-58bc4e2a9f7136dd0c5e8b3a1f742d69";

    /// <summary>
    /// Every non-terminal row can be acted on, and each action means one thing:
    /// Discard everywhere, "Clear stalled run" only where the DAEMON says no run is live.
    /// </summary>
    [AvaloniaFact]
    public void EveryNonTerminalRow_HasAReachableDiscard_AndOnlyTheStalledRowOffersTheClear()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var vm = new QueueRailViewModel(new StubQueue(), _ => { });
        var view = new QueueRailView { DataContext = vm };
        var win = HostWindow(view);
        win.Show();
        Settle();

        Assert.Equal(4, vm.Entries.Count);

        foreach (var entry in vm.Entries)
        {
            var discard = Button(view, entry.AgentId, "Discard");
            Assert.True(discard.IsEffectivelyVisible, $"{entry.AgentId}: no Discard control at all");
            // The lesson from the last instrument that measured nothing: a visible-but-disabled control
            // is exactly as unusable as an absent one, and only the second assertion catches it.
            Assert.True(discard.IsEffectivelyEnabled, $"{entry.AgentId}: Discard is rendered but disabled");
        }

        // "Clear stalled run" belongs to the row whose Verifying state has nothing behind it — and to no
        // other row, including the one that IS verifying. Inferring it from the state alone would light it
        // up on both.
        Assert.True(Button(view, Frozen, "Clear stalled run").IsEffectivelyVisible);
        Assert.True(Button(view, Frozen, "Clear stalled run").IsEffectivelyEnabled);
        Assert.False(Button(view, Live, "Clear stalled run").IsEffectivelyVisible);
        Assert.False(Button(view, Stranded, "Clear stalled run").IsEffectivelyVisible);

        // The stalled row says what it is, rather than claiming an activity that is not happening.
        Assert.Equal("Stalled", vm.Entries.Single(e => e.AgentId == Frozen).StateWord);
        Assert.Equal("Verifying", vm.Entries.Single(e => e.AgentId == Live).StateWord);

        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// The accent budget. The rail's single emphasized action stays Review; the destructive action reads
    /// destructive by hue (Button.DangerQuiet) and must not be a filled accent competing with it.
    /// </summary>
    [AvaloniaFact]
    public void TheRailKeepsExactlyOneAccent_AndTheDestructiveActionIsNotIt()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var vm = new QueueRailViewModel(new StubQueue(), _ => { });
        var view = new QueueRailView { DataContext = vm };
        var win = HostWindow(view);
        win.Show();
        Settle();

        var accents = view.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.Classes.Contains("Accent"))
            .ToList();
        var accent = Assert.Single(accents);
        Assert.Equal("Review", accent.Content);

        var discards = view.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && Equals(b.Content, "Discard"))
            .ToList();
        Assert.Equal(4, discards.Count);
        foreach (var d in discards)
        {
            Assert.Contains("DangerQuiet", d.Classes);
            Assert.DoesNotContain("Accent", d.Classes);
            // Destructive by hue: the class resolves the danger token, not a raw colour and not the
            // accent's fill. Compared against the theme's own token so this holds in every theme.
            Assert.Equal(Resource("DangerBrush"), (d.Foreground as ISolidColorBrush)?.Color);
            Assert.NotEqual(Resource("AccentBrush"), (d.Background as ISolidColorBrush)?.Color);
        }

        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// The two-step guard: pressing Discard asks before anything is dropped, and the question states the
    /// two facts a vanishing queue entry is otherwise ambiguous about — that it is NOT a merge, and that
    /// the branch survives.
    /// </summary>
    [AvaloniaFact]
    public void Discard_AsksFirst_AndTheQuestionSaysItIsNotAMerge()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var stub = new StubQueue();
        var vm = new QueueRailViewModel(stub, _ => { });
        var view = new QueueRailView { DataContext = vm };
        var win = HostWindow(view);
        win.Show();
        Settle();

        var row = vm.Entries.Single(e => e.AgentId == Stranded);
        row.BeginDiscardCommand.Execute(null);
        Settle();

        // Nothing has been asked of the queue yet — arming is not acting.
        Assert.Empty(stub.Discarded);

        var prompt = view.GetVisualDescendants().OfType<TextBlock>()
            .SingleOrDefault(t => t.IsEffectivelyVisible && (t.Text ?? "").Contains("Drop this entry"));
        Assert.NotNull(prompt);
        Assert.Contains("will not be merged", prompt!.Text);
        Assert.Contains("branch is left alone", prompt.Text);

        var confirm = Button(view, Stranded, "Yes, discard");
        Assert.True(confirm.IsEffectivelyVisible);
        Assert.True(confirm.IsEffectivelyEnabled);
        Assert.Contains("Danger", confirm.Classes);
        Assert.True(Button(view, Stranded, "Keep").IsEffectivelyVisible);

        // ...and the arming control is gone while the question is on screen, so the row offers one
        // destructive control at a time rather than two that look alike.
        Assert.False(Button(view, Stranded, "Discard").IsEffectivelyVisible);

        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// The inverted form of the same defect, on the projection the design harnesses and demo mode run on.
    ///
    /// <para><see cref="Mainguard.Agents.Agents.Mock.MockOrchestrator"/>'s <c>Verifying</c> rows really are
    /// running — its tick loop advances them while <c>Detail</c> counts "tests 12/41". If its projection
    /// lets <c>VerificationInFlight</c> default to false, the rail labels every one of them "Stalled",
    /// badges it as a warning, and offers "Clear stalled run" on a row visibly executing tests. That is the
    /// same false claim about an entry's activity this change exists to remove, pointing the other way.</para>
    /// </summary>
    [AvaloniaFact]
    public void AVerifyingRowThatIsGenuinelyRunning_IsNeverLabelledStalled()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var mock = new Mainguard.Agents.Agents.Mock.MockOrchestrator();
        var verifying = mock.GetQueue().Where(e => e.State == WorkerMergeState.Verifying).ToList();
        Assert.NotEmpty(verifying); // the fixture must actually contain the case, or this measures nothing

        var vm = new QueueRailViewModel(mock, _ => { });
        foreach (var entry in verifying)
        {
            var row = vm.Entries.Single(e => e.AgentId == entry.AgentId);
            Assert.False(row.IsVerificationStalled,
                $"{entry.AgentId} is running ({entry.Detail}) but the rail calls it stalled");
            Assert.Equal("Verifying", row.StateWord);
        }
    }

    /// <summary>
    /// <b>The stranded row gets a way forward.</b> The entry in the owner's screenshot had commits on its
    /// branch, no jail, and exactly two controls: a Verify that could only ever answer "has no live
    /// sandbox", and a Discard that threw the work away. So the row now offers Resume — and it is offered
    /// to that row and to no other, because a resume for an entry that already has a jail spends a minute
    /// building one for the daemon to refuse.
    /// </summary>
    [AvaloniaFact]
    public void OnlyTheRowWithNoSandbox_OffersResume_AndItsVerifyIsWithheldRatherThanFailing()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var vm = new QueueRailViewModel(new StubQueue(), _ => { }, resumeAgentKind: () => "claude-code");
        var view = new QueueRailView { DataContext = vm };
        var win = HostWindow(view);
        win.Show();
        Settle();

        var resume = Button(view, Stranded, "Resume");
        Assert.True(resume.IsEffectivelyVisible, "the stranded row has no Resume control at all");
        Assert.True(resume.IsEffectivelyEnabled, "Resume is rendered but disabled");
        // Not a second accent — the rail's one emphasized action stays the Review CTA.
        Assert.DoesNotContain("Accent", resume.Classes);

        // Every row that still HAS a jail is not stranded, whatever its merge state. The stalled-verifying
        // row is the sharpest case: its state is broken but its sandbox is not, so it gets the clear and
        // not a resume.
        foreach (var id in new[] { Ready, Frozen, Live })
        {
            Assert.False(Button(view, id, "Resume").IsEffectivelyVisible,
                $"{id} still has a jail — offering to spawn it another is a refusal waiting to happen");
        }

        // …and the Verify button on the stranded row is WITHHELD rather than left enabled to produce an
        // error. That enabled-button-that-only-errors is the state the screenshot was taken in.
        Assert.False(Button(view, Stranded, "Verify").IsEffectivelyEnabled,
            "Verify is enabled on an entry with no jail, so pressing it can only produce an error");
        // The positive control that keeps the assertion above from passing for the wrong reason: the row
        // that HAS a jail keeps its Verify. (Frozen's is withheld too, but for #307's separate reason —
        // its state is Verifying — so it cannot serve as this control.)
        Assert.True(Button(view, Ready, "Verify").IsEffectivelyEnabled);

        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// Pressing Resume reaches the seam with the entry's OWN id and the human's chosen CLI — the two facts
    /// the daemon keys the adoption on. A resume that sent a different id would attach a jail to the wrong
    /// branch, which is precisely why the daemon scopes it to (repo, agent) and denies it to agents.
    /// </summary>
    [AvaloniaFact]
    public void PressingResume_SendsThisEntrysOwnIdAndTheSelectedCli()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var stub = new StubQueue();
        var vm = new QueueRailViewModel(stub, _ => { }, resumeAgentKind: () => "claude-code");
        var view = new QueueRailView { DataContext = vm };
        var win = HostWindow(view);
        win.Show();
        Settle();

        // Resume is NOT two-step: it adds a sandbox and destroys nothing, so it acts on the first press.
        Assert.Empty(stub.Resumed);
        vm.Entries.Single(e => e.AgentId == Stranded).ResumeCommand.Execute(null);
        Settle();

        var sent = Assert.Single(stub.Resumed);
        Assert.Equal(Stranded, sent.AgentId);
        Assert.Equal("claude-code", sent.AgentKind);
        // Nothing was discarded on the way — the recovery action and the destructive one are distinct.
        Assert.Empty(stub.Discarded);

        HarnessHygiene.Teardown(win);
    }

    /// <summary>The captures to judge this on: the resting rail and the armed confirmation, in every theme
    /// themes. Daylight Loom is LIGHT — the quiet destructive has to read as destructive there too.</summary>
    [AvaloniaFact]
    public void Capture_QueueEntryLifecycle_AllThemes()
    {
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);

            var vm = new QueueRailViewModel(new StubQueue(), _ => { });
            var view = new QueueRailView { DataContext = vm };
            var win = HostWindow(view);
            win.Show();
            Settle();
            Assert.Equal(4, vm.Entries.Count);
            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"queue_lifecycle_{theme}.png"));

            // ...and the armed state, which is where the destructive emphasis actually appears.
            vm.Entries.Single(e => e.AgentId == Stranded).BeginDiscardCommand.Execute(null);
            Settle();
            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"queue_lifecycle_confirm_{theme}.png"));

            HarnessHygiene.Teardown(win);
        }

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>The one button with <paramref name="content"/> inside <paramref name="agentId"/>'s row.</summary>
    private static Button Button(Control view, string agentId, string content)
    {
        var row = view.GetVisualDescendants().OfType<Border>()
            .First(b => b.DataContext is QueueEntryViewModel e && e.AgentId == agentId);
        return row.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, content));
    }

    private static Color? Resource(string key) =>
        Avalonia.Application.Current!.TryGetResource(key, null, out var v) && v is ISolidColorBrush b
            ? b.Color : null;

    private static Window HostWindow(Control content)
    {
        var win = new Window { Width = 460, Height = 780, Content = content };
        if (Avalonia.Application.Current!.TryGetResource("SurfaceWindow", null, out var bg) && bg is IBrush brush)
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
    /// The four states the rail has to distinguish, as the daemon would project them. Deliberately NOT
    /// the real MergeQueue: the in-flight flag is the one fact a client cannot derive, so the harness has
    /// to be able to set "Verifying with a live run" and "Verifying with none" independently — which is
    /// exactly the distinction the rendering is being tested on.
    /// </summary>
    private sealed class StubQueue : IMergeQueueService
    {
        public List<string> Discarded { get; } = new();

        /// <summary>Every (agentId, agentKind) a resume actually reached the seam with.</summary>
        public List<(string AgentId, string AgentKind)> Resumed { get; } = new();

        public string MainSha => "a1b2c3d4e5";

        public IReadOnlyList<QueueEntry> GetQueue() => new[]
        {
            Entry(Ready, WorkerMergeState.Verified, "ready to merge"),
            // Stalled because a daemon restart lost the in-flight set — the JAIL is still there, so this
            // row is the other agent's case (clear it and re-verify), NOT a resume. Keeping the two
            // separate here is what makes the discrimination assertions mean something.
            Entry(Frozen, WorkerMergeState.Verifying, "verification stalled — no run in progress"),
            Entry(Live, WorkerMergeState.Verifying, "verifying", inFlight: true),
            // The owner's screenshot: commits on the branch, jail gone, "not verified yet".
            Entry(Stranded, WorkerMergeState.Working, "not verified yet", hasSandbox: false),
        };

        private static QueueEntry Entry(
            string id, WorkerMergeState state, string detail, bool inFlight = false, bool hasSandbox = true)
            => new(id, id, "agent/" + id, state, detail, Verification: null,
                FlaggedItems: Array.Empty<FlaggedItem>(), VerificationInFlight: inFlight,
                HasLiveSandbox: hasSandbox);

        public bool CanMerge(string agentId, out string reason)
        {
            reason = agentId == Ready ? "" : "not verified yet";
            return agentId == Ready;
        }

        public Task<MergeOutcome> ConfirmMergeAsync(string agentId) => throw new NotSupportedException();

        /// <summary>The #307 verification trigger. Answers the shape a jail-less agent really gets — a
        /// refusal that never ran — so the captures show Verify sitting beside the lifecycle actions
        /// rather than pretending a run is available for a stranded entry.</summary>
        public Task<VerificationOutcome> RunVerificationAsync(string agentId) =>
            Task.FromResult(new VerificationOutcome(
                Ran: false, Passed: false,
                Reason: $"Agent '{agentId}' has no live sandbox — verification runs in the worker's own jail."));

        public Task AcknowledgeFlaggedChangeAsync(string agentId, string itemId) => Task.CompletedTask;

        public Task<QueueEntryDiscardOutcome> DiscardEntryAsync(string agentId, string reason)
        {
            Discarded.Add(agentId);
            return Task.FromResult(new QueueEntryDiscardOutcome(agentId, "uid:1000", DateTimeOffset.UnixEpoch));
        }

        public Task ClearStalledVerificationAsync(string agentId) => Task.CompletedTask;

        public Task<QueueEntryResumeOutcome> ResumeEntryAsync(string agentId, string agentKind)
        {
            Resumed.Add((agentId, agentKind));
            return Task.FromResult(new QueueEntryResumeOutcome(
                agentId, "agent/" + agentId, WorkerMergeState.Working, ClearedStalledVerification: false));
        }
    }
}
