# T-08 — Interactive Rebase (CLI-driven) — Completion & Test-Backfill Plan

**Task ID:** T-08 · **Milestone:** M3 (audit 2.1) · **Priority:** P0
**Depends on:** T-04 (mid-rebase conflicts route to the reworked resolver), T-07 (`RunGit` maturity).
**Branch:** `plan/T-08-interactive-rebase` → implement on `feat/T-08-interactive-rebase` off `main`.

> **Source of truth:** §T-08 of the Master Doc, §TI-08 of the Test Strategy.

---

## 0. Finding — the prototype is already at contract; this is completion, not a rewrite

Unlike the conflict resolver (T-04), the interactive-rebase feature that merged in PR #21 **already
satisfies the T-08 contract** at the service layer. A from-scratch rewrite would be wasteful and would risk
regressing working, reviewed code. **The remaining work is: (1) backfill the TI-08 test suite (currently
absent), (2) verify/tighten a short list of behaviors, (3) confirm dependency alignment with the reworked
T-04 resolver.** If you (the reader) were told "full redo," this is the correct interpretation of it for
T-08 — bring it to contract + tests, don't throw away the shim machinery.

### Contract-vs-current audit

| Contract requirement (§T-08) | Status on `main` | Evidence |
|---|---|---|
| `IInteractiveRebaseService { GetRebasePlan, StartInteractiveRebase, GetRebaseProgress }` | ✅ present, signatures match | `IInteractiveRebaseService.cs` |
| `RebaseTodoItem { Sha, Action, Message, NewMessage }` | ✅ present (+ additive `FullMessage`) | `Models/RebaseTodoItem.cs` |
| `GetRebasePlan` = `baseSha..HEAD`, oldest-first, all `Pick` | ✅ `Topological | Reverse` walk | `InteractiveRebaseService.cs:15-56` |
| Refuse merge commit in range | ✅ `commits.Any(c => c.Parents.Count() > 1)` → typed throw | `:37-40` |
| Pre-flight: dirty tree / already-rebasing / first-item squash-fixup / empty-after-drops | ✅ all four present, typed | `:59-79` |
| Editor shims: `GIT_SEQUENCE_EDITOR` + `GIT_EDITOR` → `--rebase-editor` / `--rebase-msg` argv modes, `Environment.ProcessPath`, quoted | ✅ present | `:113-120`, `Program.cs` argv modes |
| Non-interactive, cross-platform (no `cp`/`sed`/shell) | ✅ shim copies files in-process | argv modes |
| Conflict mid-rebase → `MergeConflictException` (no auto-abort) | ✅ throws, leaves rebase in place | `:150-155` |
| Edit-pause surfaced (`stopped-sha`) | ✅ | `:157-162` |
| `ContinueRebase` re-runs with the same msg-queue env when `.git/rebase-merge/interactive` | ✅ | `GitServices.cs:499-511` |
| `GetRebaseProgress` from `msgnum`/`end` | ✅ | `InteractiveRebaseService.cs:165+` |
| UI: `InteractiveRebaseWindow` + VM with reorder, action dropdown, P/R/S/F/E/D, reword editor, squash/fixup fold preview | ✅ present (may need tightening — see §3) | `InteractiveRebaseViewModel.cs`, `.axaml` |
| **TI-08 test suite** | ❌ **absent** — no `InteractiveRebaseServiceTests.cs` | `ls Mainguard.Tests \| grep rebase` → nothing |

**Accepted deviation (keep it):** the message queue is keyed by **original commit SHA** (files
`<sha>.msg`), not an ordinal queue, and the `--rebase-msg` shim reads `.git/rebase-merge/done` to pick the
current commit. The Master Doc §T-08 sample uses a numbered queue, but explicitly lists "a tiny dedicated
helper / different queue scheme" under *Acceptable variations*. The SHA-keyed scheme is in fact **more**
correct for multi-squash chains (git invokes the editor once per squash *chain*, which desyncs an ordinal
queue). Do **not** "fix" this to an ordinal queue.

---

## 1. Files to create / modify

| Action | Path | Purpose |
|---|---|---|
| **Create** | `Mainguard.Tests/InteractiveRebaseServiceTests.cs` | The 10 TI-08 integration cases (the primary deliverable). |
| **Verify/tighten** | `Mainguard.Agents/Services/InteractiveRebaseService.cs` | Only if a §2 verification fails. |
| **Verify/tighten** | `Mainguard.App.Shell/ViewModels/InteractiveRebaseViewModel.cs` | Squash/fixup fold preview + `CanExecute` validation mirror (§3). |
| **Verify** | `Mainguard.App.Shell/ViewModels/*` (conflict entry) | The rebase conflict path now opens the **reworked T-04 resolver** (same `MergeConflictException`). |

No new public surface is expected. If a verification in §2 reveals a real gap, fix it minimally in the same PR.

---

## 2. Verification checklist (run each; fix only on failure)

1. **Diagnostics logging (invariant 5):** both the generated todo content and the applied todo are logged at
   Debug level. If not, add `Debug`-level logging of `todoLines` and the copied payload in the `--rebase-editor`
   mode. *(Master Doc invariant: "Both generated and applied todo content are logged for diagnosability.")*
2. **Exe-path-with-spaces:** the shim command strings quote `Environment.ProcessPath` with `"`. Confirm the
   quoting survives a path containing spaces (TI-08 edge). The current `$"{self} --rebase-editor \"{todoPath}\""`
   quotes the payload path; confirm `self` (`GetSelfInvocationPrefix()`) also quotes the exe path.
