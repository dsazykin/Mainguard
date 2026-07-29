# P2-10 — Merge Queue + Verification Runs + Stale Invalidation — Implementation Plan

**Task ID:** P2-10 · **Milestone:** M7 · **Priority:** P0 — **the product spine** (lead feature
per the July-2026 viability research).
**Depends on:** P2-09.
**Branch:** implement on `feature/P2-10-merge-queue` off `phase2`; PR targets `phase2`.

> **Verification profile:** Automated (pure state machine + scripted-swarm + Docker) + themed screenshot pass on the queue panel.
> The state machine, stale cascade, RT-D1/RT-D2 guards, forged-verify override, and canary are all deterministic CI tests (guard tests are PR-blocking / M7 exit). The Merge Queue Rail UI needs render-harness PNGs in all five themes + a human visual check against ControlCenterDesign §3; no other human step.
>
> **Source of truth:** §P2-10 of `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md`.
> **Positioning constraint that shapes the design:** GitHub's server-side queue is CI-bound,
> PR-only, GitHub-hosted, agent-blind. This queue works **pre-PR, locally, across N agent
> branches, without CI round-trips, on any host** — and it must also serve external PR entries
> (P2-12), so the queue keys on a **branch**, not on a PTY.

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
| [Master doc](../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md) §P2-10 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/Mainguard_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md) **TI-P2-10** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-10 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |
| **Design decisions (binding)** | [`ControlCenterDesign.md`](../design/ControlCenterDesign.md) §3 -- the queue renders as its state machine on the Merge Queue Rail: per-branch state chips naming the `main@sha` each verification ran against, the stale cascade as a visible re-verification wave (never a silent reorder), the `CanMerge` gate surfaced with its reason vocabulary; empty/loading/error states + badges per §9 |
| **Launch-blocker / hardening gates** | **RT-D1 (crash-mid-merge exactly-once) + RT-D2 (verification-command provenance) are M7 exit criteria owned by this task** (master doc §3.1; [red-team plan](../phase-2/Mainguard_Orchestration_RedTeam_Plan.md) §4) -- the milestone does not exit until `DaemonCrashMidMerge_ShouldRecoverToExactlyOnceOrNone` and `GamedTestCommand_ShouldBeFlaggedBeforeSilentMerge` are green; see the 2026-07-12 additions sections below |

---

## 0. Context — what exists today

P2-09 can yield agents and rebase their worktrees onto main. Nothing decides *when work is safe
to merge*. This task ships the state machine at the center of the product: every agent branch is
verified (project test command, in its own sandbox) against a specific `main@<sha>`; any merge to
main invalidates every other `Verified` branch and auto re-queues it. The human gate stays: no
auto-merge, ever.

### What you can rely on

