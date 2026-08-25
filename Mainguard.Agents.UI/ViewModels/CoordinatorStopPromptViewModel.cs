using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// Confirm-before-stop for the coordinator. Stopping fully terminates the coordinator's CLI + sandbox
/// (and cancels a launch still in progress), so it asks first — a full teardown is worth one deliberate
/// click. Confirm runs the teardown (the owner keeps the overlay up on its busy spinner and clears it
/// when done); Cancel dismisses with nothing changed.
/// </summary>
public sealed partial class CoordinatorStopPromptViewModel : ViewModelBase
{
    private readonly Func<Task> _confirm;
    private readonly Action _cancel;

    public string Title { get; }
    public string Message { get; }

    /// <summary>The confirm button label — "Stop coordinator" for a live one, "Cancel startup" mid-launch.</summary>
    public string ConfirmLabel { get; }

    [ObservableProperty] private bool _isBusy;

    public CoordinatorStopPromptViewModel(string title, string message, string confirmLabel, Func<Task> confirm, Action cancel)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        ConfirmLabel = string.IsNullOrWhiteSpace(confirmLabel) ? "Stop coordinator" : confirmLabel;
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        _cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true; // the owner tears down, then clears this prompt (StopPrompt = null)
        try
        {
            await _confirm();
        }
        catch (Exception)
        {
            // ISSUES-LOG #12: [RelayCommand] awaits this in an `async void` helper, so an escaping
            // exception is re-thrown onto the synchronization context and aborts the process. The owner's
            // teardown reports its own failures; all this must do is not be fatal — and not strand the
            // overlay on a spinner that can never be dismissed.
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (!IsBusy)
        {
            _cancel();
        }
    }
}
