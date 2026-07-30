# Reviewing phase 1 — what to look at, how, and what "correct" looks like

**Written for the pause before phase 2.** Everything below is on `phase2`. Nothing has reached `main`.

The point of this review is **not** to re-check the tests — CI does that. It is to check the
**judgement calls**: places where something was decided, a gate moved, or a claim was made that
you have to agree with before more product gets built on top of it.

Work through it in the order given; §1 is the one that matters most.

---

## 1. The merge queue actually merges — verify this yourself

**Why this first.** For most of this project the Merge button took the daemon lease, recorded a
successful merge, fired the stale-cascade, and **ran no git at all**. The branch reached terminal
`Merged` and an RT-D1 idempotency record asserted a merge that never happened — which boot
reconcile then reads as proof. Work was dropped from the queue as completed, having never reached
`main`, with a durable record saying it did.

That is fixed (#261 local, #265 external). It is also the single thing most worth confirming with
your own eyes, because "the RPC returned success" is exactly what the broken version did.

```bash
# the whole loop, against a real jail and a real checkout
dotnet test Mainguard.Server.Tests/Mainguard.Server.Tests.csproj \
  --filter "FullyQualifiedName~MergeQueueEndToEndDockerTests"
```

**Expected:** all pass. The assertions to actually read are in
`Mainguard.Server.Tests/Agents/MergeQueueEndToEndDockerTests.cs` —
`AgentBranch_VerifiedInItsOwnJail_MergesAndAdvancesMainToTheBranchTip` asserts
`refs/heads/main` in the user checkout **equals the agent branch tip and differs from the starting
sha**. Ref assertions, not RPC return values. If you read one file in this review, read that one.

Its negative control is `BranchThatFailsItsOwnTests_CannotMerge_AndMainNeverMoves` — the suite can
genuinely fail, which is what makes the passing assertions mean anything.

**Judgement to confirm:** the merge lands in **your real Windows checkout**, not the VM mirror. The
mirror is staging. That was existing design; it is now actually connected.

---

## 2. The four things you said "fully working" means

| criterion | test | what it proves |
|---|---|---|
| branch → verify → queue → merge | `AgentBranch_VerifiedInItsOwnJail_MergesAndAdvancesMainToTheBranchTip` | verification runs **in the worker's own jail**, and `main` really advances |
| flagged-change review gate | `BranchThatRewritesItsVerifyCommand_IsBlocked_SurfacedForReview_AndMergesOnlyAfterAcknowledgement` | a branch that **verifies green** is still blocked; acking the *wrong* item id clears nothing |
| stale cascade | `WhenOneBranchMerges_TheCoTenantIsInvalidated_BlockedFromMerging_…` | the co-tenant is **actually blocked** — `CanMerge` false, `BeginMerge` not granted, `main` unmoved |
| external PR entries | `ExternalPullRequestEntry_MergesUpstream_AndTheCheckoutConvergesOnThatMerge` | upstream merged via the host API **and** the checkout converged onto that commit |

**The one worth understanding properly is the second.** RT-D2 flags a branch whose
`.mainguard/verify` command drifts from `main`'s. The branch's tests pass — and it is *still*
blocked, because a branch that rewrites its own verification is the case where a green result
means least. Confirm you agree with that gate existing at all; it is the most opinionated thing in
the queue.

---

## 3. Decisions that changed a gate — read these, they are policy not code

These are in `docs/design/` on `phase2`. Each records what was decided **and why the alternative was
rejected**, because that reasoning is what stops a future change from quietly undoing it.

### `coordinator-contract.md` — the one to read closely

Two decisions materially change oversight:

- **P2-14's two-phase spawn gate is removed.** A human no longer approves whether a worker comes
  into existence. **In exchange**, plan authorship moved from the coordinator to the *worker*, and
  the worker **blocks** until you approve its plan. Net human interactions are unchanged; the
  information behind each is better — the coordinator has no worktree and no view of the repo, so
  its plan described work it could not inspect.
- **A blocked worker counts against `MaxActiveWorkers`.** This reverses my first draft. The tempting
  answer is "blocked workers are idle, exempt them" — wrong, because that cap is a *resource* cap
  and a blocked worker still holds its jail, tmpfs, network segment and worktree. Exempting them
  lets the coordinator spawn unboundedly many resource-consuming workers exactly when you are too
  busy to approve. It is backpressure by design, and the UI must say *"6 workers waiting on your
  approval"* rather than going quiet.

**Also decided:** `MaxPlanRevisions = 3`, then the worker escalates instead of looping.

### `mg-3-mediated-ref-updates.md`

The gc answer is the substantive part: **pruning breaks alternates borrowers; repacking does not.**
So `gc.auto=0`, repack-without-prune may run *any time* (this is what bounds growth without waiting
for an idle window), full prune only when no agent is attached.

### `code-signing-plan.md`

Steps 1–3 are done. Step 4 (Azure Trusted Signing, ~$120/yr) is **yours to buy** before public
distribution. The ordering was deliberate: it is now a build-property change, not a feature.

---

## 4. Run it yourself

```bash
dotnet build                              # 0 errors expected
dotnet test Mainguard.Tests/Mainguard.Tests.csproj              # 2883 pass
dotnet test Mainguard.Server.Tests/Mainguard.Server.Tests.csproj \
  --filter "Category!=RequiresDocker"                            # 408 pass
dotnet run --project Mainguard.Pro.App    # the Pro head (not the shell library)
```

`RequiresDocker` (~79 tests) needs Docker. **Concurrent runs are now safe** (#278): every
`RequiresDocker` class joins a collection whose fixture takes a host-global, cross-process lock for
the window its Docker tests run, so a second run queues rather than colliding. The resources
themselves stay shared on purpose — the egress proxy is a singleton by design, and
`mainguard-agents`/`mainguard-egress` are addressed by literal name from MG-7's resolver pin and
MG-18's posture gate, so renaming them per run would switch those security gates off for the
renamed topology. A pair of concurrent full runs takes roughly twice the wall clock of one; that
gap is the queueing, not a hang.

**Environment now working that was not before:** `git-lfs` installed, `MAINGUARD_LIBVTERM` set in
`~/.profile`, Windows SDK 10.0.302 installed per-user (Windows had 10.0.300, which cannot satisfy
`global.json`'s `10.0.301`/`latestPatch` pin — so **no Windows verification was possible at all**
until that was fixed).

**Both test suites now run against their own data root** (#287, #288). Before that, `Mainguard.Tests`
was *unrunnable* — every Avalonia-headless harness hung on the migration lock — and both suites
read and wrote the real `~/.mainguard`: the live `mainguard.db` and `config.json`, and from
`Mainguard.Server.Tests` the daemon token, **the live mTLS identity** (`daemon-client.pfx` /
`daemon-server.cer`), the plan and leader-session stores, and 13 fresh `agent-ipc/<id>/` socket
directories per run (about 1,170 had accumulated). A run now leaves the real data root
byte-identical; there is a test that fails if that stops being true.

If you have a pre-#287 `~/.mainguard`, two bits of residue may remain: the orphaned
`__EFMigrationsLock` row (cleared automatically on the next launch of a `phase2` build) and the
accumulated `agent-ipc/` directories (harmless, ~9 MB, safe to delete).

---

## 5. Known-not-working — do not chase these

**All four items previously listed here are now fixed and merged.** Kept as a record of what was
open when this guide was written, because two of them turned out to be worse than their tickets:

| was | now |
|---|---|
| `pr-<n>` ids daemon-global | fixed (#281) — and it exposed that the **kill switch would have thrown, with a jail left running** |
| Docker suites not isolated between concurrent runs | fixed (#278) — two concurrent full runs now both hit 77/0/1 |
| `AgentRefWatcher` self-eviction on any `Directory.Exists` failure | fixed (#279) |
| external merge toast said "Merged agent/pr-7 into main" | fixed (#280) |

The two items this section listed as *still open* were then fixed as well (#284, #286), along with
three more found while fixing them (#285, #287, #288). **The known-defect list is empty.**

| was | now |
|---|---|
| agents adopted at daemon boot never ref-watched | fixed (#284) |
| `TerminalSessionManager` keyed by agent id alone | fixed (#286) |
| external-PR adopt path never watched the jail it adopted | fixed (#285) |
| the test suite read and wrote the developer's real `~/.mainguard` | fixed (#287, #288) |
| the desktop head hung forever on an orphaned `__EFMigrationsLock` row | fixed (#287) |

### Read these two before you review anything else

Both are cases where the *ticket* was wrong, not just the code — which is the useful thing to know
about how this codebase fails.

**1. The boot-adoption fix (#284) was nearly a no-op.** The ticket said "wire the adopt branch".
But nothing in production `Upsert`s into the expected-agents table except the reconciler itself, and
it is SQLite-backed: restart #1 adopts and writes a row, and **every restart after that** takes the
already-expected `continue` path and is not an adoption at all. Fixing only the adopt branch would
have looked applied and regressed on the second restart. The fix watches every live jail the pass
*keeps*, on both branches. The same change found a second defect in that method — it keyed its diff
on the agent label alone, so two repos each running `pr-7` made `ToDictionary` **throw** out of a
fail-fast boot sequence: the daemon would not start.

**2. The migration hang (#287) was not a lock.** The error message said *"Another Mainguard
instance may be holding the database lock — close it and relaunch"* and that was a **guess, and
wrong**. Nothing held an OS lock (`BEGIN IMMEDIATE` succeeded in 0.7 ms), no process was running,
and there were zero pending migrations. The cause was one orphaned row in `__EFMigrationsLock`,
which EF claims *before* checking for pending migrations and then polls for forever. "Close it and
relaunch" cannot clear a row in a file. The daemon already had this fix; the desktop shell did not.

Both belong to the pattern this project keeps producing: **something that looks applied but is
not**, and **an error that asserts a cause it never checked**. When reviewing, treat any confident
diagnostic message as an unverified claim until you see the check behind it.

### Two judgment calls recorded, not resolved

- **`DockerStdinRegressionGuardTests`** failed twice with `IOException: Cannot allocate memory`
  under four concurrent test suites; passes on re-run and in CI. Recorded as environmental. That is
  a judgment, not a proof — if it recurs without load, it deserves a ticket.
- **The same key-collision class was fixed three times** (#281 session ids, #284 reconciler, #286
  terminal sessions), each in a different subsystem, each found only by looking. A fourth site may
  exist; nobody has swept for it.

---

## 6. What I would want challenged

Places where I or an agent made a call you might disagree with:

1. **The plan gate blocks.** You chose this; it means you are in the loop once per worker. If six
   workers block and you are away, the coordinator stops spawning. That is the intended trade —
   confirm you still want it once you have felt it.
2. **`external-pr` jails start with no CLI and no model key**, and a `withoutHostCredentials` flag
   stops untrusted PR heads inheriting cached CLI logins. Confirm that is the boundary you want.
3. **The toolchain file selects from a closed catalog** rather than describing an installation — a
   repo can say "this is a .NET project", not *how* .NET is obtained. That is what keeps a
   repo-writable file from being an install-time code-execution surface.
4. **Only `main`'s toolchain declaration is ever provisioned.** Flagging a branch's install *after
   it has run as root* is not a control, so branch declarations never reach the provisioner.
5. **Verification runs in the worker's own jail, always.** Host execution is a rejection trigger.
   This is why the toolchain and package-cache work existed at all — it would have been far easier
   to verify on the host.

---

## 7. The one number worth remembering

`RequiresDocker` went **26 passed / 14 failed → 77 / 0** in this stretch. The 14 were written off
as "environmental, CI is the real check" by several agents. They were not environmental: Docker.DotNet
3.125.15 (the latest published version) hangs on any exec with `AttachStdin = true` against Engine
29.x. `MainguardEnv` ships Docker 20.10.24, which is **end-of-life** — so the day that dockerd is
upgraded for security patches, **every agent spawn would have broken at the secret write**, with a
30-second timeout as the only symptom.

A diagnosis nobody had tested was hiding both a latent production failure and 13 security tests.
That is the pattern this whole stretch kept finding, and it is the reason for the discipline in §2.
