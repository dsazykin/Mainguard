# The coordinator contract — what a coordinator may do, and where that is enforced

**Status:** contract of record, decided 2026-07-27 (owner). Written **before** the orchestration work so
the eventual role-lock is a removal rather than a migration.
**Supersedes:** the P2-14 two-phase spawn gate (see §6 — that gate is being replaced, deliberately).

---

## 1. Why this document exists first

The product's end state is: the coordinator receives a user request, spawns and manages workers in the
background, and every change lands in the merge queue for human review. The coordinator is **not** a
general-purpose CLI that happens to also orchestrate — it is *only* an orchestrator.

The ordering trap is that it is currently a normal CLI. If the orchestration loop is built on that,
the coordinator's working style comes to depend on capabilities (reading files, running git, editing
code) that the final design removes — and locking it later becomes a migration that undoes the
behaviour work. So the surface is fixed **now**, the loop is built against exactly this surface, and
the lock is the removal of everything outside it.

## 2. The model

```
user request
   -> coordinator plans the shape of the work
   -> coordinator spawns workers        (free, within caps — no human approval)
   -> each worker inspects the repo and authors ITS OWN TaskPlan
   -> the worker presents that plan and BLOCKS          <- human gate #1
        approved -> proceed
        rejected -> revise against the feedback, re-present, block again
   -> worker does the work in its jail
   -> coordinator proposes readiness    (request_verification)
   -> the DAEMON verifies, in the worker's own jail
   -> on pass, the item enters the merge queue
   -> HUMAN reviews and merges                          <- human gate #2
```

> **This flow is the phase-2/3 model.** In phase 1 the coordinator *is* the working agent, so there is
> no worker to propose readiness on its behalf and `request_verification` has nothing to fire — phase 1
> triggers verification by a human action on the merge-queue entry instead. Who triggers a verification
> in which phase, and the one method an automatic caller must reuse, is recorded in
> [`verification-trigger.md`](verification-trigger.md).

**Plan authorship belongs to the worker, not the coordinator.** The coordinator has no worktree, no
git credentials, and no view of repository contents; a plan it authored would describe work it could
not inspect. The worker has the context, so the plan describes what will actually be done rather than
what was guessed before anyone looked.

### The plan gate is blocking, and rejection is feedback

The worker **does not start work until its plan is approved**. Rejection does not kill the worker —
it returns the rejection to the worker, which revises the plan and presents it again.

This is worth being precise about, because it looks superficially like the P2-14 gate that §6 removes
and it is not the same gate:

| | P2-14 (removed) | this contract |
|---|---|---|
| who authors the plan | the **coordinator** | the **worker** |
| when | before a worker exists | after the worker has inspected the repo |
| based on | what the coordinator guessed | what the code actually looks like |
| rejection means | the worker never starts | revise and re-present |

Same number of human interactions, materially better information, and a rejection now improves the
plan instead of discarding the attempt.

**Two operational consequences, decided 2026-07-27:**

**1. The revise→re-present loop is bounded at 3 rounds.** Reject → revise → reject with no limit burns
budget and wall-clock indefinitely, and a worker that keeps producing plans the human dislikes will do
so forever. After the third rejection the worker **stops and escalates to the human** rather than
looping. `MaxPlanRevisions = 3` lives in `CoordinatorLimits` beside the other daemon-side caps — **not
in a prompt**, for the same reason no other limit lives there: a limit an agent is merely told about
is a suggestion.

**2. A blocked worker DOES count against `MaxActiveWorkers`.** Working this through reversed the
answer sketched in the first draft, and the reversal is the important part:

The tempting fix is "blocked workers are idle, so don't count them." That is wrong, because
**`MaxActiveWorkers` is a resource cap and a blocked worker still holds its jail** — a container, its
tmpfs, its network segment, its worktree. Exempting them lets the coordinator spawn unboundedly many
workers that each consume real resources while doing nothing, which converts a bounded system into an
unbounded one at exactly the moment a human is too busy to approve.

