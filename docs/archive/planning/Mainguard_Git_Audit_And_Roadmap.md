# Mainguard — Git Foundation Audit & Top-Tier Roadmap

> **ARCHIVED — superseded.** Part of the phase-1 planning corpus (July 2026), written before the
> agent platform existed. Kept for provenance only. The active corpus is
> [`docs/phase-2/`](../../phase-2/); the live backlog is
> [`Mainguard_Backlog.md`](../../planning/Mainguard_Backlog.md).

**Date:** 2026-07-02
**Scope:** Perfect the Git layer before advancing to the multi-agent stages. Two parts:

1. **Category 1 — Fix/Change Existing Git Functionality** (what, why, exactly how)
2. **Category 2 — Missing Features to reach GitKraken-tier** (what, why, exactly how)

All line references are against the current `main` working tree. Core engine lives in
`Mainguard.Agents/Services/GitService.cs` (1048 lines), the graph in
`Mainguard.Agents/Graph/CommitGraphRouter.cs`, and the conflict engine (stub) in
`Mainguard.Agents/Services/MergeDiffService.cs`.

---

# CATEGORY 1 — CHANGES TO EXISTING GIT FUNCTIONALITY

Ordered by severity: crashes / data-loss first, then correctness, then robustness/UX.

---

## 1.1 — CRITICAL: The 3-way merge engine is an empty stub

**Where:** `Mainguard.Agents/Services/MergeDiffService.cs:18-31`

**What:** `GenerateMergeChunks(baseText, leftText, rightText)` builds two DiffPlex models
and then **returns an empty list** — the actual chunking logic is a `// TODO`. The
338-line `ConflictResolverWindowViewModel` is a UI with no engine behind it, so conflict
resolution cannot actually work end-to-end.

**Why:** Merge/rebase/cherry-pick all funnel the user into "resolve conflicts in the Diff
Viewer." If the merge chunker returns nothing, the resolver shows no conflicts to resolve.
This is the single biggest hole in the Git foundation — everything conflict-related is
non-functional.

**Exactly how:** Implement real 3-way chunking. Walk base vs left and base vs right,
detect regions where left and/or right diverge from base, and classify each region as
`Unchanged`, `LeftOnly`, `RightOnly`, or `Conflict` (both sides changed the same base
region). Prefer driving off Git's own conflict markers where available (LibGit2Sharp
writes `<<<<<<<`, `=======`, `>>>>>>>` into `IndexEntry` stage 1/2/3), which is more
faithful than re-diffing text.

Two implementation options:

**Option A (recommended) — read conflict stages from the index.** LibGit2Sharp exposes
`repo.Index.Conflicts`, each with `Ancestor` (stage 1 / base), `Ours` (stage 2), `Theirs`
(stage 3) blob IDs. Read the three blob texts, feed them to the chunker:

```csharp
public List<MergeChunk> GenerateMergeChunks(string baseText, string leftText, string rightText)
{
    baseText ??= ""; leftText ??= ""; rightText ??= "";

    var baseLines  = baseText.Split('\n');
    var leftDiff   = _diffBuilder.BuildDiffModel(baseText, leftText);
    var rightDiff  = _diffBuilder.BuildDiffModel(baseText, rightText);

    // Index left/right change state per base line.
    // DiffPlex gives NewText.Lines aligned to base; map each base line to
    // (changed-on-left?, changed-on-right?) then coalesce contiguous runs.
    var chunks = new List<MergeChunk>();
    int i = 0;
    while (i < baseLines.Length)
    {
        bool lChanged = LineChanged(leftDiff, i);
        bool rChanged = LineChanged(rightDiff, i);

        if (!lChanged && !rChanged) { AppendContext(chunks, baseLines[i]); i++; continue; }

        // Grow the region while either side keeps diverging.
        int start = i;
        while (i < baseLines.Length && (LineChanged(leftDiff, i) || LineChanged(rightDiff, i))) i++;

        var region = new MergeChunk
        {
            BaseText  = Slice(baseLines, start, i),
            LeftText  = ExtractSide(leftDiff, start, i),
            RightText = ExtractSide(rightDiff, start, i),
            Kind = (lChanged && rChanged) ? ChunkKind.Conflict
                 : lChanged ? ChunkKind.LeftOnly : ChunkKind.RightOnly
        };
        chunks.Add(region);
    }
    return chunks;
}
```

Add a `ChunkKind` enum to `MergeChunk.cs` and a `Resolution` field (`Unresolved`,
`TakeLeft`, `TakeRight`, `TakeBoth`, `Custom`). Then add to `IGitService`:

```csharp
IReadOnlyList<ConflictedFile> GetConflicts(string repoPath);          // from repo.Index.Conflicts
(string Base, string Ours, string Theirs) GetConflictBlobs(string repoPath, string path);
void ResolveConflict(string repoPath, string path, string mergedContent); // write file + Commands.Stage
```

Wire `ConflictResolverWindowViewModel` to call `GetConflictBlobs` → `GenerateMergeChunks`,
let the user pick per chunk, reassemble the merged text, and `ResolveConflict` to stage it.
Then re-enable "Commit merge" / "Continue rebase."

