using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.ViewModels;

/// <summary>
/// Worktree management panel (T-21) over the T-07 porcelain backend
/// (<see cref="IGitService.ListWorktrees"/> / <see cref="IGitService.AddWorktree"/> /
/// <see cref="IGitService.RemoveWorktree"/> / <see cref="IGitService.PruneWorktrees"/>). Lists each
/// worktree (path, branch/detached, locked, main) and creates one from an existing branch or a
/// new branch. <b>Validation:</b> creating a worktree checking out a branch already checked out in
/// another worktree is impossible in git, so <see cref="CanCreate"/> is false (the button disables)
/// when the selected existing branch is already checked out. All git work runs off the UI thread;
/// typed failures surface as <see cref="ErrorMessage"/>. Hosted by WorktreeWindow.
/// </summary>
public partial class WorktreePanelViewModel : ViewModelBase
{
    private readonly IGitService _git;
    private readonly string _repoPath;
    private readonly Action<string>? _onOpenWorktree;
    private readonly Mainguard.App.Shell.Services.IConfirmationService _confirm;

    public ObservableCollection<WorktreeRowViewModel> Worktrees { get; } = new();

    /// <summary>Local branch names available to base a new worktree on.</summary>
    public ObservableCollection<string> Branches { get; } = new();

    [ObservableProperty]
    private string? _selectedBranch;

    [ObservableProperty]
    private string _newWorktreePath = "";

    /// <summary>When true, create a new branch (named <see cref="NewBranchName"/>) for the worktree.</summary>
    [ObservableProperty]
    private bool _createBranch;

    [ObservableProperty]
    private string _newBranchName = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    public Action? CloseAction { get; set; }

    public WorktreePanelViewModel(IGitService git, string repoPath, Action<string>? onOpenWorktree = null,
        Mainguard.App.Shell.Services.IConfirmationService? confirm = null)
    {
        _git = git;
        _repoPath = repoPath;
        _onOpenWorktree = onOpenWorktree;
        _confirm = confirm ?? new Mainguard.App.Shell.Services.DialogConfirmationService();
        Reload();
    }

    /// <summary>Branches currently checked out by some worktree (main included) — cannot be checked out again.</summary>
    private HashSet<string> CheckedOutBranches =>
        Worktrees.Where(w => !string.IsNullOrEmpty(w.Branch))
                 .Select(w => w.Branch!)
                 .ToHashSet(StringComparer.Ordinal);

    /// <summary>True when the chosen existing branch is already checked out somewhere (blocks create).</summary>
    public bool SelectedBranchIsCheckedOut =>
        !CreateBranch && !string.IsNullOrEmpty(SelectedBranch) && CheckedOutBranches.Contains(SelectedBranch!);

    /// <summary>Whether the current form is a valid worktree-create request (drives the button's enabled state).</summary>
    public bool CanCreate
    {
        get
        {
            if (IsBusy || string.IsNullOrWhiteSpace(NewWorktreePath)) return false;
            if (CreateBranch) return !string.IsNullOrWhiteSpace(NewBranchName);
            // Existing branch: must be selected and NOT already checked out (git forbids the double checkout).
            return !string.IsNullOrEmpty(SelectedBranch) && !SelectedBranchIsCheckedOut;
        }
    }

    partial void OnSelectedBranchChanged(string? value) => RecomputeCreate();
    partial void OnNewWorktreePathChanged(string value) => RecomputeCreate();
    partial void OnCreateBranchChanged(bool value) => RecomputeCreate();
    partial void OnNewBranchNameChanged(string value) => RecomputeCreate();
    partial void OnIsBusyChanged(bool value) => RecomputeCreate();

    private void RecomputeCreate()
    {
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(SelectedBranchIsCheckedOut));
    }

    private void Reload()
    {
        Worktrees.Clear();
        Branches.Clear();
        try
        {
            foreach (var w in _git.ListWorktrees(_repoPath))
                Worktrees.Add(new WorktreeRowViewModel(w, this));
            foreach (var b in _git.GetBranches(_repoPath).Where(b => !b.IsRemote))
                if (!Branches.Contains(b.FriendlyName))
                    Branches.Add(b.FriendlyName);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        RecomputeCreate();
    }

    [RelayCommand]
    private void Refresh() => Reload();

    [RelayCommand]
    private async Task Create()
    {
        if (!CanCreate) return;
        var branch = CreateBranch ? NewBranchName.Trim() : SelectedBranch!;
        var path = NewWorktreePath.Trim();
        var create = CreateBranch;
        await RunAsync(() => _git.AddWorktree(_repoPath, path, branch, create));
        if (ErrorMessage is null)
        {
            NewWorktreePath = "";
            NewBranchName = "";
        }
    }

    internal async Task RemoveAsync(WorktreeRowViewModel row, bool force)
    {
        // A plain remove is safe — git refuses it when the worktree has changes. Force is the one
        // that throws away uncommitted work, so it always confirms first, and it fails closed:
        // no desktop lifetime (headless) → DialogConfirmationService declines and nothing is lost.
        if (force)
        {
            bool confirmed = await _confirm.ConfirmAsync(
                "Force remove worktree",
                $"Force-remove the worktree at '{row.Path}'?\n" +
                "Any uncommitted changes in it are discarded permanently and cannot be recovered.",
                "Force Remove");
            if (!confirmed) return;
        }

        await RunAsync(() => _git.RemoveWorktree(_repoPath, row.Path, force));
    }

    [RelayCommand]
    private async Task Prune() => await RunAsync(() => _git.PruneWorktrees(_repoPath));

    internal void Open(WorktreeRowViewModel row) => _onOpenWorktree?.Invoke(row.Path);

    private async Task RunAsync(Action mutate)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await Task.Run(mutate);
            Reload();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}

/// <summary>One worktree row: path + branch/detached/locked/main state and per-row open / remove.</summary>
public partial class WorktreeRowViewModel : ViewModelBase
{
    private readonly WorktreePanelViewModel _parent;

    public string Path { get; }
    public string? Branch { get; }
    public bool IsDetached { get; }
    public bool IsLocked { get; }
    public bool IsMain { get; }
    public string ShortHead { get; }

    public WorktreeRowViewModel(WorktreeItem item, WorktreePanelViewModel parent)
    {
        _parent = parent;
        Path = item.Path;
        Branch = item.Branch;
        IsDetached = item.IsDetached;
        IsLocked = item.IsLocked;
        IsMain = item.IsMain;
        ShortHead = string.IsNullOrEmpty(item.HeadSha) || item.HeadSha!.Length <= 7
            ? item.HeadSha ?? ""
            : item.HeadSha!.Substring(0, 7);
    }

    /// <summary>Branch friendly name, or "(detached)" when the worktree is on a detached HEAD.</summary>
    public string BranchLabel => IsDetached ? "(detached)" : (Branch ?? "");

    /// <summary>The main worktree can't be removed from here — only linked worktrees.</summary>
    public bool CanRemove => !IsMain;

    [RelayCommand]
    private void Open() => _parent.Open(this);

    [RelayCommand]
    private Task Remove() => _parent.RemoveAsync(this, force: false);

    /// <summary>Removes a worktree git refuses to remove because it is dirty. Bound to the "Force"
    /// button next to Remove — without it a dirty worktree could not be removed from the app at
    /// all, while Remove's own tooltip told the user to "use Force".</summary>
    [RelayCommand]
    private Task ForceRemove() => _parent.RemoveAsync(this, force: true);
}
