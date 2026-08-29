using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.UI.Theming;
using Xunit;

namespace Mainguard.Tests.Headless;

/// <summary>
/// The four phase-2 plan-gate states of the coordinator panel, rendered in EVERY one of the five themes
/// for human visual review (contract §2). PNGs land in artifacts_headless/.
///
/// <para>The states are here because each of them is otherwise invisible, and three of them are new:</para>
/// <list type="number">
/// <item><b>plan-pending</b> — a worker wrote this and is blocked on you;</item>
/// <item><b>rejected/revising</b> — the plan came back revised, with the revision counter against the
/// daemon's budget and the feedback it was written against;</item>
/// <item><b>escalated</b> — the budget is spent and the worker stopped: a card with no decision on it,
/// because the decision is no longer a click;</item>
/// <item><b>backpressure</b> — blocked workers have filled the worker cap, so the coordinator has stopped
/// spawning. This one is the reason the harness exists at all: a stall nobody can see is
/// indistinguishable from a hang.</item>
/// </list>
///
/// <para>The assertions are not decoration. Each state asserts the fact the human is supposed to be able
/// to read off the surface, so a render that silently loses it fails here rather than in the screenshot
/// nobody opened.</para>
/// </summary>
public class CoordinatorPlanGateRenderHarness
{
    private static readonly string[] ThemeKeys =
        { "MidnightLoom", "DaylightLoom", "CommandDeck", "Atelier", "LoomAurora" };

    [AvaloniaFact]
    public void PlanPending_HeadlessPng_AllThemes()
    {
        RenderAllThemes("plan_gate_pending", () => Fake.Pending(), vm =>
        {
            Assert.NotNull(vm.PendingPlan);
            Assert.Contains("Written by loom-4", vm.PendingPlan!.FactsText, StringComparison.Ordinal);
            Assert.Contains("blocked until you decide", vm.PendingPlan.FactsText, StringComparison.Ordinal);
            Assert.False(vm.PendingPlan.IsRevision);
            Assert.False(vm.PendingPlan.NextRejectionEscalates);
        });
    }

    [AvaloniaFact]
    public void RejectedAndRevised_HeadlessPng_AllThemes()
    {
        RenderAllThemes("plan_gate_revising", () => Fake.Revised(), vm =>
        {
            Assert.NotNull(vm.PendingPlan);
            Assert.Equal("revision 2 of 3", vm.PendingPlan!.RevisionText);
            Assert.Contains("revised against: too wide", vm.PendingPlan.RevisedAgainstText, StringComparison.Ordinal);
        });
    }

    [AvaloniaFact]
    public void LastRevision_WarnsThatRejectingAgainStopsTheWorker_AllThemes()
    {
        RenderAllThemes("plan_gate_last_revision", () => Fake.LastRevision(), vm =>
        {
            Assert.NotNull(vm.PendingPlan);
            Assert.True(vm.PendingPlan!.NextRejectionEscalates);
            Assert.Equal("Reject — worker will stop", vm.PendingPlan.RejectButtonText);
        });
    }

    [AvaloniaFact]
    public void Escalated_HeadlessPng_AllThemes()
    {
        RenderAllThemes("plan_gate_escalated", () => Fake.Escalated(), vm =>
        {
            Assert.Null(vm.PendingPlan); // nothing to decide — that is the point of this state
            var card = Assert.Single(vm.EscalatedPlans);
            Assert.Contains("stopped after 3 rejected plans", card.HeadlineText, StringComparison.Ordinal);
            Assert.Contains("your last feedback", card.LastFeedbackText, StringComparison.Ordinal);
        });
    }

    [AvaloniaFact]
    public void BackpressureCapSaturated_HeadlessPng_AllThemes()
    {
        RenderAllThemes("plan_gate_backpressure", () => Fake.Backpressure(), vm =>
        {
            Assert.True(vm.IsCapSaturatedByBlockedWorkers);
            Assert.Contains("6 workers are waiting on your approval", vm.BackpressureText, StringComparison.Ordinal);
            Assert.Contains("stopped spawning", vm.BackpressureText, StringComparison.Ordinal);

            // …and all six are decidable. The stall is caused by six blocked workers, so a surface that
            // renders one card describes a queue the operator cannot clear: five of the six decisions
            // holding the cap shut would be unreachable.
            Assert.Equal(6, vm.PendingPlans.Count);
        });
    }

