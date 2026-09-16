# Mainguard — Business

**Mainguard is a free, native Git client that becomes the layer where AI-agent code is verified before
it is merged.** This document is the strategy: what we sell, who buys it, what the market says, who
else is in it, why it holds, and what would break it.

| | |
|---|---|
| **Execution** — launch, channels, sales, funding, calendar | [`GTM.md`](GTM.md) |
| **Reusable copy** — decks, posts, emails, outreach, target list, grant drafts | [`GTM_Assets.md`](GTM_Assets.md) |
| **The research behind all three** | [`business/`](business/) — each section below links into it |

**In two minutes:** §1 is the whole thesis. §7.1 is the price list. §11 is what's still undecided.

<details>
<summary><b>Ground rules this document is written under</b></summary>

**Honesty contract** (binding, from [`creative/Narrative.md`](creative/Narrative.md) §0). Present-tense
claims are shipped on `main`. Everything in the agent pipeline is marked **[Horizon]** — "in
development," never "works today." Capacity is an honest 4–6 agents on a 16 GB laptop, never "swarms of
50." The audit story is "audit-grade, where procurement is heading," never "legally required." Cloud
figures are illustrative placeholders and say so at first use.

**Evidence standard.** Market and competitive claims were verified 2026-07-06/07 against primary
sources; the target-company research is dated 2026-08-20. Competitor claims are "what they publicly say"
unless noted as independently tested. Where a source list was trimmed here, the linked source document
holds the full ledger.

**Status.** Consolidated 2026-09-16 from the 33-document corpus in [`business/`](business/). This file
is the current view; the corpus is the depth behind it. Where the two disagree, this file wins.

</details>

---

## 1. The business in one page

*Detail: [Master Market Document](business/go-to-market/Mainguard_Master_Market_Document_2026-07.md) Part I; [Viability & Differentiation](business/market-analysis/Mainguard_Viability_And_Differentiation_2026-07.md).*

Mainguard launches as a **free, excellent, native, no-login Git GUI** — the trust wedge and
top-of-funnel — then monetizes the layer nobody has monetized: **making AI-agent output safe to
merge**. Sandboxed local execution, deterministic test-verification before human review, a merge queue
that re-verifies stale branches, risk-ranked review with per-hunk agent provenance, and an audit trail
an enterprise can show its compliance team. We are **Windows-first in a Mac-first category** (the
category leader, Conductor, is Mac-only; Windows is the largest developer OS) and **agent-vendor-neutral
in a locked-in category** (every first-party GUI manages only its own agents). Free users come for the
Git client; teams pay for review throughput and governance. **Orchestration monetizes at zero** — it is
free from every vendor and no one in this category has ever made it pay — so we never sell it. We sell
*trustworthy merges*.

**The one-liner:**

> **Agent CLIs made it trivial to produce ten branches an hour. Nothing on the market makes it safe to
> merge them. Mainguard is where agent work becomes trustworthy commits on main.**

**The enemy — at the core of everything:**

> **The blind merge** — code entering main on trust instead of proof.
> Rallying cry: **"Hope is not a merge strategy."**

The enemy is a *condition*, not a competitor — competitors can neutralize you with a release, a
condition can't. It is what every market number already describes: 87% distrust agent accuracy, review
time +91%, ~45% of AI code carries OWASP-class flaws, delivery stability degrading — **and people merge
it anyway**. It is explicitly **not** "AI code" (our users run six agents) and **not** "manual review"
(reviewers are our buyers). Every feature is a weapon against it: sandboxes → test gates → stale
invalidation → risk ranking and provenance → audit trail.

**Decision rule:** if a proposed feature, partnership, or piece of copy doesn't make a blind merge
harder or a verified merge easier, it's off-thesis.

**Where we stand (September 2026).** The Git client is shipped and working — 1,042 tests, and the
`T-01`…`T-33` task IDs quoted throughout are its build log. The phase-2 agent platform is specified and
in integration on the `phase2` branch. The current stage is **beta-feedback recruitment, not selling** —
Show HN is held in reserve until a reproducible spawn → verify → review → merge run exists
([`GTM.md`](GTM.md) §1). Pre-revenue by design. Founder in **Enschede, Netherlands**, which the plan
treats as a strategic fact rather than an afterthought.

**Why the bet is downstream.** "Run agents in worktrees from a GUI" is not a differentiator — that
plumbing is native to the agent CLIs and free from every vendor. **Our center of gravity is one step
later:** not *running* agents but **verifying, governing and merging what they produce.** Every trend
increases demand for that layer, and it is exactly where a deep Git engine matters and wrapper tools are
weakest.

---

## 2. Product & editions

*Detail: [Master Market Document](business/go-to-market/Mainguard_Master_Market_Document_2026-07.md) Part V (the D-1…D-6 spine); [Feature Inventory](business/market-analysis/Mainguard_Feature_Inventory_2026-07-07.md) (per-competitor gaps, the match/skip rulings, and the full novel-feature list).*

### 2.1 What is shipped, and what is [Horizon]

**Shipped (`main`).** A native Git client — Avalonia + Skia on .NET 10, LibGit2Sharp underneath, not an
Electron shell. A virtualized vector-drawn commit graph that stays smooth on large histories;
line-level staging validated against `git apply`; a synchronized 3-pane conflict resolver; an operation
journal making ref moves undoable plus a reflog viewer; branch/tag/worktree porcelain; interactive
rebase; five themes on one design system. `--force-with-lease`, never a bare `--force`. 1,042 tests.

**[Horizon] — the verification pipeline.** plan approval → sandboxed execution → your tests pass in the
agent's sandbox → risk-ranked, provenance-annotated review → a merge queue that re-verifies whatever
goes stale → human-gated merge. Specified as `P2-10`/`11`/`14`/`15` — phase-2 task IDs, whose specs live
in [`phase-2/implementation_plans/`](phase-2/implementation_plans/) — and in integration now.

### 2.2 The differentiation spine

Ordered by (defensibility × demand). D-1…D-3 are the product spine; all depend on the Git core, and
that sequencing must be protected from launch-marketing pressure.

**D-1 · The verification & merge control plane** — *the lead feature.* Verification runs recorded as
`main@<sha>` + pass/fail + artifact, and **stale-verification invalidation**: any merge to main marks
other "verified" branches stale and auto re-queues them. No competitor GUI models this, and it is pure
Git mechanics. Plus a **flagged-changes gate** on supply-chain-sensitive paths (lockfiles, CI workflows,
hooks, `package.json` scripts; post-merge installs run `--ignore-scripts`), **vendor-neutral intake** of
cloud-agent PRs through the same pipeline, and a **bounded repair loop** — one scoped repair prompt in
the same sandbox, capped and journaled, in a terminal a human can take over.

**D-2 · The review cockpit** — *the daily-driver reason to open Mainguard.* Hunks ordered by blast
radius, not alphabetically. Per-hunk provenance in the blame and diff gutters — which agent, under which
approved plan, wrote this line — **adopting Agent Trace as the interchange format**, emitting *and*
rendering it, with commit trailers as fallback. Plus a test-delta view and a diff-size/off-scope policy.
The metric to market: *"review five agent branches in twenty minutes, safely."*

