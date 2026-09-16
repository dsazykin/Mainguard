# Mainguard — Master Implementation Strategy

> **ARCHIVED — superseded.** Part of the phase-1 planning corpus (July 2026), written before the
> agent platform existed. Kept for provenance only. The active corpus is
> [`docs/phase-2/`](../../phase-2/); the live backlog is
> [`Mainguard_Backlog.md`](../../planning/Mainguard_Backlog.md).

**Date:** 2026-07-02
**Synthesized from:** `Mainguard_Git_Audit_And_Roadmap.md`, `Mainguard_Roadmap.md`, `Implementation_Plan.md`
**Audience:** Any developer picking up a task cold. Every task below is scoped to be independently implementable and PR-verifiable.

> **Path note:** the audit refers to `Mainguard.Agents/Services/GitService.cs`; the actual file on `main` is
> **`Mainguard.Agents/Services/GitServices.cs`** (interface: `IGitService.cs`). Line references in the audit map to that file.

---

## 0. Global Engineering Conventions (read before picking up any task)

These conventions apply to **every** PR and are established by the Foundation tasks (F1–F4) below.

1. **No `throw new System.Exception`.** All new/modified code throws the typed hierarchy from `Mainguard.Agents/Exceptions/` (task F1).
2. **No blocking Git/network work on the Avalonia UI thread.** ViewModels use `async Task` `[RelayCommand]`s + `Task.Run`, with `IsBusy` + `CanExecute` gating (task 1.3 establishes the pattern).
3. **All CLI fallbacks go through the single hardened `RunGit` runner** (task F2). Never `cmd.exe`, never secrets in argv, never `UseShellExecute = true`.
4. **All commit-creating paths obtain signatures via `GetSignature(repo)`** (task F3). No `repo.Config.BuildSignature(...)` call sites outside the helper.
5. **Every PR that touches a mutating Git method adds an integration test** using the `TempRepoFixture` harness (task F4). The current suite has zero coverage of commit/merge/rebase/stash/branch/discard/conflict flows — each task closes its own gap.
6. **Policy split (locked):** LibGit2Sharp for reads/status/commit/diff; the **`git` CLI drives interactive rebase, worktrees, partial staging (`apply --cached`), force-with-lease, and LFS** — these are unsupported or unreliable in libgit2.
7. **Cross-boundary rule (locked architecture):** Windows↔WSL2 state exchange is **Git objects only** (`fetch`/`merge` via the `mainguard-vm` remote). No Windows-path bind mounts into containers, no SQLite files shared across the 9P boundary, no global auth-directory mounts.

### Dependency graph (build order)

```
F1 exceptions ─┬─► F3 signature ─► 1.2 crash fixes
F2 RunGit ─────┤
F4 test harness┴─► 1.1 merge engine ─► 2.3 conflict UI ─► 1.5 pull conflicts
                                     └► 2.1 interactive rebase (also needs F2)
1.3 async VMs ─► all long-op UX
1.11/F1 typed errors ─► ViewModel routing (conflict resolver, auth prompts)
2.13 partial staging, 2.4 tags, 2.2 graph menus  (parallel after Cat-1 core)
Phase 7.1a terminal ─► 7.2 sandbox engine ─► 7.3 lifecycle ─► 7.4 UI ─► 7.5 orchestration
7.1b libvterm gated by 7.1c harness; 7.1c starts alongside 7.1a
Phase 8 rides on 7.x audit hooks; Phase 9 reuses 7.x gRPC contract unchanged
```

---

# WORKSTREAM F — FOUNDATIONS (do these first; everything depends on them)

---

## F1 — Typed Exception Hierarchy (audit 1.11, part 1)

**Priority:** P0 / Foundation — blocks correct UI routing for every other task.

**Objective & Context:** Today every failure is `throw new System.Exception(msg)` and ViewModels string-sniff messages (`UpdateProject` does `ex.Message.Contains("conflict")`). The UI must react differently to conflicts (open resolver), auth failures (prompt), missing identity (identity dialog), and generic errors. A typed hierarchy is the contract every subsequent task throws into.

**Step-by-Step Implementation Guide:**
1. Create `Mainguard.Agents/Exceptions/MainguardException.cs` with the base class and derived types (below). Keep `SshAuthenticationException` and re-parent it to `MainguardException`.
2. Sweep `GitServices.cs` for `throw new System.Exception` / `throw new Exception` and replace each with the most specific type. Wrap raw `LibGit2SharpException`s at the `ExecuteWithRepo` boundary into `GitOperationException` (preserve inner exception).
3. In `Merge`, `Rebase` (and later `Pull`, task 1.5) throw `MergeConflictException` when `MergeStatus.Conflicts` / `RebaseStatus != Complete`.
4. Update ViewModels (`RepoDashboardViewModel`, `BranchBrowserViewModel`, `StagingPanelViewModel`) to catch specific types: `MergeConflictException` → open `ConflictResolverWindow`; `AuthenticationRequiredException` → open `DeviceFlowAuthDialog`; `GitIdentityMissingException` → open the identity dialog (task F3); everything else → notification banner with the message.
5. Delete the `ex.Message.Contains("conflict")` check in `UpdateProject`'s caller.

**Code Modifications & Additions:**
- **Target files:** new `Mainguard.Agents/Exceptions/MainguardException.cs` (or one file per type); modify `Mainguard.Agents/Exceptions/SshAuthenticationException.cs`, `Mainguard.Agents/Services/GitServices.cs`, `Mainguard.App.Shell/ViewModels/RepoDashboardViewModel.cs`, `BranchBrowserViewModel.cs`, `StagingPanelViewModel.cs`.
- **Architecture & Methods:**

```csharp
namespace Mainguard.Agents.Exceptions;

public class MainguardException : Exception
{
    public MainguardException(string message, Exception? inner = null) : base(message, inner) { }
}
public sealed class MergeConflictException : MainguardException
{
    public IReadOnlyList<string> ConflictedPaths { get; }
    public MergeConflictException(string msg, IReadOnlyList<string>? paths = null)
        : base(msg) => ConflictedPaths = paths ?? Array.Empty<string>();
}
public sealed class GitIdentityMissingException : MainguardException { /* ctor(msg) */ }
public sealed class AuthenticationRequiredException : MainguardException
{
    public string? Host { get; }   // "github.com", "gitlab.com", … — drives per-host auth (2.8)
    public AuthenticationRequiredException(string msg, string? host = null) : base(msg) => Host = host;
}
public sealed class RemoteNotFoundException : MainguardException { /* ctor(msg) */ }
public sealed class GitOperationException : MainguardException { /* ctor(msg, inner) */ }
```

- **Implementation specifics:** in `ExecuteWithRepo`, add a catch shim:

```csharp
catch (LibGit2SharpException ex) when (ex.Message.Contains("401") || ex.Message.Contains("authentication"))
{ throw new AuthenticationRequiredException("Authentication failed for remote.", HostFromRemote(repoPath)); }
catch (LibGit2SharpException ex)
{ throw new GitOperationException(ex.Message, ex); }
```

Add a single `HandleGitActionException(Exception ex, string operation)` helper in a ViewModel base (or `RepoDashboardViewModel`) so every command routes errors identically.

**PR Verification & Testing Strategy:**
- Unit tests (`Mainguard.Tests/Exceptions/`): each Git failure mode maps to its type — merge two conflicting branches in a temp repo → assert `MergeConflictException` with the conflicted path listed; commit with no identity → `GitIdentityMissingException` (after F3).
- Grep gate the reviewer runs: `grep -rn "throw new System.Exception\|throw new Exception(" Mainguard.Agents/ Mainguard.App.Shell/` must return **zero** hits.
- Manual: force a merge conflict in a scratch repo through the UI → the conflict resolver window opens (no raw error dialog).

---

## F2 — Hardened Cross-Platform Git CLI Runner (audit 1.6)

**Priority:** P0 / Foundation — CRITICAL (Windows-only `cmd.exe` fallback pops a terminal window and loses stderr).

**Objective & Context:** `ExecuteGitCli` (`GitServices.cs:496-524`) hardcodes `cmd.exe /c git {args} || pause`, `UseShellExecute = true`, `CreateNoWindow = false`. It breaks on macOS/Linux, pops a raw console, and cannot capture errors. A redundant `ExecuteSilentGitCli` (`:527`) exists. Every future CLI-driven feature (interactive rebase 2.1, partial staging 2.13, worktrees 4.5, LFS 2.6, force-with-lease 2.14) depends on one hardened runner.

**Step-by-Step Implementation Guide:**
1. Add `RunGit` to `GitServices.cs` (private) as the single process runner. Resolve the git executable once at startup (`GitExecutableResolver`): probe `git` on `PATH` via `where`/`which`, allow a user override stored in `UserPreferences` ("Git Executable" preference, GitKraken-style).
2. Use `ProcessStartInfo.ArgumentList` (never a concatenated string) to eliminate quoting/injection bugs.
3. Support `CancellationToken`: register `p.Kill(entireProcessTree: true)` on cancellation.
4. Support optional `stdinContent` (needed by 1.7/2.8 for `git credential approve` and by 2.13 for piping patches) and an `IDictionary<string,string>? env` (needed by 2.1 for `GIT_SEQUENCE_EDITOR`).
5. Delete `ExecuteGitCli` entirely. Rewrite `ExecuteSilentGitCli` as a thin wrapper over `RunGit` (or delete it and update the four call sites at `:265, :309, :345, :779`).
6. On non-zero exit, throw `GitOperationException($"git {FirstArg(args)} failed: {stderr}")` — stderr must reach the in-app error panel.

**Code Modifications & Additions:**
- **Target files:** `Mainguard.Agents/Services/GitServices.cs`; new `Mainguard.Agents/Services/GitExecutableResolver.cs`; `Mainguard.Agents/Models/UserPreferences.cs` (add `GitExecutablePath`); `Mainguard.Agents/Services/SettingsService.cs`.
- **Architecture & Methods:**

```csharp
internal readonly record struct GitCliResult(int Code, string StdOut, string StdErr);

private GitCliResult RunGit(string repoPath, IReadOnlyList<string> args,
    string? stdinContent = null, IDictionary<string,string>? env = null,
    CancellationToken ct = default)
{
    var psi = new ProcessStartInfo {
        FileName = _gitExecutablePath,           // resolved once at startup
        WorkingDirectory = repoPath,
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        RedirectStandardInput = stdinContent != null
    };
    foreach (var a in args) psi.ArgumentList.Add(a);
    psi.Environment["GIT_TERMINAL_PROMPT"] = "0";      // never hang on hidden prompts
    if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

    using var p = Process.Start(psi)!;
    using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
    if (stdinContent != null) { p.StandardInput.Write(stdinContent); p.StandardInput.Close(); }
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    ct.ThrowIfCancellationRequested();
    return new GitCliResult(p.ExitCode, stdout.Result, stderr.Result);
}
```

- **Implementation specifics:** `GIT_TERMINAL_PROMPT=0` is mandatory — a hidden process asking for credentials would hang forever. Callers that expect failure (e.g. probing) check `.Code`; callers that don't, use a `RunGitOrThrow` wrapper.

**PR Verification & Testing Strategy:**
- Unit tests: `RunGit` against a temp repo — `["status","--porcelain"]` returns exit 0; `["definitely-not-a-command"]` throws `GitOperationException` containing git's stderr; a cancelled token kills the process (`Assert.Throws<OperationCanceledException>` within ~1s).
- Argument-safety test: create a file named `"; echo pwned"` and stage it via `ArgumentList` — verify no shell interpretation.
- Manual: trigger any CLI fallback path (e.g. token pull fallback) on Windows — **no console window may flash**; on failure, the in-app error panel shows git's stderr text.
- Reviewer grep: `grep -n "cmd.exe\|UseShellExecute = true" Mainguard.Agents/` → zero hits.

---

## F3 — `GetSignature` Helper + Identity Dialog (audit 1.2)

**Priority:** P0 / Foundation — CRITICAL (crashes Commit/Revert/CherryPick/Amend on fresh machines).

**Objective & Context:** `repo.Config.BuildSignature(DateTimeOffset.Now)` returns `null` when `user.name`/`user.email` are unset. `Commit` (`GitServices.cs:162`), `Revert` (`:1010`), `CherryPick` (`:1023`), `AmendCommitMessage` (`:1045`), `Pull` (`:289`), `PullWithCredentials` (`:576`), `UpdateProject` (`:468`) pass that `null` straight into LibGit2Sharp → `NullReferenceException` on the very first commit of a fresh machine or CI checkout. `Merge`/`StashPush` mask it with a `"Mainguard <mainguard@localhost>"` placeholder that pollutes history.

**Step-by-Step Implementation Guide:**
1. Add the private `GetSignature(Repository repo)` helper (below) to `GitServices.cs`.
2. Replace **every** `repo.Config.BuildSignature(...)` call site and every `??= new Signature("Mainguard", ...)` fallback with `GetSignature(repo)`. Audit with `grep -n "BuildSignature\|new Signature(" Mainguard.Agents/Services/GitServices.cs`.
3. Add `SetIdentity(string repoPath, string name, string email, bool global)` to `IGitService` writing `user.name`/`user.email` via `repo.Config.Set(..., global ? ConfigurationLevel.Global : ConfigurationLevel.Local)`.
4. Build `SetIdentityDialog` (View + ViewModel): two text boxes, "Apply to all repositories (global)" checkbox, validation (non-empty name, `MailAddress.TryCreate` for email).
5. In the ViewModel error router (F1), catch `GitIdentityMissingException` → show the dialog → on save, retry the original command once.
6. First-run check: on repo open in `RepoDashboardViewModel`, if `BuildSignature` returns null, show a dismissible inline banner ("No Git identity configured — Set identity").

**Code Modifications & Additions:**
- **Target files:** `Mainguard.Agents/Services/GitServices.cs`, `Mainguard.Agents/Services/IGitService.cs`; new `Mainguard.App.Shell/Views/SetIdentityDialog.axaml(.cs)`, `Mainguard.App.Shell/ViewModels/SetIdentityDialogViewModel.cs`; `RepoDashboardViewModel.cs`.
- **Architecture & Methods:**

```csharp
private Signature GetSignature(Repository repo)
{
    var sig = repo.Config.BuildSignature(DateTimeOffset.Now);
    if (sig != null) return sig;
    var name  = repo.Config.Get<string>("user.name")?.Value;
    var email = repo.Config.Get<string>("user.email")?.Value;
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
        throw new GitIdentityMissingException("No Git identity configured. Set user.name and user.email.");
    return new Signature(name, email, DateTimeOffset.Now);
}
```

- **Implementation specifics:** **throw, don't silently fall back** — committing as "Mainguard <mainguard@localhost>" pollutes history permanently. The dialog + retry gives a clean first-run flow instead.

**PR Verification & Testing Strategy:**
- Integration tests (needs F4): temp repo with local `user.name`/`user.email` explicitly unset **and** `HOME`/`GIT_CONFIG_GLOBAL` pointed at an empty temp dir (so the developer's real global config can't leak in) → `Commit`, `Revert`, `CherryPick`, `AmendCommitMessage` each throw `GitIdentityMissingException` (not NRE). With identity set → all succeed and the commit author matches.
- Reviewer grep: `BuildSignature` appears exactly once (inside `GetSignature`); `new Signature("Mainguard"` → zero hits.
- Manual: `git config --global --unset user.name` in a sandboxed profile, open Mainguard, commit → identity dialog appears; fill it in → commit succeeds with the entered author.

---

## F4 — Integration-Test Harness: `TempRepoFixture` (audit "Testing throughout")

**Priority:** P0 / Foundation — every subsequent PR's test requirements assume it exists.

**Objective & Context:** The suite covers `IsGitRepository`, `ExecuteWithRepo`, the watcher, and `CommitGraphRouter` — but **zero** mutating flows. Every Cat-1/Cat-2 task must land with an integration test that inits a temp repo, performs the op, and asserts repository state. That needs a shared, fast, leak-free fixture.

**Step-by-Step Implementation Guide:**
1. Create `Mainguard.Tests/Fixtures/TempRepoFixture.cs`: `IDisposable` that creates a unique temp dir, runs `Repository.Init`, sets a **local** test identity (`test-user <test@mainguard.local>`), and force-deletes on dispose (clear read-only attributes on `.git` objects first — Windows requirement).
2. Add builder helpers: `CommitFile(path, content, message)`, `CreateBranch(name)`, `Checkout(name)`, `CreateConflict(path, oursContent, theirsContent)` (branch, divergent edits, return both branch names), `AddBareRemote()` (init a second bare temp repo and add it as `origin` — enables push/pull/fetch tests with **no network**).
3. Isolate global config in the test process: set `GIT_CONFIG_GLOBAL`/`GIT_CONFIG_SYSTEM` env vars to empty temp files in the fixture so host machine config never affects assertions.
4. Add an xUnit collection fixture so tests can share expensive setup where safe, but default to per-test repos (mutating tests must not share state).

**Code Modifications & Additions:**
- **Target files:** new `Mainguard.Tests/Fixtures/TempRepoFixture.cs`; new test classes per workstream (`GitServiceCommitTests.cs`, `GitServiceMergeTests.cs`, `MergeDiffServiceTests.cs`, …).
- **Implementation specifics:**

```csharp
public sealed class TempRepoFixture : IDisposable
{
    public string RepoPath { get; }
    public Repository Repo { get; }
    public TempRepoFixture()
    {
        RepoPath = Path.Combine(Path.GetTempPath(), "mainguard-test-" + Guid.NewGuid().ToString("N"));
        Repository.Init(RepoPath);
        Repo = new Repository(RepoPath);
        Repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
        Repo.Config.Set("user.email", "test@mainguard.local", ConfigurationLevel.Local);
    }
    public Commit CommitFile(string rel, string content, string message) { /* write, Stage, Commit */ }
    public (string ours, string theirs) CreateConflict(string rel, string oursText, string theirsText) { /* … */ }
    public void Dispose() { Repo.Dispose(); ForceDelete(RepoPath); }
    static void ForceDelete(string dir) { /* clear ReadOnly attrs recursively, then Directory.Delete(dir, true) */ }
}
```

**PR Verification & Testing Strategy:**
- The PR itself ships 3 smoke tests proving the fixture: init+commit works; `CreateConflict` + `Merge` produces `repo.Index.Conflicts.Any()`; `AddBareRemote` + `Push` round-trips a commit.
- Reviewer: run the suite twice in a row — no leftover `mainguard-test-*` dirs in `%TEMP%` (dispose works on Windows read-only pack files).

---

# WORKSTREAM A — CATEGORY 1: FIX EXISTING GIT FUNCTIONALITY

Ordered by severity. F1–F4 are prerequisites for all of these.

---

## A-1.1 — Implement the 3-Way Merge Engine (audit 1.1)

**Priority:** CRITICAL — the single biggest hole in the Git foundation. Blocks Phase 4.4 completion, task B-2.3 (conflict UI), A-1.5 (pull conflicts), and B-2.1 (interactive rebase conflicts).

**Objective & Context:** `MergeDiffService.GenerateMergeChunks` (`Mainguard.Agents/Services/MergeDiffService.cs:18-31`) builds two DiffPlex models and returns an **empty list**. The 338-line `ConflictResolverWindowViewModel` is a UI with no engine — conflict resolution cannot work end-to-end anywhere in the app (merge, rebase, cherry-pick all funnel here).

