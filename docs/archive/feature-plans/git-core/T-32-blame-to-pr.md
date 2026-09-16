# T-32 — Blame → PR / Issue Jump — Implementation Plan

**Priority:** P2 · **Depends on:** T-11 (blame), T-23 (PR service + shared `GitHubApiClient`), T-24 (issues).
> **Offline slice:** commit→PR resolution + PR-body→linked-issue parsing (pure) on the shared transport +
> JSON-fixture tests + the blame-gutter jump action. **Live fetch against a real host DEFERRED.**

## 0. Why
Blame tells you *which commit* changed a line; the real question is *why* — which PR merged it and which issue
it closed. One click from a blame line to the PR (and its linked issue) turns "git archaeology" into a jump.
It's also the seam an agent uses to trace a line back to its rationale.

## 1. Contract
```csharp
// Mainguard.Agents/Models/CommitContext.cs
namespace Mainguard.Agents.Models;

public sealed class LinkedIssueRef { public int Number { get; init; } public string RepoFullName { get; init; } = ""; }  // "#12" or "org/repo#7"

public sealed class CommitContextResult
{
    public string Sha { get; init; } = "";
    public IReadOnlyList<PullRequestItem> PullRequests { get; init; } = System.Array.Empty<PullRequestItem>(); // PRs that introduced/contain the commit
    public IReadOnlyList<LinkedIssueRef> LinkedIssues { get; init; } = System.Array.Empty<LinkedIssueRef>();   // parsed from the PR bodies / titles
}
```
```csharp
// Mainguard.Agents/Services/ICommitContextService.cs
public interface ICommitContextService
{
    bool IsSupported(string repoPath);
    Task<CommitContextResult> GetForCommitAsync(string repoPath, string sha, CancellationToken ct);
}
// Mainguard.Agents/Commits/IssueReferenceParser.cs (pure): extract "#123", "org/repo#7", and closing keywords
//   (closes/fixes/resolves #n) from a PR body/title -> LinkedIssueRef[].
```

## 2. Implementation
1. **Reuse** `GitHubApiClient` + `HostConnectionResolver` (no new transport/resolver) and the T-23 `PullRequestItem`
   model.
2. **GitHub provider** via `GitHubApiClient`: `GET /repos/{o}/{r}/commits/{sha}/pulls` (PRs associated with a
   commit; needs the `groot`/preview accept header historically — use the current stable media type). Map to
   `PullRequestItem`s. For each PR, run the **pure `IssueReferenceParser`** over its title+body → `LinkedIssues`
   (dedup). Token header only; errors typed + redacted. GitLab/Bitbucket/AzDO typed stubs.
3. **Pure `IssueReferenceParser`** — matches `#123`, `owner/repo#123`, and closing keywords
   (`close[sd]?|fix(e[sd])?|resolve[sd]?` + `#n`); returns `LinkedIssueRef[]`; unit-pinned.
4. **UI — hook into the T-11 blame gutter.** A blame line's context action (or a small popover on the existing
   click-to-select-commit): **"Go to pull request"** (if one PR → open the PR panel/browser to it; if several →
   a small chooser) and **"Go to linked issue"** (open the Issues panel/browser). When unsupported/no PR found,
   the action is disabled/hidden with a hint. Off-UI-thread, `IsBusy`-gated. Reuse the PR/Issues panels + open-
   in-browser. No raw colors.

## 3. Edge cases / invariants
- Token header-only (G-4); never URL/argv/log/exception (grep + test).
- Commit with no associated PR → empty result, action hidden/disabled, no throw (e.g. a direct push).
- Multiple PRs contain the commit → all returned; UI offers a chooser.
- PR body referencing issues in another repo (`org/repo#7`) parsed with the repo; bare `#7` uses the PR's repo.
- Closing-keyword vs plain mention both captured (dedup by repo+number).
- All network off-thread + `IsBusy`; VM sees only the service + models; Core UI-free; failures typed.

## 4. Test contract (offline)
- Pure `IssueReferenceParser`: `#12`, `org/repo#7`, `Closes #3`, `fixes #4 and #5`, no-match, dedup — pinned.
- Provider parsing vs fixtures: commit→pulls (one, several, none), linked issues extracted from PR bodies, error→typed, token-never-leaks.
- `IsSupported` matrix; VM gating (single PR routes, multiple offers chooser, none disables, open URLs).
- Headless render (optional): the blame line's context popover with "Go to PR / issue".
- **Manual (deferred):** against a real repo — blame a line, jump to its PR + linked issue; confirm no token in any log.

## 5. Definition of done
- [ ] Models + pure `IssueReferenceParser` (pinned) + `ICommitContextService`/impl on the shared spine; GitHub provider (commit→pulls + linked-issue parse); typed stubs.
- [ ] Blame-gutter "Go to pull request / linked issue" action routing into the PR/Issues panels (or browser), graceful when unsupported / no PR.
- [ ] Token header-only (proven); failures typed; reuse of the T-11/T-23/T-24 spine (no duplication).
- [ ] Offline tests green (parser pinned + fixtures + VM gating [+ render]); live fetch deferred with a TODO + manual checklist. One PR linking **T-32**.