3. **No auto-abort on conflict:** confirm the conflict branch throws `MergeConflictException` and does **not**
   call `rebase --abort` (it doesn't today — keep it).
4. **`ContinueRebase` interactive path:** confirm `git rebase --continue` runs with the **same** `GIT_EDITOR`
   msg-queue env when `.git/rebase-merge/interactive` exists (so post-pause reword/squash steps still get their
   messages). Present at `GitServices.cs:499`.
5. **Editor mode exits before Avalonia init:** `--rebase-editor` / `--rebase-msg` modes must run and exit in
   `Program.cs` **before** `BuildAvaloniaApp().Start...` (rejection trigger: initializing Avalonia in editor
   mode). Confirm the argv check is the first thing in `Main`.

---

## 3. UI tightening (verify against the contract)

- Rows drag-reorderable; per-row action dropdown; keyboard shortcuts **P/R/S/F/E/D**.
- **Reword** opens an inline editor writing `NewMessage` (defaulting to `FullMessage` so bodies aren't lost).
- A **live preview** folds `Squash`/`Fixup` rows into their preceding `Pick` (visual grouping).
- `CanExecute` on "Start" mirrors the service pre-flight: disabled when the tree is dirty, a rebase is in
  progress, the plan is empty after drops, or the first kept item is `Squash`/`Fixup`. (The prior PRs already
  "dynamically validate and disable invalid rebase actions" — confirm parity with the service guards so the
  UI never lets the user submit a plan the service will reject.)
- Entry point: commit/branch context menu **"Interactive rebase onto here"** (also surfaced by T-09's graph
  menu).

---

## 4. Test contract — `InteractiveRebaseServiceTests.cs` (TI-08, `RequiresGitCli`)

Integration; each test scripts 3–5 commits via `TempRepoFixture`.
`IInteractiveRebaseService svc = new InteractiveRebaseService();`, `IGitService git = new GitService();`

| # | Test | Assertion |
|---|---|---|
| 1 | `GetRebasePlan_ShouldListRangeOldestFirst_AllPick` | plan order == commit order oldest→newest; every `Action == Pick`. |
| 2 | `Reorder_ShouldSwapHistoryOrder_AndPreserveFinalTree` | swap two items, start → history order swapped; `repo.Head.Tip.Tree.Sha` identical before/after. |
| 3 | `Reword_ShouldChangeMessage_KeepTree` | `Reword` + `NewMessage` → new message on that commit, tree unchanged. |
| 4 | `Squash_ShouldCombineTwoCommits_WithNewMessage` | 2→1 commit, `NewMessage` used, combined diff preserved. |
| 5 | `Fixup_ShouldKeepFirstMessage` | fixup drops the second message, keeps the first. |
| 6 | `Drop_ShouldRemoveCommitChanges` | dropped commit's changes absent from the final tree. |
| 7 | `ConflictMidRebase_ShouldThrowMergeConflict_AndContinueAfterResolveCompletesPlan` | conflicting reorder → `Assert.Throws<MergeConflictException>`; resolve via **T-03 `ResolveConflict`**; `ContinueRebase` completes the remaining plan. |
| 8 | `Abort_ShouldRestoreExactPreRebaseHead` | capture HEAD sha, start a conflicting plan, `AbortRebase` → HEAD sha byte-identical to pre-rebase. |
| 9 | `Start_ShouldThrowTyped_OnDirtyTree_MergeCommitInRange_FirstItemSquash_AlreadyRebasing` | four guards (`[Theory]`-style); repo untouched after each throw. |
| 10 | `GetRebaseProgress_ShouldReportStepAndTotal_MidConflict` | during a paused/conflicted rebase → `(step,total)` non-null and sane; null when not rebasing. |

Add an exe-path-with-spaces smoke case if feasible in CI (may be environment-dependent — acceptable to cover
via §2.2 manual check if not).

---

## 5. Invariants (MUST) / Rejection triggers

**MUST:** libgit2 never drives the interactive rebase (G-7); the sequence-editor mechanism is
non-interactive and cross-platform (no `cp`/`sed`/shell built-ins/real editor); failure paths never
auto-abort a conflicted rebase; pre-flight validation before any repo mutation; generated + applied todo
logged at Debug.

**Rejection triggers:** `GIT_SEQUENCE_EDITOR` built from shell commands; starting with a dirty tree "because
autostash"; editor mode that initializes Avalonia.

---

## 6. Reviewer script / Definition of done

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~InteractiveRebase"        # 10 cases green
grep -rn "rebase.*--abort" Mainguard.Agents/Services/InteractiveRebaseService.cs   # -> 0 hits in the conflict path
grep -n  "GIT_SEQUENCE_EDITOR" Mainguard.Agents/Services/InteractiveRebaseService.cs  # value is <exe> --rebase-editor, no shell
```

- [ ] `InteractiveRebaseServiceTests.cs` with all 10 TI-08 cases green.
- [ ] §2 verification checklist all pass (Debug logging added if it was missing).
- [ ] §3 UI parity confirmed (shortcuts, reword default, squash fold, `CanExecute` mirror).
- [ ] Conflict path opens the **reworked T-04 resolver** via `MergeConflictException`.
- [ ] Accepted SHA-keyed message-queue deviation left intact and documented.
- [ ] One task = one PR linking **T-08**; PR notes this was completion + test-backfill, not a rewrite.
```
