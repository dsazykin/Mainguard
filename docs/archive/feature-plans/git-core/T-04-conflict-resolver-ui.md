# T-04 — Conflict-Resolution Editor (end-to-end) — Implementation Plan

**Task ID:** T-04
**Milestone:** M2 (audit 2.3 / roadmap 4.4)
**Priority:** P0
**Depends on:** **T-02** (merge chunker) and **T-03** (conflict plumbing) — both must be merged to `main` first.
**Branch:** `plan/T-04-conflict-resolver-ui` (this doc) → implement on a fresh `feat/T-04-conflict-resolver-ui` off `main`.

> **Source of truth:** §T-04 of `docs/planning/Mainguard_Master_Implementation_Document.md`, §TI-04 + §TI-00 of
> `docs/testing/Mainguard_Test_Implementation_Strategy.md`. The Master Doc **Contract, Invariants, Edge-case matrix**
> are binding and reproduced below.

---

## 0. Context — the prototype this replaces (full-redo, reusing the visual base)

A working resolver exists today. **This task rebuilds it on the T-02/T-03 engine**, reusing the existing
3-column visual layout and accept/discard interactions as the *look*, and replacing the *data flow*. Know
exactly what you are changing:

| File | Today | After T-04 |
|---|---|---|
| `Mainguard.App.Shell/ViewModels/ConflictResolverWindowViewModel.cs` (338 lines) | `ParseFile()` reads the working-tree file and parses `<<<<<<< / ======= / >>>>>>>` markers into `ConflictBlockViewModel`s; `SaveAndClose` writes the reassembled string straight to disk. Constructor `(string filePath, Window window)`. | New constructor `(IGitService, IMergeDiffService, string repoPath, string conflictedPath)`; blocks come from `GetConflictBlobs → GenerateMergeChunks`; save goes through `AssembleMerged → ResolveConflict`. **No marker parsing, no direct disk write.** |
| `Mainguard.App.Shell/ViewModels/ConflictedFilesViewModel.cs` (110 lines) | Lists conflicted files from status; **spawns raw `git` on the UI thread** via a private `RunGit(string)` (`ProcessStartInfo("git", args)` + `WaitForExit`) for `checkout --ours/--theirs`, `add`, `merge --abort`, `rebase --abort`. | Lists files from `GetConflicts()`; per-file resolved/unresolved state + "N of M resolved" header; actions call **service methods** (`ResolveFileWithSide`, `AbortRebase`, new `AbortMerge`) via `Task.Run`. **The private `RunGit` is deleted.** |
| `Mainguard.App.Shell/Views/ConflictResolverWindow.axaml`, `ConflictedFilesWindow.axaml` | 3-column merge view; per-file list. | **Kept as the visual base**; rebind to the new VMs. Code-behind limited to scroll-sync. |

**Why the redo is worth it:** marker parsing only exposes ours/theirs (no common ancestor), so the current
resolver treats a whole marked span as one conflict and can't auto-merge non-conflicting regions. The
engine path (T-02) has the true base and classifies `Unchanged`/`LeftOnly`/`RightOnly`/`Conflict`.

### What you can rely on already existing

