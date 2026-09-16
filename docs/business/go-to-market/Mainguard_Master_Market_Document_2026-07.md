# Mainguard — Master Market & Business Document

**Date:** 2026-07-07
**Status:** Master reference — the single document for running Mainguard as a business. This is the
**full-depth consolidation**: every finding, table, template, and walkthrough from the market/GTM
corpus is contained here, plus new research on the Dutch market, Enschede/Twente, funding, legal
setup, hiring, and EU expansion. The original documents remain in the repo as history and for their
raw source lists, but this document is complete on its own.

**Consolidates (2026-07-07 state):**
`Mainguard_GTM_Plan_2026-07.md` · `Advisor_Pitch_August_2026.md` · `Mainguard_Market_Research.md` (v1) ·
`Mainguard_Market_Research_v2.md` · `Mainguard_Viability_And_Differentiation_2026-07.md` ·
`Mainguard_Competitor_Research_2026-07-07.md` · `Mainguard_MergeLoom_Deep_Dive_2026-07-07.md` ·
`Mainguard_Naming_And_Competitive_Landscape_2026-07.md` — **plus** new Netherlands research
(web-researched 2026-07-07; sources inline and in Appendix B).

**Evidence standard:** competitive and market claims from the corpus were verified 2026-07-06/07
via primary sources where possible. Competitor feature claims are "what they publicly say" unless
noted independently tested. MergeLoom claims carry the original tags: **[V]** verified by direct
page fetch, **[S]** search-snippet only, **[I]** inferred. New Dutch figures cite their source
inline; treat snippet-only claims as as-published.

---

## Contents

- **Part I** — The business in one page
- **Part II** — Market: demand, pain, and numbers (global + Netherlands)
- **Part III** — Competition: full landscape, deep profiles, MergeLoom, capability matrix, white space
- **Part IV** — Positioning, messaging, ICP & personas
- **Part V** — Product differentiation spine (D-1…D-6) & what not to build
- **Part VI** — Naming decision (Aegis research, candidate scans, MergeLoom collision)
- **Part VII** — Licensing, trust posture & technical/BYOK risk registers
- **Part VIII** — Pricing, monetization & business expansion
- **Part IX** — The Netherlands opportunity: Enschede base + target-company map
- **Part X** — Funding: the complete Dutch stack (grants and VC, equal depth)
- **Part XI** — Legal & company setup (Netherlands walkthrough)
- **Part XII** — Hiring & talent (Twente playbook)
- **Part XIII** — Customer discovery & design partners
- **Part XIV** — Launch plan & marketing (global sequence + Dutch layer + playbooks)
- **Part XV** — Pitch materials (deck, demo script, one-pager, failure modes)
- **Part XVI** — The August advisor meeting (full playbook)
- **Part XVII** — Metrics, KPIs & the seed bar
- **Part XVIII** — EU expansion & the AI-Act / sovereignty angle
- **Part XIX** — Dutch success-story playbooks
- **Part XX** — Risks, stated honestly
- **Part XXI** — Master action calendar
- **Appendix A** — Key research sources (global corpus)
- **Appendix B** — Key research sources (Netherlands, new)

---

# Part I — The business in one page

Mainguard launches as a **free, excellent, native, no-login Git GUI** (the trust wedge and
top-of-funnel), then monetizes the layer nobody has monetized: **making AI-agent output safe to
merge** — sandboxed local execution, deterministic test-verification before human review, a merge
queue that re-verifies stale branches, risk-ranked review with per-hunk agent provenance, and an
audit trail enterprises can show their compliance team. We are **Windows-first in a Mac-first
category** (the category leader, Conductor, is Mac-only; Windows is the largest developer OS), and
**agent-vendor-neutral in a locked-in category** (every first-party GUI manages only its own
agents). Free users come for the Git client; teams pay for review throughput and governance. The
2025–26 corpse pile (Bloop, Terragon) proves orchestration alone monetizes at zero — so we never
sell orchestration; we sell *trustworthy merges*.

**The one-liner:**

> **Agent CLIs made it trivial to produce ten branches an hour. Nothing on the market makes it
> safe to merge them. Mainguard is where agent work becomes trustworthy commits on main.**

**The enemy (at the core of everything we do):**

> **The blind merge** — code entering main on trust instead of proof.
> Rallying cry: **"Hope is not a merge strategy."**

The enemy is a *condition*, not a competitor — competitors can neutralize you with a release; a
condition can't. It is the villain every market number already describes: 87% distrust agent
accuracy, review time +91%, ~45% of AI code carries OWASP-class flaws, delivery stability
degrading — **and people merge it anyway**. The gap between "merged" and "verified" is what we
exist to close, and it grows on its own with every agent seat sold (by anyone). It is explicitly
**not** "AI code" (our users run six agents; we are pro-agent) and **not** "manual review"
(reviewers are our buyers — the enemy is asking them to pretend-review a firehose and merge
anyway).

Every feature is a weapon against it: sandboxes (unverified code never touches your machine) →
test gates (never reaches your eyes) → stale invalidation ("verified an hour ago" is a blind
merge in disguise — *validated-then-stale is unvalidated*) → risk ranking + provenance (no
anonymous diff walls) → audit trail (no unattributable line on main). The trust posture is the
same war turned inward: no login, local-first, FSL source-available, published telemetry —
**we refuse to ask users for blind trust, in a product whose whole point is refusing blind
trust.** And everything the enemy blesses — auto-merge, ungoverned autonomy, "CI-green = ship
it," LGTM culture — is already on the do-not-build list (§3.11, Part V). It frames every
competitor class without naming one: first-party vendors grade their own homework, cloud
reviewers add opinions not proof, MergeLoom validates then goes stale, orchestrators just
produce unverified branches faster. **Decision rule: if a proposed feature, partnership, or
piece of copy doesn't make a blind merge harder or a verified merge easier, it's off-thesis.**

**The 30-second elevator pitch (problem → solution → proof → ask):**

> "Every team is suddenly reviewing 2× more pull requests because AI agents write them, and
> review time is up 91% — developers merge code they don't fully trust, written by agents they
> can't fully see. Mainguard is a native desktop control plane that runs coding agents in isolated
> sandboxes, makes them pass your test suite *before* a human ever reviews the diff, ranks the
> review by risk, records which agent wrote which line, and only merges what's verified. The Git
> client underneath is already built and fast; we're launching the free tier this fall.
> [ASK — tailor to listener: advice / design partner / intro.]"

**Business model:** Free Git GUI (no login) → Pro $20/mo (verification pipeline, BYOK) →
Team/Enterprise $50+/seat (merge governance, provenance, audit/SIEM) → cloud worktrees
(usage-based, 2027). BYOK = no inference-margin risk.

**Where we stand (July 2026):** Git core shipped and working (T-01…T-33, 1,042 tests); Phase-2
agent platform (P2-01…P2-26) specified and in development on `phase2`; launch targeted ~October
(free client) + Q4 (agent layer); team scaling 1→5–6; pre-revenue by design until design partners
validate. Founder based in **Enschede, Netherlands** — which this document treats as a strategic
fact, not an afterthought.

**The NL angle in one paragraph:** the Netherlands gives Mainguard (a) one of Europe's densest
developer and .NET-enterprise markets within a two-hour radius, (b) a subsidy stack (WBSO,
Innovatiebox, MIT, VFF) that can fund 30–50% of R&D wages before any dilution, (c) a local
ecosystem in Enschede (Novel-T, Kennispark, UT/Saxion) that provides free incubation, talent, and
warm paths to exactly the East-NL .NET companies that fit the ICP, and (d) an EU regulatory moment
(AI Act + digital-sovereignty procurement) that makes "local-first, auditable, European" a sales
weapon no US competitor can copy.

**Strategic takeaways (Market Research v2 §8, still governing):**

1. **Ship developer-mode Mainguard fast; the category window is ~2–3 quarters.** GitKraken has
   distribution; Mainguard must own *sandboxed orchestration + merge governance* before they add
   containers.
2. **Make trust verifiable, not asserted:** source-available (FSL) daemon, published security
   architecture, independent audit report, local-only data path, BYOK.
3. **Sell the pipeline, not the sandbox:** plan approval → isolation → semantic verification →
   merge queue → human merge. That workflow is the product and the moat.
4. **Treat agent-CLI vendors as platform risk:** agent-agnostic adapters + local-model support are
   existential insurance, demonstrated by Anthropic's 2026 enforcement.
5. **Be honest about scale locally; monetize scale in the cloud.** "A few agents, perfectly
   managed" beats "50 agents" that OOM a laptop — and cloud worktrees convert the ceiling into
   revenue.

