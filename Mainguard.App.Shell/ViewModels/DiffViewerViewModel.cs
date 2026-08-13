using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.ViewModels;

public partial class DiffViewerViewModel : ViewModelBase
{
    private readonly IGitService _gitService;
    private readonly string _repoPath;
    private readonly System.Action? _onStagingChanged;
    private readonly ISettingsService? _settings;

    // Partial-staging state (T-06). The parsed patch is the source of truth the builder
    // subsets from; direction follows the file's staged state (workdir<->index vs index<->HEAD).
    private GitFileStatus? _currentFile;
    private Mainguard.Git.Models.FilePatch? _currentPatch;

    [ObservableProperty]
    private ObservableCollection<GitDiffLine> _diffLines = new();

    [ObservableProperty]
    private bool _isSideBySideView;
    partial void OnIsSideBySideViewChanged(bool value)
    {
        OnPropertyChanged(nameof(DiffViewModeText));
        UpdateVisibility();
    }

    [ObservableProperty]
    private bool _isEditMode;
    partial void OnIsEditModeChanged(bool value)
    {
        UpdateVisibility();
    }

    [ObservableProperty]
    private bool _showUnified = true;

    [ObservableProperty]
    private bool _showSideBySide = false;

    // True for a plain textual diff (i.e. not an image/binary/LFS pointer) — drives whether
    // text-only affordances like the "Code Editor" toggle are shown at all (#81).
    [ObservableProperty]
    private bool _isTextDiff = true;

    private void UpdateVisibility()
    {
        bool textDiff = !IsImageDiff && !IsBinaryDiff && !IsLfsDiff;
        IsTextDiff = textDiff;
        ShowUnified = !IsSideBySideView && !IsEditMode && textDiff;
        ShowSideBySide = IsSideBySideView && !IsEditMode && textDiff;
    }

    public string DiffViewModeText => IsSideBySideView ? "Show Unified Diff" : "Show Split Diff";

    [RelayCommand]
    private void ToggleDiffView()
    {
        IsSideBySideView = !IsSideBySideView;
    }

    [ObservableProperty]
    private string _rawContent = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    private string _filePath = string.Empty;

    public bool HasFile => !string.IsNullOrEmpty(FilePath);

    [ObservableProperty]
    private bool _hasConflicts;

    [ObservableProperty]
    private int _conflictCount;

    public System.Collections.Generic.HashSet<int> AddedLines { get; } = new();
    public System.Collections.Generic.HashSet<int> ModifiedLines { get; } = new();

    [RelayCommand]
    private async System.Threading.Tasks.Task Open3WayResolverAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            // libgit2 reports conflict paths with forward slashes; match on that form.
            var relPath = FilePath.Replace('\\', '/');
            var conflict = System.Linq.Enumerable.FirstOrDefault(
                _gitService.GetConflicts(_repoPath), c => c.Path == relPath);

            var dialog = new Views.ConflictResolverWindow();
            var vm = new ConflictResolverWindowViewModel(
                _gitService, new MergeDiffService(), _repoPath, relPath,
                conflict?.HasOurs ?? true, conflict?.HasTheirs ?? true)
            { Window = dialog };
            dialog.DataContext = vm;

