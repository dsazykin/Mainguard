using Avalonia.Interactivity;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.UI.Views;

namespace Mainguard.Agents.UI.Views;

public partial class MacOobeWindow : ChromedWindow
{
    public MacOobeWindow()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // Kick the checks once the window is really on screen — a ctor-time kick would race the
        // dispatcher on the very first frame.
        (DataContext as MacOobeViewModel)?.RunChecksCommand.Execute(null);
    }
}