    /// <summary>
    /// One card <b>per blocked worker</b>, rendered — measured on the visual tree rather than on the
    /// collection, because "the ViewModel has six" and "the human can decide six" are different claims and
    /// only the second one clears the cap.
    /// </summary>
    [AvaloniaFact]
    public void EveryBlockedWorker_GetsItsOwnDecidableCard()
    {
        using var _seed = HarnessHygiene.SeedViewAssemblies(new Mainguard.Agents.UI.Editions.ProManifest());
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        var vm = new CoordinatorPanelViewModel(Fake.Backpressure());
        var gate = new PlanGateView { DataContext = vm };
        var win = new Window { Width = 520, Height = 1600, Content = gate };
        win.Show();
        Settle();

        var approve = gate.GetVisualDescendants().OfType<Button>()
            .Where(b => (b.Content as string) == "Approve plan" && b.IsEffectivelyVisible)
            .ToList();
        Assert.Equal(6, approve.Count);
        Assert.All(approve, b => Assert.True(b.IsEnabled));

        // Each card carries the Scope of its own worker, not a repeat of the first one's.
        var workers = gate.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "")
            .Where(t => t.Contains("Written by loom-", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(6, workers.Count);

        win.Content = null;
        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// <b>The gate is mounted on the surface that ships.</b>
    ///
    /// <para>This is the assertion whose absence made phase 2 undriveable. Every other test in this file
    /// renders <c>CoordinatorPanelView</c> — a control the application never constructs — so the daemon's
    /// plan gate could be complete, tested end to end, and pinned in five themes while the operator had no
    /// button to press anywhere in the product. A gate the human cannot reach is not a gate; it is a
    /// worker that never starts.</para>
    ///
    /// <para>So this one builds the real <c>ControlCenterView</c> off a real <c>ControlCenterViewModel</c>
    /// and requires the gate to be there, visible, bound to the coordinator's own state, with a live
    /// Approve on it.</para>
    /// </summary>
    [AvaloniaFact]
    public void TheShippedControlCenterSurface_MountsThePlanGate()
    {
        using var _seed = HarnessHygiene.SeedViewAssemblies(new Mainguard.Agents.UI.Editions.ProManifest());
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        using var vm = new ControlCenterViewModel(mock);
        vm.FocusCoordinator();

        // The fixture has to actually be holding a decision, or this measures nothing.
        Assert.NotEmpty(vm.Coordinator.PendingPlans);
        Assert.True(vm.Coordinator.HasGateContent);

        var view = new ControlCenterView { DataContext = vm };
        var win = new Window { Width = 1420, Height = 920, Content = view };
        win.Show();
        Settle();
        HarnessHygiene.AssertNoUnresolvedViews(view);

        var gate = Assert.Single(view.GetVisualDescendants().OfType<PlanGateView>());
        Assert.True(gate.IsEffectivelyVisible,
            "the plan gate is in the tree but not visible — the operator still cannot approve anything");
        Assert.Same(vm.Coordinator, gate.DataContext);
        Assert.True(gate.Bounds.Height > 0 && gate.Bounds.Width > 0,
            $"the gate rendered at {gate.Bounds.Width}x{gate.Bounds.Height} — it occupies no space");

        var approve = view.GetVisualDescendants().OfType<Button>()
            .Single(b => (b.Content as string) == "Approve plan");
        Assert.True(approve.IsEffectivelyVisible);
        Assert.True(approve.IsEnabled);

        // …and the coordinator's terminal did not get pushed off the surface to make room for it.
        var terminalHost = view.GetVisualDescendants().OfType<Border>()
            .First(b => b.Name == null && b.Bounds.Height > 200);
        Assert.True(terminalHost.Bounds.Height > 200);

        win.Content = null;
        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// The captures the mount has to be judged on: the gate sitting on the <b>real</b> coordinator surface,
    /// above the real terminal, in all five themes. Daylight Loom is a LIGHT theme — the warning border and
    /// the Approve accent have to read there too, which is why nothing on the card sets a raw colour.
    /// </summary>
    [AvaloniaFact]
    public void Capture_ThePlanGateOnTheShippedSurface_AllThemes()
    {
        using var _seed = HarnessHygiene.SeedViewAssemblies(new Mainguard.Agents.UI.Editions.ProManifest());
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);
            using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
            using var vm = new ControlCenterViewModel(mock);
            vm.FocusCoordinator();
            Assert.True(vm.Coordinator.HasGateContent);

            var view = new ControlCenterView { DataContext = vm };
            var win = new Window { Width = 1420, Height = 920, Content = view };
            win.Show();
            Settle();

            Assert.Single(view.GetVisualDescendants().OfType<PlanGateView>());
            win.CaptureRenderedFrame()?.Save(
                Path.Combine(ArtifactsDir(), $"plan_gate_on_control_center_{theme}.png"));

            win.Content = null;
            HarnessHygiene.Teardown(win);
        }
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    /// <summary>
    /// <b>The decision must be clickable — all of it, at the window sizes people actually use.</b>
    ///
    /// <para>The reported defect: on the shipped surface roughly four screen points of Approve and Reject
    /// were hittable, with the terminal appearing to sit on top of them. The cause was a literal
    /// <c>MaxHeight="360"</c> on the plan-gate region. A constant cap does not move when the window does,
    /// and the decision buttons are the LAST row of every card, so the overflow it cut was always the
    /// decision — identically at 1296x759 and at 1700x1050. Measured on this harness before the fix, the
    /// second blocked worker's Approve had <i>zero</i> visible pixels at both sizes.</para>
    ///
    /// <para>So the assertion is not "the button exists": it is that no decision button is left as a
    /// sliver. A button is either wholly inside the gate's viewport or wholly scrolled out of it —
    /// a partially clipped one is the four-point target, and it is the thing that must not come back.</para>
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1296, 759)]
    [InlineData(1420, 920)]
    [InlineData(1700, 1050)]
    public void AnOverflowingGate_LeavesNoDecisionButtonAsASliver(int width, int height)
    {
        using var _seed = HarnessHygiene.SeedViewAssemblies(new Mainguard.Agents.UI.Editions.ProManifest());
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        using var vm = new ControlCenterViewModel(mock);
        vm.FocusCoordinator();
        Overfill(vm.Coordinator);

        var view = new ControlCenterView { DataContext = vm };
        var win = new Window { Width = width, Height = height, Content = view };
        win.Show();
        Settle();

        var gate = Assert.Single(view.GetVisualDescendants().OfType<PlanGateView>());
        var host = gate.GetVisualAncestors().OfType<ScrollViewer>().First();
        var (top, bottom) = VerticalSpan(host, view);

        // The fixture has to actually overflow, or this measures a surface that never had the problem.
        Assert.True(host.Extent.Height > host.Bounds.Height,
            $"the fixture did not overflow the gate ({host.Extent.Height} in {host.Bounds.Height})");

        var decisions = view.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Content is "Approve plan" or "Reject with feedback" or "Reject — worker will stop")
            .ToList();
        Assert.NotEmpty(decisions);

