# Mainguard Narrative — Market, Positioning & Launch

**The deepened market and launch layer: who we're against, what we're for, what it costs, and how the story gets told.**

Register: **brand** (external marketing) — the pass PRODUCT.md scopes out of in-app copy. Brand register is one degree warmer than product register; the personality does not change: **premium & precise**. Every string here has passed the Voice Bible's five-question gate (Appendix A of [`Mainguard_Voice_And_Delight_Bible.md`](Mainguard_Voice_And_Delight_Bible.md)); voice rules are cited as `V-#`, naming as `N-#`.

**Relationship to LaunchReserve.** LaunchReserve was the first-generation reserve of launch copy; as of the 2026-07-12 consolidation it is **archived in full** at [`docs/archive/LaunchReserve.md`](../archive/LaunchReserve.md). This document is the strategy layer above it and, where copy overlapped — the Show HN post, the founder story, the README hero — **this file's versions win** (they are re-derived against the two-act launch plan in `docs/business/go-to-market/Mainguard_GTM_Plan_2026-07.md` §7). Its two still-authoritative sections were folded verbatim into the [Voice & Delight Bible](Mainguard_Voice_And_Delight_Bible.md): the release-notes voice guide (was §6) is now **Appendix D**, agent naming (was §4) is now **Appendix C**; its comparison tables (§3) are superseded by §2 below.

**Evidence standard.** Every competitor claim below is sourced from `docs/business/market-analysis/` and `docs/business/go-to-market/`, cited inline by document and section. Where those docs mark a claim **unverified**, that caveat is carried here — never silently dropped. Nothing is invented for rhetorical convenience; the story is only as strong as its honesty (V-6).

---

## 0. The honesty contract (unchanged, load-bearing)

Carried verbatim in force from LaunchReserve; it governs every sentence in this document.

1. **Shipped vs. [Horizon].** Mainguard ships one thing today: a working, natively-rendered single-user Git client (T-01…T-33, 1,042 tests — MergeLoom Deep Dive §5). The multi-agent control plane is roadmap (`phase2`), not code. Present-tense claims are on `main`; roadmap claims are marked **[Horizon]** or "not built yet." Never quote a [Horizon] line as shipped.
2. **"Audit-grade / tamper-evident," never "legally required crypto."** EU AI Act Article 12 mandates event logging and traceability, not cryptographic immutability — and the May 2026 Digital Omnibus provisionally postponed high-risk obligations to Dec 2027 (Competitor Research probe (c); GTM Plan §4). The audit story is *enterprise trust and where procurement is heading*, never a deadline scare.
3. **Honest capacity.** The realistic near-term target is a developer supervising roughly **4–6 agents on a 16 GB laptop** — never "swarms of 50," which is both indefensible on consumer hardware and the vocabulary of the dead companies (GTM Plan §2.4, Master Market Document §4.1).

---

## 1. The market in three sentences

This is the spine every longer telling must reduce to:

> Agent CLIs made it trivial to produce ten branches an hour; review time is up 91% against a fixed human ceiling, and 87% of developers distrust what the agents wrote (Viability §1.3; GTM Plan §4). Every vendor sells generation; nobody sells trust — the five capabilities that would create it are verified empty across the entire field (GTM Plan §5.2). Mainguard is a real Git client today and, on top of it, the control plane where any agent's work becomes trustworthy commits on `main`.

The load-bearing facts, with sources kept ready for diligence:

| Fact | Figure | Source (via) |
|---|---|---|
| Review-time explosion | AI-assisted teams merge ~2× more PRs; PR review time **+91%** | Viability §1.3 |
| Distrust | **87%** concerned about agent accuracy; **81%** about security | Stack Overflow 2025, via GTM Plan §4 |
| Volume | Copilot's coding agent alone: **1M+ PRs in five months**; code churn +861% under high AI adoption | Octoverse 2025 / Faros AI, via GTM Plan §4 |
| Early innings | 84% use or plan AI tools, but only **31% run agents** today | Stack Overflow 2025, via GTM Plan §4 |
| The unserved OS | Windows: **59.2% personal / ~48% professional** developer use — while the polished-devtool wave shipped Mac-first | Stack Overflow, via GTM Plan §4 |
| Someone built it privately | Meta's internal RADAR (agent-code risk review) exists because it couldn't be bought; 1/3 revert rate caught | GTM Plan §5.1 |

---

## 2. The competitor teardown

The field has split into four layers, and the control layer is the last one without a winner (Master Market Document §3.1): AI-native IDEs → terminal agents → execution infrastructure → **orchestration & control (contested)**. Mainguard enters the contested layer from below — the only entrant whose foundation is a real Git client.

The teardown convention: for each competitor, **what they do well** (stated honestly — flattery-free respect is more persuasive than dismissal, V-6), **where they stop**, and **the sentence** we use when asked about them. The enemy is never the competitor (§3.1); the sentence positions, it doesn't sneer.

