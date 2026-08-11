# Agent branch confinement — keeping an agent's work where the merge queue can see it

**Status:** IMPLEMENTED 2026-08-06. Found during the owner's live phase-1 testing.
Extends `mg-3-mediated-ref-updates.md`; it does not change any rule that document establishes.

---

## 1. What was actually broken

An agent could move its work off its own branch and strand it, and **nothing noticed**. No warning, no
error, no queue entry — the work simply became invisible.

Measured on the owner's machine, not inferred:

- `WorktreeManager` created the worktree on `agent/<agentId>` correctly
  (`WorktreeManager.cs`, `BranchFor(agentId) => "agent/" + agentId`).
- The agent then ran `git checkout -b add-subtract-function` and committed there:
  - the linked worktree's HEAD was `ref: refs/heads/add-subtract-function`
  - inside the jail, `/workspace` was on `add-subtract-function` at `1a8490c "Add subtract function"`
  - `refs/heads/agent/ef9fe0bd…` was still at `d186bf8`, the owner's initial commit
- `AgentRepo.RefPrefix = "refs/heads/agent/"`, and `AgentRefMediator` carries **only**
  `refs/heads/agent/<id>` into the mirror.

So the work existed, was committed, and could never reach the merge queue. The owner had to be told
where to look in order to discover it.

**The bug is not that other branches are ignored. The bug is that they were ignored silently.** The
system enforced a boundary by pretending nothing had happened, which produces byte-identical
observations to "this agent has not done anything yet": a ref that is present, readable and stale, and
no error anywhere.

---

## 2. Three layers, and why confusing them would be a regression

| # | layer | where | is it a security boundary? |
|---|---|---|---|
| 1 | only `refs/heads/agent/<id>` is mediated into the mirror | daemon (`AgentRefMediator`) | **YES — this is the boundary** |
| 2 | in-jail `reference-transaction` hook refusing foreign `refs/heads/` writes | agent's own repo | **NO — ergonomics only** |
| 3 | drift detected and reported at verification | daemon (`MergeQueueProvisioner`) | no — it is the backstop |

**Layer 1 is unchanged by this work and must stay unchanged.** `AgentRefMediator`'s own remarks explain
why at length: letting an agent name the ref that feeds the merge queue would make git's refspec
semantics the policy, and a single `+` in a refspec silently turns the fast-forward rule off.

**Layer 2 is ERGONOMICS, NOT SECURITY, and nothing about it is a reason to relax layer 1.** The agent
has a shell and full write access to its own repository. All three of these bypasses were *measured*, not
assumed:

- `git -c core.hooksPath=/dev/null checkout -b foo` — succeeds; command-line config wins.
- deleting `hooks/reference-transaction` — the enclosing repository must stay group-writable for git to
  work at all, so the file can be removed.
- re-pointing `core.hooksPath` in the repo config the agent owns.

Anything enforced agent-side is advisory by construction. This project already applies that principle
elsewhere — `MaxPlanRevisions` lives in `CoordinatorLimits` rather than in a prompt. A future reader must
not mistake the hook for the boundary and weaken the daemon-side rule because "we prevent it now".

### Layer 2 has an environmental precondition, and it is not visible in the file (#68)

Writing the hook and setting mode `0755` establishes only the mode **bits**. git decides a hook exists
with `access(path, X_OK)`, which returns `EACCES` on a filesystem mounted `noexec` whatever the bits say;
git then emits a *hint* — `the hook was ignored because it's not set as executable` — and completes the
ref update with **exit 0**. The file is present, its mode is right, and nothing runs it.

This is not hypothetical. It is how the guard shipped: the test suite built its per-agent repositories
under `Path.GetTempPath()`, and when the suite runs inside the jail (`.mainguard/verify` is
`dotnet test Mainguard.slnx`) that is a Docker tmpfs, whose default flags are `nosuid,nodev,noexec`. The
jail's git is 2.39.5 — the `reference-transaction` hook has been supported since 2.28, so the version was
never the problem. **The guard fired on the host and never in the jail, which is the only place agents
run.** One test failed; three more passed vacuously, because they assert that ordinary git is *not*
blocked, which an inert hook satisfies perfectly.