        foreach (var button in decisions)
        {
            var (bTop, bBottom) = VerticalSpan(button, view);
            var shown = Math.Max(0, Math.Min(bBottom, bottom) - Math.Max(bTop, top));
            var full = bBottom - bTop;
            Assert.True(
                shown <= 0.5 || shown >= full - 0.5,
                $"'{button.Content}' is clipped to {shown:0.#} of {full:0.#} points at {width}x{height} — "
                + "a decision the human can only partly hit");
        }

        // …and the FRONT decision — the topmost one, the one the surface is asking about before any
        // scrolling — is wholly there. This is the assertion the reported defect fails: with the gate
        // capped at a constant and escalated cards ordered first, the front decision sat below the cut.
        var first = decisions.OrderBy(b => VerticalSpan(b, view).Top).First();
        var (fTop, fBottom) = VerticalSpan(first, view);
        Assert.True(fTop >= top - 0.5 && fBottom <= bottom + 0.5,
            $"the front decision '{first.Content}' sits at {fTop:0}..{fBottom:0} but the gate viewport is "
            + $"{top:0}..{bottom:0} at {width}x{height} — the human's first decision is off the surface");

        win.Content = null;
        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// The cap is a <b>share of the pane</b>, not a number. This is the property the old constant could
    /// never have: enlarging the window has to buy the gate room, or the operator's only recourse to a
    /// clipped decision (make the window bigger) does nothing at all.
    /// </summary>
    [AvaloniaFact]
    public void TheGatesCap_GrowsWithTheWindow()
    {
        using var _seed = HarnessHygiene.SeedViewAssemblies(new Mainguard.Agents.UI.Editions.ProManifest());
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        double Measure(int width, int height)
        {
            using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
            using var vm = new ControlCenterViewModel(mock);
            vm.FocusCoordinator();
            Overfill(vm.Coordinator);

            var view = new ControlCenterView { DataContext = vm };
            var win = new Window { Width = width, Height = height, Content = view };
            win.Show();
            Settle();
            var host = view.GetVisualDescendants().OfType<PlanGateView>().Single()
                .GetVisualAncestors().OfType<ScrollViewer>().First();
            var measured = host.Bounds.Height;
            win.Content = null;
            HarnessHygiene.Teardown(win);
            return measured;
        }

        var small = Measure(1296, 700);
        var large = Measure(1296, 1100);
        Assert.True(large > small + 40,
            $"the gate got {small:0} points in a 700-tall window and {large:0} in an 1100-tall one — "
            + "the cap is not tracking the pane");
    }

