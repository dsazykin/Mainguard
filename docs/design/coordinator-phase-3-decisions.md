# Coordinator orchestration, phase 3 — the role lock

**Status:** implementation record for the change that locks the coordinator to the four tools of
[`coordinator-contract.md`](coordinator-contract.md) §3. The contract is the binding document; this file
records what the implementation found, what it decided, and **why the option taken was chosen over the
alternative** — so a later change has to argue with the reasoning rather than rediscover it.

Phase 1 made the merge queue actually merge. Phase 2 inverted plan authorship and made the plan gate
blocking. This is the last step: contract §8.

---

## 1. What phase 3 found before it changed anything

Two findings reshaped the work. Both are the contract's §5 failure mode — *a control that looks present
and is not* — and neither was known when the contract was written.

### 1.1 The shipped coordinator did not have the four tools. It had two — and a full CLI.

`CoordinatorTools` (the class named after contract §3, with all four tools and every cap) **is not wired
to the shipped coordinator at all.** It is the in-process surface used by the scripted test loop. Its own
class comment says so; nothing outside tests constructs it.

The coordinator that actually ships is a vendor CLI in a jail, and its only control channel is the
`mainguard-agent` IPC shim, which served exactly two operations:

| contract §3 tool | before phase 3 |
|---|---|
| `spawn_worker` | `mainguard-agent spawn` ✔ |
| `get_worker_status` | `mainguard-agent list` — partial (no plan-gate reason) |
| `send_worker_prompt` | **absent** |
| `request_verification` | **absent** |

So the contract's surface was simultaneously **too small** (two of four tools did not exist) and
**far too large** (see 1.2). Neither is what §3 describes.

### 1.2 The coordinator's jail was byte-for-byte a worker's

`SandboxAgentLauncher.TryLaunchAsync` never saw the role. A coordinator got:

- a **read-write worktree** at `/workspace` with the repository checked out,
- a **read-write per-agent git directory**,
- the shared **mirror** mount,
- a **read-write package cache**,
- and the same CLI, image, toolchain and egress allowlist as a worker.

Contract §2 says, flatly: *"The coordinator has no worktree, no git credentials and no view of repository
contents."* That was true of the prose and of nothing else.

**And the prompt that claimed otherwise is never delivered.** `CoordinatorAgent.SystemPrompt` — "you never
write code, touch a worktree, or merge" — is consumed only by the in-process loop, which the daemon
registers with no reply engine. The shipped coordinator receives **no system prompt at all**. The only
text it ever sees describing its boundary is the shim's own `--help`.

That is §5 stated more sharply than the contract itself put it: the boundary was not merely a prompt
rather than a gate — it was a prompt nobody sent.

---

## 2. Decisions

### 2.1 The four tools are wired at the IPC surface, and the surface is an allow-list

`send_worker_prompt` and `request_verification` are now real ops (`prompt`, `verify`), and
`get_worker_status` gained a single-worker form (`status`; `list` is kept as an alias so an existing
coordinator transcript does not break). All four go through the daemon, with the caps, the plan gate and
ownership scoping applied server-side.

**Why not wire `CoordinatorTools` instead?** It would mean the shipped path and the tested path are two
implementations of one policy — and "two copies of a policy is exactly how one of them becomes decorative"
is the phase-2 lesson (decisions §3, the release rule written in two places). The daemon handler is the
one the jail can actually reach, so that is where the policy lives. `CoordinatorTools` stays as the
in-process surface for the scripted loop and is no longer the only place the four tools exist.

**The deny is an allow-list, not a `default:` that happens to be reachable.**
`AgentIpcRequest.CoordinatorOps` is the set; the handler serves an op only because a `case` names it.
`CoordinatorRoleLockTests.EveryOperationOutsideTheContractSurface_IsRefused` walks 18 op names — every §4
capability by its RPC spelling, every worker plan-gate op, and assorted others — and asserts each is
refused. Removing the deny turns **all 18 red** (mutation M1, §3 below).

### 2.2 Ownership is `(RepoHash, AgentId)` — and a stranger's worker is answered as a missing one

Contract §7. Every op that names an agent resolves it through `OwnedWorker`, which requires the target to
be a live child of the calling coordinator **in the calling coordinator's own repo**.

