# Product

<!-- impeccable:product-schema 1 -->

## Platform

desktop

The primary surface is a native Avalonia desktop application shipped as two exe heads — the free
`Mainguard.Client.App` and `Mainguard.Pro.App` — over one shared shell library. Windows is the
primary target (WSL2 substrate); macOS is shipped (native `osx-arm64` daemon). Linux is a
*substrate* the agent sandboxes run on, not a desktop product: there is no Linux GUI packaging lane.

One web surface is also in scope for this record: **mainguard.dev**, the React/Vite marketing site
in `site/`, deployed to GitHub Pages with a Cloudflare Worker + D1 backend for the waitlist and
contact form. It shares this file's product truth; its visual and voice register is a per-surface
concern, not a field here.

(`desktop` is not one of the four values this schema names — none of `web`, `ios`, `android`, or
`adaptive` describes an Avalonia desktop app, and recording `web` would route future work through
browser assumptions that do not apply.)

## Users

Adoption runs as a deliberate three-step ladder (`docs/business/go-to-market/Mainguard_GTM_Plan_2026-07.md`
§3.2), not a single persona:

- **Sam — the agent power user.** Today's actual user. Already runs several agentic CLIs (Claude
  Code, Codex, Gemini CLI, Qwen Code, OpenCode) against one repository and is losing time to them
  colliding with each other and with his own uncommitted work. High-focus keyboard-and-mouse
  desktop work, frequently on operations where a mistake costs work: rebase, force-push, discard,
  conflict resolution. Converts $0 → the paid tier personally.
- **Dana — the Windows/.NET enterprise developer.** Adopts the free client individually; her
  employer is the one who eventually pays. She is the reason the product is Windows-first in a
  Mac-first category.
- **Priya — the engineering manager.** The *buyer* at the team tier, reached through governance,
  audit, and policy rather than through the editor. **Standing rule: do not sell to Priya before
  the governance features actually exist.**

**ICP:** a 10–100-developer product company or agency, Windows-heavy or mixed-OS, already running
agentic CLIs, under some compliance pressure. Netherlands / Twente first
(`docs/business/go-to-market/sales/Target_Company_List_2026-08.md`).

**Explicit non-targets:** non-developer "vibe coders" (a later Cloud-phase audience), cloud-only
agent shops, and free-forever open-source users. The strategic law behind the ladder is that
individual developers do not pay; the free Git client is the trust wedge and the funnel, not the
business.

## Product Purpose

Mainguard is a native, high-performance Git GUI that doubles as a control center for a swarm of
autonomous coding agents running on the user's own machine. Several agents run at once, each jailed
in its own hardened container on its own branch, and their work reaches `main` only after it has
been verified against the current `main` and approved by the human.

The user stops being the person typing every line and becomes the person approving what ships,
without giving up control of their working directory.

**Success today:** the full cycle — spawn a sandboxed agent, drive it, verify it, review it, merge
it — runs end to end and is trustworthy enough that a developer actually merges agent work through
it. Alongside that, ordinary git operations are faster and less error-prone than the CLI or existing
GUIs, with none of the `.git/index.lock` collisions the product exists to prevent.

**Success later:** one human comfortably supervising several agents from a single screen, with the
evidence to show what was merged and why.

## Positioning

The differentiating claim is **safe-to-merge**, not orchestration. The market research is blunt that
the orchestration framing is dead (`docs/business/market-analysis/Mainguard_Viability_And_Differentiation_2026-07.md`
§1.1): *"'Mainguard spawns agents in isolated worktrees' is not a product,"* and the original
headline *"is no longer viable as a differentiator — that window closed in early 2026."* Viability
is conditional on owning the downstream step: verifying, governing, and merging.

The line of record (GTM Plan §2.1): *"Agent CLIs made it trivial to produce ten branches an hour.
Nothing on the market makes it safe to merge them. Mainguard is where agent work becomes trustworthy
commits on main."* The site says it shorter: *"Agents do the work. The guard holds main."*

**The mechanism a neighboring product could not truthfully copy** — each part is implemented, not
aspirational:

- The verification verdict is **the container's real exit code, read by the trusted daemon from the
  container runtime, outside the container**. An agent cannot forge a pass by printing success.
- A branch is always verified against **current** `main`: when anything merges, every other verified
  branch is invalidated and re-verified before it is eligible again. There is no "green when I
  opened it, but main moved" gap.
- Verification is **provenance-pinned** — the resolved test command and config hash are recorded
  immutably, so an agent cannot slip through by weakening what "verify" means.
- It **never auto-merges.** Verification only makes a branch eligible; the human approves in a
  risk-ranked review cockpit, and the merge is an atomic compare-and-swap so racing agents cannot
  land a stale merge.

**Two secondary axes:** Windows-first in a category where the credible tools are Mac-first, and
agent-vendor-neutral (five pinned CLI adapters rather than a bet on one vendor).

**Competitive frame** (research dated 2026-07): GitKraken is the incumbent with distribution but is
Electron, works in host worktrees, and has no sandbox or verification; Conductor is Mac-only and its
monetization is unsolved; Fork and Tower stop at AI commit messages; Sculptor (Imbue) is the closest
on containers plus verification; CodeRabbit and Greptile are cloud review with no local cockpit.

**Never lead with** "swarm", "50 agents", or "orchestration" — a hard rule in the GTM plan.

## Operating Context

- **Two processes, one privilege boundary.** A native Avalonia UI talks over gRPC to a headless
  daemon (`mainguardd`) that owns everything privileged: containers, the merge queue, verification,
  budgets, and audit. The UI never touches Docker directly. Twelve gRPC services define the surface.
- **Where agents live.** Per-repo persistent jails with ext4-native worktrees. On Windows these run
  inside MainguardOS, a lightweight background WSL2 Linux VM that avoids `/mnt/c` 9P latency and any
  Docker Desktop dependency; on macOS the daemon runs natively under launchd and sandboxes use
  whichever engine is present (Docker Desktop, OrbStack, or Colima).
- **The egress posture is part of the product.** Default-deny: model APIs and package registries are
  reachable from a jail; the git host is not — so an agent cannot clone or exfiltrate. The daemon is
  the only component permitted to reach a git host, through a read-only proxy. Toolchains are
  pre-baked so nothing fetches at runtime.
- **The user's loop.** Spawn an agent → a coordinator decomposes the work into a plan the human
  approves before any worker spawns → workers work in jails → verification runs in the worker's own
  sandbox → the review cockpit ranks risk and surfaces per-hunk provenance → the human merges. An
  always-visible stop control freezes the queue first, then pauses every agent.
- **Beyond the app.** mainguard.dev carries the public story and a working waitlist. Everything else
  written for launch — press kit, email sequences, Show HN posts — is deliberately held in reserve
  until the end-to-end run works (`docs/business/go-to-market/GTM_Execution.md` §3b: *"Nothing is sold."*).

## Capabilities and Constraints

**The Git client — shipped and stable.** Commit history with a DAG lane-routing engine on a
virtualized vector canvas at 60 FPS; staging, side-by-side and unified diffs, hunk- and line-level
partial staging on a patch engine validated against `git apply`; a synchronized three-pane conflict
editor that merge, rebase, cherry-pick, and pull all route into; branches, tags, and worktrees with
checkout-safety validation; four switchable themes.

**The agent platform — built and tested.** This is the section the previous version of this file got
wrong; these are implemented and covered by tests, including real-Docker tests in CI:

- Five version- and sha256-pinned agent CLI adapters (`claude-code`, `codex`, `gemini-cli`,
  `qwen-code`, `opencode`) with strict-schema manifests.
- Hardened sandboxes: no-new-privileges, seccomp, dropped capabilities, read-only rootfs, user
  namespaces, jail limits, reaping, and the default-deny egress proxy above.
- The verified merge queue: daemon-observed verification, the stale-invalidation cascade, a legal
  transition table, conflict parking, and an exactly-once atomic merge with no auto-merge path.
