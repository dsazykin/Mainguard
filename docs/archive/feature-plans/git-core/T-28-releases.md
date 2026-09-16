# T-28 — Releases & Tags Composer — Implementation Plan

**Priority:** P1 · **Depends on:** T-05 (tags), T-23 (shared `GitHubApiClient` + `HostConnectionResolver` — REUSE).
> **Offline slice:** release models + a **pure changelog generator over LOCAL git** + GitHub provider on the
> shared transport + JSON-fixture tests + VM gating + composer UI. **Live publish to a real host DEFERRED.**
> Note: the "generate notes" button is fully offline (it reads local commits) — only the actual publish defers.

## 0. Why
Cutting a release means leaving for the browser to write notes and publish. Compose a release in-app: pick/
create a tag, auto-generate notes from the commits since the last release, and publish. The changelog
generator is also the seam an agent later uses to draft release notes.

## 1. Contract
```csharp
// Mainguard.Agents/Models/Release.cs
namespace Mainguard.Agents.Models;

public sealed class ReleaseItem
{
    public long Id { get; init; }
    public string TagName { get; init; } = "";
    public string Name { get; init; } = "";
    public string Body { get; init; } = "";
    public bool IsDraft { get; init; }
    public bool IsPrerelease { get; init; }
    public string Author { get; init; } = "";
    public System.DateTimeOffset? PublishedAt { get; init; }
    public string Url { get; init; } = "";
}

public sealed class CreateRelease
{
    public string TagName { get; init; } = "";
    public string TargetCommitish { get; init; } = "";   // branch or sha the tag points at (for a new tag)
    public string Name { get; init; } = "";
    public string Body { get; init; } = "";
    public bool IsDraft { get; init; }
    public bool IsPrerelease { get; init; }
}
```
```csharp
// Mainguard.Agents/Analytics/ChangelogGenerator.cs  — PURE, no IO
public sealed class ChangelogEntry { public string Type { get; init; } = ""; public string Scope { get; init; } = ""; public string Description { get; init; } = ""; public string Sha { get; init; } = ""; public bool Breaking { get; init; } }
public static class ChangelogGenerator
{
    // Parse a conventional-commit subject ("feat(scope)!: desc") into an entry; non-conventional → Type="other".
    public static ChangelogEntry ParseSubject(string sha, string subject);
    // Group entries into grouped markdown notes (Features / Fixes / Breaking / Other; breaking called out).
    public static string BuildNotes(IEnumerable<ChangelogEntry> entries, string? previousTag, string newTag);
}

// Mainguard.Agents/Services/IReleaseService.cs
public interface IReleaseService
{
    bool IsSupported(string repoPath);
    Task<IReadOnlyList<ReleaseItem>> ListAsync(string repoPath, CancellationToken ct);
    /// <summary>Local-only: generate notes from commits between the previous release tag (or repo start) and target.</summary>
    string GenerateNotes(string repoPath, string newTag, string targetCommitish);
    Task<ReleaseItem> CreateAsync(string repoPath, CreateRelease request, CancellationToken ct);
}
```

## 2. Implementation
1. **Reuse** `GitHubApiClient` + `HostConnectionResolver` (no new transport/resolver) for list/create.
2. **`ChangelogGenerator` (pure, the offline heart).** `ParseSubject` handles `type(scope)!: desc`,
   `type: desc`, `BREAKING CHANGE`, and plain subjects (→ `other`). `BuildNotes` groups Features/Fixes/
   Breaking/Other with a compact `- desc (sha7)` list + a "Full changelog: prevTag…newTag" line. **Pin exact
   output in unit tests.**
3. **`GenerateNotes`** (Core, local): via `IGitService.ExecuteWithRepo`, find the previous release tag
   (highest semver-ish tag reachable, or none → whole history), walk commits `prevTag..target`, `ParseSubject`
   each, `BuildNotes`. No network. Reuse existing tag/commit reads (T-05 `GetTags`, the commit walk).
4. **GitHub provider** via `GitHubApiClient`: `GET /repos/{o}/{r}/releases`, `POST /repos/{o}/{r}/releases`
   (`{ tag_name, target_commitish, name, body, draft, prerelease }`). Token header only; errors typed+redacted.
   GitLab/Bitbucket/AzDO typed stubs.
5. **UI.** A **Releases** panel (repo menu + palette): list existing releases (tag, name, **Draft/Prerelease
   badges**, published date, open-in-browser), and a **New release** composer — tag (existing tag picker or a
   new tag name + target branch), name, body editor with an **"Auto-generate notes"** button (fills from
   `GenerateNotes`), Draft/Prerelease toggles, **Publish**. Off-thread, `IsBusy`-gated, graceful unsupported.

## 3. Edge cases / invariants
- Token header-only (G-4); never URL/argv/log/exception (grep + test).
- No previous release tag → notes cover whole history; empty repo → empty notes, no throw.
- Non-conventional subjects → grouped under Other, never dropped. Breaking (`!` or `BREAKING CHANGE`) called out.
- New tag vs existing tag both supported in `CreateRelease` (target only needed for a new tag).
- Network (list/create) off-thread + `IsBusy`; `GenerateNotes` is local + synchronous-ok but called off the UI thread in the VM. VM sees only the service; Core UI-free; failures typed.

## 4. Test contract (offline)
- Pure `ChangelogGenerator`: `ParseSubject` (feat/fix/scope/breaking/`!`/plain) + `BuildNotes` grouped-markdown **pinned exactly**.
- `GenerateNotes` over a local fixture repo: commits since a prev tag grouped correctly; no-prev-tag whole-history; empty.
- Provider parsing vs fixtures: releases list (draft/prerelease), create request body shape, error→typed, token-never-leaks.
- `IsSupported` matrix; VM gating (auto-generate fills body, publish gated by IsBusy/support, badges).
- Headless render: releases list + the composer with generated notes.
- **Manual (deferred):** against a real repo — list releases, compose one with auto-notes, publish (draft), confirm on the host; no token in any log.

## 5. Definition of done
- [ ] Models + **pure pinned `ChangelogGenerator`** + `GenerateNotes` (local) + `IReleaseService`/impl on the shared spine; GitHub provider; typed stubs.
- [ ] Releases panel (list + badges + open) + composer (tag/target, name, body, auto-generate notes, draft/prerelease, publish) with graceful unsupported.
- [ ] Token header-only (proven); failures typed; reuse of the T-23 spine (no duplication).
- [ ] Offline tests green (changelog pinned + GenerateNotes over a fixture + release fixtures + VM + render); live publish deferred with a TODO + manual checklist. One PR linking **T-28**.
