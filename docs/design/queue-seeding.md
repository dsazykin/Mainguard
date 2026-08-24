# Dev-only merge-queue seeding

**Status: design of record for `feat/queue-seeding` (2026-08-24).** Companion to
`resume-stranded-queue-entry.md`, `verification-trigger.md` and `agent-branch-confinement.md`;
subordinate to the master implementation document where they touch.

## 1. What this is, and the one rule

Manually testing the merge-queue surfaces (verify / review / merge / reject / stale cascade /
pause) requires a real agent: a Docker jail, sometimes a real LLM CLI burning real tokens, and a
full work→verify cycle per entry — worst for scenarios that need several entries in specific
states at once (overflow/ordering reproductions, a stale-cascade pair). Queue seeding produces
legitimate queue entries in any target `WorkerMergeState` instantly, without an agent.

**The one rule: nothing is fabricated.** No synthetic row is ever written to `MergeQueueRows`;
no state is asserted that the state machine did not transition into. The seeder drives the same
public `MergeQueue` methods production drives — `EnsureEntry`, `RunVerificationAsync`,
`RequestReview`, `TryReject`, `TryDiscard`, `TryConfirmHumanMerge` under a real lease,
`NotifyNewCommits`, `Cancel` — so a seeded `Verified` entry has passed through the same
transition code, the same legality checks (`Legal` table), the same persistence-per-transition
and the same audit surface a real one did. What is synthetic is exactly two things: the agent
behind the id (there is none — and the wire says so honestly via `has_live_sandbox`), and the
verification *outcome* (supplied, not executed — and the record says so, §4).

The state-drift bug class this rule defends against is documented in
`docs/review/walkthrough-2026-08-20/ISSUES-LOG.md`: a UI state that the real system cannot
produce is a test that certifies nothing.

## 2. Real git data

Every seeded entry is backed by a real branch with a real commit in the repo's bare mirror:
`refs/heads/agent/seed-<n>`, created with git plumbing (`hash-object` → scratch-index
`read-tree`/`update-index --cacheinfo`/`write-tree` → `commit-tree -p <mainSha>` →
CAS `update-ref`) through `AgentGitCommand` — the daemon's one checked git path. Diffs,
`GetMergeDiff`, bring-local and the merge itself work because the commits are real.

Why direct ref writes in the mirror are safe (verified):

- The branch-confinement `reference-transaction` hook is installed **only into agent-owned
  repositories** (`AgentBranchGuard.InstallHook`, via `WorktreeManager`); the bare mirror carries
  no hooks (`RepoProvisioner` writes config only), and every daemon git invocation runs with
  `core.hooksPath=/dev/null` anyway (`AgentGitCommand.HardeningArgs`).
- Nothing watches the mirror: `AgentRefWatcher` reads registered agents' **own** repo ref files,
  never the mirror, so a new `agent/seed-*` ref fires no observer. (The mediated publish's
  observers fire only inside `AgentRefMediator.Publish`, which the seeder never calls — the seed
  ref *is* the as-if-published input.)
- The seed ref stays inside the `agent/` namespace; mirror `main` is never written directly (§5).

Commit **flavors** choose what the branch changes, so gate classification is real, never injected:

| Flavor | Commit content | What really fires |
|---|---|---|
| `PLAIN` | `seed/<id>.txt` | nothing flagged; `CanMerge` can genuinely pass |
| `FLAGGED` | adds `.github/workflows/seed-ci.yml` | the real `RiskClassifier`/`FlaggedChangeDetector` path match → real must-acknowledge items |
| `CHANGED_TEST_COMMAND` | `.mainguard/verify` differing from main | the real RT-D2 `ChangedTestCommandGate` arms |

## 3. The synthetic-verification seam

`MergeQueue` takes its verification runner as a constructor delegate; `MergeQueueProvisioner`
composes the real one. The provisioner's verification path splits into a **mirror-read half**
(RT-D2 command/toolchain resolution, `ChangedTestCommandGate` arming, `ArmFlaggedChangeReview` —
pure `git show` against committed trees, no jail required) and a **jail half** (container
resolution, publish/alignment, toolchain probe, the sandboxed run).

A new `SyntheticVerificationRegistry` maps `(repoHash, agentId)` → a plan
`{ Passed, HoldSeconds, StaleBehavior }`. For a registered id the provisioner **keeps the entire
mirror-read half** — real provenance, real gate arming — and replaces only the jail half with the
plan's outcome. Everything upstream in `MergeQueue.RunVerificationAsync` (the `Verifying`
transition, the in-flight set, `IVerificationStore.Insert`, the settle logic, `Changed` events)
runs unchanged. Unregistered ids are untouched; the registry is **always wired and empty in
production** — only the flag-gated RPC (§7) can populate it, and it refuses any id without the
`seed-` prefix.

