# GitLoom phase2 — Defect & Crux Catalog

**Source:** `github.com/dsazykin/GitLoom` @ `phase2` (commit `1a46f20`), shallow-cloned to `/workspace/gitloom-src`, 2026-07-20.
**Update (later same day):** the working clone advanced to `phase2` @ `d9d4f67` and the repo was rebranded **GitLoom → Mainguard** (split into "edition heads"). A net-new audit of that state is in **§9**. §1–§8 below describe the original `1a46f20` structure; the defects persist but paths have relocated under `Mainguard.*` (merge/orchestration → `Mainguard.Agents/Agents/Orchestrator/`, RiskClassifier → `Mainguard.Git/Review/`, daemon → `Mainguard.Server/`).
**Scope:** the multi-agent orchestration core — the "trustless verified merge queue," the verification/review gate, and the kill switch. ~7k C# LOC across `GitLoom.Core/Agents/Orchestrator/`, `GitLoom.Core/Review/`, `GitLoom.Core/Services/ForegroundMergeService.cs`.
**Method:** three parallel scout reads (merge-queue atomicity; verification trust boundary; spec/coverage map) + direct verification against the code. Everything below marked ✅ I read and confirmed myself; 🔶 was reported by a scout and not independently re-read.
**Purpose:** a complete record of *every* problem surfaced, viable-as-a-task or not. Task-viability is a per-item note, not a filter.

---

## Status legend

- **Present@HEAD** — a real wrong outcome is reachable in the current code (a latent bug).
- **Fragility** — behaves correctly today, but a *single* guard is the only thing preventing a wrong outcome in an untested window (a prime injection point).
- **Injection-site** — correct today; a small, natural-looking edit here would create a discovery-gated bug.
- **Seam** — a real fail-open/gap that depends on out-of-scope wiring to matter.
- **Coverage-gap** — a subtle invariant that is untested (sometimes unbuilt).
- **Negative** — checked and holds up; do not chase.

Harness-cost tags: **pure** (build model literals, call a method, assert — no git/container/async), **async-gated** (needs a controllable async runner to open an interleaving), **git-fixture** (needs a real git repo), **container** (needs Docker).

---

## 1. Confirmed latent defects (Present@HEAD)

### L1 — `RiskClassifier` scripts-block detector is JSON-string-blind ✅ · pure
`GitLoom.Core/Review/RiskClassifier.cs:102-172` (`TouchesScriptsBlock` / `BraceDelta:156`).
`BraceDelta` counts `{`/`}` across *all* characters, including inside JSON string values. A context line inside `"scripts"` whose string value carries an unbalanced `}` (e.g. `"greet": "echo hello}"`) drives `depth <= 0`, flips `insideScripts=false`, and every later ADDed line — including a `"postinstall": "curl … | sh"` — is skipped. The hunk then classifies `Lockfile`, which is **not** in `FlaggedChangeDetector.FlagWorthy` (`FlaggedChangeDetector.cs:22-28`), so the poisoned `package.json` merges with **zero must-acknowledge items**. Traced feed-through: `Classify` → `TopCategory` (`:150,164`) → the flagged-change gate. `FromLockfileDeltas` only inspects dependency rows, so nothing else catches package.json's own scripts.
- **Why tests miss it:** every fixture uses brace-balanced string values (`RiskClassifierTests`, `PoisonedBranchGateTests`). The `}`-in-a-string is agent-controlled file content the honest path never emits.
- **Blast radius:** defeats the product's stated "human diff review is the gate against hostile code" guarantee — a real security bypass.
- **Task-viability:** **high** (Track B standout). Realistic bug class (brace-counting instead of JSON-aware parsing), recognition-proof (bespoke logic), trivial to harness. Caveat: on its own it's a one-function fix → pose as *"harden the classifier so no scripts edit escapes the gate"* with several evasions (string-literal braces, escaped `\"`, nested objects, the `SectionHeading` seed) to carry difficulty past find-ability.

