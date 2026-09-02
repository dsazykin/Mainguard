using Avalonia.Controls;

namespace Mainguard.Agents.UI.Views;

public partial class ControlCenterView : UserControl
{
    // The height the telemetry row was left at while it was last on screen.
    private GridLength _parkedTelemetryHeight = GridLength.Auto;

    // The same for the plan gate's row (the GateSeam GridSplitter above the terminal).
    private GridLength _parkedGateHeight = GridLength.Auto;

    public ControlCenterView()
    {
        InitializeComponent();

        // The telemetry card's row is draggable (the TelemetrySeam GridSplitter), and a GridSplitter
        // rewrites the row it resizes from Auto to a PIXEL length the moment it is first dragged. A
        // pixel row does NOT collapse when its only child hides, so switching to Conversation Deck —
        // which hides telemetry — would leave a hole in the rail exactly as tall as the panel the user
        // had just resized, stealing that space from the merge queue. Park the height while the card is
        // hidden, hand it back when the card returns.
        TelemetryCard.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            // x:Name on a RowDefinition generates no field (it is not a Control), so reach the row the
            // card actually occupies through its parent grid.
            if (TelemetryCard.Parent is not Grid rail) return;
            var index = Grid.GetRow(TelemetryCard);
            if (index < 0 || index >= rail.RowDefinitions.Count) return;
            var row = rail.RowDefinitions[index];

            if (TelemetryCard.IsVisible)
            {
                row.Height = _parkedTelemetryHeight;
            }
            else
            {
                _parkedTelemetryHeight = row.Height;
                row.Height = GridLength.Auto;
            }
        };

        // Identical defect, other seam. The gate row is Auto (an idle gate costs nothing) until the
        // GateSeam is dragged, after which it is a pixel row — and a pixel row keeps its height when the
        // gate hides (the last plan cleared), leaving a dead band above the terminal for the rest of the
        // session. Park while hidden, restore when the gate returns.
        GateHost.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (GateHost.Parent is not Grid pane) return;
            var index = Grid.GetRow(GateHost);
            if (index < 0 || index >= pane.RowDefinitions.Count) return;
            var row = pane.RowDefinitions[index];

            if (GateHost.IsVisible)
            {
                row.Height = _parkedGateHeight;
            }
            else
            {
                _parkedGateHeight = row.Height;
                row.Height = GridLength.Auto;
            }
        };
    }
}
