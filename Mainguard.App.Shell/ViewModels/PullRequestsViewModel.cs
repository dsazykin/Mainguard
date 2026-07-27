using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.ViewModels;

/// <summary>
/// Pull Requests panel (T-23): lists the repo's open PRs (title / number / author / source→target,
/// with a draft badge) and drives Create / Merge (method picker) / Close / Open-in-browser through
/// the host-agnostic <see cref="IPullRequestService"/>. When the origin host is unsupported or no
/// token is stored, it shows a graceful sign-in / unsupported affordance instead of erroring.
///
/// <para>All network work runs inside the async service (off the UI thread) and is gated by
/// <see cref="IsBusy"/>; the bound <see cref="PullRequests"/> collection is only ever mutated on the
/// <see cref="Dispatcher.UIThread"/>. Create is disabled on a detached/unborn HEAD (nothing to open a
/// PR from) with a hint to push a branch first. Hosted by PullRequestsWindow.</para>
/// </summary>
public partial class PullRequestsViewModel : ViewModelBase
{
    private readonly IPullRequestService _pr;
    private readonly IGitService _git;
    private readonly string _repoPath;
    private readonly Action<string> _openUrl;
    private readonly Func<string, Task<string?>>? _pickWorktreeFolder;
    private readonly Action<string>? _openWorktree;
    private readonly Action<string> _revealInFileExplorer;
    private CancellationTokenSource? _cts;

    public ObservableCollection<PullRequestRowViewModel> PullRequests { get; } = new();

    [ObservableProperty]
    private bool _isSupported;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>Shown when <see cref="IsSupported"/> is false — the unsupported-host / sign-in affordance text.</summary>
    [ObservableProperty]
    private string _unsupportedHint = "";

    // ---- Create-PR form -----------------------------------------------------------------------

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private string _newTitle = "";

    [ObservableProperty]
    private string _newBody = "";

    [ObservableProperty]
    private string _newSourceBranch = "";

    [ObservableProperty]
    private string _newTargetBranch = "";

    [ObservableProperty]
    private bool _newIsDraft;

    /// <summary>Non-null when Create is disabled (e.g. detached HEAD) — the reason to show the user.</summary>
    [ObservableProperty]
    private string? _createDisabledHint;

    /// <summary>True when the current HEAD is a branch that can be the source of a PR.</summary>
    public bool CanCreate { get; private set; }

    // ---- Review (T-25) ------------------------------------------------------------------------

    /// <summary>The PR whose reviews + inline comment threads are shown; null when the review panel is closed.</summary>
    [ObservableProperty]
    private PullRequestRowViewModel? _selectedReviewPr;

    /// <summary>True while a PR's reviews/comment threads are shown below the list.</summary>
    [ObservableProperty]
    private bool _isReviewOpen;

    /// <summary>The submitted reviews on the selected PR (verdict badge + author + body).</summary>
    public ObservableCollection<ReviewRowViewModel> Reviews { get; } = new();

    /// <summary>The selected PR's inline review comments, grouped into threads by file path.</summary>
    public ObservableCollection<ReviewThreadViewModel> CommentThreads { get; } = new();

    [ObservableProperty]
    private bool _hasReviews;

    [ObservableProperty]
    private bool _hasCommentThreads;

    /// <summary>The verdict the submit-review form will send.</summary>
    [ObservableProperty]
    private ReviewVerdict _submitVerdict = ReviewVerdict.Comment;

    /// <summary>The body typed into the submit-review form.</summary>
    [ObservableProperty]
    private string _reviewBody = "";

    /// <summary>The three verdicts the submit-review picker offers.</summary>
    public IReadOnlyList<ReviewVerdict> Verdicts { get; } =
        new[] { ReviewVerdict.Comment, ReviewVerdict.Approve, ReviewVerdict.RequestChanges };

    // Wired from the View so Close works from the ViewModel.
    public Action? CloseAction { get; set; }

    public PullRequestsViewModel(IPullRequestService pr, IGitService git, string repoPath,
        Action<string>? openUrl = null,
        Func<string, Task<string?>>? pickWorktreeFolder = null,
        Action<string>? openWorktree = null,
        Action<string>? revealInFileExplorer = null)
    {
        _pr = pr;
        _git = git;
        _repoPath = repoPath;
        _openUrl = openUrl ?? Services.BrowserLauncher.OpenUrl;
        _pickWorktreeFolder = pickWorktreeFolder;
        _openWorktree = openWorktree;
        _revealInFileExplorer = revealInFileExplorer ?? Services.FileExplorerLauncher.RevealFolder;

        IsSupported = SafeIsSupported();
        if (!IsSupported)
        {
            UnsupportedHint =
                "Pull requests aren't available for this repository yet. Connect an account for the origin host " +
                "(GitHub is supported today) from Accounts, then reopen this panel.";
        }

        PrefillCreateForm();
    }