**D-3 · Compliance-grade audit** — *the enterprise unlock.* Hash-chained append-only log of every
inference, spawn, plan approval and merge decision, **bound to the authorizing OS identity**; SIEM
export; an `audit verify` CLI; RFC 3161 anchoring may trail the rest. Claim "audit-grade," never
"EU-required."

**D-4 · Hardened Windows sandbox** — *the unserved flank.* Default-deny egress via proxy allowlist,
tmpfs-only credentials, no global auth-dir mounts. Publishing the security architecture is itself the
sales asset: a boundary you can audit. Docker sbx is an optional maximum-isolation backend.

**D-5 · Git surgery for agent output** — *unique to a real client.* Interactive rebase tuned for agent
WIP, the undo journal as the agent safety net, and a cross-worktree conflict radar that warns the moment
two agents touch overlapping regions — *before* either merges.

**D-6 · Cost & rate-limit gateway** — *a launch requirement, not an enterprise add-on.* 429-interception
that pauses the agent's PTY instead of letting the CLI crash, per-agent budgets, concurrency ceilings,
spend telemetry keyed to task and branch. Entry API tiers throttle at RPM levels a 3-agent swarm exceeds
instantly; without this, the first session with the headline feature is a retry storm.

### 2.3 Parity gaps that cost deals today

Uniform across the majors, cheapest strong signals first: **Linear + Jira intake**; **in-app dev-server
preview + sandbox-native port handling**; **scheduled automations landing in the review queue**;
**session kanban/status board**; **public CLI/SDK + MCP server over the daemon**; **checkpoints +
working-tree snapshots**; **inline diff comments → agent**; **Agent Trace emit/consume**; **a governed
AI-reviewer pass**; **GitLab MR parity** (the enterprise/Windows wedge demands non-GitHub).

### 2.4 What we deliberately do not build

Generic "spawn N agents" UX beyond parity (commoditized, now on Windows too) · cloud execution at
Copilot/Cursor/Jules parity (capital-intensive, off-thesis — *intake* their PRs instead; the 2027 cloud
worktree tier is the deliberate exception) · autonomous CI-fix/auto-merge parity with Composio AO (the
opposite of the governed thesis — we market *against* it) · AI commit messages and chat-with-your-repo
gimmicks (GitKraken owns the checkbox) · visual-editor breadth à la Nimbalyst (different buyer) ·
computer-use/desktop automation (security-surface explosion inside our own sandbox story) · real-time
multiplayer canvases · DORA/insights dashboards · git-flow automation · full GitButler virtual-branch
working mode.

### 2.5 Licensing & trust posture (locked)

- **Source-available under the Functional Source License (FSL)** — the headless daemon, sandbox/worktree engine, agent adapters, audit
  schema. Publicly readable and auditable while legally prohibiting competing use; converts to
  Apache-2.0 after two years. Not OSI open source, and we say so.
- **Commercial (proprietary)** — the Avalonia GUI, orchestration intelligence, enterprise governance,
  cloud worktrees.
- **Why not fully closed:** the component with root-equivalent access to customer source and API keys
  must be auditable to be adoptable; and .NET IL decompiles to near-perfect C#, so a closed binary buys
  roughly an hour more protection than visible source. The real protection is copyright plus velocity,
  distribution and brand.
- **The posture, locked:** free Git GUI requires **no account, ever**. Local-first as the second
  sentence on the homepage. Published security architecture, opt-in telemetry with a published schema,
  an independent security audit before enterprise GA, and an in-app network-transparency view. Verification
  itself is marketed as a trust feature.
- **EU addendum:** price and invoice in EUR alongside USD; publish a GDPR/data-locality statement;
  **state the legal entity and jurisdiction on the website from day one** — MergeLoom's failure to do so
  is a procurement-killer we must not replicate, and can contrast against.

---

## 3. Market: the numbers that survive citation

*Detail: [Master Market Document](business/go-to-market/Mainguard_Master_Market_Document_2026-07.md) Part II (global + Netherlands, every figure with its source); [Market Research v2](business/market-analysis/Mainguard_Market_Research_v2.md).*

**Global (verified 2026-07-06).**

- **Developers:** ~47M worldwide (SlashData; 36.5M professional), 59.5M forecast by 2029 (IDC). GitHub:
  180M+ accounts.
- **AI code tools market:** ~$7–9B today → **$20–30B by 2030–31, 22–27% CAGR** (Grand View / Mordor /
  Research & Markets consensus band).
- **The adoption/trust scissors — the "Why Now" slide:**
  - 84% of developers use or plan to use AI tools, but only **31% currently use agents** — the agent
    wave is *early* (Stack Overflow 2025).
  - **90% use AI**, median 2 hrs/day — yet only 24% trust the output "a lot," and AI adoption still
    **correlates with worse delivery stability** (DORA 2025).
  - **87% are concerned about agent accuracy; 81% about security** (Stack Overflow 2025).
  - Copilot's coding agent alone authored **1M+ PRs in five months**; code churn **+861%** under high AI
    adoption while DORA metrics stayed flat (Octoverse 2025, Faros AI).
  - AI-assisted teams merge **~2× more PRs** while **PR review time rose 91%**; PR volume +29% YoY
    against a fixed human review ceiling.
- **Windows:** the largest developer OS — **59.2% personal / ~48% professional** — while the entire
  polished-devtool wave (Conductor, Raycast, Warp, Zed) shipped Mac-first.

**The verification bottleneck — the strongest finding in the corpus.** The bottleneck moved from
generation to verification, and the analyst framing is now *"trust, not output, is the bottleneck."*

- 46% of developers actively distrust AI output accuracy, up from 31%; ~45% of AI-generated code carries
  OWASP-Top-10-class flaws in studies; 95% spend real effort reviewing and correcting agent output.
- Converging governance guidance (EU AI Act Art. 12, NIST AI RMF, OWASP LLM Top 10, ISO 42001) asks for
  append-only audit logs of agent actions, **individual attribution** — service-account blindness is
  called out as the #1 compliance gap — and runtime policy enforcement. **No orchestrator GUI ships any
  of it.**
- **Meta built RADAR internally** (catching a 1/3 revert rate) because it couldn't buy the verification
  layer. That is the make-vs-buy proof that the category is real.

**Compliance timing, stated honestly.** EU AI Act GPAI enforcement powers activated 2 Aug 2026 and
Article 50 transparency applies — but the May-2026 "Digital Omnibus" provisionally **postponed high-risk
obligations to Dec 2027**, whether Article 50 "text" covers source code is unsettled, and Article 12
requires logging and traceability but **does not literally mandate cryptographic immutability**. Pitch
audit trails as *enterprise trust and where procurement is heading* — auditors are already asking — never
as a deadline scare.

**Demand signals, each mapped to the product answer:**

