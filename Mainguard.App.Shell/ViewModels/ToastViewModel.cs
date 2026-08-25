using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.ViewModels;

/// <summary>
/// One toast notification (#85): message + severity, a manual close, and an expand toggle for
/// long messages. Owns its own auto-dismiss timer so multiple toasts age independently instead of
/// one shared timer resetting whenever a new notification arrives.
/// </summary>
public partial class ToastViewModel : ViewModelBase, IDisposable
{
    private readonly Action<ToastViewModel> _onDismiss;
    private System.Threading.Timer? _timer;

    public string Message { get; }
    public bool IsError { get; }

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Test seam: the headless render harness stacks toasts and then pumps for an
    /// unbounded amount of WALL-CLOCK time on a loaded CI runner, so the 6 s auto-dismiss firing
    /// mid-capture turns its count assertion into a race. The harness parks this high and resets
    /// it; production never sets it.</summary>
    internal static int? AutoDismissOverrideMs;

    public ToastViewModel(string message, bool isError, Action<ToastViewModel> onDismiss, int autoDismissMs = 6000)
    {
        Message = message;
        IsError = isError;
        _onDismiss = onDismiss;
        var effectiveMs = AutoDismissOverrideMs ?? autoDismissMs;
        _timer = new System.Threading.Timer(_ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(Dismiss), null, effectiveMs, System.Threading.Timeout.Infinite);
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void Dismiss()
    {
        Dispose();
        _onDismiss(this);
    }

    public void Dispose() => _timer?.Dispose();
}
