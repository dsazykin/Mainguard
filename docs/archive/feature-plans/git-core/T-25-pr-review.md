# T-25 — In-App Pull Request Review — Implementation Plan

**Priority:** P1 · **Depends on:** T-23 (PR service + `GitHubApiClient` + `HostConnectionResolver` — REUSE).
> **Offline slice:** review/comment models + GitHub review API on the shared transport + JSON-fixture tests +
> VM gating + UI. **Live review submission against a real host is DEFERRED** (host-account-gated).

## 0. Why
T-23 lets you create/list/merge PRs; the user still leaves the app to *review* them. This adds reading a PR's
reviews + inline comments and submitting a review (approve / request-changes / comment) — completing the loop.

## 1. Contract (must exist exactly)
```csharp
// Mainguard.Agents/Models/PullRequestReview.cs
namespace Mainguard.Agents.Models;

public enum ReviewVerdict { Comment, Approve, RequestChanges }          // maps to GitHub event COMMENT|APPROVE|REQUEST_CHANGES
public enum ReviewState  { Pending, Commented, Approved, ChangesRequested, Dismissed }

public sealed class PullRequestReview
{
    public long Id { get; init; }
    public string Author { get; init; } = "";
    public ReviewState State { get; init; }
    public string Body { get; init; } = "";
    public System.DateTimeOffset SubmittedAt { get; init; }
}

public sealed class ReviewComment            // an inline (file/line) comment thread entry
{
    public long Id { get; init; }
    public string Author { get; init; } = "";
    public string Path { get; init; } = "";
    public int? Line { get; init; }          // new-file line; null when outdated
    public string DiffHunk { get; init; } = "";
    public string Body { get; init; } = "";
    public System.DateTimeOffset When { get; init; }
}

public sealed class SubmitReview
{
    public ReviewVerdict Verdict { get; init; }
    public string Body { get; init; } = "";
}
```
```csharp
// Add to IPullRequestService (or a sibling IPullRequestReviewService in the same file/namespace):
Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(string repoPath, int number, CancellationToken ct);
Task<IReadOnlyList<ReviewComment>>     GetReviewCommentsAsync(string repoPath, int number, CancellationToken ct);
Task<PullRequestReview>                SubmitReviewAsync(string repoPath, int number, SubmitReview review, CancellationToken ct);
```

## 2. Implementation
1. **Reuse** `HostConnectionResolver` + `GitHubApiClient` (the T-24 shared spine) — no new resolver/HttpClient.
2. **GitHub endpoints** via `GitHubApiClient`: `GET /repos/{o}/{r}/pulls/{n}/reviews`, `GET …/pulls/{n}/comments`
   (inline review comments), `POST …/pulls/{n}/reviews` with `{ body, event }` (map `ReviewVerdict` →
   `COMMENT|APPROVE|REQUEST_CHANGES`). Token in the `Authorization` header only; errors → typed, redacted.
   Group review comments into threads by `path` (+ position) for display.
3. **UI.** Extend the PR panel: selecting a PR shows its **reviews** (author + verdict badge + body) and
   **inline comment threads** (path : line, diff-hunk context, body), plus a **Review** action: pick
   Approve / Request changes / Comment + a body → submit. Off-UI-thread, `IsBusy`-gated, graceful unsupported.
4. GitLab/Bitbucket/AzDO: the review methods on their stubs throw the typed "not yet supported".

## 3. Edge cases / invariants
- Token only in the Authorization header (G-4); never URL/argv/log/exception (grep + test).
- PR with no reviews / no inline comments → empty lists, renders cleanly.
- Approve/Request-changes/Comment map to the exact GitHub events; a 422 (e.g. can't approve own PR) → typed host message.
- Review comments on an outdated diff (`line == null`) render without crashing.
- All network off the UI thread + `IsBusy`; VM sees only the service + models; Core UI-free; every failure typed.

## 4. Test contract (offline)
- Provider parsing vs checked-in fixtures: reviews list (each state), inline comments (incl. an outdated one),
  submit-review request body shape (verdict→event, body), error→typed, token-never-leaks.
- VM gating: review action disabled when unsupported / `IsBusy`; threads grouped by path; verdict badge mapping.
- Headless render: a PR's reviews + comment threads panel + the submit-review affordance.
- **Manual (deferred):** against a real PR — read reviews, submit an approve + a request-changes + a plain comment; confirm on the host; no token in any log.

## 5. Definition of done
- [ ] Review/comment models + the three service methods; GitHub provider on the shared transport; stubs updated.
- [ ] PR panel shows reviews + inline threads; submit-review (approve/request-changes/comment) wired; graceful unsupported.
- [ ] Token header-only (proven); all failures typed; reuse of T-23/T-24 spine (no duplication).
- [ ] Offline tests green (fixtures + VM gating + render); live submission deferred with a TODO + manual checklist. One PR linking **T-25**.
