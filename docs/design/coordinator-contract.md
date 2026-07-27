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
   -> the worker presents that plan     (visible immediately; rejection stops the worker)
   -> worker does the work in its jail
   -> coordinator proposes readiness    (request_verification)
   -> the DAEMON verifies, in the worker's own jail
   -> on pass, the item enters the merge queue
   -> HUMAN reviews and merges          <- this is the gate that matters
```

**Plan authorship belongs to the worker, not the coordinator.** The coordinator has no worktree, no
git credentials, and no view of repository contents; a plan it authored would describe work it could
not inspect. The worker has the context, so the plan describes what will actually be done rather than
what was guessed before anyone looked.

**ASSUMPTION, flagged for correction:** the worker's plan is *presented*, not *blocking* — the worker
proceeds unless a human rejects it, and rejection stops the worker. Making it blocking would put the
human back in the loop once per worker, which is what freeing the spawn was meant to avoid. One line
to change here if that is wrong.

## 3. The surface — the complete set of coordinator operations

These four already exist as `CoordinatorTools`. The contract is that this list is **exhaustive**.

| tool | purpose | gates applied |
|---|---|---|
| `spawn_worker` | start a worker on a described task | kill switch · worker cap · admission · budget |
| `get_worker_status` | status of workers it owns | ownership scope |
| `send_worker_prompt` | steer a worker it owns | kill switch · ownership scope |
| `request_verification` | propose a worker's branch for daemon verification | ownership scope |

Anything not on this list is denied. Adding to this list is a deliberate contract change, reviewed as
such — not an implementation detail.

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

- Human oversight moves to where the risk concentrates — the merge queue, where changes are reviewed
  before they reach the user's real repository.
- The spawn path is not ungated: kill switch, worker cap (`MaxActiveWorkers`), admission control, and
  budget all still apply, and all are enforced daemon-side.
- `ApprovePlan`/`RejectPlan` are **not** deleted — they now act on the *worker's* plan.

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
