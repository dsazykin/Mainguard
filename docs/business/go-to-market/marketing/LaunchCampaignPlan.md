# Mainguard Launch Campaign Plan — Organic

**Lane G Part 1 · Register: brand · Status: execution plan, keyed to the two-act launch.**

This is the *campaign* layer on top of the locked launch strategy: it decides what each channel carries, in what order, and around which hook. The strategy itself is locked upstream and is not re-litigated here: the two-act launch and channel rankings live in `Mainguard_Master_Market_Document_2026-07.md` Part XIV (which supersedes `Mainguard_GTM_Plan_2026-07.md` §7), the positioning registers in Master Doc §4.1, and all copy of record in [`docs/creative/Narrative.md`](../../../creative/Narrative.md) (Show HN drafts §5.2–5.3, comment kit §5.4, founder story §5.5). Every asset this plan schedules is drafted in the companion Lane G files: [`ContentCalendar.md`](ContentCalendar.md), [`SocialLaunchReserve.md`](SocialLaunchReserve.md), [`Manifesto.md`](Manifesto.md), [`EmailSequences.md`](EmailSequences.md), [`PressKit.md`](PressKit.md), [`VideoScripts.md`](VideoScripts.md).

**Scope fence.** Organic only. Paid acquisition (ads, sponsorships beyond the one sanctioned $2–5K act-two test in Master Doc §14.3) belongs to the sales/paid plan, not here.

**The honesty contract binds every asset** (Narrative §0): present-tense claims are on `main`; the control plane is **[Horizon]** until shipped; audit is "audit-grade / tamper-evident," never "legally required"; capacity is an honest 4–6 agents on a 16 GB laptop. An asset that violates the contract does not ship, whatever the deadline.

---

## 1. The campaign in one paragraph

Two acts, one enemy, one wedge. **Act One** (~October) launches the free, native, no-login Git client and asks for nothing but installs and honest feedback — it earns the standing to be believed. **Act Two** (4–8 weeks later) spends that standing on the thesis — *your agents' work, test-verified before you see it* — and leads with the one feature that costs the audience zero switching: **external-PR intake (P2-12)**, which makes Mainguard useful on day one without anyone changing how they run agents. The enemy throughout is a practice, never a company: **the blind merge** (Narrative §3.1). The manifesto essay carries the enemy; the engineering essays carry the credibility; the comparison pages carry the search traffic; HN carries the moments.

---

## 2. The wedge — P2-12 external-PR intake as the day-one hook

### 2.1 Why this is the hook

Every act-two competitor asks the audience to *move*: adopt a new agent runner, a new orchestrator window, a new cloud. Mainguard's intake asks them to *stay put*. The mechanism (Master Implementation Document v2, P2-12): subscribe the PRs your existing cloud agents already open — Codex, Jules, Copilot, any bot author — and each PR head is fetched into the sandboxed pipeline as `agent/pr-<n>`, enters the same merge queue at `Working`, gets verified against your real test suite, reviewed in the risk-ranked cockpit, and merged back through the host's own PR API. New commits on the PR re-enter the queue; a force-push invalidates the old verification. The intake **writes nothing to the upstream PR without an explicit user action** — a MUST in the spec, and a trust line worth saying out loud.

The market evidence for leading with it: Copilot's coding agent alone opened 1M+ PRs in five months (Octoverse 2025, via GTM Plan §4); Jules ships a public API and a GitHub Action; and *nobody* offers vendor-neutral intake of arbitrary agent PRs into a local verify→review→merge pipeline (Master Doc §3.5 probe (e) — verified empty). The firehose exists; the drain does not.

### 2.2 The wedge, worded (the campaign's act-two spine)

> **Keep your agents. Point Mainguard at the PRs they already open.**
> Codex, Jules, Copilot — whoever wrote it, the branch runs your test suite in a local sandbox before you review a line, and it re-verifies if it goes stale before it merges. Nothing changes about how you run agents. What changes is what you can trust when they finish.

Supporting lines, used verbatim across assets (Narrative §3.3): *"Hope is not a merge strategy." · "Validated-then-stale is unvalidated." · "A fact, not an opinion." · "Conductor for Windows — with verification."* The wedge line is the only new framing this campaign adds; it passes the five-question gate (object: the PR intake; way back: n/a-market prose; audit-legible: every clause maps to a P2-12 contract step; no filler; severity on facts).

### 2.3 Wedge discipline

