# T-27 — Notifications Inbox — Implementation Plan

**Priority:** P1 · **Depends on:** T-23 (shared `GitHubApiClient` + `HostConnectionResolver` — REUSE).
> **Offline slice:** notification models + GitHub provider on the shared transport + JSON-fixture tests +
> VM gating + inbox UI. **Live fetch + mark-read against a real host account DEFERRED.**

## 0. Why
Mentions, review requests, and assignments still pull the user to the browser. Surface the authenticated
user's GitHub notifications in-app with mark-read and jump-to, closing the last "open the browser" loop.

## 1. Contract
```csharp
// Mainguard.Agents/Models/Notification.cs
namespace Mainguard.Agents.Models;

public enum NotificationReason { Mention, ReviewRequested, Assign, Author, Comment, StateChange, Subscribed, TeamMention, CiActivity, Other }
public enum NotificationSubjectKind { PullRequest, Issue, Commit, Release, Discussion, Other }

public sealed class NotificationItem
{
    public string Id { get; init; } = "";              // thread id (string; used for mark-read)
    public NotificationReason Reason { get; init; }
    public NotificationSubjectKind Kind { get; init; }
    public string Title { get; init; } = "";           // subject.title
    public string RepoFullName { get; init; } = "";     // owner/repo
    public string Url { get; init; } = "";              // web URL (derive from subject.url api→html; ok if best-effort)
    public bool Unread { get; init; }
    public System.DateTimeOffset UpdatedAt { get; init; }
}
```
```csharp
// Mainguard.Agents/Services/INotificationService.cs
public interface INotificationService
{
    bool IsSupported(string repoPath);   // repo's host supported AND a token stored (notifications are user-scoped)
    Task<IReadOnlyList<NotificationItem>> ListAsync(string repoPath, bool onlyUnread, CancellationToken ct);
    Task MarkReadAsync(string repoPath, string threadId, CancellationToken ct);
    Task MarkAllReadAsync(string repoPath, CancellationToken ct);
}
```

## 2. Implementation
1. **Reuse** `GitHubApiClient` + `HostConnectionResolver` (no new transport/resolver). The host/token still
   comes from the current repo's origin (notifications are the authenticated user's, scoped by that token).
2. **GitHub provider** via `GitHubApiClient`: `GET /notifications?all=` (all vs unread-only), map `reason` and
   `subject.type` to the enums (pure, testable `NotificationMapper`), keep `subject.url` for jump-to (best-
   effort api→html conversion is fine). `PATCH /notifications/threads/{id}` (mark one read),
   `PUT /notifications` (mark all read). Token header only; errors typed + redacted.
3. **Other hosts** — GitLab (`/notifications` todos), Bitbucket, AzDO as typed "not yet supported" stubs.
4. **UI.** A **Notifications** inbox panel (repo menu + command palette): list grouped by repo, each item with
   a **reason chip**, subject-kind icon, title, updated-at, **unread** styling (dot/bold); **mark read** per
   item, **mark all read**, and **open** (jump to `Url`). An **Unread only** toggle. Off-UI-thread,
   `IsBusy`-gated, graceful unsupported/no-token. Reason chips/icons from tokens (no raw colors).

## 3. Edge cases / invariants
- Token header-only (G-4); never URL/argv/log/exception (grep + test).
- No notifications → empty state, no throw. Unknown reason/subject-type → `Other`.
- Mark-read updates local state optimistically but reflects the host result; mark-all clears unread.
- All network off-thread + `IsBusy`; VM sees only the service + models; Core UI-free; failures typed.

## 4. Test contract (offline)
- Pure `NotificationMapper` — every reason + subject.type → enum (+ Other fallback).
- Provider parsing vs fixtures: mixed list (PR/Issue/Commit/Release, read+unread), unread-only, mark-read/mark-all request shapes, error→typed, token-never-leaks.
- `IsSupported` matrix; VM gating (unread-only reload, mark-read/all gated by IsBusy/support, grouped by repo, open URL).
- Headless render: the inbox (mixed reasons, unread styling) + the unsupported state.
- **Manual (deferred):** against a real account — see live notifications, mark one + all read, jump-to; confirm no token in any log.

## 5. Definition of done
- [ ] Models + pure `NotificationMapper` + `INotificationService`/impl on the shared spine; GitHub provider; typed stubs.
- [ ] Inbox panel (grouped list + reason chips + unread styling + mark-read/all + open + unread-only toggle) with graceful unsupported.
- [ ] Token header-only (proven); failures typed; reuse of the T-23 spine (no duplication).
- [ ] Offline tests green (mapper + fixtures + VM + render); live fetch/mark-read deferred with a TODO + manual checklist. One PR linking **T-27**.
