# Mainguard — Test Implementation Strategy

> **ARCHIVED — superseded.** The v1 test contract, keyed to the v1 `T-NN` tasks. Superseded by
> [`Mainguard_Test_Implementation_Strategy_v2.md`](../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md).

**Date:** 2026-07-03
**Companion to:** `Mainguard_Master_Implementation_Document.md` — every task `T-NN` there has a section `TI-NN` here. A feature PR that does not satisfy its `TI-NN` section is incomplete by definition.
**Also covers:** the coverage backfill for already-landed work (§B) and the shared test conventions (§A).

---

## A. Conventions, infrastructure, and the definition of "sufficiently tested"

### A.1 Framework and layout

- xUnit `[Fact]`/`[Theory]`, naming `Method_ShouldExpectedBehavior_Condition` (existing convention).
- One test class per feature area, file named after the class, in `Mainguard.Tests/` (Core) — e.g. `GitServiceMergeTests.cs`, `PatchParserTests.cs`.
- Integration tests that need a real repo use `Mainguard.Tests/Fixtures/TempRepoFixture.cs` (T-01). No new hand-rolled temp-dir plumbing.
- Tests that shell out to the real `git` CLI are tagged `[Trait("Category", "RequiresGitCli")]`. CI runs them (git is in the toolchain image); a dev without git can filter them out.
- Tests that need optional tooling are tagged and **skipped, not failed**, when the tool is absent: `RequiresGitLfs`, `RequiresGpg`.
- Tests that mutate process-global state (LibGit2Sharp `GlobalSettings.SetConfigSearchPaths`) live in the xUnit collection `"GlobalGitConfig"` so they never run in parallel with each other or with identity-sensitive tests.

### A.2 The three test tiers and when each is required

| Tier | What it is | Required when |
|---|---|---|
| **Pure unit** | No IO, no repo. Pure classes (chunker, parsers, matchers, hit-testers, state machines) | Always for any pure class — this is why the master doc keeps engines pure |
| **Repo integration** | `TempRepoFixture` + `GitService` methods, asserting real repository state (refs, index, workdir, remotes) | Every mutating Git method (global invariant G-6) |
| **ViewModel** | Headless Avalonia + mocked `IGitService` | Every ViewModel behavior the master doc names in an invariant (gating, busy-state, routing) |

**Definition of sufficiently tested** for a feature task: (1) every row of the task's edge-case matrix in the master doc has exactly one test that would fail if that behavior regressed; (2) every MUST invariant that is checkable in-process has a test; (3) the failure paths (typed exceptions) are asserted by type — never by message substring.

### A.3 Assertion rules

- Assert repository state, not call success: after a mutating op, open the repo and check refs/index/workdir (e.g. `repo.Head.Tip.Sha`, `repo.Index.Conflicts.Any()`, file content).
- Assert exception **types** (`Assert.Throws<MergeConflictException>`), plus payload properties where the contract defines them (e.g. `ConflictedPaths`). Never `Assert.Contains("conflict", ex.Message)`.
- Time-based components (watcher, auto-fetch, debounce) are tested with generous timeouts and "did NOT fire within X" via `Task.WhenAny(tcs.Task, Task.Delay(x))` — the existing watcher tests are the pattern. Never `Thread.Sleep` polling loops.
- Any test writing outside its fixture directory is a bug.

### A.4 TI-00 — ViewModel test infrastructure (prerequisite for all ViewModel tiers)

`Mainguard.Tests` currently references **Core only**. Before the first ViewModel test (needed from T-04 on):