**Test:** create a repo, make conflicting edits on two branches, merge, assert
`GetConflicts` returns the file and `GenerateMergeChunks` returns ≥1 `Conflict` chunk.

---

## 1.2 — CRITICAL: Null signature crashes Commit / Revert / CherryPick / Amend

**Where:** `GitService.cs:162` (Commit), `:1010` (Revert), `:1023` (CherryPick),
`:1045` (AmendCommitMessage). Also `Pull:289`, `PullWithCredentials:576`,
`UpdateProject:468`.

**What:** `repo.Config.BuildSignature(DateTimeOffset.Now)` returns **`null`** when the user
has no `user.name` / `user.email` configured. `Merge` and `StashPush` guard this with
`signature ??= new Signature(...)`, but `Commit`, `Revert`, `CherryPick`, `Amend`, and the
pull paths do **not** — they pass `null` straight into `repo.Commit(...)` →
`NullReferenceException` / `ArgumentNullException`.

**Why:** A fresh machine or a CI checkout with no global identity will crash on the very
first commit. Inconsistent handling across methods is a latent bug farm.

**Exactly how:** Add one private helper and route every call through it:

```csharp
private Signature GetSignature(Repository repo)
{
    var sig = repo.Config.BuildSignature(DateTimeOffset.Now);
    if (sig != null) return sig;

    // Fall back to any identity we can find, else a clearly-labelled placeholder,
    // but SURFACE this to the user so they set a real identity.
    var name  = repo.Config.Get<string>("user.name")?.Value;
    var email = repo.Config.Get<string>("user.email")?.Value;
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
        throw new GitIdentityMissingException(
            "No Git identity configured. Set user.name and user.email.");
    return new Signature(name, email, DateTimeOffset.Now);
}
```

Prefer **throwing a typed exception** over silently committing as "Mainguard
<mainguard@localhost>" (which pollutes history). Replace every
`repo.Config.BuildSignature(...)` and the `??= new Signature("Mainguard",...)` fallbacks with
`GetSignature(repo)`. Add a first-run check + a "Set identity" dialog in the UI when
`GitIdentityMissingException` is caught.

---

## 1.3 — CRITICAL: Network Git operations block the UI thread

**Where:** `Mainguard.App.Shell/ViewModels/RepoDashboardViewModel.cs:175-232` — `Push`, `Pull`,
`Fetch`, `UpdateProject` are **synchronous** `[RelayCommand]` methods that call the blocking
`_gitService.Push/Pull/Fetch/UpdateProject` directly. (Status refresh at `:118` *is*
correctly wrapped in `Task.Run`; the network commands are not.)

**What:** A push/pull/fetch on a slow network freezes the entire Avalonia UI until it
returns — no spinner, no cancel, "app not responding."

**Why:** This directly contradicts the "blazing-fast 60 FPS" positioning. Network latency
is unbounded; it must never run on the UI thread.

**Exactly how:** Make the commands async and offload to a worker thread, with a busy flag
and cancellation. Example for `Push` (apply the same shape to Pull/Fetch/UpdateProject):

```csharp
[ObservableProperty] private bool _isBusy;

[RelayCommand(CanExecute = nameof(CanRunGitAction))]
private async Task PushAsync(CancellationToken ct)
{
    IsBusy = true;
    try
    {
        await Task.Run(() => _gitService.Push(_repoPath), ct);
        ShowNotification("Push completed successfully.", false);
    }
    catch (OperationCanceledException) { ShowNotification("Push cancelled.", false); }
    catch (Exception ex) { HandleGitActionException(ex, "Push"); }
    finally { IsBusy = false; await RefreshStatusAsync(); }
}

private bool CanRunGitAction() => !IsBusy;
```

Add `CancellationToken` parameters through `IGitService` for the long ops (see 1.11), bind
`IsBusy` to a progress overlay, and disable the toolbar buttons via `CanExecute`.

---

## 1.4 — HIGH: `DiscardChanges` can recursively delete untracked directories

**Where:** `GitService.cs:104-133`, specifically the delete branch at `:118-121`.

**What:** For files flagged `NewInWorkdir`/`NewInIndex` it deletes the path; if the path is
a directory it does `Directory.Delete(fullPath, true)` — **recursive, unconditional**. It
also classifies purely on `NewInWorkdir|NewInIndex` flags, which mishandles files that are
staged-new-then-modified, and renames.

**Why:** "Discard changes" that silently nukes a directory tree is a data-loss trap. A
mis-selected row can wipe an untracked folder full of work with no undo.

**Exactly how:**
- Never recursively delete a directory implicitly. Discard operates on *file* paths from
  the status list; if a status entry resolves to a directory, skip it and log.
- Route deletions through the OS recycle bin / trash instead of `File.Delete`
  (`Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., RecycleOption.SendToRecycleBin)`
  on Windows; platform shim elsewhere) so discards are recoverable.
- Require an explicit confirmation dialog listing exactly which untracked files will be
  **permanently** removed vs which tracked files will be reverted (two distinct lists).
- For tracked files keep `CheckoutPaths(... Force)`; that path is fine.

---

