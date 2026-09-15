# T-12 — File History & Line History — Implementation Plan

**Task ID:** T-12 · **Milestone:** M4 (audit 2.11) · **Priority:** P1
**Depends on:** T-06 (`PatchParser` reuse for line history).
**Branch:** `plan/T-12-file-history` → implement on `feat/T-12-file-history` off `main`.

> **Source of truth:** §T-12 of the Master Doc, §TI-12 of the Test Strategy.

---

## 0. Context

`repo.Commits.QueryBy(path)` and `repo.Diff.Compare<Patch>` are already used in `GitServices.cs`
(`:878`, `:279`, `:1204`). This task exposes a dedicated file-history view: the commits that touched a file
(rename-following), each version's blob text, adjacent-version diffs, and a v1 line-history filter that
reuses T-06's `PatchParser`.

### What you can rely on

| Fact | Where |
|---|---|
| `repo.Commits.QueryBy(path)` → `LogEntry { Commit, Path }` (rename tracking free) | `GitServices.cs:878` |
| `repo.Diff.Compare<Patch>(oldTree, newTree, new[]{path})` → `.Content` | `GitServices.cs:279,1204` |
| `PatchParser.Parse` (T-06) → `FilePatch`/`DiffHunk` for line-range intersection | `feat/T-06` |
| Existing diff control renders a unified/side-by-side diff string | `DiffViewerViewModel` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Models/FileVersion.cs` |
| **Edit** | `IGitService.cs` + `GitServices.cs` (three methods) |
| **Create** | `Mainguard.App.Shell/Views/FileHistoryView.axaml(.cs)` + `FileHistoryViewModel.cs` |
| **Edit** | staging-panel + diff-viewer context menus ("History of this file") |
| **Create** | `Mainguard.Tests/GitServiceFileHistoryTests.cs` |

---

## 2. Contract (must exist exactly)

```csharp
// Mainguard.Agents/Models/FileVersion.cs
public sealed class FileVersion
{
    public string Sha { get; init; } = "";
    public string PathAtCommit { get; init; } = "";
    public string MessageShort { get; init; } = "";
    public DateTimeOffset When { get; init; } = default;
    public string AuthorName { get; init; } = "";
}
// IGitService
IReadOnlyList<FileVersion> GetFileHistory(string repoPath, string path);            // newest-first, rename-following
string GetFileAtCommit(string repoPath, string sha, string path);                  // blob text; binary -> typed throw
string GetFileDiffBetweenCommits(string repoPath, string olderSha, string newerSha, string path);
```

---

## 3. Implementation

### 3.1 `GetFileHistory`

```csharp
public IReadOnlyList<FileVersion> GetFileHistory(string repoPath, string path) =>
    ExecuteWithRepo(repoPath, repo =>
        repo.Commits.QueryBy(path, new CommitFilter { SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time })
            .Select(e => new FileVersion
            {
                Sha = e.Commit.Sha,
                PathAtCommit = e.Path,                 // historical path — follows renames
                MessageShort = e.Commit.MessageShort,
                When = e.Commit.Author.When,
                AuthorName = e.Commit.Author.Name,
            })
            .ToList());
```

Newest-first (the default walk order with `Time`). `PathAtCommit` gives the file's name at each revision so
renames render correctly.

### 3.2 `GetFileAtCommit`

```csharp
public string GetFileAtCommit(string repoPath, string sha, string path) =>
    ExecuteWithRepo(repoPath, repo =>
    {
        var commit = repo.Lookup<Commit>(sha) ?? throw new GitOperationException($"Commit {sha} not found.");
        var blob = commit[path]?.Target as Blob
            ?? throw new GitOperationException($"'{path}' not found at {sha}.");
        if (blob.IsBinary) throw new GitOperationException($"'{path}' is binary at {sha}.");   // UI shows placeholder
        return blob.GetContentText();
    });
```

### 3.3 `GetFileDiffBetweenCommits`

```csharp
public string GetFileDiffBetweenCommits(string repoPath, string olderSha, string newerSha, string path) =>
    ExecuteWithRepo(repoPath, repo =>
    {
        var older = repo.Lookup<Commit>(olderSha) ?? throw new GitOperationException($"Commit {olderSha} not found.");
        var newer = repo.Lookup<Commit>(newerSha) ?? throw new GitOperationException($"Commit {newerSha} not found.");
        return repo.Diff.Compare<Patch>(older.Tree, newer.Tree, new[] { path }).Content;
    });
```

### 3.4 UI + line history

- `FileHistoryView`: left = virtualized version list (`GetFileHistory`); right = diff of the selected
  version vs its predecessor (`GetFileDiffBetweenCommits`) rendered by the existing diff control. Entry
  points: staging panel + diff viewer context menus.
- **Line history v1:** given a selected line range, filter the file history to versions whose patch
  (`GetFileDiffBetweenCommits` → `PatchParser.Parse`) has a hunk intersecting the range. **Document that this
  approximates `git log -L`** (it is not exact rename+move tracking).

---

## 4. Invariants / Test contract — TI-12

**MUST:** history spans renames with correct `PathAtCommit`; adjacent-version diff equals
`git diff a b -- path`; binary files never render garbage (typed throw → placeholder).

`GitServiceFileHistoryTests.cs`:
1. `GetFileHistory_ShouldReturnOnlyTouchingCommits_NewestFirst` — modified in commits 1,3,5 of 6.
2. `GetFileHistory_ShouldFollowRename_WithHistoricalPaths`.
3. `GetFileAtCommit_ShouldReturnBlobText_AndThrowTypedOnBinary`.
4. `GetFileDiffBetweenCommits_ShouldMatchTreeDiff` — equals a directly computed `repo.Diff.Compare<Patch>`.
5. `LineRangeFilter_ShouldKeepVersionsIntersectingRange` (pure, reuses `PatchParser`).

---

## 5. Reviewer script / Definition of done

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~FileHistory"
```

- [ ] `FileVersion` + three methods (rename-following history, blob-at-commit with binary guard, between-commits diff).
- [ ] `FileHistoryView` list + diff; context-menu entry points.
- [ ] Line-history filter via `PatchParser`, documented as `git log -L` approximation.
- [ ] TI-12 green. One task = one PR linking **T-12**.
```
