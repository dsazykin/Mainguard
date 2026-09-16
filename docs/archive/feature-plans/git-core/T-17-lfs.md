# T-17 — Git LFS — Implementation Plan

**Task ID:** T-17 · **Milestone:** M4 · **Priority:** P1 · **Depends on:** T-14 (auth helper plumbing for LFS endpoints).
**Branch:** `plan/T-17-lfs` → implement on `feat/T-17-lfs` off `main`.

> **Source of truth:** §T-17 of the Master Doc + strategy §C-2.6, §TI-17 of the Test Strategy.

---

## 0. Context

No LFS today. LFS is **entirely a CLI concern** — shell out via `RunGit`/`RunGitChecked`. Probe availability
once and degrade gracefully when `git-lfs` isn't installed. Credentials for LFS endpoints flow through the
T-14 auth plumbing.

### What you can rely on

| Fact | Where |
|---|---|
| `RunGit(repoPath, params args)` returns `(Code, Out, Err)` — use for the availability probe | `GitServices.cs:626` |
| `RunGitChecked` for the mutating LFS ops | `GitServices.cs:741` |
| Diff viewer shows blob/patch text — LFS pointer text must be replaced with a friendly summary | `DiffViewerViewModel` |
| Auth plumbing (T-14) covers LFS endpoint credentials | `feat/T-14` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Services/ILfsService.cs` + `LfsService.cs` |
| **Create** | `Mainguard.Agents/Services/LfsPointer.cs` (pure pointer detection) |
| **Edit** | diff viewer — show "LFS object (size)" instead of pointer text |
| **Create** | LFS sidebar panel / per-repo settings toggle |
| **Create** | `Mainguard.Tests/GitServiceLfsTests.cs` (`RequiresGitLfs`), `LfsPointerTests.cs` (pure) |

---

## 2. Contract

```csharp
public interface ILfsService
{
    bool IsAvailable(string repoPath);                 // cached probe of `git lfs version`
    void Install(string repoPath);                     // lfs install --local
    void Track(string repoPath, string pattern);       // lfs track <pattern>
    void Untrack(string repoPath, string pattern);     // lfs untrack <pattern>
    IReadOnlyList<string> ListFiles(string repoPath);  // lfs ls-files
    void Pull(string repoPath);                         // lfs pull
    void Prune(string repoPath, bool dryRun);          // lfs prune [--dry-run]
}
```

---

## 3. Implementation

- **Availability probe (cached):** `RunGit(repoPath, "lfs", "version")`; `Code == 0` → available. Cache the
  result (per process, or per repo). Every mutating method first checks `IsAvailable`; if false, throw a
  typed `GitOperationException("Git LFS is not installed.")` — **never attempt the op** (TI-17 #4).
- All ops via `RunGitChecked`: `lfs install --local`, `lfs track <pat>`, `lfs untrack <pat>`, `lfs ls-files`,
  `lfs pull`, `lfs prune` (add `--dry-run` when `dryRun`; the UI shows the dry-run result and **confirms**
  before a real prune).
- **Pointer detection (pure `LfsPointer.IsPointer(string content)`):** true iff the content starts with
  `version https://git-lfs.github.com/spec/v1`. The diff viewer uses this to render "LFS object (size)"
  rather than the raw pointer text. Handle malformed/partial variants (return false).
- **UI:** LFS sidebar panel (tracked patterns from `.gitattributes` `filter=lfs`, ls-files, pull, prune with
  dry-run+confirm) + a per-repo LFS toggle in settings.

---

## 4. Test contract — TI-17 (`RequiresGitLfs`, skip when absent)

1. `LfsTrack_ShouldWriteGitattributes_AndCommitPointer` — track `*.bin`, commit a binary →
   `git show HEAD:file.bin` starts with the pointer header.
2. `LfsLsFiles_ShouldListTrackedObject`; `Untrack_ShouldRoundTrip`.
3. `PointerDetection_ShouldIdentifyPointerText` (pure: header prefix + malformed variants).
4. `LfsUnavailable_ShouldDegradeGracefully` — probe false → feature methods throw typed "LFS not installed",
   never attempt the op.

---

## 5. Reviewer script / Definition of done

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~Lfs"
```

- [ ] `ILfsService`/`LfsService` (all CLI) + cached availability probe + graceful degrade.
- [ ] Pure `LfsPointer.IsPointer`; diff viewer shows "LFS object (size)".
- [ ] LFS panel + per-repo toggle; prune dry-run+confirm.
- [ ] TI-17 gpg-... er, `RequiresGitLfs`-gated tests + pure pointer tests green. One PR linking **T-17**.
```
