using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Mainguard.UI.Views;

/// <summary>
/// The per-platform window-chrome policy for client-area-extended windows. Windows/Linux keep
/// the hand-drawn <c>NoChrome</c> title bar with its own minimize/maximize/close buttons; on
/// macOS <c>NoChrome</c> removes the traffic lights entirely — there is no other way to close
/// the window — so the system chrome is overlaid instead and the hand-drawn buttons yield:
/// they hide, and the title-bar content shifts right past the traffic-light cluster.
/// </summary>
public static class WindowChromePolicy
{
    /// <summary>Overlay the system chrome on macOS; leave the window's own hints elsewhere.</summary>
    public static void Apply(Window window)
    {
        if (System.OperatingSystem.IsMacOS())
        {
            window.ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.PreferSystemChrome;
        }
    }

    /// <summary>False on macOS, where the native traffic lights replace the hand-drawn buttons.</summary>
    public static bool CustomButtonsVisible => !System.OperatingSystem.IsMacOS();

    /// <summary>False on macOS, where File/Repository/View/Help live in the system menu bar instead
    /// of the hand-drawn title bar's hamburger-toggled toolbar — every item there is a menu or
    /// flyout, so on macOS it would only duplicate what the native menu bar already offers.</summary>
    public static bool InWindowMenuVisible => !System.OperatingSystem.IsMacOS();

    /// <summary>
    /// Title-bar padding: on macOS the leading edge starts past the traffic-light cluster
    /// (~70 px at 1x) so no content sits underneath it.
    /// </summary>
    public static Thickness TitleBarPadding(Thickness normal) =>
        System.OperatingSystem.IsMacOS()
            ? new Thickness(76, normal.Top, normal.Right, normal.Bottom)
            : normal;
}
