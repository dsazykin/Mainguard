# Coordinator orchestration, phase 2 — implementation decisions

**Status:** implementation record for the work that inverts plan authorship and makes the plan gate
blocking. The binding document is [`coordinator-contract.md`](coordinator-contract.md); this file records
the decisions the contract left to the implementation, and **why the option taken was chosen over the
alternative**, so a later change has to argue with the reasoning rather than rediscover it.

Phase 1 made the merge queue actually merge, with the coordinator acting as a normal CLI. Phase 3 locks
the coordinator to the four tools in contract §3. This is the middle step.

---

## 1. What moved

| | before (P2-14) | after (this change) |
|---|---|---|
| who authors the plan | the coordinator, before any worker existed | the **worker**, after inspecting the repo |
| `spawn_worker` | drafted a plan and spawned nothing | **spawns**, within the caps, no human approval |
| the human gate | approve the plan → a worker starts | approve the worker's plan → that worker starts |
| rejection | the attempt is discarded | **feedback**; the worker revises and re-presents |
| a blocked worker | did not exist (nothing was spawned) | **counts against `MaxActiveWorkers`** |

`ApprovePlan` / `RejectPlan` were not deleted. They act on the worker's plan now, and `RejectPlan`'s
`reason` became load-bearing: it is delivered to the worker.

---

## 2. Decisions

### 2.1 `MaxPlanRevisions = 3` means three revisions, and the **fourth rejection** escalates

The contract states the value and says "after the third rejection the worker stops", which admits two
readings — three *revisions*, or three *rejections* (i.e. two revisions). The arithmetic implemented is:

```
present (revision 0) → reject #1 → revise 1 → reject #2 → revise 2 → reject #3 → revise 3 → reject #4 → ESCALATE
```

**Why this reading.** It is the maximal one, and the contract's own principle argues for it: *rejection is
feedback, not death*. A budget named "max plan revisions" that permits three revisions is also the reading
the name supports. It is pinned in code (`CoordinatorLimits.MaxPlanRevisions`), in prose on that field, and
by `WorkerAuthoredPlanTests.ThreeRejectionsGiveThreeRevisions_AndTheFourthRejectionEscalates` — which has a
paired negative (`TheBudgetIsConfigurable_AndAHigherBudgetGrantsMoreRounds`) proving the escalation comes
from the limit and not from something incidental.

The counting is one field on the plan (`RevisionCount`, incremented on **revise**, not on reject), so
"revision 2 of 3" on the approval card and the daemon's escalation decision read the same number.

### 2.2 The gate is not the blocking call — it is the daemon holding the task

The obvious implementation of "the worker blocks" is a blocking call the worker makes. That is present
(`AwaitDecisionAsync`, and `present_plan` over the worker's IPC socket does not return until a human
decides). **It is not the control**, because a blocking call an agent can decline to make is a convention.
MG-12 is the standing reason for that framing: role authorization once looked present in the source and
was dead code that failed open.

So the enforceable version is: **the daemon never gives the worker its task.**

1. A coordinator's `spawn_worker` sends the task prompt to the *daemon*. `WorkerPlanGate.Hold` records it
   before the jail is created, and the worker's jail is started without it. The worker gets a *brief* (the
   title — enough to know what part of the repo to inspect), never the task.
2. `TryReleaseTask` yields the prompt only when `PlanApprovalService.HasApprovedPlan(worker)` is true.
   There is no override parameter; an override is how a gate becomes decorative.
3. `send_worker_prompt` is refused for a worker at the gate, so the coordinator cannot hand the task over
   out of band.
4. `request_verification` is refused for a worker with no approved plan.
5. `WorkerPlanGate` is an `IMergeGate`, ANDed into every repo's merge queue. A branch whose worker never
   had a plan approved **cannot merge, even if it verified green**. This is the backstop that still holds
   if a worker somehow did work it was not cleared for.
