# T-05 — Tag Management — Implementation Plan

**Task ID:** T-05 · **Milestone:** M3 (audit 2.4) · **Priority:** P0
**Depends on:** T-01 (fixture, already on `main`).
**Branch:** `plan/T-05-tag-management` → implement on `feat/T-05-tag-management` off `main`.

> **Source of truth:** §T-05 of the Master Doc, §TI-05 of the Test Strategy. Contract, invariants, and
> edge-case matrix below are binding.

---

## 0. Context — what exists today

Tags are **entirely absent** from Core (`grep -rn "repo.Tags" Mainguard.Agents` → 0 hits). This task adds the
full tag lifecycle to `IGitService`/`GitService`, plus a Tags section in the branch browser, a create-tag
dialog, and tag chips in the commit graph. There is a pre-existing `TagNames` boolean preference in
`CommitTimelineViewModel` (`:191`, `:322`) — it is a display toggle and is unrelated; wire the new tag chips
to respect it if convenient, but do not assume it already renders tags.

### What you can rely on

| Fact | Where |
|---|---|
| `GetSignature(repo)` — the only sanctioned signature source (G-3); throws `GitIdentityMissingException` when unset | `GitServices.cs` (used by `Commit`, `:325`) |
| Remote-push fallback pattern: try LibGit2Sharp `repo.Network.Push`, on `LibGit2SharpException` fall back to `RunGitCheckedAuthenticated(repoPath, remoteName, "push", ...)` | `GitServices.cs:827`, `:1049`, `:1067` |
| `RunGitCheckedAuthenticated(string repoPath, string remoteName, params string[] args)` — hardened, token-in-env | `GitServices.cs:769` |
| Typed exceptions (`GitOperationException`, etc.); never bare `Exception` (G-1) | `Mainguard.Agents/Exceptions/` |
| Branch browser is menu-tree based: `BranchCategoryViewModel { CategoryName, ObservableCollection<MenuItemViewModel> Branches }`; `[RelayCommand]`s for branch actions | `BranchBrowserViewModel.cs` |
| Commit model `GitCommitItem { Sha, ParentShas, ... }` has **no** ref-label field — labels are joined to commits by SHA in the graph/timeline layer | `Models/GitCommitItem.cs` |

**Policy split (G-7):** tag CRUD and checkout use **LibGit2Sharp**; only the network push/delete-remote may
fall back to the git CLI (mirroring the existing push fallback).

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Models/GitTagItem.cs` |
| **Edit** | `Mainguard.Agents/Services/IGitService.cs` + `GitServices.cs` (six methods) |
| **Edit** | `Mainguard.App.Shell/ViewModels/BranchBrowserViewModel.cs` (Tags section + context menu) |
| **Create** | `Mainguard.App.Shell/Views/CreateTagDialog.axaml(.cs)` + `Mainguard.App.Shell/ViewModels/CreateTagDialogViewModel.cs` |
| **Edit** | commit graph/timeline rendering (`CommitGraphCanvas` / `CommitTimelineViewModel`) for tag chips |
| **Create** | `Mainguard.Tests/GitServiceTagTests.cs` (TI-05) |

---

## 2. Contract (must exist exactly)

```csharp
// Mainguard.Agents/Models/GitTagItem.cs
namespace Mainguard.Agents.Models;
public sealed class GitTagItem
{
    public string Name { get; init; } = "";
    public string TargetSha { get; init; } = "";   // peeled target COMMIT sha
    public bool IsAnnotated { get; init; }
    public string? Message { get; init; }           // annotated only
    public string? TaggerName { get; init; }        // annotated only
}

// IGitService additions
IEnumerable<GitTagItem> GetTags(string repoPath);
void CreateTag(string repoPath, string name, string targetSha, string? message);  // annotated iff message != null
void DeleteTag(string repoPath, string name);
void PushTag(string repoPath, string remoteName, string name);
void DeleteRemoteTag(string repoPath, string remoteName, string name);
void CheckoutTag(string repoPath, string name);    // detached HEAD at peeled target
```

---

## 3. Service implementation (LibGit2Sharp)

### 3.1 `GetTags`

```csharp
public IEnumerable<GitTagItem> GetTags(string repoPath) =>
    ExecuteWithRepo(repoPath, repo => repo.Tags.Select(tag =>
    {
        // Peel to the underlying commit. Annotated tags -> tag.PeeledTarget; lightweight -> tag.Target.
        var peeled = tag.PeeledTarget as Commit ?? tag.Target as Commit;
        if (peeled == null) return null;                 // tag points at a non-commit (tree/blob) -> skip defensively
        return new GitTagItem
        {
            Name = tag.FriendlyName,
            TargetSha = peeled.Sha,
            IsAnnotated = tag.IsAnnotated,
            Message = tag.IsAnnotated ? tag.Annotation.Message : null,
            TaggerName = tag.IsAnnotated ? tag.Annotation.Tagger?.Name : null,
        };
    }).Where(t => t != null).Select(t => t!).ToList());