| Fact | Detail | Where |
|---|---|---|
| Async/`IsBusy` command pattern | `[RelayCommand(CanExecute = nameof(CanRunGitAction))] async Task X(CancellationToken ct)` with `IsBusy=true; try { await Task.Run(...) } catch(OperationCanceledException){} catch(Exception ex){ HandleGitActionException(...) } finally { IsBusy=false; }`. | `RepoDashboardViewModel.cs:221-289` |
| Typed-exception routing | `Unwrap<T>(ex)` + `HandleGitActionException`; a `MergeConflictException` is treated as guidance (not error) and refreshes status. **T-04 changes this branch to open the resolver window.** | `RepoDashboardViewModel.cs:182-219` |
| Conflict window is opened in 3 places | `StagingPanelViewModel.cs:293`, `BranchBrowserViewModel.cs:316` & `:760`. Each `new ConflictedFilesWindow()` + `new ConflictedFilesViewModel(_repoPath, _gitService, dialog)` + `ShowDialog`. | those files |
| RunGit family (hardened) | `private void RunGitChecked(string repoPath, params string[] args)` and an env overload — ArgumentList, no shell, `GIT_TERMINAL_PROMPT=0`, stderr captured. Use this in the **service**, never a raw `Process` in a ViewModel. | `GitServices.cs:741-767` |
| MVVM stack | CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`, `[NotifyCanExecuteChangedFor]`). Avalonia 11.1.x. `ViewLocator` resolves View from ViewModel. | throughout `Mainguard.App.Shell` |
| T-02/T-03 surface (new, from deps) | `IMergeDiffService.GenerateMergeChunks/AssembleMerged`; `IGitService.GetConflicts/GetConflictBlobs/ResolveConflict/HasUnresolvedConflicts`. | `feat/T-02`, `feat/T-03` |

---

## 1. Files to create / modify

| Action | Path | Purpose |
|---|---|---|
| **Create** | `Mainguard.Agents/Models/ConflictSide.cs` | `enum ConflictSide { Ours, Theirs }`. |
| **Edit** | `Mainguard.Agents/Services/IGitService.cs` + `GitServices.cs` | Add `ResolveFileWithSide`, `GetCurrentOperation`, and (additive) `AbortMerge`. |
| **Rewrite** | `Mainguard.App.Shell/ViewModels/ConflictResolverWindowViewModel.cs` | Engine-driven; new constructor; `MergeChunkViewModel` nested/adjacent. |
| **Rewrite** | `Mainguard.App.Shell/ViewModels/ConflictedFilesViewModel.cs` | Service-driven list + completion gating; delete `RunGit`. |
| **Edit** | `Mainguard.App.Shell/Views/ConflictResolverWindow.axaml(.cs)` | Rebind to chunk VMs; add merged-preview pane; scroll-sync only in code-behind. |
| **Edit** | `Mainguard.App.Shell/Views/ConflictedFilesWindow.axaml(.cs)` | Header "N of M resolved"; Commit-merge / Continue-rebase buttons; per-file state. |
| **Edit** | `RepoDashboardViewModel.cs`, `BranchBrowserViewModel.cs`, `StagingPanelViewModel.cs` | Route `MergeConflictException` → open resolver window. |
| **Create** | `Mainguard.Tests/MergeChunkViewModelTests.cs`, `ConflictResolverWindowViewModelTests.cs` | ViewModel tests (need **TI-00** headless infra). |
| **Edit** | `Mainguard.Tests/GitServiceConflictTests.cs` | Add the end-to-end integration case (§8.7). |

**Prerequisite:** ViewModel tests require **TI-00** (headless Avalonia test infra: `Mainguard.Tests`→`Mainguard.App.Shell`
reference, `Avalonia.Headless` + `Avalonia.Headless.XUnit` 11.1.x, `TestAppBuilder.cs` with
`[AvaloniaTestApplication]`). If TI-00 is not yet merged, land it first or in this PR's dependency — the
service-tier integration test (§8.7) does **not** need it and must land regardless.

---

## 2. Contract (must exist exactly)

### 2.1 Service additions

```csharp
// Mainguard.Agents/Models/ConflictSide.cs
namespace Mainguard.Agents.Models;
public enum ConflictSide { Ours, Theirs }