6. **(added by G1, phase 3)** The same gate decides whether the worker gets a **merge-queue row** at all.
   `MergeQueueProvisioner.EnsureEntry` asks it, and a worker still at the gate is remembered rather than
   listed; `AdmitDeferredEntries` creates the row when the plan is approved. A queue row is a claim on a
   human's attention that arrives carrying Verify, so it is authorisation-shaped and now asks.

Point 5 is why the enforcement is credible without claiming the impossible. Nothing can stop a jailed
process from editing files in its own worktree; what the daemon controls is whether that work is ever
*authorised*, *steerable*, *verifiable*, *listed* or *mergeable*.

### What the gate does NOT govern: **publication**

Stated plainly, because the list above reads like a perimeter and is not one. `AgentRefMediator` — which
carries `refs/heads/agent/<id>` from the worker's own repository into the daemon's mirror — **contains no
reference to the plan gate at all**, and that is deliberate rather than an omission:

- A branch existing is not the harm. **F1** (phase 3 §12.1) established the opposite requirement — stopping
  a worker must NOT destroy the commits it made — so a gate on publication would be a mechanism for
  throwing work away in order to tidy up a list.
- The gate's job is to decide what is *authorised*, and nothing downstream of publication can act on an
  unauthorised branch anyway: it is not listed (point 6), it cannot be verified on the daemon's own
  initiative (`MayAutoVerify`), and it cannot merge (point 5).

So an unapproved worker's branch **does** exist in the mirror, and always did. What G1 found was that it
also got a queue row, which is the thing a human is asked to act on; that is what point 6 closes. Anyone
reading this section for the perimeter should read it as: **task delivery, steering, verification, the
queue row, and the merge — not publication.**

**Deliberately not done:** the gate does not answer for agents it never held. Manual-mode agents and
external-PR heads are not coordinator-spawned and are not governed by the plan gate; making the gate
default-deny for unknown ids would have silently blocked every non-coordinated branch in the queue.
`WorkerPlanGateTests.AsAMergeGate_AgentsTheGateNeverHeldAreNotBlocked` pins that.

### 2.3 The task is withheld *before* the jail exists, not after the spawn returns

The first cut called `Hold` in the shim handler after `SpawnAsync` returned. That leaves a window in which
the jail is running and the gate is not yet armed. `SpawnAsync` now takes the held task and arms the gate
immediately after the session id is minted and before the IPC endpoint or container is created. A gate
that is armed *most* of the time is not a gate.

### 2.4 The S-8 per-coordinator drafting caps were removed, not retuned

`MaxPendingPerCoordinator` (5) and the drafting rate limit existed to stop a *coordinator* queueing more
approvals than a human would read. The coordinator no longer authors plans, so:

- keeping the cap as-is would **deadlock**: `MaxActiveWorkers` is 6, so the 6th worker admitted by the
  worker cap could never present a plan, and therefore could never finish. That is not backpressure, it is
  a stuck system.
- the volume the cap was protecting against is now bounded by `MaxActiveWorkers` itself, which is the real
  resource cap.

What replaces it is a per-worker invariant: **one live plan per worker**, enforced daemon-side in
`PlanApprovalService.Present`. Combined with the revision budget, a worker cannot put more than
`1 + MaxPlanRevisions` plans in front of a human, ever.

The old `Draft` entry point was **removed rather than left dormant**. A dormant coordinator-authoring path
is exactly the sort of thing that gets re-wired by accident, and this codebase has a documented history of
controls that looked present and were not.

### 2.5 A blocked worker counts against the cap — and the refusal says why

This is the contract's decision, not ours; what was ours is the wording. The pre-existing refusal read
*"Worker cap reached — 6/6 managed workers running. Let one finish before spawning another."* That is
**wrong and unactionable** when the workers are not busy but waiting on the human reading the message.
The refusal now names the cause and the count:

> Worker cap reached — 6/6 managed workers running. 6 workers are waiting on human plan approval — no new
> workers until those plans are cleared.