## 1.5 — HIGH: `Pull` merges silently and never surfaces conflicts

**Where:** `GitService.cs:283-316` (Pull), and `UpdateProject:459-493` uses the same
`Commands.Pull` default.

**What:** `Merge` and `Rebase` detect `MergeStatus.Conflicts` / `RebaseStatus != Complete`
and throw a helpful message. `Pull` does neither — `Commands.Pull` performs a default merge
and, on conflicts, just leaves the working tree in a conflicted state with no signal to the
user. There is also no fast-forward-only or pull-rebase option.

**Why:** A pull that hits conflicts should route the user into the (soon-to-be-working)
conflict resolver, exactly like Merge does. Silent divergence is confusing and dangerous.

**Exactly how:** Inspect the `MergeResult` returned by `Commands.Pull` and mirror the Merge
handling, and expose the pull strategy:

```csharp
public void Pull(string repoPath, PullStrategy strategy = PullStrategy.Default)
{
    ExecuteWithRepo(repoPath, repo =>
    {
        var options = new PullOptions {
            FetchOptions = new FetchOptions { CredentialsProvider = GetCredentialsProvider() },
            MergeOptions = new MergeOptions {
                FastForwardStrategy = strategy == PullStrategy.FastForwardOnly
                    ? FastForwardStrategy.FastForwardOnly : FastForwardStrategy.Default
            }
        };
        var result = Commands.Pull(repo, GetSignature(repo), options);
        if (result.Status == MergeStatus.Conflicts)
            throw new MergeConflictException("Pull produced conflicts. Resolve them, then commit.");
    });
    // (keep the CLI/token fallback in the catch, but also detect conflicts there)
}
```

Add a `PullStrategy` enum (`Default`, `FastForwardOnly`, `Rebase`) and a UI toggle. For
`Rebase`, fetch then call the existing `Rebase(...)` against the upstream tip.

---

## 1.6 — HIGH: `ExecuteGitCli` is Windows-only and pops a terminal window

**Where:** `GitService.cs:496-524`.

**What:** The CLI fallback hardcodes `FileName = "cmd.exe"`,
`Arguments = "/c git {args} || pause"`, `UseShellExecute = true`, `CreateNoWindow = false`.
This spawns a visible console window and relies on `cmd.exe` + `pause`. Error output is
explicitly *not* captured ("we can't read the error text programmatically anymore").

**Why:** (a) Breaks on macOS/Linux — the roadmap promises `.dmg`/`.AppImage`. (b) Popping a
raw terminal is not "premium GUI" UX. (c) Losing stderr means the app can't show a proper
error. There is already a `ExecuteSilentGitCli` (`:527`) that captures stderr — the noisy
variant is redundant.

**Exactly how:** Delete `ExecuteGitCli` and route all fallbacks through a single hardened,
cross-platform silent runner that captures stdout/stderr and throws a typed exception:

```csharp
private (int Code, string Out, string Err) RunGit(string repoPath, string args, CancellationToken ct = default)
{
    var psi = new ProcessStartInfo {
        FileName = _gitExecutablePath ?? "git",   // resolve once at startup; configurable
        WorkingDirectory = repoPath,
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    // Pass args via ArgumentList to avoid quoting/injection bugs (see 1.7):
    foreach (var a in SplitArgs(args)) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    return (p.ExitCode, stdout.Result, stderr.Result);
}
```

Show captured stderr in an in-app error panel. Resolve the git executable path once
(GitKraken calls this the "Git Executable" preference) and let the user override it.

---

## 1.7 — HIGH: GitHub-only auth, and the token is embedded in CLI URLs

**Where:** `GetCredentialsProvider:169-182`, `GetGitHubToken:184`,
`ConvertToTokenUrl:195-211`, and every fallback that passes `tokenUrl` into
`ExecuteSilentGitCli` (`:265, :309, :345, :779`).

**What:**
- Credentials are a single `github_token` from the keyring, passed as the *username* with
  an empty password. `ConvertToTokenUrl` only rewrites `github.com` (and `git@github.com:`).
  No GitLab, Bitbucket, Azure DevOps, or self-hosted support; no SSH passphrase handling.
- `ConvertToTokenUrl` builds `https://x-access-token:{token}@github.com/...` and passes it
  **as a command-line argument**. Tokens in argv are visible in process listings and can
  land in shell history / logs. This is a credential-leak vector.

**Why:** A "top-of-the-line" client must auth against multiple hosts, and must never leak
secrets via argv.

**Exactly how:**
- **Never put the token in the URL/argv.** Use `git`'s credential mechanism: set
  `GIT_ASKPASS` / `GIT_TERMINAL_PROMPT=0` and feed credentials via `git credential approve`
  on stdin, or use `-c credential.helper=` with an in-memory helper. LibGit2Sharp's
  `CredentialsProvider` (already used) is the primary path; keep the CLI fallback but pass
  secrets over stdin, not argv.
- Generalize the keyring: key secrets by host (`token:github.com`, `token:gitlab.com`, …)
  and pick based on the remote's host. Add a `HostKind` detector from the remote URL.
- Add SSH support: allow specifying a private key path + passphrase in
  `CredentialsProvider` via `SshUserKeyCredentials`.
