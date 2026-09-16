# T-21 — Profiles / Worktree UI / Clone Progress — Implementation Plan

**Task ID:** T-21 · **Milestone:** M5 (audit 2.17) · **Priority:** P2 · **Depends on:** T-07 (worktree porcelain backend).
**Branch:** `plan/T-21-profiles-worktree-clone` → implement on `feat/T-21-profiles-worktree-clone` off `main`.

> **Source of truth:** §T-21 of the Master Doc + strategy §D-2.17, §TI-21 of the Test Strategy.
> Three loosely-related sub-features bundled per the Master Doc; ship as one PR.

---

## 0. Context

- **Profiles:** switchable Git identities / preference sets; apply on repo open by writing **local** config.
- **Worktree UI:** a management panel over the T-07 `IReadOnlyList<WorktreeItem> ListWorktrees` /
  `AddWorktree(+createBranch)` / `RemoveWorktree(+force)` / `PruneWorktrees` backend.
- **Clone progress:** live progress on the existing `CloneDashboard` via LibGit2Sharp callbacks; a cancelled
  clone must delete the partial directory.

### What you can rely on

| Fact | Where |
|---|---|
| T-07 worktree service methods | `feat/T-07` |
| `CloneDashboardViewModel` + `CloneDashboardView` exist | `App/ViewModels/CloneDashboardViewModel.cs` |
| SQLite store (`AppDbContext`) for profiles | `Core/AppDbContext.cs` |
| `CloneOptions.OnTransferProgress` / `OnCheckoutProgress` (LibGit2Sharp) | LibGit2Sharp |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Models/GitProfile.cs` + `DbSet<GitProfile>` + migration; `ProfileService` (apply local config) |
| **Create** | `Mainguard.Agents/Models/CloneProgress.cs`; extend clone path with `IProgress<CloneProgress>` + cancellation |
| **Create** | Worktree management panel (VM + view) over T-07 methods |
| **Create** | `Mainguard.Tests/` profile-apply, clone-progress, worktree-VM cases |

---

## 2. Sub-feature specs

### 2.1 Profiles

- `GitProfile { Id, Name, UserName, UserEmail, ...prefs }` in SQLite.
- On repo open, `ProfileService.Apply(repoPath, profile)` writes `user.name`/`user.email` to **local** repo
  config only — **never global** (TI-21: point the global file at a temp path and assert it's untouched).

### 2.2 Worktree UI

- Panel listing `WorktreeItem`s (path, branch/detached, locked, main) with create (pick branch + path,
  `createBranch` toggle) / open / remove (force toggle) / prune, wired to T-07.
- Validation: creating a worktree on an **already-checked-out** branch → create button disabled (TI-21 VM
  validation).

### 2.3 Clone progress

- Report `CloneOptions.OnTransferProgress` (`ReceivedObjects`/`TotalObjects`) and `OnCheckoutProgress` into an
  `IProgress<CloneProgress>` driving the `CloneDashboard` bar. Progress must be **monotonic**.
- **Cancellation deletes the partial directory:** cancel via the transfer callback (return false / throw),
  then delete the partial clone dir. Assert the dir is gone (TI-21).

---

## 3. Test contract — TI-21

- profile apply writes **local** config only (global temp file untouched);
- clone progress reports **monotonic** `ReceivedObjects` and completes (bare-remote clone);
- **cancelled clone deletes the partial directory** (cancel via the transfer callback, assert dir gone);
- worktree panel VM validation (branch already checked out → create disabled).

---

## 4. Reviewer script / Definition of done

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~Profile|FullyQualifiedName~Clone|FullyQualifiedName~WorktreePanel"
grep -rn "Global\|--global\|SystemConfig" Mainguard.Agents/**/ProfileService.cs   # profile apply is LOCAL only
```

- [ ] `GitProfile` + migration + `ProfileService.Apply` (local config only).
- [ ] Worktree management panel over T-07 with checked-out-branch validation.
- [ ] Clone progress (monotonic) + cancellation-deletes-partial-dir.
- [ ] TI-21 green (incl. global-untouched + partial-dir-deleted). One PR linking **T-21**.
```
