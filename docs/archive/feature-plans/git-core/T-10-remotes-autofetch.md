# T-10 — Remotes Management, Auto-Fetch, Push Options — Implementation Plan

**Task ID:** T-10 · **Milestone:** M4 (audit 2.14) · **Priority:** P1 · **Depends on:** T-01.
**Branch:** `plan/T-10-remotes-autofetch` → implement on `feat/T-10-remotes-autofetch` off `main`.

> **Source of truth:** §T-10 of the Master Doc, §TI-10 of the Test Strategy.

---

## 0. Context

Today only `origin` is assumed — **6 hardcoded `"origin"` call sites** in `GitServices.cs` (Push `:379`,
Pull `:405/:407`, Fetch `:422/:424`, PushBranch `:1052`). This task adds multi-remote CRUD, replaces the
hardcoded remote with a resolver, adds `--force-with-lease`/tags/set-upstream push options (CLI-only —
libgit2 has no lease), and a background `AutoFetchService` that keeps ahead/behind fresh (closing the 1.12
stale-badge gap).

### What you can rely on

| Fact | Where |
|---|---|
| Push resolves upstream via `branch.TrackedBranch`/`RemoteName`; needs-upstream logic exists | `GitServices.cs:362-395` |
| `RunGitCheckedAuthenticated(repoPath, remoteName, params args)` — token-in-env CLI push/fetch | `GitServices.cs:769` |
| `RemoteNotFoundException` typed exception exists | `Core/Exceptions/` |
| `UserPreferences` is a plain settings class persisted by `SettingsService` | `Models/UserPreferences.cs` |
| `PeriodicTimer` is the sanctioned background timer (no `DispatcherTimer` in Core — G-5) | — |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Models/GitRemoteItem.cs` |
| **Edit** | `IGitService.cs` + `GitServices.cs` (remotes CRUD, Fetch overload, three push options, `ResolveRemoteName` helper) |
| **Create** | `Mainguard.Agents/Services/AutoFetchService.cs` |
| **Edit** | `Models/UserPreferences.cs` (`int AutoFetchMinutes = 10`) |
| **Edit** | remote UI (sidebar section, push split-button), ahead/behind "last fetched" label |
| **Create** | `Mainguard.Tests/GitServiceRemoteTests.cs` (integration, two bare remotes) |

---

## 2. Contract (must exist exactly)

```csharp
// Mainguard.Agents/Models/GitRemoteItem.cs
public sealed class GitRemoteItem { public string Name { get; init; } = ""; public string FetchUrl { get; init; } = ""; public string? PushUrl { get; init; } }

// IGitService additions
IReadOnlyList<GitRemoteItem> GetRemotes(string repoPath);
void AddRemote(string repoPath, string name, string url);
void RemoveRemote(string repoPath, string name);
void RenameRemote(string repoPath, string oldName, string newName);
void Fetch(string repoPath, string remoteName, bool prune = false);       // overload; existing Fetch(repoPath, prune) delegates to tracked-or-origin
void PushForceWithLease(string repoPath, string remoteName, string branchName);
void PushTags(string repoPath, string remoteName);
void PushSetUpstream(string repoPath, string remoteName, string branchName);

// Mainguard.Agents/Services/AutoFetchService.cs
public sealed class AutoFetchService : IDisposable
{
    public AutoFetchService(IGitService git, Func<UserPreferences> prefs);
    public void Watch(string repoPath);
    public void Unwatch(string repoPath);
    public event Action<string /*repoPath*/>? Fetched;
    public DateTimeOffset? GetLastFetched(string repoPath);
}
```

`UserPreferences` gains `int AutoFetchMinutes` (default 10; 0 = off).

---

## 3. Implementation

### 3.1 Remotes CRUD (LibGit2Sharp)

```csharp
public IReadOnlyList<GitRemoteItem> GetRemotes(string repoPath) =>
    ExecuteWithRepo(repoPath, repo => repo.Network.Remotes
        .Select(r => new GitRemoteItem { Name = r.Name, FetchUrl = r.Url, PushUrl = r.PushUrl == r.Url ? null : r.PushUrl })
        .ToList());
```

`AddRemote`/`RemoveRemote`/`RenameRemote` → `repo.Network.Remotes.Add/Remove/Rename`. Validate names
(no whitespace, no `..`, not empty) and throw typed on duplicate/missing **before** mutating.

### 3.2 Kill hardcoded `"origin"` — `ResolveRemoteName`

```csharp
private static string ResolveRemoteName(Repository repo, string? preferred = null)
{
    if (!string.IsNullOrEmpty(preferred) && repo.Network.Remotes[preferred] != null) return preferred!;
    var tracked = repo.Head.TrackedBranch?.RemoteName;
    if (!string.IsNullOrEmpty(tracked)) return tracked!;
    if (repo.Network.Remotes["origin"] != null) return "origin";
    var only = repo.Network.Remotes.SingleOrDefault();
    if (only != null) return only.Name;
    throw new RemoteNotFoundException("No remote configured for this repository.");
}
```

Sweep all 6 `"origin"` sites (Push/Pull/Fetch/PushBranch and any Delete-remote path) to resolve through this
helper. `grep -n '"origin"' Mainguard.Agents/` must end with **zero** hits outside `ResolveRemoteName`'s fallback.

### 3.3 Push options (CLI — no lease in libgit2)

```csharp
public void PushForceWithLease(string repoPath, string remoteName, string branchName) =>
    RunGitCheckedAuthenticated(repoPath, remoteName, "push", "--force-with-lease", remoteName, branchName);
