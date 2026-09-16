# T-07 — Worktree Porcelain Backend + Arbitrary-Commit Diff Entry Points — Implementation Plan

**Task ID:** T-07 · **Milestone:** M3 (roadmap 4.5 remainder) · **Priority:** P0 (agent-phase backbone)
**Depends on:** T-01. **Consumed by:** T-08 (rebase `RunGit` maturity), T-21 (worktree UI).
**Branch:** `plan/T-07-worktree-porcelain` → implement on `feat/T-07-worktree-porcelain` off `main`.

> **Source of truth:** §T-07 of the Master Doc, §TI-07 of the Test Strategy. Binding contract/edge/invariants below.

---

## 0. Context — replace the libgit2 worktree stubs with CLI porcelain

Today `ListWorktrees` returns `IEnumerable<string>` and `Add`/`RemoveWorktree` call the **libgit2**
`repo.Worktrees` API (`GitServices.cs:1141-1159`). Per the locked policy split (G-7), **worktrees are a
git-CLI feature** — libgit2's worktree API is a locked *no*. This task rewrites all worktree operations to
drive the `git worktree` porcelain via the hardened `RunGit` family, returns a rich `WorktreeItem`, and adds
prune. It also surfaces the already-existing arbitrary-commit diff methods in the commit context menu.

**Breaking change (intended):** `ListWorktrees` return type becomes `IReadOnlyList<WorktreeItem>`. There is
one App call site to update: `BranchBrowserViewModel.cs:540` (`AddWorktree(...)` — its signature also gains a
`createBranch` bool).

### What you can rely on

| Fact | Where |
|---|---|
| `internal static (int Code, string Out, string Err) RunGit(string repoPath, params string[] args)` and an env/ct overload — ArgumentList, no shell, `GIT_TERMINAL_PROMPT=0`, stderr captured | `GitServices.cs:626-667` |
| `private void RunGitChecked(string repoPath, params string[] args)` — throws `GitOperationException` with stderr on non-zero | `GitServices.cs:741` |
| Diff methods already exist: `GetDiffAgainstCommit(repoPath, commitSha, filePath)`, `GetBranchDiffAgainstWorkingTree(repoPath, branchName)` | `GitServices.cs` |
| Existing worktree call site to migrate | `BranchBrowserViewModel.cs:540` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Models/WorktreeItem.cs` |
| **Edit** | `Mainguard.Agents/Services/IGitService.cs` + `GitServices.cs` (replace 3 methods, add `PruneWorktrees`) |
| **Optional create** | `Mainguard.Agents/Services/WorktreePorcelainParser.cs` (extract stanza parser → pure + unit-testable) |
| **Edit** | `Mainguard.App.Shell/ViewModels/BranchBrowserViewModel.cs:540` (new `AddWorktree` signature) |
| **Edit** | `Mainguard.App.Shell/ViewModels/CommitTimelineViewModel.cs` ("Diff working tree against this commit" menu item) |
| **Create** | `Mainguard.Tests/GitServiceWorktreeTests.cs` (integration, `RequiresGitCli`), optional `WorktreePorcelainParserTests.cs` |

---

## 2. Contract (must exist exactly)

```csharp
// Mainguard.Agents/Models/WorktreeItem.cs
namespace Mainguard.Agents.Models;
public sealed class WorktreeItem
{
    public string Path { get; init; } = "";
    public string? HeadSha { get; init; }
    public string? Branch { get; init; }   // friendly name, null when detached
    public bool IsDetached { get; init; }
    public bool IsLocked { get; init; }
    public bool IsMain { get; init; }       // first stanza in porcelain output
}

// IGitService — REPLACE IEnumerable<string> ListWorktrees with:
IReadOnlyList<WorktreeItem> ListWorktrees(string repoPath);
void AddWorktree(string repoPath, string worktreePath, string branchName, bool createBranch); // extend existing
void RemoveWorktree(string repoPath, string worktreePath, bool force);                        // extend existing
void PruneWorktrees(string repoPath);
```

---

## 3. Service implementation (all CLI)

### 3.1 `ListWorktrees` — parse `--porcelain`

```csharp
public IReadOnlyList<WorktreeItem> ListWorktrees(string repoPath)
{
    var (code, output, err) = RunGit(repoPath, "worktree", "list", "--porcelain");
    if (code != 0) throw new GitOperationException($"git worktree list failed: {err}");
    return WorktreePorcelainParser.Parse(output);
}
```

**Porcelain format** — stanzas separated by blank lines, one attribute per line:

```
worktree /abs/path/main
HEAD 0f1e2d...
branch refs/heads/main

worktree /abs/path/feature
HEAD aa11bb...
branch refs/heads/feature

worktree /abs/path/detached
HEAD cc22dd...
detached

