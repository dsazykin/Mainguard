# P2-12 — External Agent PR Intake — Implementation Plan

**Task ID:** P2-12 · **Milestone:** M7 (accelerated 2026-07-07 — the vendor-neutral square is
still empty) · **Priority:** P0
**Depends on:** P2-10; reuses T-23 (PR list/merge API), T-29 (PR → worktree).
**Branch:** implement on `feature/P2-12-external-pr-intake` off `phase2`; PR targets `phase2`.

> **Verification profile:** Fully automated fixture-driven (T-23 seam); optional nightly live-host smoke; no human step in the PR gate.
> Poll/materialize/invalidate/cleanup are all recorded-fixture tests; the merge-path dispatch is a unit test. One live GitHub smoke behind `RequiresNetwork` is nightly, not PR.
>
> **Source of truth:** §P2-12 of `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md`.
> **Why:** teams already run Codex/Jules/Copilot cloud agents that only surface PRs. Subscribing
> those PRs into the same verify→review→merge pipeline makes Mainguard useful on day one without
> anyone changing how they run agents.

---

## 0.a Binding companions (2026-07-12 refresh)

This plan was refreshed against the master doc as consolidated on `phase2` at `0f80d21`
(2026-07-12), and this branch now carries that baseline via the merge commit in its history:
the Lane-H engineering pass (1,115-test suite, zero-warning build, [ADR-001...007](../phase-2/ADRs.md)),
the design corpus under `docs/design/`, and the orchestration hardening specs under `docs/phase-2/`.
The items below are **binding** alongside this plan. Where this plan and a companion disagree,
the master doc wins -- and fix the drift here in the same PR.

| Companion | What binds |
|---|---|
| [Master doc](../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md) §P2-12 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/Mainguard_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md) **TI-P2-12** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-12 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

P2-10's queue keys on a **branch**, not a PTY, precisely so this task can enqueue branches that
have no local agent behind them. T-23 gives an audited `IPullRequestService` (list, details,
merge via host API); T-29 can materialize a PR into a worktree. This task glues them: poll for
bot-authored PRs, mirror each PR head into the VM bare repo as `agent/pr-<n>`, run it through
verification and the cockpit, and merge via the **host PR merge API** instead of the local
foreground merge.

### What you can rely on

