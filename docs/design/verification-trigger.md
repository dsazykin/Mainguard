# Who triggers a verification, and when

**Status:** decided for phase 1 · automatic triggering deferred to phase 2
**Supersedes nothing.** Read alongside [`coordinator-contract.md`](coordinator-contract.md), whose §2
flow describes the *phase-2/3* model.

## The defect this answers

The verification mechanism was complete and **had no production caller**.

- `MergeQueue.RunVerificationAsync` — the state machine, the jail execution, the immutable record: built.
- `MergeQueueService.RunVerification` — the RPC: built.
- `DaemonClient.RunVerificationAsync` — the client method: **defined, and called from nowhere**.
- The merge-queue rail offered "Merge to Main" and "Override…" — and no way to verify.

So a queued entry reached `not verified yet` and stayed there forever. Every test passed, because every
test called `MergeQueue.RunVerificationAsync` **directly** — reaching past the exact rung that was
missing. This is the same shape as MG-12's role check, `FlaggedChangeGate`, and the AI gateway: fully
built, entirely unreachable.

## The question phase 1 has to answer for itself

`coordinator-contract.md` §2 says *"coordinator proposes readiness (`request_verification`) → the DAEMON
verifies, in the worker's own jail"*. That is correct — **for phase 2/3**, where a coordinator delegates
to workers and "the worker says it is ready" is a meaningful event. Phase 3 (PR #292) is what adds
`request_verification` to the in-jail shim.

**Phase 1's model is different: the coordinator IS the working agent, acting as a normal CLI.** There is
no worker to propose readiness on behalf of, so the phase-2/3 trigger has nothing to fire.

## Options considered

| | option | verdict |
|---|---|---|
| 1 | **A UI action on the queue entry** | **chosen** |
| 2 | Automatic when the agent's ref moves | rejected for phase 1 |
| 3 | A shim op (`request_verification`) | deferred — this is phase 3 |

**(2) Automatic on ref movement** — `AgentRefWatcher` already publishes ref movement, which makes this
tempting. It is wrong here for two independent reasons. It would verify on *every intermediate commit*,
burning jail time on work that is not claimed to be finished; and it hands an agent an **unbounded
verification loop** — push repeatedly, and it runs the repo's whole test suite repeatedly. A trigger an
agent can drive is a trigger an agent can abuse. Ref movement means "something changed", not "this is
ready", and only the second is a reason to verify.

**(3) A shim op** — this is exactly what phase 3 adds, on `AgentIpcProtocol` (which today serves only
`spawn` and `list`). Adding it now would duplicate and conflict with PR #292. It also does not help
phase 1: a shim op lets an *agent* ask, and in phase 1 the agent is the coordinator itself, which the
contract's §4 already denies the right to declare its own work merge-ready.

**(1) A UI action** — the smallest change that unblocks a human today; matches "the human is in the
loop"; and, decisively, **cannot be driven by an agent**. `DaemonClient.RunVerificationAsync` already
existed and needed only a caller and a control.

## What was built

One rung, and a button:

```
Verify button (QueueRailView)
  → QueueEntryViewModel.VerifyCommand          thin: one call, renders the answer
  → IMergeQueueService.RunVerificationAsync     the UI-facing seam  [NEW]
  → DaemonBackedOrchestrator.RunVerificationAsync   thin: resolve repo handle, one RPC  [NEW]
  → DaemonClient.RunVerificationAsync           (existed; had no caller)
  → MergeQueueService.RunVerification RPC       (existed)
  → MergeQueue.RunVerificationAsync             ← ALL the behaviour lives here
```

Everything above the last line is transport. The daemon's `MergeQueue.RunVerificationAsync` owns the
`Verifying` transition, runs the repo's test command **in the agent's own jail** (host execution is a
rejection trigger), writes the immutable `VerificationRecord`, and lands the branch on `Verified` or
back on `Working`. The new state reaches the UI on the ordinary queue stream — the ViewModel mutates no
projection of its own.

### Gates are untouched

Verifying is **not** merging. A passing run moves the branch to `Verified` and stops. The
changed-test-command gate, the flagged-change gate, and (on phase 2/3) the plan gate all still stand
between `Verified` and `Merged`, and `CanMerge` is still what the Merge button is bound to.

## The phase-2 handoff

> **Phase 2's automatic trigger must call `MergeQueue.RunVerificationAsync(agentId, ct)`.**

It is reachable daemon-side with no UI involved: `MergeQueueGrpcService.RunVerification` already resolves
the repo's `MergeQueueContext` from `IMergeQueueRegistry` and calls exactly that method. An in-daemon
caller — the handler for phase 3's `request_verification` shim op, say — resolves the same context and
calls the same method, and thereby gets identical gates, identical jail execution, identical state
transitions and identical failure handling.

**What phase 2 must NOT do is re-implement the sequence beside this one.** That failure mode has already
bitten this repo twice in a week: the plan gate's release logic lived in the IPC handler *and* in the
gate (one policy, two places, the `if` quietly doing the work while the gate's own check was unreachable
from any test), and `FlaggedChangeGate` was constructed in a ViewModel and never in the daemon. If new
orchestration logic is being written inside a ViewModel or a command handler, it is in the wrong layer.

The human trigger does not go away when the automatic one arrives. It stays as the manual override — the
way a human re-verifies a stale branch, or retries after fixing the thing that made a run refuse.
