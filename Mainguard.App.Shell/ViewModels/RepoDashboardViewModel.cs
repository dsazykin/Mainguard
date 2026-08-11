using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.UI.ViewModels;
namespace Mainguard.App.Shell.ViewModels;

public partial class RepoDashboardViewModel : ViewModelBase, System.IDisposable
{
    private readonly string _repoPath;
    private readonly IGitService _gitService;
    // Operation journal (T-19): shared SQLite-backed store. Instances are stateless (all
    // state lives in the DB), so the GitService, InteractiveRebaseService, and the history
    // panel each hold their own instance pointing at the same default AppDbContext.
    private readonly IOperationJournal _journal = new OperationJournal();
    private readonly RepositoryWatcher _watcher;

    // Shared HttpClient for the host-integration providers (T-23 pull requests, T-24 issues) — one
    // process-wide instance so opening those panels repeatedly never leaks sockets (a per-call
    // `new HttpClient` is a rejection trigger).
    private static readonly System.Net.Http.HttpClient _prHttpClient = new();

    // Background auto-fetch (T-10): keeps ahead/behind fresh off the UI thread. The
    // cadence comes from UserPreferences.AutoFetchMinutes (0 disables it).
    private readonly AutoFetchService _autoFetch;
    private System.Threading.Timer? _lastFetchedTicker;

    [ObservableProperty]
    private string _repositoryName;

    [ObservableProperty]
    private int? _aheadCount;

    [ObservableProperty]
    private int? _behindCount;

    // "Fetched N min ago" surfaced next to the ahead/behind badge; dims when stale (T-10 / 1.12).
    [ObservableProperty]
    private string _lastFetchedText = string.Empty;

    [ObservableProperty]
    private bool _isLastFetchStale;

    // True while background fetch is failing for this repo (expired token, deleted remote, no network).
    // Drives the warning colour on the freshness label — the label's TEXT says so too, because a colour
    // alone is not a message.
    [ObservableProperty]
    private bool _isAutoFetchFailing;

    private System.DateTimeOffset? _lastFetched;

    // Guards the one-and-only auto-fetch failure toast per outage: the user is TOLD the first time, and
    // then the persistent label carries it. Reset by the next success, so a new outage speaks again.
    private bool _autoFetchFailureAnnounced;

    // Toasts (#85): stacked, newest at the bottom, capped at 3 -- each owns its own auto-dismiss
    // timer (see ToastViewModel) so a burst of notifications never silently drops earlier ones.
    private const int MaxToasts = 3;
    public System.Collections.ObjectModel.ObservableCollection<ToastViewModel> Toasts { get; } = new();

    // NOTE (removed, deliberately): there used to be an inline SSH-passphrase prompt here —
    // IsSshPassphrasePromptVisible / SshPassphraseInput / Save+CancelSshPassphraseCommand. All four
    // layers of it were dead. No .axaml bound any of them, so the prompt could not appear; the handler
    // that set the flag showed no notification, so the failure was silent; the exception that triggered
    // it was never thrown by anything; and the secret it wrote ("ssh_passphrase") was never read, since
    // SshKeyService keys passphrases per key as "sshpass_<path>". Reinstating it would have meant
    // building a modal AND an SSH_ASKPASS bridge to hand the secret to ssh — a feature, not a defect
    // fix, and one that wants the passphrase off the keyring-only path this app is careful about.
    // What replaced it: SshAuthenticationException is now genuinely thrown (GitService), and
    // HandleGitActionException below reports it and routes to Settings → SSH Keys, which is where
    // passphrases are actually stored.

    public StagingPanelViewModel StagingPanel { get; }
    public DiffViewerViewModel DiffViewer { get; }
    public CommitTimelineViewModel CommitTimeline { get; }
    public BranchBrowserViewModel BranchBrowser { get; }

    // Opens another path as its own top-level repository (used by the submodules panel's
    // "open as its own repo" action). Wired from MainWindowViewModel; null in isolated tests.
    private readonly System.Action<string>? _openRepositoryPath;

