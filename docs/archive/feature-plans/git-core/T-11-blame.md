# T-11 — Blame / Inline Annotations — Implementation Plan

**Task ID:** T-11 · **Milestone:** M4 (audit 2.10) · **Priority:** P1 · **Depends on:** T-01.
**Branch:** `plan/T-11-blame` → implement on `feat/T-11-blame` off `main`.

> **Source of truth:** §T-11 of the Master Doc, §TI-11 of the Test Strategy.

---

## 0. Context

No blame today. LibGit2Sharp's `repo.Blame` gives hunk→commit mappings; this task exposes per-line blame via
`IGitService`, caches it bounded + watcher-invalidated, and renders a toggleable AvaloniaEdit gutter.

### What you can rely on

| Fact | Where |
|---|---|
| AvaloniaEdit + TextMate already referenced (11.1.0) | `App/Mainguard.App.Shell.csproj:18,23` |
| `RepositoryWatcher.RepositoryChanged` event fires on repo state change (debounced, ignore-aware) | `RepositoryWatcher.cs:32,166` |
| Async/`Task.Run` + `IsBusy` + typed-exception conventions | `RepoDashboardViewModel` |
| `WeakReferenceMessenger` available for cross-VM selection (commit selection) | CommunityToolkit.Mvvm |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Models/BlameLine.cs` |
| **Edit** | `IGitService.cs` + `GitServices.cs` (`GetBlame` + bounded cache) |
| **Edit** | file/diff viewer VM + view (gutter margin, toggle, cancellation) |
| **Create** | `Mainguard.Tests/GitServiceBlameTests.cs` |

---

## 2. Contract (must exist exactly)

```csharp
// Mainguard.Agents/Models/BlameLine.cs
public sealed class BlameLine
{
    public int LineNumber { get; init; }        // 1-based, current file
    public string Sha { get; init; } = "";
    public string ShortSha { get; init; } = "";  // 8 chars
    public string AuthorName { get; init; } = "";
    public DateTimeOffset When { get; init; }
    public string Summary { get; init; } = "";    // commit MessageShort
}
// IGitService
IReadOnlyList<BlameLine> GetBlame(string repoPath, string path, string? startingSha = null);
```

---

## 3. Implementation

### 3.1 `GetBlame`

```csharp
public IReadOnlyList<BlameLine> GetBlame(string repoPath, string path, string? startingSha = null) =>
    ExecuteWithRepo(repoPath, repo =>
    {
        // Guard the revision: path missing at that commit -> typed throw naming the path.
        var opts = new BlameOptions { StartingAt = startingSha ?? "HEAD" };
        HunkCollection hunks;
        try { hunks = repo.Blame(path, opts); }
        catch (LibGit2SharpException ex) { throw new GitOperationException($"Cannot blame '{path}' at the requested revision.", ex); }

        var lines = new List<BlameLine>();
        foreach (var h in hunks)
        {
            var c = h.FinalCommit;
            for (int i = 0; i < h.LineCount; i++)
                lines.Add(new BlameLine
                {
                    LineNumber = h.FinalStartLineNumber + i,   // NB: libgit2 FinalStartLineNumber is 0-based; +1 if your render expects 1-based — pin with test #1
                    Sha = c.Sha,
                    ShortSha = c.Sha.Substring(0, 8),
                    AuthorName = c.Author.Name,
                    When = c.Author.When,
                    Summary = c.MessageShort,
                });
        }
        return lines;
    });
```

> **Line-number base:** verify against test #1 whether `FinalStartLineNumber` is 0- or 1-based in the pinned
> LibGit2Sharp version and normalize to **1-based** in `BlameLine.LineNumber`. The test asserts exact
> line→sha mapping, so this is pinned, not guessed.

### 3.2 Bounded, watcher-invalidated cache

- Cache key `(repoPath, path, headSha)`; bounded LRU (~32 entries).
- Invalidate on `RepositoryWatcher.RepositoryChanged` (clear or drop entries for the changed repo).
- Never unbounded (rejection trigger).

### 3.3 UI

- Toggleable AvaloniaEdit **gutter margin**: `author · shortSha · relative date`, alternating dim shade on
  commit boundaries; tooltip = full SHA + summary; click → select that commit in the timeline via
  `WeakReferenceMessenger`.
- Compute on `Task.Run` with a `CancellationToken` that is **cancelled on file switch** (rapid switching
  must never render a stale gutter). Spinner in the gutter header while computing.

---

## 4. Invariants / Rejection triggers

**MUST:** per-line mapping correct for 3 disjoint-edit commits (test #1); blame never blocks the UI thread;
rapid file switching never renders a stale gutter (cancellation).
**Rejection:** blame run synchronously in a property getter; unbounded cache.

---

## 5. Test contract — `GitServiceBlameTests.cs` (TI-11)

1. `GetBlame_ShouldMapLinesToCommits` — 3 commits touching **disjoint** lines → each line's `Sha` correct.
2. `GetBlame_StartingAtPriorCommit_ShouldIgnoreNewerCommit`.
3. `GetBlame_ShouldThrowTyped_OnPathMissingAtRevision` — `GitOperationException` naming the path.
4. `GetBlame_ShouldInvalidate_OnHeadChange` — same call after a new commit reflects it (cache invalidation).
5. ViewModel (TI-00): `BlameGutter_ShouldCancelStaleLoad_OnFileSwitch` — TCS-held fake blame; assert no stale
   render callback after switching files.

---

## 6. Reviewer script / Definition of done

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~Blame"
```

- [ ] `BlameLine` + `GetBlame` with typed revision guard and 1-based line numbers.
- [ ] Bounded LRU cache invalidated on `RepositoryChanged`.
- [ ] AvaloniaEdit gutter toggle; `Task.Run` + cancellation on file switch; click→commit selection.
- [ ] TI-11 green (incl. cancellation VM test). One task = one PR linking **T-11**.
```