    /// <summary>
    /// The seam. "The pane is not resizable" was half the reported defect, and it was literally true: the
    /// gate and the terminal were two grid rows with nothing between them. It appears with the gate and
    /// disappears with it, so an idle coordinator surface grows no furniture.
    /// </summary>
    [AvaloniaFact]
    public void TheGateAndTheTerminal_AreSeparatedByADraggableSeam()
    {
        using var _seed = HarnessHygiene.SeedViewAssemblies(new Mainguard.Agents.UI.Editions.ProManifest());
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        using var vm = new ControlCenterViewModel(mock);
        vm.FocusCoordinator();
        Assert.True(vm.Coordinator.HasGateContent);

        var view = new ControlCenterView { DataContext = vm };
        var win = new Window { Width = 1296, Height = 759, Content = view };
        win.Show();
        Settle();

        var gate = view.GetVisualDescendants().OfType<PlanGateView>().Single();
        var host = gate.GetVisualAncestors().OfType<ScrollViewer>().First();
        var seam = Assert.Single(view.GetVisualDescendants().OfType<GridSplitter>(), g => g.Name == "GateSeam");

        Assert.True(seam.IsEffectivelyVisible, "the gate and the terminal have no handle between them");
        Assert.Equal(GridResizeDirection.Rows, seam.ResizeDirection);
        Assert.True(VerticalSpan(seam, view).Top >= VerticalSpan(host, view).Bottom - 1,
            "the seam is not between the gate and the terminal");

        // …and it goes away with the gate, rather than leaving a handle for a region that is not there.
        vm.Coordinator.PendingPlans.Clear();
        vm.Coordinator.EscalatedPlans.Clear();
        vm.Coordinator.BackpressureText = "";
        Settle();
        Assert.False(seam.IsEffectivelyVisible);

        win.Content = null;
        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// Escalated cards render <b>below</b> the decidable ones.
    ///
    /// <para>In the live run an escalated card sat on top and pushed the pending plan off the bottom of a
    /// bounded region. An escalated card carries no approval — nothing on it unblocks a worker or relieves
    /// the cap — so when the gate has to clip something, that is what it should reach first.</para>
    /// </summary>
    [AvaloniaFact]
    public void EscalatedCards_RenderBelowTheDecidableOnes()
    {
        using var _seed = HarnessHygiene.SeedViewAssemblies(new Mainguard.Agents.UI.Editions.ProManifest());
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        var vm = new CoordinatorPanelViewModel(Fake.PendingAndEscalated(), endWorker: _ => Task.CompletedTask);
        var gate = new PlanGateView { DataContext = vm };
        var win = new Window { Width = 520, Height = 1200, Content = gate };
        win.Show();
        Settle();

        var approve = gate.GetVisualDescendants().OfType<Button>()
            .First(b => (b.Content as string) == "Approve plan");
        var escalated = gate.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Text == "Escalated to you");

        Assert.True(
            VerticalSpan(approve, gate).Bottom <= VerticalSpan(escalated, gate).Top,
            "the escalated card is above the decision — a bounded gate would clip the decision first");

        win.Content = null;
        HarnessHygiene.Teardown(win);
    }

