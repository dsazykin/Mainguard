# T-06 — Partial-Staging UI: Patch Model, Builder, Diff-Viewer Wiring — Implementation Plan

**Task ID:** T-06 · **Milestone:** M3 (audit 2.13; backend landed as fix 1.13) · **Priority:** P0
**Depends on:** T-01. **Consumed by:** T-12 (file/line history reuses `PatchParser`), T-13 (diff quality).
**Branch:** `plan/T-06-partial-staging-ui` → implement on `feat/T-06-partial-staging-ui` off `main`.

> **Source of truth:** §T-06 of the Master Doc, §TI-06 of the Test Strategy. Contract, invariants, and
> edge-case matrix below are binding.

---

## 0. Context — what exists, what's missing

The **backend already landed** (fix 1.13): `StageHunk` / `UnstageHunk` / `DiscardHunk` each take a **patch
string** and shell it to `git apply` on **stdin** via the private `ApplyPatch(repoPath, patch, ...applyArgs)`
(`GitServices.cs:171-247`). `git apply` requires a trailing newline (the helper adds one) and never prompts.

What's missing: **nothing builds the sub-patches.** The diff viewer (`DiffViewerViewModel`) renders
`GetFileDiff(...)` as `SideBySideDiffRow`s of plain text with no hunk/line selection. This task adds:
1. a **structured patch model** (`FilePatch`/`DiffHunk`/`DiffLine`),
2. a pure **`PatchParser`** (parse + byte-identical serialize) and **`PatchBuilder`** (hunk/line subsets),
3. the **diff-viewer UI** affordances that feed builder output into the existing `StageHunk`/`UnstageHunk`/`DiscardHunk`.

### What you can rely on

| Fact | Where |
|---|---|
| `StageHunk/UnstageHunk/DiscardHunk(string repoPath, string patch)` → `ApplyPatch(..., "--cached"[, "--reverse"])`; stdin; throws `GitOperationException` on non-zero exit | `GitServices.cs:171-247` |
| `string GetFileDiff(string repoPath, string filePath, bool isStaged)` — `isStaged:false` = workdir↔index, `isStaged:true` = index↔HEAD | `GitServices.cs:269` |
| Diff viewer renders `ObservableCollection<SideBySideDiffRow>` from the raw diff text | `DiffViewerViewModel.cs:128-248` |
| Discard confirmation dialog already exists (fix 1.4 policy) | used by `DiscardChanges` |
| No new **service** methods needed — the UI calls the existing three with built patches | — |