| Fact | Where |
|---|---|
| Yield + keep-alive rebase (`NotifyMainMoved` re-entry hook promised there) | `Mainguard.Agents/Agents/Orchestrator/KeepAliveRebaser.cs` (P2-09) |
| Sandbox exec (`SandboxEngine.ExecAsync`) for running test commands in the worker's container | P2-07 |
| Daemon SQLite (persisted state; spend ledger pattern) | P2-02/P2-08 |
| Windows-side journaled operations (T-19 `IOperationJournal`) — undoable foreground merge | `Mainguard.Agents/Services/OperationJournal.cs` / `IOperationJournal.cs` |
| `mainguard-vm` remote registered on the Windows repo | P2-06 |
| Typed exceptions; `ExecuteWithRepo` on the Windows side | `Mainguard.Agents/Services/GitServices.cs` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Agents/Orchestrator/MergeQueue.cs` (`IMergeQueue`, state machine, persistence) |
| **Create** | `Mainguard.Agents/Agents/Orchestrator/VerificationRunner.cs` (test cmd in the worker sandbox; log artifact capture) |
| **Create** | `Mainguard.Agents/Agents/Orchestrator/VerificationStore.cs` (immutable records, daemon SQLite) |
| **Create** | `Mainguard.Agents/Services/ForegroundMergeService.cs` + interface (Windows side: fetch + merge + journal) |
| **Edit** | `Mainguard.Server/Services/AgentGrpcService.cs` / new `MergeQueueGrpcService` RPCs (state stream, run verification, can-merge query) + proto additions |
| **Edit** | P2-09 `KeepAliveRebaser` wiring (`NotifyMainMoved` → yield → rebase → re-verify) |
| **Create** | `Mainguard.App.Shell/ViewModels/MergeQueueViewModel.cs` + view (queue panel: states, merge button, override affordance) |
| **Create** | `Mainguard.Tests/MergeQueueStateMachineTests.cs`, `VerificationRunnerTests.cs`, `ForegroundMergeServiceTests.cs`, `Mainguard.Tests/Integration/StaleCascadeTests.cs` |
| **Edit** | `docs/repo-map/` index |

---

## 2. Contract (must exist exactly)

```csharp
// daemon Mainguard.Agents/Agents/Orchestrator/MergeQueue.cs
public enum WorkerMergeState { Working, Verifying, Verified, StaleVerified, AwaitingReview, Merged, Rejected }
public sealed record VerificationRecord(string AgentId, string MainSha, bool Passed, string LogArtifactPath, DateTimeOffset When);
public interface IMergeQueue
{
    WorkerMergeState GetState(string agentId);
    Task<VerificationRecord> RunVerificationAsync(string agentId, CancellationToken ct); // test cmd in the agent's sandbox
    void NotifyMainMoved(string newMainSha);        // marks every Verified worker StaleVerified + auto re-queues
    bool CanMerge(string agentId, out string reason); // false when stale/unverified (settings override, loudly labeled)
}
```

Windows side: `ForegroundMergeService` — "Merge to Main" =
`git fetch <SyncRemote.Name> && git merge agent/<id>` on the Windows repo (human-gated, journaled
via T-19), where `<SyncRemote.Name>` is the value `IAgentEnvironment.ResolveSyncRemote(repoHash)`
returns (ESC B1 decision SC-2, `docs/phase-2/Mainguard_Environment_Substrate_Contract.md`:
`mainguard-vm` on the WSL2 substrate, `mainguard-cloud` on cloud — **never a hardcoded literal**, so
this contract is substrate-agnostic for the P2-25 cloud path). The freshness gate is an **A5
ref-level compare-and-swap** (see invariant 3 and §3.5), not an `index.lock`-scoped read.
Post-merge installs run `--ignore-scripts` wrapped in retry (NTFS `EPERM`/`EBUSY`).

**RT-D2 extension to the record (master doc §3.1, M7 exit):** `VerificationRecord` additionally
persists **the resolved test command text and a SHA-256 of the config file that defined it**
(`ResolvedCommand`, `ConfigHash` columns on the store). A change in either vs the `main`-side
baseline becomes a dedicated must-acknowledge flagged item surfaced through P2-11/P2-35 before
`CanMerge` is true — a branch cannot self-green by rewriting its test to `exit 0`.

---

## 3. Implementation steps

### 3.1 State machine + persistence (step 1)

Transitions (exhaustive; anything else is invalid and throws typed):

```
Working      → Verifying            (RunVerification)
Verifying    → Verified | Working   (pass | fail — failure surfaced, not silently retried)
Verified     → StaleVerified        (NotifyMainMoved)
Verified     → AwaitingReview       (review requested / cockpit opens it)
AwaitingReview → Merged | Rejected  (human decision)
StaleVerified → Verifying           (auto re-queue: yield → keep-alive rebase → re-verify)
Rejected     → (teardown per policy)
any          → Working              (new commits from the agent invalidate)
```

Persist `(agentId, state, lastVerification)` in daemon SQLite inside the same transaction as the
transition — a daemon restart resumes queue state; an interrupted `Verifying` restarts or resumes
the run, **never stuck** (edge row 4).

### 3.2 Verification runs (step 2)

- Test command = the project's configured verification command (per-repo setting; absent →
  typed "no verification command configured"; merge then allowed only via the explicit unverified
  override — edge row 5).
- Run **in the worker's own sandbox** (`SandboxEngine.ExecAsync` in the agent's container,
  cwd = its worktree). Host execution is a rejection trigger.
- **Pass/fail is the daemon-observed exit code (OPS SA-1 / decision D — binding):** the daemon
  itself launches the command via the container runtime (`docker exec`), and the recorded result
  is the exit code **reported by containerd, outside the in-sandbox `AgentSupervisor`'s control
  — NEVER a value reported over the OOB `VerifyResult` frame**. The supervisor is not in the
  TCB; a compromised one could otherwise forge a `Verified` state — the single trust-ending
  forgery. Taking the result from a supervisor frame is a rejection trigger (§7).
- **Honest residual (state it in code comments and the queue panel tooltip):** the test still
  executes against agent-controlled files/deps, so a poisoned test *runner* can exit 0 over
  failing code (RT-D2 flags command-*text* changes, not the execution closure). `Verified` is a
  **quality signal, not a security boundary against an adversarial branch**; the gate against
  hostile code is human diff review (P2-11).
- Record: `main@<sha>` (mirror main at run start) + pass/fail + resolved command + config hash
  (RT-D2) + full log captured to an artifact file under the daemon's artifact dir;
  `VerificationRecord` rows are **immutable** — re-runs insert new rows (invariant 2).

### 3.3 The stale cascade (step 3 — densest tests in the milestone)

`NotifyMainMoved(newMainSha)`:

1. Every `Verified` (and `AwaitingReview`-but-verified) worker flips `StaleVerified`.
2. Each auto re-enters: P2-09 yield → keep-alive rebase onto new main → `RunVerificationAsync`
   → `Verified` (or `Working` on failure/conflict, surfaced).
3. Re-queue ordering FIFO by original verification time; concurrency capped (one verification per
   agent at a time; global cap = admission-controlled).

Callers: `ForegroundMergeService` fires it after every successful human merge (via a daemon RPC);
P2-12 merges fire it too.

### 3.4 Merge gating + override (step 4)

`CanMerge` false when state ≠ `Verified`/fresh (or flagged changes unacknowledged — P2-11 wires
its detector in here later; leave a composable predicate hook
`IMergeGate { bool Allows(agentId, out reason) }`, the queue owning the staleness gate).
The override setting exists but: loud warning label in the UI, journaled on the Windows side,
audit event (`stale_override_used`) emitted (G-17; plain journal row until P2-15 chains it).

### 3.5 `ForegroundMergeService` (Windows side)

- `MergeAgentBranch(repoPath, agentId)`: `git fetch <SyncRemote.Name>` (resolved via
  `IAgentEnvironment.ResolveSyncRemote(repoHash)` — SC-2; default `mainguard-vm` on WSL2, never a
  hardcoded literal) then `git merge agent/<id>` (LibGit2Sharp merge or CLI per the G-7 policy
  split — merge is a read-modify op the existing merge path already implements; **reuse the
  existing merge service surface**, journaled via T-19 so it is undoable).
- **A5 freshness is a ref-level CAS (OPS §6.5, corrected):** the check that
  `VerificationRecord.MainSha == main@sha` and the merge are performed as **one journaled step
  using git's own ref old-OID compare-and-swap on `refs/heads/main`** (e.g. `git merge --ff-only`
  or an explicit expected-old-OID `update-ref`) — **not** an `index.lock`-scoped read-then-merge.
  `index.lock` guards the index, not ref updates (`update-ref`/push/fetch can move `main` without
  it), so only a ref-level CAS closes the TOCTOU. If the CAS loses (main moved between check and
  merge), the merge does not happen, the branch flips `StaleVerified`, and the UI explains.
- Post-merge dependency install (when lockfile present): `--ignore-scripts` **always**, wrapped
  in NTFS retry (`EPERM`/`EBUSY` backoff). The poisoned-`postinstall` canary test asserts scripts
  do not execute.
- On success → daemon `NotifyMainMoved(newSha)`.

### 3.7 RT-D1 — crash-mid-merge exactly-once reconciliation (M7 exit gate)

The foreground merge is a two-step daemon conversation: `BeginMerge` (daemon takes a merge lease
for the repo, freezes conflicting queue actions) → Windows-side journaled merge → `ConfirmMerge`
(idempotency record; lease released; `NotifyMainMoved` fires). A crash between the committed
Windows merge and `ConfirmMerge` must not yield a double-merge or a lost merge:

- **On daemon start, merge-queue reconciliation runs BEFORE admission (P2-08 ordering):** for any
  repo with a merge lease outstanding at crash time, replay the `ForegroundMergeService` T-19
  Windows-side journal; if it shows a committed-but-unrecorded merge, **synthesize the
  `ConfirmMerge` idempotency record from the journal** (and fire `NotifyMainMoved` for the
  recorded post-merge sha) **before** accepting any new `BeginMerge`.
- If the journal shows the merge never committed, release the lease and surface the interrupted
  attempt; the human retries. Outcome is always **exactly once or none** — never twice, never a
  silently half-recorded merge.

### 3.8 RT-D2 — verification-command provenance + gamed-command gate (M7 exit gate)

- `VerificationRecord` persists `ResolvedCommand` (the exact command line after config
  resolution) and `ConfigHash` (SHA-256 of the config file that defined it) — see §2.
- At verification time, resolve the same command **from the `main`-side baseline** (the merge
  target's tree). If the branch changed the command text or its defining config, emit a
  **dedicated must-acknowledge flagged item** (`changed-test-command`) through the P2-11
  `FlaggedChangeDetector` mechanism, wired into `CanMerge` beside the staleness gate (and into
  P2-35's Diff Guard composition when that lands). Optional (per-repo setting): an
  out-of-branch, human-owned **command pin** that overrides branch-side config entirely.

### 3.6 Queue panel (UI)

`MergeQueueViewModel`: rows (agent, state badge, last verification age + main-sha match, merge
button bound to `CanMerge` with reason tooltip, override behind a confirm + warning). States
stream over gRPC. Design tokens/component classes; all five themes.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| A merges while B and C are `Verified` | B, C → `StaleVerified`, auto re-queue, re-verify against new main |
| verification fails after rebase | worker back to `Working` with the failure surfaced, not silently retried |
| merge attempted on stale verification | blocked; override path logged + audited + labeled |
| daemon restart mid-`Verifying` | run restarts or resumes; state never stuck |
| test command absent | typed "no verification command configured"; merge allowed only with the explicit unverified override |

---

## 5. Invariants (MUST)

1. A merge through the UI on a fresh `Verified` state is the **only silent path**; every other
   path warns and records.
2. Verification results are immutable records tied to a `main@<sha>`; re-verification creates a
   new record.
3. The human foreground merge happens on the Windows repo via the existing journaled service
   surface (undoable via T-19). **A5: the freshness check + merge are one journaled ref-level
   compare-and-swap on `refs/heads/main`** — never an `index.lock`-scoped read-then-merge (§3.5).
4. Verification pass/fail comes from the daemon-observed container-runtime exit code, never from
   a supervisor-reported frame (OPS SA-1 / decision D — §3.2).
5. The sync remote name is always the SC-2 resolution (`ResolveSyncRemote`), never a literal.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `StateMachine_ExhaustiveTransitions` | every legal transition; every illegal transition throws typed; property test over random legal sequences never corrupts state |
| 2 | `StaleCascade_TwoVerifiedWorkers` | A merges → B,C `StaleVerified` → re-queued FIFO → re-verified against new sha |
| 3 | `Override_LoggedAuditedLabeled` | stale merge via override → journal row + audit event + `CanMerge` still false (override is a separate path) |
| 4 | `Restart_ResumesQueue` | kill queue mid-`Verifying` (in-proc) → reload from SQLite → run restarted, terminal state reached |
| 5 | `NoTestCommand_TypedAndOverrideOnly` | edge row 5 exactly |
| 6 | `VerificationRecord_Immutable` | re-run inserts; prior row untouched (store API has no update) |
| 7 | `TwoScriptedWorkers_EndToEnd` (`RequiresDocker`) | integration: A merges → B re-verifies → merge button blocked until fresh |
| 8 | `IgnoreScriptsCanary` | fixture package with poisoned `postinstall` → merge + install → script did **not** execute (marker file absent) |
| 9 | `ForegroundMerge_JournaledUndoable` | merge → T-19 journal entry exists → undo restores pre-merge HEAD |
| 10 | `DaemonCrashMidMerge_ShouldRecoverToExactlyOnceOrNone` (**RT-D1, M7 exit — PR-blocking**) | kill the daemon between the committed Windows merge and `ConfirmMerge` → on restart, reconciliation replays the T-19 journal, synthesizes the idempotency record, fires `NotifyMainMoved`, and accepts no new `BeginMerge` first; the crashed-before-commit variant releases the lease with the attempt surfaced |
| 11 | `GamedTestCommand_ShouldBeFlaggedBeforeSilentMerge` (**RT-D2, M7 exit — PR-blocking**) | branch rewrites its test command to `exit 0` → verification records the changed `ResolvedCommand`/`ConfigHash` vs the main-side baseline → dedicated must-acknowledge flagged item → `CanMerge` false until acknowledged |
| 12 | `ForgedVerifyResult_ShouldBeOverriddenByDaemonObservedExit` (**OPS SA-1 / §9 test 14 — PR-blocking on the Docker leg**) | a scripted supervisor reports `VerifyResult{passed:true}` while the daemon-observed `docker exec` exit is non-zero → no `Verified`/mergeable state exists anywhere |
| 13 | `Verification_ShouldRunInWorkerSandbox_NeverHost` | the scripted test command writes a marker file → present in the container filesystem, absent on the host (TI-P2-10.9) |
| 14 | `NoAutoMergePathExists` | API-shape: nothing in `IMergeQueue` or the daemon surface can move a branch to `Merged` without the human foreground call (TI-P2-10.12) |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** **auto-merge of any kind** — the human gate is the product thesis; verification
run outside the worker's sandbox (host execution); **taking the verification pass/fail from a
supervisor-reported `VerifyResult{passed}` frame instead of the daemon-observed container-runtime
exit** (OPS SA-1 — a compromised, non-TCB supervisor would forge `Verified`); a hardcoded sync
remote literal instead of the SC-2 resolution; an `index.lock`-scoped freshness check standing in
for the A5 ref-level CAS; mutable verification records; a merge path skipping the journal.

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~MergeQueue|FullyQualifiedName~Verification|FullyQualifiedName~ForegroundMerge|FullyQualifiedName~StaleCascade"
dotnet test   # full suite — this task touches GitServices-adjacent surfaces (global PR rule 3)
grep -rn "install" Mainguard.Agents/Services/ForegroundMergeService.cs | grep -v "ignore-scripts"  # every install guarded
```

---

## 8. Definition of done

- [ ] State machine exact enum, persisted transitions, restart-resume.
- [ ] Sandbox-executed verification, pass/fail = **daemon-observed `docker exec` exit** (OPS SA-1/D), with immutable `main@<sha>` records + `ResolvedCommand`/`ConfigHash` provenance (RT-D2) + log artifacts.
- [ ] Stale cascade: `NotifyMainMoved` → re-queue → re-verify, integration-proven with two workers.
- [ ] Human-gated `ForegroundMergeService` (SC-2-resolved sync remote, A5 ref-level CAS, journaled, `--ignore-scripts` + NTFS retry, canary green); override loud + recorded.
- [ ] RT-D1 `BeginMerge`/`ConfirmMerge` lease + boot journal-replay reconciliation, ordered before admission; guard tests 10–12 green (**M7 does not exit without them**).
- [ ] Queue panel streaming states per ControlCenterDesign §3 (state chips naming `main@sha`, visible re-verification wave, `CanMerge` reason vocabulary, §9 badges); composable merge-gate hook for P2-11.
- [ ] Test contract satisfied as the **union** of §6 and TI-P2-10.
- [ ] `docs/repo-map/` index updated. One task = one PR linking **P2-10**, base `phase2`.
