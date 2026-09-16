# T-03 — Conflict Index Plumbing — Implementation Plan

**Task ID:** T-03
**Milestone:** M1 remainder (audit fix 1.1, part 2)
**Priority:** CRITICAL
**Depends on:** nothing (runs in parallel with T-02)
**Consumed by:** T-04 (conflict-resolution UI), which needs both this and T-02.
**Branch:** `plan/T-03-conflict-plumbing` (this doc) → implement on a fresh `feat/T-03-conflict-plumbing` off `main`.

> **Source of truth:** §T-03 of `docs/planning/Mainguard_Master_Implementation_Document.md` and §TI-03 of
> `docs/testing/Mainguard_Test_Implementation_Strategy.md`, expanded here into an implement-in-one-pass spec.
> The Master Doc **Contract, Invariants, and Edge-case matrix are binding** and are reproduced below.

---

## 0. Context — why this task exists and what is already on `main`

A working conflict resolver already exists (`Mainguard.App.Shell/ViewModels/ConflictResolverWindowViewModel.cs`),
but it feeds itself by **parsing `<<<<<<< / ======= / >>>>>>>` markers out of the working-tree file**. That
approach never sees the true common ancestor, so it can't do real 3-way classification. T-02 builds the pure
chunker that needs *base/ours/theirs* text; **this task is the bridge that gets those three texts out of Git
and writes resolutions back**, using `repo.Index.Conflicts` (the merge index stages 1/2/3) as the single
source of truth — **never** the working-tree markers.

This task is **Core-only service plumbing**. It adds four methods to `IGitService`/`GitService` and one
model. It does **not** touch the App layer (that's T-04) and does **not** build any chunking (that's T-02).

### What you can rely on already existing

| Fact | Detail | Where |
|---|---|---|
| Handle discipline | All LibGit2Sharp access goes through `ExecuteWithRepo(path, Action<Repository>)` or `T ExecuteWithRepo<T>(path, Func<Repository,T>)`. Never `new Repository(...)` yourself in a public method. | `GitServices.cs:30-51` |
| Conflicts are already reached | `repo.Index.Conflicts.Any()` is used to detect post-pull conflicts. | `GitServices.cs:411` |
| Typed exceptions exist | `Mainguard.Agents/Exceptions/`: `GitOperationException`, `MergeConflictException`, `MainguardException` (base), etc. Never `throw new Exception(...)` (invariant G-1). | `Mainguard.Agents/Exceptions/` |
| Two-parent merge commit is automatic | During a merge, `.git/MERGE_HEAD` exists; a plain `repo.Commit(msg, sig, sig)` auto-adds it as the 2nd parent. `Commit(...)`, `GetMergeMessage(...)`, `IsMergeInProgress(...)` already exist. | `GitServices.cs:320-491` |
| Model convention | `Mainguard.Agents/Models/*.cs`, `namespace Mainguard.Agents.Models;`, `public sealed class`, `{ get; init; }` props with `= ""` defaults. | `Mainguard.Agents/Models/` |
| Policy split (G-7) | Conflict **reads** and the **stage-on-resolve** go through LibGit2Sharp (this is a read/status/commit concern). Do **not** shell out to `git` for any method in this task. | Master Doc §2, G-7 |

---

## 1. Files to create / modify

| Action | Path | Purpose |
|---|---|---|
| **Create** | `Mainguard.Agents/Models/ConflictedFile.cs` | The `ConflictedFile` DTO. |
| **Edit** | `Mainguard.Agents/Services/IGitService.cs` | Add the four method signatures (G-10: interface changes in the same PR). |
| **Edit** | `Mainguard.Agents/Services/GitServices.cs` | Implement the four methods. |
| **Create** | `Mainguard.Tests/GitServiceConflictTests.cs` | The 9 integration cases from §7 (TI-03). |

Place the four new methods together in `GitServices.cs` in a clearly commented `// --- Conflict resolution ---`
region, near the existing merge/rebase methods (around `GetMergeMessage`, ~line 483), for reviewer locality.

---

## 2. Contract (must exist exactly — copied verbatim from the Master Doc)