// IGitService + GitService
void ResolveFileWithSide(string repoPath, string path, ConflictSide side);   // checkout --ours/--theirs + stage
LibGit2Sharp.CurrentOperation GetCurrentOperation(string repoPath);          // repo.Info.CurrentOperation passthrough
void AbortMerge(string repoPath);                                            // additive: git merge --abort (replaces VM's raw call)
```

### 2.2 ViewModels

- `ConflictResolverWindowViewModel` re-created with constructor
  `(IGitService gitService, IMergeDiffService mergeService, string repoPath, string conflictedPath)`.
- `MergeChunkViewModel` wraps one `MergeChunk` and exposes `TakeOursCommand` / `TakeTheirsCommand` /
  `TakeBothCommand`, an editable `CustomText`, and `IsResolved`.
- `ConflictedFilesViewModel` lists `GetConflicts()` with per-file resolved/unresolved state and a
  "N of M resolved" header; commit-merge / continue-rebase commands gated on
  `HasUnresolvedConflicts == false`.

> Naming note: the Master Doc mentions a `ConflictedFilesViewModel` header; the existing type is already
> named that. Keep the name. `MergeChunkViewModel` is new. The old `ConflictBlockViewModel` is **removed**
> (superseded by `MergeChunkViewModel`).

---

## 3. Service implementation

### 3.1 `ResolveFileWithSide`

```csharp
public void ResolveFileWithSide(string repoPath, string path, ConflictSide side) =>
    ExecuteWithRepo(repoPath, repo =>
    {
        RunGitChecked(repoPath, "checkout", side == ConflictSide.Ours ? "--ours" : "--theirs", "--", path);
        Commands.Stage(repo, path);
    });
```

`checkout --ours/--theirs -- <path>` writes the chosen full side into the working tree; `Commands.Stage`
clears the conflict. Uses `RunGitChecked` (ArgumentList — no quoting bugs), **not** a raw `Process`.

### 3.2 `GetCurrentOperation`

```csharp
public LibGit2Sharp.CurrentOperation GetCurrentOperation(string repoPath) =>
    ExecuteWithRepo(repoPath, repo => repo.Info.CurrentOperation);
```

`CurrentOperation` values you branch on: `Merge`, `Rebase` / `RebaseInteractive` / `RebaseMerge`,
`CherryPick*`, `Revert*`, `None`.

### 3.3 `AbortMerge` (additive — removes the last raw-git call from the VM)

```csharp
public void AbortMerge(string repoPath) => RunGitChecked(repoPath, "merge", "--abort");
```

Add all three to `IGitService` (G-10). `AbortRebase` already exists and is reused for the rebase case.

---

## 4. `MergeChunkViewModel`

Wraps a single `MergeChunk`. One instance per chunk in the resolver's list.

```csharp
public partial class MergeChunkViewModel : ObservableObject
{
    public MergeChunk Model { get; }
    public MergeChunkViewModel(MergeChunk model) { Model = model; CustomText = model.CustomText ?? ""; }

    public ChunkKind Kind => Model.Kind;
    public bool IsConflict => Model.Kind == ChunkKind.Conflict;
    public string BaseText  => Model.BaseText;
    public string OursText  => Model.LeftText;    // "left" == ours
    public string TheirsText => Model.RightText;  // "right" == theirs

    // Non-conflict chunks are inherently resolved.
    public bool IsResolved => !IsConflict || Model.Resolution != ChunkResolution.Unresolved;

    [ObservableProperty] private string _customText = "";

    // A resolution change must notify IsResolved AND bubble up so the parent recomputes preview/gating.
    public event Action? ResolutionChanged;

    [RelayCommand] private void TakeOurs()   => SetResolution(ChunkResolution.TakeLeft);
    [RelayCommand] private void TakeTheirs() => SetResolution(ChunkResolution.TakeRight);
    [RelayCommand] private void TakeBoth()   => SetResolution(ChunkResolution.TakeBoth);
    [RelayCommand] private void UseCustom()  { Model.CustomText = CustomText; SetResolution(ChunkResolution.Custom); }

