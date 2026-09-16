# Mainguard Competitor Research — July 2026 Refresh

**Date:** 2026-07-07
**Scope:** Current-state check on every product competitor named in Part 2 of
`Mainguard_Naming_And_Competitive_Landscape_2026-07.md`, plus Cursor/Codex/Jules/Docker Sandboxes and
five specific capability-gap probes, evaluated against Mainguard's planned Phase 2 (multi-agent control
center: WSL2 hardened sandbox with default-deny egress, AI rate-limit gateway, merge queue with
test-verification + stale invalidation, plan approval, risk-ranked review with per-hunk provenance,
hash-chained EU-AI-Act-grade audit, vendor-neutral cloud-agent PR intake).

**Evidence standard:** All findings are from web-search snippets and targeted page fetches
(gitkraken.com/kepler, github.blog Copilot-app announcement, conductor.build/changelog, mergeloom.ai)
— treat feature claims as "what they publicly say." Anything not directly evidenced is marked
**unverified**. Full source URL list at the end.

---

## 1. GitHub Copilot app (Microsoft/GitHub)

**What they ship today (verified via github.blog + coverage):**
- Announced at Microsoft Build 2026 (June 2); **GA June 17, 2026** on Windows, macOS, and Linux.
- **Sessions = auto-managed git worktrees** — each session runs in "its own git worktree, a real,
  isolated copy of your branch"; no manual setup/cleanup.
- **Agent Merge** — shepherds a PR "through review, checks, and merge": monitors CI, tracks required
  reviewers, addresses failing checks; user picks how far it goes (drive CI green / address feedback /
  merge when conditions met). This is single-PR shepherding, **not** a queue with cross-branch
  invalidation.
- **My Work view** — dashboard of "active sessions, issues, pull requests, and background automations"
  across connected repositories.
- **Canvases** — bidirectional surfaces showing plans, PRs, terminals, deployments; developers can
  "edit, reorder, approve, or redirect" agent work. This is the closest anyone big has shipped to
  *plan approval*, though it is steering-while-running rather than a hard gate before start
  (gating semantics **unverified**).
- **Sandboxes**: local sandbox with "restricted access to filesystems, network connectivity, and
  system capabilities" (granularity/default-deny posture **unverified**); **cloud sandboxes** —
  fully isolated ephemeral Linux environments with cross-device session pickup, billed by compute
  seconds/GiB-seconds/snapshot storage (example cited: ~$78/mo for 10 devs × 3 hrs/day at 4 GiB).
- Adjacent: **GitHub Desktop 3.6 (June 26, 2026)** added worktrees + deeper Copilot integration;
  Copilot CLI gained voice input and scheduled tasks; SDKs in 6 languages.

**Pricing/momentum:** Copilot switched to **usage-based "AI Credits" billing June 1, 2026**; plans
Pro $10 / Pro+ $39 / Business $19/user / Enterprise $39/user, plus a new **Copilot Max** tier for
higher volume. Local sandboxing included in the seat; cloud sandboxing metered. Momentum is maximal
— default distribution to every GitHub/Copilot user.

**Overlap with Mainguard Phase 2:** worktree sessions, cross-repo work dashboard, single-PR merge
automation, local+cloud sandboxes, canvas-based plan steering. High overlap on orchestration; near-zero
overlap on compliance audit, provenance, merge-queue re-verification, vendor-neutral agent intake.

**Mainguard beats them today:** native Git-client depth (partial staging, 3-pane resolver, interactive
rebase, undo journal); vendor neutrality (Copilot app is Copilot-first); local-first privacy; no
usage-metered billing surprise. **They beat Mainguard:** distribution, cloud sandboxes/continuity,
shipped Agent Merge, GA polish, brand.