### 2.1 `Mainguard.Agents/Models/ConflictedFile.cs`

```csharp
namespace Mainguard.Agents.Models;

public sealed class ConflictedFile
{
    public string Path { get; init; } = "";
    public bool HasBase   { get; init; }   // false on add/add conflicts
    public bool HasOurs   { get; init; }   // false when deleted on our side
    public bool HasTheirs { get; init; }   // false when deleted on their side
}
```

### 2.2 Added to `IGitService` **and** `GitService`

```csharp
IReadOnlyList<ConflictedFile> GetConflicts(string repoPath);
(string BaseText, string OursText, string TheirsText) GetConflictBlobs(string repoPath, string path);
void ResolveConflict(string repoPath, string path, string mergedContent);
bool HasUnresolvedConflicts(string repoPath);
```

**Binding:** method names, parameter names/order, the value-tuple element names (`BaseText`, `OursText`,
`TheirsText`), and return types. `Base`==ancestor (stage 1), `Ours`==stage 2, `Theirs`==stage 3.

---

## 3. LibGit2Sharp conflict API reference (exactly what you will call)

```csharp
// repo.Index.Conflicts is a ConflictCollection (enumerable + string indexer).
foreach (Conflict c in repo.Index.Conflicts) { ... }
Conflict? c = repo.Index.Conflicts[path];   // string indexer; returns null when the path is not conflicted
```

A `Conflict` exposes three `IndexEntry?` stages (any may be `null`):

| Property | Stage | Meaning | Null when |
|---|---|---|---|
| `c.Ancestor` | 1 | common base (merge-base) | add/add (no common ancestor) |
| `c.Ours` | 2 | our side (HEAD) | file deleted on our side (modify/delete) |
| `c.Theirs` | 3 | their side (MERGE_HEAD) | file deleted on their side |

Each non-null `IndexEntry` has `.Path` (repo-relative, **forward slashes**) and `.Id` (`ObjectId`).
Read a stage's text via:

```csharp
string text = repo.Lookup<Blob>(entry.Id).GetContentText();
```

