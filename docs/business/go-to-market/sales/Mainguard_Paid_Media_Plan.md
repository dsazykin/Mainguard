# Mainguard — Paid Media Plan (developer & B2B channels)

> **Detail source.** The consolidated, current view is [`BUSINESS.md`](../../../BUSINESS.md), [`GTM.md`](../../../GTM.md) and [`GTM_Assets.md`](../../../GTM_Assets.md) — start there. This file is the 2026-07/08 research they were built from: read it for depth, worked examples and its full source list. Where the two disagree, the consolidated docs win.

**Date:** 2026-07-11 · **Register:** brand (sales/GTM execution layer)
**Reads under:** `docs/business/go-to-market/Mainguard_Master_Market_Document_2026-07.md` (the strategy hub; channel
priorities §14.5, pricing Part VIII, launch plan Part XIV) and `docs/creative/Narrative.md` (positioning,
the honesty contract §0, framing shortcuts §3.3). Every ad string here has passed the Voice Bible
five-question gate (Appendix A); brand register, no exclamation marks (V-2), concrete objects (V-1),
shipped-vs-**[Horizon]** tense discipline (Narrative §0.1).

---

## 0. Ground rules before any euro is spent

1. **Paid media is an amplifier, not the engine.** The GTM Plan's channel ranking (§7.5) puts organic
   (HN two acts, build-in-public X, founder-disclosed subreddits, Console.dev) above every paid option,
   and the evidence base (GitButler/Graphite/Supabase launches) is organic. Paid spend only ever
   amplifies a message that has already proven itself organically — the GTM Plan's own rule for creator
   sponsorships ("consider one $2–5K mid-tier sponsorship only after the organic message proves out",
   §7.3) generalizes to every channel here.
2. **Ads obey the honesty contract.** An ad that runs during Act One claims only the shipped client.
   Verification-pipeline claims run only after Act Two ships and each sentence is true (Narrative §5.3:
   "the lede is written now so the build knows exactly what it must make true"). No ad ever says
   "swarm," "50 agents," or leads with "orchestration" (Narrative §3.4).
3. **Measurement respects the trust posture.** The free client has no login and no default telemetry
   (GTM Plan §2.5) — so there is no user-level attribution, by design. We measure with per-channel
   landing paths + UTMs, per-placement promo URLs, and one self-reported "where did you hear about
   Mainguard?" question on the waitlist and in the opt-in telemetry schema. Self-reported attribution is
   the primary source of truth; platform dashboards are directional only. This is a feature we can say
   out loud: *no tracking pixels in the app, ever.*
4. **Budget scale is honest.** This is a pre-revenue company in Enschede whose funding stack is
   WBSO + (later) VFF (Master Market Document Part X). The numbers below are sized to that reality,
   not to a Series-A media plan. Every phase has a kill criterion.

---

## 1. The funnel and the metrics (define before spending)

```
impression → landing page visit → download (no signup) → activation → retention → waitlist/Discord → Pro
```

| Stage | Metric | Definition | Target |
|---|---|---|---|
| Visit | LP conversion | visit → download click | ≥ 8% on intent channels (search), ≥ 2% on interrupt channels (Reddit/X) |
| Activation | **Weekly active repos** | opened a real repo and performed ≥ 1 write operation in week 1 | the product KPI #1 (GTM Plan §9.1); per-channel activation rate is the channel-quality signal |
| Retention | Week-4 return | active again in week 4 | the go/no-go for scaling any channel |
| Revenue | Free→Pro conversion | ≥ 2% of active free users (GTM Plan §8 funnel math) | measured per self-reported source |

**CAC targets** (derived from the locked pricing table, Master Market Document §8.1):