```

### 3.2 `CreateTag` — validate **before** mutating

```csharp
public void CreateTag(string repoPath, string name, string targetSha, string? message) =>
    ExecuteWithRepo(repoPath, repo =>
    {
        if (!Reference.IsValidName("refs/tags/" + name))
            throw new GitOperationException($"'{name}' is not a valid tag name.");
        if (repo.Tags[name] != null)
            throw new GitOperationException($"A tag named '{name}' already exists.");
        var target = repo.Lookup<Commit>(targetSha)
            ?? throw new GitOperationException($"No commit found for '{targetSha}'.");

        if (message != null)
            repo.Tags.Add(name, target, GetSignature(repo), message);   // annotated (G-3 signature)
        else
            repo.Tags.Add(name, target);                                // lightweight
    });
```

**Order matters:** name-validity → duplicate → target-exists, all *before* `repo.Tags.Add`. The repo is
never left with a half-created ref (invariant 1). Annotated path uses `GetSignature` (invariant 2, G-3).

### 3.3 `DeleteTag`

```csharp
public void DeleteTag(string repoPath, string name) =>
    ExecuteWithRepo(repoPath, repo =>
    {
        if (repo.Tags[name] == null) throw new GitOperationException($"No tag named '{name}'.");
        repo.Tags.Remove(name);
    });
```

### 3.4 `PushTag` / `DeleteRemoteTag` — LibGit2Sharp with CLI fallback

```csharp
public void PushTag(string repoPath, string remoteName, string name) =>
    ExecuteWithRepo(repoPath, repo =>
    {
        var remote = repo.Network.Remotes[remoteName]
            ?? throw new RemoteNotFoundException($"No remote named '{remoteName}'.");
        try
        {
            var opts = new PushOptions { CredentialsProvider = GetCredentialsProvider(...) };  // same as existing Push
            repo.Network.Push(remote, $"refs/tags/{name}:refs/tags/{name}", opts);
        }
        catch (LibGit2SharpException)
        {
            RunGitCheckedAuthenticated(repoPath, remoteName, "push", remoteName, $"refs/tags/{name}");
        }
    });

public void DeleteRemoteTag(string repoPath, string remoteName, string name) =>
    ExecuteWithRepo(repoPath, repo =>
    {
        try
        {
            var remote = repo.Network.Remotes[remoteName]
                ?? throw new RemoteNotFoundException($"No remote named '{remoteName}'.");
            var opts = new PushOptions { CredentialsProvider = GetCredentialsProvider(...) };
            repo.Network.Push(remote, $":refs/tags/{name}", opts);        // empty source = delete
        }
        catch (LibGit2SharpException)
        {
            RunGitCheckedAuthenticated(repoPath, remoteName, "push", remoteName, "--delete", $"refs/tags/{name}");
        }
    });
```

Copy the exact `GetCredentialsProvider(...)` call shape from the existing `Push`/`PushBranch` (it resolves
the token for the remote). Reuse, don't reinvent.

### 3.5 `CheckoutTag` — detached HEAD, never a branch

```csharp
public void CheckoutTag(string repoPath, string name) =>
    ExecuteWithRepo(repoPath, repo =>
    {
        var tag = repo.Tags[name] ?? throw new GitOperationException($"No tag named '{name}'.");
        var commit = (tag.PeeledTarget as Commit) ?? (tag.Target as Commit)
            ?? throw new GitOperationException($"Tag '{name}' does not point to a commit.");
        Commands.Checkout(repo, commit);   // detached HEAD at the peeled commit
    });