`GetContentText()` returns the blob decoded with auto-detected encoding (fine for text; for binary it may
mangle bytes but **must not throw** — UI gating is T-04's job, per the edge matrix).

Staging a conflicted path clears all three stage entries for it:

```csharp
Commands.Stage(repo, path);   // after writing the resolved file to disk
```

---

## 4. Implementation

### 4.1 `GetConflicts`

```csharp
public IReadOnlyList<ConflictedFile> GetConflicts(string repoPath) =>
    ExecuteWithRepo(repoPath, repo =>
        repo.Index.Conflicts
            .Select(c => new ConflictedFile
            {
                // At least one stage is always present; prefer a non-null one for the path.
                Path      = c.Ours?.Path ?? c.Theirs?.Path ?? c.Ancestor!.Path,
                HasBase   = c.Ancestor != null,
                HasOurs   = c.Ours     != null,
                HasTheirs = c.Theirs   != null,
            })
            .OrderBy(f => f.Path, StringComparer.Ordinal)   // stable UI ordering
            .ToList());
```

Return type is `IReadOnlyList<ConflictedFile>`; `List<T>` satisfies it. Empty repo/clean tree → empty list.

### 4.2 `GetConflictBlobs`

```csharp
public (string BaseText, string OursText, string TheirsText) GetConflictBlobs(string repoPath, string path) =>
    ExecuteWithRepo(repoPath, repo =>
    {
        var c = repo.Index.Conflicts[path]
            ?? throw new GitOperationException($"No conflict recorded for '{path}'.");

        string Read(IndexEntry? e) => e == null ? "" : repo.Lookup<Blob>(e.Id).GetContentText();

        return (Read(c.Ancestor), Read(c.Ours), Read(c.Theirs));
    });
```

- Non-conflicted path → **typed** `GitOperationException` naming the path (never `NullReferenceException`,
  never a bare `Exception`).
- Missing stage (add/add base, modify/delete side) → that element is `""`.

### 4.3 `ResolveConflict`

```csharp
public void ResolveConflict(string repoPath, string path, string mergedContent) =>
    ExecuteWithRepo(repoPath, repo =>
    {
        var fullPath = System.IO.Path.Combine(repo.Info.WorkingDirectory, path);
        var dir = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            System.IO.Directory.CreateDirectory(dir);      // subdirectory conflicts must work

        // UTF-8 without BOM (matches Git's expectations; avoids a spurious BOM diff).
        System.IO.File.WriteAllText(fullPath, mergedContent, new System.Text.UTF8Encoding(false));

        // Staging a conflicted path clears its stage-1/2/3 entries in the index.
        Commands.Stage(repo, path);
    });
```

- Writes the working-tree file **and** stages it. It does **not** commit — commit is a separate, explicit
  user action (rejection trigger if this method commits).
- `path` arrives with forward slashes (from `GetConflicts`); `Path.Combine` handles it on all platforms.
  Pass the original `path` to `Commands.Stage` (libgit2 wants the forward-slash repo-relative form).

### 4.4 `HasUnresolvedConflicts`

```csharp
public bool HasUnresolvedConflicts(string repoPath) =>
    ExecuteWithRepo(repoPath, repo => repo.Index.Conflicts.Any());
```

### 4.5 Interface (G-10)

Add the four signatures to `IGitService.cs` in the same PR, grouped near the merge/rebase members with a
`// Conflict resolution` comment. Add `using Mainguard.Agents.Models;` to the interface file if not already
present (it references `ConflictedFile`).

---

## 5. Edge-case matrix (binding — every row needs a test)

| Case | Required behavior |
|---|---|
| add/add conflict (no ancestor) | `HasBase == false`, `BaseText == ""` |
| modify/delete (theirs deleted) | `HasTheirs == false`, `TheirsText == ""` |
| `GetConflictBlobs` on a non-conflicted path | typed `GitOperationException`, message names the path |
| `ResolveConflict` then `Commit` during a merge | commit succeeds with **two parents** (libgit2 uses `MERGE_HEAD`) |
| `ResolveConflict` on a path in a subdirectory | works (parent dirs created, `/` separators for libgit2) |
| repo with no conflicts | `GetConflicts` → empty list; `HasUnresolvedConflicts` → false |
| binary conflicted file | `GetContentText` may mangle it — acceptable for v1, but **must not throw** |

---

## 6. Invariants (MUST — reviewers verify each)

1. Conflict enumeration reads **only** `repo.Index.Conflicts` — **no** working-tree marker parsing anywhere
   in this task. (`grep -n "<<<<<<<" Mainguard.Agents/Services/GitServices.cs` → 0 hits.)
2. `ResolveConflict` leaves the index with **zero** conflict entries for that path, and the working-tree file
   equal to `mergedContent`, and the staged blob equal to `mergedContent`.
3. After all conflicts are resolved, `HasUnresolvedConflicts` is `false` and a normal
   `Commit(repoPath, msg)` completes the merge with **2 parents**.
4. All four methods go through `ExecuteWithRepo` — no long-lived or self-opened `Repository` handle.
5. Every failure is a **typed** exception (`GitOperationException` for the non-conflict path); no bare
   `Exception`, no `NullReferenceException` leaking out.

---

## 7. Test contract — `Mainguard.Tests/GitServiceConflictTests.cs` (TI-03)

Repo integration tests using `TempRepoFixture` (T-01, already on `main`). Its `CreateConflict(rel,
oursContent, theirsContent)` builds two branches with conflicting edits and leaves HEAD on `ours`; to
produce an **index** with conflict stages you must then actually attempt the merge so libgit2 records stages
— do this with `_gitService.Merge(repoPath, theirsBranch)` wrapped in
`Assert.Throws<MergeConflictException>(...)` (Merge throws on conflict but leaves the conflicted index in
place). Instantiate `IGitService gitService = new GitService();`.

| # | Test name | Assertion |
|---|---|---|
| 1 | `GetConflicts_ShouldListConflictedPath_WithAllStagesPresent` | after `CreateConflict` + failed `Merge` → exactly one `ConflictedFile`, `Path` == the file, all three `Has*` true. |
| 2 | `GetConflicts_ShouldReturnEmpty_OnCleanRepo` | fixture with a commit, no merge → `GetConflicts` empty. |
| 3 | `GetConflictBlobs_ShouldReturnThreeDistinctTexts` | base/ours/theirs texts are the three distinct contents seeded by the fixture. |
| 4 | `GetConflictBlobs_ShouldThrowTyped_OnNonConflictedPath` | `Assert.Throws<GitOperationException>` whose message contains the path; on a path that isn't conflicted. |
| 5 | `GetConflictBlobs_AddAdd_ShouldReturnEmptyBase` | two branches each add the **same new file** with different content, merge → `BaseText == ""`, `HasBase == false`. |
| 6 | `GetConflictBlobs_ModifyDelete_ShouldFlagMissingSide` | ours modifies, theirs deletes (or vice versa) → the deleted side's `Has*` is false and its text is `""`. |
| 7 | `ResolveConflict_ShouldClearConflict_AndStageContent` | after `ResolveConflict(path, "merged")`: `repo.Index.Conflicts[path]` is null, workdir file text == `"merged"`, and the **staged** blob (index entry at stage 0) text == `"merged"`. |
| 8 | `ResolveConflict_ThenCommit_ShouldCreateTwoParentMergeCommit` | resolve the only file, `Commit(repoPath, "merge")`, then assert `repo.Head.Tip.Parents.Count() == 2`. |
| 9 | `HasUnresolvedConflicts_ShouldTrackResolutionProgress` | true right after the failed merge; false after resolving the only conflicted file. |

Include a subdirectory-path variant (edge matrix) either as an extra case or by seeding the conflict on
`sub/dir/file.txt`. Never weaken or skip a case to go green.

> **Note:** `Merge` already exists and throws `MergeConflictException` on conflicts (`GitServices.cs`).
> If the fixture's `CreateConflict` does not itself leave a conflicted index, driving the merge here does —
> assert `HasUnresolvedConflicts(repoPath)` is true at the top of the relevant tests as a precondition.

---

## 8. Acceptable variations (MAY) / Rejection triggers

**MAY:** richer `ConflictedFile` (additive per-stage `ObjectId`s); `GetConflictBlobs` via
`repo.ObjectDatabase` lookup instead of `repo.Lookup<Blob>`; LINQ vs. loops.

**Rejection triggers (any single hit → request changes):**
- Reading conflict content from the **working-tree file** / parsing `<<<<<<<` markers instead of index stages.
- `ResolveConflict` committing on its own.
- `new Repository(...)` outside `ExecuteWithRepo`.
- Any bare `throw new Exception(...)` / `throw new System.Exception(...)` (G-1).
- Shelling out to `git` for any of these methods (G-7 — these are libgit2 reads/commit-adjacent).

---

## 9. Reviewer verification script (must pass, < 2 min)

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~Conflict"     # green, 9+ cases

grep -n "<<<<<<<" Mainguard.Agents/Services/GitServices.cs               # -> 0 hits (no marker parsing)
grep -nE "throw new Exception\(|throw new System\.Exception" \
     Mainguard.Agents/Services/GitServices.cs                            # -> 0 hits
# confirm the interface carries the new surface:
grep -nE "GetConflicts|GetConflictBlobs|ResolveConflict|HasUnresolvedConflicts" \
     Mainguard.Agents/Services/IGitService.cs                            # -> 4 hits
```

---

## 10. Definition of done

- [ ] `ConflictedFile.cs` created with the exact contract.
- [ ] Four methods added to **both** `IGitService` and `GitService`, all via `ExecuteWithRepo`.
- [ ] Every row of §5 handled; every invariant in §6 holds.
- [ ] `GitServiceConflictTests.cs` contains all 9 cases (plus the subdirectory variant), all green.
- [ ] Reviewer script (§9) passes with the expected hit counts.
- [ ] No App-layer or CLI code touched. One task = one PR; PR links task **T-03** and lists the tests added.
```
