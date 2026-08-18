using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git.Models;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateMaximizeIcon();
        // macOS: overlay the system chrome (the axaml asks for NoChrome, which would remove
        // the traffic lights), hide the hand-drawn buttons, clear the traffic-light cluster.
        Mainguard.UI.Views.WindowChromePolicy.Apply(this);
        WindowButtonsPanel.IsVisible = Mainguard.UI.Views.WindowChromePolicy.CustomButtonsVisible;
        TitleBarBorder.Padding = Mainguard.UI.Views.WindowChromePolicy.TitleBarPadding(TitleBarBorder.Padding);
    }

    /// <summary>Close-to-tray: the X hides the window (the app — and any running agents — keep
    /// going) unless a FULL exit is underway (tray menu / File > Exit) or the user turned the
    /// setting off. Full exit is what stops the VM; hiding never does. With close-to-tray OFF the
    /// X IS a full exit, so it routes through the guarded path — a VM-stopping exit under live
    /// agents confirms first (PR3).</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (App.IsExiting)
        {
            return;
        }

        if (App.Settings.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // X as full exit: cancel the raw close and run the guarded exit instead (it shuts the
        // lifetime down — or leaves everything running if the user declines the agent warning).
        e.Cancel = true;
        _ = App.RequestFullExitGuardedAsync();
    }

    // --- Custom title-bar chrome (client-area window controls) ---

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            UpdateMaximizeIcon();
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Drag the window on a primary-button press over empty title-bar space;
        // interactive children (buttons, menu) capture the pointer and never reach here.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximize();

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximizeIcon()
    {
        if (MaximizeIcon is null) return;
        var key = WindowState == WindowState.Maximized ? "WindowRestoreIcon" : "WindowMaximizeIcon";
        if (this.TryFindResource(key, out var res) && res is Avalonia.Media.Geometry geo)
            MaximizeIcon.Data = geo;
    }

    // Close the palette only when the click lands on the scrim itself, not on the palette card.
    private void CommandPaletteScrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender) && DataContext is MainWindowViewModel vm)
            vm.IsCommandPaletteOpen = false;
    }

    // --- Global keyboard shortcuts (T-18): built from the ShortcutMap so rebinds take effect ---

    private MainWindowViewModel? _boundVm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_boundVm is not null)
        {
            _boundVm.CommandPaletteOpened -= FocusPalette;
            _boundVm.ShortcutsChanged -= RebuildShortcuts;
        }

        _boundVm = DataContext as MainWindowViewModel;
        if (_boundVm is not null)
        {
            _boundVm.CommandPaletteOpened += FocusPalette;
            _boundVm.ShortcutsChanged += RebuildShortcuts;
            BuildShortcutBindings(_boundVm);
        }
    }

    private void RebuildShortcuts()
    {
        if (_boundVm is not null) BuildShortcutBindings(_boundVm);
    }

    private void FocusPalette()
    {
        // Let the overlay become visible/laid-out before grabbing focus.
        Dispatcher.UIThread.Post(() =>
        {
            this.FindControl<CommandPaletteView>("CommandPalette")?.FocusInput();
        }, DispatcherPriority.Loaded);
    }

    // Translate the effective ShortcutMap (defaults + user overrides) into window KeyBindings that route
    // each gesture to the action registry via InvokeActionByIdCommand.
    public void BuildShortcutBindings(MainWindowViewModel vm)
    {
        // Preserve the declarative Escape binding, drop any previously built shortcut rows.
        for (int i = KeyBindings.Count - 1; i >= 0; i--)
        {
            if (KeyBindings[i].CommandParameter is string)
                KeyBindings.RemoveAt(i);
        }

        foreach (var kv in vm.Shortcuts.Bindings)
        {
            if (string.IsNullOrWhiteSpace(kv.Value)) continue;
            try
            {
                var gesture = KeyGesture.Parse(kv.Value);
                KeyBindings.Add(new KeyBinding
                {
                    Gesture = gesture,
                    Command = vm.InvokeActionByIdCommand,
                    CommandParameter = kv.Key,
                });
            }
            catch
            {
                // Ignore an unparseable/persisted-bad gesture rather than crashing the window.
            }
        }
    }
}