| Fact | Where |
|---|---|
| `IPullRequestService` (T-23): list/detail/merge through the one audited `GitHubApiClient` transport | `Mainguard.Agents/Services/PullRequestService.cs`, `Mainguard.Agents/Hosting/` |
| Typed host-API error path incl. rate-limit handling | `Mainguard.Agents/Hosting/GitHubApiClient.cs` |
| `IMergeQueue` — entries keyed by branch; states; `NotifyMainMoved` | P2-10 |
| VM bare repo + worktree manager + authenticated CLI fetch (`RunGitCheckedAuthenticated` family) | P2-06 |
| Review cockpit renders any queue branch; provenance falls back gracefully | P2-11 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Agents/Orchestrator/ExternalPrIntake.cs` (`IExternalPrIntake` + impl, daemon) |
| **Create** | `Mainguard.Agents/Agents/Orchestrator/PrIntakeStore.cs` (subscriptions + seen PR head SHAs, daemon SQLite) |
| **Create** | `Mainguard.Agents/Agents/Orchestrator/MergeDispatch.cs` (per-entry merge strategy: local foreground vs host API) |
| **Edit** | `Mainguard.Agents/Agents/Orchestrator/MergeQueue.cs` (entry origin field; merge step pluggable via `MergeDispatch`) |
| **Edit** | daemon scheduler (poll timer; interval setting) + `AgentService`/queue protos (external entries visible with origin metadata) |
| **Create** | `Mainguard.App.Shell/ViewModels/PrIntakeSettingsViewModel.cs` + view section (sources, author filters, poll interval) |
| **Create** | `Mainguard.Tests/ExternalPrIntakeTests.cs`, `MergeDispatchTests.cs` |
| **Edit** | `docs/repo-map/` index |

---

## 2. Contract (must exist exactly)

```csharp
// Mainguard.Agents/Agents/Orchestrator/ExternalPrIntake.cs (daemon)
public sealed record ExternalPrSource(string Host, string Owner, string Repo, string? AuthorFilter); // e.g. bots
public interface IExternalPrIntake
{
    void Subscribe(ExternalPrSource source);
    /// <summary>Poll: new/updated open PRs matching the filter → materialize each as a queue entry
    /// (fetch PR head into the VM bare repo as agent/pr-<n>, worktree, enter MergeQueue at Working).</summary>
    Task PollOnceAsync(CancellationToken ct);
}
```

---

## 3. Implementation steps

### 3.1 Subscription + polling (step 1)

- `Subscribe` persists the source; duplicate subscribe of the same `(host,owner,repo,filter)` is
  idempotent (edge row 3).
- `PollOnceAsync`: list open PRs via `IPullRequestService`; filter by author against the
  configurable bot list (defaults: `codex[bot]`, `google-jules[bot]`, `copilot`); compare each
  PR's head SHA against `PrIntakeStore` — new PR or moved head ⇒ materialize/re-materialize.
- All host traffic through the audited T-23 transport (token in the `Authorization` header only —
  a second transport copy is a rejection trigger). Rate limits → the host client's typed error
  path → backoff on the poll timer, never a crash loop (edge row 4).

### 3.2 Materialization (step 2)

For PR *n*: `git fetch origin pull/<n>/head:agent/pr-<n>` into the **VM bare repo**
(authenticated CLI path — this fetch happens daemon-side where the Windows repo's host remote is
reachable via configured credentials; note the quarantine rule cuts the *agent worktree* off from
the real remote, not the daemon's provisioning plane), create the worktree via
`IAgentWorktreeManager` (agent id `pr-<n>`), enter the P2-10 queue at `Working` → verification
runs exactly as for local agents (its own sandbox — spawn a container for the worktree; no PTY
attached).

### 3.3 Merge dispatch (step 3)

`MergeDispatch` selects per entry origin:

- **Local agent** → existing `ForegroundMergeService` (P2-10).
- **External PR** → host PR merge API (T-23 merge), then `NotifyMainMoved` after the merged sha
  lands locally (fetch main).

Unit-test the dispatch seam. Review still happens in the P2-11 cockpit; the merge click is the
same human gate.

### 3.4 Update / closure semantics (step 4)

- New commits on the PR (head moved) → old verification invalidated, entry re-enters `Working`
  (stale semantics identical to local agents; force-push is just a head move whose old sha
  disappears — edge row 1).
- PR closed/merged upstream mid-queue → entry cancelled, worktree pruned, branch `agent/pr-<n>`
  deleted (edge row 2).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| PR force-pushed | old verification invalidated, re-queued |
| PR closed upstream mid-queue | entry cancelled, worktree pruned |
| same PR subscribed twice | idempotent |
| rate limits | polls go through the host client's typed error path; backoff, never a crash loop |

---

## 5. Invariants (MUST)

1. Intake **writes nothing to the upstream PR** without an explicit user action (review submit /
   merge click).
2. Token usage stays inside the audited T-23 transport.
3. External entries obey the same `CanMerge` gates (staleness, flagged acks) as local branches.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Poll_MaterializesMatchingPrs` | fixture PR list (bot + human authors) → only bot PRs become `agent/pr-<n>` queue entries at `Working` |
| 2 | `Poll_HeadMoved_Requeues` | same PR, new head sha → verification record invalidated, state `Working`, worktree refreshed |
| 3 | `Poll_ClosedPr_Cancels` | closed upstream → entry gone, worktree + branch pruned |
| 4 | `Subscribe_Idempotent` | double subscribe → one source row, one entry per PR |
| 5 | `Poll_RateLimit_BackoffNoLoop` | host client throws typed rate-limit → next poll delayed; no tight retry |
| 6 | `MergeDispatch_RoutesByOrigin` | local → foreground service invoked; external → host merge API invoked; both fire `NotifyMainMoved` |
| 7 | `Intake_NoUpstreamWrites` | full poll+verify cycle against the fixture transport → zero non-GET requests recorded |

All fixture-driven through the `HttpMessageHandler`/transport seams — no live network.

---

## 7. Rejection triggers / Reviewer script

**Rejection:** a second host transport; upstream writes during intake; external entries bypassing
verification or the flagged gate; poll loop without backoff.

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~ExternalPrIntake|FullyQualifiedName~MergeDispatch"
grep -rn "HttpClient" Mainguard.Agents/Agents/Orchestrator/ExternalPrIntake.cs   # 0 hits — transport comes from T-23 services
```

---

## 8. Definition of done

- [ ] `IExternalPrIntake` per contract; subscriptions + seen-sha store; configurable bot filter + interval.
- [ ] Materialize → verify → cockpit → host-API merge; dispatch seam unit-tested.
- [ ] Force-push/closure/idempotency/rate-limit rows green; zero upstream writes proven.
- [ ] `docs/repo-map/` index updated. One task = one PR linking **P2-12**, base `phase2`.