```

**Never** create a branch here (rejection trigger).

Add all six to `IGitService` (G-10).

---

## 4. UI

### 4.1 Tags section in the branch browser

Add a `BranchCategoryViewModel { CategoryName = "Tags" }` populated from `GetTags()`, each a
`MenuItemViewModel` with a context menu: **Checkout** (`CheckoutTag`), **Push** (`PushTag` to the resolved
remote), **Delete** (`DeleteTag` with confirm), **Delete remote** (`DeleteRemoteTag`), **Copy name**.
All actions async (`Task.Run` + `IsBusy`) with typed-exception routing.

### 4.2 Create-tag dialog

`CreateTagDialog` launched from the **commit context menu** ("Create tag here" — this also becomes a menu
item in T-09's graph menus). Fields: name textbox; "Annotated" checkbox that reveals a message box; target
SHA prefilled from the selected commit (read-only). On OK → `CreateTag(repoPath, name, sha, annotated ?
message : null)`. Validate name client-side for instant feedback, but the **service still re-validates**
(never trust the UI).

### 4.3 Tag chips in the graph

Build `Dictionary<string /*sha*/, List<string> /*tagNames*/>` from `GetTags()` **once per refresh** (join by
`TargetSha`), and render tag chips in `CommitGraphCanvas`/`CommitTimelineView` **visually distinct from branch
labels** (e.g. a tag-shaped chip / different color). Placement follows the existing branch-label rendering
path; the Master Doc marks this plumbing an acceptable-variation location (ViewModel or a small helper) —
reviewers check rendering + refresh, not placement.

---

## 5. Edge-case matrix (binding — each needs a test)

| Case | Required behavior |
|---|---|
| invalid names `"a b"`, `"-x"`, `"a..b"`, `""` | typed throw **before** any repo mutation; tag count unchanged |
| duplicate tag name | typed throw; existing tag untouched |
| annotated tag on an old commit | `TargetSha` = peeled **commit**, not the tag object |
| lightweight tag | `IsAnnotated == false`, `Message == null` |
| checkout tag | HEAD **detached** at target; UI shows detached badge |
| push tag to bare remote (fixture) | remote `refs/tags/<name>` exists afterwards |
| delete remote tag | remote ref gone; local tag untouched |

---

## 6. Invariants (MUST)

1. Name validation happens before mutation; no half-created ref.
2. Annotated tags use `GetSignature` (no `BuildSignature`, no placeholder identity).
3. Tag data flows through `GitTagItem` — ViewModels never touch LibGit2Sharp tag types.
4. Push / delete-remote work against the **T-01 bare-remote fixture** with zero network.

---

## 7. Test contract — `Mainguard.Tests/GitServiceTagTests.cs` (TI-05)

Uses `TempRepoFixture` (+ `AddBareRemote()` for push cases). `IGitService git = new GitService();`

| # | Test | Assertion |
|---|---|---|
| 1 | `CreateTag_Lightweight_ShouldAppearInGetTags_NotAnnotated` | create with `message==null` → appears, `IsAnnotated==false`, `Message==null`. |
| 2 | `CreateTag_Annotated_ShouldCarryMessageAndTagger_AndPeelToTarget` | tag an **older** commit with a message → `Message`/`TaggerName` set, `TargetSha == olderCommitSha`. |
| 3 | `CreateTag_ShouldThrowTyped_OnInvalidName` | `[Theory]` `"a b"`,`"-x"`,`"a..b"`,`""` → `GitOperationException`, `GetTags` count unchanged. |
| 4 | `CreateTag_ShouldThrowTyped_OnDuplicateName` | second create same name → throws; original intact. |
| 5 | `DeleteTag_ShouldRemove_AndThrowTypedWhenMissing` | delete existing → gone; delete missing → `GitOperationException`. |
| 6 | `PushTag_ShouldCreateRemoteRef_OnBareRemote` + `DeleteRemoteTag_ShouldRemoveRemoteRef_KeepLocal` | after push, bare repo has `refs/tags/<name>`; after delete-remote, remote ref gone, local tag still present. |
| 7 | `CheckoutTag_ShouldDetachHead_AtPeeledTarget` | after checkout, `repo.Info.IsHeadDetached == true` and `repo.Head.Tip.Sha == TargetSha`. |

---

## 8. Rejection triggers / Reviewer script

**Rejection:** `CheckoutTag` creating a branch; tag ops letting a raw `LibGit2SharpException` escape to the
UI (wrap in typed); annotated tag via `BuildSignature` (G-3); network push without the CLI fallback.

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~Tag"                 # green, 7+ cases
grep -n "BuildSignature" Mainguard.Agents/                        # exactly 1 (inside GetSignature)
grep -nE "GetTags|CreateTag|DeleteTag|PushTag|CheckoutTag|DeleteRemoteTag" Mainguard.Agents/Services/IGitService.cs  # 6 hits
```

## 9. Definition of done

- [ ] `GitTagItem.cs` + six methods on `IGitService`/`GitService`, validation-before-mutation.
- [ ] Tags section + context menu + create-tag dialog + graph chips.
- [ ] All edge-matrix rows + invariants; TI-05 tests green (push/delete-remote on bare fixture).
- [ ] Reviewer script clean. One task = one PR linking **T-05**.
```