So the cap counts them, and the resulting behaviour is **backpressure, not deadlock**: the coordinator
stops spawning until the human clears plans. That is the plan gate doing its job, one level up.

Two things make the backpressure tolerable, and both are requirements rather than hopes:

- **Plans must arrive fast.** The worker inspects the repo and presents a plan, then blocks — it does
  not do the work first. So a full cap becomes *N plans waiting for review*, not *N half-finished
  jobs*, and reviewing them is one batch of decisions.
- **The stall must be legible.** When the cap is reached because workers await approval, the UI must
  say so — "6 workers waiting on your approval" — rather than the coordinator silently going quiet.
  A silent stall is indistinguishable from a hang, and this codebase has already spent real effort on
  failures that could not explain themselves.

Neither number is load-bearing for security; both are `CoordinatorLimits` fields and can be retuned
once there is real usage data.

### 2.1 Budget applies to BYOK workers, and not to OAuth workers

**Stated plainly because this document previously listed "budget" as an unqualified gate, and that was
not true for every worker.**

A worker's model traffic is metered only when Mainguard holds its credential:

| worker authenticates by | metered? | how |
|---|---|---|
| **BYOK** — an API key the user supplied | **yes** | the key stays daemon-side; the jail gets a `mg_sess_` token and its CLI is pointed at the daemon's model gateway, which charges `BudgetLedger` per request and refuses the agent when it is over cap |
| **interactive OAuth login** — the user signs in inside the terminal | **no** | the CLI holds its own session and talks to the provider directly; there is nothing in the path to meter |

The reason is structural rather than unfinished work: metering happens at a proxy that substitutes the
credential, and an OAuth CLI authenticates *past* any such proxy with a session Mainguard never sees and
cannot price. Confining it would mean breaking the login. See
[`oauth-budgeting.md`](oauth-budgeting.md).

Two consequences worth being explicit about, since both are easy to misread:

- A budget cap configured by the user **does not bound an OAuth worker's spend.** The cap is real, it is
  enforced daemon-side, and it applies to BYOK workers only.
- BYOK metering is an **accounting** control, not a network one. The jail no longer holds a usable
  provider key, so it has nothing to spend against the provider directly — but the model hosts remain
  reachable on the egress allowlist (OAuth workers need them), so the limit is not enforced by blocking
  the route. See the residual-bypass note in `oauth-budgeting.md`.

## 3. The surface — the complete set of coordinator operations

These four already exist as `CoordinatorTools`. The contract is that this list is **exhaustive**.

| tool | purpose | gates applied |
|---|---|---|
| `spawn_worker` | start a worker on a described task | kill switch · worker cap · admission · budget (BYOK only — see §2.1) |
| `get_worker_status` | status of workers it owns | ownership scope |
| `send_worker_prompt` | steer a worker it owns | kill switch · ownership scope |
| `request_verification` | propose a worker's branch for daemon verification | ownership scope |

Anything not on this list is denied. Adding to this list is a deliberate contract change, reviewed as
such — not an implementation detail. So is changing the shape of one of them: `spawn_worker`'s arguments
were changed on **2026-08-29** and that change is recorded as a contract change, in
[`coordinator-phase-3-decisions.md`](coordinator-phase-3-decisions.md) §13.

**`spawn_worker` takes a title AND a task, and they are different things.** The CLI form is

```
mainguard-agent spawn <agent-kind> --title "<short title>" --task <the task ...>
```

The **title is the brief** — the only thing the worker is given before its plan is approved (§3.1
`brief`), and the headline the human reads on the approval card. The **task is withheld** until that
approval. Both are required: a spawn missing either, or whose title *is* its task, is refused
daemon-side rather than defaulted. This is stated in the contract because the alternative was tried and
failed silently — the shim sent no title, the daemon derived one from the task, and "the brief is never
the task" was false everywhere while every test was green.

