using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibGit2Sharp;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Agents.Agents.Toolchains;
using Mainguard.Git.Services;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// Settings → Toolchains → "Declare a toolchain in this repository": the four-step, one-button-per-step
/// flow that turns "a repository opts in by committing <c>.mainguard/toolchain</c>" into something a
/// person can actually do, without the app doing any of it for them.
///
/// <para><b>Why four buttons and not one.</b> Writing a file, staging+committing it, pushing it, and
/// installing the toolchain on this machine are four different consequences with four different blast
/// radii. A single "Set this up" button would have to guess: guess that the dirty file next to yours may
/// ride along, guess that a branch switch is welcome, guess that a commit should leave the machine. This
/// surface refuses to guess. Each step does <i>exactly</i> its own work and nothing else — in
/// particular <see cref="CommitAsync"/> never pushes, and <see cref="WriteFileAsync"/> never stages.</para>
///
/// <para><b>Why every refusal is a sentence, not a greyed-out button.</b> This repo shipped a
/// permanently-disabled control with no explanation (#302) and it cost real time. So enablement here is
/// derived FROM the reason, not the other way round: each step has a <c>…DisabledReason</c> string, and
/// its <c>CanExecute</c> is exactly "that string is empty". A step can therefore never be disabled
/// without saying why — there is no code path that produces one without the other, and
/// <c>ToolchainDeclarationFlowTests</c> asserts the equivalence for all four.</para>
///
/// <para><b>Why nothing is done on the user's behalf.</b> A dirty working tree is refused, never
/// stashed. A non-default branch is refused (naming both branches), never checked out. Those are the two
/// places a "helpful" client silently rewrites someone's work, and both are exactly the class of bug
/// Mainguard exists to not have.</para>
///
/// <para><b>Why the default branch is resolved and never assumed.</b> The owner's repository is on
/// <c>master</c>; a hardcoded <c>main</c> would make the commit step permanently, inexplicably refuse.
/// The default comes from <c>refs/remotes/&lt;remote&gt;/HEAD</c> when the clone has one (that symbolic
/// ref IS the remote's default branch, and it is the only local authority that can disagree with the
/// branch you happen to be standing on), falling back to the existing
/// <see cref="RepoToolchainConfig.DefaultBranch"/> seam — <c>symbolic-ref --short HEAD</c>. In a
/// local-only repository with no remote there is nothing that could name a different default, so that
/// fallback correctly means "wherever HEAD points is the default". No branch name is ever written down
/// in this file.</para>
///
/// <para>Constructed directly (no DI); <see cref="IGitService"/> and <see cref="ToolchainChannel"/> are
/// the injectable seams. Two constructors — live, and design/harness — as elsewhere in this assembly.</para>
/// </summary>
public partial class ToolchainDeclarationViewModel : ViewModelBase
{
    /// <summary>The tracked path a repository declares its toolchain at. Single-sourced from the daemon
    /// side so the surface and the provisioner can never disagree about which file this is.</summary>
    public const string DeclarationPath = RepoToolchainConfig.Path;

    private readonly IGitService? _git;
    private readonly ToolchainChannel? _channel;
    private CancellationTokenSource? _cts;

    /// <summary>True once the user has picked a toolchain from the list — after that, a refresh must not
    /// silently move their selection back to whatever the file currently says.</summary>
    private bool _selectionIsTheUsers;

    /// <summary>Live constructor.</summary>
    /// <param name="repositoryPath">The open repository's working directory. Null/empty is a first-class
    /// state ("no repository is open"), reported as every step's reason rather than hidden.</param>
    /// <param name="git">The one git seam. All LibGit2Sharp access goes through its
    /// <see cref="IGitService.ExecuteWithRepo{T}"/>.</param>
    /// <param name="channel">The curated toolchain channel, for the install step. Null degrades that one
    /// step to a stated reason; the other three still work.</param>
    public ToolchainDeclarationViewModel(string? repositoryPath, IGitService git, ToolchainChannel? channel)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _channel = channel;
        _repositoryPath = string.IsNullOrWhiteSpace(repositoryPath) ? null : repositoryPath;
        _repositoryName = NameOf(_repositoryPath);