**Viability verdict (2026-07-03 re-assessment, confirmed 07-07):** the original headline ("run
swarms of agents in worktrees from a GUI") is **no longer viable as a differentiator** — that
window closed in early 2026; the plumbing is native to the agent CLIs and replicated by free
wrappers. **The product remains viable — arguably more than before — if the center of gravity
moves one step downstream:** from *running* agents to **verifying, governing, and merging what
agents produce**. Every trend (review-time explosion, distrust, security findings, audit mandates)
increases demand for that layer, and it is exactly the layer where a deep Git engine matters and
where the wrapper tools are weakest. Nobody currently combines: a real Git client (partial
staging, 3-way merge, interactive rebase, undo journal) + hardened local sandboxing + a merge
queue with re-verification + compliance-grade audit. Mainguard's locked architecture already
contains all four; what changed is which one leads the pitch.

---

# Part II — Market: demand, pain, and numbers (global + Netherlands)

## 2.1 Global market numbers (verified 2026-07-06; for the deck and the pitch)

Keep sources handy for diligence (Appendix A).

- **Developers:** ~47M worldwide (SlashData, start of 2025; 36.5M professional); IDC forecasts
  59.5M by 2029. GitHub: 180M+ accounts, one new developer per second (Octoverse 2025).
- **AI code tools market:** ~$7–9B today → **$20–30B by 2030–31, 22–27% CAGR** (Grand View,
  Mordor, Research & Markets consensus band).
- **The adoption/trust scissors (the "Why Now" slide):**
  - 84% of developers use or plan to use AI tools; but only **31% currently use agents** — the
    agent wave is still early (Stack Overflow 2025).
  - **90% of developers use AI**, median 2 hrs/day — yet only 24% trust the output "a lot," and
    AI adoption still **correlates with worse delivery stability** (Google DORA 2025).
  - **87% are concerned about agent accuracy; 81% about security** — trust at an all-time low
    (Stack Overflow 2025).
  - Copilot's coding agent alone authored **1M+ PRs in five months**; PRs merged +23% YoY; code
    churn +861% under high AI adoption while DORA metrics stayed flat (Octoverse 2025, Faros AI).
  - AI-assisted teams merge ~2× more PRs while **PR review time rose 91%**; PR volume is up
    **+29% YoY** against a fixed human review ceiling.
- **Windows:** the largest developer OS — 59.2% personal / ~48% professional use (Stack Overflow),
  while the entire polished-devtool wave (Conductor, Raycast, Warp, Zed) shipped Mac-first.
- **Compliance timing (state honestly):** EU AI Act GPAI enforcement powers activate Aug 2, 2026
  and Article 50 transparency applies — but the May 2026 "Digital Omnibus" provisionally
  **postponed high-risk obligations to Dec 2027**, and whether Article 50 "text" covers source
  code is unsettled. Pitch audit trails as *enterprise trust + where procurement is heading*
  (auditors are already asking — Codacy, AI-BOM movement), not as a deadline scare.

## 2.2 The verification bottleneck (the strongest finding in the corpus)

The bottleneck moved from generation to verification — this points *toward* Mainguard's actual
strengths:

- AI-assisted teams merged ~2× more PRs while **PR review time rose 91%**.
- **46% of developers actively distrust AI output accuracy** (up from 31%); **~45% of AI-generated
  code carries OWASP-Top-10-class flaws** in studies; **95% of developers spend real effort
  reviewing and correcting agent output**.
- Analyst framing (Futurum, SRLabs, and the general 2026 discourse): *"trust, not output, is the
  bottleneck"* — the missing layer is **independent verification and review of agent work**, not
  more agents.
- Enterprise governance became a dated demand: converging guidance (EU AI Act Art. 12, NIST AI
  RMF, OWASP LLM Top 10) demands append-only, **hash-chained audit logs** of agent actions;
  **individual attribution** (which human directed which agent action — service-account blindness
  is called out as the #1 compliance gap); and runtime policy enforcement rather than
  after-the-fact log review. None of the orchestrator GUIs ship any of this. Mainguard's planned
  P2-15/H-8.2 (tamper-evident hash-chained audit + plan-approval identity records) and P2-14/G-7.5
  (plan approval before worker start) were designed for exactly this — before the deadline existed.
- Meta built **RADAR** internally (1/3 revert-rate catch pattern) because it couldn't buy the
  verification layer — the make-vs-buy proof that the category is real.

## 2.3 Product demand & user pain (verified across v1 + v2 research; re-confirmed by category movement)

The demand signals, each mapped to the product answer:

- **Terminal clutter / lost situational awareness** running 4–6 concurrent agents; users miss
  agents blocked on approvals. → Validates the Activity Bar with status micro-badges and attention
  pulses; add OS-level notifications for waiting agents.
- **File collisions and semantic drift** in parallel edits. → Validates worktree isolation + the
  *semantic verification* step (containerized test run before human review). Semantic verification
  is a differentiator no shipping competitor has.
- **The overwriting problem:** agents make unilateral, destructive changes ("vibe coding") beyond
  the requested scope. → Plan approval + flagged-changes gate + undo journal.
- **Setup hell** (Docker, keys, toolchains) creates a high barrier to entry. → Validates pre-baked
  environment onboarding; the same finding cuts against a local Vibe Mode (see §8.4 sequencing).
- **Trust demands: dry runs, HITL gates, explainability.** Users want plan-before-code proposals,
  granular human-in-the-loop escape hatches (pause and steer without micromanagement), and clear
  rationale for *why* an AI made specific changes. → Plan-approval workflow promoted into the
  roadmap headline.
- **Quota rage.** Public backlash against opaque usage caps in Cursor/Windsurf confirms BYOK
  positioning: sell software, don't resell inference.
- **The duct-tape baseline (what power users do today):** `git worktree` + tmux to isolate CLI
  agents; a heavy manual "merge tax" from slicing tasks by file boundaries; redundant token burn
  as each agent re-indexes the codebase without shared context. Mainguard's job is to be obviously
  better than this workflow on day one.
- **Onboarding/UX lesson (v1, retained):** pure chat interfaces fail at scale. Mainguard must
  embrace **structured UI** (graphs for communication, trees for relationships, timelines for
  parallel execution), adapt views to the agent's task, and offer zero-config defaults.
- **Integrations demanded (v1, retained as roadmap candidates):** issue-tracker triggers
  (Linear/Jira via MCP — spin up worktrees when issues are tagged; now also validated by
  MergeLoom's entire product, §3.4) and a CI/CD feedback loop (intercept build failures →
  agent fixes → verified PR).

## 2.4 The classic Git-GUI market (context for the free tier)

Real but commoditized: GitKraken (largest paid share; AI commit messages / predictive conflict
detection), Tower (enterprise niche, AI commits), Fork ($59.99 one-time, fast, no AI), Sublime
Merge (dormant) — a mature, slow market where a new entrant selling "another premium Git client"
buys a knife fight over a fixed pie. The Git client is our **trust wedge and daily surface**, not
the business. GitKraken's free tier blocks private repos and requires an account — ours doesn't;
that asymmetry is the top-of-funnel play.

## 2.5 Netherlands market numbers (new research, 2026-07-07)

**Developer & ICT market size:**

| Metric | Figure | Source |
|---|---|---|
| People in ICT occupations, NL | **575,000** (Q4 2025; up from 555k a year earlier); CBS counted 622k ICT'ers across all sectors in 2024 | [CBS Digitalisering en kenniseconomie 2025](https://www.cbs.nl/nl-nl/longread/rapportages/2026/digitalisering-en-kenniseconomie-2025?onepage=true), [EURES NL labour market](https://eures.europa.eu/living-and-working/labour-market-information/labour-market-information-netherlands_en) |
| ICT companies registered | **~106,000** (2025, growing; growth mostly ICT services) | CBS (same longread) |
| ICT sector output | **~€98B (2023)**, ~2.8% growth forecast 2024–25, mainly constrained by labour shortage | [trade.gov NL ICT guide](https://www.trade.gov/country-commercial-guides/netherlands-netherlands-information-and-communication-technology) |
| ICT sector value-added growth | +3.2% (2024) vs +1.1% whole economy | CBS |
| ICT investment (companies+gov+households) | **€35.5B in 2024** = 15.5% of all Dutch investment; total ICT spend €82.3B (4.3% of all spending) | CBS |
| New ICT graduates | 10,000+/year from Dutch universities | [NICCT](https://nicct.nl/it-industry-in-the-netherlands/) |
| IT-services revenue growth | +4.4% YoY (Q2 2025) | [CBS via ChannelConnect](https://www.channelconnect.nl/mkb-en-ict/cbs-omzet-it-dienstverleners-groeit-in-tweede-kwartaal-met-44-procent/) |

**AI adoption (the "Why Now" slide, Dutch edition):**

- **1 in 6 Dutch companies used AI in 2025 — double 2023**; among 250+-employee firms it is
  **66%**, and 45% for 50–250 employees ([CBS](https://www.cbs.nl/en-gb/news/2025/09/increasing-use-of-ai-by-business),
  [NL Times](https://nltimes.nl/2025/12/14/one-six-dutch-companies-now-uses-ai-marketing-administration)).
- NL is **top-5 in the EU for enterprise AI adoption** (Eurostat `isoc_eb_ai`; EU27 average ~20%)
  ([Eurostat](https://ec.europa.eu/eurostat/statistics-explained/index.php?title=Use_of_artificial_intelligence_in_enterprises)).
- NL has **Europe's highest AI talent density: 10.9 AI professionals per 10,000 inhabitants**
  ([State of Dutch Tech 2026, Techleap/TNO/Invest-NL](https://techleap.nl/reports/state-of-dutch-tech-report-2026)).
- Primary AI use cases today: marketing/sales (35%), admin/management (32%), research/innovation
  (25%) — software-engineering agents are the *next* adoption wave, not the saturated one (CBS AI
  Monitor 2024).
- The biggest stated barrier for non-adopters is **"lack of experience" (74.6%)** (CBS AI Monitor)
  — which is precisely a governed-adoption pitch: Mainguard lets a cautious Dutch enterprise adopt
  agents *with* guardrails.

**Startup/VC climate:**

- **€2.64B venture capital invested in Dutch tech in 2025** (+26.2% YoY) across **11,301 active
  tech companies**, but deal count fell 14.5% — more money into fewer companies
  ([State of Dutch Tech 2026](https://techleap.nl/reports/state-of-dutch-tech-report-2026),
  [Invest-NL summary](https://www.invest-nl.nl/en/news/state-of-dutch-tech-2026-ready-for-scaling-to-global-size)).
- Deeptech takes 41% of Dutch VC and produces 41% of scaleups; US investors tripled their share of
  €50–100M breakout rounds to 40% while European participation fell to 21% — early rounds are
  local, growth rounds are foreign ([Viotta analysis](https://viottalaw.com/dutch-venture-capital-and-tech-market-2026-foreign-capital-deeptech-strength-and-the-dutch-scale-up-challenge/)).
- Dutch conversion of startups to scaleup status is 21.2% — a quarter of the US rate (80.9%) —
  the ecosystem's known weakness is international scaling, not starting (Techleap).

**Why NL over-indexes for Mainguard specifically:**

1. **.NET density.** Dutch finance, government, healthcare and manufacturing "have built on the
   Microsoft ecosystem for years" with structurally high demand for C#/.NET specialists and a
   chronically tight supply ([Computer Futures](https://www.computerfutures.com/nl-be/kenniscentrum/software-mobile-engineering/dot-net-developers-trends/),
   [AG Connect on C# popularity](https://www.agconnect.nl/tech-en-toekomst/development/populariteit-c-flink-gestegen)).
   The NL runs one of Europe's largest Microsoft-stack conferences (Techorama NL: 120 sessions,
   3 days, Utrecht) and active .NET user groups (dotNed, .NET Zuid). Mainguard is itself a .NET
   flagship app — a story this community loves.
2. **Windows-heavy enterprises.** The "Dana" persona (locked-down Windows machine, no premium
   native client) is the default Dutch enterprise developer.
3. **Compliance culture.** Dutch enterprises are early, structured adopters of EU regulation;
   audit-trail features sell here first (Part XVIII).
4. **Trust gap = our pitch.** High AI adoption + the CBS "lack of experience" barrier + EU AI Act
   = demand for *governed* agent adoption, which is Mainguard's exact wedge.

**Honest caveat:** the Netherlands is a **beachhead, not a market**. ~575k ICT workers is <1.5% of
the world's developers; the revenue plan stays global (HN-first, English-first). NL is where design
partners, subsidies, talent, and first enterprise logos come from — not where the ARR ceiling is.

---

# Part III — Competition: full landscape (July 2026)

## 3.1 Category state — the four layers

The AI development tool market has split into four layers, and the orchestration layer is the last
one without a dominant winner:

| Layer | Winners so far | Limitation Mainguard exploits |
|---|---|---|
| AI-native IDEs | Cursor, Windsurf | Single-workspace; parallel agents cause context bloat and file collisions |
| Terminal agents | Claude Code, Codex CLI, Aider, OpenCode | Powerful but unmanaged; swarms require tmux duct tape |
| Execution/isolation infra | Docker Sandboxes (sbx), GitHub cloud VMs | Infrastructure, not workflow; no merge pipeline, no review UX |
| **Orchestration & control** | *(contested)* GitKraken Agent Mode/Kepler, Copilot app, Conductor, indie tools | Nobody combines isolation + orchestration + governance + native UX |

## 3.2 The field at a glance (decision-grade summary)

| Class | Who | State (July 2026) | Their gap we exploit |
|---|---|---|---|
| Git clients + AI | **GitKraken** (Desktop 12 Agent Mode, Apr 2026; **Kepler** ADE, free preview June 2026), GitButler ($17M a16z Series A, agents-in-virtual-branches), Tower/Fork (AI commit messages only), Sublime Merge (dormant) | GitKraken is the aggressive incumbent; owns distribution | Electron heft; **host-level worktrees only — no sandbox**; no verification/merge pipeline; PE pricing complexity |
| Orchestrator GUIs | **Conductor** ($22M A, Mac-only, free), Superset (YC S26, 12k★), Emdash (YC W26, cross-platform), Nimbalyst (ex-Crystal), Sculptor (Imbue, containers + early output-verification), Omnara ($9/mo, mobile) | Exploded H2 2025, consolidated violently Q1–Q2 2026; **Bloop/Vibe Kanban dead Apr 2026, Terragon dead Feb 2026** | **No one verifies output** (Sculptor beta excepted), no merge queues, no audit, mostly Mac-first, **monetization unsolved at ~$0** |
| First-party absorption | **Claude Code Desktop** (Apr 2026 redesign: worktrees/session, parallel sidebar, autoVerify, diff review; Windows GA Feb 2026), **Codex app** (Mac Feb, Win Mar 2026), **Cursor 3** Agents Window (+ Graphite acquired Dec 2025 → **Origin forge, fall 2026**), GitHub **Copilot app** (GA June 17, 2026) + Agent HQ + enterprise agent control plane | The core "agents in worktrees + GUI" is now free table stakes from every vendor | **Single-vendor each**; model-self-review not deterministic test gates; no local merge orchestration; no per-change provenance; cloud/GitHub lock-in (Agent HQ) |
| Review / merge-queue layer | **CodeRabbit** ($550M val, 13M PRs, noisy), **Greptile** ($25M Benchmark), Graphite→Cursor, Mergify, Trunk, Baz ($17M, "review the plan"), Qodo, Macroscope ($40M), **MergeLoom** (solo bootstrap, £2–4/PR) | Cloud GitHub-apps first; CLIs are thin clients to cloud inference | **Nobody does local verification cockpits, per-hunk agent provenance, hunk risk-ranking, or AI re-review in the merge queue** — queues re-run CI only |
| Audit/provenance | git-ai (OSS line-level provenance, Thoughtworks Radar "Assess"), GitClear, vendor enterprise logs (Anthropic Compliance API, Codex admin logs), sigstore/gitsign, Agent Trace RFC | Demand being articulated; tooling immature; Meta's internal RADAR proves the pattern works (1/3 revert rate) | **No commercial product ties provenance to review + merge**; C2PA-for-code doesn't exist |

## 3.3 Deep competitor profiles (evidence-checked 2026-07-07)

### 3.3.1 GitHub Copilot app (Microsoft/GitHub)

**What they ship today (verified via github.blog + coverage):**
- Announced at Microsoft Build 2026 (June 2); **GA June 17, 2026** on Windows, macOS, and Linux.
- **Sessions = auto-managed git worktrees** — each session runs in "its own git worktree, a real,
  isolated copy of your branch"; no manual setup/cleanup.
- **Agent Merge** — shepherds a PR "through review, checks, and merge": monitors CI, tracks
  required reviewers, addresses failing checks; user picks how far it goes (drive CI green /
  address feedback / merge when conditions met). Single-PR shepherding, **not** a queue with
  cross-branch invalidation.
- **My Work view** — dashboard of active sessions, issues, PRs, and background automations across
  connected repositories.
- **Canvases** — bidirectional surfaces showing plans, PRs, terminals, deployments; developers can
  edit, reorder, approve, or redirect agent work. The closest anyone big has shipped to *plan
  approval*, though it is steering-while-running rather than a hard gate before start (gating
  semantics **unverified**).
- **Sandboxes:** local sandbox with "restricted access to filesystems, network connectivity, and
  system capabilities" (granularity/default-deny posture **unverified**); **cloud sandboxes** —
  fully isolated ephemeral Linux environments with cross-device session pickup, billed by compute
  seconds/GiB-seconds/snapshot storage (example: ~$78/mo for 10 devs × 3 hrs/day at 4 GiB).
- Adjacent: **GitHub Desktop 3.6 (June 26, 2026)** added worktrees + deeper Copilot integration;
  Copilot CLI gained voice input and scheduled tasks; SDKs in 6 languages.

**Pricing/momentum:** usage-based **"AI Credits" billing since June 1, 2026**; Pro $10 / Pro+ $39 /
Business $19/user / Enterprise $39/user + new **Copilot Max** tier. Local sandboxing in the seat;
cloud metered. Momentum maximal — default distribution to every GitHub/Copilot user.

**Overlap with Mainguard Phase 2:** worktree sessions, cross-repo dashboard, single-PR merge
automation, local+cloud sandboxes, canvas plan steering. High on orchestration; near-zero on
compliance audit, provenance, merge-queue re-verification, vendor-neutral intake.

**Mainguard beats them today:** native Git-client depth (partial staging, 3-pane resolver,
interactive rebase, undo journal); vendor neutrality (Copilot app is Copilot-first); local-first
privacy; no usage-metered billing surprise. **They beat Mainguard:** distribution, cloud
sandboxes/continuity, shipped Agent Merge, GA polish, brand.

**To get ahead:** ship the merge queue with **stale-verification invalidation** (Agent Merge does
not model cross-branch staleness) and **vendor-neutral intake** (pull Codex/Jules/Devin PRs through
the same pipeline — GitHub will never prioritize rival agents' PRs as first-class).

### 3.3.2 GitKraken Desktop 12 "Agent Mode" + Kepler ADE

**What they ship today (verified via gitkraken.com + PR coverage):**
- **Desktop 12.0 (announced April 16, 2026):** Agent Mode — single view to launch/monitor/manage
  parallel agent sessions; type a branch name, pick an agent (Claude Code, Codex CLI, Copilot CLI,
  Gemini CLI, OpenCode), click Start; GitKraken creates the worktree, runs setup commands, launches
  the agent. 12.0.1 maintenance release shipped since.
- **Kepler (shipped June 15, 2026):** standalone Agentic Development Environment,
  Windows/Mac/Linux. Multi-repo **Tasks** (select issues from Jira/Linear/Trello/GitHub/GitLab and
  Kepler sets up agents), kanban (Exploration → In Development → In Review → Done), session-status
  filters (Needs Attention / Active / Idle / Errored), console panels with mid-session redirection,
  side-by-side session comparison, diff review + staging + commit composition in-app. **PR-based
  task initiation** — start a task from a PR to handle review feedback (a partial form of
  external-PR intake). Agent-agnostic: Claude Code, Codex, OpenCode (Cursor/Copilot CLI per launch
  coverage).
- Kepler's page mentions **nothing** about merge queues, verification/test runs, sandbox
  isolation, provenance, or audit/compliance (checked directly).
- Earlier verified weakness (Market Research v2): worktree isolation is **host-level only** —
  agents execute directly on the user's OS with no container/VM boundary.

**Pricing/momentum:** Kepler free during limited preview; long-term access via GitKraken Pro /
Advanced / Business plans. Established paid install base; "Code Flow" positioning (SD Times).
Steady cadence, no adverse news found.

**Overlap:** highest structural overlap with Mainguard (git-GUI vendor moving into agent
orchestration, multi-repo tasks, in-app review/merge, Windows-native).

**Mainguard beats them today:** deeper git surgery (line-level staging validated against
`git apply`, 3-pane resolver, undo journal); planned sandboxing (GitKraken runs agents unsandboxed
on the host — **no isolation claims found**); audit/compliance absent from their public story.
**They beat Mainguard:** shipped and distributed to an existing paying base; multi-repo Tasks;
issue-tracker-driven intake; brand in the exact buyer segment Mainguard wants.

**To get ahead:** hardened sandbox + verification queue + audit — the three things Kepler's own
marketing does not mention. GitKraken is the competitor most likely to copy Mainguard's roadmap, so
speed on the compliance-grade pieces (hardest to retrofit) matters most.

### 3.3.3 Conductor (conductor.build)

**What they ship (verified via site/docs/changelog):** Mac app running Claude Code, Codex, **and
Cursor** agents in parallel, each in an isolated worktree/workspace with its own branch, terminal,
diff, review path. GitHub + Linear integration. Changelog weekly through **July 3, 2026
(v0.72.0)**; notable recent: Cursor support + a "Dispatcher" (June 8), create-workspace-from-issue
(June 16), OpenCode support (June 23), multiple run scripts (June 26 — a light verification hook),
browser preview, "New queue, diff diffs" (May 18 — a workspace/task queue, **not** evidenced as a
verification merge queue), Claude Sonnet 5 / Fable 5 model support within days of release.
**Still macOS-only.** No Windows/Linux signals; no sandboxing beyond worktrees, no audit, no
provenance found.

**Pricing/momentum:** free (uses your existing Claude/Codex login); paid collaboration features
still unshipped; **$22M Series A** (Spark + Matrix), YC S24, ~6 people, very fast cadence. Series-A
logos: Vercel, Notion, Ramp.

**Mainguard beats them today:** Windows (their absence is total), real Git-client depth, planned
sandbox/audit. **They beat Mainguard:** shipped product, capital, iteration speed, Mac mindshare,
GitHub PR-flow integration. **To get ahead:** own Windows/WSL2 before they port (their $22M could
fund a Windows build any quarter); ship verification-queue semantics they'd have to re-architect
for.

### 3.3.4 Nimbalyst (ex-Crystal)

**What they ship (verified):** free MIT open-source "visual workspace" over Claude Code, Codex,
OpenCode, Copilot: Monaco code editor, inline AI diff review, WYSIWYG markdown, Excalidraw
diagrams, CSV/data-model editors, session kanban, per-session worktrees, project hub;
macOS/Windows/Linux + iOS companion. Crystal repo formally deprecated Feb 2026.

**Pricing/momentum:** Free individual (MIT); **Pro $20/mo; Team ~$25/user/mo (conflicting sources
say $40 — pricing in flux, unverified); Enterprise custom.** Extremely aggressive comparison-page
content marketing — they rank for every competitor's name.

**Mainguard beats them:** git depth, native performance, security story. **They beat Mainguard:**
shipped multi-platform product, visual-editor breadth, SEO/content machine, iOS companion.
**To get ahead:** nothing structural — Nimbalyst competes for individuals; Mainguard's
governance/team pitch out-flanks it. **Copy their content-marketing playbook (comparison pages)
rather than features.**

### 3.3.5 Superset (superset.sh)

**What they ship (verified):** open-source "code editor for the AI agents era": parallel CLI
agents (Claude Code, OpenCode, Codex, Aider, Copilot, Cursor Agent, Gemini CLI) in isolated
worktrees with persistent terminals; built-in diff/file editor, chat panel, in-app browser, port
management; recently added **scheduled automations, TypeScript SDK, Slack bot, and an MCP server
letting external agents drive Superset itself**. Orchestrates ~5–7 agents reliably today; stated
goal 100 by end-2026. **macOS-only; Windows/Linux untested (open GitHub issue #499).**

**Pricing/momentum:** free tier + **Pro $20/seat/mo** (up from $15 on July 3 — pricing moved,
snippet-verified), enterprise tier; 3-person team, **YC Spring 2026**; #1 Product Hunt Feb 27, 2026.

**Mainguard beats:** Windows, git depth, security. **They beat:** shipped, automation/SDK/MCP
surface, velocity. **To get ahead:** same as Conductor — Windows + verification layer.

### 3.3.6 Parallel Code (parallelcode.app)

**What they ship (verified):** free MIT desktop app by solo dev Johannes Millan (maintainer of
20K-star Super Productivity); runs real terminal CLIs (Claude Code, Codex, Gemini, Copilot CLI,
Antigravity CLI) side by side, auto-created worktree per task, one-click merge, keeps your own
IDE; no API-key proxying. ~716 GitHub stars in four months. Cross-platform desktop (specific
Windows build **unverified** but likely).

**Assessment:** free/MIT, solo-maintained — same structural fragility as pre-shutdown Vibe Kanban.
Not a strategic threat; **a feature-parity floor** ("dispatch + one-click merge" must feel this
easy in Mainguard).

### 3.3.7 Vibe Kanban (community, post-Bloop)

**What they ship (verified):** kanban over worktree-isolated agents, 10+ backends, MCP task
creation, built-in browser with devtools. **Bloop announced shutdown April 10, 2026**; remote
services ended after 30 days; project moved to fully-local architecture under Apache 2.0,
community-maintained; founder quote: *"the vast majority are free users and we couldn't find a
business model that we could get excited about."* Users actively courted by Nimbalyst and
MergeLoom comparison pages. **Strategic value:** the clearest market evidence that **thin free
orchestration does not monetize** — plus a pool of orphaned users to target.

### 3.3.8 Composio Agent Orchestrator ("AO")

**What they ship (verified):** open-source meta-harness (repo `ComposioHQ/agent-orchestrator`,
~3.1k stars; "AgentWrapper" org rename in progress, package `@composio/ao`, details unverified):
plans tasks, spawns parallel agents in isolated worktrees, each with its own PR, and
**autonomously fixes CI failures, resolves merge conflicts, and responds to review comments** —
the furthest into autonomous PR lifecycle of any OSS tool. Claude Code, Codex, Aider, OpenCode via
plugin interface. Backed by Composio. Benchmarks itself against T3 Code, OpenAI Symphony, Cmux.

**Overlap:** autonomous CI-fixing overlaps Mainguard's verification loop with the opposite
philosophy — AO removes the human; Mainguard inserts a governed human gate. **To get ahead:**
position explicitly against ungoverned autonomy — *"AO merges when CI is green; Mainguard proves
it's still green after everyone else merged."*

### 3.3.9 Factory.ai (Droids)

**What they ship (verified):** enterprise agent-native platform; Droids across VS Code, JetBrains,
CLI, Slack, Linear; specialized parallel Droids (CodeDroid, Review Droid, QA Droid) for
migrations/refactors, run in parallel by the thousands; #1 Terminal-Bench (63.1%); explicitly
researching "how to converge multiple solution paths into one shippable result." Customers cited:
NVIDIA, Adobe, EY, Palo Alto Networks, Morgan Stanley, MongoDB, Bayer, Zapier.

**Pricing/momentum:** **$150M Series C led by Khosla (April 2026), $1.5B valuation, ~$220M total.**

**Mainguard beats:** local-first, vendor-neutral, git-native review. **They beat:** enterprise
scale, capital, benchmark halo. **To get ahead:** don't compete on execution; make Mainguard the
*governed intake point* for Droid-style cloud output (vendor-neutral PR intake).

### 3.3.10 Sculptor (Imbue)

**What they ship (verified via imbue.com/docs):** desktop UI running each agent in **its own
Docker container** (not just a worktree) — explicitly marketed against worktree-sharing-your-env;
**Pairing Mode** (one-click sync of an agent's containerized work into your local repo/git state);
session history/resume; container-startup optimizations ("10× faster to start"). **macOS + Linux,
Windows via WSL** (runs, but not Windows-native polish). Free during beta; requires Anthropic API
key or Claude Pro/Max (Claude-centric; multi-agent breadth unverified).

**Pricing/momentum:** free beta; no 2026 funding/pricing news found (Imbue's ~$200M raise
predates; trajectory unverified). **No public egress-control/default-deny claims found** —
isolation story is container filesystem/process; network posture unstated.

**Overlap:** closest competitor to Mainguard's sandbox thesis. **Mainguard beats:** Windows-first,
network egress control (theirs unstated), git-client depth, vendor neutrality, audit. **They
beat:** shipped container isolation today, Pairing Mode UX (**worth studying** — it solves "get
the agent's work into my hands" elegantly). **To get ahead:** ship WSL2 sandbox with **explicit
default-deny egress + published security architecture** — outflank on the dimension they leave
unspecified.

### 3.3.11 Dagger container-use

**What they ship (verified):** open-source MCP server + CLI: fresh container per agent on its own
git branch/worktree; logs, terminal attach, git-based review; **all environment changes
auto-committed, giving a git-native audit trail of agent activity**; works with any MCP-compatible
agent (Claude Code, Cursor, Goose). Still "early development" per Dagger. **Opportunity, not
threat:** Mainguard could interop with (or learn from) its auto-commit-as-audit pattern; its
existence normalizes "containerized agent + git review."

### 3.3.12 Cursor (Anysphere) — Cursor 3 + Origin

**Cursor 3 (April 2, 2026):** Agents Window as the central surface; up to **8 parallel agents** in
isolated worktrees, local (Composer 2) or **cloud isolation VMs**; run one prompt across multiple
models side-by-side; `/worktree` command; interface rebuilt around "the agent, not the file, is
the unit of work." Cursor also authored the **Agent Trace** attribution RFC and is expected to
emit trace records by default. Graphite acquired Dec 2025 → **Origin forge announced for fall
2026** — the only announced product aiming at "agent-scale review + merge queue," but a *cloud
forge*; our local-first counter-position is clean. Threat level: high for IDE-centric users; not
a git client, no governance layer. Earlier finding (v2): editor-centric parallelism fights the
product's own architecture; they compete for the same $20/mo wallet more than the same
job-to-be-done.

### 3.3.13 OpenAI Codex app + Symphony

**Windows since March 4, 2026** (plus macOS). Multi-agent threads organized by project, built-in
worktree support, built-in git tooling, voice, Skills, **Automations with cloud triggers**,
subagent workflows (parallel spawn + collect). Bundled in ChatGPT plans from $20/mo. **OpenAI
Symphony** (open-source orchestration spec + Elixir reference implementation: polls Linear,
auto-claims tickets, spawns Codex agents, delivers PRs with "proof-of-work"; engineering preview)
signals OpenAI moving up into the orchestration layer itself — the absorption risk the July-3 doc
predicted, now materializing.

### 3.3.14 Google Jules

Cloud agent on Gemini 3 Pro; **public API**, a **GitHub Action** (`jules-action`), CLI
(`jules remote new/list`, apply-patch-locally), opens PRs from the UI, **auto-fixes failing CI on
its own PRs and re-pushes**, reads/responds to PR review comments. Free tier. Jules is precisely
the kind of cloud-PR firehose Mainguard's vendor-neutral intake should consume.

### 3.3.15 GitHub Copilot cloud agents (pre-app context)

Ephemeral cloud VMs for issue→PR workflows. Single-agent, asynchronous, RAG-driven; no local
swarm, no cross-agent coordination, no BYOK. Strong for enterprises all-in on GitHub; weak for the
multi-agent local-control persona Mainguard targets.

### 3.3.16 Docker Sandboxes (sbx) — frenemy infrastructure

**Production GA January 30, 2026.** MicroVM per agent session (own guest kernel + own Docker
daemon, hypervisor boundary); supports Claude Code, Codex CLI, Copilot CLI, Gemini CLI, OpenCode,
Kiro, Docker Agent out of the box; secrets kept in OS keychain with **host-side proxy credential
injection**; **network policy presets: Open / Balanced (default-deny + common dev sites) / Locked
Down (all blocked unless allowed)**, per-run allowlists with wildcard domains, persistent config,
allowlist printed before launch for audit. **Works on Windows** (WSL2-based; community
walkthroughs exist; installs natively via winget/Windows Hypervisor Platform on Win 11 x86_64 —
Docker Desktop not required). **Free including commercial use** (only org-governance features
paid).

**Implications (Market Research v2, still true):** (a) Mainguard can *build on* sbx rather than
compete; (b) so can every competitor — isolation is commoditized; (c) Docker's own
agent-orchestration ambitions make it a potential category entrant with unbeatable developer
distribution. **This is the biggest change to Mainguard's sandbox calculus: default-deny egress on
Windows now exists as first-class plumbing** — but only as a CLI primitive, with no git-workflow
integration, no UI, no compliance logging.

### 3.3.17 Claude Code native sandboxing (context)

Anthropic ships OS-primitive sandboxing (bubblewrap on Linux/**WSL2**, Seatbelt on macOS) with a
proxy-based egress model: **no domains pre-allowed by default**, `allowedDomains` allowlist
enforced by hostname. Again: primitive, not product.

### 3.3.18 Indie/smaller field (one-liners)

- **Orca (stablyai)** — open-source ADE, macOS/**Windows**/Linux **+ mobile**, v1.3.50 (May 2026),
  2.1k+ stars; "run any coding agent with your own subscription."
- **Pane / runpane (dcouple) + Emdash** — per their own (self-interested but plausible) June-2026
  comparison, "the only agent managers with native, tested **Windows** support": unlimited
  parallel worktree agents, live diff viewer, x64/ARM64 installers, WSL-aware, orchestrator
  terminal. Thin and terminal-first, but it erodes "Windows is served by approximately nobody."
- **Intent** — coordinator-agent orchestrator (living spec → task decomposition → implementor
  waves); appears across Augment Code comparison pages; macOS-centric.
- **Claude Squad / Paneflow** — tmux/terminal multiplexers over worktrees; thin.
- **Canopy, Overstory, Forkbench** (v1-era indie wave) — power-user demand proof; no sandboxing,
  native terminals, or enterprise features; "feature roadmaps wearing GitHub stars" and an
  acquisition/hiring pool.
- **Cmux** — Mac agent terminal; **T3 Code** — seen only in third-party comparisons (unverified).
- **Omnara** — $9/mo mobile agent companion; its Feb 2026 pivot proved wrapping Claude Code's UI
  is unmaintainable against its release pace → integrate at the CLI/process boundary (PTY + git),
  never by wrapping vendor UIs.
- **Nimbalyst/Orca/Pane et al. collectively:** generic "spawn N agents" UX is now commoditized on
  Windows too.

## 3.4 MergeLoom (mergeloom.ai) — full deep dive

*The most important new find of the July-7 research; also a naming collision (Part VI). Method:
direct fetches of ~30 mergeloom.ai pages + GitHub org + LinkedIn + 5 searches. Tags: [V] verified
by direct fetch, [S] snippet-only, [I] inferred.*

**What it is, in one paragraph:** a **headless, workflow-embedded "governed ticket-to-code"
pipeline**: an approved ticket in Jira/Linear/GitHub/GitLab/Azure Boards/monday.dev triggers a run
that assembles cross-repo context from a pre-built index ("Context Engine"), has an AI provider
implement the change, pushes it through a six-gate validation-and-repair runway ("Quality Agents" +
"Diff Guard"), and hands a validated PR/MR back to the code host for **mandatory human review** —
with a ticket→run→validation→PR audit trail and per-run cost telemetry. **No IDE, no desktop app,
no Git client, no interactive agent session**; human touchpoints are the tracker, the code-host PR
page, and a web "Controller." Outcome pricing: £4 (cloud) / £2 (self-hosted) per opened PR/MR
after 50 free runs. [V]

**Company reality:** LinkedIn shows **1 employee, founded 2025, 25 followers** [V]; GitHub org has
2 repos (installer + Helm chart, ≤1 star, May–June 2026) [V]; blog/terms from May–June 2026 [V];
**no funding, founder, customer, or press trail found anywhere** [V]. A threat because of its
**positioning** (it occupies exactly the "governed AI delivery" ground Mainguard's P2 plan targets,
live and billing today), not its resources.

**Feature inventory (by lifecycle stage; all [V] unless tagged):**

| Stage | What they have | What they lack ([V absent]) |
|---|---|---|
| **Intake** | Tracker intake from Jira, GitHub Issues, GitLab Issues, Azure Boards, monday.dev, Linear; label/status/query routing; approval-gated start ("starts from approved work"); Jira Epic import & sync into "delivery campaigns"; Slack + Teams notifications; Confluence as context source | — |
| **Context** | "Context Engine"/vault: indexes approved repos + docs into persistent knowledge base; cross-repo architecture graph (files, symbols, APIs, events, service relationships); delta sync with monthly AI-credit refresh budgets; bounded context packs with source paths + confidence metrics attached through to the PR; AGENTS.md-aware; include/exclude scoping | — |
| **Generation** | Cloud: **Anthropic only**, MergeLoom-managed credits. Self-hosted BYO: Codex CLI, Claude Code CLI, OpenAI-compatible endpoints, Vertex AI, AWS Bedrock, Azure Foundry; static creds or workload identity. Standardized "Ticket, Context, Rules" run inputs | **Interactive steering mid-run: absent.** No terminal, no chat, no mid-run human interaction anywhere |
| **Validation** | Six-gate runway: 1 Clarity Check (ticket scope/AC) → 2 Investigation → 3 Validation (setup/lint/typecheck/tests/custom, per repo) → 4 Repair Loop (bounded auto-fix, "repair or stop", attempts logged) → 5 Specialist Review Agent (LLM diff review) → 6 Diff Guard (blocks oversized/off-scope diffs). Failed/blocked runs not billed | — |
| **Review/handoff** | PR/MR to GitHub/GitLab/Azure Repos with validation results, run history, cost telemetry attached; "No Auto-Merge" — merge stays behind customer's branch protection, always | **Review UI of their own: absent** — review happens on the code host |
| **Merge/coordination** | Epic → dependency-and-risk-ordered "delivery slices"; parallel workstreams; campaign controls (pause/resume, replan, skip/retry, per-slice cost + gate dashboard) | **Merge queue / stale re-verification: absent.** No re-validation when main moves, no cross-PR conflict handling; slices meet only at PR time |
| **Audit** | Ticket→run→requester→repo→provider→validation→PR traceability chain; three views (Controller / Ticket / Code Audit with line-level attribution); retry/repair history; cost telemetry per run (token/infra split, per-repo/provider); honest scope disclaimer (only work routed through MergeLoom is tracked) | **Tamper-evidence/hash chain/SIEM: absent. Compliance certifications: none** ("Security Review Ready" docs only) |
| **Admin/governance** | **Agent Fleets**: named codebase agents with mandate, scope, budget, file rules, cadence, review boundary (recurring maintenance); daily PR cap + open-review cap + AI budget per fleet; "self-learning" from rejected PRs [V claim, mechanics [I] — likely context/rules feedback]; Controller web UI; OIDC/SAML (enterprise only); no-training guarantee | **RBAC/SCIM/roles: not mentioned anywhere** |
| **Deployment** | Cloud (tenant-isolated, temp data deleted post-run) or self-hosted worker (Docker Compose / Helm, image 1.0, gateway + executors, local UI at 127.0.0.1:8010, **Linux/K8s only**); platform-managed secrets/workload identity | **Sandbox/egress hardening: not described. Public API/CLI/status page: none found.** Self-hosted worker still requires the SaaS controller (queue state, routing, PR metadata cross the boundary — not air-gappable). Env vars prefixed `JCA_*` → pre-rename codebase, plausibly Jira-first origin [V vars, [I] interpretation] |

**Business model [V]:** Cloud **£4/PR** (incl. 2,500 AI credits; extra £20 min per 2,000) ·
Self-hosted **£2/PR** (BYO provider) · Enterprise custom, 500+ runs/month minimum, adds OIDC/SAML.
Only runs that *open* a PR bill; extra runs in bundles of 5; no seats/contracts; 14-day first-charge
refund. Marketing claims ~£6–7 all-in per completed run vs "~£100/ticket" engineer time → "90%+
savings" (their £100 baseline is self-serving [I]). Target buyer: engineering leaders /
platform-compliance teams ("prevents vibe coding from becoming an organisational risk"); GBP
pricing + GDPR page suite → UK/EU-first [I]; self-serve (50 free runs) + Cal.com demo; paid
acquisition via **Reddit + LinkedIn ad pixels** [V]; a **161-post SEO blog essentially all dated
4 June 2026** — a bulk AI content farm [V/I]. Legal entity/jurisdiction **not disclosed** [V
absent]; SLA best-efforts [V]. **Read:** a solo-founder bootstrap that shipped a coherent v1.0 and
is buying its way into the exact "governed AI delivery" search space — low resources, high
positioning overlap, zero visible traction, but 6–12 months ahead of Mainguard's P2 milestones on
the *governance pipeline* story.

**Feature-by-feature vs Mainguard** (SHIPPED = on `main` today; PLANNED = Master Doc v2 P2-xx;
NOTHING = in no plan):

| MergeLoom feature | Mainguard status |
|---|---|
| Multi-tracker approval-triggered run intake | **NOTHING** (T-24 is a GitHub-Issues read panel; P2-14 approval is user-initiated in-app) → proposed **P2-27 work-intake adapters** |
| Persistent cross-repo Context Engine (vault, delta sync, confidence evidence) | **NOTHING** → proposed **P2-28 context vault** |
| AGENTS.md-style rules consumed by runs | Partial (agent CLIs read AGENTS.md themselves; no first-class rules surface) |
| Standardized run inputs | Partial (P2-14 plan approval + P2-08 gateway normalize the channel, not prompt content) |
| Vendor-neutral providers | PLANNED parity (P2-01 BYOK, P2-08 gateway, P2-22 adapter channel) + Mainguard adds *interactive* agents |
| Validation gate (test/lint/typecheck pre-review) | PLANNED **P2-10** (verification runs tied to main@sha) |
| **Repair loop** (bounded auto-fix) | **NOTHING** → add bounded repair iteration to P2-10 (fail → one scoped repair prompt in same sandbox → re-verify; attempts capped + journaled) |
| **Clarity check** (ticket-quality gate) | **NOTHING** (P2-14 approves plans, doesn't grade tickets) |
| **LLM Review Agent** on the diff | **NOTHING** (P2-11 risk-ranks mechanically) → optional LLM pass writing findings into the cockpit |
| **Diff Guard** (size/off-scope blocking) | Partial (P2-11 flagged-changes gate covers risky categories; add line-volume + touched-paths-vs-plan checks) |
| PR/MR handoff with evidence | SHIPPED T-23/25/26 + PLANNED P2-10/11/12 |
| Mandatory human merge | SHIPPED philosophy (P2-10 rejects "auto-merge of any kind") — parity by design |
| Merge queue + stale invalidation | PLANNED **P2-10** — **Mainguard leads; MergeLoom has nothing** |
| Epic decomposition into slices | Mostly NOTHING (P2-14 could grow multi-task plans mapped to N worktrees with declared dependencies) |
| Campaign controls (pause/replan/skip/retry) | Partial (P2-09 lifecycle, P2-08 admission; no replan/skip semantics) |
| Ticket→PR audit chain | PLANNED P2-15/16 hash-chained + SIEM — **Mainguard leads on integrity**, trails on ticket linkage |
| Line-level Code Audit | SHIPPED T-11 blame + PLANNED P2-11 per-hunk provenance on Agent Trace — **Mainguard leads when shipped** |
| Cost telemetry per work item | Partial (P2-08 budgets/spend; cost→ticket linkage unspecified) |
| **Agent Fleets** (recurring mandates + caps) | Mostly NOTHING → proposed **P2-29 fleet scheduler** on P2-08 admission control |
| **Self-learning from rejected PRs** | **NOTHING** → persist P2-11 verdicts + P2-10 failures as per-repo human-editable, in-repo, hash-chain-audited "lessons" — *governed* learning vs their opaque claim |
| Cloud execution / self-hosted VPC worker | PLANNED P2-25 (guardrails now) / different topology (desktop = inherently self-hosted; no K8s worker planned) |
| Sandbox hardening / egress control | PLANNED P2-07 — **Mainguard leads; MergeLoom claims nothing** |
| Interactive terminals / conflict radar / commit curation / full Git client | SHIPPED+PLANNED (P2-03/04/18, P2-19, P2-20, T-01…T-33) — **Mainguard leads absolutely; their agents emit one PR, fire-and-forget** |

**Gaps where they beat our plan — match + leapfrog (G1–G8):**

- **G1 Tracker-driven intake** (their core loop; our biggest hole). *Match:* P2-27 adapters
  (GitHub Issues + Jira first) — a labeled/approved ticket enqueues a P2-14 plan-approval item.
  *Leapfrog:* intake lands in the **local verified merge queue** with conflict radar — agents that
  coordinate with each other and with your working tree, which a headless SaaS structurally cannot
  see. Sell **"ticket-to-merged, not ticket-to-PR."**
- **G2 Context Engine.** *Match:* P2-28 daemon-side persistent repo index (symbols, APIs, docs,
  AGENTS.md rules) with delta refresh keyed on Git objects; bounded context packs attached to
  spawns and review. *Leapfrog:* build evidence from Git-native primitives they don't have —
  blame (T-11), file history (T-12), reflog (T-20) — every context claim links to a commit,
  rendered **inside the review cockpit next to the hunk it influenced**.
- **G3 Repair loop + Review Agent + Diff Guard.** *Match:* bounded repair iteration in P2-10;
  diff-size/off-scope policy in P2-11; optional LLM review pass. *Leapfrog:* repairs happen in a
  **visible terminal the human can take over mid-repair**, in a hardened default-deny sandbox —
  glass box vs their black box; "pairing mode for repairs."
- **G4 Agent Fleets.** *Match:* P2-29 scheduled agent profiles (cadence, repo scope, path rules,
  daily PR cap, open-review cap) + P2-15 audit of mandate/scope/budget. *Leapfrog:* fleet output
  enters the stale-invalidation queue + conflict radar so recurring agents *never* land
  conflicting/stale PRs — they mitigate reviewer-flooding with caps; we eliminate it with
  coordination. Fleets run **overnight on the developer's own hardware at ~£0 marginal cost**.
- **G5 Self-learning.** *Match:* per-repo lessons file injected into subsequent agent context.
  *Leapfrog:* human-editable, versioned in-repo, hash-chain audited — an auditor can see exactly
  what the system learned and when.
- **G6 Epic decomposition.** *Match:* multi-task approved plans mapped to N worktrees with
  dependencies. *Leapfrog:* schedule slices through the merge queue with **conflict-radar-aware
  ordering** (P2-19 predicts collisions *before they run*; they order by human-declared dependency
  and still collide at PR time).
- **G7 Cost-per-outcome telemetry.** *Match:* P2-08 StreamSpend keyed to task/ticket + branch,
  surfaced in cockpit and on the PR. *Leapfrog:* report **cost per *merged* change and cost of
  rejected work** — the number a VP wants and one MergeLoom can't compute (it never sees
  post-handoff outcomes). Publish a comparison calculator: BYOK local ≈ token cost only vs their
  £2–4 platform fee per PR.
- **G8 Time-to-market (meta-gap).** They are live, billing, with a working self-hosted worker —
  today; Mainguard's equivalent story lands at M7/M7.5. Mitigate: ship the P2-10/P2-11 vertical
  slice early and **publicly stake the "governed merge" claim before their SEO wall owns the
  category vocabulary.**

**Where we beat them — press it:** (1) a shipped, deep native Git client (they have *no* client;
demo "ticket → agent → conflict-resolved, rebased, signed, undoable merge" in one window);
(2) local execution + hardened sandbox ("your laptop is the VPC" — their self-hosted mode needs
Linux/K8s *and* a live SaaS controller, zero sandbox story, undisclosed legal entity, best-efforts
SLA); (3) merge queue + stale invalidation (name the failure mode publicly: **"validated-then-stale
is unvalidated"** — intrinsic to their architecture); (4) review-cockpit depth + Agent Trace
provenance (theirs is line attribution in a web controller); (5) interactive agents + commit
curation + conflict radar ("govern the agent *while it works*, not just its output");
(6) stronger audit integrity (hash-chained, SIEM, `audit verify` vs plain traceability — publish
an "audit-grade vs audit-flavored" comparison); (7) structural pricing attack (no per-PR meter on
your own hardware; undercuts any team >~50 PRs/month); (8) Windows (their worker is
Linux/K8s-only).

**Their weaknesses / attack surface (full list):** headless & web-only (every human touchpoint
rented); no merge-coordination layer; cloud = Anthropic-only despite "vendor-neutral" positioning;
self-hosted still phones home (not air-gappable); no sandbox/egress story for the thing executing
AI code; audit without integrity (no hash chain/signatures/SIEM/SOC 2/ISO/RBAC/SCIM; OIDC/SAML
enterprise-gated); one-person company selling governance (procurement-killing facts for their own
buyer); SEO content-farm optics (bought, not earned — contestable with genuine content); platform
gaps (no Bitbucket, no API/CLI/webhooks, no status page, GBP-only); per-PR pricing friction at
scale; `JCA_*` env prefix hints at a thin recently-renamed codebase behind broad marketing.

## 3.5 Capability-gap probes (a)–(e)

**(a) Merge queue with re-verification / stale invalidation:** GitHub's server-side merge queue
does test PRs against the future merged state and recreates/reruns merge groups when a PR fails
out — functionally stale-invalidation, but **server-side, GitHub-hosted, CI-bound, PR-only, and
not agent-aware**; Mergify similar. **No desktop/local/agent-orchestration product ships it.**
Copilot Agent Merge shepherds one PR; AO/Jules auto-fix CI per-PR. Field empty locally. Mainguard
must position against "just use GitHub merge queue": works pre-PR, locally, across N agent
branches, without CI round-trips, and for repos not on GitHub.

**(b) Per-hunk agent provenance in review:** **Agent Trace** (announced by Cognition Jan 29,
2026; released as an RFC by Cursor; backing from Cloudflare, Vercel, Google Jules, Amp, OpenCode,
git-ai; Devin building support) defines JSON trace records mapping code ranges →
conversations/contributors, file- and line-range granularity, human/AI/mixed classification.
**Not yet default in any major product, and no GUI renders it in a diff/blame review surface.**
Adjacent: git-ai (git-notes `refs/notes/ai`), "hunk" (terminal diff viewer), repowise "agent
provenance." Standard forming; review-surface empty.

**(c) Hash-chained / EU-AI-Act-grade audit for coding agents:** standalone compliance products
exist — **Agent Audit** (hash-chained receipts + optional RFC 3161 notarisation, marketed at the
Aug 2, 2026 date), **Asqav** (ML-DSA/FIPS 204-signed records for Arts. 12/19/26), **Compliora**
(SHA-256 seals), agentlifylabs/Aegis (Ed25519 audit log). **None is integrated into a coding
tool, git client, or orchestrator.** Caveat: Article 12 requires automatic event
logging/traceability but **does not literally mandate cryptographic immutability** —
hash-chaining is emerging best practice, so market "audit-grade evidence," not "legally required
crypto."

**(d) Default-deny egress sandboxing on Windows/WSL2:** exists at the **primitive** level from
two vendors — Docker Sandboxes (microVM + Balanced/Locked-Down presets, Windows-capable) and
Claude Code's bubblewrap-on-WSL2 + proxy allowlist (no pre-allowed domains); Codex CLI also has a
native Windows sandbox. **No polished GUI product integrates default-deny egress with git
workflows, review, and audit on Windows.** Claim sharpened from "nobody has it" to "nobody has
productized it."

**(e) External/cloud-agent PR intake into a local verify-review-merge pipeline:** partial
gestures only — Kepler's PR-based task initiation, Devin Review, Codex GitHub code review, Jules
responding to its own PR comments. **Nobody offers vendor-neutral intake of arbitrary agent PRs
(Codex + Jules + Devin + Copilot) into one local verification/review/merge pipeline.** Field
effectively empty.

## 3.6 Capability × competitor matrix

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

## 3.7 Where the field is empty — ranked by demand evidence

1. **Local merge queue with test-verification + stale invalidation** — nobody ships it
   client-side; GitHub's server-side queue proves the demand pattern (it exists because "passed CI
   on a stale base" is a real, named failure) and the "AI subagents delete your features" press
   documents the agent-specific pain. Strongest combination of demand evidence + empty field.
2. **Vendor-neutral external-agent PR intake → local verify/review/merge** — Jules/Codex/Devin
   mass-produce PRs (Jules has an API + GitHub Action); every producer keeps review inside its own
   silo; nobody aggregates. Demand grows with every cloud-agent seat sold — by others.
3. **Per-hunk provenance rendered in a review UI** — the Agent Trace RFC's backer list is itself
   demand evidence; emitters are coming, **no consumer/renderer exists**. First GUI to paint trace
   records into diff/blame gutters defines the category.
4. **Integrated compliance-grade audit inside a dev tool** — Aug 2, 2026 enforcement is weeks
   away; standalone audit vendors exist *because* demand is real, but none can attribute actual
   code changes. The Git side is unclaimed.
5. **Productized default-deny egress on Windows** — primitives shipped (Docker sbx, Claude Code
   sandbox), proving vendor conviction; the integrated GUI + git + audit product does not exist.
   Window narrowed since July 3 but open at the product level.
6. **AI rate-limit/budget gateway for parallel local agents** — multiple 2026 posts document the
   "9 agents, one quota, everything 429s" failure; gateway vendors solve API traffic, nobody
   solves it inside an orchestration desktop app (Copilot's AI-Credits metering bills the problem,
   doesn't prevent it).
7. **Hard plan-approval gate with identity records** — everyone has soft steering (canvases,
   approval modes, plan drafts); nobody binds "which human approved which plan" into an auditable
   gate. Weaker standalone but the linchpin that makes #4 sellable.
8. **Cross-worktree conflict radar** — nobody ships live conflict prediction across N agent
   worktrees; demand evidence is indirect (merge-conflict horror stories) — fast-follow, not lead.

Mainguard's locked architecture contains 1–5 and 7 by design; 6 and 8 are specced (P2-08, P2-19).
**That is the product story.**

## 3.8 New threats watchlist (since July 3)

- **MergeLoom** — see §3.4; positioning + naming collision.
- **OpenAI Symphony** — the platform owner moving into orchestration itself.
- **Cursor Origin** (fall 2026) — the only announced "agent-scale review + merge queue" product;
  cloud forge; **the #1 tripwire** (see Part XX).
- **Orca / Pane / Emdash** — erode "Windows served by nobody"; thin, but watch.
- **Agent Trace becoming the attribution standard** — opportunity-shaped threat: if Mainguard's
  provenance stays commit-trailer-proprietary it will be non-standard within a year → adopt the
  RFC as interchange, keep trailers as fallback for non-emitting agents.
- **Copilot AI-Credits + Copilot Max** — changes enterprise budget conversations; also creates an
  opening ("your metered agent bill needs a budget gateway").
- **GitHub Desktop 3.6 worktrees** — even the free casual client normalizes worktrees.
- **Docker's own orchestration ambitions** — unbeatable developer distribution if it moves up-stack.

## 3.9 How we beat each class

- **vs GitKraken (the incumbent):** don't fight the Git-GUI knife fight on features — fight on
  *native performance* (their #1 complaint is Electron slowness; they maintain an official
  performance-troubleshooting page), *free-tier generosity* (theirs blocks private repos and
  requires an account; ours doesn't), and *depth* (Agent Mode/Kepler launch agents but execute
  them unsandboxed on the host with no verification or merge pipeline). Expect Kepler to add
  features fast — our defense is the compound pipeline, not any single checkbox.
- **vs Conductor/Superset/Emdash (the orchestrators):** concede orchestration parity quickly,
  then differentiate where they structurally can't follow without rebuilding a Git client:
  verification gates, merge queue, undo journal, partial staging of agent output, interactive
  rebase to curate agent WIP. Also: Windows. Also: they're free and unmonetized — we don't need
  to beat them on price, we need to be worth paying for where they aren't.
- **vs first-party (Copilot/Claude/Codex/Cursor):** be Switzerland. Their GUIs manage only their
  own agents; teams already mix vendors per task ("agentmaxxing" is a documented manual practice).
  Vendor-neutral + local + deterministic gates is the ground they won't take — each vendor is
  incentivized to lock in, and none will make its GUI a better home for a rival's agent. Watch
  **Cursor Origin** most closely; it's cloud — our local-first counter-position is clean.
- **vs the review layer (CodeRabbit et al.):** their weakness is noise (audited: ~35% of
  CodeRabbit comments genuinely useful) and cloud-only posture. Deterministic "your test suite
  passed/failed in the agent's sandbox" is a fact, not an opinion — market it as the antidote to
  AI-review fatigue. Longer-term, integrate one of them as an optional signal rather than compete
  head-on.
- **vs "free" everywhere:** the failure ledger is the moat map. Bloop died with 27k GitHub stars
  and thousands of DAU because free users don't convert on orchestration. We charge for what
  teams demonstrably pay for in adjacent markets: review throughput (CodeRabbit $24–48/dev/mo,
  Graphite ~$40), merge reliability (Mergify $8+), and governance (enterprise premiums).
- **vs MergeLoom specifically:** §3.4 "press it" list; the one-sentence version — *"they stop at
  ticket-to-PR; we govern ticket-to-merged, interactively, on your own hardware, with integrity
  guarantees."*

## 3.10 Failure lessons we've priced in

- **Bloop/Vibe Kanban (dead Apr 2026):** thin orchestration + free users = no business. → We lead
  with a paid-for job (verification/governance), and the free tier is a *Git client*, which has
  independent daily-driver value.
- **Terragon (dead Feb 2026):** cloud agent-running gets absorbed by the platform vendors (its
  shutdown notice literally pointed users to Claude Code Web). → We don't compete on running
  agents in *our* cloud; local-first, their subscriptions.
- **Kite (2022, canonical):** individual developers do not pay. → Revenue plan is
  teams/enterprise; individuals are the funnel.
- **Warp:** login walls + closed source + telemetry = years of trust tax; won its best press by
  *removing* them. → No-login free tier, FSL daemon, published security architecture from day one.
- **Omnara's pivot (Feb 2026):** wrapping Claude Code's UI is unmaintainable against its release
  pace. → Integrate at the CLI/process boundary (PTY + git), never by wrapping vendor UIs.

## 3.11 Standing competitive recommendations (July-7 refresh)

**Accelerate (in order):**
1. **D-1 merge queue with stale-verification invalidation** — still the emptiest high-demand
   square; every month unshipped, Copilot's Agent Merge and Kepler get one release closer.
2. **Vendor-neutral external-PR intake** — Jules API + GitHub Action and Codex integrations make
   this cheap to build *now*; Kepler's PR-based tasks show competitors circling.
3. **D-3 audit, split:** ship the *evidence pack* (hash-chain + identity + `audit verify` + SIEM
   export) before the Aug-2 marketing moment even if RFC 3161 anchoring trails; claim
   "audit-grade/tamper-evident," not "EU-required."
4. **Adopt Agent Trace** as the provenance interchange format — emit it from the orchestrator
   *and* render it (plus git-ai notes) in diff/blame gutters; first consumer/renderer of the
   standard beats a proprietary trailer scheme.

**Add:** 5. evaluate Docker sbx as an optional "maximum isolation" backend beneath the native
WSL2 sandbox (differentiation is the integration — worktree + queue + audit + UI — not the
hypervisor); 6. study Sculptor's Pairing Mode for the review cockpit; 7. treat the MergeLoom
naming collision as a forcing function (Part VI).

**Keep, lower priority:** rate-limit gateway (D-6 — uncontested and real, but retention not
acquisition); cross-worktree conflict radar (fast-follow).

**Drop / do not build:** cloud execution (Codex/Jules/Factory own it — intake their output
instead); autonomous CI-fix/auto-merge parity with Composio AO (opposite of the governed thesis —
market against it); generic spawn-N-agents UX beyond parity (commoditized, now on Windows too);
visual-editor breadth à la Nimbalyst (different buyer); AI commit messages / chat-with-your-repo
gimmicks (GitKraken owns the checkbox).

---

# Part IV — Positioning, messaging, ICP & personas

## 4.1 Positioning statements (three registers, all locked)

**The market-facing sentence (updated July 7):**

> *Every vendor now sells you agents that produce branches; GitHub will even merge its own.
> Mainguard is the neutral control plane that verifies, attributes, and audits what **any** agent
> produced — locally, on Windows, behind a default-deny wall — before it touches main.*

**The product paragraph (Viability doc §4):**

> Agent CLIs made it trivial to *produce* ten branches an hour; nothing on the market makes it
> safe to *merge* them. Mainguard is the Git-native control plane for the agent era: a premium Git
> client underneath, and on top — sandboxed local execution with default-deny egress, a merge
> queue that re-verifies anything that goes stale, a review cockpit that ranks agent diffs by
> risk and provenance, and a tamper-evident audit trail that satisfies the EU AI Act. Run your
> agents wherever you like — Claude Code, Codex, cloud PR bots — Mainguard is where their work
> becomes trustworthy commits on main.

**The "engineering manager" framing (Market Research v2 §4.1):**

> **Mainguard is the engineering manager for your AI agents.** Run several coding agents in
> parallel, each in its own sandboxed worktree, with plans you approve before code is written,
> tests that run before you review, and merges that never happen without you. Your code stays on
> your machine; your keys stay in your keyring; every agent action is auditable.

Three deliberate changes locked in v2: "several agents" (not "swarms of 50+" — indefensible on
consumer hardware); the plan-approval step promoted into the headline (cheapest trust-builder);
auditability as core messaging (the enterprise moat).

## 4.2 Message hierarchy by audience

| Audience | Lead message | Supporting proof |
|---|---|---|
| Agent power users (running 3–6 Claude Code/Codex sessions) | "Never let an agent break your working directory again. Review five agent branches in twenty minutes, safely." | Sandboxes, worktree isolation, test-gates, risk-ranked review cockpit |
| Windows / .NET enterprise developers | "The premium native Git client Windows never got — and the only agent runner built for WSL2." | Native Avalonia perf, no Electron, no login, local-first |
| Engineering managers / CTOs (the buyers) | "Velocity *with* governance: agents that must pass tests before review, plus an audit trail of every agent action." | Merge queue + re-verification, per-hunk provenance, SIEM export roadmap |
| Investors | "The verification layer for the agent era — the bottleneck moved from writing code to trusting it, and we own the Git-native chokepoint." | SO 2025: 87% distrust agent accuracy; DORA 2025: stability degrading; Copilot agent alone shipped 1M PRs in 5 months |
| Vibe coders (Phase C only, cloud product) | "Ship products, not Git commands." Abstraction and magic; zero-terminal end-to-end builds. | Deferred to hosted Mainguard Web (see §8.4) |

## 4.3 Framing shortcuts (use deliberately)

- **"Hope is not a merge strategy."** The enemy line (Part I) — use it as the spine of every
  talk, essay, and launch post; the enemy is the **blind merge**, never a named competitor,
  never "AI."
- **"Conductor for Windows — with verification."** Journalists and Redditors will reach for an
  analogy; hand them this one. Conductor is the funded category leader and is Mac-only with no
  verification layer, so the comparison flatters us twice.
- **"Your agents' work, test-verified before you see it."** The single feature no shipped
  competitor has (only Imbue's Sculptor gestures at it).
- **"Validated-then-stale is unvalidated."** The named failure mode that positions against both
  MergeLoom and every CI-bound queue.
- **Never** lead with "swarm," "50 agents," or "orchestration" — commoditized, and the words the
  dead companies used.

## 4.4 Trust posture (decide before HN decides for us — locked)

The Warp precedent: a closed tool between a developer and their code paid a multi-year trust tax
(login requirement, telemetry) and won its best press by *removing* those. Locked posture:

1. **Free Git GUI requires no account/login.** Ever. Login only where a cloud feature genuinely
   needs it.
2. **Local-first as the second sentence on the homepage:** code never leaves the machine; agents
   run in local sandboxes; BYO keys in the OS keyring; default-deny egress.
3. **Source-available daemon (FSL)** + published security architecture doc + opt-in telemetry
   with a published schema. GitButler's fair-source reception shows this neutralizes the objection.
4. Verification itself is marketed as a trust feature: "agents must pass your tests before you
   review their code" resonates with the 87% who distrust agent output.

**EU addendum (new):** price and invoice in EUR alongside USD; publish a GDPR/data-locality
statement; **state the legal entity + jurisdiction on the website from day one** — MergeLoom's
failure to do so is a procurement-killer we must not replicate (and can contrast against).

## 4.5 Ideal customer profile (first paying team)

A **10–100 developer product company or agency, Windows-heavy or mixed-OS, already running
agentic CLIs** (Claude Code, Codex CLI, OpenCode), where an EM or staff engineer owns the "our
review queue is drowning and I don't trust what the agents merged" problem, and where compliance
or client contracts make "who wrote this code and was it tested" a real question. .NET shops,
fintech/insurance/healthcare ISVs, and government contractors over-index on every axis — the
Netherlands is full of exactly these (Part IX).

## 4.6 Personas (in adoption order)

1. **"Sam" — the agent power user (launch persona).** Senior IC, runs 3–6 parallel agent sessions
   today via tmux/worktrees or Conductor-on-Mac. Pain: terminal clutter, agents stepping on each
   other, reviewing a firehose of diffs, one API key rate-limiting everything. Finds tools on HN,
   r/ClaudeAI, X. Converts on: sandbox demo, review cockpit, rate-limit gateway. Pays $0→$20/mo.
2. **"Dana" — the Windows/.NET professional (volume persona).** Enterprise dev on a locked-down
   Windows machine; current options are a 2015-era free client or GitKraken's Electron app. Pain:
   no premium native client; agents arriving at work with no safe way to run them. Converts on:
   speed, polish, WSL2-native agent runner. Individual $0; her *company* pays.
3. **"Priya" — the engineering manager (buyer persona, phase B).** Owns review throughput and is
   accountable for AI governance. Pain: PR volume +29% YoY against a fixed human review ceiling;
   audit questions she can't answer. Converts on: merge queue metrics, provenance, audit export.
   Pays $50+/seat. **Do not sell to Priya before the governance features exist.**

**Stakeholder note (v1, retained):** primary users are developers/DevOps (and later
non-technical founders in Phase C); economic buyers are EMs and CTOs seeking velocity combined
with safety and auditability. Expected willingness-to-pay for the pro tier: ~$15–30/mo.

## 4.7 Explicit non-targets (for now)

- Vibe coders / non-technical founders — Phase C, cloud product (§8.4).
- Teams all-in on cloud agents with no local development loop (Jules-only shops) — until the
  "attach to external agent PRs" intake ships.
- OSS maintainers wanting a free-forever everything — we serve them a great free Git client, but
  they are not the revenue plan (Kite's law: individual developers do not pay).

## 4.8 User-base overlap analysis (from the naming/landscape research)

- **Direct overlap:** individual developers and small teams experimenting with running multiple
  coding agents locally — exactly Conductor's, Nimbalyst's, Superset's, and Parallel Code's
  audience, and Mainguard's near-term target.
- **Larger existential overlap:** any team already using GitKraken (sells into teams/enterprises)
  or paying for GitHub Copilot (nearly every GitHub org) can get baseline functionality with
  **zero switching cost** — the most serious competitive risk independent of naming. Mainguard
  needs a clear reason to switch: the verification pipeline + native depth + neutrality is that
  reason; single features are not.
- **Adjacent, not overlapping:** security/governance buyers of the "Aegis"-branded tools (Part
  VI) are CISOs/platform-security teams procuring a compliance layer, not developers picking a
  Git GUI — different buyer, different budget line; low direct competition but high
  *brand-confusion* risk if name + enterprise-governance pitch resemble theirs.

---

# Part V — Product differentiation spine (what to build that others can't or won't)

Ordered by (defensibility × demand). Items D-1–D-3 are the recommended product spine. All depend
on the M1–M3 Git core landing (merge engine, resolver, rebase, partial staging) — that sequencing
is the prerequisite for everything and must be protected from launch-marketing pressure.

## D-1. The Verification & Merge Control Plane (lead feature)

The G-7.3/P2-10 merge queue, promoted from infrastructure to *the product*:
- Per-agent-branch **verification runs** (project test command in the agent's sandbox), recorded
  as `main@<sha> + pass/fail + log artifact`.
- **Stale-verification invalidation:** any merge to main marks other "verified" branches stale
  and auto re-queues (rebase → re-verify). No competitor GUI models this at all — it is the
  single hardest coordination problem of parallel agents and it is pure Git+CI mechanics, our
  home turf.
- **Flagged-changes gate:** diffs touching `package.json` scripts, lockfiles, CI workflows, git
  hooks, or editor config require explicit acknowledgment in a distinct panel (supply-chain
  prompt-injection control). Post-merge installs run `--ignore-scripts`.
- Works with **local agents and cloud agents:** subscribe to Codex/Jules/Cursor-created PRs and
  pull them through the same verify→review→merge pipeline. Vendor-neutrality is the moat.
- **New since July 7 (MergeLoom G3 match):** a bounded **repair loop** — verification fails → one
  scoped repair prompt in the same sandbox → re-verify; attempts capped and journaled; repair runs
  in a visible terminal the human can take over.

## D-2. The AI-Diff Review Cockpit (the daily-driver reason to open Mainguard)

Built directly on the M3/M4 diff stack (intra-line, syntax highlighting, image diff, file
history):
- **Risk-ranked review:** order hunks by blast radius (dependency/config/security-sensitive paths
  first, driven by the same detector as the flagged-changes gate), not file-alphabetical.
- **Provenance per hunk:** attribution surfaced in blame/diff gutters — "which agent, under which
  approved plan, produced this line." **Adopt Agent Trace as the interchange format** (emit and
  render; first renderer of the standard), with commit trailers (`Agent:`, `Task:`, `Plan:`) as
  fallback for non-emitting agents. This is the audit trail made *useful* to a reviewer, and it
  feeds D-3.
- **Test-delta view:** what the verification run newly covers/failed vs main, next to the diff.
- **Diff Guard-style policy (MergeLoom G3 match):** line-volume and touched-paths-vs-plan checks
  added to the flagged-changes gate; optional LLM review pass writing findings into the cockpit.
- Target metric to market: *"review five agent branches in twenty minutes, safely."* This is the
  2026 buying trigger (review time +91%).

## D-3. Compliance-Grade Agent Audit (the enterprise unlock)

Ship H-8.2/H-8.3 (P2-15/16) earlier than "post-GA": hash-chained append-only log of every
inference, spawn, plan approval, and merge decision **with the authorizing OS identity**; RFC 3161
external anchoring; SIEM export; `audit verify` CLI. The EU AI Act enforcement date (2026-08-02)
gives this a date and a budget line inside customers. No orchestrator GUI has anything here;
observability vendors log API traffic but cannot attribute *code changes* — we own the Git side.
Split per the July-7 recommendation: evidence pack first, RFC 3161 anchoring may trail; claim
"audit-grade," not "EU-required."

## D-4. Hardened local sandbox on Windows (the unserved flank)

G-7.2c/P2-05..07 as specced: default-deny egress via proxy allowlist, tmpfs-only credentials, no
global auth-dir mounts, userns. Market reality: Conductor is Mac-only, Sculptor is the only
serious container-UI competitor (network posture unstated), and enterprise sandboxing guidance is
all cloud/microVM. A **polished WSL2-based, egress-controlled agent runner for Windows
enterprises** has effectively no productized competition (primitives exist — Docker sbx, Claude
Code sandbox — integration doesn't). Publish the security architecture (H-8.1/P2-17):
"source-available boundary you can audit" is a sales asset against both wrappers and cloud
agents. Optionally offer Docker sbx as a "maximum isolation" backend tier.

## D-5. Git-surgery for agent output (unique to a real Git client)

- **Commit-stream curation:** interactive rebase (B-4.5a/P2-20) tuned for agent WIP — one-click
  "squash agent checkpoints into reviewable commits," reword to conventional commits.
- **Undo journal (D-2.9/T-19) as the agent safety net:** every agent-driven ref move undoable;
  "go back to when it worked" restore points. Wrappers can't build this without rebuilding a Git
  client.
- **Cross-worktree conflict radar (P2-19):** continuously diff live agent worktrees against each
  other and main; warn the moment two agents touch overlapping regions — *before* either merges.
  GitKraken markets "predictive conflict detection" for humans; nobody does it across N live
  agent worktrees.

## D-6. Cost & rate-limit gateway (retention feature)

G-7.2d/P2-08: 429-interception (pause the agent's PTY instead of letting the CLI crash),
per-agent budgets, tier-derived concurrency ceilings, spend telemetry. Real daily pain for anyone
running 4–8 agents on one API key; invisible in every competitor. **Launch requirement, not an
enterprise add-on** — entry API tiers throttle at RPM levels a 3-agent swarm exceeds instantly;
without the gateway the first-session experience of the headline feature is a retry storm.
Extension (MergeLoom G7): spend records keyed to task/ticket + branch; report **cost per merged
change and cost of rejected work**.

## New workstream candidates opened by the MergeLoom analysis

- **P2-27 Work-intake adapters** (tracker-triggered runs: GitHub Issues + Jira first).
- **P2-28 Context vault** (persistent repo index with Git-native evidence).
- **P2-29 Fleet scheduler** (recurring agent mandates with caps, feeding the verified queue).
- Per-repo, human-editable, hash-chain-audited **"lessons" file** (governed self-learning).

## What NOT to invest in as differentiation

- Generic "spawn N agents" UX beyond parity — commoditized (now on Windows too).
- Competing with Codex/Jules/Factory on cloud execution — capital-intensive, off-thesis (Phase 9
  cloud worktrees remain the later, revenue-bearing exception).
- AI commit messages / chat-with-your-repo gimmicks — GitKraken already owns the checkbox.
- Autonomous CI-fix/auto-merge parity with Composio AO — opposite of the governed thesis.
- Visual-editor breadth à la Nimbalyst (mockups/diagrams) — different buyer.

---

# Part VI — Naming decision

## 6.1 The situation

Three facts, from the naming research plus the July-7 refresh:

1. **"Aegis" (the once-favored candidate) is the default name of the AI-agent-governance
   industry** — 20+ distinct companies/projects, including a Forrester analyst framework
   (AEGIS: 6 domains, 39 controls, cross-mapped to NIST AI RMF/ISO 42001/OWASP LLM Top 10/EU AI
   Act/MITRE ATLAS). Direct-category collisions include: agentlifylabs/Aegis (OSS control plane,
   Ed25519 audit log), aegissecurity.dev (runtime policy engine), Ant Group's AgentAegis,
   rtmx-ai/aegis-cli (agentic coding CLI for defense/regulated environments), cleburn's
   aegis-cli/spec/mcp governance stack, BuildSomethingAI and signal-x-studio aegis-frameworks,
   aegisaiagent.com, aegis-enterprise.com, Red Hat's aegis-ai, aegisagentcontrol.com,
   aegisplatform.ai, aegisprotocol.ai, Infocion Aegis™, Aegis Core AI, Aegent (aegent.io),
   Aegent Dev, AEGYS. Plus unrelated-industry noise (Aegis Authenticator 2FA app being the most
   recognizable, aegis.com, Aegiq, Aegis Sciences, YC's Aegis health-claims startup, the Aegis
   Forge family, etc.). **Bottom line: using it invites buyers/journalists to assume Mainguard is
   one of these headless governance layers.** Ruled out.
2. **Every fresh real-word candidate collided:** Trellis and Vertex confirmed taken earlier;
   this pass — Gantry (MLOps platform, $28.3M + a Gantry AI CAD agent), Manifold (two funded
   AI-agent-security companies), Tributary (Tributary AI — near-identical "accountability for AI
   agents" language), Interlock (Interlock AI safety/certification), Switchyard (NVIDIA NeMo
   Switchyard), Roundhouse (Roundhouse Digital, agent-deployment infra). **Crossbar** was the
   cleanest of the batch (only unrelated-industry hits: youth-sports SaaS, ReRAM hardware,
   crossbar.io IoT messaging) — but needs a proper domain/WHOIS + trademark screen before
   relying on it.
3. **"Mainguard" itself now collides with MergeLoom** (§3.4) — a governance-positioned "-Loom"
   competitor in the exact category, with a 161-post SEO wall on the category vocabulary. The
   collision is material: confusion in search, in word-of-mouth, and possibly in trademark.

## 6.2 The strategic read

Virtually any evocative real English word — guardian words (Aegis, Sentinel), connection words
(Synapse, Nexus, Confluence, Vertex), structural words (Trellis, Lattice, Cadence), industrial
words (Gantry, Manifold, Interlock, Switchyard, Roundhouse) — is already claimed by a funded
AI-agent startup, often founded in the last 6–12 months. This isn't bad luck per word; it's what
a gold-rush category looks like. Two ways out:

1. **Keep hunting real words**, budgeting several more check rounds (Crossbar survived one pass).
2. **Shift to a coined/portmanteau word** (the way Nimbalyst did) — far less likely to collide,
   at the cost of marketing spend to explain it. (The earlier coinage "Aegent" was itself taken.)

## 6.3 Decision checklist (execute before KVK registration, Part XI)

- [ ] Decide: keep "Mainguard" despite MergeLoom, or rename (real-word round 3 vs coined word).
- [ ] Proper domain/WHOIS + Benelux/EU trademark screen on the final candidate (search-engine
      evidence is not registry data).
- [ ] If keeping Mainguard: publicly stake the "governed merge" vocabulary before MergeLoom's SEO
      wall owns it (essays + comparison page), and file the EU word mark immediately.
- [ ] Name must clear: app stores, GitHub org, domain, X handle, EUIPO/BOIP databases.

---

# Part VII — Licensing, trust posture & risk registers

## 7.1 Licensing (LOCKED DECISION — July 2026): source-available core

- **Source-available under FSL (Functional Source License):** the headless daemon,
  sandbox/worktree engine, agent adapters, and audit-log schema. FSL makes the source publicly
  readable and auditable while **legally prohibiting competing use**, converting each release to
  Apache-2.0 after two years. This is *not* OSI open source — a competitor copying FSL code
  commits copyright infringement exactly as if they had stolen a closed binary.
- **Commercial (proprietary):** the Avalonia GUI (graph canvas, docking workspace, terminals),
  Coordinator orchestration intelligence, enterprise governance (RBAC/SSO, SIEM, budgets), cloud
  worktrees.
- **Why not fully closed:** (a) the component with root-equivalent access to customer source code
  and API keys must be auditable to be adoptable by the Phase-A audience; (b) .NET IL decompiles
  to near-perfect C# with free tooling, so a closed binary offers roughly one hour more technical
  protection than visible source — the real copy protection under *either* model is copyright law
  plus velocity, distribution, and brand; (c) community-auditable adapters extend agent coverage
  for free.
- **Compensating measures shipped regardless:** published security architecture document,
  independent security audit with a public report before enterprise GA, and an in-app network
  transparency view making the local-only claim user-verifiable.
- *(Superseded for history: v1 research proposed MIT/Apache core + BSL/FSL premium; v2 locked the
  FSL-daemon + proprietary-GUI split above.)*

## 7.2 BYOK risk register (load-bearing)

1. **Anthropic ToS (enforced Apr 4, 2026):** OAuth tokens from Claude Free/Pro/Max may not be
   used "in any other product, tool, or service." Mainguard drives the *official* Claude Code
   binary (permitted), but a commercial orchestrator programmatically piloting a
   consumer-subscription CLI is one policy clarification away from being cut off.
   **Mitigations:** API-key/pay-as-you-go as the primary documented path; explicit in-product
   disclosure of the risk when a user connects via subscription OAuth; local-model support
   (Ollama/vLLM-backed CLIs) as the pressure valve; monitor for a formal partner program.
2. **Rate-limit tiers break entry-level UX:** entry API tiers throttle at RPM levels a 3-agent
   swarm exceeds instantly. The **AI Gateway** (global token-bucket, queueing, 429 backoff,
   per-agent budgets) is a launch requirement, not an enterprise add-on.
3. **No inference margin:** accepted trade-off locally; recovered via cloud worktrees.
4. **Key-handling liability:** OS-keyring storage, tmpfs injection into sandboxes, and
   (enterprise) Vault/AWS Secrets Manager integration; Mainguard infrastructure never proxies or
   observes keys. *(v1 note, retained: native OS keyrings alone are insufficient for
   SOC 2-grade enterprise expectations — the Vault/ASM integrations are the enterprise answer.)*

## 7.3 Technical risk assessment (v1 assumptions corrected against official docs)

| v1 assumption | Verified reality | Strategic consequence |
|---|---|---|
| `core.fsmonitor` masks 9P latency inside the Linux sandbox | Git's builtin fsmonitor daemon **does not function on Linux** (no inotify backend) — [git-scm docs](https://git-scm.com/docs/git-fsmonitor--daemon) | The "Hollow-Core" bind-mount performance story collapses; agent worktrees move to ext4, Git itself is the Windows↔Linux sync boundary |
| Hot reload "works natively" via shared bind mount | **inotify does not propagate over 9P mounts** ([microsoft/WSL#4739](https://github.com/microsoft/WSL/issues/4739), open) | Vibe Mode's core demo breaks; fixed by the same ext4-canonical topology |
| sbx runs nested inside a private WSL2 distro, "bypassing Docker Desktop" | On Windows, sbx installs **natively** (winget, Windows Hypervisor Platform, Win 11 x86_64); Docker Desktop not required either way ([Docker docs](https://docs.docker.com/ai/sandboxes/get-started/)); nesting would require flaky nested-KVM-in-WSL2 | Two viable engines: raw Docker Engine in WSL2 (v1, full control) or native sbx (high-security backend, later). Nested design withdrawn |
| 50+ concurrent agents | WSL2 defaults to 50% of host RAM ([MS docs](https://learn.microsoft.com/en-us/windows/wsl/wsl-config)); realistic ceiling ~4–6 agents on 16 GB; rate limits bind even earlier | Marketing claim revised to "several agents, safely"; cloud worktrees promoted to core roadmap as the scale story; enforce hard memory limits in `.wslconfig` and Docker |
| Interactive rebase via LibGit2Sharp | **Unsupported in libgit2** ([libgit2#6332](https://github.com/libgit2/libgit2/issues/6332)) | Rebase/worktree operations shell out to the Git CLI; LibGit2Sharp retained for reads/status/commit |

## 7.4 Security posture

The microVM/container boundary is necessary but not sufficient. The dominant real-world threat is
**prompt-injection-driven exfiltration and host-side code execution through legitimate channels**
(writable repo mounts, credential mounts, post-merge dependency installs). Launch-tier
requirements — and *marketable* ones, exactly what the Phase-A audience will probe:
default-deny egress with provider allowlists; per-sandbox credential isolation;
`--ignore-scripts` on host installs; review-UI flagging of executable-config changes. v1's
kernel-isolation note stands for the high-security tier: standard container isolation is
insufficient against container-escape attempts — microVM (Firecracker-class; in practice Docker
sbx) or gVisor for maximum-isolation deployments.

## 7.5 Compliance clarification (SOC 2 vs product features)

SOC 2 attests the **company's** controls; product features (audit trails, RBAC, SIEM export,
retention policies) *enable customers' compliance programs*. Both tracks are needed for the
Enterprise tier; neither substitutes for the other. Full prompt/output logging creates a
sensitive data store — encryption at rest, retention limits, and redaction are part of the
feature, not afterthoughts. v1's legal-liability note stands: *"if you merge it, you own it"* —
enterprises will ask for software-composition analysis (license contamination) and strict HITL
gates for copyright provenance; the flagged-changes gate and license scanning at the merge gate
(Team tier) are the answers.

## 7.6 Structural platform risks

- **Platform dependency on agent CLIs.** Mainguard orchestrates other companies' agents; Anthropic's
  April 2026 enforcement demonstrated vendors will constrain third-party harnesses when it suits
  them. Agent-agnosticism (Claude Code, Codex, OpenCode, Aider, local models via generic
  adapters — MCP where applicable) is a survival requirement, not a feature.
- **Speed of incumbent response.** GitKraken shipped Agent Mode within roughly two quarters of the
  pattern emerging. **Assume any single differentiating feature has a ~2-quarter exclusivity
  window;** only *compound* advantages (workflow + governance + trust) persist.

---

# Part VIII — Pricing, monetization & business expansion

## 8.1 Pricing table (locked structure; July 2026 benchmarks)

| Tier | Price | What it buys | Benchmark logic & v2 notes |
|---|---|---|---|
| **Free** | $0, no login | Full Git GUI + **one** sandboxed agent | Beats GitKraken's account-walled, private-repo-blocked free tier; the funnel. The Git GUI must be genuinely excellent free — it is the top-of-funnel |
| **Pro** | **$20/mo** or $199/yr **with perpetual fallback** | Unlimited local agents, Coordinator mode, verification pipeline, review cockpit, AI gateway (rate-limit protection), BYOK | $20 = the established individual AI-tool price (Cursor Pro, Claude Pro, Copilot Pro+ band); BYO-subscription like Conductor so no inference-margin death; JetBrains-style fallback is a cheap loyalty signal to subscription-fatigued Windows devs. **Fallback caveat (v2):** fallback builds are support-scoped — agent-CLI adapters update via a separately versioned, always-current adapter channel, or fallback value decays in weeks |
| **Team/Enterprise** | **$50+/seat** | Merge queue + re-verification analytics, per-hunk provenance, audit trail/SIEM, RBAC/SSO/SCIM, budget caps, secrets-manager integration, license scanning, priority support | Sits credibly above CodeRabbit Pro ($24–48) and Graphite (~$40) because it bundles review + queue + governance; compliance features are what enterprises pay premiums for. **Do not sell this tier before the governance roadmap items exist** |
| **Cloud worktrees** | usage-based | Hosted execution pods | The usage-revenue lever BYOK deliberately forfeits locally; 2027 |

**Funnel math to keep us honest:** devtool freemium converts 1–3%. At 2% × $20/mo, ~10,000 active
free users ≈ $50K ARR — real money lives in team seats (Warp's B2B2C pattern). Adjacent-market
willingness-to-pay anchors: CodeRabbit $24–48/dev/mo (review throughput), Graphite ~$40, Mergify
$8+ (merge reliability), enterprise governance premiums above all of them. **Against MergeLoom's
per-outcome model (£2–4/PR):** BYOK local runs cost tokens only — publish the cost calculator;
"no per-PR meter on your own hardware."

## 8.2 Business expansion (priority order, v2)

1. **Cloud-hosted worktrees — promoted from "pivot" to roadmap.** Solves the hardware ceiling
   (~4–6 agents on 16 GB), creates usage revenue, reuses the client-server split. Target: private
   beta within two quarters of desktop GA.
2. **B2B observability/audit dashboard — the enterprise wedge.** Centralized agent logs, prompts,
   interventions, SIEM streaming. Highest willingness-to-pay in the portfolio;
   procurement-friendly.
3. **Enterprise AI CI/CD "janitor"** (agents fix broken builds → PRs) — strong concept,
   deferred; requires CI integrations and reputation for merge-gate reliability first.
4. **Agent skills marketplace** — deferred; requires ecosystem scale that does not yet exist.
5. *(v1 candidate, parked)* **On-demand ephemeral staging** — rapid local provisioning of exact
   branches in Docker for QA testers; revisit if design partners pull for it.

## 8.3 Audience sequencing (locked)

- **Phase A (launch): senior developers & DevOps.** Message: "Never let an AI break your working
  directory again." Channels: HN/Lobsters with a deep architecture post (the auditable
  source-available daemon *is* the marketing asset), r/LocalLLaMA and agent-CLI communities,
  technical YouTube. Proof asset: the public security architecture doc — this audience converts
  on verifiable claims.
- **Phase B (post-PMF): economic buyers (EM/CTO).** Message: velocity **with** governance — audit
  trails, SIEM export, budget caps, license scanning at the merge gate. Motion: land-and-expand
  from Phase-A seats; the audit dashboard is the expansion product.
- **Phase C: vibe coders — as a cloud product, not a local install.** The v1 dual-target strategy
  is withdrawn: a local Vibe Mode requiring admin elevation and a mid-onboarding reboot cannot
  beat browser-native rivals (Lovable, Bolt, v0, Replit) on time-to-first-magic. The
  VibeOrchestrator backend is retained as an architectural investment and ships later as hosted
  "Mainguard Web" (chat + live preview on cloud worktrees). Interim: developer-mode users may
  toggle a *simplified view*; no separate installer fork, no separate identity at OOBE.

---

# Part IX — The Netherlands opportunity: Enschede base + national target map

## 9.1 The Enschede/Twente home base

- **Novel-T** — the Twente innovation agency (founded by UT, Saxion, Municipality of Enschede,
  Twente Board, Province of Overijssel). Startup programs are **free of charge**; the **START
  program** is a 6-week validation program (10 calls/year) run at **Incubase**, the UT student
  incubator, which also provides cheap co-working plus on-tap subsidy advice from partners
  (Rabobank, EP&C, Leap, EQIB, BDO) ([novelt.com](https://novelt.com/en/products/1/start-program/),
  [incubase.nl](https://incubase.nl/en/about-incubase/)). **Action: register with Novel-T now** —
  it is the free front door to Twente's grant advisors, angels, and corporate network.
- **Kennispark Twente** — the innovation district; its 2026–2035 strategy targets growth to ~700
  companies and 16,500 jobs (+30%), >40% high-tech, with a €100M national campus-innovation
  budget flowing largely into existing science parks like Kennispark
  ([UT news](https://www.utwente.nl/nieuws/2026/3/843058/nieuwe-strategie-moet-kennispark-twente-laten-groeien-naar-700-bedrijven-en-16.500-banen),
  [kennispark.nl](https://kennispark.nl/en/)). Rising tide for anything based there.
- **Talent:** University of Twente + Saxion produce a steady CS/HBO-ICT pipeline; internship cost
  norms are €250–500/month (Part XII). Twente dev salaries run **€5–10k below the Randstad** for
  the same seniority — a structural cost advantage.
- **Local track record:** Enschede has produced devtool/startup wins before — **CodeSandbox**
  (browser IDE, Enschede-origin, angel-funded via Arches Capital) among them; Cottonwood
  Technology Fund runs its **European HQ on the UT campus**.

## 9.2 Companies to contact — the target map

Three tiers, in outreach order. Track A = free-GUI design partners (individuals); Track B =
verification/merge-pipeline design partners (teams, future paying logos) — program mechanics in
Part XIII.

**Tier 1 — East-NL / Twente (warm, local, visitable):**

| Company | Where | Why they fit | Angle |
|---|---|---|---|
| **Topicus** | Deventer | Large Dutch software product company (education/finance/healthcare platforms), heavily Microsoft-stack, listed parent (Topicus.com) | Track B: compliance-sensitive verticals need agent governance; 45 min from Enschede |
| **Thales Nederland** | Hengelo | Defense/radar software; strict compliance, air-gap-friendly, Windows-heavy | Track B (long cycle): local-first + audit trail is the *only* agent story they can adopt; the rtmx-ai/aegis-cli defense-CLI pattern proves the demand exists |
| **Nedap** | Groenlo | Product software (healthcare, security, retail), strong engineering brand | Track B: mixed-stack product teams already experimenting with AI |
| **Demcon** | Enschede | High-tech systems + embedded software group, Kennispark anchor | Track A→B: regulated-industry software units |
| **Sigmax** | Enschede (Kennispark) | 160+-person software company for national/international clients ([twente.com](https://twente.com/nl/organisaties/17937/sigmax/)) | Track B: agency economics = review throughput is billable time |
| **Heutink ICT / regional agencies** | Hengelo etc. | Dozens of 10–100-dev shops (directory: [softwarebedrijf-info.nl/enschede](https://www.softwarebedrijf-info.nl/enschede)) | Track A volume + 1–2 Track B |
| **Xsens/Movella, SciSports, LioniX** | Enschede | UT-adjacent product-engineering companies | Track A: polish/retention feedback, local community |
| **UT/Saxion spin-offs via Novel-T** | Enschede | AI-forward startups running agents daily | Track A/B: fastest feedback loops |

**Tier 2 — National .NET ISVs & consultancies (the "Dana" employers):**

| Company | Why |
|---|---|
| **Info Support** (Veenendaal) | One of NL's largest .NET/Java consultancies with a famous internal knowledge culture; runs its own conferences — one adopted champion seeds hundreds of consultants |
| **Betabit** (Rotterdam/Amsterdam/Apeldoorn/Eindhoven) | ~200-person Microsoft Solutions Partner (Digital App & Innovation, Data & AI), ships .NET/Azure content publicly, sponsors Techorama & .NET Zuid ([betabit.nl](https://www.betabit.nl/en)) — exactly the shop whose clients ask "was this AI-written code tested?" |
| **Luminis/Yuma, ilionx, Sopra Steria NL (ex-Ordina), Sioux Technologies** | Large mixed-stack consultancies; agent governance is a services opportunity for them, tooling for us |
| **Q42, Voorhoede, west-NL product agencies** | Craft-brand agencies; good design partners and loud on social |

**Tier 3 — AI-forward Dutch product companies (logo value + real agent usage):**

| Company | Why |
|---|---|
| **Adyen, Mollie** (Amsterdam) | Fintech = compliance + engineering brand; Mollie is also a candidate payment partner |
| **bol., Coolblue, Picnic** | Big Dutch engineering orgs, active tech blogs, early AI adopters |
| **ASML** (Veldhoven) | Enormous Windows/.NET+C++ engineering base; long cycle, but the ultimate "governed agents" customer |
| **Weaviate, Framer, Channable, Mews** | Scale-ups running coding agents daily; founders reachable via the Amsterdam ecosystem |

**Walkthrough — design-partner outreach (repeatable per company):**

1. Find the engineering-manager or staff-engineer champion (LinkedIn: "engineering manager" +
   company; or dotNed/Techorama speaker lists — speakers answer email).
2. Send the founder-to-champion note (template): *"I'm building Mainguard in Enschede — a native
   Git client that runs coding agents in sandboxes and makes them pass your test suite before
   anyone reviews the diff. You're one of the few teams in NL running agents seriously. Could I
   get 30 minutes for a demo in exchange for brutal feedback? Not selling anything — looking for
   3–5 design-partner teams before we charge anyone."* (Dutch or English; Dutch opens more doors
   in Tier 1/2, English is fine in Tier 3.)
3. Demo (Part XV §15.2 script), then qualify on urgency/capacity/representativeness (Part XIII).
4. Offer the Track B deal: early access + roadmap influence + free year, for real usage,
   bi-weekly calls, logo rights at GA. Time-box 6 months.
5. Log everything in the 40-candidate list; target: 25 discovery interviews Jul–Sep, 3–5 signed
   Track B teams by launch.

---

# Part X — Funding: the complete Dutch stack (grants **and** VC, equal depth)

## 10.1 Track A — Non-dilutive (start immediately; sequence matters)

**Step 0 — prerequisite: a BV + KVK registration** (Part XI). WBSO for a BV requires payroll; as
long as Mainguard is a sole proprietorship (eenmanszaak/zzp) the self-employed variant applies.

**1. WBSO (R&D wage-cost credit) — the foundation, apply first.**
- **What (2026):** for companies with payroll, a payroll-tax reduction of **36% (starters: 50%)
  of the first €391,020 of R&D wages**, 16% above that. For self-employed doing ≥500 R&D
  hours/year: a fixed income-tax deduction of **€15,979 + €7,996 starter top-up**. 2026 national
  budget €1.817B ([RVO](https://english.rvo.nl/subsidies-financing/wbso),
  [informer.nl guide](https://www.informer.nl/belastingen/aftrekposten/wbso)).
- **Fit:** Mainguard's daemon/sandbox/merge-queue development is textbook S&O (technically new
  software development). This alone can return roughly a third to half of early engineering wages.
- **Walkthrough:** (1) get eHerkenning (eH3) for the BV, or DigiD for zzp; (2) write the project
  description: technical bottlenecks + why technically new *to you* (e.g., "deterministic
  cross-process Git handle management", "default-deny egress proxy for agent sandboxes on WSL2");
  (3) submit via RVO's eLoket **before the month in which R&D starts** (companies) or by
  30 September of the calendar year (zzp); (4) keep an hours ledger per project from day one (RVO
  audits); (5) file the realized hours (mededeling) by 31 March of the following year. Free help:
  Novel-T's partner subsidy advisors (Leap is literally an Incubase partner).
- **Multiplier:** a granted WBSO declaration is the **entry ticket to the Innovatiebox** — 9%
  corporate tax on profits from the self-developed software instead of 19%/25.8%
  ([Belastingdienst](https://www.belastingdienst.nl/wps/wcm/connect/bldcontentnl/belastingdienst/zakelijk/winst/vennootschapsbelasting/innovatiebox/)).
  Not relevant pre-profit, but structure IP inside the BV now so it's available later.

**2. MIT Haalbaarheidsproject (feasibility grant) — €20k, once a year, first-come.**
- **What:** 35% of feasibility-study costs, max **€20,000**; Overijssel 2026 ceiling was €980,000
  and the window opened **7 April 2026** — first-come-first-served, typically oversubscribed on
  day one with notarial lottery ([Overijssel loket](https://regelen.overijssel.nl/Producten_en_diensten/Subsidies/Werken_en_ondernemen/MIT_Haalbaarheidsprojecten),
  [leap.nl](https://www.leap.nl/subsidies/mit-haalbaarheidsprojecten/)).
- **Reality check:** the 2026 round has passed. **Target the April 2027 round** with a
  ready-on-day-one application (e.g., "feasibility of cloud-hosted verification worktrees for EU
  enterprises"). Put a reminder in February 2027; a subsidy advisor can hold the pen.

**3. Vroegefasefinanciering (VFF) — the €50–350k pre-seed loan.**
- **What:** government loan covering an "early-phase plan" (validating product/market); for
  innovative starters **€50k–€350k at 7.19% (2026)**, covering up to 100% of plan costs;
  repayable; requires a **letter of intent from a follow-on investor** for at least the same
  amount; no prior start of the trajectory; completable within 2 years
  ([RVO voorwaarden](https://www.rvo.nl/subsidies-financiering/vroegefasefinanciering/voorwaarden),
  [Ondernemersplein](https://ondernemersplein.overheid.nl/subsidies-en-regelingen/vroegefasefinanciering-vff/)).
- **Overijssel quirk:** companies in Overijssel apply **via the regional financier (Oost NL)**,
  not RVO directly ([RVO VFF-regionaal](https://www.rvo.nl/subsidies-financiering/vff-regionaal)).
- **Play:** pairs perfectly with the design-partner phase — an angel/VC intent letter
  (henQ/NP-Hard/Cottonwood-style, or a committed angel via Leapfunder) unlocks state money that
  doubles the runway *before* a priced round. Walkthrough: (1) write the early-phase plan
  (validation milestones = the launch plan's Phase 0–2); (2) secure the intent letter; (3) apply
  at Oost NL; (4) window open all year (1 Jan–31 Dec 2026).

**4. Innovatiekrediet — the development loan (later).**
- Technical-development projects with "considerable technological risks and an excellent market
  perspective," new to the Netherlands; assessed on 11 criteria (technical feasibility,
  commercial potential, degree of innovation, exploitation/repayment plan); covers up to 45%
  (small co.) / 35% (medium) of project costs; requires **55% co-financing** with room for
  setbacks, an investment declaration from co-financing investors, and a first pledge on project
  assets; repayment = credit + fixed surcharge + accrued interest; 2026 budget €30M technical +
  €10M clinical (+€10M mixed) ([RVO](https://english.rvo.nl/subsidies-financing/innovation-credit),
  [conditions](https://english.rvo.nl/subsidies-financing/innovation-credit/conditions)).
  Relevant at the cloud-worktrees/enterprise-buildout stage (2027), not now. Do the mandatory RVO
  quick-scan first when the time comes; applications open all year.

**5. EU level (2027+):** EIC Accelerator (grant ≤€2.5M + equity) once there's traction; Horizon
Europe calls via UT partnerships opportunistically. High effort — only with a grant writer.

**6. Also real:** Rabobank's overview of 14 starter schemes
([rabobank.nl](https://www.rabobank.nl/bedrijven/eigen-bedrijf-starten/financien/14-subsidies-en-regelingen-voor-startende-ondernemers))
and provincial "regional importance" projects up to €500k (requires pre-consultation with the
province) ([Overijssel](https://www.overijssel.nl/onderwerpen/economie/ondernemerschap/innoveren)).
Check [SubsidieMatch for Overijssel pre-seed](https://www.subsidiematch.app/regio/overijssel-fase-pre-seed)
quarterly; note the province's old innovation-voucher page is defunct.

**Non-dilutive sequence:** WBSO now → VFF once an investor intent letter exists (H2 2026–H1 2027)
→ MIT April 2027 → Innovatiebox at first profit → Innovatiekrediet/EIC at scale-up.

## 10.2 Track B — Equity (mapped to thesis fit)

**The seed bar (2026 benchmarks, unchanged):** conventional: ~5,000+ MAU **or** 500 paying
customers **or** ~$300–500K ARR; burn multiple <2×. AI-infra reality: growth rate (15–20%+ MoM,
organic) and logo quality outweigh absolutes — GitButler raised on credibility + HN demand
pre-revenue; Conductor's A rested on logos (Vercel, Notion, Ramp), not seats. **Our fundable
story at the low end:** 3–5 named design-partner teams actively using the merge queue + a strong
retention curve on the free GUI + weekly-verified-merges growth; roughly $10–50K MRR with that
shape is pitchable. Position as **agent infrastructure**, never "Git client" (median seed
valuations concentrate in AI-positioned companies).

**Dutch/Benelux funds, ranked by thesis fit:**

| Fund | Base | Stage/check | Why they fit | Source |
|---|---|---|---|---|
| **NP-Hard Ventures** | Amsterdam | Pre-seed/seed; €25M Fund II | *The* on-thesis fund: "core technology infrastructure", dev tools, product-obsessed founders; portfolio incl. tldraw; partner Micha Hernandez van Leuffen founded Wercker (CI, sold to Oracle) — he *is* the ICP | [nphard.vc](https://nphard.vc/), [Silicon Canals](https://siliconcanals.com/np-hard-ventures-launches-e25m-fund-ii/) |
| **henQ** | Amsterdam | Pre-seed–A; €1–10M initial; Fund V €67.6M (target €90M); ~3 deals/yr, 8–12 teams for Fund V | B2B software, "odd ones out", unfashionable markets, atypical models; **funded Mendix from €250k** — the canonical Dutch enterprise-software bet | [henq.vc](https://www.henq.vc/), [EU-Startups](https://www.eu-startups.com/2025/11/why-henq-chooses-the-roads-less-travelled-inside-the-dutch-vcs-new-e67-6-million-fund-for-the-odd-ones-out/) |
| **Curiosity VC** | Amsterdam | (Pre-)seed; €50M fund, ~5/yr | AI-first B2B software incl. an explicit "Software Development" vertical; portfolio incl. Orq.ai (AI-agent deployment, €5M seed) and Deeploy (explainable AI) — adjacent thesis; 62 investments to date | [curiosityvc.com](https://www.curiosityvc.com/) |
| **Volta Ventures** | Benelux | (Pre-)seed; €300k–2M initial; €125M under management + dedicated €20M seed fund | Benelux B2B software at pre-revenue — the natural first institutional check if raising early | [volta.ventures](https://www.volta.ventures/volta-launches-e20m-dedicated-seed-fund-for-benelux-startups-and-announces-first-investments/) |
| **Newion** | Amsterdam | Seed/A; €1–10M in rounds up to €20M; Fund IV €130M (~20 companies) | Pure B2B software only (excludes hardware/deep science/consumer); portfolio: Collibra, Deliverect, Parloa; wants unique vision + initial customer interest + early revenue — better at seed-with-revenue than pre-seed | [newion.com](https://newion.com/), [Silicon Canals](https://siliconcanals.com/amsterdams-newion-fund-iv-close-130m/) |
| **Peak** | Amsterdam | Seed; SaaS/marketplace/platform | Generalist SaaS; warm intros easy via Amsterdam ecosystem | [peak.capital](https://peak.capital/) |
| **No Such Ventures, Keen Venture Partners, SET Ventures** | Amsterdam | Various | Second-ring options; No Such does non-standard deals | [Vestbee NL VC list](https://www.vestbee.com/insights/articles/top-vc-funds-in-the-netherlands-to-finance-your-startup) |
| **Cottonwood Technology Fund** | **Enschede (UT campus)** | (Pre-)seed; €1–2M average first tickets; Fund III open | Deeptech/patent-first thesis = imperfect fit, but they are *on campus*, know every Twente angel, and co-invest with Oost NL — take the coffee regardless | [cottonwood.vc](https://www.cottonwood.vc/), [Kennispark](https://kennispark.nl/en/cottonwood-technology-fund-opens-third-high-tech-fund/) |
| **Oost NL / Twente Innovation Fund** | Apeldoorn/Enschede | Regional development capital | Co-invests with private leads in Overijssel startups (e.g., Sound Energy with Cottonwood); also the VFF-regional counter | [oostnl.nl](https://oostnl.nl) |
| **Twente Technology Fund (TFF)** | Twente | Seed; high-tech incl. **ICT**; also pre-seed commercially viable academic projects | Regional seed capital explicitly covering ICT out of UT | [TFF LinkedIn](https://nl.linkedin.com/company/twente-technology-fund) |
| **Antler Amsterdam** | Amsterdam | 6-week residency (next cohort Mar 9, 2026-pattern) → ~$100–150k for 10–12%, up to $500k follow-on | Only if you want a cohort/co-founder environment; no equity/fees during residency itself, but terms are expensive for a product this far along — **probably skip** | [antler.co](https://www.antler.co/residency) |

**Angels:** **BAN Nederland** (umbrella of the Dutch angel networks — overview of affiliated
networks and meetings, [bannederland.nl](https://bannederland.nl/)) · **Leapfunder**
(convertible-note platform popular for Dutch pre-seed, [leapfunder.com](https://www.leapfunder.com/)) ·
**Golden Egg Check** (startup–investor matching, based in **Enschede**,
[goldeneggcheck.com](https://goldeneggcheck.com/)) · **Arches Capital** (angel syndicate; backed
Enschede-origin startups incl. CodeSandbox and PESCHECK) · plus Angel Island, Investormatch, and
regional entrepreneur associations. Ex-founders of Twente/UT companies are the best single-angel
profile — reachable via Novel-T and Golden Egg Check.

**Foreign funds (for the real seed):** the corpus's investor logic (GitButler↔a16z,
Conductor↔Spark/Matrix, Greptile↔Benchmark) implies the eventual competitive seed is likely led
from London/Berlin/US (Seedcamp, Point Nine, Cherry, boldstart-style dev-tool specialists) —
Dutch funds above are the warm, reachable start and the intent-letter source for VFF.

**Walkthrough — the raise (when triggered):**

1. Trigger check: 3–5 active Track B teams + retention curve + verified-merges growth (Part
   XVII), or a competitive forcing event (Cursor Origin shipping local execution + provenance).
2. Build the list (~30): tier by thesis fit as above; 5 dream / 15 realistic / 10 backup.
3. Warm paths first: NP-Hard via dev-tool community & HN presence; henQ via Mendix-alumni
   network; Cottonwood/TFF/Oost NL via Novel-T introductions; Curiosity via Amsterdam AI meetups;
   the advisor (post-August) for everything he offers unprompted.
4. Materials: the Part-XV deck (Sequoia skeleton, "Why Now" leads), 90-second product video, data
   room (metrics dashboard, design-partner references, FSL/security docs).
5. Run it as a 3–4-week sprint after ~6 weeks of relationship-building before the ask
   (report-back emails to 3–4 target partners with metric updates — same loop as the advisor).
6. Anchor: raise €750k–1.5M pre-seed (grants extend it ~40%) *or* skip to a $2–4M seed on launch
   traction. Decide after August advice + October launch data, not before.

---

# Part XI — Legal & company setup (Netherlands walkthrough)

## 11.1 Entity: go BV, use the starter exemptions

- **Costs (2026):** incorporating a single BV ≈ **€500–900 all-in** (notary €400–800 + KVK
  registration ~€75–82); online notaries are the cheap path for a standard incorporation
  ([holdwise.nl](https://holdwise.nl/kennisbank/bv-oprichten-kosten-stappen),
  [ligo.nl](https://www.ligo.nl/kennisbank/bv-oprichten-kosten/)). Minimum share capital €0.01.
- **Structure:** the standard Dutch startup setup is **Holding BV → Werk-BV** (holding owns your
  shares + IP can sit at holding level; operating risk in the werk-BV; dividends flow tax-free
  holding-ward under the participation exemption). Costs roughly 2× notary fees; do it at
  incorporation — restructuring later is expensive.
- **DGA salary:** the "gebruikelijk loon" norm is **€58,000 (2026)**, but **startups/loss-making
  BVs may pay down to statutory minimum wage for up to 3 years** with substantiation
  ([wetaxus](https://wetaxus.nl/kennisbank/dga-salaris-berekenen-2026),
  [bvbeginnen.nl](https://www.bvbeginnen.nl/dga-salaris/)).
- **Why BV over eenmanszaak** despite eenmanszaak being cheaper below ~€70–80k profit (BV
  becomes clearly favorable around €100–120k; admin costs €2,500–4,000/yr extra,
  [zzp-pulse](https://zzp-pulse.nl/nl/blog/zzp-vs-bv)): (a) investors and VFF/Innovatiekrediet
  effectively require it; (b) limited liability matters for a tool that executes AI-written code
  on customer machines; (c) the Innovatiebox (9% CIT) only exists inside a BV; (d) employee
  options need shares. **Action: incorporate before launch** (target: August–September 2026),
  put the IP in, sign an IP-assignment from yourself to the BV.

## 11.2 Selling software from NL: VAT & merchant of record

- Selling digital services to EU consumers means charging **each buyer's local VAT**; B2B in-EU
  reverse-charges; non-EU adds more regimes. Self-managing this on Stripe (2.9% + $0.30 + Stripe
  Tax + OSS filings) is doable but is founder-time; a **Merchant of Record** (Paddle or Lemon
  Squeezy, ~5% + $0.50) becomes the legal seller and handles global VAT/sales tax and disputes
  ([comparison](https://fintechspecs.com/blog/stripe-vs-paddle-vs-lemon-squeezy-vs-polar-merchant-of-record-b2b-saas/),
  [fee comparison](https://www.globalsolo.global/blog/stripe-vs-paddle-vs-lemon-squeezy-2026)).
  Paddle covers the most jurisdictions and has the most mature tax infrastructure; Lemon Squeezy
  (Stripe-owned since 2023) wins on integration speed.
- **Recommendation:** launch Pro on a MoR (Paddle for billing-complexity headroom —
  perpetual-fallback licenses and team seats; Lemon Squeezy if integration speed wins). Revisit
  Stripe-direct at >$30K MRR when the MoR fee exceeds a part-time accountant. Enterprise deals
  invoice directly from the BV regardless.
- **GDPR:** local-first is a structural advantage — the product processes no customer code
  server-side. Ship: privacy policy, published telemetry schema (already planned), a DPA template
  for the later cloud tier, EU data residency for any hosted component. Say all of this on the
  website; it is a differentiator in EU procurement (Part XVIII).

## 11.3 Checklist (in order)

1. Decide the name (Part VI — resolve the MergeLoom collision first; a rename after KVK
   registration is paperwork; after launch it's brand damage).
2. Online notary → Holding BV + Werk-BV; KVK + RSIN/VAT numbers arrive with registration.
3. Business bank account (bunq/Rabo/ING) + accountant (fixed-fee startup packages).
4. IP assignment (you → werk-BV), employment/DGA agreement, minimum-wage DGA salary election.
5. eHerkenning eH3 → **WBSO application** (Part X walkthrough).
6. MoR account + EUR pricing page; Terms of Service + Privacy Policy with entity/jurisdiction
   stated (contrast: MergeLoom hides theirs).
7. Trademark screen on the final name (Benelux + EU word mark, ~€250–1,000).

---

# Part XII — Hiring & talent (Twente playbook)

- **Salary benchmarks (2026, gross/yr):** medior developer **€45–65k** (avg ~€55k); senior
  **€70–95k**; Randstad pays €5–10k above the rest of the country — senior ~€70–85k in Twente vs
  €85–100k in Amsterdam/Utrecht ([ubuntustaffing](https://ubuntustaffing.nl/blog/wat-is-het-salaris-van-een-medior-software-developer-in-nederland/),
  [searchcompany](https://searchcompany.nl/werving-en-selectie-blogartikel/wat-verdient-een-senior-developer-in-2025/)).
  Budget +~30% employer costs on top of gross.
- **Interns/afstudeerders — the cheapest strong pipeline:** UT (via
  [utwente.nl/business](https://www.utwente.nl/business/talent-vinden/)) and Saxion (via
  [saxion.nl/bedrijven](https://www.saxion.nl/bedrijven/stage-en-afstuderen)) place 5-month
  full-time internships (Sep–Jan / Feb–Jul); market compensation ~€250–500/month (Saxion's own
  norm moved toward €475; government pays ~€747)
  ([SaxNow](https://www.saxnow.nl/nieuws/2023/december/voorstel-stagiairs-op-saxion-krijgen-straks-veel-meer-geld-vergoeding-naar-475-euro)).
  A native Avalonia Git client + agent sandboxes is a *magnet* graduation project. **Action:**
  post 2 afstudeeropdrachten for the February 2027 block (pitch to programme coordinators
  October–November 2026).
- **Stock options:** today's Dutch regime taxes options as wage income when shares become
  tradable — painful. A new **startup stock-option law** (Wet fiscale stimulering van startups en
  scale-ups; consultation opened 1 Apr 2026, responses until 29 Apr; entry targeted **1 Jan
  2027**) will tax only 65% of the gain (≈ box-2 level) **and defer taxation to the moment the
  shares are sold**, for young, non-listed companies with a scalable and innovative business
  model ([PwC](https://www.pwc.nl/nl/actueel-en-publicaties/belastingnieuws/loonbelasting-en-sociale-verzekeringen/consultatie-wetsvoorstel-fiscale-stimulering-startups.html),
  [startup-recht.nl](https://www.startup-recht.nl/insights/aandelenopties-bij-startups-en-scale-ups-hoe-werkt-de-fiscale-regeling-nu-en-wat-verandert-er-mogelijk)).
  **Action:** promise ESOP percentages now (e.g., 10% pool); issue options under the new regime
  in 2027; use a STAK or option agreements drafted by a startup lawyer, not the notary's default.
- **First hires:** with WBSO's 50% starter rate, a €55k medior costs the BV net ≈ €40k-equivalent
  in year one — the subsidy stack effectively funds hire #1. International hires can use the
  30%-ruling (reduced, but alive) if recruited from abroad.
- **Where to find them:** UT/Saxion career events, dotNed meetup (offer to host one at
  Kennispark), the Mainguard build-in-public feed itself (devtool startups recruit their users).

---

# Part XIII — Customer discovery & design partners

Do this *before* and *during* launch — it is the highest-information work per hour spent.

## 13.1 Discovery interviews (July–September, target 25)

- **Sources:** r/ClaudeAI and r/ChatGPTCoding power users (DM people posting multi-agent
  workflows), X build-in-public followers, .NET/C# Discords, local dev meetups (dotNed,
  Kennispark), the team's own networks, the Part-IX company map, and — after the August visit —
  Daemon's engineers if offered.
- **Script spine (ask about the past, not the future):** How many agent sessions do you run in
  parallel today? Walk me through the last time an agent broke something or two agents collided.
  How do you review agent output — what do you actually read? Have you merged agent code you
  didn't fully review? What happened? Who in your org asks "did AI write this"? What do you pay
  for today (Cursor/Claude/Copilot/review tools)?
- **Disqualifying signal to respect:** if interviewees consistently say the first-party desktop
  apps are "good enough" for review, the review-cockpit wedge needs sharpening before launch.

## 13.2 Design partner program (the a16z/First Round consensus shape)

- **Two tracks:**
  - **Track A — 5–8 individuals** on the free Git GUI (polish, retention, Windows edge cases).
  - **Track B — 3–5 teams** for the verification/merge pipeline. These become the first paying
    logos and the seed-deck proof points. Build a ~40-candidate list to land 4 (candidate
    sources: Part IX map + interview standouts).
- **Qualify on:** *urgency* (already duct-taping worktrees + tmux), *capacity* (a champion who'll
  do bi-weekly calls), *representativeness* (one .NET enterprise shop, one AI-forward startup,
  one agency).
- **The deal:** early access + roadmap influence + free year / deep discount, in exchange for
  real usage on real repos, bi-weekly feedback, and logo/case-study rights at GA. **Time-box to
  6 months** with success criteria, or it becomes unpaid consulting.
- **Daemon angle:** the single best outcome of the August meeting is Daemon's team as a Track B
  design partner (Part XVI) — an AI startup running agents all day is the exact ICP.

---

# Part XIV — Launch plan & marketing

## 14.1 Phase 0 — Pre-launch (now → ~September)

1. **Build in public on X (Linear playbook).** The product is inherently screenshot-able (graph
   canvas, five themes, 3-pane resolver). Post short clips 2–3×/week: commit-graph rendering,
   partial staging, conflict resolver, then sandbox/verification previews as they land.
   Windows-native polish is itself novel content — the "beautiful devtool" genre is all macOS.
   **Mirror to LinkedIn** (Dutch B2B buyers live there, not on X).
2. **Waitlist + cohort invites.** Landing page with direct waitlist; invite in cohorts of 10–20
   (Linear model), fix what breaks, invite the next cohort.
3. **Write 3 technical essays** (each is HN/Pointer/newsletter fodder and pre-answers launch-day
   objections): (a) *"The .git/index.lock problem: why agents corrupt your repo and how
   deterministic handle management fixes it"*; (b) *"Sandboxing coding agents on Windows/WSL2
   with default-deny egress"*; (c) *"Test-verified agent PRs: making the merge queue re-verify
   what goes stale."*
4. **Open the Discord** at beta start; seed 20–50 core members (design partners + waitlist
   enthusiasts); it doubles as the changelog feed. Don't over-build channels.
5. **Trust assets shipped before launch:** security architecture doc, telemetry policy (opt-in,
   published schema), FSL licensing statement, "network transparency" view on the roadmap.
6. **August: the advisor meeting** (Part XVI). Use the trip's deadline as the forcing function
   for a rehearsed 10-minute demo — the same demo is the launch video.

## 14.2 Phase 1 — Launch act one: the free Git client (target ~October)

- **Show HN**, plain title: *"Show HN: Mainguard – a fast, native Git GUI for Windows (free, no
  login)"*. Direct download link, no signup wall. Founder in comments within the hour, all day,
  technical and non-defensive. Tue–Thu, 9am–12pm ET. Pre-empt the known objections (why another
  client; why FSL not MIT; why .NET/Avalonia; Electron comparison benchmarks ready).
- **Same week:** Product Hunt (badge + backlink, ~10% of energy), **Console.dev submission**
  (free, editorial; we meet every criterion: developer-primary, self-service download),
  founder-disclosed posts in r/git, r/csharp, r/dotnet, r/SideProject — **plus the Dutch press
  wave (§14.5): Tweakers + IO+ pitched to land the same week.**
- **Goal:** installs, retention curve, and a believable "weekly active repos" number — not
  revenue.

## 14.3 Phase 2 — Launch act two: the agent control plane (4–8 weeks later)

- **Second Show HN / Launch Week:** *"Show HN: Run coding agents in sandboxes that must pass
  your tests before you review their code"*. This is the story for r/ClaudeAI, TLDR AI, and
  creator outreach (ThePrimeagen / Theo / Fireship: founder-to-creator email, 60-second demo
  clip, the hook is "agents that must pass tests before you see the PR"). Organic first; consider
  one $2–5K mid-tier sponsorship only after the organic message proves out.
- **Position explicitly as "Conductor for Windows, with verification."**
- Ship Pro tier ($20/mo) at or shortly after this act; design partners convert to paid logos.
- Dutch layer: AG Connect (enterprise/governance angle) + Silicon Canals (startup angle).

## 14.4 Phase 3 — Teams & governance (2027, post-PMF signal)

- Land-and-expand from Phase-2 seats; the audit dashboard + merge-queue analytics are the
  expansion product. Sell $50+/seat only once RBAC/audit/SIEM exist.
- Cloud worktrees private beta (the scale + usage-revenue story) within ~2 quarters of desktop GA.

## 14.5 Channel priorities (ranked by expected yield)

1. Hacker News (two acts + essays) — the GitButler/Graphite/Supabase evidence is unambiguous.
2. Build-in-public X (+ LinkedIn mirror) + waitlist cohorts.
3. r/ClaudeAI, r/ChatGPTCoding, r/git, r/csharp (90/10 rule, founder-disclosed).
4. Console.dev (free, high-intent) → Pointer/TLDR (paid, later, ~$3.5K+ per placement).
5. YouTube creators (organic outreach at act two).
6. Discord community (retention, not acquisition).
7. **NL press + events layer (below) — timed to the acts, not standalone.**
8. Comparison-page SEO (the Nimbalyst playbook): "Mainguard vs Conductor/Kepler/Copilot
   app/MergeLoom" pages — contest the category vocabulary before MergeLoom's SEO wall owns it.

## 14.6 The Dutch media list (in pitch order)

| Outlet | What it is | Angle to pitch |
|---|---|---|
| **Tweakers** | NL's biggest tech community/news site; devs read it daily | "Nederlandse ontwikkelaar bouwt native Git-client die AI-agents laat bewijzen dat hun code werkt" — product + local-founder angle at Phase-1 launch |
| **AG Connect** | Dutch IT-professional trade press (covers .NET/dev topics) | The enterprise/governance story at Phase 2; they already cover C#'s rise |
| **Silicon Canals** | English-language European startup news, Amsterdam-rooted ([siliconcanals.com](https://siliconcanals.com/)) | Funding/design-partner milestones; they cover every Dutch round |
| **Emerce** | Dutch digital business/e-commerce press | Business angle once first paying teams exist |
| **MT/Sprout** | Dutch startup/scale-up business magazine | Founder-story + subsidy-stack angle |
| **IO+ / Innovation Origins** | Covers East-NL innovation (already writes about Kennispark) | The "Twente builds a global devtool" regional story — easy yes |
| **Tubantia / 1Twente / U-Today (UT)** | Regional + campus media | Local-hero coverage; cheap credibility for Tier-1 outreach and hiring |

Walkthrough per outlet: find the journalist who wrote the nearest story (e.g., AG Connect's C#
piece); 5-sentence personal pitch; embargo-free; screenshots + 90-second video attached; offer a
15-minute call. Dutch pitches to Dutch outlets. Time local press to land the **same week as the
Show HN** so the story is "launch," not "plans."

## 14.7 Community & events calendar (H2 2026)

| When | What | Action |
|---|---|---|
| Ongoing | **dotNed** meetups + dotNed Saturday; .NET Zuid | Attend now; propose a talk: "Building a 60fps native Git client in Avalonia" (pure tech, zero pitch — the .NET community will carry it) |
| Ongoing | Novel-T / Incubase / Kennispark events | Register with Novel-T; use their demo-days for angel/Oost NL contact |
| **26–28 Oct 2026** | **Techorama Netherlands, Kinepolis Jaarbeurs Utrecht** — 120 sessions, 8 workshops; NL's Microsoft-stack conference ([techorama.nl](https://www.techorama.nl/)) | **Perfectly timed with Phase-1 launch.** CFP via Sessionize — submit the Avalonia/git-engine talk now; if CFP is closed, attend with stickers + laptop demos; consider the cheapest booth only if Phase 1 shipped |
| Nov 2026+ | GOTO Amsterdam (dates TBA), Codemotion, regional hackathons | Opportunistic talks at Phase 2 |
| ~~TNW Conference~~ | **Defunct** — TNW's events and media shut down by end of September 2025; the June 2025 edition was the last ([source](https://thenextweb.com/conference)) | Remove from all plans |
| NL MVP circuit | Microsoft NL DevRel + Dutch MVPs | Demo to 3 Dutch .NET MVPs pre-launch; the ".NET flagship app" story earns free advocacy |

**Dutch-market nuances:** LinkedIn is disproportionately effective for B2B in NL. Product copy
stays English; sales conversations in Dutch close Tier-1/2 companies faster. The .NET-flagship
story ("Mainguard is proof you can build world-class native UI in .NET") is itself a marketing
asset in this community.

## 14.8 Consolidated step-by-step launch playbooks

**Playbook 1 — Pre-launch (Jul–Sep):** ① landing page + waitlist + X/LinkedIn accounts; ② first
build-in-public clip this week; ③ essay (a) published ~2 wks before launch; ④ 40-company
design-partner list + 25 interviews; ⑤ trust assets shipped; ⑥ WBSO filed; ⑦ BV incorporated;
⑧ Techorama CFP submitted; ⑨ August advisor meeting executed per Part XVI.

**Playbook 2 — Show HN (Oct):** ① Tue–Thu 9am–12pm ET; plain title; direct download, no signup;
② founder in comments within the hour, all day; ③ pre-written answers: why another client / why
FSL / why .NET / Electron benchmarks; ④ same week: PH, Console.dev, r/git + r/csharp + r/dotnet
(founder-disclosed), Tweakers + IO+ pitches; ⑤ measure installs → weekly active repos.

**Playbook 3 — Act two (Nov–Dec):** ① second Show HN (verification story); ② r/ClaudeAI + TLDR
AI + creator emails (60-sec clip); ③ AG Connect + Silicon Canals pitches; ④ ship Pro via MoR;
⑤ convert Track B partners to paid pilots — tripwire: if none pre-commit within 2 months,
revisit packaging.

**Playbook 4 — Funding cadence (parallel):** WBSO (now) → advisor loop (Aug→monthly) → intent
letter + VFF via Oost NL (post-launch traction) → MIT application ready Feb 2027 → seed
conversations only on Part-X triggers.

---

# Part XV — Pitch materials

## 15.1 Deck outline (Sequoia skeleton — "Why Now" is our strongest card)

1. **Purpose** — one sentence: "Mainguard makes AI-agent code safe to merge."
2. **Problem** — the adoption/trust scissors: 90% use AI daily; 31% run agents (early!); 87%
   distrust accuracy; review time +91%; DORA stability degrading. Today's "solution" is a human
   skimming a firehose of diffs.
3. **Solution** — the pipeline: plan approval → sandboxed execution → tests pass in the agent's
   sandbox → risk-ranked, provenance-annotated review → merge queue with re-verification →
   human-gated merge. Screenshot-heavy.
4. **Why Now** — agent CLIs went mainstream in 24 months; every vendor ships "agents in
   worktrees" free; the bottleneck moved from generation to verification; nobody owns the
   verification layer (Meta built RADAR internally because it couldn't buy it).
5. **Market** — $7–9B → $20–30B by 2030; 47M developers; wedge = the largest, least-served OS.
   (For Dutch investors, add the Part-II NL slide: 575k ICT workers, 66% large-enterprise AI
   adoption, top AI-talent density in Europe.)
6. **Competition** — the §3.2 table collapsed to one slide: first-party = single-vendor, review
   layer = cloud-only opinions, orchestrators = dead or free; we own the intersection.
7. **Product** — live demo or 90-second video. The demo *is* the pitch (DHH/Docker rule).
8. **Business model** — free GUI funnel → $20 Pro → $50+ team governance; BYOK = no inference
   margin risk; benchmarked against CodeRabbit/Graphite/Cursor price points.
9. **Traction** — waitlist, retention curve, design partners, weekly verified merges.
10. **Team** — why us: shipped a real Git engine (the hard prerequisite every wrapper lacks);
    Windows/.NET depth in a Mac-first field.
11. **Ask** — tailored per audience.

## 15.2 Demo script (10 minutes, rehearse for August)

1. *(0–2 min)* Open a big real repo. Graph renders instantly; flip themes; partial-stage a hunk
   by dragging lines. Message: "this is a real Git client, not a wrapper."
2. *(2–4 min)* The pain: spawn two agent tasks; show worktree isolation (no `.git/index.lock`
   roulette, nothing touches your working directory).
3. *(4–7 min)* The wedge: agent finishes → verification runs the test suite → one branch passes,
   one fails → the failing one never reaches review. Open the passing diff: risk-ranked hunks,
   provenance gutter ("Agent A, plan #12").
4. *(7–9 min)* Merge the first branch → the second's verification goes stale → queue auto
   re-verifies before offering the merge. "No other product on the market does this step."
5. *(9–10 min)* Close on the audit view: every spawn, plan approval, and merge, attributable.
   "This is what your compliance team asks for in 2027."

*(Anything not yet built runs scripted/mocked in August — labeled honestly as "landing in vN,"
per the current Implementation Plan sequencing. Do not fake it silently for an experienced
founder; he will probe.)*

## 15.3 The one-pager pre-read (for the advisor; send 3–5 days ahead)

Keep to one page; this is the content:

> **Mainguard — make AI-agent code safe to merge.**
>
> **Problem.** 90% of developers now use AI daily and agents author code at scale (Copilot's
> agent alone: 1M+ PRs in five months) — but 87% of developers distrust agent accuracy, review
> time is up 91%, and delivery stability is measurably degrading. Every vendor ships "run agents
> in parallel"; nobody ships "trust what they produced."
>
> **Product.** A native desktop Git client (built, working — this is the demo) evolving into a
> control plane that runs agents in isolated sandboxes, requires your test suite to pass before
> a human reviews the diff, ranks review by risk with per-line agent attribution, re-verifies
> anything the merge queue lets go stale, and keeps an audit trail of every agent action.
> Vendor-neutral (Claude Code, Codex, OpenCode), local-first, Windows-first — the category
> leader is Mac-only and the largest developer OS is unserved.
>
> **Business.** Free Git client (no login) as the funnel → $20/mo Pro (verification pipeline,
> BYO agent subscriptions) → $50+/seat teams (merge governance + audit). The 2026 failure ledger
> (Bloop, Terragon) proves orchestration alone doesn't monetize; verification and governance
> price against CodeRabbit ($24–48/dev/mo) and Graphite (~$40).
>
> **Status.** Git core shipped; verification pipeline in development; launch: free client ~Oct,
> agent layer ~Q4. Team scaling 1→5. Pre-revenue by design until design partners validate.
>
> **What I want from an hour with you:** your judgment on launch sequencing, first paying
> customers, and bootstrap-vs-raise — plus anything you'd steal or kill in this plan.

## 15.4 Failure modes to avoid (technical-founder classics)

Too deep on architecture, no pain narrative; no specific customer; burying the demo behind
slides; "50 agents" scale claims the hardware can't cash; pitching the tech instead of the
switching costs + workflow depth (what 2026 investors explicitly ask of AI startups).

---

# Part XVI — The August advisor meeting (full playbook)

**Context:** August trip to visit girlfriend + her parents. Her father is a successful startup
founder, currently building an AI startup ("Daemon"). Goal: get his help, advice, and — if it
develops naturally — a formal advisor relationship.

## 16.1 Objective — and what "success" actually is

Rank the outcomes honestly and aim at the top of the list:

1. **A real working relationship**: he gives substantive advice on 2–3 named decisions, and a
   reason exists to talk again in a month. *(This is the win condition.)*
2. **Daemon as a design partner**: his engineering team — an AI startup running coding agents
   daily is Mainguard's exact ICP — tries Mainguard on real work and gives feedback. A far better
   ask than "be my advisor": concrete, bounded, useful to *him* (his team gets a tool +
   influence over it), and it produces evidence instead of promises.
3. **Eventually, a formal advisor role** — weeks or months later, after the advice loop has run.
4. **Explicitly NOT a goal in August: money.** The research consensus is blunt: *"Ask for money,
   get advice. Ask for advice, get money — twice."* Asking a girlfriend's father for investment
   on the first real conversation converts a potential champion into a polite decliner and puts
   the personal relationship at risk. If he raises investing himself, say you'd value his
   involvement more than his check right now, and park it.

## 16.2 Why this approach (the evidence, compressed)

- **Don't open with "will you be my advisor?"** — it's asking for a relationship before the
  first date (Startups.com). Ask for advice on specific problems; let the advisory role emerge
  from repeated useful sessions.
- **Specific asks attract serious engagement; vague ones attract polite nods** (Pholus, Feel the
  Boot). "I need your judgment on X, where you've done this before" beats "any thoughts?"
- **The cycle that builds an advocate:** ask → act on the advice → report back what happened.
  The report-back is the step most founders skip and it's the one that creates an emotional
  stake in Mainguard's success.
- **Don't pitch at dinner.** A social-setting ambush is the #1 documented mistake with
  warm/family connections. Ask *before the trip* for a dedicated hour.

## 16.3 Before the trip — checklist

- [ ] **Ask for the session in advance**, framed as advice-not-pitch. Template (adapt voice):
  > "While we're visiting in August, could I steal 45–60 minutes of your time? I've been
  > building a developer-tools product for the last year and I'm at the point where a few
  > decisions matter a lot — launch sequencing and how to get the first paying teams. You've
  > done zero-to-one twice; I'd really value your judgment. I'll send a one-pager ahead so I
  > don't waste your time explaining basics."
- [ ] **Research Daemon.** Public search as of 2026-07-06 could not conclusively identify an AI
  startup named "Daemon" (most plausible candidate: daemon.ai, likely stealth; others:
  daemons.ai agent platform, Daemon/dae.mn consultancy, Daemon Automation). Once you have his
  full name: LinkedIn → Crunchbase → talks/podcasts → his previous company's story (how it was
  funded, found customers, exited/scaled). Know his war stories before he tells them; ask
  questions his history makes him uniquely qualified to answer.
- [ ] **Find the genuine overlap with Daemon.** If Daemon builds with AI agents (very likely),
  his team lives Mainguard's problem. Prepare — but do not force — the design-partner ask.
- [ ] **Send the one-pager 3–5 days ahead** (Part XV §15.3).
- [ ] **Rehearse the 10-minute demo** (§15.2) on a laptop that works offline. Scope to what's
  real; anything mocked gets labeled "landing in vN" out loud. Pre-decide honest answers to:
  How many users? (none yet — launching Q4, here's the waitlist/design-partner plan.) Why won't
  Anthropic/Cursor kill you? (vendor-neutrality + real Git engine + local-first; rehearse §3.9
  and Part XX.) How do you make money? (Part VIII, with the Bloop/Terragon failure ledger.)
- [ ] **Prepare your two or three decision questions** (§16.4). Write them down. They are the
  agenda.

## 16.4 The meeting itself (45–60 min)

Structure: ~70% his input on decisions, ~30% context from you. He should talk more than you.

1. **(5 min) Context**, tight: what Mainguard is, the one-paragraph strategy (Part I), where the
   product actually stands (working Git client; verification pipeline in development).
2. **(10 min) Demo.** Let the product speak; stop narrating when he starts driving.
3. **(25–35 min) The decision questions.** Pick 2–3, phrased so his experience is the input:
   - *Sequencing:* "I can launch the free Git client in October and the agent-verification
     layer 4–8 weeks later. Would you sequence it that way, or lead with the agent story? How
     did you stage your first launch?"
   - *First customers:* "My plan is 3–5 design-partner teams before charging anyone. How did
     you land your first paying customers at [his previous company] — and what would you do
     differently?"
   - *Bootstrap vs raise:* "Given the field (funded free competitors, platform vendors
     absorbing features), would you bootstrap to revenue or raise a seed on design-partner
     traction? What did raising cost you that you didn't expect?"
   - *(If Daemon overlap is real) The practitioner question:* "How does your team at Daemon
     handle reviewing and merging agent-written code today? What would make that safer or
     faster?" — discovery gold *and* the natural bridge to the design-partner ask.
4. **(5 min) Close with one concrete ask** — the smallest, most useful one the conversation
   supports (§16.5). Then thank him and stop. Do not stack asks.

**Listen for and write down:** his objections (they preview every investor objection you'll ever
hear), names he drops ("you should talk to…" — never ask for intros directly in meeting one; let
him offer), and anything about Daemon's own workflow pain.

## 16.5 The asks, ranked (use at most one, maybe two)

1. **The follow-up loop (always make this one):** "Can I email you in a few weeks with what I
   did with this advice and how it went?" — costs him nothing, establishes the cadence, and is
   the actual mechanism by which advisors happen.
2. **Daemon as design partner (if the overlap surfaced naturally):** "Would one or two of your
   engineers be willing to try it on a real repo for a month? Free, obviously — I need brutal
   feedback from a team that runs agents daily, and you'd shape the roadmap." Bounded, valuable
   both ways, and it makes him an *evidence-based* believer or skeptic.
3. **The advisor role — NOT in this meeting.** If he's engaged, it will be obvious; raise it on
   the second or third follow-up ("Would you consider making this official? Standard FAST
   agreement, no obligations beyond what we're already doing.").
4. **Money — never first, never in August.** See §16.1.

## 16.6 Formalizing later — FAST norms (so terms are never awkward)

When (if) the advisor conversation happens, use the Founder/Advisor Standard Template (fi.co/fast,
v3 June 2026) — one page, no lawyers:

| Mainguard stage | Standard advisor (monthly session) | Expert advisor (contacts, hands-on, e.g. design-partner sponsorship + intros) |
|---|---|---|
| Idea/pre-launch (now) | 0.25% | up to 1.0% |
| Post-launch/startup | 0.20% | 0.80% |

Terms: **2-year monthly vesting, 3-month cliff** — if the relationship fizzles in the first
quarter, no equity moved and nothing is awkward at Thanksgiving. That cliff is precisely why
formalizing is *safer* for a family-adjacent relationship, not riskier — say so if it comes up.

## 16.7 Mistakes to avoid (the compressed list)

- Pitching at the dinner table, or letting the session happen ad hoc. Schedule it.
- Vague asks ("any advice?"), stacked asks, or leading with the advisor/money question.
- Overselling product state. He has founder pattern-matching; honesty about "built vs planned"
  *is* the credibility play. The Git client being real and fast does the impressing.
- Talking architecture for 20 minutes. He'll care about customers, moat, and you.
- Asking for an NDA (reads as amateur), or for intros he hasn't offered.
- Skipping the follow-up. The report-back email in 2–4 weeks is the whole game.

## 16.8 After the visit — cadence

1. **Within 48h:** thank-you note; one line on the single most useful thing he said.
2. **Within 2–4 weeks:** the report-back — "you said X, I did X, here's what happened," plus one
   new question. Repeat monthly-ish.
3. **If Daemon design-partnership started:** treat his engineers as Track B design partners
   (Part XIII) — bi-weekly feedback, real repos, and make their fingerprints visible in the
   changelog (advisors advocate for things they shaped).
4. **When the loop has run 2–3 times:** the FAST conversation (§16.6).
5. **If a raise ever happens:** he hears it first, as an insider — by then, if this worked, he's
   the one offering the check and the intros. ("Ask for advice, get money — twice.")

---

# Part XVII — Metrics, KPIs & the seed bar

## 17.1 Product KPIs (the full set)

1. **Weekly active repos** (free tier health) — instrument before any launch.
2. **Agent runs verified per week**; % of merges executed against non-stale verification
   ("verification staleness at merge" — measures merge-queue integrity).
3. **Plan-approval adoption rate** — % of worker tasks preceded by an approved plan (leading
   indicator of the trust workflow landing).
4. **Swarm concurrency rate** — average active agents/session (target >1.5).
5. **Branch acceptance vs rejection ratio** (agent branches merged vs deleted).
6. Free→Pro conversion (target ≥2%); logo count and seat expansion in Track B teams.
7. **Token spend per merged branch** — cost efficiency; feeds the enterprise budget story
   (extended per §D-6: also cost of rejected work).
8. Auto-heal success rate / circuit-breaker escalation rate (once the repair loop exists).
9. Time-to-resolution vs human baseline.
10. *(Phase C, cloud era)* zero-touch deployments — end-to-end "publish to web" events with no
    manual code.

**Investor-grade pair:** weekly active repos + agent runs verified/merged per week — the metrics
investors can't get from download counts.

## 17.2 The seed bar

See Part X §10.2 — conventional thresholds (~5,000+ MAU / 500 paying / $300–500K ARR; burn
multiple <2×) vs the AI-infra reality (growth rate + logo quality; GitButler and Conductor
precedents); our low-end fundable story: 3–5 named design-partner teams on the merge queue +
free-GUI retention + verified-merges growth at $10–50K MRR, positioned as agent infrastructure.

---

# Part XVIII — EU expansion & the AI-Act / sovereignty angle

**EU AI Act (state honestly):** GPAI enforcement powers activate **2 Aug 2026**; Article 50
transparency applies; but the May-2026 "Digital Omnibus" provisionally postponed high-risk
obligations to Dec 2027, whether Article 50 "text" covers source code is unsettled, and
Article-12 logging does **not** literally mandate cryptographic immutability. Pitch the audit
trail as **"audit-grade evidence + where EU procurement is heading"** — auditors are already
asking (Codacy, the AI-BOM movement) — never as a deadline scare. The Aug-2 date is still a
legitimate *marketing moment* for the evidence-pack feature (hash chain + identity +
`audit verify` + SIEM export) — ship the claim before competitors do. Converging frameworks to
cite in enterprise conversations: EU AI Act Art. 12, NIST AI RMF, OWASP LLM Top 10, ISO 42001
(via the Forrester AEGIS cross-map).

**Digital sovereignty is a tailwind we're born into:** >80% of Europe's digital infrastructure is
imported and US providers hold ~85% of the EU cloud market; the **EuroStack** movement and "Buy
European" procurement proposals moved into mainstream policy in 2026 — ministers describing
digital sovereignty as "a matter of national survival"; well-governed open/auditable,
self-hostable tools are explicitly favored, and organisations that move early face less
disruption later ([CEPS](https://www.ceps.eu/ceps-publications/eurostack-a-european-alternative-for-digital-sovereignty/),
[euro-stack.com](https://euro-stack.com/), [EU Perspectives](https://euperspectives.eu/2026/02/europe-bets-on-eurostack/)).
Mainguard is structurally the compliant answer: **European vendor, local-first (code never leaves
the machine), source-available (FSL) daemon, default-deny egress, BYO keys**. Actions:
① a "European digital sovereignty" page on the website (local-first + FSL + EU entity);
② get listed in European-alternative directories (european-alternatives.eu, the EuroStack
directory); ③ use it as the wedge for Dutch/German public-sector-adjacent ISVs that US cloud
tools can't enter.

**Expansion sequencing:** NL (design partners, logos, subsidies) → **DACH** (the world's densest
Windows/.NET-enterprise + compliance-culture market; Germany is 20 minutes from Enschede) →
Nordics/UK → US enterprise (with the seed round). Don't localize the product; localize the sales
conversations and case studies. First DACH motion: the same Tier-2 map for Germany (Zühlke,
adesso, msg-systems-type consultancies) once 2–3 Dutch logos exist.

---

# Part XIX — Dutch success-story playbooks (pattern-matching)

| Company | Path | Lesson for Mainguard |
|---|---|---|
| **Mendix** (Rotterdam, 2005) | €250k from henQ → €1.4M → $13M first VC round (2011) → ~€13M largely from Dutch fund Prime (2012) → ~€25M Battery Ventures (2014) → moved GTM to Boston → **$730M Siemens exit 2018**; 4,000+ enterprises incl. KLM, Philips, Royal DSM ([Wikipedia](https://en.wikipedia.org/wiki/Mendix), [Silicon Canals](https://siliconcanals.com/siemens-acquires-mendix-e-628-m/)) | The Dutch enterprise-software playbook: local henQ-style seed + Dutch enterprise logos first, US capital for scale. Named Dutch logos (KLM-class) were the credibility engine — our Tier-3 map exists for this |
| **Framer** (Amsterdam, 2015; founders sold Sofa to Facebook 2011) | Prototyping tool plateaued (~$4M rev peak 2019, then slumped) → **pivoted May 2022** to no-code website builder → $24M Series B (Atomico) → $27M C (Meritech) → $100M D at **$2B valuation** (2025); >$50M ARR, targeting $100M in 2026 ([Forbes](https://www.forbes.com/sites/iainmartin/2023/09/28/framer-website-builder-design-software-figma-challenger/), [EU-Startups](https://www.eu-startups.com/2025/08/dutch-startup-framer-raises-e85-6-million-at-e1-7-billion-valuation-to-expand-no-code-web-platform/)) | A world-class Dutch product team can mis-aim the wedge for *six years* and still win by moving one step toward where the money is. Our equivalent move is already made on paper: orchestration → verification. Execute it |
| **Weaviate** (Amsterdam, 2019) | OSS vector DB; community-first GTM (5M+ downloads, 14k stars, 2,000+ production companies) → Series A 2022 → **$50M Series B** (Index, with Battery, NEA, ING Ventures) = $67.7M total; Ricoh CVC investment Mar 2026 ([PR Newswire](https://www.prnewswire.com/news-releases/weaviate-raises-50-million-series-b-funding-to-meet-soaring-demand-for-ai-native-vector-database-technology-301803296.html)) | The devtool GTM that works from NL: open/auditable core + global developer community + cloud monetization. Validates the FSL-daemon + free-GUI funnel + cloud-worktrees sequence; Amsterdam AI credibility helps raise |
| **CodeSandbox** (Enschede-origin) | Browser IDE from a Twente founder; angel-funded via Arches Capital; became a global devtool | Enschede can birth a global devtool. Use this story with local press, Novel-T, and angels — it primes them to believe it can happen twice |

Common thread: **none of them won in the Dutch market — they won *from* it**, using Dutch seed
money, Dutch enterprise logos, and Dutch talent as the launchpad for a global (usually
developer-led) motion. That is exactly this document's structure.

---

# Part XX — Risks, stated honestly

1. **Platform absorption (highest).** Claude Code Desktop already ships worktrees + autoVerify +
   diff review; the Copilot app is GA with Agent Merge; Cursor Origin (fall 2026) aims at
   agent-scale review + merge queues; GitKraken Kepler is free during preview; OpenAI Symphony
   shows the platform owner moving into orchestration. **Mitigation:** vendor-neutrality,
   local-first, and the compound pipeline (any single feature has a ~2-quarter exclusivity
   window; the combination plus a real Git engine does not). **Tripwire:** if Origin ships local
   execution + provenance, re-plan within a quarter.
2. **Monetization ceiling.** Orchestration is worth $0; we believe verification + governance is
   worth $20–50. The design-partner program exists to prove willingness-to-pay *before* we build
   the full enterprise layer. **Tripwire:** if Track B teams won't pre-commit to paid pilots by
   two months post-act-two, revisit pricing/packaging.
3. **Execution capacity.** One founder + a forming 5–6 person team, against funded incumbents.
   The M1–M3 Git core remains the prerequisite for every differentiator — protect that
   sequencing from launch-marketing pressure.
4. **BYOK/ToS fragility.** Anthropic's 2026 OAuth enforcement showed vendors will constrain
   harnesses. API-key path primary, local-model support as the pressure valve, adapter layer
   vendor-neutral (§7.2).
5. **Naming.** MergeLoom occupies "governed AI coding" language with a colliding name and an SEO
   wall (161 bulk posts); every real-word candidate is being claimed weekly. Decide before KVK
   registration and the trademark filing (Part VI).
6. **Timing.** The August demo date is fixed; scope the demo to what's real (script the rest,
   labeled). A polished honest demo beats a broad fragile one — especially for this audience.
7. **NL-specific (new):** the home market is small — treat NL traction as *evidence*, not
   *revenue*; grant windows are rigid (MIT = one day in April; WBSO = before work starts) so the
   funding calendar must be maintained like a release calendar; TNW's death shows Dutch
   ecosystem institutions can vanish — anchor on communities (dotNed, HN), not single events.
8. **Hardware/scale honesty.** ~4–6 agents on 16 GB is the local ceiling (WSL2 takes 50% RAM by
   default); rate limits bind earlier. Never re-inflate the "50 agents" claim; cloud worktrees
   are the scale answer.

---

# Part XXI — Master action calendar

**July 2026 (now):**
- [ ] Lock the demo scope for August; rehearse the §15.2 script end-to-end twice
- [ ] Send the advisor pre-read (§15.3) + schedule the session (§16.3 template)
- [ ] Research Daemon properly once the founder's name is known (candidates logged in §16.3)
- [ ] Decide the name (MergeLoom forcing function, Part VI) → domain/WHOIS + trademark screen
- [ ] Landing page + waitlist + X/LinkedIn accounts; first build-in-public clip this week
- [ ] Register with Novel-T; book intro with their subsidy-advice partners
- [ ] Start the 40-company design-partner list (Part IX map); first 5 discovery interviews
- [ ] Submit Techorama NL CFP (Sessionize)
- [ ] Draft essay (a) on the index.lock problem
- [ ] Instrument weekly-active-repos + verified-merges metrics before any launch

**August:**
- [ ] Advisor meeting (advice-not-money; Daemon design-partnership is the prize; 48h thank-you;
      report-back in 2–4 weeks; act on one piece of advice)
- [ ] Incorporate Holding BV + Werk-BV; IP assignment; bank + accountant
- [ ] eHerkenning → **WBSO application filed before the development month starts**
- [ ] Interviews 6–15

**September:**
- [ ] Beta cohorts (10–20 invites, Linear model); Discord seeded (20–50 core members)
- [ ] Trust assets live: security architecture doc v1, telemetry policy, FSL statement
- [ ] Essay (a) published ~2 weeks before act-one; interviews 16–25; Track B qualification calls
- [ ] MoR account (Paddle/LS) + EUR pricing page; Dutch press contacts warmed (Tweakers, IO+)

**October:**
- [ ] **Show HN act one** (free Git GUI) when beta-cohort retention looks healthy + PH +
      Console.dev + subreddits + Tweakers/IO+ same week
- [ ] **Techorama NL Oct 26–28, Utrecht** — talk or hallway-track demos
- [ ] Post afstudeeropdrachten at UT/Saxion for the Feb-2027 block

**November–December:**
- [ ] **Show HN act two** (verification story) + r/ClaudeAI + TLDR + creators + AG Connect /
      Silicon Canals
- [ ] Ship Pro; convert Track B to paid pilots (tripwire watch, Part XX §2)
- [ ] Investor intent letter → **VFF application via Oost NL**
- [ ] Advisor loop iteration 2–3; FAST conversation only if it's obviously working

**Q1–Q2 2027:**
- [ ] MIT Haalbaarheid application ready for the April day-one window (Overijssel; reminder Feb)
- [ ] Seed decision per Part-X triggers; DACH outreach on the back of 2–3 Dutch logos
- [ ] New stock-option regime (target 1 Jan 2027) → issue ESOP; first hires (WBSO-subsidized)
- [ ] Innovatiebox groundwork with the accountant at first profitable quarter
- [ ] Cloud worktrees private beta within ~2 quarters of desktop GA; Innovatiekrediet quick-scan
      when that buildout starts

---

# Appendix A — Key research sources (global corpus, verified 2026-07-06/07)

**Category & competitors:**
- Conductor Series A: conductor.build/blog/series-a · changelog: conductor.build/changelog · YC profile
- Vibe Kanban shutdown: vibekanban.com/blog/shutdown · Terragon shutdown: docs.terragonlabs.com/docs/resources/shutdown
- GitKraken: gitkraken.com/kepler · gitkraken.com/blog (Apr/June 2026, Agent Mode + Kepler) · help.gitkraken.com/kepler/agent-integrations · PRNewswire Desktop 12.0 release · SD Times "Code Flow"
- GitButler Series A: blog.gitbutler.com/series-a
- GitHub Copilot app: github.blog/news-insights/product-news/github-copilot-app-the-agent-native-desktop-experience · github.blog usage-based billing · GitHub Desktop 3.6 changelog · byteiota, digitalapplied, windowsforum, helpnetsecurity, tokenmix, costbench coverage
- Claude Code Desktop & worktrees: code.claude.com/docs/en/desktop, /worktrees, /agents, /sandbox-environments
- Codex app: openai.com/index/introducing-the-codex-app · developers.openai.com/codex/changelog, /subagents · Symphony: openai.com/index/open-source-codex-orchestration-symphony
- Cursor 3 / Origin / Graphite: cursor.com/blog/graphite · datacamp.com/blog/cursor-3 · cursor.com/docs/configuration/worktrees · June 17 2026 Origin coverage
- Jules: developers.google.com/jules/api · jules.google/docs/changelog · github.com/google-labs-code/jules-action
- Docker Sandboxes: docs.docker.com/ai/sandboxes (overview, get-started, faq, security/policy) · docker.com/blog microVM architecture posts · community walkthroughs (rushis, ajeetraina, andrewlock)
- Nimbalyst: github.com/stravu/crystal · github.com/nimbalyst/nimbalyst · nimbalyst.com/pricing, /features, /blog
- Superset: superset.sh · github.com/superset-sh/superset (+issue #499) · HN item 48236770
- Parallel Code: parallelcode.app · github.com/johannesjo/parallel-code · dev.to post
- Composio AO: github.com/ComposioHQ/agent-orchestrator (+discussion 526)
- Factory: factory.ai/news (terminal-bench, series-b) · Series C coverage (tech-insider.org)
- Sculptor/Imbue: imbue.com/sculptor · docs.imbue.com/features/containers · imbue.com/blog
- container-use: github.com/dagger/container-use · dagger.io/blog/agent-container-use
- CodeRabbit: TechCrunch Sept 16 2025 · Greptile Series A: greptile.com/blog/series-a
- Orca: onorca.dev · github.com/stablyai/orca · Pane: runpane.com (+/agent-managers-for-windows) · Intent: Augment Code comparison pages
- Landscape: github.com/andyrewlee/awesome-agent-orchestrators · augmentcode.com/tools/open-source-agent-orchestrators · nimbalyst.com/blog/best-agent-management-tools-2026 · superset.sh

**MergeLoom (all fetched 2026-07-07):** mergeloom.ai — homepage, /product/* ×8, /solutions/* ×4,
/pricing, /docs (+install-worker), /compare/* ×3, /subprocessors, /terms-and-conditions,
/refund-policy, /blog, sitemaps · github.com/MergeLoom · linkedin.com/company/mergeloom-ai ·
negative results: HN, Reddit, Crunchbase, funding press, founder names, customer logos, status
page, API docs (searched, nothing found).

**Capability probes:**
- Merge queues: docs.github.com merge-queue docs · docs.mergify.com/merge-queue · tenki.cloud, kairi-ai, getautonoma posts
- Agent Trace / provenance: cognition.com/blog/agent-trace · github.com/cursor/agent-trace · agent-trace.dev · infoq.com Feb 2026 · git-ai: github.com/git-ai-project/git-ai · repowise.dev/guides/agent-provenance · opentools.ai/tools/hunk
- Audit/EU AI Act: agentaudit.co.uk/solutions/eu-ai-act · asqav.com blog · compliora.co · augmentcode.com/tools/ai-coding-tools-eu-ai-act-compliance · kognitos, miniorange, dev.to audit-trail posts
- Egress/WSL2: code.claude.com/docs/en/sandbox-environments · truefoundry.com/blog/claude-code-sandboxing · codex.danielvaughan.com Windows sandbox · penligent.ai sandbox-bypass analysis
- External-PR intake: docs.devin.ai/work-with-devin/devin-review · developers.openai.com/codex/integrations/github
- Rate limiting: tamirdresher.com (multi-agent rate limiting) · truefoundry, zuplo token-based limiting

**Market data:**
- Stack Overflow 2025 AI survey: survey.stackoverflow.co/2025/ai · DORA 2025: cloud.google.com/resources/content/2025-dora-ai-assisted-software-development-report
- Octoverse 2025: github.blog · SlashData: slashdata.co · Faros AI PR/churn data
- Verification bottleneck: futurumgroup.com (independent review layer) · srlabs.de/blog/ai-verification-bottleneck · byteiota 96%-don't-trust piece
- Meta RADAR: engineering.fb.com (Aug 2025), arXiv 2605.30208
- EU AI Act timeline + Digital Omnibus: artificialintelligenceact.eu · Gibson Dunn / Covington May 2026 analyses
- Git GUI market: thesoftwarescout.com GitKraken-vs-Fork-vs-Tower · git-tower.com/blog/best-git-client

**Technical verification (v2):**
- git-fsmonitor Linux: git-scm.com/docs/git-fsmonitor--daemon · WSL2 inotify/9P: github.com/microsoft/WSL/issues/4739 · WSL2 memory: learn.microsoft.com wsl-config · libgit2 rebase: github.com/libgit2/libgit2/issues/6332 · Anthropic OAuth ban: theregister.com (Feb 20 2026) + HN 46549823 · AF_VSOCK/ASP.NET: github.com/dotnet/aspnetcore/issues/34050

**GTM evidence & advisor norms:**
- graphite.dev/blog/launch · supabase.com/blog/supabase-how-we-launch · warp.dev/blog/lifting-login-requirement · markepear.dev HN launch guide · console.dev/selection-criteria
- fi.co/fast (FAST v3, June 2026) · startups.com "How to Recruit a Rockstar Advisor" · Pholus / Feel the Boot on specific asks

# Appendix B — Key research sources (Netherlands & EU, new 2026-07-07)

**NL market:** [CBS Digitalisering en kenniseconomie 2025](https://www.cbs.nl/nl-nl/longread/rapportages/2026/digitalisering-en-kenniseconomie-2025?onepage=true) ·
[CBS AI Monitor 2024](https://www.cbs.nl/en-gb/longread/aanvullende-statistische-diensten/2025/ai-monitor-2024?onepage=true) ·
[CBS: Increasing use of AI by business](https://www.cbs.nl/en-gb/news/2025/09/increasing-use-of-ai-by-business) ·
[Eurostat: AI in enterprises](https://ec.europa.eu/eurostat/statistics-explained/index.php?title=Use_of_artificial_intelligence_in_enterprises) ·
[NL Times AI adoption](https://nltimes.nl/2025/12/14/one-six-dutch-companies-now-uses-ai-marketing-administration) ·
[State of Dutch Tech 2026 (Techleap)](https://techleap.nl/reports/state-of-dutch-tech-report-2026) ·
[Invest-NL summary](https://www.invest-nl.nl/en/news/state-of-dutch-tech-2026-ready-for-scaling-to-global-size) ·
[Viotta on Dutch VC 2026](https://viottalaw.com/dutch-venture-capital-and-tech-market-2026-foreign-capital-deeptech-strength-and-the-dutch-scale-up-challenge/) ·
[trade.gov NL ICT](https://www.trade.gov/country-commercial-guides/netherlands-netherlands-information-and-communication-technology) ·
[EURES NL labour market](https://eures.europa.eu/living-and-working/labour-market-information/labour-market-information-netherlands_en) ·
[NICCT IT industry](https://nicct.nl/it-industry-in-the-netherlands/) ·
[CBS via ChannelConnect](https://www.channelconnect.nl/mkb-en-ict/cbs-omzet-it-dienstverleners-groeit-in-tweede-kwartaal-met-44-procent/) ·
[Computer Futures .NET trends](https://www.computerfutures.com/nl-be/kenniscentrum/software-mobile-engineering/dot-net-developers-trends/) ·
[AG Connect C# popularity](https://www.agconnect.nl/tech-en-toekomst/development/populariteit-c-flink-gestegen)

**Twente ecosystem:** [Novel-T START](https://novelt.com/en/products/1/start-program/) ·
[Incubase](https://incubase.nl/en/about-incubase/) · [UT startup support](https://www.utwente.nl/business/startupondersteuning/) ·
[Kennispark strategy 2026–2035](https://www.utwente.nl/nieuws/2026/3/843058/nieuwe-strategie-moet-kennispark-twente-laten-groeien-naar-700-bedrijven-en-16.500-banen) ·
[kennispark.nl](https://kennispark.nl/en/) ·
[Cottonwood fund III](https://www.cottonwood.vc/cottonwood-technology-fund-opent-derde-high-tech-fonds/) ·
[Cottonwood at Kennispark](https://kennispark.nl/en/cottonwood-technology-fund-opens-third-high-tech-fund/) ·
[Twente Technology Fund](https://nl.linkedin.com/company/twente-technology-fund) ·
[Sigmax](https://twente.com/nl/organisaties/17937/sigmax/) · [Enschede software companies](https://www.softwarebedrijf-info.nl/enschede) ·
[Oost NL SIIA digital economy](https://oostnl.maglr.com/siia-digital-economy-industry-english/the-strength-of-the-east-netherlands)

**Funding:** [RVO WBSO](https://english.rvo.nl/subsidies-financing/wbso) · [WBSO 2026 guide](https://www.informer.nl/belastingen/aftrekposten/wbso) ·
[WBSO kalender](https://www.rvo.nl/subsidies-financiering/wbso/kalender) ·
[Overijssel MIT loket](https://regelen.overijssel.nl/Producten_en_diensten/Subsidies/Werken_en_ondernemen/MIT_Haalbaarheidsprojecten) ·
[MIT via leap.nl](https://www.leap.nl/subsidies/mit-haalbaarheidsprojecten/) · [RVO MIT](https://www.rvo.nl/subsidies-financiering/mit) ·
[RVO VFF voorwaarden](https://www.rvo.nl/subsidies-financiering/vroegefasefinanciering/voorwaarden) · [RVO VFF-regionaal](https://www.rvo.nl/subsidies-financiering/vff-regionaal) ·
[Ondernemersplein VFF](https://ondernemersplein.overheid.nl/subsidies-en-regelingen/vroegefasefinanciering-vff/) ·
[RVO Innovatiekrediet](https://english.rvo.nl/subsidies-financing/innovation-credit) (+[conditions](https://english.rvo.nl/subsidies-financing/innovation-credit/conditions)) ·
[Belastingdienst Innovatiebox](https://www.belastingdienst.nl/wps/wcm/connect/bldcontentnl/belastingdienst/zakelijk/winst/vennootschapsbelasting/innovatiebox/) ·
[Innovatiebox 2026 uitleg](https://www.informer.nl/belastingen/aftrekposten/innovatiebox) ·
[Rabobank 14 starter schemes](https://www.rabobank.nl/bedrijven/eigen-bedrijf-starten/financien/14-subsidies-en-regelingen-voor-startende-ondernemers) ·
[SubsidieMatch Overijssel pre-seed](https://www.subsidiematch.app/regio/overijssel-fase-pre-seed) ·
[Overijssel innoveren](https://www.overijssel.nl/onderwerpen/economie/ondernemerschap/innoveren)

**VC/angels:** [nphard.vc](https://nphard.vc/) + [Fund II](https://siliconcanals.com/np-hard-ventures-launches-e25m-fund-ii/) ·
[henq.vc](https://www.henq.vc/) + [Fund V](https://www.eu-startups.com/2025/11/why-henq-chooses-the-roads-less-travelled-inside-the-dutch-vcs-new-e67-6-million-fund-for-the-odd-ones-out/) ·
[curiosityvc.com](https://www.curiosityvc.com/) · [Volta seed fund](https://www.volta.ventures/volta-launches-e20m-dedicated-seed-fund-for-benelux-startups-and-announces-first-investments/) ·
[newion.com](https://newion.com/) + [Fund IV](https://siliconcanals.com/amsterdams-newion-fund-iv-close-130m/) ·
[Vestbee NL VC list](https://www.vestbee.com/insights/articles/top-vc-funds-in-the-netherlands-to-finance-your-startup) ·
[BAN Nederland](https://bannederland.nl/) · [Leapfunder](https://www.leapfunder.com/) · [Golden Egg Check](https://goldeneggcheck.com/) ·
[Antler residency](https://www.antler.co/residency)

**Legal/hiring:** [BV kosten 2026](https://holdwise.nl/kennisbank/bv-oprichten-kosten-stappen) · [Ligo BV kosten](https://www.ligo.nl/kennisbank/bv-oprichten-kosten/) ·
[DGA-salaris 2026](https://wetaxus.nl/kennisbank/dga-salaris-berekenen-2026) · [DGA regels](https://www.bvbeginnen.nl/dga-salaris/) ·
[ZZP vs BV](https://zzp-pulse.nl/nl/blog/zzp-vs-bv) · [MoR comparison](https://fintechspecs.com/blog/stripe-vs-paddle-vs-lemon-squeezy-vs-polar-merchant-of-record-b2b-saas/) ·
[MoR fees](https://www.globalsolo.global/blog/stripe-vs-paddle-vs-lemon-squeezy-2026) ·
[Optieregeling wetsvoorstel (PwC)](https://www.pwc.nl/nl/actueel-en-publicaties/belastingnieuws/loonbelasting-en-sociale-verzekeringen/consultatie-wetsvoorstel-fiscale-stimulering-startups.html) ·
[Optieregeling uitleg](https://www.startup-recht.nl/insights/aandelenopties-bij-startups-en-scale-ups-hoe-werkt-de-fiscale-regeling-nu-en-wat-verandert-er-mogelijk) ·
[Salaris medior](https://ubuntustaffing.nl/blog/wat-is-het-salaris-van-een-medior-software-developer-in-nederland/) · [Salaris senior](https://searchcompany.nl/werving-en-selectie-blogartikel/wat-verdient-een-senior-developer-in-2025/) ·
[Saxion stage](https://www.saxion.nl/bedrijven/stage-en-afstuderen) · [Saxion stagevergoeding](https://www.saxnow.nl/nieuws/2023/december/voorstel-stagiairs-op-saxion-krijgen-straks-veel-meer-geld-vergoeding-naar-475-euro) ·
[UT talent](https://www.utwente.nl/business/talent-vinden/)

**Events/media/EU:** [Techorama NL 2026](https://www.techorama.nl/) (+[Sessionize CFP](https://sessionize.com/techorama-2026-netherlands/)) ·
[TNW shutdown](https://thenextweb.com/conference) · [Silicon Canals](https://siliconcanals.com/) ·
[CEPS EuroStack](https://www.ceps.eu/ceps-publications/eurostack-a-european-alternative-for-digital-sovereignty/) ·
[euro-stack.com](https://euro-stack.com/) · [EU Perspectives on EuroStack](https://euperspectives.eu/2026/02/europe-bets-on-eurostack/) ·
[EU Open Source Strategy](https://digital-strategy.ec.europa.eu/en/policies/open-source-strategy) ·
[Betabit](https://www.betabit.nl/en) · [Betabit at Techorama](https://www.techorama.nl/partners/betabit/) · [.NET Zuid](https://www.dotnetzuid.nl/sponsors/betabit)

**Success stories:** [Mendix (Wikipedia)](https://en.wikipedia.org/wiki/Mendix) · [Siemens acquisition](https://siliconcanals.com/siemens-acquires-mendix-e-628-m/) ·
[Siemens press release](https://press.siemens.com/global/en/pressrelease/siemens-strengthens-its-digital-enterprise-leadership-acquisition-mendix) ·
[Framer pivot (Forbes)](https://www.forbes.com/sites/iainmartin/2023/09/28/framer-website-builder-design-software-figma-challenger/) ·
[Framer Series D](https://www.eu-startups.com/2025/08/dutch-startup-framer-raises-e85-6-million-at-e1-7-billion-valuation-to-expand-no-code-web-platform/) ·
[Weaviate Series B](https://www.prnewswire.com/news-releases/weaviate-raises-50-million-series-b-funding-to-meet-soaring-demand-for-ai-native-vector-database-technology-301803296.html) ·
[Ricoh × Weaviate](https://www.ricoh.com/release/2026/0616_1)