- See Category 2.8 for the full multi-host auth + SSH key manager.

---

## 1.8 — MEDIUM: `GetRecentCommits` — in-memory multi-path filter + pagination/topo desync

**Where:** `GitService.cs:603-674`.

**What:** Two issues:
1. Multi-path filter (`:616-621`) does
   `FilePaths.SelectMany(QueryBy).Distinct().OrderByDescending(When)` — this materializes
   the full history of every path in memory before paging.
2. `SortBy = Topological | Time` combined with `.Skip(skip).Take(take)` (`:663`) is
   evaluated by re-walking from the tips each call. For deep histories this is O(N) per
   page, and because the walk is recomputed per chunk the **graph fringe handed to
   `CommitGraphRouter` can desync** at chunk boundaries if the sort isn't perfectly stable.

**Why:** Performance and graph correctness on large repos — exactly where Electron clients
fall over and where Mainguard should win.

**Exactly how:**
- For text/author/date filters, push them into the walk instead of LINQ-over-materialized:
  keep the `CommitFilter` walk lazy and `Where(...).Skip().Take()` on the *lazy*
  `IEnumerable<Commit>` (LibGit2Sharp streams the walk), which it already mostly does —
  but avoid `ToLowerInvariant()` per commit by pre-lowering the needle once (done) and
  using `IndexOf(..., StringComparison.OrdinalIgnoreCase)` to skip allocations.
- For pagination stability, page by **commit walk cursor**, not `Skip(n)`: remember the
  last SHA of the previous page and resume the walk `IncludeReachableFrom` that tip, or
  cache the ordered SHA list once per refresh and slice it. This guarantees the fringe
  passed to the router is continuous.
- For multi-path, use a single `CommitFilter` walk and test `TreeChanges` membership per
  commit rather than N separate `QueryBy` walks.

---

## 1.9 — MEDIUM: Unchecked `.Tip` dereferences on empty/unborn branches

**Where:** `GetBranchDiffAgainstWorkingTree:918` (`branch.Tip.Tree`),
`AmendCommitMessage:1040` (`repo.Head.Tip.Sha`), `PushBranch` / `UpdateProject` tip access,
`GetBranches:689` correctly uses `branch.Tip?.Sha` — so the guard is inconsistent.

**What:** A freshly-created branch with no commits, or an unborn HEAD (empty repo), has a
`null` Tip. These call sites will `NullReferenceException`.

**Why:** Empty repos and just-created branches are common (init, first commit flow).

**Exactly how:** Guard every `.Tip` access:
`if (branch?.Tip == null) throw new GitOperationException("Branch has no commits yet.");`
and in `AmendCommitMessage` check `repo.Head.Tip == null` (nothing to amend). Audit all
`.Tip.` occurrences and apply the null-safe pattern already used in `GetBranches`.

---

## 1.10 — MEDIUM: `RepositoryWatcher` watches the entire tree, no ignore filtering

**Where:** `Mainguard.Agents/Services/RepositoryWatcher.cs:53-101`.

**What:** `IncludeSubdirectories = true` on the repo root with `NotifyFilter` covering
LastWrite|FileName|DirectoryName|Size|CreationTime. Any working-tree write (including
`node_modules`, `bin/`, `obj/`, build output) triggers the debounce and a full refresh.
The README claims it targets `.git/refs`+`.git/index`, but the code watches everything and
only filters *inside* the handler. It also refreshes on `.git/index.lock` churn.

**Why:** On large/monorepo trees this fires constantly and burns CPU re-reading status. The
multi-agent vision (many agents writing) will hammer this. `index.lock` churn causes
refreshes mid-operation.

**Exactly how:**
- Ignore `.git/*.lock` and `.git/index.lock` explicitly in the handler.
- Respect `.gitignore`: cheaply skip events whose path is ignored
  (`repo.Ignore.IsPathIgnored(rel)`), cached, or maintain a prefix denylist
  (`node_modules/`, `bin/`, `obj/`, `.vs/`).
- Consider two watchers: one scoped to `.git` (HEAD/index/refs) and a separate, more
  heavily debounced one for the working tree, so metadata changes refresh instantly while
  bulk file writes coalesce.
- Coalesce: keep the debounce but add a max-refresh-rate cap (e.g. no more than 1 refresh /
  250 ms even under continuous writes).

---

## 1.11 — MEDIUM: No cancellation, no typed exceptions, generic `throw new Exception`

**Where:** Throughout `GitService.cs` — every error is `throw new System.Exception(...)`;
ViewModels `catch (System.Exception)`.

**What:** Callers cannot distinguish *conflict* vs *auth failure* vs *not-a-repo* vs
*network* without string-matching messages (`UpdateProject:486` already does
`ex.Message.Contains("conflict")` — fragile). No `CancellationToken` on long ops.

**Why:** The UI needs to react differently (open conflict resolver, prompt for credentials,
offer retry). String-sniffing exceptions is brittle and localization-hostile.

