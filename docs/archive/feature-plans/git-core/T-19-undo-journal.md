# T-19 — Operation Journal: Unlimited Undo / Redo — Implementation Plan

**Task ID:** T-19 · **Milestone:** M5 (audit 2.9) · **Priority:** P2 differentiator (P0 for the agent-phase safety net)
**Depends on:** T-05 / T-07 / T-08 **merged** — the instrumentation sweep touches every mutating method, so
do it once, late, to avoid rebase churn.
**Branch:** `plan/T-19-undo-journal` → implement on `feat/T-19-undo-journal` off `main`.

> **Source of truth:** §T-19 of the Master Doc + strategy §D-2.9, §TI-19 of the Test Strategy.

---

## 0. Context

No undo today. Git's **reflog** already persists ref history; this task adds an operation **journal** on top:
before each mutating op, snapshot the affected refs + HEAD; on `Undo`, restore them; on `Redo`, re-apply the
post-state. Every mutating `GitService` method wraps itself in `using var op = journal.BeginOperation(...)`.

### Mutating methods to instrument (the sweep)

`Commit :320`, `Rebase :426`, `Merge :453`, `CheckoutBranch :987`, `CreateBranch :1022`,
`DeleteBranch :1055`, `StashPush :1109`, `ResetToCommit :1260`, `RevertCommit :1272`, `CherryPick :1285`,
`AmendCommitMessage :1302`, plus `CreateTag`/`DeleteTag` (T-05) and `StartInteractiveRebase` (T-08). Every
one gets a `BeginOperation` wrapper.

### What you can rely on

| Fact | Where |
|---|---|
| `repo.Refs.UpdateTarget(...)`, `repo.Refs.Log(ref)` (reflog), `repo.Reset(...)` | LibGit2Sharp |
| EF Core `AppDbContext` + `Migrations/` (SQLite) for persistence | `Core/AppDbContext.cs` |
| Typed exceptions; `HasUncommittedChanges` for the dirty-tree guard | `GitServices.cs` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Services/IOperationJournal.cs` + `OperationJournal.cs` |
| **Create** | `Mainguard.Agents/Models/JournalEntry.cs` + `DbSet<JournalEntry>` in `AppDbContext` + **migration** |
| **Edit** | `GitServices.cs` — wrap **every** mutating method in `BeginOperation` |
| **Create** | operation-history UI (list with per-entry undo/redo) |
| **Create** | `Mainguard.Tests/OperationJournalTests.cs` (one round-trip per op kind) |

---

## 2. Contract

```csharp
public interface IOperationJournal
{
    IDisposable BeginOperation(string repoPath, string kind, string description); // snapshot refs+HEAD on create, post-state on dispose
    IReadOnlyList<JournalEntry> GetHistory(string repoPath, int take = 100);
    void Undo(string repoPath, long entryId);
    void Redo(string repoPath, long entryId);
}
```

`JournalEntry` persists: `Id`, `RepoPath`, `Kind`, `Description`, `When`, pre-state and post-state ref maps
(serialized `Dictionary<string,string>` of ref → sha + HEAD symbolic target), `IsUndoable`, `UndoBlockedReason?`,
and a redo-truncation marker.

---

## 3. Binding behaviors

- **Snapshot:** `BeginOperation` captures a `CaptureRefState(repo)` = `{ every branch ref → sha, HEAD symbolic
  target }` on create; the returned `IDisposable`'s `Dispose` captures the **post**-state and writes the
  entry.
- **Undo:** restore every recorded ref via `repo.Refs.UpdateTarget`; hard-reset the working tree **only after
  a clean-tree check** — a dirty tree → **typed refusal that mutates nothing** (TI-19 #3).
  - branch-delete undo recreates the branch **and its upstream config** (TI-19 #2);
  - commit undo = **mixed reset to parent**.
- **Redo:** restore the post-state ref map.
- **Truncation:** any new mutating op after an undo **truncates the redo stack** (TI-19 #4).
- **Non-undoable ops** (push, stash-pop-with-conflicts) are **journaled + flagged** with a reason, not
  silently dropped (TI-19 #5).
- **Persistence:** SQLite via `AppDbContext` + migration; survives context reopen (TI-19 #6).

---

## 4. Invariants / Test contract — TI-19 (the heart of the feature)

**MUST:** for **every** op kind, `op → Undo` restores **all** branch SHAs + HEAD symbolic target
**byte-exactly**, and `Redo` restores the post-state; undo-with-dirty-tree refuses and mutates nothing. No op
kind is exempt.

`OperationJournalTests.cs`:
1. `[Theory]` round-trip over **every** kind (Commit, Merge, Rebase, Reset(each mode), Revert, CherryPick,
   CreateBranch, DeleteBranch, StashPush, TagCreate/Delete, InteractiveRebase): perform → `Undo` → all branch
   SHAs + HEAD target == pre-op snapshot → `Redo` → == post-op snapshot. Helper `CaptureRefState(repo)` →
   `Dictionary<string,string>` + HEAD; assert dictionary equality.
2. `Undo_BranchDelete_ShouldRestoreUpstreamConfig`.
3. `Undo_WithDirtyTree_ShouldRefuseTyped_AndChangeNothing`.
4. `NewOperationAfterUndo_ShouldTruncateRedo`.
5. `NonUndoableOps_ShouldBeJournaledFlagged_WithReason` (push).
6. `Journal_ShouldPersistAcrossContextReopen` (SQLite round-trip).

---

## 5. Reviewer script / Definition of done

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~OperationJournal"
# every mutating method wrapped:
grep -cE "BeginOperation" Mainguard.Agents/Services/GitServices.cs      # >= the mutating-method count
ls Mainguard.Agents/Migrations | grep -i Journal
```

- [ ] `IOperationJournal`/`OperationJournal` + `JournalEntry` entity + migration.
- [ ] **Every** mutating method wrapped in `BeginOperation`; non-undoable ops flagged with reason.
- [ ] Undo restores all refs+HEAD byte-exactly; dirty-tree refusal mutates nothing; redo truncation.
- [ ] Operation-history UI with per-entry undo/redo.
- [ ] TI-19 round-trip green for every op kind, no exemptions. One PR linking **T-19**.
```