1. Add a project reference from `Mainguard.Tests` to `Mainguard.App.Shell`, plus packages `Avalonia.Headless` and `Avalonia.Headless.XUnit` (matching the app's Avalonia version, 11.1.x).
2. Add `TestAppBuilder.cs` with the standard `[AvaloniaTestApplication]` wiring so `[AvaloniaFact]`/`[AvaloniaTheory]` run on the headless dispatcher.
3. Add `Mainguard.Tests/Fakes/FakeGitService.cs`: an `IGitService` implementation where every member delegates to a settable `Func<>`/`Action<>` (a hand-rolled configurable fake — no mocking library, keeping with the zero-container philosophy; if the team prefers NSubstitute/Moq, adopt it here deliberately and note it in AGENTS.md).
4. Pattern for async-command tests: back the fake's method with a `TaskCompletionSource` so the test can hold the operation open and assert `IsBusy`/`CanExecute` mid-flight, then release it.

**Acceptance for TI-00 itself:** one smoke `[AvaloniaFact]` constructing `RepoDashboardViewModel` against the fake and asserting a bound property updates; suite still runs headless in CI (the Docker toolchain image already carries the needed native libs).

### A.5 CI

`ci.yml` already runs build + test + format. Additions as the suite grows:
- Keep the full suite < 3 minutes; if repo-integration tests push past it, split a `-Slow` trait and run it on a nightly schedule, never dropping it from PR CI silently.
- Coverage via coverlet is collected; treat coverage as a review signal, not a gate (no arbitrary % threshold — the edge-case-matrix rule above is the gate).

### A.6 Headless render harness — visual review + input injection

The headless infra from A.4 is configured with **Skia rendering on** (`UseHeadlessPlatformOptions { UseHeadlessDrawing = false }` in `TestAppBuilder`), which means `[AvaloniaFact]` tests can render **real Views to real pixels with no display**. This unlocks a testing tier beyond ViewModel assertions — use it for any UI where layout, theming, or an *interaction* is the thing under test and a pure ViewModel test can't prove it.

Two capabilities, both offscreen and CI-safe:

1. **Visual review.** Host a real View in a `Window`, drive its ViewModel against a real `TempRepoFixture`, pump the dispatcher, then `window.CaptureRenderedFrame().Save(...)` a PNG into **`artifacts_headless/`** (gitignored). A human or agent **reads the PNG** to confirm the render — layout, design-token colors, both light/dark themes — which a bound-property assertion can't. This is how the resolver, tag UI, and partial-staging UI were verified without a display.
2. **Input injection.** Drive real gestures with the `Avalonia.Headless` extensions `window.MouseDown/MouseMove/MouseUp(point, MouseButton)` (and `KeyPress`), locating targets via `view.GetVisualDescendants()` + `control.TranslatePoint(center, window)`. This exercises **interactions**, not just static frames — e.g. `PartialStagingRenderHarness.DragSelect_ShouldPaintSelectionAcrossChangeLines` presses on one line, drags across others, releases, and asserts the ViewModel's selection state. Prefer this to hand-poking `IsSelected` when the *interaction* (not just its effect) is the contract.

**Harnesses today** (`Mainguard.Tests/Headless/`): `ResolverRenderHarness` (conflict resolver), `TagUiRenderHarness` (create-tag dialog + graph chips), `PartialStagingRenderHarness` (hunk/line + side-by-side staging, incl. the drag-select input test). Add a `*RenderHarness` alongside these for new UI.

**Boundary (why it doesn't replace manual testing).** It renders real frames and accepts injected input, but it **cannot judge animation smoothness, gesture "feel", GPU-compositing, or font-hinting nuance**. Anything in those categories still needs a human pass — see `Mainguard_User_Testing_Guide.md`, whose ⚠️ PRIORITY items are exactly what this harness can render/drive but not evaluate for feel.

---

## B. Backfill — tests for already-landed work (fixes 1.1–1.13)

Status audit of the existing suite (2026-07-03): `GitServicesTests.cs` covers one happy path each for fixes 1.2, 1.4 (three cases), 1.5 (conflict only), 1.6, 1.8 (multi-path only), 1.9 (one guard), 1.10 (three watcher cases), 1.11 (one type), 1.12, 1.13 (stage only). `GitHostDetectorTests` covers 1.7's detection well. **The following gaps must be closed** (the backfill PR that ships this document implements them — listed here so the contract survives that PR):

| ID | Gap | Required tests (names indicative) |
|---|---|---|
| B-1 | **1.13** UnstageHunk / DiscardHunk / failure path | `UnstageHunk_ShouldRemoveOnlySelectedHunkFromIndex`; `DiscardHunk_ShouldRevertOnlySelectedHunkInWorkdir`; `StageHunk_ShouldThrowTyped_OnCorruptPatch`; `StageHunk_ShouldNoOp_OnEmptyPatch` |
| B-2 | **1.5** non-conflict pull strategies | `Pull_FastForwardOnly_ShouldAdvanceHead_WhenCleanFastForward`; `Pull_FastForwardOnly_ShouldThrowTyped_WhenDiverged` (and working tree untouched); `Pull_Rebase_ShouldReparentLocalCommit_OntoRemoteTip`; `Pull_Rebase_ShouldThrowTyped_WhenNoUpstream` |
| B-3 | **1.11/1.1** conflict-typed throws beyond pull | `Merge_ShouldThrowMergeConflict_OnConflictingBranches` (and `MERGE_HEAD` exists = merge left in progress); `CherryPick_ShouldThrowMergeConflict_OnConflict`; `Rebase_ShouldThrowMergeConflict_AndLeaveRebaseInProgress`; `Merge_ShouldThrowTyped_WhenBranchMissing`; `Rebase_ShouldThrowTyped_WhenBranchMissing` |
| B-4 | **1.2** identity guard on the other mutating ops | `RevertCommit_ShouldThrowGitIdentityMissing_WhenNoIdentity`; `CherryPick_ShouldThrowGitIdentityMissing_WhenNoIdentity`; `StashPush_ShouldThrowGitIdentityMissing_WhenNoIdentity` (all in the `GlobalGitConfig` collection) |
| B-5 | **1.4** staged-new discard | `DiscardChanges_ShouldRemoveStagedNewFile_FromIndexAndWorkdir`; `DiscardChanges_ShouldHandleMixedSelection_TrackedAndUntracked` |
| B-6 | **1.8** remaining filters + pagination | `GetRecentCommits_TextFilter_MatchesMessageAndShaPrefix`; `GetRecentCommits_AuthorFilter_MatchesNameOrEmail`; `GetRecentCommits_DateRange_BoundsInclusive`; `GetRecentCommits_Pagination_IsStableAcrossCalls` (page 2 fetched twice is identical, pages don't overlap and concatenate to the full list); `GetRecentCommits_SinglePathFilter_FollowsFile` |
| B-7 | **1.9** remaining null-tip guards | `GetBranches_ShouldReturnEmpty_OnEmptyRepo`; `AmendCommitMessage_ShouldThrowTyped_OnUnbornHead`; `GetBranchDiffAgainstWorkingTree_ShouldThrowTyped_WhenBranchMissing` |
| B-8 | **1.10** positive + coalescing watcher cases | `RepositoryWatcher_ShouldTrigger_OnRefsChange`; `RepositoryWatcher_ShouldCoalesceBurst_IntoBoundedFires` (100 rapid writes → ≥1 fire, ≤ ceiling) |
| B-9 | **1.6** runner failure surface | `RunGit_ShouldThrowTypedWithStderr_OnFailure` (exercised via `AddWorktree` with a nonexistent branch: assert `GitOperationException` and message non-empty) |
| B-10 | never-tested basics | stash lifecycle (`StashPush`→`GetStashes`→`StashApply` keeps stash→`StashPop` restores+removes→`StashDrop`); `ResetToCommit` Soft/Mixed/Hard state table; `RevertCommit_ShouldCreateInverseCommit`; `AmendCommitMessage_ShouldRewriteHeadMessage` + `ShouldThrowTyped_WhenNotHead`; `GetCommitModifiedFiles_ShouldDiffAgainstParent_AndEmptyTreeForRoot`; `GetBranchesContainingCommit_ShouldListOnlyContainingBranches` |
| B-11 | **1.3** async ViewModel behavior | requires TI-00; specified there (busy-gating, cancel, error routing) — the only backfill item deferred to TI-00 |

---

# C. Per-task test specifications (TI-01 … TI-22)

Each section lists: test files, the concrete test cases (Arrange→Act→Assert in one line each), and the tier. "Fixture" always means `TempRepoFixture`.

---

## TI-01 — TempRepoFixture (tests for the harness itself)

**File:** `TempRepoFixtureTests.cs` (repo integration).

1. `CommitFile_ShouldCreateCommit_WithFixtureIdentity` — new fixture → `CommitFile("a.txt","x","m")` → repo has 1 commit, author `test-user`.
2. `CreateConflict_ShouldProduceRealConflict` — `CreateConflict` → `Merge(theirs)` via `GitService` → `MergeConflictException` and `repo.Index.Conflicts.Any()`.
3. `AddBareRemote_ShouldRoundTripPush` — commit → `AddBareRemote` → `GitService.Push` → bare repo's branch tip equals local tip.
4. `Dispose_ShouldRemoveEverything` — create fixture + clone + bare remote, capture paths, dispose → none of the directories exist.

---

## TI-02 — Merge chunker (pure; the spec-by-test task)

**File:** `MergeDiffServiceTests.cs` (pure unit). Minimum 14 cases:

1. `GenerateMergeChunks_Identical_ShouldYieldSingleUnchanged`.
2. `GenerateMergeChunks_AllEmpty_ShouldYieldEmptyList` (pins the degenerate-case choice).
3. `GenerateMergeChunks_LeftOnlyEdit_ShouldYieldLeftOnlyChunk` (and surrounding `Unchanged`).
4. `GenerateMergeChunks_RightOnlyEdit_ShouldYieldRightOnlyChunk`.
5. `GenerateMergeChunks_SameLineEditedBothSides_ShouldYieldConflict`.
6. `GenerateMergeChunks_IdenticalEditBothSides_ShouldNotConflict`.
7. `GenerateMergeChunks_NonOverlappingEdits_ShouldYieldBothKinds_AndAssembleToTrueMerge` — assert full chunk sequence and that `AssembleMerged` (no resolutions) equals the manually-computed merge.
8. `GenerateMergeChunks_BothInsertAtSameAnchor_ShouldConflict` (middle and EOF variants — `[Theory]`).
9. `GenerateMergeChunks_AddAdd_EmptyBase_ShouldConflict`.
10. `GenerateMergeChunks_WholeFileDeleteVsEdit_ShouldConflict_WithEmptyLeftText`.
11. `GenerateMergeChunks_CrlfInput_ShouldBehaveAsLf`.
12. `AssembleMerged_Unresolved_ShouldThrowInvalidOperation`.
13. `AssembleMerged_TakeBoth_ShouldEmitLeftThenRight`; `TakeLeft`/`TakeRight`/`Custom` variants (`[Theory]`).
14. `Chunks_ShouldCoverBaseExactly` — property-style: for a table of ~10 diverse triples, concatenating chunk `BaseText`s reproduces the base (invariant 2), no two adjacent chunks share a Kind, and no Conflict chunk has `LeftText == RightText`.

---

## TI-03 — Conflict index plumbing

**File:** `GitServiceConflictTests.cs` (repo integration).

1. `GetConflicts_ShouldListConflictedPath_WithAllStagesPresent` — fixture `CreateConflict` + failed merge → one entry, all three `Has*` true.
2. `GetConflicts_ShouldReturnEmpty_OnCleanRepo`.
3. `GetConflictBlobs_ShouldReturnThreeDistinctTexts`.
4. `GetConflictBlobs_ShouldThrowTyped_OnNonConflictedPath`.
5. `GetConflictBlobs_AddAdd_ShouldReturnEmptyBase` — two branches add the same new file differently.
6. `GetConflictBlobs_ModifyDelete_ShouldFlagMissingSide` — delete on theirs → `HasTheirs == false`, `TheirsText == ""`.
7. `ResolveConflict_ShouldClearConflict_AndStageContent` — resolve → `repo.Index.Conflicts` empty for the path, workdir file equals merged content, index blob equals merged content.
8. `ResolveConflict_ThenCommit_ShouldCreateTwoParentMergeCommit`.
9. `HasUnresolvedConflicts_ShouldTrackResolutionProgress` — true after conflict, false after resolving the only file.

---

## TI-04 — Conflict resolver UI

**Files:** `MergeChunkViewModelTests.cs`, `ConflictResolverWindowViewModelTests.cs` (ViewModel tier — needs TI-00), plus one end-to-end repo integration in `GitServiceConflictTests`.

1. `TakeOurs_ShouldMarkResolved_AndUpdatePreview` (same for theirs/both — `[Theory]`).
2. `CustomEdit_ShouldSetResolutionCustom_AndCaptureText`.
3. `MarkResolved_CanExecute_ShouldBeFalse_WhileAnyChunkUnresolved` and flips true at the last resolution.
4. `CommitMerge_CanExecute_ShouldFollowHasUnresolvedConflicts` (fake service toggles the value).
5. `Load_ShouldNotTouchBoundCollections_OffUiThread` — headless dispatcher assertion (load completes without cross-thread exception).
6. `DeleteModifyConflict_ShouldOfferKeepOrDelete_NotChunkEditor` — fake blobs with `HasTheirs == false`.
7. Integration: `FullConflictLoop_ResolveMixed_ThenCommit_ShouldProduceMergedBlob` — ours for chunk 1, theirs for chunk 2, custom for chunk 3 → committed blob equals the assembled text, commit has 2 parents. Rebase variant ends with `ContinueRebase` completing.
8. Regression guard: `Preview_ShouldNeverWriteToDisk` — after arbitrary resolution toggling, the workdir file's mtime/content unchanged until `MarkResolved`.

---

## TI-05 — Tags

**File:** `GitServiceTagTests.cs` (repo integration).

1. `CreateTag_Lightweight_ShouldAppearInGetTags_NotAnnotated`.
2. `CreateTag_Annotated_ShouldCarryMessageAndTagger_AndPeelToTarget` — tag an **older** commit; `TargetSha` equals that commit, not the tag object.
3. `CreateTag_ShouldThrowTyped_OnInvalidName` — `[Theory]` with `"a b"`, `"-x"`, `"a..b"`, `""` — and repo tag count unchanged.
4. `CreateTag_ShouldThrowTyped_OnDuplicateName`.
5. `DeleteTag_ShouldRemove_AndThrowTypedWhenMissing`.
6. `PushTag_ShouldCreateRemoteRef_OnBareRemote`; `DeleteRemoteTag_ShouldRemoveRemoteRef_KeepLocal`.
7. `CheckoutTag_ShouldDetachHead_AtPeeledTarget`.

---

## TI-06 — Patch parser / builder / partial-staging UI

**Files:** `PatchParserTests.cs`, `PatchBuilderTests.cs` (pure), `GitServicePartialStagingTests.cs` (integration, `RequiresGitCli`), plus ViewModel cases after TI-00.

Parser (drive from a committed corpus in `Mainguard.Tests/TestData/patches/` — real git-produced patches):
1. `Parse_Serialize_ShouldRoundTripByteIdentically` — `[Theory]` over the corpus: simple modify, multi-hunk, multi-file, no-newline-at-EOF, rename header, adjacent hunks, add-only new file, delete-only.
2. `Parse_ShouldExposeHunkHeaderNumbers_AndSectionHeading`.
3. `Parse_ShouldAttachNoNewlineMarker_ToPrecedingLine`.

Builder (pure):
4. `BuildHunkPatch_ShouldEmitHeaderPlusSelectedHunksVerbatim`.
5. `BuildLinePatch_OnlyAdditionsSelected_ShouldRecountCorrectly` (assert exact `@@` header).
6. `BuildLinePatch_OnlyDeletionsSelected_ShouldTurnUnselectedDeletesToContext_DropUnselectedAdds`.
7. `BuildLinePatch_FirstAndLastLineOfHunk_ShouldApplyCleanly` (validated in integration below).
8. `BuildLinePatch_NothingSelected_ShouldReturnEmpty`.

Integration (the ground truth — every builder output must satisfy git):
9. `StageBuiltLinePatch_ShouldPutExactlySelectedLinesInIndex` — 4-line hunk, select 1 line → `git diff --cached` (via `GetFileDiff(isStaged:true)`) contains exactly that change; workdir untouched.
10. `UnstageBuiltPatch_ShouldReverseExactly` (built from index↔HEAD diff — direction rule).
11. `DiscardBuiltPatch_ShouldRemoveOnlySelectedLines_FromWorkdir`.
12. `StaleBuiltPatch_ShouldThrowTyped_NotSilentlyRecount` — build, modify the file, apply → `GitOperationException`.

---

## TI-07 — Worktree porcelain

**File:** `GitServiceWorktreeTests.cs` (integration, `RequiresGitCli`).

1. `ListWorktrees_ShouldParseMainAndLinked_WithBranchAndSha` — add one worktree → 2 items, first `IsMain`, branch names right.
2. `ListWorktrees_ShouldParseDetached_AndLocked` (`worktree add --detach` / `--lock` via fixture-arranged `RunGit`... arrange with the service's own Add + `git worktree lock` run through the test).
3. `AddWorktree_WithCreateBranch_ShouldCreateBranchAndDir`.
4. `AddWorktree_OnCheckedOutBranch_ShouldThrowTyped`.
5. `RemoveWorktree_Dirty_ShouldThrowWithoutForce_AndSucceedWithForce`.
6. `PruneWorktrees_ShouldCleanMetadata_AfterManualDelete`.
7. Pure sub-test for the stanza parser if extracted (`WorktreePorcelainParserTests`) — paths with spaces, missing optional fields.

---

## TI-08 — Interactive rebase

**File:** `InteractiveRebaseServiceTests.cs` (integration, `RequiresGitCli`; each test scripts 3–5 commits via the fixture).

1. `GetRebasePlan_ShouldListRangeOldestFirst_AllPick`.
2. `Reorder_ShouldSwapHistoryOrder_AndPreserveFinalTree` (compare `repo.Head.Tip.Tree.Sha` before/after).
3. `Reword_ShouldChangeMessage_KeepTree`.
4. `Squash_ShouldCombineTwoCommits_WithNewMessage`.
5. `Fixup_ShouldKeepFirstMessage`.
6. `Drop_ShouldRemoveCommitChanges`.
7. `ConflictMidRebase_ShouldThrowMergeConflict_AndContinueAfterResolveCompletesPlan` — uses T-03 `ResolveConflict`.
8. `Abort_ShouldRestoreExactPreRebaseHead`.
9. `Start_ShouldThrowTyped_OnDirtyTree_MergeCommitInRange_FirstItemSquash_AlreadyRebasing` (`[Theory]`-style four guards; repo untouched after each).
10. `GetRebaseProgress_ShouldReportStepAndTotal_MidConflict`.

---

## TI-09 — Graph interactions

**Files:** `GraphHitTesterTests.cs` (pure), `CommitTimelineMenuTests.cs` (ViewModel).

1. HitTester `[Theory]` table: node center hit; just-outside-slop miss; row rounding at scroll offsets 0/half-row/large; label rect hit wins over node; empty space → `None`.
2. `CommitMenu_ShouldHideResetItems_WhenDetachedHead`; `ShouldHideCheckout_OnHeadCommit`.
3. `HardReset_ShouldRequireConfirmation` (fake confirmation service records the ask).
4. Integration reuse: `ResetToCommit` mode table already exists from backfill B-10 — menu tests only verify routing, not git semantics.
5. `PinnedRefs_ShouldPersist_AndOrderFirst` — DbContext round-trip + router input ordering assertion.

---

## TI-10 — Remotes / auto-fetch / push options

**File:** `GitServiceRemoteTests.cs` (integration; two bare remotes from the fixture).

1. `Remotes_CrudRoundTrip` — add/rename/remove reflected in `GetRemotes`; duplicates/missing → typed.
2. `Push_ShouldUseTrackedRemote_NotOrigin` — branch tracks `upstream` (second bare remote) → push lands there, `origin` unchanged.
3. `Operations_ShouldThrowRemoteNotFound_WithZeroRemotes`.
4. `PushForceWithLease_ShouldSucceed_WhenRemoteUnmoved` (after local amend).
5. `PushForceWithLease_ShouldThrowTyped_WhenRemoteMoved` — second clone pushes first. **The safety property; never skip.**
6. `PushSetUpstream_ShouldWriteBranchConfig`.
7. `AutoFetchService_ShouldFetch_RaiseEvent_AndRecordTimestamp` (interval shrunk for test); `ShouldSkip_WhileOperationInProgress` (hold a merge conflicted); `ShouldNotOverlapItself` (slow fake fetch + two ticks → one execution).

---

## TI-11 — Blame

**File:** `GitServiceBlameTests.cs` (integration).

1. `GetBlame_ShouldMapLinesToCommits` — 3 commits touching disjoint lines → each line's `Sha` correct.
2. `GetBlame_StartingAtPriorCommit_ShouldIgnoreNewerCommit`.
3. `GetBlame_ShouldThrowTyped_OnPathMissingAtRevision`.
4. Cache: `GetBlame_ShouldInvalidate_OnHeadChange` (same call after a new commit reflects it).
5. ViewModel (TI-00): `BlameGutter_ShouldCancelStaleLoad_OnFileSwitch` (TCS-held fake; assert no stale render callback).

---

## TI-12 — File history

**File:** `GitServiceFileHistoryTests.cs` (integration).

1. `GetFileHistory_ShouldReturnOnlyTouchingCommits_NewestFirst` (modified in commits 1,3,5 of 6).
2. `GetFileHistory_ShouldFollowRename_WithHistoricalPaths`.
3. `GetFileAtCommit_ShouldReturnBlobText_AndThrowTypedOnBinary`.
4. `GetFileDiffBetweenCommits_ShouldMatchTreeDiff` (compare with a directly computed `repo.Diff.Compare<Patch>`).
5. Line-history filter (pure, reuses PatchParser): `LineRangeFilter_ShouldKeepVersionsIntersectingRange`.

---

## TI-13 — Diff quality

**Files:** `IntraLineDiffTests.cs` (pure), `GitServiceWhitespaceDiffTests.cs` (integration, `RequiresGitCli`).

1. `HighlightSpans_SingleWordChange_ShouldSpanOnlyThatWord`.
2. `HighlightSpans_FullRewrite_ShouldSpanWholeLine`.
3. `HighlightSpans_ShouldNeverSplitSurrogatePairs` (emoji/ZWJ `[Theory]`).
4. `GetFileDiff_IgnoreWhitespace_ShouldYieldZeroHunks_ForIndentOnlyChange`.
5. `GetFileDiff_IgnoreWhitespace_ShouldKeepRealHunks_InMixedChange`.
6. ViewModel: `PartialStagingActions_ShouldBeHidden_InWhitespaceIgnoredMode`.
7. Image detection (pure): `IsImageCandidate_ByExtensionAndBinaryFlag` table.

---

## TI-14 — Multi-host auth + SSH

**Files:** `HostProviderRegistryTests.cs`, `SshKeyServiceTests.cs` (pure/process), keyring round-trip in `SecureKeyringTests.cs`.

1. `Resolve_ShouldPickProviderByHostAndKind` — github.com→GitHub; gitlab self-hosted with hint→GitLab; unknown→Generic.
2. `TokenUsername_ShouldMatchHostConvention` — single source of truth (delete-the-duplicate check: this test references the same member `RunGitCheckedAuthenticated` uses).
3. `SshKeygen_ArgConstruction_ShouldUseArgumentList_NeverShellString` (assert the built `ProcessStartInfo`).
4. `SecureKeyring_SaveRetrieveDelete_RoundTrip` and `Retrieve_ShouldReturnNull_OnCorruptPayload` (write garbage to the `.keyring` file). Point the keyring at a temp dir if the constructor gains a path override — add that override in T-14 for testability.
5. Manual matrix (documented in the PR, not automated): device flow GitHub + GitLab; PAT Bitbucket/AzDO; SSH with passphrase; **process-listing check that no secret appears in argv** during each.

---

## TI-15 — Signing

**File:** `GitServiceSigningTests.cs` (`RequiresGpg`, skip when absent; integration).

1. `Commit_WithSigningOn_ShouldProduceVerifiableSignature` — fixture generates a throwaway key (`gpg --batch --gen-key`), sets repo config, commits → `git verify-commit HEAD` exit 0; parse `%G?` == `G`.
2. `Commit_WithSigningOff_ShouldShowStatusN`.
3. `SignatureStatusParser_ShouldMapAllCodes` (pure: G/B/U/N/E rows from canned `log --format` output).
4. `SigningFailure_ShouldThrowTyped_NotHang` — configure a bogus `user.signingkey`; command returns within timeout with `GitOperationException`.

---

## TI-16 — Submodules

**File:** `GitServiceSubmoduleTests.cs` (integration, `RequiresGitCli`; superproject + submodule built from two fixtures with `-c protocol.file.allow=always` **in test arrangement only**).

1. `Submodules_FreshClone_ShouldReportUninitialized_ThenUpToDateAfterInit`.
2. `Submodule_InnerCommit_ShouldFlagSuperprojectModified`.
3. `SubmoduleStatusMapping_ShouldCoverAllStates` (pure mapping test over `SubmoduleStatus` flag combinations).
4. Guard: `grep -n "protocol.file.allow" Mainguard.Agents/` → 0 hits (production never sets it) — encoded as a test reading its own source tree? No — reviewer grep in the master doc; do not automate source greps as tests.

---

## TI-17 — LFS

**File:** `GitServiceLfsTests.cs` (`RequiresGitLfs`, skip when absent).

1. `LfsTrack_ShouldWriteGitattributes_AndCommitPointer` — track `*.bin`, commit a binary → `git show HEAD:file.bin` starts with the pointer header.
2. `LfsLsFiles_ShouldListTrackedObject`; `Untrack_ShouldRoundTrip`.
3. `PointerDetection_ShouldIdentifyPointerText` (pure: header prefix, malformed variants).
4. `LfsUnavailable_ShouldDegradeGracefully` — probe result false → feature methods throw a typed "LFS not installed" (never attempt the op).

---

## TI-18 — Command palette

**Files:** `FuzzyMatcherTests.cs`, `ActionRegistryTests.cs`, `ShortcutMapTests.cs` (all pure).

1. Matcher ranking table `[Theory]`: `"chb"` ranks "Checkout Branch" above "Cherry-pick branch b"; word-boundary bonus; consecutive-run bonus; non-subsequence → excluded; case-insensitive; empty query → all.
2. `Registry_ShouldFilterByCanExecute`.
3. `Registry_DuplicateIds_ShouldThrowOnRegistration`.
4. `ShortcutMap_ConflictDetection_ShouldFlagDuplicateGesture`; `ShouldRoundTripThroughPreferences`.

---

## TI-19 — Operation journal (undo/redo)

**File:** `OperationJournalTests.cs` (integration; the heart of the feature — one round-trip per op kind, no exemptions).

1. `[Theory]`-driven round-trip: for each kind (Commit, Merge, Rebase, Reset(each mode), Revert, CherryPick, CreateBranch, DeleteBranch, StashPush, TagCreate/Delete, InteractiveRebase): perform → `Undo` → **all** branch SHAs + HEAD symbolic target equal pre-op snapshot → `Redo` → equal post-op snapshot. (Helper: `CaptureRefState(repo)` returning `Dictionary<string,string>` + HEAD target; assert dictionary equality.)
2. `Undo_BranchDelete_ShouldRestoreUpstreamConfig`.
3. `Undo_WithDirtyTree_ShouldRefuseTyped_AndChangeNothing`.
4. `NewOperationAfterUndo_ShouldTruncateRedo`.
5. `NonUndoableOps_ShouldBeJournaledFlagged_WithReason` (push).
6. `Journal_ShouldPersistAcrossContextReopen` (SQLite round-trip).

---

## TI-20 — Reflog · TI-21 — Profiles/worktrees/clone · TI-22 — Analytics

**TI-20 (`GitServiceReflogTests.cs`):** commit→hard-reset→`GetReflog` shows both moves with correct from/to; "create branch here" at the pre-reset entry restores the commit; deleted-branch recovery finds the orphaned tip; destructive action routes through the journal (assert a journal entry exists).

**TI-21:** profile apply writes **local** config only (global file untouched — point global at a temp path); clone progress reports monotonic `ReceivedObjects` and completes (bare-remote clone); **cancelled clone deletes the partial directory** (cancel via the transfer callback, assert dir gone); worktree panel ViewModel validation (branch already checked out → create disabled).

**TI-22:** analyzer on a fixture with an ignored `node_modules/` (+ a `!keep.js` negation) counts exactly the non-ignored bytes; `.git/` always skipped; cancellation honored (start on a large synthetic tree, cancel, assert prompt return); punch-card bucketing exact for scripted commit timestamps (fixed `DateTimeOffset`s, not `Now`).

---

## D. Standing rules for extending this document

1. **Every new task added to the master document gets a TI section here in the same PR** that adds the task. No TI section, no task.
2. Test cases here are contracts: renaming is fine, deleting or weakening requires the same review rigor as changing a public API.
3. When a bug escapes to `main`, the fix PR adds the regression test **and** back-fills the missing row in the relevant TI section so the gap is closed in the spec too.
4. ViewModel tests become mandatory (not aspirational) the moment TI-00 lands. TI-00 should land with T-04 at the latest.