    private void SetResolution(ChunkResolution r)
    {
        Model.Resolution = r;
        OnPropertyChanged(nameof(IsResolved));
        ResolutionChanged?.Invoke();
    }
}
```

- Editing `CustomText` alone does not resolve; the user commits it via `UseCustom` (or bind `UseCustom` to
  the text box's lost-focus — either is fine, tests pin `CustomEdit_ShouldSetResolutionCustom_AndCaptureText`).
- **v1 rule (edge matrix):** a resolved chunk may be re-resolved to a different side, but never returned to
  `Unresolved`. Do not expose an "un-resolve" command.

---

## 5. `ConflictResolverWindowViewModel` (per-file, engine-driven)

### 5.1 Load flow (off the UI thread; marshal back)

```csharp
public ConflictResolverWindowViewModel(IGitService git, IMergeDiffService merge, string repoPath, string conflictedPath)
{
    _git = git; _merge = merge; _repoPath = repoPath; _path = conflictedPath;
    _ = LoadAsync();
}

private async Task LoadAsync()
{
    IsLoading = true;
    // 1) fetch blobs + chunk on a worker thread
    var (chunks, has) = await Task.Run(() =>
    {
        var (b, o, t) = _git.GetConflictBlobs(_repoPath, _path);
        HasOurs = o.Length > 0 || /* stage-present flag from GetConflicts */ _hasOurs;
        return (_merge.GenerateMergeChunks(b, o, t), true);
    });
    // 2) touch bound collections ONLY on the UI thread
    Dispatcher.UIThread.Post(() =>
    {
        Chunks.Clear();
        foreach (var c in chunks)
        {
            var vm = new MergeChunkViewModel(c);
            vm.ResolutionChanged += OnAnyResolutionChanged;
            Chunks.Add(vm);
        }
        RecomputePreviewAndGating();
        IsLoading = false;
    });
}
```

`Chunks` is `ObservableCollection<MergeChunkViewModel>`. Never mutate it off the UI thread (invariant 3;
test `Load_ShouldNotTouchBoundCollections_OffUiThread`).

### 5.2 Merged preview (never writes to disk)

```csharp
[ObservableProperty] private string _mergedPreview = "";

private void RecomputePreviewAndGating()
{
    // Build a COPY of the chunks; render still-unresolved conflicts as marker placeholders
    // so the preview is always assemble-able. The placeholder text is PREVIEW ONLY.
    var preview = Chunks.Select(vm =>
    {
        var m = vm.Model;
        if (m.Kind == ChunkKind.Conflict && m.Resolution == ChunkResolution.Unresolved)
            return new MergeChunk {
                Kind = ChunkKind.Unchanged,
                BaseText = $"<<<<<<< ours\n{m.LeftText}\n=======\n{m.RightText}\n>>>>>>> theirs"
            };
        return m;
    });
    MergedPreview = _merge.AssembleMerged(preview);   // safe: unresolved conflicts were rewritten to Unchanged
    OnPropertyChanged(nameof(IsFullyResolved));
    MarkResolvedCommand.NotifyCanExecuteChanged();
}

private void OnAnyResolutionChanged() => RecomputePreviewAndGating();
public bool IsFullyResolved => Chunks.All(c => c.IsResolved);
```

> ⚠️ The marker text above must **never** reach disk while unresolved. It exists solely inside the preview
> copy. Regression test `Preview_ShouldNeverWriteToDisk` guards this.

### 5.3 Per-file completion

```csharp
[RelayCommand(CanExecute = nameof(IsFullyResolved))]
private async Task MarkResolved()
{
    // Assemble from the REAL chunks (all resolved by CanExecute) and persist via the service.
    var merged = _merge.AssembleMerged(Chunks.Select(c => c.Model));
    await Task.Run(() => _git.ResolveConflict(_repoPath, _path, merged));
    _window.Close(true);   // signal the parent list to refresh
}
```

### 5.4 Delete/modify + add/add files (no chunk editor)

When `GetConflicts()` reports a file with a missing stage (`HasOurs == false` or `HasTheirs == false`), this
per-file window must **not** show the chunk editor. Instead show two file-level actions (label the missing
pane "(deleted on this side)"):

- **Keep file** → `ResolveFileWithSide(repoPath, path, survivingSide)` (the side whose stage is present).
- **Delete file** → `RunGitChecked(repoPath, "rm", "--", path)` — expose as a small service method
  `RemoveFileFromMerge(repoPath, path)` (additive) rather than spawning git in the VM.

The decision (chunk editor vs keep/delete) is made from the `ConflictedFile` flags passed in from the list —
thread `HasOurs`/`HasTheirs` into the resolver VM (extend the constructor or pass the `ConflictedFile`).

---

## 6. `ConflictedFilesViewModel` (the list + session completion)

Rewrite so it is service-driven and gated:

```csharp
public ConflictedFilesViewModel(string repoPath, IGitService git, IMergeDiffService merge, Window window)
{
    _repoPath = repoPath; _git = git; _merge = merge; _window = window;
    _operation = git.GetCurrentOperation(repoPath);   // Merge vs Rebase*
    Reload();
}