**`get_worker_status` must be able to say "done".** It is the coordinator's only window onto its own
fan-out, so a status that can only ever describe the jail's liveness makes a coordinator structurally
unable to report completion — which is what it was: the session's state word was written once by the
sandbox attach ("Working") and no merge outcome ever moved it, so a worker whose branch had committed,
verified green and reached `Verified` still reported "Working … actively working", permanently. A branch's
merge state is therefore reported onto its agent as it moves, in `WorkerMergeState`'s own words
(`Verifying`, `Verified`, `StaleVerified`, `AwaitingReview`, `Merged`, `Rejected`, `Discarded`, and back to
`Working`). It is a report, not a second state machine: the merge queue remains the only thing that decides
those words, and jail liveness remains the reconciler's axis.

### 3.1 The WORKER's surface, which is a different exhaustive list

A worker's jail carries `mainguard-plan`, never `mainguard-agent`, and the daemon dispatches on the
endpoint's role — so the two lists are disjoint and neither role can reach the other's operations
(phase 2 §2.7). The worker's list is exhaustive on the same terms, and `AgentIpcRequest.WorkerOps` is the
object the daemon builds its handler table against.

| op | purpose | gates applied |
|---|---|---|
| `brief` | what am I here to plan? | never yields the task prompt |
| `present_plan` | present the plan I authored, then block | one live plan per worker |
| `revise_plan` | re-present after a **rejection**, then block | plan ownership · revision budget |
| `rescope_plan` | widen an **approved** plan, then block | plan ownership · approved plan · one live re-scope · escalation is terminal |
| `await_decision` | re-attach and block on my own plan | plan ownership |
| `commit_work` | record my approved work on my own branch | **approved plan** (`MayWork`) |

`rescope_plan` was added on **2026-08-30** and, like `spawn_worker`'s argument change, it is recorded as
a contract change rather than an implementation detail — in
[`coordinator-phase-3-decisions.md`](coordinator-phase-3-decisions.md) §23.

**An approved scope can be changed, and only by asking.** Live testing found a worker that discovered
mid-task it had to touch a neighbouring file and was refused by both existing ops — `present_plan`
because one live plan per worker means an approved plan blocks a second, `revise_plan` because it acts
only on a rejected one. Each refusal is correct about its own op; together they left a worker trying to
stay legal with two moves, both bad: exceed its approved scope silently, or stop. `rescope_plan` is the
third move. It presents a revised plan against the approval the worker already holds, and a human
decides it exactly as they decided the first.

Four properties of it are contract-level, because each of them is a thing the daemon must keep true
rather than a way the code happens to be arranged:

- **It is not a revision.** `revise_plan` answers a rejection and spends the revision budget;
  `rescope_plan` follows an approval and spends none. A worker whose plan was rejected the maximum number
  of times and *then* approved must still be able to widen it — charging the same budget would leave the
  workers that had the hardest time agreeing a plan with no legal way to change it.
- **The approved plan keeps authorising the worker until the human decides**, and is retired only when
  the wider one is approved. A worker is never suspended for asking: steering, verification and
  `commit_work` keep answering off the scope that was already approved. Blocking it would make the legal
  move more expensive than the silent one.
- **A worker has exactly one approved plan, or none.** `resolveApprovedPlan` hands that plan's scope to
  the flagged-change gate, so this is what makes F6 measure against the *current* authorisation instead
  of a stale or an absent one.
- **Work already done outside the scope is not re-policed here.** The flagged-change gate already puts
  every out-of-scope file in front of a human at verification and blocks the merge until it is
  acknowledged; a re-scope asked late is accepted so that the human hears the reason before they see the
  diff. There is one mechanism, not two.

`commit_work` is the step that makes a worker's work outlive its jail, and it is where the loop used to
end one rung short: a worker finished, stopped on an uncommitted diff, and the worktree was deleted with
the jail. It is gated by the same `MayWork` predicate as steering and verification, because a worker still
at the gate has no authorised work to record.

**The worker names only the message.** Which repository, which worktree and which branch are computed
daemon-side from the endpoint's own identity — the same structure that stops an agent naming a ref at all
(`AgentRefMediator`). It grants no capability a worker lacked: every agent already owns the repository its
worktree is linked off, precisely so that committing stays available to it.

## 4. What the coordinator may never do

Denied **at the daemon**, not by convention:

- **Merge power** — `BeginMerge`, `ConfirmMerge`, `AcknowledgeFlaggedChange`. Acknowledging a flagged
  change is the human review act that unblocks a merge, so it is merge power by another name.
- **Plan approval** — `ApprovePlan`, `RejectPlan`. A coordinator that could approve plans holds the
  gate it is denied at merge.
- **Reading other sessions** — `GetScrollback` serves any agent's daemon-side scrollback ring with no
  ownership scoping.
- **Declaring its own work merge-ready.** `request_verification` *proposes*; the daemon verifies and
  decides. The coordinator cannot enqueue.
- **Anything outside its own jail** — no filesystem, git, or network access to the host, the mirror,
  or another agent's container. (Per-agent network segments already enforce the last of these.)

## 5. Enforcement principle — daemon-side, never a prompt

**A system prompt is not a security boundary.** Telling the coordinator what it may do constrains a
cooperative model and nothing else; an agent that decides otherwise simply does otherwise.

This is not hypothetical here. MG-12 found that role authorization was **dead code that failed open**
— `Resolve` returned Operator for an unrecognised token, so the coordinator restrictions never
executed. The restrictions looked present in the code and enforced nothing. Every item in §4 must
therefore be:

1. enforced at the gRPC/IPC layer, where the daemon serves the call;
2. covered by a test that **fails when the check is removed** — the non-vacuity discipline this
   codebase now applies everywhere, after six separate cases of tests that passed while asserting
   nothing.

`RoleInterceptor` is the existing pattern and already denies the merge and plan-approval RPCs to a
coordinator credential. The rest of §4 extends it.

## 6. What changes from P2-14

P2-14 specified a **two-phase spawn gate**: `spawn_worker` never spawned directly — it drafted a
`TaskPlan` as a pending plan, and a worker started only when a human approved.

That gate is **deliberately removed**, decided by the owner 2026-07-27, and replaced by the §2 model:
the coordinator spawns freely within the caps, and the worker authors and presents its own plan.

Recording this explicitly because it is a **reduction in human oversight**, and the surrounding work
has repeatedly found gates that were assumed to be enforced and were not. This one is being removed
on purpose, with the reasoning written down:

- **The plan gate is not removed — it moves.** A human still approves a plan before any work starts;
  it is now the worker's plan, written after inspecting the repo, instead of the coordinator's guess
  written before a worker existed (§2). Net human interactions are unchanged; the information behind
  each one is better.
- The spawn path is not ungated: kill switch, worker cap (`MaxActiveWorkers`), admission control, and
  budget all still apply, and all are enforced daemon-side — with the budget carrying the **BYOK-only**
  qualification in §2.1. For an OAuth worker the other three gates apply and the budget one does not.
- `ApprovePlan`/`RejectPlan` are **not** deleted — they now act on the *worker's* plan, and rejection
  carries feedback the worker revises against rather than terminating it.

So what P2-14 actually loses is the *pre-spawn* approval — the human no longer gates whether a worker
comes into existence, only what it is about to do. That is the intended trade: the coordinator can
fan out in the background, and nothing touches the repository without a human having read a plan
grounded in the real code.

## 7. Ownership scoping

Every tool that names an agent is scoped to workers the calling coordinator spawned. A coordinator
must not read, steer, stop, or propose verification for another coordinator's workers.

Note the current state honestly: `GetScrollback` is denied to coordinators **wholesale** because
per-agent ownership scoping does not exist yet. Ownership scoping is a prerequisite for this section
and is tracked as its own work item, not assumed.

## 8. The role-lock (final step)

Once the loop runs against this surface, locking the coordinator is:

1. remove every capability outside §3 from the coordinator's runtime;
2. verify §4 is denied **at the daemon** with a failing-when-removed test for each item;
3. confirm the coordinator still completes a full user request using only §3 — which is the real
   proof the surface is sufficient.

Step 3 is the acceptance test for this entire contract. If the coordinator cannot do its job with
only these four tools, the surface is wrong and the contract changes — deliberately, not by quietly
handing it back a capability.
