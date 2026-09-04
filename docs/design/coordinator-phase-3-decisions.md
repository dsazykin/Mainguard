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
`AgentIpcRequest.CoordinatorOps` is the set, and the daemon builds its handler table *against* that set at
construction: an op is served because the set contains it, and a handler registered for anything else
makes `AgentSpawnService`'s constructor **throw** rather than becoming a fifth coordinator tool.
`CoordinatorRoleLockTests.EveryOperationOutsideTheContractSurface_IsRefused` walks 18 op names — every §4
capability by its RPC spelling, every worker plan-gate op, and assorted others — and asserts each is
refused. Removing the deny turns **all 18 red** (mutation M1, §3 below).

**This was not true when it was first written, and the correction is the interesting part.** The sentence
above originally described a bare `switch`, and `CoordinatorOps` was consumed by *nothing* — it appeared
in a comment, its own declaration and a `<see cref>`. Adding
`case "read_worker_scrollback": return new AgentIpcResponse(Ok: true);` therefore shipped a fifth,
unlisted coordinator tool — reading another session's scrollback, which §4 forbids by name — with the
whole 95-test suite **green**. The positive control (the identical mutation spelled `"exec"`, which *is*
one of the 18) went red, so the method was sound and the coverage was not: an 18-name blocklist cannot
catch name 19, and there was nothing else looking. The allow-list is now load-bearing rather than
decorative, and `TheDaemonServesExactlyTheContractSurface_AndNothingElse` asserts the served set against
the contract set in both directions. Re-run of the same mutation after the fix: **30 of 30 red** (the
daemon refuses to construct); the variant that also adds the name to `CoordinatorOps` — the only spelling
that could ever work — goes red on the contract-set assertion instead.

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

### 3.0 Re-verified after merging the updated phase-2 base

Phase 2 moved under this branch after phase 3 was written: PR #291 wired `FlaggedChangeGate` into the
merge spine, and two Copilot-found bugs were fixed on the phase-2 branch — `TryReleaseTask` was not
idempotent, and the approve/reject buttons latched disabled. Merging that in produced three conflicts,
one of them **on the exact three lines phase 3 had re-keyed**: `TryReleaseTask`'s write-back.

That is the highest-risk kind of conflict this project has, so the reconciled code was re-mutated rather
than assumed:

| # | enforcement removed | result |
|---|---|---|
| M9 | write-back under `("", id)` instead of the resolved key | **2 failed** / 17 passed |
| M8 | the phase-2 once-only guard (did *their* fix survive?) | **3 failed** / 16 passed |
| M3 | the phase-3 composite key (did *mine* survive?) | **2 failed** / 17 passed |
| M1 | the §3 exhaustive deny (re-run) | **18 failed** / 11 passed |
| M2 | ownership scoping (re-run) | **5 failed** / 24 passed |
| M10 | `FlaggedChangeGate` dropped from the ANDed gates array | **4 failed** / 18 passed |

**M9 found a real gap the merge created, and it is the reason this section exists.** On its first run only
*one* test caught the wrong key — and not one of the release-once tests. Every pre-existing release-once
test holds at the **default empty repo hash**, where the wrong key `("", id)` and the right key coincide,
so all of them stay green against a write-back that silently stops latching `Released`. The interaction
was untested by both sides: phase 2's tests never used a repo hash, phase 3's key tests never released
twice. `ReleasingTwiceForARepoScopedWorker_StillAuditsAndAnnouncesOnce` closes it, and M9 re-run then
fails **2** tests instead of 1.

**M10** covers the instruction that all three gates survive the merge. They AND and are independent —
`ChangedTestCommandGate`, `FlaggedChangeGate`, and the plan gate — and dropping the flagged-change gate to
simplify the array is caught by four of phase 2's own provisioner tests.

*(A note on a hazard that turned out not to be one: taking phase 2's write-back line verbatim —
`_held[workerAgentId]` — does **not** compile once the dictionary is tuple-keyed, so that particular
mistake is caught by the type system rather than by a test. The comment on that line originally claimed
otherwise and was corrected. The silent version is a write-back under a *different composite* key, which
is what M9 exercises.)*

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
  any coordinator, or omit it and receive every plan on the daemon. **Half closed**: it is now in
  `CoordinatorDeniedMethods` (`RoleInterceptor_DeniesPlanStreamToCoordinator`, with the operator's
  positive beside it, mutation-checked). The field is still trusted for callers that are not the
  coordinator role — the durable fix is a DERIVED caller identity, which is the same missing-identity
  root cause as the item above and a change to the authentication model rather than to this branch.
  Not reachable by the in-jail coordinator (no gRPC route), so it is an operator-surface issue rather than
  a coordinator-surface one — but it is the same missing-identity root cause as the item above.
- **`PlanApprovalService` is still keyed by bare worker/coordinator id.** `WorkerPlanGate` now fails closed
  on an ambiguous id, which prevents the dangerous direction (one repo's approval releasing another's
  task), but the plan store itself would still refuse a second repo's `pr-7` under one-live-plan-per-worker.
  Fixing that properly means carrying the repo through `PendingPlan`, the proto, and the UI — a change
  larger than this one and unrelated to the role lock.
- **The "a coordinator never gets a merge-queue row" guard is untested.** The guard is one `if` in
  `AgentSpawnService`; the in-proc fake substrate never provisions a merge queue, so there is no
  observable to assert against (§3.1). **The Docker-tier claim is now too pessimistic**: the queue-seeding
  posture (`QueueSeedingRpcTests` — real composition root, real origin repo, the shipped `ProvisionRepo`
  RPC) builds a REAL merge queue in the non-Docker tier, which is the observable §3.1 said did not exist.
  `MergeQueueProvisioner` is `sealed`, so it is that posture rather than a spy. Still open, but cheap and
  no longer blocked on Docker.
- ~~**`FlaggedChangeGate` has no production wiring**~~ — **this entry was stale when written and is now
  wrong.** Phase 2 §3a records the resolution: the gate is registered and ANDed into the queue's `gates`,
  armed at verification time by `MergeQueueProvisioner.ArmFlaggedChangeReview`, and the composition root
  passes `resolveApprovedPlan` (`GatewayServiceRegistration`). The rows reach a human too —
  `MergeQueueGrpcService.FlaggedItemsFor` yields the P2-11 risk-hunk / lockfile / out-of-approved-scope
  items from `FlaggedChanges.PeekStore`, not `ChangedTestCommandGate` alone. Left visible rather than
  deleted, because §6 understating the branch is the kind of error a reviewer inherits.

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

---

## 8. The first turn — the deadlock a live run found, and the fix

§4 said the live four-tool run was the one open item of contract §8 and that the loop should be treated as
unproven until someone watched it. Someone watched it. It does not run.

### 8.1 What was observed, and why it is a deadlock rather than a bug

A worker jail launched `claude --append-system-prompt <worker operating instructions>` **and nothing
else**. The CLI sat interactive with an empty input box, banner only. Six minutes later: outbox empty, no
session transcript, `mainguard-plan` never run.

The cause is one sentence of vendor behaviour that §7 of this document did not think to ask about: **a CLI
does not act on a system prompt. It needs a user turn.** §1.2 and the change that added
`AgentOperatingInstructions` fixed "nobody told the agent what it is". Nothing fixed "nobody ever asked it
to start".

What makes it a *deadlock* and not merely a slow start is the interaction with the two controls this
branch is about:

1. The task the worker would work on lives in the plan gate (phase 2 §2.2 — "the daemon never gives the
   worker its task"), so there is nothing in the jail to react to.
2. The only mechanism that delivers text to a worker's CLI is `AgentCliBinder.TrySendPromptAsync`, reached
   only through `send_worker_prompt`, which is plan-gated. Verbatim, from a coordinator trying:
   `<worker-id> has not presented a plan yet — no work is authorised.`
3. A worker's terminal is input-locked daemon-side (P2-14/MG-5), so a human cannot rescue it either.

So the worker could not present a plan without a first turn, and nothing could send it a first turn until
it had presented a plan. **Phase 2's worker-authored-plan loop could not start once on a real CLI**, and
neither phase noticed, because the end-to-end test drives `CoordinatorTools` — the in-process surface §1.1
found is not wired to the shipped coordinator at all.

Verified independently before anything was changed, and stated as measurements rather than as reasoning:

| probe (real claude-code 2.1.250, outside Mainguard, under a real pty) | result |
|---|---|
| system prompt + `--allowedTools` grant, no turn | banner, empty input box; shim **never ran** in 60 s |
| the same, plus the turn appended LAST | shim **never ran** in 90 s — see §8.3 |
| the same, turn as the FIRST positional | ran `brief`, read the code, wrote a plan, ran `present` — unattended |

### 8.2 The fix, and the invariant it rests on

The first turn is delivered as a **launch argument**: `AgentKickoffPrompt.For(role, shimPath)`, placed on
the launch line by `SandboxAgentLauncher.ApplyInitialPrompt`, declared per adapter as
`initialPromptStyle` — the same shape as `systemPromptArg` and `preApprovedCommandArg` beside it, because
this too is vendor knowledge.

**The invariant relied on: the withheld thing is the TASK, and it stays withheld.** The kickoff text is a
compile-time constant of `(role, shimPath)`. It has no task parameter, no title, no agent id, no
coordinator id — look at the signature — so it cannot carry the work even by mistake, and a test asserts
that structurally (by the parameter types, not by sampling today's strings, which would keep passing the
day someone threads the task in). What the text *says* is "run `mainguard-plan brief`", which is exactly
what phase 2 §2.2 already gives a worker up front: *"the worker gets a brief (the title — enough to know
what part of the repo to inspect), never the task."*

All four enforcement points from §2.2 are untouched and still answer no. A worker that follows the turn to
the letter arrives at the gate and blocks, which is where phase 2 wanted it:

- `TryReleaseTask` still requires `HasApprovedPlan` and still has no override parameter;
- `send_worker_prompt` and `request_verification` are still refused at the gate;
- `WorkerPlanGate` is still ANDed into every repo's merge queue as an `IMergeGate`.

**Why not the two other candidates.**

- *Write the turn into the worker's pty at spawn.* This creates a **second, ungated writer to an agent's
  stdin**. The moment it exists it is capable of carrying the task, and only convention keeps it from
  doing so — which is the exact reasoning §5 and MG-12 say not to accept. As a launch argument there is no
  runtime delivery path at all: `TrySendPromptAsync` remains the only way to write to a worker's CLI,
  still `internal`, still plan-gated.
- *Let the plan gate permit an initial turn whose content is provably the brief.* This means adding a
  bypass branch to the gate. Phase 2 §2.2 point 2 is explicit — *"There is no override parameter; an
  override is how a gate becomes decorative"* — and an override that admits "provably the brief" is one
  refactor away from admitting more. The chosen fix required **no change to `WorkerPlanGate` at all**,
  which is the strongest form of "the gate was not weakened".

**The coordinator deliberately gets no first turn**, and the asymmetry is a decision. Its terminal is not
input-locked (only `AgentRoles.Managed` is), so a human *can* type into it — which is what makes the
worker's missing turn a deadlock and the coordinator's merely a wait. And a coordinator's real first turn
is the operator's request, which the daemon does not have and must not invent: a synthetic one would set a
coordinator fanning out workers for work nobody asked for.

### 8.3 The trap: the obvious spelling of this fix ships the fix and keeps the bug