public void PushTags(string repoPath, string remoteName) =>
    RunGitCheckedAuthenticated(repoPath, remoteName, "push", remoteName, "--tags");
public void PushSetUpstream(string repoPath, string remoteName, string branchName) =>
    RunGitCheckedAuthenticated(repoPath, remoteName, "push", "-u", remoteName, branchName);
```

**Never** plain `push --force` anywhere (rejection trigger) — `--force-with-lease` only.

### 3.4 `AutoFetchService`

- One background loop (`PeriodicTimer`) over the watched repo set. Per tick, per repo:
  - **skip** when `repo.Info.CurrentOperation != CurrentOperation.None` (mid merge/rebase) or
    `prefs().AutoFetchMinutes == 0`;
  - **skip if a fetch for that repo is already running** (per-repo guard — never overlap itself);
  - call `Fetch(repo, prune: true)` in try/catch — failures are **logged and counted**, never toasted;
  - on success record `DateTimeOffset.Now` and raise `Fetched(repoPath)`.
- Interval = `AutoFetchMinutes` (tests shrink it). No UI-thread work, no `DispatcherTimer` (G-5).
- UI: "last fetched N min ago" next to the ahead/behind badge, dimmed when > 15 min (closes 1.12).

### 3.5 UI

Remotes sidebar section (add/edit/remove dialogs), push split-button (normal / force-with-lease / push tags
/ set upstream), prune toggle on manual fetch.

---

## 4. Edge-case matrix / Invariants

| Case | Required behavior |
|---|---|
| two remotes, tracked branch on `upstream` | Push/Fetch target `upstream`, not `origin` |
| zero remotes | typed `RemoteNotFoundException`, friendly message |
| force-with-lease, remote moved (2nd clone pushed first) | **fails typed** — the safety property |
| force-with-lease after local amend, remote unmoved | succeeds |
| `-u` push | `branch.<name>.remote` + `.merge` config set |
| auto-fetch during merge/rebase | skipped, no interference |
| auto-fetch network failure ×3 | subtle warning state, no modal/toast spam |

**MUST:** the lease-failure test exists and passes; zero hardcoded `"origin"` outside `ResolveRemoteName`;
auto-fetch never runs concurrently with itself per repo.
**Rejection:** `push --force` anywhere; auto-fetch on the UI thread / `DispatcherTimer` in Core.

---

## 5. Test contract — `GitServiceRemoteTests.cs` (TI-10, two bare remotes from fixture)

1. `Remotes_CrudRoundTrip` — add/rename/remove reflected in `GetRemotes`; duplicate/missing → typed.
2. `Push_ShouldUseTrackedRemote_NotOrigin` — branch tracks `upstream` → push lands there, `origin` unchanged.
3. `Operations_ShouldThrowRemoteNotFound_WithZeroRemotes`.
4. `PushForceWithLease_ShouldSucceed_WhenRemoteUnmoved` (after local amend).
5. `PushForceWithLease_ShouldThrowTyped_WhenRemoteMoved` — second clone pushes first. **Never skip.**
6. `PushSetUpstream_ShouldWriteBranchConfig`.
7. `AutoFetchService_ShouldFetch_RaiseEvent_AndRecordTimestamp` (interval shrunk); `ShouldSkip_WhileOperationInProgress`; `ShouldNotOverlapItself` (slow fake fetch + two ticks → one execution).

---

## 6. Reviewer script / Definition of done

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~Remote|FullyQualifiedName~AutoFetch"
grep -n '"origin"' Mainguard.Agents/Services/GitServices.cs        # only inside ResolveRemoteName fallback
grep -rn "push --force\b\|\"--force\"" Mainguard.Agents/           # -> 0 hits (lease only)
```

- [ ] `GitRemoteItem` + CRUD + `Fetch(remoteName,prune)` overload + three push options.
- [ ] `ResolveRemoteName` replaces all hardcoded `"origin"`.
- [ ] `AutoFetchService` (PeriodicTimer, skip-in-op, no self-overlap, event + timestamp); `AutoFetchMinutes` pref.
- [ ] Remotes UI + push split-button + last-fetched label.
- [ ] TI-10 green incl. the lease-moved failure. One task = one PR linking **T-10**.
```
