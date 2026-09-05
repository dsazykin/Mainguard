using Avalonia.Controls;

namespace Mainguard.Agents.UI.Views;

/// <summary>
/// The worker-authored plan gate, hostable on its own. Binds <see cref="ViewModels.CoordinatorPanelViewModel"/>
/// but renders only the decisions — no transcript, no composer — so the shipped Control Center can put the
/// gate above the coordinator's terminal without a second, dead chat box beside a live one.
/// </summary>
public partial class PlanGateView : UserControl
{
    public PlanGateView()
    {
        InitializeComponent();
    }
}