### 2.1 The classic Git clients

The mature, commoditized market Mainguard's free tier enters — deliberately not to win a knife fight over a fixed pie, but as the trust wedge and daily surface (Master Market Document §2.4).

#### GitKraken — the aggressive incumbent

- **What they do well.** Largest paid share of the Git-GUI market; shipped Agent Mode in Desktop 12 (April 2026) and the standalone Kepler ADE (June 2026) with multi-repo Tasks, issue-tracker intake, kanban session views, and PR-based task initiation — distributed to an existing paying base (Competitor Research §2).
- **Where they stop.** Electron heft — their #1 complaint, with an official performance-troubleshooting page (GTM Plan §5.3). Agents run **unsandboxed on the host**, worktree isolation only; Kepler's own marketing page, checked directly, mentions **nothing** about merge queues, verification runs, sandbox isolation, provenance, or audit (Competitor Research §2). Their free tier requires an account and blocks private repos (GTM Plan §5.3). Client depth stops above the line: partial staging, not line-level (Feature Inventory §11).
- **The sentence.** *GitKraken launches agents. Mainguard is built for what happens after they finish.*
- **The standing caution.** GitKraken is the competitor most likely to copy the roadmap; the defense is speed on the compliance-grade pieces (queue semantics, audit integrity) that are hardest to retrofit (Competitor Research §2), plus a compound pipeline no single checkbox replicates (GTM Plan §12.1).

#### Fork — the craft benchmark

- **What they do well.** Fast, genuinely native, loved, and **$59.99 one-time** — proof that individual developers will pay once for craft (Viability §1.5). Honest reading: against Fork, native rendering is *not* Mainguard's edge — Fork is native too (LaunchReserve §3, Table A).
- **Where they stop.** No AI features at all, by choice; no agent story of any kind; a quiet cadence (Master Market Document §3.2: "AI commit messages only" is Tower — Fork has none).
- **The sentence.** *Fork set the bar for what a paid-once native client owes its user. Mainguard's client honors that bar — and then answers the question Fork doesn't ask: what do you do when most of your diffs weren't written by you?*

#### Tower — the enterprise-safety niche

- **What they do well.** The enterprise niche, built on a safety promise adjacent to ours: undo any operation with Ctrl+Z, Custom Workflows, and PR management across GitHub, GitLab, Bitbucket, Azure DevOps, and Beanstalk — broader host coverage than Mainguard ships (Feature Inventory §11).
- **Where they stop.** Their AI ceiling is commit messages (Master Market Document §3.2). No orchestration, no verification, no agent governance; the safety promise ends at human-driven operations.
- **The sentence.** *Tower sells undo for what you did. Mainguard extends the same promise to what your agents did — journaled, attributable, reversible.*

#### Sublime Merge — the cautionary proof

- **What they do well.** The speed benchmark of the category; command palette; the `smerge` standalone mergetool mode Mainguard still lacks (Feature Inventory §11).
- **Where they stop.** **Dormant** (Master Market Document §2.4, §3.2). The fastest client in the market stopped moving — which is the lesson, not the opening.
- **The sentence.** *Sublime Merge proved that speed alone is necessary and not sufficient. We priced that lesson in: Mainguard never sells speed alone — the client is the wedge, verification is the business.*

### 2.2 Conductor and the orchestrator field

#### Conductor — the funded category leader

- **What they do well.** $22M Series A (Spark + Matrix, YC S24), ~6 people, weekly release cadence through July 2026 (v0.72.0); parallel Claude Code / Codex / Cursor agents in per-workspace worktrees; GitHub + Linear integration; free, riding the user's own agent subscriptions (Competitor Research §3).
- **Where they stop.** **macOS-only** — no Windows or Linux signals anywhere in the changelog. Worktree-only isolation; their "queue" is a task queue, not a verification queue; no audit, no provenance (Competitor Research §3). And free means unmonetized — the collaboration features that would charge are unshipped.
- **The sentence — the handed analogy.** *"Conductor for Windows — with verification."* Journalists and readers will reach for an analogy; hand them this one — it flatters twice, because the category leader is Mac-only and has no verification layer (GTM Plan §2.4).
- **The standing caution.** $22M can fund a Windows port any quarter. Own Windows/WSL2 first, and ship queue semantics they would have to re-architect to follow (Competitor Research §3).

#### The rest of the field, compressed