- The risk-ranked review cockpit with per-hunk provenance and a flagged-change acknowledgement gate.
- Coordinator with role separation and a blocking plan-approval gate — **phases 1, 2, and 3 are all
  merged** (worker-authored plans with a revision cap; the coordinator's tool surface locked to a
  four-op contract disjoint from a worker's).
- The stop-all kill switch: freezes the queue first, then pauses agents, under a hard 30-second
  ceiling a compromised worker cannot stretch.
- AI gateway with BYOK keys in the OS keyring, per-agent and per-day token/cost budgets, rate-limit
  backoff, and admission control.
- External PR intake, so bot-authored PRs (Codex, Jules, Copilot) enter the same
  verify → review → merge pipeline.
- Real OS pseudo-terminals (ConPTY / forkpty).
- A **hash-chained, tamper-evident audit log** with typed events, retention, RFC-3161 anchoring, and
  a verification CLI. (README still lists this as planned; it is not.)
- The MainguardOS bootstrapper, Windows OOBE, installer, and uninstaller.
- A dev-only, flag-gated merge-queue seeder that produces any queue state by driving real state
  transitions, behind three independent gates a shipped build cannot pass.

**Partial or in-flight.**

- **End-to-end Alpha assembly** is the live milestone: the pieces are being wired into one runnable
  control center. Recent work (PRs #359–#372) has been a security-audit remediation program rather
  than new features.
- The libvterm terminal grid engine exists but is **flag-gated off**; the interim engine is the
  default pending parity sign-off.
- Kill-switch round-trip time is **not measured** — reports honestly stamp it unknown.
- macOS OOBE is partial; launch currently routes straight to the control center.
- Vibe Mode has a view model and a view but thin evidence of a working end-to-end path.

**Planned only — do not describe as existing.** SIEM streaming, an optional AI-reviewer pass,
cross-worktree conflict radar, a production terminal engine, Vibe Mode as a shipped experience, and
any Linux desktop packaging.

**Editions.** Three products are an accepted decision (`docs/adr/0001-product-editions.md`,
2026-07-19): the free **Client**, **Pro** (the local agent platform), and **Cloud** as a separate
later head. Client and Pro are editions composed on one trunk via `IEditionManifest` — not branches,
not `#if` — and the free client's dependency closure physically excludes the agent platform, which
CI enforces. Both heads are at v0.2.8.

**Nothing is sold today.** No licensing, activation, entitlement, trial, or billing code exists
anywhere in the app, agent, server, or installer sources; the edition split is packaging only. A
price list and a founding-user program are drafted but nothing has been charged and the terms are
not settled. Code signing is a hook awaiting a real certificate. Licensing terms are deliberately
left unrecorded here.

**Known constraints.** All Docker and sandbox evidence is Linux-engine only — no hosted runner
reproduces WSL2 Docker. The Windows test leg is advisory with a known-failure backlog.

**Terminology.** *MainguardOS* is the internal name of the WSL2 VM payload, not a consumer brand.
*mainguardd* is the daemon.

## Brand Commitments

- **Name: Mainguard**, renamed from GitLoom on 2026-07-16 as a deliberate trademark clean break.
  Domain: mainguard.dev. Trademark clearance (Nice classes 9 and 42) is still owed, and rebrand
  phases 1–5 have not started.
- **Character: premium and precise.** A high-craft instrument for serious engineering work —
  controlled, confident, engineered — never a hobby project or a themed wrapper around a web view.
  When the stakes are a human's working directory, nothing may read as loose or ambiguous.
- **The guard metaphor is binding.** Agents work outside the walls; nothing reaches `main` without
  passing inspection. Play it as calm, disciplined protection — night watch, honor guard, lighthouse
  keeper. **Never** carceral or militaristic, and never through security-vendor clichés: no shields,
  no padlocks, no fortresses.
- **The specific guard vocabulary is *not* binding** (decided 2026-09-14). Earlier drafts fixed the
  terms "the Gate", "cleared"/"turned back", "stand down", and "the watchtower view"; none were ever
  adopted in the product, whose shipped copy is plain (the kill switch reads "Stop all"). The
  metaphor governs; the literal terms do not, and the plain language is the real voice.
- **Voice rules V-1…V-8** (`docs/creative/Mainguard_Voice_And_Delight_Bible.md`) are binding for
  product copy: precise and calm, no exclamation marks, no mascots or emoji, no "we", no "oops",
  destructive-safety forward, sentence case, never "please" or "sorry". Warmth and emoji are allowed
  only in the external brand register (`docs/creative/Narrative.md`).
- **Logo:** an M drawn as a gatehouse. It exists **only as an interim programmatic SVG**
  (`site/src/components/Wordmark.tsx`, `site/public/favicon.svg`); a crafted logo has not been made.
- **Known naming drift to resolve, not to propagate:** the site ships five theme names (Midnight
  Watch, Day Watch, Command Deck, Atelier, Aurora) while the app and DESIGN.md carry four (Midnight
  Loom, Daylight Loom, Graphite, Atelier). DESIGN.md is the authority; the site lags. The Voice
  Bible's creative north star ("The Precision Loom") is likewise stale against DESIGN.md's
  "The Quiet Gatehouse".

## Evidence on Hand

**Real material that exists:**

- Roughly eighty UI screenshots from two hands-on walkthroughs, with a logged issues list
  (`docs/review/walkthrough-windows-2026-08-24/screenshots/`, `docs/review/walkthrough-2026-08-20/`).
- A large automated test suite — roughly 3,900 `[Fact]`/`[Theory]` methods across `Mainguard.Tests`
  and `Mainguard.Server.Tests` as of 2026-09 (theory cases expand beyond that) — plus CI that runs
  real-Docker sandbox security tests and an in-jail end-to-end verification job. **The "1,042 tests"
  figure in the creative and marketing docs is stale**: it counts the v1 Git client on `main` only.
  Re-count before quoting a number publicly.
- Audit-log evidence written up in `docs/archive/reports/P2-15_Audit_Log_Evidence_2026-08-19.md`.
- A deployed marketing site with working waitlist and contact infrastructure
  (`site/`, `site/worker/`, Cloudflare D1 + Turnstile + Resend).
- Site imagery: `site/src/assets/hero.png`, `site/public/og.png`.

**What does not exist — never fabricate, imply, or design around it:**

- **No benchmark artifacts.** A marketing draft currently claims benchmarks are in the repository;
  that claim is false and must not be repeated.
- **No demo video or recording** — only unproduced scripts.
- **No users, customers, revenue, paying pilots, testimonials, case studies, press coverage, or
  customer logos.** No published waitlist count.
- **No investor, term sheet, or advisor.** The advisor pitch document describes a conversation goal,
  not a relationship. Grant applications are drafts and the 2026 round was missed.
- **No designed deck** — decks exist as markdown only.
- **No team.** Effectively a solo founder (Daniel Sazykin, Enschede, Netherlands); the team-structure
  document is a plan and only one intake form exists.

## Product Principles

1. **Verified, then approved — never automatic.** Verification earns a branch the right to be
   considered; a human always makes the merge decision. Any feature that would quietly merge agent
   work contradicts the product.
2. **Trust what the daemon observes, not what the agent reports.** Every guarantee must be readable
   from outside the thing being judged. A value an agent could supply is not evidence, and anything
   that is supplied rather than observed must label itself as such.
3. **Destructive-safety first.** Wherever the user can lose work — rebase, discard, force-push,
   conflict resolution, stopping a swarm — default to the safer, more legible path. This is the
   product's founding promise, not a preference.
4. **The free client has to be worth using on its own.** It is the trust wedge and the funnel, and a
   wedge that feels like a crippled demo does not wedge. It is never degraded to make Pro look good.
5. **Stay agent-vendor-neutral.** Support for any one coding CLI is an adapter, never an assumption.
   The product's value must survive its users switching model vendors.

## Accessibility & Inclusion

WCAG 2.1 AA as the baseline: contrast and full keyboard navigation. No further requirement has been
established. Revisit if colour-blind-safe requirements surface — the five commit-graph lane colours
and the diff add/remove colours currently rely on hue distinction alone.