**Exactly how:** Introduce a small exception hierarchy in `Mainguard.Agents/Exceptions/`
(there's already `SshAuthenticationException`):

```
MainguardException (base)
├─ MergeConflictException
├─ GitIdentityMissingException
├─ AuthenticationRequiredException
├─ RemoteNotFoundException
└─ GitOperationException
```

Throw the specific type at each site; have ViewModels `catch` the specific types and route
accordingly (open `ConflictResolverWindow` on `MergeConflictException`, etc.). Add
`CancellationToken ct = default` to `Push/Pull/Fetch/Clone/GetRecentCommits` and honor it in
`RunGit` (kill the process on cancel) and in walk loops.

---

## 1.12 — LOW: Ahead/Behind can be stale (no fetch), `CheckoutBranch` re-lookup

**Where:** `GetAheadBehind:592-601`; `CheckoutBranch:696-723`.

**What:** `TrackingDetails.AheadBy/BehindBy` reflect the last fetch — with no auto-fetch,
the dashboard can show stale counts indefinitely. `CheckoutBranch` re-indexes
`repo.Branches[branchName]` at `:717` after already having the object.

**Why:** Users trust the ahead/behind badge; stale counts erode trust. Minor perf/clarity
on the checkout path.

**Exactly how:** Add a background auto-fetch (see 2.14) and/or a "last fetched N min ago"
label. In `CheckoutBranch`, capture the remote branch reference once and reuse it; and guard
dirty-working-tree checkouts through the existing `CheckoutConflictDialog` before calling
`Commands.Checkout` (verify that path is wired).

---

## 1.13 — LOW: Only whole-file staging (no hunk/line staging)

**Where:** `StageFile:78-84` / `StageFiles:94-97` use `Commands.Stage(repo, path)`.

**What:** Staging is all-or-nothing per file. There is no way to stage individual hunks or
lines. (Listed here because it's a *change* to the staging model, but the full feature is in
Category 2.13.)

**Why:** Partial staging is table-stakes for a serious Git client; crafting atomic commits
is a core workflow.

**Exactly how:** See 2.13 — apply a filtered patch to the index via
`git apply --cached` with a hunk-subset patch, or LibGit2Sharp `Index` blob manipulation.

---

# CATEGORY 2 — MISSING FEATURES FOR A TOP-TIER GIT GUI

Benchmarked against **GitKraken Desktop**, **Fork**, **Tower**, and **Sublime Merge**.
Each item: what it is, why it matters, and exactly how to build it in Mainguard's stack
(Avalonia + CommunityToolkit.Mvvm + LibGit2Sharp, with `git` CLI fallback).

Priority tiers: **P0** = required to be credible, **P1** = expected of a premium client,
**P2** = differentiators.

---

## 2.1 — Interactive Rebase (P0)

**What:** A rebase editor: reorder commits by drag, and per-commit actions
**pick / reword / squash / fixup / edit / drop** (GitKraken's P/R/S/D shortcuts). Undo/redo
of the whole operation.

**Why:** The #1 "power" feature every top client has (GitKraken, Fork, Tower, Sublime). Core
to cleaning history before pushing — and directly relevant to curating agent-generated
commits later.

**How:**
- LibGit2Sharp cannot script an interactive rebase todo list. Drive it through the CLI with
  a scripted sequence editor: set `GIT_SEQUENCE_EDITOR` to a tiny helper that writes the
  todo file Mainguard generated, and `GIT_EDITOR` to a helper that supplies reword messages,
  then run `git rebase -i <base>` via the hardened `RunGit`.
- Model: `RebaseTodoItem { Sha, Action (Pick/Reword/Squash/Fixup/Edit/Drop), Message }`,
  an `ObservableCollection` bound to a drag-reorderable `ItemsControl`.
- On conflict mid-rebase, reuse `IsRebasing` / `ContinueRebase` / `AbortRebase` (already
  present, `GitService.cs:421-453`) plus the now-working conflict resolver (1.1).
- Add `Undo` by snapshotting `ORIG_HEAD`/reflog before starting (see 2.9).

---

## 2.2 — Rich Commit-Graph Interactions (P0)

**What:** Right-click context menu on commits/branches directly in the graph (checkout,
merge, rebase onto, reset, revert, cherry-pick, create branch/tag, copy SHA), **drag-and-drop
merge/rebase** (drag one branch onto another), branch **pinning** to the left, and graph
**filtering** to the current branch + upstream.

**Why:** GitKraken's signature UX. Mainguard already has a fast `CommitGraphCanvas` and router
— the actions largely exist in `GitService`; this is wiring them into the canvas.

**How:**
- Add pointer hit-testing in `CommitGraphCanvas` to map a click to a `GraphNode`/SHA
  (the router already assigns `RowIndex`/`LaneIndex`). Surface an Avalonia `ContextMenu`
  whose `MenuItem`s bind to existing commands (`CheckoutRevision`, `ResetToCommit`,
  `RevertCommit`, `CherryPick`, `CreateBranch`, plus new tag commands from 2.4).
- Drag-drop: implement `DragDrop` handlers on branch labels; on drop, infer intent
  (drop branch A onto B → offer "Merge A into B" / "Rebase A onto B").
- Branch pinning + filtering: add a `HashSet<string> PinnedRefs` and a filter mode to the
  graph query (`CommitFilter.IncludeReachableFrom = {HEAD, upstream}`); render pinned lanes
  left-most (the router already enforces left-most dominance).

---

## 2.3 — Full Conflict-Resolution Editor (P0)

**What:** A true 4-pane merge tool: **Base | Ours | Theirs | Merged Output**, with
per-hunk "take ours / take theirs / take both / edit," and one-click resolve for whole
files. (GitKraken's and Fork's most-praised feature.)

**Why:** This is the "single biggest time saver" per the comparison research, and Mainguard's
engine for it is currently a stub (1.1).

**How:** After implementing the chunker (1.1), build the UI on the existing
`MergeEditorControl` / `ConflictResolverWindow`: bind the four text panes, render
`MergeChunk`s with accept buttons, reassemble on save, `ResolveConflict` to stage. Add
file-level "Resolve using Ours/Theirs" (`git checkout --ours/--theirs -- <path>` then stage).
Show a conflict list with resolved/unresolved counts.

---

## 2.4 — Tag Management (P0)

**What:** Create (lightweight + annotated), delete, push, and checkout tags; show tags in
the graph and a tags list.

**Why:** Completely absent today (`grep` for tags in Core returns nothing). Tags are
fundamental — releases, versioning. No serious client omits them.

**How:** Add to `IGitService`/`GitService`:

```csharp
IEnumerable<GitTagItem> GetTags(string repoPath);
void CreateTag(string repoPath, string name, string targetSha, string? message); // annotated if message
void DeleteTag(string repoPath, string name);
void PushTag(string repoPath, string name);   // push <remote> refs/tags/<name>
void CheckoutTag(string repoPath, string name);
```

LibGit2Sharp: `repo.Tags.Add(name, target, signature, message)` for annotated,
`repo.Tags.Add(name, target)` for lightweight, `repo.Tags.Remove(name)`. Push via
`repo.Network.Push(remote, "refs/tags/"+name, options)`. Render tag refs in the graph
(the router has SHAs; join against `repo.Tags` by target). Add a "New Tag" context action
(ties into 2.2).

---

## 2.5 — Submodule Support (P1)

**What:** Show submodule status, init/update/sync, and open a submodule as its own repo.

**Why:** Absent. Common in larger/enterprise repos; a "complete" client handles them.

**How:** LibGit2Sharp exposes `repo.Submodules` (status, `Update(...)`). For init/sync,
fall back to `RunGit`: `submodule update --init --recursive`, `submodule sync`. Add a
Submodules panel listing each with status (uninitialized/up-to-date/modified) and actions.

---

## 2.6 — Git LFS Support (P1)

**What:** Detect LFS repos, show LFS-tracked patterns, pull/prune LFS objects, and track new
patterns.

**Why:** Expected for teams with binary assets. GitKraken has per-repo LFS config.

**How:** LFS is a CLI concern — shell out via `RunGit`: `lfs install`, `lfs track <pat>`,
`lfs ls-files`, `lfs pull`, `lfs prune`. Detect `.gitattributes` `filter=lfs` entries. Add a
per-repo LFS toggle in settings. Ensure credentials flow (1.7) works for LFS endpoints.

---

## 2.7 — Commit & Tag Signing (GPG / SSH) (P1)

**What:** Sign commits and tags with GPG or SSH keys; show verified badges.

**Why:** Required for orgs enforcing signed commits; also feeds the future SOC2 "audit
trail" positioning in the README.

**How:** LibGit2Sharp supports creating commits with a signature callback
(`repo.ObjectDatabase.CreateCommitWithSignature`) — or drive `git -c commit.gpgsign=true`
via `RunGit` with the configured `user.signingkey` and `gpg.format` (`openpgp`/`ssh`).
Add Preferences: signing key dropdown, GPG/SSH format, "sign by default." Show
verification state from `git log --show-signature` / `%G?`.

---

## 2.8 — Multi-Host Auth + SSH Key Manager (P1)

**What:** Authenticate against GitHub, GitLab, Bitbucket, Azure DevOps, and self-hosted;
built-in SSH key generation/registration; OAuth device flow per host.

**Why:** Today it's GitHub-token-only (1.7). Tower/SmartGit's built-in SSH management is a
noted differentiator; Mainguard already has `GitHubAuthClient` + `SecureKeyring` to build on.

**How:**
- Key secrets by host in `SecureKeyring` (`token:<host>`), detect host from remote URL,
  select provider accordingly. Generalize `ConvertToTokenUrl`/`GetCredentialsProvider` to a
  `IHostProvider` abstraction (`GitHubProvider`, `GitLabProvider`, …).
- SSH: generate keypairs (`ssh-keygen` via `RunGit`-style shell), store the private key path,
  register the public key via each host's API, and pass `SshUserKeyCredentials` to
  LibGit2Sharp. Handle passphrases (resolves the existing `SshAuthenticationException`).
- Device-flow OAuth per host (extend `GitHubAuthClient`).

---

## 2.9 — Unlimited Undo / Redo of Git Operations (P2, differentiator)

**What:** Undo *any* Git action — commit, merge, rebase, reset, branch delete, cherry-pick
— and redo it. (Tower's headline feature; GitKraken has op-specific undo/redo.)

**Why:** Massive confidence booster and a strong marketing bullet. Also a safety net for the
future agent swarm rewriting history.

**How:** Maintain an operation journal. Before each mutating op, record `HEAD` (and relevant
ref) SHAs; Git's **reflog** already persists these. Implement:
- `Undo`: reset the affected ref(s) back to the pre-op SHA from the journal/reflog
  (`repo.Refs.UpdateTarget`), restoring working tree as needed.
- `Redo`: re-apply from the forward journal entry.
- Branch-delete undo: recreate the branch at its recorded tip.
- Surface a visible operation history list (like Tower) with per-entry undo.

---

## 2.10 — Blame / Inline File Annotations (P1)

**What:** Line-by-line blame (author, commit, date) in the diff/file viewer, with click-through
to the commit. (GitLens' core feature.)

**Why:** Essential for understanding code provenance; heavily used daily.

**How:** LibGit2Sharp `repo.Blame(path, new BlameOptions { StartingAt = commit })` returns
hunks mapping line ranges → commits. Render a gutter column in the file viewer with
author/short-SHA, tooltip with full commit, click → open commit. Cache per file+SHA.

---

## 2.11 — File History & Line History (P1)

**What:** "History of this file" and "history of these lines" — the sequence of commits that
touched a file (or line range), with side-by-side diffs across versions.

**Why:** Standard in all four benchmark clients. Mainguard already has `QueryBy(path)`
(used in `GetRecentCommits`) — this exposes it as a dedicated view.

**How:** Add `GetFileHistory(repoPath, path)` → `repo.Commits.QueryBy(path)` mapping each
`LogEntry` to a commit + its version of the file. UI: a timeline of the file with a diff
between adjacent versions. Line history: use blame + follow renames (`--follow`).

---

## 2.12 — Reflog Viewer & Recovery (P2)

**What:** Browse `HEAD`/branch reflog, and restore to any prior state ("undo a bad reset,"
recover a deleted branch).

**Why:** Power-user safety net; pairs with 2.9 (undo). Rarely built well — a differentiator.

**How:** `repo.Refs.Log(reference)` yields `ReflogEntry` (from/to SHA, message, committer).
UI: a list per ref; "Restore" = `repo.Reset(Hard, entry.To)` or recreate a branch at that
SHA. Read-only first, then add restore actions with confirmation.

---

## 2.13 — Interactive / Partial Staging (hunk & line) (P0)

**What:** Stage/unstage individual hunks and individual lines from the diff, plus
"stage selection." (Sublime Merge & Fork excel here.)

**Why:** Crafting atomic commits is a core daily workflow; whole-file staging (current
limit, 1.13) is not enough for a serious client.

**How:**
- The diff viewer already renders patch hunks (`GetFileDiff` → `Patch.Content`). Add hunk
  and line selection in the UI.
- To stage a subset: construct a valid unified-diff patch containing only the selected
  hunks/lines and apply it to the index via `RunGit` `apply --cached` (and `apply --cached -R`
  for unstaging). This is how most clients do partial staging reliably.
- Alternatively manipulate the `Index` blob directly with LibGit2Sharp, but patch-apply is
  simpler and battle-tested.
- Add "discard hunk" / "discard lines" using the same patch mechanism (reverse-apply to the
  working tree) — routed through the safe-discard confirmation from 1.4.

---

## 2.14 — Remotes Management + Auto-Fetch + Push Options (P1)

**What:** Add/remove/rename remotes, support multiple remotes, prune; background auto-fetch
so ahead/behind is always fresh; push options: **force-with-lease**, push tags, push all,
set-upstream, pull-rebase.

**Why:** Today only `origin` is assumed (`Fetch`, `PushBranch`, `GetRemoteUrl` all hardcode
`"origin"`). Auto-fetch fixes the stale ahead/behind (1.12). Force-with-lease is a safety
must for rebase workflows.

**How:**
- `IGitService`: `GetRemotes`, `AddRemote`, `RemoveRemote`, `RenameRemote`
  (LibGit2Sharp `repo.Network.Remotes.Add/Remove/Rename`). Replace hardcoded `"origin"` with
  the tracked branch's remote (`branch.RemoteName`) or a user selection.
- Auto-fetch: a background timer (respecting a Preferences interval) calling `Fetch(prune)`
  off the UI thread, then refresh ahead/behind. Show "last fetched" time.
- Push options: `Push(force-with-lease)` → `RunGit push --force-with-lease` (LibGit2Sharp's
  push doesn't do lease); `--tags`, `-u`. Expose as a push dropdown.

---

## 2.15 — Integrated Terminal, Command Palette & Keyboard Shortcuts (P1/P2)

**What:** (a) A real embedded terminal (already on the roadmap via ConPTY/Pty.Net).
(b) A command palette (Ctrl/Cmd-P) for fuzzy actions & repo/branch switching.
(c) Comprehensive keyboard shortcuts. Sublime Merge is the bar here.

**Why:** Keyboard-centric power users (a large segment) choose clients on this alone. The
command palette also becomes the natural entry point for future agent commands.

**How:**
- Terminal: `Pty.Net` (ConPTY on Windows, forkfpty on *nix) rendered in an Avalonia control
  (roadmap item #4). Scope for the Git-perfection phase: at minimum a "Open in terminal"
  action.
- Command palette: an Avalonia overlay + fuzzy matcher over a registry of `[RelayCommand]`s,
  branches, and repos.
- Shortcuts: define an Avalonia `KeyBindings` map; make it user-editable in Preferences.

---

## 2.16 — Diff Quality: intra-line, syntax highlighting, images, whitespace (P1)

**What:** Word/character-level intra-line highlighting, syntax highlighting in diffs,
image diff (side-by-side/swipe), and whitespace-ignore toggles.

**Why:** "Diff rendering among the most readable" is why people pick Sublime Merge/Fork.
Mainguard's diff is currently plain-text patch content (`GetFileDiff`).

**How:**
- Intra-line: run a token/word diff (DiffPlex, already referenced) within changed lines and
  highlight the differing spans.
- Syntax highlighting: integrate AvaloniaEdit/TextMate grammars in the diff view.
- Whitespace: pass `ignoreWhitespace` into LibGit2Sharp `CompareOptions` /
  `git diff -w`.
- Image diff: detect binary/image blobs, render both revisions in an image compare control.

---

## 2.17 — Repository Management: profiles, worktree UI, clone-with-progress (P2)

**What:** (a) Profiles = switchable Git identities / preference sets (GitKraken). (b) A
worktree management UI (backend exists: `ListWorktrees`/`AddWorktree`/`RemoveWorktree`,
`GitService.cs:876-897`). (c) Clone with live progress (there's a `CloneDashboard`).

**Why:** Profiles matter for contributors with work/personal identities; worktrees are the
backbone of the future agent-isolation model (each agent gets a worktree), so a first-class
worktree UI now pays off later.

**How:**
- Profiles: store identity + prefs sets in the existing SQLite store; apply on repo open by
  writing `user.name`/`user.email` to local config.
- Worktree UI: a panel listing worktrees with create (pick branch + path) / open / remove,
  wired to the existing service methods; add progress + validation.
- Clone: report `TransferProgress`/`CheckoutProgress` callbacks from LibGit2Sharp into the
  CloneDashboard progress bar.

---

## Suggested Sequencing

1. **Unblock conflicts & stop crashes/data-loss:** 1.1 (merge engine), 1.2 (signature),
   1.4 (safe discard), 1.3 (async network). Then 2.3 (conflict UI) falls out of 1.1.
2. **Correctness/robustness:** 1.5, 1.6, 1.7, 1.9, 1.10, 1.11.
3. **P0 features:** 2.13 (partial staging), 2.4 (tags), 2.1 (interactive rebase),
   2.2 (graph interactions).
4. **P1 features:** 2.10 blame, 2.11 file history, 2.14 remotes/auto-fetch,
   2.16 diff quality, 2.8 auth, 2.7 signing, 2.5/2.6 submodules/LFS.
5. **P2 differentiators:** 2.9 unlimited undo, 2.12 reflog, 2.15 terminal/palette,
   2.17 profiles/worktrees.
6. **Testing throughout:** the current suite covers `IsGitRepository`, `ExecuteWithRepo`,
   the watcher, and the graph router well — but has **zero coverage of commit, merge,
   rebase, stash, branch, discard, or conflict flows.** Add integration tests (init a temp
   repo, perform the op, assert state) for every mutating method as it's touched.

---

## Sources (GitKraken / competitor research)

- [Interactive Rebase with GitKraken Desktop](https://help.gitkraken.com/gitkraken-desktop/interactive-rebase/)
- [Branch, Merge, and Rebase in GitKraken Desktop](https://help.gitkraken.com/gitkraken-desktop/branching-and-merging/)
- [GitKraken Desktop Interface](https://help.gitkraken.com/gitkraken-desktop/interface/)
- [Visual Git Commit Graph — GitKraken](https://www.gitkraken.com/features/commit-graph)
- [Commit Signing with GPG in GitKraken Desktop](https://help.gitkraken.com/gitkraken-desktop/commit-signing-with-gpg/)
- [Authentication with Other Git Hosts in GitKraken Desktop](https://help.gitkraken.com/gitkraken-desktop/authentication/)
- [GitKraken Desktop Profiles](https://help.gitkraken.com/gitkraken-desktop/profiles/)
- [Core Features | GitLens](https://help.gitkraken.com/gitlens/gitlens-features/)
- [Best Git GUI Clients in 2025: GitKraken, SourceTree, Fork, and More Compared (DEV)](https://dev.to/_d7eb1c1703182e3ce1782/best-git-gui-clients-in-2025-gitkraken-sourcetree-fork-and-more-compared-4gjd)
- [Best Git Client for Mac and Windows in 2026 (Tower Blog)](https://www.git-tower.com/blog/best-git-client)
- [Best Git GUI Clients 2026 (The Software Scout)](https://thesoftwarescout.com/best-git-clients-2026-top-gui-tools-for-version-control/)
</content>
</invoke>
