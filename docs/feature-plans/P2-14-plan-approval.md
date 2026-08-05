# P2-14 — Plan Approval + Dual-Mode Orchestration — Implementation Plan

**Task ID:** P2-14 · **Milestone:** M7 · **Priority:** P0 — the product thesis.
**Depends on:** P2-08 (budgets/admission), P2-09 (worker lifecycle), P2-13 (coordinator tab UI).
**Branch:** implement on `feature/P2-14-plan-approval` off `phase2`; PR targets `phase2`.

> **Verification profile:** Automated scripted-swarm + in-proc gRPC (incl. RT-D3/D4 + SA-1 guards) + **real-model coordinator smoke** + approval-card screenshot pass.
> Every governance invariant (input lock, role denial, freeze-first ordering, hard-ceiling timing, daemon-derived identity, pending-plan cap) is deterministic with `ScriptedAgentHarness` and a hand-crafted client. Before ship, run one session with a real LLM coordinator to sanity-check plan-drafting quality and tool-call ergonomics (model testing — CI cannot judge plan usefulness). The TaskPlan approval card gets themed PNGs + human visual approval per ControlCenterDesign §5.
>
> **Source of truth:** §P2-14 of `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md` (binds
> strategy §G-7.5, with plan-approval promoted to the headline). Security-adjacent: the
> input-lock and role invariants are enforced **daemon-side**, never UI-side.

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
| [Master doc](../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md) §P2-14 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/Mainguard_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md) **TI-P2-14** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-14 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |
| **Design decisions (binding)** | [`ControlCenterDesign.md`](../design/ControlCenterDesign.md) §5 -- coordinator chat, the TaskPlan approval card, and the always-visible kill switch (rail foot, every section per §0) are one surface |
| **Launch-blocker / hardening gates** | **RT-D3 + RT-D4 (M7/M7.5 exit) + OPS SA-1/F2 (daemon-derived approver identity) + SA-1/F4 (freeze-queue-first kill switch) + S-8 anti-approval-fatigue** are owned here (master doc §3.1; [OPS](../phase-2/Mainguard_Orchestration_Protocol_Spec.md) §2.8) -- see the 2026-07-12 additions sections below |

---

## 0. Context — what exists today

Everything below the orchestration layer exists after P2-08/09/13: spawn-capable sandboxes,
lifecycle, gateway, UI chrome. This task adds the two operating modes — **manual** (user drives
each agent directly) and **coordinated** (a Coordinator chat agent decomposes work and spawns
workers) — with the governance spine: structured plans approved by a human before any worker
starts, daemon-enforced terminal locking for managed workers, and one always-visible kill switch.

### What you can rely on