| What users report | Our answer |
|---|---|
| Terminal clutter, lost awareness of 4–6 concurrent agents, missed approval prompts | Activity bar with status badges + OS notifications |
| File collisions and semantic drift across parallel edits | Worktree isolation + semantic verification (D-1) |
| The overwriting problem — unilateral destructive changes beyond the requested scope | Plan approval + flagged-changes gate + undo journal |
| Setup hell (Docker, keys, toolchains) | Pre-baked environment onboarding |
| Demands for dry runs, human-in-the-loop gates, explainability | Plan approval, promoted into the headline |
| **Quota rage** — backlash against opaque caps in Cursor/Windsurf | BYOK — "bring your own key": the user supplies their own model credentials, so we sell software and never resell inference |

The duct-tape baseline power users run today is `git worktree` + tmux with a heavy manual merge tax.
**Mainguard's job is to be obviously better than that on day one.**

**The classic Git-GUI market (context for the free tier).** Real but commoditized: GitKraken (largest
paid share), Tower (enterprise niche), Fork ($59.99 one-time, fast, native, no AI), Sublime Merge
(dormant). A new entrant selling "another premium Git client" buys a knife fight over a fixed pie. The
client is our **trust wedge and daily surface, not the business** — and GitKraken's free tier blocks
private repos and requires an account while ours doesn't. That asymmetry is the top-of-funnel play.

**The Netherlands (researched 2026-07-07) — beachhead, not market.** ~575,000 people in ICT occupations
(CBS, Q4 2025); ~106,000 registered ICT companies; ICT investment €35.5B in 2024. **1 in 6 Dutch
companies used AI in 2025 — double 2023 — and 66% among 250+-employee firms**; NL is top-5 in the EU for
enterprise AI adoption and has **Europe's highest AI talent density (10.9 per 10,000)**. The biggest
stated barrier for non-adopters is "lack of experience" (74.6%) — precisely a governed-adoption pitch.
€2.64B VC invested in Dutch tech in 2025 (+26.2% YoY) across 11,301 active tech companies, though deal
count fell 14.5%.

NL over-indexes for Mainguard specifically because of **.NET density** (Dutch finance, government,
healthcare and manufacturing built on the Microsoft ecosystem; Techorama NL is one of Europe's largest
Microsoft-stack conferences, and Mainguard is itself a .NET flagship app), **Windows-heavy enterprises**
(the "Dana" persona is the default Dutch enterprise developer), **compliance culture**, and the **trust
gap** created by high adoption plus the experience barrier. **Honest caveat:** 575k ICT workers is <1.5%
of the world's developers. NL is where design partners, subsidies, talent and first enterprise logos come
from — not where the ARR ceiling is. The revenue plan stays global and English-first.

---

## 4. Competition

*Detail: [Competitor Research](business/market-analysis/Mainguard_Competitor_Research_2026-07-07.md) (17 profiles, evidence-checked); [MergeLoom Deep Dive](business/market-analysis/Mainguard_MergeLoom_Deep_Dive_2026-07-07.md) (feature-by-feature, the G1–G8 gaps); [Master Market Document](business/go-to-market/Mainguard_Master_Market_Document_2026-07.md) Part III (the capability x competitor matrix).*

### 4.1 The four layers

| Layer | Winners so far | Limitation we exploit |
|---|---|---|
| AI-native IDEs | Cursor, Windsurf | Single-workspace; parallel agents cause context bloat and file collisions |
| Terminal agents | Claude Code, Codex CLI, Aider, OpenCode | Powerful but unmanaged; swarms require tmux duct tape |
| Execution/isolation infra | Docker Sandboxes (sbx), GitHub cloud VMs | Infrastructure, not workflow; no merge pipeline, no review UX |
| **Orchestration & control** | *(contested)* | Nobody combines isolation + orchestration + governance + native UX |

### 4.2 The field at a glance

| Class | Who | Where they stop |
|---|---|---|
| **Git clients + AI** | **GitKraken** (Desktop 12 Agent Mode Apr 2026; **Kepler** ADE June 2026), GitButler ($17M a16z A), Tower/Fork (AI commit messages only), Sublime Merge (dormant) | Electron heft; **host-level worktrees only — no sandbox**; no verification/merge pipeline |
| **Orchestrator GUIs** | **Conductor** ($22M A, Mac-only, free), Superset (YC S26), Emdash, Nimbalyst, Sculptor (Imbue — containers), Omnara | **No one verifies output** (Sculptor beta excepted); no merge queues, no audit, mostly Mac-first, **monetization unsolved at ~$0** |
| **First-party absorption** | **Claude Code Desktop**, **Codex app** (Win since Mar 2026), **Cursor 3** (+Graphite → **Origin**, fall 2026), GitHub **Copilot app** (GA 17 June 2026) + Agent HQ | **Single-vendor each**; model self-review, not deterministic test gates; no local merge orchestration; no per-change provenance; platform lock-in |
| **Review / merge-queue layer** | **CodeRabbit** ($550M val), **Greptile**, Graphite→Cursor, Mergify, Trunk, Baz, Qodo, **MergeLoom** (£2–4/PR) | Cloud GitHub-apps first; **nobody does local verification cockpits, per-hunk provenance, hunk risk-ranking, or re-verification in the queue** — queues re-run CI only |
| **Audit/provenance** | git-ai, GitClear, vendor enterprise logs, sigstore/gitsign, the Agent Trace RFC | Demand articulated, tooling immature; **no commercial product ties provenance to review + merge** |

### 4.3 The four that matter

#### GitHub Copilot app — the distribution threat

GA 17 June 2026 on all three platforms. Sessions are auto-managed git worktrees; **Agent Merge** shepherds
a PR through review, checks and merge; Canvases are the closest anyone big has shipped to plan approval.
Local sandbox plus metered cloud sandboxes, on usage-based "AI Credits" billing.

- **Stops at:** single-PR shepherding — **no cross-branch staleness model**; Canvases steer while running
  rather than gate before start; Copilot-only intake; near-zero compliance audit or provenance.
- **They beat us on:** distribution, cloud continuity, GA polish, brand.
- **To get ahead:** the re-verifying queue, and vendor-neutral intake — GitHub will never make rival
  agents' PRs first-class.

#### GitKraken Desktop 12 + Kepler — the highest structural overlap

Agent Mode launches parallel agent sessions across five CLIs; Kepler adds multi-repo Tasks from
Jira/Linear/GitHub, a kanban, session-status filters, in-app diff review, and PR-based task initiation.

- **Stops at:** their pages mention **nothing** about merge queues, verification, sandboxing, provenance
  or audit — and worktree isolation is **host-level only**, with agents running directly on the user's OS.
- **They beat us on:** being shipped to an existing paying base, multi-repo tasks, brand in our exact
  buyer segment.
- **To get ahead:** sandbox + verification queue + audit. **They are the likeliest to copy our roadmap**,
  so speed on the compliance-grade pieces — the hardest to retrofit — matters most.

#### Conductor — the funded category leader

Mac app running Claude Code, Codex and Cursor agents in parallel worktrees. $22M Series A, YC S24, ~6
people, weekly cadence, logos Vercel/Notion/Ramp. Free, riding the user's own agent logins.

