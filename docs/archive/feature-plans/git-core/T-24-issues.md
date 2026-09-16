# T-24 — GitHub/Host Issue Tracking — Implementation Plan

**Task ID:** T-24 (post-roadmap feature, user-requested) · **Priority:** P1 ("ultimate client" surface)
**Depends on:** T-10 (remote/host resolution), T-14 (multi-host token storage), **T-23 (host-agnostic
provider pattern + shared HttpClient + `GitHostDetector.ParseOwnerRepo` — REUSE, do not duplicate)**.

> **Offline slice this pass:** host-agnostic issue service + GitHub provider + JSON-fixture parsing tests +
> `IsSupported` matrix + ViewModel gating + panel. The **live create/list/comment/close matrix against a real
> host account is DEFERRED** (host-account-gated, exactly like T-23's live matrix).

---

## 0. Why

Mainguard can open, review, and merge PRs (T-23) but the user still leaves the app to triage issues. Seeing and
managing issues in-app closes the last "leave to the browser" loop and is a natural peer of the PR panel. It
also becomes a seam the agent layer later drives (an agent picks an issue → works it → opens a PR). This task
mirrors T-23 exactly: **host-agnostic issue service + provider adapters + UI**, GitHub as the v1 provider.

## 1. Contract (must exist exactly)

```csharp
// Mainguard.Agents/Models/Issue.cs
namespace Mainguard.Agents.Models;

public enum IssueState { Open, Closed }

public sealed class IssueLabel { public string Name { get; init; } = ""; public string Color { get; init; } = ""; } // color = host hex (6-digit), for the chip

public sealed class IssueItem
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public IssueState State { get; init; }
    public int CommentCount { get; init; }
    public IReadOnlyList<IssueLabel> Labels { get; init; } = Array.Empty<IssueLabel>();
    public IReadOnlyList<string> Assignees { get; init; } = Array.Empty<string>();
    public string Url { get; init; } = "";            // web URL, for "open in browser"
    public System.DateTimeOffset UpdatedAt { get; init; }
}

public sealed class IssueComment { public string Author { get; init; } = ""; public string Body { get; init; } = ""; public System.DateTimeOffset When { get; init; } }

public sealed class IssueDetail
{
    public IssueItem Summary { get; init; } = new();
    public string Body { get; init; } = "";
    public IReadOnlyList<IssueComment> Comments { get; init; } = Array.Empty<IssueComment>();
}

public sealed class CreateIssue
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Assignees { get; init; } = Array.Empty<string>();
}
```

```csharp
// Mainguard.Agents/Services/IIssueService.cs
namespace Mainguard.Agents.Services;

public interface IIssueService
{
    bool IsSupported(string repoPath);   // origin host supported AND a token stored (reuse T-23's resolution)
    Task<IReadOnlyList<IssueItem>> ListAsync(string repoPath, IssueState filter, CancellationToken ct);
    Task<IssueDetail> GetAsync(string repoPath, int number, CancellationToken ct);
    Task<IssueItem> CreateAsync(string repoPath, CreateIssue request, CancellationToken ct);
    Task<IssueComment> CommentAsync(string repoPath, int number, string body, CancellationToken ct);
    Task<IssueItem> SetStateAsync(string repoPath, int number, IssueState state, CancellationToken ct); // close/reopen
}
// IssueService dispatches by host to an internal IIssueProvider (GitHubIssueProvider first; GitLab/Bitbucket/AzDO stubs).
```

## 2. Implementation steps

1. **Reuse T-23 plumbing.** Host + token resolution, owner/repo parse (`GitHostDetector.ParseOwnerRepo`), the
   shared/injected `HttpClient`, `Redact`, and the typed-error mapping are already built for PRs — factor the
   shared bits so both services use one path (do NOT copy-paste a second host/token resolver or a second
   `HttpClient`). If a small shared helper (host+token+slug resolution) makes sense, extract it.
2. **GitHub issue provider (v1).** REST v3: `GET /repos/{o}/{r}/issues?state=&per_page=` (**CRITICAL edge
   case:** GitHub's issues endpoint returns PRs too — every item with a `pull_request` field IS a PR and MUST
   be filtered out of the issue list), `GET …/issues/{n}` + `GET …/issues/{n}/comments`, `POST …/issues`,
   `POST …/issues/{n}/comments`, `PATCH …/issues/{n}` with `state=open|closed`. Token in the `Authorization:
   Bearer` header **only** (never argv/URL/log/exception — G-4). Map JSON → the models; error/unknown → typed
   `GitOperationException`/`AuthenticationRequiredException`, host text redacted of any token.
3. **Other hosts.** GitLab (`/projects/:id/issues`), Bitbucket, Azure DevOps as typed "not yet supported for
   <host>" stubs behind the same interface (additive).
4. **UI.** An **Issues** panel (`IssuesView`/`IssuesViewModel`) reachable from the repo workspace menu and the
   command palette (T-18): a list of open issues (number, title, author, **label chips**, assignees, comment
   count, updated-at), a **filter** toggle (Open/Closed), a **New issue** command (dialog: title + body +
   optional labels/assignees), and per-issue **Comment** / **Close·Reopen** / **Open in browser**. Graceful
   **unsupported/no-token** state (sign-in affordance, never errors/blocks). All network off the UI thread,
   `IsBusy`-gated, collections mutated only on `Dispatcher.UIThread`.
5. **Label chip color.** The label's host hex is data, not a raw *UI* color choice — render it as a chip
   background computed from that hex with a token-derived readable foreground (auto-contrast), so it honors
   the host's label colors without hardcoding app UI colors. Everything else uses design tokens.

## 3. Edge-case matrix

| Case | Required behavior |
|---|---|
| issues endpoint returns PRs mixed in | items with a `pull_request` field filtered OUT (only real issues listed) |
| origin host unsupported / no token | `IsSupported == false`; panel shows unsupported/sign-in state, no throw |
| create issue with labels/assignees the repo lacks | surface the host's 422 as a typed message; do not crash |
| close an already-closed issue (or reopen an open one) | idempotent-safe: host result mapped, local state consistent |
| issue with no labels / no assignees / no comments | empty collections, renders cleanly |
| token invalid/expired | typed `AuthenticationRequiredException`; route to re-auth, never log the token |
| rate-limited / network down | typed `GitOperationException`; list shows retry, app stays responsive |
| very long body / unicode / emoji in title | preserved, not mangled |

## 4. Invariants (MUST)

1. Token only in the `Authorization` header — never argv, URL query, exception text, or logs (G-4); a token
   placeholder never appears in any produced string (assert in tests).
2. All issue network calls off the UI thread, `IsBusy`-gated; bound collections mutated only on `Dispatcher.UIThread`.
3. VM consumes only `IIssueService` + models; host JSON shapes confined to the provider (G-10).
4. Core stays UI-free (no Avalonia ref in the service/providers).
5. Every failure is a typed exception; no bare `Exception`, no raw host error leaking a token.
6. The PR-vs-issue distinction is enforced in the provider (PRs never appear as issues).
7. Reuse T-23's host/token/HttpClient plumbing — no duplicate resolver or per-call `new HttpClient`.

## 5. Rejection triggers

- Token in a URL, argv, log line, or exception message.
- A PR leaking into the issue list (missing the `pull_request` filter).
- Any issue network call on the UI thread; host-specific JSON leaking into the ViewModel.
- A second copy-pasted host/token resolver or `HttpClient` (should reuse T-23's).

## 6. Test contract (offline, mirrors TI-23)

- **Provider parsing** against checked-in JSON fixtures (list incl. a **mixed PR+issue payload proving PRs are
  filtered out**; get+comments; create; comment; close/reopen → models; error bodies → typed exceptions; a
  token placeholder never appears in any produced string). No live network (injected `HttpMessageHandler`).
- **`IsSupported`** matrix over host/token combos (fake keyring + detector).
- **ViewModel** gating/state: New-issue disabled when unsupported, `IsBusy` gates commands, Open/Closed filter
  reloads, list marshals to the UI thread, label chips built.
- **Headless render**: the Issues panel populated-from-fixture + the unsupported/no-token empty state.
- **Manual (host-account-gated, deferred):** against a real repo — list open issues, create one, comment,
  close, reopen, open-in-browser; confirm no token in any argv/log. Mirrors T-23's manual matrix.

## 7. Definition of done

- [ ] `Issue.cs` models + `IIssueService`/`IssueService` dispatching by host; GitHub provider (v1) + typed stubs.
- [ ] PR-vs-issue filtering enforced + tested with a mixed fixture.
- [ ] Reuses T-23 host/token/HttpClient plumbing (no duplication).
- [ ] Issues panel (list + filter + create + comment + close/reopen + open-in-browser) with graceful unsupported state; repo menu + command palette entries.
- [ ] Token only in the Authorization header (grep + test proven); all failures typed.
- [ ] Offline test suite green (fixtures + IsSupported + VM gating + render harness); live matrix deferred with a TODO marker + manual checklist. One PR linking **T-24**.
