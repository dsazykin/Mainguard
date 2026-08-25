# Mainguard

**Safe, autonomous multi-agent coding — on your own machine.**

Mainguard is a native, high-performance Git GUI that doubles as a **control center for a swarm of AI coding agents**. Run several autonomous agents at once, each jailed in its own hardened sandbox on its own branch, and merge their work only after it has been **verified against your current `main` and reviewed by you**. You stop being the person typing every line and become the engineering manager approving what ships.

Built on **.NET 10 · Avalonia 11 · LibGit2Sharp · SQLite/EF Core**.

---

## The problem

Modern agentic CLIs (Claude Code, Codex, Jules, OpenCode, …) are excellent at building whole features — but running *several* of them autonomously is a mess:

- They **step on each other** and on your uncommitted work, and collide on `.git/index.lock`.
- They edit your working directory live, so one bad run **breaks your environment**.
- You **can't trust their output** enough to merge it blindly — and reviewing N branches by hand across N terminals doesn't scale.

Managing a swarm in split terminals turns into babysitting. Mainguard exists to make it *safe and orchestrated* instead.

---

## The core idea: a trustless, verified merge queue

Mainguard's thesis — and its moat — is **"safe-to-merge."** Agents don't push to `main`; they land in a merge queue that guarantees what merges is actually sound:

- **It runs your own verification** (build + tests) inside the agent's isolated container, and the verdict is the **container's real exit code** — read by the trusted daemon from the container runtime, *outside* the container. An agent **cannot forge a "passed"** by printing success.
- **It's always verified against *current* `main`.** When any branch merges, every other verified branch is invalidated and **re-verified against the new `main`** before it's eligible — no "green when I opened it, but main moved 20 commits" gap.
- **It's provenance-pinned:** the resolved test command + config are recorded, so an agent can't sneak through by weakening what "verify" means.
- **It never auto-merges.** Verification makes a branch *eligible*; **you** approve it in a risk-ranked review cockpit, and the merge itself is an atomic compare-and-swap so racing agents can't slip a stale merge in.

The guarantees are the ones you specifically need *because an AI wrote the code and several agents are racing to merge at once.*

---

## What's built

Mainguard started as a polished Git client and has grown the agent platform underneath it. Status is marked honestly.

### The Git client — **shipped & stable**
A blazing-fast, natively rendered client that stands on its own:
- **Commit history & graph** — an isolated DAG lane-routing engine on a virtualized vector canvas, 60 FPS on complex histories.
- **Staging, diffs & committing** — side-by-side and unified diffs, **hunk- and line-level partial staging** on a pure patch engine validated against `git apply`, push/pull with ahead/behind tracking.
- **Conflict resolution** — a synchronized 3-pane merge editor (Ours | Result | Theirs) that merge/rebase/cherry-pick/pull all route into.
- **Branches, tags & worktrees** — checkout-safety validation, full tag lifecycle, git-worktree porcelain.
- **Four switchable themes** — a tokenized design system: Midnight Loom (default), Daylight Loom (light), Graphite (macOS-native neutral), Atelier.