| Competitor | Their strength | Their stop | Source |
|---|---|---|---|
| **Superset** | Automations, TypeScript SDK, MCP surface; YC Spring 2026; Pro $20/seat | macOS-only (Windows untested, open issue); no sandbox, queue, audit, or provenance | Competitor Research §5 |
| **Nimbalyst** (ex-Crystal) | Free/MIT breadth (editors, diagrams, iOS companion) and a relentless comparison-page SEO machine | No sandbox, no queue, no audit; competes for individuals, not teams — copy their content playbook, not their features | Competitor Research §4 |
| **Parallel Code** | Free, MIT, solo-built, "dispatch + one-click merge" simplicity | Nothing downstream of generation; structurally fragile. Not a threat — a **parity floor**: Mainguard must feel this easy | Competitor Research §6 |
| **Vibe Kanban** | Once the community favorite | **Bloop shut down April 2026.** Founder: "the vast majority are free users and we couldn't find a business model that we could get excited about" | Competitor Research §7 |
| **Composio AO** | The furthest into autonomous PR lifecycle: auto-fixes CI, resolves conflicts, answers review comments | The opposite philosophy — it removes the human. *AO merges when CI is green; Mainguard proves it's still green after everyone else merged* | Competitor Research §8 |
| **Sculptor** (Imbue) | The closest sandbox thesis: real Docker container per agent; Pairing Mode is genuinely elegant and worth studying | Network posture unstated — no default-deny egress claims anywhere; Claude-centric; Windows via WSL, not native. Outflank on the dimension they leave unspecified | Competitor Research §10 |
| **Factory.ai** | $1.5B valuation, enterprise logos, benchmark halo | Cloud-first, vendor-owned. Don't compete on execution — become the governed intake point for Droid-style output | Competitor Research §9 |

#### First-party gravity — GitHub, Anthropic, OpenAI, Cursor, Google

The core "agents in worktrees + a GUI" is now free table stakes from every platform vendor: the Copilot app (GA June 17, 2026 — worktree sessions, single-PR Agent Merge, cloud sandboxes, usage-metered AI Credits), Claude Code Desktop (worktrees, autoVerify, diff review), the Codex app (Windows since March 2026), Cursor 3's Agents Window and the announced **Origin** cloud forge, Jules mass-producing PRs with an API and a GitHub Action (Competitor Research §1, "Also checked"; GTM Plan §5.1).

- **Where they all stop, structurally.** Each is single-vendor. Each reviews with its own model, not deterministic local test gates. None models cross-branch staleness. None will ever make its GUI a better home for a rival's agents — each is incentivized to lock in (GTM Plan §5.3).
- **The sentence.** *Be Switzerland. Their agents are welcome here; their lock-in isn't.* Jules and Codex aren't competitors to beat — they're the PR firehose Mainguard's vendor-neutral intake is built to drink from (Competitor Research, recommendation 2).
- **The tripwire.** Watch Cursor Origin (fall 2026) most closely — the only announced product aiming at agent-scale review + merge queue. It is a *cloud forge*; the local-first counter-position is clean. If Origin ships local execution + provenance, re-plan within a quarter (GTM Plan §12.1).

#### MergeLoom — the mirror image

The most instructive competitor, because it occupies Mainguard's exact "governed AI delivery" language while being Mainguard's structural opposite (MergeLoom Deep Dive, in full):

- **What they do well.** Live and billing today: ticket-triggered runs, a six-gate validation runway with a bounded repair loop, an AI review agent, Diff Guard scope policing, outcome pricing (£2–4 per opened PR/MR), self-hosted worker in the customer's VPC. Six to twelve months ahead of Mainguard on the governance-pipeline *story* (Deep Dive §0, §2).
- **Where they stop.** **No client at all** — no desktop app, no review UI, no terminal, no interactive steering; every human touchpoint is rented from the tracker and the code host. No merge coordination: no queue, no stale re-verification; epic slices collide at PR time. No sandbox or egress hardening claims for the thing that executes AI-written code. Audit without integrity: no hash chain, no SIEM, no certifications. A one-person company (LinkedIn: 1 employee, 25 followers) selling governance, with an SEO wall of 161 blog posts stamped on a single day (Deep Dive §6 — all [V]).
- **The sentence — the named failure mode.** *They stop at "PR opened." A branch validated an hour ago is stale the moment main moves, and they never re-verify. **Validated-then-stale is unvalidated** — it's intrinsic to their architecture, and it's the exact thing Mainguard's spine fixes* (Deep Dive §5.3).
- **The naming caveat, stated plainly.** A governance-positioned "-Loom" competitor makes the Mainguard name materially riskier than it was; the naming decision (Master Market Document Part VI) treats this collision as a forcing function. This document uses "Mainguard" throughout and every positioning line here survives a rename — none of the copy puns on the name.

### 2.3 The failure ledger — lessons already priced in

The corpse pile is the moat map (GTM Plan §5.4). Each failure produced a standing rule:

| Failure | What died | The rule it bought |
|---|---|---|
| **Bloop / Vibe Kanban** (Apr 2026) | Thin free orchestration, 27k stars, thousands of DAU — no business | Never sell orchestration. The free tier is a *Git client* with independent daily-driver value |
| **Terragon** (Feb 2026) | Cloud agent-running; its shutdown notice pointed users to the platform vendor | Don't run agents in our cloud. Local-first; their subscriptions |
| **Kite** (2022, canonical) | Individual-developer monetization | Revenue is teams and enterprises; individuals are the funnel |
| **Warp** (trust tax, not death) | Years of goodwill lost to login walls, closed source, telemetry | No-login free tier, source-available daemon (FSL), published security architecture — from day one |
| **Omnara's pivot** (Feb 2026) | Wrapping a vendor's UI against its release pace | Integrate at the CLI/process boundary (PTY + git), never by wrapping vendor UIs |

### 2.4 The five empty squares

Verified across the entire field — no shipped product combines these (GTM Plan §5.2; Competitor Research, "Where the field is empty"):

1. **A local desktop verification cockpit** — builds and tests run locally as merge gates, not LLM opinions from a cloud app.
2. **Per-hunk agent provenance in review** — "agent X, model Y, session Z, under approved plan P wrote these lines," in the diff and blame gutters. The Agent Trace RFC has emitters coming and **no consumer/renderer exists** (Competitor Research probe (b)).
3. **Hunk-level risk ranking** — ordering the review itself by blast radius. Exists only inside Meta (Competitor Research; GTM Plan §5.2).
4. **A merge queue that re-verifies** — every queue on the market re-runs CI; none re-runs verification on the post-rebase state of agent branches (probe (a)).
5. **Cross-vendor, Windows-native** — every first-party GUI is single-vendor; the category leader is Mac-only; GitHub's cross-vendor play is cloud-locked.

Mainguard's locked architecture already contains all five. That is the product story — and every one of the five is **[Horizon]** until shipped.

---

## 3. Positioning and the enemy

### 3.1 The enemy is the blind merge

The enemy is never a named competitor, and never "AI" (Master Market Document §4.3). Both would be false: the competitors are mostly good tools with different theses, and the agents are the reason the product needs to exist. The enemy is a *practice*:

> **The blind merge** — code nobody fully read, verified against a main that has since moved, written by a process nobody can attribute, merged because the diff was too long and the queue was too deep. **Hope is not a merge strategy.**

This enemy framing does three jobs at once. It is *universal* (every reader has committed a blind merge; no reader is insulted). It is *precise* (it names a behavior, not a vibe — V-1 applied to marketing). And it is *ours structurally* — every differentiator is a specific answer to a specific way the blind merge happens:

| How the blind merge happens | The answer [Horizon] |
|---|---|
| The diff was too long to really read | Risk-ranked review — blast radius first, not file-alphabetical (D-2) |
| "It passed tests" — an hour ago, against an older main | The re-verifying merge queue; *validated-then-stale is unvalidated* (D-1) |
| Nobody knows which agent wrote which line | Per-hunk provenance on the Agent Trace standard (D-2) |
| The agent could have touched anything while it worked | Hardened sandbox, default-deny egress (D-4) |
| No record of who approved what | Audit-grade, tamper-evident trail with identity (D-3) |

### 3.2 The three positioning registers (locked)

Locked in the Master Market Document §4.1; reproduced here because every launch asset derives from one of them:

- **Market-facing sentence:** *Every vendor now sells you agents that produce branches; GitHub will even merge its own. Mainguard is the neutral control plane that verifies, attributes, and audits what **any** agent produced — locally, on Windows, behind a default-deny wall — before it touches main.*
- **Product paragraph:** *Agent CLIs made it trivial to produce ten branches an hour; nothing on the market makes it safe to merge them. Mainguard is the Git-native control plane for the agent era: a premium Git client underneath, and on top — sandboxed local execution, a merge queue that re-verifies anything that goes stale, a review cockpit that ranks agent diffs by risk and provenance, and a tamper-evident audit trail. Run your agents wherever you like — Mainguard is where their work becomes trustworthy commits on main.*
- **Engineering-manager framing:** *Mainguard is the engineering manager for your AI agents. Several agents in parallel, each in its own sandboxed worktree, with plans you approve before code is written, tests that run before you review, and merges that never happen without you.*

### 3.3 The named failure modes (framing shortcuts)

Precision applied to persuasion: each shortcut names an exact failure the way V-1 names an exact file. Use them deliberately, verbatim, until the market repeats them back:

- **"Hope is not a merge strategy."** The enemy line; the spine of every talk and essay.
- **"Validated-then-stale is unvalidated."** Positions against MergeLoom and every CI-bound queue at once.
- **"Your agents' work, test-verified before you see it."** The single feature no shipped competitor has (Sculptor gestures at it) — GTM Plan §2.4.
- **"`.git/index.lock` roulette."** The founding footgun, named; the bridge from the shipped client to the agent thesis.
- **"Conductor for Windows — with verification."** The handed analogy (§2.2).
- **"A fact, not an opinion."** Deterministic test verdicts vs. AI-review comments (~35% of CodeRabbit comments audited as genuinely useful — GTM Plan §5.3). The antidote to AI-review fatigue.

