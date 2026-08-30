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