### The agent platform — **engines built & tested**
Each of these is implemented behind a clean interface and covered by tests (including real-Docker tests in CI):
- **Hardened agent sandboxes** — every agent runs in a locked-down container (no-new-privileges, seccomp, dropped caps, read-only rootfs, user-namespaced) with a **default-deny egress proxy**: model APIs and package registries are reachable; **the git host is not** — so an agent can't clone/exfiltrate. Toolchains are pre-baked, so nothing fetches at runtime.
- **The verified merge queue** — the safe-merge engine described above (stale invalidation, daemon-observed verification, exactly-once atomic merge).
- **Risk-ranked review cockpit** — per-hunk provenance, a flagged-changes acknowledgement gate, branch-vs-`main` diffs.
- **Coordinator + plan approval** — a coordinator agent decomposes work into a structured **plan you approve before any worker spawns**; the approver identity is derived by the daemon (not client-supplied), and managed workers' terminals are locked at the gRPC layer.
- **Always-visible kill switch** — freezes the merge queue *first*, then pauses every agent, with a hard timeout ceiling that a compromised worker can't stretch.
- **AI gateway** — BYOK keys via the OS keyring; per-agent and per-day token/cost budgets, rate-limit backoff, admission control.
- **External PR intake** — subscribe bot-authored PRs (Codex/Jules/Copilot) into the same verify→review→merge pipeline.
- **Native terminals** — real OS pseudo-terminals (ConPTY/forkpty) rendered with Skia, so interactive CLIs and fast logs work without dropped keystrokes.
- **MainguardOS bootstrapper** — a lightweight background Linux VM (WSL2) gives agents native ext4 Docker performance while you keep a native Windows UI (no `/mnt/c` 9P latency, no Docker Desktop dependency). On **macOS** the daemon runs natively (`osx-arm64`) and sandboxes run through whichever Docker engine the machine has (Docker Desktop / OrbStack / Colima).
- **Dev-only merge-queue seeding** — a flag-gated testing capability that produces *legitimate* queue entries in any state (Working, Verifying-in-flight, Verified, StaleVerified, AwaitingReview, Merged, Rejected, Discarded, flagged, verify-FAIL) instantly — no container, no LLM tokens — by driving the real state-machine transitions with synthetic input. It exists so the merge-queue UI and its race windows (a merge landing mid-verification, the stale cascade, queue overflow) can be exercised reproducibly; a shipped build cannot reach it. See the technical section below.

### In final assembly — **the Alpha integration**
The pieces above are being wired into a single runnable control center — launch → spawn a real sandboxed agent → drive it → verify → review → merge. The real container spawn is validated in CI; the GUI surfaces are in live testing now.

### Planned — **the roadmap beyond Alpha**
A turnkey installer/OOBE, a tamper-evident audit trail + SIEM streaming, an optional AI-reviewer pass, cross-worktree conflict radar, a production terminal engine, and **"Vibe Mode"** (a zero-terminal experience that auto-heals dev-server errors for non-developers). These are specified, not yet built.

---

## Architecture

A native Avalonia UI talks over gRPC to a **headless daemon** that owns everything privileged — sandboxes, the merge queue, verification, budgets, and audit. The UI never touches Docker directly. Agents live in per-repo persistent jails inside the MainguardOS VM; their worktrees are ext4-native, and the daemon is the only component permitted to reach a git host (via a read-only proxy). One design system drives five live-switchable color themes across the whole surface.

**Under the hood:** Avalonia 11 · `CommunityToolkit.Mvvm` · `LibGit2Sharp` · SQLite/EF Core · gRPC · Docker.

---

## Status

| Layer | State |
|---|---|
| Git client | **Stable** — usable today |
| Agent platform engines (sandbox, merge queue, cockpit, coordinator, gateway, terminal, bootstrapper) | **Built & tested** |
| End-to-end assembly (runnable swarm) | **In final integration** |
| Turnkey installer, audit/SIEM, AI review, Vibe Mode | **Planned** |

Mainguard is in active development — the foundation is real and tested; the fully packaged, one-click product is on the way.

---

## Getting started (developer preview)

Requires the **.NET 10 SDK** (pinned via `global.json`, so `dotnet` picks the right toolchain automatically).

```bash
git clone <this repo>
cd Mainguard
dotnet restore
dotnet build                              # build the whole solution
dotnet run --project Mainguard.Client.App # the free Git-client head
dotnet run --project Mainguard.Pro.App    # the Pro head (agent platform)
```

The app ships as **two edition heads over one shared shell library** — you run a head, never the
shell (`Mainguard.App.Shell` is a library with no entry point).

Or open `Mainguard.slnx` in Visual Studio / Rider.

### Containerized build & test (optional)

A Docker image reproduces the exact .NET 10 build/test toolchain (plus native `LibGit2Sharp`/`SkiaSharp` deps) so builds and tests run identically anywhere — **for building, testing, and EF migrations only, not the GUI**:

```bash
docker compose run --rm build     # restore + build the solution
docker compose run --rm test      # run all test suites headlessly
docker compose run --rm shell     # interactive toolchain shell (e.g. dotnet ef ...)
```

---

## Documentation

- [`AGENTS.md`](AGENTS.md) — architecture, the design system, conventions, and the current repository map (kept in sync with the code).
- [`docs/security-architecture.md`](docs/security-architecture.md) — the sandbox, egress, and merge-safety security model.
- [`docs/phase-2/`](docs/phase-2/) — the multi-agent platform design and implementation plans.