**Step-by-Step Implementation Guide:**
1. **Extend the model.** In `Mainguard.Agents/Models/MergeChunk.cs` add:

```csharp
public enum ChunkKind { Unchanged, LeftOnly, RightOnly, Conflict }
public enum ChunkResolution { Unresolved, TakeLeft, TakeRight, TakeBoth, Custom }
// on MergeChunk:
public ChunkKind Kind { get; set; }
public ChunkResolution Resolution { get; set; } = ChunkResolution.Unresolved;
public string BaseText { get; set; }  public string LeftText { get; set; }  public string RightText { get; set; }
public string? CustomText { get; set; }   // populated when Resolution == Custom
```

2. **Implement the chunker.** In `MergeDiffService.GenerateMergeChunks(baseText, leftText, rightText)`:
   - Null-coalesce all three inputs to `""`.
   - Build `_diffBuilder.BuildDiffModel(baseText, leftText)` and `(baseText, rightText)`.
   - From each `DiffPaneModel`, derive a per-base-line change map: walk `OldText.Lines` (aligned to base); a base line index *i* is "changed on that side" if its `ChangeType` is `Deleted`/`Modified`, or an `Inserted` run in `NewText` anchors adjacent to it. Precompute two `bool[] leftChanged, rightChanged` of length `baseLines.Length`, plus per-side insertion buckets keyed by base anchor index (insertions at base position *i* attach to the region containing *i*).
   - Coalesce contiguous runs: while neither side changed → accumulate an `Unchanged` chunk; when either side changes, grow the region while `leftChanged[i] || rightChanged[i]`, then classify `Conflict` (both), `LeftOnly`, or `RightOnly` and extract each side's text for that base range (base slice with that side's deletions removed and insertions spliced in).
   - Edge cases that must work: insert-at-EOF on both sides (conflict), one side deletes the whole file (`leftText == ""`), pure insertions at the same anchor from both sides (conflict), trailing-newline differences (normalize split on `'\n'`, preserve a `HadTrailingNewline` flag for reassembly).
3. **Index-stage plumbing (Option A from the audit — the source of truth).** Add to `IGitService`/`GitServices.cs`:

```csharp
IReadOnlyList<ConflictedFile> GetConflicts(string repoPath);   // repo.Index.Conflicts → paths + stage presence
(string Base, string Ours, string Theirs) GetConflictBlobs(string repoPath, string path);
void ResolveConflict(string repoPath, string path, string mergedContent);
```

   - `GetConflictBlobs`: from `repo.Index.Conflicts[path]`, look up `Ancestor`/`Ours`/`Theirs` `IndexEntry.Id` → `repo.Lookup<Blob>(id).GetContentText()`. Any stage may be **null** (add/add conflicts have no ancestor; delete/modify has no ours or theirs) — return `""` for missing stages and expose which stages existed so the UI can label "deleted on their side".
   - `ResolveConflict`: `File.WriteAllText(Path.Combine(repoPath, path), mergedContent)` then `Commands.Stage(repo, path)` — staging a conflicted path clears its conflict entries.
4. **Reassembly.** Add `MergeDiffService.AssembleMerged(IEnumerable<MergeChunk> chunks)`: concatenates per chunk — `Unchanged` → base text; `LeftOnly` → left; `RightOnly` → right; `Conflict` → text per its `Resolution` (`TakeBoth` = left then right; `Custom` = `CustomText`). Throws if any `Conflict` chunk is still `Unresolved`.
5. Add `bool HasUnresolvedConflicts(string repoPath)` (`repo.Index.Conflicts.Any()`) — gates "Commit merge"/"Continue rebase" (used by B-2.3).

**Code Modifications & Additions:**
- **Target files:** `Mainguard.Agents/Services/MergeDiffService.cs`, `Mainguard.Agents/Services/IMergeDiffService.cs`, `Mainguard.Agents/Models/MergeChunk.cs`, `Mainguard.Agents/Services/GitServices.cs`, `IGitService.cs`; new `Mainguard.Agents/Models/ConflictedFile.cs`.
- **Architecture:** the chunker stays pure (strings in, chunks out — no repo access) so it is unit-testable without Git; all repo access lives in `GitServices`.

**PR Verification & Testing Strategy:**
- Unit tests (`MergeDiffServiceTests`, pure, no Git): (a) identical inputs → single `Unchanged` chunk; (b) left-only edit → `LeftOnly`; (c) both edit same base line → `Conflict`; (d) non-overlapping edits → `LeftOnly` + `Unchanged` + `RightOnly`, and `AssembleMerged` equals the true merged text; (e) add/add with empty base; (f) delete-whole-file vs edit → `Conflict`; (g) round-trip property: for any resolution choice, `AssembleMerged` output splits back into consistent lines.
- Integration tests (F4): `CreateConflict` + `Merge` → `GetConflicts` returns the file; `GetConflictBlobs` returns three distinct texts; `GenerateMergeChunks` yields ≥1 `Conflict` chunk; `ResolveConflict` clears `repo.Index.Conflicts` and a subsequent `Commit` succeeds with two parents.
- Manual: two-branch conflicting edit in a scratch repo → merge in UI → resolver shows the conflicting region (full UI verified in B-2.3).

---

## A-1.3 — Async Network Commands in ViewModels (audit 1.3)

**Priority:** CRITICAL — UI freezes on every push/pull/fetch.

**Objective & Context:** `Push`, `Pull`, `Fetch`, `UpdateProject` in `Mainguard.App.Shell/ViewModels/RepoDashboardViewModel.cs:175-232` are synchronous `[RelayCommand]`s calling blocking `IGitService` methods on the UI thread. Unbounded network latency = "app not responding". (Status refresh at `:118` already uses `Task.Run` — copy that discipline.)

