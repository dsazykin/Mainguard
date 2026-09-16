# Mainguard — Master Implementation Document

> **ARCHIVED — superseded.** Part of the phase-1 planning corpus (July 2026), written before the
> agent platform existed. Kept for provenance only. The active corpus is
> [`docs/phase-2/`](../../phase-2/); the live backlog is
> [`Mainguard_Backlog.md`](../../planning/Mainguard_Backlog.md).

**Date:** 2026-07-03
**Supersedes for execution purposes:** `Mainguard_Implementation_Strategy.md` (which remains the strategic index).
**Companion document:** `Mainguard_Test_Implementation_Strategy.md` (the test contract for every task below — a PR is incomplete without its section there being satisfied).

> **Status (2026-07-07): fully implemented.** Every task below (T-01…T-22, plus the
> follow-on tasks T-23…T-33 in `docs/feature-plans/`) is merged to `main`; the suite stands
> at 1,000+ green tests. `main` is now in release-hardening mode — fixes/polish for these
> features keep targeting `main`. The v2 of this document promised in §3/§5 exists as
> `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md` **on the `phase2` branch**, which is
> where all agent-platform work is built (branch policy: AGENTS.md → Git Hygiene). When the
> core client is released, `phase2` merges back into `main`.

---

## 0. How to use this document

This document is written for two audiences with two different protocols. Read the one that applies to you before touching anything else.

### 0.1 If you are IMPLEMENTING a task

1. Find your task in §4 (tasks are numbered `T-01`…`T-22` in **build order** — do not start a task whose *Depends on* list is not fully merged to `main`).
2. Read the whole task before writing code. The **Contract** section is binding: the listed public types, method signatures, and behaviors must exist exactly as written (namespaces, parameter names and order included). Everything in **Implementation steps** is a known-good path — follow it literally unless you have a concrete reason not to; if you deviate, your implementation must still satisfy every line of the **Invariants** table.
3. Work through **Implementation steps** in order. Each step is deliberately small; finish and build (`dotnet build`) after each step. Do not reorder steps that touch the same file.
4. Handle every row of the **Edge-case matrix**. These are not optional hardening — they are part of the definition of done, and each has a required test.
5. Write the tests listed in the companion test document for your task ID **in the same PR**.
6. Before opening the PR, run every command in the task's **Reviewer verification script** yourself. If any check fails, the PR is not ready.
7. Never bundle two task IDs in one PR.

### 0.2 If you are REVIEWING a PR for a task

A correct implementation may look different from the sample code in this document. The sample code is *one valid implementation*, not the spec. Review against the following, in priority order:

1. **Contract:** every public type/method in the task's Contract section exists with the exact signature. Missing or renamed public surface → request changes.
2. **Invariants (MUST):** each row must demonstrably hold. These are written as observable behaviors precisely so that a structurally different implementation can still pass. If you cannot convince yourself an invariant holds by reading the code, run the verification script or ask for a test that proves it.
3. **Rejection triggers:** any single hit → request changes, no judgment call needed.
4. **Acceptable variations (MAY):** listed to *prevent* review churn. Do **not** request changes for anything in this list (e.g. LINQ vs. loops, different private helper decomposition, different-but-equivalent algorithms) as long as invariants hold.
5. **Tests:** the PR must contain the tests specified for the task in `Mainguard_Test_Implementation_Strategy.md`, and they must pass in CI. A PR that weakens, deletes, or skips an existing test to go green is an automatic request-changes.
6. Run the **Reviewer verification script** locally. It is designed to take < 5 minutes.

### 0.3 Global PR rules (unchanged from the strategy doc, restated as binding)

1. One task = one PR. Foundation work is never bundled with feature work.
2. The PR description links the task ID, lists manual verification performed (with output/screenshots), and names the tests added.
3. Any PR touching `Mainguard.Agents/Services/GitServices.cs` runs the full test suite locally before pushing.
4. No PR may reintroduce: `cmd.exe` shells, secrets in argv or URLs, `repo.Config.BuildSignature` call sites outside `GetSignature`, blocking Git/network work on the UI thread, `Directory.Delete` inside discard paths, or `throw new Exception(...)` / `throw new System.Exception(...)`.

---

## 1. Baseline — what exists on `main` today (2026-07-03)

Any implementer must treat this section as the ground truth of the starting state. All Category-1 audit fixes have **landed**:

| Audit fix | What landed | Where |
|---|---|---|
| 1.1 | Dead 3-way merge stub and orphaned conflict UI **removed** (the real engine is T-02/T-03/T-04 below — it does not exist yet) | — |
| 1.2 | All commit-creating paths route through private `GetSignature(repo)`; throws `GitIdentityMissingException` when unset | `GitServices.cs:299` |
| 1.3 | Push/Pull/Fetch/UpdateProject run off the UI thread in `RepoDashboardViewModel` | `Mainguard.App.Shell/ViewModels/RepoDashboardViewModel.cs` |
| 1.4 | `DiscardChanges` never deletes directories; untracked files go to Recycle Bin on Windows (`SafeDeleteFile`); staged-new files unstaged first | `GitServices.cs:105-163` |
| 1.5 | `Pull(path, PullStrategy)` surfaces conflicts as `MergeConflictException`; `PullStrategy { Default, FastForwardOnly, Rebase }` | `GitServices.cs:437-496`, `Models/PullStrategy.cs` |
| 1.6 | Single hardened cross-platform runner `RunGit` / `RunGitChecked` (ArgumentList, no shell, `GIT_TERMINAL_PROMPT=0`, stderr captured, redacted args) | `GitServices.cs:681-775` |
| 1.7 | Multi-host token auth via git credential-helper mechanism (`RunGitCheckedAuthenticated`, token in child env only); `GitHostDetector`; host-keyed keyring keys `token_<host>` with legacy `github_token` fallback | `GitServices.cs:784-811`, `Security/GitHostDetector.cs` |
| 1.8 | `GetRecentCommits`: single lazy walk, multi-path membership filter (`CommitTouchesAnyPath`), allocation-free text search | `GitServices.cs:863-960` |
| 1.9 | Null-`Tip` guards on branch/create/amend/diff paths; unborn branches skipped in walks | throughout `GitServices.cs` |
| 1.10 | `RepositoryWatcher`: `.lock` churn ignored, static dir denylist (`node_modules`, `bin`, `obj`, …), 250 ms rate cap, debounce | `Services/RepositoryWatcher.cs` |
| 1.11 | Typed exception hierarchy `MainguardException` → `MergeConflictException`, `GitIdentityMissingException`, `AuthenticationRequiredException`, `RemoteNotFoundException`, `GitOperationException`, `SshAuthenticationException` | `Mainguard.Agents/Exceptions/` |
| 1.12 | `CheckoutBranch` captures the remote ref once (no re-lookup); creates tracking local branch | `GitServices.cs:982-1015` |
| 1.13 | Partial staging backend: `StageHunk` / `UnstageHunk` / `DiscardHunk` via `git apply` with the patch on **stdin** | `GitServices.cs:171-247` |

Also present: stash operations (push/list/pop/apply/drop — pop/apply via CLI), branch CRUD, reset/revert/cherry-pick/amend, worktree add/remove/list (basic), `GetDiffAgainstCommit`, `GetBranchDiffAgainstWorkingTree`, `CommitGraphRouter` + `CommitGraphCanvas`, analytics scaffolding, `SecureKeyring` (DataProtection file-backed), `GitHubAuthClient` device flow, `AppDbContext` (SQLite, migrations on startup).

**Key structural facts an implementer must not fight:**

- There is **no DI container**. Services are instantiated directly. Follow the existing pattern.
- All LibGit2Sharp access goes through `IGitService.ExecuteWithRepo(...)` which opens/disposes the native handle per call. Never hold a `Repository` long-lived.
- The test project `Mainguard.Tests` references **Core only** (not the App project). ViewModel tests require the infrastructure task TI-00 in the test strategy doc before they can exist.
- `Mainguard.slnx` is the solution file. `.NET 10` SDK pinned via `global.json`.
- Naming: interface-first services (`IGitService`/`GitService`) in `Mainguard.Agents/Services/`; models in `Mainguard.Agents/Models/`; one View + ViewModel pair per screen resolved by `ViewLocator`.

---

## 2. Global engineering invariants (every PR, every task)

Each invariant comes with the check a reviewer runs. These apply *in addition to* per-task invariants.

| # | Invariant | Reviewer check |
|---|---|---|
| G-1 | No untyped throws in Core/App | `grep -rn "throw new Exception(\|throw new System.Exception" Mainguard.Agents/ Mainguard.App.Shell/` → 0 hits |
| G-2 | No shell execution; git CLI only via the `RunGit` family | `grep -rn "cmd.exe\|UseShellExecute = true" Mainguard.Agents/ Mainguard.App.Shell/` → 0 hits; any new `ProcessStartInfo` outside `GitServices.RunGit`/`ApplyPatch` needs explicit justification in the PR |
| G-3 | Signatures only via `GetSignature` | `grep -n "BuildSignature" Mainguard.Agents/` → exactly 1 hit (inside `GetSignature`); `grep -rn "new Signature(\"Mainguard\"" Mainguard.Agents/` → 0 hits |
| G-4 | No secrets in argv, URLs, logs, or exception text | any credential flows through env/stdin/keyring; `grep -rn "x-access-token:" Mainguard.Agents/` → 0 hits outside `GitHostDetector.UsernameForToken` |
| G-5 | No blocking Git/network work on the UI thread | every new `[RelayCommand]` that calls `IGitService` network/long ops is `async Task` + `Task.Run`, gated by `IsBusy` |
| G-6 | Mutating Git methods ship with an integration test in the same PR | test file exists per the companion doc; CI green |
| G-7 | Policy split: LibGit2Sharp for reads/status/commit/diff; **git CLI** for interactive rebase, worktrees, partial staging, force-with-lease, LFS, stash pop/apply | new features on the wrong side of the split → request changes |
| G-8 | `GIT_TERMINAL_PROMPT=0` on every spawned git process | read the `ProcessStartInfo` setup |
| G-9 | Never `Directory.Delete` on user data in discard/cleanup paths | `grep -n "Directory.Delete" Mainguard.Agents/Services/GitServices.cs` → only test-fixture/temp cleanup, never in discard |
| G-10 | Public Core surface consumed by ViewModels goes behind the interface (`IGitService` or a new `I*Service`) | interface updated in the same PR as the implementation |

