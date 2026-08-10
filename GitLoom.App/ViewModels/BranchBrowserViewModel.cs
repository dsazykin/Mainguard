using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

public class SeparatorViewModel : MenuItemViewModel
{
    public SeparatorViewModel()
    {
        Header = "-";
        IsEnabled = false;
    }
}

public partial class BranchCategoryViewModel : ObservableObject
{
    [ObservableProperty]
    private string _categoryName = string.Empty;

    [ObservableProperty]
    private ObservableCollection<MenuItemViewModel> _branches = new();

    [ObservableProperty]
    private bool _isExpanded = false;
}

public partial class BranchBrowserViewModel : ViewModelBase
{
    private readonly IGitService _gitService;
    private readonly string _repoPath;
    private readonly Action? _onBranchChangedAction;
    private readonly Action<string>? _showNotificationAction;
    private readonly Action<string>? _onCompareBranchAction;
    // T-23: opens the Pull Requests panel straight into "create" for the current branch.
    private readonly Action? _onCreatePullRequestAction;
    private readonly Action<GitBranchItem>? _onCheckoutInWorktreeAction;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private ObservableCollection<BranchCategoryViewModel> _branchCategories = new();

    [ObservableProperty]
    private string _currentBranchName = "Branches";

    public BranchBrowserViewModel(IGitService gitService, string repoPath, Action? onBranchChangedAction = null, Action<string>? showNotificationAction = null, Action<string>? onCompareBranchAction = null, Action? onCreatePullRequestAction = null, Action<GitBranchItem>? onCheckoutInWorktreeAction = null)
    {
        _gitService = gitService;
        _repoPath = repoPath;
        _onBranchChangedAction = onBranchChangedAction;
        _showNotificationAction = showNotificationAction;
        _onCompareBranchAction = onCompareBranchAction;
        _onCreatePullRequestAction = onCreatePullRequestAction;
        _onCheckoutInWorktreeAction = onCheckoutInWorktreeAction;
    }

    // T-29: branch-context "Check out in new worktree" — fetches nothing, just creates a worktree
    // checked out to this (local or remote-tracking) branch via the open-worktree flow in the dashboard.
    [RelayCommand]
    private void CheckoutInWorktree(GitBranchItem branch) => _onCheckoutInWorktreeAction?.Invoke(branch);

    // T-23: branch-context "Create pull request" — opens the PR panel's create form (prefilled with
    // the current branch as source). The branch argument is accepted for menu symmetry.
    [RelayCommand]
    private void CreatePullRequest(GitBranchItem branch) => _onCreatePullRequestAction?.Invoke();