            var result = await dialog.ShowDialog<bool>(desktop.MainWindow);
            if (result)
            {
                // ResolveConflict already staged the file; refresh the UI to show the new state.
                var status = new GitFileStatus { FilePath = FilePath, State = LibGit2Sharp.FileStatus.ModifiedInIndex };
                UpdateDiff(status);
            }
        }
    }

    private void CheckForConflicts()
    {
        if (string.IsNullOrEmpty(RawContent))
        {
            HasConflicts = false;
            ConflictCount = 0;
            return;
        }

        var regex = new System.Text.RegularExpressions.Regex(@"<<<<<<<.*?\n");
        var matches = regex.Matches(RawContent);
        ConflictCount = matches.Count;
        HasConflicts = ConflictCount > 0;

        // Auto-switch to edit mode if there are conflicts to resolve
        if (HasConflicts && !IsEditMode)
        {
            IsEditMode = true;
        }
    }

    // Set when the last SaveFile attempt failed; cleared by a successful save. Edit mode
    // auto-enables whenever conflict markers are present (CheckForConflicts), so the text this
    // command writes is frequently a merge resolution — swallowing the write meant the resolution
    // was silently lost while the markers stayed on disk and the UI looked saved.
    [ObservableProperty]
    private string? _saveError;

    /// <summary>True after a failed save — drives the error banner over the editor.</summary>
    public bool HasSaveError => !string.IsNullOrEmpty(SaveError);

    partial void OnSaveErrorChanged(string? value) => OnPropertyChanged(nameof(HasSaveError));

    /// <summary>True only after a save that actually reached disk — lets tests and callers tell
    /// "saved" from "looked saved".</summary>
    public bool LastSaveSucceeded { get; private set; }

    [RelayCommand]
    private void SaveFile()
    {
        LastSaveSucceeded = false;

        if (string.IsNullOrEmpty(FilePath))
        {
            SaveError = "No file is open, so there is nothing to save.";
            return;
        }

        var fullPath = System.IO.Path.Combine(_repoPath, FilePath);
        try
        {
            System.IO.File.WriteAllText(fullPath, RawContent);
        }
        catch (System.Exception ex)
        {
            // Never report success here. The buffer may be the only copy of a conflict
            // resolution; the user has to know it is still unwritten.
            SaveError = $"Could not save '{FilePath}': {ex.Message}";
            return;
        }

        SaveError = null;
        LastSaveSucceeded = true;
        // The markers the auto-edit-mode switch keyed off are only really gone once the write
        // landed, so re-derive the conflict state from what we just persisted.
        CheckForConflicts();
    }

    [ObservableProperty]
    private ObservableCollection<SideBySideDiffRow> _sideBySideLines = new();

    // Structured hunks for partial staging (T-06). Rendered in the unified view.
    [ObservableProperty]
    private ObservableCollection<DiffHunkRowViewModel> _hunks = new();

    [ObservableProperty]
    private bool _hasSelectedLines;

    // True when viewing the staged (index<->HEAD) diff → actions unstage rather than stage/discard.
    [ObservableProperty]
    private bool _isStagedView;

    [ObservableProperty]
    private string? _partialStagingError;

    [ObservableProperty]
    private bool _isBusy;

    public DiffViewerViewModel(IGitService gitService, string repoPath, System.Action? onStagingChanged = null,
        ISettingsService? settings = null)
    {
        _gitService = gitService;
        _repoPath = repoPath;
        _onStagingChanged = onStagingChanged;
        _settings = settings;
        _syntaxHighlightDiffs = settings?.Current.SyntaxHighlightDiffs ?? true;
    }

    // ---- Diff-quality toggles (T-13) ---------------------------------------

    // Ignore-whitespace mode: re-runs the diff via `git diff -w`; whitespace-only changes vanish.
    // Partial staging is genuinely unavailable in this mode (the -w offsets don't map back to a
    // patch that `git apply` would accept), so the stage/discard/unstage actions are hidden.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PartialStagingAvailable))]
    private bool _ignoreWhitespace;

    partial void OnIgnoreWhitespaceChanged(bool value)
    {
        if (_currentFile != null) UpdateDiff(_currentFile);
    }

    /// <summary>False in ignore-whitespace mode — the view hides every stage/discard action.</summary>
    public bool PartialStagingAvailable => !IgnoreWhitespace;

    [RelayCommand]
    private void ToggleIgnoreWhitespace() => IgnoreWhitespace = !IgnoreWhitespace;

    // Syntax-highlight preference (persisted). When off, the editor renders plain text.
    [ObservableProperty]
    private bool _syntaxHighlightDiffs = true;

    partial void OnSyntaxHighlightDiffsChanged(bool value)
        => _settings?.Update(p => p.SyntaxHighlightDiffs = value);

    [RelayCommand]
    private void ToggleSyntaxHighlighting() => SyntaxHighlightDiffs = !SyntaxHighlightDiffs;

    // ---- Image / binary diff (T-13) ----------------------------------------

    // True when the change is a recognized image blob pair -> the image-diff control renders,
    // instead of the text hunks. For other binaries, IsBinaryDiff + BinarySummary drive a summary.
    [ObservableProperty]
    private bool _isImageDiff;
    partial void OnIsImageDiffChanged(bool value) => UpdateVisibility();

    [ObservableProperty]
    private bool _isBinaryDiff;
    partial void OnIsBinaryDiffChanged(bool value) => UpdateVisibility();

    [ObservableProperty]
    private string _binarySummary = string.Empty;

    // LFS pointer diff (T-17): when the diffed content is a Git LFS pointer (the real object lives
    // outside the tree), the raw pointer text is useless — render a friendly "LFS object (size)".
    [ObservableProperty]
    private bool _isLfsDiff;
    partial void OnIsLfsDiffChanged(bool value) => UpdateVisibility();

    [ObservableProperty]
    private string _lfsSummary = string.Empty;

    public ImageDiffViewModel ImageDiff { get; } = new();

    /// <summary>Raised when the user asks for the current file's history (T-12); the host opens the
    /// dedicated file-history dialog. Kept as an event so window-opening stays in one place.</summary>
    public event System.Action<string>? FileHistoryRequested;

    /// <summary>Raised when the user asks to blame the current file (T-33); the host opens the
    /// dedicated blame dialog. Mirrors <see cref="FileHistoryRequested"/> so window-opening stays in
    /// one place.</summary>
    public event System.Action<string>? BlameRequested;

    [RelayCommand]
    private void ShowFileHistory()
    {
        if (!string.IsNullOrEmpty(FilePath)) FileHistoryRequested?.Invoke(FilePath);
    }

    [RelayCommand]
    private void ShowBlame()
    {
        if (!string.IsNullOrEmpty(FilePath)) BlameRequested?.Invoke(FilePath);
    }

    public void UpdateDiff(GitFileStatus? file)
    {
        if (file == null)
        {
            DiffLines.Clear();
            SideBySideLines.Clear();
            Hunks.Clear();
            _currentFile = null;
            _currentPatch = null;
            HasSelectedLines = false;
            PartialStagingError = null;
            RawContent = string.Empty;
            FilePath = string.Empty;
            ClearBinaryState();
            return;
        }

        _currentFile = file;
        IsStagedView = file.IsStaged;
        PartialStagingError = null;
        ClearBinaryState();
        FilePath = file.FilePath;
        var fullPath = System.IO.Path.Combine(_repoPath, FilePath);
        if (System.IO.File.Exists(fullPath))
        {
            RawContent = System.IO.File.ReadAllText(fullPath);
        }
        else
        {
            RawContent = string.Empty;
        }

        // CheckForConflicts already flips IsEditMode on when markers are present.
        CheckForConflicts();

        var rawDiff = _gitService.GetFileDiff(_repoPath, file.FilePath, file.IsStaged, IgnoreWhitespace);

        // LFS pointer diff (T-17): if either side of the change is an LFS pointer, the raw pointer
        // text is not what the user wants to see — show "LFS object (size)" and skip text rendering.
        if (DetectLfsDiff(rawDiff))
        {
            DiffLines = new ObservableCollection<GitDiffLine>();
            SideBySideLines = new ObservableCollection<SideBySideDiffRow>();
            Hunks = new ObservableCollection<DiffHunkRowViewModel>();
            _currentPatch = null;
            HasSelectedLines = false;
            return;
        }

        // Binary / image diff (T-13): if git reports a binary change (or the working file scans as
        // binary), don't try to render textual hunks. Recognized images route to the image-diff
        // control; other binaries show a size summary.
        if (DetectBinaryDiff(file, rawDiff))
        {
            DiffLines = new ObservableCollection<GitDiffLine>();
            SideBySideLines = new ObservableCollection<SideBySideDiffRow>();
            Hunks = new ObservableCollection<DiffHunkRowViewModel>();
            _currentPatch = null;
            HasSelectedLines = false;
            return;
        }

        var unifiedLines = new ObservableCollection<GitDiffLine>();
        var sbsLines = new ObservableCollection<SideBySideDiffRow>();

        AddedLines.Clear();
        ModifiedLines.Clear();

        if (!string.IsNullOrEmpty(rawDiff))
        {
            var lines = rawDiff.Split('\n');
            int i = 0;

            int currentNewLine = 0;

            while (i < lines.Length)
            {
                var line = lines[i];
                if (string.IsNullOrEmpty(line)) { i++; continue; }

                var diffLine = new GitDiffLine { LineType = line[0], Content = line };
                unifiedLines.Add(diffLine);

                char type = line[0];
                if (type == '@')
                {
                    // Parse @@ -old,old_count +new,new_count @@
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"\+([0-9]+)");
                    if (match.Success)
                    {
                        currentNewLine = int.Parse(match.Groups[1].Value);
                    }
                    sbsLines.Add(new SideBySideDiffRow { LeftLine = diffLine, RightLine = diffLine });
                    i++;
                }
                else if (type == ' ')
                {
                    currentNewLine++;
                    sbsLines.Add(new SideBySideDiffRow { LeftLine = diffLine, RightLine = diffLine });
                    i++;
                }
                else if (type == '-' || type == '+')
                {
                    var deletions = new System.Collections.Generic.List<GitDiffLine>();
                    var additions = new System.Collections.Generic.List<GitDiffLine>();

                    while (i < lines.Length && !string.IsNullOrEmpty(lines[i]) && (lines[i][0] == '-' || lines[i][0] == '+'))
                    {
                        var chunkLine = new GitDiffLine { LineType = lines[i][0], Content = lines[i] };
                        unifiedLines.Add(chunkLine);

                        if (lines[i][0] == '-') deletions.Add(chunkLine);
                        else additions.Add(chunkLine);

                        i++;
                    }

                    int maxRows = System.Math.Max(deletions.Count, additions.Count);
                    var emptyLine = new GitDiffLine { LineType = ' ', Content = "" };

                    bool isModification = deletions.Count > 0 && additions.Count > 0;

                    for (int j = 0; j < additions.Count; j++)
                    {
                        if (isModification) ModifiedLines.Add(currentNewLine);
                        else AddedLines.Add(currentNewLine);
                        currentNewLine++;
                    }

                    for (int j = 0; j < maxRows; j++)
                    {
                        sbsLines.Add(new SideBySideDiffRow
                        {
                            LeftLine = j < deletions.Count ? deletions[j] : emptyLine,
                            RightLine = j < additions.Count ? additions[j] : emptyLine
                        });
                    }
                }
                else
                {
                    i++;
                }
            }
        }

        DiffLines = unifiedLines;
        SideBySideLines = sbsLines;

        BuildHunks(rawDiff, file.IsStaged);
    }

    // ---- Binary / image detection (T-13) -----------------------------------

    private void ClearBinaryState()
    {
        IsImageDiff = false;
        IsBinaryDiff = false;
        BinarySummary = string.Empty;
        IsLfsDiff = false;
        LfsSummary = string.Empty;
        ImageDiff.Clear();
    }

    // ---- LFS pointer detection (T-17) --------------------------------------

    // Detects whether the change is a Git LFS pointer (working-tree pointer, or a diff whose new/old
    // side is a pointer) and, if so, populates the friendly summary and returns true so text hunks
    // are skipped. Pointer parsing is the pure LfsPointer helper.
    private bool DetectLfsDiff(string rawDiff)
    {
        string? pointer = null;
        if (Mainguard.Git.Services.LfsPointer.IsPointer(RawContent))
        {
            pointer = RawContent;
        }
        else
        {
            var added = ExtractDiffSide(rawDiff, '+');
            if (Mainguard.Git.Services.LfsPointer.IsPointer(added)) pointer = added;
            else
            {
                var removed = ExtractDiffSide(rawDiff, '-');
                if (Mainguard.Git.Services.LfsPointer.IsPointer(removed)) pointer = removed;
            }
        }

        if (pointer == null) return false;

        var size = Mainguard.Git.Services.LfsPointer.ParseSize(pointer);
        LfsSummary = size is { } bytes ? $"LFS object ({FormatBytes(bytes)})" : "LFS object";
        IsLfsDiff = true;
        return true;
    }

    // Reconstructs one side of a unified diff (added '+' or removed '-' body), skipping the
    // +++/--- file headers, so it can be tested for the LFS pointer header.
    private static string ExtractDiffSide(string rawDiff, char side)
    {
        if (string.IsNullOrEmpty(rawDiff)) return string.Empty;
        var header = side == '+' ? "+++" : "---";
        var sb = new System.Text.StringBuilder();
        foreach (var line in rawDiff.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0 || line[0] != side) continue;
            if (line.StartsWith(header, System.StringComparison.Ordinal)) continue;
            sb.Append(line, 1, line.Length - 1).Append('\n');
        }
        return sb.ToString();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0
            ? $"{bytes} B"
            : $"{value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} {units[unit]}";
    }

    // Decides whether the change is binary; if so populates the image-diff VM (recognized images)
    // or the size summary (other binaries) and returns true so the caller skips text rendering.
    private bool DetectBinaryDiff(GitFileStatus file, string rawDiff)
    {
        var fullPath = System.IO.Path.Combine(_repoPath, file.FilePath);
        long newSize = -1;
        bool onDiskBinary = false;
        try
        {
            if (System.IO.File.Exists(fullPath))
            {
                newSize = new System.IO.FileInfo(fullPath).Length;
                using var fs = System.IO.File.OpenRead(fullPath);
                var buf = new byte[System.Math.Min(8000, newSize < 0 ? 8000 : newSize)];
                int read = fs.Read(buf, 0, buf.Length);
                onDiskBinary = ImageDiffDetection.LooksBinary(new System.ReadOnlySpan<byte>(buf, 0, read));
            }
        }
        catch { /* unreadable working file: fall back to the diff-body marker */ }

        bool isBinary = ImageDiffDetection.DiffIndicatesBinary(rawDiff) || onDiskBinary;
        if (!isBinary) return false;

        // libgit2 reports paths with forward slashes; blob lookups expect that form.
        var gitPath = file.FilePath.Replace('\\', '/');
        byte[]? oldBytes = TryHeadBlob(gitPath);
        long oldSize = oldBytes?.LongLength ?? -1;

        if (ImageDiffDetection.IsImageCandidate(file.FilePath, isBinary))
        {
            byte[]? newBytes = null;
            try { if (System.IO.File.Exists(fullPath)) newBytes = System.IO.File.ReadAllBytes(fullPath); }
            catch { }

            ImageDiff.SetImages(oldBytes, newBytes);
            ImageDiff.OldSize = oldSize < 0 ? 0 : oldSize;
            ImageDiff.NewSize = newSize < 0 ? (newBytes?.LongLength ?? 0) : newSize;
            IsImageDiff = true;
        }
        else
        {
            BinarySummary = ImageDiffDetection.FormatBinarySummary(
                oldSize < 0 ? 0 : oldSize, newSize < 0 ? 0 : newSize);
            IsBinaryDiff = true;
        }
        return true;
    }

    private byte[]? TryHeadBlob(string gitPath)
    {
        try { return _gitService.GetBlobBytesAtCommit(_repoPath, "HEAD", gitPath); }
        catch { return null; } // newly added file, or no HEAD yet
    }

    // ---- Partial staging (T-06) --------------------------------------------

    private void BuildHunks(string rawDiff, bool isStaged)
    {
        var hunks = new ObservableCollection<DiffHunkRowViewModel>();
        _currentPatch = null;
        HasSelectedLines = false;

        if (!string.IsNullOrEmpty(rawDiff))
        {
            // Exactly one FilePatch per diff-viewer (a single file is shown at a time).
            var file = System.Linq.Enumerable.FirstOrDefault(Mainguard.Git.Services.PatchParser.Parse(rawDiff));
            if (file != null)
            {
                _currentPatch = file;
                for (int h = 0; h < file.Hunks.Count; h++)
                {
                    var hunk = file.Hunks[h];
                    var intra = ComputeHunkIntraLine(hunk);
                    var row = new DiffHunkRowViewModel
                    {
                        HunkIndex = h,
                        HeaderText = HunkHeaderText(hunk),
                        IsStaged = isStaged
                    };
                    for (int i = 0; i < hunk.Lines.Count; i++)
                    {
                        var line = hunk.Lines[i];
                        // Spans are text-relative; shift by 1 for the leading +/-/space prefix in DisplayText.
                        row.Lines.Add(new DiffLineRowViewModel
                        {
                            IndexInHunk = i,
                            Kind = line.Kind,
                            DisplayText = PrefixOf(line.Kind) + line.Text,
                            HighlightSpans = Shift(intra.GetValueOrDefault(i), 1),
                            TrailingWhitespaceSpan = ShiftOne(WhitespaceMarkers.TrailingWhitespace(line.Text), 1),
                            OnSelectionChanged = RecomputeSelection
                        });
                    }
                    FillSideRows(row, hunk, intra);
                    hunks.Add(row);
                }
            }
        }

        Hunks = hunks;
    }

    // Pairs a hunk's deletes/adds into old|new rows (deletes left, adds right, filler where
    // one side is shorter) for the block-level side-by-side view. Carries the intra-line word
    // spans (keyed by line index in the hunk) onto each GitDiffLine so changed runs render darker.
    private static void FillSideRows(DiffHunkRowViewModel row, Mainguard.Git.Models.DiffHunk hunk,
        System.Collections.Generic.IReadOnlyDictionary<int, System.Collections.Generic.List<(int, int)>> intra)
    {
        var empty = new GitDiffLine { LineType = ' ', Content = "" };
        var dels = new System.Collections.Generic.List<(GitDiffLine Line, DiffLineRowViewModel Row)>();
        var adds = new System.Collections.Generic.List<(GitDiffLine Line, DiffLineRowViewModel Row)>();

        void Flush()
        {
            int max = System.Math.Max(dels.Count, adds.Count);
            for (int j = 0; j < max; j++)
            {
                row.SideRows.Add(new SideBySideLineRowViewModel
                {
                    LeftLine = j < dels.Count ? dels[j].Line : empty,
                    RightLine = j < adds.Count ? adds[j].Line : empty,
                    LeftLineRow = j < dels.Count ? dels[j].Row : null,
                    RightLineRow = j < adds.Count ? adds[j].Row : null
                });
            }
            dels.Clear();
            adds.Clear();
        }

        GitDiffLine Annotate(GitDiffLine gl, int indexInHunk, string text)
        {
            gl.HighlightSpans = Shift(intra.GetValueOrDefault(indexInHunk), 1);
            gl.TrailingWhitespaceSpan = ShiftOne(WhitespaceMarkers.TrailingWhitespace(text), 1);
            return gl;
        }

        for (int i = 0; i < hunk.Lines.Count; i++)
        {
            var line = hunk.Lines[i];
            // row.Lines was already built (same index i) by the caller before FillSideRows runs,
            // so each side-by-side slot can carry a direct reference to its unified DiffLineRowViewModel.
            var lineRow = row.Lines[i];
            switch (line.Kind)
            {
                case Mainguard.Git.Models.DiffLineKind.Context:
                    Flush();
                    var ctx = Annotate(new GitDiffLine { LineType = ' ', Content = " " + line.Text }, i, line.Text);
                    row.SideRows.Add(new SideBySideLineRowViewModel { LeftLine = ctx, RightLine = ctx });
                    break;
                case Mainguard.Git.Models.DiffLineKind.Delete:
                    dels.Add((Annotate(new GitDiffLine { LineType = '-', Content = "-" + line.Text }, i, line.Text), lineRow));
                    break;
                case Mainguard.Git.Models.DiffLineKind.Add:
                    adds.Add((Annotate(new GitDiffLine { LineType = '+', Content = "+" + line.Text }, i, line.Text), lineRow));
                    break;
            }
        }
        Flush();
    }

    // Pairs each contiguous delete-run with the following add-run in a hunk and computes the
    // word-level changed spans per line (text-relative, no prefix), keyed by the line's index in
    // the hunk. Pure — delegates to the Core IntraLineDiff engine.
    private static System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<(int, int)>>
        ComputeHunkIntraLine(Mainguard.Git.Models.DiffHunk hunk)
    {
        var map = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<(int, int)>>();
        var delIdx = new System.Collections.Generic.List<int>();
        var addIdx = new System.Collections.Generic.List<int>();

        void PairBlock()
        {
            int pairs = System.Math.Min(delIdx.Count, addIdx.Count);
            for (int k = 0; k < pairs; k++)
            {
                // ComputeEmphasis (not Compute): a pair that shares nothing gets NO word-level
                // emphasis — the line tint already reads "replaced", and whole-line emphasis is noise.
                var (oldSpans, newSpans) = Mainguard.Git.Services.IntraLineDiff.ComputeEmphasis(
                    hunk.Lines[delIdx[k]].Text, hunk.Lines[addIdx[k]].Text);
                if (oldSpans.Count > 0) map[delIdx[k]] = new System.Collections.Generic.List<(int, int)>(oldSpans);
                if (newSpans.Count > 0) map[addIdx[k]] = new System.Collections.Generic.List<(int, int)>(newSpans);
            }
            delIdx.Clear();
            addIdx.Clear();
        }

        for (int i = 0; i < hunk.Lines.Count; i++)
        {
            switch (hunk.Lines[i].Kind)
            {
                case Mainguard.Git.Models.DiffLineKind.Delete: delIdx.Add(i); break;
                case Mainguard.Git.Models.DiffLineKind.Add: addIdx.Add(i); break;
                default: PairBlock(); break; // context ends the change block
            }
        }
        PairBlock();
        return map;
    }

    // Offsets a span list by <paramref name="delta"/> (to account for the +/-/space prefix in the
    // rendered text). Returns a fresh list; null/empty input yields an empty list.
    private static System.Collections.Generic.List<(int Start, int Length)> Shift(
        System.Collections.Generic.List<(int, int)>? spans, int delta)
    {
        var result = new System.Collections.Generic.List<(int, int)>();
        if (spans == null) return result;
        foreach (var (start, length) in spans) result.Add((start + delta, length));
        return result;
    }

    private static (int Start, int Length)? ShiftOne((int Start, int Length)? span, int delta)
        => span == null ? null : (span.Value.Start + delta, span.Value.Length);

    private static string HunkHeaderText(Mainguard.Git.Models.DiffHunk h)
    {
        var oldSpan = h.OldCountOmitted ? $"{h.OldStart}" : $"{h.OldStart},{h.OldCount}";
        var newSpan = h.NewCountOmitted ? $"{h.NewStart}" : $"{h.NewStart},{h.NewCount}";
        return $"@@ -{oldSpan} +{newSpan} @@{h.SectionHeading}";
    }

    private static char PrefixOf(Mainguard.Git.Models.DiffLineKind kind) => kind switch
    {
        Mainguard.Git.Models.DiffLineKind.Add => '+',
        Mainguard.Git.Models.DiffLineKind.Delete => '-',
        _ => ' '
    };

    private void RecomputeSelection()
        => HasSelectedLines = System.Linq.Enumerable.Any(Hunks, h => System.Linq.Enumerable.Any(h.Lines, l => l.IsSelected));

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var hunk in Hunks)
            foreach (var line in hunk.Lines)
                line.IsSelected = false;
        HasSelectedLines = false;
    }

    [RelayCommand]
    private System.Threading.Tasks.Task StageHunk(int hunkIndex)
        => ApplyPartialAsync(() => _gitService.StageHunk(_repoPath, BuildHunkPatch(hunkIndex)), confirmDiscard: false);

    [RelayCommand]
    private System.Threading.Tasks.Task UnstageHunk(int hunkIndex)
        => ApplyPartialAsync(() => _gitService.UnstageHunk(_repoPath, BuildHunkPatch(hunkIndex)), confirmDiscard: false);

    [RelayCommand]
    private System.Threading.Tasks.Task DiscardHunk(int hunkIndex)
        => ApplyPartialAsync(() => _gitService.DiscardHunk(_repoPath, BuildHunkPatch(hunkIndex)),
            confirmDiscard: true, discardWhat: "this hunk");

    [RelayCommand]
    private System.Threading.Tasks.Task StageSelectedLines()
        => ApplyPartialAsync(() => _gitService.StageHunk(_repoPath, BuildSelectedLinesPatch()), confirmDiscard: false);

    [RelayCommand]
    private System.Threading.Tasks.Task UnstageSelectedLines()
        => ApplyPartialAsync(() => _gitService.UnstageHunk(_repoPath, BuildSelectedLinesPatch()), confirmDiscard: false);

    [RelayCommand]
    private System.Threading.Tasks.Task DiscardSelectedLines()
        => ApplyPartialAsync(() => _gitService.DiscardHunk(_repoPath, BuildSelectedLinesPatch()),
            confirmDiscard: true, discardWhat: "the selected lines");

    private string BuildHunkPatch(int hunkIndex)
        => _currentPatch == null ? "" : Mainguard.Git.Services.PatchBuilder.BuildHunkPatch(_currentPatch, new[] { hunkIndex });

    // Combines each hunk's selected-line subset under a single file header so the whole
    // selection applies atomically in one `git apply`.
    private string BuildSelectedLinesPatch()
    {
        if (_currentPatch == null) return "";
        var sb = new System.Text.StringBuilder();
        bool any = false;
        foreach (var hunk in Hunks)
        {
            var sel = System.Linq.Enumerable.ToList(
                System.Linq.Enumerable.Select(
                    System.Linq.Enumerable.Where(hunk.Lines, l => l.IsSelected), l => l.IndexInHunk));
            if (sel.Count == 0) continue;

            var single = Mainguard.Git.Services.PatchBuilder.BuildLinePatch(_currentPatch, hunk.HunkIndex, sel);
            if (string.IsNullOrEmpty(single)) continue;

            if (!any) { sb.Append(_currentPatch.Header); any = true; }
            sb.Append(single.Substring(_currentPatch.Header.Length)); // drop the duplicate header, keep the hunk body
        }
        return any ? sb.ToString() : "";
    }

    private async System.Threading.Tasks.Task ApplyPartialAsync(System.Action apply, bool confirmDiscard, string? discardWhat = null)
    {
        if (IsBusy || _currentFile == null) return;

        if (confirmDiscard)
        {
            if (!await ConfirmDiscardAsync(discardWhat ?? "these changes")) return;
        }

        IsBusy = true;
        PartialStagingError = null;
        try
        {
            await System.Threading.Tasks.Task.Run(apply);
            // Re-read this file's diff (same direction) so the viewer reflects the new state,
            // and refresh the staging panel counts.
            UpdateDiff(_currentFile);
            _onStagingChanged?.Invoke();
        }
        catch (Mainguard.Git.Exceptions.GitOperationException)
        {
            // Staleness: the file moved under the built patch. Reset the selection and re-parse
            // from a fresh diff — never silently recount / retry.
            UpdateDiff(_currentFile);
            PartialStagingError = "The file changed on disk — selection reset, try again.";
        }
        catch (System.Exception ex)
        {
            PartialStagingError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async System.Threading.Tasks.Task<bool> ConfirmDiscardAsync(string what)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var vm = new ConfirmationDialogViewModel
            {
                Title = "Discard Changes",
                Message = $"Discard {what} from the working tree?\nThis cannot be undone.",
                ConfirmButtonText = "Discard"
            };
            var dialog = new Views.ConfirmationDialog { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);
            return vm.IsConfirmed;
        }
        return false;
    }
}