        if (channel is not null)
        {
            foreach (var id in channel.Manifest.KnownIds)
                AvailableToolchainIds.Add(id);
            _selectedToolchainId = AvailableToolchainIds.FirstOrDefault() ?? string.Empty;
        }

        ComputeReasons();
    }

    /// <summary>Design/harness constructor: fixed representative state, no services behind it. It runs
    /// the REAL <see cref="ComputeReasons"/>, so a rendered design state shows the shipped wording rather
    /// than a hand-written imitation of it.</summary>
    public ToolchainDeclarationViewModel(
        string repositoryName,
        string currentBranch,
        string defaultBranch,
        IEnumerable<string> availableToolchainIds,
        string? committedDeclaration = null,
        string? workingTreeDeclaration = null,
        bool declaredToolchainInstalled = false,
        IEnumerable<string>? otherChangedPaths = null,
        bool hasRemote = true,
        int? aheadBy = null)
    {
        _repositoryName = repositoryName;
        _isRepositoryAvailable = true;
        _currentBranch = currentBranch;
        _defaultBranch = defaultBranch;
        _committedDeclaration = committedDeclaration;
        _workingTreeDeclaration = workingTreeDeclaration;
        _declaredToolchainInstalled = declaredToolchainInstalled;
        _hasRemote = hasRemote;
        _aheadBy = aheadBy;
        foreach (var id in availableToolchainIds)
            AvailableToolchainIds.Add(id);
        _selectedToolchainId = AvailableToolchainIds.FirstOrDefault() ?? string.Empty;
        foreach (var p in otherChangedPaths ?? Array.Empty<string>())
            OtherChangedPaths.Add(p);
        _declarationHasUncommittedChange =
            !string.Equals(_workingTreeDeclaration, _committedDeclaration, StringComparison.Ordinal);

        RecomputeDeclared();
        ComputeReasons();
    }

    // ---- command enablement bridge ---------------------------------------------------------------
    //
    // Same shape as ToolchainSettingsViewModel's row bridge, for the same reason: a Button caches its
    // last CanExecute result, so a predicate whose inputs change without a CanExecuteChanged renders
    // visible and permanently dead (#302). Here every CanExecute reads exactly one thing — its step's
    // …DisabledReason — so the input set is those four names. ANY new CanExecute input must be added
    // to StepCommandInputs or its button will freeze in whatever state it was first evaluated in.

    private static readonly HashSet<string> StepCommandInputs = new(StringComparer.Ordinal)
    {
        nameof(WriteFileDisabledReason),
        nameof(CommitDisabledReason),
        nameof(PushDisabledReason),
        nameof(InstallDisabledReason),
    };

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // A null/empty name means "everything changed" (INotifyPropertyChanged convention).
        if (string.IsNullOrEmpty(e.PropertyName) || StepCommandInputs.Contains(e.PropertyName))
            NotifyStepCommands();
    }

    private void NotifyStepCommands()
    {
        WriteFileCommand.NotifyCanExecuteChanged();
        CommitCommand.NotifyCanExecuteChanged();
        PushCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }

    // ---- observed repository state ---------------------------------------------------------------

    /// <summary>The open repository's working directory, or null when none is open.</summary>
    [ObservableProperty]
    private string? _repositoryPath;

    [ObservableProperty]
    private string _repositoryName = string.Empty;

    /// <summary>A repository is open AND git can read it.</summary>
    [ObservableProperty]
    private bool _isRepositoryAvailable;

    /// <summary>The branch HEAD is on. Empty when HEAD is detached — which is stated, not glossed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnDefaultBranch))]
    [NotifyPropertyChangedFor(nameof(BranchSummary))]
    private string _currentBranch = string.Empty;

    /// <summary>The repository's default branch, RESOLVED (see the type remarks). Never a literal.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnDefaultBranch))]
    [NotifyPropertyChangedFor(nameof(BranchSummary))]
    private string _defaultBranch = string.Empty;

    public bool IsOnDefaultBranch =>
        CurrentBranch.Length > 0 && string.Equals(CurrentBranch, DefaultBranch, StringComparison.Ordinal);

    /// <summary>The one line the header shows: where you are and where the flow needs you to be.</summary>
    public string BranchSummary =>
        CurrentBranch.Length == 0
            ? "Mainguard cannot tell which branch you are on."
            : IsOnDefaultBranch
                ? $"On '{CurrentBranch}', which is this repository's default branch."
                : $"On '{CurrentBranch}'. This repository's default branch is '{DefaultBranch}'.";

    /// <summary><c>.mainguard/toolchain</c> as the last commit on this branch has it; null when the
    /// repository has never committed one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommittedDeclarationDisplay))]
    [NotifyPropertyChangedFor(nameof(DeclarationExistsNowhere))]
    private string? _committedDeclaration;

    /// <summary>The same path as it is ON DISK right now; null when the file does not exist.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkingTreeDeclarationDisplay))]
    [NotifyPropertyChangedFor(nameof(DeclarationExistsNowhere))]
    private string? _workingTreeDeclaration;

    public string CommittedDeclarationDisplay =>
        string.IsNullOrWhiteSpace(CommittedDeclaration)
            ? $"No {DeclarationPath} is committed on this branch."
            : CommittedDeclaration!.Trim();

    public string WorkingTreeDeclarationDisplay =>
        string.IsNullOrWhiteSpace(WorkingTreeDeclaration)
            ? $"No {DeclarationPath} exists in your working tree."
            : WorkingTreeDeclaration!.Trim();

    /// <summary>
    /// The declaration exists on NEITHER side: not in the last commit, not on disk. A distinct fact from
    /// "the two sides agree", and the two must never be reported with the same sentence.
    ///
    /// <para>They were. <c>DeclarationHasUncommittedChange</c> is derived from the git-status entry for
    /// the path, and a file that exists nowhere produces no status entry — so "absent everywhere" arrived
    /// at the commit step's last clause looking exactly like "identical to the last commit", and the page
    /// stated, in three consecutive lines, that nothing is committed, that nothing is in the working
    /// tree, and that the two already match. The first two were true. Deriving this from the CONTENT the
    /// snapshot already carries, rather than from the absence of a status entry, keeps the three lines
    /// consistent with each other by construction — it is the same emptiness test the two display strings
    /// above use, so the page cannot say "no file" and "already matches" at the same time again.</para>
    /// </summary>
    public bool DeclarationExistsNowhere =>
        string.IsNullOrWhiteSpace(CommittedDeclaration) && string.IsNullOrWhiteSpace(WorkingTreeDeclaration);

    /// <summary>Uncommitted changes to anything OTHER than the declaration — the tree the commit step
    /// refuses to touch and will not stash.</summary>
    public ObservableCollection<string> OtherChangedPaths { get; } = new();

    /// <summary>The declaration path itself differs from the last commit (so there is something to commit).</summary>
    [ObservableProperty]
    private bool _declarationHasUncommittedChange;

    /// <summary>The declaration path is in the INDEX. The write step must never cause this — it is what
    /// "writes the working tree only" is measured by.</summary>
    [ObservableProperty]
    private bool _declarationIsStaged;

    /// <summary>The id <c>.mainguard/toolchain</c> actually declares (working tree first, else the
    /// committed file). Null when nothing is declared yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeclaredToolchain))]
    private string? _declaredToolchainId;

    public bool HasDeclaredToolchain => !string.IsNullOrEmpty(DeclaredToolchainId);

    /// <summary>Why the current declaration could not be parsed (unknown id, not an id at all). Null when
    /// it parses or when there is nothing to parse.</summary>
    [ObservableProperty]
    private string? _declarationParseError;

    /// <summary>The declared toolchain was just PROVEN to run at its pinned version — the channel's
    /// probe, never a marker file.</summary>
    [ObservableProperty]
    private bool _declaredToolchainInstalled;

    /// <summary>What that probe reported. Null when nothing was probed.</summary>
    [ObservableProperty]
    private string? _declaredToolchainDetail;

    [ObservableProperty]
    private bool _hasRemote;

    /// <summary>Commits on this branch the upstream does not have. Null = no upstream configured yet (a
    /// push would set one), which is NOT the same as "nothing to push".</summary>
    [ObservableProperty]
    private int? _aheadBy;

    /// <summary>A step is running. Everything disables — with that as the stated reason.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>What just happened (or is happening). Null when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>The last step failed; <see cref="StatusMessage"/> carries the cause.</summary>
    [ObservableProperty]
    private bool _isFailed;

    // ---- what the user is declaring ---------------------------------------------------------------

    /// <summary>The curated ids this build can declare (the channel's manifest — never a hand-kept list).</summary>
    public ObservableCollection<string> AvailableToolchainIds { get; } = new();

    /// <summary>The id the write step will put in the file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DesiredContent))]
    [NotifyPropertyChangedFor(nameof(CommitMessage))]
    private string _selectedToolchainId = string.Empty;

    partial void OnSelectedToolchainIdChanged(string value)
    {
        _selectionIsTheUsers = true;
        ComputeReasons();
    }

    /// <summary>Exactly what <see cref="WriteFileAsync"/> writes — one catalogued id per line, which is
    /// the file's whole grammar.</summary>
    public string DesiredContent => SelectedToolchainId + "\n";

    /// <summary>The commit message the commit step will use, shown BEFORE the commit happens (together
    /// with <see cref="CurrentBranch"/>) so nothing about that commit is a surprise.</summary>
    public string CommitMessage =>
        $"chore(toolchain): declare {SelectedToolchainId} in {DeclarationPath}";

    // ---- the four steps ---------------------------------------------------------------------------
    //
    // Enablement is derived from the reason: CanExecute is "the reason is empty". There is deliberately
    // no way to express "disabled, no reason".

    [ObservableProperty]
    private string _writeFileDisabledReason = string.Empty;

    [ObservableProperty]
    private string _commitDisabledReason = string.Empty;

    [ObservableProperty]
    private string _pushDisabledReason = string.Empty;

    [ObservableProperty]
    private string _installDisabledReason = string.Empty;

    private bool CanWriteFile() => WriteFileDisabledReason.Length == 0;
    private bool CanCommit() => CommitDisabledReason.Length == 0;
    private bool CanPush() => PushDisabledReason.Length == 0;
    private bool CanInstall() => InstallDisabledReason.Length == 0;

    /// <summary>
    /// Step 1 — write <c>.mainguard/toolchain</c> into the WORKING TREE. That is the entire action: no
    /// <c>git add</c>, no commit. The next step's preconditions are then re-measured, so what the flow
    /// shows afterwards is the repository's real state and not this method's expectation of it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWriteFile))]
    private async Task WriteFileAsync()
    {
        if (_git is null || RepositoryPath is null)
            return;

        IsBusy = true;
        IsFailed = false;
        ComputeReasons();
        try
        {
            var full = Path.Combine(RepositoryPath, DeclarationPath.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var content = DesiredContent;
            await Task.Run(() => File.WriteAllText(full, content)).ConfigureAwait(true);

            // Nothing else happens here. Step 1 writes a file; staging belongs to step 2 and is the
            // user's own decision, taken by pressing its own button. This line used to also call
            // StageFile — while the message below told the reader nothing had been staged, so the
            // surface actively contradicted itself. That is the exact failure the button-per-step
            // shape exists to prevent: an action that quietly does more than its label says.
            StatusMessage = $"Wrote {DeclarationPath} in your working tree. Nothing has been staged or "
                + "committed — that is the next button.";
        }
        catch (Exception ex)
        {
            IsFailed = true;
            StatusMessage = $"{DeclarationPath} could not be written: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Step 2 — stage THAT ONE PATH and commit it. Never <c>git add -A</c>, never a stash, never a
    /// checkout, and — the point of splitting the flow at all — never a push. If the working tree holds
    /// anything else uncommitted, or HEAD is not on the default branch, this step refuses in words and
    /// leaves the repository exactly as it found it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        if (_git is null || RepositoryPath is null)
            return;

        var repoPath = RepositoryPath;
        var message = CommitMessage;
        IsBusy = true;
        IsFailed = false;
        ComputeReasons();
        try
        {
            await Task.Run(() =>
            {
                _git.StageFile(repoPath, DeclarationPath);
                _git.Commit(repoPath, message);
            }).ConfigureAwait(true);
            StatusMessage = $"Committed {DeclarationPath} on '{CurrentBranch}'. Nothing was pushed — "
                + "the remote still has no idea. Push is the next button.";
        }
        catch (Exception ex)
        {
            IsFailed = true;
            StatusMessage = $"The commit did not happen: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Step 3 — push. The ONLY thing in this view model that talks to a remote, and it is reached only
    /// by the user pressing it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPush))]
    private async Task PushAsync()
    {
        if (_git is null || RepositoryPath is null)
            return;

        var repoPath = RepositoryPath;
        IsBusy = true;
        IsFailed = false;
        ComputeReasons();
        try
        {
            await Task.Run(() => _git.Push(repoPath)).ConfigureAwait(true);
            StatusMessage = $"Pushed '{CurrentBranch}'.";
        }
        catch (Exception ex)
        {
            IsFailed = true;
            StatusMessage = $"The push did not complete: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Step 4 — install the DECLARED toolchain on this machine, if the probe says it is not already
    /// there. Declaring a toolchain in a repository and having it on this machine are two different
    /// facts; this button is the second one, and it changes nothing in the repository.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (_channel is null || DeclaredToolchainId is null)
            return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        IsFailed = false;
        ComputeReasons();

        // Progress<T> can post a report AFTER the operation returned; `live` stops a late "Unpacking…"
        // from overwriting the outcome (the same window ToolchainSettingsViewModel closes).
        var live = true;
        var progress = new Progress<string>(line =>
        {
            if (live)
                StatusMessage = line;
        });

        try
        {
            ToolchainStatus status;
            try
            {
                status = await _channel.InstallAsync(DeclaredToolchainId, progress, _cts.Token).ConfigureAwait(true);
            }
            finally
            {
                live = false;
            }

            DeclaredToolchainInstalled = status.IsInstalled;
            DeclaredToolchainDetail = status.Detail;
            StatusMessage = $"{status.Entry.DisplayName} {status.Entry.Version} is installed and was just run.";
        }
        catch (OperationCanceledException)
        {
            live = false;
            StatusMessage = "Cancelled. Nothing was left half-installed — you can install it again anytime.";
        }
        catch (ToolchainChannelException ex)
        {
            live = false;
            IsFailed = true;
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            live = false;
            IsFailed = true;
            StatusMessage = $"{DeclaredToolchainId} could not be installed. {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Aborts an in-flight install.</summary>
    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    // ---- preconditions ----------------------------------------------------------------------------

    /// <summary>
    /// Re-measures the repository and recomputes every step's reason. Called on activation and after
    /// EVERY step, so the flow can never present a precondition it inferred rather than observed — the
    /// failure mode where "Commit" stays lit because the view model believes it wrote a file.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_git is null)
        {
            // Design/harness instance: no services to measure with.
            ComputeReasons();
            return;
        }

        var path = RepositoryPath;
        if (string.IsNullOrWhiteSpace(path) || !SafeIsRepository(path!))
        {
            IsRepositoryAvailable = false;
            CurrentBranch = string.Empty;
            DefaultBranch = string.Empty;
            CommittedDeclaration = null;
            WorkingTreeDeclaration = null;
            OtherChangedPaths.Clear();
            DeclarationHasUncommittedChange = false;
            DeclarationIsStaged = false;
            RecomputeDeclared();
            ComputeReasons();
            return;
        }

        RepositoryName = NameOf(path);

        RepoSnapshot snapshot;
        try
        {
            snapshot = await Task.Run(() => ReadSnapshot(path!)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            IsRepositoryAvailable = false;
            IsFailed = true;
            StatusMessage = $"Mainguard could not read this repository: {ex.Message}";
            ComputeReasons();
            return;
        }

        IsRepositoryAvailable = true;
        CurrentBranch = snapshot.CurrentBranch;
        DefaultBranch = snapshot.DefaultBranch;
        CommittedDeclaration = snapshot.Committed;
        WorkingTreeDeclaration = snapshot.WorkingTree;
        DeclarationHasUncommittedChange = snapshot.DeclarationChanged;
        DeclarationIsStaged = snapshot.DeclarationStaged;
        HasRemote = snapshot.HasRemote;
        AheadBy = snapshot.AheadBy;

        OtherChangedPaths.Clear();
        foreach (var p in snapshot.OtherChangedPaths)
            OtherChangedPaths.Add(p);

        RecomputeDeclared();

        // Whether the declared toolchain is on THIS machine is a probe, not a memory (the channel runs
        // it), so it is re-established here rather than remembered across steps.
        if (_channel is not null && DeclaredToolchainId is not null
            && _channel.Manifest.TryGet(DeclaredToolchainId) is { } entry)
        {
            try
            {
                var status = await _channel.StatusAsync(entry, CancellationToken.None).ConfigureAwait(true);
                DeclaredToolchainInstalled = status.IsInstalled;
                DeclaredToolchainDetail = status.Detail;
            }
            catch (Exception ex)
            {
                DeclaredToolchainInstalled = false;
                DeclaredToolchainDetail = $"Mainguard could not check whether it is installed: {ex.Message}";
            }
        }
        else
        {
            DeclaredToolchainInstalled = false;
            DeclaredToolchainDetail = null;
        }

        // Follow the file until the user makes a choice of their own — the common case is "this repo
        // already declares python-3 and I am here to install it", and pre-selecting something else would
        // invite writing a declaration nobody asked for.
        if (!_selectionIsTheUsers && DeclaredToolchainId is not null
            && AvailableToolchainIds.Contains(DeclaredToolchainId))
        {
            SelectedToolchainId = DeclaredToolchainId;
            // The setter's change hook marks the selection as the user's; this one came from the file,
            // so put the flag back — otherwise the first refresh would freeze the selection forever.
            _selectionIsTheUsers = false;
        }

        ComputeReasons();
        NotifyStepCommands();
    }

    /// <summary>
    /// The one place a step's enablement is decided. Each reason is the FIRST thing that is wrong, in
    /// the order the user would hit it, so the sentence is always about the next real obstacle.
    /// </summary>
    private void ComputeReasons()
    {
        WriteFileDisabledReason =
            NoRepositoryReason()
            ?? BusyReason()
            ?? (string.IsNullOrWhiteSpace(SelectedToolchainId)
                ? "Choose a toolchain first — there is nothing to declare until one is selected."
                : null)
            ?? (string.Equals(WorkingTreeDeclaration, DesiredContent, StringComparison.Ordinal)
                ? $"{DeclarationPath} in your working tree already declares {SelectedToolchainId}, so there is nothing to write."
                : null)
            ?? string.Empty;

        CommitDisabledReason =
            NoRepositoryReason()
            ?? BusyReason()
            ?? (CurrentBranch.Length == 0
                ? "Mainguard cannot tell which branch you are on (HEAD is detached). Check out a branch "
                  + "yourself, then come back — Mainguard will not move HEAD for you."
                : null)
            ?? (!IsOnDefaultBranch
                ? $"You are on '{CurrentBranch}'; this repository's default branch is '{DefaultBranch}', "
                  + $"and the toolchain declaration is only read from the default branch. Check out "
                  + $"'{DefaultBranch}' yourself — Mainguard will not switch branches for you."
                : null)
            // "It does not exist" comes BEFORE "your tree is dirty": when there is no file at all, the
            // next thing this person has to do is press the first button, and saying anything else sends
            // them to clean a working tree that was never in the way.
            ?? (DeclarationExistsNowhere
                ? $"There is nothing to commit — {DeclarationPath} does not exist yet, neither in the "
                  + $"last commit on '{CurrentBranch}' nor in your working tree. Write it with the first "
                  + "button above, then this step will commit exactly that file."
                : null)
            ?? (OtherChangedPaths.Count > 0
                ? $"Your working tree has {OtherChangedPaths.Count} other uncommitted "
                  + $"change{(OtherChangedPaths.Count == 1 ? "" : "s")} ({OtherChangedPathsSummary()}). "
                  + "Commit them or set them aside yourself — Mainguard will not stash or discard your work."
                : null)
            // Reached only when the file DOES exist on at least one side, so this really is a match.
            ?? (!DeclarationHasUncommittedChange
                ? $"There is nothing to commit — {DeclarationPath} already matches the last commit on "
                  + $"'{CurrentBranch}'."
                : null)
            ?? string.Empty;

        PushDisabledReason =
            NoRepositoryReason()
            ?? BusyReason()
            ?? (CurrentBranch.Length == 0
                ? "Mainguard cannot tell which branch you are on (HEAD is detached), so there is no branch to push."
                : null)
            ?? (!HasRemote
                ? "This repository has no remote configured, so there is nowhere to push. Add a remote first."
                : null)
            ?? (AheadBy == 0
                ? $"Nothing to push — '{CurrentBranch}' has no commits the remote does not already have."
                : null)
            ?? string.Empty;

        InstallDisabledReason =
            BusyReason()
            ?? (_channel is null && _git is not null
                ? "The toolchain catalog is not available in this window, so Mainguard cannot install anything from here."
                : null)
            ?? (DeclarationParseError is not null
                ? $"{DeclarationPath} cannot be read as a toolchain declaration: {DeclarationParseError}"
                : null)
            ?? (!HasDeclaredToolchain
                ? $"{DeclarationPath} does not declare a toolchain yet — write it with the first button above."
                : null)
            ?? (DeclaredToolchainInstalled
                ? $"{DeclaredToolchainId} is already installed on this machine and was just run to prove it."
                : null)
            ?? string.Empty;
    }

    private string? NoRepositoryReason() =>
        RepositoryPath is null && _git is not null
            ? "No repository is open. Open one in Mainguard, then come back to this page."
            : !IsRepositoryAvailable && _git is not null
                ? $"'{RepositoryPath}' is not a git repository Mainguard can read, so there is nothing to declare in it."
                : null;

    private string? BusyReason() =>
        IsBusy ? "A step is running — this will be available again the moment it finishes." : null;

    private string OtherChangedPathsSummary()
    {
        const int shown = 3;
        var head = string.Join(", ", OtherChangedPaths.Take(shown));
        var rest = OtherChangedPaths.Count - shown;
        return rest > 0
            ? $"{head} and {rest.ToString(CultureInfo.InvariantCulture)} more"
            : head;
    }

    private void RecomputeDeclared()
    {
        DeclarationParseError = null;
        DeclaredToolchainId = null;

        var text = WorkingTreeDeclaration ?? CommittedDeclaration;
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            var parsed = ToolchainDeclarationResolver.Parse(text!, RepositoryName);
            DeclaredToolchainId = parsed.Ids.IsDefaultOrEmpty ? null : parsed.Ids[0];
        }
        catch (Exception ex)
        {
            DeclarationParseError = ex.Message;
        }
    }

    private bool SafeIsRepository(string path)
    {
        try
        {
            return _git!.IsGitRepository(path);
        }
        catch
        {
            return false;
        }
    }

    private static string NameOf(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>One measurement of the repository, taken under a single native handle.</summary>
    private sealed record RepoSnapshot(
        string CurrentBranch,
        string DefaultBranch,
        string? Committed,
        string? WorkingTree,
        IReadOnlyList<string> OtherChangedPaths,
        bool DeclarationChanged,
        bool DeclarationStaged,
        bool HasRemote,
        int? AheadBy);

    /// <summary>
    /// Reads everything the four steps' preconditions depend on. All LibGit2Sharp access goes through
    /// <see cref="IGitService.ExecuteWithRepo{T}"/> — one open, one dispose, no ad-hoc handle.
    /// </summary>
    private RepoSnapshot ReadSnapshot(string path)
    {
        // Spawns git (symbolic-ref), so it happens OUTSIDE the native handle rather than while one is held.
        var fallbackDefault = RepoToolchainConfig.DefaultBranch(path);

        var onDisk = ReadWorkingTreeDeclaration(path);

        return _git!.ExecuteWithRepo(path, repo =>
        {
            var currentBranch = repo.Info.IsHeadDetached ? string.Empty : repo.Head.FriendlyName;

            string? committed = null;
            if (repo.Head.Tip?[DeclarationPath]?.Target is Blob blob)
                committed = blob.GetContentText();

            // RecurseUntrackedDirs matters: without it a brand-new, still-untracked `.mainguard/` is
            // reported as the DIRECTORY, and the flow would neither see its own file nor be able to tell
            // it apart from someone else's untracked work.
            var status = repo.RetrieveStatus(new StatusOptions
            {
                IncludeUntracked = true,
                RecurseUntrackedDirs = true,
            });

            var others = new List<string>();
            StatusEntry? declEntry = null;
            foreach (var entry in status)
            {
                if (entry.State == FileStatus.Ignored || entry.State == FileStatus.Unaltered)
                    continue;
                if (string.Equals(entry.FilePath, DeclarationPath, StringComparison.Ordinal))
                {
                    declEntry = entry;
                    continue;
                }

                others.Add(entry.FilePath);
            }

            others.Sort(StringComparer.Ordinal);

            const FileStatus indexBits =
                FileStatus.NewInIndex | FileStatus.ModifiedInIndex | FileStatus.DeletedFromIndex
                | FileStatus.RenamedInIndex | FileStatus.TypeChangeInIndex;

            var hasRemote = repo.Network.Remotes.Any();

            return new RepoSnapshot(
                currentBranch,
                ResolveDefaultBranch(repo) ?? fallbackDefault,
                committed,
                onDisk,
                others,
                DeclarationChanged: declEntry is not null,
                DeclarationStaged: declEntry is not null && (declEntry.State & indexBits) != 0,
                HasRemote: hasRemote,
                // An unborn HEAD has no tracking details at all, so ask only once there is a commit.
                AheadBy: repo.Info.IsHeadDetached || repo.Head.Tip is null
                    ? null
                    : repo.Head.TrackingDetails?.AheadBy);
        });
    }

    /// <summary>
    /// The default branch as the CLONE records it: <c>refs/remotes/&lt;remote&gt;/HEAD</c>, which git
    /// writes from the remote's own HEAD. Null when no remote publishes one — the caller then falls back
    /// to <see cref="RepoToolchainConfig.DefaultBranch"/>. <c>origin</c> is preferred when present, but
    /// only by ordering; no remote name and no branch name is ever assumed.
    /// </summary>
    private static string? ResolveDefaultBranch(Repository repo)
    {
        foreach (var remote in repo.Network.Remotes
                     .OrderByDescending(r => string.Equals(r.Name, "origin", StringComparison.Ordinal)))
        {
            var prefix = $"refs/remotes/{remote.Name}/";
            if (repo.Refs[prefix + "HEAD"] is SymbolicReference symbolic
                && symbolic.TargetIdentifier is { } target
                && target.StartsWith(prefix, StringComparison.Ordinal))
            {
                var name = target[prefix.Length..];
                if (name.Length > 0 && !string.Equals(name, "HEAD", StringComparison.Ordinal))
                    return name;
            }
        }

        return null;
    }

    private static string? ReadWorkingTreeDeclaration(string repoPath)
    {
        try
        {
            var full = Path.Combine(repoPath, DeclarationPath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(full) ? File.ReadAllText(full) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