**MG-40 dependency (pinned, not assumed):** `FlaggedChangeGate.Allows` default-DENIES an id whose
review never ran. Seeded entries become mergeable only because the mirror-read half still runs
`ArmFlaggedChangeReview` (empty set for `PLAIN` ⇒ `AllAcknowledged`). `SeedingCompatibilityTests`
pins both gate defaults — `ChangedTestCommandGate` passes unknown ids, `FlaggedChangeGate` denies
them — so removing the mirror-read half from the synthetic path fails loudly instead of silently
minting unmergeable (or worse, unreviewed-mergeable) entries.

**Holds.** A plan may hold the synthetic run in flight for up to 600 s (`Task.Delay` on a linked
CTS). The entry is then *genuinely* verifying: `IsVerificationInFlight` is true on the wire,
`ClearStalledVerification` refuses with "wait", and a discard landing mid-hold exercises the real
terminal-during-run guards. Overlapping holds are the point: they make race windows reproducible
that real agents cannot time — merge-lands-while-co-tenant-verifies, discard-during-verify,
main-moves-under-a-run.

## 4. The record is visibly synthetic — the forgery rule

P2-10's trust model names a value-supplied verification pass **the** forgery the queue exists to
prevent (pass/fail must be the daemon-observed container exit, OPS SA-1). A seeded outcome is by
definition value-supplied, so the immutable record must say so about itself:

- `VerificationRecord.ResolvedCommand` = the *really resolved* command + `" [seeded — not
  executed]"`.
- `LogArtifactPath` points at a real artifact file stating that no run was executed and what
  outcome was requested.
- Every seeded entry appends a `queue_entry_seeded` audit event (daemon-derived actor) to the
  daemon's registered audit sink (`MergeQueueProvisioner.AuditLog` — pinned identical to the
  host's `IAuditLog` by the composition-root test).

A seeded record can therefore never be mistaken for evidence that a test suite ran — it is
evidence that a transition legally occurred, which is exactly what UI/state-machine testing needs.

## 5. Main only ever moves for real

The mirror's `main` is force-fetched from origin (`TryRefreshMirrorMainAfterMerge`,
provision-time fetches), so advancing the mirror alone would later be rolled back — recreating
the documented walked-backwards-main defect. Both seeded main movers therefore go through the
origin checkout, which is why **seeding is a scratch-repo tool** (§8):

