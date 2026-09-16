# Mainguard — Go-To-Market & Startup Execution Plan

**Date:** 2026-07-06
**Status:** Working plan — revisit after the August advisor meeting and again before launch.
**Reads alongside:** `docs/business/market-analysis/Mainguard_Market_Research_v2.md` (market/moat/licensing) and
`docs/business/market-analysis/Mainguard_Viability_And_Differentiation_2026-07.md` (positioning pivot). This doc
is the *execution* layer: who we sell to, how we launch, how we pitch, and how we win against the
July 2026 field. Competitive facts below were re-verified 2026-07-06.
**Companion:** `Advisor_Pitch_August_2026.md` — the playbook for the August advisor meeting.

---

## 1. The strategy in one paragraph

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

---

## 2. Positioning & messaging

### 2.1 The one-liner

> **Agent CLIs made it trivial to produce ten branches an hour. Nothing on the market makes it
> safe to merge them. Mainguard is where agent work becomes trustworthy commits on main.**

### 2.2 The 30-second elevator pitch (problem → solution → proof → ask)

> "Every team is suddenly reviewing 2× more pull requests because AI agents write them, and
> review time is up 91% — developers merge code they don't fully trust, written by agents they
> can't fully see. Mainguard is a native desktop control plane that runs coding agents in isolated
> sandboxes, makes them pass your test suite *before* a human ever reviews the diff, ranks the
> review by risk, records which agent wrote which line, and only merges what's verified. The Git
> client underneath is already built and fast; we're launching the free tier this fall.
> [ASK — tailor to listener: advice / design partner / intro.]"

### 2.3 Message hierarchy by audience

| Audience | Lead message | Supporting proof |
|---|---|---|
| Agent power users (running 3–6 Claude Code/Codex sessions) | "Never let an agent break your working directory again. Review five agent branches in twenty minutes, safely." | Sandboxes, worktree isolation, test-gates, risk-ranked review cockpit |
| Windows / .NET enterprise developers | "The premium native Git client Windows never got — and the only agent runner built for WSL2." | Native Avalonia perf, no Electron, no login, local-first |
| Engineering managers / CTOs (the buyers) | "Velocity *with* governance: agents that must pass tests before review, plus an audit trail of every agent action." | Merge queue + re-verification, per-hunk provenance, SIEM export roadmap |
| Investors | "The verification layer for the agent era — the bottleneck moved from writing code to trusting it, and we own the Git-native chokepoint." | SO 2025: 87% distrust agent accuracy; DORA 2025: stability degrading; Copilot agent alone shipped 1M PRs in 5 months |

### 2.4 Framing shortcuts (use deliberately)

- **"Conductor for Windows — with verification."** Journalists and Redditors will reach for an
  analogy; hand them this one. Conductor is the funded category leader and is Mac-only with no
  verification layer, so the comparison flatters us twice.
