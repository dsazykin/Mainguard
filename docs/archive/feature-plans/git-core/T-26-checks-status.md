# T-26 — CI / Checks Status — Implementation Plan

**Priority:** P1 · **Depends on:** T-23 (shared `GitHubApiClient` + `HostConnectionResolver` — REUSE).
> **Offline slice:** check-run/commit-status models + overall-state derivation (pure) + GitHub provider on the
> shared transport + JSON-fixture tests + badge VM + UI. **Live fetch + re-run against a real host DEFERRED.**

## 0. Why
Push a branch / open a PR and you still switch to the browser to see if CI passed. Surface GitHub Actions +
check-run + legacy commit-status state (green/red/pending) in-app on commits, branches, and PRs, with a link
to logs and a re-run action.

## 1. Contract
```csharp
// Mainguard.Agents/Models/CheckStatus.cs
namespace Mainguard.Agents.Models;

public enum CheckState { Pending, Success, Failure, Neutral }          // rolled-up, UI-facing

public sealed class CheckRunItem
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public CheckState State { get; init; }                              // mapped from status+conclusion
    public string RawStatus { get; init; } = "";                        // queued|in_progress|completed
    public string? Conclusion { get; init; }                            // success|failure|neutral|cancelled|timed_out|action_required|skipped
    public string DetailsUrl { get; init; } = "";                       // "view logs"
    public System.DateTimeOffset? CompletedAt { get; init; }
}

public sealed class CommitChecks
{
    public string Sha { get; init; } = "";
    public CheckState Overall { get; init; }                            // Failure if any failure; else Pending if any pending; else Success (Neutral ignored); empty -> Pending? see rule
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Pending { get; init; }
    public IReadOnlyList<CheckRunItem> Runs { get; init; } = System.Array.Empty<CheckRunItem>();
    public bool HasAny => Runs.Count > 0;
}
```
```csharp
// Mainguard.Agents/Services/ICheckStatusService.cs
public interface ICheckStatusService
{
    bool IsSupported(string repoPath);
    Task<CommitChecks> GetChecksAsync(string repoPath, string sha, CancellationToken ct);
    Task RerequestAsync(string repoPath, long checkRunId, CancellationToken ct);   // re-run (live-deferred behavior, but wired)
}
```

## 2. Implementation
1. **Reuse** `GitHubApiClient` + `HostConnectionResolver` (no new transport/resolver).
2. **Pure roll-up** — a testable `CheckStateMapper`: map each check-run (`status`+`conclusion`) and legacy
   status (`state`) to `CheckState`; then `Overall` = **Failure** if any Failure, else **Pending** if any
   Pending/queued/in_progress, else **Success** (Neutral/skipped don't fail). Empty → `HasAny=false`
   (treat as no-CI, not a failure). Pin these in unit tests.
3. **GitHub provider** via `GitHubApiClient`: `GET /repos/{o}/{r}/commits/{sha}/check-runs` (Actions/apps) +
   `GET /repos/{o}/{r}/commits/{sha}/status` (legacy combined status) → merge into `CommitChecks`.
   `POST /repos/{o}/{r}/check-runs/{id}/rerequest` for re-run. Token header only; errors typed + redacted.
4. **Other hosts** — GitLab (`/pipelines`), Bitbucket, AzDO as typed "not yet supported" stubs.
5. **UI.**
   - A compact **status badge** (✓ green / ✕ red / • amber pending / hidden when none) shown on the selected
     commit's detail area and (reuse) on PR rows in the PR panel.
   - A **Checks** panel/popover listing each run: name, state icon, "view logs" (open `DetailsUrl` in browser),
     and a **Re-run** action per completed run. Off-UI-thread, `IsBusy`-gated, graceful unsupported.
   - Badge/icon colors from tokens (Success/Danger/Warning), never raw.

## 3. Edge cases / invariants
- Token header-only (G-4); never URL/argv/log/exception (grep + test).
- Commit with no checks → `HasAny=false`, badge hidden, no throw.
- Mixed conclusions → Overall rule pinned (failure dominates; pending over success; neutral ignored).
- in_progress/queued → Pending. action_required/timed_out/cancelled → Failure. skipped/neutral → Neutral.
- Legacy status + check-runs both present → merged, not double-counted by name where they overlap (dedup by name+id is acceptable; document choice).
- All network off-thread + `IsBusy`; VM sees only the service + models; Core UI-free; failures typed.

## 4. Test contract (offline)
- Pure `CheckStateMapper` — pinned mapping for every status/conclusion + the Overall roll-up (all-pass, any-fail, any-pending, empty, neutral-only, mixed).
- Provider parsing vs fixtures: check-runs list, legacy status, merged `CommitChecks`, error→typed, token-never-leaks.
- `IsSupported` matrix; VM gating (badge state, re-run gated by IsBusy/support, open-logs URL).
- Headless render: the checks panel (mixed states) + the commit badge.
- **Manual (deferred):** against a real repo/PR — see live check state, open logs, re-run a check; confirm no token in any log.

## 5. Definition of done
- [x] Models + pure `CheckStateMapper` (pinned) + `ICheckStatusService`/impl on the shared spine; GitHub provider; typed stubs.
- [x] Commit status badge (inline on the commit detail card) + Checks panel (list + view-logs + re-run) with graceful unsupported. _(PR-row badge skipped — would need a per-PR fetch, not clean in the offline slice; documented.)_
- [x] Token header-only (proven — grep-audited + a token-never-leaks test); failures typed; reuse of the T-23 spine (shared `HostConnectionResolver` + `GitHubApiClient`, no duplication).
- [x] Offline tests green (mapper + fixtures + VM + render); live fetch/re-run deferred with `// TODO(T-26 human-review): live checks matrix` + a manual checklist (User-Testing Guide §23). One PR linking **T-26**.