### 3.4 What we never say

The negative space is part of the register (V-2, V-6):

- Never **"swarm," "50 agents," "orchestration"** as the pitch — commoditized, hardware-dishonest, and the vocabulary of the dead companies (GTM Plan §2.4).
- Never **"EU AI Act requires..."** — it doesn't require crypto, and the Omnibus moved the dates. "Audit-grade," "tamper-evident," "what procurement is asking for" (honesty contract §0.2).
- Never **a named competitor as the villain** — teardown in analysis, respect in public. The sentence formats in §2 are as sharp as public comparison gets.
- Never **certainty the tool lacks**: "verified in its sandbox — not yet reviewed by you" is the register even in marketing (V-6).
- Never **exclamation marks, "blazing," "insanely," "game-changing."** Severity and importance ride the facts, not the volume (V-2 in brand register).

---

## 4. Pricing logic

### 4.1 The principle: charge for trust, not orchestration

The market has already priced orchestration at zero — twice, fatally (Bloop, Terragon; §2.3). It has also demonstrated what teams *do* pay for in adjacent markets: review throughput (CodeRabbit $24–48/dev/mo), merge reliability (Mergify $8+), stacked-review workflow (Graphite ~$40), and governance premiums above all of them (GTM Plan §8). Mainguard's pricing therefore never charges for spawning agents; it charges for the pipeline that makes their output mergeable.

### 4.2 The tiers, and why each number is what it is

Structure locked (Master Market Document §8.1; GTM Plan §8):