- The wedge is **act-two copy only**. At act one it does not exist on any surface — the client launch must stand on the client (the corpse-pile lesson, GTM Plan §5.4).
- The wedge ships as copy **only when P2-12 demonstrably works** against at least two real bot-PR sources. The lede is written now so the build knows what it must make true (the Narrative §5.3 method).
- The wedge is never "we support every agent" (an unverifiable superlative). It is "any PR author you configure" — the mechanism, stated.

---

## 3. Channels — what each one carries

Rankings are locked (Master Doc §14.5); this table assigns each channel its *job* and its *assets*. Cadence is a commitment, not an aspiration — a channel we can't feed on schedule gets cut, not diluted.

| # | Channel | Job | Act | Assets (drafted in) | Cadence / timing |
|---|---|---|---|---|---|
| 1 | **Hacker News** | The two launch moments + essay distribution | Both | Show HN posts + first-hour FAQ ([SocialLaunchReserve](SocialLaunchReserve.md) §1–3); essays as regular submissions | 2 Show HNs; essays submitted on publish, Tue–Thu 9am–12pm ET |
| 2 | **Build-in-public X, mirrored to LinkedIn** | Continuity, screenshots, the retention audience | Pre → post | Thread series ([SocialLaunchReserve](SocialLaunchReserve.md) §4); clip subjects: graph on a huge repo, line-drag staging, 3-pane resolver, five-theme flip; act two adds queue-rail + stale-cascade captures from the prototype (labeled as prototype) | 2–3 clips/week from pre-launch; threads at each act |
| 3 | **Bluesky** | The HN-adjacent dev audience that left X | Both | Same threads, retoned per network notes ([SocialLaunchReserve](SocialLaunchReserve.md) §4.4) | Mirrors X cadence |
| 4 | **Reddit** (r/git, r/csharp, r/dotnet at act one; r/ClaudeAI, r/ChatGPTCoding at act two) | Founder-disclosed depth posts where each community's pain lives | Split by act | Act one: the client + index.lock essay angle. Act two: the wedge post — "I made my Codex/Jules PRs pass my tests locally before I review them" | 90/10 rule; one post per subreddit per act, founder-disclosed |
| 5 | **Console.dev → newsletters** | High-intent editorial reach | Act one submit; act two re-pitch | Press one-pager ([PressKit](PressKit.md)); TLDR AI pitched at act two with the wedge | Console.dev free submission launch week |
| 6 | **Engineering blog + comparison pages (SEO)** | Own the category vocabulary before MergeLoom's SEO wall does (Master Doc §14.5 #8) | Continuous | Everything in [ContentCalendar](ContentCalendar.md): 2 flagship engineering essays, 3 comparison pages, backlog | 1 substantial piece every 2 weeks minimum |
| 7 | **YouTube creators** (ThePrimeagen / Theo / Fireship tier) | The act-two amplification bet | Act two only | Founder-to-creator email + 60-second demo clip ([VideoScripts](VideoScripts.md) §1 cut-down); hook: "agents that must pass tests before you see the PR" | Organic outreach at act two; paid mid-tier only after organic proves |
| 8 | **Discord** | Retention and changelog, not acquisition | Beta onward | Release notes in the Voice Bible Appendix D voice (ex-LaunchReserve §6) | Seeded 20–50 members at beta |
| 9 | **Email list (waitlist)** | The only owned channel — and deliberately small, because the free tier has no login | Pre → post | Full sequences in [EmailSequences](EmailSequences.md) | Nurture bi-weekly; launch sends at each act |
| 10 | **NL press + events layer** | Local credibility timed to the acts (Tweakers/IO+ at act one; AG Connect/Silicon Canals at act two; Techorama 26–28 Oct) | Both | Press kit + Dutch angles ([PressKit](PressKit.md) §4); pitch walkthrough per Master Doc §14.6 | Same week as each Show HN — the story must be "launch," not "plans" |

**What is deliberately absent:** Product Hunt gets its sanctioned ~10% of launch-week energy (badge + backlink) and no more; no TikTok/Instagram (wrong audience for a desktop Git instrument); no growth-hack tactics (fake scarcity, engagement bait) — all off-voice and off-thesis.

---

## 4. Sequencing — the campaign calendar skeleton

Dates key off **L1** (act-one Show HN, target ~October per Master Doc §14.2) and **L2** (act-two Show HN, L1 + 4–8 weeks). The full editorial calendar with per-piece detail is [ContentCalendar.md](ContentCalendar.md) §1.