    /// <summary>
    /// <b>The escalated card can release the slot its own copy promises.</b>
    ///
    /// <para>"It will not try again — steer it or end it" has always been on this card, and until now the
    /// surface offered neither. An escalated worker keeps its jail and keeps counting against the worker
    /// cap, and the only route to ending it was Resources → right-click → End task: a context menu on a
    /// row that named no agent. This asserts the button is there, that it asks first, and that confirming
    /// ends <i>that</i> worker.</para>
    /// </summary>
    [AvaloniaFact]
    public void AnEscalatedCard_OffersTheEndItsCopyPromises_AndConfirmsFirst()
    {
        using var _seed = HarnessHygiene.SeedViewAssemblies(new Mainguard.Agents.UI.Editions.ProManifest());
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        var ended = new List<string>();
        var vm = new CoordinatorPanelViewModel(Fake.Escalated(), endWorker: id =>
        {
            ended.Add(id);
            return Task.CompletedTask;
        });

        var gate = new PlanGateView { DataContext = vm };
        var win = new Window { Width = 520, Height = 780, Content = gate };
        win.Show();
        Settle();

        var card = Assert.Single(vm.EscalatedPlans);
        Assert.True(card.ShowEndAction);

        var end = gate.GetVisualDescendants().OfType<Button>()
            .Single(b => (b.Content as string) == "End this worker");
        Assert.True(end.IsEffectivelyVisible);

        // Arming asks; nothing has happened yet.
        card.BeginEndCommand.Execute(null);
        Settle();
        Assert.True(card.IsConfirmingEnd);
        Assert.Empty(ended);
        Assert.Contains("frees the slot", card.EndConfirmText, StringComparison.Ordinal);
        Assert.Contains("branch is kept", card.EndConfirmText, StringComparison.Ordinal);

        card.ConfirmEndCommand.Execute(null);
        Settle();
        Assert.Equal(new[] { "loom-4" }, ended);

        win.Content = null;
        HarnessHygiene.Teardown(win);
    }