public partial class DiffHunkRowViewModel : ObservableObject
{
    public int HunkIndex { get; init; }
    public string HeaderText { get; init; } = "";
    public bool IsStaged { get; init; }

    public bool ShowStage => !IsStaged;
    public bool ShowUnstage => IsStaged;
    public bool ShowDiscard => !IsStaged;

    // Unified view: flat line rows (click/drag to select).
    public ObservableCollection<DiffLineRowViewModel> Lines { get; } = new();

    // Side-by-side view: old|new paired rows for this block. Each side optionally carries the
    // corresponding unified-view DiffLineRowViewModel (#74) so a per-line drag-select in the split
    // view toggles the exact same IsSelected the existing partial-staging pipeline already reads
    // (BuildSelectedLinesPatch iterates Lines, unchanged) -- filler/context sides carry no line.
    public ObservableCollection<SideBySideLineRowViewModel> SideRows { get; } = new();
}

/// <summary>
/// Wraps one side-by-side paired row's left/right GitDiffLine data together with the (optional)
/// unified DiffLineRowViewModel each side corresponds to, so the split view can drive per-line
/// selection through the exact same DiffLineRowViewModel.IsSelected the unified view and the
/// existing BuildSelectedLinesPatch pipeline already use (#74). Left/RightLineRow is null for a
/// filler slot (the other side had no counterpart in this block).
/// </summary>
public sealed class SideBySideLineRowViewModel
{
    public required Mainguard.Git.Models.GitDiffLine LeftLine { get; init; }
    public required Mainguard.Git.Models.GitDiffLine RightLine { get; init; }
    public DiffLineRowViewModel? LeftLineRow { get; init; }
    public DiffLineRowViewModel? RightLineRow { get; init; }
}