    private bool SafeIsSupported()
    {
        try { return _pr.IsSupported(_repoPath); }
        catch { return false; }
    }

    // Prefills the create form: source = current branch, target = default branch, title = last commit
    // subject. Also decides whether Create is allowed at all (detached/unborn HEAD → disabled + hint).
    private void PrefillCreateForm()
    {
        try
        {
            var head = _git.GetHeadState(_repoPath);
            if (head.IsDetached || head.IsUnborn || string.IsNullOrEmpty(head.CurrentBranchName))
            {
                CanCreate = false;
                CreateDisabledHint = head.IsUnborn
                    ? "This repository has no commits yet — commit before opening a pull request."
                    : "HEAD is detached — check out a branch (and push it) before opening a pull request.";
            }
            else
            {
                CanCreate = true;
                NewSourceBranch = head.CurrentBranchName;
                NewTargetBranch = ResolveDefaultBranch(head.CurrentBranchName);
                NewTitle = LastCommitSubject();
            }
        }
        catch
        {
            CanCreate = false;
            CreateDisabledHint = "Could not read the current branch.";
        }

        OnPropertyChanged(nameof(CanCreate));
        NotifyCommandStates();
    }

    private string ResolveDefaultBranch(string current)
    {
        try
        {
            var locals = _git.GetBranches(_repoPath)
                .Where(b => !b.IsRemote)
                .Select(b => b.FriendlyName)
                .ToList();
            foreach (var candidate in new[] { "main", "master", "develop" })
                if (locals.Contains(candidate) && !string.Equals(candidate, current, StringComparison.Ordinal))
                    return candidate;
            return locals.FirstOrDefault(b => !string.Equals(b, current, StringComparison.Ordinal)) ?? current;
        }
        catch
        {
            return current;
        }
    }

    private string LastCommitSubject()
    {
        try { return _git.GetRecentCommits(_repoPath, 0, 1).FirstOrDefault()?.MessageShort ?? ""; }
        catch { return ""; }
    }

    // ---- List --------------------------------------------------------------------------------

