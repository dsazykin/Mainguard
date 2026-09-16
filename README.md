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
- **Tamper-evident audit log** — a hash-chained, RFC-3161-anchored record of privileged actions, written by the daemon (`Mainguard.Git/Audit/`). SIEM export is still planned.
- **MainguardOS bootstrapper** — a lightweight background Linux VM (WSL2) gives agents native ext4 Docker performance while you keep a native Windows UI (no `/mnt/c` 9P latency, no Docker Desktop dependency). On **macOS** the daemon runs natively (`osx-arm64`) and sandboxes run through whichever Docker engine the machine has (Docker Desktop / OrbStack / Colima).
- **Dev-only merge-queue seeding** — a flag-gated testing capability that produces *legitimate* queue entries in any state (Working, Verifying-in-flight, Verified, StaleVerified, AwaitingReview, Merged, Rejected, Discarded, flagged, verify-FAIL) instantly — no container, no LLM tokens — by driving the real state-machine transitions with synthetic input. It exists so the merge-queue UI and its race windows (a merge landing mid-verification, the stale cascade, queue overflow) can be exercised reproducibly; a shipped build cannot reach it. See the technical section below.

### In final assembly — **the Alpha integration**
The pieces above are being wired into a single runnable control center — launch → spawn a real sandboxed agent → drive it → verify → review → merge. The real container spawn is validated in CI; the GUI surfaces are in live testing now.

### Planned — **the roadmap beyond Alpha**
A turnkey installer/OOBE, SIEM streaming of the audit log, an optional AI-reviewer pass, cross-worktree conflict radar, a production terminal engine, and **"Vibe Mode"** (a zero-terminal experience that auto-heals dev-server errors for non-developers). These are specified, not yet built.

---

## Architecture

A native Avalonia UI talks over gRPC to a **headless daemon** that owns everything privileged — sandboxes, the merge queue, verification, budgets, and audit. The UI never touches Docker directly. Agents live in per-repo persistent jails inside the MainguardOS VM; their worktrees are ext4-native, and the daemon is the only component permitted to reach a git host (via a read-only proxy). One design system drives four live-switchable color themes across the whole surface.

**Under the hood:** Avalonia 11 · `CommunityToolkit.Mvvm` · `LibGit2Sharp` · SQLite/EF Core · gRPC · Docker.

---

## Status

| Layer | State |
|---|---|
| Git client | **Stable** — usable today |
| Agent platform engines (sandbox, merge queue, cockpit, coordinator, gateway, terminal, bootstrapper) | **Built & tested** |
| End-to-end assembly (runnable swarm) | **In final integration** |
| Tamper-evident audit log | **Built & tested** |
| Turnkey installer, SIEM export, AI review, Vibe Mode | **Planned** |

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

- [`CONTRIBUTING.md`](CONTRIBUTING.md) — build, test, run. Start here to work on the code.
- [`AGENTS.md`](AGENTS.md) — architecture, the design system, conventions, and the hard review rules. The source of truth.
- [`docs/README.md`](docs/README.md) — **the documentation index**: what is in `docs/`, in what reading order, and where a new doc goes.
- [`docs/security-architecture.md`](docs/security-architecture.md) — the sandbox, egress, and merge-safety security model.
- [`docs/phase-2/`](docs/phase-2/) — the multi-agent platform design and the binding implementation spec.

Anything superseded lives in [`docs/archive/`](docs/archive/README.md) and is labelled as such — nothing in that folder describes current behaviour.

---

## For developers: merge-queue seeding (dev-only)

Testing the merge-queue UI used to mean spawning a real agent — a real container, sometimes a real
LLM burning real tokens — and waiting through a whole work→verify cycle just to get one entry into
one state. **Seeding produces any queue state instantly**, without faking anything the queue would
later have to be trusted about: every transition is the real production one, every seeded entry has
a real branch and a real commit, and the single synthetic fact (the pass/fail outcome) labels itself
`[seeded — not executed]` in its immutable record. It is flag-gated three ways and a shipped build
cannot reach it.

```bash
MAINGUARD_ENABLE_QUEUE_SEEDING=1 dotnet run --project Mainguard.Pro.App
```

A **"Queue seeding (dev)"** card then appears in the Control Center's right rail, with scenario
presets (*Stale pair*, *Merge during verify*, *Overflow ×12*, …). The same surface is scriptable over
gRPC via `QueueSeedingService`.

**[`docs/design/queue-seeding.md`](docs/design/queue-seeding.md) is the design of record** — the
honesty model, how it is wired, the three gates, the test suites that prove it never touches a jail,
and the known limitations. Read it before changing anything here.
