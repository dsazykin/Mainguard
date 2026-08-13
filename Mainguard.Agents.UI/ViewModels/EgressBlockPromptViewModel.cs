using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// Fix 2 — the egress block-notification prompt. An agent's CLI died because the default-deny sandbox
/// proxy refused <see cref="Host"/>; this names what was blocked and which agent needed it, and offers
/// <b>Unblock</b> (add the host to the allowlist + retry) or <b>Keep blocked</b> (dismiss). An instrument,
/// not a nag: the user opts IN to widening egress — Keep blocked is the safe default.
/// </summary>
public sealed partial class EgressBlockPromptViewModel : ViewModelBase
{
    private readonly Func<string, Task> _unblock;
    private readonly Action _dismiss;
    private readonly Func<Task>? _manageAllowlist;

    public string Host { get; }
    public string AgentLabel { get; }
    public string Message { get; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _error = "";

    /// <param name="unblock">Adds <paramref name="host"/> to the allowlist and retries; the owner clears the
    /// prompt on success. Throwing surfaces its message here and leaves the prompt open.</param>
    /// <param name="dismiss">Keep-blocked / close.</param>
    /// <param name="manageAllowlist">Opens the full allowlist editor. Optional so the prompt still works
    /// where there is no editor to open, but the two-button version of this prompt was a trap: Unblock
    /// can only ever add the ONE host the block-detector parsed out of a death message, and a CLI that
    /// needs two hosts (or one the detector got slightly wrong) left the operator with no next move at
    /// all — the editor was unreachable from anywhere in the app.</param>
    public EgressBlockPromptViewModel(
        string host, string agentLabel, Func<string, Task> unblock, Action dismiss,
        Func<Task>? manageAllowlist = null)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        AgentLabel = string.IsNullOrWhiteSpace(agentLabel) ? "An agent" : agentLabel;
        _unblock = unblock ?? throw new ArgumentNullException(nameof(unblock));
        _dismiss = dismiss ?? throw new ArgumentNullException(nameof(dismiss));
        _manageAllowlist = manageAllowlist;
        Message = $"{AgentLabel} couldn't reach {Host} — the sandbox's default-deny egress blocked it.";
    }

    /// <summary>Whether the "Manage allowlist…" affordance is offered.</summary>
    public bool CanManageAllowlist => _manageAllowlist is not null;

    [RelayCommand]
    private async Task ManageAllowlistAsync()
    {
        if (_manageAllowlist is null) return;
        _dismiss(); // the editor supersedes this prompt — two overlapping surfaces on one decision
        await _manageAllowlist();
    }

    [RelayCommand]
    private async Task UnblockAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = "";
        try
        {
            await _unblock(Host); // add to allowlist + retry; the owner dismisses on success
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            IsBusy = false; // stay open so the user can retry or keep it blocked
        }
    }

    [RelayCommand]
    private void KeepBlocked() => _dismiss();
}