---

## For developers: merge-queue seeding (dev-only)

The design of record is [`docs/design/queue-seeding.md`](docs/design/queue-seeding.md); this is the
working summary. Testing the merge-queue UI used to mean spawning a real agent — a real container,
sometimes a real LLM burning real tokens — and waiting through a real work→verify cycle just to get
one entry into one state. Seeding produces any queue state instantly, and it does it **without
faking anything the queue would later have to be trusted about**.

### The honesty model — why this isn't "test data"

The merge queue is a trust boundary, so the seeder is built around one rule: **nothing is
fabricated.** No synthetic row is ever written to the store; no state is asserted that the state
machine did not transition into.

- **Every transition is the real one.** The seeder only calls the same public `MergeQueue` methods
  production calls — `EnsureEntry`, `RunVerificationAsync`, `RequestReview`, `TryReject`,
  `TryDiscard`, and for `Merged` the full lease walk (`TryBegin` → a real `git merge --ff-only` in
  the origin checkout → `TryConfirmHumanMerge` under the queue lock → `Confirm` → mirror refresh).
  Every legality check, persisted row, gate evaluation, and audit event is the production code path.
- **Every entry has real git data.** A seeded entry's branch (`refs/heads/agent/seed-<n>`) is a real
  commit fabricated with git plumbing on top of the mirror's `main`, so diffs, review, bring-local
  and the merge itself all work. The commit's *content* is chosen per flavor so gate classification
  is real, never injected: `PLAIN` (inert file), `FLAGGED` (touches a CI workflow file → the real
  risk classifier flags it), `CHANGED_TEST_COMMAND` (drifts `.mainguard/verify` → the real RT-D2
  gate arms).
- **The one synthetic fact labels itself.** Verification pass/fail is normally the container's real
  exit code — a value-supplied "pass" is exactly the forgery the architecture exists to prevent. A
  seeded outcome is value-supplied by definition, so its immutable record says so: the resolved
  command carries a `[seeded — not executed]` marker and the log artifact states no run happened.
  A seeded record can never masquerade as evidence that tests ran; it is evidence that a transition
  legally occurred — which is exactly what UI testing needs.
- **Main only ever moves for real.** Seeding `Merged` performs the same ff-only merge in the origin
  checkout the GUI's Merge button performs; seeding `StaleVerified` makes a real out-of-band commit
  on origin `main`. The real stale cascade then fires over the whole queue. This is why seeding is
  a **scratch-repo tool** — the daemon says so loudly at boot.

### How it's wired

- `SyntheticVerificationRegistry` (`Mainguard.Agents/Agents/Orchestrator/`) maps a seeded id to a
  plan `{passed, holdSeconds, staleBehavior}`. `MergeQueueProvisioner` consults it inside its
  verification path: a registered id skips only the *jail half* (container resolve, publish,
  toolchain probe, sandboxed run) while the *mirror-read half* (RT-D2 provenance, gate arming, the
  flagged-change review) still executes for real. Unregistered ids — every id a shipped daemon ever
  sees — take the path untouched. The registry is registered unconditionally and stays empty unless
  the flag-gated RPC populates it; ids without the `seed-` prefix are refused at registration.
- `QueueSeeder` (same folder) owns the state walks, the plumbing commits, the origin-checkout
  merges, `PushCommits` (append a real commit to a seeded branch and drive the real
  `NotifyNewCommits` invalidation — the "commits arriving from several places at once" primitive),
  and `ClearAsync`, whose ordering is load-bearing: an entry is made terminal (or its in-flight
  hold drained) *before* `Cancel` deletes the row, because a run settling after the delete would
  re-mint the row.
- **Holds make races reproducible.** A `Verifying` seed keeps its synthetic run genuinely in flight
  for up to 600 s — the in-flight set, the wire's `verification_in_flight`, and
  `ClearStalledVerification`'s "wait" refusal all measure a real await. Hold one entry mid-run,
  merge a sibling, and you have deterministically constructed the merge-lands-during-verification
  window that real agents only hit by accident.

### Enabling and using it

