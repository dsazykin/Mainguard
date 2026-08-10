using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

public partial class StagingPanelViewModel : ViewModelBase
{
    private readonly IGitService _gitService;
    private readonly string _repoPath;
    private readonly Action _onCommitAction;
    private readonly Action<string, bool>? _showNotification;
    private readonly Func<UserPreferences> _preferences;
    private readonly ISettingsService? _settings;

    // T-30 pre-commit scanner findings panel. Owns the scan/gating state; the commit commands below
    // route through it so a blocker (secret / merge marker) pauses for an explicit "Commit anyway".
    public PreCommitFindingsViewModel PreCommitFindings { get; }

    // T-31 conventional-commit composer. In structured mode the commit uses its assembled message
    // (still through the existing commit path + the T-30 scan); in plain mode the box below is used.
    public CommitComposerViewModel CommitComposer { get; } = new();

    // The commit the user asked for, held while a blocker banner is shown so "Commit anyway" can run it.
    private Func<System.Threading.Tasks.Task>? _pendingCommit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionedFilesCountText))]
    private ObservableCollection<GitFileStatus> _versionedFiles = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnversionedFilesCountText))]
    private ObservableCollection<GitFileStatus> _unversionedFiles = new();

    public string VersionedFilesCountText => $"{VersionedFiles.Count} file{(VersionedFiles.Count == 1 ? "" : "s")}";

    public string UnversionedFilesCountText => $"{UnversionedFiles.Count} file{(UnversionedFiles.Count == 1 ? "" : "s")}";

    [ObservableProperty]
    private bool _isChangesExpanded = true;

    [ObservableProperty]
    private bool _isUnversionedExpanded = false;

    [ObservableProperty]
    private ObservableCollection<GitStashItem> _stashes = new();

    [ObservableProperty]
    private bool _isRebasing;

    [ObservableProperty]
    private bool _isStashTabActive = false;

    [ObservableProperty]
    private GitFileStatus? _selectedFile;

    [ObservableProperty]
    private bool _amendLastCommit;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StashPushCommand))]
    private string _stashMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitAndPushCommand))]
    private string _commitMessage = string.Empty;

    public event Action<GitFileStatus?>? SelectedFileChanged;
    public event Action<string>? OnFileHistoryRequested;
    public event Action<string>? OnBlameRequested;

    public StagingPanelViewModel(
        IGitService gitService,
        string repoPath,
        Action onCommitAction,
        Action<string, bool>? showNotification = null,
        IPreCommitScanner? scanner = null,
        Func<UserPreferences>? preferences = null,
        ISettingsService? settings = null)
    {
        _gitService = gitService;
        _repoPath = repoPath;
        _onCommitAction = onCommitAction;
        _showNotification = showNotification;
        _preferences = preferences ?? (() => new UserPreferences());
        _settings = settings;

        // Re-gate the commit commands whenever the structured composer's assembled message or
        // validation state changes (an error blocks the default Commit; warnings are advisory).
        CommitComposer.Changed += () =>
        {
            CommitCommand.NotifyCanExecuteChanged();
            CommitAndPushCommand.NotifyCanExecuteChanged();
        };

        PreCommitFindings = new PreCommitFindingsViewModel(
            scanner ?? new PreCommitScanner(gitService),
            repoPath,
            preferences,
            settings,
            revealInDiff: (path, _) => RevealInDiff(path));
        // "Commit anyway" on a blocker resumes the exact commit the user asked for.
        PreCommitFindings.CommitConfirmed += async () =>
        {
            var pending = _pendingCommit;
            _pendingCommit = null;
            if (pending != null) await pending();
        };
    }

    // Reveal-in-diff jump: select the finding's file so the diff viewer shows it.
    private void RevealInDiff(string path)
    {
        var match = VersionedFiles.FirstOrDefault(f => f.FilePath == path)
                    ?? UnversionedFiles.FirstOrDefault(f => f.FilePath == path);
        if (match != null) SelectedFile = match;
    }

    public void UpdateStatus(System.Collections.Generic.List<GitFileStatus> allChanges)
    {
        IsRebasing = _gitService.IsRebasing(_repoPath);

        // Remove old handlers
        foreach (var f in VersionedFiles) f.PropertyChanged -= File_PropertyChanged;
        foreach (var f in UnversionedFiles) f.PropertyChanged -= File_PropertyChanged;

        var versioned = allChanges.Where(f => !f.IsUntracked).ToList();
        var unversioned = allChanges.Where(f => f.IsUntracked).ToList();

        // Initialize IsSelected based on whether it was already staged, or true for untracked just for convenience
        var repoName = System.IO.Path.GetFileName(_repoPath.TrimEnd('\\', '/'));

        foreach (var v in versioned)
        {
            var dir = System.IO.Path.GetDirectoryName(v.FilePath);
            v.DirectoryPath = string.IsNullOrEmpty(dir) ? repoName : dir;
            v.IsSelected = v.IsStaged;
            v.PropertyChanged += File_PropertyChanged;
        }
        foreach (var u in unversioned)
        {
            var dir = System.IO.Path.GetDirectoryName(u.FilePath);
            u.DirectoryPath = string.IsNullOrEmpty(dir) ? repoName : dir;
            u.IsSelected = false; // default untracked to not selected
            u.PropertyChanged += File_PropertyChanged;
        }

        VersionedFiles = new ObservableCollection<GitFileStatus>(versioned);
        UnversionedFiles = new ObservableCollection<GitFileStatus>(unversioned);

        OnPropertyChanged(nameof(VersionedFilesCountText));
        OnPropertyChanged(nameof(UnversionedFilesCountText));

        UpdateTriStates();

        CommitCommand.NotifyCanExecuteChanged();
        CommitAndPushCommand.NotifyCanExecuteChanged();
        StashPushCommand.NotifyCanExecuteChanged();
    }

    public void LoadStashes()
    {
        Stashes = new ObservableCollection<GitStashItem>(_gitService.GetStashes(_repoPath));
    }

    partial void OnSelectedFileChanged(GitFileStatus? value)
    {
        SelectedFileChanged?.Invoke(value);
    }

    partial void OnCommitMessageChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;

        CommitCommand.NotifyCanExecuteChanged();
        CommitAndPushCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    private bool _isRefreshing;

    [RelayCommand]
    private async System.Threading.Tasks.Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        var minTask = System.Threading.Tasks.Task.Delay(1000);
        _onCommitAction?.Invoke();
        await minTask;
        IsRefreshing = false;
    }

    // --- Tri-State Checkboxes Logic ---

    [ObservableProperty]
    private bool? _isAllVersionedSelected = false;

    [ObservableProperty]
    private bool? _isAllUnversionedSelected = false;

    private bool _isUpdatingTriState = false;

    private void File_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GitFileStatus.IsSelected))
        {
            UpdateTriStates();
            CommitCommand.NotifyCanExecuteChanged();
            CommitAndPushCommand.NotifyCanExecuteChanged();
        }
    }

    private void UpdateTriStates()
    {
        if (_isUpdatingTriState) return;
        _isUpdatingTriState = true;

        if (VersionedFiles.Count == 0) IsAllVersionedSelected = false;
        else if (VersionedFiles.All(f => f.IsSelected)) IsAllVersionedSelected = true;
        else if (VersionedFiles.All(f => !f.IsSelected)) IsAllVersionedSelected = false;
        else IsAllVersionedSelected = null;

        if (UnversionedFiles.Count == 0) IsAllUnversionedSelected = false;
        else if (UnversionedFiles.All(f => f.IsSelected)) IsAllUnversionedSelected = true;
        else if (UnversionedFiles.All(f => !f.IsSelected)) IsAllUnversionedSelected = false;
        else IsAllUnversionedSelected = null;

        _isUpdatingTriState = false;
    }

    [RelayCommand]
    private void ToggleVersionedSelection()
    {
        bool targetState = (IsAllVersionedSelected == true) ? false : true;
        foreach (var f in VersionedFiles) f.IsSelected = targetState;
    }

    [RelayCommand]
    private void SwitchToCommitTab()
    {
        IsStashTabActive = false;
    }

    [RelayCommand]
    private void SwitchToStashTab()
    {
        IsStashTabActive = true;
    }

    [RelayCommand]
    private void ToggleUnversionedSelection()
    {
        bool targetState = (IsAllUnversionedSelected == true) ? false : true;
        foreach (var f in UnversionedFiles) f.IsSelected = targetState;
    }

    // --- Commit Logic ---

    /// <summary>
    /// Plain ⇄ structured composer toggle (T-31), persisted in <see cref="UserPreferences"/>. In
    /// structured mode the commit uses the composer's assembled message; the plain box is the escape hatch.
    /// </summary>
    public bool UseStructuredComposer
    {
        get => _preferences().UseStructuredCommitComposer;
        set
        {
            if (_preferences().UseStructuredCommitComposer == value) return;
            _settings?.Update(p => p.UseStructuredCommitComposer = value);
            OnPropertyChanged();
            CommitCommand.NotifyCanExecuteChanged();
            CommitAndPushCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void UsePlainComposer() => UseStructuredComposer = false;

    [RelayCommand]
    private void UseStructuredComposerMode() => UseStructuredComposer = true;

    // The message actually committed: the composer's assembled message in structured mode, else the box.
    private string EffectiveCommitMessage => UseStructuredComposer ? CommitComposer.Preview : CommitMessage;

    private bool HasCommitContent =>
        UseStructuredComposer
            ? !string.IsNullOrWhiteSpace(CommitComposer.Description) && !CommitComposer.HasErrors
            : !string.IsNullOrWhiteSpace(CommitMessage);

    private bool CanCommit => HasCommitContent && (VersionedFiles.Any(f => f.IsSelected) || UnversionedFiles.Any(f => f.IsSelected));

    private void PrepareStagingForCommit()
    {
        var selectedPaths = VersionedFiles.Where(f => f.IsSelected).Select(f => f.FilePath)
            .Concat(UnversionedFiles.Where(f => f.IsSelected).Select(f => f.FilePath)).ToList();

        var unselectedPaths = VersionedFiles.Where(f => !f.IsSelected).Select(f => f.FilePath)
            .Concat(UnversionedFiles.Where(f => !f.IsSelected).Select(f => f.FilePath)).ToList();

        // Stash/unstage is complex, but in our gitService, we can just unstage unselected, and stage selected.
        if (unselectedPaths.Count > 0)
        {
            _gitService.UnstageFiles(_repoPath, unselectedPaths);
        }
        if (selectedPaths.Count > 0)
        {
            _gitService.StageFiles(_repoPath, selectedPaths);
        }
    }

    // Runs the pre-commit scan (when enabled) after staging is prepared. Returns true when a blocker
    // was found and the commit must pause for an explicit "Commit anyway". Warnings do not block.
    private async System.Threading.Tasks.Task<bool> IsBlockedByScanAsync()
    {
        if (!PreCommitFindings.AutoScanEnabled) return false;
        await PreCommitFindings.ScanAsync();
        return PreCommitFindings.HasBlockers;
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async System.Threading.Tasks.Task Commit()
    {
        try
        {
            PrepareStagingForCommit();
            if (await IsBlockedByScanAsync())
            {
                _pendingCommit = DoCommitAsync;
                PreCommitFindings.AwaitingOverride = true;
                return;
            }
            await DoCommitAsync();
        }
        catch (Exception ex)
        {
            _showNotification?.Invoke($"Commit Failed: {ex.Message}", true);
        }
    }

    private System.Threading.Tasks.Task DoCommitAsync()
    {
        _gitService.Commit(_repoPath, EffectiveCommitMessage, AmendLastCommit);
        ResetComposerAfterCommit();
        _onCommitAction?.Invoke();
        return System.Threading.Tasks.Task.CompletedTask;
    }

    // Clears whichever composer was used so the next commit starts blank.
    private void ResetComposerAfterCommit()
    {
        CommitMessage = string.Empty;
        CommitComposer.Clear();
        // Amend is a per-commit choice, never a mode: leaving it ticked would make the NEXT
        // commit silently rewrite the one just made. Only reached on success, so a rejected
        // amend (published HEAD) keeps the box ticked and the message intact for a retry.
        AmendLastCommit = false;
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async System.Threading.Tasks.Task CommitAndPush()
    {
        try
        {
            PrepareStagingForCommit();
            if (await IsBlockedByScanAsync())
            {
                _pendingCommit = DoCommitAndPushAsync;
                PreCommitFindings.AwaitingOverride = true;
                return;
            }
            await DoCommitAndPushAsync();
        }
        catch (Exception ex)
        {
            _showNotification?.Invoke($"Commit Failed: {ex.Message}", true);
        }
    }

    private System.Threading.Tasks.Task DoCommitAndPushAsync()
    {
        _gitService.Commit(_repoPath, EffectiveCommitMessage, AmendLastCommit);
        ResetComposerAfterCommit();
        _onCommitAction?.Invoke();

        try
        {
            _gitService.Push(_repoPath);
            _showNotification?.Invoke("Commit and Push completed successfully.", false);
        }
        catch (Exception ex)
        {
            _showNotification?.Invoke($"Push Failed: {ex.Message}", true);
        }
        return System.Threading.Tasks.Task.CompletedTask;
    }

    // Opens the conflicted-files window, which lists every conflicted path and
    // offers "Accept Incoming/Outgoing" plus "Modify" (the 3-way resolver).
    // The inline diff-viewer resolver only surfaces once a conflicted file is
    // hand-selected and markers are detected; during a rebase pause the user
    // needs a discoverable entry point that always works, or the rebase is
    // impossible to finish.
    [RelayCommand]
    private async System.Threading.Tasks.Task ResolveConflicts()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow == null)
        {
            return;
        }

        try
        {
            var dialog = new Views.ConflictedFilesWindow();
            var vm = new ConflictedFilesViewModel(_repoPath, _gitService, new MergeDiffService(), dialog);
            dialog.DataContext = vm;
            await dialog.ShowDialog(desktop.MainWindow);
        }
        catch (Exception ex)
        {
            _showNotification?.Invoke($"Resolve Conflicts failed: {ex.Message}", true);
        }
        finally
        {
            // Refresh so staged resolutions (and any abort) are reflected and
            // the "Continue Rebase" button sees the updated state.
            _onCommitAction?.Invoke();
        }
    }

    // Failures here must be surfaced (audit 1.11): silently swallowing them
    // leaves the user staring at a rebase banner with no idea why the button
    // appeared to do nothing (e.g. unresolved conflicts on Continue).
    [RelayCommand]
    private void ContinueRebase()
    {
        try
        {
            _gitService.ContinueRebase(_repoPath);
            _onCommitAction?.Invoke();
        }
        catch (Exception ex)
        {
            _showNotification?.Invoke($"Continue Rebase failed: {ex.Message}", true);
        }
    }

    [RelayCommand]
    private void AbortRebase()
    {
        try
        {
            _gitService.AbortRebase(_repoPath);
            _onCommitAction?.Invoke();
        }
        catch (Exception ex)
        {
            _showNotification?.Invoke($"Abort Rebase failed: {ex.Message}", true);
        }
    }



    [RelayCommand]
    private void DeleteFile(GitFileStatus? file)
    {
        if (file == null) return;
        try
        {
            var fullPath = System.IO.Path.Combine(_repoPath, file.FilePath);

            // NEVER recursively delete a directory (audit 1.4): a mis-clicked
            // "Delete" on a path that resolves to an untracked directory would
            // silently wipe a tree full of the user's work with no undo.
            if (System.IO.Directory.Exists(fullPath))
            {
                _showNotification?.Invoke(
                    $"'{file.FilePath}' is a directory — delete it from your file manager if you really mean to.", true);
                return;
            }

            if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
            _onCommitAction?.Invoke();
        }
        catch (Exception ex)
        {
            _showNotification?.Invoke($"Delete Failed: {ex.Message}", true);
        }
    }

    [RelayCommand]
    private void AddToGitignore(GitFileStatus? file)
    {
        if (file == null) return;
        try
        {
            var gitignorePath = System.IO.Path.Combine(_repoPath, ".gitignore");
            System.IO.File.AppendAllText(gitignorePath, "\n" + file.FilePath);
            _onCommitAction?.Invoke();
        }
        catch (Exception ex)
        {
            _showNotification?.Invoke($"AddToGitignore Failed: {ex.Message}", true);
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RollbackFile(GitFileStatus? file)
    {
        if (file == null) return;
        try
        {
            if (!await ConfirmDiscardAsync(new[] { file })) return;

            _gitService.DiscardChanges(_repoPath, new[] { file.FilePath });
            _onCommitAction?.Invoke();
        }
        catch (Exception ex)
        {
            _showNotification?.Invoke($"Rollback Failed: {ex.Message}", true);
        }
    }

    /// <summary>
    /// Explicit discard confirmation (audit 1.4): lists exactly which tracked
    /// files will be reverted vs which untracked files will be removed, as two
    /// distinct lists, before anything is touched.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> ConfirmDiscardAsync(
        System.Collections.Generic.IReadOnlyList<GitFileStatus> files)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow == null)
        {
            // No window to attach a dialog to (should not happen in the app);
            // refuse rather than discarding without consent.
            return false;
        }

        var tracked = files.Where(f => !f.IsUntracked).Select(f => f.FilePath).ToList();
        var untracked = files.Where(f => f.IsUntracked).Select(f => f.FilePath).ToList();

        var message = new System.Text.StringBuilder();
        if (tracked.Count > 0)
        {
            message.AppendLine($"Revert {tracked.Count} tracked file{(tracked.Count == 1 ? "" : "s")} to the last commit:");
            foreach (var path in tracked) message.AppendLine($"    {path}");
        }
        if (untracked.Count > 0)
        {
            if (message.Length > 0) message.AppendLine();
            message.AppendLine($"Remove {untracked.Count} untracked file{(untracked.Count == 1 ? "" : "s")} from disk:");
            foreach (var path in untracked) message.AppendLine($"    {path}");
        }

        var vm = new ConfirmationDialogViewModel
        {
            Title = "Discard Changes",
            Message = message.ToString().TrimEnd(),
            ConfirmButtonText = untracked.Count > 0
                ? $"Revert {tracked.Count} and remove {untracked.Count}"
                : $"Revert {tracked.Count} file{(tracked.Count == 1 ? "" : "s")}"
        };
        var dialog = new Views.ConfirmationDialog { DataContext = vm };
        await dialog.ShowDialog(desktop.MainWindow);
        return vm.IsConfirmed;
    }

    [RelayCommand]
    private void AddToVcs(GitFileStatus? file)
    {
        if (file == null) return;
        try
        {
            _gitService.StageFile(_repoPath, file.FilePath);
            _onCommitAction?.Invoke();
        }
        catch (Exception ex)
        {
            _showNotification?.Invoke($"Add to VCS Failed: {ex.Message}", true);
        }
    }

    [RelayCommand]
    private void ShowFileHistory(GitFileStatus? file)
    {
        if (file == null) return;
        OnFileHistoryRequested?.Invoke(file.FilePath);
    }

    [RelayCommand]
    private void ShowBlame(GitFileStatus? file)
    {
        if (file == null) return;
        OnBlameRequested?.Invoke(file.FilePath);
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RollbackSelected()
    {
        try
        {
            var selectedFiles = VersionedFiles.Where(f => f.IsSelected)
                .Concat(UnversionedFiles.Where(f => f.IsSelected)).ToList();

            if (selectedFiles.Count > 0)
            {
                if (!await ConfirmDiscardAsync(selectedFiles)) return;

                _gitService.DiscardChanges(_repoPath, selectedFiles.Select(f => f.FilePath).ToList());
                _onCommitAction?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _showNotification?.Invoke($"Rollback Failed: {ex.Message}", true);
        }
    }

    private bool CanStashPush => !string.IsNullOrWhiteSpace(StashMessage) && (VersionedFiles.Count > 0 || UnversionedFiles.Count > 0);

    [RelayCommand(CanExecute = nameof(CanStashPush))]
    private void StashPush()
    {
        _gitService.StashPush(_repoPath, StashMessage);
        StashMessage = string.Empty;
        _onCommitAction?.Invoke(); // Trigger refresh
    }

    [RelayCommand]
    private void StashPop(GitStashItem stash)
    {
        try
        {
            _gitService.StashPop(_repoPath, stash.Index);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Stash Pop Error: {ex.Message}");
        }
        _onCommitAction?.Invoke();
    }

    [RelayCommand]
    private void StashApply(GitStashItem stash)
    {
        try
        {
            _gitService.StashApply(_repoPath, stash.Index);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Stash Apply Error: {ex.Message}");
        }
        _onCommitAction?.Invoke();
    }

    [RelayCommand]
    private void StashDrop(GitStashItem stash)
    {
        _gitService.StashDrop(_repoPath, stash.Index);
        _onCommitAction?.Invoke();
    }
}