---

## 3. Build order and dependency graph

Execute strictly top-to-bottom within a column; a task may start when everything it *Depends on* is merged.

```
T-01 TempRepoFixture harness            (no deps — LANDS WITH THE TEST-BACKFILL PR)
T-02 Merge chunker (pure)               (no deps)
T-03 Conflict index plumbing            (no deps)
T-04 Conflict resolver UI               (T-02, T-03)
T-05 Tag management                     (T-01)
T-06 Partial-staging UI + patch model   (T-01; backend already landed as 1.13)
T-07 Worktree porcelain + commit diffs  (T-01)
T-08 Interactive rebase                 (T-04, T-07)
T-09 Graph interactions                 (T-05; drag-rebase item also wants T-08)
T-10 Remotes mgmt, auto-fetch, push opts(T-01)
T-11 Blame                              (T-01)
T-12 File history                       (T-06 PatchParser reuse)
T-13 Diff quality                       (T-06)
T-14 Multi-host auth UI + SSH manager   (1.7 landed; no code deps)
T-15 Commit/tag signing                 (T-05)
T-16 Submodules                         (T-01)
T-17 LFS                                (T-14 helper plumbing)
T-18 Command palette                    (no deps)
T-19 Undo journal                       (touches every mutating method — schedule after T-05/T-07/T-08 to avoid rebase churn)
T-20 Reflog viewer                      (T-19 for undoable actions)
T-21 Profiles / worktree UI / clone progress (T-07)
T-22 Analytics completion               (no deps)
```

Milestone mapping: T-01…T-04 close out M1/M2 remainders; T-05…T-09 = M3; T-10…T-18 = M4; T-19…T-22 = M5. Phases 6–9 and the installer/Vibe workstreams (F6, G-7.x, H-8.x, I, J, K) are **out of scope for this document version** — their architecture is locked in `Mainguard_Implementation_Strategy.md` and they receive this same deep-specification treatment in a v2 of this document once M3 is complete and the daemon spike starts. Do not begin them from the strategy doc alone.

---

# 4. TASK SPECIFICATIONS

---

## T-01 — `TempRepoFixture` integration-test harness

**Milestone:** M1 remainder · **Priority:** P0 · **Depends on:** nothing.
**Status note:** this task is delivered together with the test-backfill PR that accompanies this document. The spec remains here because every later task builds on it and reviewers need the contract.

### Why

Every task below must land integration tests that init a repo, mutate it, and assert state. Ad-hoc temp-dir code is already duplicated across `GitServicesTests` (three private helpers, four copies of identity setup). One fixture kills the duplication and makes the required tests cheap to write.

### Contract (must exist exactly)

```csharp
namespace Mainguard.Tests.Fixtures;

/// <summary>Disposable temp Git repository with builder helpers for tests.</summary>
public sealed class TempRepoFixture : IDisposable
{
    public string RepoPath { get; }                       // absolute path of the working tree
    public string CommitFile(string relativePath, string content, string message);   // returns the commit SHA
    public string CommitFile(string relativePath, string content, string message,
        string authorName, string authorEmail, DateTimeOffset when);                 // author/date-controlled overload
    public void WriteFile(string relativePath, string content);
    public string CreateBranch(string name);              // returns the branch name; does NOT checkout
    public void Checkout(string name);
    /// <summary>Creates two branches with conflicting edits to <paramref name="relativePath"/>.
    /// Leaves HEAD on <c>ours</c>. Returns (oursBranch, theirsBranch).</summary>
    public (string Ours, string Theirs) CreateConflict(string relativePath, string oursContent, string theirsContent);
    /// <summary>Inits a second bare repo, adds it as remote "origin", pushes HEAD. Returns the bare path.</summary>
    public string AddBareRemote();
    public string ClonePath();                            // clone RepoPath to a new temp dir, returns it
    public void Dispose();                                // force-delete everything it created
}
```

### Implementation steps