with a paired negative test (`CapRefusalWithNoBlockedWorkers_KeepsTheOrdinaryWording`) so the new wording
cannot be emitted when nothing is actually blocked. An error that asserts a cause it never checked is a
failure mode this repo keeps paying for.

### 2.6 Backpressure is carried from the daemon, not re-derived in the client

`PlanUpdate` gained `blocked_worker_count`, `escalated_worker_count`, `active_worker_count`,
`max_active_workers`, `max_plan_revisions` and a rendered `backpressure_signal`. The UI renders the
daemon's string.

The alternative — the client counting pending cards — would produce a number that can disagree with the
number that actually refuses the coordinator. A surface that disagrees with its gate is how a human ends
up believing they have unblocked something that is still blocked (the same class of bug as the flagged-item
acknowledgment that cleared a store no merge consulted).

### 2.7 The worker's IPC channel is a second, role-scoped endpoint

Before phase 2 only a coordinator got an IPC endpoint ("workers have no spawn channel — least privilege").
Workers now get one, carrying **only** `mainguard-plan`. The role is fixed on the endpoint at creation, so
dispatch does not depend on anything in the request:

- a worker sending `{"op":"spawn"}` gets `unknown op`, not a worker;
- a coordinator sending `{"op":"present_plan"}` gets `unknown op`, not a plan.

Plan ownership is checked daemon-side as well: a worker asking about another worker's plan is answered
`no plan '<id>'` — the same answer as a plan that does not exist, so the channel is not an existence oracle
for other agents' work.

### 2.8 `CoordinatorIpcServer` → `AgentIpcServer`

The class now serves both roles. Renaming it is churn; leaving it named for one of its two clients is the
kind of drift the repo-map rule exists to prevent, and it would mislead the next reader about which agents
have a channel. Renamed, with the repo map updated in the same change.

### 2.9 No new design tokens

Every new UI state is built from tokens that already exist in all five themes (`WarningBrush`,
`DangerBrush`, `TextMuted`, `TextPrimary`, `SurfaceCard`, `BorderHairline`, `AccentBrush`). Adding a token
would mean editing five theme files to express something the palette already says; the backpressure banner
is a warning and the escalation card is a stop, and the design system already has words for both.

---

## 3. Defects found and fixed while implementing

- **The worker's task prompt was silently dropped.** `mainguard-agent spawn <kind> <prompt>` parsed
  `taskPrompt` off the wire and then never passed it to `SpawnAsync` — a coordinator-spawned worker was
  started with no task at all. The field now has a home (the plan gate) and a gate.
- **A failed spawn could leave a held task behind.** The rollback path tore down the endpoint and the
  terminal lock but would have left the gate holding a task for an id that names nothing, which would keep
  counting toward the backpressure the operator is asked to act on. `Forget` is now part of both the
  rollback and the stop path.
- **`RejectPlanResponse` could not distinguish rejection from escalation.** It returned a bare
  `rejected: true`, so a UI could not tell "the worker will revise" from "the worker has stopped". It now
  carries `escalated` and `revisions_remaining`.
- **Two daemon tests shared the literal worker id `worker-1`, and the new invariant found them.**
  `TestDataRootIsolation` gives the whole `Mainguard.Server.Tests` assembly **one** data root, so every
  `DaemonFixture` in the suite rehydrates from the same restart-safe plan store. Two unrelated tests both
  presenting for `worker-1` therefore tripped one-live-plan-per-worker — correct behaviour, in the wrong
  place. Both now mint a unique id, and both assert the presentation succeeded with the refusal message
  attached, so the next collision says what it was rather than failing on a bare `Assert.True`.
- **The release rule was written in two places, and mutation testing caught it.** The first cut of the
  daemon's plan-channel handler read `if (decision.Approved) { release }` and only *then* consulted
  `WorkerPlanGate.TryReleaseTask`. Breaking the gate's own approval check changed nothing observable
  through that channel — the `if` was quietly doing the work, and the gate's check was unreachable from
  any daemon-side test. Two copies of a policy is exactly how one of them becomes decorative (MG-12), so
  the handler now asks the gate on **every** decision and the gate is the single authority. The mutation
  that used to pass now fails, which is the point.