- **Stops at:** **macOS only** — no Windows or Linux signals anywhere in the changelog. No sandboxing
  beyond worktrees, no verification, no audit, no provenance; its "queue" is a task queue.
- **To get ahead:** own Windows/WSL2 before they port — $22M could fund it any quarter — and ship queue
  semantics they'd have to re-architect for.

#### MergeLoom — the structural mirror image

A headless "governed ticket-to-code" pipeline: an approved ticket triggers a run, a context engine
assembles cross-repo context, an AI provider implements, a six-gate validation-and-repair runway runs, and
a validated PR goes back for mandatory human review. No IDE, no desktop app, no Git client, no interactive
session. £4/PR cloud, £2/PR self-hosted. One employee, no funding or customer trail, and a 161-post SEO
blog dated essentially one day.

- **They beat our plan on:** tracker-driven intake, a persistent context engine, the repair loop, a
  clarity gate, an LLM review pass, Diff Guard, agent fleets with caps, cost-per-outcome telemetry — and
  time-to-market, since they are live and billing today.
- **We beat them on:** a shipped deep Git client (they have none) · local execution and a hardened sandbox
  ("your laptop is the VPC" — their self-hosted mode needs Linux/K8s *and* a live SaaS controller, so it
  isn't air-gappable) · the re-verifying queue (*validated-then-stale is unvalidated* is intrinsic to their
  architecture — they stop at "PR opened") · review depth and provenance · interactive agents · audit
  **integrity** rather than plain traceability — "audit-grade vs audit-flavored" · no per-PR meter on your
  own hardware · Windows.

### 4.4 Where the field is empty — ranked by demand evidence

1. **Local merge queue with test-verification + stale invalidation** — nobody ships it client-side;
   GitHub's server-side queue proves the demand pattern. Strongest combination of demand evidence and
   empty field.
2. **Vendor-neutral external-agent PR intake → local verify/review/merge** — Jules/Codex/Devin
   mass-produce PRs; every producer keeps review inside its own silo; nobody aggregates. Demand grows
   with every cloud-agent seat sold *by others*.
3. **Per-hunk provenance rendered in a review UI** — the Agent Trace RFC has emitters coming and **no
   consumer/renderer exists**. First GUI to paint trace records into diff/blame gutters defines the
   category.
4. **Integrated compliance-grade audit inside a dev tool** — standalone audit vendors exist because
   demand is real, but none can attribute actual code changes. The Git side is unclaimed.
5. **Productized default-deny egress on Windows** — primitives shipped (Docker sbx, Claude Code
   sandbox); the integrated GUI + git + audit product does not exist. Claim sharpened from "nobody has
   it" to **"nobody has productized it."**
6. **AI rate-limit/budget gateway for parallel local agents** — the "9 agents, one quota, everything
   429s" failure is documented; gateway vendors solve API traffic, nobody solves it inside an
   orchestration desktop app.
7. **Hard plan-approval gate with identity records** — everyone has soft steering; nobody binds "which
   human approved which plan" into an auditable gate. The linchpin that makes #4 sellable.
8. **Cross-worktree conflict radar** — nobody ships live conflict prediction across N agent worktrees.
   Fast-follow, not lead.

Our architecture contains 1–5 and 7 by design; 6 and 8 are specced. **That is the product story.**

### 4.5 How we beat each class

- **GitKraken:** don't fight the Git-GUI knife fight on features — fight on native performance, free-tier
  generosity, and depth. Expect Kepler to add features fast; the defense is the compound pipeline.
- **The orchestrators:** concede parity quickly, then differentiate where they can't follow without
  rebuilding a Git client. They're free and unmonetized — we don't beat them on price, we're worth paying
  for where they aren't.
- **First-party vendors:** be Switzerland. Each is incentivized to lock in; none will make its GUI a
  better home for a rival's agent.
- **The review layer:** their weakness is noise (an audit found ~35% of CodeRabbit comments genuinely
  useful) and a cloud-only posture. "Your test suite passed in the agent's sandbox" is a fact, not an
  opinion — the antidote to AI-review fatigue. Longer term, integrate one as an optional signal.

*What to watch, and what to do about each, is the erosion dashboard in §8.4.*

---

## 5. Ideal customer, personas & positioning

*Detail: [Master Market Document](business/go-to-market/Mainguard_Master_Market_Document_2026-07.md) Part IV; [Company Sourcing Playbook](business/go-to-market/sales/Mainguard_Company_Sourcing_Playbook.md) (the three findable segments).*

### 5.1 Positioning (locked)

**Market-facing:**

> *Every vendor now sells you agents that produce branches; GitHub will even merge its own. Mainguard is
> the neutral control plane that verifies, attributes, and audits what **any** agent produced — locally,
> on Windows, behind a default-deny wall — before it touches main.*

**For a buyer:**

> **Mainguard is the engineering manager for your AI agents.** Run several coding agents in parallel,
> each in its own sandboxed worktree, with plans you approve before code is written, tests that run
> before you review, and merges that never happen without you. Your code stays on your machine; your keys
> stay in your keyring; every agent action is auditable.

**Never lead with "swarm," "50 agents," or "orchestration."** All three are commoditized, and the first
two are hardware-dishonest. Three deliberate locks follow from that: **"several agents"**, not "swarms of
50+" (indefensible on consumer hardware);
**plan approval in the headline** (the cheapest trust-builder we have); **auditability as core
messaging**, not an enterprise footnote.

### 5.2 Message hierarchy

| Audience | Lead message | Supporting proof |
|---|---|---|
| Agent power users | "Never let an agent break your working directory again. Review five agent branches in twenty minutes, safely." | Sandboxes, worktree isolation, test gates, risk-ranked cockpit |
| Windows / .NET enterprise devs | "The premium native Git client Windows never got — and the only agent runner built for WSL2." | Native Avalonia perf, no Electron, no login, local-first |
| Engineering managers / CTOs (buyers) | "Velocity *with* governance: agents that must pass tests before review, plus an audit trail of every agent action." | Merge queue + re-verification, per-hunk provenance, SIEM export |
| Investors | "The verification layer for the agent era — the bottleneck moved from writing code to trusting it, and we own the Git-native chokepoint." | 87% distrust; DORA stability degrading; 1M PRs in 5 months |

### 5.3 The ideal customer profile (ICP)

A **10–100 developer product company or agency, Windows-heavy or mixed-OS, already running agentic CLIs**,
where an EM or staff engineer owns the "our review queue is drowning and I don't trust what the agents
merged" problem, and where compliance or client contracts make "who wrote this code and was it tested" a
real question. .NET shops, fintech/insurance/healthcare ISVs and government contractors over-index on
every axis.

For sourcing, that intersection splits into three independently findable axes — **A: teams already
running agents** (the urgency axis; high findability, since adoption leaks into repos, job ads and
talks), **B: Windows-first .NET shops** (the underserved flank; very high findability), **C: regulated
orgs** (the audit-trail axis; **sourced and nurtured now, sold to only when the governance tier ships**).
Companies scoring on two or three axes are the bullseye.

### 5.4 Personas (in adoption order)

