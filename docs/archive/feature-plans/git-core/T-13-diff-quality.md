# T-13 — Diff Quality: Intra-line, Syntax Highlighting, Whitespace, Images — Implementation Plan

**Task ID:** T-13 · **Milestone:** M4 (audit 2.16) · **Priority:** P1 · **Depends on:** T-06.
**Branch:** `plan/T-13-diff-quality` → implement on `feat/T-13-diff-quality` off `main`.

> **Source of truth:** §T-13 of the Master Doc (condensed; strategy §C-2.16), §TI-13 of the Test Strategy.

---

## 0. Context

The diff currently renders `SideBySideDiffRow { LeftLine, RightLine }` of plain text. Syntax highlighting via
**TextMate is already installed** in `DiffViewerView.axaml.cs` (`InstallTextMate`, grammar-by-extension). This
task adds the remaining four quality features: intra-line (word) highlighting, a whitespace-ignore toggle,
image diff, and a preference to disable syntax highlighting.

### What you can rely on

| Fact | Where |
|---|---|
| `SideBySideDiffRow { GitDiffLine LeftLine, RightLine }`; `GitDiffLine { Content, LineType }` | `Models/SideBySideDiffRows.cs`, `Models/GitDiffLine.cs` |
| TextMate installed on the editor; grammar chosen by file extension | `DiffViewerView.axaml.cs:23,79` |
| DiffPlex referenced (word diff via `Differ.CreateWordDiffs` / `WordChunker`) | `Core/Mainguard.Agents.csproj` |
| `PatchParser` (T-06) to parse `git diff -w` output | `feat/T-06` |
| `GetFileDiff(repoPath, path, isStaged)` + `RunGit` for `-w` | `GitServices.cs:269`, `:626` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Edit** | `Models/GitDiffLine.cs` — add `List<(int Start,int Length)> HighlightSpans` (additive) |
| **Create** | `Mainguard.Agents/Services/IntraLineDiff.cs` (pure word-span computation) |
| **Edit** | `IGitService.cs` + `GitServices.cs` — `GetFileDiff(..., bool ignoreWhitespace)` overload |
| **Edit** | `DiffViewerViewModel` + view — render spans, whitespace toggle, hide partial-staging in `-w` mode |
| **Create** | `Mainguard.App.Shell/Controls/ImageDiffControl.axaml(.cs)` + image detection helper |
| **Create** | `Mainguard.Tests/IntraLineDiffTests.cs` (pure), `GitServiceWhitespaceDiffTests.cs` (integration) |

---

## 2. Feature specs

### 2.1 Intra-line highlighting (pure)

- For each `Modified` line **pair** in `SideBySideDiffRows`, compute word-level spans via DiffPlex
  (`Differ.Instance.CreateWordDiffs(oldLine, newLine, ignoreWhitespace:false, new WordChunker())` — or
  equivalent) into `GitDiffLine.HighlightSpans` (`List<(int Start, int Length)>`, character offsets into
  `Content`). Render the differing runs darker.
- **Spans must never split a surrogate pair** (test with emoji/ZWJ): compute offsets on runes/text elements,
  not raw UTF-16 indices, or snap span boundaries outward to the nearest surrogate boundary. Pinned by test #3.

### 2.2 Syntax highlighting

- Already installed; add a **preference toggle** (`UserPreferences.SyntaxHighlightDiffs`, default true) to
  disable it (falls back to plain text). No new engine.

### 2.3 Whitespace toggle

```csharp
string GetFileDiff(string repoPath, string path, bool isStaged, bool ignoreWhitespace);
```

When `ignoreWhitespace` is on: `RunGit(repoPath, "diff", "-w", ...)` (staged variant adds `--cached`), parse
with `PatchParser`, render. **Partial staging is disabled in this mode** (offsets differ) — enforce by
**hiding the stage/discard buttons**, not by letting `git apply` fail. Whitespace-only change with `-w` →
zero hunks (test #4).

### 2.4 Image diff

- Detect: binary **and** image extension `{png,jpg,jpeg,gif,bmp,webp,ico}` → `ImageDiffControl` renders both
  blob revisions side-by-side with an opacity/swipe slider. Other binaries → "Binary file changed (old → new
  size)". Extraction of the two revisions uses `GetFileAtCommit`-style blob reads (or the existing binary
  path). The **image-candidate detection** is a pure helper (test #7 table).

---

## 3. Invariants / Test contract — TI-13

**MUST:** whitespace-only change with `-w` → zero hunks; a 5k-line diff keeps 60 FPS (profile in the PR);
partial staging genuinely unavailable in whitespace-ignored view.

Pure (`IntraLineDiffTests.cs`):
1. `HighlightSpans_SingleWordChange_ShouldSpanOnlyThatWord`.
2. `HighlightSpans_FullRewrite_ShouldSpanWholeLine`.
3. `HighlightSpans_ShouldNeverSplitSurrogatePairs` (`[Theory]` emoji/ZWJ).
7. `IsImageCandidate_ByExtensionAndBinaryFlag` (table).

Integration (`GitServiceWhitespaceDiffTests.cs`, `RequiresGitCli`):
4. `GetFileDiff_IgnoreWhitespace_ShouldYieldZeroHunks_ForIndentOnlyChange`.
5. `GetFileDiff_IgnoreWhitespace_ShouldKeepRealHunks_InMixedChange`.

ViewModel (TI-00):
6. `PartialStagingActions_ShouldBeHidden_InWhitespaceIgnoredMode`.

---

## 4. Reviewer script / Definition of done

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~IntraLineDiff|FullyQualifiedName~WhitespaceDiff"
```

- [ ] `HighlightSpans` on `GitDiffLine`; pure intra-line computation, surrogate-safe.
- [ ] Syntax-highlight preference toggle.
- [ ] `GetFileDiff(...,ignoreWhitespace)` overload; partial-staging buttons hidden in `-w` mode.
- [ ] Image diff control + pure detection helper; non-image binaries show size summary.
- [ ] TI-13 green; 5k-line perf noted in PR. One task = one PR linking **T-13**.
```