    private bool CanRefresh => IsSupported && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshList()
    {
        if (!IsSupported || IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var items = await _pr.ListAsync(_repoPath, PullRequestState.Open, ct);
            await ApplyOnUiAsync(() =>
            {
                PullRequests.Clear();
                foreach (var item in items)
                    PullRequests.Add(new PullRequestRowViewModel(item, this));
                IsEmpty = PullRequests.Count == 0;
            });
        }
        catch (OperationCanceledException) { /* superseded by a newer refresh — ignore */ }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Create ------------------------------------------------------------------------------

    private bool CanBeginCreate => IsSupported && CanCreate && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanBeginCreate))]
    private void BeginCreate()
    {
        ErrorMessage = null;
        IsCreating = true;
    }

    [RelayCommand]
    private void CancelCreate() => IsCreating = false;

    private bool CanSubmitCreate =>
        IsSupported && CanCreate && !IsBusy
        && !string.IsNullOrWhiteSpace(NewTitle)
        && !string.IsNullOrWhiteSpace(NewSourceBranch)
        && !string.IsNullOrWhiteSpace(NewTargetBranch);

    [RelayCommand(CanExecute = nameof(CanSubmitCreate))]
    private async Task SubmitCreate()
    {
        if (!CanSubmitCreate) return;
        IsBusy = true;
        ErrorMessage = null;
        var request = new CreatePullRequest
        {
            Title = NewTitle.Trim(),
            Body = NewBody,
            SourceBranch = NewSourceBranch.Trim(),
            TargetBranch = NewTargetBranch.Trim(),
            IsDraft = NewIsDraft,
        };
        try
        {
            await _pr.CreateAsync(_repoPath, request, CancellationToken.None);
            await ApplyOnUiAsync(() => IsCreating = false);
        }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (ErrorMessage is null)
            await RefreshList();
    }

    // ---- Per-row actions ---------------------------------------------------------------------

    internal async Task MergeAsync(PullRequestRowViewModel row, PullRequestMergeMethod method)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            // No head CAS here on purpose: this is the manual PR panel, where the human is looking at the
            // pull request they are merging and no verification is being spent on it. The merge-queue path
            // (P2-12 external entries) passes the verified head and is refused if the PR moved since.
            await _pr.MergeAsync(_repoPath, row.Number, method, expectedHeadSha: null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (ErrorMessage is null)
            await RefreshList();
    }

    internal async Task CloseAsync(PullRequestRowViewModel row)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await _pr.CloseAsync(_repoPath, row.Number, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (ErrorMessage is null)
            await RefreshList();
    }

    internal void OpenInBrowser(PullRequestRowViewModel row)
    {
        if (!string.IsNullOrWhiteSpace(row.Url))
            _openUrl(row.Url);
    }

    // ---- Check out locally (T-29) -------------------------------------------------------------

    /// <summary>The path of the worktree the last "Check out locally" created; drives the Open-worktree affordance.</summary>
    [ObservableProperty]
    private string? _lastCheckoutPath;

    /// <summary>True once a PR has been checked out locally this session (an "Open worktree" button is offered).</summary>
    [ObservableProperty]
    private bool _canOpenWorktree;

    /// <summary>The PR number that was last checked out locally (shown next to the Open-worktree affordance).</summary>
    [ObservableProperty]
    private int _lastCheckoutPrNumber;

    // Fetches the PR head into a separate worktree so the reviewer can build/run it without disturbing
    // the current checkout. Picks a folder (default `../<repo>-pr-<n>`), runs off the UI thread under the
    // IsBusy guard, and on success offers "Open worktree" (routes through the open-as-repo path) AND
    // reveals the new worktree root in the OS file explorer so the user can get to the files immediately.
    internal async Task CheckoutLocallyAsync(PullRequestRowViewModel row)
    {
        if (IsBusy) return;

        var defaultPath = DefaultWorktreePath(row.Number);
        var target = _pickWorktreeFolder is not null ? await _pickWorktreeFolder(defaultPath) : defaultPath;
        if (string.IsNullOrWhiteSpace(target)) return;   // user cancelled the folder pick

        IsBusy = true;
        ErrorMessage = null;
        string? created = null;
        try
        {
            var remote = _git.GetDefaultRemoteName(_repoPath);
            created = await _git.CheckoutPullRequestWorktree(_repoPath, row.Number, remote, target, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (created is not null && ErrorMessage is null)
        {
            await ApplyOnUiAsync(() =>
            {
                LastCheckoutPath = created;
                LastCheckoutPrNumber = row.Number;
                CanOpenWorktree = true;
            });

            // In addition to the in-app "Open worktree" affordance above, reveal the freshly
            // created worktree in the OS file explorer so the user can get to the files immediately.
            _revealInFileExplorer(created);
        }
    }

    private bool CanOpenLastWorktree => CanOpenWorktree && !string.IsNullOrWhiteSpace(LastCheckoutPath);

    [RelayCommand(CanExecute = nameof(CanOpenLastWorktree))]
    private void OpenWorktree()
    {
        if (!string.IsNullOrWhiteSpace(LastCheckoutPath))
            _openWorktree?.Invoke(LastCheckoutPath!);
    }

    // Default worktree target: a sibling of the repo named `<repo>-pr-<n>`.
    private string DefaultWorktreePath(int prNumber)
    {
        var trimmed = _repoPath.TrimEnd('/', '\\');
        var parent = System.IO.Path.GetDirectoryName(trimmed) ?? trimmed;
        var name = System.IO.Path.GetFileName(trimmed);
        return System.IO.Path.Combine(parent, $"{name}-pr-{prNumber}");
    }

    // ---- Review (T-25) ------------------------------------------------------------------------

    // Opens the review panel for a PR and loads its reviews + inline comment threads off the UI thread.
    internal async Task OpenReviewAsync(PullRequestRowViewModel row)
    {
        if (!IsSupported || IsBusy) return;
        SelectedReviewPr = row;
        IsReviewOpen = true;
        ReviewBody = "";
        SubmitVerdict = ReviewVerdict.Comment;
        await LoadReviewsAsync(row.Number);
    }

    [RelayCommand]
    private void CloseReview()
    {
        IsReviewOpen = false;
        SelectedReviewPr = null;
        Reviews.Clear();
        CommentThreads.Clear();
        HasReviews = false;
        HasCommentThreads = false;
        ReviewBody = "";
    }

    private async Task LoadReviewsAsync(int number)
    {
        IsBusy = true;
        ErrorMessage = null;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var reviews = await _pr.GetReviewsAsync(_repoPath, number, ct);
            var comments = await _pr.GetReviewCommentsAsync(_repoPath, number, ct);

            // Group inline comments into threads by file path (stable path order, comments oldest-first).
            var threads = comments
                .GroupBy(c => c.Path)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new ReviewThreadViewModel(g.Key, g.OrderBy(c => c.When).ToList()))
                .ToList();

            await ApplyOnUiAsync(() =>
            {
                Reviews.Clear();
                foreach (var r in reviews)
                    Reviews.Add(new ReviewRowViewModel(r));
                HasReviews = Reviews.Count > 0;

                CommentThreads.Clear();
                foreach (var t in threads)
                    CommentThreads.Add(t);
                HasCommentThreads = CommentThreads.Count > 0;
            });
        }
        catch (OperationCanceledException) { /* superseded by a newer load — ignore */ }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // A body is required for a plain Comment or a Request-changes verdict (GitHub rejects an empty one);
    // Approve may carry an empty body.
    private bool CanSubmitReview =>
        IsSupported && !IsBusy && SelectedReviewPr is not null
        && (SubmitVerdict == ReviewVerdict.Approve || !string.IsNullOrWhiteSpace(ReviewBody));

    [RelayCommand(CanExecute = nameof(CanSubmitReview))]
    private async Task SubmitReview()
    {
        if (!CanSubmitReview || SelectedReviewPr is null) return;
        var number = SelectedReviewPr.Number;
        IsBusy = true;
        ErrorMessage = null;
        var request = new SubmitReview { Verdict = SubmitVerdict, Body = ReviewBody };
        try
        {
            await _pr.SubmitReviewAsync(_repoPath, number, request, CancellationToken.None);
            await ApplyOnUiAsync(() => ReviewBody = "");
        }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (ErrorMessage is null)
            await LoadReviewsAsync(number);
    }

    // ---- Plumbing ----------------------------------------------------------------------------

    partial void OnIsBusyChanged(bool value) => NotifyCommandStates();
    partial void OnIsSupportedChanged(bool value) => NotifyCommandStates();
    partial void OnNewTitleChanged(string value) => SubmitCreateCommand.NotifyCanExecuteChanged();
    partial void OnNewSourceBranchChanged(string value) => SubmitCreateCommand.NotifyCanExecuteChanged();
    partial void OnNewTargetBranchChanged(string value) => SubmitCreateCommand.NotifyCanExecuteChanged();
    partial void OnReviewBodyChanged(string value) => SubmitReviewCommand.NotifyCanExecuteChanged();
    partial void OnSubmitVerdictChanged(ReviewVerdict value) => SubmitReviewCommand.NotifyCanExecuteChanged();
    partial void OnSelectedReviewPrChanged(PullRequestRowViewModel? value) => SubmitReviewCommand.NotifyCanExecuteChanged();
    partial void OnCanOpenWorktreeChanged(bool value) => OpenWorktreeCommand.NotifyCanExecuteChanged();
    partial void OnLastCheckoutPathChanged(string? value) => OpenWorktreeCommand.NotifyCanExecuteChanged();

    private void NotifyCommandStates()
    {
        RefreshListCommand.NotifyCanExecuteChanged();
        BeginCreateCommand.NotifyCanExecuteChanged();
        SubmitCreateCommand.NotifyCanExecuteChanged();
        SubmitReviewCommand.NotifyCanExecuteChanged();
    }

    // Applies a mutation to bound state on the UI thread (invariant G-5): never mutates the
    // observable collection off-thread. Runs inline when already on the UI thread.
    private static Task ApplyOnUiAsync(Action apply)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            apply();
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(apply).GetTask();
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}