---

## 3a. A defect found and deliberately NOT fixed here — read this one

**`FlaggedChangeGate` (P2-11) has no production wiring, so the plan's `Scope` is compared against
nothing.**

The contract calls `Scope` "the load-bearing field (SA-1/F6 compares diffs against it)", and phase 2 is
the change that finally makes that field describe real, inspected code. It is therefore worth stating
plainly that **nothing in the running daemon performs that comparison**:

- `FlaggedChangeDetector.DetectFlagged(diff, approvedPlan, managed)` — which produces the
  `out-of-approved-scope` item — has exactly one non-test caller: `ReviewCockpitViewModel`, and only on
  its **local** composition branch. The shipped app always supplies `live`, which takes the other branch.
- `FlaggedChangeGate` is constructed in exactly one non-test place: that same dead branch. It is never
  registered in `DaemonHost`, never passed to `MergeQueue`'s `gates`, and never consulted by `CanMerge`.
- The daemon's `StreamQueue` fills `FlaggedItems` from `ChangedTestCommandGate` alone
  (`MergeQueueGrpcService.FlaggedItemsFor`), so the risk-hunk, lockfile and out-of-scope items never
  reach a human either.

So a managed worker can today touch any file at all, and the approved plan's scope neither flags nor
blocks it. This is the same shape as MG-12 and MG-10: code that looks like a control and is not reached.

**Why it is not fixed in this change.** Wiring it is not a one-liner — it needs the merge diff computed
daemon-side at verification time, the approved plan resolved per agent, stable item ids for the existing
acknowledgment path, and the gate ANDed into the queue. That is a **new blocking gate on the merge
spine**: every branch touching a CI workflow, a git hook or an executable config would begin requiring an
acknowledgment it does not require today. Landing that unreviewed, inside a PR about plan authorship,
would be a behaviour change larger than the one the PR is named for — and the spine is the thing this
project has already had to repair once.

It should be the next change, on its own, with its own tests. The phase-2 work makes it *more* worth
doing, not less: for the first time the scope being compared against was written by something that had
read the code.

**Resolved — and which half resolved when matters.** The gate half landed on `phase2` before this branch
merged with it: `FlaggedChangeGate` is registered, ANDed into the queue's `gates`, and armed at
verification time from the committed trees (`MergeQueueProvisioner.ArmFlaggedChangeReview`), with MG-40's
default-DENY for an id whose review never ran. What that half could not supply was the *comparison* —
`resolveApprovedPlan` stayed deliberately unwired, because the daemon had no agent→approved-plan binding
and a guessed one would have compared diffs against the wrong scope and reported that as enforcement.

Phase 2 is that binding: a plan is keyed by the **worker's own agent id**, which is the same id the plan
gate holds and the same id the merge queue tracks the branch under. The composition root therefore now
passes `resolveApprovedPlan` (approved plans only — a pending or rejected plan's scope has authorised
nothing; an agent with no approved plan still resolves null and skips the comparison),
`CompositionRootResolutionTests` pins it in the wired set with that reason, and the arm is exercised
without spawning an agent through the queue seeder's `with_plan`/`scope` specs
(`docs/design/queue-seeding.md` §9). The behaviour change is bounded to plan-gated workers: an unmanaged
branch is classified exactly as it was.

---

## 4. What phase 3 still has to do

Unchanged by this work, and listed so it is not assumed done:

- Remove every capability outside contract §3 from the coordinator's runtime (it is still a general CLI).
- Ownership scoping for `get_worker_status` / `send_worker_prompt` / `request_verification` across
  *coordinators* — `CoordinatorTools` scopes to the workers `IWorkerControl` reports, and the shim's
  `list` op is parent-scoped, but there is no cross-coordinator ownership model yet (contract §7 says so).
- `GetScrollback` remains denied to coordinators wholesale for the same reason.