private void Reload()
{
    Files.Clear();
    foreach (var c in _git.GetConflicts(_repoPath))
        Files.Add(new ConflictedFileItem(c));         // carries Path + Has* flags + resolved state
    UnresolvedCount = _git.GetConflicts(_repoPath).Count;   // or track locally
    OnPropertyChanged(nameof(HeaderText));
    CommitMergeCommand.NotifyCanExecuteChanged();
    ContinueRebaseCommand.NotifyCanExecuteChanged();
}

public string HeaderText => $"{Total - Unresolved} of {Total} resolved";
private bool CanComplete() => !_git.HasUnresolvedConflicts(_repoPath) && !IsBusy;
```

Per-file commands (all async / `Task.Run` / `IsBusy`, then `Reload()`):
- **Resolve with ours / theirs** → `ResolveFileWithSide(...)`.
- **Open editor** → construct `ConflictResolverWindowViewModel(git, merge, repoPath, item.Path, item.HasOurs, item.HasTheirs)`, `ShowDialog`; on `true`, `Reload()`.
- **Delete `RunGit`** — the private raw-`Process` method is removed entirely.

Session completion (exactly one enabled, gated by `CanComplete`):

```csharp
[RelayCommand(CanExecute = nameof(CanComplete))]
private async Task CommitMerge()          // visible when _operation == CurrentOperation.Merge
{
    IsBusy = true;
    try { await Task.Run(() => _git.Commit(_repoPath, _git.GetMergeMessage(_repoPath))); _window.Close(true); }
    catch (Exception ex) { /* HandleGitActionException-style */ }
    finally { IsBusy = false; }
}

