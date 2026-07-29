# P2-08 — AI Gateway + Admission Control + Swarm Reconciler — Implementation Plan

**Task ID:** P2-08 · **Milestone:** M6 exit · **Priority:** P0 — **launch-blocking** (without it
the first session of the headline feature is a retry storm).
**Depends on:** P2-01 (key health ceilings), P2-07 (egress proxy the gateway fronts).
**Branch:** implement on `feature/P2-08-ai-gateway` off `phase2`; PR targets `phase2`.

> **Verification profile:** Automated (property + scripted-swarm + `FakeModelEndpoint` + Docker reconciler) + **live-model smoke before ship**.
> Bucket math is property-tested; the no-raw-429 invariant is asserted end-to-end against the fake endpoint; reconciler cases need the Docker leg. Recommended pre-ship: one real-provider session under sustained load to validate real rate-limit header behavior (`RequiresNetwork`, not in the PR gate).
>
> **Source of truth:** §P2-08 of `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md`. The market
> traceability rows also bind: cost-per-merged-change lands in this task's spend telemetry.

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
| [Master doc](../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md) §P2-08 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/Mainguard_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md) **TI-P2-08** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-08 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |
| **Launch-blocker / hardening gates** | **RT-D1 reconciler ordering** (master doc §3.1): on daemon boot, the P2-10 merge-reconcile pass runs **before** admission -- the reconciler must not admit new work for a repo with an outstanding merge lease until P2-10's journal replay has synthesized any missing `ConfirmMerge` record |

---

## 0. Context — what exists today

P2-01 knows a key's rate ceilings; P2-07 forces all model traffic through the egress proxy. This
task adds the daemon-side gateway that sits on that path: token-bucket rate limiting so N agents
share one key without anyone seeing a 429, budgets + spend telemetry, honest admission control
("4–6 agents on 16 GB"), and the boot-time reconciler that makes Docker the single source of truth
for swarm state.

### What you can rely on