**Policy split (G-7):** partial staging is a **git-CLI** feature (patch → `git apply`). Keep it that way.

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Models/DiffHunk.cs` (`DiffLineKind`, `DiffLine`, `DiffHunk`, `FilePatch`) |
| **Create** | `Mainguard.Agents/Services/PatchParser.cs` (pure static) |
| **Create** | `Mainguard.Agents/Services/PatchBuilder.cs` (pure static) |
| **Edit** | `Mainguard.App.Shell/ViewModels/DiffViewerViewModel.cs` (parse diff, hunk/line selection, stage/unstage/discard) |
| **Edit** | `StagingPanelViewModel` refresh coupling (a file can be staged+modified at once) |
| **Create** | `Mainguard.Tests/PatchParserTests.cs`, `PatchBuilderTests.cs` (pure), `Mainguard.Tests/TestData/patches/*` corpus |
| **Edit** | `Mainguard.Tests/GitServicePartialStagingTests.cs` (integration, `RequiresGitCli`) |

---

## 2. Contract (must exist exactly)

```csharp
// Mainguard.Agents/Models/DiffHunk.cs
namespace Mainguard.Agents.Models;

public enum DiffLineKind { Context, Add, Delete }

public sealed class DiffLine
{
    public DiffLineKind Kind { get; init; }
    public string Text { get; init; } = "";      // WITHOUT the +/-/space prefix
    public bool NoNewlineAtEof { get; init; }     // "\ No newline at end of file" applies to this line
}
public sealed class DiffHunk
{
    public int OldStart { get; init; } public int OldCount { get; init; }
    public int NewStart { get; init; } public int NewCount { get; init; }
    public string SectionHeading { get; init; } = "";   // text after the second @@
    public IReadOnlyList<DiffLine> Lines { get; init; } = Array.Empty<DiffLine>();
}
public sealed class FilePatch
{
    public string Header { get; init; } = "";           // everything before the first @@ (diff --git, ---, +++, index, mode)
    public IReadOnlyList<DiffHunk> Hunks { get; init; } = Array.Empty<DiffHunk>();
}

// Mainguard.Agents/Services/PatchParser.cs
public static class PatchParser
{
    public static IReadOnlyList<FilePatch> Parse(string unifiedDiff);
    public static string Serialize(FilePatch patch);        // round-trips byte-identically (LF input)
}

// Mainguard.Agents/Services/PatchBuilder.cs
public static class PatchBuilder
{
    public static string BuildHunkPatch(FilePatch file, IReadOnlyList<int> selectedHunkIndexes);
    public static string BuildLinePatch(FilePatch file, int hunkIndex, IReadOnlyList<int> selectedLineIndexes);
}
```

Both classes are **pure** (no IO, no repo).

---

## 3. `PatchParser`

### 3.1 Parse

1. Split into file sections: a new section starts at a line beginning `diff --git `. Everything from that
   line up to (but excluding) the first `@@` line is the section's `Header` (verbatim, includes `diff --git`,
   `index`, `--- `, `+++ `, and `rename from/to` / mode lines).
2. Hunk header regex (exact): `^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$`. A missing count group means
   `1`. Group 5 (after the second `@@`) is `SectionHeading` (keep leading space if git emitted one — the
   serializer must reproduce it exactly).
3. Body lines until the next `@@` / `diff --git` / EOF:
   - prefix `' '` → `Context`, `'+'` → `Add`, `'-'` → `Delete`; `Text` = the line **without** the prefix.
   - a line starting `'\'` (`\ No newline at end of file`) is **not** a `DiffLine`; instead set
     `NoNewlineAtEof = true` on the **immediately preceding** `DiffLine`.

### 3.2 Serialize (byte-identical round-trip)

Emit `Header` verbatim, then for each hunk:
`@@ -{OldStart},{OldCount} +{NewStart},{NewCount} @@{SectionHeading}\n`, then each body line with its prefix
restored (`' '`/`'+'`/`'-'`), and after any line flagged `NoNewlineAtEof`, emit `\ No newline at end of
file\n`.

> **Count-omission caveat:** git omits `,1` counts (`@@ -3 +3 @@`). To guarantee byte-identical round-trip on
> the corpus, record on each `DiffHunk` whether the original header used the short form (add an internal/
> additive flag, e.g. `bool OldCountOmitted`/`NewCountOmitted`, set during parse) and reproduce it. **This
> flag is the difference between passing and failing the round-trip theory.** It is an acceptable additive
> to the model. (Simplest: store the raw header string too and re-emit it in `Serialize`, recomputing only
> when the builder changes counts.)

**Property (acceptance test):** `Serialize(Parse(x)[i])` reproduces file section `i` of `x`
byte-for-byte, for every corpus file (LF input).

---

## 4. `PatchBuilder`

### 4.1 `BuildHunkPatch` (whole hunks)

`Header` + the selected hunks serialized **verbatim**, in original order. Empty selection → `""`.

### 4.2 `BuildLinePatch` (the `git add -p` split semantics — the tricky one)

Operate within the single chosen hunk. Build a new line list applying these rules per original line:

| Original line | Selected? | Result |
|---|---|---|
| `Context` | (n/a) | keep as `Context` |
| `Add` | selected | keep as `Add` |
| `Add` | **not** selected | **drop entirely** |
| `Delete` | selected | keep as `Delete` |
| `Delete` | **not** selected | **convert to `Context`** (the old text stays, since the deletion isn't taken) |

Then recompute the header counts from the **resulting** lines:
- `OldCount` = (#Context + #Delete) in the result;
- `NewCount` = (#Context + #Add) in the result;
- `OldStart` = original hunk `OldStart` (unchanged);
- `NewStart` = original hunk `NewStart` (reference choice — single-hunk subsets apply against a fresh
  workdir↔index diff; `git apply` tolerates it). **Correctness is pinned by the integration tests applying
  the result — not by the exact `NewStart`.**

Preserve any `NoNewlineAtEof` flags on kept lines. Selecting only `Context` (or nothing) → return `""`
(callers treat empty as a no-op; matches the existing `ApplyPatch` guard).

### 4.3 Worked example (pin your mental model)

Hunk (old start 10): ` a` / `-b` / `+B` / `-c` / ` d`  (context a, delete b, add B, delete c, context d).
Header parsed: `@@ -10,3 +10,2 @@` (old: a,b,c,d? no — old lines = context+delete = a,b,c,d = 4… example
uses 3 for brevity; trust the recount, not the literal).
Select **only the `+B` add** (drop `-b`? it's unselected delete → becomes context; `-c` unselected delete →
context; other adds none):
Result lines: ` a` / ` b` (was `-b`→context) / `+B` / ` c` (was `-c`→context) / ` d`.
`OldCount` = context(a,b,c,d)=4; `NewCount` = context(4)+add(1)=5. Emit `@@ -10,4 +10,5 @@`. Apply → only
`B` is inserted; `b`/`c` remain. That is the desired "stage just this addition" behavior.

---

## 5. Diff-viewer wiring

1. In `DiffViewerViewModel`, parse the current file diff into a `FilePatch` (there is exactly one file per
   viewer). Render hunk-header rows with **Stage / Unstage / Discard** buttons and a per-line gutter that
   supports multi-select (click + drag) with a context menu "Stage selected lines" / "Discard selected
   lines".
2. **Direction rule (invariant 4):**
   - stage-subsets are built from the **workdir↔index** diff (`GetFileDiff(path, isStaged:false)`) → `StageHunk`.
   - unstage-subsets from the **index↔HEAD** diff (`GetFileDiff(path, isStaged:true)`) → `UnstageHunk`.
3. **Discard** routes through the existing confirmation dialog (fix 1.4 policy), with the line/hunk count in
   the button text → `DiscardHunk`.
4. After **every** partial op: re-fetch the file diff **and** refresh `StagingPanelViewModel` (a file can be
   simultaneously staged and modified — status already reports both).
5. **Staleness rule (invariant, edge matrix):** always build subsets from a freshly fetched diff. If `git
   apply` rejects (non-zero → `GitOperationException` from `ApplyPatch`), refresh the diff and surface
   "The file changed on disk — selection reset, try again." **Never** retry with `--recount` or
   `--unidiff-zero` to paper over wrong counts.
6. All ops async (`Task.Run` + `IsBusy`); typed-exception routing.

---

## 6. Edge-case matrix (binding)

| Case | Required behavior |
|---|---|
| patch ending `\ No newline at end of file` | parse + serialize round-trip preserves it |
| adjacent hunks (zero context between) | two hunks; hunk-subset of either applies cleanly |
| rename header (`rename from/to`) | header preserved verbatim by serializer |
| line-subset: only additions selected | valid header counts; applies |
| line-subset: only deletions selected | unselected adds dropped, counts right; applies |
| line-subset: first/last line of hunk | applies |
| nothing selected | `""` → no-op |
| stale patch (file changed since diff) | typed failure surfaced; no silent `--recount` |
| multi-file diff input | `Parse` returns one `FilePatch` per file |

---

## 7. Invariants (MUST)

1. `PatchParser`/`PatchBuilder` are pure (no IO, no repo access).
2. Round-trip: `Serialize(Parse(x)[i])` reproduces each file section byte-identically (LF input).
3. Every builder output either applies cleanly via `git apply` in integration tests, or is `""`.
4. Unstage subsets are built from the **index↔HEAD** diff, never workdir↔index.
5. Partial-discard goes through the confirmation dialog (data-loss guard, fix 1.4 policy).

---

## 8. Test contract — TI-06

**Corpus:** commit real git-produced patches to `Mainguard.Tests/TestData/patches/` covering: simple modify,
multi-hunk, multi-file, no-newline-at-EOF, rename header, adjacent hunks, add-only new file, delete-only.

Pure (`PatchParserTests.cs`, `PatchBuilderTests.cs`):
1. `Parse_Serialize_ShouldRoundTripByteIdentically` — `[Theory]` over the whole corpus.
2. `Parse_ShouldExposeHunkHeaderNumbers_AndSectionHeading`.
3. `Parse_ShouldAttachNoNewlineMarker_ToPrecedingLine`.
4. `BuildHunkPatch_ShouldEmitHeaderPlusSelectedHunksVerbatim`.
5. `BuildLinePatch_OnlyAdditionsSelected_ShouldRecountCorrectly` (assert the exact `@@` header).
6. `BuildLinePatch_OnlyDeletionsSelected_ShouldTurnUnselectedDeletesToContext_DropUnselectedAdds`.
7. `BuildLinePatch_FirstAndLastLineOfHunk_ShouldApplyCleanly` (proven in #9's integration).
8. `BuildLinePatch_NothingSelected_ShouldReturnEmpty`.

Integration (`GitServicePartialStagingTests.cs`, `RequiresGitCli` trait — ground truth is that git accepts
the output):
9. `StageBuiltLinePatch_ShouldPutExactlySelectedLinesInIndex` — 4-line hunk, select 1 line → `GetFileDiff(isStaged:true)` shows exactly that change; workdir untouched.
10. `UnstageBuiltPatch_ShouldReverseExactly` — built from the **index↔HEAD** diff.
11. `DiscardBuiltPatch_ShouldRemoveOnlySelectedLines_FromWorkdir`.
12. `StaleBuiltPatch_ShouldThrowTyped_NotSilentlyRecount` — build, modify the file, apply → `GitOperationException`.

ViewModel selection cases land after TI-00.

---

## 9. Rejection triggers / Reviewer script

**Rejection:** building patches by string-slicing the raw diff without parsing; any `--recount` /
`--unidiff-zero` to mask wrong counts (`--unidiff-zero` only ever if the builder emits genuinely
zero-context patches by design — the reference design does not); discard-lines without confirmation.

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~PatchParser|FullyQualifiedName~PatchBuilder|FullyQualifiedName~PartialStaging"
grep -nE "Repository|System\.IO|File\.|Directory\." Mainguard.Agents/Services/PatchParser.cs Mainguard.Agents/Services/PatchBuilder.cs   # -> 0 hits (pure)
grep -rn "\-\-recount\|--unidiff-zero" Mainguard.Agents/ Mainguard.App.Shell/   # -> 0 hits
```

## 10. Definition of done

- [ ] Model + pure `PatchParser` (byte-identical round-trip) + `PatchBuilder` (hunk & line subsets).
- [ ] Diff viewer: hunk buttons + line multi-select; direction rule; discard confirmation; refresh coupling.
- [ ] Staleness → typed failure, no silent recount.
- [ ] Corpus committed; TI-06 pure + integration green (integration proves git accepts every output).
- [ ] Reviewer script clean. One task = one PR linking **T-06**.
```