    /// <summary>Without an ending seam the action is not offered — never a button that does nothing.</summary>
    [AvaloniaFact]
    public void WithNoWayToEndAWorker_TheEscalatedCardOffersNoButton()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var vm = new CoordinatorPanelViewModel(Fake.Escalated());
        Assert.False(Assert.Single(vm.EscalatedPlans).ShowEndAction);
    }

    /// <summary>
    /// <b>The banner and the cards cannot disagree.</b>
    ///
    /// <para>The reported state: an escalated worker's card stayed on the gate after its agent was ended
    /// and its jail torn down, while the amber banner — counting a different population — had already
    /// dropped it. Client-side, the fact both halves are read through is now derived from the collections
    /// and the sentence rather than assigned after them, so there is no ordering in which the region can
    /// be showing over an empty gate or collapsed over a full one.</para>
    /// </summary>
    [AvaloniaFact]
    public void TheGatesVisibility_IsDerivedFromItsContents()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var vm = new CoordinatorPanelViewModel(Fake.Escalated());
        Assert.True(vm.HasGateContent);

        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.HasGateContent)) raised++;
        };

        vm.EscalatedPlans.Clear();
        vm.BackpressureText = "";
        Assert.False(vm.HasGateContent);
        Assert.True(raised > 0, "the host was never told the gate emptied");

        vm.BackpressureText = "1 worker is waiting on your approval.";
        Assert.True(vm.HasGateContent);
    }

    /// <summary>Vertical extent of a control in another control's coordinates.</summary>
    private static (double Top, double Bottom) VerticalSpan(Visual control, Visual relativeTo)
    {
        var top = control.TranslatePoint(new Point(0, 0), relativeTo) ?? default;
        var bottom = control.TranslatePoint(new Point(0, control.Bounds.Height), relativeTo) ?? default;
        return (top.Y, bottom.Y);
    }

    /// <summary>
    /// The live stress-run shape: an escalated worker plus more than one blocked one, which is more gate
    /// than any window gives it. Built by hand rather than from the mock because the mock's scripted
    /// fleet holds exactly one plan, and one card never overflowed.
    /// </summary>
    private static void Overfill(CoordinatorPanelViewModel gate)
    {
        gate.EscalatedPlans.Add(new EscalatedPlanViewModel(new WorkerPlanCard(
            "plan-esc", "fad68b04fa5a429fab790d64fb807a9f", "coordinator", "multiply helper",
            new[] { "calc.js", "test.js" }, "Add the multiply guard.", "node test.js",
            1.00m, DateTimeOffset.Now.AddMinutes(-20), "Escalated", 3, 0, 3,
            "Still not right: three new guards is more than test.js needs and -2*3 adds nothing once 6*7 "
            + "and 0.5*8 are there. Keep exactly two guards, and put the multiply line before subtract in "
            + "calc.js so the file reads alphabetically too.")));

        foreach (var i in Enumerable.Range(1, 3))
        {
            gate.PendingPlans.Add(new PlanCardViewModel(new WorkerPlanCard(
                $"plan-{i}", $"b3d2e7a48d09462fb9b6421bcd6da68{i}", "coordinator", $"divide helper {i}",
                new[] { "calc.js", "test.js", "src/ops/divide.js", "src/ops/index.js", "docs/ops.md" },
                "Add divide beside multiply, guarding divide-by-zero, and export it from the ops index.",
                "node test.js plus two boundary cases", 1.25m, DateTimeOffset.Now.AddMinutes(-5),
                "Pending", 0, 3, 3, ""), (_, _, _) => Task.CompletedTask));
        }
    }

    /// <summary>
    /// The negative control for the mount test: with nothing waiting, the gate must take <b>no</b> vertical
    /// space on the coordinator surface. A region that is always there is a region the terminal has
    /// permanently lost.
    /// </summary>
    [AvaloniaFact]
    public void WithNothingWaiting_TheGateTakesNoSpaceOnTheShippedSurface()
    {
        using var _seed = HarnessHygiene.SeedViewAssemblies(new Mainguard.Agents.UI.Editions.ProManifest());
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        using var vm = new ControlCenterViewModel(mock);
        vm.FocusCoordinator();

        var view = new ControlCenterView { DataContext = vm };
        var win = new Window { Width = 1420, Height = 920, Content = view };
        win.Show();
        Settle();

        // Clear the gate the way approving everything would. HasGateContent is derived, so nothing sets
        // it — which is the point: the region cannot be left showing over an empty gate.
        vm.Coordinator.PendingPlans.Clear();
        vm.Coordinator.EscalatedPlans.Clear();
        vm.Coordinator.BackpressureText = "";
        Assert.False(vm.Coordinator.HasGateContent);
        Settle();

        var gate = Assert.Single(view.GetVisualDescendants().OfType<PlanGateView>());
        Assert.False(gate.IsEffectivelyVisible, "an idle plan gate must collapse, not sit there empty");

        win.Content = null;
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void NoBlockedWorkers_ShowsNoBackpressureLine()
    {
        // The negative control for the harness above: with nothing waiting, the surface must NOT claim a
        // stall. An always-on warning is the same as no warning.
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        var vm = new CoordinatorPanelViewModel(Fake.Quiet());

        Assert.Equal("", vm.BackpressureText);
        Assert.False(vm.IsCapSaturatedByBlockedWorkers);
        Assert.Empty(vm.EscalatedPlans);
    }

    private static void RenderAllThemes(
        string name, Func<Fake> service, Action<CoordinatorPanelViewModel> assert)
    {
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);

            var vm = new CoordinatorPanelViewModel(service());
            assert(vm);

            var win = new Window
            {
                Width = 520,
                Height = 780,
                Content = new CoordinatorPanelView { DataContext = vm },
            };
            win.Show();
            Settle();
            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"{name}_{theme}.png"));
            win.Content = null;
            HarnessHygiene.Teardown(win);
        }

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    private static void Settle()
    {
        for (int i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }

    /// <summary>A deterministic <see cref="ICoordinatorService"/> pinned to one plan-gate state.</summary>
    private sealed class Fake : ICoordinatorService
    {
        private readonly IReadOnlyList<ChatLine> _transcript;
        private readonly IReadOnlyList<WorkerPlanCard> _cards;
        private readonly OrchestrationBackpressure _backpressure;

        private Fake(
            IReadOnlyList<ChatLine> transcript,
            IReadOnlyList<WorkerPlanCard> cards,
            OrchestrationBackpressure backpressure)
        {
            _transcript = transcript;
            _cards = cards;
            _backpressure = backpressure;
        }

        public static Fake Pending() => new(
            Lines("Loom-4 presented its plan and is blocked awaiting your approval."),
            new[] { Card(revision: 0, remaining: 3, status: "Pending", feedback: "") },
            new OrchestrationBackpressure(1, 0, 2, 6, 3, "1 worker is waiting on your approval."));

        public static Fake Revised() => new(
            Lines("Loom-4 revised its plan against your feedback (revision 2 of 3)."),
            new[] { Card(revision: 2, remaining: 1, status: "Pending", feedback: "too wide — scope it to the clock") },
            new OrchestrationBackpressure(1, 0, 2, 6, 3, "1 worker is waiting on your approval."));

        public static Fake LastRevision() => new(
            Lines("Loom-4 revised its plan against your feedback (revision 3 of 3)."),
            new[] { Card(revision: 3, remaining: 0, status: "Pending", feedback: "still touching the refresh service") },
            new OrchestrationBackpressure(1, 0, 2, 6, 3, "1 worker is waiting on your approval."));

        public static Fake Escalated() => new(
            Lines("Loom-4 stopped after 3 rejected plans and escalated to you."),
            new[] { Card(revision: 3, remaining: 0, status: "Escalated", feedback: "this still is not the right approach") },
            new OrchestrationBackpressure(0, 1, 2, 6, 3, "1 escalated after 3 rejected plans."));

        public static Fake Backpressure()
        {
            var cards = Enumerable.Range(1, 6)
                .Select(i => Card(revision: 0, remaining: 3, status: "Pending", feedback: "", worker: $"loom-{i}"))
                .ToArray();
            return new Fake(
                Lines("spawn_worker → [Rejected] Worker cap reached — 6/6 running."),
                cards,
                new OrchestrationBackpressure(6, 0, 6, 6, 3,
                    "6 workers are waiting on your approval. The worker cap (6/6) is full — " +
                    "the coordinator has stopped spawning until you clear plans."));
        }

        public static Fake PendingAndEscalated() => new(
            Lines("loom-9 is blocked on your approval; loom-4 escalated after 3 rejected plans."),
            new[]
            {
                Card(revision: 0, remaining: 3, status: "Pending", feedback: "", worker: "loom-9"),
                Card(revision: 3, remaining: 0, status: "Escalated", feedback: "not the right approach"),
            },
            new OrchestrationBackpressure(1, 1, 3, 6, 3,
                "1 worker is waiting on your approval · 1 escalated after 3 rejected plans."));

        public static Fake Quiet() => new(
            Lines("Nothing is waiting on you."), Array.Empty<WorkerPlanCard>(), OrchestrationBackpressure.None);

        private static IReadOnlyList<ChatLine> Lines(string systemLine) => new[]
        {
            new ChatLine(ChatLineKind.Human, "Fix the token expiry off-by-one.", DateTimeOffset.Now.AddMinutes(-9)),
            new ChatLine(ChatLineKind.Coordinator,
                "Starting a worker on the auth path — it will inspect the repo and write its own plan.",
                DateTimeOffset.Now.AddMinutes(-9)),
            new ChatLine(ChatLineKind.ToolCall, "spawn_worker(title=\"Fix token expiry\")", DateTimeOffset.Now.AddMinutes(-8)),
            new ChatLine(ChatLineKind.SystemLine, systemLine, DateTimeOffset.Now.AddMinutes(-2)),
        };

        private static WorkerPlanCard Card(
            int revision, int remaining, string status, string feedback, string worker = "loom-4") => new(
            "plan-7", worker, "coordinator", "Fix token expiry off-by-one",
            new[] { "src/Auth/TokenClock.cs", "src/Auth/RefreshService.cs", "tests/AuthTests.cs" },
            "Extract the clock behind ITokenClock; inject a fixed clock in tests; correct the expiry comparison.",
            "AuthTests green plus two new expiry-boundary cases.",
            1.50m, DateTimeOffset.Now.AddMinutes(-2), status, revision, remaining, 3, feedback);

        public IReadOnlyList<ChatLine> GetTranscript() => _transcript;

        public IReadOnlyList<TaskPlan> GetPendingPlans() => _cards
            .Where(c => c.IsPending)
            .Select(c => new TaskPlan(c.PlanId, c.Title, c.Scope, c.Approach, c.TestStrategy, c.BudgetUsd, c.PresentedAt))
            .ToArray();

        public TaskPlan? GetPlan(string planId) => GetPendingPlans().FirstOrDefault(p => p.PlanId == planId);

        public IReadOnlyList<WorkerPlanCard> GetWorkerPlans() => _cards;

        public OrchestrationBackpressure GetBackpressure() => _backpressure;

        public event Action? Changed { add { } remove { } }

        public Task SendAsync(string text) => Task.CompletedTask;

        public Task SubmitPlanDecisionAsync(string planId, bool approve, string? feedback = null) => Task.CompletedTask;
    }
}
