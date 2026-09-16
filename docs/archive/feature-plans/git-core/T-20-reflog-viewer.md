# T-20 — Reflog Viewer & Recovery — Implementation Plan

**Task ID:** T-20 · **Milestone:** M5 (audit 2.12) · **Priority:** P2 differentiator
**Depends on:** T-19 (destructive reflog actions route through the journal so they're undoable).
**Branch:** `plan/T-20-reflog-viewer` → implement on `feat/T-20-reflog-viewer` off `main`.

> **Source of truth:** §T-20 of the Master Doc + strategy §D-2.12, §TI-20 of the Test Strategy.

---

## 0. Context

No reflog UI today. `repo.Refs.Log(reference)` yields `ReflogEntry` (from/to sha, message, committer).
This task surfaces the reflog read-only first, then adds recovery actions (restore, recover deleted branch)
that **route through the T-19 journal** so even reflog-driven resets are undoable.

### What you can rely on

| Fact | Where |
|---|---|
| `repo.Refs.Log("HEAD")` / `Log(refName)` → `ReflogEntry { From, To, Message, Committer }` | LibGit2Sharp |
| T-19 journal for making restores undoable | `feat/T-19` |
| `ResetToCommit` / `CreateBranch` for restore actions | `GitServices.cs` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Models/ReflogItem.cs` |
| **Edit** | `IGitService.cs` + `GitServices.cs` — `GetReflog` |
| **Create** | reflog viewer panel (list per ref + restore actions) |
| **Create** | `Mainguard.Tests/GitServiceReflogTests.cs` |

---

## 2. Contract

```csharp
// Mainguard.Agents/Models/ReflogItem.cs
public sealed class ReflogItem { public string FromSha { get; init; } = ""; public string ToSha { get; init; } = ""; public string Message { get; init; } = ""; public DateTimeOffset When { get; init; } }
// IGitService
IReadOnlyList<ReflogItem> GetReflog(string repoPath, string refName = "HEAD", int take = 200);
```

---

## 3. Implementation

- `GetReflog`: `ExecuteWithRepo` → `repo.Refs.Log(refName).Take(take)` → map to `ReflogItem`
  (`From.Sha`/`To.Sha`, `Message`, `Committer.When`). Missing ref → typed throw.
- **UI:** a list per ref with each move (from→to, message, when). Actions:
  - **Restore** = `ResetToCommit(Hard, entry.To)` (behind confirmation), **routed through the T-19 journal**
    so it's undoable.
  - **Create branch here** at a chosen entry (recovers an orphaned tip → deleted-branch recovery).
- Read-only rendering first; then wire the two destructive actions.

---

## 4. Test contract — TI-20 (`GitServiceReflogTests.cs`)

- commit → hard-reset → `GetReflog` shows **both** moves with correct from/to;
- "create branch here" at the pre-reset entry restores the commit;
- deleted-branch recovery finds the orphaned tip;
- destructive action **routes through the journal** (assert a journal entry exists afterward).

---

## 5. Reviewer script / Definition of done

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~Reflog"
```

- [ ] `ReflogItem` + `GetReflog(refName, take)` with typed missing-ref guard.
- [ ] Reflog panel; restore + create-branch-here recovery, restore confirmed and journaled (T-19).
- [ ] TI-20 green incl. the journal-entry assertion. One PR linking **T-20**.
```