```bash
MAINGUARD_ENABLE_QUEUE_SEEDING=1 dotnet run --project Mainguard.Pro.App
```

The flag is captured **once at daemon startup** (an app-spawned daemon inherits it from the app's
environment). Three gates stand between a shipped build and this surface:

1. **Unmapped service (primary):** without the boot flag the daemon never maps
   `QueueSeedingService` — every call answers `UNIMPLEMENTED`, which is also how the Pro app's dev
   panel decides to stay hidden (absent, not disabled).
2. **`SeedingGateInterceptor` (belt):** prefix-denies the service when disabled, so an accidental
   unconditional mapping still refuses loudly.
3. **Coordinator-denied (unconditional):** every seeding method is on
   `RoleInterceptor.CoordinatorDeniedMethods` — seeding composes entry-creation + a supplied
   verification outcome + merge power, none of which an agent credential may hold. Seeding is also
   never exposed as an in-jail IPC op.

With the flag up, a **"Queue seeding (dev)"** card appears in the Control Center's right rail:
state/flavor/count pickers, a verify-fails toggle, a hold duration, and scenario presets that are
client-side compositions of the RPC primitives — *Stale pair* (`[Verified, Merged]`; the merge is
real and its cascade stales the co-seed), *One of each*, *Overflow ×12*, *Merge during verify*,
*Push commits*, and *Clear seeded* (structurally scoped to `seed-` ids). Every refusal renders the
daemon's words verbatim. The same surface is scriptable over gRPC:
`QueueSeedingService.SeedQueueEntries / PushCommits / ClearSeededEntries / GetSeedingStatus`
(`Mainguard.Protos/protos/mainguard/v1/queueseeding.proto`).

Every seeded entry appends a `queue_entry_seeded` audit event with the daemon-derived actor, and
seeded entries honestly have no sandbox — the jail reconciler marks them stranded within ~30 s,
which exercises the jail-less rendering path for free.

### Testing it (and testing with it)

```bash
dotnet test Mainguard.Tests        --filter "FullyQualifiedName~QueueSeederTests"
dotnet test Mainguard.Server.Tests --filter "FullyQualifiedName~Seeding"
```

- **`QueueSeederTests`** run the seeder over a real bare mirror + real origin checkout with a
  sandbox engine that **throws on any use** and a container resolver that answers null — the suite
  passing is itself the structural proof that seeding never touches a jail. The record half of the
  honesty rule (the visible `[seeded — not executed]` provenance) is asserted directly.
- **`QueueSeedingRpcTests`** drive the whole surface end-to-end over a seeding-enabled in-proc
  daemon (the real composition root): provision → seed → assert what the *ordinary* merge-queue
  RPCs then serve, the over-the-wire stale pair really advancing origin `main`, and the typed
  refusals.
- **`SeedingCompatibilityTests`** are the forward contract. They pin the properties seeding relies
  on — both merge-gate defaults (including `FlaggedChangeGate`'s default-DENY, which is what makes
  the mirror-read half load-bearing), the unknown-id refusals, and the boot-flag wire behavior —
  and carry a **tripwire that fails the moment the coordinator phase-2/3 branches land** (worker
  plan gate / auto-verify trigger). The failure message tells the merger exactly what to do: extend
  the seeder with real plan seeding via `SeedEntrySpec`'s reserved proto fields 8/9, re-pin the new
  properties directly, and only then remove the tripwire. The seeder is not allowed to silently
  stop covering what the queue grows.
- **In-proc test hosts** enable seeding via `new DaemonFixture { EnableQueueSeeding = true }` — a
  DI singleton replacement, deliberately: a process-wide env var can't differ between two test
  daemons in one process, and a `UseSetting` configuration key measurably never reaches
  `builder.Configuration` during the daemon's `ConfigureServices` under the minimal-hosting test
  factory.

Known limitations (by design, documented in the design doc): a held `Verifying` row does not
survive a daemon restart as a hold (the restart resume strands it to `Working` — real behavior,
and incidentally the recipe for reproducing the "stuck Verifying" shape); a seeded merge writes no
client-side undo-journal entry; and the auto-provisioned `.mainguard/verify` means a repo whose
real state is "no verification command" can't be seeded *as that repo*.
