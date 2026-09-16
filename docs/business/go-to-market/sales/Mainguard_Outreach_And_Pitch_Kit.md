# Mainguard — Outreach Sequences & Pitch Kit

> **Detail source.** The consolidated, current view is [`BUSINESS.md`](../../../BUSINESS.md), [`GTM.md`](../../../GTM.md) and [`GTM_Assets.md`](../../../GTM_Assets.md) — start there. This file is the 2026-07/08 research they were built from: read it for depth, worked examples and its full source list. Where the two disagree, the consolidated docs win.

**Date:** 2026-07-11 · **Register:** brand (sales/GTM execution layer)
**Reads under:** `docs/business/go-to-market/Mainguard_Master_Market_Document_2026-07.md` (personas §4.6,
design-partner program Part XIII, demo script §15.2, pricing Part VIII) and `docs/creative/Narrative.md`
(positioning §3, pricing logic §4, the honesty contract §0, the first-hour comment kit §5.4 — which this
document extends into sales objections). Companies come from
`Mainguard_Company_Sourcing_Playbook.md`; ad spend lives in `Mainguard_Paid_Media_Plan.md`.

**The voice rule for everything here:** outreach is product copy with a send button. Every template
passes the five-question gate (Voice Bible Appendix A): name the exact thing we observed, state what
Mainguard is in one honest tense-disciplined sentence, make one small ask, offer an easy out. No "hope
you're well," no manufactured urgency, no follow-up guilt ("just bumping this"), nothing a recipient
would call spam. Two touches and a close-out; silence is an answer.

---

## 1. The staged motion (what we are selling, when)

Selling the wrong thing at the wrong stage is the failure mode this kit exists to prevent.

| Stage | Window | What we ask for | What we never ask for |
|---|---|---|---|
| **1 — Design partners** | now → Act Two | 30 minutes for a demo in exchange for brutal feedback; then the Track B deal (Part XIII) | money. "Not selling anything — looking for 3–5 design-partner teams before we charge anyone" (Part IX walkthrough) |
| **2 — Paid pilots** | Act Two + (the §12.2 tripwire window) | conversion of Track B teams + new teams to paid pilots (Pro seats, §6) | the Team tier — its governance features don't exist yet |
| **3 — Team/Enterprise** | 2027, governance shipped | $50+/seat expansion on pilot evidence | anything the audit/RBAC/SIEM build hasn't shipped ("do not sell to Priya before the governance features exist," GTM Plan §3.2) |

---

## 2. The pitch, tailored per persona

One product, three doors. Different pain, different hook, same honesty. Each pitch: **pain · hook ·
proof · ask · never-say**.

### 2.1 The engineering manager (owns review throughput)

- **Pain:** PR volume up ~2× with AI assistance while review time rose 91% against a fixed human
  ceiling (Viability §1.3 via Narrative §1); accountable for what the agents merged without the tools
  to answer for it.
- **Hook:** *"Mainguard is the engineering manager for your AI agents. Several agents in parallel, each
  in its own sandboxed worktree, with plans you approve before code is written, tests that run before
  you review, and merges that never happen without you."* (The locked EM register, Narrative §3.2.)
- **Proof:** the live demo (§4) — deterministic test verdicts, not AI review comments: *"a fact, not
  an opinion"* (Narrative §3.3). The client underneath is real and exercised by 1,042 tests.
- **Ask (stage 1):** a 30-minute demo with one of their teams that already runs agents; qualify for
  Track B.
- **Never say:** throughput promises with invented numbers; "replaces code review"; anything about
  the audit tier as if it shipped. Tense discipline: pipeline features are demonstrable in the demo
  build or labeled "landing in vN" (§15.2 note).

### 2.2 The security / compliance stakeholder (the audit-trail door)

- **Pain:** auditors and client contracts asking "who wrote this code and was it tested" — with no
  record to point at. Procurement direction: EU AI Act Art. 12 logging, NIST AI RMF, ISO 42001
  (Part XVIII).
- **Hook:** *"Agents can be wrong without being dangerous: sandboxed, default-deny, and every action
  attributable. Audit-grade evidence of what each agent did — local-first, so the code never leaves
  your machines."* All of it **[Horizon]**, and said so.
- **Proof:** the trust posture that already exists — no login, local-first, source-available daemon
  (FSL), published security architecture, keys in the OS keyring (GTM Plan §2.5); the EU sovereignty
  structural fit (European vendor, local-first, self-hostable — Part XVIII).