/// <summary>One pull-request row (T-23): number/title/author/source→target with a draft badge, plus the
/// per-PR Merge (method picker) / Close / Open-in-browser affordances routed back through the parent.</summary>
public partial class PullRequestRowViewModel : ViewModelBase
{
    private readonly PullRequestsViewModel _parent;

    public int Number { get; }
    public string Title { get; }
    public string Author { get; }
    public string SourceBranch { get; }
    public string TargetBranch { get; }
    public string Url { get; }
    public bool IsDraft { get; }
    public PullRequestState State { get; }

    public PullRequestRowViewModel(PullRequestItem item, PullRequestsViewModel parent)
    {
        _parent = parent;
        Number = item.Number;
        Title = string.IsNullOrEmpty(item.Title) ? "(no title)" : item.Title;
        Author = item.Author;
        SourceBranch = item.SourceBranch;
        TargetBranch = item.TargetBranch;
        Url = item.Url;
        IsDraft = item.IsDraft;
        State = item.State;
    }

    public string NumberText => $"#{Number}";
    public string BranchFlow => $"{SourceBranch} → {TargetBranch}";
    public string AuthorText => string.IsNullOrEmpty(Author) ? "" : $"by {Author}";
    public bool ShowDraftBadge => IsDraft;
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);

    /// <summary>Merge methods offered by the picker (T-23): a normal merge commit, squash, or rebase.</summary>
    public IReadOnlyList<PullRequestMergeMethod> MergeMethods { get; } =
        new[] { PullRequestMergeMethod.Merge, PullRequestMergeMethod.Squash, PullRequestMergeMethod.Rebase };

    [ObservableProperty]
    private PullRequestMergeMethod _selectedMergeMethod = PullRequestMergeMethod.Merge;

    [RelayCommand]
    private Task Merge() => _parent.MergeAsync(this, SelectedMergeMethod);

    [RelayCommand]
    private Task ClosePr() => _parent.CloseAsync(this);

    [RelayCommand]
    private void OpenInBrowser() => _parent.OpenInBrowser(this);

    /// <summary>Opens the review panel for this PR (loads its reviews + inline comment threads). (T-25)</summary>
    [RelayCommand]
    private Task Review() => _parent.OpenReviewAsync(this);

    /// <summary>Fetches this PR into a separate worktree so it can be built/run without disturbing the
    /// current checkout, then offers to open it as a repo. (T-29)</summary>
    [RelayCommand]
    private Task CheckoutLocally() => _parent.CheckoutLocallyAsync(this);
}