1. Create `Mainguard.Tests/Fixtures/TempRepoFixture.cs`. Constructor: `RepoPath = Path.Combine(Path.GetTempPath(), "mainguard-test-" + Guid.NewGuid().ToString("N"))`; `Repository.Init(RepoPath)`; set **local** config `user.name = "test-user"`, `user.email = "test@mainguard.local"`.
2. `WriteFile`: create parent dirs, write text. `CommitFile`: `WriteFile` → `Commands.Stage` → `repo.Commit(message, sig, sig)` with a signature built from the local config (or the overload's explicit author/when); return the commit **SHA as a string** — never a `Commit` object, which would dangle once the handle is disposed. Open/dispose the `Repository` handle inside each helper (mirror the `ExecuteWithRepo` discipline; never cache a handle on the fixture).
3. `CreateConflict(rel, ours, theirs)`: require an initial commit (make one seeding `rel` with `"base\n"` if the repo is empty); create branch `theirs` from HEAD, create branch `ours` from HEAD; on `theirs` commit `theirsContent` to `rel`; checkout `ours`, commit `oursContent` to `rel`; return the pair. HEAD ends on `ours`.
4. `AddBareRemote`: `Repository.Init(barePath, isBare: true)`; `repo.Network.Remotes.Add("origin", barePath)`; push HEAD if born. Track `barePath` for disposal.
5. `Dispose`: recursively clear `FileAttributes.ReadOnly` (Windows pack files), then delete `RepoPath`, every clone, and every bare remote created. Swallow cleanup exceptions (never fail a test in Dispose).

### Invariants (MUST)

| # | Invariant |
|---|---|
| 1 | A test using only the fixture leaks zero directories after the run (double-run check) |
| 2 | Fixture repos have a deterministic local identity and are immune to the developer's global gitconfig for the operations the fixture itself performs |
| 3 | `CreateConflict` produces branches whose merge genuinely conflicts (same line changed both sides) |
| 4 | `AddBareRemote` enables push/pull/fetch tests with **zero network** |
| 5 | Helpers never leave a `Repository` handle undisposed |

### Acceptable variations (MAY)

- Additional helpers (e.g. `CommitAll`, `Tag`) are welcome.
- Internal file layout/naming of private methods is free.
- Whether `ClonePath()` registers the clone for auto-dispose or returns a second fixture — either, as long as invariant 1 holds.

### Rejection triggers

- Fixture caches a `Repository` instance as a field used across helpers.
- Helpers shell out to git for things LibGit2Sharp does (violates G-7 in reverse — the fixture is a *libgit2 consumer* except where the product code itself is CLI-driven).
- Any test in the PR still hand-rolls temp-repo setup that the fixture provides.

### Reviewer verification script

```bash
dotnet test                                    # green
ls ${TMPDIR:-/tmp} | grep mainguard-test- | wc -l  # 0 after the run
grep -rn "GetTempPath" Mainguard.Tests --include=*.cs | grep -v Fixtures | wc -l   # trending to 0 in new tests
```

**Required tests:** see companion doc §TI-01.

---

## T-02 — 3-way merge chunker (pure engine)

**Milestone:** M1 remainder (audit 1.1, part 1) · **Priority:** CRITICAL · **Depends on:** nothing.

### Why

Fix 1.1 *removed* the dead `MergeDiffService` stub; the app currently has **no** merge-conflict engine at all. This task builds the pure text engine (strings in → chunks out, zero repo access) that T-04's UI consumes. Keeping it pure makes it unit-testable without Git and reviewable by reading tests alone.

### Contract (must exist exactly)

```csharp
// Mainguard.Agents/Models/MergeChunk.cs
namespace Mainguard.Agents.Models;

public enum ChunkKind { Unchanged, LeftOnly, RightOnly, Conflict }
public enum ChunkResolution { Unresolved, TakeLeft, TakeRight, TakeBoth, Custom }

public sealed class MergeChunk
{
    public ChunkKind Kind { get; init; }
    public ChunkResolution Resolution { get; set; } = ChunkResolution.Unresolved;
    public string BaseText  { get; init; } = "";   // this chunk's slice of the base
    public string LeftText  { get; init; } = "";   // this chunk's slice of "ours"
    public string RightText { get; init; } = "";   // this chunk's slice of "theirs"
    public string? CustomText { get; set; }        // used when Resolution == Custom
}
```

```csharp
// Mainguard.Agents/Services/IMergeDiffService.cs
namespace Mainguard.Agents.Services;

public interface IMergeDiffService
{
    /// <summary>Splits a 3-way merge into ordered chunks covering the whole document.</summary>
    IReadOnlyList<MergeChunk> GenerateMergeChunks(string? baseText, string? leftText, string? rightText);

    /// <summary>Concatenates chunks per their Kind/Resolution into the merged document.
    /// Throws InvalidOperationException if any Conflict chunk is Unresolved.</summary>
    string AssembleMerged(IEnumerable<MergeChunk> chunks);
}
// Mainguard.Agents/Services/MergeDiffService.cs : IMergeDiffService (public class)
```

### Implementation steps

1. Add the `MergeChunk` model file exactly as above.
2. Create `MergeDiffService`. Normalize inputs: `baseText ??= ""` etc.; record `leftHadTrailingNewline = leftText.EndsWith("\n")` (same for right); split all three on `'\n'` after stripping `'\r'` (`text.Replace("\r\n", "\n")`), dropping the final empty element produced by a trailing newline.
3. Diff base→left and base→right with DiffPlex (already a Core dependency): `Differ.Instance.CreateDiffs(baseJoined, leftJoined, false, false, new LineChunker())`. Each resulting `DiffBlock` gives: base range `[DeleteStartA, DeleteStartA + DeleteCountA)` is replaced by side range `[InsertStartB, InsertStartB + InsertCountB)`.
4. For each side build:
   - `bool[] changed` over base line indexes — mark every index inside a block's delete range;
   - `Dictionary<int, List<string>> insertions` — for blocks with `DeleteCountA == 0`, the inserted side lines keyed by anchor `DeleteStartA` (meaning: inserted **before** base line at that index; index == baseLineCount means insert at EOF).
5. Build regions: walk base indexes `0..N`; an index is *hot* if `leftChanged[i] || rightChanged[i] || leftInsertions.ContainsKey(i) || rightInsertions.ContainsKey(i)`. Coalesce maximal runs of hot indexes into regions `[s, e)`; a pure-insertion anchor with no changed neighbor forms a zero-length region `[i, i)`. Indexes `N` (EOF anchor) can also open a zero-length region.
6. Emit chunks in document order: between regions emit `Unchanged` chunks whose `BaseText`/`LeftText`/`RightText` are all the identical base slice. For each region compute each side's slice: base lines `[s,e)` minus that side's deleted lines, with that side's insertions spliced at their anchors. Classify:
   - side slices both equal base slice → should not happen (region wouldn't be hot) — defend by emitting `Unchanged`;
   - only left differs from base → `LeftOnly`; only right differs → `RightOnly`;
   - both differ and `leftSlice == rightSlice` → **not a conflict**: emit `LeftOnly` (identical independent edits merge cleanly);
   - both differ and slices differ → `Conflict`.
7. Store each slice as lines re-joined with `"\n"` (chunks never carry a trailing `"\n"`; assembly adds separators).
8. `AssembleMerged`: build the merged line list chunk-by-chunk — `Unchanged` → base; `LeftOnly` → left; `RightOnly` → right; `Conflict` → by `Resolution` (`TakeLeft`/`TakeRight`/`TakeBoth` = left then right/`Custom` = `CustomText ?? ""`); `Unresolved` → `throw new InvalidOperationException("Cannot assemble: unresolved conflict chunk.")`. Skip empty slices (a chunk whose chosen text is `""` contributes no lines, e.g. take-ours of a deletion). Join with `"\n"` and append a final `"\n"` iff `leftHadTrailingNewline || rightHadTrailingNewline` (recorded in step 2; carry the flags on the service call — simplest is computing assembly inside the same service instance is NOT allowed since `AssembleMerged` only receives chunks; therefore: **append `"\n"` iff the assembled text is non-empty**, and document that policy in a code comment. Tests pin this exact behavior.)
9. Guard degenerate cases explicitly (they fall out of the algorithm but must be tested): all three inputs empty → return a single empty `Unchanged` chunk (or empty list — pick one, test pins it; the reference choice is **empty list**); base empty with both sides adding different text → one `Conflict` chunk; one side entirely deletes the file → that side's slice is `""`.

### Edge-case matrix

| Input | Required behavior |
|---|---|
| identical `base == left == right` | single `Unchanged` chunk (or empty list when all empty) |
| left edits line 5, right untouched | `Unchanged` + `LeftOnly` + `Unchanged` |
| both edit the same line differently | that region is one `Conflict` chunk |
| both make the *identical* edit | **no** `Conflict` — clean merge |
| non-overlapping edits (left line 2, right line 9) | `LeftOnly` and `RightOnly` chunks separated by `Unchanged`; `AssembleMerged` (no resolutions needed) equals the true merge |
| both insert different text at the same anchor (incl. EOF) | `Conflict` |
| base empty, both sides add different content (add/add) | `Conflict` |
| left deletes whole file, right edits it | `Conflict` with `LeftText == ""` |
| CRLF input | handled identically to LF (normalized) |
| `AssembleMerged` with an `Unresolved` conflict | `InvalidOperationException` |
| `TakeBoth` | left lines then right lines |

### Invariants (MUST)

| # | Invariant |
|---|---|
| 1 | The service is pure: no `Repository`, no file I/O, no statics mutated |
| 2 | Chunks are ordered and **cover the base document exactly** — concatenating `BaseText` of all chunks (with `"\n"` separators, skipping empty) reproduces the base |
| 3 | With zero conflict chunks, `AssembleMerged` needs no resolutions and equals the clean 3-way merge result |
| 4 | Identical edits on both sides never produce `Conflict` |
| 5 | Assembly with any unresolved `Conflict` throws `InvalidOperationException` |
| 6 | No `ChunkKind.Conflict` chunk has `LeftText == RightText` |

### Acceptable variations (MAY)

- Any diff back-end (DiffPlex blocks, DiffPlex models, or a hand-rolled Myers) — invariants and tests are the contract.
- Different internal region representation (interval list, bool arrays, state machine).
- Emitting a single `Unchanged` chunk vs. several adjacent ones **is NOT acceptable variation** — adjacent same-kind chunks must be coalesced (the UI renders per-chunk widgets); reviewers check invariant 2 plus "no two adjacent chunks share the same Kind unless separated by a Conflict".
- Extra convenience members on `MergeChunk` (line numbers for the UI) are fine as additive.

### Rejection triggers

- Repo/file access anywhere in the service.
- Trailing-newline crash or mangling of CRLF inputs.
- Conflict detection by textual `<<<<<<<` markers.
- `AssembleMerged` silently emitting placeholder text for unresolved chunks.

### Reviewer verification script

```bash
dotnet test --filter "FullyQualifiedName~MergeDiffService"      # all green, count ≥ 12
grep -n "Repository\|File\." Mainguard.Agents/Services/MergeDiffService.cs   # no repo/file IO
```

**Required tests:** companion doc §TI-02 (this is the most test-dense task in the milestone — the tests *are* the spec).

---

## T-03 — Conflict index plumbing (`GetConflicts` / `GetConflictBlobs` / `ResolveConflict`)

**Milestone:** M1 remainder (audit 1.1, part 2) · **Priority:** CRITICAL · **Depends on:** nothing (parallel with T-02).

### Why

The chunker needs the three blob texts (base/ours/theirs) for a conflicted path, and the UI needs to enumerate conflicts and write resolutions back to the index. `repo.Index.Conflicts` is the source of truth (Option A from the audit — never parse `<<<<<<<` markers from the working tree).

### Contract (must exist exactly)

```csharp
// Mainguard.Agents/Models/ConflictedFile.cs
namespace Mainguard.Agents.Models;

public sealed class ConflictedFile
{
    public string Path { get; init; } = "";
    public bool HasBase   { get; init; }   // false on add/add conflicts
    public bool HasOurs   { get; init; }   // false when deleted on our side
    public bool HasTheirs { get; init; }   // false when deleted on their side
}
```

```csharp
// added to IGitService + GitService
IReadOnlyList<ConflictedFile> GetConflicts(string repoPath);
(string BaseText, string OursText, string TheirsText) GetConflictBlobs(string repoPath, string path);
void ResolveConflict(string repoPath, string path, string mergedContent);
bool HasUnresolvedConflicts(string repoPath);
```

### Implementation steps

1. `GetConflicts`: inside `ExecuteWithRepo`, map `repo.Index.Conflicts` → `ConflictedFile` (`Path` = `conflict.Ours?.Path ?? conflict.Theirs?.Path ?? conflict.Ancestor!.Path`; the three `Has*` flags = null-checks of `Ancestor`/`Ours`/`Theirs`). Order by `Path` ordinal for a stable UI.
2. `GetConflictBlobs`: find the conflict entry for `path` (`repo.Index.Conflicts[path]`); throw `GitOperationException($"No conflict recorded for '{path}'.")` when absent. For each present stage: `repo.Lookup<Blob>(entry.Id).GetContentText()`; missing stage → `""`.
3. `ResolveConflict`: write `mergedContent` to `Path.Combine(repo.Info.WorkingDirectory, path)` (create parent dirs; write UTF-8 no BOM), then `Commands.Stage(repo, path)` — staging a conflicted path clears its conflict entries in libgit2.
4. `HasUnresolvedConflicts`: `repo.Index.Conflicts.Any()`.
5. Update `IGitService` with all four members (G-10).

### Edge-case matrix

| Case | Required behavior |
|---|---|
| add/add conflict (no ancestor) | `HasBase == false`, `BaseText == ""` |
| modify/delete (theirs deleted) | `HasTheirs == false`, `TheirsText == ""` |
| `GetConflictBlobs` on a non-conflicted path | `GitOperationException` (typed, message names the path) |
| `ResolveConflict` then `Commit` during a merge | commit succeeds with **two parents** (libgit2 auto-uses `MERGE_HEAD`) |
| `ResolveConflict` on a path in a subdirectory | works (parent dirs, path separators normalized to `/` for libgit2) |
| repo with no conflicts | `GetConflicts` → empty list; `HasUnresolvedConflicts` → false |
| binary conflicted file | `GetContentText` may mangle it — acceptable for v1, but must not throw; UI gating is T-04's job |

### Invariants (MUST)

1. Conflict enumeration reads **only** `repo.Index.Conflicts` — no working-tree marker parsing.
2. `ResolveConflict` leaves the index with zero conflict entries for that path and the working tree file equal to `mergedContent`.
3. After all conflicts are resolved, `HasUnresolvedConflicts` is false and a normal `Commit(repoPath, msg)` completes the merge with 2 parents.
4. All four methods go through `ExecuteWithRepo` (handle discipline).

### Acceptable variations (MAY)

- Returning a richer `ConflictedFile` (e.g. per-stage blob ids) — additive only.
- `GetConflictBlobs` implemented via `repo.ObjectDatabase` lookup instead of `repo.Lookup<Blob>`.

### Rejection triggers

- Reading conflict content from the working-tree file (marker soup) instead of index stages.
- `ResolveConflict` committing on its own (commit is a separate explicit user action).
- New `Repository(...)` outside `ExecuteWithRepo`.

### Reviewer verification script

```bash
dotnet test --filter "FullyQualifiedName~Conflict"     # green
grep -n "<<<<<<<" Mainguard.Agents/Services/GitServices.cs # 0 hits
```

**Required tests:** companion doc §TI-03.

---

## T-04 — Conflict-resolution editor (end-to-end)

**Milestone:** M2 (audit 2.3 / roadmap 4.4) · **Priority:** P0 · **Depends on:** T-02, T-03.

### Why

Merge/rebase/cherry-pick/pull all throw `MergeConflictException` today and the user has nowhere to go. This wires a working resolver: conflict list → per-file chunk view → per-chunk take-ours/theirs/both/custom → mark resolved → commit merge / continue rebase.

### Contract

New/changed public surface:

```csharp
// IGitService additions
public enum ConflictSide { Ours, Theirs }                    // Mainguard.Agents/Models/ConflictSide.cs
void ResolveFileWithSide(string repoPath, string path, ConflictSide side);  // git checkout --ours/--theirs -- <path> + stage
CurrentOperation GetCurrentOperation(string repoPath);        // repo.Info.CurrentOperation passthrough
```

ViewModels (in `Mainguard.App.Shell`): `ConflictResolverWindowViewModel` re-created with constructor `(IGitService gitService, IMergeDiffService mergeService, string repoPath, string conflictedPath)`; `ConflictedFilesViewModel` listing `GetConflicts()` with per-file resolved/unresolved state and a "N of M resolved" header; commit-merge / continue-rebase commands gated on `HasUnresolvedConflicts == false`.

### Implementation steps

1. **Service bits.** `ResolveFileWithSide`: run `RunGitChecked(repoPath, "checkout", side == ConflictSide.Ours ? "--ours" : "--theirs", "--", path)` then `Commands.Stage(repo, path)` inside `ExecuteWithRepo`. `GetCurrentOperation`: `ExecuteWithRepo(path, repo => repo.Info.CurrentOperation)`.
2. **Load flow.** In `ConflictResolverWindowViewModel`, on activation run on a background thread: `GetConflictBlobs` → `GenerateMergeChunks` → project to an `ObservableCollection<MergeChunkViewModel>`; marshal to the UI thread with `Dispatcher.UIThread.Post` before touching bound collections. `MergeChunkViewModel` wraps one `MergeChunk` and exposes `TakeOursCommand` / `TakeTheirsCommand` / `TakeBothCommand`, an editable `CustomText`, and `IsResolved`.
3. **Merged preview.** A `MergedPreview` string recomputed on every resolution change: call `AssembleMerged` over a *copy* of the chunks where still-unresolved conflicts render as `<<<<<<< ours / ======= / >>>>>>> theirs` placeholder text (preview only — the placeholder text must never be writable to disk while unresolved).
4. **Per-file completion.** `MarkResolvedCommand` (CanExecute = all chunks resolved): `AssembleMerged` → `ResolveConflict`. Refresh the conflict list.
5. **Delete/modify + add/add handling.** When a stage is missing (flags from `ConflictedFile`), skip the chunk editor for that file and show two file-level actions: "Keep file" (resolve with the surviving side's full text) and "Delete file" (`RunGitChecked(repoPath, "rm", "--", path)`). Label panes "(deleted on this side)".
6. **Session completion.** In `ConflictedFilesViewModel`: when `HasUnresolvedConflicts()` flips false, enable exactly one of: **Commit merge** (when `GetCurrentOperation() == CurrentOperation.Merge`: `Commit(repoPath, GetMergeMessage(repoPath))`) or **Continue rebase** (`CurrentOperation.RebaseMerge` etc.: `ContinueRebase`). Route both through the async/`IsBusy` pattern.
7. **Entry wiring.** Every `catch (MergeConflictException)` in ViewModels routes to opening the conflict window (search: `RepoDashboardViewModel`, `BranchBrowserViewModel`, `StagingPanelViewModel`).
8. Keep code-behind to scroll-sync only; all logic in ViewModels/services (testability + G-5).

### Edge-case matrix

| Case | Required behavior |
|---|---|
| resolve chunks in any order | preview always consistent; gate opens only when all resolved |
| un-resolving is not supported in v1 | once resolved, a chunk may be re-resolved to a different side (state change), but never back to `Unresolved` |
| file with delete/modify conflict | no chunk editor; keep/delete actions; resolving updates the list |
| rebase conflict (not merge) | completion button says/does Continue rebase |
| user closes the window mid-resolution | index untouched for unresolved files; already-`ResolveConflict`ed files stay resolved |
| commit merge with dirty unrelated files | merge commit contains only what the merge staged — do not `StageAll` |

### Invariants (MUST)

1. Disk writes happen **only** in `ResolveConflict` / `ResolveFileWithSide` — never from preview code.
2. Completion gating: commit/continue commands are un-executable while `HasUnresolvedConflicts()` is true.
3. All Git work off the UI thread; bound collections mutated only on `Dispatcher.UIThread`.
4. The final merge commit has two parents; a rebase-conflict flow ends with `ContinueRebase` succeeding.
5. Resolution logic lives in ViewModel/service classes, not code-behind.

### Acceptable variations (MAY)

- 3-pane vs 4-pane layout; gutter design; scroll-sync mechanism.
- Whether `MergeChunkViewModel` recomputes preview incrementally or from scratch.
- Additional "resolve whole file with ours/theirs" buttons anywhere convenient.

### Rejection triggers

- Conflict markers written to the working tree by the preview path.
- String-sniffing exception messages to detect conflicts (typed `MergeConflictException` is the only signal).
- Any new blocking Git call in a `[RelayCommand]` without `Task.Run`.

### Reviewer verification script

```bash
dotnet test --filter "FullyQualifiedName~ConflictResolver|FullyQualifiedName~MergeChunkViewModel"
# Manual (scripted repo): merge two conflicting branches in the UI →
#   resolver opens, mixed resolution (ours/theirs/custom) works,
#   commit-merge button enables only at the end, git log shows a 2-parent commit.
```

**Required tests:** companion doc §TI-04 (ViewModel tests require TI-00 headless infra).

> **Status (2026-07-06):** Implemented and verified — the resolver is a synchronized
> IntelliJ-style 3-pane merge editor (Ours | Result | Theirs) with per-side accept/reject/undo,
> live Result, stacked add/add slots + flow-down connectors, and red/grey/green color semantics,
> all validated through the headless render harness (TI-00). **UI-polish follow-up deferred —
> return to perfect it:** the accept/reject gutters should become *overlays embedded on top of the
> code columns* (code scrolling underneath a continuous highlight) instead of the current dedicated
> fixed-width gutter column; plus a base-line hint on unresolved modify rows and a word-diff
> "Show Details" toggle. Non-blocking; details in `docs/feature-plans/T-04-conflict-resolver-ui.md`
> §12 and `docs/reports/Mainguard_Session_Handoff.md`.

---

## T-05 — Tag management

**Milestone:** M3 (audit 2.4) · **Priority:** P0 · **Depends on:** T-01.

### Contract

```csharp
// Mainguard.Agents/Models/GitTagItem.cs
public sealed class GitTagItem
{
    public string Name { get; init; } = "";
    public string TargetSha { get; init; } = "";   // peeled target commit SHA
    public bool IsAnnotated { get; init; }
    public string? Message { get; init; }           // annotated only
    public string? TaggerName { get; init; }        // annotated only
}

// IGitService additions
IEnumerable<GitTagItem> GetTags(string repoPath);
void CreateTag(string repoPath, string name, string targetSha, string? message); // annotated iff message != null
void DeleteTag(string repoPath, string name);
void PushTag(string repoPath, string remoteName, string name);
void DeleteRemoteTag(string repoPath, string remoteName, string name);
void CheckoutTag(string repoPath, string name);     // detached HEAD at the peeled target
```

### Implementation steps

1. `GetTags`: map `repo.Tags`; for annotated tags (`tag.IsAnnotated`) peel via `tag.PeeledTarget` (must be a `Commit`; if a tag targets a non-commit, skip it defensively) and read `tag.Annotation.Message` / `.Tagger.Name`.
2. `CreateTag`: validate first — `Reference.IsValidName("refs/tags/" + name)` else `GitOperationException($"'{name}' is not a valid tag name.")`; look up the commit (`repo.Lookup<Commit>(targetSha)`, null → typed throw); duplicate name → typed throw **before** calling Add. Annotated path uses `GetSignature(repo)` (G-3).
3. `DeleteTag`: `repo.Tags.Remove(name)` after an exists-check (missing → typed throw naming the tag).
4. `PushTag`: LibGit2Sharp `repo.Network.Push(remote, "refs/tags/" + name + ":refs/tags/" + name, opts)` with the credentials provider; on `LibGit2SharpException` fall back to `RunGitCheckedAuthenticated(repoPath, remoteName, "push", remoteName, "refs/tags/" + name)` (mirror the existing Push fallback pattern).
5. `DeleteRemoteTag`: push the empty refspec `":refs/tags/" + name` (same fallback pattern with `"push", remoteName, "--delete", "refs/tags/" + name`).
6. `CheckoutTag`: resolve tag → peeled commit → `Commands.Checkout(repo, commit)` (detached HEAD).
7. **Graph labels:** in the commit-list pipeline (`GetRecentCommits` consumers / `CommitRowViewModel`), build `Dictionary<string sha, List<string> tagNames>` from `GetTags` once per refresh and render tag chips distinctly from branch labels in `CommitGraphCanvas`/`CommitTimelineView`.
8. **UI:** "Tags" section in `BranchBrowserViewModel` (list + context menu: checkout / push / delete / copy name); `CreateTagDialog` (name box, "annotated" checkbox + message box, target SHA prefilled) launched from the commit context menu. All commands async/`IsBusy`.

### Edge-case matrix

| Case | Required behavior |
|---|---|
| invalid names: `"a b"`, `"-x"`, `"a..b"`, `""` | typed throw **before** any repo mutation |
| duplicate tag name | typed throw, existing tag untouched |
| annotated tag on an old commit | `TargetSha` = peeled commit, not the tag object |
| lightweight tag | `IsAnnotated == false`, `Message == null` |
| checkout tag | HEAD detached at the target; UI shows the detached badge |
| push tag to bare remote (fixture) | remote `refs/tags/<name>` exists afterwards |
| delete remote tag | remote ref gone; local tag untouched |

### Invariants (MUST)

1. Name validation happens before mutation; the repo is never left with a half-created ref.
2. Annotated tags use `GetSignature` (no BuildSignature, no placeholder identity).
3. Tag data flows through `GitTagItem` (ViewModels never touch LibGit2Sharp types — existing convention).
4. Push/delete-remote work against the T-01 bare-remote fixture with no network.

### Acceptable variations

- Graph-label plumbing may live in the ViewModel layer or a small service — reviewer checks rendering + refresh, not placement.
- `PushTag` may push via refspec string or `repo.Network.Push(remote, tag.CanonicalName, ...)` overloads.

### Rejection triggers

- `CheckoutTag` creating a branch implicitly.
- Tag operations bypassing typed exceptions (raw `LibGit2SharpException` escaping to the UI).

**Required tests:** companion doc §TI-05.

---

## T-06 — Partial-staging UI: patch model, builder, and diff-viewer wiring

**Milestone:** M3 (audit 2.13 — backend landed as fix 1.13) · **Priority:** P0 · **Depends on:** T-01.

### Why

`StageHunk`/`UnstageHunk`/`DiscardHunk` exist and are tested at whole-hunk granularity, but nothing *builds* the sub-patches: the diff viewer renders `Patch.Content` as plain text. This task adds the structured patch model, the hunk/line subset builder, and the UI affordances.

### Contract

```csharp
// Mainguard.Agents/Models/DiffHunk.cs
public enum DiffLineKind { Context, Add, Delete }
public sealed class DiffLine
{
    public DiffLineKind Kind { get; init; }
    public string Text { get; init; } = "";          // WITHOUT the +/-/space prefix
    public bool NoNewlineAtEof { get; init; }        // the "\ No newline at end of file" marker applies to this line
}
public sealed class DiffHunk
{
    public int OldStart { get; init; } public int OldCount { get; init; }
    public int NewStart { get; init; } public int NewCount { get; init; }
    public string SectionHeading { get; init; } = ""; // text after the second @@
    public IReadOnlyList<DiffLine> Lines { get; init; } = Array.Empty<DiffLine>();
}
public sealed class FilePatch
{
    public string Header { get; init; } = "";        // everything before the first @@ (diff --git, ---, +++, index, mode lines)
    public IReadOnlyList<DiffHunk> Hunks { get; init; } = Array.Empty<DiffHunk>();
}

// Mainguard.Agents/Services/PatchParser.cs  (pure static class)
public static class PatchParser
{
    public static IReadOnlyList<FilePatch> Parse(string unifiedDiff);
    public static string Serialize(FilePatch patch);          // round-trips byte-identically
}

// Mainguard.Agents/Services/PatchBuilder.cs (pure static class)
public static class PatchBuilder
{
    public static string BuildHunkPatch(FilePatch file, IReadOnlyList<int> selectedHunkIndexes);
    public static string BuildLinePatch(FilePatch file, int hunkIndex, IReadOnlyList<int> selectedLineIndexes);
}
```

### Implementation steps

1. **Parser.** Split the diff into file sections on lines starting `diff --git `; within a section, everything up to the first `@@` line is `Header`. Parse hunk headers with the exact regex `^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$` (missing count = 1). Body lines: prefix `' '` → Context, `'+'` → Add, `'-'` → Delete, `'\'` → set `NoNewlineAtEof` on the **previous** line. Stop a hunk at the next `@@`/`diff --git`/EOF.
2. **Serializer.** Emit `Header` verbatim, then each hunk: `@@ -{OldStart},{OldCount} +{NewStart},{NewCount} @@{SectionHeading}` + body lines with prefixes restored + `\ No newline at end of file` after any flagged line. Property: `Serialize(Parse(x)[0])` is byte-identical to a single-file input `x` (this is the parser's acceptance test).
3. **Hunk subset.** `BuildHunkPatch`: header + selected hunks serialized verbatim, in original order.
4. **Line subset** (the tricky one — this is `git add -p`'s `s`+edit semantics):
   - Within the chosen hunk keep selected `Add`/`Delete` lines as-is.
   - **Unselected `Delete` lines become `Context` lines** (the change isn't taken, so the old text is still context).
   - **Unselected `Add` lines are dropped** entirely.
   - `Context` lines always stay.
   - Recompute counts: `OldCount` = context + deletes in the result; `NewCount` = context + adds in the result; `OldStart` unchanged; `NewStart` = `OldStart` adjusted by nothing for a single-hunk patch (use `NewStart = OldStart` when building against a fresh workdir↔index diff; keep the original `NewStart` otherwise — the reference implementation recomputes `NewStart = hunk.NewStart` and relies on `git apply` tolerance for single-hunk subsets; tests pin correctness by applying the result).
   - Selecting only `Context` lines (or nothing) returns `""` — callers treat empty as no-op (matches the existing `ApplyPatch` guard).
5. **Service glue.** No new service methods needed — the UI feeds `PatchBuilder` output into the existing `StageHunk`/`UnstageHunk`/`DiscardHunk`. **Direction rule:** stage-subsets are built from the workdir↔index diff (`GetFileDiff(path, isStaged: false)`); unstage-subsets from the index↔HEAD diff (`isStaged: true`).
6. **UI.** `DiffViewerViewModel`: parse the current diff into `FilePatch`; render hunk header rows with Stage/Unstage/Discard buttons; line gutter multi-select (click + drag) with context-menu "Stage selected lines" / "Discard selected lines". Discard routes through the existing confirmation dialog with the line/hunk count in the button text. After every partial op: re-fetch the file diff **and** refresh `StagingPanelViewModel` (a file can be staged and modified simultaneously — status already reports both).
7. **Staleness rule.** Always build subsets from a freshly fetched diff. If `git apply` rejects (exit ≠ 0 → `GitOperationException`), refresh the diff and surface "The file changed on disk — selection reset, try again." Never retry with `--recount` silently.

### Edge-case matrix

| Case | Required behavior |
|---|---|
| patch ending `\ No newline at end of file` | parse + serialize round-trip preserves it |
| adjacent hunks (zero context between) | parsed as two hunks; hunk-subset of either applies cleanly |
| rename header (`rename from/to`) | header preserved verbatim by serializer |
| line-subset: only additions selected | valid header counts; applies |
| line-subset: only deletions selected | unselected adds dropped, counts right; applies |
| line-subset: first/last line of hunk | applies |
| nothing selected | `""` → no-op |
| stale patch (file changed since diff) | typed failure surfaced; no silent `--recount` |
| multi-file diff input | `Parse` returns one `FilePatch` per file |

### Invariants (MUST)

1. `PatchParser`/`PatchBuilder` are pure (no IO, no repo access).
2. Round-trip: `Serialize(Parse(x)[i])` reproduces each file section of `x` byte-identically (given LF input).
3. Every builder output either applies cleanly via `git apply` in the integration tests or is `""`.
4. Unstage subsets are built from the index↔HEAD diff, never workdir↔index.
5. Partial-discard goes through the confirmation dialog (data-loss guard, same policy as fix 1.4).

### Acceptable variations

- Parser implemented with regex, spans, or a hand state machine.
- Line-selection UX (checkboxes vs. gutter drag) — behavior, not looks, is specced.
- `NewStart` recomputation strategy, **as long as** the integration tests (apply → exact index/workdir state) pass.

### Rejection triggers

- Building patches by string-slicing the raw diff without parsing (offset bugs).
- Any use of `--recount` or `--unidiff-zero` to paper over wrong counts (`--unidiff-zero` only if the builder genuinely emits zero-context patches by design — the reference design does not).
- Discard-lines without confirmation.

**Required tests:** companion doc §TI-06 (parser round-trip corpus + builder math + integration applies).

---

## T-07 — Worktree porcelain backend + arbitrary-commit diff entry points

**Milestone:** M3 (roadmap 4.5 remainder) · **Priority:** P0 (agent-phase backbone) · **Depends on:** T-01.

### Contract

```csharp
// Mainguard.Agents/Models/WorktreeItem.cs
public sealed class WorktreeItem
{
    public string Path { get; init; } = "";
    public string? HeadSha { get; init; }
    public string? Branch { get; init; }      // friendly name, null when detached
    public bool IsDetached { get; init; }
    public bool IsLocked { get; init; }
    public bool IsMain { get; init; }         // first stanza in porcelain output
}

// IGitService: replace IEnumerable<string> ListWorktrees with:
IReadOnlyList<WorktreeItem> ListWorktrees(string repoPath);
void AddWorktree(string repoPath, string worktreePath, string branchName, bool createBranch); // extend existing
void RemoveWorktree(string repoPath, string worktreePath, bool force);                        // extend existing
void PruneWorktrees(string repoPath);
```

(Breaking the `ListWorktrees` return type is intended; update the single existing call site.)

### Implementation steps

1. `ListWorktrees`: `RunGit(repoPath, "worktree", "list", "--porcelain")`; non-zero → typed throw. Parse stanzas separated by blank lines: `worktree <path>` starts a stanza; then optional `HEAD <sha>`, `branch <refs/heads/name>` (strip the prefix), bare `detached`, `locked[ <reason>]`. First stanza → `IsMain = true`.
2. `AddWorktree`: args `worktree add` + (`-b <branch>` when `createBranch`) + `<path>` + (`<branch>` when not creating). Via `RunGitChecked`.
3. `RemoveWorktree`: `worktree remove` + optional `--force` + path. `PruneWorktrees`: `worktree prune`.
4. Diff entry points (service methods `GetDiffAgainstCommit` / `GetBranchDiffAgainstWorkingTree` already exist): add commit-context-menu command "Diff working tree against this commit" in `CommitTimelineViewModel` reusing the diff viewer.

### Edge cases / invariants

| Case | Required behavior |
|---|---|
| detached worktree | `Branch == null`, `IsDetached == true` |
| locked worktree | `IsLocked == true` |
| worktree path containing spaces | parsed correctly (porcelain format is line-oriented — no quoting issues) |
| add on an already-checked-out branch | typed failure with git's stderr |
| remove with dirty tree, `force == false` | typed failure; with `force == true` succeeds |
| prune after manually deleting a worktree dir | metadata cleaned, `ListWorktrees` shrinks |

MUSTs: all four methods CLI-driven via the `RunGit` family (G-7 — libgit2 worktree API is a locked **no**); porcelain parsing (never the human-readable format); stderr surfaced in typed exceptions.
Rejection triggers: parsing `worktree list` without `--porcelain`; LibGit2Sharp `repo.Worktrees` usage for add/remove.

**Required tests:** companion doc §TI-07.

---

## T-08 — Interactive rebase (CLI-driven)

**Milestone:** M3 (audit 2.1) · **Priority:** P0 · **Depends on:** T-04 (mid-rebase conflicts), T-07 (`RunGit` maturity), F2-landed.

### Contract

```csharp
// Mainguard.Agents/Models/RebaseTodoItem.cs
public enum RebaseAction { Pick, Reword, Squash, Fixup, Edit, Drop }
public sealed class RebaseTodoItem
{
    public string Sha { get; init; } = "";            // full SHA
    public RebaseAction Action { get; set; } = RebaseAction.Pick;
    public string Message { get; init; } = "";        // original subject line
    public string? NewMessage { get; set; }           // for Reword / Squash result
}

// Mainguard.Agents/Services/IInteractiveRebaseService.cs + InteractiveRebaseService.cs
public interface IInteractiveRebaseService
{
    IReadOnlyList<RebaseTodoItem> GetRebasePlan(string repoPath, string baseSha);   // baseSha..HEAD oldest-first, all Pick
    void StartInteractiveRebase(string repoPath, string baseSha, IReadOnlyList<RebaseTodoItem> plan);
    (int Step, int Total)? GetRebaseProgress(string repoPath);                      // null when not rebasing
}
```

Plus `Program.cs` argv modes `--rebase-editor <payloadPath>` and `--rebase-msg <queueDir>` that run **before Avalonia init** and exit.

### Implementation steps

1. **Plan.** `GetRebasePlan`: walk `repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = repo.Head, ExcludeReachableFrom = baseSha, SortBy = Reverse|Topological })` → oldest-first `Pick` items. Refuse (typed) if the range contains a merge commit (v1 blocks; `--rebase-merges` is a follow-up).
2. **Todo generation.** Serialize the (possibly reordered/edited) plan to git-todo format, one line per item: `{action.ToString().ToLowerInvariant()} {Sha} {Message}`; `Drop` items are **omitted** (equivalent and simpler than `drop` lines). Validation before starting (typed throws): plan non-empty after drops; first item is not `Squash`/`Fixup`; working tree clean (`HasUncommittedChanges` → false) ; `IsRebasing` → false.
3. **Editor shims.** Write the generated todo to a temp file. Build env:
   - `GIT_SEQUENCE_EDITOR = "<mainguardExe> --rebase-editor <generatedTodoPath>"` — that mode copies the payload file over the path git passes as its last argument, then exits 0.
   - For rewords/squash messages: write each `NewMessage` (plan order) to numbered files in a temp queue dir; `GIT_EDITOR = "<mainguardExe> --rebase-msg <queueDir>"` — that mode pops the lowest-numbered file, copies it over git's argument, exits 0; empty queue → exit 0 leaving git's default message.
   - `<mainguardExe>` = `Environment.ProcessPath`; quote both command paths (git parses the editor value with the shell — use double quotes around each path).
4. **Run.** `RunGit(repoPath, env, "rebase", "-i", baseSha)`. Exit 0 → done. Non-zero: if `ExecuteWithRepo(.., repo => repo.Index.Conflicts.Any())` → throw `MergeConflictException` (routes to T-04; **do not abort**); else if stopped for `edit` (`.git/rebase-merge/stopped-sha` exists) → surface "paused at <sha>" state; else typed failure with stderr.
5. **Continue/abort.** Reuse existing `ContinueRebase`/`AbortRebase`, but `git rebase --continue` may re-invoke `GIT_EDITOR` — so continue must run through `RunGitChecked(repoPath, sameEnv, "rebase", "--continue")` when an interactive rebase (marker: `.git/rebase-merge/interactive`) is in progress. Add that branch to `ContinueRebase`.
6. **Progress.** Parse `.git/rebase-merge/msgnum` and `end` → `(step, total)`; missing files → null.
7. **UI.** `InteractiveRebaseWindow` + ViewModel: rows with drag-reorder + action dropdown + P/R/S/F/E/D keyboard shortcuts; reword opens inline editor writing `NewMessage`; a live preview folds squash/fixup rows into their predecessor pick. Entry point: commit/branch context menu "Interactive rebase onto here". Validation from step 2 mirrored in `CanExecute`.

### Edge-case matrix

| Case | Required behavior |
|---|---|
| reorder two commits | history order swapped, final tree identical |
| reword | new message, identical tree |
| squash 2→1 | one commit, `NewMessage` used, combined diff |
| fixup | first message kept |
| drop | commit's changes absent from the result |
| conflict mid-rebase | `MergeConflictException`; resolve via T-04; continue completes the remaining plan |
| abort at any point | HEAD/branch exactly at the pre-rebase SHA |
| dirty working tree | refused before starting (typed) |
| plan with first item Squash | refused before starting (typed) |
| Mainguard exe path with spaces | editor shim still works (quoting) |

### Invariants (MUST)

1. libgit2 is never used to drive the interactive rebase (G-7 locked decision).
2. The sequence-editor mechanism is non-interactive and cross-platform: no reliance on `cp`, `sed`, shell built-ins, or a real editor.
3. Failure paths never auto-abort a conflicted rebase.
4. Pre-flight validation happens before any repo mutation.
5. Both generated and applied todo content are logged (Debug-level) for diagnosability.

### Acceptable variations

- A tiny dedicated helper binary instead of argv modes on the main exe (must ship in the publish output).
- `drop` written as explicit `drop` lines instead of omission.
- Progress parsed from `done` instead of `msgnum`.

### Rejection triggers

- `GIT_SEQUENCE_EDITOR` built from shell commands (`"cp a b"`, `"sed -i ..."`).
- Starting a rebase with a dirty tree "because git allows autostash" (autostash is a later opt-in, not a default).
- Editor mode that initializes Avalonia (must exit in milliseconds).

**Required tests:** companion doc §TI-08 (integration, `RequiresGitCli` trait).

---

## T-09 — Rich commit-graph interactions

**Milestone:** M3 (audit 2.2) · **Priority:** P0 · **Depends on:** T-05 (tag actions in menus); the drag-rebase flyout item needs T-08.

### Contract

```csharp
// Mainguard.App.Shell/Controls/GraphHitTester.cs (pure math, unit-testable)
public enum GraphHitKind { None, Node, Label }
public readonly record struct GraphHit(GraphHitKind Kind, string? Sha, string? RefName);
public sealed class GraphHitTester
{
    public GraphHitTester(double rowHeight, double laneWidth, double nodeRadius, double hitSlop);
    public void SetLabelBounds(IReadOnlyList<(Avalonia.Rect Bounds, string RefName, string Sha)> frame); // recorded per render pass
    public GraphHit HitTest(Avalonia.Point p, double verticalScrollOffset, IReadOnlyList<(int RowIndex, int LaneIndex, string Sha)> nodes);
}
```

`ResetToCommit` already supports `ResetMode` — expose Soft/Mixed/Hard in the menu, Hard behind the confirmation dialog.

### Implementation steps

1. **Hit-testing.** Row = `(int)((p.Y + verticalScrollOffset) / rowHeight)`; node hit when `|p.X - laneCenterX(lane)| <= nodeRadius + hitSlop` for the node at that row; label hit via the recorded per-frame rects (labels win over nodes). Keep it a plain class with no Avalonia control dependencies beyond `Point`/`Rect` so it unit-tests.
2. **Context menus.** On right-click with a hit, build `MenuItemViewModel` trees (existing pattern): commit menu = Checkout (detached) / Create branch here / Create tag here (T-05 dialog) / Cherry-pick / Revert / Reset current branch here → Soft·Mixed·Hard(confirm) / Interactive rebase onto here (T-08) / Copy SHA. Branch-label menu = the existing Phase-4.3 branch menu. Menu construction lives in `CommitTimelineViewModel` (testable), rendering in the canvas.
3. **Context rules:** detached HEAD hides "Reset current branch here"; menu on the HEAD commit hides "Checkout"; unborn/empty graph → no menu.
4. **Drag-and-drop merge/rebase.** Drag branch label A onto label B → flyout with exactly two actions: "Merge A into B" and "Rebase A onto B". v1 requires B checked out for merge — otherwise the flyout offers "Checkout B, then merge A" as the action text.
5. **Pinning + filter.** `PinnedRefs` (per-repo, persisted via `AppDbContext` — new table + migration); pinned refs order first into `CommitGraphRouter` input (earlier refs get left-most lanes). "Current branch only" toggle rebuilds the walk with `IncludeReachableFrom = { HEAD, upstream }`.
6. `Delete` key on a selected branch label = delete branch with the existing safety dialog.

### Invariants / rejection triggers

MUSTs: hit-testing math is pure and unit-tested at scroll offsets; every menu action routes through async/`IsBusy` + typed-exception handling; hard reset always confirms; pinned refs persist across restart.
Rejection: hit-testing buried in the canvas control untestably; menu commands calling `IGitService` synchronously on the UI thread; a merge implemented in-memory against a non-checked-out branch.

**Required tests:** companion doc §TI-09.

---

## T-10 — Remotes management, auto-fetch, push options

**Milestone:** M4 (audit 2.14) · **Priority:** P1 · **Depends on:** T-01.

### Contract

```csharp
// Mainguard.Agents/Models/GitRemoteItem.cs
public sealed class GitRemoteItem { public string Name { get; init; } = ""; public string FetchUrl { get; init; } = ""; public string? PushUrl { get; init; } }

// IGitService additions
IReadOnlyList<GitRemoteItem> GetRemotes(string repoPath);
void AddRemote(string repoPath, string name, string url);
void RemoveRemote(string repoPath, string name);
void RenameRemote(string repoPath, string oldName, string newName);
void Fetch(string repoPath, string remoteName, bool prune = false);      // overload; existing Fetch(repoPath, prune) delegates to tracked-or-origin
void PushForceWithLease(string repoPath, string remoteName, string branchName);
void PushTags(string repoPath, string remoteName);
void PushSetUpstream(string repoPath, string remoteName, string branchName);

// Mainguard.Agents/Services/AutoFetchService.cs
public sealed class AutoFetchService : IDisposable
{
    public AutoFetchService(IGitService git, Func<UserPreferences> prefs);
    public void Watch(string repoPath);      // begin periodic fetch for a repo
    public void Unwatch(string repoPath);
    public event Action<string /*repoPath*/>? Fetched;   // raised after each successful fetch
    public DateTimeOffset? GetLastFetched(string repoPath);
}
```

`UserPreferences` gains `int AutoFetchMinutes` (default 10, 0 = off).

### Implementation steps

1. Remotes CRUD via LibGit2Sharp (`repo.Network.Remotes.Add/Remove/Rename`), typed throws on duplicates/missing, name validation (`Remote` name rules: no whitespace, no `..`, not empty).
2. **Kill hardcoded `"origin"`.** Sweep `grep -n '"origin"' Mainguard.Agents/` — every call site resolves the remote as: tracked branch's `branch.RemoteName` → else `"origin"` if it exists → else the single existing remote → else typed `RemoteNotFoundException`. Extract a private helper `ResolveRemoteName(Repository repo, string? preferred = null)` used by Push/Pull/Fetch/PushBranch/DeleteBranch.
3. **Force-with-lease / tags / set-upstream:** all three via `RunGitCheckedAuthenticated` (`push --force-with-lease <remote> <branch>`, `push <remote> --tags`, `push -u <remote> <branch>`); libgit2 has no lease support (G-7).
4. **AutoFetchService:** one background loop (`PeriodicTimer`) over the watched set; per tick and per repo: skip when a Git op is running (`repo.Info.CurrentOperation != None`) or preferences disable it; call `Fetch(repo, prune: true)` in try/catch — failures are logged and counted, never toasted; raise `Fetched` on success and record the timestamp. Surface "last fetched N min ago" + dimming (> 15 min) next to the ahead/behind badge (closes the 1.12 stale-badge plumbing).
5. UI: Remotes sidebar section (add/edit/remove dialogs), push split-button (normal / force-with-lease / push tags / set upstream), prune toggle on manual fetch.

### Edge-case matrix (key rows)

| Case | Required behavior |
|---|---|
| repo with two remotes, tracked branch on `upstream` | Push/Fetch target `upstream`, not `origin` |
| repo with zero remotes | typed `RemoteNotFoundException`, friendly message |
| force-with-lease when the remote moved (second clone pushed first) | **fails typed** — this is the safety property |
| force-with-lease after local amend, remote unmoved | succeeds |
| `-u` push | `branch.<name>.remote` + `.merge` config set |
| auto-fetch during a merge/rebase | skipped, no interference |
| auto-fetch network failure ×3 | subtle warning state, zero modal/toast spam |

### Invariants / rejection triggers

MUSTs: the lease failure test exists and passes; zero remaining hardcoded `"origin"` outside `ResolveRemoteName`'s fallback; auto-fetch never runs concurrently with itself per repo.
Rejection: `push --force` anywhere (`--force-with-lease` only); auto-fetch on the UI thread or via a `DispatcherTimer` in Core.

**Required tests:** companion doc §TI-10.

---

## T-11 — Blame / inline annotations

**Milestone:** M4 (audit 2.10) · **Priority:** P1 · **Depends on:** T-01.

### Contract

```csharp
// Mainguard.Agents/Models/BlameLine.cs
public sealed class BlameLine
{
    public int LineNumber { get; init; }           // 1-based, current file
    public string Sha { get; init; } = "";
    public string ShortSha { get; init; } = "";    // 8 chars
    public string AuthorName { get; init; } = "";
    public DateTimeOffset When { get; init; }
    public string Summary { get; init; } = "";     // commit MessageShort
}
// IGitService
IReadOnlyList<BlameLine> GetBlame(string repoPath, string path, string? startingSha = null);
```

### Implementation steps

1. `repo.Blame(path, new BlameOptions { StartingAt = startingSha ?? "HEAD" })`; expand each `BlameHunk` (`FinalStartLineNumber`, `LineCount`, `FinalCommit`) into per-line rows. File missing at that revision → typed throw naming the path.
2. Cache per `(repoPath, path, headSha)` in a small bounded dictionary (e.g. 32 entries LRU) invalidated on `RepositoryWatcher.RepositoryChanged`.
3. UI: toggleable AvaloniaEdit gutter margin (`author · shortSha · relative date`, alternating dim on commit boundaries); tooltip = full SHA + summary; click selects the commit in the timeline (`WeakReferenceMessenger`). Compute on `Task.Run` with `CancellationToken` cancelled on file switch; spinner in the gutter header.

Invariants: per-line mapping correct for 3 disjoint-edit commits (test); blame never blocks the UI thread; rapid file switching never renders a stale gutter (cancellation).
Rejection: blame run synchronously in a property getter; unbounded cache.

**Required tests:** companion doc §TI-11.

---

## T-12 — File history & line history

**Milestone:** M4 (audit 2.11) · **Priority:** P1 · **Depends on:** T-06 (`PatchParser` reuse for line history).

### Contract

```csharp
// Mainguard.Agents/Models/FileVersion.cs
public sealed class FileVersion { public string Sha { get; init; } = ""; public string PathAtCommit { get; init; } = ""; public string MessageShort { get; init; } = ""; public DateTimeOffset When { get; init; } = default; public string AuthorName { get; init; } = ""; }
// IGitService
IReadOnlyList<FileVersion> GetFileHistory(string repoPath, string path);   // newest-first, rename-following
string GetFileAtCommit(string repoPath, string sha, string path);          // blob text; binary → typed throw with IsBinary info
string GetFileDiffBetweenCommits(string repoPath, string olderSha, string newerSha, string path);
```

### Implementation steps

1. `GetFileHistory`: `repo.Commits.QueryBy(path, new CommitFilter { SortBy = Topological | Time })` mapping `LogEntry.Path` (rename tracking comes free) + `LogEntry.Commit`.
2. `GetFileAtCommit`: `commit[path]?.Target as Blob`; null → typed throw; `blob.IsBinary` → typed throw `GitOperationException("binary file")` (UI shows placeholder).
3. `GetFileDiffBetweenCommits`: `repo.Diff.Compare<Patch>(older.Tree, newer.Tree, new[] { path })`, `.Content`.
4. UI `FileHistoryView`: left = virtualized version list; right = diff of selected vs previous version via the existing diff control. Entry points: staging panel + diff viewer context menus.
5. Line history v1: given a selected line range, filter the file history to versions whose patch (parsed with `PatchParser`) has a hunk intersecting the range; document that this approximates `git log -L`.

Invariants: history spans renames with correct `PathAtCommit`; adjacent-version diff equals `git diff a b -- path`; binary files never render garbage.
**Required tests:** companion doc §TI-12.

---

## T-13 — Diff quality: intra-line, syntax highlighting, whitespace, images

**Milestone:** M4 (audit 2.16) · **Priority:** P1 · **Depends on:** T-06.

### Steps (condensed contract — full details in strategy §C-2.16, unchanged)

1. **Intra-line:** for Modified line pairs in `SideBySideDiffRows`, compute word-level spans (`Differ.CreateWordDiffs`) into `GitDiffLine.HighlightSpans` (`List<(int Start, int Length)>` — new property); render as darker runs. Spans must never split a surrogate pair (test with emoji).
2. **Syntax highlighting:** `AvaloniaEdit.TextMate` + `TextMateSharp.Grammars`, grammar by extension, preference toggle to disable.
3. **Whitespace toggle:** `GetFileDiff(repoPath, path, isStaged, bool ignoreWhitespace)` overload → when on, `RunGit(repoPath, "diff", "-w", ...)` parsed via `PatchParser`; UI notes partial staging is disabled in this mode (offsets differ — enforce by hiding stage buttons, not by letting apply fail).
4. **Image diff:** binary + image extension ({png,jpg,jpeg,gif,bmp,webp,ico}) → load both blob revisions into bitmaps, side-by-side + opacity slider (`ImageDiffControl`); other binaries → "Binary file changed (old → new size)".

Invariants: whitespace-only change with `-w` → zero hunks; 5k-line diff keeps 60 FPS (profile in PR); partial staging genuinely unavailable in whitespace-ignored view.
**Required tests:** companion doc §TI-13.

---

## T-14 — Multi-host auth UI + SSH key manager

**Milestone:** M4 (audit 2.8; fix 1.7 is the foundation) · **Priority:** P1.

### Steps (contract summary)

1. `IHostProvider` in `Mainguard.Agents/Sync/`: `{ string Host; bool SupportsDeviceFlow; string TokenUsername; Task<string> AcquireTokenAsync(CancellationToken); }` — refactor `GitHubAuthClient` into `GitHubProvider`; add `GitLabProvider` (device flow), `BitbucketProvider`/`AzureDevOpsProvider`/`GenericHostProvider` (PAT dialog v1). `HostProviderRegistry.Resolve(host, HostKind)` chooses.
2. `GetCredentialsProvider`/`RunGitCheckedAuthenticated` already key off `GitHostDetector` — extend the username mapping to consult the registry (single source of truth; delete the duplicate switch if one appears).
3. `SshKeyService`: generate (`ssh-keygen -t ed25519 -f <path> -N <passphrase>` via ArgumentList — never a shell string), list `~/.ssh` keys, copy public key; passphrase stored as `sshpass_<sanitized-keypath>` in `SecureKeyring`. `GetCredentialsProvider` returns `SshUserKeyCredentials` for SSH-form remotes.
4. Preferences "Accounts" page: per-host rows, add-account flow (device flow or PAT), SSH keys page.

Invariants: no secret ever in argv/URL (extends 1.7's invariant to SSH passphrases); token keys remain `token_<host>` (compat with landed keyring); unknown host with no token → `AuthenticationRequiredException(host)` routed to the PAT dialog.
**Required tests:** companion doc §TI-14.

---

## T-15 — Commit & tag signing

**Milestone:** M4 (audit 2.7) · **Priority:** P1 · **Depends on:** T-05.

### Steps (contract summary)

1. When `UserPreferences.SignCommits` is on, the commit path branches to `RunGitChecked(repoPath, "commit", "-m", message)` so git orchestrates gpg/ssh signing from repo config (`commit.gpgsign`, `user.signingkey`, `gpg.format`). Unsigned path stays LibGit2Sharp. Same branch for `CreateTag` → `tag -s`.
2. Verification badges: batch `RunGit(repoPath, "log", "--format=%H %G? %GS", "<range>")` for visible commits; map `%G?` (G/B/U/N…) to a badge + tooltip on `CommitRowViewModel`.
3. Preferences: signing on/off, format, key picker (`gpg --list-secret-keys --keyid-format long` / `~/.ssh/*.pub`), gpg program override.

Invariants: signing failures surface typed (never hang — `GIT_TERMINAL_PROMPT=0` inherited from `RunGit`); unsigned repos show no badges (no `%G?` cost when the column is off).
**Required tests:** companion doc §TI-15 (gpg-gated trait).

---

## T-16 — Submodules · T-17 — Git LFS

**Milestone:** M4 · **Priority:** P1. These two follow the strategy doc (§C-2.5, §C-2.6) verbatim with these binding clarifications:

- Submodule reads via `repo.Submodules` → `SubmoduleItem { Path, Url, HeadSha, Status(enum Uninitialized|UpToDate|Modified|Dirty) }`; all mutations via `RunGitChecked` (`submodule update --init --recursive`, `update --remote <path>`, `sync`).
- Tests pass `-c protocol.file.allow=always` **in test setup only** — a rejection trigger is that flag appearing in production code paths.
- LFS: probe `RunGit(repoPath, "lfs", "version")` once, cache availability; all ops CLI (`lfs install --local`, `track`, `untrack`, `ls-files`, `pull`, `prune` with dry-run+confirm). Pointer detection: file content starts `version https://git-lfs.github.com/spec/v1` → diff viewer shows "LFS object (size)" not pointer text.
- Both: sidebar panels, async commands, typed errors.

**Required tests:** companion doc §TI-16 / §TI-17.

---

## T-18 — Command palette & shortcuts

**Milestone:** M4 (audit 2.15) · **Priority:** P1 · **Depends on:** nothing.

Contract summary: `ActionRegistry` in Core (`AppAction { Id, Title, Category, Func<bool> CanExecute, Func<Task> Execute }`, UI-free — it later becomes the agent command surface); `FuzzyMatcher` (pure, ~80 lines, subsequence scoring with consecutive-run + word-boundary bonuses); `CommandPaletteViewModel` (Ctrl+P overlay over actions + branch names + bookmarked repos); `ShortcutMap` persisted in `UserPreferences` with rebind UI + conflict detection. Defaults: Ctrl+P palette, Ctrl+Enter commit, Ctrl+Shift+P push, F5 refresh, Ctrl+B new branch.

Invariants: matcher is pure and property-tested (ranking table); disabled actions filtered by `CanExecute`, not hidden post-hoc crash; rebinds survive restart.
**Required tests:** companion doc §TI-18.

---

## T-19 — Operation journal: unlimited undo/redo

**Milestone:** M5 (audit 2.9) · **Priority:** P2 differentiator, P0 for the agent phase safety net · **Depends on:** T-05/T-07/T-08 merged (instrumentation sweep touches every mutating method — do it once, late).

Contract summary (full algorithm in strategy §D-2.9, binding here):

```csharp
public interface IOperationJournal
{
    IDisposable BeginOperation(string repoPath, string kind, string description); // snapshots refs+HEAD on create, post-state on dispose
    IReadOnlyList<JournalEntry> GetHistory(string repoPath, int take = 100);
    void Undo(string repoPath, long entryId);
    void Redo(string repoPath, long entryId);
}
```

Binding behaviors: undo restores every recorded ref via `repo.Refs.UpdateTarget`, hard-resets the tree **only after** a clean-tree check (dirty → typed refusal, nothing changed); branch-delete undo recreates branch + upstream; commit undo = mixed reset to parent; any new mutating op truncates redo; non-undoable ops (push, stash-pop-with-conflicts) journaled + flagged with a reason. Every mutating `GitServices` method wraps itself in `using var op = journal.BeginOperation(...)`. SQLite persistence via `AppDbContext` + migration.

Invariants (the whole feature): for every op kind, `op → Undo` restores *all* branch SHAs + HEAD target byte-exactly, and `Redo` restores post-state; undo-with-dirty-tree refuses and mutates nothing.
**Required tests:** companion doc §TI-19 — one round-trip test per op kind, no exceptions.

---

## T-20 — Reflog viewer · T-21 — Profiles / worktree UI / clone progress · T-22 — Analytics completion

These follow strategy §§D-2.12, D-2.17, E-5.1–5.4 with the same conventions as everything above (typed errors, async commands, interface-first, tests-with-PR). Binding additions:

- **T-20:** `GetReflog(repoPath, refName = "HEAD", take = 200)` → `ReflogItem { FromSha, ToSha, Message, When }`; destructive per-entry actions route through T-19's journal so even reflog-driven resets are undoable.
- **T-21:** clone progress via `CloneOptions.OnTransferProgress`/`OnCheckoutProgress` → `IProgress<CloneProgress>`; **cancelled clone must delete the partial directory**; profile apply writes local repo config only (never global).
- **T-22:** `RepositoryAnalyzer` walk becomes gitignore-aware (`repo.Ignore.IsPathIgnored`, cached per-dir) + always skips `.git/`; runs with `CancellationToken`; churn/punch-card per strategy.

**Required tests:** companion doc §TI-20/21/22.

---

## 5. Later phases (6–9, J, K)

The BYOK key management (F6), daemon/gRPC (G-7.0), terminal engine (G-7.1a–d), sandbox/provisioner/gateway (G-7.2a–d), lifecycle/merge-queue (G-7.3), swarm UI (G-7.4), orchestration (G-7.5), enterprise governance (H-8.x), cloud worktrees (I), installer (J) and Vibe mode (K) remain specified at strategy level in `Mainguard_Implementation_Strategy.md` with their architecture locked. **They must not be started from that document alone.** When M3 completes, this document gets a v2 appendix giving those workstreams the same contract/invariant/step treatment, informed by what the Git-client milestones taught us. The locked decisions (WSL2 + gRPC, Git-objects-only boundary, no Windows bind mounts, FSL split, `--force-with-lease` only, etc.) are restated in §2 and the strategy appendix and already bind any exploratory spikes.