[RelayCommand(CanExecute = nameof(CanComplete))]
private async Task ContinueRebase()       // visible when _operation is a Rebase* variant
{
    IsBusy = true;
    try { await Task.Run(() => _git.ContinueRebase(_repoPath)); _window.Close(true); }
    catch (MergeConflictException) { Reload(); /* next conflict in the rebase */ }
    finally { IsBusy = false; }
}
```

- Which button is **visible** is chosen from `_operation` (`Merge` → Commit merge; `Rebase*` → Continue
  rebase; `CherryPick*`/`Revert*` → Commit, same as Merge). Both are **disabled** while any conflict is
  unresolved.
- **Cancel** → `AbortRebase` when rebasing else `AbortMerge` (service methods; no raw git).
- Commit-merge must **not** `StageAll` — the merge commit contains only what the merge/resolution staged
  (edge matrix: "dirty unrelated files" stay out).

---

## 7. Entry wiring — route `MergeConflictException` to the resolver

Today `HandleGitActionException` (`RepoDashboardViewModel.cs:195`) treats `MergeConflictException` as a
toast + refresh. Change every catch site that can produce it to **open the conflict window** instead:

- `RepoDashboardViewModel` (Pull/UpdateProject/Merge paths via `HandleGitActionException`).
- `BranchBrowserViewModel:316` and `:760` (merge/rebase from the branch UI).
- `StagingPanelViewModel:293` (`ResolveConflicts`).

Factor a single helper (e.g. in a shared place or duplicated minimally) that does the
`new ConflictedFilesWindow()` + `new ConflictedFilesViewModel(repoPath, git, merge, dialog)` + `ShowDialog`,
and call it from the `MergeConflictException` branch. **The only signal used to detect a conflict is the
typed `MergeConflictException`** — never string-matching messages (rejection trigger).

Because `ConflictedFilesViewModel` now needs `IMergeDiffService`, construct one (`new MergeDiffService()` —
no DI container) at each call site or hold one on the parent VM.

---

## 8. Edge-case matrix + Test contract (TI-04)

### 8.1 Edge-case matrix (binding)

| Case | Required behavior |
|---|---|
| resolve chunks in any order | preview always consistent; completion gate opens only when all resolved |
| re-resolve a chunk | allowed to switch sides; never returns to `Unresolved` |
| delete/modify conflict file | no chunk editor; Keep/Delete actions; resolving updates the list |
| rebase conflict (not merge) | completion button says/does **Continue rebase** |
| user closes window mid-resolution | index untouched for unresolved files; already-resolved files stay resolved |
| commit merge with dirty unrelated files | merge commit contains only merge-staged content — no `StageAll` |

### 8.2 Tests — `MergeChunkViewModelTests.cs`, `ConflictResolverWindowViewModelTests.cs` (need TI-00), + one integration in `GitServiceConflictTests.cs`

| # | Test | Assertion |
|---|---|---|
| 1 | `TakeOurs_ShouldMarkResolved_AndUpdatePreview` (`[Theory]` ours/theirs/both) | after the command, `IsResolved` true and `MergedPreview` contains the chosen side. |
| 2 | `CustomEdit_ShouldSetResolutionCustom_AndCaptureText` | set `CustomText`, invoke `UseCustom` → `Model.Resolution == Custom`, `Model.CustomText` captured. |
| 3 | `MarkResolved_CanExecute_ShouldBeFalse_WhileAnyChunkUnresolved` | false with any unresolved conflict; flips true at the last resolution. |
| 4 | `CommitMerge_CanExecute_ShouldFollowHasUnresolvedConflicts` | fake `IGitService` toggles `HasUnresolvedConflicts`; command CanExecute tracks it. |
| 5 | `Load_ShouldNotTouchBoundCollections_OffUiThread` | headless dispatcher: load completes with no cross-thread exception; `Chunks` populated. |
| 6 | `DeleteModifyConflict_ShouldOfferKeepOrDelete_NotChunkEditor` | fake blobs with `HasTheirs == false` → resolver exposes Keep/Delete, not the chunk editor. |
| 7 | `FullConflictLoop_ResolveMixed_ThenCommit_ShouldProduceMergedBlob` (integration, no TI-00) | seed a 3-conflict file via `TempRepoFixture`; take ours/ theirs/ custom across the three; `MarkResolved`; `Commit` → committed blob equals `AssembleMerged` output and `Head.Tip.Parents.Count()==2`. Rebase variant ends with `ContinueRebase` completing. |
| 8 | `Preview_ShouldNeverWriteToDisk` (regression) | capture workdir file mtime+content; toggle resolutions arbitrarily; assert unchanged until `MarkResolved`. |

Use the fake `IGitService`/`IMergeDiffService` for ViewModel tests (TI-00 pattern). The integration case (#7)
uses the real services + fixture and must land even if TI-00 slips.

---

## 9. Invariants (MUST) / Acceptable variations / Rejection triggers

**MUST:**
1. Disk writes happen **only** in `ResolveConflict` / `ResolveFileWithSide` (/`RemoveFileFromMerge`) — never
   from preview code.
2. Completion gating: commit/continue commands are un-executable while `HasUnresolvedConflicts()` is true.
3. All Git work off the UI thread; bound collections mutated only on `Dispatcher.UIThread`.
4. The final merge commit has two parents; a rebase-conflict flow ends with `ContinueRebase` succeeding.
5. Resolution logic lives in ViewModel/service classes, not code-behind (code-behind = scroll-sync only).

**MAY:** 3-pane vs 4-pane layout; gutter/scroll-sync design; incremental vs from-scratch preview recompute;
extra "resolve whole file with ours/theirs" buttons.

**Rejection triggers:**
- Conflict markers written to the working tree by the preview path.
- String-sniffing exception messages to detect conflicts (typed `MergeConflictException` only).
- Any blocking Git call in a `[RelayCommand]` without `Task.Run` (the current `AcceptIncoming`/`Cancel`
  synchronous `RunGit` is exactly what must disappear).
- Any raw `ProcessStartInfo("git", ...)` left in a ViewModel (route through the service's `RunGit` family).
- `StageAll` before the merge commit.

---

## 10. Reviewer verification script

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~ConflictResolver|FullyQualifiedName~MergeChunkViewModel|FullyQualifiedName~Conflict"

# the raw-git-in-VM prototype is gone:
grep -rn 'ProcessStartInfo("git"' Mainguard.App.Shell/                      # -> 0 hits
grep -n  'ParseFile\|<<<<<<<' Mainguard.App.Shell/ViewModels/ConflictResolverWindowViewModel.cs   # -> 0 hits (marker parsing removed)
grep -rn 'new System.Exception\|throw new Exception' Mainguard.App.Shell/    # -> 0 hits

# Manual (scripted repo): merge two conflicting branches in the UI ->
#   resolver opens, mixed resolution (ours/theirs/custom) works,
#   commit-merge enables only at the end, git log shows a 2-parent commit;
#   repeat with a rebase to confirm Continue-rebase path.
```