worktree /abs/path/locked
HEAD ee33ff...
branch refs/heads/wip
locked optional reason text
```

Parser rules:
- A `worktree <path>` line **starts a new stanza**. The path is the rest of the line verbatim (porcelain is
  line-oriented — paths with spaces are **not** quoted, so do **not** split on spaces; take everything after
  `"worktree "`).
- `HEAD <sha>` → `HeadSha`. `branch refs/heads/<name>` → `Branch` = `<name>` (strip `refs/heads/`).
- bare `detached` line → `IsDetached = true`, `Branch = null`.
- `locked` (optionally `locked <reason>`) → `IsLocked = true`.
- The **first** stanza → `IsMain = true`; all others `false`.
- Blank line ends a stanza. Emit stanzas in file order.

Extract this into a pure `static IReadOnlyList<WorktreeItem> WorktreePorcelainParser.Parse(string porcelain)`
so it unit-tests without a repo (TI-07 case 7).

### 3.2 `AddWorktree`

```csharp
public void AddWorktree(string repoPath, string worktreePath, string branchName, bool createBranch)
{
    // git worktree add [-b <branch>] <path> [<branch>]
    var args = new List<string> { "worktree", "add" };
    if (createBranch) { args.Add("-b"); args.Add(branchName); args.Add(worktreePath); }
    else              { args.Add(worktreePath); args.Add(branchName); }
    RunGitChecked(repoPath, args.ToArray());
}
```

- `createBranch: true` → `-b <branch> <path>` (creates the branch).
- `createBranch: false` → `<path> <branch>` (checks out an existing branch).
- Adding a branch already checked out elsewhere → git exits non-zero → `RunGitChecked` throws typed with
  git's stderr (edge matrix / TI-07 #4).

### 3.3 `RemoveWorktree` / `PruneWorktrees`

```csharp
public void RemoveWorktree(string repoPath, string worktreePath, bool force)
{
    var args = new List<string> { "worktree", "remove" };
    if (force) args.Add("--force");
    args.Add(worktreePath);
    RunGitChecked(repoPath, args.ToArray());
}

public void PruneWorktrees(string repoPath) => RunGitChecked(repoPath, "worktree", "prune");
```

Update `IGitService` (G-10). Update the App call site `BranchBrowserViewModel.cs:540` to pass the new
`createBranch` argument (choose the correct value for that flow — it currently adds a worktree for an
existing branch, so `createBranch: false`).

### 3.4 Diff entry points

`GetDiffAgainstCommit` / `GetBranchDiffAgainstWorkingTree` already exist. Add a commit-context-menu command
**"Diff working tree against this commit"** in `CommitTimelineViewModel` that reuses the diff viewer with
`GetDiffAgainstCommit`. (This is a thin wiring task, not new engine code.)

---

## 4. Edge-case matrix / Invariants

| Case | Required behavior |
|---|---|
| detached worktree | `Branch == null`, `IsDetached == true` |
| locked worktree | `IsLocked == true` |
| worktree path containing spaces | parsed correctly (line-oriented porcelain — no space-splitting) |
| add on an already-checked-out branch | typed failure carrying git's stderr |
| remove with dirty tree, `force == false` | typed failure; `force == true` succeeds |
| prune after manually deleting a worktree dir | metadata cleaned, `ListWorktrees` shrinks |

**MUST:** all four methods CLI-driven via the `RunGit` family (G-7 — libgit2 worktree API is a locked no);
parse the **porcelain** format only (never the human-readable `worktree list`); stderr surfaced in typed
exceptions.
**Rejection triggers:** parsing `worktree list` **without** `--porcelain`; any `repo.Worktrees` usage for
add/remove/list.

---

## 5. Test contract — `GitServiceWorktreeTests.cs` (integration, `RequiresGitCli`)

Uses `TempRepoFixture`. `IGitService git = new GitService();`

| # | Test | Assertion |
|---|---|---|
| 1 | `ListWorktrees_ShouldParseMainAndLinked_WithBranchAndSha` | after adding one worktree → 2 items; first `IsMain`; branch names + `HeadSha` correct. |
| 2 | `ListWorktrees_ShouldParseDetached_AndLocked` | arrange via the service's `AddWorktree` + a `git worktree lock`/`--detach` run through the test's own `RunGit`; assert flags. |
| 3 | `AddWorktree_WithCreateBranch_ShouldCreateBranchAndDir` | `createBranch:true` → new branch exists + directory created. |
| 4 | `AddWorktree_OnCheckedOutBranch_ShouldThrowTyped` | adding the currently-checked-out branch → `GitOperationException`. |
| 5 | `RemoveWorktree_Dirty_ShouldThrowWithoutForce_AndSucceedWithForce` | dirty linked worktree: `force:false` throws; `force:true` removes. |
| 6 | `PruneWorktrees_ShouldCleanMetadata_AfterManualDelete` | delete a worktree dir on disk, `PruneWorktrees`, `ListWorktrees` shrinks. |
| 7 | `WorktreePorcelainParserTests` (pure, if extracted) | paths with spaces, missing optional fields, detached/locked stanzas. |

---

## 6. Reviewer script / Definition of done

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~Worktree"
grep -n "repo.Worktrees" Mainguard.Agents/Services/GitServices.cs      # -> 0 hits (libgit2 worktree API gone)
grep -n "worktree.*list" Mainguard.Agents/Services/GitServices.cs      # -> uses --porcelain
```

- [ ] `WorktreeItem.cs`; `ListWorktrees`→`IReadOnlyList<WorktreeItem>`; `AddWorktree(+createBranch)`; `RemoveWorktree(+force)`; `PruneWorktrees`.
- [ ] Pure porcelain parser; all four methods via `RunGit`/`RunGitChecked`; App call site migrated.
- [ ] "Diff working tree against this commit" menu item wired.
- [ ] Every edge row + invariant; TI-07 green. One task = one PR linking **T-07**.
```
