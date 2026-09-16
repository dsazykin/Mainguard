# T-16 — Submodules — Implementation Plan

**Task ID:** T-16 · **Milestone:** M4 · **Priority:** P1 · **Depends on:** T-01.
**Branch:** `plan/T-16-submodules` → implement on `feat/T-16-submodules` off `main`.

> **Source of truth:** §T-16 of the Master Doc + strategy §C-2.5, §TI-16 of the Test Strategy.

---

## 0. Context

No submodule support today. Reads come from LibGit2Sharp `repo.Submodules`; all **mutations go through the
git CLI** (`RunGitChecked`) per policy split G-7.

### What you can rely on

| Fact | Where |
|---|---|
| `repo.Submodules` → each has `Path`, `Url`, `HeadCommitId`, `RetrieveStatus()` → `SubmoduleStatus` flags | LibGit2Sharp |
| `RunGitChecked` for `submodule` porcelain | `GitServices.cs:741` |
| Typed exceptions; async command + sidebar-panel conventions | throughout |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Models/SubmoduleItem.cs` (+ `SubmoduleState` enum) |
| **Edit** | `IGitService.cs` + `GitServices.cs` (list + init/update/sync) |
| **Create** | `Mainguard.Agents/Services/SubmoduleStatusMapper.cs` (pure flag→state) |
| **Create** | Submodules sidebar panel (VM + view) |
| **Create** | `Mainguard.Tests/GitServiceSubmoduleTests.cs`, `SubmoduleStatusMapperTests.cs` (pure) |

---

## 2. Contract

```csharp
// Mainguard.Agents/Models/SubmoduleItem.cs
public enum SubmoduleState { Uninitialized, UpToDate, Modified, Dirty }
public sealed class SubmoduleItem
{
    public string Path { get; init; } = "";
    public string Url { get; init; } = "";
    public string? HeadSha { get; init; }
    public SubmoduleState Status { get; init; }
}

// IGitService additions
IReadOnlyList<SubmoduleItem> GetSubmodules(string repoPath);
void UpdateSubmodules(string repoPath);                 // submodule update --init --recursive
void UpdateSubmoduleRemote(string repoPath, string path); // submodule update --remote <path>
void SyncSubmodules(string repoPath);                   // submodule sync
```

---

## 3. Implementation

- `GetSubmodules`: `ExecuteWithRepo` → map `repo.Submodules` to `SubmoduleItem`, `Status` via
  `SubmoduleStatusMapper.Map(sm.RetrieveStatus())`. The mapper is **pure** (`SubmoduleStatus` flags →
  `SubmoduleState`): not-in-workdir/uninitialized → `Uninitialized`; index/head/wd all in sync →
  `UpToDate`; new commits in the submodule (`WdIndexModified`/`WdWdModified`) → `Modified`; untracked/dirty
  working tree inside → `Dirty`. Cover all flag combinations in the pure test.
- Mutations via `RunGitChecked`:
  - `UpdateSubmodules` → `submodule`, `update`, `--init`, `--recursive`.
  - `UpdateSubmoduleRemote` → `submodule`, `update`, `--remote`, `<path>`.
  - `SyncSubmodules` → `submodule`, `sync`.
- **UI:** Submodules sidebar panel listing each with status + actions (init/update, update-remote, sync,
  "open as its own repo"). Async commands, typed errors.

**Test-only flag:** superproject/submodule fixtures need `-c protocol.file.allow=always` **in test
arrangement only**. Production code must never set it — rejection trigger; reviewer greps
`grep -n "protocol.file.allow" Mainguard.Agents/` → 0 hits (do **not** automate that as a test).

---

## 4. Test contract — TI-16 (`RequiresGitCli`)

1. `Submodules_FreshClone_ShouldReportUninitialized_ThenUpToDateAfterInit`.
2. `Submodule_InnerCommit_ShouldFlagSuperprojectModified`.
3. `SubmoduleStatusMapping_ShouldCoverAllStates` (pure mapper over flag combinations).

---

## 5. Reviewer script / Definition of done

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~Submodule"
grep -n "protocol.file.allow" Mainguard.Agents/        # -> 0 hits (production never sets it)
```

- [ ] `SubmoduleItem`/`SubmoduleState` + list + init/update/sync (mutations via CLI).
- [ ] Pure status mapper covering all flags.
- [ ] Submodules panel with async actions + typed errors.
- [ ] TI-16 green; production free of `protocol.file.allow`. One PR linking **T-16**.
```