- **`Merged` target** — the real thing, end to end: `IMergeLeaseStore.TryBegin` → refuse unless
  the origin checkout is on `<main>` → `git -C <origin> fetch <bare> refs/heads/agent/seed-N` →
  `git merge --ff-only <sha>` (the same operation the GUI's foreground merge performs) →
  `MergeQueue.TryConfirmHumanMerge(agentId, newSha, expectedMainSha)` (full gate + CAS evaluation
  under the queue lock) → `Leases.Confirm` → mirror refresh. Any refusal is returned verbatim;
  every non-merged exit releases the lease. A `FLAGGED` seed genuinely cannot reach `Merged`
  without its items acknowledged. Known gap: no client-side T-19 journal entry is written (the
  journal lives in the GUI's merge path), so a seeded merge is absent from the client undo
  history.
- **`StaleVerified` target** — a real `git commit --allow-empty` on origin `main` (an out-of-band
  main move, a scenario the reconcile path explicitly supports) → mirror refresh → `EnsureQueue`
  reconcile fires the real `NotifyMainMoved` cascade.

**Loud warning: the cascade is real and repo-wide.** A seeded merge or main move stales every
`Verified`/`AwaitingReview` entry in that repo's queue — including real agents'. Correct by
design (that *is* the cascade), but it is why the tool is pointed at a scratch repo.

Per-entry `StaleBehavior` decides what the cascade's re-queue does with a seeded stale entry:
`Hold` (default) leaves it resting at `StaleVerified` — indistinguishable from awaiting its FIFO
turn, the state you seed in order to look at — while `Cascade` falls through to the real requeue
path, which for a jail-less entry ends at `Working` with the real "no live sandbox" reason. Both
are behaviors the production system exhibits.

## 6. Dynamics — commits from multiple places at once

The orchestrator era's hard-to-test surface is temporal: several `agent/<id>` branches advancing
concurrently, verifications overlapping, a merge landing mid-run. Two primitives cover it:

- **`PushCommits`**: appends a real commit to an existing seeded branch (same plumbing, parent =
  branch tip) and calls `queue.NotifyNewCommits(agentId)` — the same public transition the
  watcher path drives, with the same guards (terminal entries ignore it; verification evidence is
  cleared; requeue blocks retire). A seeded `Verified` entry drops to `Working` for real; a push
  landing mid-hold exercises the real settle/staleness handling.
- **Overlapping holds** (§3) for reproducible races.

Scenario presets ("stale pair", "overflow ×12", "concurrent verify ×3", "commit storm",
"merge during verify") live in the dev panel as client-side drivers over these primitives — the
daemon surface stays small and every individual call remains an honest state-machine drive.

## 7. Gating — three layers, one flag

`MAINGUARD_ENABLE_QUEUE_SEEDING=1` is captured **once at daemon startup** into a
`QueueSeedingOptions` singleton and never re-read. (The in-proc test tier flips it by replacing
that singleton in `ConfigureTestServices` — `DaemonFixture.EnableQueueSeeding`; a `UseSetting`
configuration key measurably never reaches `builder.Configuration` during the daemon's
`ConfigureServices` under the minimal-hosting test factory, and a process-wide env var cannot
differ between two side-by-side test hosts.)

1. **Primary: the service is not mapped.** `DaemonHost.MapServices` maps
   `QueueSeedingGrpcService` only when enabled; disabled ⇒ every call is `UNIMPLEMENTED`.
2. **Belt: `SeedingGateInterceptor`** (the `RoleInterceptor` pattern) denies the
   `/mainguard.v1.QueueSeedingService/` method prefix with `PermissionDenied` when disabled —
   phase-3's own critique ("defence-in-depth for a token nothing mints") is why this is the belt
   and not the primary.
3. **Unconditional:** all seeding methods are in `RoleInterceptor.CoordinatorDeniedMethods` — a
   coordinator credential can never seed (seeding is `EnsureEntry` + verify + merge power in one
   RPC). Session-token auth applies as to every RPC.

A loud `LogWarning` banner announces seeding at boot when enabled. **Seeding is never an in-jail
IPC op** — the coordinator branches pin the agent-socket op surface with a substring assert
(`discard|clear|stalled|resume|merge|verif`); seeding lives on the operator gRPC surface only.

## 8. Identification, cleanup, and limits

- Seeded ids carry an enforced `seed-` prefix; entries are `MergeEntryOrigin.Local` so the rail
  renders them exactly as real local entries (the point of UI testing). Identification is the
  prefix + the `queue_entry_seeded` audit event — a deliberate trade accepted in review.
- If `main` has no committed `.mainguard/verify`, the seeder commits one (content `true`) to
  origin main first and reports `provisioned_verify_config=true`. Caveat, accepted: a repo whose
  real state is "no verification command" cannot be seeded *as that repo*.
- **`ClearSeededEntries` ordering is mandatory** (resurrection hazard: `MergeQueue.Cancel`
  deletes the row and `GetStateLocked` defaults unknown ids to `Working`, so a hold completing
  after `Cancel` would re-mint the row): per id — (1) `TryDiscard`, or cancel the hold CTS and
  await the retained run task; (2) `queue.Cancel`; (3) `update-ref -d` the mirror ref;
  (4) drop the registry row. Only `seed-`-prefixed ids are ever touched, structurally.
- **Restart limitation:** a held `Verifying` row rehydrates after a daemon restart with no run
  behind it; the restart resume probes for a jail, finds none, and strands it to `Working` — real
  behavior, and incidentally the recipe for producing the "stuck Verifying" shape on demand.
- Seeded entries have no jail, honestly: `ReconcileJails` marks them stranded within ~30 s and
  `has_live_sandbox=false` flows on the wire — the jail-less rendering hardened by commit
  `cb713d89`, exercised for free.
- **Clear before disabling:** `ClearSeededEntries` itself requires the flag; seeded rows left
  behind with the flag off are real rows and must be discarded through the ordinary UI.

## 9. The phase-2/3 compatibility contract

The coordinator branches (`feat/coordinator-phase-2-worker-authored-plans`,
`feat/coordinator-phase-3-role-lock`) add `WorkerPlanGate` (a third `IMergeGate`, permissive for
ids never `Hold()`-ed) and `WorkerReadinessTrigger` (auto-verification; `MayAutoVerify` refuses
un-held ids). `SeedingCompatibilityTests` pins the properties seeding relies on so merging those
branches **fails loudly** until the seeder is extended rather than silently rotting:

- (a) the gate defaults of §3, in both directions;
- (b) no automatic caller of `RunVerificationAsync` fires for a `seed-` id — after the merge,
  `WorkerReadinessTrigger` must report such ids `Ineligible`, and plan-gated flows count as
  covered only once the seeder gains real plan seeding (`WorkerPlanGate.Hold` →
  `PlanApprovalService.Present` → approve, for a synthetic id);
- (c) `TryDiscard`/`TryReject` refuse unknown ids, and the seeder always `EnsureEntry`s first;
- (d) the service is `UNIMPLEMENTED` without the flag, and a coordinator token is
  `PermissionDenied` on every seeding method with it.

The proto reserves field numbers for the plan dimension (`with_plan`, `scope`) with comments
naming those branches, so the extension lands as fields filling reserved slots rather than a
redesign.

## 10. Honesty ledger

**Real:** every state transition (legality-checked), every persisted row, every audit event, the
branches/commits/diffs, gate arming and flag classification, the merge (actual ff-only under an
actual lease, actual cascade), the jail-liveness measurements, the new-commits invalidation.
**Synthetic:** the test-command *execution* (outcome supplied; provenance real and self-labelled),
the agent behind the id (absent, reported absent), and the client-side T-19 journal entry for a
seeded merge (absent; documented).
