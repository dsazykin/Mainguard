# Resuming a stranded merge-queue entry

**Status:** implemented. **Supersedes:** the "Continue / resume — what is honestly possible" section of
[#310](https://github.com/dsazykin/Mainguard/pull/310), which recorded that a resume was not buildable.
**Related:** [`coordinator-contract.md`](coordinator-contract.md) §4, [`verification-trigger.md`](verification-trigger.md),
[`agent-branch-confinement.md`](agent-branch-confinement.md) (MG-3).

## 1. The state this exists for

A queue entry rendered like this, with two controls and no way out:

> **a9755c5be8744715a1036ad…**  Working
> `agent/a9755c5be8744715a1036adcd7f68ec6` · not verified yet
> *"Can't verify — Agent '…' has no live sandbox — verification runs in the worker's own jail, never on the host."*
> `[ Verify ]` `[ Discard ]`

Every word of it is true. The commits are on `agent/<id>`. The jail is gone. Verification runs in the
worker's own sandbox by design (contract §3.2 — host execution is a rejection trigger), so the entry can
never reach `Verified` and can therefore never merge. `Verify` is an enabled button whose only possible
behaviour is that error, and `Discard` throws the work away.

An entry gets here whenever a jail dies without its entry being torn down: a daemon restart, a VM stop, a
crash. (A clean `StopAgent` is different — it deletes the branch too, and that case is genuinely
unrecoverable; §5.)

## 2. Why #310 said this could not be built, and what changed

#310's reasoning was correct at the time: `SpawnAgentRequest` carries **no agent id**, the daemon mints a
fresh one on every spawn, so nothing in the contract could re-attach a jail to an existing entry. A
"Continue" button would have failed every time, and the PR deliberately shipped none.

What changed is a decision, not a discovery. The owner's call: *"resuming should work now as the
coordinator isnt really a coordinator but just an agent."* In phase 1 the coordinator does the work itself,
so a jail bound to an existing entry's id and branch is a coherent thing to spawn — and the contract can
grow the capability, provided it grows as a **human-only, daemon-enforced** one. §6 is why that
qualification is not decoration.

## 3. The three design questions, answered

### 3.1 Adoption, not re-creation

**The entry keeps its identity.** Same agent id, same `agent/<id>` branch, same persisted `MergeQueueRow`,
same origin badge, same verification records. A resume creates nothing in the queue: the daemon refuses
outright for an id its queue does not already hold, and the spawn path's `EnsureEntry` finds the existing
entry and only re-stamps its origin.

Re-creation was rejected on the human's terms, not the implementation's. A new entry superseding the old
would leave two rows for one piece of work, with the stranded one *still stranded* — the problem would have
been duplicated rather than solved — and the new row would carry none of the old one's history.

**No illegal transition is smuggled in.** Resume performs at most one state transition, and it is one the
machine already permits:

| entry state at resume | transition | why |
|---|---|---|
| `Working` | none | already the state a verification starts from |
| `Verified` / `StaleVerified` / `AwaitingReview` | none | see §3.2 |
| `Verifying` **with no run behind it** | `Verifying → Working` | legal in `MergeQueue.Legal`; §3.2 |
| `Verifying` **with a run in flight** | refused | the entry has a jail; the honest answer is "wait" |
| `Merged` / `Rejected` / `Discarded` | refused | terminal; nothing leaves them, by construction |

The `Verifying → Working` walk goes through **`MergeQueue.TryClearStalledVerification`** — the same method
the standalone `ClearStalledVerification` RPC uses — rather than a second implementation beside it. One
implementation of a transition cannot drift from itself.

### 3.2 What happens to work already recorded

**Evidence is never discarded.** `VerificationRecord`s are immutable and live in `IVerificationStore`;
resume does not touch them. A `Verified` entry whose record is still fresh against the current `main@sha`
keeps its state and stays mergeable — that record is a statement about the *branch's bytes* against a
*specific main*, not about a container, and the merge itself runs on the host from the mirror. Resuming
such an entry is a legitimate "I want to do more work here", and the moment the new jail commits,
`NotifyNewCommits` invalidates the verification exactly as it would for any other push.

**No stale claim is resurrected, either.** The one thing a resume *does* retract is a `Verifying` state
with nothing behind it. That state is not evidence; it is a false claim about current activity, produced
because queue state is persisted per transition while the in-flight set is daemon memory. Leaving it
standing would mean the entry goes on reporting a run inside a jail that no longer exists.

Because that retraction is a state change the human did not directly ask for, it is **stated, never
silent**: `ResumeAgentResponse.cleared_stalled_verification` carries it, the success sentence says *"Its
stalled verification was cleared, so it can be verified again"*, and — the case that would otherwise be a
genuine silent side effect — **a refusal that happens after the retraction appends it to the refusal
text**, because a refusal otherwise reads as "nothing happened".

### 3.3 What if the branch is gone too

**Refused, by name.** `AdoptAgentWorktree` requires `refs/heads/agent/<id>` in the repo's mirror and throws
`AgentBranchMissingException` when it is absent; the resume turns that into
`resumed=false, reason="Branch 'agent/<id>' no longer exists in this repository's mirror … discard the
entry instead"`.

The rejected alternative is the dangerous one: `worktree add -b` would happily create a fresh empty branch
under the same name and the resume would report success. The human would get a jail, an entry that looks
recovered, and no commits — with nothing anywhere saying so.

Two more half-works are refused on the same principle:

- **A resume that produces no sandbox** is rolled back and refused. The spawn chain degrades to a
  session-only record for an unprovisioned repo; that is unreachable from here (a live queue implies a
  mirror), but it is *measured* rather than assumed, because "unreachable" is precisely the assumption this
  codebase keeps finding to be false.
- **A failed adoption never runs the ordinary teardown.** `RemoveAgentWorktree` ends in `branch -D`, which
  on this path would delete the only surviving copy of the work being recovered.
  `RemoveAgentWorktreeKeepingBranch` is a separate method (not a flag) precisely because that `branch -D`
  is what makes a teardown final, and its interface default **throws** rather than falling back to the
  branch-deleting removal — the caller swallows the throw, so the worst outcome is residue, never lost
  commits.

## 4. The mechanism

```
Resume  ──▶ AgentService.ResumeAgent (human-only; coordinator-denied at RoleInterceptor)
              │
              ├─ AgentResumeService: all authorization, keyed on (RepoHash, AgentId)
              │    entry exists in THIS repo's queue · not terminal · no live session ·
              │    no outstanding merge lease · no verification in flight
              │    → retract a stale `Verifying` (via TryClearStalledVerification)
              │    → read the entry's ORIGIN off the queue (never default Local)
              │
              └─ AgentSpawnService.SpawnAsync(agentId: <the entry's id>, adoptExistingBranch: true)
                   └─ SandboxAgentLauncher → WorktreeManager.AdoptAgentWorktree
                        1. rescue: publish the dead jail's own repo into the mirror (FF-only)
                        2. require refs/heads/agent/<id>            ← else AgentBranchMissingException
                        3. clear worktree + per-agent repo residue  ← WITHOUT `branch -D`
                        4. clone the agent repo from the mirror (carries the branch at its tip)
                        5. `git worktree add <path> agent/<id>`     ← no `-b`; the whole difference
```

**Why a separate RPC rather than an `agent_id` field on `SpawnAgentRequest`.** Two reasons, both
enforcement rather than taste:

1. `RoleInterceptor` dispatches by **method**. A whole method can go on the coordinator denial list; a
   field inside a shared message cannot be seen there at all.
2. `ResumeAgentRequest` has **no `role` field**, so a resume structurally cannot mint a coordinator or a
   managed session. That is a property by construction, not by validation.

**Step 1 is not housekeeping.** The MG-3 ref watcher publishes on its own clock and the
last-publish-on-teardown only runs for a *clean* stop, so a crashed jail can hold commits in its own
repository that the mirror never saw. Step 4 clones from the mirror and step 3 deletes that repository —
so anything not carried across first would be destroyed by the operation invoked to save it.

**The origin is read and handed back.** `EnsureEntry` overwrites the origin on every call, and the spawn's
default is `Local`. A resume that let it default would silently re-badge an intake'd external pull request,
and the origin is what routes its merge through the host's pull-request API instead of fast-forwarding the
mirrored branch behind the PR's back.

**The package cache is kept.** It belongs to the same agent, and it is the one part of a jail that is
expensive to rebuild.

## 5. `has_live_sandbox` — the fact the surface was missing

`QueueEntry` gains `optional bool has_live_sandbox`. Whether an entry still has a jail is the fact that
decides whether it is workable at all, and only the daemon holds the session table, so no client can derive
it. With it on the wire:

- the stranded row offers **Resume**, and only the stranded row does — offering it for a live agent would
  spend a minute building a jail for the daemon to refuse;
- **Verify is withheld** on a jail-less entry instead of being an enabled button whose only behaviour is
  the error in §1.

It is `optional` for the same reason `AgentResourceReading.cpu_percent` is: proto3 scalars default to
`false`, and a `false` meaning "this daemon does not report liveness" is indistinguishable from one meaning
"this entry's jail is gone" — which would render every entry served by an older daemon as stranded. The
client projection is `bool?`; **unknown changes nothing**.

The fact is answered from `(repoHandle, agentId)`. Answering from the id alone would let another
repository's live `pr-7` report this repository's stranded `pr-7` as healthy — the collision class fixed in
#281, #284, #286 and #292.

## 6. Security

**Adoption is strictly more dangerous than the merge power contract §4 already denies the coordinator.** An
agent that could adopt an arbitrary agent id could take over another agent's entry, write that branch from
inside the adopted jail, and have the daemon verify the result under the original entry's identity. It
subsumes `AcknowledgeFlaggedChange` (already treated as merge power by another name) rather than sitting
beside it.

Enforcement, all daemon-side:

| control | where | proven by |
|---|---|---|
| coordinator credential denied `ResumeAgent` | `RoleInterceptor.CoordinatorDeniedMethods` | `Coordinator_IsDeniedResume_ByTheRoleLayer_AndNothingIsAdopted` (asserts the **role** layer, not the bearer layer) |
| the in-jail IPC surface has no resume op, and no agent id at all | `AgentIpcRequest` op set `{spawn, list}` | `AgentIpcSurface_HasNoResumeOp` (reflection, so it cannot grow one by accident) |
| scoped to `(RepoHash, AgentId)` | queue resolved by handle; session by `AgentSessionKey` | `Resume_IsScopedToTheRepo_SoAnotherReposEntryOfTheSameNameIsNotAdopted` |
| the entry must already exist and be non-terminal | `AgentResumeService` §1 | four refusal tests |
| refused while the id has a live session | `AgentResumeService` §2 | `Resume_RefusesAnEntryThatAlreadyHasALiveAgent` |
| refused inside the `BeginMerge`→`ConfirmMerge` window | `AgentResumeService` §3 | `Resume_RefusesWhileThisEntryHoldsTheReposMergeLease` |
| refused while a verification is genuinely running | `AgentResumeService` §4 | `Resume_RefusesWhileAVerificationIsGenuinelyInFlight` |
| the actor is daemon-derived | `IApproverIdentityResolver`; no actor field on the wire | `ResumeRequest_CarriesNoActorField` |
| audited (`queue_entry_resumed`: repo, agent, by, from/to state, branch, kind) | `AgentResumeService` | the end-to-end Docker case asserts the event's fields |

The kill switch gates it for free: a resume is a spawn, and `AgentSpawnService` refuses spawns while frozen.

**Nothing load-bearing is in the ViewModel.** The rail's Resume command is a thin drive of the RPC, and
because the daemon answers a refused resume with an ordinary successful RPC, "no exception" is not evidence
a jail exists — the adapter turns `resumed=false` into a throw and `MergeActionRunner` turns the throw into
a warning carrying the daemon's reason verbatim.

## 7. What this deliberately does not do

- **It is not the restart-reconcile path.** A run interrupted while its jail is still alive re-drives itself
  (`ResumeAfterRestartAsync`); that is the no-human-needed case and it is being wired separately. This is
  the jail-is-gone case, which needs a human decision because the CLI to run and the choice to spend the
  resources are both theirs.
- **It does not resurrect a discarded entry.** A discard is a decision a human made and recorded; undoing
  it is a different act, and the refusal says so rather than treating "absent from the live queue" as
  "unknown".
- **It does not choose the CLI.** `MergeQueueRow` records no agent kind, so the kind is the human's pick at
  resume time — asked, not guessed.

## 8. Known limits, stated rather than discovered

- **The rail offers Resume on liveness alone; the branch check is the daemon's.** So an entry whose agent
  was stopped *cleanly* — teardown deleted `agent/<id>` with the worktree — can still show a Resume that
  the daemon then refuses with "its commits are gone; discard the entry instead". That refusal is correct
  and informative, and putting branch existence on the wire would mean the client re-deciding a question
  the daemon already owns. It is a wording cost, not a correctness one.
- **`has_live_sandbox` only refreshes when the queue re-pushes.** The stream re-pushes on the queue's own
  `Changed` event, and stopping an agent moves no queue state — so a row that has just lost its jail keeps
  showing the old liveness until some transition happens. It converges; it is not instant. (The spawn path
  is unaffected: `EnsureEntry` fires `Changed` immediately after the sandbox is attached, so a resumed row
  updates at once.)
- **The CLI is asked, not remembered.** `MergeQueueRow` records no agent kind, so a resume runs whichever
  CLI is selected — which may not be the one that produced the branch. Recording the kind on the row would
  fix it and is deliberately out of scope here.

## 9. Design system

The rail's **one accent stays the Review CTA**. Resume is `Button.Secondary` — a recovery action that
out-shouted the merge CTA would move the surface's emphasis onto cleanup. **No new tokens**; the action row
is already a `WrapPanel` (#310), which now carries up to five controls on a rail #306 made resizable down
to a narrow column.