1. **"Sam" — the agent power user (launch persona).** Senior IC running 3–6 parallel agent sessions today
   via tmux/worktrees or Conductor-on-Mac. Pain: terminal clutter, agents stepping on each other, a
   firehose of diffs, one API key rate-limiting everything. Found via HN, r/ClaudeAI, X. Converts on the
   sandbox demo, review cockpit, rate-limit gateway. Pays $0 → $20/mo.
2. **"Dana" — the Windows/.NET professional (volume persona).** Enterprise dev on a locked-down Windows
   machine whose options are a 2015-era free client or an Electron app with an account wall. Converts on
   speed, polish, and a WSL2-native agent runner. Individual pays $0; her *company* pays.
3. **"Priya" — the engineering manager (buyer persona).** Owns review throughput, accountable for AI
   governance, facing audit questions she can't answer. Converts on queue metrics, provenance, audit
   export. Pays $50+/seat. **Do not sell to Priya before the governance features exist.**

### 5.5 Explicit non-targets (for now)

Vibe coders and non-technical founders (a later cloud product, not a local install — a local Vibe Mode
requiring admin elevation and a mid-onboarding reboot cannot beat browser-native rivals on
time-to-first-magic) · teams all-in on cloud agents with no local loop, until external-PR intake ships ·
OSS maintainers wanting free-forever everything — we serve them a great free client, but individual
developers are not the revenue plan.

**The overlap risk worth naming:** any team already using GitKraken or paying for Copilot can get baseline
functionality at **zero switching cost**. That is the most serious competitive risk independent of
anything else, and it means Mainguard needs a clear reason to switch — the verification pipeline plus
native depth plus neutrality *is* that reason; single features are not.

---

## 6. Objections (concede → fact → line)

*Detail: [Objection Handling](business/go-to-market/Mainguard_Objection_Handling.md) (full steel-manned treatment); HN phrasings live in [Narrative.md](creative/Narrative.md) §5.4.*

The register: concede first (every objection contains a true fact; agreeing buys the credibility the
counter spends — never open with "actually"), sourced facts with no adjectives, never a villain, tense
discipline under pressure, and **leave the line, then stop**.

**1. "Just use GitHub's merge queue."** Concede: it's real, good at what it does, and right for keeping
CI green on `main` under high PR volume. Fact: three structural gaps — it re-runs **CI, not
verification** (no queue on the market re-runs your test suite in the agent's sandbox on the post-rebase
state); it gates *after* push, on GitHub's runners, while we gate before anything reaches the remote, on
branches from any vendor, including repos not on GitHub; and it carries **no agent semantics** at all.
Line: *GitHub's queue keeps CI green on the batch. Mainguard keeps the promise that nothing lands on main
that wasn't verified — your tests, the agent's sandbox — against the main it lands on.*

**2. "Conductor already exists."** Concede: it's the funded category leader, small and fast, and deserves
the lead on orchestration — conceded as strategy, not courtesy. Fact: macOS-only with no Windows signals
anywhere in its changelog; worktree-only isolation with no verification layer; and free means unmonetized
in a category where free orchestration already killed two companies. State the caution before the
investor does — $22M can fund a Windows port any quarter, which is why the defense is Windows *paired
with* queue semantics they'd have to re-architect to follow. Line: *"Conductor for Windows — with
verification." The analogy flatters twice.*

**3. "Agents will get good enough to not need this."** Concede: they will keep getting better — we're
counting on it, and some of today's babysitting UX will age out. Fact: as capability rose through
2025–26, trust *fell* and delivery stability still correlates negatively; **verification demand scales
with volume, not error rate**, so ten branches an hour against a fixed review ceiling gets *worse* as
agents improve; the most capable organizations verify the most (Meta built RADAR); and half the product
isn't about model quality at all — attribution, audit evidence, budget governance and merge coordination
hold even for flawless agents. *Residual risk, owned:* if agents become near-perfect **and** organizations
stop caring about attribution, this shrinks to coordination plumbing — we consider the second condition
the less likely one. Line: *Better agents produce more branches, not more trust. The gap between "merged"
and "verified" grows with every agent seat sold — by anyone.*

**4. "GitHub / Anthropic / Cursor will just ship this."** Concede: they're shipping generation
aggressively, and any single feature has a ~2-quarter window. Fact: each is single-vendor *by incentive*;
none reviews with deterministic local gates; none models cross-branch staleness. The one announced
exception is named as our tripwire in writing. Line: *Vendor-neutral verification is structurally
Switzerland's job — and we've published the tripwire for the day that changes.*

**5. "The Git-client market is commoditized."** Concede: fair, and Fork proves craft alone earns $59.99
once, not a company. Fact: the client is the wedge and the *prerequisite* — verification is only buildable
on a real Git engine — and the free tier exists because the funnel must be excellent against an
account-walled incumbent. Line: *The client is the foundation, not the pitch — and it has to be excellent
anyway, because you'll live in it.*

**6. "Orchestration tools all died. Why are you different?"** Concede: they did. Fact: they were selling
orchestration at $0 to individuals. We never sell orchestration; the free tier is a Git client with
independent daily value, and every paid tier prices against what teams demonstrably already pay for —
review throughput, merge reliability, governance. Line: *We don't charge for the part that has never been
worth money.*

**7. "MergeLoom already sells governed AI delivery."** Concede: live, billing, and 6–12 months ahead on
the governance *story* — the most instructive competitor we have. Fact: structural opposites — no client,
no review surface, no interactive steering, no merge coordination, no sandbox claims, audit without
integrity, one person. Line: *They stop at "PR opened." We govern the last mile — and a branch validated
an hour ago, against an older main, is not validated.*

**8. "Individual developers don't pay."** Concede: largely true, and we don't plan on it. Fact: the funnel's outputs are
distribution, the in-company champion, and the two investor-grade metrics download counts can't fake.
Line: *Individuals are the funnel; the business is the team tier — and we don't sell it before the
governance features exist.*

**9. "The EU AI Act doesn't actually require any of this."** Concede: correct — Art. 12 mandates logging
and traceability, not cryptography, and the Omnibus moved high-risk obligations to Dec 2027. We say so
unprompted. Fact: the pitch is audit-grade evidence and where procurement is heading; auditors are already
asking. Line: *We sell what procurement is starting to ask for — not a deadline scare. If we're early,
early is where trust features have to be.*

**10. "Your cloud revenue is mostly pass-through — the ARR is fake."** Concede: at 10,000 active cloud
users, ~$212K of ~$228K monthly revenue would be model spend passing through our invoice, and our own cost
model says quoting it unflagged would flatter the business. Fact: the tier runs on gross-margin dollars,
break-even ≈ 3,200 active users, and the GA gate is "beta unit economics match the model within
tolerance." Line: *We flagged that number ourselves before you found it — the tier is priced on margin
dollars, not revenue optics.*

**11. "Windows-first is a niche bet."** Concede: the loud devtool market is Mac-first. Fact: Windows is
the *largest* developer OS and the entire polished wave skipped it; WSL2 depth is the unglamorous work
Mac-first teams fund last. Line: *Mac-first is where the demos are; Windows is where the developers are.*

