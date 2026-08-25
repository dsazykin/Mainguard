using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Mainguard.UI.Views;

namespace Mainguard.UI.Controls;

public partial class CustomTitleBar : UserControl
{
    public CustomTitleBar()
    {
        InitializeComponent();
        // macOS shows the native traffic lights over the extended client area
        // (WindowChromePolicy), so the hand-drawn buttons yield and the content
        // starts past the traffic-light cluster.
        WindowButtons.IsVisible = WindowChromePolicy.CustomButtonsVisible;
        TitleBarRoot.Padding = WindowChromePolicy.TitleBarPadding(TitleBarRoot.Padding);
    }

    private ChromedWindow? Host => this.VisualRoot as ChromedWindow;

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e) => Host?.BeginTitleBarDrag(e);

    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e) => Host?.ToggleMaximizeFromTitleBar();

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (Host is { } host) host.WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object? sender, RoutedEventArgs e) => Host?.ToggleMaximizeFromTitleBar();

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Host?.Close();
}
