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

    // A branch whose tests went RED, with its jail intact. It is this suite's positive control for the
    // Verify affordance, and it has to be a state the DAEMON can actually start a run from: Verified is
    // not one (there is no Verified → Verifying edge), so a control that used the Ready row was asserting
    // that the rail offers a button the daemon refuses. VerificationFailed is exactly the case the button
    // exists for — read the output, push a fix, or ask for the run again.
    private const string Red = "4a0c8e6b5d17-92fb3c1e7a4d508b6c2e9f0a1d834b57";

    // The row this suite's conflict assertions are about: a merge moved main, the daemon's auto-rebase hit
    // this branch's changes, and the worktree is parked mid-rebase with the jail paused. Its STATE is
    // `Working` — identical to the stranded row's — which is exactly why the conflict facts have to travel
    // as their own field: the state word cannot tell a parked conflict from a branch nobody ever verified.
    private const string Conflicted = "6d51fa9c3e08-47a1b8d2c6f309e5417b0da8c93e62f1";

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

        Assert.Equal(6, vm.Entries.Count);

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
        // One per row: every non-terminal entry can be dropped, and all six of this fixture's rows are.
        Assert.Equal(6, discards.Count);
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
        foreach (var id in new[] { Ready, Frozen, Live, Red })
        {
            Assert.False(Button(view, id, "Resume").IsEffectivelyVisible,
                $"{id} still has a jail — offering to spawn it another is a refusal waiting to happen");
        }

        // …and the Verify button on the stranded row is WITHHELD rather than left enabled to produce an
        // error. That enabled-button-that-only-errors is the state the screenshot was taken in.
        Assert.False(Button(view, Stranded, "Verify").IsEffectivelyEnabled,
            "Verify is enabled on an entry with no jail, so pressing it can only produce an error");
        // The positive control that keeps the assertion above from passing for the wrong reason: a row
        // that HAS a jail, in a state a run can legally start from, keeps its Verify.
        //
        // It is the RED row and not the Ready one, which is a correction rather than a preference. Verify
        // used to be offered on Verified as well, and the daemon has never had a Verified → Verifying
        // edge, so this control was previously asserting that the rail offers a button whose every press
        // returns "Illegal merge-state transition Verified → Verifying" — an assertion that the product
        // lies to the user. (Frozen and Live are withheld for #307's separate reason: their state is
        // Verifying.)
        Assert.True(Button(view, Red, "Verify").IsEffectivelyEnabled);
        Assert.False(Button(view, Ready, "Verify").IsEffectivelyEnabled,
            "a green entry offers a Verify the daemon refuses from Verified — an action that always fails");

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

    /// <summary>
    /// The defect this suite was extended for: a conflicted entry's card told the human that a rebase
    /// conflict "needs a human to resolve it" and offered nothing that could. Its jail is PAUSED, so
    /// Verify cannot run in it; Review is absent because the entry is not Verified — which is precisely
    /// what a conflict makes it — and the only remaining control threw the work away.
    ///
    /// <para>Both new controls are asserted REACHABLE, not merely present: this repository has already
    /// shipped a test that checked a button's visibility when the defect was that it was disabled.</para>
    /// </summary>
    [AvaloniaFact]
    public void TheConflictedRow_OffersBothConflictControls_AndOnlyThatRowDoes()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var vm = new QueueRailViewModel(new StubQueue(), _ => { });
        var view = new QueueRailView { DataContext = vm };
        var win = HostWindow(view);
        win.Show();
        Settle();

        var handBack = Button(view, Conflicted, "Let the agent resolve");
        Assert.True(handBack.IsEffectivelyVisible, "the conflicted row has no hand-back control at all");
        Assert.True(handBack.IsEffectivelyEnabled, "the hand-back is rendered but disabled");

        var abort = Button(view, Conflicted, "Abort rebase");
        Assert.True(abort.IsEffectivelyVisible, "the conflicted row has no abort control at all");
        Assert.True(abort.IsEffectivelyEnabled, "the abort is rendered but disabled");

        // …and on no other row. The conflicted entry's STATE is `Working`, identical to the stranded
        // row's, so a control lit from the state word would appear on both — and "abort rebase" on a
        // branch with no rebase in progress is a button whose whole behaviour is an error message.
        foreach (var other in vm.Entries.Where(e => e.AgentId != Conflicted))
        {
            Assert.False(other.HasRebaseConflict, $"{other.AgentId}: claims a conflict it does not have");
            var row = view.GetVisualDescendants().OfType<Border>()
                .First(b => b.DataContext is QueueEntryViewModel e && e.AgentId == other.AgentId);
            Assert.DoesNotContain(row.GetVisualDescendants().OfType<Button>(),
                b => b.IsEffectivelyVisible
                    && (Equals(b.Content, "Abort rebase") || Equals(b.Content, "Let the agent resolve")));
        }

        // The rail's accent budget is unchanged: the two new controls are recovery actions, and one that
        // out-shouted the merge CTA would move the surface's emphasis onto cleanup.
        Assert.DoesNotContain("Accent", handBack.Classes);
        Assert.Contains("Secondary", handBack.Classes);
        Assert.Contains("DangerQuiet", abort.Classes);
        Assert.DoesNotContain("Accent", abort.Classes);
        var accent = Assert.Single(view.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.Classes.Contains("Accent")));
        Assert.Equal("Review", accent.Content);

        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// The card has to say WHAT conflicts and WHERE it is parked. Before this, the one row on the rail
    /// asking for human judgment carried the least evidence of any of them: a sentence naming an action,
    /// and no file, no branch, no location.
    /// </summary>
    [AvaloniaFact]
    public void TheConflictedRow_NamesTheConflictingFilesAndTheParkedWorktree()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var vm = new QueueRailViewModel(new StubQueue(), _ => { });
        var view = new QueueRailView { DataContext = vm };
        var win = HostWindow(view);
        win.Show();
        Settle();

        var texts = view.GetVisualDescendants().OfType<Border>()
            .First(b => b.DataContext is QueueEntryViewModel e && e.AgentId == Conflicted)
            .GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text ?? "")
            .ToList();

        Assert.Contains(texts, t => t.Contains("src/Merge/MergeQueue.cs") && t.Contains("docs/repo-map/README.md"));
        Assert.Contains(texts, t => t.Contains("/srv/mainguard/agents/9f2c/6d51fa9c3e08/worktree"));

        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// An empty path list from the daemon means <b>not measured</b>, and the card must say so. Rendering
    /// nothing — or worse, rendering it as an absence of conflicts — would contradict the very card it is
    /// printed on, which is the same shape of quiet fabrication as the "not verified yet" that used to be
    /// shown over a failed test run.
    /// </summary>
    [AvaloniaFact]
    public void AConflictWithNoMeasuredFiles_SaysNotMeasured_NeverThatNothingConflicts()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var vm = new QueueRailViewModel(new UnmeasuredConflictStub(), _ => { });
        var view = new QueueRailView { DataContext = vm };
        var win = HostWindow(view);
        win.Show();
        Settle();

        var line = view.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "")
            .SingleOrDefault(t => t.StartsWith("Conflicting files:", StringComparison.Ordinal));
        Assert.NotNull(line);
        Assert.Contains("not measured", line!);

        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// The abort throws away rebase progress and cannot be undone, so it asks first — the same two-step
    /// idiom the discard uses. The question states what is lost AND what is not, because "abort" alone
    /// reads as though it might throw the branch away.
    /// </summary>
    [AvaloniaFact]
    public void AbortRebase_AsksFirst_AndTheQuestionSaysTheCommitsSurvive()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var stub = new StubQueue();
        var vm = new QueueRailViewModel(stub, _ => { });
        var view = new QueueRailView { DataContext = vm };
        var win = HostWindow(view);
        win.Show();
        Settle();

        // The BUTTON, not the view model's command: what is under test is that the control on screen arms
        // the two-step guard rather than firing the abort, and a test that invoked the command directly
        // would pass with the button wired straight to the confirm.
        var arm = Button(view, Conflicted, "Abort rebase");
        arm.Command!.Execute(arm.CommandParameter);
        Settle();

        // Arming is not acting.
        Assert.Empty(stub.Aborted);

        var prompt = view.GetVisualDescendants().OfType<TextBlock>()
            .SingleOrDefault(t => t.IsEffectivelyVisible && (t.Text ?? "").Contains("Abort this rebase?"));
        Assert.NotNull(prompt);
        Assert.Contains("commits are untouched", prompt!.Text);
        Assert.Contains("needs verifying again", prompt.Text);

        var confirm = Button(view, Conflicted, "Yes, abort");
        Assert.True(confirm.IsEffectivelyVisible);
        Assert.True(confirm.IsEffectivelyEnabled);
        Assert.Contains("Danger", confirm.Classes);
        Assert.True(Button(view, Conflicted, "Keep it").IsEffectivelyVisible);

        // The whole action row is hidden while a question is on screen — including the OTHER destructive
        // control. A confirmation and a live Discard offering to do different things to the same entry at
        // the same time is how a human clicks the wrong one.
        Assert.DoesNotContain(
            view.GetVisualDescendants().OfType<Border>()
                .First(b => b.DataContext is QueueEntryViewModel e && e.AgentId == Conflicted)
                .GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible
                && (Equals(b.Content, "Discard") || Equals(b.Content, "Abort rebase")));

        confirm.Command!.Execute(confirm.CommandParameter);
        Settle();
        Assert.Equal(new[] { Conflicted }, stub.Aborted);
        // The recovery action and the destructive one stay distinct — nothing was discarded on the way.
        Assert.Empty(stub.Discarded);

        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// The hand-back is NOT confirmed first, deliberately: it changes nothing that cannot be undone (the
    /// branch stays exactly as it is, mid-rebase) and the entry can still be aborted or discarded
    /// afterwards. Ceremony on the recovery action while the irreversible one asks is the wrong way round.
    /// </summary>
    [AvaloniaFact]
    public void LetTheAgentResolve_ReachesTheDaemonInOnePress()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var stub = new StubQueue();
        var vm = new QueueRailViewModel(stub, _ => { });
        var view = new QueueRailView { DataContext = vm };
        var win = HostWindow(view);
        win.Show();
        Settle();

        var handBack = Button(view, Conflicted, "Let the agent resolve");
        handBack.Command!.Execute(handBack.CommandParameter);
        Settle();

        Assert.Equal(new[] { Conflicted }, stub.HandedBack);
        Assert.Empty(stub.Aborted);
        Assert.Empty(stub.Discarded);

        HarnessHygiene.Teardown(win);
    }

    /// <summary>A conflicted entry whose files the daemon could not measure — the "empty means unknown"
    /// fixture, kept separate so the main stub keeps its realistic two-file conflict.</summary>
    private sealed class UnmeasuredConflictStub : IMergeQueueService
    {
        public event Action? Changed;

        public string MainSha => "a1b2c3d4e5";

        public IReadOnlyList<QueueEntry> GetQueue() => new[]
        {
            new QueueEntry(
                Conflicted, Conflicted, "agent/" + Conflicted, WorkerMergeState.Working,
                "rebasing this branch onto the new main hit a conflict", Verification: null,
                FlaggedItems: Array.Empty<FlaggedItem>(), HasLiveSandbox: true,
                RebaseConflict: new QueueRebaseConflict(
                    "/srv/mainguard/agents/9f2c/6d51fa9c3e08/worktree", "main",
                    Array.Empty<string>(), DateTimeOffset.UnixEpoch)),
        };

        public bool CanMerge(string agentId, out string reason)
        {
            reason = "rebasing this branch onto the new main hit a conflict";
            return false;
        }

        public Task<MergeOutcome> ConfirmMergeAsync(string agentId) => throw new NotSupportedException();

        public Task<VerificationOutcome> RunVerificationAsync(string agentId) =>
            throw new NotSupportedException();

        public Task<VerificationLog> GetVerificationLogAsync(string agentId) =>
            Task.FromResult(new VerificationLog(false, false, "", "", null, "", false, ""));

        public Task AcknowledgeFlaggedChangeAsync(string agentId, string itemId) => Task.CompletedTask;

        public Task<QueueEntryDiscardOutcome> DiscardEntryAsync(string agentId, string reason) =>
            throw new NotSupportedException();

        public Task<QueueEntryRejectOutcome> RejectEntryAsync(string agentId, string reason) =>
            throw new NotSupportedException();

        public Task ClearStalledVerificationAsync(string agentId) => Task.CompletedTask;

        public Task<QueueEntryResumeOutcome> ResumeEntryAsync(string agentId, string agentKind) =>
            throw new NotSupportedException();

        public Task ResolveConflictWithAgentAsync(string agentId) => Task.CompletedTask;

        public Task AbortRebaseAsync(string agentId) => Task.CompletedTask;
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
            Assert.Equal(6, vm.Entries.Count);
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
        public event Action? Changed;

        public List<string> Discarded { get; } = new();

        /// <summary>Every (agentId, agentKind) a resume actually reached the seam with.</summary>
        public List<(string AgentId, string AgentKind)> Resumed { get; } = new();

        public string MainSha => "a1b2c3d4e5";

        public IReadOnlyList<QueueEntry> GetQueue() => new[]
        {
            // Carries a real (green) verdict, so this fixture's captures show the verdict line saying what
            // a verified row's record actually is rather than "not verified yet" under a Verified badge.
            Entry(Ready, WorkerMergeState.Verified, "ready to merge",
                verdict: new VerificationVerdict(true, "dotnet test", DateTimeOffset.UnixEpoch)),
            // Stalled because a daemon restart lost the in-flight set — the JAIL is still there, so this
            // row is the other agent's case (clear it and re-verify), NOT a resume. Keeping the two
            // separate here is what makes the discrimination assertions mean something.
            Entry(Frozen, WorkerMergeState.Verifying, "verification stalled — no run in progress"),
            Entry(Live, WorkerMergeState.Verifying, "verifying", inFlight: true),
            Entry(Red, WorkerMergeState.VerificationFailed,
                "the verification FAILED (dotnet test) — read the run output, then push a fix or discard the entry",
                verdict: new VerificationVerdict(false, "dotnet test", DateTimeOffset.UnixEpoch)),
            // The owner's screenshot: commits on the branch, jail gone, "not verified yet".
            Entry(Stranded, WorkerMergeState.Working, "not verified yet", hasSandbox: false),
            // The parked conflict, with the daemon's own gate sentence — the one that names a required
            // human action the rail had no operation for.
            Entry(Conflicted, WorkerMergeState.Working,
                "rebasing this branch onto the new main hit a conflict — the agent is paused with the "
                + "rebase in progress and needs a human to resolve it",
                conflict: new QueueRebaseConflict(
                    "/srv/mainguard/agents/9f2c/6d51fa9c3e08/worktree", "main",
                    new[] { "src/Merge/MergeQueue.cs", "docs/repo-map/README.md" },
                    DateTimeOffset.UnixEpoch)),
        };

        private static QueueEntry Entry(
            string id, WorkerMergeState state, string detail, bool inFlight = false, bool hasSandbox = true,
            VerificationVerdict? verdict = null, QueueRebaseConflict? conflict = null)
            => new(id, id, "agent/" + id, state, detail, Verification: verdict,
                FlaggedItems: Array.Empty<FlaggedItem>(), VerificationInFlight: inFlight,
                HasLiveSandbox: hasSandbox, RebaseConflict: conflict);

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

        /// <summary>Reading the recorded output — never a run. Only <see cref="Ready"/> has a record here;
        /// every other row must be told there is none rather than shown an empty log.</summary>
        public Task<VerificationLog> GetVerificationLogAsync(string agentId) =>
            Task.FromResult(agentId == Ready
                ? new VerificationLog(true, true, "dotnet test", "a1b2c3d4e5", DateTimeOffset.UnixEpoch,
                    "$ dotnet test\n  All tests passed.", false, "")
                : new VerificationLog(false, false, "", "", null, "", false, ""));

        public Task AcknowledgeFlaggedChangeAsync(string agentId, string itemId) => Task.CompletedTask;

        public Task<QueueEntryDiscardOutcome> DiscardEntryAsync(string agentId, string reason)
        {
            Discarded.Add(agentId);
            return Task.FromResult(new QueueEntryDiscardOutcome(agentId, "uid:1000", DateTimeOffset.UnixEpoch));
        }

        public Task<QueueEntryRejectOutcome> RejectEntryAsync(string agentId, string reason) =>
            Task.FromResult(new QueueEntryRejectOutcome(agentId, "uid:1000", DateTimeOffset.UnixEpoch));

        public Task ClearStalledVerificationAsync(string agentId) => Task.CompletedTask;

        public Task<QueueEntryResumeOutcome> ResumeEntryAsync(string agentId, string agentKind)
        {
            Resumed.Add((agentId, agentKind));
            return Task.FromResult(new QueueEntryResumeOutcome(
                agentId, "agent/" + agentId, WorkerMergeState.Working, ClearedStalledVerification: false));
        }

        /// <summary>Every entry a hand-back actually reached the seam with.</summary>
        public List<string> HandedBack { get; } = new();

        /// <summary>Every entry an abort actually reached the seam with.</summary>
        public List<string> Aborted { get; } = new();

        public Task ResolveConflictWithAgentAsync(string agentId)
        {
            HandedBack.Add(agentId);
            return Task.CompletedTask;
        }

        public Task AbortRebaseAsync(string agentId)
        {
            Aborted.Add(agentId);
            return Task.CompletedTask;
        }
    }
}