- **Ask:** **not a sale** (stage rule). A 30-minute conversation to learn what their auditors ask for
  today — a discovery interview that shapes the governance tier they'd buy in 2027.
- **Never say:** "the EU AI Act requires this" (it doesn't require crypto, and the Omnibus moved the
  dates — honesty contract §0.2); "tamper-proof" (the word is *tamper-evident*); any certification we
  don't hold.

### 2.3 The founder / CTO of an AI-forward startup

- **Pain:** the team ships with agents daily; velocity is the business, but every blind merge is a
  production incident on credit. Tool sprawl: worktrees + tmux + a Mac-only orchestrator half the
  team can't run.
- **Hook:** *"Run your agents wherever you like — Mainguard is where their work becomes trustworthy
  commits on main."* (Narrative §3.2.) For the analogy-minded: *"Conductor for Windows — with
  verification"* (§3.3).
- **Proof:** vendor-neutral by design (Claude Code, Codex, OpenCode through one pipeline); BYOK — their
  existing subscriptions, no inference margin, **no per-PR meter on your own hardware** (Narrative
  §4.3); honest capacity — 4–6 agents on a 16 GB laptop, not "swarms" (§0.3).
- **Ask (stage 1):** design partnership — roadmap influence while it's still being shaped, free year
  at GA (Part XIII deal), in exchange for real usage and blunt feedback.
- **Never say:** "swarm," "50 agents," orchestration-as-pitch (Narrative §3.4); growth theater of any
  kind — founders detect it fastest.

### 2.4 The champion (Sam — how we usually get in)

Most company doors open from below: a senior IC already running 3–6 agent sessions (GTM Plan §3.2).
The champion pitch is the product itself: the free client today, Act Two when it ships. What we ask a
champion for is 20 minutes of workflow walkthrough (a §6.1 discovery interview) — and, if it resonates,
an intro to whoever owns the review-queue problem. Champions are never asked to "sell internally";
they're given the demo, the essays, and the one-pager, which do that on their own.

---

## 3. Outreach sequences

Rules of engagement: every message names the *real observed evidence* from the sourcing playbook
(a repo file, a JD, a talk, a post) — if we can't name why *them*, we don't write. Dutch for Tier 1/2,
English for Tier 3 (Part IX). Two touches, then a polite close-out, then nothing.
Sequences below are stage-1 (design partner); stage-2 conversions reuse §6.

### 3.1 Cold email — engineering manager (Segment A evidence)

**Touch 1 — subject: `agent branches, verified before review — 30 min?`**