| Fact | Where |
|---|---|
| Agent spawn/stop RPCs + lifecycle + teardown | P2-02/P2-09 |
| `IAiGateway` leases + budgets; `AdmissionController.CanSpawn` | P2-08 |
| Coordinator pinned tab + `IsAttentionRequired` pulse + workspaces | P2-13 |
| Merge queue human gate (`AwaitingReview` → human merge only) | P2-10 |
| Adapter channel (agent CLIs at pinned versions) | P2-22 (parallel; use the P2-03 spawn shape meanwhile) |
| Audit journal rows for approvals (G-17; chained later by P2-15) | plain rows now |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Agents/Orchestrator/CoordinatorAgent.cs` (chat agent host: tool loop over the gateway) |
| **Create** | `Mainguard.Agents/Agents/Orchestrator/CoordinatorTools.cs` (`spawn_worker`, `get_worker_status`, `send_worker_prompt`, `request_verification` — tool schemas + dispatch) |
| **Create** | `Mainguard.Agents/Agents/Orchestrator/TaskPlan.cs` (`TaskPlan { Scope: files[], Approach, TestStrategy }` + JSON-schema validation) |
| **Create** | `Mainguard.Agents/Agents/Orchestrator/PlanApprovalService.cs` (pending plans, approve/reject, approver OS identity persisted) |
| **Create** | `Mainguard.Server/Auth/RoleInterceptor.cs` (coordinator credential class: merge RPCs denied; worker input RPCs denied for locked terminals) |
| **Create** | `Mainguard.Agents/Agents/Orchestrator/KillSwitch.cs` (yield-all → pause fan-out + queue freeze + journal snapshot) |
| **Create** | `Mainguard.App.Shell/ViewModels/Agents/CoordinatorChatViewModel.cs`, `PlanApprovalViewModel.cs`, kill-switch command surfaced in `ActivityBarViewModel` |
| **Create** | corresponding Views (`CoordinatorChatView`, `PlanApprovalView`) |
| **Create** | `Mainguard.Tests/TaskPlanSchemaTests.cs`, `PlanApprovalTests.cs`, `CoordinatorToolCapTests.cs`, `InputLockGrpcTests.cs`, `KillSwitchTests.cs`, `Mainguard.Tests/Integration/ScriptedCoordinatorEndToEndTests.cs` |
| **Edit** | protos (coordinator chat bridge, plan approval RPCs, kill switch RPC) + `docs/repo-map/` index |

---

## 2. Contract (binding)

- **`CoordinatorAgent`** — chat agent with **no code, no worktree, no merges**; tools:
  `spawn_worker(taskSpec)`, `get_worker_status`, `send_worker_prompt`, `request_verification`;
  every tool capped by limits/budgets/admission.
- **Two-phase spawn:** structured `TaskPlan { Scope: files[], Approach, TestStrategy }`
  (JSON-schema validated) → rendered for approval → **workers start only on approved plans**;
  plan + approver OS identity persisted (P2-15 chains it). Approved `Scope` is what P2-11's
  out-of-Scope flag (SA-1/F6) later compares diffs against — the plan binds to a Scope.
- **Approver identity is derived daemon-side (OPS SA-1 / F2 — binding):** the recorded approver
  is resolved by the daemon **from the authenticated connection's OS peer credential** —
  **NEVER a client-supplied field**. A client-set `osIdentity` would let token-holding host
  malware forge an attributable approval; daemon-side derivation removes the trivial audit
  forgery. (Honest residual, stated: host malware running as the user can still drive approvals
  with a valid token — the host-trust boundary, OPS §1.1; a host-un-forgeable presence factor is
  deferred, OPS §10.1.)
- **Terminal locking** for managed workers enforced **daemon-side** — the input stream is severed
  at the gRPC layer (interceptor rejects `Attach` input frames for locked agents), not just UI
  read-only.
- **Kill switch (OPS SA-1 / F4 ordering — binding):** **freeze the merge queue FIRST** (in-proc,
  instant — reject `BeginMerge`/`ConfirmMerge`/spawn with `FAILED_PRECONDITION`) **before** the
  yield fan-out, so no merge slips through the up-to-ceiling fan-out window; then yield-all
  (timeout → `docker pause`) + journal snapshot; one always-visible control (rail foot, every
  section — ControlCenterDesign §0/§5).
- **Kill-switch timing (RT-D4 — M7 exit):** every safety-critical control timeout is
  `min(ceiling, max(floor, k×RTT))` with a **fixed absolute ceiling independent of the measured
  `RttBudget` EWMA** — the OOB channel's RTT terminates at the *untrusted supervisor*, so a
  supervisor-influenced RTT must never stretch the emergency stop. Local profile:
  `min(ceiling, max(5 s, 50×RTT))`. `docker pause` needs no supervisor cooperation, so the
  ceiling bounds only *how long* an agent runs during a kill, not correctness. An anomalous RTT
  spike feeds the P2-08 A3 `Unresponsive` signal rather than only a longer deadline.
- **Anti-approval-fatigue (S-8):** a `PlanPending` plan consumes no admission or budget, so cap
  concurrent `PlanPending` plans per Coordinator and rate-limit `spawn_worker` drafting; surface
  an "N plans pending" pressure signal; excess draft attempts return `RESOURCE_EXHAUSTED` and
  are audited.
- **RT-D3 (M7.5 exit, with P2-15):** the kill switch stays non-blocking (freeze-then-audit
  best-effort); on audit-store recovery the daemon appends a chained
  `killswitch_audit_gap{killEpochId, observedAt}` so a kill during an audit outage is
  tamper-evident, not a silent absence.
- **Human handoff:** `AwaitingReview` badge; merges only via the P2-10 human path — the
  coordinator **cannot invoke merge RPCs** (interceptor-enforced role, not convention).
- Coordinator serializes dependent tasks; partitioning quality (parallel vs serialized ratio,
  conflict-radar hits between its workers) is tracked telemetry.

---

## 3. Implementation steps

1. **`TaskPlan` + schema:** POCO + embedded JSON schema; `Validate(json) → errors[]`. Corpus of
   valid/invalid fixtures (missing scope, empty files, wrong types, extra fields tolerated?—
   decide: reject unknown top-level fields for forward-compat honesty).
2. **`PlanApprovalService`:** coordinator's `spawn_worker` lands a **pending plan** (persisted,
   with the coordinator's stated task spec); UI renders it as the TaskPlan approval card
   (ControlCenterDesign §5: scope file list, approach, test strategy); approve records
   `(plan, approver identity, timestamp)` — **the identity resolved daemon-side from the
   authenticated connection's OS peer credential (SO_PEERCRED uid on Linux, named-pipe/loopback
   peer identity on Windows), never from any client-supplied proto field; the proto has no
   `osIdentity` input field at all** (SA-1/F2) — and only then does the spawn proceed (P2-09
   path, admission + budget checks apply). Reject → nothing spawned, **no worktree residue**
   (edge row 1), coordinator informed via tool result.
   **S-8 caps:** at most N concurrent `PlanPending` per Coordinator (config, sane default) and a
   drafting rate limit; excess → `RESOURCE_EXHAUSTED` + audit event; the pending count renders
   as a pressure signal on the coordinator surface.
3. **`CoordinatorAgent`:** system-prompted chat loop over the gateway (P2-08 lease per turn);
   tool dispatch through `CoordinatorTools` with hard caps (max workers, per-day budget,
   admission check before spawn tool returns). The coordinator's container (if any) gets **no
   worktree mount and no git credentials** — it is chat + tools only.
4. **Role enforcement:** daemon issues distinct credential classes: the coordinator's channel
   token carries role `coordinator` — `RoleInterceptor` denies merge RPCs and human-only RPCs to
   that role (test with a hand-crafted client); worker terminal locking: agents spawned in
   coordinated mode are `Locked` — `Attach` input frames rejected at the interceptor (read-only
   streams still flow); manual-mode agents unlocked.
5. **Kill switch (ordering is binding — SA-1/F4):** step 1 = **freeze the merge queue in-proc,
   instantly** — from receipt, `BeginMerge`/`ConfirmMerge`/spawn return `FAILED_PRECONDITION`;
   step 2 = fan-out yield-all with per-agent timeout → `docker pause`; step 3 = journal snapshot
   (agent list + states + queue) written before returning. Timeouts per RT-D4:
   `min(ceiling, max(5 s, 50×RTT))` with the **absolute ceiling a compile-time-visible constant
   independent of the measured RTT** (a supervisor-influenced RTT can never stretch the stop).
   Audit is best-effort during the kill (freeze-then-audit); the RT-D3 gap marker on recovery is
   P2-15's `killswitch_audit_gap`. Always-visible control at the section-rail foot, every
   section (ControlCenterDesign §0/§5).
6. **Dual mode:** manual-mode spawn (user-initiated) bypasses the coordinator but **not**
   admission/budgets (edge row 3); mode is per-agent, coexisting.
7. **Telemetry:** counters for plans proposed/approved/rejected, workers serialized vs parallel,
   mid-flight conflicts (P2-19 feed when present).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| plan rejected | worker never spawns, no worktree residue |
| kill switch with an agent mid-yield | pause lands after the yield timeout; total < 5 s |
| manual-mode spawn | bypasses coordinator, still admission/budget-checked |
| plan JSON fails schema | typed rejection to the coordinator tool call; nothing persisted as pending |
| approve after daemon restart | pending plans persisted; approval path intact |

---

## 5. Invariants (MUST)

1. Input-lock verified **at the gRPC layer** by test — a hand-crafted client sending input frames
   to a locked agent is rejected.
2. The kill-switch fan-out bound is `min(ceiling, max(5 s, 50×RTT))` — the "< 5 s" figure is the
   local profile of the RTT-scaled formula, and **the absolute ceiling holds regardless of the
   measured RTT** (RT-D4).
3. The merge-queue freeze precedes the yield fan-out; no `BeginMerge` can land in the fan-out
   window after kill-switch receipt (SA-1/F4).
4. The coordinator cannot invoke merge RPCs — interceptor-enforced role, not convention.
5. Workers start only on approved plans; approver identity persisted with the plan and
   **daemon-derived — no client-supplied identity field exists in the proto** (SA-1/F2).
6. Pending-plan count per Coordinator is capped; excess drafts are `RESOURCE_EXHAUSTED` +
   audited (S-8).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `TaskPlan_SchemaCorpus` | valid/invalid fixture matrix → exact error sets |
| 2 | `PlanRejected_NoResidue` | reject → zero worktrees/containers/branches for that task |
| 3 | `Approval_PersistsIdentity` | approve → plan + OS identity + timestamp row; survives restart |
| 4 | `SpawnCap_BudgetRejection` | coordinator exceeding caps → tool error, no spawn |
| 5 | `InputLock_GrpcLayer` | raw gRPC client sends input to locked agent → rejected; read stream still works |
| 6 | `RoleInterceptor_DeniesMergeToCoordinator` | coordinator-token call to merge RPC → `PERMISSION_DENIED` |
| 7 | `KillSwitch_FanOutUnder5s` | 3 scripted agents (one ignoring yield) → all paused/frozen < 5 s (local RTT profile); journal snapshot written; queue frozen |
| 8 | `ScriptedCoordinator_EndToEnd` (`RequiresDocker`) | scripted coordinator: 2 independent tasks → 2 plans → approvals → parallel workers → verified → sequential human merges with a stale re-verify between (the full M7 story) |
| 9 | `KillSwitchBound_HardCeiling_IndependentOfRtt` (**RT-D4, M7 exit — PR-blocking**) | feed an artificially inflated `RttBudget` EWMA (supervisor-influenced RTT) → the effective kill/yield-to-pause deadline is clamped at the fixed absolute ceiling; the RTT spike feeds A3 `Unresponsive` |
| 10 | `KillSwitch_FreezesQueueBeforeFanOut` (**OPS SA-1/F4**) | a `BeginMerge` issued in the fan-out window after `KillSwitch` receipt → rejected `FAILED_PRECONDITION` (ordering asserted on the harness timeline) |
| 11 | `ApproverIdentity_IsDaemonDerived_NotClientField` (**OPS SA-1/F2**) | a hand-crafted client attempting to supply an identity value cannot influence the recorded approver; the recorded identity equals the connection's OS peer credential |
| 12 | `PendingPlanCap_ExcessDraftsRejected` (**S-8**) | N+1th concurrent `PlanPending` draft → `RESOURCE_EXHAUSTED` + audit event; pressure signal reflects N |
| 13 | `KillSwitchDuringAuditOutage_ShouldMarkGapOnRecovery` (**RT-D3, M7.5 exit — lands with P2-15's `IAuditLog`; the kill-side hook ships here**) | kill fired while the audit store is down → kill is NOT blocked; on store recovery a chained `killswitch_audit_gap{killEpochId, observedAt}` appears |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** UI-only input locking; coordinator with a worktree/credentials; spawn before
approval; kill switch that only signals without pausing; **a client-supplied approver-identity
field in any proto** (SA-1/F2); **yield fan-out starting before the queue freeze** (SA-1/F4);
**any safety timeout scaled by RTT without a fixed absolute ceiling** (RT-D4); **a kill switch
that blocks on audit availability** (RT-D3 requires freeze-then-audit + the recovery gap
marker); unbounded `PlanPending` drafts (S-8); role checks by convention.

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~TaskPlan|FullyQualifiedName~PlanApproval|FullyQualifiedName~CoordinatorTool|FullyQualifiedName~InputLock|FullyQualifiedName~KillSwitch"
grep -rn "IsReadOnly" Mainguard.App.Shell/ViewModels/Agents/ | grep -i terminal   # UI read-only may exist, but never alone — check RoleInterceptor coverage
```

---

## 8. Definition of done

- [ ] `TaskPlan` schema + approval service (identity **daemon-derived** per SA-1/F2, persisted, restart-safe; S-8 pending cap + rate limit).
- [ ] Coordinator chat agent with capped tools, no code/worktree/merge power (interceptor-proven).
- [ ] Daemon-side terminal locking; kill switch **freeze-queue-first** (SA-1/F4) with the RT-D4 hard-ceiling bound + snapshot; RT-D3 gap-marker hook; dual-mode spawn rules.
- [ ] Coordinator chat + TaskPlan approval card + rail-foot kill switch per ControlCenterDesign §5/§0.
- [ ] Scripted end-to-end green incl. stale re-verify between merges; guard tests 9–13 green (**M7 does not exit without 9–11**).
- [ ] Test contract satisfied as the **union** of §6 and TI-P2-14.
- [ ] `docs/repo-map/` index updated. One task = one PR linking **P2-14**, base `phase2`.