    public RepoDashboardViewModel(Repository repository, System.Action<string>? openRepositoryPath = null)
    {
        _repoPath = repository.Path;
        RepositoryName = repository.DisplayName;
        _openRepositoryPath = openRepositoryPath;
        // Feed the live signing preferences to the git service so an enabled "Sign Commits"
        // toggle takes effect on the next commit/tag without a restart (T-15).
        _gitService = new GitService(
            () => Mainguard.App.Shell.App.Settings?.Current ?? new Mainguard.Git.Models.UserPreferences(),
            _journal);

        StagingPanel = new StagingPanelViewModel(_gitService, _repoPath, () =>
        {
            _watcher?.ForceRefresh();
        }, (msg, isError) =>
        {
            ShowNotification(msg, isError);
        },
        scanner: new Mainguard.Git.Services.PreCommitScanner(_gitService),
        preferences: () => Mainguard.App.Shell.App.Settings?.Current ?? new Mainguard.Git.Models.UserPreferences(),
        settings: Mainguard.App.Shell.App.Settings);
        DiffViewer = new DiffViewerViewModel(_gitService, _repoPath,
            onStagingChanged: () => _watcher?.ForceRefresh(),
            settings: Mainguard.App.Shell.App.Settings);
        DiffViewer.FileHistoryRequested += (filePath) => _ = OpenFileHistoryAsync(filePath);
        DiffViewer.BlameRequested += (filePath) => _ = OpenBlameAsync(filePath);
        CommitTimeline = new CommitTimelineViewModel(_gitService, _repoPath, ShowNotification);
        BranchBrowser = new BranchBrowserViewModel(_gitService, _repoPath,
            onBranchChangedAction: () =>
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(500);
                    _watcher?.ForceRefresh();
                });
            },
            showNotificationAction: (msg) =>
            {
                ShowNotification(msg, msg.Contains("fail", System.StringComparison.OrdinalIgnoreCase) || msg.Contains("Error", System.StringComparison.OrdinalIgnoreCase));
            },
            onCompareBranchAction: (branchName) =>
            {
                CommitTimeline.LoadInitialCommits(branchName);
            },
            onCreatePullRequestAction: () => _ = OpenPullRequestsAsync(beginCreate: true),
            onCheckoutInWorktreeAction: branch => _ = CheckoutBranchInWorktreeAsync(branch)
        );

        StagingPanel.OnFileHistoryRequested += (filePath) =>
        {
            _ = OpenFileHistoryAsync(filePath);
        };

        StagingPanel.OnBlameRequested += (filePath) =>
        {
            _ = OpenBlameAsync(filePath);
        };

        StagingPanel.SelectedFileChanged += (file) => DiffViewer.UpdateDiff(file);

        // Load immediately
        _ = LoadRepositoryAsync();

        // Start listening for background folder changes
        _watcher = new RepositoryWatcher(_repoPath);
        _watcher.RepositoryChanged += OnRepositoryChanged;

        // Background auto-fetch: on each successful fetch, refresh ahead/behind and the
        // "last fetched" label on the UI thread. Failures are surfaced too — see OnAutoFetchFailed.
        _autoFetch = new AutoFetchService(_gitService,
            () => Mainguard.App.Shell.App.Settings?.Current ?? new Mainguard.Git.Models.UserPreferences());
        _autoFetch.Fetched += OnAutoFetched;
        _autoFetch.FetchFailed += OnAutoFetchFailed;
        _autoFetch.Watch(_repoPath);

        // Refresh the relative "N min ago" label once a minute so it ages correctly.
        _lastFetchedTicker = new System.Threading.Timer(
            _ => Dispatcher.UIThread.Post(UpdateLastFetchedLabel), null,
            System.TimeSpan.FromMinutes(1), System.TimeSpan.FromMinutes(1));
    }

    /// <summary>The background auto-fetch loop this dashboard owns. Exposed so a test can drive one real
    /// cycle (<c>RunCycleAsync</c>) against a real broken remote and observe what the USER is told —
    /// rather than poking the private handler and proving only that a field moved.</summary>
    internal AutoFetchService AutoFetch => _autoFetch;

    private void OnAutoFetched(string repoPath)
    {
        _lastFetched = _autoFetch.GetLastFetched(repoPath);
        Dispatcher.UIThread.Post(async () =>
        {
            IsAutoFetchFailing = false;
            _autoFetchFailureAnnounced = false;
            UpdateLastFetchedLabel();
            await RefreshCoreAsync();
        });
    }

    /// <summary>
    /// Background fetch failed. The point of surfacing this is NOT the failure itself — it is that the
    /// ahead/behind badge stops being true the moment fetching stops, and the freshness label beside it
    /// keeps counting up from the last SUCCESS, which reads as "we checked, you're up to date". An
    /// expired token or a deleted remote therefore used to present as a repo that is simply not moving.
    ///
    /// <para>One toast per outage (the actionable moment), then a persistent warning on the label so the
    /// state is still legible ten minutes later. Not per-cycle: that is the toast spam the original
    /// design note was right to refuse — it just refused it by saying nothing at all.</para>
    /// </summary>
    private void OnAutoFetchFailed(string repoPath, int consecutiveFailures, string reason)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsAutoFetchFailing = true;
            UpdateLastFetchedLabel();

            if (_autoFetchFailureAnnounced) return;
            _autoFetchFailureAnnounced = true;
            ShowNotification(
                $"Background fetch failed — ahead/behind counts may be out of date. {reason}", true);
        });
    }

    private void UpdateLastFetchedLabel()
    {
        if (_lastFetched is not { } when)
        {
            // Never fetched successfully AND currently failing: say so, rather than showing nothing at
            // all — an empty label is indistinguishable from auto-fetch being switched off.
            LastFetchedText = IsAutoFetchFailing ? "Fetch failing" : string.Empty;
            IsLastFetchStale = false;
            return;
        }

        var elapsed = System.DateTimeOffset.Now - when;
        var minutes = (int)elapsed.TotalMinutes;
        var fetched = minutes <= 0
            ? "Fetched just now"
            : minutes == 1 ? "Fetched 1 min ago" : $"Fetched {minutes} min ago";
        LastFetchedText = IsAutoFetchFailing ? $"Fetch failing — {fetched.ToLowerInvariant()}" : fetched;
        // Dimmed once the picture is more than 15 minutes old (closes the 1.12 stale badge). A failing
        // fetch is never dimmed — it is the one state that must not recede.
        IsLastFetchStale = minutes > 15 && !IsAutoFetchFailing;
    }

    private void OnRepositoryChanged()
    {
        Dispatcher.UIThread.InvokeAsync(async () => await RefreshCoreAsync());
    }

    // --- Command palette surface (T-18) ---

    /// <summary>The open repository's path, exposed so palette actions (e.g. Analytics) can target it.</summary>
    public string RepositoryPath => _repoPath;

    /// <summary>Local branches for the palette to offer as checkout targets. Returns empty on any read error.</summary>
    public System.Collections.Generic.IReadOnlyList<GitBranchItem> ListLocalBranches()
    {
        try
        {
            return _gitService.GetBranches(_repoPath).Where(b => !b.IsRemote).ToList();
        }
        catch
        {
            return System.Array.Empty<GitBranchItem>();
        }
    }

    /// <summary>Checks out a branch selected from the palette (delegates to the branch browser's command).</summary>
    public void CheckoutBranchFromPalette(GitBranchItem branch) => BranchBrowser.CheckoutBranchCommand.Execute(branch);

    public void ShowNotification(string message, bool isError = false)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var toast = new ToastViewModel(message, isError, t => Toasts.Remove(t));
            Toasts.Add(toast);
            while (Toasts.Count > MaxToasts)
            {
                var oldest = Toasts[0];
                Toasts.RemoveAt(0);
                oldest.Dispose();
            }
        });
    }

    [ObservableProperty]
    private bool _isLoading = true;

    // True while a network git op is running; disables the toolbar commands and
    // drives a progress overlay so the UI never appears frozen.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PushCommand))]
    [NotifyCanExecuteChangedFor(nameof(PullCommand))]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateProjectCommand))]
    private bool _isBusy;

    private bool CanRunGitAction() => !IsBusy;

    // First open only (constructor): shows the full-panel "Parsing Repository..." loading screen
    // while the initial status/timeline/branch data loads.
    private async System.Threading.Tasks.Task LoadRepositoryAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => IsLoading = true);
        await RefreshCoreAsync();
        await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
    }

    // Every subsequent refresh (file-watcher change, post-commit/checkout, post-push/pull/fetch,
    // after a Manage* dialog closes, auto-fetch) — refreshes status/timeline/branches in place
    // WITHOUT the full-screen loading overlay, so a single commit doesn't blank the dashboard (#64).
    // Marshals every mutation via Dispatcher.UIThread so it is safe to call from a background thread
    // too — the constructor's initial call now runs from inside a Task.Run (#63).
    private async System.Threading.Tasks.Task RefreshCoreAsync()
    {
        var allChanges = await System.Threading.Tasks.Task.Run(() => _gitService.GetRepositoryStatus(_repoPath));
        var aheadBehind = await System.Threading.Tasks.Task.Run(() => _gitService.GetAheadBehind(_repoPath));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            StagingPanel.UpdateStatus(allChanges);
            StagingPanel.LoadStashes();
            AheadCount = aheadBehind.Ahead;
            BehindCount = aheadBehind.Behind;
            CommitTimeline.LoadInitialCommits();
            BranchBrowser.LoadBranches();
        });
    }

    // Returns the exception of type T whether it is the thrown exception itself
    // or wrapped as its InnerException, so callers can surface the typed
    // exception's own actionable message rather than an outer wrapper's text.
    private static T? Unwrap<T>(System.Exception ex) where T : class
        => ex as T ?? ex.InnerException as T;

    private void HandleGitActionException(System.Exception ex, string actionName)
    {
        if (Unwrap<Mainguard.Git.Exceptions.SshAuthenticationException>(ex) is { } ssh)
        {
            // An SSH key problem is NOT a token problem: the Accounts/PAT route below cannot fix it.
            // Say what failed, then land the user on the page that owns SSH keys and their passphrases.
            ShowNotification($"{actionName} failed: {ssh.Message}", true);
            _ = ManageSshKeysAsync();
        }
        else if (Unwrap<Mainguard.Git.Exceptions.MergeConflictException>(ex) is { } conflict)
        {
            // Not a hard failure: the repo is now in a conflicted state. Surface
            // guidance (not an error toast) and open the resolver so the user can
            // resolve base/ours/theirs and complete the merge/rebase.
            ShowNotification(conflict.Message, false);
            _ = ShowConflictResolverAsync();
        }
        else if (Unwrap<Mainguard.Git.Exceptions.GitIdentityMissingException>(ex) is { } identity)
        {
            ShowNotification(identity.Message, true);
        }
        else if (Unwrap<Mainguard.Git.Exceptions.AuthenticationRequiredException>(ex) is { } auth)
        {
            // T-14: when the failing host is known, route straight to the Accounts page
            // (PAT dialog) for that host; otherwise show actionable guidance.
            if (!string.IsNullOrEmpty(auth.Host))
            {
                ShowNotification($"{actionName} failed: sign in to {auth.Host} to continue.", true);
                _ = OpenAccountsAsync(auth.Host);
            }
            else
            {
                ShowNotification(
                    $"{actionName} failed: authentication required. Open Accounts to sign in or store a token for this host.",
                    true);
            }
        }
        else
        {
            ShowNotification($"{actionName} Failed: {ex.Message}", true);
        }
    }

    // Opens the conflict resolver over the main window, then refreshes so resolved
    // state (or an abort) is reflected. Routed to from the MergeConflictException branch.
    private async System.Threading.Tasks.Task ShowConflictResolverAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var dialog = new Views.ConflictedFilesWindow();
            dialog.DataContext = new ConflictedFilesViewModel(_repoPath, _gitService, new MergeDiffService(), dialog);
            await dialog.ShowDialog(desktop.MainWindow);
        }
        await RefreshCoreAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRunGitAction))]
    private async System.Threading.Tasks.Task PushAsync(System.Threading.CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await System.Threading.Tasks.Task.Run(() => _gitService.Push(_repoPath), ct);
            ShowNotification("Push completed successfully.", false);
        }
        catch (System.OperationCanceledException) { ShowNotification("Push cancelled.", false); }
        catch (System.Exception ex) { HandleGitActionException(ex, "Push"); }
        finally
        {
            IsBusy = false;
            await RefreshCoreAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunGitAction))]
    private async System.Threading.Tasks.Task PullAsync(System.Threading.CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await System.Threading.Tasks.Task.Run(() => _gitService.Pull(_repoPath), ct);
            ShowNotification("Pull completed successfully.", false);
        }
        catch (System.OperationCanceledException) { ShowNotification("Pull cancelled.", false); }
        catch (System.Exception ex) { HandleGitActionException(ex, "Pull"); }
        finally
        {
            IsBusy = false;
            await RefreshCoreAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunGitAction))]
    private async System.Threading.Tasks.Task FetchAsync(System.Threading.CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await System.Threading.Tasks.Task.Run(() => _gitService.Fetch(_repoPath), ct);
            BranchBrowser.LoadBranches();
            ShowNotification("Fetch completed successfully.", false);
        }
        catch (System.OperationCanceledException) { ShowNotification("Fetch cancelled.", false); }
        catch (System.Exception ex) { HandleGitActionException(ex, "Fetch"); }
        finally
        {
            IsBusy = false;
            await RefreshCoreAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunGitAction))]
    private async System.Threading.Tasks.Task UpdateProjectAsync(System.Threading.CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await System.Threading.Tasks.Task.Run(() => _gitService.UpdateProject(_repoPath), ct);
            ShowNotification("Project updated successfully.", false);
        }
        catch (System.OperationCanceledException) { ShowNotification("Update cancelled.", false); }
        catch (System.Exception ex) { HandleGitActionException(ex, "UpdateProject"); }
        finally
        {
            IsBusy = false;
            await RefreshCoreAsync();
        }
    }

    // ---- Push options (T-10) ------------------------------------------------
    // Each resolves the target remote (tracked → origin → sole) in Core; the current
    // branch comes from the branch browser. force-with-lease is the only force path.

    [RelayCommand(CanExecute = nameof(CanRunGitAction))]
    private async System.Threading.Tasks.Task PushForceWithLeaseAsync(System.Threading.CancellationToken ct)
        => await RunPushOptionAsync("Force push (with lease)", (remote, branch) =>
            _gitService.PushForceWithLease(_repoPath, remote, branch), ct);

    [RelayCommand(CanExecute = nameof(CanRunGitAction))]
    private async System.Threading.Tasks.Task PushSetUpstreamAsync(System.Threading.CancellationToken ct)
        => await RunPushOptionAsync("Push (set upstream)", (remote, branch) =>
            _gitService.PushSetUpstream(_repoPath, remote, branch), ct);

    [RelayCommand(CanExecute = nameof(CanRunGitAction))]
    private async System.Threading.Tasks.Task PushTagsAsync(System.Threading.CancellationToken ct)
        => await RunPushOptionAsync("Push tags", (remote, _) =>
            _gitService.PushTags(_repoPath, remote), ct);

    private async System.Threading.Tasks.Task RunPushOptionAsync(
        string label, System.Action<string, string> op, System.Threading.CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            var branch = BranchBrowser.CurrentBranchName;
            await System.Threading.Tasks.Task.Run(() =>
            {
                var remote = _gitService.GetDefaultRemoteName(_repoPath);
                op(remote, branch);
            }, ct);
            ShowNotification($"{label} completed successfully.", false);
        }
        catch (System.OperationCanceledException) { ShowNotification($"{label} cancelled.", false); }
        catch (System.Exception ex) { HandleGitActionException(ex, label); }
        finally
        {
            IsBusy = false;
            await RefreshCoreAsync();
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ManageRemotesAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var dialog = new Views.RemotesWindow
            {
                DataContext = new RemotesViewModel(_gitService, _repoPath,
                    onChanged: () => _watcher?.ForceRefresh())
            };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshCoreAsync();
        }
    }

    // Operation history panel (T-19): per-entry undo/redo of journaled operations.
    [RelayCommand]
    private async System.Threading.Tasks.Task ManageOperationHistoryAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var dialog = new Views.OperationHistoryWindow
            {
                DataContext = new OperationHistoryViewModel(_journal, _repoPath,
                    onChanged: () => _watcher?.ForceRefresh())
            };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshCoreAsync();
        }
    }

    // Reflog viewer & recovery (T-20): per-ref reflog with restore (journaled hard reset) and
    // create-branch-here (journaled orphan-tip recovery).
    [RelayCommand]
    private async System.Threading.Tasks.Task ViewReflogAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var dialog = new Views.ReflogWindow
            {
                DataContext = new ReflogViewModel(_gitService, _repoPath,
                    onChanged: () => _watcher?.ForceRefresh())
            };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshCoreAsync();
        }
    }


    // ---- Host-surface VM factories (2026-07-11): the MainWindow section tabs and the
    // legacy dialog entry points build the exact same ViewModels through these. ----

    public PullRequestsViewModel CreatePullRequestsViewModel() => new(
        new Mainguard.Git.Services.PullRequestService(_gitService, httpClient: _prHttpClient),
        _gitService, _repoPath,
        openUrl: null,
        pickWorktreeFolder: PickWorktreeTargetAsync,
        openWorktree: path => _openRepositoryPath?.Invoke(path));

    public IssuesViewModel CreateIssuesViewModel() =>
        new(new Mainguard.Git.Services.IssueService(_gitService, httpClient: _prHttpClient), _repoPath);

    public NotificationsViewModel CreateNotificationsViewModel() =>
        new(new Mainguard.Git.Services.NotificationService(_gitService, httpClient: _prHttpClient), _repoPath);

    public ReleasesViewModel CreateReleasesViewModel() =>
        new(new Mainguard.Git.Services.ReleaseService(_gitService, httpClient: _prHttpClient), _gitService, _repoPath);

    /// <summary>Ahead/behind + status refresh after a host surface mutated remote state.
    /// Routes through the in-place refresh (#64) — no full-screen loading overlay.</summary>
    public System.Threading.Tasks.Task RefreshAfterHostSurfaceAsync() => RefreshCoreAsync();

    // Pull Requests panel (T-23): host-agnostic PR list/create/merge/close over IPullRequestService
    // (GitHub v1). Gracefully shows an unsupported/sign-in state when the origin host has no provider
    // or no stored token. beginCreate=true (from the branch context menu / palette) opens the create form.
    [RelayCommand]
    private System.Threading.Tasks.Task ManagePullRequests() => OpenPullRequestsAsync(beginCreate: false);

    /// <summary>Opens the Pull Requests panel; <paramref name="beginCreate"/> jumps straight to the create form.</summary>
    public async System.Threading.Tasks.Task OpenPullRequestsAsync(bool beginCreate)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            // phase2's factory; #143's revealInFileExplorer defaults to the real launcher
            // inside PullRequestsViewModel when not supplied.
            var vm = CreatePullRequestsViewModel();
            if (beginCreate && vm.BeginCreateCommand.CanExecute(null))
                vm.BeginCreateCommand.Execute(null);
            var dialog = new Views.PullRequestsWindow { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshCoreAsync();
        }
    }

    // Folder pick for a PR/branch worktree (T-29). Given a default full worktree path (`../<repo>-pr-<n>`),
    // prompts for the PARENT directory (a folder picker selects existing dirs; `git worktree add` creates the
    // leaf), then returns `<chosen parent>/<leaf>`. Returns null when the user cancels.
    private async System.Threading.Tasks.Task<string?> PickWorktreeTargetAsync(string defaultPath)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var trimmed = defaultPath.TrimEnd('/', '\\');
            var leaf = System.IO.Path.GetFileName(trimmed);
            var parent = System.IO.Path.GetDirectoryName(trimmed);
            var storageProvider = desktop.MainWindow.StorageProvider;
            var options = new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Choose where to create the worktree",
                AllowMultiple = false,
            };
            if (!string.IsNullOrEmpty(parent))
            {
                try { options.SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(new System.Uri(parent)); }
                catch { /* best-effort start location */ }
            }
            var result = await storageProvider.OpenFolderPickerAsync(options);
            if (result.Count > 0)
                return System.IO.Path.Combine(result[0].Path.LocalPath, leaf);
            return null;
        }
        return defaultPath;
    }

    // Branch → new worktree (T-29). Picks a target folder (default `../<repo>-<branch-leaf>`), creates a
    // worktree checked out to the (local or remote-tracking) branch off the UI thread, then opens it as a
    // repo. Remote-tracking refs get a local tracking branch created first (in the service).
    public async System.Threading.Tasks.Task CheckoutBranchInWorktreeAsync(Mainguard.Git.Models.GitBranchItem branch)
    {
        var trimmed = _repoPath.TrimEnd('/', '\\');
        var parent = System.IO.Path.GetDirectoryName(trimmed) ?? trimmed;
        var repoName = System.IO.Path.GetFileName(trimmed);
        var branchLeaf = branch.FriendlyName.Split('/').Last();
        var defaultPath = System.IO.Path.Combine(parent, $"{repoName}-{branchLeaf}");

        var target = await PickWorktreeTargetAsync(defaultPath);
        if (string.IsNullOrWhiteSpace(target)) return;

        try
        {
            var created = await System.Threading.Tasks.Task.Run(
                () => _gitService.CheckoutBranchWorktree(_repoPath, branch.FriendlyName, target));
            ShowNotification($"Checked out '{branch.FriendlyName}' into a new worktree.");
            _openRepositoryPath?.Invoke(created);
        }
        catch (System.Exception ex)
        {
            ShowNotification(ex.Message, isError: true);
        }
    }

    // Issues panel (T-24): host-agnostic issue list/create/comment/close over IIssueService (GitHub v1).
    // Gracefully shows an unsupported/sign-in state when the origin host has no provider or no stored
    // token. Reuses the same shared HttpClient as the PR panel (no second client).
    [RelayCommand]
    private async System.Threading.Tasks.Task ManageIssues()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var vm = CreateIssuesViewModel();
            var dialog = new Views.IssuesWindow { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshCoreAsync();
        }
    }

    // Notifications inbox (T-27): the authenticated user's notifications for the origin host over
    // INotificationService (GitHub v1), grouped by repo with mark-read / mark-all / open + an unread-only
    // toggle. Gracefully shows an unsupported/sign-in state when the origin host has no provider or no
    // stored token. Reuses the same shared HttpClient as the PR/issue panels (no second client).
    [RelayCommand]
    private async System.Threading.Tasks.Task ManageNotifications()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var vm = CreateNotificationsViewModel();
            var dialog = new Views.NotificationsWindow { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshCoreAsync();
        }
    }

    // Releases panel (T-28): host-agnostic release list/create over IReleaseService (GitHub v1) plus a
    // fully-local "auto-generate notes" (walks the commit history through the pure ChangelogGenerator — no
    // network). Gracefully shows an unsupported/sign-in state when the origin host has no provider or no
    // stored token. Reuses the same shared HttpClient as the PR/issue panels (no second client).
    [RelayCommand]
    private async System.Threading.Tasks.Task ManageReleases()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var vm = CreateReleasesViewModel();
            var dialog = new Views.ReleasesWindow { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshCoreAsync();
        }
    }

    // Worktree management panel (T-21) over the T-07 porcelain backend: list, add (existing/new branch),
    // open, remove/force, prune. Branch-already-checked-out is validated in the VM (create disabled).
    [RelayCommand]
    private async System.Threading.Tasks.Task ManageWorktreesAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var dialog = new Views.WorktreeWindow
            {
                DataContext = new WorktreePanelViewModel(_gitService, _repoPath,
                    onOpenWorktree: _openRepositoryPath)
            };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshCoreAsync();
        }
    }

    // Git identity profiles (T-21) moved to Settings → Git Profiles (ProfilesPageViewModel), which
    // reads the open repo's path live and calls RefreshAfterHostSurfaceAsync on navigating away —
    // no toolbar button triggers this anymore, so the command doesn't need to exist here.

    // Submodules panel (T-16): list + init/update, update-to-remote, sync, open-as-repo.
    [RelayCommand]
    private async System.Threading.Tasks.Task ManageSubmodulesAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var dialog = new Views.SubmodulesWindow
            {
                DataContext = new SubmodulesViewModel(_gitService, _repoPath,
                    onChanged: () => _watcher?.ForceRefresh(),
                    openRepository: _openRepositoryPath)
            };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshCoreAsync();
        }
    }

    // Git LFS panel (T-17): per-repo enable toggle, tracked patterns, LFS objects, pull, prune.
    [RelayCommand]
    private async System.Threading.Tasks.Task ManageLfsAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            // LfsService composes the concrete GitService so LFS network ops reuse the one
            // audited authenticated CLI path (T-14). _gitService is always a GitService here.
            var lfs = new LfsService((GitService)_gitService);
            var dialog = new Views.LfsWindow
            {
                DataContext = new LfsViewModel(lfs, _repoPath)
            };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshCoreAsync();
        }
    }

    // Pro Tools commands (step 1c): AI Providers/Agent CLIs/Daemon logs/Rebuild sandbox images/Add
    // Repos to Mainguard OS moved to Settings sidebar pages (SettingsViewModel, built from
    // App.Edition.ProTools directly) — none of the five had any deep-link call site besides the old
    // Tools dropdown, which is gone, so the commands don't need to exist here anymore.

    // Accounts/SSH Keys (T-14) moved to Settings sidebar pages too, but Accounts keeps a real
    // in-session deep link: a git-auth failure (HandleGitActionException) still needs to jump
    // straight there with the failing host pre-filled. Route both through the shell's Settings
    // navigation (MainWindowViewModel.OpenSettingsAsync) instead of constructing AccountsWindow/
    // SshKeysWindow directly — those two windows still exist standalone for the Client edition's
    // first-run flow (App.axaml.cs), which runs before Settings exists.
    [RelayCommand]
    private System.Threading.Tasks.Task ManageAccountsAsync() => OpenAccountsAsync(null);

    private System.Threading.Tasks.Task OpenAccountsAsync(string? focusHost)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow?.DataContext is MainWindowViewModel shell)
        {
            return shell.OpenSettingsAsync("Accounts", focusHost);
        }
        return System.Threading.Tasks.Task.CompletedTask;
    }

    [RelayCommand]
    private System.Threading.Tasks.Task ManageSshKeysAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow?.DataContext is MainWindowViewModel shell)
        {
            return shell.OpenSettingsAsync("SshKeys");
        }
        return System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>Opens the dedicated file-history dialog (T-12) for a repo-relative path. Shared by
    /// the staging-panel and diff-viewer "Show History" entry points.</summary>
    public async System.Threading.Tasks.Task OpenFileHistoryAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var dialog = new Views.FileHistoryView
            {
                DataContext = new FileHistoryViewModel(_gitService, _repoPath, filePath)
            };
            await dialog.ShowDialog(desktop.MainWindow);
        }
    }

    /// <summary>Opens the dedicated blame dialog (T-33) for a repo-relative path — the entry point that
    /// makes the T-11 blame gutter and the T-32 "Why this line" PR/issue popover reachable. Shared by the
    /// staging-panel and diff-viewer "Blame this file" entry points. The commit-context service (T-32) is
    /// constructed on the shared <see cref="_prHttpClient"/> and its jumps route into the in-app PR /
    /// Issues panels (falling back to the browser only when a panel can't be shown).</summary>
    public async System.Threading.Tasks.Task OpenBlameAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var commitContext = new Mainguard.Git.Services.CommitContextService(_gitService, httpClient: _prHttpClient);
            var vm = new BlameViewModel(_gitService, _repoPath, commitContext,
                openPullRequest: pr => { _ = OpenPullRequestsAsync(beginCreate: false); },
                openLinkedIssue: issue => ManageIssuesCommand.Execute(null))
            {
                FilePath = (filePath ?? string.Empty).Replace('\\', '/')
            };
            var dialog = new Views.BlameWindow { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);
        }
    }

    public void Dispose()
    {
        _autoFetch.Fetched -= OnAutoFetched;
        _autoFetch.FetchFailed -= OnAutoFetchFailed;
        _autoFetch.Dispose();
        _lastFetchedTicker?.Dispose();
        foreach (var toast in Toasts) toast.Dispose();
        _watcher.RepositoryChanged -= OnRepositoryChanged;
        _watcher.Dispose();
        CommitTimeline.Dispose();
    }
}