**12. "4–6 agents is too small a swarm to matter."** Concede: that's the honest local ceiling on 16 GB,
and we refuse to claim more. Fact: 4–6 *governed* agents already breaks every workflow on the market, and
scale beyond the laptop is the cloud tier's job on the same binary. Line: *A few agents, perfectly
managed, beats fifty that OOM your laptop — and when you need fifty, that's what the cloud tier is for.*

---

## 7. Pricing & unit economics

*Detail: [Business Plan](business/go-to-market/Mainguard_Business_Plan.md) (the reasoning); [Cloud Vibe Companion](phase-2/Mainguard_Cloud_Vibe_Companion.md) §5 (the arithmetic, assumptions A1–A9).*

### 7.1 The tiers (locked structure)

| Tier | Price | What it buys | Why this number |
|---|---|---|---|
| **Free** | $0, no login, ever | The full Git client + one sandboxed agent **[Horizon]** | The funnel must be genuinely excellent free. GitKraken's free tier is account-walled and blocks private repos; ours has no wall to hit. A login wall on the tool that sits between a developer and their code is a cost we refuse to pay |
| **Pro** | **$20/mo** or $199/yr with perpetual fallback | Unlimited local agents, verification pipeline, review cockpit, AI gateway, BYOK **[Horizon]** | $20 is the established individual AI-tool price (Cursor Pro, Claude Pro, Copilot Pro+) — no anchoring fight. BYOK means no inference-margin death. The JetBrains-style fallback is a loyalty signal, support-scoped with a separately versioned adapter channel |
| **Team / Enterprise** | **$50+/seat** | Merge-queue + re-verification analytics, per-hunk provenance, audit/SIEM, RBAC/SSO/SCIM, budget caps, license scanning **[Horizon]** | Sits credibly above CodeRabbit Pro ($24–48/dev/mo) and Graphite (~$40) because it bundles what they each sell a slice of. **Not sold before the governance features exist** |
| **Cloud worktrees** | usage-based, 2027 | Hosted execution sessions **[Horizon]** | The usage-revenue lever BYOK deliberately forfeits locally; solves the honest 4–6-agent ceiling |

### 7.2 The four refusals

Each is a standing rule, not a preference, and each closes off a way this business could fail.

- **We never charge for spawning agents.** Orchestration is free from every vendor and has never
  monetized anywhere in this category. We sell the pipeline that makes agent output mergeable.
- **We never resell inference at a flat rate.** A "$25/month, everything included" tier dies the moment
  heavy users arrive — one heavy session a day is ~$117/month of model cost against $25 of revenue. BYOK
  locally; metered pass-through in the cloud.
- **We never sell the Team tier before its features exist.** Selling promises to a compliance buyer is
  the one unrecoverable trust failure.
- **We never meter the customer's own hardware.** MergeLoom charges £2–4 per opened PR; our local runs
  cost tokens only. *No per-PR meter on your own hardware.*

### 7.3 BYOK vs cloud — the one consequential decision

**The desktop product earns nothing from usage, on purpose.** Locally the user brings their own key; the
model bill goes straight from vendor to user. What that buys: no inference cost on our books, no exposure
to model-price swings, no incentive to throttle the user's agents, and the trust posture the market has
repeatedly rewarded. What it costs: every dollar of usage economics. Desktop revenue is therefore *pure
software revenue* — clean, predictable, capped by seat counts.

**In the cloud we run the compute, so usage finally becomes revenue.** *Be honest about scale locally;
monetize scale in the cloud.* The cloud tier is not a pivot — it's the same daemon binary in a per-tenant
pod — and it exists for two customers the desktop structurally cannot serve: the developer who wants more
than 4–6 agents, and the non-technical founder for whom cloud + managed key isn't an upsell but the only
door into the product. Two modes, because two audiences: *managed-key* (+10% handling) and *cloud-BYOK*
(model spend drops out of our revenue and our risk; we bill platform units only).

### 7.4 Cloud unit economics — illustrative, and the caveat we volunteer

**Every figure here is an illustrative placeholder chosen to show the shape of the math — not a quote, not
a committed price.** Real numbers come from beta telemetry, and the GA gate is literally "beta unit
economics match the model within tolerance." Full arithmetic lives in
[`phase-2/Mainguard_Cloud_Vibe_Companion.md`](phase-2/Mainguard_Cloud_Vibe_Companion.md) §5.

**The one ratio that dictates the architecture.** A typical 20-minute session costs ~$1.23 all-in — of
which **~$1.20 is model spend and ~$0.03 is us**. Model spend is 87–95% of session cost. So model spend is
**pass-through**, re-billed at cost +10% handling and never counted as product revenue; **platform units
are the product**, marked up 3.5× — and because they're cents, that markup is invisible to the customer.