**Why the repo is in the key.** A bare agent id is unique only *within* a repo. The external-PR intake
names its sessions `pr-<n>`, and two subscribed repositories that each have a pull request #7 both want
`pr-7`. This exact collision has been fixed three times (#281 `AgentSessionStore`, #284 `SwarmReconciler`,
#286 `TerminalSessionManager`), and the pre-existing `list` op reproduced it a fourth time: it filtered on
`ParentAgentId` alone, and its rows carried no repo, so two coordinators sharing an id in different repos
would have seen each other's workers with no way to tell them apart.

**Why "not yours" and "no such worker" return the same string.** An ownership check that distinguishes the
two is an existence oracle: a coordinator could enumerate every other coordinator's fan-out by probing ids
and reading which error came back. `AStrangersWorker_IsRefusedIdenticallyToAWorkerThatDoesNotExist` pins
the refusals as byte-identical modulo the id.

### 2.3 A coordinator's jail carries no path into the repository — enforced fail-closed in the pure builder

`ContainerSpecBuilder` gained `WithoutRepositoryAccess`. For that role the worktree, mirror, per-agent git
dir and package cache are **all absent**, leaving only two read-only mounts: the adapters root (the CLI it
runs) and the IPC dir (its four tools). The jail therefore has **no writable bind mount at all**; combined
with the read-only rootfs its only writable storage is tmpfs, which dies with the container.

`/workspace` becomes an empty agent-owned tmpfs, because `WorkingDir` is still `/workspace` and
`docker exec -w /workspace` needs the path to exist.

**Why the builder refuses rather than trusting the caller.** The launcher already passes `null` for every
repository path. The builder *also* throws if one is supplied alongside the flag. That redundancy is
deliberate and is the whole lesson of MG-12: a future caller that starts passing a worktree for a
coordinator gets a typed spawn failure instead of silently regaining the capability the lock removed.
The alternative — one check, in the caller — is the shape that has already failed here twice.

**The worktree is not created, not merely unmounted.** Provisioning a worktree and a branch the jail can
never see would leave residue per coordinator, and a later change that started mounting it would find the
content already there.

**A coordinator also never gets a merge-queue row.** It has no `agent/<id>` branch, so a row would name a
branch that does not exist — and §4 denies it "declaring its own work merge-ready". A coordinator holding
a queue row is precisely the surface that denial is about.

### 2.4 `send_worker_prompt` deliberately does not honour the terminal input lock

`TerminalLockRegistry` severs **human** keyboard input to a managed worker (P2-14) so steering goes through
the sanctioned channel instead of a human typing into an agent's session. `send_worker_prompt` **is** that
sanctioned channel. Honouring the lock there would make the tool permanently impossible — every worker a
coordinator owns is Managed and therefore locked, so a contract entry could never once succeed.

The checks that make it safe are applied before delivery, by the daemon: the target must be owned, and it
must already hold an approved plan. `AgentCliBinder.TrySendPromptAsync` is `internal` so no other transport
can reach the delivery mechanism without passing them.

### 2.5 `GetScrollback` stays denied wholesale — and this is the one place the contract argues with itself

Contract §7 records that `GetScrollback` is denied wholesale "because per-agent ownership scoping does not
exist yet", implying that building the scoping would let it be scoped instead of denied. Phase 3 built the
scoping. It still stays denied, for two reasons that outrank that implication:

1. **§3 is exhaustive and does not contain it.** Serving scrollback — scoped or not — would be a fifth
   tool, and §3 says adding one "is a deliberate contract change, reviewed as such". §4 independently
   denies it under "Reading other sessions".
2. **The coordinator has no route to it anyway.** `GetScrollback` is a gRPC RPC, and the in-jail
   coordinator has no gRPC channel — only the IPC socket. Adding scrollback to the coordinator's surface
   would mean *creating* the capability, not scoping an existing one.

So the maximal option here is the safe one, and the gap §7 describes is closed where the coordinator
actually acts: the four tools are ownership-scoped. **Recorded as a contract tension for the owner to
settle**, since §7's note and §3/§4 cannot both be taken literally.

### 2.6 The plan gate's held tasks are re-keyed to `(RepoHash, AgentId)`, fail-closed on ambiguity

`WorkerPlanGate._held` was keyed by the bare agent id, and `Hold` is idempotent per key. Two repos with a
`pr-7` would therefore have **shared one held task**: the second repo's task silently discarded, and
approving one repo's plan authorising the other repo's worker — through `TryReleaseTask`, `MayWork`,
`MayReceivePrompt`, `MayRequestVerification` *and* the `IMergeGate` backstop.

**This is not reachable today, and it is fixed anyway.** The only plan-gated spawn path is the
coordinator's `spawn` op, which never names an id, so every held worker currently has a minted GUID. That
is a property of one call site, not of the type — and "the caller happens not to do the dangerous thing"
is exactly the reasoning that stops being true without anyone noticing.

Entry points that receive a bare id (the worker's IPC socket carries no repo — identity is positional)
resolve **unique-or-nothing**, following the `AgentSessionStore.Find(agentId)` precedent: an id held by two
repos resolves to *nothing* rather than to an arbitrary one of them. Every caller treats "no held task" as
"not authorised", so ambiguity fails closed.

---

## 3. The mutation log — every denial was watched failing

Contract §5.2 and the standing house rule: *a test you did not personally watch fail is not evidence.*
Each enforcement below was removed from a real build, the suite was run, the failure was observed, and the
enforcement was restored. This is the actual output.

| # | enforcement removed | file | result |
|---|---|---|---|
| M1 | the §3 exhaustive deny (`default:` → serve the op) | `AgentSpawnService` | **18 failed**, 10 passed |
| M2 | ownership scoping (parent + repo → id only) | `AgentSpawnService` | **5 failed**, 23 passed |
| M3 | the plan gate's composite key (→ bare agent id) | `WorkerPlanGate` | **2 failed**, 14 passed |
| M4 | the plan-gate check on `prompt` and `verify` | `AgentSpawnService` | **2 failed**, 26 passed |
| M5 | the §4 gRPC role denial (`if (false && …)`) | `RoleInterceptor` | **6 failed**, 3 passed |
| M6 | the jail role lock (`WithoutRepositoryAccess` ignored) | `ContainerSpecBuilder` | **6 failed**, 2 passed |
| M7 | the daemon's role→flag wiring (`isCoordinator = false`) | `AgentSpawnService` | **1 failed**, 28 passed |

**M1 — remove the §3 exhaustive deny.** All 18 denied ops served:

```
EveryOperationOutsideTheContractSurface_IsRefused(op: "ApprovePlan") [FAIL]
EveryOperationOutsideTheContractSurface_IsRefused(op: "BeginMerge") [FAIL]
EveryOperationOutsideTheContractSurface_IsRefused(op: "AcknowledgeFlaggedChange") [FAIL]
EveryOperationOutsideTheContractSurface_IsRefused(op: "GetScrollback") [FAIL]
EveryOperationOutsideTheContractSurface_IsRefused(op: "present_plan") [FAIL]
…
Failed!  - Failed: 18, Passed: 10, Skipped: 0, Total: 28
```

**M2 — remove ownership scoping.** Note the third line: the cross-repo `pr-7` leak is visible directly.

```
AnotherCoordinatorsWorker_CannotBeRead_Steered_OrProposedForVerification [FAIL]
AStrangersWorker_IsRefusedIdenticallyToAWorkerThatDoesNotExist [FAIL]
Ownership_IsKeyedByRepoAndAgentId_NotTheBareAgentId [FAIL]
    Assert.Single() Failure: The collection contained 2 matching items
ACoordinator_CannotNameItselfAsAWorker [FAIL]
Status_ListsOnlyTheCallersOwnWorkers [FAIL]
    Assert.DoesNotContain() Failure: Filter matched in collection
Failed!  - Failed: 5, Passed: 23, Skipped: 0, Total: 28
```

**M3 — revert the plan gate to a bare-id key.**

```
WorkerPlanGateTests.TwoRepositoriesMayEachHoldATaskForTheSameAgentId [FAIL]
WorkerPlanGateTests.AnAmbiguousAgentId_FailsClosed_RatherThanResolvingToEitherRepo [FAIL]
Failed!  - Failed: 2, Passed: 14, Skipped: 0, Total: 16
```

**M4 — remove the plan-gate check from `prompt` and `verify`.** *This mutation found a vacuous assertion
in our own new test and is the reason the log is worth keeping.* On the first run only **one** test went
red; `ACoordinator_NeverBecomesAMergeQueueMember` stayed green because its `Assert.False(verify.Ok)` was
satisfied by an unrelated failure (the fake substrate has no verification command), not by the gate. The
assertion now names the cause, and the mutation takes both:

```
# before fixing the assertion
SendWorkerPrompt_IsRefusedForAWorkerStillAtThePlanGate [FAIL]
Failed!  - Failed: 1, Passed: 27, Skipped: 0, Total: 28

# after
SendWorkerPrompt_IsRefusedForAWorkerStillAtThePlanGate [FAIL]
ACoordinator_CannotProposeVerificationForUnauthorisedWork [FAIL]
    Assert.Contains() Failure: Sub-string not found
Failed!  - Failed: 2, Passed: 27, Skipped: 0, Total: 29
```

**M5 — neuter the §4 gRPC role denial.** Every §4 RPC, including the three phase 3 added:

```
RoleInterceptor_DeniesPlanApprovalToCoordinator [FAIL]
RoleInterceptor_DeniesPlanRejectionToCoordinator [FAIL]
RoleInterceptor_DeniesMergeToCoordinator [FAIL]
RoleInterceptor_DeniesScrollbackToCoordinator [FAIL]
    Assert.Throws() Failure: No exception was thrown
RoleInterceptor_DeniesTheRestOfTheMergeConversationToCoordinator [FAIL]
CoordinatorToken_Authenticates_ThenIsDeniedByTheRoleLayer_NotTheBearerLayer [FAIL]
Failed!  - Failed: 6, Passed: 3, Skipped: 0, Total: 9
```

**M6 — make the spec builder ignore the role lock.**

```
ARepositoryLessJail_RefusesAnyRepositoryPath(worktree: "…/worktrees/abc123/agent-1", …) [FAIL]
ARepositoryLessJail_RefusesAnyRepositoryPath(mirror: "…/repos/abc123.git", …) [FAIL]
ARepositoryLessJail_RefusesAnyRepositoryPath(agentRepo: "…/agents/abc123/agent-1.git", …) [FAIL]
ARepositoryLessJail_RefusesAnyRepositoryPath(cache: "…/caches/abc123/agent-1") [FAIL]
    Assert.Throws() Failure: No exception was thrown
ACoordinatorJail_HasNoWritableBindMount [FAIL]
    Assert.All() Failure: 1 out of 3 items in the collection did not pass.
ACoordinatorJail_MountsNoWorktree_NoMirror_NoGitDir_AndNoCache [FAIL]
    Assert.DoesNotContain() Failure: Item found in collection
Failed!  - Failed: 6, Passed: 2, Skipped: 0, Total: 8
```

**M7 — the daemon stops setting the flag, leaving a perfectly correct spec builder nobody calls
correctly.** This is the MG-12 shape reproduced deliberately, and it is caught:

```
TheDaemonSpawnsACoordinatorWithNoRepositoryAccess_AndAWorkerWithIt [FAIL]
Failed!  - Failed: 1, Passed: 28, Skipped: 0, Total: 29
```

### 3.1 A second vacuous assertion, found the same way

`ACoordinator_NeverBecomesAMergeQueueMember` asserted the coordinator was absent from its repo's merge
queue — inside `if (ctx is not null)`. Making the null check an assertion showed `ctx` **is always null**:
the in-proc fake substrate never provisions a merge queue, so that assertion had never executed. It was
deleted rather than left looking like coverage, the test was renamed to what it actually proves
(`ACoordinator_CannotProposeVerificationForUnauthorisedWork`), and the uncovered guard is recorded in §6.

Two vacuous assertions in one new file, both caught only by removing the enforcement and watching. That is
the argument for the discipline, made on this change rather than a historical one.

---

## 4. The acceptance test (contract §8 step 3) — what was and was not run

**The live four-tool coordinator run was NOT performed. Stated plainly, because describing a scripted run
as a live one is the failure this project keeps auditing for.** What *was* run against real Docker is
described below, and it is a materially different claim.

#### What ran against real containers

After the owner restarted Docker mid-session, the full jail suite was run and is **green**:

```
dotnet test Mainguard.Server.Tests --filter "Category=RequiresDocker"
Passed!  - Failed: 0, Passed: 79, Skipped: 1, Total: 80, Duration: 6 m 20 s
```

That matters for this change specifically: `ContainerSpecBuilder` is the security-critical path phase 3
modifies, and those 79 tests create **real hardened jails** — mirror read-only, package cache, network
isolation, secret delivery, egress, spawn/teardown. The role lock did not break real jail creation.

(An earlier Docker outage in this session — `docker run` printing container output and then never
returning, then `docker ps` itself timing out — was environmental and is **not** a finding. Everything
re-run after the restart passed.)

#### Why the live coordinator run still did not happen

It is no longer blocked by Docker. It is blocked by what it would require: the box has a **live,
running Mainguard install** — `/opt/mainguard/mainguardd` is running in the `MainguardEnv` WSL distro, with
Claude Code installed at `/home/mainguard/mainguard/adapters/bin/claude`. That daemon is the **owner's
installed build**, and it does not contain this change.

Driving a real coordinator through the four new tools therefore means **replacing the owner's running
daemon binary with an unreviewed build from an unmerged branch**. That is a destructive change to their
environment, outside the diff, made while they are asleep and unable to consent, and it is not something
this change should do on its own authority — least of all when the whole point of the branch is that the
owner reviews it before it lands.

So: **left open, deliberately, and it is the one item of contract §8 this change does not close.** Treat
the four-tool acceptance run as unproven until someone watches it.

#### What IS proven, end to end

`CoordinatorRoleLockTests` — 29 tests — exercises all four tools through the **production
`AgentSpawnService` handlers over a real Unix-domain socket**, writing the same bytes an in-jail shim
writes. That is the real daemon code path; what it is not is a real *vendor CLI* deciding to call it.
Plus `ScriptedCoordinatorEndToEndTests` (phase 2, unchanged) for the scripted loop.

#### One command for the owner

Once this branch is reviewed and the daemon is rebuilt from it:

```bash
# 1. the real-jail suite (already green here)
dotnet test Mainguard.Server.Tests --filter "Category=RequiresDocker"

# 2. the live four-tool run, against a daemon built from THIS branch
#    (replaces the installed daemon — the step deliberately not taken above)
dotnet publish Mainguard.Server -c Release -r linux-x64 --self-contained false \
  -o /tmp/mgd-phase3
wsl -d MainguardEnv -- sudo systemctl stop mainguardd
wsl -d MainguardEnv -- sudo cp /tmp/mgd-phase3/mainguardd /opt/mainguard/mainguardd
wsl -d MainguardEnv -- sudo systemctl start mainguardd
# then, from a coordinator jail's terminal:
#   mainguard-agent spawn claude-code "<the user request>"
#   mainguard-agent status
#   mainguard-agent prompt <worker-id> "<steer>"
#   mainguard-agent verify <worker-id>
```

The credential leg needs no work — see §4.1.

### 4.1 A correction to the phase-3 brief: the login round-trip is already wired

The task that commissioned this work asked for `loginStateFiles` to be wired in the adapter manifest for
`claude-code`, on the stated grounds that "every entry currently has `loginStateFiles: None`, so the
host-keychain→jail→harvest round-trip the design describes is modelled but never exercised".

**That premise is false, and nothing was changed on its account.** There is no `loginStateFiles` field. The
field is `credentialPaths`, it is populated for every adapter including `claude-code`
(`.claude/.credentials.json`, `.claude.json`), and the round-trip is implemented on both legs:

- **restore** — `DockerSandboxEngine.RestoreCliCredentialsAsync`, one `docker exec` per file as the agent
  uid, `umask 0077`, content on **stdin only** (never argv/env), atomic `mv`, write-if-absent.
- **harvest** — `SandboxAgentLauncher.HarvestCliCredentialsAsync`, driven by the adapter's declared
  `credentialPaths`, run before teardown.

The credential-hygiene concern in the brief was also already addressed, and by the exact mechanism the
brief warned about: `ExecStdinTransport` documents that `PUT /containers/{id}/archive` reports success
while the bytes land **in the image layer underneath the tmpfs**, and that is precisely why this code path
uses exec-stdin instead of `docker cp`. Destination is the `/home/agent` tmpfs
(`size=256m,mode=0700,uid=1000`) on a `ReadonlyRootfs = true` container. No image-layer write occurs.

Two real gaps in that subsystem were found and are **not** fixed here, because both are outside the role
lock and each deserves its own reviewed change:

- `DaemonBackedOrchestrator.PersistLiveAgentLoginsAsync` — the periodic/shutdown harvest sweep — **has no
  callers anywhere**. The daemon leg and the RPC exist; the client caller was never wired, so a login is
  still lost on app close / VM stop / crash without an explicit Stop.
- `HarvestCredentialsRpcTests` is **vacuous as round-trip coverage**: every case spawns against
  `RepoHandle = "unprovisioned-handle"`, which by design produces a session with no container, so the
  harvest short-circuits before touching a jail. It covers auth and argument validation only.

---

## 5. Defects found and fixed in this change

- **The `list` op leaked across repositories.** It filtered on `ParentAgentId` alone and returned rows
  carrying no repo (§2.2). Now `(RepoHash, AgentId)`-scoped.
- **The coordinator jail had full read-write repository access** (§1.2, §2.3).
- **A coordinator became a merge-queue member** despite having no branch (§2.3).
- **`WorkerPlanGate` aliased held tasks across repositories** (§2.6). Latent, not reachable today, fixed
  at the key.
- **`AbandonMerge`, `AcknowledgeFlaggedChange` and `RejectPlan` were in `CoordinatorDeniedMethods` with no
  test.** Nothing would have noticed any of the three being dropped from the set. Now covered.
- **One of our own new tests asserted nothing useful** and was caught by mutation M4 (§3).

## 6. Known-unfixed, recorded honestly

- **The live four-tool acceptance run** (§4). The one open item of contract §8.
- **`RoleInterceptor` governs a credential class production never issues.**
  `ConnectionRoleRegistry.IssueCoordinatorToken` / `RegisterCoordinatorToken` have **no production
  callers** — only tests. The §4 gRPC denials are therefore defence-in-depth for a token nothing mints.
  This is *currently* moot because the in-jail coordinator has no gRPC route at all (its only channel is
  the IPC socket), which is why phase 3 put the enforceable surface there. But it is the MG-12 shape
  again — a control that looks present and is never reached — and it should either be wired or deleted.
  Left alone here because doing either is a change to the daemon's authentication model, not a role lock.
- **`PlanApprovalGrpcService.StreamPlans` trusts a client-asserted `coordinator_id`.** Any caller may name
  any coordinator, or omit it and receive every plan on the daemon. It is not in `CoordinatorDeniedMethods`.
  Not reachable by the in-jail coordinator (no gRPC route), so it is an operator-surface issue rather than
  a coordinator-surface one — but it is the same missing-identity root cause as the item above.
- **`PlanApprovalService` is still keyed by bare worker/coordinator id.** `WorkerPlanGate` now fails closed
  on an ambiguous id, which prevents the dangerous direction (one repo's approval releasing another's
  task), but the plan store itself would still refuse a second repo's `pr-7` under one-live-plan-per-worker.
  Fixing that properly means carrying the repo through `PendingPlan`, the proto, and the UI — a change
  larger than this one and unrelated to the role lock.
- **The "a coordinator never gets a merge-queue row" guard is untested.** The guard is one `if` in
  `AgentSpawnService`; the in-proc fake substrate never provisions a merge queue, so there is no
  observable to assert against without a Docker-tier rig (§3.1).
- **`FlaggedChangeGate` has no production wiring** — unchanged from phase 2 §3a, being fixed separately.

---

## 7. Is the four-tool surface sufficient? (contract §8's real question)

Yes, with one caveat worth the owner's attention — and stated here rather than resolved by quietly adding
a capability, which the contract explicitly forbids.

The four tools cover the loop: spawn workers, watch them, steer them once their plans are approved, and
propose them for verification. Two things that were previously reachable only by the coordinator being a
general CLI are now answered *inside* the surface rather than removed from it:

- **"Why is this worker doing nothing?"** `get_worker_status` rows now carry the plan-gate reason verbatim,
  so the question does not push the coordinator toward reading a worker's terminal (which §4 denies).
- **"Why was my spawn refused?"** the backpressure signal rides on the same response.

**The caveat.** The coordinator can no longer read the repository at all, which is correct per §2 — but it
means the *quality* of `spawn_worker`'s `title`/`task_prompt` now rests entirely on the operator's original
request. Phase 2 already moved plan authorship to the worker precisely because the coordinator "cannot see
the code", so this is the contract working as designed rather than a regression. If real usage shows
coordinators producing briefs too vague for a worker to plan against, the fix is a better *brief*
(operator-side), not a repository read — and if the owner ever concludes otherwise, that is a §3 change and
their call.