- **Per activated free user:** ≤ €10 blended paid. Above that, the channel is buying downloads, not users.
- **Per Pro subscriber:** ≤ €150 — roughly nine months of $199/yr revenue; acceptable because Pro is
  also the Team-tier funnel (Kite's law: individuals are the funnel, teams are the revenue — GTM Plan §5.4).
- **Per Team seat (2027, once the tier exists):** ≤ €500, justified by $50+/seat × multi-seat × low churn.
- **Attribution hygiene:** every placement gets its own path (`mainguard.dev/tldr`, `/console`, `/nick`),
  and newsletter/podcast placements get a named promo mention so self-report can catch what UTMs miss.

**The one dashboard:** channel · spend · visits · downloads · activated (self-report + UTM) · CAC/activated ·
week-4 retention of that cohort. Reviewed monthly; any channel below half the activation rate of the
organic baseline for two consecutive months is cut.

---

## 2. Per-channel plan

Order is by expected yield for this product, not by the fame of the channel. Each entry: **fit ·
targeting · budget share (at scale, §3 Phase 3) · creative · measurement**.

### 2.1 Dev newsletters — TLDR, Console, Pointer, C# Digest, Bytes

**Fit: highest paid-channel fit.** Newsletter readers are self-selected developers in a reading (not
dodging-ads) mode; the GTM Plan already ranks Console→Pointer/TLDR as the first paid step (§7.5, ~$3.5K+
per placement). One placement is also a clean, measurable experiment: one date, one link, one audience.

- **Console.dev** — submit editorially first (free; we meet every stated criterion: developer-primary,
  self-service download — GTM Plan §7.2). Buy a paid sponsorship only if the free feature converts well.
- **TLDR** — TLDR Dev for Act One (the client), **TLDR AI for Act Two** (the verification story — the GTM
  Plan names TLDR AI as an Act Two channel, §7.3). Large but broad; expect lower activation than Console.
- **Pointer** — senior-engineer/EM readership; the best fit for the Act Two message and the essays
  (the three pre-launch essays are explicitly "HN/Pointer/newsletter fodder", GTM Plan §7.1).
- **C# Digest / .NET-specific letters** — not in the original brief but the *highest-precision* audience
  for the Dana persona (Windows/.NET professional, GTM Plan §3.2). Cheaper than TLDR, exactly on-ICP.
- **Bytes (ui.dev)** — **low fit, skip.** Bytes is a JavaScript-ecosystem newsletter; Mainguard's wedge
  audiences are agent power users and Windows/.NET professionals. Revisit only if a front-end-heavy
  use case emerges organically. Named here so nobody re-litigates it.

**Targeting:** the newsletter *is* the targeting — pick letters whose reader is Sam (TLDR AI, Pointer)
or Dana (C# Digest, Console).
**Budget share at scale: 30%.**
**Creative (Act One, shipped claims only — one-line sponsor slot):**

> Mainguard — a native Git client for Windows. Line-level staging validated against `git apply`, a 3-pane
> conflict resolver, a 60fps commit graph. Free, no login, nothing leaves your machine.

**Creative (Act Two — runs only when every clause is shipped):**

> Mainguard runs coding agents in local sandboxes and makes them pass your test suite before you review
> the diff. Merge one branch and every stale "verified" branch re-verifies — validated-then-stale is
> unvalidated. Vendor-neutral: Claude Code, Codex, OpenCode. Free client; Pro $20/mo.

**Measurement:** unique path per letter + promo mention; judge on activated users per €, not clicks.

### 2.2 YouTube dev channels & dev podcasts

**Fit: high — the only paid format that can carry the demo.** The product is inherently
screenshot-able (GTM Plan §7.1) and the pitch *is* the demo (§10.1 rule 7). A sponsor read plus a
20-second screen capture of the stale-branch re-verification does what no banner can.

- **Sequencing per the GTM Plan (§7.3):** organic creator outreach first (ThePrimeagen, Theo, Fireship —
  founder-to-creator email, 60-second clip, hook: "agents that must pass tests before you see the PR").
  Paid only after the organic message proves out, starting with **one** $2–5K mid-tier placement.
- **Paid shortlist, in fit order:** Nick Chapsas (.NET — the single best Dana match), IAmTimCorey
  (.NET, more junior-skewed), a mid-tier AI-coding-workflow channel for the Sam audience at Act Two.
  Podcasts: .NET Rocks! (Dana), The Changelog / Syntax-class shows only at scale phase (broad).
- **Targeting:** channel choice is the targeting; require audience-geography and viewer-profile stats
  before booking.

**Budget share at scale: 25%.**
**Creative — the 30-second sponsor read (Act Two version, creator's own voice, no hype words):**

> This episode is sponsored by Mainguard, a native Git client for Windows that's grown a control plane
> for coding agents. Your agents run in local sandboxes, their branches only reach you after your own
> test suite passes, and when main moves, anything stale gets re-verified before it can merge. Local,
> bring-your-own-keys, and the Git client underneath is free with no login. Link and demo below.

**Measurement:** named URL (`mainguard.dev/nick`) + self-report; a creator placement is judged over 60
days (long-tail views), unlike newsletters (7 days).

### 2.3 Google Search — competitor and intent keywords

**Fit: medium-high, small but warm.** Search is the only channel where the user asks first. Volumes on
these queries are low; that is fine — this is a precision channel, capped and always-on.

**Keyword groups (each its own ad group and landing section):**

1. **Competitor-alternative intent:** `gitkraken alternative`, `fork git client windows`,
   `sublime merge alternative`, `sourcetree alternative windows`, `conductor for windows`. Policy note:
   bidding on competitor brand terms is permitted; **their trademarks never appear in our ad copy**
   (also the Narrative §3.4 rule — no named competitor as villain).
2. **Category intent:** `git gui windows`, `native git client windows`, `git client with partial staging`.
3. **Agent-workflow intent (Act Two on):** `run claude code in parallel`, `multiple coding agents git
   worktrees`, `review AI generated pull requests`, `merge queue for AI code`.
4. **Pain intent (route to the essay, not the download):** `git index.lock fix`, `another git process
   seems to be running` — land on the index.lock essay (GTM Plan §7.1 essay (a)) with a quiet download
   CTA. Cheap clicks, exactly our founding story.

**Targeting:** exact/phrase match only; geo: EN worldwide with NL/DACH bid boost (the expansion
sequencing, Master Market Document Part XVIII); negative keywords: `github` (as a bare term), `tutorial`, `download crack`.
**Budget share at scale: 15%** (capped ~€40/day; raise only with maintained activation).
**Creative (RSA components, Act One):**

- Headlines: `A native Git client for Windows` · `Stage down to the line` · `Free. No login.` ·
  `Not another Electron app` · `Validated against git apply`
- Descriptions: `60fps commit graph, 3-pane conflict resolver, an operation journal that makes ref
  moves undoable. Nothing leaves your machine.` · `Mainguard is Avalonia + LibGit2Sharp — a real Git
  engine with a native UI. Free for private repos, no account.`

**Measurement:** the cleanest channel — conversion per keyword; prune weekly at first.

### 2.4 Reddit ads — r/programming, r/devops, r/git (+ the ones that fit better)

**Fit: medium.** Reddit's organic lanes are already in the plan (r/ClaudeAI, r/ChatGPTCoding, r/git,
r/csharp, founder-disclosed, 90/10 rule — GTM Plan §7.5). Paid Reddit works only when the ad reads like
a post a developer would upvote. Note honestly: **r/programming and r/devops are broader than our ICP**;
the better paid subreddit set is `r/git`, `r/csharp`, `r/dotnet` (Dana) and `r/ClaudeAI`,
`r/ChatGPTCoding` (Sam). Buy r/programming only at scale phase for Act Two awareness.

**Targeting:** subreddit-level placement only (no interest-graph broadening); desktop-heavy schedule;
geo EN + NL/DACH boost.
**Budget share at scale: 15%.**
**Creative (promoted post, Act One, r/git / r/csharp — written as a post, not a banner):**

> **Title:** Mainguard — a native (Avalonia, not Electron) Git GUI for Windows. Free, no login.
> **Body:** Line-level staging that's validated against `git apply`, a synchronized 3-pane conflict
> resolver, an operation journal so ref moves are undoable, and a commit graph that stays smooth on
> large histories. Built on LibGit2Sharp with one rule: every repo handle opens and closes through a
> single deterministic path, so the app can never leave a stale `index.lock` behind. Feedback wanted —
> especially from anyone whose repo makes other clients stutter.

**Creative (promoted post, Act Two, r/ClaudeAI — runs only when shipped):**

> **Title:** Your agents' work, test-verified before you see it — a local, vendor-neutral control plane
> **Body:** Mainguard runs Claude Code, Codex, or OpenCode sessions in isolated sandboxed worktrees.
> A branch only reaches your review queue after your test suite passes in its sandbox, and when you
> merge one branch, every other verified branch goes stale and re-verifies. Realistic capacity: a
> developer supervising 4–6 agents on a 16 GB laptop. The Git client underneath is free.

**Measurement:** Reddit CTR is vanity; judge on landing-path activation. Comments on promoted posts
stay open and the founder answers them — same discipline as the HN thread (Narrative §5.4).

### 2.5 Hacker News — the Show HN, and what is actually buyable

**Fit: the #1 channel — and it is not for sale.** HN sells no self-serve ads (job posts are for YC
companies). "HN" in this plan means the two organic Show HN acts, already fully drafted:

- **Act One** (~October): *"Show HN: Mainguard – a fast, native Git GUI for Windows (free, no login)"* —
  final draft, body, and self-check in Narrative §5.2; Tue–Thu 9am–12pm ET, direct download, founder in
  comments within the hour with the pre-answered objection kit (Narrative §5.4; GTM Plan §7.2).
- **Act Two** (4–8 weeks later): the verification story — title and lede locked in Narrative §5.3,
  shipped only when every sentence is true.
- **Timing interlock with paid:** no paid placement runs in the same week as either Show HN. A Show HN
  that smells coordinated with an ad flight reads as a campaign, not a launch; the earned-trust posture
  (GTM Plan §2.5) is worth more than one week of impressions. Paid flights start the week *after*,
  amplifying the message the thread validated.
- **The buyable adjacency:** HN-recap newsletters (e.g. Hacker Newsletter) sell sponsorships — a
  legitimate scale-phase placement under the newsletter budget (§2.1), not an "HN ad."

**Budget share: 0% (structural).** **Measurement:** thread rank, referrer traffic, and the retention
curve of the launch-week cohort — the launch goal is installs + a believable weekly-active-repos
number, not revenue (GTM Plan §7.2).

### 2.6 X/Twitter promoted

**Fit: low for cold promoted; the organic build-in-public feed is the X strategy** (GTM Plan §7.1 —
2–3 clips/week, Linear playbook; "Windows-native polish is itself novel content"). Developer ad
blindness on X is severe and targeting quality has degraded; cold promoted posts to developers are the
weakest spend in this plan.

- **The one paid use that earns its keep:** boosting an *already-proven* organic clip (top ~5% engagement)
  for 72 hours to followers-of lookalikes (followers of Conductor, Claude Code, GitKraken, Avalonia
  accounts). Never boost anything that hasn't already worked unpaid.

**Targeting:** follower-lookalike only; no keyword/interest targeting.
**Budget share at scale: ≤ 5%.**
**Creative:** whichever organic clip earned it — typically the graph render, the theme switch, or (Act
Two) the stale-branch cascade re-verifying. Caption style, Act One example:

> Staging down to the individual line, validated against `git apply`. Native Avalonia, not Electron.
> Free, no login. (12-second clip)

**Measurement:** boosted-clip landing-path activation vs the organic baseline of the same clip.

### 2.7 LinkedIn — the eng-manager / compliance buyer

**Fit: deferred by rule, then high.** LinkedIn is the only channel that reaches Priya (the EM/buyer
persona) in a buying context, and "LinkedIn is disproportionately effective for B2B in NL" (Master
Market Document §14.7). But the standing rule is binding: **"Do not sell to Priya before the governance
features exist"** (GTM Plan §3.2). Therefore:

- **Phases 0–2: €0 paid LinkedIn.** Organic only — mirror the build-in-public feed (§14.5 channel 2),
  publish the essays, let NL eng-managers discover the story.
- **Phase 3 (Team tier shipped, 2027):** paid activates for pilot-generation.
  - **Targeting:** job titles Engineering Manager / Head of Engineering / VP Engineering / CTO;
    company size 11–200; industries software, financial services, health tech; geos NL first, then DACH
    (the expansion sequence, Part XVIII). Retarget essay readers and the sovereignty page.
  - **Format:** single-image or document ads carrying one fact, never a stock-photo "team celebrating."
- **Budget share at scale: 10%** (LinkedIn CPCs are 5–10× Reddit's; only the $50+/seat tier justifies them).

**Creative (Phase 3 only — every claim must be shipped by then):**

> Your team merges more AI-written code every quarter. Can you say who wrote which line, what was
> tested, and against which main it was verified? Mainguard keeps that record — locally, per hunk,
> audit-grade. Book a 30-minute pilot conversation.

**Measurement:** cost per booked pilot conversation, nothing softer. Lead-gen forms off; route to a
calendar page so intent stays high.

### 2.8 Dev.to and Stack Overflow

**Fit: low. Named so nobody re-opens them quarterly.**

- **Dev.to:** organic cross-posting of the three essays costs nothing and earns long-tail search;
  do that. Paid billboards are cheap but low-intent; a €300 one-month test at scale phase is the
  maximum justified experiment.
- **Stack Overflow ads:** the audience-decline since AI assistants is well documented and its
  ad product favors big-brand awareness buys. Skip; revisit only if SO's collective/advertising
  products change materially.

**Budget share at scale: ≤ 5% combined, and only if the Dev.to test clears the activation bar.**
**Measurement:** same landing-path discipline; a failed test is not repeated.

### 2.9 OSS & conference sponsorship

**Fit: high per euro — credibility spend, not reach spend.** For a trust-positioned tool, being a
visible good citizen of its own stack is advertising that compounds.

- **OSS sponsorships (start pre-launch — the one paid action allowed in Phase 0):**
  **Avalonia** and **LibGit2Sharp** — the two projects Mainguard is built on (CLAUDE.md architecture).
  €100–300/mo total via GitHub Sponsors/OpenCollective. The audience seeing those logos is precisely
  Dana and the .NET-flagship story ("proof you can build world-class native UI in .NET" — Master Market
  Document §14.7) becomes credible because we fund the stack we praise.
- **Conferences (NL-first, per the events calendar §14.7):**
  - **Techorama Netherlands, 26–28 Oct 2026, Utrecht** — "perfectly timed with Phase-1 launch." CFP
    talk first (free, already planned: "Building a 60fps native Git client in Avalonia"); **the cheapest
    booth only if Phase 1 shipped** (the master doc's own condition). Budget if taken: ~€2–4K.
  - **dotNed meetups** — offer to host at Kennispark (§14.7): venue + pizza ≈ €300, exactly our room.
  - **GOTO Amsterdam / Codemotion** — opportunistic talks at Act Two; sponsorship only at scale phase.
- **What we don't sponsor:** hackathons-for-logo-walls and generic startup events; wrong audience,
  wrong signal.

**Budget share at scale: 10%** (treated as an annual line, not monthly).
**Measurement:** honest — this is brand spend. Track waitlist/Discord joins and design-partner
conversations attributable per event ("met at Techorama" column in the 40-candidate list).

---

## 3. Phased budget

Currency €; assume $≈€ for vendor prices. Each phase has an entry condition and a kill criterion.

### Phase 0 — Pre-launch (now → ~Sep 2026) · **≈ €150–300/mo, effectively €0 media**

| Line | Amount | Note |
|---|---|---|
| OSS sponsorships (Avalonia, LibGit2Sharp) | €100–300/mo | §2.9 — the only paid line |
| Everything else | €0 | Build-in-public X, essays, waitlist, Console.dev submission prep — all organic (GTM Plan §7.1) |
| Prep work | founder time | Landing paths + UTM scheme + the self-report question wired into the waitlist; keyword list drafted; creator shortlist with audience stats requested |

**Kill criterion:** none needed — there is nothing to kill. **Exit condition:** Act One ships.

### Phase 1 — Launch, Act One (~Oct 2026) · **≤ €1,500 total**

| Line | Amount | Note |
|---|---|---|
| Show HN, PH, Console.dev, subreddits, NL press | €0 | The launch itself is organic by design (GTM Plan §7.2; Dutch media list §14.6) |
| Reddit promoted-post test (r/git + r/csharp) | €500–1,000 | Starts the week *after* Show HN (§2.5 interlock), reusing the message the thread validated |
| Google Search, groups 1–2 + pain intent | ~€10/day | Always-on precision floor |
| Techorama presence | €0–2,000 | Talk if CFP accepted; booth only under the §2.9 condition (own budget line, not media) |

**Kill criterion:** if the Reddit test's cost per activated user exceeds €10, stop Reddit until Act Two
changes the message. **Exit condition:** Act Two ships and its lede is true.

### Phase 2 — Launch, Act Two (Nov–Dec 2026) · **≈ €5,000–9,000 total**

| Line | Amount | Note |
|---|---|---|
| One newsletter placement (TLDR AI **or** Pointer) | ~€3,500 | The GTM Plan's own next paid step (§7.5) — one placement, fully measured, before any second |
| One mid-tier creator sponsorship | €2,000–5,000 | Per §7.3 — only after organic creator outreach proves the message; Nick Chapsas-class for Dana or an AI-workflow channel for Sam |
| Google Search + Reddit (Act Two creative) | ~€800/mo | Add keyword group 3; add r/ClaudeAI promoted post |
| X boost of the best Act Two clip | ≤ €300 | §2.6 rule |

**Kill criterion per placement:** < 50 activated users or week-4 retention materially below the organic
cohort ⇒ that vendor is not rebooked. **Exit condition to scale:** the GTM tripwire — design-partner
teams pre-committing to paid pilots within two months of Act Two (GTM Plan §12.2). **If the tripwire
fails, paid media freezes with it** — packaging gets fixed before spend resumes.

### Phase 3 — Scale (2027, post willingness-to-pay signal) · **≈ €8,000–15,000/mo**

Entered only with paying teams and (per Part X) VFF/seed headroom. Budget shares as stated per channel:

| Channel | Share |
|---|---|
| Newsletters (TLDR/Pointer/Console/C# Digest + HN-recap letters) | 30% |
| YouTube/podcast sponsorships | 25% |
| Google Search (all four groups, NL/DACH boosted) | 15% |
| Reddit (add r/programming, r/devops for Act Two awareness) | 15% |
| LinkedIn (Team tier live — §2.7 rule satisfied) | 10% |
| OSS/conference (annualized) + Dev.to test + X boosts | ≤ 10% |

Reviewed monthly against the §1 dashboard; the two-month half-of-organic-activation rule cuts laggards.

---

## 4. Creative governance

- Every ad string passes the five-question gate (Voice Bible Appendix A) before trafficking; the
  drafted creative above is the approved baseline — variants change facts shown, never register.
- Tense discipline is release-gated: Act Two creative is pre-written (this doc) but **cannot traffic
  until the Act Two Show HN itself is true** (Narrative §5.3). One person owns that gate.
- Banned vocabulary in all paid placements: "swarm," "50 agents," orchestration-as-pitch, "blazing,"
  "game-changing," exclamation marks, "EU AI Act requires" (Narrative §3.4; honesty contract §0.2).
  The compliance angle in ads is always "audit-grade" / "what procurement is asking for."
- Competitor names never appear in ad copy — in targeting (keywords, follower lookalikes), yes;
  in the words, no (Narrative §3.4).

## 5. What would change this plan

- **Cursor Origin ships local execution + provenance** (the standing tripwire, GTM Plan §12.1) —
  re-plan positioning within a quarter; pause Act Two creative until re-derived.
- **A newsletter or creator placement over-performs 3×** — concentrate; this plan prefers depth in two
  proven channels over presence in eight.
- **Act One retention is weak** — paid stops entirely; ads cannot fix a leaky product, and the free
  client is the trust wedge everything else stands on (GTM Plan §1).