| | Per active user-month |
|---|---|
| Revenue | $22.85 |
| COGS | $19.74 |
| **Gross margin** | **$3.11** — platform markup $1.19 (small, but priced by *us*) + model handling $1.93 (larger, but riding a price we don't control) |
| Break-even | ~3,200 active cloud users against an assumed $10K/month fixed platform cost |

**Margin protection is operational, and every measure is a design decision already made:** nested
container packing, aggressive idle reclamation, storage crypto-shred discipline, and one metered number so
the dashboard and the invoice can never disagree.

**The caveat we carry into every pitch.** At 10,000 users, **$212K of the $228.5K monthly revenue is the
model vendor's money passing through our invoice.** Quoting that as ARR would flatter the business, so
every internal target is set against the gross-margin column. In a category that died of over-claiming,
volunteering this before diligence finds it is itself a moat.

**Sensitivity.** Model prices falling 50% is the biggest exposure (margin → $2.16, break-even → ~4,600
users), mitigated because usage expands as prices fall and the platform markup is untouched — the model is
volume-and-markup driven by design. Prompt caching is upside either way. A flat instead of nested topology
roughly triples platform COGS, which is why nested is the default and per-agent pods are a *paid isolation
capability*. Failed idle reclamation is both a margin leak and a trust breach.

### 7.5 Desktop funnel math, kept honest

Devtool freemium converts 1–3%. At 2% of 10,000 active free users × $20/mo ≈ **$50K ARR** — which is the
argument, not the disappointment: **real money lives in team seats**. The desktop funnel's job is
distribution and trust, the in-company champion who becomes the Team-tier deal, and the two metrics
investors can't get from download counts. Adjacent willingness-to-pay anchors: CodeRabbit $24–48/dev/mo,
Graphite ~$40, Mergify $8+, governance premiums above all.

### 7.6 The path from $20 seats to $50+ seats

**Step 1 — Land.** Individuals adopt the free client; power users convert to Pro. Individuals are the
funnel, not the revenue plan. **Step 2 — Prove willingness-to-pay before building the enterprise layer.**
3–5 design-partner teams use the merge queue on real repos, time-boxed to six months. **Tripwire:** if they
won't pre-commit to paid pilots by two months after the agent layer launches, packaging gets revisited before another euro of
enterprise build. **Step 3 — Expand on governance.** The audit dashboard and queue analytics, sold to
Priya. **Step 4 — The Team tier rides the cloud rails.** The identity stack and tenant infrastructure built
for cloud sessions carry the Team tier's server features — one infrastructure investment, two revenue lines.

**Business expansion, in priority order:** cloud-hosted worktrees (promoted from "pivot" to roadmap) →
the B2B observability/audit dashboard (highest willingness-to-pay, procurement-friendly) → an enterprise
CI/CD "janitor" (deferred; needs CI integrations and merge-gate reputation first) → an agent skills
marketplace (deferred; needs ecosystem scale that doesn't exist).

---

## 8. Defensibility

*Detail: [Defensibility Memo](business/go-to-market/Mainguard_Defensibility_Memo.md) (layer-by-layer retrofit cost and erosion scenarios).*

### 8.1 The thesis

Defensibility is not any single feature — **any single feature has a ~2-quarter exclusivity window**. The
moat is the *stack*: five layers that compound because each requires the one below it, and the bottom
layer — a real Git engine — is the one thing no competitor class possesses or can acquire cheaply.

One sentence for the partner meeting: **everyone else must either build a Git client, betray their
platform incentive, or re-architect a shipped product to follow us — and most must do two of the three.**

### 8.2 The five layers, with retrofit cost and erosion

**Layer 1 — The Git engine (shipped; the prerequisite).** Every downstream differentiator is a Git-engine
problem: stale re-verification *is* rebase machinery; curating agent WIP *is* interactive rebase + partial
staging; "undo what the agent did" *is* the operation journal; per-hunk provenance *is* a diff stack +
blame. The orchestrator field has none of this. *Retrofit:* orchestrators must build a client from scratch
(a year+, off-thesis for a 6-person team); first-party vendors have engineers but not incentive; GitKraken
has a client but host-level unsandboxed execution; MergeLoom has no client at all. *Erosion:* an incumbent
**acquires** a client instead of building one (the Cursor–Graphite pattern) → reassess within the quarter.

**Layer 2 — Containment by construction [Horizon].** Sandboxes whose *only* push target is a daemon-owned
quarantine mirror, with no real-remote credential present and default-deny egress — escape structurally
impossible rather than firewall-blocked. The field's isolation stories stop at worktrees; Sculptor is the
closest thesis but publishes no egress posture; MergeLoom, a *governance vendor*, publishes no sandbox
hardening for the thing executing AI-written code. *Erosion:* hardened sandboxes commoditize as
primitives — partially priced in, because the *integration* is the product.

**Layer 3 — Deterministic verification + the re-verifying queue [Horizon] — the keystone.** Probe-verified
across the field: every queue re-runs CI; **none re-runs verification on the post-rebase state of agent
branches**. For GitHub, retrofitting means owning local execution — inverting a cloud product. For
MergeLoom, staleness is intrinsic. For the review layer, verdicts are LLM opinions by construction.
*Dependency stated:* this layer's moat value requires shipping it credibly. *Erosion:* **Cursor Origin** —
if it ships local execution + provenance, re-plan within a quarter.

**Layer 4 — Provenance + risk-ranked review [Horizon].** Hunk-level risk ranking exists in production only
inside Meta. The Agent Trace standard has emitters coming and **no renderer** — being the first means the
vendors do the emission work while the value accrues to whoever owns the review surface, which requires
Layer 1. A standards position is cheap to take early and expensive to take late. *Erosion:* the standard
fragments.

**Layer 5 — The audit chain [Horizon].** Hash-chained, append-only, identity-bound, SIEM-exportable,
offline-verifiable — against the field's nothing.

**Plus two positional assets protecting all five.** **Vendor-neutrality:** every first-party GUI manages
only its own agents; each is *incentivized* to lock in. This is the rare moat enforced by the
*competitors'* economics rather than ours — absorbing our position requires betraying their own, and their
PR output is our intake supply, not our competition. **Windows/WSL2-first:** the largest developer OS,
shipped Mac-first by the entire polished wave; real WSL2 depth is unglamorous integration work a Mac-first
team funds last. *Erosion:* $22M funds a Windows port any quarter — which is why the defense is *pairing*
Windows with queue semantics, never Windows alone.

**And one cultural asset.** The trust posture — no login, no private-repo wall, local-first, BYOK in the OS
keyring, source-available daemon, published telemetry and security architecture — is nearly free for us and
structurally costly for each attacker: GitKraken monetizes the account wall it would have to demolish, the
first-party vendors monetize the lock-in, and every closed tool in this space pays for the account wall in adoption. In
a product whose thesis is "refuse blind trust," the marketing *is* the architecture. This moat can only be
lost voluntarily.

### 8.3 What is *not* a moat (kept honest, so the rest is believed)

Native rendering vs Fork (Fork is native, fast and loved; against Fork the edge is the agent thesis, not
the renderer) · orchestration (commoditized, free, and the vocabulary of the dead companies) · "agents in
worktrees" (a checkbox since Claude Code v2.1.49) · the name (every positioning line survives a rename by
design) · speed alone (Sublime Merge is the fastest client in the market, and dormant) · **any single
feature for more than ~2 quarters**.

### 8.4 Erosion dashboard (the standing watch)

| Watch item | Signal | Response |
|---|---|---|
| **Cursor Origin** (fall 2026) | Ships local execution + provenance | Re-plan within a quarter (the named tripwire) |
| **GitKraken Kepler** | Ships sandbox / queue / provenance semantics | Accelerate the pieces hardest to retrofit (queue semantics, audit integrity) |
| **Conductor** | Windows port announced | Press verification + queue, where they must re-architect |
| **Agent Trace** | Standard fragments, or a first-party renderer ships | Reassess Layer 4; the cross-vendor surface stays ours if Layer 1 holds |
| **Design partners** | Won't pre-commit to paid pilots 2 months after the agent layer launches | Revisit packaging before more enterprise build |
| **Anthropic / OpenAI ToS** | Further harness constraints | API-key path primary; local-model pressure valve; vendor-neutral adapters |
| **An incumbent acquires a Git client** | Any orchestrator/review-layer acquisition | Reassess Layer 1 within the quarter |
| **OpenAI Symphony** | The platform owner moving into orchestration itself | Already priced in — we don't sell orchestration |
| **Orca / Pane / Emdash** | Thin Windows-native agent managers eroding "Windows is served by nobody" | Windows alone was never the moat; press the pairing with queue semantics |
| **Docker** | Moves up-stack from sandboxes into orchestration | Unbeatable distribution if it happens; the integration (queue + audit + UI) stays the product |

---

## 9. Risks & registers

*Detail: [Master Market Document](business/go-to-market/Mainguard_Master_Market_Document_2026-07.md) Parts VII and XX.*

### 9.1 Risks, stated honestly

1. **Platform absorption (highest).** Claude Code Desktop ships worktrees + autoVerify + diff review; the
   Copilot app is GA with Agent Merge; Cursor Origin aims at agent-scale review; Kepler is free during
   preview; OpenAI Symphony shows the platform owner moving into orchestration. *Mitigation:*
   vendor-neutrality, local-first, and the compound pipeline — any single feature has a ~2-quarter window;
   the combination plus a real Git engine does not.
2. **Monetization ceiling.** Orchestration is worth $0; we believe verification + governance is worth
   $20–50. The design-partner program exists to prove willingness-to-pay *before* building the full
   enterprise layer.
3. **Execution capacity.** One founder and a forming team against funded incumbents. The Git core remains
   the prerequisite for every differentiator — protect that sequencing from launch-marketing pressure.
4. **BYOK / ToS fragility.** Anthropic's April-2026 enforcement showed vendors will constrain third-party
   harnesses when it suits them. API-key path primary, local-model support as the pressure valve,
   vendor-neutral adapters as a survival requirement rather than a feature.
5. **Timing.** Scope demos to what's real and script the rest, labeled. A polished honest demo beats a
   broad fragile one.
6. **NL-specific.** The home market is small — treat NL traction as *evidence*, not *revenue*. Grant
   windows are rigid, so the funding calendar must be maintained like a release calendar. Dutch ecosystem
   institutions can vanish, so anchor on communities rather than any single event.
7. **Hardware honesty.** ~4–6 agents on 16 GB is the local ceiling (WSL2 takes 50% of RAM by default);
   rate limits bind earlier. Never re-inflate the "50 agents" claim; cloud worktrees are the scale answer.

### 9.2 BYOK risk register (load-bearing)

Anthropic's ToS (enforced 4 Apr 2026) says OAuth tokens from consumer plans may not be used "in any other
product, tool, or service" — Mainguard drives the *official* binary (permitted), but a commercial
orchestrator piloting a consumer-subscription CLI is one policy clarification from being cut off.
*Mitigations:* API-key/pay-as-you-go as the primary documented path, explicit in-product disclosure when a
user connects via subscription OAuth, local-model support as the pressure valve, and monitoring for a
formal partner program. Rate-limit tiers break entry-level UX, so the AI gateway is a launch requirement.
No inference margin is an accepted local trade-off, recovered via cloud. Key-handling liability is answered
by OS-keyring storage, tmpfs injection, and (enterprise) Vault/Secrets-Manager integration — Mainguard
infrastructure never proxies or observes keys.

### 9.3 Platform constraints the plan is built on

Four verified limits that bound what can be promised. Each is a fact about the platform, not a
preference, and each has already shaped the architecture.

| Constraint | Consequence |
|---|---|
| **~4–6 concurrent agents on a 16 GB machine** — WSL2 takes 50% of host RAM by default, and API rate limits bind even earlier | The capacity claim is "several agents, safely." Scale beyond the laptop is the cloud tier's job, not a marketing adjective |
| **inotify does not propagate over 9P mounts**, and Git's builtin fsmonitor daemon does not function on Linux | Agent worktrees live on ext4; Git itself is the Windows↔Linux sync boundary |
| **Interactive rebase is unsupported in libgit2** | Rebase and worktree operations shell out to the Git CLI; LibGit2Sharp is retained for reads, status and commit |
| **Docker sbx installs natively on Windows** and cannot be nested inside a private WSL2 distro | Two viable engines: Docker Engine inside WSL2, or native sbx as an optional high-security backend |

### 9.4 Security posture

The container boundary is necessary but not sufficient. The dominant real-world threat is
**prompt-injection-driven exfiltration and host-side code execution through legitimate channels** —
writable repo mounts, credential mounts, post-merge dependency installs. Launch-tier requirements, and
marketable ones: default-deny egress with provider allowlists, per-sandbox credential isolation,
`--ignore-scripts` on host installs, and review-UI flagging of executable-config changes. For the
high-security tier, standard container isolation is insufficient against escape attempts — microVM
(Firecracker-class, in practice Docker sbx) or gVisor.

**Compliance clarification:** SOC 2 attests the *company's* controls; product features (audit trails, RBAC,
SIEM export, retention) *enable customers' compliance programs*. Both are needed for Enterprise; neither
substitutes for the other. Full prompt/output logging creates a sensitive data store — encryption at rest,
retention limits and redaction are part of the feature, not afterthoughts. *"If you merge it, you own it"*:
enterprises will ask for software-composition analysis and strict HITL gates for copyright provenance.