/// <summary>
/// One submitted review on a PR (T-25): author + verdict badge + body + when. The verdict is exposed as
/// mutually-exclusive booleans so the View picks a design-token-styled badge (no color logic in the VM).
/// </summary>
public sealed class ReviewRowViewModel
{
    public string Author { get; }
    public ReviewState State { get; }
    public string Body { get; }
    public System.DateTimeOffset SubmittedAt { get; }

    public ReviewRowViewModel(PullRequestReview review)
    {
        Author = review.Author;
        State = review.State;
        Body = review.Body;
        SubmittedAt = review.SubmittedAt;
    }

    public string AuthorText => string.IsNullOrEmpty(Author) ? "(unknown)" : Author;
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);
    public string WhenText => SubmittedAt == default ? "" : SubmittedAt.LocalDateTime.ToString("MMM d, yyyy");

    // Verdict badge routing — the View selects a token-styled badge from these; the VM holds no brushes.
    public bool IsApproved => State == ReviewState.Approved;
    public bool IsChangesRequested => State == ReviewState.ChangesRequested;
    public bool IsNeutral => !IsApproved && !IsChangesRequested;

    public string VerdictText => State switch
    {
        ReviewState.Approved => "Approved",
        ReviewState.ChangesRequested => "Changes requested",
        ReviewState.Dismissed => "Dismissed",
        ReviewState.Pending => "Pending",
        _ => "Commented",
    };
}

/// <summary>One inline-comment thread on a PR (T-25): all review comments on a single file path.</summary>
public sealed class ReviewThreadViewModel
{
    public string Path { get; }
    public IReadOnlyList<ReviewCommentRowViewModel> Comments { get; }

    public ReviewThreadViewModel(string path, IReadOnlyList<ReviewComment> comments)
    {
        Path = string.IsNullOrEmpty(path) ? "(file)" : path;
        Comments = comments.Select(c => new ReviewCommentRowViewModel(c)).ToList();
    }

    public string CountText => Comments.Count == 1 ? "1 comment" : $"{Comments.Count} comments";
}

/// <summary>One inline review comment (T-25): path:line (or "outdated"), the diff-hunk context, author, body.</summary>
public sealed class ReviewCommentRowViewModel
{
    public string Author { get; }
    public int? Line { get; }
    public string DiffHunk { get; }
    public string Body { get; }
    public System.DateTimeOffset When { get; }

    public ReviewCommentRowViewModel(ReviewComment comment)
    {
        Author = comment.Author;
        Line = comment.Line;
        DiffHunk = comment.DiffHunk;
        Body = comment.Body;
        When = comment.When;
    }

    public string AuthorText => string.IsNullOrEmpty(Author) ? "(unknown)" : Author;
    public bool IsOutdated => Line is null;               // comment on a since-changed diff
    public bool HasLine => Line is not null;
    public string LineText => Line is int l ? $"line {l}" : "outdated";
    public bool HasDiffHunk => !string.IsNullOrWhiteSpace(DiffHunk);
    public string WhenText => When == default ? "" : When.LocalDateTime.ToString("MMM d, yyyy");
}