So `InstallHook` returns **ARMED, not written**: `MeasureHookCanRun` runs the hook it just wrote (with a
phase other than `prepared`, which the script's own first line makes a side-effect-free exit 0), catching
both a `noexec` mount and a CRLF `bad interpreter`. When it cannot fire, the reason goes to the
`WorktreeManager` warning sink and the spawn continues — layer 3 still reports the drift — but never
silently. **A control that knows it is inert must not look armed.** The corresponding rule for anything
that tests this layer: assert the hook *fires*, never that its file exists.

---

## 3. Layer 2: what the hook does, and the four exemptions

`reference-transaction` was chosen over any porcelain hook because it fires on the **ref write itself**,
so `checkout -b`, `branch`, `update-ref` and a commit onto a foreign branch are all covered — plumbing as
well as porcelain. It is installed into the agent repository's own `hooks/` directory at spawn.

The rule: in the `prepared` phase, refuse a transaction line when the ref is under `refs/heads/`, is not
`refs/heads/agent/<id>`, is not a deletion, and is not a no-op.

Each exemption was measured to be required. A guard that breaks ordinary git is a guard the next person
deletes:

1. **Only `prepared` decides.** `committed` and `aborted` are reports; a non-zero exit there refuses
   nothing and only produces noise.
2. **Only `refs/heads/` is considered.** `git stash` (`refs/stash`), `git tag`, `git fetch`
   (`refs/remotes/…`) and the pseudo-refs every merge/rebase/cherry-pick writes (`ORIG_HEAD`,
   `AUTO_MERGE`, `REBASE_HEAD`, `CHERRY_PICK_HEAD`) all live outside it. **`HEAD` is deliberately
   excluded**: rebase, merge and `checkout --detach` move it constantly, so refusing HEAD updates would
   break rebase outright while catching nothing — the stranded *commit* is a `refs/heads/` write however
   HEAD got there.
3. **Deletions pass.** `git branch -D old` strands nothing. Refusing it was measured to break
   `git branch -D` for no benefit.
4. **No-op rewrites pass.** `git pack-refs` — which `git gc` runs — re-states every loose ref as a create
   (`0000… -> sha`) followed by a delete of the loose copy. In a jail that *already* carries a stranded
   branch this is indistinguishable from a real branch creation by name and shas alone, so the hook asks
   git what the ref currently resolves to and lets the transaction through when the value is not
   changing. **Without this clause, installing the guard breaks `git pack-refs` and `git gc` in exactly
   the repositories this change ships to fix** — measured, and covered by
   `TheHookSurvivesAnUpgradeOverAJailThatIsALREADYStranded`.

**The daemon is unaffected by its own hook and needs no exemption for it.** Every daemon-side git runs
with `-c core.hooksPath=/dev/null` (`AgentGitCommand.HardeningArgs`, from MG-1), so `worktree add -b`,
the mediated fetch, teardown's `branch -D` and the foreign-ref cleanup keep working with no ordering
constraint. This was measured too, and is asserted by
`TheHookDoesNotObstructTheDaemonsOwnGit`.

**Legitimate workflows verified unaffected** (17 operations, all measured green with the hook live):
commit, `commit --amend`, stash/stash pop, tag, `remote add`, `push origin`, `fetch`, `reset --hard`,
`checkout --detach`, committing on a detached HEAD, `branch -f agent/<id> HEAD`, checkout back,
`pack-refs --all`, `gc --prune=now`, `tag -d`, `branch -D`.

**The one workflow this does remove** is a named scratch branch merged back into `agent/<id>`. The
replacement is stated in the refusal message the agent sees: `git checkout --detach` to experiment, then
`git branch -f agent/<id> HEAD` to submit — both permitted, so isolated experimentation still works.
This was judged an acceptable trade because an agent has exactly one deliverable branch by design; if it
proves to obstruct real work, layer 2 can be dropped and layer 3 alone still closes the reported defect.

---

## 4. Layer 3, and the auto-recovery decision

### Where it runs

`MergeQueueProvisioner.RunVerificationAsync`, immediately after the pre-verification publish. That is
the point the work is proposed as ready, and it is the point at which "the agent produced nothing" and
"the agent produced work the mirror will never see" stop being distinguishable by anything downstream.
Drift is **raised**, not logged: `MergeQueueGrpcService` maps `InvalidOperationException` to
`FAILED_PRECONDITION` carrying the message, so the operator reads the measurement. It is also recorded
to the G-17 audit log as `agent_branch_drift`.

The message names the branch found, the branch expected, both shas, and a recovery — and it states only
what was measured. `merge-base --is-ancestor` is actually run, so the report says "this is a
fast-forward, run `git branch -f …`" or "these have diverged, merge or rebase" rather than guessing; if
either sha could not be read it says no recovery could be established instead of implying one.

### Decision: do NOT auto-recover. Rejected.

The trust argument *for* fast-forwarding `agent/<id>` onto the agent's own HEAD is real and was taken
seriously: same objects, same agent, same repository, and the mediator already carries whatever that ref
points at. The boundary is "never another agent's ref", not "the agent may not choose which of its own
commits to submit" — and indeed the agent can already do exactly this itself with
`git branch -f agent/<id> HEAD`, which the hook permits.

It is rejected for three reasons:

1. **It would replace a silent no-op with a silent yes-op.** The property that made this defect invisible
   is that the system took a decision and said nothing. Auto-recovery preserves that property exactly,
   and the next person to hit a subtler version of it gets the same experience the owner just had.
2. **The daemon has no signal that the branch the agent is standing on is the branch it means to
   submit.** There is no readiness declaration from the agent today — `IWorkerControl.RequestVerificationAsync`
   has no production implementation, and verification is triggered by a human, not by the agent saying
   "done". HEAD is simply wherever the agent last happened to `checkout`; it may be a scratch branch, a
   bisect, or a detached exploration. Adopting it would make a human's "verify" action silently redefine
   what is being verified.
3. **It is not always possible anyway.** When HEAD does not contain `agent/<id>`, a fast-forward is
   refused and the system is back to reporting — so the reporting path has to be good regardless. Given
   that, a second path that sometimes silently succeeds adds a mode without removing one.

The obligation this decision creates is that the error must be actionable in one step, which is why the
message carries the exact command and the ancestry was measured rather than assumed.

---

## 5. Should anything notice earlier than readiness?

Considered and deliberately limited.

- **The hook is the earliest possible notice** — it fires at the instant of drift, inside the jail, and
  tells the agent directly. That answers the question without new machinery.
- **A background watcher was rejected.** `AgentRefWatcher`'s change signal is a snapshot of
  `refs/heads/agent/<id>`, and the whole point of this defect is that that ref does *not* move. Detecting
  drift on the sweep would mean an extra probe per agent per tick for a condition a clear failure at the
  right moment already covers.
- **Agent status was not touched.** A status field would need a UI surface to be worth anything, and the
  queue UI has no verify control yet (being wired separately). Adding an indicator nobody renders would
  be another control that exists and is read by nothing — the failure mode this repository keeps
  producing.

---

## 6. Coverage, and what is NOT covered

Covered, each proven non-vacuous by mutating the fix and watching the test go red:

| test | mutation that turns it red |
|---|---|
| `AgentThatMovedItsWorkOffItsOwnBranch_IsRefusedWithTheMeasurement_NotVerifiedSilently` | remove the check from the verification path → *"No exception was thrown"* |
| `AgentBranchGuardTests` detection set (4) | make `Probe` always report `OnAgentBranch` |
| `AgentBranchGuardTests` hook set (3) | remove the `InstallHook` call from spawn |
| `TheHookSurvivesAnUpgradeOverAJailThatIsALREADYStranded` | drop the pack-refs no-op exemption |
| `TheHookLeavesOrdinaryGitAlone` (5 cases) | drop the `refs/heads/` scoping |
| `TheHarnessItself_PutsAgentRepositoriesWhereHooksCanRun` (#68) | put the test VM root back on unconditional `Path.GetTempPath()` and run the suite in the jail |
| `TheArmingMeasurement_AnswersNo_ForAHookGitWouldSkip` (#68) | make `MeasureHookCanRun` check mode bits instead of running the hook |
| `AGuardThatCannotFire_IsReported_AndDoesNotClaimToBeInstalled` (#68) | let `InstallHook` return `true` on the strength of the write |
| `AnArmedGuard_IsReportedAsInstalled_AndWarnsAboutNothing` (#68) | hard-wire `InstallHook` to "not armed" |
| `MergeQueueProvisioner_IsWiredToCheckWhichBranchAnAgentIsActuallyOn` | drop the argument in `GatewayServiceRegistration` |

**Not covered, stated rather than implied:**

- **Pre-existing jails get no hook.** The hook is installed at worktree creation. Every agent spawned
  before this change — including the one the defect was found in — has no guard rail until it is
  respawned. Those jails are covered by layer 3 only, which is why layer 3 is written to work with no
  hook present at all, and why the provisioner test drives the agent through LibGit2Sharp (which never
  executes hooks) rather than through a git CLI that would hit the guard first.
- **Verification cannot currently be triggered from the product.** `DaemonClient.RunVerificationAsync`
  has no caller and the queue UI has no verify control; that is being wired separately. Layer 3 is
  therefore correct and reachable via the RPC, but its user-visible effect arrives with that work. This
  is stated because "we shipped it" and "a user can reach it" are different claims.