| Tier | Price | What it buys | Why this number |
|---|---|---|---|
| **Free** | $0, no login, ever | The full Git client + one sandboxed agent [Horizon] | The funnel must be *genuinely excellent free*. The asymmetry is deliberate: GitKraken's free tier is account-walled and blocks private repos (GTM Plan §5.3); ours has no wall to hit. Warp's trust tax (§2.3) is why "no login" is a feature, not an absence |
| **Pro** | $20/mo, or $199/yr with perpetual fallback | Unlimited local agents, verification pipeline, review cockpit, AI gateway, BYOK [Horizon] | $20 is the established individual AI-tool price (Cursor Pro, Claude Pro, Copilot Pro+ band) — no anchoring fight. BYOK means no inference-margin death (Conductor's model, kept). The JetBrains-style fallback is a loyalty signal to subscription-fatigued Windows developers — with the honest caveat that fallback builds keep current agent-CLI adapters via a separately versioned channel, or the fallback's value decays in weeks (Master Market Document §8.1) |
| **Team / Enterprise** | $50+/seat | Merge-queue analytics, per-hunk provenance, audit/SIEM, RBAC/SSO/SCIM, budget caps [Horizon] | Sits credibly above CodeRabbit Pro ($24–48) and Graphite (~$40) because it bundles what they each sell a slice of: review + queue + governance. Standing rule: **do not sell this tier before the governance features exist** (GTM Plan §3.2 — "do not sell to Priya") |
| **Cloud worktrees** | usage-based, 2027 | Hosted execution pods | The usage-revenue lever BYOK deliberately forfeits locally; solves the honest 4–6-agent hardware ceiling (Master Market Document §8.2) |

### 4.3 The structural counter-position: no meter on your own hardware

MergeLoom's whole model is a £2–£4 platform tax per opened PR, plus AI cost (Deep Dive §2). Mainguard's BYOK local runs cost tokens only. Any team producing more than ~50 PRs a month is better off on a flat license — and recurring "fleet" agents running overnight on the developer's own hardware cost approximately nothing marginal (Deep Dive §5.7, G4). Publish the cost calculator; the line is:

> **No per-PR meter on your own hardware.** Your agents, your keys, your machine — Mainguard charges for the pipeline, not per unit of your own work.

The same logic counters Copilot's usage-based AI Credits (June 2026): metering *bills* the problem of runaway agent spend; a budget gateway *prevents* it (Competitor Research §1 and "Where the field is empty" #6).

### 4.4 How pricing sounds (on-voice)

Three lines, one per tier, each passing the five-question gate — concrete object, no filler, no hype:

- **Free:** `The Git client is free. No account, no private-repo wall, and nothing leaves your machine.`
- **Pro:** `$20 a month. Your agents run in local sandboxes, pass your tests before you review, and your keys stay in your keyring.`
- **Team:** `Everything in Pro, plus the record: who approved which plan, which agent wrote which line, and proof the merge was verified against the main it landed on.`

### 4.5 Funnel math, kept honest

Devtool freemium converts at 1–3%. At 2% of 10,000 active free users × $20/mo ≈ $50K ARR — real money lives in team seats (Warp's B2B2C pattern; GTM Plan §8). The metrics that matter from day one are **weekly active repos** and **agent runs verified/merged per week** — the numbers investors can't get from download counts. Tripwire: if design-partner teams won't pre-commit to paid pilots two months after launch act two, revisit packaging (GTM Plan §12.2).

---

## 5. The launch narrative

### 5.1 Two acts, on purpose

The launch is deliberately split (GTM Plan §7). **Act One** ships the free Git client — a real, complete thing whose value doesn't depend on believing a roadmap. **Act Two**, four to eight weeks later, spends that earned trust on the thesis: sandboxed agents whose work is test-verified before review. The corpse pile explains the ordering — a launch that leads with orchestration promises joins the category that died; a launch that leads with a working instrument earns the right to make one promise, later, and be held to it.

The founder-story essay (§5.5) and the three technical essays (index.lock, WSL2 sandboxing, the re-verifying queue — GTM Plan §7.1) publish *before* Act One, so launch day has substance to point at.

### 5.2 Show HN — Act One (final draft)

Written for launch conditions (a downloadable free client). If launch precedes packaging, substitute the sentence marked ††.

**Title:**

> **Show HN: Mainguard – a fast, native Git GUI for Windows (free, no login)**

**Body:**

> Mainguard is a Git client I've been building for about a year. It's a native desktop app — Avalonia + Skia on .NET 10, LibGit2Sharp underneath — not an Electron shell around a web view. It's free, there's no account, and nothing leaves your machine. ††(Today it's a build-from-source dev preview: `dotnet build`, launch `Mainguard.App.Shell`.)
>
> What it does:
>
> - A commit graph that stays smooth on large histories — a virtualized, vector-drawn DAG lane router rendered directly at 60fps, not a chart library.
> - Staging down to the line. Stage, unstage, or discard by hunk; drag-select individual lines in the unified view; accept or reject blocks side-by-side. The patch engine is validated against `git apply`, so what you stage is exactly what Git stages.
> - A synchronized 3-pane conflict resolver (Ours | Result | Theirs) with per-side accept/reject/undo. Merge, rebase, cherry-pick, and pull all route conflicts through it.
> - An operation-history journal so ref moves are undoable, and a reflog viewer for the ones that aren't. Force-push is `--force-with-lease`, never a bare `--force`.
> - Branch, tag, and worktree porcelain; interactive rebase; five switchable themes on one design system.
>
> Why it exists: I got tired of `.git/index.lock` roulette — two tools touch the index, one exits early, and the next operation fails with a message that blames nothing and suggests nothing. Mainguard's one non-negotiable architectural rule is that every repository handle opens and closes through a single deterministic path, so the app itself can never leave that lock behind. When it finds a stale lock some other process left, it says so plainly and tells you how to check whether it's safe to remove — it won't silently delete a file another process might hold.
>
> Where it's going, stated honestly: the roadmap is a control plane for coding agents — a merge queue that re-verifies branches that go stale when main moves, risk-ranked review with per-hunk provenance, hardened local sandboxes. None of that is built. Today it's a fast, precise Git client for one developer, and I'd rather you hold me to the roadmap than believe it already exists.
>
> Feedback I'd most value: does the graph stay smooth on your gnarliest repo, and does line-level staging behave exactly like `git apply` for you?

*Self-check: every present-tense claim is on `main` (honesty contract §0.1); the roadmap paragraph names itself unbuilt (V-6); no exclamation marks (V-2); the objects are concrete — `git apply`, `index.lock`, `--force-with-lease` (V-1); nothing decorative survives deletion test (V-7).*

### 5.3 Show HN — Act Two (title and lede)

Four to eight weeks later, when the verification pipeline demonstrably works:

**Title:**

> **Show HN: Run coding agents in sandboxes that must pass your tests before you review their code**

**Lede:**

> A few weeks ago I posted Mainguard, a native Git client (thanks for the brutal and useful feedback — the graph got faster). This is the part I said wasn't built yet. It works now: spawn agents into isolated sandboxed worktrees, and their branches only reach your review queue after your test suite passes inside their sandbox. Merge one branch and every other "verified" branch goes stale and re-verifies automatically — because validated-then-stale is unvalidated. Vendor-neutral: Claude Code, Codex, OpenCode, and PRs from cloud agents all go through the same pipeline. Local, BYOK, no meter on your own hardware.

The Act Two post ships only when each sentence in the lede is true; the lede is written now so the build knows exactly what it must make true (V-6 as a planning tool).

### 5.4 The first-hour comment kit

The founder is in the HN thread within the hour, all day, technical and non-defensive (GTM Plan §7.2). Pre-answered objections, each on-voice — concede what's true, state the fact, never bristle:

- **"Why another Git client?"** — Fair — the client market is mature. The client is the foundation, not the pitch: the roadmap is verification and merge governance for agent-written code, and that layer is only buildable on a real Git engine. The client has to be excellent anyway, because you'll live in it.
- **"Why .NET/Avalonia and not Electron/Tauri?"** — Deterministic native rendering and real native controls. The commit graph is a virtualized vector canvas at 60fps; the diff and terminal surfaces need per-pixel control Electron makes expensive. Benchmarks against the Electron incumbents are in the repo. (Have them ready — GTM Plan §7.2.)
- **"Why FSL and not MIT?"** — The daemon is source-available so the security boundary is auditable — that's the part where trust matters. The failure ledger is public: free-and-thin died twice in this category in 2026. FSL keeps the code inspectable and the company alive to maintain it. (GitButler's fair-source reception suggests this lands — GTM Plan §2.5.)
- **"GitKraken / Fork already exists."** — They do, and Fork especially is excellent. Differences today: line-level staging validated against `git apply`, the operation journal, no account wall, native rendering (vs. GitKraken's Electron). Difference tomorrow: none of them is building verification and merge governance for agent output. (Never disparage; state the deltas — §3.4.)
- **"Won't GitHub/Anthropic/Cursor just ship this?"** — They're shipping the generation side, single-vendor each. None of them is incentivized to make its GUI a better home for a rival's agents; vendor-neutral verification is structurally Switzerland's job. If Cursor Origin ships local execution + provenance, that's the tripwire and I've said so publicly.
- **"What do you collect?"** — Nothing without opt-in. No login, no account, telemetry opt-in with a published schema, keys in the OS keyring, and the sandbox roadmap is default-deny egress. The security architecture doc is linked.
- **"Is the agent stuff vaporware?"** — It's a roadmap, labeled as one in the post and the README. What's real today is exercised by 1,042 tests. I'd rather under-claim here and be held to the rest.

### 5.5 The founder story — "Why I'm building Mainguard" (final draft)

The pre-launch essay and the About page. Three beats: the lock file, the instrument, the trust problem. Supersedes LaunchReserve §2.

---

> **The lock file**
>
> Every developer who has run more than one Git process against a repository has met `.git/index.lock`. A process exits early — a crashed editor plugin, a killed script, two tools reaching for the index in the same instant — and leaves the lock behind. The next operation fails with a message that blames nothing and suggests nothing. You delete a file you're not sure is safe to delete, and you hope.
>
> Mainguard began as an answer to that one footgun. Its first architectural rule is still its most important: every repository handle opens and closes through a single deterministic path, so the app can never leak the native state that leaves locks behind. And when Mainguard finds a stale lock some other process abandoned, it says so plainly — it names the file, says who probably left it, and refuses to silently delete something another process might still hold. A tool that guards your work doesn't guess on your behalf.
>
> That rule turned out to be a thesis in miniature: *the bug this app exists to prevent is losing work to a tool that was supposed to protect it.*
>
> **The instrument**
>
> The second belief is that a tool for high-stakes work should feel like an instrument, not a web page in a frame. So Mainguard renders natively — the commit graph is a vector-drawn lane router at 60fps, the surfaces are layered and quiet, the motion is fast and functional. One design system, five palettes, a single accent reserved for the one place your eye should land.
>
> The care concentrates where the stakes do. Force-push is `--force-with-lease`, never a bare `--force`, and the confirmation tells you what changes, what stays recoverable, and which safer path exists — before you click. Discards, hard-resets, and rebases all point to the way back in the same breath: reflog, journal, stash. Staging works down to the individual line, and every patch is validated against `git apply`, because "approximately what you selected" is not a thing a precision instrument does.
>
> **The trust problem**
>
> While I was building the client, the ground moved. Coding agents made it trivial to produce ten branches an hour — and produced a new kind of debt: review time is up 91% against a fixed human ceiling, and most developers say they don't fully trust what the agents wrote. Every vendor is selling faster generation. Almost nobody is working on the part that actually gates shipping: how you *verify*, *attribute*, and *safely merge* work you didn't write.
>
> That's where Mainguard is going, and I'll be precise about tense: none of it is built yet. The roadmap is a merge queue that re-verifies any branch that goes stale the moment main moves — because a branch validated an hour ago, against an older main, is not validated. A review cockpit that orders hunks by blast radius and shows which agent, under which approved plan, wrote each line. Sandboxes with default-deny egress, so an agent can be wrong without being dangerous. And a tamper-evident record of all of it, because "trust me" is not an audit trail.
>
> The client ships first because the client is the proof. Anyone can promise a control plane; the way you earn the right to build one is to ship the instrument underneath it and let people hold your work to the same standard the tool will hold the agents to. That's the deal, and I'd rather be held to it than believed in advance.

---

*Self-check: three beats, each grounded in a shipped mechanism or a named roadmap item; the unbuilt layer is declared twice (V-6); no competitor named, the enemy is the practice (§3.1); "91%" and "don't fully trust" trace to Viability §1.3 and GTM Plan §4; no exclamation, no "we're excited" (V-2, V-3).*

---

## 6. README hero — proposed copy

Replacement for the top of `README.md` (title through the "Why Build This?" section). Brand register; leads with what is true today; drops the emoji headers and the "blazing-fast" register for the instrument tone (PRODUCT.md anti-references; V-3 emoji allowance deliberately unused). Supersedes LaunchReserve §5.

**Left as proposed copy, not applied:** `README.md` carries uncommitted modifications from parallel workstreams on this branch, so an in-place edit risks a collision; per the in-doubt rule this block is drop-in ready for whoever owns that file.

---

> # Mainguard
>
> **A native Git client for high-stakes work — becoming the control plane for the agent era.**
>
> Mainguard is a precise, natively-rendered Git GUI: a 60fps commit graph, staging down to the individual line validated against `git apply`, and a 3-pane conflict resolver — built on Avalonia and LibGit2Sharp with .NET 10. It's an instrument, not a web view in a frame. The client works today. On top of it, we're building the harder thing: the place where any agent's work becomes trustworthy commits on `main`.
>
> ## What works today
>
> A fully working single-user Git client, exercised by the test suite:
>
> - **A commit graph that stays smooth.** A virtualized, vector-drawn DAG lane router rendered directly at 60fps — not a chart library — over large, tangled histories.
> - **Staging down to the line.** Stage, unstage, or discard by hunk; drag-select individual lines; accept or reject blocks side-by-side. Every patch is validated against `git apply`.
> - **A conflict resolver that respects your work.** Synchronized Ours | Result | Theirs panes with per-side accept/reject/undo; merge, rebase, cherry-pick, and pull all route here.
> - **Undo you can trust.** An operation-history journal makes ref moves reversible, backed by a reflog viewer for the rest. Force-push is `--force-with-lease`, never a bare `--force`.
> - **One design system, five themes.** Midnight Loom, Daylight Loom, Command Deck, Atelier, and Loom Aurora — switchable live, persisted across sessions.
>
> ## Where it's going *(roadmap — not built yet)*
>
> The multi-agent control plane below is **planned, not shipped** — marked plainly, because the whole thesis is trust. On the roadmap: a merge queue that re-verifies any branch that goes stale when `main` moves; a review cockpit that ranks agent diffs by risk and shows per-hunk provenance; hardened local sandboxes with default-deny egress; and an audit-grade, tamper-evident record of what each agent did — vendor-neutral, local-first, Windows-first. Read the roadmap as the destination; the client above is the current state.

---

*Changes vs. LaunchReserve §5: "the control plane" (definite article — the category claim we intend to own, backed by §2.4's verified empty field); "The client works today" replaces "That client is shipping today" (a dev preview "works"; "shipping" over-claims distribution — V-6); the [Horizon] paragraph now names vendor-neutral / local-first / Windows-first, the three positioning axes (§3.2), instead of listing features only.*

---

## Appendix — self-gate & source ledger

**The five-question gate (Voice Bible Appendix A), applied to this document's copy:**

1. *Point at the object* — every claim names a mechanism (`git apply`, `--force-with-lease`, `index.lock`, the re-verifying queue), a figure (+91%, 87%, $22M, £2–4/PR), or a source section.
2. *Where's the way back* — n/a for market prose; where copy touches product behavior (founder story, Show HN) recovery paths are named (reflog, journal, stash).
3. *Would it read the same in an audit log* — every competitor cell traces to a market-analysis doc, unverified caveats carried; every Mainguard capability is tensed shipped or [Horizon].
4. *Delete a word* — "successfully/blazing/insanely/very" class absent throughout; each framing shortcut (§3.3) is load-bearing or cut.
5. *Does severity ride the role* — no exclamation marks, no scare framing; the EU AI Act is "where procurement is heading," not a deadline threat.

**Sources by section:**

- §1–§2: `docs/business/market-analysis/Mainguard_Competitor_Research_2026-07-07.md` (per-competitor sections §§1–11, probes (a)–(f), synthesis), `Mainguard_MergeLoom_Deep_Dive_2026-07-07.md` (§§0–6), `Mainguard_Viability_And_Differentiation_2026-07.md` (§§1.2–1.5, 3), `Mainguard_Feature_Inventory_2026-07-07.md` (§11 classic clients), `Mainguard_Naming_And_Competitive_Landscape_2026-07.md` (Part 2), `docs/business/go-to-market/Mainguard_Master_Market_Document_2026-07.md` (§§2.4, 3.1–3.2).
- §3: Master Market Document §§4.1–4.4; Viability §4; GTM Plan §§2.1–2.5.
- §4: Master Market Document §8.1–8.3; GTM Plan §8; MergeLoom Deep Dive §2, §5.7.
- §5: GTM Plan §7 (two-act plan, channels, objection prep), §10.2 (demo script the Act Two lede mirrors); Viability §1.3 (the 91% figure); LaunchReserve §§1–2 (superseded drafts).
- §6: LaunchReserve §5 (superseded draft); PRODUCT.md anti-references; README.md current state.