### L4 — External PR merge path has no freshness gate ✅ · pure
`GitLoom.Core/Agents/Orchestrator/MergeDispatch.cs:104-121` (`MergeExternalAsync`).
The local path merges via `ForegroundMergeService`, whose `git merge --ff-only` is an atomic ref-level CAS that refuses a stale merge. The external path calls `_prService.MergeAsync(...)` on the host with **no `CanMerge` check, no use of `request.ExpectedMainSha`** (carried in the request as "the A5 CAS old-OID," silently dropped), and **hardcodes `CasLost:false`**. A `pr-*` entry verified against `sha0` merges through the host even after main moved to `sha1`. Extra: if the entry is already `StaleVerified`, the host merge fires, then `ConfirmHumanMerge` hits `StaleVerified→Merged` (illegal, `MergeQueue.cs:168`) and **throws *after* the irreversible host merge**, leaving lease/queue inconsistent and the cascade unfired.
- **Why tests miss it:** `MergeDispatchTests` never moves main between verify and dispatch, so `Merged:true`/`sha-ext-1`/cascade all look right.
- **Blast radius:** the one path with **no CAS backstop** → a genuine stale merge. (Caveat: a caller may gate on `CanMerge` before dispatch; regardless, the dispatch has no *internal* freshness gate the way the local path does.)
- **Task-viability:** **high** (Track A). Pure in-memory; fix = add the missing `CanMerge`/CAS guard.

