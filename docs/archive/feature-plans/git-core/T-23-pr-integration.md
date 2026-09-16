# T-23 — Pull/Merge Request Integration — Implementation Plan

> **Source:** extracted from the T-23 section of the Master Implementation Document as it exists on
> the `origin/docs/pr-integration-plan` branch (it was never merged to main's copy). This is the
> binding contract. **Offline slice only** this pass: host-agnostic service + GitHub provider + JSON-
> fixture parsing tests + IsSupported matrix + VM gating. The **live create/list/merge matrix against a
> real host account is DEFERRED** (host-account-gated, per the milestone note).

## T-23 — Pull/Merge request integration (host-account-gated)

**Milestone:** M4′ (hosting workstream) · **Priority:** P1 (headline "ultimate client" feature) · **Depends on:** T-10 (remotes mgmt — resolves the upstream remote/host), T-14 (multi-host auth — per-host token/OAuth storage). Reuses `Security/GitHostDetector.cs` and `Sync/GitHubAuthClient.cs` (both on `main`).

### Why

Mainguard can push branches but the user still leaves the app to open, review, and merge a PR. Direct PR/MR integration closes the loop — create a PR from the current branch, list/inspect open PRs, and merge/close them in-app — and it is the seam the agentic layer later drives programmatically (an agent finishes a task → opens a PR). This task builds the **host-agnostic PR service + provider adapters + UI**; GitHub is the v1 provider, with GitLab/Bitbucket/Azure DevOps as adapter stubs behind the same interface.

### Contract (must exist exactly)

```csharp
// Mainguard.Agents/Models/PullRequest.cs
namespace Mainguard.Agents.Models;

public enum PullRequestState { Open, Closed, Merged, Draft }
public enum PullRequestMergeMethod { Merge, Squash, Rebase }

public sealed class PullRequestItem
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string SourceBranch { get; init; } = "";   // head ref (friendly)
    public string TargetBranch { get; init; } = "";   // base ref (friendly)
    public PullRequestState State { get; init; }
    public bool IsDraft { get; init; }
    public string Url { get; init; } = "";            // web URL, for "open in browser"
}

public sealed class PullRequestDetail
{
    public PullRequestItem Summary { get; init; } = new();
    public string Body { get; init; } = "";
    public bool Mergeable { get; init; }
    public IReadOnlyList<string> Reviewers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<(string Name, string State)> Checks { get; init; } = Array.Empty<(string, string)>();
}

public sealed class CreatePullRequest
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string SourceBranch { get; init; } = "";
    public string TargetBranch { get; init; } = "";
    public bool IsDraft { get; init; }
}
```

```csharp
// Mainguard.Agents/Services/IPullRequestService.cs
namespace Mainguard.Agents.Services;

public interface IPullRequestService
{
    /// <summary>True when the repo's origin host is supported and a token is stored for it.</summary>
    bool IsSupported(string repoPath);
    Task<IReadOnlyList<PullRequestItem>> ListAsync(string repoPath, PullRequestState filter, CancellationToken ct);
    Task<PullRequestDetail> GetAsync(string repoPath, int number, CancellationToken ct);
    Task<PullRequestItem> CreateAsync(string repoPath, CreatePullRequest request, CancellationToken ct);
    Task<PullRequestItem> MergeAsync(string repoPath, int number, PullRequestMergeMethod method, CancellationToken ct);
    Task CloseAsync(string repoPath, int number, CancellationToken ct);
}
// Mainguard.Agents/Services/PullRequestService.cs : IPullRequestService dispatches by host to an
// internal IPullRequestProvider (GitHubPullRequestProvider first; GitLab/Bitbucket/AzureDevOps stubs).
```

### Implementation steps

1. **Host + token resolution.** From `repoPath`, read `origin` URL, classify with `GitHostDetector` → `HostKind`, load the token via `SecureKeyring` key `token_<host>` (the T-14 storage; GitHub also honors the `GitHubAuthClient` device-flow token). `IsSupported` = host has a provider **and** a token is present.
2. **Provider interface** (`internal interface IPullRequestProvider`) with the five async operations, taking `(owner, repo, token, …)`. `PullRequestService` parses owner/repo from the remote URL once and dispatches.
3. **GitHub provider (v1).** REST v3 over `HttpClient`: `GET /repos/{o}/{r}/pulls?state=`, `POST /repos/{o}/{r}/pulls`, `PUT /repos/{o}/{r}/pulls/{n}/merge` (map `PullRequestMergeMethod` → `merge|squash|rebase`), `PATCH …/pulls/{n}` with `state=closed`. Token in the `Authorization: Bearer` **header only** (never argv/URL/logs — G-4). Map JSON → the models above; unknown/error responses → typed `GitOperationException` with the host's message (redacted of any token).
4. **Other hosts.** GitLab (`/projects/:id/merge_requests`), Bitbucket, Azure DevOps providers may ship as stubs that throw a typed "not yet supported for <host>" — the interface and dispatch must exist so adding them is additive.
5. **UI.** A **Pull Requests** panel (new `PullRequestsView`/`PullRequestsViewModel`) reachable from the repo workspace: a list of open PRs (title, number, author, source→target, draft/checks badges), a **Create PR** command (dialog prefilled source = current branch, target = default branch, title = last commit subject, body editable), and per-PR **Merge** (method picker) / **Close** / **Open in browser**. A "Create pull request" entry also appears in the branch context menu and (via T-18) the command palette. All network work is `async`/`Task.Run`-free (the service is already async) but still gated by `IsBusy`; results marshalled to the UI thread.
6. **Graceful absence.** When `IsSupported` is false (unsupported host or no token), the panel shows a sign-in / unsupported affordance instead of erroring; never block the rest of the app.

### Edge-case matrix

| Case | Required behavior |
|---|---|
| origin host unsupported | `IsSupported == false`; UI shows unsupported state, no throw |
| no token stored for host | `IsSupported == false`; UI routes to the T-14 sign-in flow |
| create PR when one already exists for the branch | surface the host's 422 as a typed message ("a PR already exists") — do not duplicate |
| merge a PR that isn't mergeable | typed failure with the host's reason; local state unchanged |
| detached HEAD / no upstream branch | Create-PR disabled with a hint to push the branch first |
| token invalid/expired | typed `AuthenticationRequiredException`; route to re-auth, never log the token |
| rate-limited / network down | typed `GitOperationException`; list view shows a retry, app stays responsive |

### Invariants (MUST)

1. Tokens flow through keyring/env and the `Authorization` header only — never argv, URL query, exception text, or logs (G-4). `grep` for the token variable shows it never string-interpolated into a URL or message.
2. All PR network calls are off the UI thread and gated by `IsBusy` (G-5); bound collections mutated only on `Dispatcher.UIThread`.
3. Public surface consumed by the ViewModel is behind `IPullRequestService` (G-10); host specifics live in providers, not the ViewModel.
4. Core stays UI-free; the service and providers live in `Mainguard.Agents` with no Avalonia reference.
5. Every failure is a typed exception (`GitOperationException` / `AuthenticationRequiredException`); no bare `Exception`, no raw host error leaking a token.

### Acceptable variations (MAY)

- Octokit (or another maintained client) instead of hand-rolled `HttpClient` for the GitHub provider, as long as the token still never leaks and Core takes no UI dependency.
- Richer `PullRequestDetail` (labels, assignees, timeline) as additive members.
- PR list embedded in the existing branch browser vs. a dedicated panel — reviewer checks behavior, not placement.

### Rejection triggers

- Token in a URL, argv, log line, or exception message.
- Any PR network call on the UI thread, or host-specific JSON shapes leaking into the ViewModel.
- Provider `HttpClient` per-call `new` without reuse (socket exhaustion) — use a shared/injected client.

### Test contract (companion doc §TI-23)

- **Provider parsing** against **recorded JSON fixtures** (checked-in sample responses) — list/create/merge/close map to the models correctly, error bodies map to typed exceptions, and a token placeholder never appears in any produced string. **No live network.**
- **`IsSupported`** matrix over host/token combinations (fake keyring + detector).
- **ViewModel** gating/state tests (need TI-00 headless infra): Create-PR disabled on detached HEAD, `IsBusy` gates commands, list marshals to the UI thread.
- **Manual (host-account-gated, not automated):** against a real GitHub repo — create a draft PR from a branch, list it, merge (squash) it, confirm on the host; then the same on a second provider once its adapter lands. This mirrors T-14's manual matrix and is why T-23 is skipped in offline auto-implement passes.

**Milestone note:** land the host-agnostic service + GitHub provider + parsing tests first (offline-verifiable); the multi-provider matrix and live flows follow as credentials/accounts become available.

---