> Hi {name},
>
> I saw {evidence — e.g. "your team's CLAUDE.md conventions in the {repo} repo" / "your post on running
> parallel Codex sessions"} — you're further into agent-driven development than most teams in NL.
>
> I'm building Mainguard in Enschede: a native Git client that runs coding agents in sandboxed
> worktrees and makes them pass your test suite before anyone reviews the diff. The client is built
> and fast; the verification pipeline is the part I'm shaping now with a handful of design-partner
> teams — before we charge anyone.
>
> Could I get 30 minutes for a demo in exchange for brutal feedback? If the review queue isn't a
> problem for you yet, say so and I'll leave you alone.
>
> {founder name} — Mainguard, Enschede

**Touch 2 (day 8) — reply in-thread, one new fact:**

> One thing I didn't say: when you merge one agent branch, every other "verified" branch is stale
> against the new main — the queue I'm building re-verifies them before they can merge. Nothing on
> the market does that step, which is exactly why I want design partners shaping it. Still happy to
> show it in 30 minutes; otherwise I'll close this out.

**Close-out (day 20):**

> Closing the loop — no reply needed. If agent review ever becomes the bottleneck, the demo offer
> stands: {link}. The free client is at {link} either way.

### 3.2 Cold email — Windows/.NET shop (Segment B evidence)

**Touch 1 — subject: `een native Git-client voor Windows — gebouwd in Enschede`** *(Dutch for Tier 1/2;
English mirror for international shops)*

> Hallo {name},
>
> {evidence — bijv. "Jullie Techorama-sessie over {onderwerp}" / "jullie vacature voor een
> .NET-developer met Azure DevOps"} — jullie zijn precies het soort Microsoft-stack team waarvoor ik
> Mainguard bouw.
>
> Mainguard is een native Git-client (Avalonia + LibGit2Sharp, geen Electron): staging tot op de regel,
> gevalideerd tegen `git apply`, een 3-pane conflict resolver, 60fps commit graph. Gratis, geen
> account, en er verlaat niets je machine. Daarbovenop bouwen we een control plane voor AI-coding-
> agents — dat deel is roadmap, en dat zeg ik er eerlijk bij.
>
> Ik zoek een paar Windows-teams die de client op hun echte repos willen breken en daar eerlijk over
> zijn. 30 minuten demo, geen verkooppraatje?
>
> {founder name} — Mainguard, Enschede

**Touch 2 (day 8):** one new fact — the index.lock essay link ("waarom agents je repo corrupt maken,
en hoe deterministisch handle-management dat oplost") + the offer restated in one line.
**Close-out (day 20):** as §3.1, with the download link.

### 3.3 Cold email — compliance/regulated org (Segment C, discovery-only)

**Touch 1 — subject: `how do your auditors ask about AI-written code?`**

> Hi {name},
>
> {evidence — e.g. "your DORA-readiness post" / "the ISO 27001 scope on your site"} prompted this.
> I'm building Mainguard, a local-first control plane for AI coding agents — sandboxed execution,
> test-verification before review, and an audit-grade record of which agent wrote which line. Part of
> that is shipped, part is roadmap, and I'm deliberately designing the audit layer *with* the people
> who'll have to defend it to auditors.
>
> No sale here — there's nothing to buy yet at your tier. I'd value 30 minutes on what your auditors
> and clients actually ask about AI-written code today. In exchange you'll see exactly where the
> tooling is heading before procurement asks you about it.
>
> {founder name} — Mainguard, Enschede

**Touch 2 (day 10):** the sovereignty angle, one paragraph (European vendor, local-first, source-
available, self-hostable — Part XVIII), plus the ask restated. **Close-out (day 21).**

### 3.4 LinkedIn DMs (shorter, same rules)

Connection note (≤ 300 chars) — **EM/founder:**

> Building Mainguard in Enschede — agents in sandboxed worktrees, test-verified before review. Saw
> {evidence}. Looking for design-partner teams, not selling. Open to connecting?

First DM after accept — **EM/founder:**

> Thanks for connecting, {name}. The short version: Mainguard is a native Git client (shipped, free, no
> login) growing a verification pipeline for agent branches — pass-your-tests-before-review, re-verify
> when main moves. I'm taking 3–5 design-partner teams before charging anyone. 30-minute demo for
> brutal feedback — interested? If not, no follow-up.

**Dana (individual, Track A):**

> Saw you're deep in .NET — Mainguard might be your kind of tool: native Avalonia Git client, staging to
> the line, free, no account. If you try it on your gnarliest repo and tell me what breaks, that's
> exactly the feedback I need: {link}

**Champion → intro ask (only after a real conversation):**

> That walkthrough was genuinely useful — thank you. If the review-queue pain we discussed lands on
> someone's desk at {company}, I'd value an intro. And if that's awkward, forget I asked.

### 3.5 The warm-intro one-liner (for Novel-T, MVPs, the advisor)

What we send people who offered to forward us:

> Mainguard (Enschede): a native Git client for Windows — free, no login — growing into a control plane
> that makes AI-agent code safe to merge: sandboxes, test-verification before review, a merge queue
> that re-verifies stale branches. Looking for design-partner teams that already run coding agents.
> 30-minute demo, feedback in return, nothing for sale yet. {link}

---

## 4. Demo → pilot → close

### 4.1 Discovery first (15 minutes, before any demo)

The §6.1 script spine, asked about the past, not the future: how many agent sessions in parallel
today · the last time an agent broke something or two collided · what do you actually read when
reviewing agent output · ever merged agent code you didn't fully review — what happened · who asks
"did AI write this" · what do you pay for today. Their answers select which demo beats get weight —
and a team with no real answers is Track A material, not Track B (respect the disqualifier).

### 4.2 The demo (the rehearsed 10 minutes, §15.2)

Beat structure, verbatim from the master doc: (1) real repo, instant graph, themes, line-level
staging — "a real Git client, not a wrapper"; (2) two agent tasks, worktree isolation, no
`.git/index.lock` roulette; (3) verification — one branch passes, one fails, the failing one never
reaches review; risk-ranked hunks, provenance gutter; (4) merge → the second branch's verification
goes stale → the queue re-verifies — "no other product on the market does this step"; (5) the audit
view. **Anything not yet built runs scripted/mocked, labeled "landing in vN" — never faked silently**
(§15.2 note). Close by asking what they'd need to see to run it on a real repo for a month.

### 4.3 Qualify (the Part XIII bar)

Track B requires all three: **urgency** (already duct-taping worktrees + tmux), **capacity** (a named
champion who'll do bi-weekly calls), **representativeness** (across the portfolio: one .NET enterprise
shop, one AI-forward startup, one agency). Teams failing the bar get Track A (free client, occasional
feedback) — kindly, explicitly, and in writing, so nobody's time is wasted.

### 4.4 The pilot (design-partner structure, time-boxed)

The Track B deal, offered in one page:

- **They get:** early access to the verification pipeline as it lands · direct roadmap influence ·
  a free year (or deep discount) at GA · the founder on a bi-weekly call.
- **We get:** real usage on real repos · bi-weekly feedback · logo/case-study rights at GA.
- **Time-box: 6 months** with success criteria set at signing — or it becomes unpaid consulting
  (Part XIII). Suggested criteria: ≥ N weekly active repos on the client within 30 days; the agent
  pipeline exercised weekly once it lands; a written verdict at month 3 ("would you pay, what's
  missing").
- **Instrumented from day one:** weekly active repos, agent runs verified/merged per week — the same
  KPIs the seed story needs (GTM Plan §9).

### 4.5 The close (stage 2, at Act Two)

The conversion conversation happens when the pipeline demonstrably works, and it is short because the
pilot generated the evidence: *"Your team verified {n} agent branches in the last month; {m} went
stale and re-verified before merge. Pro is $20 per developer per month — your design-partner year is
free; we're asking you to commit to converting when it ends, and to say so publicly."* The tripwire is
honest in both directions: if Track B teams won't pre-commit to paid pilots within two months of Act
Two, we revisit packaging, not the pressure (GTM Plan §12.2).

---

## 5. Pricing & negotiation talking points

The locked table (Free $0 no-login · Pro $20/mo or $199/yr with perpetual fallback · Team $50+/seat
when it exists · cloud worktrees usage-based 2027 — Master Market Document §8.1) is not negotiated in
the room. What flexes: pilot length, onboarding help, design-partner discounts. Talking points:

1. **The anchor set.** Teams already pay CodeRabbit $24–48/dev/mo for review comments, Graphite ~$40
   for stacked review, Mergify $8+ for merge reliability (Narrative §4.1). Mainguard's Team tier sits
   above them because it bundles what they each sell a slice of — review + queue + governance.
2. **A fact, not an opinion.** Their review-bot spend buys probabilistic comments (~35% of CodeRabbit
   comments audited as genuinely useful — GTM Plan §5.3); a test suite passing in the agent's sandbox
   is deterministic. Same budget line, harder evidence.
3. **No per-PR meter on your own hardware.** BYOK local runs cost tokens only; any team over ~50
   PRs/month beats per-PR pricing on a flat license (Narrative §4.3). We charge for the pipeline, not
   per unit of your own work — and a budget gateway *prevents* runaway agent spend rather than billing
   it.
4. **The fallback is real.** $199/yr keeps a perpetual-fallback build (JetBrains-style), with the
   honest caveat attached: fallback builds keep current agent-CLI adapters via a separately versioned
   channel, or the fallback's value decays in weeks (§8.1). Say the caveat before they find it.
5. **What we discount:** annual prepay (the $199 already is), design partners (free year, locked),
   multi-seat pilots (time-boxed, converting to list). **What we never do:** free Team tier
   ("orchestration monetizes at zero" is a grave, not a strategy — GTM Plan §5.4) · per-PR pricing ·
   selling governance features before they ship · lifetime deals on the pipeline.
6. **Procurement asks (EU):** local-first means no code processed server-side (GDPR posture,
   Master Market Document §11.2); FSL source-available daemon; published telemetry schema; EU entity
   (BV, Part XI). These answers are differentiators — volunteer them.

---

## 6. Objection handling (the sales extension of the Narrative §5.4 kit)

Format: concede what's true, state the fact, never bristle.

| Objection | The honest answer |
|---|---|
| **"GitHub/Copilot/Cursor will just ship this."** | They're shipping the generation side, single-vendor each. None is incentivized to make its GUI a better home for a rival's agents; teams already mix vendors per task. Vendor-neutral verification is structurally Switzerland's job (Narrative §2.2). And if Cursor Origin ships local execution + provenance, that's our published tripwire — we'd re-plan and say so (GTM Plan §12.1). |
| **"Conductor is free — why would we pay?"** | It is, and it's good — on a Mac, for launching agents. It's also unmonetized; two funded free orchestrators died in 2026 (Bloop, Terragon). We never charge for launching agents either — the paid layer is verification and merge governance, the part with no free substitute (Narrative §4.1). |
| **"You're one person. Vendor risk."** | Today, yes — with a real shipped client (1,042 tests), a source-available daemon so the security boundary outlives any vendor, local-first so your code and history are never hostage, and a perpetual-fallback license. Compare that failure mode honestly with a cloud tool that turns off. (And we sell nothing to your tier until the team and the features exist.) |
| **"We already pay for Copilot/Cursor/CodeRabbit."** | Keep them — Mainguard is the intake, not the replacement. Copilot and Jules are the PR firehose our pipeline drinks from (Narrative §2.2); a review bot can stay as an optional signal. What you don't have is deterministic local verification and a queue that re-verifies stale branches — nobody ships that (§2.4). |
| **"Our devs are on Mac."** | Some are — Mainguard is cross-platform Avalonia, Windows-first because that's the unserved half (59.2% of developers). If you're all-Mac and happy with Conductor, we're honestly not your first tool; when you need verification or your Windows colleagues need anything at all, we're here. |
| **"Running coding agents at all is a security risk."** | Today it mostly is — agents run unsandboxed on hosts with worktree isolation at best (Competitor Research via Narrative §2). That's the reason for the roadmap: hardened sandboxes, default-deny egress, so an agent can be wrong without being dangerous. Until you allow agents at all, the free client stands on its own. |
| **"Why not MIT / fully open source?"** | The daemon is source-available (FSL) so the security boundary is auditable — the part where trust matters. Free-and-thin died twice in this category in 2026; FSL keeps the code inspectable and the company alive to maintain it (Narrative §5.4). |
| **"We don't run agents yet."** | Then don't buy anything — 31% of developers run agents today, so you're in the majority (GTM Plan §4). Take the free client; it's a complete tool, not a trial. When agents arrive at your shop, the safe way to run them will already be installed. |
| **"The EU AI Act doesn't actually require this."** | Correct — Article 12 mandates logging and traceability, not cryptography, and the Omnibus moved high-risk dates to Dec 2027 (honesty contract §0.2). We don't sell deadline fear. What's real: auditors and procurement are already asking who wrote what and what was tested — the record is worth having before it's demanded. |
| **"We could build this internally."** | You could — Meta did (RADAR exists because it couldn't be bought, and it catches a 1/3 revert rate — GTM Plan §5.1). The question is whether a verification pipeline is your product or ours. The build cost includes a real Git engine — that's the prerequisite every wrapper lacks. |
| **"Validated by tests isn't validated — our test suite is weak."** | True, and we won't pretend otherwise: verification is as strong as your gates. The pipeline runs *your* suite plus any checks you add, and the honest register survives: "passed in its sandbox — not yet reviewed by you" (V-6). Mainguard removes the blind part of the merge; it doesn't remove the review. |
| **"Why is the sales motion so slow/small?"** | By design: 3–5 design-partner teams before anyone pays, pilots time-boxed to six months, governance sold only when it ships. The product's promise is that nothing merges unverified — the company sells the same way. |

---

## 7. Kit inventory (what exists, what this doc adds)

Already written elsewhere and reused as-is: the one-pager pre-read (Master Market Document §15.3) ·
the deck skeleton (§15.1) · the demo script (§15.2) · the founder-to-champion note (Part IX
walkthrough) · the Show HN drafts and comment kit (Narrative §5). This document adds: the staged
motion (§1), per-persona pitches (§2), the outreach sequences and DM set (§3), the
discovery→demo→pilot→close flow (§4), pricing talking points (§5), and the sales objection table
(§6). When any upstream fact changes (pricing, tripwires, competitor state), the master document
changes first and this kit follows — never the reverse.