- **"Your agents' work, test-verified before you see it."** The single feature no shipped
  competitor has (only Imbue's Sculptor gestures at it).
- **Never** lead with "swarm," "50 agents," or "orchestration" — commoditized, and the words the
  dead companies used.

### 2.5 Trust posture (decide before HN decides for us)

The Warp precedent: a closed tool between a developer and their code paid a multi-year trust tax
(login requirement, telemetry) and won its best press by *removing* those. Locked posture for
Mainguard, consistent with the FSL decision in Market Research v2:

1. **Free Git GUI requires no account/login.** Ever. Login only where a cloud feature genuinely needs it.
2. **Local-first as the second sentence on the homepage:** code never leaves the machine; agents
   run in local sandboxes; BYO keys in the OS keyring; default-deny egress.
3. **Source-available daemon (FSL)** + published security architecture doc + opt-in telemetry with
   a published schema. GitButler's fair-source reception shows this neutralizes the objection.
4. Verification itself is marketed as a trust feature: "agents must pass your tests before you
   review their code" resonates with the 87% who distrust agent output.

---

## 3. Who we sell to — ICP and personas

### 3.1 Ideal customer profile (first paying team)

A **10–100 developer product company or agency, Windows-heavy or mixed-OS, already running agentic
CLIs** (Claude Code, Codex CLI, OpenCode), where an EM or staff engineer owns the "our review
queue is drowning and I don't trust what the agents merged" problem, and where compliance or
client contracts make "who wrote this code and was it tested" a real question. .NET shops,
fintech/insurance/healthcare ISVs, and government contractors over-index on every axis.

### 3.2 Personas (in adoption order)

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

### 3.3 Explicit non-targets (for now)

- Vibe coders / non-technical founders — Phase C, cloud product, per Market Research v2 §5.1.
- Teams all-in on cloud agents with no local development loop (Jules-only shops) — until the
  "attach to external agent PRs" intake ships.
- OSS maintainers wanting a free forever everything — we serve them a great free Git client, but
  they are not the revenue plan (Kite's law: individual developers do not pay).

---

## 4. Market numbers (for the deck and the pitch)

All figures verified 2026-07-06; keep sources handy for diligence.

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
  - AI-assisted teams merge ~2× more PRs while **PR review time rose 91%** (see Viability doc §1.3).
- **Windows:** the largest developer OS — 59.2% personal / ~48% professional use (Stack Overflow),
  while the entire polished-devtool wave (Conductor, Raycast, Warp, Zed) shipped Mac-first.
- **Compliance timing (state honestly):** EU AI Act GPAI enforcement powers activate Aug 2, 2026
  and Article 50 transparency applies — but the May 2026 "Digital Omnibus" provisionally
  **postponed high-risk obligations to Dec 2027**, and whether Article 50 "text" covers source
  code is unsettled. Pitch audit trails as *enterprise trust + where procurement is heading*
  (auditors are already asking — Codacy, AI-BOM movement), not as a deadline scare.

---

## 5. Competitive landscape — July 2026 snapshot

Full detail lives in the research corpus; this is the decision-grade summary.

### 5.1 The field at a glance

| Class | Who | State (July 2026) | Their gap we exploit |
|---|---|---|---|
| Git clients + AI | **GitKraken** (Desktop 12 Agent Mode, Apr 2026; **Kepler** ADE, free preview June 2026), GitButler ($17M a16z Series A, agents-in-virtual-branches), Tower/Fork (AI commit messages only), Sublime Merge (dormant) | GitKraken is the aggressive incumbent; owns distribution | Electron heft; **host-level worktrees only — no sandbox**; no verification/merge pipeline; PE pricing complexity |
| Orchestrator GUIs | **Conductor** ($22M A, Mac-only, free), Superset (YC S26, 12k★), Emdash (YC W26, cross-platform), Nimbalyst (ex-Crystal), Sculptor (Imbue, containers + early output-verification), Omnara ($9/mo, mobile) | Exploded H2 2025, consolidated violently Q1–Q2 2026; **Bloop/Vibe Kanban dead Apr 2026, Terragon dead Feb 2026** | **No one verifies output** (Sculptor beta excepted), no merge queues, no audit, mostly Mac-first, **monetization unsolved at ~$0** |
| First-party absorption | **Claude Code Desktop** (Apr 2026 redesign: worktrees/session, parallel sidebar, autoVerify, diff review; Windows GA Feb 2026), **Codex app** (Mac Feb, Win Mar 2026), **Cursor 3** Agents Window (+ Graphite acquired Dec 2025 → **Origin forge, fall 2026**), GitHub **Agent HQ** + enterprise agent control plane | The core "agents in worktrees + GUI" is now free table stakes from every vendor | **Single-vendor each**; model-self-review not deterministic test gates; no local merge orchestration; no per-change provenance; cloud/GitHub lock-in (Agent HQ) |
| Review / merge-queue layer | **CodeRabbit** ($550M val, 13M PRs, noisy), **Greptile** ($25M Benchmark), Graphite→Cursor, Mergify, Trunk, Baz ($17M, "review the plan"), Qodo, Macroscope ($40M) | Cloud GitHub-apps first; CLIs are thin clients to cloud inference | **Nobody does local verification cockpits, per-hunk agent provenance, hunk risk-ranking, or AI re-review in the merge queue** — queues re-run CI only |
| Audit/provenance | git-ai (OSS line-level provenance, Thoughtworks Radar "Assess"), GitClear, vendor enterprise logs (Anthropic Compliance API, Codex admin logs), sigstore/gitsign | Demand being articulated; tooling immature; Meta's internal RADAR proves the pattern works (1/3 revert rate) | **No commercial product ties provenance to review + merge**; C2PA-for-code doesn't exist |

### 5.2 The white space (verified — no shipped product combines these)

1. **Local desktop verification cockpit** — run builds/tests locally as merge gates, not LLM
   comments from a cloud app.
2. **Per-hunk agent provenance** — "agent X, model Y, session Z, approved plan P wrote these
   lines," surfaced in review and blame. Nobody ships this; auditors have started asking for it.
3. **Hunk-level risk ranking** — exists only inside Meta (Diff Risk Score); Greptile has
   per-comment confidence, Baz highlights risky changes; no one orders the review itself.
4. **Merge queue that re-verifies** — every queue on the market re-runs CI; none re-runs
   verification (tests *or* AI review) on the post-rebase state of agent branches.
5. **Cross-vendor, Windows-native.** Every first-party GUI is single-vendor; the category leader
   is Mac-only; GitHub's cross-vendor play is cloud-locked.

Mainguard's locked architecture already contains all five. That is the product story.

### 5.3 How we beat each class

- **vs GitKraken (the incumbent):** don't fight the Git-GUI knife fight on features — fight on
  *native performance* (their #1 complaint is Electron slowness; they maintain an official
  performance-troubleshooting page), *free-tier generosity* (their free tier blocks private repos
  and requires an account; ours doesn't), and *depth* (Agent Mode/Kepler launch agents but execute
  them unsandboxed on the host with no verification or merge pipeline). Expect Kepler to add
  features fast — our defense is the compound pipeline, not any single checkbox.
- **vs Conductor/Superset/Emdash (the orchestrators):** concede orchestration parity quickly,
  then differentiate where they structurally can't follow without rebuilding a Git client:
  verification gates, merge queue, undo journal, partial staging of agent output, interactive
  rebase to curate agent WIP. Also: Windows. Also: they're free and unmonetized — we don't need to
  beat them on price, we need to be worth paying for where they aren't.
- **vs first-party (Claude/Codex/Cursor):** be Switzerland. Their GUIs manage only their own
  agents; teams already mix vendors per task ("agentmaxxing" is a documented manual practice).
  Vendor-neutral + local + deterministic gates is the ground they won't take — each vendor is
  incentivized to lock in, and none will make its GUI a better home for a rival's agent. Watch
  **Cursor Origin** (fall 2026) most closely: it's the only announced product aiming at
  "agent-scale review + merge queue," but it's a *cloud forge* — our local-first counter-position
  is clean.
- **vs the review layer (CodeRabbit et al.):** their weakness is noise (audited: ~35% of
  CodeRabbit comments genuinely useful) and cloud-only posture. Deterministic "your test suite
  passed/failed in the agent's sandbox" is a fact, not an opinion — market it as the antidote to
  AI-review fatigue. Longer-term, integrate one of them as an optional signal rather than compete
  head-on.
- **vs "free" everywhere:** the failure ledger is the moat map. Bloop died with 27k GitHub stars
  and thousands of DAU because free users don't convert on orchestration. We charge for what
  teams demonstrably pay for in adjacent markets: review throughput (CodeRabbit $24–48/dev/mo,
  Graphite ~$40), merge reliability (Mergify $8+), and governance (enterprise premiums).

### 5.4 Failure lessons we've priced in

- **Bloop/Vibe Kanban (dead Apr 2026):** thin orchestration + free users = no business. → We lead
  with a paid-for job (verification/governance), and the free tier is a *Git client*, which has
  independent daily-driver value.
- **Terragon (dead Feb 2026):** cloud agent-running gets absorbed by the platform vendors
  (its shutdown notice literally pointed users to Claude Code Web). → We don't compete on running
  agents in *our* cloud; local-first, their subscriptions.
- **Kite (2022, canonical):** individual developers don't pay. → Revenue plan is teams/enterprise;
  individuals are the funnel.
- **Warp:** login walls + closed source + telemetry = years of trust tax. → No-login free tier,
  FSL daemon, published security architecture from day one.
- **Omnara's pivot (Feb 2026):** wrapping Claude Code's UI is unmaintainable against its release
  pace. → Integrate at the CLI/process boundary (PTY + git), never by wrapping vendor UIs.

---

## 6. Customer discovery & design partners

Do this *before* and *during* launch — it is the highest-information work per hour spent.

### 6.1 Discovery interviews (July–September, target 25)

- **Sources:** r/ClaudeAI and r/ChatGPTCoding power users (DM people posting multi-agent
  workflows), X build-in-public followers, .NET/C# Discords, local dev meetups, the team's own
  networks, and — after the August visit — Daemon's engineers if offered.
- **Script spine (ask about the past, not the future):** How many agent sessions do you run in
  parallel today? Walk me through the last time an agent broke something or two agents collided.
  How do you review agent output — what do you actually read? Have you merged agent code you
  didn't fully review? What happened? Who in your org asks "did AI write this"? What do you pay
  for today (Cursor/Claude/Copilot/review tools)?
- **Disqualifying signal to respect:** if interviewees consistently say the first-party desktop
  apps are "good enough" for review, the review-cockpit wedge needs sharpening before launch.

### 6.2 Design partner program (the a16z/First Round consensus shape)

- **Two tracks:**
  - **Track A — 5–8 individuals** on the free Git GUI (polish, retention, Windows edge cases).
  - **Track B — 3–5 teams** for the verification/merge pipeline. These become the first paying
    logos and the seed-deck proof points. Build a ~40-candidate list to land 4.
- **Qualify on:** *urgency* (already duct-taping worktrees + tmux), *capacity* (a champion who'll
  do bi-weekly calls), *representativeness* (one .NET enterprise shop, one AI-forward startup,
  one agency).
- **The deal:** early access + roadmap influence + free year / deep discount, in exchange for real
  usage on real repos, bi-weekly feedback, and logo/case-study rights at GA. **Time-box to 6
  months** with success criteria, or it becomes unpaid consulting.
- **Daemon angle:** the single best outcome of the August meeting is Daemon's team as a Track B
  design partner (see companion doc) — an AI startup running agents all day is the exact ICP.

---

## 7. Launch plan

### 7.1 Phase 0 — Pre-launch (now → ~September)

1. **Build in public on X (Linear playbook).** The product is inherently screenshot-able (graph
   canvas, five themes, 3-pane resolver). Post short clips 2–3×/week: commit-graph rendering,
   partial staging, conflict resolver, then sandbox/verification previews as they land.
   Windows-native polish is itself novel content — the "beautiful devtool" genre is all macOS.
2. **Waitlist + cohort invites.** Landing page with direct waitlist; invite in cohorts of 10–20
   (Linear model), fix what breaks, invite the next cohort.
3. **Write 3 technical essays** (each is HN/Pointer/newsletter fodder and pre-answers launch-day
   objections): (a) *"The .git/index.lock problem: why agents corrupt your repo and how
   deterministic handle management fixes it"*; (b) *"Sandboxing coding agents on Windows/WSL2 with
   default-deny egress"*; (c) *"Test-verified agent PRs: making the merge queue re-verify what
   goes stale."*
4. **Open the Discord** at beta start; seed 20–50 core members (design partners + waitlist
   enthusiasts); it doubles as the changelog feed. Don't over-build channels.
5. **Trust assets shipped before launch:** security architecture doc, telemetry policy (opt-in,
   published schema), FSL licensing statement, "network transparency" view on the roadmap.
6. **August: the advisor meeting** (companion doc). Also use the trip's deadline as the forcing
   function for a rehearsed 10-minute demo — the same demo is the launch video.

### 7.2 Phase 1 — Launch act one: the free Git client (target ~October)

- **Show HN**, plain title: *"Show HN: Mainguard – a fast, native Git GUI for Windows (free, no
  login)"*. Direct download link, no signup wall. Founder in comments within the hour, all day,
  technical and non-defensive. Tue–Thu, 9am–12pm ET. Pre-empt the known objections (why another
  client; why FSL not MIT; why .NET/Avalonia; Electron comparison benchmarks ready).
- **Same week:** Product Hunt (badge + backlink, ~10% of energy), **Console.dev submission**
  (free, editorial, we meet every criterion: developer-primary, self-service download), founder-
  disclosed posts in r/git, r/csharp, r/dotnet, r/SideProject.
- **Goal:** installs, retention curve, and a believable "weekly active repos" number — not revenue.

### 7.3 Phase 2 — Launch act two: the agent control plane (4–8 weeks later)

- **Second Show HN / Launch Week:** *"Show HN: Run coding agents in sandboxes that must pass your
  tests before you review their code"*. This is the story for r/ClaudeAI, TLDR AI, and creator
  outreach (ThePrimeagen / Theo / Fireship: founder-to-creator email, 60-second demo clip, the
  hook is "agents that must pass tests before you see the PR"). Organic first; consider one
  $2–5K mid-tier sponsorship only after the organic message proves out.
- **Position explicitly as "Conductor for Windows, with verification."**
- Ship Pro tier ($20/mo) at or shortly after this act; design partners convert to paid logos.

### 7.4 Phase 3 — Teams & governance (2027, post-PMF signal)

- Land-and-expand from Phase-2 seats; the audit dashboard + merge-queue analytics are the
  expansion product. Sell $50+/seat only once RBAC/audit/SIEM exist (Market Research v2 rule).
- Cloud worktrees private beta (the scale + usage-revenue story) within ~2 quarters of desktop GA.

### 7.5 Channel priorities (ranked by expected yield)

1. Hacker News (two acts + essays) — the GitButler/Graphite/Supabase evidence is unambiguous.
2. Build-in-public X + waitlist cohorts.
3. r/ClaudeAI, r/ChatGPTCoding, r/git, r/csharp (90/10 rule, founder-disclosed).
4. Console.dev (free, high-intent) → Pointer/TLDR (paid, later, ~$3.5K+ per placement).
5. YouTube creators (organic outreach at act two).
6. Discord community (retention, not acquisition).

---

## 8. Pricing & monetization

Unchanged in structure from Market Research v2 §5.3; sharpened with July 2026 benchmarks:

| Tier | Price | What it buys | Benchmark logic |
|---|---|---|---|
| **Free** | $0, no login | Full Git GUI + **one** sandboxed agent | Beats GitKraken's account-walled, private-repo-blocked free tier; the funnel |
| **Pro** | **$20/mo** or $199/yr **with perpetual fallback** | Unlimited local agents, verification pipeline, review cockpit, AI gateway (rate-limit protection), BYOK | $20 = the established individual AI-tool price (Cursor Pro, Claude Pro, Copilot Pro+ band); BYO-subscription like Conductor so no inference margin death; JetBrains-style fallback is a cheap loyalty signal to subscription-fatigued Windows devs |
| **Team/Enterprise** | **$50+/seat** | Merge queue + re-verification analytics, per-hunk provenance, audit trail/SIEM, RBAC/SSO, budget caps | Sits credibly above CodeRabbit Pro ($24–48) and Graphite (~$40) because it bundles review + queue + governance; compliance features are what enterprises pay premiums for |
| **Cloud worktrees** | usage-based | Hosted execution pods | The revenue lever BYOK forfeits locally; 2027 |

**Funnel math to keep us honest:** devtool freemium converts 1–3%. At 2% × $20/mo, ~10,000 active
free users ≈ $50K ARR — real money lives in team seats (Warp's B2B2C pattern). Instrument
**weekly active repos** and **agent runs verified/merged per week** from day one; these are the
metrics investors can't get from download counts.

---

## 9. Metrics & milestones

### 9.1 Product KPIs (extends Market Research v2 §4.3)

1. Weekly active repos (free tier health).
2. Agent runs verified per week; % of merges executed against non-stale verification.
3. Plan-approval adoption rate; branch acceptance vs rejection ratio.
4. Free→Pro conversion (target ≥2%); logo count and seat expansion in Track B teams.
5. Token spend per merged branch (feeds the enterprise budget story).

### 9.2 The seed bar (if/when we raise — 2026 benchmarks)

- Conventional: ~5,000+ MAU **or** 500 paying customers **or** ~$300–500K ARR; burn multiple <2×.
- AI-infra reality: growth rate (15–20%+ MoM, organic) and logo quality outweigh absolutes —
  GitButler raised on credibility + HN demand pre-revenue; Conductor's A rested on logos
  (Vercel, Notion, Ramp), not seats.
- **Our fundable story at the low end:** 3–5 named design-partner teams actively using the merge
  queue + a strong retention curve on the free GUI + weekly-verified-merges growth. Roughly
  $10–50K MRR with that shape is pitchable; position as **agent infrastructure**, never "Git
  client" (median seed valuations concentrate in AI-positioned companies).

---

## 10. Pitch materials

### 10.1 Deck outline (Sequoia skeleton — "Why Now" is our strongest card)

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
6. **Competition** — the 5.1 table collapsed to one slide: first-party = single-vendor,
   review layer = cloud-only opinions, orchestrators = dead or free; we own the intersection.
7. **Product** — live demo or 90-second video. The demo *is* the pitch (DHH/Docker rule).
8. **Business model** — free GUI funnel → $20 Pro → $50+ team governance; BYOK = no inference
   margin risk; benchmarked against CodeRabbit/Graphite/Cursor price points.
9. **Traction** — waitlist, retention curve, design partners, weekly verified merges.
10. **Team** — why us: shipped a real Git engine (the hard prerequisite every wrapper lacks);
    Windows/.NET depth in a Mac-first field.
11. **Ask** — tailored per audience.

### 10.2 Demo script (10 minutes, rehearse for August)

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

*(Anything not yet built runs scripted/mocked in August — labeled honestly as "landing in vN" —
per the current Implementation Plan sequencing. Do not fake it silently for an experienced
founder; he will probe.)*

### 10.3 Failure modes to avoid (technical-founder classics)

Too deep on architecture, no pain narrative; no specific customer; burying the demo behind
slides; "50 agents" scale claims the hardware can't cash (Market Research v2 killed this);
pitching the tech instead of the switching costs + workflow depth (what 2026 investors ask of
AI startups explicitly).

---

## 11. The August advisor meeting

Full playbook in `Advisor_Pitch_August_2026.md`. The strategy in three lines: **ask for advice,
not money** ("ask for advice, get money — twice"); make it a scheduled working session with a
demo and two named decisions, not a dinner ambush; the best concrete outcome is Daemon's team as
a design partner, and the relationship compounds from acting on his advice and reporting back.
Formalize an advisor role (FAST agreement, 0.25–1%, 2-year vest, 3-month cliff) only after the
advice loop has run at least once.

---

## 12. Risks — stated honestly

1. **Platform absorption (highest).** Claude Code Desktop already ships worktrees + autoVerify +
   diff review; Cursor Origin (fall 2026) aims at agent-scale review + merge queues; GitKraken
   Kepler is free during preview. **Mitigation:** vendor-neutrality, local-first, and the
   compound pipeline (any single feature has a ~2-quarter exclusivity window; the combination
   plus a real Git engine does not). **Tripwire:** if Origin ships local execution + provenance,
   re-plan within a quarter.
2. **Monetization ceiling.** Orchestration is worth $0; we believe verification + governance is
   worth $20–50. The design-partner program exists to prove willingness-to-pay *before* we build
   the full enterprise layer. **Tripwire:** if Track B teams won't pre-commit to paid pilots by
   two months post-act-two, revisit pricing/packaging.
3. **Execution capacity.** One founder + a forming 5–6 person team, against funded incumbents.
   The M1–M3 Git core remains the prerequisite for every differentiator (Viability doc §2) —
   protect that sequencing from launch-marketing pressure.
4. **BYOK/ToS fragility.** Anthropic's 2026 OAuth enforcement showed vendors will constrain
   harnesses. API-key path primary, local-model support as the pressure valve, adapter layer
   vendor-neutral (unchanged from Market Research v2 §5.4).
5. **Timing.** The August demo date is fixed; scope the demo to what's real (script the rest,
   labeled). A polished honest demo beats a broad fragile one — especially for this audience.

---

## 13. 90-day action checklist (July 6 → early October)

- [ ] Lock the demo scope for August; rehearse the §10.2 script end-to-end twice.
- [ ] Write the one-page pre-read for the advisor meeting (in companion doc) and send it ahead.
- [ ] Research Daemon properly once the founder's name is known (couldn't be identified from
      public sources as of 2026-07-06 — likely stealth; candidates logged in research).
