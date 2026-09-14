using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// The loading-screen offer to stop another Mainguard daemon that is holding the loopback port.
///
/// <para>Hosted in the startup surface exactly like <see cref="VmUpgradeOfferViewModel"/>, and for the
/// same reason: the app is about to do something irreversible to something the user did not name, so it
/// is always an explicit press. Before this existed, the symptom of an orphaned daemon was a bind
/// failure in the terminal and a stack trace — the app could not start, could not say why in its own
/// window, and the only way out was <c>lsof</c> and <c>kill</c> by hand.</para>
///
/// <para>The two cases are not presented the same way. A holder whose payload is <b>gone from disk</b>
/// is unambiguously stale — it can never be restarted, updated or stopped by its own controller — and
/// is presented as safe to stop. A holder from a build that still exists might be a second daemon
/// someone is deliberately running, so it is presented as a choice with the consequence named.</para>
/// </summary>
public partial class DaemonPortConflictViewModel : ViewModelBase
{
    private readonly Func<CancellationToken, Task<bool>>? _stop;

    /// <summary>Live constructor.</summary>
    /// <param name="holder">The observed port holder — the same one the probe saw.</param>
    /// <param name="stop">Stops that holder; true when it is really gone.</param>
    public DaemonPortConflictViewModel(DaemonPortHolder holder, Func<CancellationToken, Task<bool>> stop)
        : this(holder)
    {
        _stop = stop ?? throw new ArgumentNullException(nameof(stop));
    }

    /// <summary>Design/render constructor: no action behind the buttons.</summary>
    public DaemonPortConflictViewModel(DaemonPortHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        Pid = holder.Pid;
        PayloadPath = holder.PayloadDllPath;
        PayloadIsGone = holder.PayloadIsGone;
    }

    /// <summary>The holding process id.</summary>
    public int Pid { get; }

    /// <summary>The build it was launched from, when the command line named one.</summary>
    public string? PayloadPath { get; }

    /// <summary>True when that build no longer exists — the unambiguously-stale case.</summary>
    public bool PayloadIsGone { get; }

    /// <summary>The headline, which states the finding rather than the remedy.</summary>
    public string Headline => PayloadIsGone
        ? "A leftover Mainguard daemon is still running"
        : "Another Mainguard daemon is already running";

    /// <summary>What was observed and what stopping it would mean — never remedy without evidence.</summary>
    public string Explanation => PayloadIsGone
        ? $"Process {Pid} is using the port Mainguard needs. It was started from "
          + $"'{PayloadPath}', which no longer exists on disk, so nothing can restart or update it and "
          + "it will keep holding the port until it is stopped. Stopping it is safe."
        : $"Process {Pid} is using the port Mainguard needs, and this app cannot authenticate to it. "
          + "It was started from a different Mainguard build. Stopping it will end any agents it is "
          + "running.";

    /// <summary>The primary action's label — the consequence is in <see cref="Explanation"/>.</summary>
    public string StopLabel => PayloadIsGone ? "Stop it and continue" : "Stop it anyway and continue";

    /// <summary>True while the stop is in flight; both buttons disable.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaterCommand))]
    private bool _isBusy;

    /// <summary>Set when the stop did not work, so the surface never implies a fix that did not happen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    /// <summary>Whether a failed attempt is being reported.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>Invoked with the outcome: true when the holder was stopped, false when the user declined.</summary>
    public Action<bool>? CloseAction { get; set; }

    /// <summary>Optional breadcrumb sink (the app's oobe.log writer).</summary>
    public Action<string>? LogSink { get; set; }

    private bool CanAct() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task StopAsync()
    {
        if (_stop is null)
        {
            return;
        }

        IsBusy = true;
        ErrorText = null;
        try
        {
            var stopped = await _stop(CancellationToken.None).ConfigureAwait(true);
            LogSink?.Invoke($"startup: stop of port holder pid {Pid} {(stopped ? "succeeded" : "failed")}");

            if (!stopped)
            {
                // Report the attempt's own failure; never close as though it worked.
                ErrorText =
                    $"Could not stop process {Pid}. You can stop it from a terminal with "
                    + $"`kill {Pid}`, then start Mainguard again.";
                return;
            }

            CloseAction?.Invoke(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Later()
    {
        LogSink?.Invoke($"startup: user declined to stop port holder pid {Pid}");
        CloseAction?.Invoke(false);
    }
}
