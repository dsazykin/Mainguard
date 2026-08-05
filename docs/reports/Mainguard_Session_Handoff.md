# Mainguard — Implementation Session Handoff

This document is the entry point for continuing the feature-implementation effort in a
fresh conversation. Read this first, then `AGENTS.md`, then the planning docs in §0.

_Last updated: 2026-07-06 (end of the T-08 session)._

---

## 0. The overarching task

Implement every feature in **`docs/planning/Mainguard_Master_Implementation_Document.md`** one by one,
in build order (**T-02 → T-22**, plus **T-23** = direct PR/MR integration). Each task has:

- a **contract / edge-case matrix / invariants** in the Master Doc (binding),
- a matching **test contract** `TI-NN` in **`docs/test_implementation_plan/Mainguard_Test_Implementation_Strategy.md`**,
- a detailed **plan doc** that lives on the task's `plan/T-0X-*` branch at
  `docs/feature-plans/T-0X-*.md` (only merged tasks' plan docs are on `main`), and
- a **manual-verification triage** in **`docs/planning/Mainguard_Feature_Plan_Triage.md`** (may be a
  local/untracked file — the key conclusions are reproduced in §5 below so they survive).

---

## 1. WORKFLOW RULES (confirmed by the user this session — these OVERRIDE the old ones)

1. **Branch each task off `origin/main`.** `origin/main` already contains T-02…T-08. Create
   `feature/T-0X-*` from `origin/main`, then `git checkout plan/T-0X-* -- docs/feature-plans/T-0X-*.md`
   to carry that task's plan doc onto the branch (as T-05/06/07/08 did).
2. **Open AND merge the PR yourself** (changed as of the T-08 session — you now have permission).
   Commit + push the branch, then `gh pr create` (base `main`) and `gh pr merge <n> --squash`
   (linear history is required — squash only). **Do NOT put the Claude/"Generated with Claude Code"
   trailer in the PR body or the commit message body.** Watch the required checks first
   (`gh pr checks <n> --watch`); merge once **Build & Test** + **Format check** are green.
   *(Local gotcha: `gh pr merge --delete-branch` can fail to switch branches locally if you have
   uncommitted doc edits in the tree — the remote merge still succeeds; then `git stash -u`,
   `git checkout main`, `git pull --ff-only`, `git stash pop`, and delete the local branch.)*
3. **Merge `origin/main` into your working branch** — right after branching and again before
   finalizing: `git fetch origin && git merge origin/main --no-edit`. `origin/main` moves; keep
   branches synced. Resolve conflicts (usually just the `docs/repo-map/` index — combine both sides).
4. **Update `docs/test_implementation_plan/Mainguard_User_Testing_Guide.md` after every feature.** Add a section for the new
   task with hands-on steps; mark the interaction/animation/native-dialog items **⚠️ PRIORITY**
   (those are what the human pass exists for). This is part of "done," same as the Repository Map.
5. **Come back to the user for anything not FULLY self-verifiable.** The headless render harness
   + present tooling make most tasks auto-verifiable, but for the interaction/animation weak spots
   and external-account tasks (see §5), implement the verifiable parts, then **pause and hand the
   user a manual checklist** (the User-Testing Guide section from rule 4) rather than self-signing-off.
6. Follow **`AGENTS.md`** (design system, Repository Map upkeep in the SAME change, invariants,
   no raw colors, `type: summary` commits). It is the source of truth and is kept current.

---

## 2. What is DONE and MERGED (or pushed)

`origin/main` currently contains, in order:

| Task | Feature | State |
|---|---|---|
| **T-02 / T-03 / T-04** | Conflict resolution: pure 3-way merge chunker, conflict-index plumbing, and the synchronized 3-pane resolver UI | Merged (PR #25) |
| **T-05** | Tag management: `GitTagItem`; `GetTags/CreateTag/DeleteTag/PushTag/DeleteRemoteTag/CheckoutTag`; Tags section in the branch browser; `CreateTagDialog`; graph tag chips | Merged (PR #26) |
| **T-06** | Partial staging: pure `PatchParser`/`PatchBuilder` engine + diff-viewer UI (per-hunk stage/unstage/discard, unified-view drag-select of lines, side-by-side resolver-style block accept/discard) | Merged (PR #27) |
| **T-07** | Worktree porcelain: `WorktreeItem` + pure `WorktreePorcelainParser`; `ListWorktrees` (→`IReadOnlyList<WorktreeItem>`), `AddWorktree(+createBranch)`, `RemoveWorktree(+force)`, `PruneWorktrees`; whole-tree `GetDiffAgainstCommit` + "Diff working tree against this commit" menu | Merged (PR #28) |
| **T-08** | Interactive rebase (completion + test-backfill; the service prototype was already at contract): the **10 TI-08** integration cases + `InteractiveRebaseRenderHarness`; `GitService.SelfInvocationOverride` test seam (points the editor shim at the built `Mainguard.App.Shell`); Debug-level todo logging (invariant 5); UI §3 — P/R/S/F/E/D shortcuts, squash/fixup fold rail, `CanExecute` mirror of the service pre-flight | Merged (PR #29) |

**Full suite is green: 222 tests** (on `main`, post-T-08 merge).

### T-04 deferred UI polish (still open, non-blocking)
The resolver's accept/reject **gutters** should become overlays embedded on the code columns
(code scrolling under a continuous highlight) rather than the dedicated fixed-width gutter; plus a
base-line hint on unresolved modify rows and a word-diff "Show Details" toggle. See
`docs/feature-plans/T-04-conflict-resolver-ui.md` §12. Revisit when convenient.

---

## 3. What's NEXT (resume here)

**T-09 — rich commit-graph interactions.** Its dependency (T-05) is merged, and the drag-rebase
flyout item's dependency (T-08) is now merged too. Triage flags it a **come-back** task: the
hit-tester and the `CommitTimelineViewModel` menu construction are pure/testable, but the
**drag-to-rebase** gesture needs a human pass for feel. Plan doc on `plan/T-09-*`; test contract is
`TI-09` (`GraphHitTesterTests` pure + `CommitTimelineMenuTests` ViewModel). Its plan expects the
"Interactive rebase onto here" menu entry to route into the T-08 dialog (already wired in
`CommitTimelineViewModel.InteractiveRebase`).

Then continue **T-10 … T-22** per the Master Doc build order (§3 of that doc is authoritative for
per-task dependencies). **Skip on reach** (blocked on external accounts/tooling): **T-14** (multi-host
OAuth/SSH), **T-23** (PR/MR integration) — build their offline slices only, defer the live matrix.

---

## 4. Environment & build (critical operational facts)

- **Build/test with the Windows SDK invoked from WSL:** `"/mnt/c/Program Files/dotnet/dotnet.exe"`.
  There is no Linux `dotnet` and no Docker here.
  ```bash
  "/mnt/c/Program Files/dotnet/dotnet.exe" build Mainguard.slnx -v q -clp:ErrorsOnly
  "/mnt/c/Program Files/dotnet/dotnet.exe" test  Mainguard.Tests/Mainguard.Tests.csproj
  "/mnt/c/Program Files/dotnet/dotnet.exe" format Mainguard.slnx --verbosity minimal   # CI has a required Format check
  "/mnt/c/Program Files/dotnet/dotnet.exe" test  Mainguard.Tests/Mainguard.Tests.csproj --filter "FullyQualifiedName~Worktree"
  ```
- **Tests run under Windows git/gpg/git-lfs** (Git-for-Windows bundles all three: git 2.53, gpg 2.4.8,
  git-lfs 3.5.1). So `RequiresGitCli`, `RequiresGpg`, and `RequiresGitLfs` suites all **run here** (they'd
  skip on a machine lacking the tool — flag any green that depends on gpg/lfs as "verified *here*").
- **Build-lock gotcha:** `dotnet build` fails with **MSB3021/MSB3027 "file locked by Mainguard.App.Shell (PID)"**
  if the app is running. That's a *lock, not a compile error* — ask the user to close the app, then rebuild.
- **Headless render harness** (`Mainguard.Tests/Headless/`, `[AvaloniaFact]` + Skia): drives real Views
  against real fixture repos and saves PNGs to `artifacts_headless/` (gitignored) — **read the PNGs** to
  inspect UI. It also **injects pointer input** (see `PartialStagingRenderHarness` drag-select test), so
  interactions can be exercised, not just rendered. Harnesses: `ResolverRenderHarness` (conflict resolver),
  `TagUiRenderHarness` (tags), `PartialStagingRenderHarness` (partial staging),
  `InteractiveRebaseRenderHarness` (rebase plan + fold rail).
- **`main` is protected** (ruleset `main-protection`): required checks `Build & Test` + `Format check`,
  **linear history** (squash/rebase merge only). As of the T-08 session **you merge** via
  `gh pr merge <n> --squash` once checks are green (see §1 rule 2).
- **Git remote actions use `gh` / HTTPS token** (SSH fails). Remote: `github.com/dsazykin/Mainguard.git`.

---

## 5. Self-verification triage map (which upcoming tasks need a "come back")

Produced this session from `Mainguard_Feature_Plan_Triage.md` + the headless harness + present tooling.
**Fully self-verifiable (proceed autonomously):** T-10, T-12, T-16, T-18, T-19 (sweeping — check
every mutating op is covered), T-20, T-22, and **T-15 / T-17 locally** (gpg + git-lfs present).
_(T-08 is **done** — it was self-verifiable via the editor-shim test seam + the render harness.)_
**Come back to the user** (implement the verifiable slice, then hand the User-Testing-Guide checklist):
- **T-09** *(NEXT)* — graph interactions: hit-tester is pure/testable; the **drag-to-rebase** gesture needs a human pass.
- **T-11** — blame: service/cache testable; the AvaloniaEdit blame-gutter **rendering** wants a look.
- **T-13** — diff quality: intra-line/whitespace pinned; the **image-diff swipe** control needs a human pass.
- **T-21** — profiles/clone: profile-apply + cancel-delete testable; the **live progress animation** wants a look.
- **T-14, T-23** — external accounts (real GitHub/GitLab OAuth/PAT/SSH; PR create/list/merge). Skip on reach.

The interaction weak spots (drag/swipe/animation) are exactly what the harness can render and even
drive, but can't judge for *feel* — hence the come-back.

---

## 6. Persistent memories in play (auto-loaded each session)

- **Merge origin/main into working branches** — keep feature branches synced with remote `main`.
- **Come back on not-fully-verifiable triage tasks** — pause for a human pass (see §5).
- **Use `gh` for git remote actions** — SSH fails; HTTPS/token via gh.

---

## 7. Key files added across the conflict-resolution → interactive-rebase sessions

(See the `docs/repo-map/` index for the authoritative index.)

- **Core models:** `MergeChunk`, `ConflictedFile`, `ConflictSide`, `GitTagItem`,
  `DiffLine`/`DiffHunk`/`FilePatch` (`DiffHunk.cs`), `WorktreeItem`, `RebaseTodoItem`.
- **Core services:** `IMergeDiffService`/`MergeDiffService`, `PatchParser`, `PatchBuilder`,
  `WorktreePorcelainParser`, `IInteractiveRebaseService`/`InteractiveRebaseService`, plus the
  tag/conflict/worktree methods on `IGitService`/`GitServices` and the `SelfInvocationOverride`
  test seam (internal, `InternalsVisibleTo Mainguard.Tests`).
- **App:** `ConflictResolverWindow`/`ConflictedFilesWindow` + VMs; `CreateTagDialog` + VM;
  `DiffViewerViewModel` partial-staging (+ `DiffHunkRowViewModel`/`DiffLineRowViewModel`);
  `InteractiveRebaseWindow` + `InteractiveRebaseViewModel`; `Program.cs` `--rebase-editor`/`--rebase-msg` shims.
- **Tests:** `MergeDiffServiceTests`, `GitServiceConflictTests`, `MergeChunkViewModelTests`,
  `GitServiceTagTests`, `PatchParserTests`, `PatchBuilderTests`, `GitServicePartialStagingTests`,
  `WorktreePorcelainParserTests`, `GitServiceWorktreeTests`, `InteractiveRebaseServiceTests`, and the four
  `Headless/*RenderHarness`. Real-git patch corpus in `Mainguard.Tests/TestData/patches/` (LF-locked via `.gitattributes`).