---

## 10. Metrics & the seed bar

*Detail: [Master Market Document](business/go-to-market/Mainguard_Master_Market_Document_2026-07.md) Parts XVII and X §10.2.*

### 10.1 Product KPIs

1. **Weekly active repos** (free-tier health) — instrument before any launch.
2. **Agent runs verified per week**, and **% of merges executed against non-stale verification**
   (verification staleness at merge — the merge-queue integrity measure).
3. **Plan-approval adoption rate** — % of worker tasks preceded by an approved plan; the leading indicator
   that the trust workflow landed.
4. **Concurrency rate** — average active agents per session (target > 1.5).
5. **Branch acceptance vs rejection ratio.**
6. **Free → Pro conversion** (target ≥ 2%); logo count and seat expansion in design-partner teams.
7. **Token spend per merged branch** — and cost of rejected work; feeds the enterprise budget story.
8. Auto-heal success / circuit-breaker escalation rate, once the repair loop exists.

**The investor-grade pair:** weekly active repos + agent runs verified/merged per week — the two numbers
investors cannot get from download counts.

### 10.2 The seed bar

**Conventional (2026):** ~5,000+ MAU **or** 500 paying customers **or** ~$300–500K ARR; burn multiple < 2×.
**AI-infra reality:** growth rate (15–20%+ MoM, organic) and logo quality outweigh absolutes — GitButler
raised on credibility plus HN demand pre-revenue; Conductor's A rested on logos, not seats.

**Our fundable story at the low end:** 3–5 named design-partner teams actively using the merge queue, a
strong retention curve on the free GUI, and weekly-verified-merges growth — roughly $10–50K MRR with that
shape is pitchable. Position as **agent infrastructure**, never "Git client": median seed valuations
concentrate in AI-positioned companies.

---

## 11. Open decisions

| Decision | State | Where it bites |
|---|---|---|
| **Founding-user discount** | Open. Recommended: **50% off Pro for its first two years**, locked per person, honourable at any future price | The number must be identical in the outreach email, the waitlist page and the beta welcome mail — see [`GTM.md`](GTM.md) §3 |
| **Trademark clearance** | Not filed. Owed before GA — USPTO/EUIPO, Nice classes 9 + 42 | Search-engine evidence is not registry data; a collision found after launch is brand damage, not paperwork |
| **Entity** | Holding BV → Werk-BV not yet incorporated | Gates WBSO for payroll, VFF, and the Innovatiebox — [`GTM.md`](GTM.md) §5–6 |
| **Download gating while unsigned** | Open. Recommended: **reply-gated** — it gets the conversation, delivers the SmartScreen warning personally, and keeps an unsigned binary off a public button | [`GTM.md`](GTM.md) §3 |
| **Raise shape** | €750k–1.5M pre-seed (grants extend it ~40%) *or* skip to a $2–4M seed on launch traction. Decide on launch data, not before | [`GTM.md`](GTM.md) §5 |