Every other field on this launch line is **appended**. Appending the turn does not work.
`--allowedTools` is variadic (`<tools...>` in claude-code's own usage), so it swallows every positional
that follows it: the turn becomes another tool pattern and the CLI idles exactly as it did with none.
Measured, not reasoned — 90 seconds, shim never run.

So the turn goes **first**, ahead of every flag the daemon appends, the wire value says so
(`first-positional` rather than a truthful-but-useless `positional`), and `BuildLaunchArgv` is the single
place that knows the order. `WorkerFirstTurnTests` asserts POSITION and not merely presence for this
reason: with the turn appended last, the presence-only test stays **green** against a build that is still
deadlocked (mutation K2 below).

### 8.4 The mutation log

Each enforcement was removed from a real build, the suite was run, the failure was watched, and the
enforcement was restored.

| # | enforcement removed | result |
|---|---|---|
| K1 | the first turn is never delivered (pre-change behaviour) | **3 failed** / 30 passed |
| K2 | the turn appended LAST instead of first | **2 failed** / 31 passed |
| K3 | the `ipcDirPath` gate on the turn | **2 failed** / 31 passed |
| K4 | the role gate (a coordinator gets the worker's turn) | **1 + 1 failed** |
| K5 | the `BadInitialPrompt` parse refusal | **4 failed** / 91 passed |
| K6 | the field stops at the manifest (marker never carries it) | **0 failed — see below** |
| K7 | the `brief` step deleted from the turn | **1 + 2 failed** |

**K2 is the one worth reading.** `APlanGatedWorkerJail_IsLaunchedWithAFirstUserTurn` stayed **green**
while the build was still deadlocked, because the turn was on the line — just where the CLI would never
read it. That is why the ordering assertion is a separate test with its own reason written on it.

**K6 found a real gap and is the reason this log is worth keeping.** Dropping `spec.InitialPromptStyle`
from the marker `AdapterChannel` writes left **the entire suite green**: the manifest declared it, the
spec carried it, the launcher honoured it, and no jail ever saw it, because the daemon reads the marker
and not the manifest. That is phase 3's own M7 shape reproduced by accident — a correct builder nobody
calls correctly — and it is now caught by
`AdapterInitialPromptTests.TheInstallChannel_WritesTheFieldIntoTheMarkerTheDaemonReads`, driven through
the real `AdapterChannel.EnsureAsync`. Re-run after adding it: **1 red.**

### 8.5 Found and NOT fixed here — two further unattended stalls on the same loop

Both were measured during the probes above, both are outside the defect this change is for, and each
deserves its own reviewed change. Recorded so a reviewer does not inherit the impression that a live run
is now guaranteed to work.

- **The folder-trust dialog.** In a directory claude-code has not been trusted for, the CLI shows a
  blocking "Is this a project you trust?" prompt **before** the banner, and the first turn does not run —
  reproduced with the turn present and correctly positioned. Trust is recorded per project path in
  `.claude.json`, which IS restored into the jail from the host store (`credentialPaths`), and the jail's
  path is always `/workspace` — so a box whose stored `.claude.json` already trusts `/workspace` never
  sees it, which is consistent with the live run reporting "banner only". A fresh install may not be so
  lucky. The fix is to seed that trust in the restored config, which is a change to what the daemon writes
  into an agent's home, not to a launch line.
- **Nothing else stalls.** Worth stating because it was checked rather than assumed: writing the plan file
  does NOT require a permission a jail cannot answer. In the unattended probe the CLI wrote its plan into
  its own scratchpad and passed that path to `mainguard-plan present`, and the whole `brief` → read →
  `present` loop completed with no human. The single `Bash(<shim>:*)` grant from defect C2 is sufficient.

### 8.6 What this does and does not close of §4

It does not perform the live four-tool acceptance run either — that still requires replacing the owner's
installed daemon (§4). What it removes is the reason such a run could not have succeeded. The claim made
here is narrower and stated exactly: **the launch line a worker jail receives now carries a first user
turn, and a real claude-code driven with that line ran the full brief → inspect → present loop
unattended.** What has still not been watched is that loop over the real daemon's socket, in a real jail,
with a real human approving.

## 9. Defect D1 — an unknown agent-kind produced a CLI-less jail and reported SUCCESS

### 9.1 What was observed

A real coordinator, on its first move, ran:

```
mainguard-agent spawn coder "<the task>"
```

`coder` is not an installed adapter — the box has `claude-code` and five `scripted-*`. So
`InstalledAdapterCatalog.TryGet` answered null, `launchCommand` stayed null, and the jail was created
with **nothing to run**: `docker top` showed `sleep infinity` and no CLI. The shim answered
`Ok, Status: AwaitingPlan`, so the coordinator believed it had a worker, and that dead jail held a slot
against `MaxActiveWorkers` for the rest of the session.

**This was the instructions being followed, not ignored.** `AgentOperatingInstructions.Coordinator` said
`spawn <agent-kind>` and never said what a kind was. `coder` is the obvious guess.

### 9.2 The two halves, and why the second is not prose

**(a) The refusal.** `CoordinatorSpawnGate.RefuseUnknownKind`, called from
`AgentSpawnService.SpawnWorkerAsync` before anything is minted, so a bad kind costs no session, no jail
and no cap slot. It names the kind and every installed one.

It is **not** in `SandboxAgentLauncher`, deliberately. A CLI-less jail is a legitimate, wanted outcome of
two other paths and the launcher's call site already says so: the operator spawning an unknown kind gets a
bare sandbox **with a human on its PTY**, and `ExternalPrWorkerHost` spawns kind `external-pr`, which no
adapter answers to by design. What makes it never legitimate *here* is attendance: a coordinator's workers
are `Managed`, their terminals are daemon-locked read-only (P2-14), so nobody can ever type into the shell
such a spawn produces. Moving the check into `SpawnAsync` would have fixed the coordinator by breaking
both of those.

An **empty** catalog stays permissive. That is the documented meaning of `InstalledAdapterCatalog.HasAny`
("a dev/unprovisioned box"), and it is also the only honest option: the entire value of this refusal is
the list of alternatives it carries, and with nothing installed there is no list.

**(b) The instructions.** `Coordinator(shimPath, installedKinds)` now renders the kinds, per spawn, from
`InstalledAdapterCatalog.InstalledKinds()`. Writing them into the prose would have been the same defect
one layer up — a hardcoded list stops describing the machine the first time a CLI is installed or removed
(MG-12), and this repo has paid for that shape repeatedly. The instructions and the refusal render through
one function (`AgentOperatingInstructions.SpellKinds`) over one set, so they cannot be edited apart.

`InstalledKinds()` exists as a named member for exactly that reason, and
`CoordinatorSpawnKindTests.TheInstructionsAndTheRefusal_ReadTheSameSet` is the assertion that keeps it so.

### 9.3 The mutation log

| # | enforcement removed | result |
|---|---|---|
| M1 | the kind guard (the pre-change behaviour) | **2 failed** / 5 passed |
| M2 | the empty-catalog carve-out | **1 failed** / 6 passed |
| M3 | the refusal stops naming the installed kinds | **2 failed** / 5 passed |
| M4 | the launcher passes no kinds to the instructions | **2 failed** / 5 passed |
| M5 | the kind list hardcoded in the prose | **3 + 2 failed** |

M1 is the verification: with the guard removed the daemon reproduces the report exactly — `spawn coder`
answers `Ok`, a session is minted, and a jail is requested for a kind nothing can launch. M4 is the one
worth reading: every prose assertion about the instructions stayed green while the launcher handed them an
empty list, which is why the binding is asserted at the launcher and not only at the string function.

## 10. Defect D5 — the pre-approval fix was inert on a real install, and the fallback granted the wrong shim

### 10.1 What was measured, before anything was changed

Two files on the owner's machine.

`~/mainguard/adapters/registry/claude-code.json` — no `preApprovedCommandArg`, no
`preApprovedCommandFormat`, no `initialPromptStyle`. It was written the day before those fields existed,
and the daemon reads the **marker**, not the manifest. So §8's first-turn fix and the C2 pre-approval fix
were both **completely inert on the only install that matters**, with every test green.

`~/.mainguard/cli-settings/<repo>/claude-code.json` — a harvested
`workspace/.claude/settings.local.json` holding:

```json
{ "permissions": { "allow": [ "Bash(node *)", "Bash(/opt/mainguard/ipc/mainguard-agent *)" ] } }
```

That is the **coordinator's** shim, recorded when the owner answered "yes, don't ask again" in an attended
coordinator terminal. It is restored into every later jail for that repository — workers included. So the
live coordinator worked only because of a stale file, and that same file was queued to hand a worker the
coordinator's tool grant.

### 10.2 (a) The migration: the marker stops being a second source of truth

The obvious mechanisms were considered and rejected in this order:

- **Re-derive the marker when the version matches.** It would not have repaired *this* install. The CLI had
  been updated forward of the shipped pin (installed 2.1.234, manifest 2.1.218), so the versions differ and
  every new field stays masked. `AdapterMarkerProjectionTests.TheProjectionAppliesEvenWhenTheInstalledVersionHasMovedPastTheShippedPin`
  is that rejection written down as a test rather than as a comment.
- **Rewrite markers on daemon start.** A write that can fail, that can race the installer, and that leaves a
  window in which the stale copy is still authoritative. It also leaves the marker as a second source of
  truth, so the next field to be added has the same failure available to it.
- **Write the marker on `EnsureAsync`'s `AlreadyHealthy` path.** Correct as far as it goes, and useless
  here: nothing re-runs an install on a healthy CLI.

What shipped instead: `InstalledAdapterCatalog.List()` **projects the shipped manifest over every marker it
reads**. A marker is a record of an install, so it keeps the two things only the install knows — the
`version` that probed green and the `launch` argv that probed green — and every manifest-declared field
(which is a description of the *vendor's CLI*, and was only ever a dated copy of the manifest's answer)
comes from the manifest. No migration, no window, no write, no ordering; an install that predates a field
picks it up on the very next spawn, and the class of defect is gone rather than instanced.

Two consequences stated deliberately:

- **Projection is by adapter id, not by version.** A marker's copy of these fields came from an *older*
  manifest, so preferring the current one is never worse than preferring the stale one.
- **Nulls project too.** A field the shipped manifest stops declaring is *removed*, not inherited. This set
  contains a grant of execution (`preApprovedCommandArg`), and a revocation an old marker could veto would
  not be a revocation.

`InstalledAdapterMarker.FromSpec` is now the single spec→marker mapping, shared by `AdapterChannel`'s writer
and by the projector. It replaced a 14-argument positional constructor call — which is exactly the shape
mutation K6 caught last time.

### 10.3 (b) The wrong-shim grant: what changed about who can run what

**Stated exactly.** `/opt/mainguard/ipc` is Mainguard's own mount, and the grants for it are issued per
jail and per role at launch by `SandboxAgentLauncher.ApplyShimPreApproval` — one absolute path, the shim
that jail's role was actually given. Therefore **no persisted settings file may say anything about that
mount**. `CliSettingsGrantScrub.Scrub` removes every JSON string naming it and every property keyed by one,
at any depth.

Who can run what, before and after:

| | before | after |
|---|---|---|
| coordinator jail | `mainguard-agent` — via a stale harvested file, on repos that happened to have one | `mainguard-agent` — via its own launch flag, on every repo |
| worker jail | carried the **coordinator's** `mainguard-agent` grant from the repo store | `mainguard-plan` only, from its own launch flag |
| external-PR head | no IPC dir ⇒ no grant (unchanged) | unchanged |
| anything else in `/opt/mainguard/ipc` | whatever the repo store had accumulated | nothing, ever |

**Applied in both directions, and the restore direction is the one that matters today.** Scrubbing only the
harvest would have fixed nothing already on disk — the poisoned entry is in the owner's store now, and would
keep being restored until some later attended stop happened to overwrite it. Scrubbing the restore
neutralises every stored file immediately, with no migration; scrubbing the harvest stops the store
re-acquiring one and makes it self-heal at the next attended stop, since a harvested file replaces its
stored copy.

**A DENY naming the mount is dropped too**, and that is the one direction of this change a reviewer should
argue with. It is not a widening: what replaces a dropped rule is not "anything goes" but the daemon's own
per-jail, per-role, one-absolute-path grant. Mainguard is the authority on its own mount, and a rule from an
agent-writable file cannot be a boundary anyway — it is indistinguishable from one the agent wrote itself.

**Fail closed, and lossless otherwise.** A file that names the mount and will not parse as JSON does not
travel at all (refusing costs a re-approval; carrying bytes nobody can read is how a grant survives unseen).
A file that never names the mount is returned **byte-identical** — it is the owner's own configuration, and
reformatting it as a side effect of a security scrub would be its own defect.

### 10.4 The mutation log

| # | enforcement removed | result |
|---|---|---|
| M6 | the manifest projection (the pre-change behaviour) | **4 failed** / 17 passed |
| M7 | projection gated on version equality (the design NOT chosen) | **3 failed** / 18 passed |
| M8 | a null manifest field inherits the marker's instead of revoking | **1 failed** / 20 passed |
| M9 | the manifest also overwrites `version` + `launch` | **2 failed** / 19 passed |
| M10 | the scrub removed from the RESTORE direction | **1 failed** / 11 passed |
| M11 | the scrub removed from the HARVEST direction | **1 failed** / 11 passed |
| M12 | the scrub keyed on one shim filename instead of the mount | **5 failed** / 16 passed |
| M13 | fail OPEN on an unparseable file that names the mount | **1 failed** / 20 passed |
| M14 | the post-serialisation re-check | **0 failed — see below** |
| M15 | the restore scrub drops the whole file instead of the rule | **1 failed** / 11 passed |

**M14 is recorded as untested, not as enforced.** Removing the final `Mentions(bytes)` check left the suite
green, and no input reaches it: `Strip` handles every JSON node kind, so nothing that names the mount can
survive the walk. It stays as a fail-closed backstop against a future edit to `Strip` — cheap, and the
alternative is that such an edit ships silently — but it is not evidence of anything today and is not
presented as such.

### 10.5 Found and NOT fixed here

- **The owner's stored file still contains the stale grant.** It is inert — the restore scrub removes it on
  every spawn, and the next attended stop rewrites the file without it — but the bytes are on disk until
  then. Rewriting a user's store from the daemon at startup is a separate, reviewable change, and this one
  deliberately does not write to it.
- **§8.5's folder-trust dialog is still open**, unchanged by either defect here.

---

## 11. The first end-to-end run's two defects (E1, E2 in the run report; E1/E3 here)

The coordinator loop ran end to end for the first time. It reached the merge queue's doorstep and did
two things wrong on the way, both of which the *worker* noticed before we did.

### 11.1 Mainguard's own instruction file was polluting the user's branch

`ab8234b7` made the daemon write the adapter's declared `instructionsFile` (claude-code: `CLAUDE.md`)
into the worker's worktree root, because that is what the CLI opens unprompted. That worked. Nothing
ignored it.

Measured in the live jail, and reproduced in a container against real git before anything was changed:

```
.git/info/exclude            → only "/.claude/settings.local.json"
git check-ignore CLAUDE.md   → rc=1
git status --porcelain       → ?? CLAUDE.md
git add -A                   → stages CLAUDE.md
```

So every worker's own `git add -A` would commit Mainguard's briefing into the user's branch, forever.
The worker in that run flagged the stray file in its report unprompted — the shape of a defect a user
finds first.

**The fix follows the declared name, not `CLAUDE.md`.** `SandboxAgentLauncher.DeclaredWorkspaceIgnorePaths`
unions the adapter's workspace settings paths with its `instructionsFile`, and one field now decides both
what is written and what is excluded. Hardcoding today's filename would have kept every test green and
silently stopped covering the next CLI (MG-12's standing shape). The engine method that applies the list
learns no filename at all and was renamed `ApplyWorkspaceIgnoreAsync` to stop claiming it is about
settings.

**The second half, which the exclude cannot do.** *A git exclude does not apply to a tracked file.*
Probed in a container: in a repository that tracks `CLAUDE.md` — this one, and every repository with
project instructions — with `/CLAUDE.md` present in `info/exclude`, `check-ignore` still answers rc=1,
`status` reports `M CLAUDE.md`, and `git add -A` stages the daemon's text OVER the user's own. So for
exactly the repositories most likely to hit it, the ignore is inert and the write is destructive. The
launcher therefore **never overwrites**: it stages into an empty slot or logs and skips. `File.Exists`
rather than "is it tracked", because on a freshly created worktree those are the same question and it is
the destructive case the rule has to be right about; on a resumed worktree the file is this daemon's own
dropping, already excluded, and for claude-code the launch flag re-delivers the current text anyway.

**Also closed, found while doing it:** `instructionsFile` was never validated. `Path.Combine(worktree,
"../../x")` writes outside the worktree. It is now refused at manifest parse *and* re-checked in the
launcher, because an `InstalledAdapterMarker` is a JSON file on disk that no manifest parse re-reads.

**Deliberately left:** nothing else the daemon writes into `/workspace` was found. The package cache is
outside the worktree (MG-43), credentials go to the tmpfs `$HOME`, and the shim and IPC copy of the
instructions live under the read-only `/opt/mainguard/ipc` mount.

| # | enforcement removed | result |
|---|---|---|
| M1 | the ignore list drops the instructions file (the pre-change behaviour) | **6 failed** / 23 passed |
| M2 | the ignore list hardcodes `CLAUDE.md` instead of the declared name | **4 failed** / 25 passed |
| M3 | the ignore list drops the `IsHomeRelativeFilePath` filter | **3 failed** / 26 passed |
| M4 | the staging step drops the no-clobber refusal | **2 failed** / 27 passed |
| M5 | the staging step drops the escaping-name check | **2 failed** / 27 passed |
| M6 | the spawn reverts to the settings-only list (a correct function nobody calls correctly) | **1 failed** / 28 passed |
| M7 | the manifest parse refusal removed | **5 failed** / 8 passed |

### 11.2 The loop ended one step short of the merge queue

The worker did the approved work and stopped with a **20-line uncommitted diff**. Stopping the agent
deleted the worktree, so the diff went with it; `agent/<id>` carried no commit; and its merge-queue row
joined ~19 dead "the agent's sandbox is gone" entries. `AgentOperatingInstructions.Worker` had never
mentioned committing.

**Why nothing rescued it.** The readiness trigger observes `refs/heads/agent/<id>` *advancing* and then
going quiet (`AgentRefWatcher.Advanced` → `WorkerReadinessTrigger` → `MergeQueue.RunVerificationAsync`).
With no commit there was nothing to observe, and the failure is silent by construction: a worker that
never commits is indistinguishable, to every mechanism downstream, from one that did nothing.

**Why telling the worker to run `git` does not work — measured, not reasoned.** claude-code 2.1.251,
under the jail's real posture (`--permission-mode default`, one `--allowedTools` grant for its own shim),
asked to commit finished work:

| launch line | result |
|---|---|
| the user's own `defaultMode: auto` (the confounded first probe) | committed — and proved nothing about a jail |
| `--permission-mode default` + `Bash(<shim>:*)` only | **refused twice; could not even run `git status`; stopped without committing** |

In a real jail that is not a refusal but an approval prompt with nobody to answer it — the exact stall
`100e0227` added `preApprovedCommandArg` to end.

**So the delivery is the shim, which is already the one command a worker may run.** `commit_work` on the
worker endpoint; `mainguard-plan commit <message>` on the command line; the commit performed daemon-side
by `WorktreeManager.CommitAgentWork`.

- **Rejected: widen the grant to `Bash(git commit:*)`.** Strictly worse. It costs a second standing
  grant in every worker jail and leaves the daemon trusting a CLI to name the right branch. Here the
  worker supplies a *message* and nothing else — repo, worktree and branch are computed from the id the
  endpoint already proves, which is the same structural argument that makes `AgentRefMediator` safe.
- **Rejected: commit for the worker at teardown.** It would rescue this exact diff and is the wrong
  rule: a human's Stop means stop, and a commit nobody asked for entering the merge queue is a worse
  default than a lost draft. Not done, and recorded as such below.
- **No new capability.** `CreateAgentWorktree` gives every agent its own repository *specifically* so the
  mirror's mount can be read-only "without taking `git commit` away from the agent". What was missing was
  a permitted route, not the permission.

**The gate is the existing one.** `commit_work` asks `WorkerPlanGate.MayWork` — the same predicate
`send_worker_prompt` and `request_verification` ask. A worker still at the gate has no authorised work to
record, and the refusal is the sentence a human reads elsewhere rather than a second opinion about what
"approved" means.

**Two traps, both load-bearing:**

1. **The commit must NOT publish.** `AgentRefWatcher.PollOnce` raises `Advanced` only for an outcome of
   `Published`; publishing eagerly makes the sweep's own publish `Unchanged`, which is `Current` (so the
   snapshot is recorded) and not `Published` (so no event fires). An eager publish would therefore
   silently disarm the trigger for the very commit it exists to react to. The watcher carries the tip
   across on its own tick, exactly as before.
2. **`git add -A` is why 11.1 had to ship with this.** This commit is the thing that would otherwise
   carry Mainguard's own files into the user's history, and the worker's plan file was the other one —
   the instructions and the kickoff turn now both say to write it to `/tmp/plan.json`, outside the tree
   the commit records.

**"Nothing to commit" is not "committed".** A clean tree answers truthfully and the response says
`committed: false`; reporting it as a commit would tell a worker its work is safe while its branch sits
exactly where it was — the original defect wearing a success message.

| # | enforcement removed | result |
|---|---|---|
| M8 | the worker instructions never mention committing (the pre-change behaviour) | **1 failed** / 22 passed |
| M8b | the kickoff turn stops steering the plan file out of the worktree | **1 failed** / 22 passed |
| M9 | the plan gate is not asked before committing | **1 failed** / 12 passed |
| M10 | the commit uses `request.AgentId` instead of the endpoint's own id | **1 failed** / 12 passed |
| M11 | the branch-alignment refusal removed (commit onto whatever HEAD is) | **1 failed** / 14 passed |
| M12 | "nothing to commit" answered as a commit, at the channel | **1 failed** / 12 passed |
| M12b | …and at the worktree | **1 failed** / 14 passed |
| M13 | the commit publishes to the mirror itself (disarming the trigger) | **1 failed** / 15 passed |
| M14 | `git add -A` narrowed to tracked files only | **4 failed** / 11 passed |
| M15 | the shim loses its `commit` subcommand (a handler nothing can reach) | **1 failed** / 13 passed |
| M16 | the shim ships syntactically invalid python | **1 failed** / 13 passed |

**A note on how these were measured, because it nearly produced a false result.** The first pass ran M12
and reported it GREEN. It was not: the harness restored each mutated file with `mv`, which put back the
ORIGINAL mtime — older than the assembly just built from the mutation — so MSBuild considered the project
up to date and the next run tested the *mutated* binary. Every number above was re-measured with the
restore touching the file. A mutation harness that cannot restore is a harness that reports whatever it
likes.

**Found and NOT fixed here**

- **Teardown still does not commit a dirty worktree.** Deliberate, per the rejected option above. The
  worker is now told, in three places, that uncommitted work is lost.
- **M13 is guarded indirectly, and the test says so.** No test drives a live watcher sweep over
  wall-clock time; what is asserted instead is the fact an eager publish would destroy — that straight
  after a commit the MIRROR still lags the agent's own repository, and that the ordinary publish is what
  closes the gap. That is the precondition `Advanced` needs, measured; the event itself is not.
- **Whether a jailed CLI can EDIT files unattended is still unmeasured.** The live run produced a real
  diff, so it could — but that jail restored the owner's own workspace settings, and a first-ever
  session in a fresh repository restores nothing. If edits also stall on approval, the fix is the same
  shape as this one and is a separate, reviewable change.
- **§8.5's folder-trust dialog is still open.**

## 12. The first end-to-end run's next two defects (F1, F2)

§11 got a worker to commit. The very next run got the commit all the way to `Verified` — and then threw
it away.

### 12.1 F1 — stopping a worker DESTROYED the commit that had just been verified

Observed, then reproduced against real git before anything was changed:

```
commit 34adf55 on agent/25e17f73…  → published to the mirror
                                    → auto-verification PASSED
                                    → merge-queue row Verified, primary CTA "Review"
git log refs/heads/agent/25e17f73…  → 34adf55
StopAgent                           → RemoveAgentWorktree(force: true) → `branch -D agent/<id>`
                                    → MirrorMaintenance.AfterAgentDetached prunes unreachable objects
git log refs/heads/agent/25e17f73…  → fatal: ambiguous argument … unknown revision
```

The commit is dangling and gc-eligible. The row still says `Verified` and still offers `Review` on a
branch that no longer exists, and `AgentResumeService` — the "resume the entry" affordance the queue rail
offers a stranded row — refuses it with `AgentBranchMissingException`, because there is nothing to adopt.

**The lifecycle deleted its own output.** `AgentOperatingInstructions.Worker` tells a worker to commit,
report and stop. §11's `commit_work` made the commit possible; the stop those same instructions ask for is
what removed it.

**The boundary: a measurement, not a memory.** `branch -D` may run only when it destroys nothing, and that
question has one exact answer — is this branch's tip already contained in the mirror's own integration
branch? If yes, every object it names is reachable from main and the delete removes a name. If no, the ref
is the only name for at least one commit.

Asked as `AgentRefMediator.MayReap` → `AgentBranchReapVerdict`, and asked there because that class already
owns both halves of the arithmetic: rule 2's `merge-base --is-ancestor`, and rule 4's "the mirror's OWN
default branch, never the literal `main`". A second copy of either is how one of them becomes decorative.
`Undecidable` (git could not answer) refuses the delete: this value gates a destructive operation, so the
unknown answer is "no" — and the failure mode being prevented is a probe that errors and reads as
"contained in main", which would delete on exactly the repositories git is unhappy with.

**Why not the merge queue's state, which was the obvious answer.** Because `Working` is the queue's state
for BOTH an agent that never committed and one that committed a second ago: the readiness trigger fires on
`AgentRefWatcher.Advanced`, which happens on the watcher's own clock, and §11.2 already states the
consequence — "a worker that never commits is indistinguishable, to every mechanism downstream, from one
that did nothing." A rule keyed on the queue would therefore go on destroying work for the whole window
between the commit and the verification, which is the widest window in the loop. Queue state records a
decision that was taken; the git fact records what exists, and only the second can answer "will this delete
lose a commit". (`MergeQueue`, `MergeQueueProvisioner` and `AgentRefMediator` were all read for this; the
mediator is the one that can decide.)

**"No residue" is not deleted, it is scoped to what it was written about.** The rule exists because a
mirror that accumulates a ref per agent forever can never be pruned — MG-3 §4 only deletes unreachable
objects, and a live ref makes them reachable. That argument applies in full to a branch that never left the
base commit: every coordinator, every failed spawn, every worker that did nothing. Those are still reaped.
It does not apply to a branch carrying a commit, where the ref is not residue but the only name for work.
And a branch becomes reapable again the moment its work reaches main, so the ordinary lifecycle still
cleans up after itself — `Teardown_ReapsABranchOnceItsWorkIsContainedInMain`.

**A kept branch is not silent**: a warning through the same sink MG-3's refusals use, plus the G-17
`agent_branch_kept` event carrying the sha, the outcome and the reason. An operator who stops an agent and
finds a branch left behind can find out why.

**The one deletion taken on a caller's word: `DiscardAgentBranch`.** The external-PR intake's
`ReleaseWorkerAsync` runs when a pull request closed upstream or a human discarded its entry, and there the
new rule is wrong twice over — those commits were fetched FROM the pull request and still live there, and
`pr-<n>` is a REUSED id, so a kept branch makes the next intake of that number collide with
`CreateAgentWorktree`'s duplicate refusal on every poll, forever. Shipping F1 without this would have
traded a data-loss bug for a silent, permanent intake failure. It is a separate, named method rather than a
`force` flag, audited with the sha it removed, and it is the only path that deletes work without first
proving the delete is free. The release path's early `return` after a successful stop was removed in the
same change: a discard behind it would have been unreachable on exactly the case it exists for.

**Blast radius.** Three production callers reach `RemoveAgentWorktree` and all three improve:
`SandboxAgentLauncher.TeardownAsync` (the stop — F1 itself), `SwarmReconciler` (a jail whose container
vanished — its entry becomes genuinely resumable rather than only nominally so), and
`RepoSyncGrpcService.RemoveWorktree` (the operator asked to remove a *worktree*, not to delete a branch).

### 12.2 F2 — the coordinator could never be told the job finished

After the branch reached `Verified`, the coordinator reported: *"Worker 25e17f73 (claude-code) — Working …
it's past the pre-plan stage and actively working."* `ListAgents` agreed (`state=Working`).

It was not wrong about what it could see. `AgentSession.State` is a **liveness** word, written once by
`AttachSandbox`, and nothing in the session's own world ever learns that a branch verified. Contract §3's
`get_worker_status` is the coordinator's only window onto its fan-out, so a status that cannot ever say
"done" makes a coordinator structurally unable to report the completion of its own work.

**The honest states are the ones the queue already has**, and the transition belongs where the state
actually moves: `MergeQueue.SetStateLocked`, surfaced as an `onStateChanged(agentId, newState)` seam that
`MergeQueueProvisioner` wires to the SAME `IAgentSupervisor` its keep-alive `MarkRunState` already writes
through. No parallel state machine: the words are `WorkerMergeState`'s own, the queue remains the only
thing that decides them, and each carries the sentence a human reads ("Verified against the current main —
waiting for a human to review and merge it").

The session store already treats those words as first class, and says so: `AgentSessionReconciler`'s drift
pass corrects **only the pause axis**, refusing to flatten "orchestration meaning the container cannot know
— RateLimited, Yielding, AwaitingReview" back to `Working`. `AwaitingReview` is a `WorkerMergeState`. This
change is the writer that member was already written for.

Three properties, each with a test rather than a comment:

- it fires on REAL moves only (a `EnsureEntry` row write moves nothing and reports nothing) — a
  notification for a transition that did not happen describes something other than the state machine;
- it carries the agent id, so two workers under one coordinator cannot be told each other's outcome;
- **it can never damage a transition.** The row is persisted before the sink runs, so an exception escaping
  would leave the store and memory disagreeing about a branch's merge eligibility. A throwing sink is
  driven directly (`AThrowingStateSink_CannotBreakATransition`) rather than assumed.

The paired negative is the one that matters most: a RED verification returns the entry to `Working` and
must never report `Verified`. A status that can say "done" is only worth anything if it declines to.

**Called under the queue's lock, deliberately.** The alternative — deferring to the ~15 sites that raise
`Changed` outside it — is a second, hand-maintained list of transition points, and the one that gets
forgotten is the one that silently stops reporting. The sink is a bounded, non-blocking in-memory write
(`AgentSessionStore.MarkState` → `TryWrite`), strictly cheaper than the SQLite `Save` the same method
already performs under the same lock, and nothing in the session store ever calls back into a queue.

### 12.3 The ~20 pre-existing dead rows: a coherent story, and no migration

They are the branch-never-moved case. A worker that never committed left `agent/<id>` pointing exactly at
the base commit, so the old unconditional `branch -D` and the new conditional one do the same thing to it —
it is reaped, correctly, and there was never anything on it to lose. Those rows are honest: no work is
recoverable, `Resume` correctly refuses ("that branch no longer exists — discard the entry instead"), and
`Discard` is the right action, which the queue already reaches from every non-terminal state.

So: **no migration, and none is possible for anything else.** If any of those rows *did* once carry a
commit, its objects were pruned by `AfterAgentDetached` on the same teardown; nothing in a database brings
back a git object that is not on disk. What the fix buys is that no NEW row of that shape hides destroyed
work — and `WhenTheBranchIsGoneToo_ResumeRefusesAndNamesIt_AndBuildsNoJail` now constructs that exact state
the way it really arose (stop an agent that never committed) instead of the way it used to (stop an agent
that had).

### 12.4 The mutation log

Every guard was removed, the suite watched failing, and the file **touched** on restore — a `mv`-style
restore preserves the original mtime, MSBuild then skips the rebuild, and the next run tests the MUTATED
binary and reports green. §11.2 recorded that trap; every number below was measured with a touching
restore, and the whole set was re-run green afterwards from a clean tree.

| # | enforcement removed | result |
|---|---|---|
| N1 | `branch -D` unconditional again (the pre-change behaviour) | **4 failed** / 18 passed |
| N2 | `Undecidable` counts as permission to delete | **1 failed** / 21 passed |
| N3 | the reap dropped entirely — nothing is ever deleted ("no residue" gone) | **3 failed** / 19 passed |
| N4 | a kept branch leaves no `agent_branch_kept` audit event | **1 failed** / 21 passed |
| N4b | the kept-branch warning no longer names the branch it kept | **1 failed** / 21 passed |
| N5 | `DiscardAgentBranch` refuses a branch that carries work | **1 failed** / 42 passed |
| N6 | the intake's release stops discarding the branch | **2 failed** / 19 passed |
| N7 | the intake's release keeps its early `return` after a successful stop | **1 failed** / 20 passed |
| N8 | `onStateChanged` is not passed to the queue at all | **2 failed** / 28 passed |
| N9 | the notice fires on every row write, not only on a real transition | **1 failed** / 35 passed |
| N10 | the state sink is unguarded, so a throwing sink aborts the transition | **1 failed** / 35 passed |
| N11 | a FAILED verification is reported as `Verified` | **1 failed** / 29 passed |

**One mutation survived and was replaced, which is worth recording.** The first N4 spelling gated the audit
on `Outcome != CarriesWork` — but `CarriesWork` is the only outcome the test reaches that line with, so the
guard was a no-op and the suite stayed green. That is a defect in the mutation, not evidence about the
code: a mutation has to change behaviour on the path the test actually walks, or its green says nothing.
Re-spelled as an unconditional early return, it went red.

### 12.5 Deliberately left

- **`MirrorMaintenance` is unchanged.** A kept branch makes its objects reachable, so the prune two lines
  later has nothing to take. The size guard already surfaces a mirror that grows, and the natural
  reclamation is 12.1's "a branch becomes reapable once main carries it" rather than a new sweeper.
- **The teardown still does not commit a dirty worktree** — unchanged from §11.2's recorded decision. A
  human's Stop means stop. What changed is that a worker which DID commit no longer loses the commit.
- **A stopped worker leaves the coordinator's fan-out entirely** (the session record is removed), so
  `get_worker_status` answers "no worker '<id>'" rather than a terminal state. F2 fixes the reported defect
  — a LIVE worker whose branch verified — and this is a different, larger question about how long a
  coordinator can see a finished worker. It needs the session record to outlive the jail, which is a
  lifecycle change, not a reporting one.
- **`QueueEntryResumeDockerTests` is intermittently red in the FULL `RequiresDocker` run** (3 of 5 passes
  green; the two reds named *different* tests in that class, and the class is green every time it is run on
  its own). This is the docker-suite ordering flake `e42ebea8` and `9f629dc2` both recorded and neither
  diagnosed. This change made it likelier by adding a third jail-spawning test to the class, then less
  likely again by having each test stop the jail it leaves running — the class's own cleanup runs only
  after every test in it, so a jail left standing competes with the next spawn for admission headroom and
  the bridge pool. Reported as still-open rather than as fixed.

---

## 13. A contract §3 change — `spawn_worker` takes a title, and the brief stops being the task

**Decided by the owner, 2026-08-29.** This section is longer than the diff because §3 says a change to
the coordinator's surface is "a deliberate contract change, reviewed as such — not an implementation
detail", and this is one. [`coordinator-contract.md`](coordinator-contract.md) §3 was amended in the
same change.

### 13.1 What was found

Phase 2 §2.2/§2.3 and the worker's `MAINGUARD.md` both say a worker is given a **brief** — *"what you
are here to plan (never the task itself)"* — and that the task is withheld until a human approves the
plan. In the shipped daemon, `mainguard-plan brief` returned the **task, verbatim**. Three lines, in
three files, made that true:

```python
# AgentSpawnShim — the coordinator's CLI sent no title at all
request = {"op": "spawn", "agentKind": argv[2], "taskPrompt": " ".join(argv[3:])}
```
```csharp
// AgentSpawnService.SpawnWorkerAsync — so the daemon derived one
heldTaskTitle: request.Title ?? request.TaskPrompt ?? "Untitled task",
```
```csharp
// WorkerPlanGate.PlanningBriefFor — which is what `brief` returns
return FindLocked(workerAgentId)?.Title;
```

`brief == task`, by fallback rather than by intent. The documented separation collapsed, and **nothing
was red**: every daemon-side test constructed its own `AgentIpcRequest` and supplied a `Title` no real
coordinator could send, so the one component that never appeared in a test — the shim — was the one
component that was wrong. `WorkerPlanChannelIpcTests.ACoordinatorSpawnedWorker_GetsAPlanShimAndABrief_
ButNotItsTask` asserted the brief equalled the string it had spawned with, and passed on the defect it
was named for.

**Verified before anything was changed**, in a real jail (`SandboxFixture` + the real shim, no paid
CLI): running `mainguard-agent spawn claude-code --title 'Fix the token clock' --task rewrite …` from
inside a container, the daemon received

```
title='' taskPrompt='--title Fix the token clock --task rewrite TokenClock so expiry is …'
```

— no title, and the flags swallowed into the prompt. A title-less spawn was also accepted, exit 0.

### 13.2 The decision, and the spelling

The coordinator supplies a title separately from the task. The CLI form is a **single-sourced
constant**, `AgentSpawnShim.SpawnUsage`, interpolated into the shim's `--help`, the shim's refusals, and
`AgentOperatingInstructions.Coordinator` — three renderings of one command is how they come to disagree:

```
mainguard-agent spawn <agent-kind> --title "<short title>" --task <the task ...>
```

**Why not a second positional** (`spawn <kind> <title> <task ...>`), which is the obvious repair. The
caller is a language model that has read the usage once, and a positional pair fails it three ways: the
order can be remembered backwards (and the failure — the task on the approval card, the title withheld —
is exactly inverted, not merely wrong); an unquoted title silently eats the first word of the task; an
unquoted task silently extends the title. All three are *silent*.

Two named flags fix each of those:

| slip | positional form | this form |
|---|---|---|
| arguments swapped | silent, and inverted | impossible — each argument says which it is |
| task not quoted | silently absorbed into the title | fine: `--task` takes every remaining word |
| title not quoted | silently truncates the title, corrupts the task | **detected** — the stray words land where `--task` must be, and the shim says so |

The last row is the load-bearing one. `--title` takes exactly ONE argument, so a quoting slip cannot
produce a plausible-looking parse; it produces a shape the parser can see. The refusal names the cause
(*"--title must be ONE quoted argument (found 'the' where --task was expected — quote the title)"*) and
prints the working form. And the argument that is *hardest* to quote — the long free-form task — is the
one that needs no quotes at all.

### 13.3 What happens when the coordinator omits the title: **refuse**

Not derive, not truncate, not fall back. A derived brief is precisely the defect; a truncated task would
be worse, because it would *look* like a title while still leaking the work.

- **The shim refuses locally**, before any round trip, and prints the form. That is an affordance, not
  the enforcement — the shim is a file in the jail's mount and a jailed process can speak the socket
  itself.
- **The daemon refuses**, and that is the enforcement. `WorkerPlanGate.RefuseBrief(title, taskPrompt)`
  is the single authority; it lives on the gate because the gate is the one object that holds both
  strings and the sole source of `PlanningBriefFor`. It is called from `WorkerPlanGate.Hold` (which
  throws, so no caller can store a brief that is not one), from `AgentSpawnService.SpawnAsync` before
  `_store.Spawn` mints anything, and from `SpawnWorkerAsync` at the channel.

**Why the channel call is required and not merely defensive.** `SpawnAsync` reads *"neither a title nor
a task"* as *"this spawn is not plan-gated"* — that is how an operator's own spawn is spelled. So a shim
request carrying neither would not have been refused downstream: it would have produced a Managed worker
with **no plan gate at all**, which is strictly worse than the defect being fixed. The check at the
channel is what closes that, and `ASpawnWhoseBriefIsMissingOrIsTheTask_IsRefused_AndSpawnsNothing`
asserts the session count, not just the refusal.

The rules, each with its own refusal text and its own row in the theory: a title is required; a task is
required; the title is one line; the title is at most `MaxBriefLength` (120) characters; and **the title
must not equal the task**. That last one is a tripwire for exactly this defect returning through the
front door, and the code says so — it is not claimed to catch a title that merely paraphrases its task.

### 13.4 The UI consequence, which is why "short" is in the rules

The title is what a human reads on the plan-approval card (`PlanGateView`, `{Binding Title}`). So the
constraints are not stylistic:

- the 120-character cap and the single-line rule are there because a card headline that is a paragraph
  is a card nobody reads — and "paste the task in as the title" is the shape the old fallback produced
  automatically, every time;
- the coordinator's operating instructions **say** the title is the headline the human decides from, and
  tell it to write one like a pull-request title, with a worked example. A model that is not told this
  optimises the title for itself;
- `AgentOperatingInstructionsTests.TheCoordinatorIsToldThatTheTitleIsTheBrief_AndTheTaskIsWithheld`
  pins that the text says all of it, so the guidance cannot be edited away silently.

The card's own title still prefers the worker's `present` title and falls back to the brief, which is
now a real headline either way — so the card improved without a UI change.

### 13.5 Keeping the instructions honest

`AgentOperatingInstructionsTests` already pinned the coordinator text against
`AgentIpcRequest.CoordinatorOps` in both directions. That check is about *which* ops exist and could not
have caught this: the op is still `spawn`. So it gained two:
`TheCoordinatorIsTaughtTheSpawnFormTheShimActuallyParses` (the instructions carry
`AgentSpawnShim.SpawnUsage` verbatim, and so does the script), and the meaning test above. The shim's own
end is covered by `AgentIpcProtocolTests.TheShimsSpawnParser_…`, which runs the real `main()` under
python3 with the transport stubbed — see §13.6 for why it does not call the parser directly.

The op set itself is unchanged — `CoordinatorOps` still has exactly its five members and
`CoordinatorRoleLockTests` still set-equals it against the served surface. This change alters the shape
of one op, not the surface.

### 13.6 The mutation log — every guard watched failing

Each guard was broken, the named test run, and the failure observed; the guard was then restored with
`git checkout --` **followed by `touch`**, because a restore that preserves mtime lets MSBuild skip the
rebuild and re-run the tests against the *mutated* binary.

| # | mutation | went red |
|---|---|---|
| M1 | `RefuseBrief` returns `null` unconditionally | `WorkerPlanGateTests` — 10 failures across the theory, the boundary test and `HoldRefuses…` |
| M2 | drop the title-equals-task check | `ABriefThatIsMissingOrIsTheTask_IsRefused` (both equality rows) + `HoldRefusesABriefThatIsTheTask_AndHoldsNothing` |
| M3 | drop the length cap | `AnOverLongTitle_IsRefused_AndTheBoundaryItselfIsNot` |
| M4 | drop the single-line check | `ABriefThatIsMissingOrIsTheTask_IsRefused(title: "Fix\nthe clock")` |
| M5 | `Hold` no longer calls `RefuseBrief` | `HoldRefusesABriefThatIsTheTask_AndHoldsNothing` |
| M6 | `SpawnWorkerAsync` drops the channel check | `ASpawnWhoseBriefIsMissingOrIsTheTask_IsRefused_AndSpawnsNothing(null, null)` — and **only** that row, which is the point: the other rows are still caught downstream, and the one the channel check exists for is the request that would otherwise mint an ungated worker |
| M7 | `SpawnAsync` drops the pre-`_store.Spawn` check | `SpawnAsync_WithAPlanGatedTaskAndNoBrief_ThrowsBeforeMintingASession` — it still throws (from `Hold`), but a session record is left behind, which is what the assertion catches |
| M8 | `PlanningBriefFor` returns `TaskPrompt` — **the defect itself** | `ACoordinatorSpawnedWorker_GetsAPlanShimAndABrief_ButNotItsTask`, `BackendWorkflowSimulation.Phase2_…`, and 3 in `WorkerPlanGateTests` |
| M9 | the shim's `main` restores the old `taskPrompt: " ".join(argv[3:])` | `AgentIpcJailDockerTests.TheRealShimsSpawn_SendsTheTitleAndTheTaskAsSeparateFields` + `…RefusesTheOldTitlelessForm_…` — and, after the fix below, 6 rows of the host-side theory |
| M10 | the shim derives a title from the task instead of refusing | host-side theory + `…RefusesTheOldTitlelessForm_AndSaysWhatToRunInstead` |
| M11 | the shim stops detecting a mis-split (unquoted) title | host-side theory, the `ONE quoted argument` row |
| M12 | the instructions revert to the old positional spawn line | `TheCoordinatorIsTaughtTheSpawnFormTheShimActuallyParses` |
| M13 | the instructions drop the `--title`/`--task` section | `TheCoordinatorIsToldThatTheTitleIsTheBrief_AndTheTaskIsWithheld` |

**One mutation exposed a weak test, and the test was fixed rather than the mutation excused.** M9 changed
only the shim's `main`, not its `spawn_request`, and the host-side test called `spawn_request` directly —
so it stayed **green** while the shim on disk sent no title. A correct parser that `main` does not route
through is exactly the shape of the original defect. The test now loads the shim, **stubs `call`, and
runs the real `main()`**, so it covers the parser, the dispatch, and the fact that a refused spawn never
reaches the daemon at all. Re-run against M9 it goes red, as it should have the first time.

### 13.7 What this does not do

- **The worker's `present`/`revise` title is still worker-supplied and still falls back to the brief.**
  That is a different string (the plan's own headline) and the fallback there is to a real title now, so
  it was left alone.
- **`CoordinatorAgent.SystemPrompt` / `CoordinatorTools.spawn_worker` already took a title** and are
  unchanged. They are the un-wired in-process tool API; the defect was that the *shipped* CLI had no
  equivalent. This change gives the CLI parity with the API rather than the reverse.
- **The old title-less invocation now fails.** That is intended and is the reason this is recorded as a
  contract change: an existing coordinator transcript that used it was producing a worker whose brief was
  its task, and the refusal names the form that works.

## 14. Defect G1 — one symlink in an agent's outbox kills the daemon

`a71bf2c3` gave every IPC endpoint an `outbox/` directory, bind-mounted **read-write** into the jail on
substrates whose mount cannot carry an AF_UNIX socket. It stated one bound on the capability that grants:

> anything past the cap is deleted unread rather than read into the daemon's memory

The cap inspected the wrong object, and the directory had no aggregate bound at all.

### 14.1 What was reproduced, before anything was changed

The daemon's exact sequence, run by hand on this machine:

```
ln -s /dev/zero zero.req
FileInfo("zero.req").Length   ->  9          # the length of the STRING "/dev/zero"
                                             # (LinkTarget = "/dev/zero", Attributes = ReparsePoint)
File.Move(req, claim)         ->  moves the LINK; rename does not follow
File.ReadAllText(claim)       ->  follows it. RSS passed 4.2 GB and was still climbing
                                  when the process was killed; it ends in OutOfMemoryException
```

So a coordinator — or any jail with an outbox — creates one symlink and the daemon dies. That is not one
agent's channel: the daemon serves **every** running agent's control plane out of that process, and the
kill needs no capability, no network, and one shell builtin.

Three further measurements from the same session, each of which turned out to matter more than it looked:

- **A FIFO is invisible to managed code.** `mkfifo x.req` needs no capability at all in a directory the
  jail can write, and `new FileInfo(x)` reports `Attributes = Normal`, `LinkTarget = null`, `Length = 0`
  — identical to an ordinary empty file. .NET 10 exposes **no** file-type API; there is nothing to ask.
- **The blocking open is on the poll loop's own thread.** `File.ReadAllTextAsync` opens synchronously
  before its first await, and `_ = ServeOutboxRequestAsync(...)` runs synchronously up to that point on
  the caller. Measured: the whole sweep parks, so one FIFO ends that agent's file-framed channel for
  good — worse than the "parks the handler task" the report expected.
- **Stat and read are two syscalls on a path the jail owns.** A regular file that passes the cap can be
  grown through a descriptor the jail kept open across the claim; `rename` does not invalidate it.

**Confidentiality was checked and holds.** A line that does not parse produces the fixed string
`"malformed request (expected one JSON line)"` toward the jail and a fixed `reason` in the observer; the
only jail-supplied text that reaches a log line is `op`, control-stripped and truncated to 40 chars by
`ChannelObserver.Echo`. Nothing that was read is echoed anywhere. The exposure is availability, exactly
as reported. (One adjacent note, unchanged and pre-existing on both framings: a handler that throws
returns `ex.Message` to the jail, so a daemon-side exception could name a host path. It is daemon code
throwing, not attacker-shaped input, and it is not touched here.)

### 14.2 The design, and the one that was measured wrong first

**The rejected design was `O_NOFOLLOW`,** and it is worth recording why, because it is the obvious answer.
.NET 10 has no no-follow open — `FileStreamOptions` carries no such flag — so it means P/Invoking
`open(2)` with a hand-written flag table. That table is **architecture-dependent on Linux**: `O_NOFOLLOW`
is `0o400000` on x86-64 and `0o100000` on arm64. The x86-64 value was probed inside a real Linux/arm64
container against a real `/dev/zero` symlink and it did **not** refuse — it opened the link and read
64 KiB of zeros. A guard built on a wrong magic number looks exactly like a guard. That is a bad
foundation for a security boundary, and it was caught only because it was measured.

**What shipped instead rests on one structural fact and no magic numbers: the claim leaves the jail's
directory.** `outbox/` is the read-write mount; its *sibling* `inflight/` is inside the read-only IPC
mount and outside it. Claiming by renaming across that line (same filesystem, so `rename(2)` still gives
the atomic dispatch-exactly-once property the plan gate depends on) means the claimed entry has **no
second writer**. Every check after it is therefore about the same inode that is then opened:

| Guard | What it refuses | Why it is sound here |
|---|---|---|
| `LinkTarget is not null` | a symlink, unread | lstat semantics, on a frozen entry — no check-then-open race |
| `Length <= 0` | FIFO, socket, device node, empty file | every non-regular inode stats as zero-length, and a zero-length request is malformed anyway — so one test covers the whole class without asking a managed API a question it cannot answer |
| read stops at `cap + 1` | anything bigger, however it got that way | the ceiling is on the **read**, so the grow-after-stat window has nothing to run into. The `Length > cap` check above it is a cheap first look, not the bound |

The aggregate bound is `MaxOutboxFiles = 64` and `MaxOutboxBytes = 1 MiB` (16 full-size requests). A shim
writes one request and blocks for its answer, so a healthy outbox holds a handful of files; 64 is roughly
twenty concurrent calls. **On breach the directory is reclaimed and polling CONTINUES.** Both
alternatives are worse in opposite directions: leaving the files is the defect (a jail fills the host's
disk 64 KiB at a time inside `~/.mainguard`, degrading its own sweep as it goes), and *stopping* the poll
would let a jail switch off a control plane the human depends on too — a jail does not get that decision.
Recovery is automatic, so a legitimate burst that overshoots costs a retry, never the channel. The sweep
also stops enumerating the moment the count passes the limit, so a pass costs the **limit**, never the
directory.

**Stale `.busy`: swept when the endpoint comes UP, never on a timer.** Age cannot be the signal — a
worker's plan presentation parks on its claim for as long as the human takes, and a TTL would be a timer
on the human. At the moment an endpoint is created nothing can be in flight by construction, so anything
left in either directory belongs to a daemon that died mid-call and goes.

Every refusal goes through the existing `ChannelObserver.Rejected` (capped at five lines and one audit
event per endpoint, because the directory is jail-writable). No second reporting path was built. The
over-quota report fires on the **transition** into breach rather than every 100 ms, so a jail that camps
over the line cannot spend the endpoint's whole rejection budget on one condition.

A refused request is deleted and **not answered**, matching the treatment oversize requests already had:
a request the daemon will not read is one it has no honest answer for, and writing a response per refusal
into a jail-writable directory would be a way to make the daemon fill that directory on demand.

### 14.3 The mutation log

Each guard was reverted, rebuilt (`touch`ing the restored file — `mv` preserves mtime and MSBuild will
happily skip the rebuild and test the mutated binary), and watched.

| # | Mutation | Expected | Result |
|---|---|---|---|
| M0 | the whole reader back to `FileInfo.Length` + `ReadAllText` — the shipped code | 2 red | `ASymlinkedRequest…` and `ANonRegularRequest…` red, 8 green |
| M1 | symlink guard off | red | `ASymlinkedRequest…` red (on the audited REASON, so the ceiling's refusal cannot stand in for it) |
| M2 | non-regular / zero-length guard off | red | `ANonRegularRequest…` red — 15 s, the poll loop parked |
| M3 | stat pre-filter removed, read ceiling kept | **green** | `AnOversizeRequest_IsDeletedUnread` green — the cap is the read, which is the TOCTOU fix stated as a test |
| M4 | stat pre-filter AND read ceiling removed | red | `AnOversizeRequest_IsDeletedUnread` red |
| M5 | aggregate bound off | red | `AnOutboxPastItsAggregateBound…` red |
| M6 | claim renamed within the outbox, as before | red | `AClaimedRequest_IsRenamedOutOfTheDirectoryTheJailCanWrite` red |
| M7 | startup leftover sweep off | red | `AnEndpointComingUp_ClearsWhat…` red |

M1 is the interesting one: with the symlink guard off, the read ceiling still refuses the request. That is
defence in depth working, and it is exactly why the test asserts the audited reason rather than the
effect — a test that cannot tell which guard fired cannot show that this one exists.

### 14.4 Left alone, deliberately

- **The response file is still written into the jail-writable outbox.** It has to be: that is how the
  shim reads it. It is bounded by the dispatch limit and reclaimed with everything else on a breach.
- **A jail can still forge its own `<ticket>.res`** and read its own forgery. It is talking to itself;
  nothing crosses a trust boundary.
- **`ex.Message` from a throwing handler still reaches the jail** on both framings (§14.1). Pre-existing,
  daemon-authored text, and a different change.

---

## 15. The concurrent stress run's three defects (G2, G3, G4)

A stress run of three concurrent coordinators, on the daemon built from `92083bd7`, produced three
regressions in commits landed the same night. Each was reproduced on this machine before anything was
changed.

### 15.1 Defect G2 — one jail, two briefings, and they disagreed about the machine

`2414130c` made the coordinator's instructions render the installed agent kinds per spawn, and said why
in `SpellKinds`' own doc-comment: the text and the enforcement are *"the same claim about the same set,
made to the same reader seconds apart, and two renderings of one set is how they come to disagree."*

There were two renderings.

```
SandboxAgentLauncher.InstructionsFor(role, _adapters)      -> Coordinator(shim, adapters.InstalledKinds())
AgentIpcServer.Endpoint.Start (~line 415)                  -> For(role, SandboxShimPath(role))
                                                              …installedKinds defaulted to null
```

So in one and the same jail the `--append-system-prompt` copy named every installed CLI and the
`MAINGUARD.md` copy — the one a CLI opens unprompted — said `(none installed on this machine)`. A
coordinator that read the file and believed it has no correct move: every kind it can name is refused,
and the refusal it would get names a list its own briefing said was empty.

**Reproduced before changing anything**, daemon-side, in a spawn through the real service against a temp
adapter registry (`CoordinatorSpawnKindTests.TheTwoDeliveriesOfOneJailsInstructions_AreTheSameText`):

```
Expected: ···"ed on this machine\n\n`probe-cli`, `second-"···   (the launch-line copy)
Actual:   ···"ed on this machine\n\n(none installed on th"···   (the file beside the shim)
```

**The fix is not the missing argument.** Passing `adapters.InstalledKinds()` at the second call site
would have restored agreement and left the defect's actual shape untouched: a rendering that can be
reached without the thing it describes. Two changes remove that shape.

- **You cannot render without the catalog.** `Coordinator` and `For` take an
  `InstalledAdapterCatalog` — required, no default — instead of an optional
  `IReadOnlyCollection<string>?`. The list is no longer a value a caller carries; it is read from the
  same object the `spawn` refusal reads, at the moment of rendering. A third call site therefore gets the
  machine's real state whatever it forgets, because there is nothing left to forget. The shim path went
  the same way: it was a second caller-supplied string that both sites happened to spell identically, and
  it is now derived from the role.
- **A delivery site does not render.** `AgentIpcServer.CreateEndpoint` takes the *rendered* text as a
  required argument and writes it. The daemon's one production rendering is
  `SandboxAgentLauncher.InstructionsFor(role)`, an instance method bound to that launcher's own catalog;
  `AgentSpawnService` asks it for the string before creating the endpoint (the endpoint is a mount source,
  so it must exist first) and the launcher puts the same string on the launch line moments later.

**Why an instance method and not a static over a catalog argument.** The interesting failure is not "the
text can name kinds" — it is "the text names the kinds *this daemon* has". A static invites a caller to
supply some other catalog, and G2 was that shape with the argument omitted altogether.

`NoWayToRenderTheCoordinatorText_WithoutTheInstalledCatalog` asserts the structural half by reflection,
because "nobody will add an overload" is the kind of promise this file exists to stop making.

**What this deliberately does not change.** `SpellKinds` keeps its list parameter: the refusal path
(`CoordinatorSpawnGate.RefuseUnknownKind`, `AgentSpawnService.SpawnWorkerAsync`) already holds
`_launcher.InstalledAgentKinds` and is the *enforcement*, not a description that can go stale.

#### The mutation log

Each guard was broken, the named tests run, the failure observed, then `git checkout --` **followed by
`touch`** — a restore that preserves mtime lets MSBuild skip the rebuild and re-run the suite against the
mutated binary (§12.4). All numbers are `CoordinatorSpawnKindTests`, 10 tests.

| # | mutation | result |
|---|---|---|
| M1 | the endpoint renders its own copy again, from a catalog that resolves to nothing — **the shipped defect** | **2 failed** / 8 passed (`TheTwoDeliveries…`, `TheInstructionsFileAJailOpens…`) |
| M2 | the catalog becomes an optional argument again | **1 failed** / 9 passed (`NoWayToRenderTheCoordinatorText…` — and *only* that one, which is the point: agreement between two copies is restorable by accident, the ability to render from nothing is not) |
| M3 | the launcher renders from a fresh empty catalog instead of its own | **2 failed** / 8 passed |
| M4 | the endpoint is handed the other role's instructions | **2 failed** / 8 passed |

M2 is the one worth reading. It leaves both deliveries agreeing — they still come from one string — so
every equality assertion stays green, and only the reflection test notices. That is exactly the state the
codebase was in before `AgentIpcServer` was written: a latent defect with a green suite.

### 15.2 Defect G3 — the quoting advice was false, and the failure it caused was invisible

`2a087c71` §13.2 argued for two named flags partly on this ground:

> the argument that is *hardest* to quote — the long free-form task — is the one that needs no quotes
> at all.

That is true of `spawn_request`, which joins `argv[6:]`. It is false of the coordinator, because the
coordinator does not produce argv. It produces a **command line**, which its CLI hands to `bash -c`.

**Reproduced first, at a shell on this machine:**

```
$ bash -c '... spawn claude-code --title "Fix the clock" --task rewrite add() and multiply() so they ...'
bash: -c: line 1: syntax error near unexpected token `('
exit=2
```

The shim never runs. Task text describing code carries `()`, `&&`, `|`, `$`, `*` and quotes constantly,
so this is not an edge case — in the stress run two of three first spawns died here, exit 2, **zero
daemon log lines**, and three coordinators stalling read as three coordinators thinking.

#### Both halves, and why "a more robust invocation" is not one of them

**(a) The advice is made true.** `AgentSpawnShim.SpawnUsage` becomes
`spawn <agent-kind> --title "<short title>" --task "<the task ...>"`, and because that constant is the
single source for the shim's `--help`, the shim's refusals and the coordinator's instructions, all three
move together. The instructions now say *why* — a shell reads the line first — and name the exact error
a coordinator will see, with a worked example containing parentheses. Naming the symptom matters: a model
that has seen `syntax error near unexpected token` in its own transcript can connect the two.

**The parser is unchanged and still joins a multi-word tail.** An existing coordinator using the old form
keeps working whenever its text happens to be shell-safe, so nothing regresses; what changed is which
form is *taught*. That is the whole of the "more robust invocation" available here — the hazard class
shrinks from "any shell metacharacter in free-form English" to "a quote character in the text", which is
the one a model is best trained to handle.

**Why nothing stronger was built.** A `--task-file`, a heredoc form, or a stdin form would each be
immune, and each adds a second spelling of the one command a coordinator has. §13.2's argument against a
second positional applies verbatim to a second *form*: the caller is a language model that read the usage
once, and the failure mode of two forms is that it composes them. The single quoted form is one thing to
remember and it is shell-complete.

**(b) A spawn that fails leaves a record.** This is the half that survives the advice being ignored.
`report_refused_spawn` sends, over the channel the jail already has, the spawn the shim could not build —
so the daemon refuses it, logs a warning naming the coordinator, and appends `shim_spawn_refused`.

- **Nothing is derived.** A field the parse did not establish is sent absent. A spawn missing either a
  title or a task is refused at the channel before anything is minted (§13.3's `RefuseBrief` call, which
  exists precisely so a request carrying neither cannot become an ungated worker), so a report can never
  be served. `AgentIpcJailDockerTests.TheRealShimsSpawn_RefusesTheOldTitlelessForm_…` now asserts that —
  it used to assert that nothing reached the daemon at all, which is a statement about a round trip
  rather than about the defect.
- **This reverses §13.3's "before any round trip".** That sentence defended a local refusal as cheap,
  and it is; what it did not weigh is that a refusal only the jail can see is a failure nobody can debug.
  The local diagnosis is still what the coordinator reads — only the shim saw argv, so only the shim can
  say *"found 'the' where --task was expected"*.
- **The third refusal branch was silent, and it is the one reports land in.** Two of
  `SpawnWorkerAsync`'s three refusals logged and audited; the "no agent kind" branch answered the jail
  and told the operator nothing. Three copies of one report is how one comes to be missing, and one was:
  it is now `RefuseShimSpawn`, called from all three.

**What is still invisible, stated rather than papered over.** A line the shell refuses to parse never
starts the shim, so nothing in this repository can report it — the daemon's only shadow of it is the
existing `ipc_channel_silent` warning, and only for a coordinator that never calls at all. That is the
reason (a) exists. One diagnostic property does fall out of (b): after this change, `exit 2` from a
coordinator's spawn **with no daemon-side record** can only be the shell.

#### The mutation log

| # | mutation | went red |
|---|---|---|
| M5 | `SpawnUsage` reverts to the unquoted `--task <the task ...>` | `TheCoordinatorIsToldAShellReadsItsCommandLine_AndIsNotToldTheTaskNeedsNoQuotes` — 1 of 35 |
| M6 | the instructions revert to "the long one needs no quotes at all" | the same test — 1 of 14 |
| M7 | the shim stops reporting a spawn it could not build | `TheShimsSpawnParser_…` — **5 of 21**, every refusal row |
| M8 | the report derives the task it could not parse | `TheShimsSpawnParser_…` — 5 of 21 |
| M9 | the kind-less refusal stops being recorded | `ASpawnTheShimCouldNotBuild_IsRecorded_AndNotJustAnswered` — 1 of 11 |

**And the shell test was checked for vacuity**, because it exits early where `bash` or `python3` is
absent and a silently-skipping test proves nothing. Inverting its assertion produced the real thing:

```
String:    "bash: -c: line 1: syntax error near unexp"···
Not found: "NOT-A-REAL-STRING"
```

`TheTaughtSpawnForm_SurvivesAShell_AndTheOldUnquotedOneDoesNot` runs the taught line through a real
`bash -c` and then the shim's own `main()`: the quoted form arrives with `add()` intact, the unquoted
form never reaches the shim, and — the point — reports nothing, because nothing ran.

### 15.3 Defect G4 — `mainguard-plan commit` destroyed the record it exists to create

A worker sent a subject, a blank line and two body paragraphs. What landed on `agent/<id>` had every
newline replaced by a space, was cut at **200 characters mid-word**, and had an empty `%b`. The op
answered `COMMITTED: <sha>`. Two of three commits in the stress run were mangled this way.

**Reproduced before changing anything**, through the real `WorktreeManager` against real git:

```
Expected: "feat(auth): recompute token expiry in UTC"
Actual:   ···" token expiry in UTC  The clock read the "···
```

The whole of it was `WorktreeManager.CommitSubject`, and so was the reasoning:

> Trimmed to one line and bounded, because a subject is one argv element that ends up in the user's
> history — a newline **would turn the rest into a body nobody chose**.

That is exactly backwards. A commit message *is* a subject and a body separated by a blank line. The
body is not an accident of a newline; it is the structure git defines, every tool downstream splits on,
and the thing a human reads at review. §11.2 called the commit "the durable record of what an agent
did" and then shipped the one function that destroyed it.

#### What the constraint should be, and why not simply a bigger number

Raising 200 moves where a message is destroyed; it does not stop a message being destroyed. The real
constraint is git's own, so it is git's own that is enforced — in one pure class,
`AgentCommitMessage`:

| rule | why |
|---|---|
| line 1 is the subject, **≤ 72 characters** | git's documented convention and the width `git log` shows without wrapping. It is deliberately **smaller** than the 200 it replaces: 200 was never a limit anyone chose, it was the offset at which the string was cut |
| if there is a body, **line 2 is blank** | without it `%s` swallows the body and `%b` is empty — silently the same outcome as the flattening being removed, produced by git instead of by us |
| the whole message ≤ 8 KiB, no control characters but `\n`/`\t` | it arrives from inside a sandbox over a channel whose own ceiling is 64 KiB; bounded, named in the refusal, never shortened |

**Everything else is left alone.** `Normalize` folds CRLF, trims trailing whitespace per line, and drops
leading/trailing blank lines. It does not reflow, does not collapse blank lines inside the body, and does
not truncate — a body's indentation is the worker's code block and its paragraph spacing is the worker's
paragraphs. `git commit` is then given `--cleanup=verbatim` **explicitly**, because the default for `-m`
is `whitespace`, which collapses consecutive blank lines, and because that default is
`commit.cleanup`-configurable — leaving it implicit makes the shape of an agent's commit depend on a
config key nobody set on purpose.

**A message that cannot be honoured fails loudly.** `AgentWorkCommitOutcome.RefusedMessage`, with the
reason, and the check runs **first — before `git add -A`**, so the worktree is exactly as the worker left
it and a corrected message is a retry rather than a recovery. The reader is an agent that can read a
sentence and try again: a refusal costs it a turn, a mangle costs the record and nobody finds out until a
human is reading the log.

**What is deliberately NOT refused: an absent message.** It still commits under `wip: work by agent <id>`.
No structure is being discarded there, and §11.2's reasoning stands — refusing would lose the work, which
is the defect `commit_work` exists to fix.

**The shim's half.** `commit` took `' '.join(argv[2:])`, which rejoins with single spaces whatever the
shell already split — so an unquoted message arrived flat and the join hid that anything had been lost.
It now takes **ONE quoted argument** and refuses a second positional, naming the cause. That is G3's
lesson applied to the other shim: the slip that can be seen is refused, and the one that cannot be seen
is designed out of the taught form. The worker's operating instructions carry the shape (subject, blank
line, body), the cap, the quoting, and the fact that a bad message is refused — a rule an agent is not
told about is a rule it discovers by having a commit refused.

#### The mutation log

| # | mutation | went red |
|---|---|---|
| M10 | `Normalize` back to flatten-to-spaces + cut at 200 — **the shipped defect** | **7 of 50**: the verbatim commit, the reflow guard, 4 normalisation rows, and the missing-blank-line refusal |
| M11 | the subject cap truncates instead of refusing | 3 of 50 (`…IsRefused_WithTheReason(subject too long)`, the boundary test, `ARefusedMessage_CommitsNothing…`) |
| M12 | the blank-line rule dropped | 1 of 50 |
| M13 | `--cleanup=verbatim` removed, so git's default tidies the body | 1 of 50 (`TheCommitDoesNotLetGitReflowTheBody`) |
| M14 | the plan shim rejoins a shell-split message | 1 of 50 (`TheShimsCommit_TakesOneQuotedMessage…`) |
| M15 | the message is not judged before the commit runs | 1 of 50 (`ARefusedMessage_CommitsNothing_AndLeavesTheWorkToRetryWith` — it commits the bad message, which is what the assertion catches) |

M11 is the one that shows the choice: keeping a cap and truncating at it keeps every "the message is
bounded" assertion green. Only a test that asserts the *refusal* can tell a limit from a shredder.

### 15.4 Left alone, deliberately

- **The `commit_work` channel handler is unchanged.** `RefusedMessage` falls into its existing
  `_ => Ok: false` arm carrying `Detail`, which is the sentence the worker reads. Adding a case would be
  a second opinion about an outcome the manager already decided.
- **An absent commit message still defaults** (§15.3), and a title-less spawn still refuses (§13.3). The
  two look inconsistent and are not: a spawn with no brief produces an ungated worker, which is a
  capability; a commit with no subject produces a commit with a truthful generic subject, which is a
  cosmetic loss set against losing the diff.
- **Nothing was done about a shell that will not parse the line** (§15.2). It cannot be observed from
  inside this system, and pretending otherwise would be worse than the honest gap.

---

## 16. The merge queue told the human things that were not true (H2, H3, H4, G1)

Four defects from one live run, with one shape between them: the daemon *knew* something and the human was
told otherwise. They are recorded together because three of them are the same missing fact seen from three
surfaces, and the fourth is the same failure of honesty applied to a list rather than a verdict.

### 16.1 What was measured, before anything was changed

Straight from the owner's daemon, not reconstructed:

```
sqlite> select Id,AgentId,Passed,ResolvedCommand from VerificationRows order by Id desc limit 3;
49|221760f27c…|1|node test.js
48|b3d2e7a48d…|0|node test.js      ← the human's redundant second run
47|b3d2e7a48d…|0|node test.js      ← the daemon's automatic run, 92 seconds earlier

sqlite> select AgentId,State,LastVerificationId from MergeQueueRows where AgentId like 'b3d2e7%';
b3d2e7a48d…|Working|48

$ cat …/verify-artifacts/verify_b3d2e7a48d…log
container-runtime-exit: 1
---- stderr ----
subtract(5,3) !== 2

$ grep auto-verify ~/.mainguard/logs/*.log | wc -l
6            # six "…verifying" lines. Zero outcome lines.
```

So: the branch had failed twice, the row said `Working`, and `Working` is where an entry that has **never
been verified** sits. And G1, from the same database — three agent ids with a `MergeQueueRows` row and no
entry of any kind in the plan store: the `scripted` probes that made zero plan calls.

### 16.2 H2 — the state model, and why a new member rather than a flag

**The choice.** `WorkerMergeState` gains `VerificationFailed`, a real, non-terminal member of the existing
machine. Reachable from `Verifying` and from nowhere else; leaves to `Verifying` (retry), `Working` (the
agent pushed a fix) and `Discarded` (the human dropped it).

**Why not the alternatives.**

- *Keep `Working` and carry the verdict as a side fact.* This is what the code effectively did — the red
  record was sitting in `_lastVerification` the whole time — and it is exactly what produced the defect:
  every consumer switches on the state, so the fact existed and nothing read it. A fact no surface consults
  is the shape this repository keeps paying for (MG-12, MG-10).
- *A boolean `LastRunFailed` beside the state.* Two things that must agree, updated in two places, with the
  state machine unable to refuse an impossible pair (`Verified` + failed). The enum already IS the vocabulary
  every surface renders; adding a second one is a parallel state machine, which the brief ruled out and which
  is the right call.
- *Make it terminal, like `Rejected`.* Tempting and wrong. A failing test is the most ordinary event in the
  system and the branch is still the agent's to fix; a terminal state would mean every red run needed a
  human to discard the entry and the agent to be re-spawned.

**What it can and cannot do.** No edge to `Merged` — there is no passing record, and `CanMerge` refuses on
the state before it ever looks at one. No edge to `Rejected` — that is a verdict on *reviewed* work, and
`TryReject` already says so verbatim ("only a verified branch can be rejected in review … discard the entry
instead"). Discard works from it, as it does from every non-terminal state.

**The distinction the fix must not blur, and nearly did.** A verification that is *refused before it runs*
(no jail, a drifted branch, a malformed verify command) writes no record and still settles to `Working`.
Both paths call the same `SettleAfterVerificationLocked`, so routing both to the new state is a two-character
mistake that would turn "we could not run your tests" into "your tests failed" — the one distinction the
merge decision rests on, and the thing PR #322 exists for. M2 in the log below is that mutation.

**What a failed row offers.** Retry (the human's Verify, which is a legal `VerificationFailed → Verifying`
edge) and Discard. Not Reject, not Merge.

**What happens when the agent pushes a fix** — and this is the part that would otherwise have made the new
state a trap. Nothing in the daemon calls `MergeQueue.NotifyNewCommits` for a locally-spawned agent, so a
push does not walk an entry back to `Working` by itself. `VerificationFailed` therefore joins `Working` and
`StaleVerified` in `WorkerReadinessTrigger`'s eligible set: the trigger's **once-per-tip** bound means it
fires only for a tip that has never been attempted, i.e. only for work pushed *since* the failure. Without
that arm a worker that repaired its own branch would sit red forever. (`Verified` is still not re-fired on a
push — the machine has no `Verified → Verifying` edge, and inventing one here would be the trigger changing
the merge spine instead of triggering it. That limitation is unchanged.)

**The other surfaces the fallback would have hidden.** `MergeQueueProvisioner.MarkMergeState` is what a
coordinator's `get_worker_status` reads (contract §3). It had no arm for the new state and would have fallen
to `_ => null` — so the state fix would have reached the rail and left the coordinator, whose reader is an
agent that acts on the answer, still hearing "Back at work". `AgentStatusMap.FromMergeState` had the same
shape with a worse default: its `_ => AgentStatus.Working` would have badged a branch whose tests just
failed as ordinary live work, and the type's own doc claims it is "total by construction", which is a claim
a `_` arm makes unfalsifiable. Both now have explicit arms, and the badge one is pinned by a test that
asserts exactly one state maps to `Working`.

**No migration.** `MergeQueueRow.State` is a TEXT column and the member was appended to the enum, so existing
rows are untouched and rehydrate exactly as before.

**The name was already in the codebase, which is the strongest argument for the model.** Phase 3's own role
lock (95ef2c95) answers a coordinator's `request_verification` with
`Status: record.Passed ? "Verified" : "VerificationFailed"` — a literal string, because there was no state
to name. So the daemon has been telling coordinators about a `VerificationFailed` outcome all along while
the queue had no such state to put the branch in, and the queue put it in `Working` instead. The two now
agree, and the agreement is not a coincidence: the vocabulary was right and the state machine was missing a
word for it.

### 16.3 H3 — the outcome, logged

`WorkerReadinessTrigger.RunAsync` announced `…verifying`, logged both of its *catch* arms, and said nothing
at all on the path that actually completes. `MergeQueueGrpcService.RunVerification` had the mirror-image
gap: it logged every refusal and never a result. Both now log the verdict, the resolved command, the
resulting state and **the artifact path**, so the output is one `cat` away instead of a directory to guess
at. The verdict word comes from `record.Passed` — the daemon-observed container exit the queue itself
settled from — and never a second opinion; the resulting state is logged separately because a run whose
entry was discarded mid-flight settles nowhere, and a line asserting the transition would be wrong exactly
then.

### 16.4 H4 — the output, made reachable

The artifact was written, its path recorded in SQLite, and **no wire carried any of it**: `QueueEntry` had
no verification field, and the client projection hardcoded `Verification: null`. Two additions:

- `QueueEntry` gains `last_verification_passed` (**`optional`**), `last_verification_command` and
  `last_verification_at`. The `optional` is load-bearing for the same reason `has_live_sandbox` is optional:
  a proto3 `bool` defaults to false, so a plain field would make "never verified" and "failed" the same value
  again — H2's defect reintroduced one layer down. M10 is that mutation.
- A new `GetVerificationLog` RPC returning the artifact's **content**, never its path (G-14; a daemon path is
  also useless to a client that is not on that machine). Bounded to 256 KiB, and it returns the **tail** —
  a test runner prints its failures last, so truncating from the front truncates away the reason the human
  opened the log. Three answers are kept apart: *no record*, *a record whose artifact reads*, and *a record
  whose artifact is gone* (which keeps the verdict and states why the output is missing). Collapsing the
  third into an empty log would render a deleted artifact as a suite that printed nothing.

**Not added to the coordinator deny-list**, deliberately. `GetMergeDiff` — the entire branch-vs-main diff —
is already open to the coordinator role on this same service, so denying the test output would be theatre
next to it; and a coordinator steering a worker has a legitimate need for the failure. Recorded here rather
than left as an unexplained absence.

### 16.5 G1 — a queue row now requires an approved plan

**What was already true.** `WorkerPlanGate` IS an `IMergeGate` and IS in every queue's `gates`, so
unapproved work genuinely could not merge. That boundary was real and is untouched.

**The boundary added, and the one deliberately not.** The row is gated; the **publish is not**. A branch
existing is not the harm — F1 (§12.1) established that branches must survive teardown, so gating publication
would destroy work to fix a display problem. A queue row is different in kind: it is a claim on human
attention that arrives carrying Verify, i.e. the daemon offering to spend a test-suite run on work nobody
authorised. `MergeQueueProvisioner.EnsureEntry` now asks the gate — the *same* `Allows` the merge already
asks, so an id the gate never held (manual agents, external-PR heads, seeded entries) is permitted here
exactly as it is permitted to merge. A second opinion about what "approved" means is how one of the copies
goes decorative (MG-12).

**What is withheld is only the row.** `EnsureEntry` still calls `EnsureQueue` on the deferred path, because
that call is what BUILDS a coordinator-spawned worker's repo queue — that spawn path creates its worktree
inside the launcher rather than through the RepoSync RPC, so an early return would also have skipped
registering the queue, the main-sha reconcile and the restart resume for the whole repository. Gating a row
is not a reason to stop governing a repo.

**The half that makes it a fix rather than a regression.** Every coordinator-spawned worker is spawned
*before* it has presented anything, so withholding without a way back would mean no legitimate worker ever
got a row. `AdmitDeferredEntries` creates the owed rows, subscribed in the composition root to
`PlanApprovalService.PlanApproved` — the exact moment the gate's answer changes. It **re-asks the gate** for
every candidate rather than trusting the event: the event says *a* plan was approved, which is not the same
claim as "this agent may have a row" (M8). Deferred rows are dropped on admission and on `Remove(repo)`, so
the set is bounded the way `AgentRefMediator`'s per-agent gates are.

Phase 2's §2.2 has been corrected to say what the gate governs (task delivery, steering, verification, the
queue row, the merge) and to state out loud that publication is not on that list.

### 16.6 The mutation log

Every guard broken, rebuilt, and watched failing. (`mv` on restore preserves mtime and MSBuild then skips
the rebuild, so every restore here is followed by `touch`; two mutations that did not compile were redone
by hand rather than trusted — a `--no-build` run over a stale binary reports a pass that means nothing.)

| # | mutation | tests that caught it |
|---|---|---|
| M1 | a failed run settles to `Working` again (H2 undone) | 8 — `RunVerification_Fail_SettlesToVerificationFailed…`, `VerificationFailed_SurvivesARestart`, `…NewCommits_ReturnToWorking…`, `AFailingRun_SettlesThroughTheQueue…`, +4 trigger tests |
| M2 | the **refusal** path also routed to `VerificationFailed` | 3 — `RunVerification_RefusedBeforeItRan_StaysWorking_AndIsNotAFailure`, `NoTestCommand_Throws_Typed_AndReturnsToWorking`, `ARefusedVerification_RecordsNothing…` |
| M3 | `VerificationFailed` falls back to the generic gate reason | 4 — incl. `AFailingRealExit_LeavesTheBranchUnmergeable`, `SeedVerifyFail_SettlesToVerificationFailed…` |
| M4 | the trigger stops logging the outcome (H3 undone) | 2 — `AFailedRun_LogsTheOutcome_WithTheVerdictAndTheArtifact`, `APassingRun_AlsoLogsItsOutcome` |
| M5 | the trigger stops firing from `VerificationFailed` (the self-healing arm) | 2 — `AFailedEntry_IsReVerified_WhenTheAgentPushesAFix…`, `AGrinderIsHeldOffByTheCooldown…` |
| M6 | the queue-row plan gate removed (G1 undone) | 5 of `QueueRowRequiresApprovedPlanTests` |
| M7 | the gate becomes default-**deny** for ids it never held | 2 — `AnAgentTheGateNeverHeld_GetsItsRowUnchanged`, `AnAlreadyApprovedWorker_GetsItsRowImmediately` |
| M8 | `AdmitDeferredEntries` trusts the caller instead of re-asking the gate | 1 — `AdmitDeferredEntries_ReAsksTheGate_AndLeavesUnapprovedWorkersDeferred` |
| M9 | the composition root stops subscribing `PlanApproved` | 1 — `AWorkerWithNoApprovedPlan_GetsNoRow_UntilItsPlanIsApproved` |
| M10 | the wire's verdict becomes a plain `bool` | 1 — `AnEntryThatWasNeverVerified_CarriesNoVerdictAtAll` |
| M11 | a missing artifact renders as an empty log | 1 — `GetVerificationLog_WhenTheArtifactIsGone_KeepsTheVerdict_AndSaysWhy` |
| M12 | `ReadTail` returns the head instead of the tail | 1 — `ReadTail_KeepsTheEndAndSaysItTruncated` |
| M13 | the rail badge falls back to `Working` for the new state | 1 — `EveryMergeState_HasADeliberateBadge_NotTheWorkingFallback` |

M7 is the one worth reading twice. It is the *only* mutation whose damage is invisible in the tests about
the defect being fixed — every G1-positive test still passes with a default-deny gate, because they are all
about workers the gate holds. What fails is the pair of negatives about agents it never held, and without
those the fix would have silently emptied the merge queue of every manual and external-PR branch: a far
larger failure than the one being repaired.

### 16.7 Left alone, deliberately

- **The client-side rendering of the verification output.** The daemon now carries the verdict and serves
  the log; `DaemonBackedOrchestrator` still hardcodes `Verification: null` and there is no
  `GetVerificationLogAsync` on the client. That is Agents.UI work, concurrent with this change, and taking
  it here would have meant editing another agent's files mid-flight. Both new wire facts are additive, so
  the client work is a read, not a migration. **Since closed — see §18.**
- **`NotifyNewCommits` still has no production caller for local agents**, so a push does not itself
  invalidate a `Verified` entry. The automatic trigger covers the case this change created
  (`VerificationFailed` → re-verify on a new tip) and nothing more; widening it to `Verified` is a change to
  the merge spine and belongs on its own.
- **The stranded-jail reason does not override the failure reason.** A `VerificationFailed` entry whose jail
  is gone reports the verdict, not `StrandedReason`. The verdict is the more actionable truth about that row,
  and `has_live_sandbox` already reaches the surface separately and withholds Verify on its own.

---

## 17. Defect H5 — `send_worker_prompt` has never once worked, and a test blessed it

`AgentCliBinder.TrySendPromptAsync` wrote `prompt + "\n"` to the worker's PTY. **A PTY-attached CLI
submits on CR, not LF.** Observed in a live stress run: three prompts to two workers all sat
UNSUBMITTED in the CLIs' input lines and ACCUMULATED — one worker's input box held two concatenated
prompts — and the jail transcripts show no new turn at any of the three timestamps.

Every layer above reported success. The daemon logged `coordinator prompt delivered (184 bytes)`, and
the bytes genuinely did arrive; the CLI simply never saw an Enter. `send_worker_prompt` is one of the
contract's four tools and the coordinator's **only** steering channel, so for the whole life of the
phase-3 surface the coordinator could spawn, watch, and verify — but never steer. It also made the
escalation card's own advice ("steer it or end it") a false choice with one arm.

### 17.1 What was measured, before anything was changed

Not reasoned from first principles. `claude-code v2.1.251` was driven under a real `forkpty` (a Python
harness: fork a PTY, set a 120×40 winsize, write bytes at the master, replay the output through a small
VT screen model, and read the rendered input box). The tty modes were sampled at every write.

**Modes at the CLI's prompt** — `ICANON=False, ECHO=False, ISIG=False, ICRNL=False, INLCR=False,
IGNCR=False`. Raw mode. With `ICRNL` off the line discipline translates nothing, so the CLI receives the
byte that was written, whichever it is. That is the mechanism behind everything below.

| # | written at the master | rendered result |
|---|---|---|
| 1 | `AAA` then `0x0A` then `BBB` then `0x0A` | input box shows `AAA` / `BBB` on two lines. **Nothing submitted, ever** — an exact reproduction of the live failure, accumulation included |
| 2 | `AAA` then `0x0D` | box clears, `❯ AAA` moves into the transcript, the CLI runs a turn and answers |
| 3 | `line1\nline2\nline3` then `0x0D` | submitted **once**, intact; the CLI's own reply: "I received your message as three lines … the multiline input came through intact" |
| 4 | `say A_OK` `0x0D` `say B_OK` then `0x0D` | **two turns.** The embedded CR submitted the prefix; `say B_OK` was left in the box |
| 5 | `AAA` then `0x0D 0x0A` | submitted; the trailing LF did no visible harm — but only because the CLI was mid-turn, so this is not a property to rely on |
| 6 | a menu (the folder-trust dialog): `0x0D` | confirms the highlighted option |
| 7 | `/bin/sh -i`, `echo CR_WORKS` + `0x0D`, then `echo LF_WORKS` + `0x0A` | **both ran** |

Row 1 is the shipped defect, reproduced on demand. Row 2 is the fix. Rows 3–4 are why the encoding is
more than a one-byte swap. Row 6 is why an empty prompt must not be encoded as a bare CR. Row 7 is the
whole per-adapter argument, below.

### 17.2 Is the submit sequence per-adapter? No — and the argument matters more than the answer

There is real precedent for putting it on the adapter: `systemPromptArg`, `preApprovedCommandArg` and
`initialPromptStyle` are all per-adapter, all in `AdapterManifest`, all justified the same way — *only
the CLI's author knows how their binary spells this*. The question is whether "which byte means Enter"
is that kind of knowledge.

It is not. It is knowledge about the **terminal**, and both classes of PTY-attached program agree:

- **A TUI runs the tty in raw mode** (row 1's `ICANON=False`, and with it `ICRNL=False`), so it sees the
  byte the terminal sent. A terminal sends **CR** for Enter. Every such TUI therefore binds CR; LF is at
  best a literal newline in its input buffer (measured: exactly that).
- **A line-oriented reader** either leaves the tty canonical — where the line discipline's `ICRNL`, on by
  default, turns CR into NL and completes the line — or uses a line editor (readline/libedit), which
  binds CR and LF alike to accept-line. Row 7 measures this on the class the scripted test adapters
  actually are: `scripted-agent` and friends end in `exec sh -i`, and **both** bytes worked.

So CR is correct for both classes and LF is correct for only one. A manifest field could therefore only
ever be set to the single value that always works — while inviting an adapter author to write `"\n"` and
silently reintroduce this exact defect, in a file the daemon trusts. That is a worse property than a
missing field.

The codebase already agreed and nobody noticed: `TerminalControl.MapKey` maps `Key.Enter` to a bare
`0x0D` for every adapter, consults no manifest, and works. The human keystroke path and the
coordinator's steering path were writing **different bytes for the same act**, and only one of them was
right.

**Decision.** One shared encoder, `Mainguard.Agents/Terminal/TerminalSubmit.cs`, with the rule stated
once. `TryEncodeLine`:

1. rewrites every embedded CR (and CRLF) to LF — row 4: an embedded CR submits a *prefix* as its own
   turn and strands the remainder, and CRLF text arrives from a Windows-authored or log-pasted message
   routinely;
2. keeps embedded LFs — row 3: a TUI inserts them, so a multi-line steer arrives intact and is submitted
   once, by the single terminator;
3. trims trailing whitespace, so a caller that already ended with a newline does not submit a blank line;
4. **refuses an empty message rather than writing a bare CR** — row 6: a lone CR is Enter, pressed at
   whatever the CLI currently has focused. A worker sitting on a permission dialog would have its
   highlighted option confirmed by a steer that said nothing;
5. terminates with **CR**.

### 17.3 Making it observable — and being honest about the limit

The defect's real damage was not the byte. It was that **every layer reported success for a prompt that
did nothing**, so the failure survived a live run, a green test, and a log line that said "delivered".

What evidence can the daemon reasonably have? A write to a PTY master succeeds whether or not the child
ever reads it, so the old signal — the write returned — was evidence of nothing. The one thing available
in band is that **a PTY-attached CLI cannot consume a keystroke silently: it re-renders.**
`BoundTerminalSession.WriteInputAndAwaitOutputAsync` subscribes *before* the write and then waits up to
`AgentCliBinder.PromptReactionWindow` (2s) for output.

**And the honest limit, stated rather than papered over.** That observation is *necessary but not
sufficient*. A CLI already mid-turn emits output continuously and would satisfy the wait on its own, so
a positive reading is not proof the line was submitted, and it is certainly not proof the CLI understood
it. Its evidential weight is in the negative direction: an idle CLI that produces nothing at all after a
keystroke did not see one. It is therefore **reported, never asserted on** — `TrySendPromptAsync` returns
`PromptDelivery(Submitted, Reacted, Refusal)`, delivery is never failed on a missing reaction (a busy
worker would be reported as unreachable, and a coordinator told to retry would double-steer it), and the
`coordinator_worker_prompt` audit event now carries `terminator=CR` and `cli_reacted`.

The ground truth — that a prompt became a *turn* — exists only in the CLI's own transcript inside the
jail. The daemon does not read agent home directories, and building a signal on a vendor's private
transcript format would be exactly the per-adapter coupling §17.2 refused. **So it is not claimed.** The
daemon reports what it pressed and what it saw; the transcript remains the arbiter, out of band.

**A second observability gap, found on the way.** The response carried `AgentId: owned.Id, Status:
"PromptSent"` — and the shim prints an id when one is present and the status only when one is not. So
the coordinator's `mainguard-agent prompt` printed the worker id it had just typed, and nothing about
the prompt's fate could reach it even in principle. The response now carries no id and a status the
caller can act on:

```
prompt submitted to pr-7 — Enter was pressed and its CLI redrew in response.
prompt submitted to pr-7, but its CLI produced no output for 2s — it may be mid-turn. Check it with
`mainguard-agent status pr-7` or its terminal before sending another; a second prompt is a second
turn, not a retry.
```

The last clause is deliberate: the unacknowledged reading must not read as "retry", because a repeat is
a second turn and a worker that was merely busy would be steered twice.

### 17.4 The test that blessed the defect, and what it asserts now

`CoordinatorToolPositivesTests` asserted `Assert.Equal("prefer the stdlib\n", written)` against a
`MemoryStream`. Its own doc comment explains that it asserts the bytes "not on the response flag" —
the right instinct, aimed at the wrong side of the boundary. It proved delivery **to the PTY**, which
was never in doubt, and could not see that the CLI on the other side pressed nothing.

The correction is not a different literal. A literal is the same mistake with a better constant. The
double is now `RawModeCliDouble` — a stand-in for the **CLI's side**: raw mode, CR submits the input
buffer, LF inserts a newline into it, a submit repaints (the repaint being the daemon's only evidence,
so the double has to produce it). The rules are the ones measured in §17.1. Assertions are on
`SubmittedLines` and `PendingInput`, which is what tells "the CLI received a submitted line" from
"bytes reached the pty" — a byte log cannot.

Four cases were added around it: two steers in a row must land as **two** submitted lines and not one
accumulated input box (the live failure, as a test); a message carrying CRLF must submit exactly once; and
both readings of the reaction observation, including that the caller can actually see it
(`Assert.Null(response.AgentId)`).

### 17.5 The same class of bug, checked elsewhere

Every place the daemon writes to an agent PTY expecting the CLI to act:

| site | verdict |
|---|---|
| `AgentCliBinder.TrySendPromptAsync` | **the defect.** Fixed |
| `TerminalGrpcService` (two attach-input paths) | **clean** — forwards bytes the UI already encoded |
| `TerminalControl.MapKey` (`Key.Enter`) | **clean** — `0x0D` already, and shift+Enter is CSI-u `13;2u`, alt+Enter `ESC CR` |
| `TerminalControl.BuildPasteBytes` | **clean** — normalises `\r\n`→`\r` and `\n`→`\r`, which is what a real terminal sends for a paste |
| the worker's first turn | **not a PTY write at all** — `initialPromptStyle: first-positional` puts it in the launch argv (`SandboxAgentLauncher`), which is why the first turn worked while every steer after it did not |
| `mainguard-plan` / `mainguard-agent` shims | **not a PTY write** — newline-delimited JSON over a socket/outbox, where LF is the framing and correct |

That last row is the reason this survived so long: the one prompt that visibly worked never went
through a PTY.

### 17.6 The mutation log

Every guard watched failing. `TerminalSubmitTests` is 15 tests in `Mainguard.Tests`;
`CoordinatorToolPositivesTests` + `PromptDeliveryBinderTests` are 10 in `Mainguard.Server.Tests`.
Restores used `touch`, because `mv`ing a file back preserves its mtime and MSBuild then skips the
rebuild — you test the mutated binary and every mutation looks survivable.

| # | mutation | went red |
|---|---|---|
| M1 | terminator back to `\n` — **the shipped defect** | **9 of 15** encoder, **6 of 10** server |
| M2 | embedded CR left alone (no CR→LF normalisation) | 5 of 15, 1 of 10 |
| M3 | an empty message encoded as a bare CR instead of refused | 5 of 15, 1 of 10 |
| M4 | trailing whitespace not trimmed | 8 of 15, 2 of 10 |
| M5 | the reaction observation faked (always "the CLI reacted") | 1 of 10 |
| M6 | the response echoes the worker id again, hiding the status from the shim | 2 of 10 |
| M7 | the binder's empty-prompt guard removed | 1 of 10 |

M7 is the one worth reading. It went red **only** because `PromptDeliveryBinderTests` exists: that guard
is unreachable through the IPC surface, since `AgentSpawnService.PromptAsync` rejects a blank prompt with
a usage sentence first. A guard no test can turn red is indistinguishable from a guard that was deleted,
so the delivery layer is now reached directly rather than left as decoration.

### 17.7 What this does not close

- **The live four-tool run of contract §8 still has not been performed** (§4 remains open). What is new
  is that the tool it would have exercised is no longer inert, and that the live failure mode has a test.
- **A steer sent while the worker's CLI is showing a modal** (a permission dialog, an autocomplete
  popup) types into that modal rather than into the message box. The empty-prompt refusal removes the
  worst case — pressing Enter at a dialog with nothing to say — but a real prompt sent at that moment is
  still delivered into whatever has focus. Detecting a modal from PTY output is per-CLI screen-scraping,
  which §17.2 refused for the same reason; recorded, not fixed.
- **No proof of comprehension.** §17.3 is explicit that the daemon reports what it pressed and what it
  saw, and that the CLI's own transcript is the arbiter. Any stronger claim would be an invented signal.
- **Adjacent, not fixed here: `mainguard-agent prompt <agent-id> <text ...>` is taught unquoted**, which
  is defect G3 (§15.2) in the other shim — a coordinator reaching this through its CLI's Bash tool with
  text containing `()`, `&&`, `|`, `$` or a quote never gets past the shell. `spawn` was hardened by
  teaching a quoted `--task`; `prompt` was not. Recorded so the next change to the shim's usage text
  does both.


### 17.8 J2 / J3 — the CR fix was real but half of it, and the success signal asserted what it could not know

§17 fixed the byte. A live run then found the tool still did not work: a **132-byte steer did not
submit, while a 3-byte poke (`go`) submitted instantly.** (The harness fixture below is the same
message at 139 bytes — the live figure counts the transcript's elided form.) The worker's own transcript carried the proof
that the right byte had arrived and had not been read as Enter:

```
USER TURN 2 = 'Add one more assertion to test.js ... mainguard-plan commit.\rgo'
```

The CR survived **as a literal character inside the message body**. So §17's encoder shipped a defect
whose symptom is length-dependent — which is exactly why 15 encoder tests and 10 server tests, all
written against three-word literals, stayed green over it.

#### 17.8.1 What was measured

Same method as §17.1, same binary (`claude-code v2.1.251`) under a real `forkpty`, 120×40, a small VT
screen model reading the rendered input box. One change to the harness worth recording: the API base URL
was pointed at a dead port, so the CLI does all of its **local** input handling — echo, paste detection,
buffer, submit — exactly as normal and then fails the network turn. Submission is a purely local UI
event, so it is fully observable this way and costs nothing. "A turn started" reads unambiguously off the
screen as the connection-refused retry line.

The tty modes were re-sampled and are unchanged from §17.1: `ICANON=False, ECHO=False, ISIG=False,
ICRNL=False, INLCR=False, IGNCR=False`.

| # | written at the master | result |
|---|---|---|
| 1 | 139-byte steer **+ CR, one write** — the shipped encoding | **NOT submitted.** The text wraps across two lines *inside* the input box; no turn. The live failure, reproduced on demand |
| 2 | `go` + CR, one write | **submitted** — `❯ go` moves into the transcript, box empties, a turn starts. The 3-byte case that made the defect look fixed |
| 3 | 139-byte body, then CR as a second write, **no pause** | **NOT submitted.** Two `write()` calls are not two reads: the PTY coalesces them |
| 4 | body, pause **5 ms**, then CR | **submitted** (also 10, 15, 20, 50, 100, 200, 500 ms — all submitted, ×2 reps) |
| 5 | body, **wait for the CLI's echo**, then CR | **submitted** at 2, 139 and 872 bytes, and for a multi-line message. Echo arrived in **0–1 ms** every time |
| 6 | `ESC[200~` body `ESC[201~` CR, one write (explicit bracketed paste) | submitted — but see 7 |
| 7 | the same bracketed-paste bytes at `/bin/sh -i` | **corrupted**: `sh: 00~echo: command not found`. The shell has no bracketed-paste mode and ate the escape as text |
| 8 | body, pause, CR at `/bin/sh -i` | **ran** — as did body+CR in one write, since a line-oriented reader has no paste heuristic at all |

Row 1 is the defect. Row 3 is the important negative: **splitting the write is not enough**; the
separation has to be real, because the boundary the CLI cares about is the *read*, not the *write*. Row 4
shows the threshold is tiny — 5 ms already works — which identifies the mechanism as a read boundary
rather than a byte-rate window. Row 5 is the fix that needs no timing at all.

#### 17.8.2 The mechanism, and why bracketed paste was rejected

A modern TUI classifies input as **typed** or **pasted** by the read burst it arrives in (a human cannot
type 139 bytes in one `read()`), and **inside a paste a CR is content — a line break — not Enter.**
`TryEncodeLine` returned `body + CR` as one buffer, so the daemon issued one write, the CLI performed one
read, and the terminator was absorbed into the message. Every byte was correct.

Rows 6–8 settle the shape of the fix, and they settle it the same way §17.2 settled CR-vs-LF. Bracketed
paste *works* on the CLI that supports it and **corrupts** the line-oriented class outright — and the
scripted test adapters (`scripted-agent` and friends) end in `exec sh -i`, so that class is not
hypothetical. Whether a program understands `ESC[200~` is knowledge about **that program**; a gap between
two writes is a property of the **terminal transport** and harms nobody. Same argument, same answer: the
universal mechanism wins.

**Decision.** `TerminalSubmit.TryEncodeSubmission` returns the body and the terminator as **two
buffers**, and nothing offers to concatenate them — the split is the fix, so it must not be undoable by
accident at a call site. `BoundTerminalSession.SubmitLineAndAwaitOutputAsync` writes the body, separates,
then writes the CR. The separator is preferentially **the CLI's own echo** (`PromptEchoWindow`, 250 ms),
which is causal rather than timed: a CLI that has repainted has already read those bytes, so the CR
cannot land in the same read. Only when no echo appears — a CLI mid-turn, not repainting its input line —
does it fall back to `TerminalSubmit.TerminatorSeparation` (50 ms; 5 ms measured sufficient, an order of
magnitude of headroom, imperceptible beside the 2 s reaction window).

#### 17.8.3 The same defect on the UI side

`DaemonBackedOrchestrator.SendPromptAsync` — the Pro app's agent-document composer — wrote
`prompt + "\r"` in one gRPC frame, which the daemon turns into one PTY write. Identical defect, identical
length dependency, and its test asserted the concatenated frame at 19 bytes and passed. §17.5 had cleared
`TerminalGrpcService` as "forwards bytes the UI already encoded", which was true and not enough: the UI
was encoding them wrong. It now sends the body and the CR as separate frames with the fixed separation
between them (this side has no echo to watch).

#### 17.8.4 J3 — the status asserted what it could not know

`BoundTerminalSession` stated the rule correctly — the reaction is "necessary but not sufficient…
report it as an observation, never assert it as proof" — and `AgentSpawnService.PromptStatus` then
asserted it anyway:

> prompt submitted to pr-7 — Enter was pressed and its CLI redrew in response.

The redraw fires on the CLI's own echo of the keystrokes; a CLI already mid-turn repaints regardless. The
sentence nonetheless reads as confirmation, and in the live run **the same sentence came back for all six
prompt calls** — where the coordinator had to reason its own way out of trusting its tool: *"Six prompt
calls this session, every one returning that identical redraw line… prompt confirms keystrokes landed,
not that the worker accepted anything."* An agent should not have to discount its own tools' success
messages.

The status now reports the act and the observation separately and states the limit in the message rather
than leaving it to be derived:

```
typed the prompt into pr-7's CLI, then pressed Enter as a separate keystroke. Observed: it redrew both
while the text was arriving and after Enter, so it is reading its terminal. That is NOT confirmation the
prompt became a turn — a redraw only shows the CLI is reading its terminal, and one already mid-turn
redraws regardless. Only pr-7 itself can confirm it acted: check `mainguard-agent status pr-7` or its
terminal before sending another. A second prompt is a second turn, not a retry.
```

**Is a stronger signal available now?** One genuinely is, and it is reported for what it is rather than
promoted. The two-phase write yields a *new* observation — `Echoed`, the CLI repainting the body
**before** Enter. That is strictly more than "the write returned": it proves the child consumed the body,
which is precisely what makes the CR a separate keystroke, so it is the daemon's **runtime detector for a
J2 regression**. It is still not proof of a turn, and it is not claimed as one. What §17.3 said remains
true and unchanged: the transcript is the arbiter, out of band, and the daemon does not read it.

#### 17.8.5 The tests that passed over this, and what changed

The J2 half survived §17's correction because of **two** things, and the second is the interesting one.

1. **Every fixture was short.** `"prefer the stdlib"`, `"status?"`, `"fix the failing test"`. The defect
   is length-dependent, so a suite of pokes proves the case that already worked. All steer fixtures are
   now the 139-byte live-run message, and `TerminalSubmitTests` adds an explicit case that a poke and a
   realistic steer encode to the same shape.
2. **`RawModeCliDouble` modelled a CLI that does not exist.** Its `Feed` walked the incoming bytes one at
   a time and acted on every CR regardless of which write it arrived in — a CLI with **no paste handling
   at all**. So `body + CR` in one write submitted against the double while failing against the real
   binary. §17.4 replaced a byte log with a model of the CLI's side, which was the right move; the model
   was just not yet faithful on the axis that mattered. It now honours **write boundaries**: a CR
   arriving in the same read as a substantial body is pasted content, and it emits its echo on every
   read rather than only on a submit (which is what the daemon waits on).

Mutation N9 pins that second point directly, and the exact result is worth reading. With the double's
paste rule removed, the defect mutation N1 goes from **11 tests red to 2** — and the 2 survivors are not
submission tests at all, they are the two that assert the *write structure* (that the CLI saw two reads,
separated). Every assertion about what was SUBMITTED goes green over the defect. So the paste rule is
precisely what makes submission-based tests able to see J2, and the write-structure guards are a second,
independent detector that does not depend on modelling the CLI's heuristic correctly. Two detectors, one
of them not reliant on the double being faithful, is the property worth having here — the first version
of this file had one detector and it was the unfaithful one.

#### 17.8.6 The mutation log

Every guard watched failing. Restores were `git checkout` **plus `touch`**, because a restore that
preserves mtime makes MSBuild skip the rebuild — you then measure the mutated binary and every mutation
looks survivable.

Suites: `TerminalSubmitTests` (enc, 20), `SendPromptDeliveryTests` (ui, 4),
`PromptDeliveryBinderTests` + `CoordinatorToolPositivesTests` (srv, 17). All counts are from one pass
against the final code.

| # | mutation | went red |
|---|---|---|
| N1 | body+CR concatenated into ONE write — **the shipped J2 defect** | **11 of 17** srv |
| N2 | the terminator separation removed | 1 of 17 srv |
| N3 | an empty message encoded as a bare CR instead of refused | 5 of 20 enc, 1 of 17 srv |
| N4 | trailing whitespace not trimmed | 9 of 20 enc, 2 of 17 srv |
| N5 | embedded CR left alone (no CR→LF normalisation) | 6 of 20 enc, 1 of 17 srv |
| N6 | **J3**: the status asserts the redraw as proof again | 2 of 17 srv |
| N7 | the UI sends body+CR in one frame | 2 of 4 ui |
| N8 | the UI drops the separation between its frames | 1 of 4 ui |
| N9 | *meta*: N1 again, with the double's paste rule removed | 2 of 17 srv (see below) |

Three of these are worth reading rather than counting.

**N2 survived the first pass**, and the reason was real rather than a missing assertion: on the ordinary
no-echo path the echo window has *already* lapsed 250 ms before the terminator is written, so the extra
delay added nothing and a flat `if (!echoed) delay` was dead code wearing a guard's clothes. The case it
genuinely covers is narrower — the echo wait returns `false` **instantly** when the output stream has
completed (a CLI that has died), reaching the terminator having waited no time at all. The code now
measures the elapsed time rather than sleeping unconditionally, and the guard is a test that kills the
CLI first. §17.6's M7 lesson, hit again from the other side: a guard no mutation can turn red is
indistinguishable from a guard that was deleted — and this one had to be *narrowed to its real case*
before it could be tested at all.

**N3 survives on the UI suite**, correctly: `SendPromptAsync` rejects an empty prompt itself before the
encoder is reached, so the encoder's refusal is unreachable from there. Recorded rather than papered
over; the guard is reached directly from enc and srv.

**N9 is the meta-check** on the double, described above: 11 red becomes 2, and the 2 are the
write-structure guards rather than any assertion about what was submitted.

#### 17.8.7 What this still does not close

- **§17.7's open items stand.** The live four-tool run of contract §8 has still not been performed (§4),
  a steer sent while the worker's CLI is showing a modal still types into that modal, and the shim still
  teaches `mainguard-agent prompt <id> <text …>` unquoted (defect G3, §15.2).
- **Still no proof of comprehension, and now it is said in the tool's own words.** The daemon reports
  what it typed, that it pressed Enter separately, and what it saw. The CLI's transcript remains the
  arbiter, out of band. J3's fix was to stop the status implying otherwise, not to invent the signal.
- **The paste heuristic is not a specification.** `PasteBurstBytes` in the double is a stand-in; real
  TUIs use burst size, inter-byte timing, bracketed paste, or some mixture, and a future CLI could draw
  the line somewhere this model does not. What the fix relies on is weaker and safer than any particular
  threshold: **the terminator arrives in a read of its own.** That is true regardless of where a given
  CLI draws its line, and it is what the write-structure guards assert directly.
- **The 50 ms fallback is a floor, not a guarantee.** If the child is descheduled for longer than the
  gap, two writes can still be coalesced in the PTY buffer. The echo path has no such hole — it is
  causal — and it is the path taken whenever the CLI is responsive at all (measured at 0–1 ms). The
  fallback covers a CLI that is silent, where the alternative is not writing at all.

---

## 18. H4, client half — the verdict, and reading the failure without paying for it again

§16.7 left this open on purpose: the daemon carried the verdict and served the log, and
`DaemonBackedOrchestrator` still hardcoded `Verification: null` with no `GetVerificationLogAsync`. Both wire
facts were additive, so this is a read, not a migration.

### 18.1 The fabricated-counts trap, and what was done about it

The client-side type the projection would have filled was
`VerificationRecord(AgentId, MainSha, Passed, TestsPassed, TestsTotal, When)`. **Two of those six fields do
not exist anywhere in this system.** Verification observes a process exit code inside the worker's jail; it
parses nobody's test runner, so there is no "58 of 58" for any layer to project. Filling them from the
nearest available number would have printed an invented count into a review surface — and a reviewer who
reads "58/58 green" believes a measurement that was never taken, which is worse than a reviewer who sees
none.

So the **type was narrowed rather than the projection padded**. `VerificationVerdict(Passed,
ResolvedCommand, When)` mirrors exactly the three fields the wire carries. `MainSha` went too: the wire
already sends `verified_main_sha` as its own `QueueEntry` field, and that is where the sha now lives — one
fact, one home. Guarded structurally (M13), because the realistic regression is somebody re-adding
`TestsPassed` "for the mock" and the projection quietly finding something to put in it.

Null keeps meaning **no record**, matching the wire's `optional`. The projection keys on protobuf field
PRESENCE (`HasLastVerificationPassed`), not on the value: reading the value would have made every entry an
older daemon served render as *failed*, which is H2's conflation pointing the other way (M1).

### 18.2 Where the log surfaces, and why there

**On the merge-queue row, inline and expandable**, and — via the same child ViewModel — in the worker pane's
review section.

- **The row is the only surface every entry has.** An entry whose agent was stopped has no worker document,
  and one that never reached `Verified` cannot open the review cockpit. A log reachable only from either
  would be missing for exactly the entries that failed.
- **The evidence and the decision belong together.** A failure is read *while* deciding whether to retry,
  hand it back to the agent, or discard — and those controls are on the row. A separate window would put
  them in two places.
- **The worker pane gets the identical panel rather than its own wording.** It was the second surface saying
  "no verification record yet" about a red branch; sharing one projection is what stops the two drifting
  apart again.

Long output is handled by **bounding the reader, not shortening the log**: a `MaxHeight` cap with its own
scroll, so the daemon's 256 KiB tail cannot push a row's actions off the surface. Horizontal scrolling is
ON inside it — unlike the rail's deliberately-wrapping identifiers — for the reason `ReviewCockpitView`
scrolls its diff sideways: wrapping a stack trace destroys the alignment that makes it readable.

It is **collapsed by default and fetched on first expand**, cached against the verdict it belongs to and
dropped when a new verdict arrives. The rail re-projects on every queue event; loading eagerly would turn a
queue refresh into one daemon-side file read per row, for text nobody asked to see.

**Reading is never re-running.** The panel calls `GetVerificationLogAsync` and never `RunVerificationAsync`
(M7). That is the whole feature: before it, the only way to see why a branch went red was to press Verify
again — minutes of real jail time, on a run that can legitimately answer differently.

### 18.3 Jail text is sanitized at the projection boundary

`GetVerificationLog` returns the artifact's bytes verbatim, and that artifact was written by a test runner
executing inside a sandbox. It is the one path handing jail output straight to a human surface, so the
client applies the discipline the daemon already applies before jail text reaches a log line
(`AgentIpcServer.Echo` drops control characters and bounds length; `AgentCommitMessage` refuses a message
carrying one).

`JailText.Sanitize` keeps `\n` and `\t` — in a test log they are structure, exactly the pair
`AgentCommitMessage` permits — makes other control characters visible as `.` rather than dropping them
silently, collapses CR and CRLF to one break so a progress bar's redraws become lines, and consumes ANSI
escape sequences **whole** (CSI / OSC / two-character, including one the daemon's tail cut in half).
Replacing an ESC character-by-character would smear `.[31m` through the output, which is worse to read than
the raw text. It runs in `DaemonBackedOrchestrator`, not in each surface, so no consumer can forget.

### 18.4 The mutation log

Every guard was removed in turn, the tier rebuilt, and the failure recorded.

| mutation | result |
|---|---|
| M1 the verdict built from the VALUE instead of field presence (never-run becomes failed) | 1 test red |
| M2 the projection back to `Verification: null` | 3 red |
| M3 a red verdict worded as "Not verified yet" | 1 red |
| M4 `IsFailed` never set | 4 red |
| M5 the `Classes.failed` binding dropped from the view (danger token never resolves) | 1 red |
| M6 the reader offered on an entry with no record | 1 red |
| M7 expanding the log also runs a verification | 1 red |
| M8 a cached log kept under a new verdict | 1 red |
| M9 truncation silently elided | 1 red |
| M10 a missing artifact rendered as an empty log | 1 red |
| M11 escape sequences smeared instead of consumed | 3 red |
| M12 the worker pane stops reading the verdict | 1 red |
| M13 `TestsPassed` re-added to the verdict type | 1 red |

M13 is the one worth reading: it is the only guard in this change that protects against a *plausible* edit
rather than a careless one. Re-adding a count field is exactly what a future author would do to make a mock
look richer, and the structural assertion is what turns that into a red test instead of a fabricated number
in a review surface.

### 18.5 Left alone, deliberately

- **`MergeQueueViewModel`** (the in-proc rail bound to the real `MergeQueue`, constructed only by
  `MergeQueueRenderHarness`) already words `VerificationFailed` and badges it red. It reads the state
  machine directly, not the wire, so nothing here applies to it and it was not given a log reader.
- **The review cockpit was not touched.** A failed entry cannot reach it — it requires `Verified` — so the
  reader would be dead code there. If a rejected-in-review flow ever needs the last run's output, it
  composes the same panel.
- **No history.** The panel reads the LAST verification only, which is what `GetVerificationLog` serves.
  `VerificationStore.History` exists daemon-side and has no RPC; a "previous runs" surface is a separate
  change with its own wire.

## 19. Defect H6 — a `Verified` row froze forever, and the product offered the merge anyway

The most dangerous thing the live testing found. Not "the UI is confusing" — the UI was *confident*, and
wrong, about the one question a merge rests on.

### 19.1 What was observed, with ground truth

Agent `4c43d17a`, 2026-08-30. `VerificationRows` 50 and `~/.mainguard/logs/{coordinator,merge}.log`:

| time | what happened |
|---|---|
| 01:35:12 | auto-verify **PASSED** against `main@ffbc3bc7`; the entry settled to `Verified` |
| 01:41:20 | `plan-shim request: op=commit_work` |
| 01:59:28 | `op=commit_work` |
| 02:13:29 | `op=commit_work` |
| 02:18:33 | the human pressed Verify: `RunVerification refused … Illegal merge-state transition Verified → Verifying` |

Three commits over ~38 minutes, and `MergeQueueRows` shows the entry's `UpdatedUtc` still equal to its
`VerifiedAtUtc` — the row never moved again. Meanwhile the review cockpit listed the *old* files, stamped
the header "verified", footed **"ready to merge"**, and left **Merge enabled**, for a tip carrying an
out-of-scope change and arithmetic that fails the repo's own tests.

The contrast is in the same log and it is what makes this a design defect rather than an accident: agent
`9b4a546f` went **red** at 02:23 and was re-verified at 02:33 the moment it pushed a fix — because
`VerificationFailed` *is* in the readiness trigger's eligible set. Recovery worked for the failing branch
and not for the passing one.

### 19.2 Three failures, one root

1. **The evidence did not record what it was evidence FOR.** A `VerificationRecord` pinned `main@sha` and
   nothing else. So the queue could ask "has main moved under this?" — it does, in `CanMerge` and in the
   stale cascade — and *structurally could not ask* "has the branch moved out from under it?".
2. **Nothing told the queue the branch had moved.** `MergeQueue.NotifyNewCommits` existed, `Verified →
   Working` was already legal, and the method had exactly two callers: `ExternalPrIntake` (an upstream PR
   head moved) and the dev queue seeder. Neither fires for a worker in a jail. `WorkerReadinessTrigger`
   starts runs only from `Working`/`StaleVerified`/`VerificationFailed`, so with nothing walking the row
   out of `Verified`, no automatic verification could ever fire again.
3. **Two gates were disarmed as a consequence.** `ArmFlaggedChangeReview` runs *only* inside a
   verification, so the F6 out-of-scope classification and every human acknowledgment stayed pinned to a
   two-commit-old diff — which means a newly introduced CI workflow, git hook, executable config or
   out-of-scope file was **never detected at all**. The comment at `MergeQueueProvisioner.cs:1003` asserted
   the opposite ("a branch that pushes new work re-verifies, re-classifies, and drops every ack that
   covered the old bytes"); it documented behaviour that did not exist.

### 19.3 The state model, and why it is not a new state

The obvious move is a sibling of `StaleVerified`. It is wrong, and the reason is worth stating precisely.

**`StaleVerified` means the evidence is still ABOUT this tree, measured against an old main.** The
branch's bytes are unchanged. Every acknowledgment still binds to the same flagged-set hash, the F6 scope
verdict still holds, the diff a human reviewed is still the diff that will merge — and the honest remedy is
mechanical: rebase onto the new main and re-run. That is exactly what the cascade does, and it is why the
cascade *keeps* the record.

**The agent's own commits mean the evidence is about a tree that no longer exists.** Different diff,
different flagged set, void acks, a scope verdict computed from bytes nobody will merge. There is nothing
to rebase and nothing to salvage; there is only "verify the new thing". Routing it through `StaleVerified`
would put it in the cascade, retain a `VerificationRecord` and a `VerifiedAtUtc` asserting a verdict about
a vanished tree, and let the rail go on reading "was green, just needs a refresh" about work nobody has
ever tested.

The state that already means *there is no evidence about this branch* is `Working`. `Verified → Working` is
already legal and already documented as "new commits from the agent invalidate", and `NotifyNewCommits`
already implements it exactly. **So no state was added and no edge was added.** What was added is the
caller — which is what the defect always was.

The invalidation is scoped to `Verified` and `AwaitingReview`: precisely the two states `CanMerge` admits,
i.e. the two where stale evidence is not merely wrong but *dangerous*. `StaleVerified` and
`VerificationFailed` already refuse the merge and are already in the trigger's eligible set, and demoting
`VerificationFailed` would erase a red verdict a human is reading and replace it with "not verified yet" —
the exact conflation §16's H2 exists to end.

### 19.4 What changed

- **`VerificationRecord.BranchSha`** (+ `VerificationRow.BranchSha`, migration `AddVerificationBranchSha`,
  and both shas in the artifact header). The provisioner resolves `agent/<id>` from the mirror *after* the
  pre-verification publish, so it names the tree the container is measured on. Existing rows take `""`,
  which is the correct value rather than a backfill: nothing knows what tip they ran on, and every
  freshness comparison reads empty as "not measured" and declines to answer.
- **`MergeQueue.NotifyBranchAdvanced`** — records the tip; for `Verified`/`AwaitingReview` clears the
  record and walks to `Working` with a render-verbatim `BranchMovedReason`.
- **`BranchTipInvalidator`** — the missing caller, a second subscriber on the `AgentRefWatcher.Advanced`
  sweep the readiness trigger was already riding. Deliberately **not** a branch of that trigger: the
  trigger debounces (five commits cost one test run), and the debounce window is exactly the window in
  which a human can merge a verdict that has already gone void. It also keeps the trigger's own contract
  intact — it answers *when to verify*, never *what a state means*.
- **The mid-run window.** A commit that lands *while the tests are running* cannot move the state (the run
  owns the entry, and a `Working → Verified` settle would throw out of a background completion nobody
  awaits), so the advance is recorded and the settle refuses to promote the pass — `Working`, not
  `Verified`. This is measured from a window opened when the run starts, **not** by comparing against the
  last known tip: a rebase or a commit made while nothing was watching moves the branch with no
  announcement, so the re-verification that follows legitimately measures a tip the queue has never heard
  of. Reading that as "moved mid-run" demoted every re-verified entry straight back to `Working` — a
  cascade that can never finish. Two of `MergeQueueProvisionerTests` caught it; M8 is that guard.
- **`CanMerge` gained the branch-side compare.** Both shas must be KNOWN to refuse. It is a belt, not the
  mechanism — a wired queue reaches it with the two equal — and it stays because the invalidation depends
  on an *observation*, which can be missed, delayed, or absent on a substrate with no watcher. A gate that
  only holds while an event fires is not a gate.
- **The Verify button.** Now offered on exactly `Working`/`StaleVerified`/`VerificationFailed`. It was
  offered on `Verified` and `AwaitingReview` on a comment's belief that "re-verifying against a moved main
  is the normal way a stale entry gets fresh again" — true, and about `StaleVerified`, which was already in
  the set. From the other two it did the same thing every press for the life of the feature. An action that
  is offered and always fails is worse than an absent one: it reads as the recovery the human is looking
  for, and it teaches them the product is broken rather than that the entry is fine. A green entry needs no
  Verify; when that stops being true the daemon walks it to `Working` and the button returns on its own.
- **`MergeQueueProvisioner.cs:1003`** — the false comment now says what is true, and names the test that
  keeps it true.

### 19.5 The other stale-evidence cases, found and NOT fixed here

Same class — a decision recorded against a sha that has since moved. Reported rather than fixed: each has
its own blast radius on the merge spine and belongs in its own change.

1. **`MergeReconcileTask` synthesizes a `Merged` confirm from unrelated evidence.** At boot it walks an
   outstanding lease to terminal `Merged` when `currentMain != lease.ExpectedMainSha` **and** the repo's
   journal contains *any* entry of kind `Merge` — unfiltered by lease id, agent, branch, sha or timestamp.
   A `git pull`, a co-tenant's merge or a hand commit satisfies the first; any historical merge satisfies
   the second. This is the worst of the remaining set: it fabricates a terminal state and fires
   `NotifyMainMoved` for a merge that never happened.
2. **`ForegroundMergeService.ResolveMergeSource` prefers a stale local `refs/heads/agent/<id>`** over the
   ref it just fetched. The fetch is treated as load-bearing ("merging it would land work the queue never
   verified") and is then discarded by the preference order on any checkout that once brought the branch
   local. `--ff-only` still guarantees main-ancestry; it guarantees nothing about the source being the
   verified ref.
3. **The merge lease is not a CAS on the branch.** `MergeLeaseRow` carries only `ExpectedMainSha`, and
   `DbMergeLeaseStore.TryBegin` is a uniqueness check, not a compare-and-swap. `ConfirmMerge`'s
   `NewMainSha` is client-supplied and validated against no ref.
4. **External-PR merge re-derives its "verified head" at merge time** (`ExternalPrMergeService`
   `ResolveVerifiedHead`) rather than reading one the queue recorded, so between a force-push and the next
   intake poll both sides of its head compare are the new head and it passes on unverified code. This is
   the external-origin twin of H6 and the fix here does not reach it: `PrHeadFetcher` hard-resets
   `agent/pr-<n>` *before* the intake calls `NotifyNewCommits`.
5. **The approved `TaskPlan` scope is keyed to a plan id with no tie to code** — no repo sha, no branch, no
   diff hash. `WorkerPlanGate.Allows` answers "was some plan approved", never "does the current tip still
   match its scope"; the scope is only re-evaluated when `ArmFlaggedChangeReview` runs, so it inherits this
   defect's cadence exactly. Fixing the cadence (above) is what makes it correct today.
6. **`GitMutationGuard`** computes its verdict from a `.git` snapshot and then runs the action after up to
   five `index.lock` backoff attempts, re-checking only the lock and not the `GitDirState` it decided from.

One more was found and **is** fixed here, because it is the header the observation was made on:
`verified_main_sha` was projected as `queue.CurrentMainSha` while the wire documented it as "the main@sha
this branch's verification ran against". The cockpit's "verified @ `<sha>`" stamp therefore named today's
main whatever the evidence was measured on — most visibly on a `StaleVerified` entry, where the two are
guaranteed to differ and the stamp asserted the very freshness the state exists to deny. It now projects
the record's own `MainSha`.

### 19.6 The mutation log

Every guard removed in turn, the tier rebuilt (`touch` on restore — an `mv` preserves mtime and MSBuild
then skips the rebuild, so the mutated binary is what gets tested), and the failure recorded.

| mutation | result |
|---|---|
| M1 `NotifyBranchAdvanced` records the tip but never moves state | 4 red |
| M2 the invalidator never subscribes to the sweep | 2 red |
| M3 the invalidation moves state but leaves the record standing | 1 red |
| M4 a run overtaken mid-flight still settles `Verified` | 1 red |
| M5 `CanMerge` drops the branch-side compare | **0 red — see below** |
| M6 the runner drops `BranchSha` from the record | 1 red |
| M7 the provisioner never resolves the branch tip | 1 red |
| M8 the mid-run window compared against the last known tip instead | 2 red |
| M9 the settle stops advancing the queue's known tip | 1 red |
| M10 the Verify button is offered on `Verified` again | 3 red |
| M11 the store stops persisting `BranchSha` | 1 red |
| M12 `verified_main_sha` reverts to today's main | 1 red |
| M13 the daemon registers the invalidator but nothing resolves it | 1 red |
| M14 the composition root stops building the invalidator | 2 red |
| M15 the readiness trigger stops being constructed at boot | 1 red |

Four of these are worth reading.

**M8 is the one that found a real bug in the fix.** The first implementation asked "did the branch move
mid-run?" by comparing the record's tip against the last tip the queue knew. That is a different question:
a rebase, or a commit made while nothing was watching, moves the branch with no announcement, so a
re-verification legitimately measures a tip the queue has never heard of. Reading that as a mid-run move
demoted every re-verified entry straight back to `Working` — a cascade that can never finish.
`MergeQueueProvisionerTests` caught it, and the guard is now an explicit window opened when the run starts.

**M9 is its mirror**, and it guards a *false refusal* rather than a false pass: without the settle
advancing the queue's known tip, a branch the cascade rebased would re-verify green and then be refused by
the branch-side compare forever, with nothing anywhere saying why.

**M2, M13, M14 and M15 were all 0 red at first**, and that was the most useful result of the pass. The
wiring tests asserted `GetService<T>()` is non-null — but a DI singleton is built on first resolve, so the
*test itself* was the only thing that had ever constructed either subscriber, and "registered, never
running" passed. `BothSubscribersAreConstructedAtBoot_NotMerelyRegistered` reads the watcher's subscriber
list before asking the container for anything, and `Watcher_AdvancedInvalidatesAVerifiedEntry…` drives a
real commit through a real sweep. The gap existed for the readiness trigger too (M15), and is now closed
for both.

**M5 is deliberately uncovered.** With the invalidation wired, `CanMerge`'s branch-side compare is
unreachable: every path that could put a newer tip in `_branchTip` under a `Verified` entry either
invalidates it (`NotifyBranchAdvanced`) or is caught by the settle (`Verifying`). It is kept because it
does not depend on an *observation* — the thing that can be missed, delayed, or absent on a substrate with
no watcher — and its premise, that `CanMerge`'s admit set and the invalidator's demote set are the same
set, is itself asserted by `EveryStateThatCanMerge_IsAStateAnAdvancedBranchInvalidates` over all nine
states. Manufacturing a test that reached it would have meant weakening the mechanism to exercise the
belt.

### 19.7 Left alone, deliberately

- **No `Verified → Verifying` edge was added.** It would let a human re-run tests on a green branch, and a
  *refused* re-run (a jail that died, a drifted branch) settles to `Working` — losing a legitimate green to
  a transient failure. Withholding the button is the smaller, honest answer, and the invalidation returns
  it the moment there is something to verify.
- **`StaleVerified` and `VerificationFailed` are not invalidated** on a tip move. Neither can grant a
  merge, both are already in the trigger's eligible set, and demoting the red one would delete the verdict
  a human is reading.
- **`_branchTip` is not persisted.** Like the jail-liveness axis, it is a MEASUREMENT of the mirror rather
  than a decision this queue made, and a measurement written to SQLite outlives its own truth. A restart
  re-learns it from the watcher's first sweep, and an unknown tip declines to answer rather than
  manufacturing a "fresh".

---

## 20. Defects L1 and L3 — the restart dead end, and the rows that reported the opposite of the truth

§19 fixed the freeze that let a stale `Verified` row offer a merge. These two are what the same live run
found next, and they are the same shape from the other side: a row that could not merge and could never
be made mergeable, and a row that could not merge for a reason the product refused to say.

### 20.1 What was measured, before anything was changed

`~/.mainguard/mainguard-daemon.db`, `MergeQueueRows` + `VerificationRows`, 2026-08-30:

| row | agent | state | `LastVerificationId` | that record |
|---|---|---|---|---|
| 76 | `221760f2` | `Working` | 49 | **passed**, `main@ffbc3bc7`, 21:26 |
| 77 | `4c43d17a` | `Working` | 50 | **passed**, `main@ffbc3bc7`, 01:35 |
| 78 | `9b4a546f` | `Working` | 52 | **passed**, `main@ffbc3bc7`, 02:33 |

All three moved at `2026-08-30 05:28:20`, the instant row 79 (`affa7294`) reached `Merged` and main
advanced `ffbc3bc7 → d8a987ff`. `AuditRecords` holds four `stale_requeue_blocked` events. The daemon log
carries, for each of them, *"this branch needs rebasing onto the new main and its agent has no live
sandbox — resume the agent"*. The UI said *"Not verified yet — no test run has been recorded for this
branch."*

Separately, three `Verified` rows had already been observed unmergeable **forever** after a restart, the
gate answering *"flagged-change review has not run for this branch (no acknowledgment record)"* with no
control anywhere that could change the answer.

### 20.2 L1 — `Verified` plus a restart was a permanent dead end

Four true things that only compose into a trap:

1. `FlaggedChangeGate._stores` is an in-memory `ConcurrentDictionary`. A restart wipes every
   `AcknowledgmentStore`.
2. `ArmFlaggedChangeReview` — the only thing that has ever created one — runs *only* inside a
   verification.
3. Verify is withheld from a `Verified` row, correctly (§19.7: a refused re-run settles to `Working` and
   loses a legitimate green to a transient failure).
4. `WorkerReadinessTrigger` starts runs from `Working`/`StaleVerified`/`VerificationFailed` only.

So the gate failed closed — which is right, MG-40 — and **nothing in the product could ever re-arm it**.
The defect was never the default. It was that the armed state did not survive and there was no path back.

#### The option NOT taken, and why

The obvious move is to persist the store. It is wrong for the reason §19.7 already gave about
`_branchTip`: a flagged set is a **classification of a diff**, i.e. a measurement, and a measurement
written to SQLite outlives its own truth. Persisted *acknowledgments* are worse still.
`FlaggedChange.Id` is `kind|path|contentHash` precisely so an ack cannot survive the push that changes the
bytes it was granted for; a durable ack is a standing invitation to reconstruct the id it was granted
under, and the whole gate rests on that being impossible. Persisting the items without the acks fixes
nothing anyway — an empty store is still a denied store.

The second option, making a `Verified` row re-armable through the Verify button, re-opens the edge §19.7
closed for a reason that has not changed.

#### What was done: re-derive, never restore

`ArmFlaggedChangeReview` needs **no jail, no container and no agent** — only the mirror. It is a merge
diff plus a lockfile read. So the honest fix is to run the review again, and the change is a caller, the
same way §19's was:

- **`MergeQueueProvisioner.RearmAfterRestart`** (+ `BeginRearmAfterRestart` / `LastRearm`), started on
  `EnsureQueue`'s `created` branch beside `BeginResumeAfterRestart`. For every entry in
  `RearmableStates` with no store, it re-runs the classification and republishes via
  `NotifyGateChanged` — the gate's *answer* changed while no state moved, and the stream re-pushes only
  on `Changed`.
- **`RearmableStates`** is public and is `{ Verified, AwaitingReview }` — exactly `CanMerge`'s admit set,
  because those are the only states in which a missing store is a dead end. Everything else is already in
  the readiness trigger's eligible set (or is a run in flight) and its next verification arms the gate as
  it always did. `EveryStateThatCanMerge_IsAStateTheRestartRearmCovers` asserts the equality over all nine
  states rather than the two names.

#### Why this cannot become a way to merge unreviewed work

1. **Nothing is restored, so nothing is trusted.** The pass writes no acknowledgment and reads none from
   anywhere. A re-armed store comes back with **zero** acks, so every flagged item must be acknowledged
   again, by a human, in the cockpit. A restart can only ever *increase* the review owed; it can never
   discharge any of it.
2. **The set describes the bytes that will merge.** It is computed from the mirror's current
   `agent/<id>` against main, at that instant, by the same code path verification uses — so the items and
   their content hashes bind to today's diff, not to a remembered one.
3. **The evidence is checked against the tree first** — see below.
4. **Fail-closed is untouched.** A diff that cannot be computed still leaves the store absent, and an
   absent store is still the default-DENY. This adds a second chance to classify; it adds no way to skip
   classification.

#### `PrimeBranchTipsAfterRestart`, and why it is inline

Point 3 is the part that needed new code, and §19's own hint is what it rests on: **evidence should
record what it is evidence for.** J1 gave `VerificationRecord.BranchSha`, and that field is persisted —
unlike `_branchTip`, which §19.7 deliberately leaves in memory. So at boot the queue can ask a durable
question: *does the tip this record says it measured still name the tree in the mirror?*

`NotifyBranchAdvanced` already answers it (it compares `newSha` against `VerifiedBranchShaLocked`). The
missing piece was, again, a caller. `PrimeBranchTipsAfterRestart` does one `rev-parse` per re-armable
entry and hands the answer to the queue, which demotes anything whose evidence is about a different tree.

This closes a hole the observed path cannot reach at all: `AgentRefWatcher` only sweeps agents something
called `Watch` for, i.e. agents with a live sandbox. A branch that moved while the daemon was down and
whose agent has since been stopped is announced by **nothing**, and `_branchTip` is not persisted — so
without this the entry would rehydrate `Verified`, be re-armed, and be offered.

It runs **inline, before the context is returned**, and that was measured rather than assumed. The first
implementation put it inside the background pass: a tip announced into a queue that has since started a
verification is recorded as a mid-run move (§19.4's M8 window) and demotes the run's own green. Twenty of
forty-four `MergeQueueProvisionerTests` went red. Running before anyone holds the queue means there is no
queue to race.

Its state filter is deliberate too: an entry outside `RearmableStates` has nothing to protect (the gate
refuses from the state alone) and asking git about it is a subprocess for an answer nobody reads.

### 20.3 L3 — the honest reason, and the record, both dropped at the same terminus

`MergeQueueProvisioner.Block`'s comment has always claimed the reason is *"rendered verbatim as the
CanMerge reason"*. Two separate things falsified it, both in `TryReturnToWorking`'s neighbourhood.

**(a) The reason was outranked.** `CanMergeLocked`'s switch ordered

```
WorkerMergeState.StaleVerified or WorkerMergeState.Working when _stranded.Contains(agentId) => StrandedReason,
```

*above* the `_workingReasons` arm. `ReconcileJails` had marked all three rows stranded — their agents were
stopped — so the generic *"the agent's sandbox is gone — resume the entry to give it one, or discard
it"* replaced the measured *"this branch needs rebasing onto the new main and its agent has no live
sandbox — resume the agent"*, which says everything the generic line says **and** why the branch is back
at `Working`.

The fix is not a blanket reorder, and that distinction is the whole design. `_workingReasons` now holds a
`WorkingReason(Reason, AccountsForMissingSandbox)`, and only a reason that was itself produced BY
establishing the missing sandbox outranks `StrandedReason`. The cascade's other termini — the parked
rebase conflict, the skipped reparent — were every one of them measured with a live jail in hand; if the
sandbox has since gone, they would send a human into a container that no longer exists, and ISSUES-LOG
#24's argument still applies to them exactly as written.

The flag is not persisted, for the same reason `_branchTip` is not. After a restart the measured reason is
gone and the re-measured `_stranded` mark answers instead — which is the correct ordering of a remembered
claim and a fresh one.

**(b) The passing record was erased.** `TryReturnToWorking` did `_lastVerification[agentId] = null`, and
`LastVerification` is what the wire's verdict and `VerificationPanelViewModel` render. Hence "no test run
has been recorded for this branch" about three branches holding passing runs.

Its docstring justified this as "whatever evidence existed was against a main this branch is no longer
parented on" — which is *equally true of `StaleVerified`*, and `StaleVerified` **keeps** the record, for
the reason §19.3 spells out: the branch's bytes did not change, only its parentage, so the evidence is
still ABOUT this tree. This method is reached only from entries the cascade just moved through
`StaleVerified`. The two disagreed and `StaleVerified` was right.

So the record is kept and `VerifiedAtUtc` is still cleared — the row must not claim to be verified against
anything it can merge into, but it may say what its last run found. It cannot leak into a merge:
`CanMergeLocked` reads the record only for `Verified`/`AwaitingReview`, and the only non-terminal edge out
of `Working` is `Verifying`, whose settle overwrites the record before either is reachable.

### 20.4 Is `StaleVerified` reachable? Yes — and it is entered on every cascade

Reported rather than changed, because the premise turned out to be false.

`StaleVerified` **is** entered, and the live DB proves it without needing a transition log.
`NotifyMainMoved` walks each affected entry to `StaleVerified` under the lock — `SetStateLocked`, so the
row is persisted — *before* handing it to `RequeueAllAsync`. Every one of the four
`stale_requeue_blocked` audit events is `TryReturnToWorking` called from `RequeueStaleAsync`, which runs
only on an entry `NotifyMainMoved` has just placed in `StaleVerified`. Four events is four entries that
were in that state. `BootStaleCascadeTests`, `StaleCascadeTests` and `MergeQueueJailReconcileTests` all
assert it directly.

What is true is that it is never **seen**. It is transient by construction: every entry that enters it is
immediately handed to the cascade, which leaves it in one operation — `Block → Working`, or
`rebase → Verifying`. The only ways to rest there are the dev seeder's `Hold` and the null-rebaser unit
path. That is the state doing its job, not a dead member: it exists so the cascade's FIFO walk has
somewhere to name its subjects, and so `CanMerge` can distinguish "stale, being re-verified" from "never
verified". A state nobody rests in is not the same defect as a control nobody calls.

### 20.5 The mutation log

Every guard removed in turn, the tier rebuilt (`touch` on restore — an `mv` preserves mtime and MSBuild
then skips the rebuild, so the mutated binary is what gets tested), and the failure recorded.

| mutation | result |
|---|---|
| M1 the restart re-arm never runs | 1 red |
| M2 no branch-tip prime at boot | 1 red |
| M3 the re-arm manufactures an empty store instead of classifying | 1 red |
| M4 `StrandedReason` outranks the measured no-sandbox reason again | 1 red |
| M5 blanket reorder — every measured reason beats stranded | 1 red |
| M6 the cascade block erases the passing verification record | 1 red |
| M7 the no-jail block stops declaring itself sandbox-aware | 1 red |
| M8 `RearmableStates` drifts from `CanMerge`'s admit set | 2 red |
| M9 the branch-tip prime moves into the background pass | **0 red — see below** |
| M10 the re-arm never republishes the queue | 1 red |
| M11 the prime backgrounded AND unfiltered (the first implementation) | 3 red |

**M3 is the one that matters most.** It is the persisted-store design expressed as a mutation: hand the
entry a store it did not earn instead of classifying its diff. An empty store is `AllAcknowledged`, so
the mutant merges a branch nobody reviewed — and the test says so.

**M9 and M11 are a pair, and reading only M9 gets the wrong lesson.** With the state filter in place, a
backgrounded prime is *nearly* safe: it skips `Verifying`, so it rarely reaches the mid-run window. What
remains is a check-then-act — `GetState` can answer `Verified` and the entry can enter `Verifying` before
`NotifyBranchAdvanced` takes the lock — and manufacturing a test for that window would mean weakening the
mechanism to exercise it, exactly as with §19's M5. M11 removes the filter as well, which is the
implementation that was actually written first, and it goes red immediately. Inline is kept because it
makes the window not exist rather than making it small.

### 20.6 Left alone, deliberately

- **The flagged-change store is still not persisted, and neither is the working reason.** Both are
  measurements. §19.7 settled what happens to a measurement in SQLite, and L1's fix is what makes
  re-deriving cheap enough that persisting is not tempting.
- **`StaleVerified` gets no new resting behaviour.** It is entered, it is asserted, and the cascade
  leaving it promptly is the feature.
- **The five stale-evidence cases of §19.5 are still open.** Nothing here reaches
  `MergeReconcileTask`'s synthesized `Merged`, `ForegroundMergeService.ResolveMergeSource`, the
  non-CAS merge lease, `ExternalPrMergeService.ResolveVerifiedHead`, or the plan-scope/code binding.
- **Pre-`BranchSha` records are demoted once, not backfilled.** An empty `BranchSha` names no tree, so
  the boot prime cannot establish that such a record is about the mirror's tree and walks the row to
  `Working`. That is a one-time re-verification, not a permanent refusal — which is why it is the right
  answer here while `CanMerge`'s belt still declines to refuse from ignorance: the belt guards a refusal
  with no way out, this walks the row to the one state the product re-measures from.
## 21. Defects L2 and L4 — the audit chain did not record the merge, or the waiver that allowed it

Two holes in the tamper-evident chain, both found in live testing, both the same shape: the paths that
*matter most* were the paths that wrote nothing.

### 21.1 What was observed, with ground truth

`~/.mainguard/mainguard-daemon.db`, 2026-08-30, after a real merge driven through the UI:

| table | row |
|---|---|
| `MergeLeaseRows` id 14 | `ExpectedMainSha=ffbc3bc7…`, `Confirmed=1`, `PostMergeSha=d8a987ff…`, `BeginUtc=05:28:19` |
| `AuditRecords` | seq 800/801 `acknowledged_flagged_change`, 802–805 `stop`, 806 `queue_entry_jail_reconciled`, 807 `agent_session_reconcile`, 808 `queue_entry_discarded` |
| `JournalEntries` | **empty** |

Main moved `ffbc3bc → d8a987f` in the mirror and in the user's checkout, the queue row went `Merged`, the
lease recorded the merge — and **the chain got nothing**. `SELECT Type, COUNT(*) FROM AuditRecords GROUP BY
Type` returned **33 types, and none of them was a merge**. They included `queue_entry_discarded`: the
product recorded the act of DROPPING an entry and not the act of merging one.

**L4** is the same defect one layer down. `ChangedTestCommandGate.Acknowledge` recorded the waiver in a
plain `HashSet<string>` and audited nothing, while the neighbouring `FlaggedChangeGate`'s acks *did* write
`acknowledged_flagged_change` — the two events at seq 800/801 above are those. The item waived by the
silent one is *"(verification command) — a branch cannot be allowed to self-green"*: a human waiving the
fact that a branch **changed the command that verifies it**. The single most security-relevant click in
the product was the one click that left no trace.

### 21.2 Which points in the merge conversation deserve an event

The conversation is `BeginMerge` → the client merges on the user's own checkout → `ConfirmMerge`, with
`AbandonMerge` as the non-merge terminal and the RT-D1 boot reconcile as the crash path. Not every step
earns a record, and adding one everywhere would have made the chain less useful, not more.

| point | event | why / why not |
|---|---|---|
| `BeginMerge` **granted** | — | A grant is not an act. Every grant that leads to a merge is already named by that merge's record (which carries the lease id and the expected sha); every grant that leads nowhere would be a non-event filling the chain. |
| `BeginMerge` **refused** | — | A merge that has not happened and now will not. Already a daemon log line. |
| the client's merge | — | It happens on the user's machine, outside the daemon. The T-19 journal is its record; the chain records the daemon's *acceptance* of it. |
| `ConfirmMerge` **accepted** | **`queue_entry_merged`** | The act. Main moved. |
| `ConfirmMerge` **refused** | **`merge_confirm_refused`** | The asymmetry that makes this worth an event and a `BeginMerge` refusal not: **by the time this RPC is reached the git operation has already run.** Refusing does not prevent a merge — it means the daemon and the user's repository may now disagree about what main is, which is the one outcome the whole subsystem exists to prevent. That divergence is precisely what someone investigates later. |
| `AbandonMerge` | — | Nothing landed and nothing was recorded; the design's own words. An event here would record an intention that was withdrawn. |
| the RT-D2 waiver | **`acknowledged_flagged_change`** | L4 — the same event type the flagged-change acks already use, so "what did a human wave through on this branch" has one answer and not two lists to union. `kind` separates them, exactly as it does across `FlaggedKind`. |

### 21.3 Where `queue_entry_merged` is emitted, and why not at the RPC

**Four paths reach `Merged`**: the `ConfirmMerge` RPC, the RT-D1 boot reconcile (`ConfirmHumanMerge`, wired
through `GatewayServiceRegistration`'s `onMerged`), the P2-12 external-PR dispatch, and dev seeding. An
event wired to the RPC alone would have left a crash-recovered merge *exactly as unrecorded as every merge
was before* — the same defect in the one case nobody watched happen.

So the append lives in `MergeQueue`, at both confirm entry points, which is what makes the real invariant
enforceable: **no transition to `Merged`, by any path, without exactly one `queue_entry_merged`.** What the
queue cannot know is *who* and *by which path*, so that is a parameter — `MergeAuthorization` — and its
`source` field distinguishes `confirm_rpc` from `boot_reconcile` from `external_dispatch` from `seeded`. A
record that could not tell those apart would put a person's name on a daemon's reconciliation, the mistake
`verification_restart_resume` exists as a separate type to avoid. A caller that names nobody is recorded as
`unknown`/`unattributed` rather than borrowing a name.

The payload is built **before** the transition, under the same lock that decided it. Read afterwards,
`from_state` is always `Merged` and the pre-merge main is gone — an audit record of its own effect.

### 21.4 What the record carries, and why each field is there

`queue_entry_merged`: `repo`, `agent`, `by`, `source`, `lease`, `from_state`, `pre_main_sha`,
`post_main_sha`, `when`, the verification block (`verification_main_sha`, `verification_branch_sha`,
`verification_passed`, `verification_command`, `verification_config_hash`, `verification_when`), and
`gates`.

- **Both shas.** An event carrying only the new one cannot answer "what did main used to be", which is the
  first question anyone asks when a merge turns out to have been wrong.
- **The verification block** answers "which run said this was green" — unanswerable from the state machine
  once the entry is terminal. Its **absence** is a real state (the boot reconcile records merges for
  entries a rebuilt queue never verified), so it is stated as `verification = none recorded` rather than
  rendered as blank fields that read like a run which printed nothing. Same distinction as
  `GetVerificationLog`'s `unavailable_reason`.
- **`gates`** is the evidence half, and it is the reason a new seam was added. `IMergeGate.Allows`
  returning true is not a record of anything: it is equally true of every merge that ever happened,
  including the one under investigation. The default-null `IMergeGate.MergeEvidence(agentId)` lets each
  gate state what it had *established* — `flagged-change review: 3/3 acknowledged (set <hash>)`,
  `changed-test-command: test command changed vs main — acknowledged`, `plan gate: plan approved`. The
  flagged-set hash is included because it is the only thing that makes the count mean anything later.

`acknowledged_flagged_change` from the RT-D2 gate adds `path`, `from`, `to`, `from_hash`, `to_hash` and
`by` to the shape the flagged-change store already writes. "The test command changed" names a category; the
record has to say **which command, from what to what, and who waived it**. The two committed config trees
are the only place the baseline and the replacement exist together, so `MergeQueueProvisioner` now hands
both to the gate at flag time. Excerpts are capped (a repo's config file must not decide the size of an
audit payload) with the full content pinned by SHA-256 beside them, and three answers are kept apart —
`(not recorded)`, `(absent)`, and the content — because collapsing the first two would render "we did not
capture the baseline" as "this branch invented a verification command out of nothing".

One event **per item waived**, not per click: the click clears every armed item at once by design, but what
was waived is the items, and a single event would make "the command changed" and "the toolchain changed"
indistinguishable. Idempotent, so a cockpit that refreshes twice cannot inflate how often a human decided.

### 21.5 What happens when the audit store is down

`queue_entry_merged` lets the append **throw** (the chained log's documented contract). The merge has
landed on a ref either way; the throw surfaces as a failed `ConfirmMerge` with the lease still outstanding,
so the next boot's RT-D1 reconcile picks it up. An audit outage therefore *delays* the record instead of
silently losing it. `merge_confirm_refused` is the opposite — best-effort, swallowed into the daemon log —
because the refusal reason is the caller's answer and must not be replaced by an audit-store error.

### 21.6 The mutation log

Sixteen mutations, each rebuilt and re-run; every one red. (`touch` on restore — an `mv`/`cp` restore
preserves mtime, MSBuild skips the rebuild, and the next run tests the *mutated* binary.)

| # | mutation | caught by |
|---|---|---|
| M1 | `TryConfirmHumanMerge` drops the merged append | `MergeAuditEventTests` (5 red) |
| M2 | `ConfirmHumanMerge` (the reconcile entry point) drops it | `MergeAuditEventTests` (3 red) |
| M3 | payload built *after* `MarkMergedLocked` | `MergedEvent_CarriesBothShas…` |
| M4 | absent verification rendered as empty fields | `MergedEvent_WithNoVerificationRecord…` |
| M5 | gate evidence dropped from the record | `MergedEvent_RecordsWhatEachGateHadEstablished` |
| M6 | merged event appended even when the gate **refuses** | `RefusedConfirm_AuditsNothing` |
| M7 | `ChangedTestCommandGate.Acknowledge` audits nothing (**the original L4 defect**) | `ChangedTestCommandAuditTests` (8 red) |
| M8 | the waiver re-appends on every call | `AcknowledgingTwice_AppendsOnce` |
| M9 | the waiver drops the actor | `AnUnattributedWaiver_SaysUnknown` |
| M10 | `(not recorded)` collapses into `(absent)` | `AnUnrecordedDrift_AndAnAbsentFile_ReadDifferently` |
| M11 | excerpt cap removed | `AHugeCommand_IsExcerptedButStillHashedInFull` |
| M12 | drift detail not refreshed when the item re-arms | `ADriftThatChangesWhileArmed_IsRecordedAsItsLatestForm` |
| M13 | the provisioner stops handing the gate the two trees | `TheWaiverRecord_NamesTheBaselineAndTheReplacement…` |
| M14 | `ConfirmMerge` drops the `MergeAuthorization` | `AMergeThroughTheRpcs_LeavesOneRecordInTheChain` |
| M15 | the lease-stage refusal is not audited | `ARefusedConfirm_IsRecorded_AndRecordsNoMerge` |
| M16 | `AcknowledgeFlaggedChange` drops the actor | `AcknowledgingTheChangedTestCommand_WritesTheWaiver…` |

M12 was **MISSED on the first pass** and is the useful one. The re-arm test cleared the item before
re-flagging it, which removes the stored detail on the way through, so a mutation that only refused to
*overwrite* an existing entry slipped past. The real shape is a branch that pushes again while the item
stays armed — the provisioner's own cadence — and the test now walks it.

### 21.7 Left alone, deliberately

- **`BeginMerge` is not audited.** See §21.2. Its grant is named by the merge record's `lease` field.
- **`acknowledged_flagged_change` carries no `repo`.** Neither the RT-D2 gate nor `AcknowledgmentStore`
  knows its repo, and the requirement was consistency with the event the flagged-change acks already
  write. Adding the field to one of the two writers would have produced a different inconsistency.
- **A drift that changes while the item is already flagged does not re-arm the gate.** `SetFlagged` re-arms
  on `items.Add` returning true, so a *content* change under a still-armed item leaves an existing
  acknowledgment standing — unlike `AcknowledgmentStore`, whose acks bind to a content hash. That is a
  pre-existing gap in the RT-D2 gate and is out of scope here; what this change guarantees is that the
  waiver *record* describes the latest drift rather than the first (M12).

---

## 22. The cascade test that "never returned B to Verified" — two defects stacked, and only one of them was in the test

`MergeQueueEndToEndDockerTests.WhenOneBranchMerges_TheCoTenantIsInvalidated_BlockedFromMerging_AndReVerifiedAgainstTheNewMain`
was the one red left on the merged phase-3 branch, reporting *"the cascade's auto re-verification never
returned B to Verified"*. Three agents had looked at it and reached three different conclusions, and a
live stress run had watched the same cascade work. All of that turns out to be consistent.

### 22.1 The evidence, reconciled

| who | what they saw | why |
|---|---|---|
| J1 (`be817cbe`) | `RequiresDocker` **130/0** | the coin came up heads |
| L1/L3 (`f7c90466`) | **130 passed, no flakes** | likewise |
| L2/L4 (`a651126c`) | red on its branch AND on base `e6327fe3` → "pre-existing" | **correct**, and the only one who checked |
| a live stress run | a real co-tenant reparented and re-verified green in seconds | live, nothing races the cascade |

Measured here rather than argued. One test, run alone, at four points:

| commit | result |
|---|---|
| `9ed5e767` — the commit BEFORE the J1 lane | **1 red, 1 green** in two runs |
| `be817cbe` — J1 | 1 green, **2 red** in three runs |
| `e6327fe3` — the J1 merge | **3 red** in three runs |
| `4de197f8` — the tip | **3 red** in three runs |

It fails before J1 exists. Nothing regressed. The failure is a **coin flip with a bias that varies with
machine speed**, which is exactly what four agents disagreeing about one test looks like. The passing
count never changed either: the tier is 132 with 2 skips, and the base branch measured **today** gives
`128 passed / 2 failed` with the cascade test GREEN and a different second red — see §22.5.

### 22.2 The outer defect: the fixture's dwell knob was untracked, and that was not enough

`FixtureRepo.DelayFile` (`.verify-delay-ms`) is a worktree-only knob the fixture's `verify.js` reads to
make B's re-verification dwell, so the invalidated window is a fact rather than a race. Its own doc
said why it must never be in a tree: *"anything in the tree would differ between a branch and main and
arm the RT-D2 gate."*

`KeepAliveRebaser` preserves an agent's uncommitted work across the reparent by committing
`git add -A` as `wip: sync` **before** it rebases — and `add -A` stages untracked files. So the cascade
swept the knob into B's tree, and the test's next line — `DeleteUntracked`, there to shorten the
re-verify — turned it into an **unstaged deletion**, which `git rebase` refuses outright. Measured, with
the cycle instrumented:

```
t0=07:42:33.879 dirty=[?? .verify-delay-ms]        → wip: sync commits the knob
        34.007  the test deletes the knob
t1=07:42:34.082 dirty=[ D .verify-delay-ms]        → error: cannot rebase: You have unstaged changes
```

A ~200 ms window, and the test walked straight into it because the cascade's yield and the test's own
two refused-merge RPCs take about the same time. The cascade then blocked with the honest reason —
*"this branch is not on top of the new main yet — Rebase returned 1 …"* — which nobody ever saw, because
the assertion said only "never returned B to Verified" after waiting out a three-minute timeout.

**The fix is the missing half of "untracked": the seed writes a `.gitignore` naming the knob.** `add -A`
then does not stage it, the knob is in no tree (which is what its doc always claimed), and the rebase is
clean. The test also stops deleting the knob — the delete existed only to shorten the re-verify, it cost
one dwell, and removing it means the test no longer interleaves with the cascade it is measuring at all.
That is what turns the `.gitignore` from a mitigation into a checkable guard: with the delete gone, an
un-ignored knob is swept into the tree on **every** run.

### 22.3 The inner defect, which the outer one had been hiding: the cascade could rebase onto a main the mirror had never seen

With the fixture fixed, the test failed differently — and this one is a product bug:

```
aTip = mirrorMain = checkoutMain = queueMain = e8802a18
mirrorB = c4eab929   log: c4eab92 docs: notes | 168a0d6 seed: node fixture project
```

B is `Verified`, `CanMerge` says yes, and **B's branch does not contain main at all**. It was never
reparented; it was re-verified where it stood.

`ConfirmMerge` does two things in this order:

1. `TryConfirmHumanMerge` — which, under the queue lock, marks `Merged` and fires `NotifyMainMoved`, i.e.
   **starts the cascade**;
2. `TryRefreshMirrorMainAfterMerge` — which pulls the mirror's `refs/heads/main` forward from origin.

So a cascade that reaches its rebase before step 2 lands carries the **pre-merge** main into the agent's
repository. `git rebase main` finds the branch already on top of *that* main, exits 0 having moved
nothing, and the cycle reports `CleanNoop` — which `RebaseCycleResult.BranchIsOnTopOfMain` reads as *"safe
to re-verify."* The re-verification is pinned to the **new** main (the queue's), passes, and settles
`Verified`. Green rail, enabled Merge button, and a `--ff-only` that refuses forever: the precise
loop-forever state `TryReturnToWorking` exists to prevent, reached by the one route that skips it.

This is the half of the defect `KeepAliveRebaser.TryRefreshMainFromMirror` did not close.
`42b6a9a1` made that method read the fetch's exit code, with the right argument — *"a cycle that cannot
establish what main IS must not claim the branch is on top of it."* A fetch that **succeeds** against a
mirror which is merely behind establishes a main just as confidently, and the wrong one.

Also pre-existing: `TryRefreshMirrorMainAfterMerge` has been at the end of `ConfirmMerge` since
`04e8b2d8` (2026-08-18), twelve days before the J1 lane. Its own doc even names the reason nobody caught
it — *"the window between a merge and the next repo-open is a trap the E2E suite never walks (it verifies
every agent before merging)."* The cascade test is the one test that walks it, and the fixture bug meant
it never got far enough to.

### 22.4 What changed, and why not somewhere else

Two guards in `MergeQueueProvisioner.RequeueStaleAsync`, both refusing only on a **positive** mismatch —
an unreadable mirror or an empty sha answers nothing, and refusing from ignorance would strand every
substrate-less caller.

- **`TryAlignMirrorMain`, before the rebase.** If the mirror's main is not the queue's `main@sha`, catch
  it up with the same one-refspec pull the merge-confirm makes, then require equality; a mirror that still
  disagrees blocks with a measured reason instead of minting a green. Making the cascade idempotent about
  the catch-up is what stops it having to *win* a race with `ConfirmMerge` — the ordering inside that RPC
  is left alone, because reordering it would advance the mirror for a merge the gate may still refuse, and
  the fix has to hold for the cascade's other origins (`MergeDispatch`, the RT-D1 boot reconcile, the
  seeder) too.
- **`BranchDescendsFromMain`, before the re-verify.** Ask git whether the published `agent/<id>` really
  contains the queue's main. That is the single predicate the whole re-entry exists to establish, and
  every way the cascade has ever got it wrong — a stale rebase target, a rebase that exited 0 having moved
  nothing, a publish that reported success — ends with this answer being no. It costs one `merge-base`, it
  depends on no event having fired, and it is kept for the same reason §19.6's M5 keeps `CanMerge`'s
  branch-side compare.

The catch-up introduces its own hazard and the code says so: `TryRefreshMirrorMainAfterMerge` is a
**forced** single-refspec fetch, so firing it at a mirror that is *ahead* of the queue would drag main
backwards and reparent every co-tenant onto a commit the human has already moved past. So a mirror that
already CONTAINS the queue's main is left alone — being ahead is not this cascade's business.

The test grew two assertions to match: the cascade must have really **reparented** B (`aTip` is an
ancestor of the mirror's `agent/<id>`, not merely "B reached Verified"), and the dwell knob must be in no
tree. Its failure message now carries the queue's own gate reason, because "still Working" is the symptom
of every terminus the re-entry has and a bare timeout is what sent three agents hunting a regression that
was never in the product.

### 22.5 The mutation log

Every guard removed in turn, the tier rebuilt and re-run. (`touch` on restore — an `mv`/`cp` preserves
mtime, MSBuild skips the rebuild, and the next run tests the *mutated* binary. This bit once here: a
first M1 pass reported 0 red against a stale assembly, and the mutation runner now refuses to report
unless the built `Mainguard.Agents.dll` mtime actually moved.)

| # | mutation | result |
|---|---|---|
| M1 | the mirror catch-up never runs | 1 red — `WhenTheMirrorHasNotSeenTheMergeYet…` |
| M2 | it compares but never catches up (blocks instead) | 1 red — same test; pins *catch up*, not merely *refuse* |
| M3 | the ahead carve-out is dropped, so the forced fetch can drag main backwards | 1 red — `AMirrorAlreadyAheadOfTheQueue…` |
| M4 | the descent belt is dropped | 1 red — `ARebasedBranchThatNeverReachedTheMirror…` |
| M5 | the belt asks the ancestry the wrong way round | **3 red** |
| M6 | the fixture's `.gitignore` is dropped | 1 red (2 of 2 runs) — the knob is swept into the tree |

**M3 was 0 red on the first pass**, and that is why it has a test now. The carve-out is a hazard *this
change introduced*, so leaving it uncovered on the §19.6-M5 precedent would have been the wrong reading:
M5 is a belt on a defect, this is a new forced-write path. `AMirrorAlreadyAheadOfTheQueue…` puts the
mirror two commits ahead of the sha the queue is told about and asserts the cascade left it there.

**M6 was also 0 red at first**, and the reason is worth keeping. Once the catch-up existed, its `git
fetch` shifted the cascade's own timing enough that the test's delete reliably landed *before* the wip
commit — the coin was still being flipped, it had simply started landing the other way. Deleting the
knob at all was the last interleaving between the test and the cascade; removing that line is what makes
the guard bite on every run rather than on a lucky one.

M1's effect on the E2E tier was measured directly rather than inferred: the pre-fix product failed the
new reparent assertion in 4 of 6 runs, with B `Verified` on a branch that did not contain main.

### 22.6 Tier results, and the two flakes that remain

`Category=RequiresDocker`, three runs after the fix: **128/2**, **128/2**, then **129 passed / 1 failed /
2 skipped of 132** — against **128 / 2** on the unmodified base `4de197f8`, measured on the same machine
within the hour and with the cascade test GREEN in that base run. That last fact is the one that matters:
the headline number did not move because the tier has *two* intermittent tests, and different agents have
been reading different pairs of them as "the failure".

- The cascade test itself: **8 green in a row** (5 single runs + 3 after the fixture change) plus green
  in all three post-fix tier runs, down from ~3 minutes to ~15 seconds.
- `SandboxNetworkIsolationDockerTests.ReachabilityProbe_ClassifiesOnTheHandshake_NotOnGettingAPrettyReply`
  — the known flake. Confirmed: red in the tier, red once and then green on a repeat in isolation.
- `QueueEntryResumeDockerTests.AStrandedEntry_IsResumed_VerifiesInItsNewJail_AndBecomesMergeable`
  — **§12.5's documented docker-suite ordering flake**, verbatim: red in the full run, green every time
  the class is run on its own (3/3 here), and red on the unmodified base too. Its failure is
  `verified.Passed == false` in the resumed jail. It did not fire in the third run at all. Still
  undiagnosed, still recorded rather than fixed — and, with the network probe, now the only thing between
  this tier and a clean run.

### 22.7 Left alone, deliberately

- **`ConfirmMerge`'s internal ordering is unchanged.** Refreshing the mirror before
  `TryConfirmHumanMerge` would advance it for a merge the gate can still refuse, and would fix only one
  of the four paths that fire the cascade.
- **`RebaseCycleKind.CleanNoop` still implies `BranchIsOnTopOfMain`.** Given a correct rebase target it
  is a true statement, and the cycle is not the right place to learn what the queue's main is; the caller
  that knows both is the one that now checks.
- **`git add -A` in the wip commit is not narrowed.** Sweeping untracked files is what preserves an
  agent's in-flight work across a reparent, and `.gitignore` is the answer a repository already has for
  files that must not be swept — which is exactly what the fixture was missing.
- **The two tier flakes are not chased.** §12.5 owns the resume one and says why it is still open.

---

## 23. Merge identity — the six stale-evidence defects §19.5 catalogued, taken as one problem

§19.5 listed six. They are not six bugs; they are one, six times. Every one of them is a **decision
recorded against a sha, acted on after that sha moved** — J1's own sentence, and the reason that fix
(`BranchTipInvalidator` + `VerificationRecord.BranchSha`) closed exactly one instance of it and could not
reach the rest. §22 then found a seventh independently, which is what a class of defect looks like when
you keep patching instances of it.

### 23.1 What merge identity is

The merge is the one irreversible act in the product: it moves the user's `main`. Everything upstream of
it — verification, the flagged-change gates, the review cockpit, the plan gate — exists to produce **one
decision**: *this branch may land on that main*. So the decision is not a boolean. It is a claim about a
**triple**:

| | what it is | where it is recorded today |
|---|---|---|
| **branch** | the `agent/<id>` tip the evidence was measured ON | `VerificationRecord.BranchSha` (J1), and now `MergeLeaseRow.ExpectedBranchSha` |
| **main** | the `main@sha` the evidence was measured AGAINST, and which the merge may fast-forward | `VerificationRecord.MainSha`, `MergeLeaseRow.ExpectedMainSha` |
| **result** | the sha main becomes | `MergeLeaseRow.PostMergeSha`, `queue_entry_merged`'s `post_main_sha` |

Three rules follow, and every change in this section is one of them applied somewhere:

1. **Evidence must record what it is evidence FOR.** A record that pins one leg of the triple can only
   ever ask about that leg. §19's H6 is the general form: a `VerificationRecord` that pinned `main@sha`
   and nothing else was *structurally unable* to notice the branch had moved. K3 and K4 are the same
   sentence about the lease.
2. **The check happens at the moment of ACTION, not the moment of decision.** A verdict computed from a
   snapshot and acted on later is a verdict about a world that may no longer exist. K6 is the purest
   instance — the guard re-checked the lock, which was never part of its verdict, and not the three
   things that were.
3. **Where a check can ask git instead of trusting a component's self-report, ask git.** §22's
   `BranchDescendsFromMain` is the precedent, and K1 is where it mattered most.

And the disposition when the triple cannot be established: **refuse.** `AgentBranchReapVerdict.Undecidable`
(J1) is the precedent — an unanswerable question gating a destructive act is a "no". That is deliberately
NOT the same rule as "an unknown never manufactures a refusal", which this codebase also follows and which
§19.6's `CanMerge` compare states: **an unknown declines to answer; an unknown does not license an act.**
The two look contradictory and are not. `CanMerge` refusing on ignorance would strand every pre-existing
row forever and merges nothing; `MergeReconcileTask` proceeding on ignorance walks a row to a terminal
state and moves a ref. The asymmetry is whether the ignorance sits in front of an action or in front of a
refusal.

### 23.2 K1 — `MergeReconcileTask` could mark an unrelated entry Merged on a coincidence

The worst of the set, and it is worth stating exactly how little it required. The old predicate:

```csharp
var advanced   = currentMain != lease.ExpectedMainSha;
var hasMergeEntry = _journal.GetHistory(repoPath)
    .Any(e => e.Kind == JournalKinds.Merge);   // ANY entry. Any age. Any branch. Any repo state.
if (advanced && hasMergeEntry) { Confirm(...); onMerged(...); }
```

Two facts about a **repository**, neither of them about **this lease**. `advanced` is satisfied by a
`git pull`, a co-tenant's merge, a hotfix commit, a rebase — anything that moves main. `hasMergeEntry` is
satisfied by a merge somebody performed last month. Together they synthesized a `ConfirmMerge`: the lease
went `Confirmed` (so nothing could ever revisit it), the queue row went terminal `Merged`, and
`NotifyMainMoved` fired the whole stale cascade at every co-tenant — for a merge of that agent that never
happened. This is the only defect in the set that can **fabricate a terminal state**, which is why it was
done first.

**Verified before changing anything.** `ART_D1_Reconcile_DoesNotMarkALeaseMerged_WhenItWasADifferentBranchThatMerged`
builds the shape and asserts on the old predicate's own inputs: two branches `agent/x` and `agent/y`, both
one commit ahead of the same seed, a lease taken for `x`, and then `y` merged. Both conditions hold. The
old code confirms `x`.

**The fix is the identity, asked of git.** `Classify` returns one of three verdicts:

| verdict | when |
|---|---|
| `NeverCommitted` | main is still the sha the lease was authorized against. Unambiguous, and the one arm that was always right. |
| `Merged` | main moved **forward from** `ExpectedMainSha` **and** now **contains** `agent/<id>` — `merge-base --is-ancestor`, twice. That pair is what `merge --ff-only agent/<id>` *means*, and it is false for every unrelated reason main might have moved. |
| `Undecidable` | anything else — main unreadable, main not a descendant of the authorized sha, or the branch gone and the journal unable to identify the merge. |

Three smaller decisions inside that are load-bearing:

- **An unreadable main is not an unchanged main.** The old code read "no sha" as `advanced == false` and
  told the human "no ref moved" as a fact. It now says it could not tell.
- **The forward-descent check is separate from the containment check**, and it comes first. A rewound or
  sideways main cannot be a fast-forward's effect whatever else is true, and saying so as its own arm is
  what makes the failure legible rather than lumped in with "the branch isn't in main".
- **The journal is corroboration, never an independent sufficient condition.** It is consulted only when
  the branch ref is *gone* (deleted after the merge, or never local in this checkout) and git therefore has
  nothing left to be asked. Even then it is used in identity-bound form only: kind `Merge`, at or after
  `lease.BeginUtc`, a description naming this branch, and — read through the new
  `OperationJournal.TryReadRef` — the entry's own pre/post snapshots showing `refs/heads/<main>` moving
  **from** the lease's expected sha **to** the sha main holds now. Both snapshot reads must SUCCEED; a
  snapshot that does not name main is an entry that cannot say what the merge did, and treating a missing
  answer as a match is how "any `Merge` entry anywhere" got written in the first place. The description
  match is a **self-report**, which is exactly the kind of evidence §22 says to prefer git over — so it is
  the fallback, under three independent constraints, and never the check.

**`Undecidable` still hands the lease back.** Holding it would strand every future merge on the repo until
the next restart, which is the RT-D1 strand this task exists to sweep. What is withheld is the only
irreversible half: nothing is marked `Merged`, no cascade fires, and the human is told the ambiguity
("main has since moved for some other reason — nothing shows this merge landed, so it was NOT recorded")
rather than one horn of it restated as fact.

### 23.3 K2 — the merge consumed a stale local ref over the one it had just fetched

`PerformJournaledMerge` step (2) fetches the sync remote and treats a failure as fatal, in its own words:
*"whatever `agent/<id>` happens to be in this repo is then an unknown-age copy, and merging it would land
work the queue never verified."* Step (4) then called `ResolveMergeSource`, which **preferred the local
`refs/heads/agent/<id>`** and fell back to the tracking ref only when there was no local branch at all. So
on any checkout that had ever brought the branch local, the fetch's result was discarded and the merge
consumed precisely the unknown-age copy step (2) exists to prevent. `--ff-only` does not help: it
guarantees main-ancestry and guarantees nothing about the source being the verified ref.

Now two rules, in order:

1. **Identity, when it is known.** The lease carries the verified `agent/<id>` tip (§23.4). Whichever
   spelling resolves to exactly that sha is the source. If **neither** does, the merge refuses — reported
   with `CasLost: true`, because the branch really has moved out from under the evidence and re-verifying
   is the right answer. A namesake is not the branch.
2. **Freshness, when identity is unknown.** With no measured sha the honest tiebreak is recency, and the
   just-fetched remote-tracking ref is the only one whose age this method can vouch for. Local is used
   only when there is no tracking ref at all — the exact inversion of the old preference.

### 23.4 K3 — the lease was a mutex over a repository, and `ConfirmMerge` believed the client

Two halves, and the second is the one that could move a ref.

**The lease.** `MergeLeaseRow` carried `ExpectedMainSha` and no branch sha, so it recorded *which main a
merge was authorized against* and could not record *which commits were authorized to land on it*. The
branch could move between the grant and the merge; the merge could consume a different ref of the same
name (K2); the merge could be of a completely different tip; and every one of those still satisfied the
lease. `MergeLeaseRow.ExpectedBranchSha` (migration `AddMergeLeaseExpectedBranchSha`, default `""`) is the
missing half, populated at `BeginMerge` from the daemon's **own** record —
`Queue.LastVerification(agentId)?.BranchSha` — and returned to the client on `BeginMergeResponse` beside
`expected_main_sha`, for exactly the reason that field is already returned there: a client's own
projection is a stream snapshot and is allowed to be a revision behind.

**Is it a CAS now? — the argument, because "make the lease a CAS" is the wrong shape.**
`DbMergeLeaseStore.TryBegin` is a uniqueness check: one unconfirmed lease per repo, first caller wins,
losers are told "another merge is already in progress for this repository". That is **mutual exclusion**,
and it is correct — it is what freezes conflicting queue actions for the duration, and the merge is
human-driven and irreversible, so the one thing a losing caller must never get is a silent retry that
merges later against a world nobody looked at. Turning `TryBegin` into a compare-and-swap **loop** would
convert an honest refusal into exactly that.

What a CAS actually gives you is narrower and is the part that was missing: *the write happens only if the
world is still the one you decided from*. That is now true, and it is enforced at three points rather than
inside the take:

| where | compare | on failure |
|---|---|---|
| `PerformJournaledMerge` (4) | the merge source must BE `ExpectedBranchSha` | refuses, `CasLost: true`, no ref touched |
| `ConfirmMerge` RPC | the reported `NewMainSha` must be the identity's result (below) | `FailedPrecondition`, lease released, `merge_confirm_refused` audited |
| `TryConfirmHumanMerge` under the queue lock | `ExpectedMainSha == _currentMainSha`, plus `CanMergeLocked`'s record-vs-main and record-vs-branch compares | refuses, nothing transitions |

**What a losing caller now sees.** Unchanged for the contended case: a second `BeginMerge` while a lease is
outstanding is still refused with the same sentence, and nothing spins or blocks. What is new is that a
caller whose *identity* moved under it gets a specific refusal instead of a successful merge of the wrong
thing — at the merge leg, "`agent/<id>` in this repository is not the commit the queue verified (`<sha>`);
re-verifying", and at the confirm, a `FailedPrecondition` naming the mismatch. Both are `CasLost`-shaped:
the queue re-verifies and the entry becomes mergeable again on its own. **No call that used to succeed on a
matching identity now fails** — the compares are all equalities against a sha the daemon itself recorded,
and an empty `ExpectedBranchSha` (a seeded row, a lease from before the column existed) is read everywhere
as "not measured" and never as a mismatch.

**`ConfirmMerge`'s `NewMainSha`.** It was client-supplied and validated against nothing. The daemon wrote it
into the idempotency record, set the queue's authoritative main to it, and fired the cascade at every
co-tenant on the strength of a claim about a ref on the caller's machine. A wrong value is not cosmetic:
every co-tenant is then reparented onto, and re-verified against, a main that may not exist, and `CanMerge`
compares its evidence against a phantom forever. Three checks, before anything transitions:

- **Shape.** 7–64 hex characters, or refuse. Deliberately a shape check and nothing more — the daemon
  cannot resolve a sha in a repository it does not hold, and a shape check pretending to be an existence
  check would be the same fabrication as the claim it screens.
- **Non-triviality.** `NewMainSha != lease.ExpectedMainSha`. A confirm reporting the main it was authorized
  against is a merge that moved nothing.
- **Identity, and it is exact.** A **local** entry merges by `git merge --ff-only agent/<id>`, and a
  fast-forward sets main **to the source's tip**. So the sha main moved to must BE the `agent/<id>` tip the
  queue verified — a sha the daemon put on the lease itself, so the client's claim is checkable against the
  daemon's own record without reading the client's repository at all.

That last one is stated as a limit rather than stretched: it applies to `MergeEntryOrigin.Local` only. The
P2-12 external leg lands the **host's merge commit**, which is not the PR head and could not be, so the
same equality would be false for every honest external merge. That path has its own head CAS (K4) and the
host's own `sha` merge parameter. A refusal at any of the three releases the lease and writes
`merge_confirm_refused` with `stage = "identity"` — §21.2's asymmetry applies unchanged: by the time this
RPC is reached the git operation has already run, so a refusal means the daemon and the user's repository
may now disagree about what main is, and that divergence is what someone investigates later.

### 23.5 K4 — the external merge re-derived the head it was supposed to be checking

`ExternalPrMergeService.PrepareCheckout` computed its "verified head" by reading whatever
`refs/heads/agent/pr-<n>` this checkout currently held, and `ClassifyUpstreamState` then compared the PR's
upstream head against it. But `PrHeadFetcher` **hard-resets** `agent/pr-<n>` to the PR's newest head
*before* the intake calls `NotifyNewCommits`. So between a force-push and the next intake poll, both sides
of the compare are the new head, the CAS passes, and unverified third-party code merges. The comment
above it asserted the opposite — *"the mirror's tip IS the commit the queue verified"* — which stopped
being true the moment the reset was ordered that way. This is the external-origin twin of H6, and §19's
fix cannot reach it precisely because the reset happens first.

The verified head is now **read**, not derived: `request.ExpectedBranchSha`, from the lease. The local ref
is used only to establish that the recorded head is *present* in this checkout under either spelling —
"is the commit the queue verified here", never "what shall we call the verified commit".

**This is the one place in the lane that refuses on an unknown.** Everywhere else an unmeasured sha means
"decline to answer"; here declining to answer means merging code from outside this installation on the
strength of a compare that cannot fail. So an empty `ExpectedBranchSha` refuses: *"the queue has no
recorded verified head for this pull request… re-verify the entry, then merge."* The live path populates
it — external entries are ordinary queue entries verified by the provisioner, which resolves `agent/<id>`
from the mirror after the pre-verification publish — so the refusal is reachable only by a genuinely
unverified entry, which is the one it is for.

### 23.6 K6 — the guard re-checked the one precondition that was never part of its verdict

`GitMutationGuard.CanMutate` decides from a `GitDirState` snapshot: mid-rebase, detached HEAD,
`MERGE_HEAD`. `RunGuarded` then waits out up to five `index.lock` backoff attempts — ~1.5 s by default —
and re-checked **only the lock**, which was never one of the three. The three that *were* went unlooked-at,
and they are exactly the states a worktree enters while a lock is held: the usual reason the backoff is
running at all is that git is busy establishing one of them. So the keep-alive cycle could run
`git add -A; git commit` and `git rebase main` against a worktree whose agent had started its own rebase
during the wait.

`RunGuarded` gains `recheck`, evaluated once the lock is clear and immediately before the action, and
`KeepAliveRebaser` hands it a re-read of the same `Inspect`+`CanMutate` pair the cycle opened with — the
same predicate, not a second opinion about it. A refusing verdict raises
`GitMutationStateChangedException` (distinct from `GitMutationLockException`: that one means the lock never
cleared and nothing was attempted; this one means the lock cleared and the worktree had moved on), the
cycle returns `Skipped` with the measured reason, and the next tick retries — the "skip and retry" arm the
cooperative-yield contract has always had. `WipCommitCreated` is reported honestly, because the wip commit
may already have landed before the rebase leg refused.

The parameter is nullable only so a caller with no worktree to inspect (the pure-backoff tests) can still
exercise the lock loop; a re-check that cannot be performed must not fabricate a refusal.

### 23.7 K5 — deliberately NOT fixed here, and why, with the design it needs

**The defect is real.** `WorkerPlanGate.Allows` answers "was some plan approved for this worker", never
"does the current tip still match its scope". An approved `TaskPlan` is keyed to a plan id with **no tie to
code**: no repo sha, no branch sha, no diff hash. The scope is only ever re-evaluated when
`ArmFlaggedChangeReview` runs, so it inherits §19's cadence exactly — which is what makes it *correct
today* and structurally unable to stay correct.

**What was changed here is only the identity half**, because a re-scope operation is being added
concurrently on another branch and the scope machinery is its subject. `MergeEvidence` said `plan gate:
plan approved` — naming no plan. A worker can present, revise and re-present, so "some plan was approved"
is not a reference to anything, and §21's whole argument for `gates` is that a record has to say what was
*established*. It now names the plan id and title, with a separate sentence for the case where `MayWork`
says yes and no plan can be identified — an audit line that claims an approval it cannot identify is the
fabrication this lane exists to remove. **No scope evaluation, no `IMergeGate.Allows` behaviour, and no
plan-approval state was touched.**

**The recommended design for the rest**, for whoever lands it: an approved plan should record the
`(branchSha, mainSha)` it was approved against, exactly as `VerificationRecord` now does — the approval is
evidence and must record what it is evidence FOR (§23.1 rule 1). `Allows` then has a question it can
actually ask: *is the current tip's diff-against-main still within the scope this plan was approved for?*
With `BranchTipInvalidator` already walking a moved branch out of `Verified`, the re-scope trigger is
free: the same sweep that voids a verification is the sweep that should void a scope verdict. Until then
the honest statement is that the plan gate is a gate on **provenance** (was this worker cleared to work)
and not on **content**, and §23's merge record now says which clearance.

### 23.8 The mutation log

Every guard removed in turn, the tier rebuilt, the failure recorded. `touch` on restore — an `mv`/`cp`
preserves mtime, MSBuild skips the rebuild, and the next run tests the *mutated* binary. §22.5's lesson is
mechanized here: the runner **refuses to report** unless a built assembly's mtime actually moved.

| # | mutation | result |
|---|---|---|
| K1-M1 | the git identity check is dropped (main moved ⇒ Merged) | 2 red |
| K1-M2 | the forward-descent check is dropped | 1 red — see below |
| K1-M3 | the journal fallback drops the branch-name filter | 1 red |
| K1-M4 | an unreadable main is read as `NeverCommitted` | 1 red |
| K1-M5 | the journal fallback drops the post-state main compare | 1 red — see below |
| K1-M6 | the containment is asked the wrong way round | 1 red — see below |
| K1-M7 | `Undecidable` synthesizes the confirm anyway | 4 red |
| K1-M8 | the multi-remote branch lookup takes git's first listing instead of refusing | 1 red |
| K2-M1 | the stale local ref is preferred again (the original defect) | 1 red |
| K2-M2 | the identity compare is dropped, falling back to freshness | 1 red |
| K2-M3 | a source matching neither ref is merged anyway | 1 red |
| K3-M1 | `BeginMerge` records no branch sha on the lease | 17 red |
| K3-M2 | the post-merge sha's shape screen is dropped | 1 red |
| K3-M3 | the non-triviality compare is dropped | 1 red |
| K3-M4 | the fast-forward identity compare is dropped | 1 red |
| K3-M5 | that compare is applied to `External` entries too | 4 red |
| K4-M1 | the verified head is re-derived at merge time (the original defect, restored whole) | 2 red |
| K4-M2 | an unrecorded verified head no longer refuses | 1 red |
| K4-M3 | the presence check accepts a head that is not the recorded one | 1 red |
| K5-M1 | the merge evidence stops naming the plan | 1 red |
| K5-M2 | an unidentifiable approval reads as an ordinary one | **0 red — deliberately uncovered** |
| K6-M1 | the re-check is never consulted | 2 red |
| K6-M2 | the re-check runs and its refusal is ignored | 1 red |
| K6-M3 | the re-check is evaluated before the lock loop instead of at the action | 1 red |
| K6-M4 | the rebaser stops handing the guard a re-check | 1 red |

Five are worth reading.

**K1-M2 and K1-M6 were both 0 red on the first pass**, and for the same reason: the K1 control had
`main == agent/x`'s tip, because a fast-forward leaves them equal. Two different checks are
indistinguishable when the two shas they compare are the same sha. `…RefusesWhenMainDidNotMoveForwardFrom…`
now resets main sideways onto `agent/x` itself — main CONTAINS the branch, so the containment half says
yes, and only the descent check refuses — and `…StillReconciles_WhenMainMovedFurtherAfterTheMerge` lands a
commit on main after the merge, so `main` contains the branch and the branch does not contain `main`. That
is §22.5's M5 lesson arriving from a different direction: a control where the operands coincide cannot
tell an asymmetric check from its own reverse.

**K1-M5 was 0 red at first** because the journal fallback has four independent filters and the test only
exercised the branch-name one. `…JournalFallback_RefusesWhenTheEntryDoesNotDescribeTodaysMain` walks the
real shape — the merge landed, a later commit moved main further, the branch ref was deleted — where
confirming would record this lease's merge with a post-merge sha it did not produce.

**K4-M1's first two attempts were 0 red, and both were bad mutations rather than good news.** Removing only
the *derivation* left the new presence guard standing, so the mutated code refused for a different reason
and the tier stayed green — a mutation that does not remove the guard is not a measurement of it. Restoring
the ORIGINAL method whole (empty-check, presence check and derivation all replaced by local-then-tracking)
is the honest mutation, and it is 2 red. The rule this reinforces: a mutation has to restore the *defect*,
not delete a line near it.

**K6-M4 is the wiring mutation**, and it is the one §19.6's M2/M13/M14 exist as precedent for. `RunGuarded`
honouring a re-check proves nothing if no caller hands it one; the unit tests around the guard stayed green
under it. `KeepAlive_AgentStartsItsOwnRebase_AfterTheGuardLooked_MutatesNothing` runs the real cycle on a
real worktree and opens the window exactly where production opens it — the `Rebasing` state callback, which
fires after the guard's read and before the first mutation — then asserts that neither the wip commit nor
the reparent happened.

**K5-M2 is deliberately uncovered.** The "the approved plan could not be identified" arm is unreachable
while `MayWork` and `LatestForWorker` read the same store: `MayWork` returning true implies a plan exists.
It is kept for the §19.6-M5 reason — it does not depend on the two accessors staying in agreement — and
manufacturing a test that reached it would have meant weakening one of them to exercise the other.

### 23.9 Tier results

- `dotnet build Mainguard.slnx -c Release` — clean, 0 errors, no new warnings (12 pre-existing).
- `dotnet test Mainguard.Tests -c Release` — **3807 passed, 25 skipped, 0 failed**.
- `dotnet test Mainguard.Server.Tests -c Release --filter "Category!=RequiresDocker"` — **780 passed,
  22 skipped, 0 failed**.
- `dotnet test Mainguard.Server.Tests -c Release --filter "Category=RequiresDocker"` — the FULL tier,
  **130 passed / 0 failed / 2 skipped of 132**. A clean run: both §12.5/§22.6 intermittents
  (`SandboxNetworkIsolationDockerTests.ReachabilityProbe_ClassifiesOnTheHandshake_NotOnGettingAPrettyReply`
  and `QueueEntryResumeDockerTests`) stayed green. An earlier run in this lane measured 129/1 with the
  network probe red, which is its documented behaviour; against the **128 / 2** the same machine measures
  on the unmodified base, nothing here regressed and the two flakes remain the only thing between this
  tier and a clean run. Every `MergeQueueEndToEndDockerTests` case — the merge spine's own E2E, §22's
  cascade test included — is green.
- `dotnet format --verify-no-changes` — exit 0.

Two test fixtures were changed rather than the product, and both are the fix landing correctly rather
than a test being relaxed:

- `MergeConfirmGateTests` and `MergeAuditRpcTests` used readable placeholder shas (`"main-sha-0001"`)
  as the post-merge main the caller reports. That is exactly the class of value `ConfirmMerge` now
  refuses, and the fixtures use real object ids.
- `ExternalPrMergePathTests` did not record which head its verification was measured on. The live
  provisioner does (it resolves `agent/<id>` from the mirror after the pre-verification publish), so the
  fixture now does too — and the entry that deliberately records none is its own test.

### 23.10 Left alone, deliberately

- **`TryBegin` is still a uniqueness check.** §23.4 argues it; the short form is that mutual exclusion is
  the right primitive for a human-driven irreversible act, and a CAS retry loop would convert an honest
  "another merge is already in progress" into a silent later merge against a world nobody looked at.
- **`ConfirmMerge`'s ordering is unchanged**, for §22.7's reasons. The new checks all sit before the
  transition and read only the daemon's own records, so none of them needs the mirror refreshed first.
- **The daemon does not resolve the reported sha in a repository.** It cannot: the merge runs on the
  user's checkout. The shape screen is a shape screen and says so; pretending otherwise would be the same
  fabrication as the claim it screens.
- **K5's tie between an approved plan and the code it was approved for.** §23.7 — a concurrent change owns
  re-scoping, and the recommended design is recorded there.
- **`RebaseCycleKind.Skipped` is reused for the K6 refusal** rather than gaining a kind of its own. It is
  the same outcome the start-of-cycle guard skip produces, for the same reason, and the reason string
  carries the difference. A new kind would make every caller switch on a distinction none of them acts on.
## 24. A contract §3.1 change — a worker may ask to widen an approved scope (`rescope`)

**Decided by the owner, 2026-08-30.** Recorded here and in
[`coordinator-contract.md`](coordinator-contract.md) §3.1 because it changes the **worker's** exhaustive
op list, which §3 says is "a deliberate contract change, reviewed as such — not an implementation
detail". §13 did the same for the coordinator's `--title`.

### 24.1 What was found, measured before anything was changed

Live testing found two things, and they are halves of one shape.

**(a) An approved plan's `scope` does not bind a steered worker's commits.** Approved scope was
`["test.js"]`; the worker was steered, committed `bf966d1` (added `multiply` to `src/calc.js`) and
`7828d8c` (`subtract -> a - b + 1`), and `mainguard-plan commit` accepted both with zero scope or
flagged lines in the daemon log at commit time.

**(b) A worker that tried to widen its scope legitimately was refused**, with
`Plan '2b3552fd…' is already approved for this worker.`

Both were reproduced on this branch before a line was written — (b) at the daemon over the real socket,
through the production handlers:

```
commit ok=True committed=True error=''
present-again ok=False error='Plan '70664802…' is already approved for this worker.'
revise ok=False       error='Plan '70664802…' is Approved — only a rejected plan can be revised.'
served worker ops: await_decision, brief, commit_work, present_plan, revise_plan
```

**(a) is not a defect in the commit path, and is deliberately not "fixed" here.** Scope is not a
commit-time control and was never designed as one: phase 2 §3a puts the comparison at *verification*,
where `FlaggedChangeGate` classifies the merge diff against the approved plan and blocks the merge until
a human acknowledges every out-of-scope file. That mechanism was confirmed working live. What (a)
actually establishes is *why* (b) matters: because scope binds at the merge and not at the keystroke,
the only thing a worker can do about a scope that turned out to be too narrow is **ask** — and there was
no op for asking. The two refusals in the transcript above are each correct about their own op, and
together they left a worker trying to stay legal with two moves, both bad: exceed its scope silently, or
stop.

### 24.2 The op, and why a model will not confuse it with `revise`

```
mainguard-plan rescope <approved-plan-id> <plan.json>
```

`WorkerPlanShim.RescopeUsage` is the single source of that string — interpolated into the shim's usage
text, the shim's own refusal, the worker's operating instructions, and both daemon refusals that point
at it. Five renderings of one command is how they come to disagree (§13.2).

`rescope` and `revise` are near-homographs, which is a real risk and is answered structurally rather
than by hoping: **they are refused in complementary states.** `revise` requires a `Rejected` plan;
`rescope` requires an `Approved` one. So a mis-picked verb is *always* refused — never plausibly
accepted — and each refusal names the other by name and prints its form. This is §13.2's argument in a
different place: the property that makes an argument shape safe for a language model is that the slip is
**detectable**, not that it is unlikely.

Rejected alternatives:

| spelling | why not |
|---|---|
| reuse `revise` | it answers a rejection and spends the revision budget; overloading it makes "the human sent this back" and "the job grew" the same event, and the budget arithmetic on the card stops meaning anything |
| `widen` | a re-scope is not necessarily a widening — a plan may drop a path — and a verb that names one direction is the one place a removal would hide |
| `amend` | already means "modify the last commit" to every reader in this repo, and the worker's other op is `commit` |
| no id (`rescope <plan.json>`) | the daemon would have to guess which approval was being widened. §13.3's call: a guessed target produces a plausible card for an authorisation nobody named. The shim refuses locally and the daemon refuses too — the second is the enforcement, the first costs no turn |

### 24.3 The budget: a re-scope spends **no** revision, and the argument both ways

**For charging it:** one counter, one number on the card, and a hard ceiling of `1 + MaxPlanRevisions`
plan cards per worker for a human to read. It is the simplest thing that is obviously bounded.

**Against, and this is what was decided:** the budget bounds *"your plans keep being wrong"*. A re-scope
is *"the job is bigger than it looked"* — a different failure with a different remedy, and one that says
nothing about the quality of the plan the human already approved. Charging it is wrong in the direction
that matters: a worker rejected three times and *then* approved arrives at its approval with nothing
left, so **the workers that had the hardest time agreeing a plan would be exactly the ones with no legal
way to widen it** — the defect re-created, for the population most likely to hit it.
`AWorkerThatSpentEveryRevisionBeforeApproval_MayStillRescope` sets up at that boundary deliberately.

A re-scope is a new plan record, so it carries its **own** fresh `MaxPlanRevisions` for its own
reject→revise loop. That is a hole if the path can be re-entered — reject ×4 escalates, and a worker
allowed to ask again would get another three rounds, forever, without a human ever saying yes. Two
things close it, and neither is a new number:

- **Escalation is terminal for this path.** A worker whose re-scope escalated is refused another. It
  keeps the approval it already had and finishes what that covers.
- **One live re-scope at a time**, the same invariant, for the same reason, as one live plan.

**The number of re-scopes is deliberately not capped.** Every one costs a human approval, which is the
actual scarce resource and the actual gate; a numeric cap puts a worker back at "no legal path" at a
boundary nobody could justify. What runaway widening needs is to be *visible to the person paying for
it*, so `RescopeCount` travels to the card ("this worker's scope has already been widened 2 time(s)")
rather than silently refusing at 3.

### 24.4 The store: supersede, not stack — and the F6 trap on the way there

A re-scope is a **new** `PendingPlan` with `SupersedesPlanId` set. The plan it widens stays `Approved`
until the new one is approved, at which moment the old one moves to the new terminal
`PlanStatus.Superseded`, **in the same lock**. So a worker has exactly one approved plan or none — never
two.

That invariant is not tidiness. Phase 2 §3a is explicit that the approved plan's scope is what
`resolveApprovedPlan` hands the flagged-change gate, so two approved plans would be a gate measuring a
diff against whichever one a lookup happened to return.

**The trap, which this change walked straight into.** The composition root resolved that plan as
`LatestForWorker(agentId) is { Status: Approved }`. That is correct only while a worker's newest plan is
always its authorisation — and a **pending re-scope is newer than the plan it widens**, so the read
answers `null`. `null` means *unmanaged* to `FlaggedChangeDetector`, which then skips the out-of-scope
comparison entirely. A worker would have lost its F6 coverage **by the act of asking to widen legally**,
silently, for as long as the human took to decide.

The fix is a single authority, `PlanApprovalService.ApprovedForWorker` (single-or-nothing, following
`WorkerPlanGate.ResolveKeyLocked`'s fail-closed precedent), with the composition root reduced to a call
to it — so there is one copy of the rule to be wrong.
`TheApprovedPlanF6MeasuresAgainst_IsTheAuthorisation_NotTheNewestPlan` asserts **both** halves: that the
old shape really would have answered null, and that the wired one answers the approved plan.

### 24.5 Work already done outside the scope: allowed, not re-policed

A worker may ask to re-scope *after* it has already touched the extra file. That is **not refused, and
this path does not look at the worktree.**

- The flagged-change gate already catches out-of-scope files at verification and forces a human
  acknowledgement before the merge (phase 2 §3a / F6, confirmed working live). The file reaches a human
  either way; a second check here would be two controls answering one question, which is how one of them
  becomes decorative (MG-12).
- Refusing a late re-scope would **re-open the dead end** in its worst form: a worker that had already
  slipped could never get legal again, which is precisely the state this op exists to remove.

So the two outcomes both end at a human, and `ALateRescope_IsNotRefused_AndTheOneMechanismStillDecides…`
asserts both: approved, and the file is inside the authorisation F6 measures against; declined, and it is
flagged exactly as it was. What the op adds is that the human hears the worker's **reason** before they
see the diff, instead of only after.

### 24.6 While a re-scope is pending, the worker is **not** blocked

It holds an approved plan and is mid-work. Asking for more does not withdraw what it already has, so
`HasApprovedPlan` — and therefore `MayWork`, steering, verification and `commit_work` — keeps answering
off the plan being widened, for exactly the scope that was approved.

The alternative (treat a pending re-scope as "no approved plan") is wrong twice:

- it makes the legal move **more expensive than the silent one**. The worker that says nothing keeps
  working; the worker that asks stops. That trains exactly the behaviour half (a) is made of.
- it refuses `commit_work` to a running worker, which is how F1's "stopping a worker must NOT destroy
  the commits it made" gets undone by a human taking an hour to read a card.

What *does* block is the worker's own `rescope` call, exactly as `present` blocks — it asked a question
and is waiting for the answer, and `await <id>` re-attaches after a crash or a restart like any other
plan. And a pending re-scope **counts** toward the backpressure sentence, because it is a card in front
of a human. `PendingPlan.BlocksWorker` and `HasApprovedPlan` were always different questions; the
re-scope is the first case where they have different answers at the same instant, and both records say
so.

### 24.7 The card, which is a different decision from the one it resembles

The human is approving a **widening of something they already approved**, so the card says which and
shows what changed: `SupersedesPlanId`, `PreviousScope` (**copied at presentation**, never looked up — a
lookup renders a different claim as soon as a second re-scope exists) and `RescopeCount` travel on
`PlanEntry`, and `WorkerPlanCard` derives **Adds** and **Drops** from them. Drops is its own row in
`DangerBrush`: a re-scope that removes a path the human already agreed to is the one direction this op
can take something away in, and a card rendering only additions is exactly where that would hide.

Three strings change with the card kind, and each of them would otherwise be **false**:

- Reject reads *"Decline the widening"*, not *"Reject — worker will stop"*. Declining stops nothing.
- The last-round warning says the widening closes for good and the worker keeps its existing approval.
- An escalated re-scope's card no longer says the worker "stopped after N rejected plans" — it says the
  widening closed and the worker is still working under its original approval. A human who reads the
  generic sentence ends a worker that is doing approved work.

No new design tokens (phase 2 §2.9's rule still holds): `WarningBrush`, `DangerBrush`, `TextMuted`,
`TextPrimary` already say all of it.

The card is rendered headless in all four themes by
`CoordinatorPlanGateRenderHarness.Rescope_ShowsWhatChanges_AndThatDecliningDoesNotStopTheWorker_AllThemes`,
whose fake both **adds** and **drops** a path — a fixture that only ever widened would never exercise the
Drops row, which is the one this section says must not be able to hide.

### 24.8 Keeping the instructions honest — and the pin that did not exist

`AgentOperatingInstructions.Worker` teaches the op. The **coordinator** text has been pinned against
`CoordinatorOps` in both directions since §13.5; the worker text was pinned against nothing, so an op
could be added to `WorkerOps`, served by the daemon, spelled by the shim, and never mentioned to the only
reader who can run it — which is how the loop once ended one rung short of `commit_work`, whose
instructions had never mentioned committing.

`TheWorkerIsToldAboutEveryOpTheDaemonServesIt` closes that, through `WorkerPlanShim.Verbs` — a worker
meets each op twice, as the wire op (`rescope_plan`) and as the verb it types (`rescope`), and the
instructions can only teach the second. The map is set-equalled against `WorkerOps` in the same test, so
it cannot itself become the leak.

**§13's G3 lesson applies to every sentence written here.** The instructions once told a coordinator
`--task` "needs no quotes at all" — true of the parser, false of the world, and two of three spawns in a
stress run died on it. So each claim is one the shipped code actually makes: the command is
`RescopeUsage` verbatim, "your existing approval stands" is `WorkerRescopeTests`, and the sentence about
a widening that stops being available is the terminal-escalation rule with its own test.

**That last sentence was itself wrong on its first draft**, in exactly the arithmetic phase 2 §2.1 exists
to pin: it read *"a widening the human refuses three times stops being available"*, and with
`MaxPlanRevisions = 3` it is the **fourth** rejection that escalates. It now says the budget is finite and
that the daemon reports what is left — which is true, is the same thing the paragraph above it already
tells the worker about ordinary revisions, and does not hardcode a number the limit is allowed to change.

**One sentence written in this change was G3 on its first draft.** The id-less `rescope` refusal ends
*"(`mainguard-plan brief` prints the id of your live plan.)"* — and it did not. The daemon put `planId`
and `status` on the brief response; the shim printed only the brief text. A worker following that advice
got a headline and no id, and no way to run the command it had just been told to run. The shim now prints
`PLAN: <id> (<status>)` beneath the brief, asserted **twice** — host-side through the real `main()` (M15),
and in a real jail beside the refusal that sends the worker there, because the whole reason that file
exists is that a claim about the shim has to be checked where the shim runs.

### 24.9 The mutation log — every guard watched failing

Run by `build/mutate.sh` (driver: `build/mutations/run-all.sh`). Two properties of that harness are
load-bearing, and the first cost a real false green here:

1. **Every restore is followed by `touch`.** Restoring a mutated file with `mv`/`cp` preserves its
   mtime, so MSBuild skips the rebuild and the "restored" run executes the **mutated** assembly.
2. **The run refuses to report unless the assembly under test actually rebuilt** — and it must be the
   assembly that *contains* the mutated code, as the test host loads it (the owning project's dll inside
   the test project's output dir). The harness caught its own first version doing this wrong: it watched
   `Mainguard.Tests.dll`, which legitimately stayed up to date while `Mainguard.Agents.dll` rebuilt, and
   it exited 99 rather than reporting. Watching the wrong file is the same failure as not watching one.

| # | mutation | went red |
|---|---|---|
| M1 | `Rescope` stops requiring an `Approved` plan | `OnlyAnApprovedPlanCanBeRescoped` — all 4 rows |
| M2 | an escalated re-scope stops being terminal | `AnEscalatedRescope_IsTerminal_AndTheWorkerKeepsWorkingUnderItsOldApproval` |
| M3 | unbounded live re-scopes | `AWorkerMayHaveOnlyOneLiveRescope` |
| M4 | approving a re-scope stops superseding | `ApprovingARescope_SupersedesTheOldPlan_AndBecomesTheOneAuthorisation` |
| M5 | `ApprovedPlanFor` goes back to newest-filtered — **the F6 hole** | `TheApprovedPlanF6MeasuresAgainst_IsTheAuthorisation_NotTheNewestPlan`, `WhileARescopeIsPending_…` |
| M6 | `PreviousScope` is not copied | `ARescopeCarriesWhatChanged_AndTheCopyIsNotALookup` |
| M7 | the "already approved" refusal loses the hint — **the defect itself** | `TheDeadEnd_ThatThisOpExistsToRemove` |
| M8 | `revise`'s refusal stops naming `rescope` | `TheDeadEnd_ThatThisOpExistsToRemove` |
| M9 | the shim stops refusing an id-less `rescope` | `TheShimsRescope_NamesThePlanItWidens_AndRefusesLocallyWhenItCannot` — the two refusal rows |
| M10 | the worker's instructions drop the re-scope section | `TheWorkerIsToldWhatARescopeIs_AndThatAskingCostsItNothing` — and **only** that one, correctly: the command list above the section still names the verb, so the two exhaustiveness tests still pass. What M10 deletes is the MEANING, and the meaning test is the guard for it |
| M11 | `WorkerPlanShim.Verbs` loses the entry | `TheWorkerIsToldAboutEveryOpTheDaemonServesIt` (the set-equality half) |
| M12 | the decision stops carrying `rescopeOf` | `ARescopeBlocksOnTheHuman_…`, `ADeclinedRescope_LeavesTheWorkerAuthorised_…` |
| M13 | the handler skips the plan-ownership check | `AWorkerCannotRescopeAnotherWorkersPlan` |
| M14 | the handler infers the plan id instead of refusing | `ARescopeThatNamesNoPlan_IsRefused_AndQueuesNothing` |
| M15 | `brief` stops printing the live plan id — **the refusal's advice stops working** | `TheShimsBrief_PrintsTheLivePlanId_BecauseTheRescopeRefusalSendsTheWorkerThere` |
| M16 | the card's Reject button stops distinguishing a re-scope | `Rescope_ShowsWhatChanges_AndThatDecliningDoesNotStopTheWorker_AllThemes` |

**M13 exposed a weak test, and the test was fixed rather than the mutation excused** — the same shape as
§13.6's M9. Both M13 and M14 remove a check *in front of* a call that **blocks on a human**, so the
mutated daemon does not refuse the request: it accepts it and parks. Under the first version of those two
tests the run therefore **hung** instead of failing, and a hung run reports nothing at all. A guard whose
absence produces a hang is a guard nothing is watching. Both assertions are now bounded
(`WaitAsync(15s)`), the bound is part of what they assert, and re-run against M13/M14 they go red in 15
seconds as they should have the first time.

### 24.10 Tier results

All four tiers on the shipped tree, run serially on one machine:

| tier | result |
|---|---|
| `dotnet build Mainguard.slnx -c Release` | 0 errors |
| `dotnet format --verify-no-changes` | exit 0 |
| `dotnet test Mainguard.Tests -c Release` | **3815 passed**, 0 failed, 25 skipped |
| `Mainguard.Server.Tests` · `Category!=RequiresDocker` | **778 passed**, 0 failed, 22 skipped |
| `Mainguard.Server.Tests` · `Category=RequiresDocker` | **131 passed**, 0 failed, 2 skipped |

The Docker tier was **clean**, which is worth stating because it usually is not: neither §12.5's
`QueueEntryResumeDockerTests` ordering flake nor
`SandboxNetworkIsolationDockerTests.ReachabilityProbe_…` fired. An earlier run of the same tier on this
branch did hit the first one — `ACleanStopAfterACommit_KeepsTheBranch_AndTheEntryIsStillResumable`, with
`OCI runtime exec failed: chdir to cwd ("/workspace") … no such file or directory`, i.e. the jail was gone
by the time the exec landed. Recorded, not chased: it is the documented flake, its failure is about
container lifecycle rather than anything this change touches, and it did not reproduce.

### 24.11 Left alone, deliberately

- **Half (a) is not turned into a commit-time scope check.** The owner's brief says not to build a
  second mechanism that contradicts the flagged-change gate, and it would be one: the gate already
  refuses the *merge* and asks a human, which is where the decision belongs. A commit-time refusal would
  also destroy work — a worker unable to record a diff loses it with its jail (F1).
- **`MaxPlanRevisions` is not retuned.** The re-scope path leans on it (a re-scope's own reject→revise
  loop, and the terminal escalation) and 3 is still the right number for both readings.
- **The coordinator learns nothing about re-scopes.** Its four tools are unchanged, it does not author
  plans, and `get_worker_status` already reports a worker held at the gate. A fifth thing to tell it
  would be a §3 change with no capability behind it.
- **A superseded plan keeps its own card history.** It stops being a gate item (it is not `Pending` or
  `Escalated`) and stays in the store as the record of what was authorised and by whom, which is what
  the audit chain's `plan_superseded` event points at.
## 25. The plan-mode toggle — planning becomes the operator's choice, and OFF is a mode

**Owner decision, 2026-08-30:** *"the worker should be required to create a plan, this is something that
should be changed. just like in a normal cli users can choose to use plan mode, same thing should apply
here. if the plan mode toggle in mainguard is on, then by default all worker agents spawn in plan mode
and then the presented plan is the one they create. otherwise they can just go straight for
implementing."*

Phase 2 made the plan gate **mandatory**. This makes it the operator's choice, with ON as the default and
as the shipped behaviour.

### 25.1 What prompted it, and the trap in the obvious fix

Live testing found the resulting plan can be hollow. The brief carries only `--title` (the §13 fix,
deliberately, so the task stays withheld), so a worker can invent a scope from the title alone without
reading the repository — one did, producing a 5-file scope derived purely from its title. A human then
approves *that* scope, and F6 later measures the real diff against it.

The reading of the owner's decision that was investigated first was: with the toggle ON, spawn each worker
in **the vendor CLI's own native plan mode**, so the presented plan is grounded in the repository by
construction. That reading was **measured and rejected for this change**; §25.6 is the measurement and
§25.7 is the recommended design for landing it separately. The change recorded here is the toggle itself
and — the part that is genuinely new — what OFF means.

### 25.2 Where the toggle lives: daemon-side, because the gate is

`PlanModeSwitch` (`Mainguard.Agents/Agents/Orchestrator/PlanModeSwitch.cs`), registered in
`DaemonHost` and persisted by `JsonPlanModeStore` beside the plan store.

**Not `App.Settings`/`UserPreferences`.** That store is a client-side `config.json`
(`Mainguard.Git/Services/SettingsService.cs`) and **the daemon never reads it**. A gate enforced where the
daemon serves the call, switched by a value the enforcement point cannot see, is the exact shape of
control this codebase keeps finding to be decorative (MG-12). The setting lives where the actor is — the
established pattern, and the one PR intake was built on after a settings screen that looked saved and
changed nothing.

The wire is `PlanApprovalService.GetPlanMode`/`SetPlanMode`, plus `plan_mode_enabled`/`plan_mode_summary`
on the existing `PlanUpdate` stream so the state arrives **with the cards it explains** rather than on a
second poll that can disagree with them.

- **`SetPlanMode` is denied to the coordinator role** (`RoleInterceptor`), on contract §4's plan-approval
  boundary and for a strictly stronger reason: a coordinator that could approve one plan holds the gate
  for one worker; a coordinator that could turn plan mode off removes it for every worker it spawns from
  then on, with no card ever reaching a human.
- **Fail-closed.** A missing or unparseable settings file is plan mode **ON**. The default of a
  human-approval gate is that it is there, and an unreadable file must not be a way to remove one.
- **No EF migration.** The plan subsystem's own record is already a JSON file in the same directory, for
  the same reason: the daemon must be able to answer "is the gate on?" before anything needing a database
  is up.

**The UI affordance is on `PlanGateView`** — the surface the decisions arrive on, and therefore where a
*missing* decision is noticed. It is a checkbox bound `OneWay` plus a `Command`, deliberately: a two-way
binding would render a setting the daemon rejected or never received, and this is the one disagreement
where a human believes they still have an approval step and do not. The click moves the box, the command
tells the daemon, and the refresh puts the daemon's answer back.

The gate region's visibility flag (`HasGateContent`) now includes `|| !PlanModeEnabled`. With approvals
off nothing is ever pending, so a gate that only appeared for pending cards would go dark permanently —
and a dark gate is exactly what an *idle* orchestration looks like. **An off gate stays on screen saying
so**, in `WarningBrush`.

### 25.3 What OFF means — a recorded mode, not a set of disabled checks

This is the part that needed the most care, and the tempting implementation is the wrong one.

**Rejected: "with the toggle off, don't call `Hold`."** The daemon already has an ungated-managed-worker
path (an operator's own spawn, spelled as "neither title nor task"), and `AgentSpawnService` already calls
producing one *"strictly worse than the defect being fixed"* — correctly. An unheld worker:

- is **ineligible** for automatic verification (`MayAutoVerify` treats an id the gate never held as not a
  plan-gated worker at all, deliberately, so the daemon does not start test runs on manual agents);
- carries the **manual-agent wording** on its merge record, so a later reader cannot tell a delegated
  worker the operator authorised in advance from a human driving a jail by hand;
- has **no recorded authorisation** anywhere.

**Taken: the gate still holds every delegated worker; the toggle decides whether the task is withheld.**
`WorkerPlanMode{Gated,Ungated}` is read once from the switch at `Hold` and then owned by that worker. So
the *population* every predicate reasons over is identical in both modes, and exactly one answer changes:

| | plan mode ON | plan mode OFF |
|---|---|---|
| `Hold` | held, task withheld | **held**, task not withheld (`Mode = Ungated`, on the `worker_task_withheld` audit event as `plan_mode=off`) |
| `MayWork` (and `MayReceivePrompt` / `MayRequestVerification`, which delegate to it) | no until approved | **yes from the start** |
| `TryReleaseTask` | only against an approved plan | yields the task |
| `MayAutoVerify` | no until approved | **yes** — it is still a delegated worker, and an unheld agent is still ineligible |
| `Allows` (`IMergeGate`) | no until approved | yes |
| G1 merge-queue row | deferred until approval | **created at spawn** |
| `MergeEvidence` | `plan gate: plan approved` | **`plan gate: OFF at spawn — delegated worker, no plan was authored or approved`** |
| `BlockedWorkerCount` / backpressure | counts | silent — nothing is waiting on a human |
| `spawn` response status | `AwaitingPlan` | `Working` |
| `present_plan` / `revise_plan` / `await_decision` | the gate | **refused**, with the reason |

Three of those rows are the design, not bookkeeping:

**`MergeEvidence` gained a third outcome.** Collapsing OFF into "plan approved" would put a sentence on a
merge record asserting a human decision that never happened — the single worst thing that record could
say. Collapsing it into "not a plan-gated worker" would borrow the manual-agent wording. Off is a
different authorisation (the operator authorised this *class* of work once, in advance, and it is on the
audit chain as `plan_mode_changed` with the actor), so it says so.

**`present_plan` is refused rather than humoured.** A worker following stale instructions would otherwise
queue a card in front of an operator who switched approvals off and is not watching for one, then block on
`await` forever — holding a jail and a cap slot, having already been given its task.

**The toggle is not retroactive, in either direction.** A worker already blocked at the gate must not be
authorised by a switch nobody pointed at it (an approval nobody gave); a worker already told to start must
not be stranded mid-task by a preference change. Hence the mode is a property of the worker.

### 25.4 The task needs a door when there is no `present` — contract §3.1 gains `task`

`present` was the only path that ever returned the withheld task. With plan mode off it is neither run nor
accepted, so a worker would have no way to learn its task at all. The two alternatives were worse:

- **Redefine `brief` to yield the task when the gate is off.** The brief's one documented property is
  "never the task itself" — asserted in the contract, in `AgentIpcProtocol`, in the worker's instructions
  and in the shim's own help. Making it conditionally false is how a documented invariant becomes a
  sentence nobody can trust, and §13 is that exact defect.
- **Put the task in the launch argv.** `AgentKickoffPrompt` is a pure function of `(role, shimPath)`
  *precisely so that it cannot carry the work even by mistake*. Adding the task as a parameter trades a
  structural guarantee for a conditional one, in the one place the task would then sit in a process
  argument list.

So `task` is a **second door onto the gate's one exit**, not a second copy of the decision: it calls
`TryReleaseTask`, with its release-once audit record, and is authorised by the same `MayWork` predicate as
`commit_work`. With plan mode ON it is refused before approval exactly like everything else.

While wiring it, `TryReleaseTask` stopped reading `HasApprovedPlan` directly and now asks `MayWork`. The
two were independent spellings of one policy, and this change is precisely the one that would have split
them.

### 25.5 The jail text is mode-dependent, because instructions that assert a gate the daemon is not applying are worse than none

A worker told "present your plan, then wait for the human" with approvals off would block forever on a
card nobody is reviewing, having already been handed its task. `AgentOperatingInstructions.Worker`,
`.Coordinator` and `AgentKickoffPrompt.For` all take the mode; **every default is `Gated`**, so a caller
that has not been taught about the toggle renders the text that describes a gate, never the text that
promises there is none. The commit half — "this is the only way your work leaves this jail" — is shared
verbatim between the two worker texts, because it has nothing to do with plans and a second wording of it
would drift.

The coordinator's text loses four paragraphs it would otherwise state falsely: the withheld task, the
`prompt`/`verify` refusal at the gate, the cap-refusal advice about plans waiting on a human, and the
brief/task framing. It keeps *"you do not write task plans"* in both modes — that one is true for a
different reason (the coordinator cannot see the code).

### 25.6 The vendor CLI's native plan mode — what was measured, and why it is not in this change

Measured against **claude-code 2.1.251** on this machine (and cross-checked against the strings of the
installed adapter binary, 2.1.234 linux-arm64, which the managed updater had moved past the bundled
2.1.218 pin). One real session, `--permission-mode plan -p --output-format stream-json`, $0.17.

| # | finding |
|---|---|
| 1 | **`--permission-mode plan` is real.** Choices: `acceptEdits, auto, bypassPermissions, manual, dontAsk, plan`. Session init reports `"permissionMode":"plan"`. Present in the installed adapter binary too. |
| 2 | **Plan mode is read-only, and it enforces that against exactly the call the shim path needs.** Asked to run a trivial mutating Bash command as part of exploring, the CLI refused: *"plan mode restricts me to read-only actions (plus editing the plan file itself), and creating a file isn't read-only."* Nothing was created. Its system prompt says so in stronger terms — *"you MUST NOT … run any non-readonly tools … This supercedes any other instructions you have received."* |
| 3 | **So the shim path and native plan mode are structurally incompatible.** `mainguard-plan present /tmp/plan.json` requires writing a file outside the plan file and running a non-read-only command — both of which plan mode's own instructions forbid, and the refusal is the model's, so an `--allowedTools` grant does not reliably override it. |
| 4 | **The plan is delivered as free-text markdown, via `ExitPlanMode({"plan": "<markdown>"})`.** It is not structured and does not map to `TaskPlanFields{scope[], approach, testStrategy}`. Inventing that structure from it is the §18 fabricated-counts failure class, so it must not be done. |
| 5 | **The plan is also written incrementally to a plan file at `$HOME/.claude/plans/plan-<slug>-<random>.md`** — a non-deterministic name, in the jail's tmpfs home, with no completion signal in the file. The daemon cannot know the path in advance, and building on a vendor's private plan-file layout is the per-CLI coupling §17.2 refused. |
| 6 | **`ExitPlanMode` requires user approval** — an interactive dialog. In a jail whose terminal is input-locked and which nobody is watching, nothing answers it. |

**Conclusion.** Adding `--permission-mode plan` to the launch line *on its own would deadlock every
worker*: it cannot run the shim, and its `ExitPlanMode` dialog has no one to answer it. Capturing a
CLI-authored plan needs a **`PreToolUse` hook on `ExitPlanMode`** — a new capture channel (hook config
injected into the jail's `.claude/settings.json`, a hook subcommand on the shim, a decision returned as
`permissionDecision`) plus an honest free-text plan representation carried through
`PlanApprovalService`, the proto, and the approval card. That is a change to the plan's data model, which
F6/`FlaggedChangeGate` compares diffs against — a merge-spine gate. Landing it inside a PR named for a
toggle would be the same misjudgement phase 2 §3a declined to make. §25.7 is the design; it is not built.

### 25.7 Recommended follow-up: capture the CLI's plan at `ExitPlanMode`, additively

The shape that avoids changing the plan's data model, and therefore avoids touching F6:

1. Launch a gated worker with `--permission-mode plan` **and** a `PreToolUse` hook matching
   `ExitPlanMode`, both declared **per adapter** (this is genuine vendor knowledge, like `systemPromptArg`
   — an adapter that declares neither keeps today's shim-authored path, which is the answer to "what does
   an adapter with no plan mode do when the toggle is ON": it behaves exactly as it does now, and nothing
   is refused).
2. The hook posts the markdown `tool_input.plan` to the daemon over the existing IPC transport and
   returns `permissionDecision: "allow"`, so plan mode ends and the CLI is free to run the shim.
3. The daemon stores that markdown against the worker as **narrative evidence** — a new free-text field on
   the plan record and the card, never parsed into `scope[]`.
4. The worker then presents the structured plan through the existing shim path, now authored by a model
   that has just read the repository, and the human sees both.

`TaskPlanFields` is unchanged, F6 keeps comparing against a `scope[]` a model wrote, and the CLI's own
plan is shown verbatim beside it rather than being fabricated into fields.

A smaller, independent guard worth landing either way: **refuse a presented plan whose scope names no path
that exists in the worker's worktree.** A plan invented from a title alone names paths that are not there,
and that is a daemon-side, non-fabricated check on exactly the defect §25.1 opens with.

### 25.8 Per-adapter — argued, and deliberately empty for now

Plan mode *is* vendor knowledge, and §17.2's rule is the test: a manifest field is right when only the
CLI's author knows how their binary spells the thing, and wrong when it invites an adapter author to write
a value that silently reintroduces a defect. Both halves apply here, so the answer depends on which
mechanism ships:

- **In this change there is no manifest field, because there is nothing that would read it.** The gate is
  vendor-neutral — the shim path works identically on `claude-code`, `codex`, `gemini-cli`, `qwen-code`,
  `opencode` and the scripted test adapters — so every adapter behaves the same under both toggle
  positions. Adding `planModeArg` now would be a declaration nothing consults, which is the species of
  thing this repo keeps finding to be decorative.
- **When §25.7 lands it needs two fields** (`planModeArg`, and the hook declaration), and the answer to
  "what should an adapter with no plan mode do when the toggle is ON?" is: **fall back to the
  shim-authored plan, and do not refuse the spawn.** Refusing would make an operator preference into a
  per-CLI capability gate — a worker that cannot be spawned at all because its vendor lacks a feature is a
  much larger behaviour change than the toggle, and the shim path is a working, tested plan gate for
  exactly those adapters. Half-declaring the pair must be refused at parse time, like
  `preApprovedCommandArg`/`preApprovedCommandFormat`: a CLI put into plan mode with no capture hook is the
  deadlock measured in §25.6 row 6.

### 25.9 The mutation log

Every guard watched failing. Restores use `touch`, and the runner **refuses to report a result unless the
witness dll inside the test project's own output directory has a newer mtime than before the build** — a
run that reports 0 red against a stale assembly is worse than no run. It also holds a lock file: two
concurrent runs restore each other's originals over each other's mutations, which was observed once and
left the tree carrying a mutation nobody was testing.

`PlanModeToggleTests` is 26 tests in `Mainguard.Tests`; `PlanModeToggleDaemonTests` is 10 in
`Mainguard.Server.Tests`. The counts below are over the filtered plan-gate slice of each tier (82 and 92
tests), so a mutation that breaks a neighbouring guarantee shows up as more than one.

| # | mutation | went red |
|---|---|---|
| M1 | plan-mode default flips to OFF (fail-open) | **5** unit · **17** daemon |
| M2 | `MayWork` loses its ungated arm | 4 · 3 |
| M3 | `TryReleaseTask` reads `HasApprovedPlan` again instead of the one authority | 3 · 1 |
| M4 | `MergeEvidence` collapses OFF into "plan approved" | 1 · 1 |
| M5 | `RefusePlanPresentation` never refuses | 1 · 1 |
| M6 | `Hold` ignores the mode it was given | 7 · 5 |
| M7 | `Hold`'s `mode` parameter defaults to `Ungated` (fail-open default) | **14** · 0 |
| M8 | the kickoff turn ignores the mode | 1 · 0 |
| M9 | the operating instructions ignore the mode (dropped inside `For`) | 1 · 0 |
| M10 | the worker text's mode switch always renders the gated half | 2 · 0 |
| M11 | the spawn path always holds the worker as gated | — · 5 |
| M12 | the spawn path never reads the switch | — · 5 |
| M13 | the shim spawn reports the literal `AwaitingPlan` again | — · 1 |
| M14 | the `task` op skips the gate and hands the task over unconditionally | — · 1 |
| M15 | `present_plan` drops the ungated refusal | — · 1 |
| M16 | `SetPlanMode` is not denied to a coordinator | — · 1 |
| M17 | the plan stream stops carrying the plan-mode state | — · 1 |
| M18 | `SetPlanMode` echoes the request instead of the daemon's state | — · 2 |

**Three of these are worth reading rather than counting.**

**M9 survived the first full run — 0 red in both tiers — and that is the one this pass actually bought.**
`AgentOperatingInstructions.For(role, adapters, mode)` is the entry point the launcher calls, and dropping
the argument inside it gives every jail the gated text whatever the operator set. Nothing went red:
`EveryModeDefaultIsTheGatedOne` compares the no-argument overload against `Gated`, which a `For` that
always renders gated satisfies perfectly, and every other text assertion called `Worker`/`Coordinator`
directly, so none of them crossed the one forwarding step that can drop the argument.
`ForRoutesTheModeToBothRolesTexts` was written for it and asserts both halves — that `For` forwards, and
that the argument changes what comes back — because equality alone would still hold if the methods it
forwards to ignored the mode too.

**M7's asymmetry (14 unit, 0 daemon) is honest and expected**, not a hole: the daemon always passes the
mode explicitly, so a fail-open *default* is unreachable from it. The default's whole risk is a future
caller that forgets the argument, and that is exactly what the unit tier measures.

**M5 first showed up as a HANG, not a failure**, and that is a defect in the test that has been fixed.
With the ungated refusal removed, `present_plan` is accepted, and an accepted presentation **parks on the
socket until a human decides** — which is the gate working, so the shim gives it no timeout. The test
therefore sat for fifteen minutes until the run was killed, and the mutation scored nothing. It now goes
through `CallWithinAsync`, which fails the assertion on a missed deadline. A guard whose failure mode is a
hang is indistinguishable from a guard nobody tested.

**Two process-level notes on the harness itself**, because both cost a run:

- A harness that survived a session interruption **raced the replacement run**, mutated a file behind its
  back, and made two mutations report "pattern not found" while polluting every result in between. A lock
  *file* does not catch that (a killed run leaves a stale one; an older copy of the script never took
  one), so the runner now asks the **process table** — `pgrep -f mutate.py` — before it starts.
- One mutation was committed and pushed as if it were the implementation, because a `git add -A` ran
  while the harness had a file mutated. Backed out in `d3ec9612`. The runner now verifies
  `git diff --quiet` on each restored file and refuses to continue if a restore did not take, and prints
  the tracked-file dirt at the end (`none (clean)` for the run above) — but the real lesson is not to
  stage anything while a mutation run is in flight.

### 25.10 Tier results — and the one tier that was deliberately NOT run

- `dotnet build Mainguard.slnx -c Release` — **succeeded**. The four warnings it prints
  (`MergeQueue.cs`, `MergeQueueProvisioner.cs`, `BranchHandoffService.cs`, and four `CS0067` in test
  harnesses) are pre-existing and in files this change does not touch.
- `dotnet test Mainguard.Tests -c Release` — **3816 passed, 0 failed**, 25 skipped.
- `dotnet test Mainguard.Server.Tests -c Release --filter "Category!=RequiresDocker"` — **783 passed,
  0 failed**, 22 skipped.
- `dotnet format Mainguard.slnx --verify-no-changes` — **exit 0**. It first reported four `xUnit2031`
  analyzer warnings against the new test file (`Assert.Single` over a `Where` clause); those are fixed,
  not suppressed.
- `dotnet test Mainguard.Server.Tests -c Release --filter "Category=RequiresDocker"` — **NOT RUN, on
  purpose.** The reasoning is below, because "it was not run" is only useful with the evidence attached.

**Why the Docker tier was not run here.** CONTRIBUTING's rule is that a machine with a live agent jail
uses `Category!=RequiresDocker`, because `DockerSuiteIsolation` sweeps on **both** construction and
dispose. Measured on this machine at the time of the run:

- **There is exactly one usable Docker engine.** `docker context ls` shows OrbStack as current;
  `/var/run/docker.sock` is a symlink to OrbStack's socket, and Docker Desktop's socket
  (`~/.docker/run/docker.sock`) **does not exist**. So the two-daemon separation CONTRIBUTING relies on
  ("tests run against Docker Desktop, real jails live in MainguardEnv's own engine") is simply not
  present under the macos-host substrate (ADR-008): the daemon under test and the owner's jails share one
  engine.
- **Two live `mainguard-*` jails were running on it** — one since 07:24 (the operator's Pro app, itself
  up since 07:12) and one started 14:38, i.e. during this session.
- **`DockerSuiteSweepGuard.RefusalFor` would not have stopped it.** Its evidence test is
  `MainguardOsHost.IsInside()` — whether the *test process* is running inside Mainguard OS. On
  macos-host the daemon runs natively on the Mac against the host's engine, so the process is outside,
  the guard returns `null`, and the sweep proceeds. **The guard's own refusal text describes exactly this
  machine's situation** ("the sweep would leave live jails running with no networks … with nothing in the
  symptom pointing back at a test run") while its predicate cannot detect it. Recorded here, not fixed:
  it belongs to whoever owns that fixture, and widening the predicate is a change to a destructive
  safety check that deserves its own review.
- The blast radius today happens to be narrow — at the moment of checking, no `mainguard-agents` /
  `mainguard-egress` / `mainguard-agent-*` networks existed, no egress proxy container existed, and both
  jails were attached to no network — but that is a property of one instant, and the Pro app is live and
  can attach a jail to those literal names at any point during a seven-minute suite.

**What this change would have been asking that tier to prove, and why little is at stake.** Nothing here
adds a sandbox, network, or jail surface. The plan-mode toggle touches the plan gate, the worker/coordinator
IPC surface, one gRPC service, the role interceptor and one view. The one place it reaches the real spawn
chain is `AgentSpawnService.SpawnAsync`, and for every caller that is not a coordinator-delegated spawn —
which is every `RequiresDocker` test — `delegated` is false, the mode resolves to `Gated`, and the launch
line is byte-identical to before. **It should still be run**, on Linux CI or on this machine with the Pro
app stopped, before the PR merges.

### 25.11 Left alone, deliberately

- **`CoordinatorTools` is not mode-aware.** Its `SpawnWorkerAsync` success string still says the worker
  "will inspect the repo, author its plan, and block for approval", which is false with the toggle off.
  It has **no production construction site** (all 14 are in `Mainguard.Tests`) — the wired equivalent is
  `AgentSpawnService`'s IPC handlers, which are mode-aware. Threading the mode through a surface nothing
  reaches would add a second copy of the policy for no behaviour.
- **`RefuseBrief` still requires a title in both modes.** With approvals off the title is no longer a
  brief, but it is still the headline on the merge-queue row and in `status`, and the "title must not be
  the task" tripwire costs nothing to keep. One code path, and the spawn form does not change under the
  operator.
- **The switch is not exposed in Settings → General.** It belongs beside the decisions it governs; a
  second entry point would be a second place for the state to be read from and disagree.
- **Nothing retroactively re-gates or un-gates a live worker**, by design (§25.3), so there is no
  migration and no reconciliation pass.

## 26. The approved plan's *approach* — a control that constrained only the file list

**Found in a live UI test, 2026-08-31.** A worker's plan was approved by a human. Its `approach` read:

> the module has no error-handling or validation idiom anywhere in it, so I will keep plain `a / b` and
> let the language semantics stand

The worker then shipped `divide()` throwing `RangeError` on zero, plus a `checkOperands`/`checkResult`
layer throwing `TypeError` on **every** helper — which silently changed the behaviour of the pre-existing
`add` / `subtract` / `square` (`add('2',3)` began throwing instead of returning `'23'`).

### 26.1 Why every control held and none of them was looking

Nothing was broken. Each mechanism did exactly what it says it does:

- **The plan's `scope` was honoured**, so the SA-1/F6 out-of-approved-scope arm compared the diff against
  the approved file list, found nothing outside it, and produced `FlaggedItems = []`.
- **`CanMerge` was true, the state was `Verified`, `node test.js` passed** — because the worker also
  authored the tests asserting its own new behaviour. **A worker that owns its tests can always turn its
  divergence green.** Verification proves a diff is self-consistent; it has never proved that the diff
  matches what was approved, and it cannot.
- **Nothing anywhere compared the approved approach against the produced diff.** The approach text was
  written by the worker, read once by the human at approval, and then never surfaced again — it lived on
  the daemon and reached no review surface.

This is the sibling of the MG-12 class this codebase keeps re-finding: *a control that looks like it
constrains the work and actually constrains only the file list.* A plan has two halves. One of them is
machine-comparable and is compared; the other is prose, was never compared, and was also never **shown**.

### 26.2 The decision — two parts, neither claiming more authority than it has

**(1) Put the approved approach in front of the reviewer.** The queue entry now carries
`approved_plan_id` / `approved_plan_title` / `approved_plan_approach` / `deviation_declaration` (proto
fields 13–16), the client projects them onto `QueueEntry`, and the Review Cockpit renders the approach
verbatim above the diff. A review is a comparison; this surface only ever had one side of it on screen.
Rendered **verbatim**, and scrolled rather than truncated: a summarised approach is the surface choosing
which sentence the reviewer gets to compare against, and the sentence that would get dropped is the one
the diff disagrees with. No approved plan ⇒ **no panel at all**, because an empty "approved approach"
card would assert an approval a manual agent or an external PR never had.

**(2) Make the worker declare its deviations, and route them through `FlaggedItems`.**
`mainguard-plan commit "<message>"` now takes exactly one of `--no-deviations` or one-or-more
`--deviated "<what and why>"`. Declared departures become `FlaggedKind.DeclaredDeviation` must-acknowledge
rows on the same channel that already carries out-of-scope files, rendered under
`WORKER-DECLARED DEVIATIONS (n)`.

**Explicitly rejected: any automated or LLM comparison of approach-vs-diff.** Nothing in this change reads
the approach and the diff together. The human does that; this makes it possible for them to.

### 26.3 Silence is not an answer — the three outcomes

The declaration would be worthless as an optional field: it would be empty on exactly the runs that needed
it. Worse, an **absent** must-acknowledge item is an *acknowledged* one (an empty flagged set is
`AllAcknowledged`), so an unanswered question would render as reassurance. So
`DeviationDeclaration` has three values, the same call `WorkerPlanGate.MergeEvidence` makes and the same
call `FlaggedKind.LockfileAdvisoryUnknown` makes:

| | what it means | what the review does with it |
|---|---|---|
| `Declared` | the worker named departures | one must-ack row per departure, carrying its words verbatim |
| `None` | the worker asserted it followed the approach | **no row** — rendered beside the approach as the claim it is |
| `NotDeclared` | nobody ever answered | one must-ack row saying so (fail-closed) |

A gated `commit_work` carrying neither answer is **refused**, before the commit — which is the only reason
it is safe to make mandatory (see §26.5). Both answers at once is refused rather than resolved by
precedence; a rule about which one wins would be invisible at the call site.

**A declaration cannot be walked back.** Declarations accumulate across a worker's several commits and
`Declared` is sticky: a final `--no-deviations` cannot erase a disclosure made three commits earlier. That
is the specific way this mechanism would have become a rubber stamp, and it would have been reachable by
accident rather than by malice.

### 26.4 The refusal must never strand a finished diff

This is the one way this change could be worse than the defect it closes. **`commit_work` is the only
route work has out of the jail, and an uncommitted worktree is destroyed at teardown** — that has already
cost this project a real 20-line diff, and it is why the op exists at all. A declaration gate that
stranded a finished change would be a strictly worse failure than an undeclared divergence. Three
properties make it safe, and each is pinned by a test rather than argued:

1. **The refusal lands before the commit, and is recoverable in the same turn.** `DeviationRefusal` runs
   ahead of `WorktreeManager.CommitAgentWork`, so nothing is written and nothing is undone; the worker
   re-runs the identical command with an answer and its work lands. Asserted as a *sequence* —
   refuse, then immediately retry and succeed — because the two facts being separately true is not the
   property that matters (`ARefusedDeclaration_CostsATurnAndNotTheWork`).
2. **The refusal is self-sufficient documentation.** It names `WorkerPlanShim.CommitUsage` verbatim *and*
   says, in the same breath, that nothing was committed and nothing is lost. That sentence is not
   reassurance — it is for the two workers who need it most: one whose jail was created by a daemon
   predating the flags (its `MAINGUARD.md` never mentions them, so the refusal is its only teaching) and
   one that fumbles the flag on its last turn. A worker that read "refused" as "my diff is gone" could
   stop with the work still in it. The shim's own local refusals carry the same sentence.
3. **An unapproved agent is exempt, not blocked.** The requirement is keyed on
   `PlanApprovalService.ApprovedForWorker` — not on `PlanModeSwitch` — so a plan-mode-off worker, a
   manual agent and an external-PR head all commit exactly as before. A gated worker whose plan is still
   pending is stopped one rung earlier by the plan gate itself, never by a declaration it could not have
   made. The ambiguous case (`ApprovedForWorker` refusing to guess between two approved plans) also
   resolves to *exempt*, which is the safe direction here.

### 26.5 Where it lives, and why there

- **On the plan record** (`PendingPlan.Deviation` / `DeclaredDeviations`, persisted by
  `JsonPlanApprovalStore`). A deviation is a deviation *from that plan*: it belongs to the record that
  authorised the work, it is resolved by the same `ApprovedForWorker` the scope comparison uses, and it
  persists for free. It has to persist — a worker declares at **commit** time and the row is armed at
  **verification** time, so a daemon restart between the two would otherwise turn an answer back into a
  question and put a must-ack row in front of a human for something that *was* answered.
- **Armed daemon-side**, in `MergeQueueProvisioner.ArmFlaggedChangeReview`, into the
  `AcknowledgmentStore` the merge gate actually reads — the same reasoning `ReviewLockfiles` writes down:
  rows composed client-side render and block nothing, and an acknowledgment addressed to a locally-minted
  id clears a store no merge consults.
- **One resolver, not two.** `resolveApprovedPlan` became `resolveApprovedWork` and returns
  `ApprovedWork(Plan, Declaration, Deviations)`. The scope the diff is measured against and the approach
  shown to the reviewer must describe the **same** approved plan; two independent seams would be free to
  name two different ones the instant a re-scope lands, and the surface would then present an approach the
  diff was never measured against.
- **The acknowledgment resets on a push**, like every other flagged row. A branch-level item hashed only
  on its own text would survive a push that rewrote the entire diff it was acknowledged against, so
  `FlaggedChangeDetector.HashDiff` (a content hash over the whole diff) is folded into its id.

### 26.6 The ungated case

With plan mode off there is no approved approach, so there is nothing to have departed from. A declaration
is therefore **neither required nor recorded** for an ungated worker — demanded anyway, it would be a
ritual, i.e. the "always present, never means anything" shape this codebase deletes on sight. That is
decided by `ApprovedForWorker` (the single authority), not by a second reading of the mode switch. The
ungated worker's operating instructions teach none of it, and a declaration one volunteers anyway is
**told** it was not recorded rather than silently dropped or turned into a failed commit — the commit is
the thing that must not be lost.

### 26.7 The queue seeder declares "none" for the plans it fabricates

`QueueSeeder.SeedPlan` records `DeviationDeclaration.None` on the plan it synthesises, alongside the
authorship and the human approval it already synthesises (and already labels as synthetic in the two
free-text fields a human reads). Left at `NotDeclared` it would arm a must-acknowledge row on **every**
seeded plan-gated entry, forever — a row whose real cause is "a dev tool made this entry" rather than
anything about the branch. A blocker that is always present for a whole class of entries and never means
anything is precisely how a gate teaches people to click through it. The fail-closed default stays where
it matters: on a real worker, which is the only thing that can actually answer.

### 26.8 The record is bounded, and what it drops it declares

`commit_work` may be called any number of times and records on a clean tree too, so the accumulated
declaration is an **agent-controlled growth path through a file the daemon rewrites on every save** — and
it was the one agent-authored field with no oversized guard, while `TaskPlanSchema` bounds every sibling
(`MaxScopeFiles` 200, `MaxFieldLength` 4 000, `MaxPlanBytes` 64 KiB).

Both bounds fail **loud rather than closed**, because closed here is a dead end: a worker refused at the
cap could never commit again, and its work dies with the jail — the "limit that is really a dead end"
shape §24 argues against at length.

- **Count** (`MaxDeclaredDeviations` = 20). Excess texts are not stored, and the record gains an explicit
  "…and N further declared deviation(s) that this record cannot hold" row, which the human reads like any
  other. The notice is recomputed from what actually dropped rather than accumulated, so a second
  overflowing round does not leave two of them, and it never counts toward the cap itself.
- **Length** (`TaskPlanSchema.MaxFieldLength`, the same bound the `approach` it is about gets). An
  over-long deviation is truncated with `…[truncated]` appended. An unmarked cut is the one way
  truncation would be worse than either alternative.

### 26.9 Left alone, deliberately

- **No automated approach-vs-diff comparison**, per the decision above.
- **The declaration does not gate the commit's content**, only its acceptance. The work still leaves the
  jail; the declaration is evidence attached to it, and the block lands at the merge, where a human is.
- **Nothing verifies the declaration.** A worker can assert `--no-deviations` untruthfully, exactly as it
  could write a passing test for the wrong behaviour. What changed is that the claim now exists, is
  attributable, is on the audit chain (`worker_deviation_declared`), and sits on the same screen as the
  approach it is about. That is the honest limit of this control and it is stated in the worker's own
  instructions rather than implied.
- **`ControlCenterViewModel.OpenReviewAsync`'s wiring hop is not covered by a test.** The context
  properties it sets are pinned on both sides (the wire projection and the cockpit's rendering), but the
  line that joins them needs a live daemon plus a diff RPC. This is the same gap
  `ReviewCockpitVerifiedShaTests` documents for `VerifiedAgainstSha`, and it is where that defect actually
  lived — worth closing with a live-wiring harness, by whoever builds one.

## 27. The 2026-09-02 review — what a green suite was hiding, and the owner's decisions

A full read of this branch against the contract and this document, with the build clean and both
non-Docker tiers green (3912 + 824), found the class of defect §5 names — a control described here and
not delivered — in eight places that mattered. Each was verified in code before it was reported; the
owner decided every design question on 2026-09-03; the fixes landed as narrow commits on this branch.

### 27.1 What was found

| # | finding | where | severity |
|---|---|---|---|
| 1 | The outbox **response** write followed a jail-created symlink: `<outbox>/<ticket>.out` was opened, written and chmod-ed under a path the jail chooses, in a 0777 directory with no sticky bit, by a daemon running as the user. One `ln -s ~/.zshrc outbox/<t>.out` and a request wrote the status JSON into the host file. §14 hardened the request side only. | `AgentIpcServer.ServeOutboxRequestAsync` | Critical (macOS substrate) |
| 2 | The loop **did not survive a daemon restart** with jails alive: `WorkerPlanGate._held` was memory-only (so `Allows` opened for an unapproved worker and an approved one never received its task), adopted sessions had no `ParentAgentId` (so a coordinator owned none of its workers), and `AgentIpcServer.Dispose` deleted the IPC directory (so every adopted jail's shim wrote into an orphaned mount). The restart test covered the JSON plan store only. | `WorkerPlanGate`, `AgentSessionReconciler`, `AgentIpcServer` | High |
| 3 | The RT-D1 boot reconcile was a **no-op**: `resolveRepoPath: _ => null`, so `MergeReconcileTask.Reconcile` returned for every lease. All of §23's K1 was unreachable, and a lease that survived a crash between `BeginMerge` and `ConfirmMerge` blocked merging, discard and reject on that repo forever (the lease store has no expiry). | `GatewayServiceRegistration`, `MergeReconcileTask` | Critical |
| 4 | A gate-stage `ConfirmMerge` refusal after the client's `--ff-only` had landed released the lease and claimed the boot reconcile would recover the merge (it could not, and a released lease is never reconciled). User main and the queue diverged, the mirror lagged. | `MergeQueueGrpcService.ConfirmMerge` | High |
| 5 | The coordinator shim used the 60 s default for every op while `verify` ran the whole suite synchronously and `spawn` could build a toolchain layer: the coordinator was told "cannot reach the daemon" while the daemon completed the operation, and retried. | `AgentSpawnShim`, `AgentSpawnService.VerifyAsync` | High |
| 6 | Three callers re-verified a branch that did not descend from main — the trigger and the human's Verify on `StaleVerified`, and the post-abort path — minting the §22 loop-forever `Verified`. Only the cascade's own re-entry asked about descent. | `MergeQueueProvisioner.RunVerificationAsync` | High |
| 7 | Teardown deleted the agent's repository after a publish the mediator **refused** (non-fast-forward after an amend), destroying the only copy of the rewritten commits, under a comment asserting that could not happen. | `SandboxAgentLauncher.TeardownAsync`, `WorktreeManager` | High |
| 8 | The socket framing read a line with no bound (the §14 defect on the other transport), and had no per-endpoint in-flight cap. | `AgentIpcServer.ServeConnectionAsync` | High |
| 9 | Escalation was not terminal on the primary plan path: `Present` only refused a *live* plan, and an escalated one is not live, so a worker presented a fresh plan with a fresh budget — the loop the limit exists to bound, reopened from the worker's side. §24.3 had closed exactly this for re-scopes. | `PlanApprovalService.Present` | Medium |
| 10 | The frozen-jail guard read the session state word, which `MarkMergeState` rewrites on every queue transition, so a paused or conflicted worker read `StaleVerified`/`Working` for up to a reconciler interval and prompt/verify passed. | `FrozenJailPolicy` | Medium |
| 11 | `EnsureQueue` walked the queue's main backwards to a lagging mirror in the window after a confirm; `CliSettingsGrantScrub` decided on unparsed bytes so `\/opt\/…` passed; the J2 echo separator counted a mid-turn CLI's output as echo and skipped the floor; the readiness trigger spent its once-per-tip attempt on transient refusals; the plan gate streamed every coordinator's cards with no repository on them; escalated cards were rebuilt on every refresh; the gate row kept a dragged pixel height after hiding. | various | Medium |

### 27.2 The decisions (owner, 2026-09-03)

- **Restart:** the loop survives with jails intact — held tasks persisted (`JsonHeldTaskStore`), the parent
  on a container label, the IPC directory kept across shutdown and the endpoint re-bound on adoption.
- **Stuck leases:** the reconcile runs per repo when its queue comes up (mirror-based classification), and
  on demand when `BeginMerge` finds an outstanding lease; `Classify` also refuses the zero-commit
  coincidence.
- **Late confirm:** a gate-stage refusal whose reported sha equals the lease's `ExpectedBranchSha`
  records `Merged` under `confirm_rpc_late`; otherwise the lease stays outstanding for the reconcile.
- **Refused publish at Stop:** keep the agent's repository, warn, audit (`agent_repo_kept`).
- **`request_verification` proposes** and returns; the verdict arrives on `get_worker_status` (contract §3
  amended). The shim gets per-op deadlines.
- **Escalation:** terminal, with one human-granted exception — the operator-only `RequestNewPlan` RPC
  (denied to the coordinator role) sends guidance and admits exactly one fresh plan; a second escalation
  is terminal for good (contract §3.1 amended).
- **`StaleVerified` re-verification** routes through the cascade; `RunVerificationAsync` refuses a local
  branch that does not descend from the queue's main.
- **The frozen guard reads a pause axis of its own** (`AgentSessionStore.MarkFrozen`), written only by the
  paths that freeze or thaw a jail; the trigger defers on it instead of spending the tip.
- **One coordinator per daemon** (`CoordinatorLimits.MaxLiveCoordinators = 1`, contract §2.2).
- **Delivery:** narrow, targeted commits directly on this branch; the Docker tier run even though it
  sweeps the owner's live jails.

### 27.3 Left alone, deliberately

- ~~The hand-back publish after "let the agent resolve"~~ — **built on 2026-09-04 (owner decision:
  mark and rebase-publish).** "Let the agent resolve" now sets a hand-back mark in
  `RebaseConflictParkingStore`; the composition root installs it on the ref mediator as
  `RewritePermitted`, so the worker's finished rebase — a rewrite of published history, with patches that
  legitimately differ after conflict resolution — is published exactly once, the mark is consumed on that
  publish, and rule 2 is absolute again afterwards. The publish raises `Advanced`, the readiness trigger
  re-verifies, and the descent belt passes because the branch now sits on main. A mark for one agent does
  not leak to another. Pinned at the worktree manager (real git, an amend, the grant consumed, a bystander
  still refused), at the provisioner (the hand-back sets the mark), and at the composition root (the
  policy is installed).
- Stale `.res` reclaim on a live endpoint: the aggregate bound already reclaims, and the per-op shim
  deadlines remove the normal source of leftovers.
- ~~The launcher's teardown routing on a refused publish~~ — pinned on 2026-09-04 by
  `TeardownAfterRefusedPublishTests` (a recording worktree manager; each publish outcome routes to exactly
  one of the two removals).
- **The resume Docker flake** (`QueueEntryResumeDockerTests.ACleanStopAfterACommit…`, §12.5/§22.6): chased
  on 2026-09-04 with three full instrumented `RequiresDocker` runs (per-test output, ~400 executions) and it
  did not reproduce once; the network reachability probe fired once instead. Its one occurrence in this
  pass was during the first integrated run while three other worktrees were building on the same machine,
  which matches the documented load dependence. The only hypothesis the code offers is unverified: the
  first `StopAgent` force-removes the old container and deletes its worktree, and under load a slow
  removal could still be in flight when the resume creates the new worktree at the same path. Owner
  decision: stop here, keep it documented; CI keeps running the tier.
- `VerifiedFreezeTests` raced its own cascade (no requeue delegate, so `NotifyMainMoved`'s FIFO walk
  direct-ran a re-verify against the test's); the rig now records requeues, as the trigger rig does.

### 27.4 The mirror's age (owner decision, 2026-09-04)

The daemon's mirror is a bare clone of the user's checkout, and its `main` was pulled forward only when
something else happened to do it — repo-open (the provision RPC's incremental fetch), merge-confirm, the
cascade's align step, and the reconcile paths. A pull or a commit made on `main` outside Mainguard therefore
left every entry verified against a `main` the checkout no longer had until one of those moments, and
nothing on any surface said how old the mirror's claim was. Safe — the merge's identity checks refuse
rather than land the wrong thing — but silent, and on Windows the mirror lives inside WSL where a user
cannot easily look.

Built: `MergeQueueProvisioner.RefreshMainFromCheckout` (the same guarded reconcile `EnsureQueue` performs —
a mirror behind the queue is never walked backwards, a failed fetch is recorded and moves nothing, every
attempt republishes the queue), run by `MirrorMainRefreshHostedService` every
`CoordinatorLimits.MirrorRefreshSeconds` (60) and on demand by the new `MergeQueueService.RefreshMirrorMain`
RPC, which the Pro app calls when its window regains focus. `QueueUpdate` carries `mirror_main_refreshed_at`
and `mirror_main_refresh_error`; the queue rail renders "mirror refreshed from your checkout N min ago", or
the daemon's error as a warning. The RPC is denied to the coordinator role (it can fire the stale cascade).

