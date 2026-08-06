using Avalonia.Controls;

namespace Mainguard.Agents.UI.Views;

public partial class ControlCenterView : UserControl
{
    // The height the telemetry row was left at while it was last on screen.
    private GridLength _parkedTelemetryHeight = GridLength.Auto;

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
    }
}