### L5 — Crash-reconcile evidence not correlated to the lease ✅ · git-fixture
`GitLoom.Core/Agents/Orchestrator/MergeReconcileTask.cs:75-88`.
Two sub-defects: (a) `hasMergeEntry = journal.GetHistory(repoPath).Any(e => e.Kind == "Merge")` matches *any* Merge entry in the last 100 ops, not one tied to `lease.AgentId`/`ExpectedMainSha`/`BeginUtc`. If main advanced for a non-lease reason during downtime (a direct push, a different branch's merge) and any old Merge entry exists, reconcile **synthesizes a confirm for the wrong agent** and fires `_onMerged(lease.AgentId, currentMain)` — a phantom merge, cascade mis-attributed. (b) `Confirm` runs before `_onMerged` (`:86-87`); a crash between them leaves the lease `Confirmed` (skipped by the next boot) while the cascade never fired → siblings never invalidated.
- **Why tests miss it:** `DaemonCrashMidMerge_*` use a fresh repo, one merge, main advanced *because of this merge*; no unrelated Merge entry, no crash between the two writes.
- **Task-viability:** **medium-high** (Track A), but the harness needs a real git repo (`RevParse` uses static `GitService.RunGit:100`) → heavier than the pure candidates. The correct fix (correlate the journal entry to the lease) is a strong *hard-when-stated* crux.

### L6 — `ChangedTestCommandGate` acks are per-agent, not content-bound ✅ · pure (wiring-dependent)
`GitLoom.Core/Agents/Orchestrator/VerificationRunner.cs:205-263`.
`SetFlagged`/`Acknowledge`/`Allows` key entirely on `agentId`; there is no content hash. Re-arming depends solely on the wiring calling `SetFlagged(agent, changed:true)` again after each resolve. Its correct sibling `AcknowledgmentStore` (used by `FlaggedChangeGate`) binds every ack to a content hash and auto-invalidates on any change (pinned by `PoisonedBranchGateTests.NewPush_AfterAck_ReArmsTheGate`). So if a branch changes the command to X (flagged, human acks X) then pushes a *different* weakening Y, the ack for X authorizes Y unless something re-invokes `SetFlagged` — the gate has no self-invalidation fallback.
- **Why tests miss it:** `ChangedTestCommand_ShouldBlockCanMergeUntilAcked` does a single change→ack→done; never a second, different change.
- **Task-viability:** **medium** — a clean *oracle-lies-via-wrong-sibling* shape (the right pattern is one class over). Wiring-dependent, so framing needs care to keep it non-bivalent.

### L7 — `KillSwitch.NotifyAuditStoreRecovered` drains gaps before appending ✅ · pure
`GitLoom.Core/Agents/Orchestrator/KillSwitch.cs:264-281`.
`gaps = _pendingAuditGaps.ToList(); _pendingAuditGaps.Clear();` runs, then `foreach … _audit.Append(...)` — the drain is not transactional with the append, and (unlike `TryAuditKill:311-329`, which re-queues on failure) it has no try/catch. If the store is still/again down on recovery (a flap, or a premature recovery signal), `Append` throws after the list is cleared and the tamper-evidence markers vanish permanently — the exact silent-absence RT-D3 exists to prevent.
- **Why tests miss it:** `KillSwitchDuringAuditOutage_ShouldMarkGapOnRecovery` sets `Down=false` before recovery, so the append always succeeds.
- **Task-viability:** **medium-secondary** — pure and deterministic, but peripheral to the merge guarantee (it's audit tamper-evidence). Good "second bug" or a standalone if you want an audit-integrity task.

---

## 2. Missing-invariant / fragility

### L2/L3a — The `CanMerge` stale-sha clause is the *sole* value gate in two untested windows ✅ · pure (L2) / async-gated (L3a)
`GitLoom.Core/Agents/Orchestrator/MergeQueue.cs:566-571` (the clause) + `:319-320` (cascade filter) + `:631-655` (`Hydrate`).
Freshness is enforced twice at runtime: the `NotifyMainMoved` state flip (`Verified/AwaitingReview → StaleVerified`) **and** the value clause `record.MainSha == _currentMainSha` at `:567`. Two windows bypass the *state* flip, leaving `:567` as the only guard:
- **L2 (restart):** `Hydrate` restores a persisted `Verified`/`AwaitingReview` row but never reconciles against the (possibly advanced) `currentMainSha` — no flip to `StaleVerified`. **pure**, deterministic (construct a second `MergeQueue` over the same stores at an advanced sha).
- **L3a (mid-`Verifying`):** a branch in `Verifying` when main moves is skipped by the `:319-320` filter, then completes to `Verified` stamped against the *old* sha (`:294-297`), and the cascade already ran without it. **async-gated** (needs a runner that captures the sha at verification *start*; the shipped synchronous test runner stamps `CurrentMainSha` at call time and can't build the window).
- **Status: Fragility, not a live bad-merge.** At HEAD the `:567` clause *does* return `CanMerge=false` in both windows — so today they only make a branch look mergeable *by state* while the value gate blocks it. Their value is as a **single-point-of-failure injection crux**: delete/weaken the `:567` clause and both windows (plus the external path L4, which has no value gate at all) leak. **Two independent scouts converged on this exact clause** — strong signal it's the load-bearing line.
- **Task-viability:** **high** (Track A standout). The multi-root framing — *"a branch verified against an old main must never merge through any path, or survive a restart"* — forces the agent to restore the guard across restart + external + interleave, where the shipped defense-in-depth hides the remaining holes (partial-fix trap / misleading localization; the `doit` archetype).

### F1 — Verification provenance decoupled from execution ✅ · pure
`GitLoom.Core/Agents/Orchestrator/VerificationRunner.cs:57-79`.
`RunAsync` executes `request.Command` but records `request.ResolvedCommand` + `request.ConfigHash` — three independent request fields, never cross-checked (no assertion `Command == Tokenize(ResolvedCommand)`; the tokenizer lives one class over in `VerificationCommandResolver`). A wiring change/refactor passing a shell-wrapped or pre-tokenized `Command` while keeping `ResolvedCommand="npm test"` yields a record that truthfully says "passed" about a command it misrepresents, and the RT-D2 drift gate then reasons over text decoupled from execution.
- **Status:** correct-looking at HEAD (the wiring builds consistent triples); this is a **missing invariant** → more injection-site than present bug.
- **Task-viability:** medium; the exploit needs a wiring change, so it's better as a "add the binding guard" injection than a find-the-bug.

---

## 3. Fail-open seams (real, wiring-dependent)

### S1 — `FlaggedChangeGate.Allows` fail-opens when no store exists ✅
`GitLoom.Core/Agents/Orchestrator/FlaggedChangeGate.cs:28-41`. Returns `true` if `_stores` has no entry for the agent. If the cockpit wiring never populated the agent's flagged set, the gate is a no-op.

### S2 — F6 scope check skipped for a plan-less managed worker ✅
`GitLoom.Core/Agents/Orchestrator/FlaggedChangeDetector.cs:71`. `applyScope = managed && approvedPlan is not null`; a managed worker with `approvedPlan == null` gets **no** out-of-scope enforcement (risk-category flags still fire). Fail-open on the F6 leg only.

*Both are real but depend on out-of-scope cockpit wiring to bite → seam risks, not self-contained cruxes.*

---

## 4. Injection sites (correct at HEAD; a natural edit yields a discovery-gated bug)

- **I1 — `NotifyMainMoved` filter drops `AwaitingReview`** (`MergeQueue.cs:319-320`). Removing `AwaitingReview` from the filter → a branch already opened for review isn't invalidated on a main move. Backstopped by the `:567` clause, so **liveness/UI-truth**, not a bad-merge. Good "second bug." Discovery-gated (tests only set up `Verified` branches). **pure.**
- **I2 — `_currentMainSha` assignment order** (`MergeQueue.cs:315`). Moving it *after* the `staleFifo` computation → `IsVerificationStaleLocked` compares against the old tip, flags nothing. **Weak** — existing FIFO/stale tests fail immediately (caught).
- **I3 — `ConfirmHumanMerge` ordering** (`MergeQueue.cs:368-383`). Sets `Merged` before `NotifyMainMoved` (correct: excludes itself from its own cascade). Reversing → `StaleVerified→Merged` throw. **Weak** — `Verified_To_AwaitingReview_To_Merged` + dispatch tests catch it.

---

## 5. Low-severity smells

- **D1 — `Hydrate` uses `Latest()` not the persisted `row.LastVerificationId`** (`MergeQueue.cs:620` written, `:650` read via `Latest`). The pinned pointer is written by `SaveRowLocked` and never read back. **Benign at HEAD** (reaching `Verified` always routes through `Verifying`, so `Latest` stays consistent with the state) — but a latent inconsistency worth noting.
- **D2 — Stale-cascade FIFO orders by last-verification time, not first-enqueue** (`MergeQueue.cs:322`). A re-verify overwrites `_verifiedAt`, so a second cascade reorders. Fairness/liveness only; untested.
- **D3 — Two `VerificationRecord` types, same name** — `GitLoom.Core.Agents.VerificationRecord` (`OrchestrationModels.cs:41`, `TestsPassed/TestsTotal` — a UI-mock DTO) vs `GitLoom.Core.Agents.Orchestrator.VerificationRecord` (`MergeQueue.cs:18`, `MainSha/ResolvedCommand/ConfigHash` — the real one). Both test files alias the Orchestrator one. A harness/compile footgun, not a runtime bug.
- **D4 — `GitMutationGuard` edge cases** 🔶 (not independently verified). Scout-reported: `IsDetachedHead` returns `false` on a missing HEAD; `ResolveGitDir` follows an agent-writable `.git` `gitdir:` pointer. Agent incentive is to force a keep-alive *skip* (DoS), not forge a pass → low severity.

---

## 6. Coverage gaps / unbuilt guarantees (spec + test map) 🔶

From the design docs (`docs/phase-2/GitLoom_Orchestration_RedTeam_Plan.md`, `GitLoom_Risk_Register.md`) cross-referenced against the test suite. Reported by the spec scout; test-absence partially confirmed via the test-file listing.

- **C1 — RT-5 concurrent merge linearization: UNTESTED.** No test fires two `BeginMerge`s concurrently and asserts "one commits, the second ABORTs + `StaleVerified`, duplicate `ConfirmMerge` returns the stored success." Only *sequential* merges are tested. (Buildable-feature; could be a crux.)
- **C2 — RT-6 mirror-vs-authoritative CAS: UNTESTED distinction.** Tests move `main` in the same repo the service reads, so they can't catch a "CAS read the lagging mirror" regression. Mostly a design-doc worry; abstract in this Linux code (there's no separate Windows-authoritative tip).
- **C3 — RT-9/RT-10 stale cascade to hibernated workers + dropped events: UNTESTED.** Backstopped by the `:567` re-derivation (the gate doesn't consume the cascade event), so likely **liveness**, not a safety hole.
- **C4 — RT-13 audit chain-ordering: UNTESTED *and* UNBUILT.** `IAuditLog` is documented as a plain ordered journal; the hash-chained tamper-evident impl is a later milestone. **Not bug-fix material** (unimplemented).
- **C5 — RT-2/RT-3 forged/replayed OOB `ControlFrame`: UNTESTED *and* largely UNBUILT.** No HMAC frame / `seq` / `session_epoch` logic; the yield handshake still rides a spoofable in-band marker. **Not bug-fix material.** Note: the daemon-verifies-quiescence backstop (`GitMutationGuard` + `KeepAliveRebaser`) *is* well-tested, so a forged marker can't mutate a tree.

---

## 7. Checked and holds up (negatives — don't chase)

- **Verdict source** = the container-runtime exit code; the runner never reads/references a supervisor frame (`VerificationRunner.cs:64-66`). Tight. ✅
- **`VerificationCommandResolver` drift** — `Normalize` only folds CRLF/trim, so `changed=false` requires near-byte-identical config; it can't hide a weakening. Tight. ✅
- **`ScopeMatcher` glob translation** (`**`, `**/`, `*`→`[^/]*`, `?`, bare-dir prefix) — correct against the pinned cases. ✅
- **CAS + crash-recovery happy path** — `ForegroundMergeService` (`--ff-only` CAS) and the `MergeReconcileTask` exactly-once path are **heavily pinned** (`A5Cas_Lost_WhenMainMoved_NoMerge`, `DaemonCrashMidMerge_CommittedButUnconfirmed_RecoversExactlyOnce`, `…_NeverCommitted_ReleasesLease_SurfacesAttempt`). A bug injected *here* is caught by `dotnet test` → **poor** crux target. ✅

---

## 8. Cross-cutting observations (for task design)

- **Shared root:** the *await-outside-the-lock* seam in `RunVerificationAsync` (`MergeQueue.cs:252-308`) underlies both L3a (stale-sha survives) and the L3b throw variant (new commits during verify → `Working→Verified` illegal-transition, swallowed in the cascade/resume `catch` → **silently lost passing verdict**, Present@HEAD).
- **Defense-in-depth is the difficulty lever *and* the trap:** state gate (`StaleVerified`) + value gate (`:567`) + local `--ff-only` CAS + stale cascade. This layering masks single-point breaks — exactly the *partial-fix trap / misleading-localization* material. L2/L4 only bite because they sit **outside** the `--ff-only` backstop (restart, external path).
- **Harness-cost tiers:** pure (L1, L2, L4, L6, L7, F1, S1/S2, I1) < async-gated (L3a) < git-fixture (L5) < container (the `RequiresDocker` tests). All sit behind the one unretired gate below.
- **The gate for the whole idea:** can a Triton pytest verifier compile and drive this C#/.NET code headless in the Docker harness (hidden xUnit project or a small scenario driver) in bounded time? Go/no-go, independent of crux. **Next step.**
- **Provenance/hygiene if this becomes a task:** no `LICENSE` file in the repo — it's the user's own project, so add a permissive license to any seed. Reskin the `GitLoom` naming and **strip the agent-guidance files** (`AGENTS.md`, `CLAUDE.md`, `GEMINI.md`, `CHATGPT.md`) so a trial agent can't find the public repo or be steered by them.

---

## Shortlist (the viable cruxes, ranked by fit × harness-ease)

1. **L1** — RiskClassifier string-blind scripts detector (security bypass; pure; needs multi-facet framing).
2. **L2/L3a** — the `:567` sole-guard, multi-root "stale branch must never merge / survive restart" (pure+async; richest depth; `doit` shape).
3. **L4** — external path missing CAS (pure; clean omission).
4. **L5** — reconcile lease-correlation (deep hard-when-stated; git-fixture).
5. **L6 / L7** — wrong-sibling ack gate / kill-switch audit drain (pure; good secondary bugs).

---

## 9. Net-new audit — phase2 @ `d9d4f67` (post GitLoom→Mainguard rebrand)

Repo updated `1a46f20` → `d9d4f67`. The rebrand is mechanical; **no merge-queue logic changed**, so §1–§8 defects persist (relocated under `Mainguard.*`).

**Reframing (✅ verified firsthand):** the "verified merge queue" exists only as tested library units — `new MergeQueue(...)` and `MergeQueueRegistry.Register(...)` appear ONLY in tests; `MergeAgentBranch`/`DispatchMergeAsync` have no non-test callers; the registry the gRPC service resolves from is never populated → **the merge path is NOT wired into the live daemon.** What IS live: coordinator spawn (in-jail shim), the kill-switch freeze, the Docker jail, sessions/terminals. So the strongest *Present@HEAD* defects are in the agent-platform, not the merge queue.

### Live agent-platform defects (Present@HEAD)
- **N1 / B1 (✅ verified) — coordinator spawn bypasses plan-approval + all caps.** `Mainguard.Server/Runtime/AgentSpawnService.cs:75-167,195-231`: `SpawnAsync` enforces only the kill-switch + non-empty kind; the coordinator's in-jail `mainguard-agent spawn <kind>` shim (`HandleShimRequestAsync`) calls it directly as `Managed`. Admission/worker-cap/budget/plan checks live in `CoordinatorTools` (never constructed outside tests). A jailed coordinator (or prompt-injection into it) loops the shim → unbounded managed workers, no approval, no cap. **Strongest net-new crux; a live "safe agent use" authority bypass.** `CoordinatorToolCapTests` is falsely reassuring (exercises `AdmissionController` directly, never the wired shim path).
- **N2 / B2 — CoordinatorIpcServer DoS.** `CoordinatorIpcServer.cs:151,164-165`: unbounded `ReadLineAsync` (no length cap) + unbounded concurrent connections on the jail-mounted socket → daemon OOM / task exhaustion from one agent.
- **N3 / B3 — kill switch is label-only + spawn race.** Wired `IKillTarget` (`SessionStoreKillTarget.cs:28-39`) never `docker pause`s (relabels only); `SwarmReconciler` default `Adopt` re-adopts a "killed" container on boot. A spawn passing its freeze-check before `_store.Spawn` registers *after* the fan-out snapshot → escapes the kill.
- **N4 / B4 — budgets/rate-limits/admission inert on live traffic.** Nothing feeds `BudgetLedger.Record` (spend writer lives in the unmounted proxy middleware); `AdmissionController` has no production caller. Operator-set caps enforce nothing.
- **N5 / C2 — persistent-jail reuse never re-injects `/run/secrets`.** `DockerSandboxEngine.cs:53-58` returns on the reuse branch before `WriteSecretFileAsync`; after stop→restart the agent-cred/OOB-key tmpfs files are empty (availability, not disclosure).

### Merge-path defects that would bite when wired (architecture / Fragility)
- **N6 / A1 — the human-review flagged-change gate has NO daemon-side enforcement.** `MergeQueueGrpcService.BeginMerge/ConfirmMerge` never consult `CanMerge`/gates; the gate is `new`'d UI-side; the ack path is `=> Task.CompletedTask` (no ack RPC exists). The "diff review is the gate against hostile code" guarantee is structurally client-side — contradicts their own `RoleInterceptor` "never UI-only" rule.
- **N7 / A2 — `ConfirmMerge` trusts a client-supplied post-merge SHA** and the UI passes the *pre*-merge tip, so the stale cascade fires on the wrong/old sha. `MergeQueueGrpcService.cs:111-112`.
- **N8 / A4 — RT-D1 reconcile wired but inert** (`resolveRepoPath: _ => null`, `onMerged` iterates `Array.Empty`) — a live injection-seam for whoever wires the active-handle set. `GatewayServiceRegistration.cs:115-128`.
- **N9 / A3 — StreamQueue reports `VerifiedMainSha = CurrentMainSha` for every entry** (UI-truth, low). `MergeQueueGrpcService.cs:165-170`.

### Other (Fragility / unmounted / low)
- **N10 / B7 — coordinator-role gRPC authz is inert dead code; its tests pass for the wrong reason** (bearer rejects the fake token first); `ConnectionRoleRegistry.Resolve` fails **open** to `Operator` for unknown tokens. Fails-closed today (single-principal) but regression-blind.
- **N11 / B5 — `GatewayForwarder` under-meters via attacker-controlled token estimate on streamed responses** (unmounted proxy; primary budget-evasion vector if mounted).
- **N12 / B6 — external PR intake trusts the author string + collides on `pr-<n>` across sources** (intake inert: `resolveTarget => null`).
- **N13 / C1 — host path traversal via unsanitized `repoHash`/`agentId` in `WorktreeManager.Path.Combine`** — client/operator-token-reachable, NOT agent-reachable (defense-in-depth).
- **N14 / B8 — plan approver identity is the daemon's own euid (constant); `SO_PEERCRED` never read** (documented residual).
- **N15 / C3 — userns remap never set** by the spec (relies on daemon default). **N16 / C4 smells:** `CAP_DAC_OVERRIDE` in the add-back set; seccomp re-assertion is substring-presence; egress wildcard renders as a literal anchored regex (fails closed); `SwarmReconciler.ToDictionary` throws on a dup label (boot DoS); `SessionLeader.Reattach` OR-match reattaches a stale session.

**Verified holds-up (don't re-chase):** the Docker jail is genuinely hardened + LIVE (CapDrop ALL, no-new-privileges, non-unconfined seccomp default-deny incl. `ptrace`, readonly rootfs, non-root user, no docker-socket/host mounts, ext4-only, secrets on 0400 tmpfs never in Env); egress is network-enforced (internal network + default-deny proxy + dnsmasq NXDOMAIN + iptables), not env-advisory; `K` confidentiality solid; plan approval TOCTOU-safe; AcknowledgmentStore content-bound; `--ff-only` CAS + exactly-once reconcile *logic* pinned; bearer auth constant-time, loopback-only, single-principal.

**Verification status:** the reframing (not-wired) and N1/B1 confirmed firsthand against `d9d4f67`; N6/A1's premise (empty registry) corroborated; the remainder are auditor-reported (thorough, file:line-cited) but not each line-verified.