**To get ahead:** ship the merge queue with **stale-verification invalidation** (Agent Merge does not
model cross-branch staleness) and **vendor-neutral intake** (pull Codex/Jules/Devin PRs through the
same pipeline — GitHub will never prioritize rival agents' PRs as first-class).

---

## 2. GitKraken Desktop 12 "Agent Mode" + Kepler ADE

**What they ship today (verified via gitkraken.com + PR coverage):**
- **Desktop 12.0 (announced April 16, 2026):** Agent Mode — single view to launch/monitor/manage
  parallel agent sessions; type a branch name, pick an agent (Claude Code, Codex CLI, Copilot CLI,
  Gemini CLI, OpenCode), click Start; GitKraken creates the worktree, runs setup commands, launches
  the agent. 12.0.1 maintenance release shipped since.
- **Kepler (shipped June 15, 2026):** standalone Agentic Development Environment, Windows/Mac/Linux.
  Multi-repo **Tasks** (select issues from Jira/Linear/Trello/GitHub/GitLab and Kepler sets up agents),
  kanban (Exploration → In Development → In Review → Done), session-status filters (Needs Attention /
  Active / Idle / Errored), console panels with mid-session redirection, side-by-side session
  comparison, diff review + staging + commit composition in-app. **PR-based task initiation** — start
  a task from a PR to handle review feedback (a partial form of external-PR intake).
  Agent-agnostic: Claude Code, Codex, OpenCode (Cursor/Copilot CLI per launch coverage).
- Kepler page mentions **nothing** about merge queues, verification/test runs, sandbox isolation,
  provenance, or audit/compliance (checked directly).

**Pricing/momentum:** Kepler free during limited preview; long-term access via GitKraken Pro /
Advanced / Business plans. Established paid install base; also launched "Code Flow" positioning
(SD Times). Steady cadence, no adverse news found.

**Overlap:** highest structural overlap with Mainguard (git-GUI vendor moving into agent orchestration,
multi-repo tasks, in-app review/merge, Windows-native).

**Mainguard beats them today:** deeper git surgery (line-level staging validated against `git apply`,
3-pane resolver, undo journal); planned sandboxing (GitKraken runs agents unsandboxed on the host,
worktree-isolation only — **no isolation claims found**); audit/compliance absent from their public
story. **They beat Mainguard:** shipped and distributed to an existing paying base; multi-repo Tasks;
issue-tracker-driven intake; brand in the exact buyer segment Mainguard wants.

**To get ahead:** hardened sandbox + verification queue + audit — the three things Kepler's own
marketing does not mention. GitKraken is the competitor most likely to copy Mainguard's roadmap, so
speed on the compliance-grade pieces (hardest to retrofit) matters most.

---

## 3. Conductor (conductor.build)

**What they ship today (verified via site/docs/changelog):**
- Mac app running Claude Code, Codex, **and Cursor** agents in parallel, each in an isolated
  worktree/workspace with its own branch, terminal, diff, review path. GitHub + Linear integration.
- Changelog is weekly through **July 3, 2026 (v0.72.0)**. Notable recent: Cursor support + a
  "Dispatcher" (June 8), create-workspace-from-issue (June 16), OpenCode support (June 23), multiple
  run scripts (June 26), browser preview, "New queue, diff diffs" (May 18 — a workspace/task queue,
  **not** evidenced as a verification merge queue), Claude Sonnet 5 / Fable 5 model support within
  days of release.
- **Still macOS-only.** No Windows/Linux signals in changelog. No sandboxing beyond worktrees, no
  audit, no provenance found.

**Pricing/momentum:** free (uses your existing Claude/Codex login); paid collaboration features still
unshipped; **$22M Series A** (Spark + Matrix), YC S24, ~6 people, very fast cadence.

**Overlap:** parallel local agents, per-workspace review/merge, issue intake, run scripts
(a light verification hook — "multiple run scripts" suggests configurable per-workspace commands).

**Mainguard beats them today:** Windows (their absence is total), real Git-client depth, planned
sandbox/audit. **They beat Mainguard:** shipped product, capital, iteration speed, Mac mindshare,
existing GitHub PR-flow integration.

**To get ahead:** own Windows/WSL2 before they port (their $22M could fund a Windows build any
quarter); ship verification-queue semantics they'd have to re-architect for.

---

## 4. Nimbalyst (ex-Crystal)

**What they ship today (verified):** free MIT open-source "visual workspace" over Claude Code, Codex,
OpenCode, Copilot: Monaco code editor, inline AI diff review, WYSIWYG markdown, Excalidraw diagrams,
CSV/data-model editors, session kanban, per-session worktrees, project hub; macOS/Windows/Linux +
iOS companion. Crystal repo formally deprecated Feb 2026 in favor of Nimbalyst.

**Pricing/momentum:** Free individual (MIT); **Pro $20/mo; Team ~$25/user/mo (conflicting sources
also say $40 — pricing in flux, unverified); Enterprise custom.** Extremely aggressive comparison-page
content marketing (they rank for every competitor's name — including "Vibe Kanban alternative").

**Overlap:** session management, diff review, worktrees. No sandbox, no merge queue, no audit, no
egress control found.

**Mainguard beats them today:** git depth, native performance, security story. **They beat Mainguard:**
shipped multi-platform product, visual-editor breadth (mockups/diagrams), SEO/content machine, iOS
companion.

**To get ahead:** nothing structural — Nimbalyst competes for individuals; Mainguard's governance/team
pitch out-flanks it. Copy their content-marketing playbook (comparison pages) rather than features.

---

## 5. Superset (superset.sh)

**What they ship today (verified):** open-source "code editor for the AI agents era": parallel CLI
agents (Claude Code, OpenCode, Codex, Aider, Copilot, Cursor Agent, Gemini CLI) in isolated worktrees
with persistent terminals; built-in diff/file editor, chat panel, in-app browser, port management;
recently added **scheduled automations, TypeScript SDK, Slack bot, and an MCP server letting external
agents drive Superset itself**. Currently orchestrates ~5–7 agents reliably; stated goal 100 by
end-2026. **macOS-only; Windows/Linux untested (open GitHub issue #499).**

**Pricing/momentum:** free tier + **Pro $20/seat/mo** (up from the $15 recorded on July 3 —
**pricing moved**, snippet-verified), enterprise tier; 3-person team, **YC Spring 2026**; #1 Product
Hunt Feb 27, 2026.

**Overlap:** orchestration + automations. No sandbox/audit/queue/provenance.

**Mainguard beats:** Windows, git depth, security. **They beat:** shipped, automation/SDK/MCP surface,
velocity. **To get ahead:** same as Conductor — Windows + verification layer.

---

## 6. Parallel Code (parallelcode.app)

**What they ship today (verified):** free MIT desktop app by solo dev Johannes Millan (maintainer of
20K-star Super Productivity); runs real terminal CLIs (Claude Code, Codex, Gemini, Copilot CLI,
Antigravity CLI) side by side, auto-created worktree per task, one-click merge, keeps your own IDE;
no API-key proxying. ~716 GitHub stars in four months. Cross-platform desktop
(specific Windows build **unverified** but likely given the author's stack).

**Pricing/momentum:** free/MIT, solo-maintained — same structural fragility as pre-shutdown Vibe
Kanban.

**Overlap:** minimal beyond worktree orchestration. **Mainguard beats:** everything downstream of
generation. **They beat:** zero-cost simplicity. **To get ahead:** not a strategic threat; a
feature-parity floor ("dispatch + one-click merge" must feel this easy in Mainguard).

---

## 7. Vibe Kanban (community, post-Bloop)

**What they ship today (verified):** kanban over worktree-isolated agents, 10+ backends, MCP task
creation. **Bloop announced shutdown April 10, 2026**; remote services ended after 30 days; project
moved to fully-local architecture under **Apache 2.0, community-maintained**; founder quote: "the
vast majority are free users and we couldn't find a business model that we could get excited about."

**Momentum:** best-effort maintenance; users actively courted by Nimbalyst and MergeLoom comparison
pages. **Strategic value to Mainguard:** the clearest market evidence that **thin free orchestration
does not monetize** — reinforces the July-3 verdict; also a pool of orphaned users to target.

---

## 8. Composio Agent Orchestrator ("AO")

**What they ship today (verified):** open-source meta-harness (repo `ComposioHQ/agent-orchestrator`,
~3.1k stars, GitHub UI now also shows an "AgentWrapper" org identity — org/maintainer rename in
progress, package renamed `@composio/ao`; details **unverified**): plans tasks, spawns parallel agents
in isolated worktrees, each with its own PR, and **autonomously fixes CI failures, resolves merge
conflicts, and responds to review comments** — the furthest into autonomous PR lifecycle of any OSS
tool. Works with Claude Code, Codex, Aider, OpenCode via plugin interface. Backed by Composio,
actively maintained (as of March 2026 coverage). Their own competitive-landscape discussion benchmarks
against **T3 Code, OpenAI Symphony, and Cmux**.

**Overlap:** autonomous CI-fixing overlaps Mainguard's verification loop but with opposite philosophy —
AO removes the human; Mainguard's Phase 2 inserts a governed human gate.

**Mainguard beats:** governance, review UX, Windows GUI, audit. **They beat:** autonomy depth,
API-first automation. **To get ahead:** position explicitly against ungoverned autonomy ("AO merges
when CI is green; Mainguard proves it's still green *after* everyone else merged").

---

## 9. Factory.ai (Droids)

**What they ship today (verified):** enterprise agent-native platform; Droids across VS Code,
JetBrains, CLI, Slack, Linear; parallel Droids for migrations/refactors; #1 Terminal-Bench (63.1%).
Customers cited: NVIDIA, Adobe, EY, Palo Alto Networks, Morgan Stanley, MongoDB, Bayer, Zapier.

**Pricing/momentum:** **$150M Series C led by Khosla (April 2026), $1.5B valuation, ~$220M total
raised.** Massive enterprise momentum.

**Overlap:** parallel enterprise agents; but cloud-first, agent-vendor-owned. Not a git client, no
local sandbox story, compliance posture enterprise-sales-driven (public hash-chain/EU-AI-Act
specifics: none found).

**Mainguard beats:** local-first, vendor-neutral, git-native review. **They beat:** enterprise scale,
capital, benchmark halo. **To get ahead:** don't compete on execution; make Mainguard the *governed
intake point* for Droid-style cloud output (vendor-neutral PR intake).

---

## 10. Sculptor (Imbue)

**What they ship today (verified via imbue.com/docs):** desktop UI running each agent in **its own
Docker container** (not just a worktree) — explicitly marketed against worktree-sharing-your-env;
**Pairing Mode** (one-click sync of an agent's containerized work into your local repo/git state);
session history/resume; container-startup optimizations ("10x faster to start"). **macOS + Linux,
Windows via WSL** (i.e., runs, but not Windows-native polish). Free during beta; requires Anthropic
API key or Claude Pro/Max (Claude-centric; multi-agent breadth **unverified**).

**Pricing/momentum:** free beta; no 2026 funding/pricing news found (Imbue's ~$200M raise predates;
current commercial trajectory **unverified**). **No public egress-control/default-deny claims found**
— isolation story is container filesystem/process, network posture unstated.

**Overlap:** closest competitor to Mainguard's sandbox thesis. **Mainguard beats:** Windows-first,
network egress control (theirs unstated), git-client depth, vendor neutrality, audit. **They beat:**
shipped container isolation today, Pairing Mode UX (worth studying — it solves "get the agent's work
into my hands" elegantly). **To get ahead:** ship WSL2 sandbox with **explicit default-deny egress +
published security architecture** — outflank on the dimension they leave unspecified.

---

## 11. Dagger container-use

**What they ship today (verified):** open-source MCP server + CLI: fresh container per agent on its
own git branch/worktree; logs, terminal attach, git-based review; **all environment changes
auto-committed, giving a git-native audit trail of agent activity**; works with any MCP-compatible
agent (Claude Code, Cursor, Goose). Still "early development" per Dagger.

**Overlap:** plumbing for exactly Mainguard's sandbox layer. Not a product; no UI, no queue, no
compliance. **Opportunity, not threat:** Mainguard could interop with (or learn from) its
auto-commit-as-audit pattern; its existence also normalizes "containerized agent + git review" for
the market.

---

## Also checked (briefer)

### Cursor (Anysphere) — parallel-agent orchestration
**Cursor 3 (April 2, 2026)**: Agents Window as the central surface; up to **8 parallel agents** in
isolated worktrees, local (Composer 2) or **cloud isolation VMs**; run one prompt across multiple
models side-by-side; `/worktree` command; interface rebuilt around "the agent, not the file, is the
unit of work." Cursor also authored the **Agent Trace** attribution RFC and is expected to emit trace
records by default (see provenance below). Threat level: high for IDE-centric users; not a git
client, no governance layer.

### OpenAI Codex app
**Windows since March 4, 2026** (plus macOS). Multi-agent threads organized by project, built-in
worktree support, built-in git tooling, voice, Skills, **Automations with cloud triggers**, subagent
workflows (parallel spawn + collect). Bundled in ChatGPT plans from $20/mo. **OpenAI Symphony**
(open-source orchestration spec + Elixir reference implementation: polls Linear, auto-claims tickets,
spawns Codex agents, delivers PRs with "proof-of-work"; engineering preview) signals OpenAI moving up
into the orchestration layer itself.

### Google Jules
Cloud agent on Gemini 3 Pro; now has a **public API**, a **GitHub Action** (`jules-action`), CLI
(`jules remote new/list`, apply-patch-locally), opens PRs from the UI, **auto-fixes failing CI on its
own PRs and re-pushes**, and reads/responds to PR review comments. Free tier. Jules is precisely the
kind of cloud-PR firehose Mainguard's vendor-neutral intake should consume.

### Docker Sandboxes (sbx)
**Production GA January 30, 2026.** MicroVM per agent session (own guest kernel + own Docker daemon,
hypervisor boundary); supports Claude Code, Codex CLI, Copilot CLI, Gemini CLI, OpenCode, Kiro,
Docker Agent out of the box; secrets kept in OS keychain with **host-side proxy credential
injection**; **network policy presets: Open / Balanced (default-deny + common dev sites) / Locked
Down (all blocked unless allowed)**, per-run allowlists with wildcard domains, persistent config,
allowlist printed before launch for audit. **Works on Windows** (WSL2-based; community walkthroughs
exist). No Docker Desktop license required standalone. **This is the biggest change to Mainguard's
sandbox calculus: default-deny egress on Windows now exists as first-class plumbing** — but only as a
CLI primitive, with no git-workflow integration, no UI, no compliance logging.

### Claude Code native sandboxing (context)
Anthropic ships OS-primitive sandboxing (bubblewrap on Linux/**WSL2**, Seatbelt on macOS) with a
proxy-based egress model: **no domains pre-allowed by default**, `allowedDomains` allowlist enforced
by hostname. Again: primitive, not product.

---

## Capability-gap probes (a)–(e)

**(a) Merge queue with re-verification / stale invalidation:** GitHub's server-side merge queue does
test PRs against the future merged state and recreates/reruns merge groups when a PR fails out —
functionally stale-invalidation, but **server-side, GitHub-hosted, CI-bound, PR-only, and not
agent-aware**; Mergify similar. **No desktop/local/agent-orchestration product ships it.** Copilot
Agent Merge shepherds one PR; AO/Jules auto-fix CI per-PR. Field empty locally; Mainguard must position
against "just use GitHub merge queue" (answer: works pre-PR, locally, across N agent branches,
without CI round-trips, and for repos not on GitHub).

**(b) Per-hunk agent provenance in review:** **Agent Trace** (announced by Cognition Jan 29, 2026;
released as an RFC by Cursor; backing from Cloudflare, Vercel, Google Jules, Amp, OpenCode, git-ai;
Devin building support) defines JSON trace records mapping code ranges → conversations/contributors,
file- and line-range granularity, human/AI/mixed classification. **Not yet default in any major
product, and no GUI renders it in a diff/blame review surface.** Adjacent: git-ai (git-notes
`refs/notes/ai`), "hunk" (terminal diff viewer for agent output), repowise "agent provenance" from
git history. Field: standard forming, review-surface empty.

**(c) Hash-chained / EU-AI-Act-grade audit for coding agents:** standalone compliance products exist
— **Agent Audit** (hash-chained receipts + optional RFC 3161 notarisation, explicitly marketed at the
Aug 2, 2026 enforcement date), **Asqav** (ML-DSA/FIPS 204-signed records for Arts. 12/19/26),
**Compliora** (SHA-256 seals), plus agentlifylabs/Aegis (Ed25519 audit log for agent control planes).
**None is integrated into a coding tool, git client, or orchestrator.** Caveat found in coverage:
Article 12 requires automatic event logging/traceability but **does not literally mandate
cryptographic immutability** — hash-chaining is the emerging best practice, so market it as
"audit-grade evidence," not "legally required crypto."

**(d) Default-deny egress sandboxing on Windows/WSL2:** now exists at the **primitive** level from
two vendors — Docker Sandboxes (microVM + Balanced/Locked-Down default-deny, Windows-capable) and
Claude Code's bubblewrap-on-WSL2 + proxy allowlist (no pre-allowed domains). Codex CLI also has a
native Windows sandbox. **No polished GUI product integrates default-deny egress with git workflows,
review, and audit on Windows.** Mainguard's July-3 claim needs sharpening from "nobody has it" to
"nobody has productized it."

**(e) External/cloud-agent PR intake into a local verify-review-merge pipeline:** partial gestures
only — Kepler's **PR-based task initiation**, Devin Review (cloud-side PR review platform for
GitHub/GitLab), Codex GitHub code review, Jules responding to its own PR comments. **Nobody offers
vendor-neutral intake of arbitrary agent PRs (Codex + Jules + Devin + Copilot) into one local
verification/review/merge pipeline.** Field effectively empty.

---

## Synthesis

### Capability × competitor coverage

Legend: ● ships today · ◐ partial/adjacent · ○ nothing found · ? unverified

| Capability | Copilot app | GitKraken/Kepler | Conductor | Nimbalyst | Superset | Parallel Code | Vibe Kanban | Composio AO | Factory | Sculptor | container-use | Cursor 3 | Codex app | Jules | Docker sbx |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Merge queue w/ re-verify + stale invalidation | ○ (Agent Merge = 1 PR) | ○ | ○ ("queue" = task queue) | ○ | ○ | ○ | ○ | ◐ (auto CI-fix, no queue semantics) | ? | ○ | ○ | ○ | ○ | ◐ (auto-fix own PR CI) | ○ |
| Plan approval before agent start | ◐ (canvases: steer/approve) | ○ | ○ | ○ | ○ | ○ | ○ | ◐ (plans tasks, autonomy-first) | ? | ○ | ○ | ◐ (plan mode) | ◐ (approval modes) | ◐ (drafts plan) | n/a |
| Sandbox isolation level | ● local restricted + cloud VM | ○ (worktree only) | ○ (worktree) | ○ | ○ | ○ | ○ | ○ | ● (cloud) | ● (Docker/agent) | ● (container) | ◐ (cloud VM opt.) | ◐ (cloud + native sandbox) | ● (cloud VM) | ● (microVM) |
| Default-deny egress control | ? ("restricted network") | ○ | ○ | ○ | ○ | ○ | ○ | ○ | ? | ? (unstated) | ◐ | ○ | ◐ | n/a (cloud) | ● (Balanced/Locked-Down) |
| Audit / compliance (hash-chained, EU AI Act) | ○ | ○ | ○ | ○ | ○ | ○ | ○ | ○ | ? (enterprise sales) | ○ | ◐ (auto-commit trail) | ○ | ○ | ○ | ◐ (allowlist printed) |
| Per-hunk agent provenance in review | ○ | ○ | ○ | ○ | ○ | ○ | ○ | ○ | ○ | ○ | ◐ (per-env commits) | ◐ (Agent Trace emitter, no review UI) | ○ | ◐ (Agent Trace backer) | ○ |
| External/cloud-agent PR intake (vendor-neutral) | ○ (Copilot-only) | ◐ (PR-based tasks) | ◐ (GitHub PR flow, own agents) | ○ | ○ | ○ | ○ | ◐ (own PRs only) | ○ | ○ | ○ | ○ | ○ | n/a (is the producer) | ○ |
| Windows support | ● | ● | ○ | ● | ○ | ●? | ◐ (web) | ◐ (CLI) | n/a | ◐ (WSL) | ◐ | ● | ● (Mar 2026) | n/a | ● |
| Native Git client depth (staging/merge/rebase/undo) | ○ | ◐ (GitKraken Desktop, not line-level) | ○ | ○ | ○ | ○ | ○ | ○ | ○ | ○ | ○ | ○ | ◐ ("git tooling") | ○ | ○ |
| AI rate-limit / budget gateway | ◐ (AI Credits metering ≠ protection) | ○ | ○ | ○ | ○ | ○ | ○ | ○ | ? | ○ | ○ | ○ | ○ | ○ | ○ |

### Where the field is empty — ranked by demand evidence

1. **Local merge queue with test-verification + stale invalidation** — nobody ships it client-side;
   GitHub's server-side queue proves the demand pattern (it exists because "passed CI on a stale base"
   is a real, named failure) and the AI-subagent press ("AI subagents delete your features") documents
   the agent-specific version of the pain. Strongest combination of demand evidence + empty field.
2. **Vendor-neutral external-agent PR intake → local verify/review/merge** — Jules/Codex/Devin are
   mass-producing PRs (Jules even has an API + GitHub Action); every producer keeps review inside its
   own silo; nobody aggregates. Demand grows with every cloud-agent seat sold — by others.
3. **Per-hunk provenance rendered in a review UI** — the Agent Trace RFC's backer list (Cognition,
   Cursor, Cloudflare, Vercel, Google, Amp, OpenCode) is itself demand evidence; the spec exists,
   emitters are coming, **no consumer/renderer exists**. First GUI to paint trace records into
   diff/blame gutters defines the category.
4. **Integrated compliance-grade audit inside a dev tool** — Aug 2, 2026 enforcement is 26 days away;
   standalone audit vendors (Agent Audit, Asqav, Compliora) exist *because* the demand is real, but
   none can attribute actual code changes. The Git side is unclaimed.
5. **Productized default-deny egress on Windows** — primitives shipped (Docker sbx, Claude Code
   sandbox), proving vendor conviction; the integrated GUI + git + audit product does not exist. Window
   narrowed since July 3 but still open at the product level.
6. **AI rate-limit/budget gateway for parallel local agents** — multiple 2026 posts document the
   "9 agents, one quota, everything 429s" failure; gateway vendors solve it for API traffic, nobody
   solves it inside an agent-orchestration desktop app (Copilot's AI-Credits metering bills the
   problem, it doesn't prevent it).
7. **Hard plan-approval gate with identity records** — everyone has soft steering (canvases, approval
   modes, plan drafts); nobody binds "which human approved which plan" into an auditable gate. Demand
   evidence is weaker standalone but it is the linchpin that makes #4 sellable.
8. **Cross-worktree conflict radar** — still nobody ships live conflict prediction across N agent
   worktrees; demand evidence is indirect (merge-conflict horror stories), so keep it as a
   fast-follow, not a lead.

### New threats since the July-3 research

- **MergeLoom (mergeloom.ai)** — *the most important new find.* Governance-first "ticket-to-validated-
  PR/MR" automation: guardrails, repeatable context, validation/repair agents before handoff, audit
  trails, human review control, **self-hosted worker in customer VPC**, vendor-neutral AI providers,
  outcome pricing (from £2–4 per PR/MR, 50 free runs). Launch date/team unknown (**unverified**). It
  is headless/web (no git client, no sandbox/egress/EU-AI-Act claims found), but it occupies Mainguard's
  exact "governed AI coding" language — **and the name collides with "Mainguard" badly enough to
  matter for the ongoing naming decision.**
- **Orca (stablyai)** — open-source ADE, macOS/**Windows**/Linux **+ mobile**, v1.3.50 (May 2026),
  2.1k+ stars; "run any coding agent with your own subscription."
- **Pane / runpane (dcouple) + Emdash** — per their own (self-interested but plausible) June-2026
  comparison, "the only agent managers with native, tested **Windows** support": unlimited parallel
  worktree agents, live diff viewer, x64/ARM64 installers, WSL-aware, orchestrator terminal. Thin and
  terminal-first, but it erodes "Windows is served by approximately nobody."
- **OpenAI Symphony** — OpenAI's own open-source orchestration spec (Linear-polling, ticket
  auto-claim, PRs with proof-of-work; engineering preview). The platform owner moving into the
  orchestration layer is the absorption risk the July-3 doc predicted, now materializing.
- **Intent** — coordinator-agent orchestrator (living spec → task decomposition → implementor waves);
  appears across Augment Code comparison pages; macOS-centric.
- **Agent Trace becoming the attribution standard** (Jan 29/Feb 2026) — an opportunity-shaped threat:
  if Mainguard's provenance stays commit-trailer-proprietary, it will be non-standard within a year.
- **Copilot usage-based AI Credits (June 1, 2026) + Copilot Max** — changes enterprise budget
  conversations; also creates an opening ("your metered agent bill needs a budget gateway").
- **GitHub Desktop 3.6 worktrees (June 26, 2026)** — even the free casual client now normalizes
  worktrees.
- **Cmux / T3 Code** — minor; Cmux is a Mac agent terminal; T3 Code seen only in third-party
  comparisons (**unverified**).

### Recommendations

**Accelerate (in order):**
1. **D-1 merge queue with stale-verification invalidation** — still the emptiest high-demand square;
   every month it stays unshipped, Copilot's Agent Merge and Kepler get one release closer.
2. **Vendor-neutral external-PR intake** (Jules API + GitHub Action and Codex integrations make this
   cheap to build *now*; Kepler's PR-based tasks show competitors circling it).
3. **D-3 audit, but split it**: ship the *evidence pack* (hash-chain + identity + `audit verify` +
   SIEM export) before Aug 2 marketing moment even if RFC 3161 anchoring trails; adjust claims to
   "audit-grade/tamper-evident" rather than "EU-required" (Article 12 doesn't mandate crypto).
4. **Adopt Agent Trace** as the provenance interchange format — emit it from the orchestrator *and*
   render it (plus git-ai notes) in diff/blame gutters. Being the first *consumer/renderer* of the
   standard is cheaper and more defensible than a proprietary trailer scheme; keep trailers as a
   fallback for non-emitting agents.

**Add (new since July 3):**
5. **Evaluate Docker Sandboxes (sbx) as an optional isolation backend** beneath Mainguard's own WSL2
   sandbox: microVM + default-deny presets already exist on Windows; Mainguard's differentiation is the
   integration (worktree + queue + audit + UI), not reinventing the hypervisor. Keep the native WSL2
   path for zero-extra-install, offer sbx as "maximum isolation" tier.
6. **Study Sculptor's Pairing Mode** — a one-click "bring the sandboxed agent's work into my local
   repo, synced" flow should exist in Mainguard's review cockpit.
7. **Naming: treat the MergeLoom collision as a forcing function.** A governance-positioned
   "-Loom" competitor makes "Mainguard" materially riskier than it was on July 3.

**Keep, lower priority:** rate-limit gateway (D-6) — still uncontested and real, but it's a retention
feature, not an acquisition wedge; cross-worktree conflict radar — fast-follow.

**Drop / do not build:** cloud execution (Codex/Jules/Factory own it; intake their output instead);
autonomous CI-fix/auto-merge parity with Composio AO (opposite of the governed thesis — market
against it); generic spawn-N-agents UX beyond parity (now also commoditized on Windows by Pane/Orca/
Codex-app); visual-editor breadth à la Nimbalyst (different buyer).

**Positioning sentence (updated):** *Every vendor now sells you agents that produce branches; GitHub
will even merge its own. Mainguard is the neutral control plane that verifies, attributes, and audits
what **any** agent produced — locally, on Windows, behind a default-deny wall — before it touches
main.*

---

## Sources

Search-snippet and page-fetch evidence gathered 2026-07-07.

**GitHub Copilot app / GitHub**
- https://github.blog/news-insights/product-news/github-copilot-app-the-agent-native-desktop-experience/ (fetched)
- https://byteiota.com/github-copilot-app-is-ga-parallel-agents-worktrees-and-no-more-sidebar/
- https://www.digitalapplied.com/blog/github-copilot-app-agent-native-desktop-orchestration-2026
- https://windowsforum.com/threads/github-copilot-desktop-app-ga-2026-turns-ai-coding-into-a-supervised-agent-control-plane.427657/
- https://github.blog/changelog/2026-06-26-github-desktop-3-6-worktrees-and-deeper-copilot-integration/
- https://github.blog/news-insights/company-news/github-copilot-is-moving-to-usage-based-billing/
- https://costbench.com/software/ai-coding-assistants/github-copilot/
- https://tokenmix.ai/blog/github-copilot-app-sdk-sandboxes-cli-2026
- https://www.helpnetsecurity.com/2026/06/08/github-copilot-app-ai-coding-agents/

**GitKraken**
- https://www.gitkraken.com/kepler (fetched)
- https://www.gitkraken.com/blog/introducing-kepler-the-delivery-engine-for-agent-driven-development
- https://www.prnewswire.com/news-releases/gitkraken-desktop-12-0-introduces-agent-mode-gives-developers-ultimate-control--visualization-while-scaling-parallel-agent-workflows-302745055.html
- https://help.gitkraken.com/kepler/agent-integrations/
- https://sdtimes.com/softwaredev/gitkraken-unveils-code-flow-to-help-teams-navigate-the-ai-era/
- https://www.gitkraken.com/blog/gitkraken-desktop-12-0-1-update

**Conductor**
- https://www.conductor.build/ ; https://www.conductor.build/changelog (fetched) ; https://docs.conductor.build/
- https://www.ycombinator.com/companies/conductor ; https://rywalker.com/research/conductor

**Nimbalyst**
- https://github.com/stravu/crystal ; https://github.com/nimbalyst/nimbalyst
- https://nimbalyst.com/pricing/ ; https://aisotools.com/pricing/nimbalyst ; https://nimbalyst.com/features/

**Superset**
- https://superset.sh/ ; https://github.com/superset-sh/superset ; https://github.com/superset-sh/superset/issues/499
- https://news.ycombinator.com/item?id=48236770 ; https://www.founderland.ai/articles/superset-launches-ide-to-orchestrate-100-ai-coding-agents-in-mpz8db7u

**Parallel Code**
- https://parallelcode.app/ ; https://github.com/johannesjo/parallel-code
- https://dev.to/johannesjo/why-multitasking-with-ai-coding-agents-breaks-down-and-how-i-fixed-it-2lm0

**Vibe Kanban / Bloop**
- https://www.vibekanban.com/blog/shutdown ; https://github.com/BloopAI/vibe-kanban
- https://nimbalyst.com/blog/vibe-kanban-after-bloop-whats-next/

**Composio AO**
- https://github.com/ComposioHQ/agent-orchestrator ; https://github.com/ComposioHQ/agent-orchestrator/discussions/526
- https://www.augmentcode.com/tools/open-source-agent-orchestrators ; https://htdocs.dev/posts/from-conductor-to-orchestrator-a-practical-guide-to-multi-agent-coding-in-2026/

**Factory.ai**
- https://tech-insider.org/factory-ai-150-million-series-c-khosla-coding-droids-2026/ ; https://theaiagentindex.com/agents/factory-ai
- https://factory.ai/news/terminal-bench ; https://factory.ai/news/series-b

**Sculptor / Imbue**
- https://imbue.com/sculptor/ ; https://imbue.com/blog/sculptor-announce ; https://github.com/imbue-ai/sculptor
- https://docs.imbue.com/features/containers ; https://imbue.com/blog/containers

**Dagger container-use**
- https://github.com/dagger/container-use ; https://dagger.io/blog/agent-container-use/ ; https://deepwiki.com/dagger/container-use

**Cursor**
- https://www.datacamp.com/blog/cursor-3 ; https://www.agentpatterns.ai/tools/cursor/agents-window/
- https://cursor.com/docs/configuration/worktrees ; https://liranbaba.dev/blog/cursor-3-parallel-agents/

**OpenAI Codex / Symphony**
- https://openai.com/index/introducing-the-codex-app/ ; https://developers.openai.com/codex/changelog ; https://developers.openai.com/codex/subagents
- https://openai.com/index/open-source-codex-orchestration-symphony/ ; https://www.verdent.ai/guides/codex-app-first-impressions-2026

**Google Jules**
- https://developers.google.com/jules/api ; https://jules.google/docs/changelog/ ; https://github.com/google-labs-code/jules-action
- https://www.digitalapplied.com/blog/google-jules-gemini-async-coding-agent-guide

**Docker Sandboxes (sbx)**
- https://docs.docker.com/ai/sandboxes/security/policy/ ; https://docs.docker.com/reference/cli/sbx/policy/allow/network/
- https://www.docker.com/blog/why-microvms-the-architecture-behind-docker-sandboxes/ ; https://www.docker.com/blog/comparing-sandboxing-approaches-ai-agents/
- https://www.rushis.com/docker-sandboxes-sbx-running-ai-coding-agents-in-fully-isolated-microvms/ ; https://www.ajeetraina.com/running-coding-agents-in-a-secure-microvm-on-windows-with-sbx/
- https://andrewlock.net/running-ai-agents-safely-in-a-microvm-using-docker-sandbox/

**Capability probes**
- Merge queue: https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/configuring-pull-request-merges/managing-a-merge-queue ; https://tenki.cloud/blog/github-merge-queue-setup ; https://medium.com/kairi-ai/githubs-2026-merge-queue-makes-branch-capacity-decide-what-ships-e16eff9aabdf ; https://getautonoma.com/blog/ai-subagent-merge-conflicts ; https://docs.mergify.com/merge-queue/
- Provenance / Agent Trace: https://cognition.com/blog/agent-trace ; https://github.com/cursor/agent-trace ; https://www.infoq.com/news/2026/02/agent-trace-cursor/ ; https://agent-trace.dev/ ; https://www.repowise.dev/guides/agent-provenance ; https://opentools.ai/tools/hunk ; https://mattgoodrich.com/posts/ai-code-attribution-and-provenance/
- Audit/EU AI Act: https://www.agentaudit.co.uk/solutions/eu-ai-act/ ; https://www.asqav.com/blog/posts/eu-ai-act-audit-trail-requirements ; https://compliora.co/ ; https://www.augmentcode.com/tools/ai-coding-tools-eu-ai-act-compliance ; https://github.com/langchain-ai/langchain/issues/35357
- Egress/WSL2: https://code.claude.com/docs/en/sandbox-environments ; https://www.truefoundry.com/blog/claude-code-sandboxing ; https://codex.danielvaughan.com/2026/04/01/codex-cli-windows-native-sandbox-wsl/ ; https://www.penligent.ai/hackinglabs/claude-code-sandbox-bypass/
- External-PR intake: https://docs.devin.ai/work-with-devin/devin-review ; https://developers.openai.com/codex/integrations/github ; https://www.augmentcode.com/tools/devin-vs-codex-desktop-app
- Rate limiting: https://www.tamirdresher.com/blog/2026/03/21/rate-limiting-multi-agent ; https://www.truefoundry.com/blog/rate-limiting-ai-agents-preventing-llm-api-exhaustion ; https://zuplo.com/learning-center/token-based-rate-limiting-ai-agents

**New entrants**
- MergeLoom: https://mergeloom.ai/ (fetched) ; https://mergeloom.ai/compare/vibe-kanban/ ; https://mergeloom.ai/product/self-hosted-ai-coding-infrastructure/
- Orca: https://www.onorca.dev/ ; https://github.com/stablyai/orca ; https://dev.to/andrew-ooo/orca-review-the-ide-built-for-parallel-coding-agents-15df
- Pane/runpane: https://runpane.com/ ; https://github.com/dcouple/Pane ; https://runpane.com/agent-managers-for-windows
- Intent: https://www.augmentcode.com/tools/intent-vs-conductor-macos-agent-orchestrators ; https://www.augmentcode.com/tools/intent-vs-codex-desktop-app
- Landscape: https://github.com/andyrewlee/awesome-agent-orchestrators ; https://dev.to/illegalcall/agent-orchestrator-vs-t3-code-vs-openai-symphony-vs-cmux-hands-on-comparison-1ba8
