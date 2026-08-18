using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Mainguard.UI.Views;

/// <summary>
/// Base Window for every secondary dialog/panel (#77) — extends the client area over the OS
/// decorations the same way MainWindow does, so CustomTitleBar can render one consistent
/// hand-drawn title bar (drag/minimize/maximize/close) across the whole app instead of each
/// window falling back to plain OS chrome.
/// </summary>
public class ChromedWindow : Window
{
    public ChromedWindow()
    {
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = -1;
        // macOS: NoChrome would remove the traffic lights — the only close control there.
        WindowChromePolicy.Apply(this);
    }

    /// <summary>Wired by CustomTitleBar's PointerPressed so any window using it gets window-drag for free.</summary>
    public void BeginTitleBarDrag(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    /// <summary>Wired by CustomTitleBar's DoubleTapped.</summary>
    public void ToggleMaximizeFromTitleBar() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