| Phase | Window | Beats |
|---|---|---|
| **Pre-launch** | now → L1 | Build-in-public clips 2–3×/week. Publish, in order: founder story (Narrative §5.5, the About page + essay), the index.lock essay (~2 weeks before L1), the 60fps commit-graph essay (the "this team can build" proof). Waitlist nurture emails ride each publish. Trust assets (security architecture doc, telemetry policy, FSL statement) live *before* L1. Techorama CFP submitted. |
| **Act one** | L1 week | Show HN (Tue–Thu, 9am–12pm ET; founder in thread within the hour, all day). Same week: Console.dev, PH badge, r/git + r/csharp + r/dotnet posts, Tweakers + IO+ pitches, launch email, X/LinkedIn/Bluesky threads. Goal: installs, retention curve, weekly-active-repos — not revenue. |
| **Interlude** | L1 → L2 | Ship visibly against HN feedback ("the graph got faster" is act two's opening line — Narrative §5.3). Publish: comparison pages (GitKraken, Fork, Conductor), the WSL2 sandbox essay, the governed-merge-queue design essay. Onboarding drip runs for downloaders on the list. |
| **Act two** | L2 week | The **manifesto** publishes 3–5 days *before* L2 (the thesis lands first, the product answers it). Show HN act two (the verification story, wedge-led). Same week: r/ClaudeAI + r/ChatGPTCoding wedge posts, TLDR AI pitch, creator emails with the demo clip, AG Connect + Silicon Canals, act-two email, act-two threads. Pro tier ships at or shortly after. |
| **Post** | L2 + | Cadence holds: one substantial piece / 2 weeks (backlog in ContentCalendar §3), release notes per the Voice Bible Appendix D (ex-LaunchReserve §6), design-partner case studies as they mature into logos. |

**Sequencing rules that outrank the calendar:** (1) An act slips before an asset lies — no [Horizon] feature is ever presented as shipped to hit a date. (2) The manifesto never publishes before act one; enemy framing without a shipped instrument reads as vaporware rhetoric. (3) Every HN submission gets the founder's full day; if the day isn't available, the submission moves.

---

## 5. Message discipline — the checklist every asset clears

1. **The five-question gate** (Voice Bible Appendix A) — run on every headline, tweet, and email subject, not just long copy.
2. **Tense audit** — shipped vs **[Horizon]** explicitly; the client is "works today," the control plane is "roadmap, not built" until it is.
3. **The never-say list** (Narrative §3.4) — no "swarm"/"50 agents"/"orchestration" as pitch; no "EU AI Act requires"; no named-competitor villain; no exclamation marks, "blazing," "insanely," "game-changing."
4. **The competitor sentences** (Narrative §2) are the only public comparison register — concede what's true, state the deltas, never sneer.
5. **One hook per asset.** Act one assets carry the instrument; act two assets carry the wedge. An asset that tries to say both says neither.
6. **Every figure carries its source** in the canonical form (+91% review time — Viability §1.3; 87% distrust — SO 2025; 1M+ Copilot PRs — Octoverse 2025) so any claim survives a hostile comment thread.

---

## 6. Metrics and tripwires

Instrumented before L1 (Master Doc §17.1): **weekly active repos** (act-one health), **agent runs verified / merged per week** (act-two health), free→Pro conversion (target ≥2%), retention curve on beta cohorts. Campaign-level: HN front-page dwell and comment sentiment (read qualitatively — the objections we didn't pre-answer become FAQ additions), essay→waitlist conversion, comparison-page impressions for the category queries ("Conductor alternative Windows", "GitKraken alternative native").

Tripwires (inherited, restated): if design-partner teams won't pre-commit to paid pilots within two months of L2, revisit packaging (Master Doc §XX); if Cursor Origin ships local execution + provenance, re-plan positioning within a quarter (Narrative §2.2); if act-one retention is weak, L2 waits — trust is spent once.

---

## Self-gate (Part 1)

- Grounded: every channel, date rule, and figure cites Master Doc Part XIV/§4/§17, GTM Plan §5/§7, Narrative §§0–5, or the P2-12 spec in the Master Implementation Document v2.
- The wedge is a real, specified task (P2-12, M7, P0) with its contract steps reflected accurately, and is fenced to act two behind a "demonstrably works" gate.
- Honesty contract carried: [Horizon] discipline, audit-grade framing, 4–6 agent capacity, enemy-is-a-practice.
- No paid tactics smuggled in; anti-reference tone held (no growth-hack vocabulary, no hype register).