**Step-by-Step Implementation Guide:**
1. Add `[ObservableProperty] private bool _isBusy;` to `RepoDashboardViewModel` and a shared `CanRunGitAction() => !IsBusy` predicate.
2. Convert each of the four commands to `async Task` with `CancellationToken` (CommunityToolkit generates `IAsyncRelayCommand` with a `Cancel` command when the method takes a `CancellationToken` and `IncludeCancelCommand = true`).
3. Wrap the service call in `Task.Run(() => _gitService.Push(_repoPath, ct), ct)`; catch `OperationCanceledException` separately; route all other exceptions through `HandleGitActionException` (F1).
4. `finally { IsBusy = false; await RefreshStatusAsync(); }` — status refresh must always run, including after failures.
5. Add `CancellationToken ct = default` parameters to `IGitService.Push/Pull/Fetch/UpdateProject/Clone/GetRecentCommits` now (audit 1.11's other half). Honor them: pass into `RunGit` (F2 kills the process) and check `ct.ThrowIfCancellationRequested()` inside commit-walk loops. LibGit2Sharp network ops can't be interrupted mid-transfer — that's acceptable; the token still prevents queued follow-up work.
6. UI: bind a progress overlay (indeterminate bar + operation name + Cancel button) to `IsBusy`; toolbar buttons disable via `CanExecute` (call `NotifyCanExecuteChanged` from the `IsBusy` setter, or use `[NotifyCanExecuteChangedFor]`).

**Code Modifications & Additions:**
- **Target files:** `RepoDashboardViewModel.cs`, `Mainguard.App.Shell/Views/RepoDashboardView.axaml` (overlay), `IGitService.cs`, `GitServices.cs`.
- **Implementation specifics:**

```csharp
[RelayCommand(CanExecute = nameof(CanRunGitAction), IncludeCancelCommand = true)]
private async Task PushAsync(CancellationToken ct)
{
    IsBusy = true;
    try { await Task.Run(() => _gitService.Push(_repoPath, ct), ct); ShowNotification("Push completed.", false); }
    catch (OperationCanceledException) { ShowNotification("Push cancelled.", false); }
    catch (Exception ex) { HandleGitActionException(ex, "Push"); }
    finally { IsBusy = false; await RefreshStatusAsync(); }
}
```

**PR Verification & Testing Strategy:**
- Unit test (ViewModel, mock `IGitService` whose `Push` blocks on a `TaskCompletionSource`): invoking `PushCommand` sets `IsBusy = true` and `PushCommand.CanExecute` → false; completing the TCS flips both back and calls the refresh.
- Manual (the decisive check): add `Thread.Sleep(10_000)` temporarily inside the mocked/dev push path (or pull a large repo over a throttled connection) → the window stays fully interactive, overlay visible, Cancel works, and other Git buttons are disabled during the op.
- Regression: push with no identity/no remote → typed error surfaces in the banner, `IsBusy` returns to false (no stuck overlay).

---

## A-1.4 — Safe Discard: No Recursive Deletes, Recycle Bin, Explicit Confirmation (audit 1.4)

**Priority:** HIGH — data-loss trap.

**Objective & Context:** `DiscardChanges` (`GitServices.cs:104-133`) deletes untracked paths and, if a path is a directory, calls `Directory.Delete(fullPath, true)` — recursive and unconditional. It also classifies purely on `NewInWorkdir|NewInIndex` flags (mishandles staged-new-then-modified and renames). One mis-click can wipe a folder of work with no undo.

**Step-by-Step Implementation Guide:**
1. In `DiscardChanges`, **remove the `Directory.Delete` branch entirely.** If a status path resolves to a directory, skip it and record it in the operation result ("skipped: directory").
2. Split the incoming paths into two lists *before* doing anything: **untracked** (`NewInWorkdir`/`NewInIndex` → will be removed) and **tracked** (→ will be reverted via `repo.CheckoutPaths(head, paths, Force)` — that path is fine, keep it). For staged-new-then-modified, treat as untracked-removal (unstage first via `Commands.Unstage`, then remove). For renames, discard both sides (restore old path, remove new path).
3. Route untracked-file deletion through the OS trash. New `Mainguard.Agents/Services/ITrashService.cs` + `TrashService.cs`:
   - Windows: P/Invoke `SHFileOperationW` with `FO_DELETE | FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT` (avoids the `Microsoft.VisualBasic` assembly reference).
   - macOS: `NSFileManager trashItemAtURL` via `osascript`/`trash` shim; Linux: freedesktop trash spec (move into `~/.local/share/Trash/files` + write `.trashinfo`).
   - Fallback: if trashing fails, abort the discard for that file and report — never silently hard-delete.
4. Change `IGitService.DiscardChanges` to return a `DiscardPlan`/`DiscardResult` pair: `PlanDiscard(paths)` → `{ TrackedToRevert: [...], UntrackedToTrash: [...], SkippedDirectories: [...] }`; `ExecuteDiscard(plan)` performs it. The ViewModel shows the plan first.
5. UI: `DiscardConfirmationDialog` listing the **two distinct lists** (reverted vs removed-to-trash) with counts in the confirm button ("Revert 3 files and trash 2 untracked files"). Wire into `StagingPanelViewModel`.

**Code Modifications & Additions:**
- **Target files:** `GitServices.cs`, `IGitService.cs`; new `Mainguard.Agents/Services/ITrashService.cs`, `TrashService.cs`; new `Mainguard.App.Shell/Views/DiscardConfirmationDialog.axaml(.cs)` + ViewModel; `StagingPanelViewModel.cs`.
- **Implementation specifics:** `SHFileOperationW` requires double-null-terminated path lists; batch all files into one call so the user gets one undoable trash operation.

**PR Verification & Testing Strategy:**
- Integration tests: (a) plan for a mixed selection separates tracked/untracked correctly, including staged-new-then-modified; (b) a status entry resolving to a directory lands in `SkippedDirectories` and **the directory still exists** after execution; (c) tracked file content is restored to HEAD; (d) untracked file is gone from the working tree after execution (trash itself mocked via `ITrashService` in tests).
- Manual (Windows): discard an untracked file → it appears in the Recycle Bin and is restorable; create `untracked-dir/` with files, select it → discard skips it with a visible notice.
- Reviewer grep: `Directory.Delete` must not appear in `DiscardChanges`' implementation.

---

## A-1.5 — Pull: Surface Conflicts + Pull Strategy (audit 1.5)

**Priority:** HIGH. **Depends on:** F1, F3, A-1.1 (resolver must exist for the routed flow to land anywhere useful).

**Objective & Context:** `Pull` (`GitServices.cs:283-316`) and `UpdateProject` (`:459-493`) call `Commands.Pull` and ignore the returned `MergeResult` — on conflicts the working tree is silently left conflicted with no signal. There is also no fast-forward-only or pull-rebase option.

**Step-by-Step Implementation Guide:**
1. Add `public enum PullStrategy { Default, FastForwardOnly, Rebase }` in `Mainguard.Agents/Models/`.
2. Rewrite `Pull(string repoPath, PullStrategy strategy = PullStrategy.Default, CancellationToken ct = default)`:
   - Build `PullOptions` with `FetchOptions.CredentialsProvider = GetCredentialsProvider()` and `MergeOptions.FastForwardStrategy = FastForwardOnly` when requested.
   - `var result = Commands.Pull(repo, GetSignature(repo), options);`
   - `if (result.Status == MergeStatus.Conflicts) throw new MergeConflictException("Pull produced conflicts. Resolve them, then commit.", GetConflicts(repoPath).Select(c => c.Path).ToList());`
   - `FastForwardOnly` + non-FF → LibGit2Sharp throws `NonFastForwardException`; wrap it into `GitOperationException("Cannot fast-forward; local branch has diverged.")`.
   - `Rebase`: `Commands.Fetch(...)` then call the existing `Rebase(...)` against `repo.Head.TrackedBranch.Tip`; rebase conflicts already throw via F1's typed path.
3. In the CLI/token fallback catch block, detect conflicts after the CLI pull returns: check `repo.Index.Conflicts.Any()` (or `MERGE_HEAD` existence) and throw the same `MergeConflictException` — both paths must behave identically.
4. Apply the identical `MergeResult` inspection inside `UpdateProject` and `PullWithCredentials`.
5. UI: a split-button/dropdown on Pull (`Pull (merge)` / `Pull (fast-forward only)` / `Pull (rebase)`); persist the choice in `UserPreferences.DefaultPullStrategy`. `MergeConflictException` routes to the conflict resolver via the F1 handler.

**Code Modifications & Additions:**
- **Target files:** `GitServices.cs`, `IGitService.cs`, `Mainguard.Agents/Models/UserPreferences.cs`, `RepoDashboardViewModel.cs`, `RepoDashboardView.axaml`.

**PR Verification & Testing Strategy:**
- Integration tests (F4 `AddBareRemote`, no network): (a) clone the bare remote twice; commit conflicting edits in clone B and push; commit locally in clone A; `Pull(Default)` → `MergeConflictException` with the path; (b) remote ahead / local clean → `Pull(FastForwardOnly)` succeeds and HEAD moved without a merge commit; (c) diverged + `FastForwardOnly` → typed failure, working tree untouched; (d) `Pull(Rebase)` → local commit reparented onto remote tip (`repo.Head.Tip.Parents.Single().Sha == remoteTip`).
- Manual: force a conflicting pull → conflict resolver opens automatically; resolve → commit merge completes.

---

## A-1.7 — Credential Handling: No Tokens in argv, Host-Keyed Secrets (audit 1.7)

**Priority:** HIGH — credential-leak vector. (Full multi-host auth + SSH manager is C-2.8; this task closes the leak and lays the keyring groundwork.)

**Objective & Context:** `ConvertToTokenUrl` (`GitServices.cs:195-211`) builds `https://x-access-token:{token}@github.com/...` and passes it **as a CLI argument** (call sites `:265, :309, :345, :779`). Tokens in argv are visible in process listings and shell history. Auth is also GitHub-only (`GetGitHubToken`, `:184`).

**Step-by-Step Implementation Guide:**
1. **Delete `ConvertToTokenUrl`** and every `tokenUrl` argument. The CLI fallback keeps using the repo's normal remote URL.
2. Feed credentials to the git CLI via the credential mechanism instead: in `RunGit` calls that hit the network, pass `env = { ["GIT_TERMINAL_PROMPT"]="0" }` plus `-c credential.helper=` + `-c credential.helper=<mainguard-helper>` args, where the helper is Mainguard itself in helper mode:
   - Add a hidden CLI mode to the app binary (or a tiny companion executable `mainguard-credential`): invoked as `mainguard-credential get`, it reads the standard `protocol=/host=` key-value request on stdin, looks up `token:<host>` in `SecureKeyring`, and prints `username=x-access-token\npassword=<token>\n` to stdout. Secrets flow stdin/stdout only — never argv, never env of a child git.
   - Wire it: `RunGit(repoPath, ["-c", "credential.helper=", "-c", $"credential.helper={helperPath}", "pull", ...])`. The empty first helper disables system helpers so behavior is deterministic.
3. **Generalize the keyring keys now:** `SecureKeyring` (`Mainguard.Agents/Security/SecureKeyring.cs`) reads/writes `token:<host>` (e.g. `token:github.com`); migrate the legacy `github_token` entry on first read. Add `GetHostFromRemoteUrl(string url)` (handles `https://host/...` and `git@host:...` forms) and use it in `GetCredentialsProvider` to select the secret.
4. `GetCredentialsProvider` (LibGit2Sharp path — primary) keys off the URL's host; unknown host with no stored token → throw `AuthenticationRequiredException(host)` so the UI can prompt (device flow for GitHub today; PAT entry dialog for others until C-2.8).
5. SSH: pass `SshUserKeyCredentials { Username = "git", PrivateKey = prefs.SshKeyPath, Passphrase = ... }` from the provider when the remote is SSH-form; passphrase pulled from keyring (`sshpass:<keypath>`). Full key generation/registration UX is C-2.8.

**Code Modifications & Additions:**
- **Target files:** `GitServices.cs`, `Mainguard.Agents/Security/SecureKeyring.cs`, `Mainguard.Agents/Sync/GitHubAuthClient.cs` (store under new key), new `Mainguard.Agents/Security/GitCredentialHelper.cs` + entry-point wiring in `Mainguard.App.Shell/Program.cs` (args `["credential", "get|store|erase"]` short-circuit before Avalonia starts).
- **Implementation specifics:** the helper protocol is line-based `key=value` pairs terminated by a blank line; implement `get` fully, `store`/`erase` as no-ops (Mainguard owns storage).

**PR Verification & Testing Strategy:**
- Unit tests: `GetHostFromRemoteUrl` for https/ssh/scp-style/self-hosted-with-port URLs; keyring migration test (`github_token` → `token:github.com`); credential-helper protocol test (feed `protocol=https\nhost=github.com\n\n` to the helper entry point with a seeded keyring → stdout contains the token, exit 0; unknown host → empty output, exit 0 so git falls through).
- **Security check the reviewer must run:** while a fallback pull executes, capture the process list (`wmic process get commandline` / `ps -ef`) → no token substring appears in any command line. Grep: `ConvertToTokenUrl` → zero hits.
- Integration: F4 bare-remote push/pull still works (no credentials needed for file remotes — proves the helper doesn't break the non-auth path).

---

## A-1.8 — `GetRecentCommits`: Streaming Filters + Stable Cursor Pagination (audit 1.8)

**Priority:** MEDIUM — performance + graph correctness on large repos.

**Objective & Context:** (`GitServices.cs:603-674`) Multi-path filtering materializes full history per path (`SelectMany(QueryBy)` then sorts in memory); `Skip(n)/Take(n)` on a topological walk re-walks from the tips every page — O(N) per page and, if ordering isn't perfectly stable, the fringe handed to `CommitGraphRouter` desyncs at chunk boundaries (visible as broken lanes at 500-commit seams).

**Step-by-Step Implementation Guide:**
1. **Cache the ordered SHA list per refresh.** Add a small `CommitWalkCache` (per repo instance, invalidated by the `RepositoryWatcher` refresh event): on first page request, run the `CommitFilter` walk **once** (`SortBy = Topological | Time`), storing only SHAs in a `List<string>` (40 bytes/commit — 1M commits ≈ 40 MB worst case; store as `ObjectId` to halve it). Page = slice of that list, hydrating `GitCommitItem`s only for the slice (`repo.Lookup<Commit>(id)`). This guarantees a byte-stable order across pages → router fringe is always continuous.
2. **Push filters into the walk.** Text/author/date filters become a lazy `Where` over the streaming walk during that single cached pass. Pre-lower the needle once; compare with `IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0` (no per-commit `ToLowerInvariant()` allocation).
3. **Multi-path:** replace `FilePaths.SelectMany(QueryBy)` with a single walk + per-commit tree-diff membership test: for each candidate commit, `repo.Diff.Compare<TreeChanges>(parentTree, commit.Tree, paths)` (pass paths as the explicit paths argument — libgit2 prunes the diff to those paths, cheap) and keep the commit if any change matched. First-parent only for merges to avoid duplicates.
4. Honor `CancellationToken` inside the walk loop (from A-1.3).
5. Keep the public signature `GetRecentCommits(repoPath, skip, take, filter)` — callers unchanged; the cache makes `skip` cheap.

**Code Modifications & Additions:**
- **Target files:** `GitServices.cs`; new `Mainguard.Agents/Services/CommitWalkCache.cs`; `Mainguard.Agents/Models/CommitSearchFilter.cs` (no shape change expected); `RepositoryWatcher` hookup in `RepoDashboardViewModel` (invalidate cache on refresh).

**PR Verification & Testing Strategy:**
- Integration tests: (a) **seam stability** — build a repo with 1,200 commits across octopus/criss-cross branches (script it in the fixture); fetch pages of 500 twice; assert page 2 of run 1 == page 2 of run 2 and that `page1.Last()`'s parents appear at the head of page 2's walk (fringe continuity); feed both pages to `CommitGraphRouter` and assert no orphan lanes; (b) multi-path filter returns exactly commits touching either path, no duplicates; (c) filter + pagination composes (page boundaries don't drop matches).
- Perf check (reviewer, on a large clone like `linux` or `dotnet/runtime`): time page 10 (skip 4500) before/after — after must be O(slice) (<50 ms), not a full re-walk.

---

## A-1.9 — Null-Guard All `.Tip` Dereferences (audit 1.9)

**Priority:** MEDIUM — crashes on empty repos / just-created branches.

**Objective & Context:** `GetBranchDiffAgainstWorkingTree` (`GitServices.cs:918` — `branch.Tip.Tree`), `AmendCommitMessage` (`:1040` — `repo.Head.Tip.Sha`), and `PushBranch`/`UpdateProject` tip accesses NRE on unborn HEAD (fresh `git init`) or a branch with no commits. `GetBranches` (`:689`) already does `branch.Tip?.Sha` — the guard is inconsistent.

**Step-by-Step Implementation Guide:**
1. `grep -n "\.Tip" Mainguard.Agents/Services/GitServices.cs` and audit every hit.
2. For each mutating/diffing site, add: `if (branch?.Tip == null) throw new GitOperationException("Branch '<name>' has no commits yet.");` In `AmendCommitMessage`, check `repo.Head.Tip == null` → "Nothing to amend — the repository has no commits."
3. For read/list sites, prefer the `?.` null-propagation pattern from `GetBranches` (return empty results instead of throwing).
4. Also guard `repo.Head.TrackedBranch` (null when no upstream) anywhere ahead/behind or push targets are derived.
5. ViewModel sweep: `RepoDashboardViewModel` must render an empty repo (no commits) without crashing — timeline empty-state, disabled amend, working "first commit" flow.

**Code Modifications & Additions:**
- **Target files:** `GitServices.cs`; possibly `CommitTimelineViewModel.cs`, `BranchBrowserViewModel.cs` empty-state handling.

**PR Verification & Testing Strategy:**
- Integration tests: on a bare-`Init` temp repo (unborn HEAD): `GetBranches` returns empty (no throw), `AmendCommitMessage` throws `GitOperationException` with the friendly message, `GetBranchDiffAgainstWorkingTree` throws typed (not NRE); create a branch ref without commits → same. First commit on unborn HEAD succeeds.
- Manual: `git init` an empty folder, open it in Mainguard → dashboard loads, staging works, first commit succeeds, no crash anywhere while clicking around.

---

## A-1.10 — Scope + Throttle the `RepositoryWatcher` (audit 1.10)

**Priority:** MEDIUM — CPU burn on big trees; critical prerequisite for the multi-agent phase (many concurrent writers).

**Objective & Context:** `RepositoryWatcher` (`Mainguard.Agents/Services/RepositoryWatcher.cs:53-101`) watches the whole tree (`IncludeSubdirectories = true`) with broad `NotifyFilter`s; `node_modules`/`bin`/`obj` churn triggers full refreshes, and `.git/index.lock` churn refreshes mid-operation. (README claims it targets `.git/refs`+`index` — the code does not.)

**Step-by-Step Implementation Guide:**
1. **Split into two watchers** inside `RepositoryWatcher`:
   - **MetaWatcher** on `<repo>/.git` (`HEAD`, `index`, `refs/` subtree, `MERGE_HEAD`, `rebase-merge/`): short debounce (150 ms) → fires `RepositoryStateChanged`.
   - **WorkTreeWatcher** on the repo root: heavier debounce (500 ms) → fires `WorkingTreeChanged`.
2. In both handlers, drop events early:
   - Any path ending in `.lock` (covers `index.lock`, ref locks) → ignore.
   - Any path under `.git/` in the WorkTreeWatcher → ignore (MetaWatcher owns it).
   - Static prefix denylist first (cheap): `node_modules/`, `bin/`, `obj/`, `.vs/`, `.idea/`, `dist/`, `target/`.
   - Then `.gitignore` check via a **cached** `repo.Ignore.IsPathIgnored(rel)` — maintain an LRU `ConcurrentDictionary<string,bool>` keyed by relative dir, invalidated when `.gitignore` itself changes.
3. **Rate cap:** even under continuous events, emit at most 1 refresh per 250 ms (track `_lastRefreshUtc`; if a suppressed event arrives, schedule one trailing refresh so the final state is never missed).
4. Update consumers: `RepoDashboardViewModel` refreshes status on either event but only re-walks commits/graph on `RepositoryStateChanged` (cache invalidation from A-1.8 hooks here too).
5. Fix the README claim or the code — after this task they agree.

**Code Modifications & Additions:**
- **Target files:** `RepositoryWatcher.cs`, `RepoDashboardViewModel.cs`, `README.md`.

**PR Verification & Testing Strategy:**
- Unit tests (watcher already has coverage — extend it): writing `<repo>/.git/index.lock` fires **no** event; writing `node_modules/x.js` fires no event; writing a tracked file fires `WorkingTreeChanged` once (not N times) after a 100-file burst; updating `.git/refs/heads/main` fires `RepositoryStateChanged` within the short debounce; a 2-second continuous write loop produces ≤ 8 refreshes (250 ms cap) plus one trailing.
- Manual: open a JS monorepo, run `npm install` → Mainguard's CPU (watch in Task Manager) stays near-idle and the UI refreshes once at the end, not continuously.

---

## A-1.12 — Fresh Ahead/Behind + `CheckoutBranch` Cleanup (audit 1.12)

**Priority:** LOW. **Pairs with:** C-2.14 (auto-fetch delivers the real fix).

**Objective & Context:** `GetAheadBehind` (`GitServices.cs:592-601`) reads `TrackingDetails` which is only as fresh as the last fetch — with no auto-fetch the badge can be stale forever. `CheckoutBranch` (`:696-723`) re-indexes `repo.Branches[branchName]` at `:717` after already holding the object.

**Step-by-Step Implementation Guide:**
1. Track `LastFetchedUtc` per repo: record it whenever `Fetch`/`Pull` succeeds (persist in the existing SQLite bookmarks table via `AppDbContext` — add a nullable column + migration). Surface "last fetched N min ago" next to the ahead/behind badge; render the badge dimmed when > 15 min.
2. In `CheckoutBranch`, capture the branch/remote-branch reference once at the top and reuse it (delete the `:717` re-lookup).
3. Verify the dirty-working-tree checkout path actually routes through `CheckoutConflictDialog` before `Commands.Checkout` (the audit flags this as "verify wired"): if `repo.RetrieveStatus().IsDirty` and the checkout would overwrite, the ViewModel must show the dialog (stash / discard / cancel) — wire it if missing.
4. The background auto-fetch timer itself lands in C-2.14; this task ships the plumbing (`LastFetchedUtc`, dimming) it feeds.

**Code Modifications & Additions:**
- **Target files:** `GitServices.cs`, `AppDbContext.cs` + new migration, `RepoDashboardViewModel.cs`, `RepoDashboardView.axaml`, `BranchBrowserViewModel.cs`.

**PR Verification & Testing Strategy:**
- Integration: `Fetch` against the F4 bare remote updates `LastFetchedUtc`; checkout with dirty tree over a conflicting file surfaces the dialog decision path (test the ViewModel branch with a mock).
- Manual: open a repo not fetched recently → badge shows dimmed with "last fetched …"; press Fetch → badge brightens and timestamp resets. Checkout between branches with a dirty conflicting file → dialog appears, all three choices behave.

---

*(Audit 1.2 = F3, 1.6 = F2, 1.11 = F1 + A-1.3 step 5, 1.13 = B-2.13 — covered elsewhere.)*

---

# WORKSTREAM B — PHASE 4.4/4.5 COMPLETION + CATEGORY 2 P0 FEATURES

---

## B-2.3 — Full Conflict-Resolution Editor (audit 2.3; completes Roadmap Phase 4.4)

**Priority:** P0. **Depends on:** A-1.1.

**Objective & Context:** With the chunker real (A-1.1), wire the existing `ConflictResolverWindow` / `MergeEditorControl` / `MergeGutterControl` into a true 4-pane merge tool (Base | Ours | Theirs | Merged) with per-chunk take-ours/take-theirs/take-both/edit, whole-file resolve, and a conflict list with resolved/unresolved counts. This is the most-praised feature of GitKraken/Fork and currently a facade.

**Step-by-Step Implementation Guide:**
1. **Data flow.** `ConflictResolverWindowViewModel` gains: constructor input `(repoPath, conflictedPath)`; on load (background thread): `GetConflictBlobs` → `GenerateMergeChunks` → project into `ObservableCollection<MergeChunkViewModel>` (chunk + `Resolution` + commands). Marshal results to the UI thread via `Dispatcher.UIThread.Post` — never construct Avalonia-bound collections off-thread.
2. **Panes.** `ConflictResolverWindow.axaml`: 4 panes (Base read-only, Ours read-only, Theirs read-only, Merged editable AvaloniaEdit — the Phase 4.4 interactive editor is already done). Chunk-aligned scrolling: bind all four `ScrollViewer.Offset`s through the ViewModel (single `ScrollOffset` property) and render chunk backgrounds via `MergeGutterControl` (conflict = red tint, left-only = green, right-only = blue).
3. **Per-chunk actions.** Buttons in the gutter per `Conflict` chunk: Take Ours / Take Theirs / Take Both; direct edits in the Merged pane set `Resolution = Custom` and capture `CustomText` for the chunk under the caret's line range. Recompute the Merged pane text via `AssembleMerged` on every resolution change (allow unresolved chunks to render as `<<<<<<<`-style placeholders in the preview only).
4. **File-level actions.** "Resolve using Ours/Theirs" buttons → `RunGit(repoPath, ["checkout", "--ours"|"--theirs", "--", path])` then `Commands.Stage`. Add to `IGitService`: `ResolveFileWithSide(repoPath, path, ConflictSide side)`.
5. **Conflict list.** `ConflictedFilesViewModel` (exists) lists `GetConflicts()` with per-file state (Unresolved/Resolved) and a header count "2 of 5 resolved". Selecting a file loads it into the resolver. Refresh from the MetaWatcher event.
6. **Completion flow.** "Mark resolved" (enabled when no `Unresolved` chunks) → `AssembleMerged` → `ResolveConflict`. When `HasUnresolvedConflicts == false`, enable **Commit merge** (merge in progress: commit with the preserved `MERGE_MSG`; LibGit2Sharp: `repo.Commit(msg, sig, sig)` — it auto-records both parents while `MERGE_HEAD` exists) or **Continue rebase** (`ContinueRebase`, exists at `GitServices.cs:421-453`). Detect which flow via `repo.Info.CurrentOperation`.
7. **Delete/modify conflicts.** When a stage is missing (from A-1.1's stage-presence flags), replace that pane with a "(deleted on this side)" placeholder and offer "Keep file" / "Delete file" instead of chunk actions.

**Code Modifications & Additions:**
- **Target files:** `Mainguard.App.Shell/ViewModels/ConflictResolverWindowViewModel.cs`, `ConflictedFilesViewModel.cs`, `MergeConflictViewModel.cs`; `Mainguard.App.Shell/Views/ConflictResolverWindow.axaml(.cs)`, `ConflictedFilesWindow.axaml`; `Mainguard.App.Shell/Controls/MergeEditorControl.axaml(.cs)`, `MergeGutterControl.cs`; `GitServices.cs` (+`ResolveFileWithSide`, `HasUnresolvedConflicts`, `GetCurrentOperation`).
- **Implementation specifics:** keep the ViewModel testable — all resolution logic (chunk state machine, assemble, completion gating) lives in the ViewModel/service, none in code-behind; code-behind only wires scroll sync.

**PR Verification & Testing Strategy:**
- Unit tests: `MergeChunkViewModel` state machine (resolve → merged text updates; un-resolve → gate closes); completion gating (`CanCommitMerge` false while any chunk unresolved); delete/modify pane substitution logic.
- Integration: full loop on a temp repo — conflict → resolve mixed (ours for chunk 1, theirs for chunk 2, custom for chunk 3) → `ResolveConflict` → commit merge → assert 2-parent commit whose blob equals the assembled text; rebase-conflict variant ends with `ContinueRebase` completing.
- Manual PR checklist: 4 panes scroll in lockstep; take-ours/theirs/both each visibly update Merged; hand-edit works; file-level ours/theirs works; counts update; commit-merge button disabled until all resolved; delete/modify conflict shows placeholder panes.

---

## B-4.5a — Interactive Rebase via git CLI (audit 2.1; Roadmap Phase 4.5)

**Priority:** P0. **Depends on:** F2, A-1.1/B-2.3 (mid-rebase conflicts), D-2.9 journal hooks optional.

**Objective & Context:** Reorder/pick/reword/squash/fixup/edit/drop with drag-and-drop — the #1 power feature of every benchmark client and directly relevant to curating agent-generated commits later. **Locked decision:** libgit2 does not support interactive rebase; drive the `git` CLI with a scripted `GIT_SEQUENCE_EDITOR`.

**Step-by-Step Implementation Guide:**
1. **Model.** `Mainguard.Agents/Models/RebaseTodoItem.cs`: `{ string Sha, RebaseAction Action, string Message, string? NewMessage }` with `enum RebaseAction { Pick, Reword, Squash, Fixup, Edit, Drop }`.
2. **Service.** New `Mainguard.Agents/Services/InteractiveRebaseService.cs` (+ interface):
   - `GetRebasePlan(repoPath, baseSha)` → walk `baseSha..HEAD` oldest-first into `RebaseTodoItem`s (default `Pick`).
   - `StartInteractiveRebase(repoPath, baseSha, IReadOnlyList<RebaseTodoItem> plan, ct)`:
     a. Snapshot `HEAD` SHA + branch name for undo (journal entry; see D-2.9).
     b. Write the todo file content Mainguard generated (`pick <sha> <msg>` lines in plan order; `drop` lines omitted or written as `drop`).
     c. **Sequence editor shim:** no helper binary needed — use git itself: `GIT_SEQUENCE_EDITOR="cp <generatedTodoPath>"` won't cross platforms reliably, so ship a tiny built-in mode instead: invoke Mainguard's own executable as the editor — `GIT_SEQUENCE_EDITOR="<mainguardExe> --rebase-editor <generatedTodoPath>"`; that mode copies the generated file over `argv[^1]` (the todo path git passes) and exits 0. Same trick for rewords: `GIT_EDITOR="<mainguardExe> --rebase-msg <msgDir>"` pops messages from a queue dir written by the service (one file per `Reword`/`Squash` in plan order).
     d. `RunGit(repoPath, ["rebase", "-i", baseSha], env: { GIT_SEQUENCE_EDITOR, GIT_EDITOR }, ct)`.
   - `GetRebaseProgress(repoPath)` → parse `.git/rebase-merge/msgnum` / `end` + `done` for "step 3/7"; `IsRebasing` already exists.
   - On conflict: git exits non-zero with the conflicted tree; detect `repo.Index.Conflicts.Any()` → throw `MergeConflictException` → resolver (B-2.3) → `ContinueRebase` reuses the same env shims? **No** — `git rebase --continue` re-invokes `GIT_EDITOR` for squash messages; run continue through the same env.
   - `Edit` action: rebase stops at the commit (normal git behavior); surface "Rebase paused at <sha> — amend, then Continue".
3. **UI.** New `InteractiveRebaseWindow.axaml` + `InteractiveRebaseViewModel`: `ObservableCollection<RebaseTodoItemViewModel>` in an `ItemsControl` with drag-reorder (Avalonia `DragDrop.SetAllowDrop` + a reorder adorner, or `ItemsControl` + pointer-moved swap — keep it dependency-free); per-row action dropdown + keyboard shortcuts P/R/S/F/E/D; reword opens an inline message editor; live preview panel showing resulting history (fold squash/fixup rows into their parent pick). Entry point: branch/commit context menu "Interactive rebase onto…" (ties into B-2.2).
4. **Safety rails:** refuse to start if working tree dirty (offer stash), if `IsRebasing` already true, or if the range includes merge commits (v1: block with a message; `--rebase-merges` is a follow-up). First todo item cannot be `Squash`/`Fixup` (validate in the ViewModel).

**Code Modifications & Additions:**
- **Target files:** new `InteractiveRebaseService.cs`, `IInteractiveRebaseService.cs`, `Models/RebaseTodoItem.cs`, `Mainguard.App.Shell/ViewModels/InteractiveRebaseViewModel.cs`, `Views/InteractiveRebaseWindow.axaml(.cs)`; `Mainguard.App.Shell/Program.cs` (the `--rebase-editor`/`--rebase-msg` argv modes must run before Avalonia init and exit fast); `GitServices.cs` (`ContinueRebase` gains env passthrough).
- **Implementation specifics:** the editor-mode argv contract: git invokes `<editor> <path-to-file>`; Mainguard's handler copies its payload onto that path. Log both generated and final todo files to the operation journal for debuggability.

**PR Verification & Testing Strategy:**
- Integration tests (all via F4 + real git CLI — mark the test class `[Trait("Category","RequiresGitCli")]`): (a) reorder two commits → history order swapped, tree identical; (b) reword → new message, same tree; (c) squash 2→1 → one commit, combined message, combined diff; (d) fixup keeps first message; (e) drop removes the commit's changes; (f) conflict mid-rebase throws `MergeConflictException`, `ContinueRebase` after resolution completes the plan; (g) abort restores original HEAD exactly.
- Manual PR checklist: drag-reorder works; P/R/S/F/E/D shortcuts work; preview matches result; mid-rebase conflict routes to the resolver and continue finishes; Abort always returns to the pre-rebase SHA (verify with `git reflog`).

---

## B-4.5b — Worktree CLI Backend + Arbitrary-Commit Diffs (Roadmap Phase 4.5, remaining items)

**Priority:** P0 for the agent phases (worktrees are the agent-isolation backbone); MEDIUM for standalone Git UX.

**Objective & Context:** Phase 4.5 has two unfinished items besides interactive rebase: (1) worktree ops must drive the `git` CLI (`ListWorktrees`/`AddWorktree`/`RemoveWorktree` exist at `GitServices.cs:876-897` but LibGit2Sharp's worktree API has an empty-worktree creation bug and no working-directory property — locked decision says CLI); (2) working-tree diffs against arbitrary commits.

**Step-by-Step Implementation Guide:**
1. Reimplement the three worktree methods over `RunGit`:
   - `ListWorktrees` → `worktree list --porcelain`; parse stanzas (`worktree <path>` / `HEAD <sha>` / `branch <ref>` / `detached` / `locked`), map to `WorktreeItem { Path, HeadSha, Branch, IsLocked, IsMain }`.
   - `AddWorktree(repoPath, path, branch, createBranch)` → `worktree add [-b <branch>] <path> [<start>]`.
   - `RemoveWorktree(repoPath, path, force)` → `worktree remove [--force] <path>`; add `PruneWorktrees` → `worktree prune`.
2. Arbitrary-commit diffs: add `GetDiffAgainstCommit(repoPath, sha, string? path = null)` → `repo.Diff.Compare<Patch>(repo.Lookup<Commit>(sha).Tree, DiffTargets.WorkingDirectory | DiffTargets.Index, paths)`; surface in the commit context menu ("Diff working tree against this commit") reusing `DiffViewerControl`.
3. Worktree management UI is D-2.17b — this task is the backend.

**Code Modifications & Additions:**
- **Target files:** `GitServices.cs`, `IGitService.cs`; new `Mainguard.Agents/Models/WorktreeItem.cs`; `CommitTimelineViewModel.cs` (context-menu command), `DiffViewerViewModel.cs`.

**PR Verification & Testing Strategy:**
- Integration (RequiresGitCli): add worktree on a new branch → porcelain list shows 2 entries with correct branch/SHA; remove → 1 entry; prune after manual dir deletion cleans metadata; add-on-checked-out-branch fails typed. Diff-against-commit: modify a file, diff vs HEAD~1 → patch contains both committed and uncommitted deltas.
- Manual: create/remove a worktree from a scratch repo via a temporary debug command; open the added worktree as a repo in Mainguard — status/graph work.

---

## B-2.13 — Interactive / Partial Staging: Hunks & Lines (audit 2.13 / 1.13)

**Priority:** P0 — table-stakes; Sublime Merge/Fork's flagship. **Depends on:** F2, A-1.4 (safe-discard confirmation reuse).

**Objective & Context:** Staging is whole-file only (`Commands.Stage`, `GitServices.cs:78-97`). Crafting atomic commits needs hunk- and line-level stage/unstage/discard from the diff viewer.

**Step-by-Step Implementation Guide:**
1. **Structured patch model.** The diff viewer currently renders `Patch.Content` text. Add `Mainguard.Agents/Models/DiffHunk.cs`: parse the unified diff into `FilePatch { Header (---/+++ lines), List<DiffHunk> }`, `DiffHunk { OldStart, OldCount, NewStart, NewCount, List<DiffLine> Lines }`, `DiffLine { Kind (Context/Add/Del), Text }`. New `Mainguard.Agents/Services/PatchParser.cs` (pure; parse + serialize back to valid unified diff).
2. **Subset-patch builder.** `PatchBuilder.BuildHunkPatch(filePatch, selectedHunks)` and `BuildLinePatch(filePatch, hunk, selectedLineIndexes)`:
   - Hunk subset: emit header + selected hunks verbatim.
   - Line subset (the tricky one): within the hunk, keep selected `+`/`-` lines as-is; **unselected `-` lines become context lines; unselected `+` lines are dropped**; recompute the `@@ -a,b +c,d @@` counts from the resulting line mix. This is exactly what `git add -p`'s line-splitting does.
3. **Apply paths** (all via `RunGit`, patch over **stdin** — F2's `stdinContent`):
   - Stage: `git apply --cached --unidiff-zero -` .
   - Unstage: `git apply --cached -R -` (build the subset from the **index-vs-HEAD** patch for unstaging, not workdir-vs-index).
   - Discard: `git apply -R -` against the working tree — routed through A-1.4's confirmation dialog ("Discard 3 lines in foo.cs — this cannot be undone from the trash").
   - Add `IGitService.StagePatch / UnstagePatch / DiscardPatch(repoPath, string unifiedDiff)`.
4. **UI.** `DiffViewerViewModel`/`DiffViewerView.axaml`: hunk header rows gain Stage/Unstage/Discard buttons; line gutter supports click+drag multi-select with "Stage selected lines" in a context menu. After any partial op, re-fetch the file's diff and refresh `StagingPanelViewModel` (a file can now be simultaneously staged and modified — ensure the status grouping shows it in both lists, which LibGit2Sharp status already reports).
5. Both diff directions must be selectable in the viewer: workdir↔index (stageable) and index↔HEAD (unstageable) — add the toggle if not present.

**Code Modifications & Additions:**
- **Target files:** new `PatchParser.cs`, `PatchBuilder.cs`, `Models/DiffHunk.cs`; `GitServices.cs`, `IGitService.cs`; `DiffViewerViewModel.cs`, `DiffViewerView.axaml`, `StagingPanelViewModel.cs`.
- **Implementation specifics:** always operate on the freshly-fetched patch (stale hunk offsets = `git apply` rejects; on rejection, refresh the diff and ask the user to retry — never force with `--recount` silently). Handle `\ No newline at end of file` markers in parse/serialize.

**PR Verification & Testing Strategy:**
- Unit tests (pure, many): parser round-trips real patches byte-identically (fixture patches incl. no-newline-at-EOF, adjacent hunks, rename headers); line-subset builder produces correct recounted headers for: stage only additions, only deletions, mixed, first/last line of hunk.
- Integration (RequiresGitCli): file with 3 separated edits → stage hunk 2 only → `git diff --cached` contains exactly hunk 2, working tree unchanged; stage 1 line of a 4-line hunk → index blob contains exactly that line's change; unstage reverses; discard-lines removes them from workdir only after confirmation path.
- Manual PR checklist: stage/unstage/discard at hunk and line level from the UI; staging panel shows split state; commit of a partial stage contains only the staged lines (verify in the timeline diff).

---

## B-2.4 — Tag Management (audit 2.4)

**Priority:** P0 — completely absent today; no serious client omits tags.

**Objective & Context:** Create (lightweight + annotated), delete, push, checkout tags; render tag refs in the graph and a tags list in the sidebar.

**Step-by-Step Implementation Guide:**
1. Add to `IGitService`/`GitServices.cs`:

```csharp
IEnumerable<GitTagItem> GetTags(string repoPath);                 // name, targetSha, isAnnotated, message, tagger
void CreateTag(string repoPath, string name, string targetSha, string? message);  // annotated iff message != null
void DeleteTag(string repoPath, string name);                     // repo.Tags.Remove
void PushTag(string repoPath, string remoteName, string name);    // repo.Network.Push(remote, "refs/tags/"+name, opts)
void DeleteRemoteTag(string repoPath, string remoteName, string name); // push ":refs/tags/<name>"
void CheckoutTag(string repoPath, string name);                   // Commands.Checkout(repo, tag.Target.Sha) → detached HEAD
```

   Annotated: `repo.Tags.Add(name, target, GetSignature(repo), message)`; lightweight: `repo.Tags.Add(name, target)`. Validate names via `Reference.IsValidName("refs/tags/" + name)`.
2. **Graph rendering:** in the commit query path, build a `Dictionary<sha, List<RefLabel>>` joining `repo.Tags` (peel annotated tags to their target commit: `tag.PeeledTarget`) — merge into the existing branch-label pipeline feeding `CommitGraphCanvas`/`CommitRowViewModel`; render tag chips with a distinct shape/color.
3. **UI:** sidebar "Tags" section in `BranchBrowserViewModel` (list, context menu: checkout / push / delete / copy name); "New Tag…" in the commit context menu (B-2.2) opening `CreateTagDialog` (name, annotated checkbox, message box, target SHA prefilled).
4. `CheckoutTag` lands on detached HEAD — surface the existing detached-HEAD indicator (verify one exists; if not, add a header badge "Detached at v1.2.0").

**Code Modifications & Additions:**
- **Target files:** `GitServices.cs`, `IGitService.cs`; new `Models/GitTagItem.cs`, `Views/CreateTagDialog.axaml(.cs)` + ViewModel; `BranchBrowserViewModel.cs`, `CommitRowViewModel.cs`, `CommitTimelineViewModel.cs`, `Controls/CommitGraphCanvas.cs` (label rendering only).

**PR Verification & Testing Strategy:**
- Integration: create lightweight + annotated → `GetTags` returns both with correct `IsAnnotated`/message/target; annotated tag on an old commit peels to the right SHA; delete removes; push to F4 bare remote → remote repo's `refs/tags/` contains it; delete-remote clears it; invalid names (`"a b"`, `"-x"`, `"a..b"`) throw typed before touching the repo.
- Manual: create a tag from the commit context menu → chip appears on the right graph row instantly (watcher refresh); checkout tag → detached badge; push tag → visible on the remote.

---

## B-2.2 — Rich Commit-Graph Interactions (audit 2.2)

**Priority:** P0 — GitKraken's signature UX; mostly wiring existing commands. **Depends on:** B-2.4 (tag actions), B-4.5a (rebase-onto action).

**Objective & Context:** Right-click context menus on commits/branch labels in `CommitGraphCanvas`, drag-and-drop merge/rebase between branch labels, branch pinning, and graph filtering to current-branch+upstream. The service commands (`CheckoutRevision`, `ResetToCommit`, `RevertCommit`, `CherryPick`, `CreateBranch`) already exist.

**Step-by-Step Implementation Guide:**
1. **Hit-testing.** `CommitGraphCanvas` renders by `RowIndex`/`LaneIndex` from `CommitGraphRouter`. Add `GraphHitTester.HitTest(Point p) → GraphHit { Sha?, RefLabel?, Kind (Node/Label/Empty) }`: row = `(int)(p.Y + scrollOffset) / RowHeight`; node if `|p.X - laneCenterX(node.LaneIndex)| < NodeRadius + slop`; labels get recorded bounding rects during render (keep a per-frame `List<(Rect, RefLabel)>`).
2. **Context menu.** On `PointerReleased` right-click with a hit: build a `ContextMenu` from `MenuItemViewModel` trees (pattern already exists from Phase 4.3 branch menus). Commit menu: Checkout (detached), Create branch here, Create tag here, Cherry-pick, Revert, Reset current branch here (Soft/Mixed/Hard submenu, Hard gated by a confirmation dialog), Interactive rebase onto here, Copy SHA. Branch-label menu: reuse the Phase 4.3 branch context menu. All commands route through the async/`IsBusy` pattern (A-1.3).
3. **Drag-and-drop merge/rebase.** `DragDrop` on branch labels: drag label A, drop on label B → popup flyout with exactly two options: "Merge A into B" / "Rebase A onto B". Merging into a non-checked-out branch B: v1 keeps it simple — require B checked out (offer "Checkout B and merge A"); do not implement in-memory merges.
4. **Pinning + filtering.** `HashSet<string> PinnedRefs` persisted per-repo in SQLite; router input ordering puts pinned refs first (router already gives left-most dominance to earlier refs). Filter toggle "Current branch only": rebuild the walk with `CommitFilter.IncludeReachableFrom = { HEAD, upstream }` (flows through A-1.8's cache — the cache key must include the filter mode + pinned set).
5. Keyboard: `Delete` on a selected branch label = delete branch (with the existing safety dialog).

**Code Modifications & Additions:**
- **Target files:** `Controls/CommitGraphCanvas.cs`, new `Controls/GraphHitTester.cs`; `CommitTimelineViewModel.cs` (commands + menu construction), `MenuItemViewModel.cs`; `AppDbContext.cs` + migration (pinned refs); `GitServices.cs` (ensure `ResetToCommit` supports Soft/Mixed/Hard).

**PR Verification & Testing Strategy:**
- Unit tests: `GraphHitTester` pure-math tests (node center, label rect, row rounding at scroll offsets, misses); menu-construction tests (detached-HEAD hides "reset current branch"; menu on HEAD commit hides checkout).
- Integration: reset Soft/Mixed/Hard state assertions; cherry-pick from menu command path equals service call result.
- Manual PR checklist: right-click any node → correct menu; every action fires and refreshes; drag branch A onto B shows the two-option flyout and both work; pin a branch → its lane moves left-most and persists across restart; filter toggle collapses the graph to HEAD+upstream; hard reset demands confirmation.

---

# WORKSTREAM C — CATEGORY 2 P1 FEATURES (premium-client expectations)

---

## C-2.10 — Blame / Inline File Annotations

**Priority:** P1.

**Objective & Context:** Line-by-line blame (author, short-SHA, date) in a gutter of the file/diff viewer with click-through to the commit — GitLens' core daily-driver feature.

**Step-by-Step Implementation Guide:**
1. `IGitService.GetBlame(repoPath, path, string? startingSha = null)` → `repo.Blame(path, new BlameOptions { StartingAt = ... })`; map `BlameHunk`s to `BlameLine { LineNumber, Sha, ShortSha, Author, When, Summary }` (expand hunks to per-line rows for simple binding).
2. Cache per `(path, headSha)` in a bounded `MemoryCache`-style dictionary; invalidate on `RepositoryStateChanged`.
3. UI: a toggleable gutter column in the file viewer (AvaloniaEdit margin: implement a custom `AbstractMargin` rendering `author · shortSha · relative-date`, dimmed alternating by commit boundary). Tooltip = full SHA + message; click → select that commit in the timeline (message via `WeakReferenceMessenger`).
4. Run blame on a background thread (`Task.Run`) with a spinner in the gutter header — blame on big files is slow; honor `CancellationToken` when the user switches files.

**Code Modifications & Additions:**
- **Target files:** `GitServices.cs`, `IGitService.cs`; new `Models/BlameLine.cs`, `Mainguard.App.Shell/Controls/BlameMargin.cs`; `DiffViewerViewModel.cs`/file-viewer VM.

**PR Verification & Testing Strategy:**
- Integration: 3 commits touching disjoint lines of one file → blame maps each line to the right SHA/author; rename+edit keeps blame usable (document rename behavior); blame at `StartingAt = HEAD~1` ignores the newest commit.
- Manual: toggle blame on a large file → UI stays responsive; click a line → timeline selects the commit; switch files rapidly → no stale gutter (cancellation works).

---

## C-2.11 — File History & Line History

**Priority:** P1.

**Objective & Context:** "History of this file" (sequence of commits + diffs between adjacent versions) and line-range history. Backend primitive (`QueryBy(path)`) already exists inside `GetRecentCommits`.

**Step-by-Step Implementation Guide:**
1. `IGitService.GetFileHistory(repoPath, path)` → `repo.Commits.QueryBy(path, new CommitFilter { SortBy = Topological | Time })` mapping each `LogEntry` to `FileVersion { Commit, PathAtThatCommit }` (LogEntry tracks renames — expose the historical path).
2. `GetFileAtCommit(repoPath, sha, path)` → `commit[path].Target as Blob → GetContentText()` (binary check via `blob.IsBinary`).
3. UI: `FileHistoryView` — left: virtualized commit list for the file; right: `DiffViewerControl` diffing version N vs N−1 (`repo.Diff.Compare<Patch>(older.Tree, newer.Tree, new[]{ path })`). Entry points: staging-panel and diff-viewer context menu "File history".
4. Line history v1: from a selected line range, filter file history to commits whose patch intersects the range (parse hunks via B-2.13's `PatchParser` — reuse); follow renames via LogEntry paths. (Exact `git log -L` emulation is a follow-up; document the approximation.)

**Code Modifications & Additions:**
- **Target files:** `GitServices.cs`, `IGitService.cs`; new `ViewModels/FileHistoryViewModel.cs`, `Views/FileHistoryView.axaml(.cs)`; context-menu wiring in `StagingPanelViewModel.cs`, `DiffViewerViewModel.cs`.

**PR Verification & Testing Strategy:**
- Integration: file modified in commits 1,3,5 of 6 → history returns exactly those, newest first; rename in commit 4 → history spans the rename with correct historical paths; adjacent-version diff equals `git diff <a> <b> -- path`.
- Manual: open history on a frequently-edited file; arrow through versions; verify rename follow; binary file shows "binary" placeholder not garbage.

---

## C-2.14 — Remotes Management, Auto-Fetch & Push Options

**Priority:** P1 — fixes hardcoded `"origin"` everywhere and stale ahead/behind (A-1.12).

**Objective & Context:** `Fetch`, `PushBranch`, `GetRemoteUrl` hardcode `"origin"`; no add/remove/rename/prune, no background fetch, no force-with-lease / push-tags / set-upstream.

**Step-by-Step Implementation Guide:**
1. **Remotes CRUD:** `GetRemotes` (`repo.Network.Remotes` → name+fetch/push URLs), `AddRemote`, `RemoveRemote`, `RenameRemote` (LibGit2Sharp direct). UI: "Remotes" sidebar section with add/edit/remove dialogs.
2. **Kill hardcoded origin:** every call site derives the remote from the tracked branch (`branch.RemoteName`) falling back to a user selection when untracked or multiple remotes exist. Sweep: `grep -n '"origin"' Mainguard.Agents/`.
3. **Auto-fetch:** `AutoFetchService` in Core — a `PeriodicTimer` loop (interval from `UserPreferences.AutoFetchMinutes`, 0 = off, default 10) calling `Fetch(remote, prune: true)` per open repo off the UI thread; skip while `IsBusy`/mid-operation (`repo.Info.CurrentOperation != None`); on success update `LastFetchedUtc` (A-1.12) + refresh ahead/behind. Failures are silent-but-logged (no toast spam on flaky networks; show a subtle warning icon after 3 consecutive failures).
4. **Push options:** push dropdown — normal / **force-with-lease** (`RunGit ["push","--force-with-lease","<remote>","<ref>"]` — libgit2 has no lease) / push tags (`--tags`) / set upstream (`-u`). `PullStrategy` toggle already landed in A-1.5.
5. Prune toggle on manual fetch (`FetchOptions.Prune = true`).

**Code Modifications & Additions:**
- **Target files:** `GitServices.cs`, `IGitService.cs`; new `Services/AutoFetchService.cs`; `UserPreferences.cs`, `SettingsService.cs`; `RepoDashboardViewModel.cs`, `BranchBrowserViewModel.cs`, new `Views/RemoteEditDialog.axaml` + VM.

**PR Verification & Testing Strategy:**
- Integration: CRUD round-trip on remotes; push to a second remote (two F4 bare remotes) uses the tracked remote not `origin`; force-with-lease succeeds after local rebase when remote unchanged, **fails typed** when the remote moved (simulate by pushing from a second clone first — this is the safety property, test it explicitly); `-u` sets `branch.<name>.remote/merge` config.
- Manual: add a second remote in UI; auto-fetch fires on schedule (set 1 min, watch `LastFetchedUtc`); force-with-lease push after amend works; fetch-prune removes a deleted remote branch from the sidebar.

---

## C-2.16 — Diff Quality: Intra-line, Syntax Highlighting, Whitespace, Images

**Priority:** P1 — "most readable diff" is why people pick Sublime Merge/Fork.

**Step-by-Step Implementation Guide:**
1. **Intra-line:** for each Modified line pair in the side-by-side model (`SideBySideDiffRows.cs`), run DiffPlex word-level diff (`Differ.CreateWordDiffs`) and store changed-span ranges on `GitDiffLine` (`List<(int start,int len)> HighlightSpans`); render as inline background runs in the diff control (darker green/red on the light band).
2. **Syntax highlighting:** integrate `AvaloniaEdit.TextMate` + `TextMateSharp` grammars in the diff view (the AvaloniaEdit dependency exists from Phase 4.4). Grammar chosen by file extension; colorize token foreground, keep add/del background bands. Toggle in preferences (perf escape hatch).
3. **Whitespace toggle:** `GetFileDiff(..., ignoreWhitespace)` — LibGit2Sharp `CompareOptions` doesn't expose ignore-whitespace reliably; fall back to `RunGit ["diff","-w",...]` parsed through `PatchParser` (B-2.13) when the toggle is on. Note in the UI that whitespace-ignored view disables partial staging (offsets differ).
4. **Image diff:** if `blob.IsBinary` and extension ∈ {png,jpg,jpeg,gif,bmp,webp,ico}: load both revisions into `Bitmap`s (from blob streams) and show `ImageDiffControl` (side-by-side + opacity swipe slider). Other binaries: "Binary file changed (old size → new size)".

**Code Modifications & Additions:**
- **Target files:** `Models/GitDiffLine.cs`, `Models/SideBySideDiffRows.cs`, `DiffViewerViewModel.cs`, `Views/DiffViewerView.axaml`; new `Controls/ImageDiffControl.axaml(.cs)`; `GitServices.cs` (`ignoreWhitespace` param); NuGet: `AvaloniaEdit.TextMate`, `TextMateSharp.Grammars`.

**PR Verification & Testing Strategy:**
- Unit: intra-line span computation (single word change → one span; full-line rewrite → whole-line span; unicode/emoji don't split spans mid-codepoint).
- Integration: `-w` diff of a whitespace-only change → zero hunks; mixed change → only real hunks.
- Manual: C# file diff shows keywords colored + word-level highlight; whitespace toggle hides indent-only churn; PNG change shows side-by-side + swipe; 5k-line diff still scrolls at 60 FPS (profile before merge).

---

## C-2.8 — Multi-Host Auth + SSH Key Manager

**Priority:** P1. **Depends on:** A-1.7 (host-keyed keyring + credential helper are its foundation).

**Objective & Context:** Generalize GitHub-only auth to GitLab/Bitbucket/Azure DevOps/self-hosted; built-in SSH keygen + upload; per-host OAuth device flow where supported.

**Step-by-Step Implementation Guide:**
1. `IHostProvider` abstraction in `Mainguard.Agents/Sync/`: `{ string Host; bool SupportsDeviceFlow; Task<string> AcquireTokenAsync(...); Task RegisterSshKeyAsync(string pubKey, string title); string TokenUsername { get; } }` — GitHub uses `x-access-token`, GitLab uses `oauth2`, Bitbucket `x-token-auth`, Azure DevOps PAT-as-password. Implement `GitHubProvider` (refactor `GitHubAuthClient` into it), `GitLabProvider` (device flow supported), `BitbucketProvider` + `AzureDevOpsProvider` (PAT entry dialog v1), `GenericHostProvider` (PAT/basic).
2. `HostProviderRegistry.Resolve(host)` → provider (self-hosted GitLab detectable by a user setting per host). `GetCredentialsProvider` and the credential helper (A-1.7) ask the registry for the username scheme.
3. **SSH manager:** `SshKeyService`: generate (`ssh-keygen -t ed25519 -f <path> -N <passphrase>` via process runner), list keys in `~/.ssh`, copy public key, upload via provider API. Store passphrase in keyring (`sshpass:<keypath>`). `GetCredentialsProvider` returns `SshUserKeyCredentials` for SSH remotes (resolves the existing `SshAuthenticationException` path).
4. Preferences UI: "Accounts" page — per-host rows (host, auth kind, status), add-account flow (pick host kind → device flow or PAT), SSH keys page (list/generate/upload).

**Code Modifications & Additions:**
- **Target files:** `Mainguard.Agents/Sync/GitHubAuthClient.cs` → refactor; new `Sync/IHostProvider.cs`, `Sync/GitLabProvider.cs` etc., `Security/SshKeyService.cs`; `Security/SecureKeyring.cs`; new `Views/AccountsSettingsView.axaml` + VMs; `DeviceFlowAuthDialog` generalization.

**PR Verification & Testing Strategy:**
- Unit: registry resolution (github.com→GitHub; gitlab.selfhosted.corp with user hint→GitLab; unknown→Generic); per-provider token-username mapping; keygen arg construction (passphrase never in a shell string — ArgumentList).
- Integration (manual, per host — document in the PR): clone/push a private repo on GitHub (device flow), GitLab (device flow), Bitbucket (PAT), Azure DevOps (PAT), and one SSH remote with passphrase. Security re-check from A-1.7: no secrets in argv during any flow.

---

## C-2.7 — Commit & Tag Signing (GPG / SSH)

**Priority:** P1.

**Step-by-Step Implementation Guide:**
1. **Create signed commits via the CLI** (simplest correct path): when `prefs.SignCommits`, commit through `RunGit ["commit","-m",msg]` honoring repo config `commit.gpgsign=true`, `user.signingkey`, `gpg.format` (`openpgp`|`ssh`) — git orchestrates gpg/ssh-keygen itself. Keep LibGit2Sharp commits for unsigned. (Alt path `repo.ObjectDatabase.CreateCommitWithSignature` requires Mainguard to run gpg and splice the signature — more code, same result; use only if CLI-less operation becomes a requirement.)
2. Signed tags: `RunGit ["tag","-s",name,"-m",msg,target]` when signing on (extends B-2.4's `CreateTag`).
3. **Verification badges:** batch `RunGit ["log","--format=%H %G? %GS","<range>"]` for visible commits; map `%G?` (G=good, B=bad, U=unknown, N=none) to a badge on `CommitRowViewModel` (shield icon: green/yellow/red/none) with tooltip = signer.
4. Preferences: signing on/off, format (GPG/SSH), key picker (list via `gpg --list-secret-keys --keyid-format long` / `~/.ssh/*.pub`), "GPG program" path override (writes `gpg.program`).

**Code Modifications & Additions:**
- **Target files:** `GitServices.cs` (commit path branch, `GetSignatureStatuses`), `IGitService.cs`; `CommitRowViewModel.cs`, `CommitTimelineView.axaml`; settings page + `UserPreferences.cs`.

**PR Verification & Testing Strategy:**
- Integration (RequiresGitCli, skip-if-no-gpg): generate a throwaway GPG key in the test fixture (`gpg --batch --gen-key`), enable signing, commit → `git verify-commit HEAD` exits 0 and the badge status is `G`; unsigned commit shows `N`; SSH-format variant with a generated ed25519 key + `gpg.ssh.allowedSignersFile`.
- Manual: enable signing with a real key, commit, confirm GitHub shows "Verified"; wrong key configured → typed error surfaced (not a hang — `GIT_TERMINAL_PROMPT=0` + pinentry mode must be non-interactive-safe; document loopback pinentry caveat).

---

## C-2.5 — Submodule Support

**Priority:** P1.

**Step-by-Step Implementation Guide:**
1. Read: `repo.Submodules` → `SubmoduleItem { Path, Url, HeadSha, Status }` (map `SubmoduleStatus` flags to Uninitialized / UpToDate / Modified / Dirty).
2. Mutate via `RunGit`: `submodule update --init --recursive`, `submodule update --remote <path>`, `submodule sync`.
3. UI: "Submodules" sidebar panel — per-row status dot + actions (Init, Update, Sync, **Open as repository** → opens the submodule path as a new repo tab). Dirty submodules also appear in the staging panel as the special gitlink entry — label them clearly ("submodule commit changed").

**Target files:** `GitServices.cs`, `IGitService.cs`, new `Models/SubmoduleItem.cs`, `BranchBrowserViewModel.cs` (or new `SubmodulesViewModel`), sidebar view.

**PR Verification & Testing Strategy:**
- Integration (RequiresGitCli): superproject + submodule from two F4 temp repos (`-c protocol.file.allow=always` — modern git blocks file-protocol submodules by default; the code must pass this only in tests, not production); fresh clone shows Uninitialized → init → UpToDate; commit inside submodule → superproject shows Modified gitlink.
- Manual: open a real repo with submodules; init/update from the panel; open-as-repo works and the sub-repo is fully functional.

---

## C-2.6 — Git LFS Support

**Priority:** P1.

**Step-by-Step Implementation Guide:**
1. Detection: `.gitattributes` contains `filter=lfs` → repo is LFS; `RunGit ["lfs","version"]` probe once for availability (missing → panel shows install guidance, features disabled).
2. All ops via `RunGit`: `lfs install --local`, `lfs track "<pattern>"` (then stage `.gitattributes`), `lfs untrack`, `lfs ls-files`, `lfs pull`, `lfs prune --dry-run` → confirm → `lfs prune`.
3. UI: per-repo "LFS" settings section — tracked-pattern list (add/remove), tracked-files list with sizes, Pull/Prune buttons (async, A-1.3 pattern). Mark LFS pointer files in the diff viewer ("LFS object — 45 MB") instead of showing pointer text as a diff.
4. Credentials for LFS endpoints flow through the same credential helper (A-1.7) — LFS invokes `git credential` itself, so this comes free once the helper is set for the repo.

**Target files:** new `Services/LfsService.cs` + interface; settings/panel views; `DiffViewerViewModel.cs` (pointer detection: file starts with `version https://git-lfs.github.com/spec/v1`).

**PR Verification & Testing Strategy:**
- Integration (RequiresGitCli + git-lfs, skippable trait): `lfs track "*.bin"` writes `.gitattributes`; committed binary is a pointer in the object DB (`git show HEAD:file.bin` starts with `version https://`); `ls-files` lists it; untrack round-trips.
- Manual: clone a real LFS repo (with credentials) → pull fetches objects; diff on an LFS image shows the image-diff control (C-2.16) fed from the local object, not pointer text.

---

## C/D-2.15 — Command Palette & Keyboard Shortcuts (+ "Open in Terminal")

**Priority:** P1 (palette/shortcuts); the embedded terminal itself is Phase 7.1 (Workstream G) — do not build it twice.

**Step-by-Step Implementation Guide:**
1. **Action registry:** `Mainguard.Agents/Services/ActionRegistry.cs` — `AppAction { Id, Title, Category, KeyGesture?, Func<bool> CanExecute, Func<Task> Execute }`. ViewModels register their commands at construction (checkout, push, pull, stash, create branch, open repo, …). This registry later becomes the agent-command entry point (Phase 7.5) — design it UI-free.
2. **Palette:** overlay (`Ctrl+P`) — fuzzy matcher (subsequence scoring: consecutive-run + word-boundary bonus, ~80 lines, no dependency) over actions + branch names ("checkout ") + bookmarked repos. `ListBox` results, Enter executes, Esc closes.
3. **Shortcuts:** central `KeyBindings` map applied at `MainWindow` scope, defined in a `ShortcutMap` (action-id → gesture) persisted in `UserPreferences`; Preferences page lists actions with rebind capability (capture-next-keypress textbox) and conflict detection. Defaults: Ctrl+P palette, Ctrl+Enter commit, Ctrl+Shift+P push, F5 refresh, Ctrl+B new branch.
4. **Open in Terminal:** repo/worktree context action launching the OS terminal at the repo path (Windows: `wt.exe -d <path>` falling back to `cmd /K cd /d <path>` via ProcessStartInfo without shell-execute quirks; macOS `open -a Terminal <path>`; Linux `x-terminal-emulator`). This is the Git-phase stopgap until 7.1 embeds one.

**Target files:** new `ActionRegistry.cs`, `FuzzyMatcher.cs`, `ViewModels/CommandPaletteViewModel.cs`, `Views/CommandPaletteView.axaml`; `MainWindow.axaml(.cs)`, `MainWindowViewModel.cs`, `UserPreferences.cs`, settings page.

**PR Verification & Testing Strategy:**
- Unit: fuzzy matcher ranking table ("chb" matches "Checkout Branch" above "Cherry-pick... b"); registry `CanExecute` filtering; shortcut-conflict detector.
- Manual: Ctrl+P → type 3 letters → correct top hit → Enter executes; rebind a gesture, restart, still bound; palette respects `IsBusy` (disabled actions greyed); Open in Terminal lands in the right cwd on Windows.

---

# WORKSTREAM D — CATEGORY 2 P2 DIFFERENTIATORS

---

## D-2.9 — Unlimited Undo / Redo of Git Operations

**Priority:** P2 differentiator (Tower's headline feature); also the safety net for agent-driven history rewriting (Phase 7).

**Objective & Context:** Undo any mutating op — commit, merge, rebase, reset, branch delete, cherry-pick — and redo it, backed by an operation journal + Git's reflog (which already persists every ref move).

**Step-by-Step Implementation Guide:**
1. **Journal.** `Mainguard.Agents/Services/OperationJournal.cs` + SQLite table (`OperationJournalEntry { Id, RepoPath, Kind, WhenUtc, RefSnapshots(json: refName→sha, incl. HEAD symbolic target), PostSnapshots(json, filled after op), Description }`). API: `IDisposable BeginOperation(repoPath, kind, description)` — captures pre-state on create, post-state on dispose.
2. **Instrument every mutating `GitServices` method** (Commit, Merge, Rebase start/continue/abort, Reset, Revert, CherryPick, DeleteBranch, CreateBranch, StashPush/Pop, tag ops, interactive rebase B-4.5a): wrap the body in `using var op = _journal.BeginOperation(...)`. Capture: HEAD symbolic ref + SHA, the affected branch refs, and for branch-delete the deleted tip SHA + upstream config.
3. **Undo:** restore each recorded ref via `repo.Refs.UpdateTarget(refName, preSha)`; if HEAD's branch tip moved, follow with a working-tree reset (`repo.Reset(ResetMode.Hard, preSha)`) **only after** a dirty-tree check — if the tree is dirty, refuse with "commit or stash first" (never destroy uncommitted work). Branch-delete undo = recreate branch at recorded tip + restore upstream. Commit undo = mixed reset to the parent (keeps the work staged→unstaged, nothing lost).
4. **Redo:** symmetric, from `PostSnapshots`. Any *new* mutating op truncates the redo stack.
5. **Non-undoable ops are journaled but flagged** (`Push` — remote side; `StashPop` content conflicts): the UI shows them in history without an Undo button, with a reason tooltip.
6. **UI:** toolbar Undo/Redo (Ctrl+Z/Ctrl+Y at repo scope, only when no text editor focused) + an "Operation History" flyout (Tower-style list, per-entry Undo). Confirmation when undo hard-moves HEAD.

**Target files:** new `OperationJournal.cs` + `AppDbContext` migration; every mutating method in `GitServices.cs`; `RepoDashboardViewModel.cs`, new `Views/OperationHistoryFlyout.axaml` + VM.

**PR Verification & Testing Strategy:**
- Integration (the heart of the PR — one test per op kind): perform op → Undo → assert full ref/HEAD state equals pre-op (compare all branch SHAs + HEAD target) → Redo → equals post-op. Branch-delete round-trip preserves upstream. Undo-with-dirty-tree refuses and changes nothing. New op after undo clears redo.
- Manual: rebase a branch, click Undo → branch exactly back (verify `git log`); delete a branch, Undo from history flyout → restored with tracking.

---

## D-2.12 — Reflog Viewer & Recovery

**Priority:** P2 — pairs with D-2.9.

**Step-by-Step Implementation Guide:**
1. `IGitService.GetReflog(repoPath, refName = "HEAD", int take = 200)` → `repo.Refs.Log(refName)` → `ReflogItem { From, To, Message, Committer, When }`.
2. UI: `ReflogView` — ref picker (HEAD + local branches), virtualized entry list. Read-only first render; per-entry actions behind confirmations: "Create branch here" (safe, default), "Reset current branch here (hard)" (destructive confirm, routes through the journal so it's undoable).
3. "Recover deleted branch" quick flow: filter HEAD reflog for `branch: deleted` / checkout messages and offer one-click branch recreation at the orphaned SHA.

**Target files:** `GitServices.cs`, `IGitService.cs`, new `Models/ReflogItem.cs`, `ViewModels/ReflogViewModel.cs`, `Views/ReflogView.axaml`.

**PR Verification & Testing Strategy:**
- Integration: commit→reset-hard→reflog shows both moves; "create branch here" at the pre-reset entry restores the lost commit; deleted-branch recovery finds the tip.
- Manual: bad hard reset in a scratch repo → recover via reflog view entirely inside Mainguard.

---

## D-2.17 — Repository Management: Profiles, Worktree UI, Clone Progress

**Priority:** P2. **Depends on:** B-4.5b (worktree backend).

**Step-by-Step Implementation Guide:**
1. **Profiles:** `Profile { Name, UserName, Email, SigningKey?, DefaultPullStrategy }` in SQLite; per-repo assigned profile (bookmark column). On repo open / profile switch, write `user.name`/`user.email` (+ signing config) to **local** repo config. UI: profile manager in Preferences + a profile dropdown in the repo header. Auto-suggest: if a repo's remote host matches a profile's host hint, offer it.
2. **Worktree UI:** sidebar "Worktrees" panel over B-4.5b — list (path, branch, locked, current), Create dialog (branch picker/new-branch + path picker + validation: non-empty dir, branch not already checked out), Open (new repo tab), Remove (confirm; force option when dirty, routed through a dirty-state warning). This panel is the same component the agent phase reuses — keep the ViewModel free of "agent" assumptions.
3. **Clone progress:** `Clone` gains an `IProgress<CloneProgress>` (`{ Stage, ReceivedObjects, TotalObjects, ReceivedBytes, CheckoutCompleted, CheckoutTotal }`) wired from `CloneOptions.OnTransferProgress` + `OnCheckoutProgress`; `CloneDashboardViewModel` binds a two-stage progress bar (receiving → checking out) + cancel (transfer progress callback returning false cancels libgit2 transfer).

**Target files:** `AppDbContext.cs` + migration, new `Models/Profile.cs`, `Services/ProfileService.cs`; new `ViewModels/WorktreePanelViewModel.cs`, `Views/WorktreePanelView.axaml`; `GitServices.cs` (Clone), `CloneDashboardViewModel.cs`, `Views/CloneDashboardView.axaml`.

**PR Verification & Testing Strategy:**
- Integration: profile apply writes local config (global untouched); clone of the F4 bare remote reports monotonic progress and completes; cancelled clone leaves no partial directory (cleanup on cancel).
- Manual: two profiles, switch → next commit uses the right author; create/open/remove worktree from the panel; clone a large repo → live progress, cancel mid-transfer works.

---

# WORKSTREAM E — PHASE 5: ANALYTICS & PREMIUM POLISH

---

## E-5.1 — gitignore-Aware Language Parser (finish the crawler)

**Priority:** P2 polish. The donut-chart wiring is done; the crawler must respect `.gitignore` recursively.

**Steps:** In `RepositoryAnalyzer` (`Mainguard.Agents/Analytics/RepositoryAnalyzer.cs`), replace raw directory recursion with an ignore-aware walk: use `repo.Ignore.IsPathIgnored(rel)` per directory before descending (cached, same LRU as A-1.10), and always skip `.git/`. Run fully on a background thread with `CancellationToken` (cancel on repo switch). Count bytes per extension via `LanguageRegistry`.
**Target files:** `RepositoryAnalyzer.cs`, `AnalyticsViewModel.cs`.
**Verification:** integration test — repo with `node_modules/` ignored: its bytes excluded, tracked source included; nested negation patterns (`!keep.js`) honored (LibGit2Sharp evaluates them). Manual: analytics on a JS monorepo completes quickly and matches `github-linguist`-ish proportions.

## E-5.2 — Churn & Punch Card

**Priority:** P2 polish.

**Steps:** `AnalyticsService.ComputeChurn(repoPath, since)` — single background history walk (reuse A-1.8's cached SHA list); per commit `repo.Diff.Compare<PatchStats>(parent.Tree, commit.Tree)` for +/− totals bucketed by week; `PunchCardStats` (exists) buckets commits by (day-of-week, hour). Stream partial results via `IProgress` so charts fill in live. Cache results per HEAD SHA in SQLite so re-opening analytics is instant.
**Target files:** new `Analytics/AnalyticsService.cs`, `PunchCardStats.cs`, `AnalyticsViewModel.cs`, `AnalyticsView.axaml` (LiveChartsCore line/heat charts).
**Verification:** integration — scripted repo with known commit times/sizes yields exact bucket values; walk honors cancellation. Manual: 10k-commit repo computes without UI stalls; charts animate in.

## E-5.3 — UI Transitions & Micro-Animations

**Priority:** P2 polish.

**Steps:** Avalonia `TransitioningContentControl` for tab navigation (150–200 ms opacity+translate); `Transitions` on analytics cards (opacity on load); loading indicators get fade-in-delayed (only show if >150 ms — no flicker on fast ops). Central `Styles` resource (`Animations.axaml`) so durations/easings are consistent; respect a "reduce motion" preference.
**Target files:** new `Mainguard.App.Shell/Styles/Animations.axaml`, `App.axaml`, `MainWindow.axaml`, `AnalyticsView.axaml`.
**Verification:** manual/UX review — tab switches animate at 60 FPS (no layout thrash — animate opacity/transform only, verified with the Avalonia diagnostics overlay); reduce-motion kills all transitions.

## E-5.4 — Ghost Loading / Skeleton Screens

**Priority:** P2 polish (explicit roadmap item).

**Steps:** `SkeletonView` overlay for `RepoDashboardView`: static mock frames of Staging Panel, Diff Viewer, and Timeline drawn as rounded gray blocks with a shared pulsing opacity animation. Show while `RepoDashboardViewModel.IsLoading` (set true immediately on repo switch, false when the first status+commit page lands). Content renders **behind** progressively — skeleton fades per-region as each region's data arrives (three independent `IsXLoading` flags), so fast repos flash nothing (150 ms minimum-show guard).
**Target files:** new `Views/SkeletonOverlay.axaml`, `RepoDashboardView.axaml`, `RepoDashboardViewModel.cs`.
**Verification:** manual — open a huge repo: skeleton appears instantly, regions fill independently, no unstyled flash; open a tiny repo: no skeleton flicker. Automated: VM test that `IsLoading` flags sequence correctly around the async load.

---

# WORKSTREAM F6 — PHASE 6.4: LLM API KEY MANAGEMENT (BYOK)

**Priority:** P0 for Phase 7 (agents need keys before anything else in the swarm works).

**Objective & Context:** Store user LLM keys (Anthropic/OpenAI/…) encrypted via the OS keyring, never in plaintext config; validate tier/rate limits at entry so users learn their realistic swarm ceiling before their first 429; inject into sandboxes via tmpfs only (never argv/env-file-on-disk); show the subscription-OAuth ToS notice.

**Step-by-Step Implementation Guide:**
1. **`ISecureKeyStore`** in `Mainguard.Agents/Security/`: formalize what `SecureKeyring` does behind an interface — `Set/Get/Delete(string key)`; backends: DPAPI (`ProtectedData.Protect`, `DataProtectionScope.CurrentUser`) on Windows, Keychain via `security` CLI or Security.framework P/Invoke on macOS, `libsecret` on Linux. Key names: `llm:anthropic`, `llm:openai`, `llm:<provider>` — reuse/extend the existing `SecureKeyring` rather than duplicating it.
2. **Entry UI:** `ApiKeySettingsViewModel` + settings page — masked `TextBox` (`PasswordChar`), provider dropdown, Save/Delete per provider. Zero the string reference after storing; never write the value to logs, bindings that persist, or exceptions.
3. **Key health check:** on save, call the provider's cheapest authenticated endpoint (Anthropic: tiny `POST /v1/messages` with `max_tokens: 1`; OpenAI: `GET /v1/models`) — read rate-limit headers (`anthropic-ratelimit-requests-limit`, tokens-per-minute) and render "Key valid — tier supports ~N concurrent agents" (N = conservative mapping table maintained in code). Invalid → inline error, key not stored.
4. **tmpfs injection (contract for Phase 7.2):** `CredentialInjector.BuildEnvFileContent(agentId)` → decrypt in memory → transfer over the authenticated gRPC channel → daemon writes `/dev/shm/mainguard/<agentId>/.env` inside the container's tmpfs mount (`--mount type=tmpfs,destination=/dev/shm`), mode 0400, owner = agent uid. Agent CLIs launch with `docker exec -e DOTENV_CONFIG_PATH=/dev/shm/.env ...` — the key never appears in `ps` output, container image layers, or persistent disk.
5. **ToS disclosure:** when a user picks CLI OAuth (Claude Pro/Max subscription) instead of an API key, show the mandatory notice dialog (Anthropic's April 2026 third-party OAuth restriction; API key = primary supported path) and record the acknowledgment.

**Target files:** `Security/SecureKeyring.cs` → extract `ISecureKeyStore`; new `Security/ApiKeyHealthService.cs`, `Security/CredentialInjector.cs` (contract now, daemon side lands in G-7.2); new `ViewModels/ApiKeySettingsViewModel.cs` + settings view; ToS dialog.

**PR Verification & Testing Strategy:**
- Unit: keystore round-trip per platform backend (Windows CI at minimum); health-check parser against recorded provider responses (fixtures, no live calls in CI); ceiling mapping table.
- **Security review checklist (blocking):** grep repo + logs for key material after a full save/use cycle → nothing on disk unencrypted; key not in any `ProcessStartInfo.Arguments`; settings `config.json` contains no key field at all.
- Manual: enter a real Anthropic key → "valid, tier X" appears; enter garbage → rejected; delete → gone from OS credential store (verify in Windows Credential Manager).

---

# WORKSTREAM G — PHASE 7: INTEGRATED MULTI-AGENT CONTROL CENTER

> **Architecture (locked):** native Avalonia client on Windows ⇄ **gRPC over localhost** (WSL2 mirrored networking, per-session token) ⇄ headless .NET daemon in the `MainguardOS` WSL2 distro running raw `dockerd`. Agent I/O lives on ext4; Windows↔VM exchange is Git objects only. AF_VSOCK = research fallback only. sbx microVMs = post-v1 optional backend, never nested in WSL2.

---

## G-7.0 — The Daemon & gRPC Contract (prerequisite the roadmap implies)

**Priority:** P0 — everything in Phase 7 needs a process to live in.

**Objective & Context:** Today Mainguard is a single Avalonia process. The Phase 7 architecture requires `Mainguard.Server`: a headless .NET daemon (Kestrel gRPC) that will run inside `MainguardOS`, owning containers, PTYs, worktrees, the SQLite DB (single-owner rule), the AI Gateway, and session durability. The UI becomes a gRPC client for all agent features (existing local-repo Git features stay in-process).

**Step-by-Step Implementation Guide:**
1. New projects: `Mainguard.Server` (ASP.NET Core gRPC host, Linux-x64 publish target) and `Mainguard.Protos` (shared `.proto` contracts, `Grpc.Tools` codegen consumed by both Server and App).
2. Define v1 proto services (versioned package `mainguard.v1`):
   - `AgentService`: `SpawnAgent`, `StopAgent`, `ListAgents`, `StreamAgentEvents` (server-stream: status transitions, badges).
   - `TerminalService`: `Attach(agentId)` (bidi stream: client→input/resize, server→output frames — raw bytes in 7.1a, grid damage in 7.1b; make the frame a `oneof { bytes raw; GridUpdate grid; }` **now** so 7.1b is not a breaking change).
   - `RepoSyncService`: `ProvisionRepo`, `GetWorktrees`, `MergeQueueService` ops (G-7.3).
   - `GatewayService`: budgets, token-spend telemetry stream.
3. **Auth:** daemon generates a per-session token at startup, written to a file readable only by the user (`%LOCALAPPDATA%\Mainguard\daemon.token` via `\\wsl$` write-back or handed over the launch channel); client sends it as a gRPC metadata header; interceptor rejects everything else. Bind `127.0.0.1` only (mirrored networking makes that reachable from Windows on 11 22H2+; document `dnsTunneling`/`autoProxy` flags in `.wslconfig` for VPN cases).
4. **Dev loop:** the daemon must also run directly on Windows/localhost (no WSL) behind a `--local-dev` flag so client work doesn't require the VM; CI runs it this way.
5. Client side: `Mainguard.App.Shell/Services/DaemonClient.cs` wrapping channel creation, token metadata, reconnect-with-backoff, and a connection-state observable the Activity Bar renders.

**Target files:** new `Mainguard.Server/` (Program.cs, `Services/*GrpcService.cs`), new `Mainguard.Protos/` (`agent.proto`, `terminal.proto`, `reposync.proto`, `gateway.proto`); `Mainguard.slnx`; `Mainguard.App.Shell/Services/DaemonClient.cs`.

**PR Verification & Testing Strategy:**
- Integration: spin the daemon in-proc (`WebApplicationFactory`) — authenticated call succeeds, missing/wrong token → `PERMISSION_DENIED`; terminal bidi stream echoes a test payload; client reconnect after daemon restart resumes `StreamAgentEvents`.
- Manual: run daemon under WSL2 with mirrored networking, client on Windows → `ListAgents` round-trips; kill daemon → client shows disconnected state and recovers on restart.

---

## G-7.1a — Terminal Engine, Interim: Porta.Pty + Vendored Iciclecreek Renderer

**Priority:** P0 (ship fast). **Gated by:** G-7.1c harness (runs against this engine from day one).

**Objective & Context:** Native PTYs so agent CLIs see a real TTY (`isatty()` true — Ink TUIs like Claude Code degrade otherwise). PTY bytes framed on the daemon into 16 ms chunks with a **stateful VT boundary detector**, streamed over gRPC, rendered by a vendored fork of `Iciclecreek.Avalonia.Terminal` behind a stable `ITerminalView` interface so the 7.1b engine swap never touches ViewModels.

**Step-by-Step Implementation Guide:**
1. **PTY shim.** `Mainguard.Agents/Agents/PtyProcessShim.cs` over `Porta.Pty` (NuGet): `Spawn(command, args, cwd, env, cols, rows)` → `{ Stream IO, Resize(cols,rows), Kill(), ExitCode task }`. `Cwd` locked to the agent's ext4 worktree. ConPTY on Windows (dev loop), forkpty on Linux (production daemon).
2. **Framing.** Daemon-side `TerminalStreamer`: read PTY into `ArrayPool<byte>` buffers; a 16 ms ticker flushes accumulated bytes as one gRPC frame. **VT boundary detector:** a small state machine tracking whether the buffer tail is mid-sequence — states: Ground / Esc / CSI / OSC / DCS / SS3, plus UTF-8 continuation counting; if the tail is mid-sequence or mid-codepoint, hold those bytes for the next frame (cap holdback at 4 KB then flush regardless — a malformed stream must not stall forever).
3. **`ITerminalView`** in `Mainguard.Agents`: `FeedOutput(ReadOnlyMemory<byte>)`, `event Action<byte[]> InputAvailable`, `Resize(cols,rows)`, `GetStateSnapshot()/RestoreState()`. Vendor the Iciclecreek fork into `external/Iciclecreek.Avalonia.Terminal/` (project reference, license file retained) and adapt it behind the interface.
4. **Client wiring.** `TerminalViewModel` consumes `TerminalService.Attach`; keystrokes (incl. Ctrl+C = 0x03) encode to bytes → input stream; resize events propagate to the PTY. Render invalidation on a 60 FPS `DispatcherTimer` that only fires when dirty.
5. **Bounds:** 10,000-line circular scrollback on the control's backing model (hard cap, overwrite oldest).

**Target files:** new `Mainguard.Agents/Agents/PtyProcessShim.cs`, `Mainguard.Server/Services/TerminalStreamer.cs`, `Mainguard.Agents/Terminal/ITerminalView.cs`, `Mainguard.Agents/Terminal/VtBoundaryDetector.cs`; vendored `external/Iciclecreek.Avalonia.Terminal/`; `Mainguard.App.Shell/ViewModels/TerminalViewModel.cs`, new `Views/TerminalView.axaml`.

**PR Verification & Testing Strategy:**
- Unit: `VtBoundaryDetector` — split every fixture sequence (CSI SGR, OSC 8 with ST and BEL terminators, DCS, 2/3/4-byte UTF-8, emoji ZWJ) at every byte offset and assert reassembly is byte-identical and no frame ever ends mid-sequence; holdback cap flushes malformed input.
- Integration: spawn `/bin/cat` via the shim → echo round-trip; spawn a curses probe (`python -c` with curses init) → no crash, `isatty` reports true inside the PTY.
- Manual: run real Claude Code in the terminal — spinners, colors, box-drawing render correctly at 60 FPS; Ctrl+C interrupts; resize reflows; scrollback caps at 10k lines (memory flat under `yes | head -c 100M`).

---

## G-7.1b — Terminal Engine, Target: Server-Side libvterm + First-Party Skia Grid Renderer

**Priority:** P0 before beta. **Gated by:** G-7.1c passing on this engine. **Depends on:** G-7.0, G-7.1a (interfaces).

**Objective & Context:** Move VT emulation into the daemon on `libvterm` (Neovim's `:terminal` core — conformance-proven against exactly the Ink TUIs Mainguard hosts). Stream **screen-grid damage updates** instead of raw bytes. Payoffs: terminal state survives client crashes (= Session Durability for terminals), and the grid protocol is unchanged for Cloud Worktrees (Phase 9).

**Step-by-Step Implementation Guide:**
1. **Bindings.** `Mainguard.Agents.Terminal.Native/LibVterm.cs`: P/Invoke for `vterm_new`, `vterm_set_size`, `vterm_input_write`, `vterm_obtain_screen`, `vterm_screen_set_callbacks` (`damage`, `movecursor`, `settermprop`, `sb_pushline`, `sb_popline`), `vterm_screen_get_cell`, `vterm_keyboard_*`. Bundle a Linux-x64 `libvterm.so` with the daemon publish (build in CI from pinned upstream source; daemon-side only — no client native binaries).
2. **Emulator session.** One `VtermSession` per agent PTY, owned by the persistent session leader (G-7.3): PTY output → `vterm_input_write`; damage callbacks accumulate dirty rects into a pending-update set; the existing 16 ms ticker drains it into a `GridUpdate` proto (runs of cells: glyph (UTF-32 + combining), fg/bg (truecolor), attr bitset; cursor pos/visibility/shape; scroll ops as first-class messages so the client can blit). Scrollback ring buffer (10k lines) implemented on `sb_pushline`/`sb_popline`.
3. **Snapshot/attach.** On client (re)connect: full-grid snapshot + cursor + mode flags + scrollback page-in (paged, lazy). This one code path serves UI crash recovery, daemon reattach, and future cloud attach.
4. **Renderer.** `Mainguard.App.Shell/Controls/TerminalGridControl.cs` (~1–2k lines, fully owned): Skia monospace cell grid — glyph-run cache keyed by (glyph, style), damage-only redraw, selection + clipboard, IME composition overlay, CJK double-width cells, mouse reporting encoder, keyboard encoder (incl. bracketed paste, modifyOtherKeys basics). Implements `ITerminalView`; swapped in behind a feature flag (`TerminalEngine=libvterm|interim`) so both engines coexist until 7.1c signs off.
5. Keep 7.1a's raw path compiled and selectable until the flag flips permanently.

**Target files:** new `Mainguard.Agents.Terminal.Native/` (bindings + build script), `Mainguard.Server/Services/VtermSession.cs`, proto `terminal.proto` (`GridUpdate` — already a `oneof` slot from G-7.0), `Mainguard.App.Shell/Controls/TerminalGridControl.cs`; feature flag in settings.

**PR Verification & Testing Strategy:**
- G-7.1c conformance + replay suites pass on libvterm engine ≥ parity with 7.1a (this is the merge gate — no regressions on any golden transcript).
- Integration: kill the client mid-`htop`, reattach → identical grid (snapshot path); daemon restart with session leader alive → reattach shows live session.
- Perf: sustained `cat` of a 50 MB log — client CPU bounded, damage coalescing keeps frame payloads sane (measure; no full-grid sends in steady scroll).
- Manual: Claude Code, vim, htop, tmux each drive correctly incl. alternate screen, mouse, truecolor, emoji width.

---

## G-7.1c — VT Conformance & Replay Harness (non-negotiable gate)

**Priority:** P0 — starts alongside 7.1a; gates 7.1a and 7.1b.

**Step-by-Step Implementation Guide:**
1. **Conformance CI:** run `vttest`/`esctest` scripted against the active engine (drive the emulator headless: feed test sequences, assert screen state). Wrap as xUnit `[Theory]` fixtures with a known-failures allowlist file (so progress is monotonic and visible in review).
2. **Golden transcripts:** record real sessions with `script`/asciinema — Claude Code, OpenCode, vim (open/edit/split/quit), htop (60 s), tmux (split/switch). Store under `Mainguard.Tests/Transcripts/` with a recording README. Replay: feed the raw byte stream through the emulator (deterministic timing not required — replay is byte-order-only), snapshot the final grid + selected intermediate checkpoints (embedded markers), compare cell-by-cell (glyph, fg, bg, attrs) against committed goldens.
3. **Required coverage matrix** (each has at least one transcript or synthetic test): alternate screen, DEC 2026 synchronized output, truecolor, CJK/emoji width, bracketed paste, mouse reporting, OSC 8 hyperlinks.
4. Harness runs against **both** engines via `ITerminalView`-level abstraction of "feed bytes → read grid"; 7.1a's control needs a headless grid-readback hook (test-only).

**Target files:** new `Mainguard.Tests/Terminal/ConformanceTests.cs`, `ReplayTests.cs`, `Transcripts/`, `Goldens/`; CI workflow update.

**PR Verification & Testing Strategy:** the harness is itself the deliverable — PR shows it red/green on the interim engine with the allowlist checked in; reviewer regenerates one golden locally and gets a byte-identical file (determinism proof).

---

## G-7.1d — Break-Glass xterm.js Fallback

**Priority:** P2, feature-flagged, unadvertised.

**Steps:** an `xterm.js` pane inside the existing WebView2/CefGlue host (ships for Vibe preview anyway): local HTML asset bundling xterm.js, bridged to `TerminalService.Attach` raw-byte frames via WebView messaging; input events posted back. Behind `Settings.EnableFallbackTerminal`, default off, no UI affordance beyond a hidden setting. Never marketed.
**Target files:** new `Mainguard.App.Shell/Controls/WebTerminalFallback/` (html asset + host control), flag in settings.
**Verification:** flag on → agent terminal renders via xterm.js and passes the basic replay smoke set; flag off (default) → no WebView instantiated (assert in a memory test).

---

## G-7.2a — `MainguardOS` Bootstrapper

**Priority:** P0. **Depends on:** installer Phase J for the OOBE path; this task covers the runtime bootstrap the daemon/app performs.

**Steps:**
1. `Mainguard.Agents/Agents/MainguardOsBootstrapper.cs` (runs on Windows client): check `wsl.exe --list --quiet` for `MainguardEnv`; if absent, `wsl --import MainguardEnv <appdata-dir> <tarball>` (tarball ships via installer, versioned — see J-3).
2. Generate/merge `%UserProfile%\.wslconfig`: memory cap (default: min(50% RAM, 8 GB)), processor count, `autoMemoryReclaim=gradual`; **merge, never clobber** the user's existing file (parse INI, add only our keys under `[wsl2]`, back up first).
3. Inside the instance on first boot: append `fs.inotify.max_user_watches=524288` to `/etc/sysctl.conf` + `sysctl -p`; start `dockerd` via the distro's init hook (`/etc/wsl.conf` `[boot] command=`), wait for `/var/run/docker.sock`.
4. Launch the daemon inside the VM (`wsl -d MainguardEnv -- /opt/mainguard/mainguardd --token-out ...`), health-check the gRPC endpoint, surface bootstrap progress in the UI (staged checklist view).
5. Idempotency: every step checks-then-acts; a partial previous bootstrap resumes cleanly.

**Target files:** new `MainguardOsBootstrapper.cs`, `WslConfigWriter.cs`; OOBE/first-run VM wiring; daemon systemd-style unit inside the tarball build.

**Verification:** integration on a Windows box with WSL2 — fresh import completes < 60 s; `.wslconfig` merge preserves pre-existing user keys (unit-test the INI merger with fixtures); re-run is a no-op; `docker info` succeeds inside `MainguardEnv`; kill the VM (`wsl --terminate`) → next app start re-bootstraps silently. **Uninstall test:** terminate→poll-stopped→unregister leaves other distros untouched (never `wsl --shutdown`).

---

## G-7.2b — Repo Provisioner: the Git-Native Sync Boundary

**Priority:** P0 — the locked replacement for Hollow-Core; the data path every agent depends on.

**Objective & Context:** On project open, mirror the Windows repo into a bare repo on ext4, create agent worktrees there, and register the VM repo as a remote (`mainguard-vm`) of the Windows repo. All cross-boundary state = Git objects. **No Windows-path bind mounts, ever.**

**Steps:**
1. `Mainguard.Agents/Agents/RepoProvisioner.cs` (daemon-side): `Provision(windowsRepoIdentity)`:
   - Compute `<hash>` = SHA-256 of the normalized Windows repo path → `~/mainguard/repos/<hash>.git`.
   - First time: `git clone --bare <source> ~/mainguard/repos/<hash>.git`; source = the Windows repo reached via the WSL interop path (`/mnt/c/...`) **for clone/fetch only** — object transfer over 9P is acceptable (one-time bulk + incremental packs), file *watching* over 9P is what's forbidden. Subsequent opens: `git fetch` to update.
   - `git config core.untrackedCache true` in the bare repo template for worktrees.
   - On the **Windows side** (client, via local `GitServices`): `git remote add mainguard-vm <\\wsl.localhost\MainguardEnv\home\...\repos\<hash>.git>` if missing (use the `\\wsl.localhost` UNC form; verify `git fetch mainguard-vm` works through it — fallback: `wsl -d MainguardEnv git ...` transport shim).
2. `WorktreeManager.cs` (daemon): `CreateAgentWorktree(repoHash, agentId)` → `git branch agent/<id> main && git worktree add ~/mainguard/worktrees/<repo>/<agentId> agent/<id>` (CLI via F2's runner compiled into the daemon); `RemoveAgentWorktree` → `worktree remove --force` + `branch -D`; `Prune()`.
3. `pnpm install` immediately after worktree creation when a `pnpm-lock.yaml` exists (global content-addressable store → hardlinked `node_modules`, N agents ≈ 1× disk).
4. Expose via `RepoSyncService` proto: `ProvisionRepo(path) → { repoHash, vmRemoteUrl }`, `CreateWorktree(agentId)`, `ListWorktrees`, `RemoveWorktree`.

**Target files:** new `Mainguard.Agents/Agents/RepoProvisioner.cs`, `WorktreeManager.cs`; `Mainguard.Server/Services/RepoSyncGrpcService.cs`; Windows-side remote registration in `RepoDashboardViewModel`/project-open flow.

**Verification:**
- Integration (Linux CI): provision from a fixture repo → bare repo exists, fetch updates it, worktree add/remove round-trips, second provision is incremental (measure: no re-clone).
- **Boundary audit (blocking manual check):** `docker inspect` on any agent container shows **zero** mounts under `/mnt/c` or any `drvfs` path; the only repo data path is ext4. `git fetch mainguard-vm && git merge agent/<id>` on the Windows repo brings over an agent commit byte-identically.
- Manual: open a Windows repo → provisioning completes; make an agent commit in the VM worktree; merge to main from the UI → commit appears in the Windows repo log.

---

## G-7.2c — Container Hardening + Default-Deny Egress Firewall

**Priority:** P0 launch-tier security (promoted from Phase 8) — the primary prompt-injection exfiltration control.

**Steps:**
1. **Container spec** (`Docker.DotNet` `CreateContainerAsync` from the daemon): static base image (Nix/Devbox preinstalled — toolchains sideload at runtime with `devbox add`, **never `docker build` at runtime**, it severs PTYs); `--userns-remap` (engine daemon.json), `no-new-privileges:true`, default seccomp, `--memory` + `--pids-limit` per container, worktree mounted **from ext4 only**, tmpfs at `/dev/shm` for credentials (F6 contract), read-only rootfs where the CLI tolerates it.
2. **Egress:** attach all agent containers to an internal Docker network whose only route out is a proxy container (Envoy or tinyproxy+iptables): default-deny; allowlist = model APIs (`api.anthropic.com`, `api.openai.com`, …) + package registries (npm, PyPI, NuGet, crates) + the git host of the current repo. Config generated by the daemon; DNS pinned to the proxy. `HTTP(S)_PROXY` env into containers **and** iptables DROP on direct egress (belt and braces — CLIs that ignore proxy env must hard-fail).
3. **Credential isolation:** per-sandbox tmpfs env file only (F6); **no `~/.claude` or any global auth-dir mounts** — each sandbox sees exactly its own agent's material, read-only where the CLI permits.
4. Per-repo persistent jail: one container per repo kept across restarts (preserves node_modules + agent session caches); `docker start` if stopped.
5. Allowlist is user-visible and editable (feeds Phase 8.1's network transparency view); changes logged.

**Target files:** new `Mainguard.Agents/Agents/SandboxEngine.cs` (Docker.DotNet), `EgressProxyConfigurator.cs`; base image Dockerfile under `build/mainguardos/`; `GatewayService` proto additions for allowlist CRUD.

**Verification (security boundaries — must be in the PR description with evidence):**
- From inside an agent container: `curl https://api.anthropic.com` (allowlisted) succeeds **via proxy**; `curl https://example.com` fails (connection refused/blocked, not slow-timeout); direct-IP egress fails (iptables); DNS exfil attempt (`dig randomhost.attacker.tld`) fails.
- `docker inspect`: no Windows paths, no global auth mounts, `no-new-privileges` present, memory/pids limits set, userns active (`/proc/self/uid_map` ≠ identity).
- Integration: sideload a toolchain with `devbox add jq` while a PTY session is live → session survives.

---

## G-7.2d — AI Gateway + Admission Control + Zombie Prevention

**Priority:** P0 launch-blocking (Gateway), P0 (admission), P1 (zombie).

**Steps:**
1. **`AiGateway.cs`** (daemon): agents reach model APIs **through the egress proxy → gateway** (gateway is the allowlisted route for model hosts): global token-bucket (requests + tokens/min from the key's measured tier, F6 health check), FIFO queue per priority class, 429/`Retry-After` interception → **pause the worker's PTY input + mark status `RateLimited`** with exponential backoff instead of letting the CLI crash and lose context; per-agent/per-day budget enforcement (reject + surface when exhausted); cost telemetry events (tokens in/out per agent) streamed via `GatewayService` to the Resource Monitor.
2. **Admission control:** daemon samples VM memory (`/proc/meminfo`) every 5 s; spawning blocked above threshold (default 85%, overridable in Settings) with an honest message ("4–6 agents on 16 GB"). Expose current headroom in `ListAgents` metadata.
3. **Zombie prevention:** on daemon boot, enumerate containers via `Docker.DotNet` (**sole source of truth — no lockfiles**, PID recycling makes them lie); reconcile against the DB's expected agents; dead container → `git worktree prune`, mark agent `Dead` for UI disposal; orphaned live container (no DB row) → adopt or stop per policy.

**Target files:** `Mainguard.Agents/Agents/AiGateway.cs`, `AdmissionController.cs`, `SwarmReconciler.cs`; proto `gateway.proto`; Resource Monitor hooks (G-7.4).

**Verification:**
- Unit: token-bucket math (burst, refill, multi-agent fairness); backoff schedule; budget cutoff.
- Integration: fake model endpoint returning 429 with `Retry-After: 5` → worker pauses, resumes at ~5 s, CLI process never saw the 429 (it saw a delayed 200); memory pressure simulated → spawn rejected with typed reason; kill a container out-of-band → boot reconcile prunes its worktree and the UI shows Dead→cleanup.
- Manual: two agents hammering one key → both proceed without crashes, spend counters tick in the Resource Monitor.

---

## G-7.3 — Agent Lifecycle, Merge Queue & Session Durability

**Priority:** P0. **Depends on:** G-7.2b/c/d.

**Steps:**
1. **Cooperative Yield Protocol:** daemon writes `[IPC_UPDATE_REQUESTED]` to the agent's stdin channel (or the adapter's control mechanism), awaits `[IPC_UPDATE_READY]` on output (timeout → force-suspend the container via `docker pause`). Only then touch the worktree. Guard every Git mutation: abort if `.git/worktrees/<id>/rebase-merge/` exists or HEAD is detached; wrap Git ops in exponential-backoff retry on `index.lock` errors.
2. **Keep-alive rebase:** suspend (yield) → `git -C <worktree> add -A && git commit -m "wip: sync" && git rebase main` → resume. Conflicts → agent status `Conflict`, surface the 3-way resolver (B-2.3) against the worktree.
3. **`MergeQueue.cs`:** state machine per worker: `Working → Verifying → Verified(main@<sha>) → AwaitingReview → Merged | Rejected`, plus `StaleVerified`. **Every merge to `main` marks all other `Verified` workers stale**; stale workers auto re-enter (yield → keep-alive rebase → re-run verification container). Merges on stale verification are **blocked by default** (settings override, loudly labeled).
4. **Verification runs:** containerized test-suite execution (project's configured test command) in the worker's sandbox; result recorded as `main@<sha>` + pass/fail + log artifact.
5. **Foreground merge (human, on Windows):** "Merge to Main" → Windows-side `git fetch mainguard-vm && git merge agent/<id>` → post-merge `npm install --ignore-scripts` wrapped in Polly retry (3 attempts, 1500 ms exponential backoff) against NTFS `EPERM`/`EBUSY`. **Flagged-changes gate:** before the merge button enables, any diff touching `package.json` scripts, lockfiles, `.github/workflows/`, `.vscode/`, or git hooks must be explicitly acknowledged in a distinct review panel (detector = path+content rules over the merge diff).
6. **Rejection:** delete agent branch, `npm prune` in the sandbox, teardown per policy.
7. **Session Durability:** PTYs spawn under a persistent session leader in the VM (tmux server or a first-party leader process holding the PTY master, daemon-independent); daemon restart reattaches (leader registry in the daemon DB, reconciled like G-7.2d). With 7.1b, terminal state also survives via daemon-side grids.
8. **Teardown:** `IDisposable` agent context — kill PTY, `git worktree remove --force`, `git branch -D agent/<id>`, close floating `Dock.Avalonia` windows (G-7.4), verify clean filesystem.
9. **Remote IDE review:** "Review in IDE" launches `code --folder-uri vscode-remote://wsl+MainguardEnv<worktree-path>`.

**Target files:** new `Mainguard.Agents/Agents/Orchestrator/MergeQueue.cs`, `AgentLifecycle.cs`, `CooperativeYield.cs`, `SessionLeader` assets in the tarball; Windows-side `ForegroundMergeService.cs` (+ Polly NuGet), `FlaggedChangeDetector.cs`; proto `reposync.proto` merge-queue RPCs.

**PR Verification & Testing Strategy:**
- Unit: MergeQueue state machine exhaustively (incl. stale cascade on merge, re-verify re-entry, blocked-stale-merge); FlaggedChangeDetector fixtures (script edit in package.json flags; dependency bump alone doesn't flag scripts category but does flag lockfile).
- Integration: two fake workers (scripted containers) → A merges → B flips to StaleVerified, auto-rebases, re-verifies; merge button blocked until B fresh. `index.lock` contention test (hold the lock, run keep-alive) retries then succeeds.
- **Security manual check:** poison a fixture `package.json` `postinstall` in an agent branch → flagged panel appears, and post-merge install runs `--ignore-scripts` (verify script did NOT execute via a canary file).
- Manual: full loop — spawn agent, agent commits, verify, review in VS Code Remote, merge to main on Windows, teardown leaves no worktree/branch/container residue (`git worktree list`, `docker ps -a`).

---

## G-7.4 — Split Activity Bar & Docking UI

**Priority:** P0 (the swarm is unusable without mission control).

**Steps:**
1. **Dock layout:** integrate `Dock.Avalonia`; default agent workspace = Terminal + Diff Viewer (agent-branch vs main, reuses `DiffViewerControl`) + Staging Tree. Layout persisted per user.
2. **Activity bar** (`ActivityBarView.axaml`): 2-row grid — Row 0: Resource Monitor (LiveChartsCore 60 s CPU/RAM of the VM + per-agent drill-down sparklines + token-spend counters from `GatewayService` stream) and pinned core tabs incl. Coordinator (pulse animation bound to `IsAttentionRequired`); Row 1: `ItemsControl` + `VirtualizingStackPanel` over `ObservableCollection<WorkerAgentViewModel>`, LIFO insert (`Insert(0, vm)`).
3. **Micro-badges:** `AgentStatus → Brush` converter: Running 🟢, AwaitingMerge 🟡, Conflict 🔴, StaleVerified ⚪/gray, RateLimited (pattern from G-7.2d), Dead.
4. **OS notifications** on transitions into waiting/blocked states (Windows toast via `Microsoft.Toolkit.Uwp.Notifications` or Avalonia's managed notifications; respect focus — suppress when the app is foreground on that agent).
5. **Teardown discipline:** `WeakReferenceMessenger` for global events; `AgentSandboxViewModel : IDisposable` — on tab close: stop `DispatcherTimer`s, dispose WebView2, clear terminal buffers, traverse the `IDock` factory and `Close()` floating `IWindow`s (documented Dock.Avalonia leak).

**Target files:** new `Mainguard.App.Shell/ViewModels/ActivityBarViewModel.cs`, `AgentSandboxViewModel.cs`, `WorkerAgentViewModel.cs`; `Views/ActivityBarView.axaml`, `AgentSandboxView.axaml`; `Converters/AgentStatusToBrushConverter.cs`; NuGet `Dock.Avalonia`, `LiveChartsCore.SkiaSharpView.Avalonia` (present).

**Verification:**
- Unit: status→brush mapping; LIFO ordering; `IsAttentionRequired` derivation.
- Memory test (blocking): open/close an agent tab 50× → stable heap (no Skia visual-tree or timer leaks; use `dotMemory`/`GC.GetTotalMemory` assertion harness); floating window count returns to 0.
- Manual: 4 concurrent agents — badges live-update, resource charts tick, coordinator pulses on a blocked worker, OS toast fires when backgrounded.

---

## G-7.5 — Dual-Mode Orchestration (Manual / Coordinator)

**Priority:** P0 — the product thesis.

**Steps:**
1. **Coordinator (Middle Manager):** `CoordinatorAgent.cs` — chat interface agent with **no code, no worktree, no merges**; internal tool-calling API: `spawn_worker(taskSpec)`, `get_worker_status`, `send_worker_prompt`, `request_verification` — capped by `MaxSubagentsLimit` + Gateway budgets + admission control.
2. **Plan-Approval Dry Runs:** worker spawn is two-phase: Coordinator emits a structured `TaskPlan { Scope: files[], Approach, TestStrategy }` (JSON schema-validated); UI renders it for approval; **workers only start on approved plans**; plan + approver identity persisted to the audit log (Phase 8.2 hook — write the log record now, hash-chaining later).
3. **Terminal locking:** worker `TerminalViewModel.IsReadOnly` bound to `IsCoordinatorManaged` (input stream severed daemon-side too — client-side-only locking is not a boundary); 🔒 "Managed by Coordinator" banner. Manual Mode: `[+]` spawns read-write agents.
4. **Kill Switch:** one command — Cooperative Yield to all agents (timeout→`docker pause`), freeze containers, snapshot state (journal entry + container pause + queue freeze); single UI control, always visible.
5. **Human handoff:** Coordinator flags readiness (AwaitingReview badge); merges only ever happen via the human foreground path (G-7.3). Conflicts during the human's merge open the 3-way tool; the Coordinator is unaffected (it never merges).
6. Cross-agent dependency: Coordinator serializes dependent tasks (B waits for A's merge, then keep-alive rebase) — task-partitioning quality is a tracked KPI (telemetry: % tasks parallelizable, rework rate).

**Target files:** `Mainguard.Agents/Agents/Orchestrator/CoordinatorAgent.cs`, `WorkerAgent.cs`, `TaskPlan.cs`; `Mainguard.App.Shell` plan-approval view + chat view; daemon-side input-lock in `TerminalService`.

**Verification:**
- Unit: spawn cap + budget rejection; plan schema validation; kill-switch fan-out ordering (yield before pause).
- Integration: scripted Coordinator (fake LLM) partitions 2 independent tasks → both workers run concurrently → both verified → sequential human merges with stale-re-verify in between (end-to-end of the whole workstream).
- **Security manual:** attempt to type into a locked worker terminal → daemon rejects input (verify at the gRPC layer, not just UI); kill switch freezes all containers within 5 s (`docker ps` shows paused).

---

# WORKSTREAM H — PHASE 8: ENTERPRISE AI GOVERNANCE & SECURITY

---

## H-8.1 — Source-Available Trust Architecture (LOCKED DECISION)

**Priority:** P0 for enterprise GA; licensing split is locked (FSL backend / proprietary GUI+Coordinator).

**Steps:**
1. **Repo split to enforce the license boundary:** extract the daemon, sandbox/worktree engine, and agent adapters (`Mainguard.Server`, `Mainguard.Agents/Agents`, adapters) into a source-available repository under **FSL** (converts to Apache-2.0 after two years); the Avalonia GUI, Coordinator intelligence, and governance features stay in the private repo consuming the FSL packages. CI publishes the FSL repo's NuGet artifacts; the private repo pins versions.
2. **Security architecture document** (`docs/security-architecture.md`, published): egress allowlisting, credential isolation, sandbox boundaries, gRPC auth, exactly what leaves the machine (answer: BYOK provider calls only). Kept in the FSL repo so it's auditable next to the code it describes.
3. **Network transparency view:** in-app panel streaming every outbound connection from daemon + sandboxes (source = egress proxy logs, G-7.2c): destination, agent, bytes, allow/deny verdict. Filterable; export.
4. **Independent security audit** commissioned pre-enterprise-GA; findings + report published (engineering task: a `SECURITY.md` intake + fix workflow).

**Target files:** repo/CI restructuring; `docs/security-architecture.md`; new `ViewModels/NetworkTransparencyViewModel.cs` + view; proxy log streaming RPC.

**Verification:** license headers + `LICENSE` correct per artifact (CI check); transparency view shows a live allowlisted call and a denied attempt within seconds of occurrence; doc review sign-off by the team against the shipped defaults (each doc claim has a test or config reference).

---

## H-8.2 — Tamper-Evident Swarm Auditing

**Priority:** P1 (enterprise). **Depends on:** G-7.5 plan-approval records.

**Steps:**
1. `AuditLog` (daemon, SQLite + append-only file): every inference (model version, full input prompt, raw output), every spawn/kill, every plan approval/merge decision with the authorizing OS identity. Records are `{ seq, timestamp, type, payload, prevHash, hash = SHA256(prevHash ‖ canonical(payload)) }` — a hash chain.
2. **External anchoring:** every N records / 24 h, submit the head hash to an RFC 3161 timestamping authority; store the TSA token alongside. Verification tool: `mainguardd audit verify` walks the chain + validates anchors.
3. **Log governance:** AES-GCM encryption at rest (key in OS keyring), configurable retention (default 90 d), redaction API (redact payload, keep hash-chain integrity by storing the redaction as a new chained event referencing the original's hash — never rewrite history).

**Target files:** new `Mainguard.Agents/Audit/AuditLog.cs`, `HashChain.cs`, `Rfc3161Anchor.cs`; daemon wiring at every Gateway/lifecycle/approval touchpoint; `mainguardd` verify subcommand.

**Verification:** unit — chain verification detects any single-byte tamper; redaction preserves verifiability; anchor round-trip against a public TSA (integration, network-gated). Manual: run a swarm session, `audit verify` passes; hand-edit a record → verify fails at the right seq.

---

## H-8.3 — SIEM Exportability

**Priority:** P1 (enterprise).

**Steps:** structured audit events (from H-8.2 stream) exported as: CEF/JSON over syslog (TCP/TLS), Splunk HEC, and generic webhook. `SiemExporter` with per-sink config, buffering + retry, and a delivery-status panel. Event taxonomy documented (`docs/siem-events.md`): plan_approved, merge_approved, merge_rejected, agent_spawned, egress_denied, budget_exceeded, killswitch, …

**Target files:** new `Mainguard.Agents/Audit/SiemExporter.cs` + sink implementations; settings UI; docs.

**Verification:** integration against a local syslog container + mock HEC — events arrive schema-valid (JSON-schema test); sink outage → buffered redelivery, no loss up to buffer cap; load test 1k events/min.

---

## H-8.4 — Enterprise Access & Policy (RBAC / SSO / SCIM)

**Priority:** P2 (enterprise GA).

**Steps:** policy model `{ Role → permissions: spawn_agents, approve_plans, approve_merges, edit_egress, edit_budgets }`; OIDC SSO login in the client (loopback+PKCE, reuse J-4 infra) mapping IdP groups→roles; SCIM 2.0 endpoint on the org-config service for provisioning; **enforcement lives in the daemon interceptors** (every gRPC call carries the identity token; UI hiding is not enforcement). Centralized policy doc (model allowlists, egress rules, per-team budgets) fetched signed from the org endpoint and enforced by the AI Gateway + egress configurator.

**Target files:** `Mainguard.Server/Auth/` (OIDC validation, policy interceptor), policy schema + fetcher; admin settings UI.

**Verification:** integration — role without `approve_merges` gets `PERMISSION_DENIED` on the merge RPC even with a hand-crafted client; policy update propagates without daemon restart; SCIM create/deactivate round-trip against a SCIM test harness.

---

## H-8.5 — Supply-Chain & Secrets Compliance

**Priority:** P2 (enterprise GA).

**Steps:** (1) secrets-manager backends for `ISecureKeyStore` (HashiCorp Vault KV2, AWS Secrets Manager) selectable per org policy alongside OS keyrings; (2) **SCA/license scan at the merge gate:** on `Verified`, scan the agent diff for introduced dependencies (lockfile delta) → license lookup (SPDX data, local database) → copyleft heuristics flag GPL/AGPL contamination as a blocking review category next to the G-7.3 flagged-changes panel (same UI pattern). Note in docs: these support *customers'* compliance; Mainguard-the-company SOC 2 is an organizational track, not a product feature.

**Target files:** `Security/VaultKeyStore.cs`, `AwsSecretsKeyStore.cs`; `Agents/Orchestrator/LicenseGate.cs`; merge-gate UI panel.

**Verification:** unit — lockfile-delta extraction (npm/pnpm/NuGet fixtures) and license classification table; integration — agent branch adding an AGPL package blocks the merge button with the flag; Vault round-trip against a dev-mode Vault container.

---

# WORKSTREAM I — PHASE 9: CLOUD WORKTREES

**Priority:** post-desktop-GA (private beta target: within two quarters of GA). Listed for architectural guardrails that land **now**:

1. **Protocol discipline (continuous):** every G-7.0 proto change stays transport-agnostic — no localhost assumptions, no file-path leakage from daemon→client except opaque handles; the 7.1b grid protocol and merge-queue RPCs must work over WAN latency (test with `tc netem` 80 ms in CI once per release).
2. **Implementation when scheduled:** daemon container image (same binary, cloud pod), mTLS + user auth replacing the local session token, per-tenant repo encryption at rest, and a `RemoteEnvironment` picker in the client (local VM | cloud). Repo sync becomes `git push mainguard-cloud` over HTTPS instead of the UNC fetch.
3. **Verification:** the G-7.5 end-to-end integration suite re-runs against a cloud-deployed daemon unchanged — that suite passing over WAN **is** the acceptance test; latency budget: terminal echo < 100 ms at 80 ms RTT (grid protocol batching).

---

# WORKSTREAM J — VELOPACK DISTRIBUTION & OOBE FIRST-RUN

---

## J-1 — Diagnostics & Requirements Gate (Installer Phase 1)

**Priority:** P0 for distribution.

**Steps:** `Mainguard.App.Shell/Oobe/SystemDiagnostics.cs` — Windows 11 x86_64 check (`RuntimeInformation` + build number), WMI `Win32_ComputerSystem.HypervisorPresent` / `VirtualizationFirmwareEnabled` for VT-x/AMD-V, WSL2 state (`wsl --status` parse), free disk ≥ 20 GB on the target volume. macOS: Colima presence; Linux: Docker Engine socket. Each check returns `{ Pass | Fail(actionable message + doc link) }`; the OOBE view renders the checklist and **hard-stops before any system modification** on failure. ARM64 → explicit "unsupported in v1" gate.
**Verification:** unit-test each parser against captured outputs (WSL status strings vary by version — fixture them); manual matrix: pass machine, no-virtualization VM (fails with BIOS guidance), ARM64 (blocked).

## J-2 — OS Enablement & Auto-Resume Reboot (Installer Phase 2)

**Steps:** unelevated setup UI; UAC requested only at "Construct Sandbox" (relaunch elevated helper). If `VirtualMachinePlatform` disabled → `Enable-WindowsOptionalFeature` (surface the raw PowerShell being run — transparency default). If reboot required: create an **elevated Scheduled Task** with `--resume` (never `RunOnce` — it drops privileges), prompt, reboot; on resume the task relaunches setup at the exact step (state file in `%LOCALAPPDATA%\Mainguard\oobe-state.json`).
**Verification:** VM snapshot testing — WSL-less Windows 11: full flow incl. reboot resumes at the right step with elevation intact; cancel mid-flow → resumable; task deleted after completion.

## J-3 — `MainguardOS` Payload & Update Pipeline (Installer Phase 3)

**Steps:** build pipeline for `MainguardOS.tar.gz` (stripped rootfs: `dockerd`, `git`, node, python3, session-leader assets, `mainguardd`) — reproducible build script under `build/mainguardos/`, **versioned** (`/etc/mainguardos-release`); silent `wsl --import` (reuses G-7.2a code). **Update pipeline:** app checks tarball version vs installed VM; in-place upgrade path (export agent state → replace VM or apply overlay update → restore); defined CVE patch cadence for `dockerd`/base OS documented in `docs/mainguardos-updates.md` (first enterprise question).
**Verification:** CI builds the tarball reproducibly (hash-stable given pinned inputs); import→boot→`docker info` green in an automated Windows runner; upgrade test: v N VM with a live provisioned repo → v N+1 upgrade preserves repos/worktrees.

## J-4 — Windows Deep Integration: Context Menus & Loopback OAuth (Installer Phase 4)

**Steps:** (1) `HKCR\Directory\shell\Mainguard` + `Directory\Background\shell` "Open with Mainguard" (icon, `%V` arg) written at install, removed at uninstall. (2) **OAuth = RFC 8252 loopback (`127.0.0.1` ephemeral port) + PKCE** for all token flows (agents, deployment, SSO); `mainguard://` URL handler registered **only for non-secret deep links** (open-repo). Shared `LoopbackOAuthListener` (HttpListener on an ephemeral port, `state` validation, single-use, 5-min timeout, success page). Backend-detected agent OAuth URLs carry `state=<agent_uuid>` → callback routes to the right sandbox.
**Verification:** unit — PKCE verifier/challenge, state round-trip/rejection; manual — context menu opens the repo; full device-independent OAuth against a real provider; **security check:** no token ever appears in a `mainguard://` URL (grep handler registrations + code path).

## J-5 — Agent Provisioning & Pinned Adapter Channel (Installer Phase 5)

**Steps:** OOBE step listing supported CLIs (Claude Code, AGY, OpenCode) → API-key entry (primary, F6 flow) or CLI OAuth (with ToS notice). **Pinned adapters:** a separately versioned manifest (`adapters.json`: cli → { version, install command, config shims, health probe }) fetched from a Mainguard-owned channel, installed *inside the VM* at pinned versions — never `@latest`. Adapter updates ship independently of app releases (keeps perpetual-fallback licenses functional). Health probe per adapter post-install (`claude --version` + a no-op invocation). Finish → launch into an authenticated workspace.
**Verification:** manifest schema tests; install of a pinned adapter into a fresh VM passes its probe; simulate upstream CLI breaking change with a newer version → pinned install unaffected; adapter-channel update applies without an app update.

## J-6 — Clean Teardown (Uninstaller)

**Steps:** uninstall hook: `wsl.exe --terminate MainguardEnv` → poll `wsl -l -v` until Stopped (releases `.vhdx` locks) → `wsl.exe --unregister MainguardEnv`; **never `wsl --shutdown`** (kills the user's personal distros). Remove registry keys, scheduled tasks, `%LOCALAPPDATA%\Mainguard`. Data-safety by design: user source lives on the Windows drive; the VM repo is a mirror — the Windows repo just loses the `mainguard-vm` remote (optionally remove it for them).
**Verification:** manual on a machine with a personal Ubuntu distro: uninstall → MainguardEnv gone, Ubuntu untouched, no orphaned `.vhdx` (check disk), user repo intact and fully functional; reinstall after uninstall works cleanly.

---

# WORKSTREAM K — VIBE MODE (POST-V1; orchestrator ships with v1)

**Sequencing (locked):** the `VibeOrchestrator` backend ships as shared architecture with developer-mode v1 (the Coordinator reuses it); the standalone Vibe *product* ships later, end-state cloud ("Mainguard Web"). Build K-1 alongside Phase 7; defer K-2..K-5 UI until post-v1.

## K-1 — `VibeOrchestrator` Engine + Stream Interception (Vibe Phases 1.2, 3.1)

**Steps:** daemon service hooking agent-CLI and dev-server PTY streams **in memory** (subscribes to the same `TerminalStreamer` taps): (a) dev-server port harvesting — regex `http://localhost:([0-9]+)` → emit `[APP_READY_ON_PORT_X]` event; (b) OAuth URL detection → `[AUTH_REQUIRED]` with `state=<agent_uuid>` → client opens browser (J-4 loopback flow); (c) error interception — `ERR!`/stack-trace patterns → write fix prompt + trace into the agent CLI stdin (bytes never leave the VM); (d) **circuit breaker** — SHA-256 the normalized stack trace; 3 identical hashes OR 5 errors/10 min → hard-suspend (docker pause) + escalate event. Chat bridge RPC: UI text → orchestrator → agent stdin.
**Target files:** new `Mainguard.Agents/Agents/Orchestrator/VibeOrchestrator.cs`, `StreamPatternMatcher.cs`, `CircuitBreaker.cs`; proto additions.
**Verification:** unit — pattern matcher against recorded dev-server/CLI transcripts (port lines, npm ERR!, node/python tracebacks; ANSI codes stripped before matching); breaker math (identical-hash and rate triggers). Integration: scripted crashing dev server → fix prompt lands in agent stdin; 3 repeats → suspended + escalation event.

## K-2 — Autonomous Git Abstraction (Vibe Phase 2)

**Steps:** auto-checkpoints — on each successful generation loop, `StageAll` + commit "Auto-Checkpoint: <summary>" in the agent worktree (F3 signature rules; a dedicated Vibe author identity); autonomous conflict resolution — on `MergeConflictException`, feed conflict-marker content to the agent CLI with a resolve prompt, finalize on success, escalate to human on failure.
**Verification:** integration — N chat turns → N checkpoints, each tree-valid; induced conflict resolved by a scripted agent finalizes the merge; unresolvable → escalation, repo left in a clean conflicted state (no half-finalized merge).

## K-3 — Escalation UX (Vibe Phase 3.3 — the most important Vibe feature)

**Steps:** plain-language triage screen on circuit-breaker trip with exactly three actions: **"Try a different approach"** (re-prompt with failure context), **"Go back to when it worked"** (one-click hard restore to the last green auto-checkpoint — worktree-scoped, journaled via D-2.9 so even this is undoable), **"Get help"** (diagnostic bundle: recent transcript, breaker state, checkpoints list — redacted of secrets).
**Verification:** restore lands exactly on the last checkpoint that preceded a green verification; bundle contains no key material (automated grep of the artifact); all three buttons work from a real breaker trip.

## K-4 — Vibe UI: Mode Toggle, Chat, Live Preview (Vibe Phases 4.1–4.3)

**Steps:** in-app mode switch (never installer-forked) hiding dev dock panels → 2-pane Chat + `LivePreviewControl` (WebView2/CefGlue) that navigates on `[APP_READY_ON_PORT_X]` through the localhost bridge port-forward. Hot reload works because dev server + sources share ext4 (inotify functional) — the preview merely points at the forwarded port. Chat renders orchestrator status events; terminal visuals preserved end-to-end (raw PTY in 7.1a / grid updates in 7.1b).
**Verification:** manual — scaffold a Vite app via chat → preview appears on ready-event; edit via chat → hot reload in preview without refresh; toggle back to Developer Mode → full dock returns, same session intact.

## K-5 — One-Click Deployment (Vibe Phase 5)

**Steps:** Vercel/Netlify OAuth (J-4 loopback+PKCE); "Publish to Web" = final auto-checkpoint → push to GitHub → trigger cloud build → poll → present live URL; failures route into K-3's triage pattern.
**Verification:** end-to-end publish of a template app to a test Vercel account; token storage via keyring only; failure (bad build) surfaces triage, not raw logs.

---

# APPENDIX — SEQUENCED DELIVERY PLAN & PR RULES

## Milestone order (synthesizes the audit's sequencing + roadmap phases)

| Milestone | Contents | Exit criterion |
|---|---|---|
| **M1: Stop the bleeding** | F1–F4, A-1.1, A-1.3, A-1.4 | No known crash/data-loss path; conflict engine real; suite has mutating-op coverage |
| **M2: Correct & robust Git core** | B-2.3, A-1.5, A-1.7, A-1.9, A-1.10, A-1.8, A-1.12 | Conflicts resolvable end-to-end from merge/rebase/pull; no secrets in argv |
| **M3: Credible top-tier client (P0)** | B-2.13, B-2.4, B-4.5a, B-4.5b, B-2.2 | Feature parity on the P0 checklist vs GitKraken/Fork |
| **M4: Premium (P1)** | C-2.10, C-2.11, C-2.14, C-2.16, C-2.8, C-2.7, C-2.5, C-2.6, C-2.15 | — |
| **M5: Differentiators (P2) + polish** | D-2.9, D-2.12, D-2.17, E-5.1–5.4 | — |
| **M6: Agent foundation** | F6, G-7.0, G-7.1a, G-7.1c, G-7.2a–d | One hardened agent runs in a sandboxed worktree with gateway + egress control |
| **M7: The swarm** | G-7.3, G-7.4, G-7.5, G-7.1b, J-1–J-6 | Full coordinator workflow + installable OOBE; 7.1c green on libvterm |
| **M8: Enterprise + beyond** | H-8.1–8.5, I, K | Published security architecture; Vibe/cloud per locked sequencing |

## PR rules (apply to every task above)

1. One task = one PR (foundation tasks may not be bundled with feature tasks).
2. PR description links the task ID here, lists the manual verification steps performed **with output/screenshots**, and states which integration tests were added.
3. Any PR touching `GitServices.cs` runs the full integration suite; any PR touching terminal code runs 7.1c.
4. Security-relevant PRs (A-1.4, A-1.7, F6, G-7.2c, G-7.3 step 5, J-4) require the listed security checks executed and evidenced in the PR — reviewer re-runs at least one.
5. No PR may reintroduce: `cmd.exe` shells, secrets in argv, `BuildSignature` call sites, blocking UI-thread Git calls, Windows bind mounts into containers, or `wsl --shutdown`.