    public void LoadBranches()
    {
        var branches = _gitService.GetBranches(_repoPath).ToList();
        var currentBranch = branches.FirstOrDefault(b => b.IsCurrentRepositoryHead);
        string currentBranchName = currentBranch?.FriendlyName ?? "Branches";
        CurrentBranchName = currentBranchName;

        var localBranchLookup = new Dictionary<string, MenuItemViewModel>(StringComparer.Ordinal);
        var localViewModels = new ObservableCollection<MenuItemViewModel>();
        foreach (var b in branches.Where(x => !x.IsRemote).OrderBy(x => x.FriendlyName))
        {
            var vm = CreateLocalBranchMenu(b, currentBranchName);
            localViewModels.Add(vm);
            localBranchLookup[b.FriendlyName] = vm;
        }

        var remoteViewModels = new ObservableCollection<MenuItemViewModel>();
        foreach (var b in branches.Where(x => x.IsRemote).OrderBy(x => x.FriendlyName))
        {
            remoteViewModels.Add(CreateRemoteBranchMenu(b, currentBranchName));
        }

        var tagViewModels = new ObservableCollection<MenuItemViewModel>();
        foreach (var t in _gitService.GetTags(_repoPath).OrderBy(x => x.Name))
        {
            tagViewModels.Add(CreateTagMenu(t));
        }

        // Issue #70: "Recent" is derived from actual checkout recency (HEAD reflog), not an
        // alphabetical slice of the local branch list. Falls back to alphabetical order to fill
        // any remaining slots when the reflog is shallow/fresh or its checkouts don't resolve to
        // branches that still exist.
        var recentViewModels = new ObservableCollection<MenuItemViewModel>();
        try
        {
            var reflog = _gitService.GetReflog(_repoPath, "HEAD", 200);
            var recentNames = RecentBranchResolver.Resolve(
                reflog,
                localBranchLookup.Keys,
                localBranchLookup.Keys.OrderBy(n => n, StringComparer.Ordinal),
                take: 3);
            foreach (var name in recentNames)
            {
                if (localBranchLookup.TryGetValue(name, out var vm))
                {
                    recentViewModels.Add(vm);
                }
            }
        }
        catch
        {
            // Reflog unavailable (e.g. brand-new repo) — fall back to the old alphabetical slice
            // rather than leaving "Recent" empty.
            foreach (var vm in localViewModels.Take(3))
            {
                recentViewModels.Add(vm);
            }
        }

        var oldCategories = BranchCategories.ToDictionary(c => c.CategoryName, c => c.IsExpanded);

        // Issue #71: group Local/Remote/Recent by slash-delimited subfolder instead of one flat
        // list per section (Tags stays flat — not called out in the issue and a flat list of tags
        // is what most repos actually want). GroupIntoTree reuses the same MenuItemViewModel leaf
        // instances built above so their Command/SubItems (the per-branch action flyout) are
        // untouched; it only adds folder wrapper nodes and adjusts DisplayHeader for nested leaves.
        var newCategories = new ObservableCollection<BranchCategoryViewModel>
        {
            // Recent is the reflog-derived recency list (#70), then grouped by subfolder (#71) like
            // Local/Remote; Tags stays flat.
            new BranchCategoryViewModel { CategoryName = "Recent", Branches = GroupIntoTree(recentViewModels) },
            new BranchCategoryViewModel { CategoryName = "Local", Branches = GroupIntoTree(localViewModels) },
            new BranchCategoryViewModel { CategoryName = "Remote", Branches = GroupIntoTree(remoteViewModels) },
            new BranchCategoryViewModel { CategoryName = "Tags", Branches = tagViewModels }
        };

        foreach (var category in newCategories)
        {
            if (oldCategories.TryGetValue(category.CategoryName, out var wasExpanded))
            {
                category.IsExpanded = wasExpanded;
            }
            else
            {
                category.IsExpanded = GitLoom.App.App.Settings.Current.SidebarExpandedStates.GetValueOrDefault("Branch_" + category.CategoryName, false);
            }

            category.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(BranchCategoryViewModel.IsExpanded))
                {
                    GitLoom.App.App.Settings.Update(p => p.SidebarExpandedStates["Branch_" + category.CategoryName] = category.IsExpanded);
                }
            };
        }

        BranchCategories = newCategories;

        ErrorMessage = string.Empty;
    }

    // Issue #71: groups a flat list of branch-row MenuItemViewModels (Header = the branch's full
    // friendly name) into a nested tree via the pure Core BranchTreeBuilder, reusing the given
    // instances as leaves (so their Command/SubItems context menu is unaffected) and synthesizing
    // non-clickable folder nodes (IsFolder = true) for shared path segments.
    private static ObservableCollection<MenuItemViewModel> GroupIntoTree(IReadOnlyList<MenuItemViewModel> flatItems)
    {
        var result = new ObservableCollection<MenuItemViewModel>();
        if (flatItems.Count == 0) return result;

        var lookup = new Dictionary<string, MenuItemViewModel>(StringComparer.Ordinal);
        foreach (var item in flatItems)
        {
            lookup[item.Header] = item;
        }

        var roots = BranchTreeBuilder.Build(flatItems.Select(i => i.Header));
        foreach (var node in roots)
        {
            result.Add(MapTreeNode(node, lookup));
        }

        return result;
    }

    private static MenuItemViewModel MapTreeNode(BranchTreeNode node, IReadOnlyDictionary<string, MenuItemViewModel> lookup)
    {
        if (!node.IsFolder && node.FullName != null && lookup.TryGetValue(node.FullName, out var leaf))
        {
            // Show just the last path segment once nested under a folder; Header itself (the full
            // friendly name) is left untouched since branch-action menu text depends on it.
            leaf.DisplayHeader = node.Name;
            return leaf;
        }

        var folder = new MenuItemViewModel { Header = node.Name, IsFolder = true };
        foreach (var child in node.Children)
        {
            folder.Children.Add(MapTreeNode(child, lookup));
        }
        return folder;
    }

    /// <summary>
    /// Builds the context menu for a ref label hit in the commit graph (T-09). Reuses the exact
    /// branch/tag menus the sidebar shows so the actions stay in one place. Returns <c>null</c>
    /// when the ref no longer resolves (deleted between render and click).
    /// </summary>
    public MenuItemViewModel? BuildRefMenu(string refName)
    {
        var branches = _gitService.GetBranches(_repoPath).ToList();
        var currentBranch = branches.FirstOrDefault(b => b.IsCurrentRepositoryHead);
        string currentBranchName = currentBranch?.FriendlyName ?? "Branches";

        var branch = branches.FirstOrDefault(b => b.Name == refName || b.FriendlyName == refName);
        if (branch != null)
        {
            return branch.IsRemote
                ? CreateRemoteBranchMenu(branch, currentBranchName)
                : CreateLocalBranchMenu(branch, currentBranchName);
        }

        var tag = _gitService.GetTags(_repoPath).FirstOrDefault(t => t.Name == refName);
        return tag != null ? CreateTagMenu(tag) : null;
    }

    private MenuItemViewModel CreateLocalBranchMenu(GitBranchItem branch, string currentBranchName)
    {
        var menu = new MenuItemViewModel { Header = branch.FriendlyName, IsCurrentBranch = branch.IsCurrentRepositoryHead };

        // Group 1: Workspace Management
        menu.SubItems.Add(new MenuItemViewModel { Header = "Checkout", Command = CheckoutBranchCommand, CommandParameter = branch, IsEnabled = !branch.IsCurrentRepositoryHead });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Check out in new worktree", Command = CheckoutInWorktreeCommand, CommandParameter = branch });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"New branch from {branch.FriendlyName}", Command = CreateBranchFromCommand, CommandParameter = branch });

        menu.SubItems.Add(new SeparatorViewModel());

        // Group 2: Remote Operations
        menu.SubItems.Add(new MenuItemViewModel { Header = "Push", Command = PushBranchCommand, CommandParameter = branch });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Create pull request", Command = CreatePullRequestCommand, CommandParameter = branch });

        menu.SubItems.Add(new SeparatorViewModel());

        // Group 3: Integration
        menu.SubItems.Add(new MenuItemViewModel { Header = $"Merge {branch.FriendlyName} into {currentBranchName}", Command = MergeIntoCommand, CommandParameter = branch, IsEnabled = !branch.IsCurrentRepositoryHead });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"Rebase {currentBranchName} onto {branch.FriendlyName}", Command = RebaseIntoCommand, CommandParameter = branch, IsEnabled = !branch.IsCurrentRepositoryHead });

        menu.SubItems.Add(new SeparatorViewModel());

        // Group 4: Review
        menu.SubItems.Add(new MenuItemViewModel { Header = "Show diff with working tree", Command = ShowDiffWithWorkingTreeCommand, CommandParameter = branch });

        menu.SubItems.Add(new SeparatorViewModel());

        // Group 5: Management
        menu.SubItems.Add(new MenuItemViewModel { Header = "Rename", Command = RenameBranchCommand, CommandParameter = branch });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Delete", Command = DeleteBranchCommand, CommandParameter = branch, IsEnabled = !branch.IsCurrentRepositoryHead });

        return menu;
    }

    private MenuItemViewModel CreateRemoteBranchMenu(GitBranchItem branch, string currentBranchName)
    {
        var menu = new MenuItemViewModel
        {
            Header = branch.FriendlyName,
            IsCurrentBranch = branch.IsCurrentRepositoryHead,
            SubItems = new ObservableCollection<MenuItemViewModel>()
        };

        // Group 1: Workspace Management
        menu.SubItems.Add(new MenuItemViewModel { Header = "Checkout", Command = CheckoutBranchCommand, CommandParameter = branch });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Check out in new worktree", Command = CheckoutInWorktreeCommand, CommandParameter = branch });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"New branch from {branch.FriendlyName}", Command = CreateBranchFromCommand, CommandParameter = branch });

        menu.SubItems.Add(new SeparatorViewModel());

        // Group 2: Integration
        menu.SubItems.Add(new MenuItemViewModel { Header = $"Merge {branch.FriendlyName} into {currentBranchName}", Command = MergeIntoCommand, CommandParameter = branch });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"Rebase {currentBranchName} onto {branch.FriendlyName}", Command = RebaseIntoCommand, CommandParameter = branch });

        menu.SubItems.Add(new SeparatorViewModel());

        // Group 3: Review
        menu.SubItems.Add(new MenuItemViewModel { Header = "Show diff with working tree", Command = ShowDiffWithWorkingTreeCommand, CommandParameter = branch });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"Compare with {currentBranchName}", Command = CompareBranchesCommand, CommandParameter = branch });

        menu.SubItems.Add(new SeparatorViewModel());

        // Group 4: Management
        menu.SubItems.Add(new MenuItemViewModel { Header = "Delete", Command = DeleteBranchCommand, CommandParameter = branch });

        return menu;
    }

    private MenuItemViewModel CreateTagMenu(GitTagItem tag)
    {
        var menu = new MenuItemViewModel { Header = tag.Name };

        // Group 1: Workspace
        menu.SubItems.Add(new MenuItemViewModel { Header = "Checkout", Command = CheckoutTagCommand, CommandParameter = tag });

        menu.SubItems.Add(new SeparatorViewModel());

        // Group 2: Remote
        menu.SubItems.Add(new MenuItemViewModel { Header = "Push to origin", Command = PushTagCommand, CommandParameter = tag });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Delete from origin", Command = DeleteRemoteTagCommand, CommandParameter = tag });

        menu.SubItems.Add(new SeparatorViewModel());

        // Group 3: Management
        menu.SubItems.Add(new MenuItemViewModel { Header = "Copy name", Command = CopyTagNameCommand, CommandParameter = tag });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Delete", Command = DeleteTagCommand, CommandParameter = tag });

        return menu;
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CheckoutBranchAsync(GitBranchItem branch)
    {
        bool success = await PerformCheckoutWithFallbackAsync(branch.Name, branch.FriendlyName);
        if (success)
        {
            ErrorMessage = string.Empty;
            _onBranchChangedAction?.Invoke();
            _showNotificationAction?.Invoke($"Checked out '{branch.FriendlyName}'");
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CheckoutAndRebaseAsync(GitBranchItem targetBranch)
    {
        try
        {
            var branches = _gitService.GetBranches(_repoPath).ToList();
            var currentBranch = branches.FirstOrDefault(b => b.IsCurrentRepositoryHead);
            string currentBranchName = currentBranch?.FriendlyName ?? "main";

            bool success = await PerformCheckoutWithFallbackAsync(targetBranch.Name, targetBranch.FriendlyName);

            if (success)
            {
                _gitService.Rebase(_repoPath, currentBranchName);
                _onBranchChangedAction?.Invoke();
                _showNotificationAction?.Invoke($"Successfully checked out and rebased {targetBranch.FriendlyName} onto {currentBranchName}.");
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Checkout and Rebase failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
        }
    }

    [RelayCommand]
    private void RebaseInto(GitBranchItem targetBranch)
    {
        try
        {
            var branches = _gitService.GetBranches(_repoPath).ToList();
            var currentBranch = branches.FirstOrDefault(b => b.IsCurrentRepositoryHead);
            string currentBranchName = currentBranch?.FriendlyName ?? "main";

            _gitService.Rebase(_repoPath, targetBranch.FriendlyName);
            _onBranchChangedAction?.Invoke();
            _showNotificationAction?.Invoke($"Successfully rebased {currentBranchName} onto {targetBranch.FriendlyName}.");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Rebase failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RebaseLocalIntoTrackedAsync(GitBranchItem localBranch)
    {
        try
        {
            bool success = await PerformCheckoutWithFallbackAsync(localBranch.Name, localBranch.FriendlyName);
            if (success)
            {
                _gitService.Rebase(_repoPath, $"origin/{localBranch.FriendlyName}");
                _onBranchChangedAction?.Invoke();
                _showNotificationAction?.Invoke($"Successfully rebased {localBranch.FriendlyName} onto origin/{localBranch.FriendlyName}.");
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Rebase failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task PullWithRebaseAsync(GitBranchItem branch)
    {
        try
        {
            _gitService.Fetch(_repoPath);

            if (branch.IsRemote)
            {
                var branches = _gitService.GetBranches(_repoPath).ToList();
                var currentBranch = branches.FirstOrDefault(b => b.IsCurrentRepositoryHead);
                string currentBranchName = currentBranch?.FriendlyName ?? "main";

                _gitService.Rebase(_repoPath, branch.FriendlyName);
                _onBranchChangedAction?.Invoke();
                _showNotificationAction?.Invoke($"Successfully pulled and rebased {branch.FriendlyName} into {currentBranchName}.");
            }
            else
            {
                bool success = await PerformCheckoutWithFallbackAsync(branch.Name, branch.FriendlyName);
                if (success)
                {
                    _gitService.Rebase(_repoPath, $"origin/{branch.FriendlyName}");
                    _onBranchChangedAction?.Invoke();
                    _showNotificationAction?.Invoke($"Successfully pulled and rebased origin/{branch.FriendlyName} into {branch.FriendlyName}.");
                }
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Pull with Rebase failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task MergeIntoAsync(GitBranchItem branch)
    {
        try
        {
            var branches = _gitService.GetBranches(_repoPath).ToList();
            var currentBranch = branches.FirstOrDefault(b => b.IsCurrentRepositoryHead);
            if (currentBranch == null) return;

            string sourceBranchName = branch.IsRemote ? branch.FriendlyName : branch.Name;

            if (branch.IsRemote)
            {
                _gitService.Fetch(_repoPath);
            }

            _gitService.Merge(_repoPath, sourceBranchName);
            _onBranchChangedAction?.Invoke();
            _showNotificationAction?.Invoke($"Successfully merged {sourceBranchName} into {currentBranch.FriendlyName}.");
        }
        catch (System.Exception ex)
        {
            if (Unwrap<GitLoom.Core.Exceptions.MergeConflictException>(ex) is not null)
            {
                await ShowConflictResolverAsync();
            }
            else
            {
                ErrorMessage = $"Merge failed: {ex.Message}";
                _showNotificationAction?.Invoke(ErrorMessage);
            }
        }

        await CheckAndShowMergeCommitDialogAsync();
        _onBranchChangedAction?.Invoke();
    }

    private static T? Unwrap<T>(System.Exception ex) where T : class
        => ex as T ?? ex.InnerException as T;

    // Opens the engine-driven conflict resolver over the main window. The only
    // signal used to detect a conflict is the typed MergeConflictException.
    private async System.Threading.Tasks.Task ShowConflictResolverAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var dialog = new Views.ConflictedFilesWindow();
            dialog.DataContext = new ConflictedFilesViewModel(_repoPath, _gitService, new MergeDiffService(), dialog);
            await dialog.ShowDialog(desktop.MainWindow);
        }
    }

    private async System.Threading.Tasks.Task CheckAndShowMergeCommitDialogAsync()
    {
        if (_gitService.IsMergeInProgress(_repoPath))
        {
            var status = _gitService.GetRepositoryStatus(_repoPath);
            if (System.Linq.Enumerable.Any(status, x => x.State.HasFlag(LibGit2Sharp.FileStatus.Conflicted)))
            {
                // Conflicts are still unresolved (user closed the window without finishing).
                return;
            }

            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                var dialog = new Views.MergeCommitDialog();
                var vm = new MergeCommitDialogViewModel(_repoPath, _gitService, dialog);
                dialog.DataContext = vm;
                await dialog.ShowDialog(desktop.MainWindow);

                if (vm.Result != MergeCommitResult.Cancel)
                {
                    _showNotificationAction?.Invoke("Merge successfully committed" + (vm.Result == MergeCommitResult.CommitAndPush ? " and pushed" : "") + ".");
                }
            }
        }
    }

    private async System.Threading.Tasks.Task<bool> PerformCheckoutWithFallbackAsync(string branchName, string friendlyName)
    {
        if (_gitService.HasUncommittedChanges(_repoPath))
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                var vm = new CheckoutConflictDialogViewModel();
                var dialog = new Views.CheckoutConflictDialog { DataContext = vm };
                await dialog.ShowDialog(desktop.MainWindow);

                if (vm.Result == CheckoutConflictResult.Cancel)
                {
                    return false;
                }
                else if (vm.Result == CheckoutConflictResult.Stash)
                {
                    _gitService.StashChanges(_repoPath, $"Auto-stash before checkout {friendlyName}");
                    _showNotificationAction?.Invoke("Changes stashed.");
                }
            }
        }

        try
        {
            _gitService.CheckoutBranch(_repoPath, branchName);
            return true;
        }
        catch (Exception ex)
        {
            // Detect the checkout-conflict case by exception TYPE, not by
            // message sniffing (audit 1.11): LibGit2Sharp throws a dedicated
            // CheckoutConflictException when local changes block the checkout.
            if (ex is LibGit2Sharp.CheckoutConflictException)
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
                {
                    var vm = new ConfirmationDialogViewModel
                    {
                        Title = "Checkout Conflicts",
                        Message = "Carry over failed due to conflicts. Would you like to stash your changes and continue checking out?",
                        ConfirmButtonText = "Stash & Checkout"
                    };
                    var dialog = new Views.ConfirmationDialog { DataContext = vm };
                    await dialog.ShowDialog(desktop.MainWindow);

                    if (vm.IsConfirmed)
                    {
                        _gitService.StashChanges(_repoPath, $"Auto-stash after failed carry over to {friendlyName}");
                        _gitService.CheckoutBranch(_repoPath, branchName);
                        _showNotificationAction?.Invoke("Changes stashed.");
                        return true;
                    }
                }
            }

            ErrorMessage = $"Checkout failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
            return false;
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task DeleteBranchAsync(GitBranchItem branch)
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                // Say what is actually true: a local delete is recorded in the operation
                // journal and can be undone from History; deleting a remote branch pushes
                // the deletion, which the journal cannot reverse.
                var vm = new ConfirmationDialogViewModel
                {
                    Title = "Delete Branch",
                    Message = branch.IsRemote
                        ? $"Are you sure you want to delete the remote branch '{branch.FriendlyName}'?\nThis pushes the deletion to the remote and cannot be undone from History."
                        : $"Are you sure you want to delete the branch '{branch.FriendlyName}'?\nYou can undo this from History.",
                    ConfirmButtonText = "Delete"
                };

                if (branch.IsRemote)
                {
                    vm.OptionalCheckboxText = "Also delete local tracking branch";
                    vm.IsOptionalCheckboxVisible = true;
                    vm.IsOptionalCheckboxChecked = true;
                }

                var dialog = new Views.ConfirmationDialog { DataContext = vm };
                await dialog.ShowDialog(desktop.MainWindow);

                if (!vm.IsConfirmed)
                {
                    return;
                }

                if (branch.IsRemote && vm.IsOptionalCheckboxChecked)
                {
                    try
                    {
                        var localName = branch.FriendlyName.Substring(branch.FriendlyName.IndexOf('/') + 1);
                        await DeleteBranchGuardedAsync(localName);
                    }
                    catch (Exception) { /* Ignore if local doesn't exist */ }
                }
            }

            if (!await DeleteBranchGuardedAsync(branch.Name)) return;
            ErrorMessage = string.Empty;
            _onBranchChangedAction?.Invoke();
            _showNotificationAction?.Invoke($"Deleted branch '{branch.FriendlyName}'");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Delete failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
        }
    }

    /// <summary>
    /// Deletes a branch with git's <c>-d</c> safety. The service refuses an unmerged branch
    /// (<see cref="BranchNotMergedException"/>) instead of orphaning its commits, so the user is
    /// asked once, explicitly, before we force. Returns false when they declined; every other
    /// failure propagates to the caller's handler.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> DeleteBranchGuardedAsync(string branchName)
    {
        try
        {
            _gitService.DeleteBranch(_repoPath, branchName, force: false);
            return true;
        }
        catch (BranchNotMergedException ex)
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow == null)
            {
                // No window to ask in — never force behind the user's back.
                throw;
            }

            var vm = new ConfirmationDialogViewModel
            {
                Title = "Branch Not Merged",
                Message = ex.Message,
                ConfirmButtonText = "Delete anyway"
            };
            var dialog = new Views.ConfirmationDialog { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);
            if (!vm.IsConfirmed) return false;

            _gitService.DeleteBranch(_repoPath, branchName, force: true);
            return true;
        }
    }

    [RelayCommand]
    private void NotImplemented()
    {
        ErrorMessage = "Action coming soon!";
        _showNotificationAction?.Invoke(ErrorMessage);
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RebaseCurrentOntoAsync(GitBranchItem branch)
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                var vm = new ConfirmationDialogViewModel
                {
                    Title = "Rebase",
                    Message = $"Are you sure you want to rebase your current branch onto '{branch.FriendlyName}'?",
                    ConfirmButtonText = "Rebase"
                };
                var dialog = new Views.ConfirmationDialog { DataContext = vm };
                await dialog.ShowDialog(desktop.MainWindow);

                if (!vm.IsConfirmed) return;
            }

            _gitService.Rebase(_repoPath, branch.FriendlyName);
            _onBranchChangedAction?.Invoke();
            _showNotificationAction?.Invoke($"Successfully rebased current branch onto '{branch.FriendlyName}'.");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Rebase failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
        }
    }

    [RelayCommand]
    private void ShowDiff(GitBranchItem branch)
    {
        try
        {
            // For now, we generate a high-level patch or notify. The backend takes a specific file, but we can pass null for the whole tree diffing if supported by LibGit2Sharp, or just simulate the UI action.
            // Since GitService.GetDiffAgainstCommit takes a file path, we might just want to show the count of worktrees as a test for Phase 4.5.
            _showNotificationAction?.Invoke($"Diff generation backend ready for {branch.FriendlyName}. Connect DiffViewer UI next.");
        }
        catch (Exception ex)
        {
            _showNotificationAction?.Invoke($"Diff failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task NewWorktreeAsync(GitBranchItem branch)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var result = await desktop.MainWindow.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = $"Select folder for new worktree ({branch.FriendlyName})",
                AllowMultiple = false
            });

            if (result.Count > 0)
            {
                string path = result[0].Path.LocalPath;
                try
                {
                    // Existing branch checked out into a new worktree dir.
                    _gitService.AddWorktree(_repoPath, path, branch.Name, createBranch: false);
                    _showNotificationAction?.Invoke($"Successfully created new worktree at {path}.");
                }
                catch (Exception ex)
                {
                    _showNotificationAction?.Invoke($"Failed to create worktree: {ex.Message}");
                }
            }
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CreateBranchFromCurrentAsync()
    {
        // Added in main branch
        await OpenCreateBranchDialogAsync();
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task OpenCreateBranchDialogAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var vm = new CreateBranchDialogViewModel();
            var dialog = new Views.CreateBranchDialog { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);

            if (vm.IsConfirmed)
            {
                try
                {
                    _gitService.CreateBranch(_repoPath, vm.BranchName, "", checkout: false);
                    ErrorMessage = string.Empty;
                    _onBranchChangedAction?.Invoke();

                    if (vm.CheckoutImmediately)
                    {
                        bool checkedOut = await PerformCheckoutWithFallbackAsync(vm.BranchName, vm.BranchName);
                        if (checkedOut)
                        {
                            _onBranchChangedAction?.Invoke();
                            _showNotificationAction?.Invoke($"Created and checked out '{vm.BranchName}'");
                        }
                    }
                    else
                    {
                        _showNotificationAction?.Invoke($"Created branch '{vm.BranchName}'");
                    }
                }
                catch (Exception ex)
                {
                    ErrorMessage = $"Create branch failed: {ex.Message}";
                    _showNotificationAction?.Invoke(ErrorMessage);
                }
            }
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CreateBranchFromAsync(GitBranchItem branch)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var vm = new CreateBranchDialogViewModel();
            var dialog = new Views.CreateBranchDialog { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);

            if (vm.IsConfirmed)
            {
                try
                {
                    _gitService.CreateBranch(_repoPath, vm.BranchName, branch.Name, checkout: false);
                    _onBranchChangedAction?.Invoke();

                    if (vm.CheckoutImmediately)
                    {
                        bool checkedOut = await PerformCheckoutWithFallbackAsync(vm.BranchName, vm.BranchName);
                        if (checkedOut)
                        {
                            _onBranchChangedAction?.Invoke();
                            _showNotificationAction?.Invoke($"Created and checked out '{vm.BranchName}' from '{branch.FriendlyName}'.");
                        }
                    }
                    else
                    {
                        _showNotificationAction?.Invoke($"Created branch '{vm.BranchName}' from '{branch.FriendlyName}'.");
                    }
                }
                catch (Exception ex)
                {
                    _showNotificationAction?.Invoke($"Create Branch Failed: {ex.Message}");
                }
            }
        }
    }

    [RelayCommand]
    private void UpdateBranch(GitBranchItem branch)
    {
        try
        {
            if (branch.IsCurrentRepositoryHead)
            {
                _gitService.Pull(_repoPath);
            }
            else
            {
                _gitService.Fetch(_repoPath);
                _gitService.Merge(_repoPath, $"origin/{branch.FriendlyName}");
            }
            _onBranchChangedAction?.Invoke();
            _showNotificationAction?.Invoke($"Successfully updated '{branch.FriendlyName}'.");
        }
        catch (Exception ex)
        {
            _showNotificationAction?.Invoke($"Update Failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void PushBranch(GitBranchItem branch)
    {
        try
        {
            _gitService.PushBranch(_repoPath, branch.Name);
            _showNotificationAction?.Invoke($"Successfully pushed '{branch.FriendlyName}'.");
        }
        catch (Exception ex)
        {
            _showNotificationAction?.Invoke($"Push Failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RenameBranchAsync(GitBranchItem branch)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var vm = new CreateBranchDialogViewModel();
            // Using CreateBranch dialog as a quick hack for rename since it has a text input
            var dialog = new Views.CreateBranchDialog { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);

            if (vm.IsConfirmed)
            {
                try
                {
                    _gitService.RenameBranch(_repoPath, branch.Name, vm.BranchName);
                    _onBranchChangedAction?.Invoke();
                    _showNotificationAction?.Invoke($"Renamed '{branch.FriendlyName}' to '{vm.BranchName}'.");
                }
                catch (Exception ex)
                {
                    _showNotificationAction?.Invoke($"Rename Failed: {ex.Message}");
                }
            }
        }
    }

    [RelayCommand]
    private void ShowDiffWithWorkingTree(GitBranchItem branch)
    {
        try
        {
            var diff = _gitService.GetBranchDiffAgainstWorkingTree(_repoPath, branch.Name);
            if (string.IsNullOrWhiteSpace(diff))
            {
                _showNotificationAction?.Invoke($"No differences between working tree and {branch.FriendlyName}.");
                return;
            }

            string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GitLoom_{branch.FriendlyName.Replace("/", "_")}_diff.patch");
            System.IO.File.WriteAllText(tempFile, diff);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tempFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _showNotificationAction?.Invoke($"Failed to generate diff: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CompareBranches(GitBranchItem branch)
    {
        _onCompareBranchAction?.Invoke(branch.Name);
        _showNotificationAction?.Invoke($"Commit timeline filtered by {branch.FriendlyName}");
    }


    [RelayCommand]
    private async System.Threading.Tasks.Task PullWithMerge(GitBranchItem branch)
    {
        try
        {
            _gitService.Fetch(_repoPath);

            if (branch.IsRemote)
            {
                var branches = _gitService.GetBranches(_repoPath).ToList();
                var currentBranch = branches.FirstOrDefault(b => b.IsCurrentRepositoryHead);
                string currentBranchName = currentBranch?.FriendlyName ?? "main";

                _gitService.Merge(_repoPath, branch.FriendlyName);
                _onBranchChangedAction?.Invoke();
                _showNotificationAction?.Invoke($"Successfully pulled and merged {branch.FriendlyName} into {currentBranchName}.");
            }
            else
            {
                _gitService.Merge(_repoPath, $"origin/{branch.FriendlyName}");
                _onBranchChangedAction?.Invoke();
                _showNotificationAction?.Invoke($"Successfully pulled and merged origin/{branch.FriendlyName} into {branch.FriendlyName}.");
            }
        }
        catch (System.Exception ex)
        {
            if (Unwrap<GitLoom.Core.Exceptions.MergeConflictException>(ex) is not null)
            {
                await ShowConflictResolverAsync();
            }
            else
            {
                _showNotificationAction?.Invoke($"Pull with Merge failed: {ex.Message}");
            }
        }

        await CheckAndShowMergeCommitDialogAsync();
        _onBranchChangedAction?.Invoke();
    }

    // ---- Tags (T-05) -------------------------------------------------------

    [RelayCommand]
    private async System.Threading.Tasks.Task CheckoutTagAsync(GitTagItem tag)
    {
        try
        {
            await System.Threading.Tasks.Task.Run(() => _gitService.CheckoutTag(_repoPath, tag.Name));
            ErrorMessage = string.Empty;
            _onBranchChangedAction?.Invoke();
            _showNotificationAction?.Invoke($"Checked out tag '{tag.Name}' (detached HEAD).");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Checkout tag failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task PushTagAsync(GitTagItem tag)
    {
        try
        {
            await System.Threading.Tasks.Task.Run(() => _gitService.PushTag(_repoPath, "origin", tag.Name));
            _showNotificationAction?.Invoke($"Pushed tag '{tag.Name}' to origin.");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Push tag failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task DeleteTagAsync(GitTagItem tag)
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                var vm = new ConfirmationDialogViewModel
                {
                    Title = "Delete Tag",
                    Message = $"Are you sure you want to delete the tag '{tag.Name}'?\nThis action cannot be undone.",
                    ConfirmButtonText = "Delete"
                };
                var dialog = new Views.ConfirmationDialog { DataContext = vm };
                await dialog.ShowDialog(desktop.MainWindow);
                if (!vm.IsConfirmed) return;
            }

            await System.Threading.Tasks.Task.Run(() => _gitService.DeleteTag(_repoPath, tag.Name));
            ErrorMessage = string.Empty;
            LoadBranches();
            _onBranchChangedAction?.Invoke();
            _showNotificationAction?.Invoke($"Deleted tag '{tag.Name}'.");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Delete tag failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task DeleteRemoteTagAsync(GitTagItem tag)
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                var vm = new ConfirmationDialogViewModel
                {
                    Title = "Delete Remote Tag",
                    Message = $"Delete the tag '{tag.Name}' from 'origin'?\nThe local tag is kept.",
                    ConfirmButtonText = "Delete"
                };
                var dialog = new Views.ConfirmationDialog { DataContext = vm };
                await dialog.ShowDialog(desktop.MainWindow);
                if (!vm.IsConfirmed) return;
            }

            await System.Threading.Tasks.Task.Run(() => _gitService.DeleteRemoteTag(_repoPath, "origin", tag.Name));
            _showNotificationAction?.Invoke($"Deleted tag '{tag.Name}' from origin.");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Delete remote tag failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CopyTagNameAsync(GitTagItem tag)
    {
        var app = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var clipboard = app?.MainWindow?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(tag.Name);
        _showNotificationAction?.Invoke($"Copied '{tag.Name}'.");
    }
}