---

## 11. Definition of done

- [ ] `ConflictSide.cs`; `ResolveFileWithSide`, `GetCurrentOperation`, `AbortMerge` (+ `RemoveFileFromMerge`) on `IGitService`/`GitService`.
- [ ] `ConflictResolverWindowViewModel` rebuilt engine-driven (new constructor); `MergeChunkViewModel` added; `ConflictBlockViewModel` and `ParseFile` removed.
- [ ] `ConflictedFilesViewModel` service-driven, gated, `RunGit` deleted; delete/modify + add/add handled.
- [ ] Views rebound; code-behind scroll-sync only; merged-preview pane present.
- [ ] `MergeConflictException` routes to the resolver window at all three catch sites.
- [ ] Every edge-matrix row + every invariant satisfied; TI-04 tests (+ integration) green; TI-00 landed if ViewModel tests are in this PR.
- [ ] Reviewer script passes with the expected zero-hit greps. One task = one PR; PR links **T-04**.
```

---

## 12. Follow-up — UI polish to revisit (deferred, not blocking)

The resolver was rebuilt as a synchronized IntelliJ-style 3-pane merge editor and
iterated against the JetBrains reference (`reference_merge_window.png`). The
resolution model, color semantics, stacked add/add slots, flow-down connectors, and
equal/side-hugging accept-reject glyphs are all done and verified via the headless
render harness (`Mainguard.Tests/Headless/ResolverRenderHarness.cs`).

**One reference behavior is intentionally deferred — come back to perfect it:**

- **Gutters as overlays, not dedicated columns.** In the JetBrains reference the
  accept/reject gutters are *embedded on top of* the code columns (the change
  highlight is continuous and the code text scrolls *underneath* the gutter), rather
  than being their own fixed-width column that reserves horizontal space (our current
  `MergeGutter` approach with the `*,52,*,52,*` grid). Making the gutters true
  overlays with horizontal scroll pass-through in AvaloniaEdit is a deeper structural
  change and was consciously left for a later pass.

Other nice-to-haves for that pass: base-revision hint on unresolved modify rows
(reference shows the base line in the Result), and a "Show Details" word-diff toggle.

This does **not** block T-04 acceptance — the resolver is fully functional. It is a
fidelity/polish item to return to once the higher-priority build-order tasks land.
