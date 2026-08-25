using System;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Mainguard.App.Shell.Controls;

/// <summary>
/// The clickable surface of one repository row in <c>RepoPickerWindow</c> (W2). The row used to be a
/// plain <see cref="Grid"/> carrying raw pointer handlers, which made it invisible to UI Automation:
/// the only thing a screen reader (or any automation driver) could see was the bare
/// <c>ControlType.Text</c> of the name <c>TextBlock</c>, with no <c>InvokePattern</c> anywhere up the
/// ancestor chain — so the picker's whole list of repositories had no keyboard or assistive-tech
/// activation path at all, only a mouse double-click.
///
/// This subclass keeps the row a Grid (so the existing pointer/drag/context-menu wiring, the styles,
/// and the two-column layout are all untouched) and adds the one thing that was missing: a real
/// activation path. <see cref="Activate"/> raises <see cref="ActivatedEvent"/>, and all three entry
/// points funnel into it — the Enter/Space key when the row has keyboard focus, UI Automation's
/// <see cref="IInvokeProvider.Invoke"/>, and the window's own double-tap handler. A <see cref="Button"/>
/// wrapper would have given the peer for free but would also have swallowed the pointer press that
/// drives select-then-drag, so the peer is written by hand instead.
/// </summary>
public class RepoRow : Grid
{
    /// <summary>Raised when the row is activated (Enter/Space, or UI Automation <c>Invoke</c>).</summary>
    public static readonly RoutedEvent<RoutedEventArgs> ActivatedEvent =
        RoutedEvent.Register<RepoRow, RoutedEventArgs>(nameof(Activated), RoutingStrategies.Bubble);

    public event EventHandler<RoutedEventArgs>? Activated
    {
        add => AddHandler(ActivatedEvent, value);
        remove => RemoveHandler(ActivatedEvent, value);
    }

    public RepoRow()
    {
        // Makes the row a tab stop and reports IsKeyboardFocusable=true to UI Automation.
        Focusable = true;
    }

    /// <summary>The single activation path shared by the keyboard and UI Automation.</summary>
    public void Activate() => RaiseEvent(new RoutedEventArgs(ActivatedEvent, this));

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            Activate();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RepoRowAutomationPeer(this);
}

/// <summary>
/// Exposes a <see cref="RepoRow"/> as an invokable list item so UI Automation — and therefore screen
/// readers — can open a repository. The accessible name comes from <c>AutomationProperties.Name</c>,
/// bound to the repository's <c>DisplayName</c> in the row's DataTemplate.
/// </summary>
public sealed class RepoRowAutomationPeer : ControlAutomationPeer, IInvokeProvider
{
    public RepoRowAutomationPeer(RepoRow owner) : base(owner)
    {
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    public void Invoke() => ((RepoRow)Owner).Activate();
}