| Fact | Where |
|---|---|
| `KeyHealth.RequestsPerMinute/TokensPerMinute/EstimatedConcurrentAgents` | `Mainguard.Agents/Security/ApiKeyHealthService.cs` (P2-01) |
| Egress proxy is the only route to model APIs | P2-07 `EgressProxyConfigurator` |
| `GatewayService` RPC stubs: `GetBudgets`, `SetBudgets`, `StreamSpend` | `Mainguard.Server/Services/GatewayGrpcService.cs` (P2-02) |
| `SandboxEngine` lifecycle + Docker-as-truth | P2-07 |
| PTY input pause point (worker PTY owned by the daemon) | P2-03 `PtySession` |
| Daemon SQLite for persisted state | P2-02 server infrastructure |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Agents/AiGateway.cs` (`IAiGateway`, `GatewayLease`, `GatewaySnapshot`) |
| **Create** | `Mainguard.Agents/Agents/TokenBucket.cs` (pure: requests + tokens/min buckets, FIFO fairness) |
| **Create** | `Mainguard.Agents/Agents/AdmissionController.cs` |
| **Create** | `Mainguard.Agents/Agents/SwarmReconciler.cs` |
| **Create** | `Mainguard.Agents/Agents/BudgetLedger.cs` (per-agent/per-day token + cost caps; spend rows persisted) |
| **Create** | `Mainguard.Server/Gateway/ModelProxyMiddleware.cs` (the in-path interception: proxy route → gateway lease → upstream; 429 handling) |
| **Edit** | `Mainguard.Server/Services/GatewayGrpcService.cs` (stubs → bodies: budgets get/set, spend stream) |
| **Edit** | P2-07 proxy config — model-API traffic routes through the gateway listener |
| **Create** | `Mainguard.Tests/TokenBucketTests.cs`, `AdmissionControllerTests.cs`, `SwarmReconcilerTests.cs`, `BudgetLedgerTests.cs`, `Mainguard.Tests/Integration/Fake429EndpointTests.cs` |
| **Edit** | `docs/repo-map/` index |

---

## 2. Contract (must exist exactly)

```csharp
// daemon Mainguard.Agents/Agents/AiGateway.cs
public interface IAiGateway
{
    Task<GatewayLease> AcquireAsync(string agentId, int estimatedTokens, CancellationToken ct); // FIFO within priority
    void Report429(string agentId, TimeSpan? retryAfter);
    GatewaySnapshot GetSnapshot();      // per-agent spend, queue depth, current limits
}
// AdmissionController.cs: bool CanSpawn(out string reason)  — VM memory sampled ≤5s, threshold default 85%
// SwarmReconciler.cs: reconcile Docker (sole source of truth — no lockfiles) against expected agents on boot
```

---

## 3. Implementation steps

### 3.1 `TokenBucket` (pure, property-tested)

Two coupled buckets per key (requests/min, tokens/min) seeded from the stored `KeyHealth`
(re-checked when the key changes). `TryAcquire(estimatedTokens)`; refill continuous
(fractional per elapsed time); **FIFO within a priority class** — a queue of waiters completed in
order as capacity refills; leases carry the estimate and are **settled with actuals** on release
(`Release(lease, actualTokens)` reconciles the bucket). Fairness requirement: with two saturating
consumers, grant counts stay within a bounded ratio (property test).

### 3.2 In-path 429 interception (step 2 — the invariant everything hangs on)

The model host is reachable **only** via the egress proxy route the gateway fronts
(`ModelProxyMiddleware` terminates the proxied CONNECT/forward for allowlisted model hosts):

1. Request arrives from agent container → `AcquireAsync(agentId, estimate)` (blocks in FIFO).
2. Forward upstream. On **429**: `Report429(agentId, retryAfter)` → the gateway (a) pauses the
   worker's **PTY input** (via the P2-03 session — the CLI stops issuing new work),
   (b) marks the agent `RateLimited` in `ListAgents` metadata, (c) exponential backoff honoring
   `Retry-After`, (d) retries the request, (e) resumes the PTY. **The CLI process never sees the
   429** — it sees a delayed 200.
3. Response token usage (provider usage headers/body) settles the lease.

Agent attribution: per-agent proxy credentials or per-agent listener port (choose one; the spec
requires the gateway to know which agent a request belongs to — per-agent port is simplest and
test-friendly).

### 3.3 Budgets + telemetry (step 3)

`BudgetLedger`: per-agent and per-day token + cost caps (cost from a static per-model price table,
documented in code). Exhausted → typed rejection surfaced in UI; the agent is **paused with a
typed reason, not killed** (edge row 3). Spend rows persisted (daemon SQLite) and streamed over
`GatewayService.StreamSpend`; snapshot exposes per-agent spend + queue depth + current limits.
Include the **cost-per-merged-change** hook: ledger rows carry `agentId` so P2-10 can join spend
to merged tasks — expose `GetSpendSince(agentId, DateTimeOffset)`.

### 3.4 Admission control (step 4)

`/proc/meminfo` (`MemAvailable`) sampled on demand with a ≤5 s cache; threshold default 85% used.
`CanSpawn(out reason)` false above threshold with the honest message ("Running N agents now;
16 GB supports 4–6 comfortably — free memory or stop an agent"). Spawn path consults it;
headroom surfaces in `ListAgents` metadata.

### 3.5 `SwarmReconciler` (step 5)

On daemon boot: list Docker containers labeled `mainguard.agent` (labels set by P2-07 spawn) vs the
expected-agents table (daemon SQLite):

- Expected but container dead/missing → prune worktree (P2-06 `RemoveAgentWorktree(force:true)`),
  mark `Dead` with disposal reason surfaced to UI.
- Live container but not expected (orphan) → adopt-or-stop per a policy setting (default adopt).
- **No PID files, no lockfiles** — Docker state is the sole truth (rejection trigger otherwise).
- **RT-D1 boot ordering (master doc §3.1 — binding once P2-10 lands):** the reconciler runs
  **after** P2-10's merge-reconcile pass — for any repo with a merge lease outstanding at crash
  time, the P2-10 journal replay (synthesizing a missing `ConfirmMerge`) completes **before**
  this reconciler admits new work or the `AdmissionController` accepts spawns for that repo.
  Ship the ordering seam now (an ordered boot-task list, merge-reconcile slot first), even
  though the slot is empty until P2-10.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| two agents, one key, sustained load | both proceed, fairness (neither starves), zero CLI crashes |
| 429 with `Retry-After: 5` | worker paused, resumes ≈5 s, CLI saw a delayed 200 |
| budget exhausted mid-task | agent paused with a typed reason, not killed |
| daemon reboot with 3 live containers, 1 dead | reconcile adopts 3, prunes 1, UI shows `Dead` disposal |
| memory ≥ threshold | spawn rejected with typed reason; existing agents unaffected |

---

## 5. Invariants (MUST)

1. **No agent process ever observes a raw 429** — integration-asserted with a fake model endpoint.
2. Bucket math is pure and property-tested (burst, refill, fairness).
3. Reconciler trusts Docker state only.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Bucket_RefillAndBurst_Properties` | property tests: never exceeds capacity; grants ≤ refill over any window; estimate-vs-actual settlement conserves tokens |
| 2 | `Bucket_FifoFairness` | two saturating waiters → alternating grants within bound; no starvation |
| 3 | `Backoff_HonorsRetryAfter` | `Retry-After: 5` → resume at ≈5 s (virtual clock) |
| 4 | `Budget_ExhaustionPausesTyped` | cap hit mid-stream → `RateLimited/BudgetExhausted` state, typed reason, PTY paused not killed |
| 5 | `Fake429Endpoint_CliNeverSees429` | fake model server returns 429 then 200; the agent-side client observes exactly one 200; PTY input pause/resume invoked |
| 6 | `Admission_ThresholdBlocksSpawn` | fake meminfo ≥85% → `CanSpawn=false` + honest reason; below → true |
| 7 | `Reconciler_DeadPrunedOrphanAdopted` | simulated Docker listing vs expected table → prune/adopt/mark-Dead outcomes; out-of-band `docker rm` covered |
| 8 | `Spend_StreamAndSnapshot` | ledger rows stream over `StreamSpend`; snapshot totals match rows |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** PID/lockfiles for liveness; gateway bypassed for any model host (proxy allowlist
entry without gateway fronting); bucket math with wall-clock reads inline (must be injectable
clock); killing agents on budget exhaustion.

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~TokenBucket|FullyQualifiedName~Admission|FullyQualifiedName~SwarmReconciler|FullyQualifiedName~Budget|FullyQualifiedName~Fake429"
grep -rn "DateTime.Now\|DateTime.UtcNow" Mainguard.Agents/Agents/TokenBucket.cs   # 0 hits (injected clock)
grep -rn "\.pid\|lockfile" Mainguard.Agents/Agents/SwarmReconciler.cs             # 0 hits
```

---

## 8. Definition of done

- [ ] Pure property-tested `TokenBucket`; FIFO fairness; actual-settlement.
- [ ] In-path 429 interception with PTY pause/resume; fake-endpoint integration green.
- [ ] Budgets + persisted spend + `StreamSpend`; cost-join hook for P2-10.
- [ ] Admission control with honest messaging; boot reconciler (Docker-as-truth).
- [ ] All edge rows tested; `docs/repo-map/` index updated. One task = one PR linking **P2-08**, base `phase2`.
