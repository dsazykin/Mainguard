# Who triggers a verification, and when

**Status:** decided for phase 1 · **phase 2's automatic trigger is now built — see "The phase-2 trigger"
at the end**, which supersedes the handoff note rather than the decisions above it.
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

---

## The phase-2 trigger (built)

`WorkerReadinessTrigger` is that automatic caller. It calls `MergeQueue.RunVerificationAsync(agentId, ct)`
and does nothing else: no gate of its own, no jail execution, no state transition. The handoff obligation
above is met literally — there is one verification, and this is a second way of asking for it.

### What counts as "ready": ref **quiescence**

**The signal is the agent's branch going quiet, not the agent saying it is done.**
`refs/heads/agent/<id>` advancing in the mirror is the one readiness-adjacent fact the daemon *observes*;
`AgentRefWatcher` already computed it once a second and threw it away (`PollOnce`'s return value was
discarded by its own loop), so the change is a new `AgentRefWatcher.Advanced` event and a subscriber.

| | candidate | verdict |
|---|---|---|
| 1 | **the agent's ref going quiet** | **chosen** |
| 2 | the worker declaring done over IPC | rejected — a claim, and phase 3 already owns that slot |
| 3 | the CLI process exiting | rejected — structurally cannot verify |

**(3) CLI exit is not merely a weaker signal; it cannot work.** Verification runs in the worker's own jail
and host execution is a rejection trigger (§3.2), while `AgentContext` teardown stops that container *and*
deletes `agent/<id>` from the mirror. A verification triggered by exit races the destruction of the two
things it needs. Exit also conflates completion with a crash, a budget kill and a human Stop, and would
verify the half-finished branch of every one of them.

**(2) A worker declaring done is a claim, and the slot is taken.** Phase 3's `verify` shim op is the
*coordinator* proposing readiness (contract §3), landing on this same queue method. A worker-side twin built
in phase 2 would be a second declaration path competing with it at merge time — and it would not answer the
thing this change is about, which is that today **nobody, human or agent, is required to remember to ask**.
The security argument for preferring an observed signal is real but secondary: verification cannot merge
anything, so what an agent-driven trigger can actually spend is jail time, which is bounded below.

**(1) Movement means "something changed", not "this is ready" — so the trigger is quiescence, not movement.**
Phase 1 rejected ref movement for two reasons and both were correct: it verifies every intermediate commit,
and it hands an agent an unbounded verification loop. Four things answer them, and phase 2 supplies the
authorization phase 1 did not have:

1. **Quiescence, not movement.** Each observed advance restarts the window
   (`CoordinatorLimits.AutoVerifyQuietSeconds`, default 90). Five commits inside the window are **one**
   verification. Measuring from the *last* advance rather than the first is what makes that true, and is its
   own mutation test.
2. **Once per tip.** A sha already attempted is never attempted again, so N automatic runs cost the agent N
   distinct commits of real work.
3. **A cooldown per worker** (`AutoVerifyCooldownSeconds`, default 600) bounds a grinder committing just
   under the window. It throttles only the automatic path; a human's Verify is never delayed.
4. **`WorkerPlanGate.MayAutoVerify`** — no approved plan, no automatic verification. It is deliberately
   *stricter* than the merge gate's `Allows`, which answers "true" for ids the gate never held so that
   manual-mode agents and external-PR heads are not blocked. Reading that default here would have started
   spending suite runs on every agent in the daemon.

### The tradeoff, stated rather than hidden

**A worker that pauses longer than the quiet period mid-task gets verified early, and the cost is one wasted
suite run.** That is the price of a debounce and no threshold removes it. What it does *not* do is produce a
false record: a `VerificationRecord` says the repo's test command passed against that tip on that main, and
it has never claimed the worker was finished. Human gate #2 still reads the actual diff. Because the quiet
period is a tunable in `CoordinatorLimits` rather than a constant, the failure is adjustable in the direction
real usage shows.

### A refusal must never become a result

This is the interaction with PR #322 (`MalformedVerificationCommandException`), and it is the one place an
automatic trigger could quietly undo someone else's fix. A throw out of `RunVerificationAsync` means the run
was **refused** before it produced a verdict — no live jail, a drifted branch, a missing toolchain, or a
verify command whose shell operators survived tokenisation. The queue already does the only correct thing:
it writes **no** `VerificationRecord` (the `_verifications.Insert` is never reached) and settles the entry
back to `Working`.

The trigger therefore **logs the refusal and stops**. It does not mark the entry failed, and it cannot: it
holds no verification store and touches no queue state. An automatic caller that caught the exception and
recorded `Passed=false` would turn *"we could not run your tests"* into *"your tests failed"* — the exact
defect #322 fixes — and would do it silently, about a run no human asked for.
`ARefusedVerification_RecordsNothing_AndIsNotRetriedOnTheSameTip` keeps that a claim which can fail; a
mutation that records a failed row on the refusal path turns it red. The refusal is also **not retried on
the same tip**, so a malformed verify command costs one attempt rather than one per sweep forever, and the
fix for it (a commit) is what re-arms the branch.

### What it refuses to do

- **Never fights the in-flight guard.** It asks `IsVerificationInFlight` *before* it asks about the merge
  state — a live run means the entry is already `Verifying`, so the other order made the in-flight branch
  unreachable dead code. It also treats the guard's throw as a deferral, because the check is across a lock
  it does not hold.
- **Only fires from `Working` or `StaleVerified`**, the states with a legal edge to `Verifying`.
- **Never creates a queue.** An agent pushing must not provision a repo as a side effect.

### Known limitation, not worked around

**A worker that pushes while its entry sits at `Verified` is not auto-verified**, because the state machine
has no `Verified → Verifying` edge. The human Verify button has exactly the same limitation today. Closing
it means giving `NotifyNewCommits` a production caller so new commits invalidate a verification — a change
to the merge spine's behaviour, which is not a trigger's business and should land on its own with its own
tests. Until it does, a branch can carry a `Verified` state whose tip has moved past the verified bytes.

### Wiring

```
AgentRefWatcher sweep (1 Hz, already running)
  → AgentRefWatcher.Advanced                      [NEW event — the sweep result was discarded before]
  → WorkerReadinessTrigger.NotifyAdvanced          arm/re-arm, restarting the quiet window
  → WorkerReadinessTrigger.PollOnce                debounce · once-per-tip · cooldown · plan gate · state
  → MergeQueue.RunVerificationAsync                ← unchanged; ALL the behaviour still lives here
```

`GatewayServiceRegistration` builds it over the daemon's own `WorktreeManager.RefWatcher`, and
`WorkerReadinessHostedService` exists solely to *resolve* it at boot — a DI singleton nobody asks for is
never constructed, which is the same "registered and not running" shape as MG-10's empty registry.
`WorkerReadinessTriggerWiringTests` asserts both facts from the real composition root.