- [ ] Landing page + waitlist + X account; first build-in-public clip this week.
- [ ] Draft essay (a) on the index.lock problem; publish ~2 weeks before act-one launch.
- [ ] Build the 40-candidate design-partner list; start 25 discovery interviews.
- [ ] Ship trust assets: security architecture doc v1, telemetry policy, FSL statement.
- [ ] Instrument weekly-active-repos + verified-merges metrics before any launch.
- [ ] Show HN act one (free Git GUI) when retention on beta cohorts looks healthy.
- [ ] Submit to Console.dev; PH same week; subreddit posts (founder-disclosed).
- [ ] Post-meeting: send follow-up + act on one piece of advice + report back within 4 weeks.

---

## Appendix: key research sources

Competitive and market claims in this document were verified 2026-07-06 via primary sources where
possible. Highest-value references:

- Conductor Series A: conductor.build/blog/series-a · Vibe Kanban shutdown: vibekanban.com/blog/shutdown
- Terragon shutdown: docs.terragonlabs.com/docs/resources/shutdown
- GitKraken Agent Mode/Kepler: gitkraken.com/blog (Apr/June 2026) · GitButler Series A: blog.gitbutler.com/series-a
- Claude Code Desktop & worktrees: code.claude.com/docs/en/desktop, /worktrees · Codex app: openai.com/index/introducing-the-codex-app
- Cursor Origin: Cursor announcement coverage, June 17 2026 · Graphite acquisition: cursor.com/blog/graphite
- CodeRabbit funding/traction: TechCrunch Sept 16 2025 · Greptile Series A: greptile.com/blog/series-a
- Meta RADAR: engineering.fb.com (Aug 2025), arXiv 2605.30208 · git-ai: github.com/git-ai-project/git-ai
- Stack Overflow 2025 AI survey: survey.stackoverflow.co/2025/ai · DORA 2025: cloud.google.com/resources/content/2025-dora-ai-assisted-software-development-report
- Octoverse 2025: github.blog · SlashData developer population: slashdata.co
- EU AI Act timeline + Digital Omnibus: artificialintelligenceact.eu, Gibson Dunn / Covington May 2026 analyses
- GTM evidence: graphite.dev/blog/launch · supabase.com/blog/supabase-how-we-launch · warp.dev/blog/lifting-login-requirement · markepear.dev HN launch guide · console.dev/selection-criteria
- Advisor norms: fi.co/fast (FAST v3, June 2026) · startups.com "How to Recruit a Rockstar Advisor"