public partial class DiffLineRowViewModel : ObservableObject
{
    public int IndexInHunk { get; init; }
    public Mainguard.Git.Models.DiffLineKind Kind { get; init; }
    public string DisplayText { get; init; } = "";

    // Intra-line emphasis (T-13): changed-word spans as UTF-16 offsets INTO DisplayText (prefix
    // already accounted for). Empty for unchanged/unpaired lines. Rendered by IntraLineDiffTextBlock.
    public System.Collections.Generic.List<(int Start, int Length)> HighlightSpans { get; init; } = new();
    public (int Start, int Length)? TrailingWhitespaceSpan { get; init; }
    public string EmphasisKey => IsAdd ? "DiffAddedEmphasis" : "DiffRemovedEmphasis";

    public bool IsChange => Kind == Mainguard.Git.Models.DiffLineKind.Add || Kind == Mainguard.Git.Models.DiffLineKind.Delete;
    public bool IsAdd => Kind == Mainguard.Git.Models.DiffLineKind.Add;
    public bool IsDelete => Kind == Mainguard.Git.Models.DiffLineKind.Delete;

    // Raised so the parent can recompute HasSelectedLines without holding per-line subscriptions.
    public System.Action? OnSelectionChanged { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => OnSelectionChanged?.Invoke();
}
