# Mainguard — Go-To-Market

**How the business gets in front of people.** What stage we're in, how the launch is sequenced, how beta
users and design partners are recruited, which channels carry what, how the sales motion runs, how the
company is funded and incorporated, and the calendar that governs all of it.

| | |
|---|---|
| **The strategy this executes** — positioning, market, pricing, moat | [`BUSINESS.md`](BUSINESS.md) |
| **The copy each step uses** | [`GTM_Assets.md`](GTM_Assets.md) |
| **The research behind all three** | [`business/`](business/) — each section below links into it |

**Two rules outrank everything else here.** **Gates, not dates** — an act slips before an asset lies. And
the honesty contract ([`creative/Narrative.md`](creative/Narrative.md) §0): shipped is shipped, everything
else is **[Horizon]**, and no asset claims the agent platform works today.

---

## 1. Where we are: the stage rule

*Detail: [GTM Execution](business/go-to-market/GTM_Execution.md) §3b (the three sequencing changes, on the record).*

**We are not selling anything. We are recruiting people who will tell us the truth, and paying them in
early access and founding-user terms.** The Git client works and is worth installing today; the agent
control center is in integration and is not. Every message says both of those things out loud, because
the people we're writing to can tell the difference in about ten seconds — and the ones who can are
exactly the ones we want.

The July-2026 plan was written for a *launch*: two acts, a Show HN at each, and a discovery → demo →
pilot → close sales motion. The August-2026 beta lane narrowed it, and three sequencing changes are on
the record:

1. **The manifesto moves from launch-minus-one-week to first.** It is finished, it is pure argument, and
   it needs no demo — which makes it the only anchor available while the pipeline is in integration. The
   calendar then runs *toward* a launch rather than away from one.
2. **Both Show HN bodies stay in the drawer.** Show HN is one shot; spending it before there is a
   reproducible end-to-end run converts the best distribution event available into a mediocre one. **The
   gate is a working spawn → verify → review → merge, not a date.**
3. **Nothing is sold.** Paid pilots and the Team tier stay gated. Anyone offering to pay is deferred,
   with a reason — *"I'm not taking money for something in integration."* That refusal is worth more than
   the money, and it will be repeated for you.

**Honest framing of what a "yes" gets us at this stage:** a 30-minute call, notes, and possibly a person
who installs the client. That is the whole win.

---

## 2. The launch sequence (when the gate opens)

*Detail: [Launch Campaign Plan](business/go-to-market/marketing/LaunchCampaignPlan.md); [Master Market Document](business/go-to-market/Mainguard_Master_Market_Document_2026-07.md) Part XIV (playbooks 1–4).*

### 2.1 Phase 0 — Pre-launch (current)

1. **Build in public.** The product is inherently screenshot-able — graph canvas, five themes, 3-pane
   resolver. Short clips 2–3×/week: commit-graph rendering, partial staging, conflict resolver, then
   sandbox/verification previews as they land. Windows-native polish is itself novel content; the
   "beautiful devtool" genre is all macOS. **Mirror to LinkedIn** — Dutch B2B buyers live there, not on X.
2. **Waitlist + cohort invites.** Invite in cohorts of 10–20, fix what breaks, invite the next cohort.
3. **Publish the technical essays.** Each is HN/newsletter fodder *and* pre-answers launch-day objections:
   (a) *"The .git/index.lock problem: why agents corrupt your repo and how deterministic handle management
   fixes it"*; (b) *"Sandboxing coding agents on Windows/WSL2 with default-deny egress"*; (c)
   *"Test-verified agent PRs: making the merge queue re-verify what goes stale."*
4. **Open the Discord** at beta start; seed 20–50 core members. It doubles as the changelog feed. Don't
   over-build channels.
5. **Trust assets shipped before launch:** the security architecture doc, the telemetry policy (opt-in,
   published schema), the FSL licensing statement, and the network-transparency view on the roadmap.

### 2.2 Act one — the free Git client

**Gate:** client GA-ready and beta-cohort retention healthy.

- **Show HN**, plain title: *"Show HN: Mainguard – a fast, native Git GUI for Windows (free, no login)"*.
  Direct download link, no signup wall. Founder in the comments within the hour and all day, technical
  and non-defensive. Tue–Thu, 9am–12pm ET. Pre-empt the known objections — why another client, why FSL
  not MIT, why .NET/Avalonia, Electron benchmarks ready.
- **Same week:** Product Hunt (badge + backlink, ~10% of the energy), **Console.dev submission** (free,
  editorial; we meet every criterion), founder-disclosed posts in r/git, r/csharp, r/dotnet, plus the
  Dutch press wave (§4.4) pitched to land the same week — so the story is "launch," not "plans."
- **Goal:** installs, a retention curve, and a believable weekly-active-repos number. Not revenue.

### 2.3 Act two — the agent control plane (4–8 weeks later)

**Gate:** external-PR intake demonstrably works against two real bot-PR sources.

- **Second Show HN:** *"Show HN: Run coding agents in sandboxes that must pass your tests before you
  review their code."* This is the story for r/ClaudeAI, TLDR AI and creator outreach (founder-to-creator
  email, 60-second clip; the hook is "agents that must pass tests before you see the PR"). Organic first;
  consider one $2–5K mid-tier sponsorship only after the organic message proves out.
- **Position explicitly as "Conductor for Windows — with verification."**
- Ship Pro at or shortly after this act; design partners convert to paid logos.
- Dutch layer: AG Connect (enterprise/governance angle) + Silicon Canals (startup angle).

### 2.4 Phase 3 — Teams & governance (2027, post-PMF signal)

Land-and-expand from act-two seats; the audit dashboard and merge-queue analytics are the expansion
product. **Sell $50+/seat only once RBAC/audit/SIEM exist.** Cloud worktrees private beta within ~2
quarters of desktop GA.

---

## 3. The founding-user program & the beta motion

*Detail: [Founding User Program](business/go-to-market/Founding_User_Program_2026-08.md); [Master Market Document](business/go-to-market/Mainguard_Master_Market_Document_2026-07.md) Part XIII (design-partner program).*

### 3.1 Why it's defined

The site already promises *"founding-member terms"* and nowhere defines them. **A vague promise does no
work now and creates an obligation later** — every reader imagines their own number, and whatever you
choose disappoints one group, who will be your loudest early users, in a small country, talking to each
other. A specific promise gives outreach a concrete thing to trade for a conversation and costs nothing
today, because there is nothing to charge for yet.

Design constraints: **honourable at any future price** (define it as a relationship to the eventual price,
not an absolute number) · **rewards the behaviour we want** (feedback, not an email address) · **doesn't
compromise the free client** (permanently free, no account — a positioning commitment, not a tier) ·
**small enough to keep** (if 500 people take it, we must still be glad we offered it).

### 3.2 Tier 1 — Founding User (the open offer)

**Who:** anyone who joins the waitlist and, when the beta lands, installs it and sends back one piece of
substantive feedback — a bug, an annoyance, a "this doesn't fit how we work." One paragraph is enough. The
bar is *engagement*, not volume.

**What they get:** **50% off Pro for its first two years**, from whenever Pro starts charging, at whatever
Pro costs then · **locked per person, not per plan** — if the price rises, the discount rides the new
price · **first access to every build** before public release · **a named credit in the release notes**
for anyone whose report changes the product, if they want it.

**Why 50%/two years and not "lifetime free":** lifetime free costs the entire cohort's revenue exactly
when early revenue is proof of a business, and it selects for people who want free software rather than
people who want *this* software. Two years at half price is generous enough to be worth having, cheap
enough to keep, and converts at a natural moment.

**The alternatives, weighed:** *lifetime 30%* — simplest sentence, but a permanent haircut on the
best-disposed cohort, forever. *First year free* — strongest hook, but zero revenue from early believers
precisely when early revenue is the proof point, and it attracts free-year people. *Perpetual founding
price* — the most loved option among developers and costs nothing today, but unbounded over a long horizon
and hard to model.

**Status: this decision is still open.** Everything downstream depends on it, and the number must be
identical in the outreach email, the waitlist page and the beta welcome mail.

### 3.3 Tier 2 — Design Partner (invitation only, cap 8 teams)

**Who:** a team that runs Mainguard against a real repository and commits to a recurring conversation —
30 minutes a month for six months. Invited from the Tier A target list, or from anyone who turns out to be
unusually useful.

**What they get:** Pro free for the whole team for one year from GA · 50% off the following year, then
standard pricing · a direct line — a personal address, not a support queue · **real influence on the
roadmap, stated honestly** (their problems get worked on first; not a veto, not a guarantee, and say so) ·
an **optional** public reference — logo, quote or joint post, **never a condition**. A design partner whose
procurement forbids being named is still a design partner.

**What we get:** the recurring conversation, permission to watch them work, and — for the ones who can — a
reference that unlocks the next ten companies. In the Netherlands one named Dutch team is worth more than
any amount of copy.

**Why cap at 8:** eight monthly conversations is four hours a month, the honest ceiling alongside
development. A design-partner program you cannot service is worse than none, because the failure is visible
to exactly the people whose opinion matters most.

**Qualify on:** *urgency* (already duct-taping worktrees + tmux), *capacity* (a champion who'll do the
calls), *representativeness* (one .NET enterprise shop, one AI-forward startup, one agency).

### 3.4 What neither tier gets — say it at sign-up

**No SLA, no support commitment, no uptime promise** — it's a beta. **No guarantee any specific feature
ships** — roadmap influence is real; roadmap authority isn't. **No promise of a date** — the pipeline is
finished when it's finished. Saying this up front costs almost no one and saves every awkward conversation
later.

### 3.5 Site and schema changes this requires

- Replace the waitlist lede — the current line makes a dated promise ("this fall") and an undefined one
  ("founding-member terms"). Copy in [`GTM_Assets.md`](GTM_Assets.md) §6.
- Replace the success state, which currently asks for nothing while the person is at their most willing.
- **Schema:** add an optional *"what are you running today"* free-text field (the richest qualitative
  source, and people fill it in when asked at the right moment — the existing `message` column can carry
  it with no migration) and a **`founding_status`** column so we can mark who actually sent feedback and
  therefore earned the discount. A one-line `ALTER TABLE`. **Do both before the campaign starts** —
  retrofitting provenance onto a list is the exact problem this product is about.
- **Keep a single list of who was promised what, with dates.** In a year this is the only record that
  matters, and it is the one thing nobody ever writes down at the time.

### 3.6 Discovery interviews (the highest-information work per hour)

**Target 25.** Sources: r/ClaudeAI and r/ChatGPTCoding power users posting multi-agent workflows,
build-in-public followers, .NET/C# Discords, local meetups (dotNed, Kennispark), and the target list.

**Script spine — ask about the past, not the future:** How many agent sessions do you run in parallel
today? Walk me through the last time an agent broke something, or two agents collided. How do you review
agent output — what do you actually read? Have you merged agent code you didn't fully review? What
happened? Who in your org asks "did AI write this"? What do you pay for today?

**Disqualifying signal to respect:** if interviewees consistently say the first-party desktop apps are
"good enough" for review, the review-cockpit wedge needs sharpening before launch.

---

## 4. Channels — what each one carries

*Detail: [Beta Publishing Calendar](business/go-to-market/marketing/Beta_Publishing_Calendar_2026-08.md) (the six weeks, per-channel); [Paid Media Plan](business/go-to-market/sales/Mainguard_Paid_Media_Plan.md) (per-channel budgets, creative, kill criteria).*

### 4.1 Ranked by expected yield

1. **Hacker News** (two acts + the essays) — the GitButler/Graphite/Supabase evidence is unambiguous.
   Held in reserve until the gate (§1).
2. **Build-in-public X + LinkedIn mirror** + waitlist cohorts.
3. **r/ClaudeAI, r/ChatGPTCoding, r/git, r/csharp** — 90/10 rule, founder-disclosed, always.
4. **Console.dev** (free, high-intent) → Pointer/TLDR later (paid, ~$3.5K+ per placement).
5. **YouTube creators** — organic outreach at act two.
6. **Discord** — retention, not acquisition.
7. **NL press + events** — timed to the acts, never standalone.
8. **Comparison-page SEO** — "Mainguard vs Conductor / Kepler / Copilot app / MergeLoom" pages, to contest
   the category vocabulary before a competitor's SEO wall owns it.

### 4.2 The current six-week calendar (LinkedIn-primary)

While the pipeline is in integration: LinkedIn is primary, the manifesto anchors week 1, and each
subsequent week carries one substantial piece. What "posting" means with no demo: argument, mechanism, and
honest build-log — never a mockup presented as a product. Measure replies and conversations started;
ignore impressions.

### 4.3 Paid media — ground rules before any euro is spent

1. **Paid is an amplifier, not the engine.** Organic outranks every paid option, and the evidence base is
   organic. Paid spend only ever amplifies a message that already proved itself.
2. **Ads obey the honesty contract.** An ad running during act one claims only the shipped client.
   Verification claims run only after act two ships and each sentence is true. No ad ever says "swarm,"
   "50 agents," or leads with "orchestration."
3. **Measurement respects the trust posture.** The free client has no login and no default telemetry — so
   **there is no user-level attribution, by design**. We measure with per-channel landing paths and UTMs,
   per-placement promo URLs, and one self-reported "where did you hear about Mainguard?" question.
   Self-reported attribution is the primary source of truth; platform dashboards are directional only.
   **This is a feature we say out loud: no tracking pixels in the app, ever.**
4. **Budget scale is honest.** Pre-revenue, funded by WBSO and later VFF. Phased: ≤ €1.5K at act one,
   €5–9K at act two, €8–15K/month at scale only if funnel metrics hold. **Every phase has a kill
   criterion.**

The funnel: `impression → landing page visit → download (no signup) → activation → retention → waitlist →
Pro`. Target LP conversion ≥ 8% on intent channels (search), ≥ 2% on interrupt channels (Reddit/X).
**Activation = weekly active repos** — opened a real repo and performed ≥ 1 write operation in week one;
per-channel activation rate is the channel-quality signal.

Channel notes: HN is organic-only (it sells no ads — say so honestly). LinkedIn paid is deferred by the
do-not-sell-to-Priya rule. Dev newsletters, YouTube/podcast sponsorships, Google Search on competitor and
intent keywords, and Reddit are the live candidates.

### 4.4 The Dutch media list (in pitch order)

| Outlet | What it is | Angle |
|---|---|---|
| **Tweakers** | NL's biggest tech community; devs read it daily | Product + local-founder angle at act one |
| **AG Connect** | Dutch IT-professional trade press | The enterprise/governance story at act two |
| **Silicon Canals** | English-language European startup news | Funding / design-partner milestones |
| **Emerce** | Dutch digital business press | Business angle once first paying teams exist |
| **MT/Sprout** | Dutch startup/scale-up magazine | Founder story + subsidy-stack angle |
| **IO+ / Innovation Origins** | Covers East-NL innovation | "Twente builds a global devtool" — an easy yes |
| **Tubantia / 1Twente / U-Today** | Regional + campus media | Local-hero coverage; cheap credibility for outreach and hiring |

Per outlet: find the journalist who wrote the nearest story, send a five-sentence personal pitch,
embargo-free, screenshots + video attached, offer a 15-minute call. Dutch pitches to Dutch outlets.

### 4.5 Community & events

- **dotNed** meetups and dotNed Saturday, **.NET Zuid** — attend now; propose a pure-tech talk ("Building a
  60fps native Git client in Avalonia") with zero pitch; the .NET community will carry it.
- **Novel-T / Incubase / Kennispark** events — register with Novel-T and use their demo days for angel and
  Oost NL contact.
- **Techorama Netherlands, Utrecht — 26–28 Oct 2026** ("Medieval Edition"): 26 Oct is the workshop day at
  Van der Valk; the conference days are 27–28 Oct at Kinepolis/Jaarbeurs. Six targets are partners and in
  one building. Submit via Sessionize; otherwise attend with stickers and laptop demos.
- **Hacktoberfest at Arcady's Zwolle office — 8 and 15 Oct 2026.** An hour away, no introduction needed.
- **Bitbash**, Info Support HQ Veenendaal, **22–23 Jan 2027** — free, and a talk slot to aim at.
- **NL MVP circuit** — demo to three Dutch .NET MVPs pre-launch; the ".NET flagship app" story earns free
  advocacy.
- *TNW Conference is defunct* (events and media shut down end of September 2025). Remove it from all plans.

**Dutch-market nuances:** LinkedIn is disproportionately effective for B2B in NL. Product copy stays
English; sales conversations in Dutch close Tier-1/2 companies faster. "Mainguard is proof you can build
world-class native UI in .NET" is itself a marketing asset in this community.

---

## 5. Sales motion & sourcing

*Detail: [Company Sourcing Playbook](business/go-to-market/sales/Mainguard_Company_Sourcing_Playbook.md) (six plays); [Beta Outreach Sequences](business/go-to-market/sales/Beta_Outreach_Sequences_2026-08.md); [Outreach & Pitch Kit](business/go-to-market/sales/Mainguard_Outreach_And_Pitch_Kit.md) (the later pilot-to-close motion).*

### 5.1 The staged motion

**Now:** design-partner and beta-feedback recruitment (§3). **After act two:** paid pilots open.
**2027:** Team-tier selling opens, ending the do-not-sell-to-Priya rule.

### 5.2 Sourcing plays, ranked by what actually worked

1. **Microsoft conference sponsor lists — confirmed, by a wide margin.** Techorama NL/BE and VISUG partner
   pages produced ten of the twelve best targets in two fetches. **Still unmined:** Azure Lowlands,
   devNetNoord (Groningen), dotNed Saturday, Update Conference Prague. Work these next.
2. **Dutch/Belgian .NET consultancies, 20–80 people.** Highest yield per hour; nearly every Tier A entry.
3. **Govtech ISVs.** DigiD/BIO obligations make the provenance conversation trivial to start.
4. **GitHub org signals** — `CLAUDE.md` / `AGENTS.md` / `copilot-instructions` / agent-bot PRs are adoption
   evidence you can grep for.
5. **Job postings** — agent adoption is advertised in the wild; .NET stacks are advertised in every ad.
6. **Communities and inbound** — the waitlist is a sourcing engine once the "what are you running today"
   field exists (§3.5).

**A correction worth keeping: the original tier list aimed one layer too high.** Topicus, Thales, Nedap,
Info Support, ilionx and Sopra Steria are all 500–2,250 people — far outside the 10–100 developer band.
The companies that actually match the ICP were found via Microsoft conference sponsor lists: 30–60-person
Dutch and Belgian .NET consultancies and govtech ISVs. **Healthcare ISVs largely failed and shouldn't be
repeated** — the compliance half holds but the .NET half doesn't, and not one showed any AI-coding-agent
evidence.

**Scoring model** (A agent adoption · B Windows/.NET · C compliance pressure · R reachability · S size
fit, each 0–2). The scored, contactable list with named contacts and verification warnings is
[`GTM_Assets.md`](GTM_Assets.md) §7.

### 5.3 Outreach cadence and rules

**Three rules that outrank every template:**

1. **Never claim the agent platform is shipped.** Shipped: the Git client. Everything else is marked, in
   the email, as not-yet.
2. **Never send a template unedited.** Every target has a specific public artefact — a repo, a post, a job
   ad, a tech radar. If you can't name theirs in the first sentence, you're not ready to write to them.
3. **Volume is ten a week, not a hundred.** At a few hours a week the constraint is follow-through, not
   send volume.

**Four openers**, chosen by their situation: **peer** (they publish code or artefacts about agents — the
highest reply rate, and where two apply, peer wins), **quote-back** (they published your argument in their
own words), **greenfield** (regulated, no agent tooling yet), **neighbour** (within an hour's drive).
**Follow-ups: two, then stop.** Bodies in [`GTM_Assets.md`](GTM_Assets.md) §5.

**The call:** 30 minutes, no demo. Listen for what they run, what broke, and what they'd fix. Anyone who
wants to talk about price is politely deferred with the founding-user answer (§3.2).

### 5.4 The one objection that decides everything in NL

**EU hosting and data residency is the qualifying criterion, not a nice-to-have.** It appeared
independently at every serious target — Topicus (*"Data mag niet naar Big Tech"*), Adyen (on-prem LLMs to
avoid prompt leakage), Blis Digital's own FAQ (under the AVG, an AI code tool is an external processor) —
and it's structurally unavoidable at the Wpg/BIO, NEN 7510, ISO 13485 and defence targets. Local-first,
BYOK-in-the-OS-keyring and default-deny egress are the direct answer to all of it. **Lead with it in the
Netherlands.** Being Dutch, local-first and source-available is the leverage, and it is the one axis where
no Mac-only, cloud-first, US-funded competitor can follow.

---

## 6. Funding

*Detail: [Grant Applications NL](business/go-to-market/Mainguard_Grant_Applications_NL.md); [Master Market Document](business/go-to-market/Mainguard_Master_Market_Document_2026-07.md) Part X (the full fund and angel tables); [Advisor Pitch brief](business/go-to-market/Advisor_Pitch_August_2026.md).*

### 6.1 Track A — non-dilutive (start immediately; sequence matters)

**Locked sequence:** WBSO now → VFF once an investor intent letter exists → MIT April 2027 → Innovatiebox
at first profit → Innovatiekrediet/EIC at scale-up.

**Step 0 — prerequisite: a BV + KVK registration** (§7). WBSO for a BV requires payroll; while Mainguard
is a sole proprietorship the self-employed variant applies.

**1 · WBSO — the R&D wage-cost credit. File first; it's the foundation.** For companies with payroll: a
payroll-tax reduction of **36% (starters: 50%) of the first €391,020 of R&D wages**, 16% above that. For
self-employed doing ≥ 500 R&D hours/year: a fixed income-tax deduction of **€15,979 + €7,996 starter
top-up**. Mainguard's daemon/sandbox/merge-queue work is textbook **S&O** (*speur- en
ontwikkelingswerk*, the Dutch statutory term for qualifying R&D) — technically new software
development with demonstrable bottlenecks. *Walkthrough:* get eHerkenning eH3 (or DigiD while zzp) → write
the project description (technical bottlenecks + why technically new *to you*) → submit via RVO's eLoket
**before the month in which R&D starts** → keep an hours ledger from day one (RVO audits) → file realized
hours by 31 March of the following year. Free help: Novel-T's partner subsidy advisors. **Multiplier:** a
granted WBSO declaration is the entry ticket to the **Innovatiebox** — 9% corporate tax on profits from
self-developed software instead of 19%/25.8%. Irrelevant pre-profit, but structure the IP inside the BV now
so it's available later. *Draft project descriptions:* [`GTM_Assets.md`](GTM_Assets.md) §8.

**2 · VFF (Vroegefasefinanciering) — the €50–350k pre-seed loan.** Covers an "early-phase plan" validating
product/market; **€50k–€350k at 7.19%**, up to 100% of plan costs, repayable, **requires a letter of intent
from a follow-on investor** for at least the same amount, the trajectory must not have started, completable
within two years. **Overijssel quirk: apply via Oost NL, the regional financier, not RVO directly.** Window
open all year. *The play:* it pairs perfectly with the design-partner phase — an angel or VC intent letter
unlocks state money that doubles the runway *before* a priced round.

**3 · MIT Haalbaarheidsproject — €20k, once a year, first-come.** 35% of feasibility-study costs, max
€20,000; the Overijssel window opens on **one day in April**, first-come-first-served, typically
oversubscribed on day one with a notarial lottery. **Target the April 2027 round with a ready-on-day-one
application** (e.g. "feasibility of cloud-hosted verification worktrees for EU enterprises"). Reminder:
February 2027. A subsidy advisor can hold the pen.

**4 · Innovatiekrediet — the development loan (later).** For technical-development projects with
considerable technological risk and excellent market perspective; covers up to 45% (small company) of
project costs, requires **55% co-financing** and a first pledge on project assets. Relevant at the
cloud-worktrees/enterprise-buildout stage, not now. Do the mandatory RVO quick-scan first.

**5 · EU level (2027+).** EIC Accelerator (grant ≤ €2.5M + equity) once there's traction; Horizon Europe
via UT partnerships opportunistically. High effort — only with a grant writer.

**Treat the funding calendar like a release calendar.** Grant windows are rigid; missing one costs a year.

### 6.2 Track B — equity

**The trigger:** 3–5 active design-partner teams + a retention curve + verified-merges growth, **or** a
competitive forcing event (Cursor Origin shipping local execution + provenance).

**Dutch/Benelux funds, ranked by thesis fit:**

| Fund | Base | Stage / check | Why they fit |
|---|---|---|---|
| **NP-Hard Ventures** | Amsterdam | Pre-seed/seed; €25M Fund II | *The* on-thesis fund: core technology infrastructure, dev tools. Partner Micha Hernandez van Leuffen founded Wercker (CI, sold to Oracle) — he *is* the ICP |
| **henQ** | Amsterdam | Pre-seed–A; €1–10M initial; Fund V €67.6M | B2B software, "odd ones out," unfashionable markets; **funded Mendix from €250k** — the canonical Dutch enterprise-software bet |
| **Curiosity VC** | Amsterdam | (Pre-)seed; €50M fund | AI-first B2B with an explicit "Software Development" vertical; adjacent portfolio |
| **Volta Ventures** | Benelux | (Pre-)seed; €300k–2M initial | Benelux B2B software at pre-revenue — the natural first institutional check if raising early |
| **Newion** | Amsterdam | Seed/A; Fund IV €130M | Pure B2B software; better at seed-with-revenue than pre-seed |
| **Peak** | Amsterdam | Seed; SaaS/platform | Generalist; warm intros easy via the Amsterdam ecosystem |
| **Cottonwood Technology Fund** | **Enschede (UT campus)** | (Pre-)seed; €1–2M first tickets | Deeptech/patent-first thesis is an imperfect fit, but they are *on campus*, know every Twente angel, and co-invest with Oost NL — take the coffee regardless |
| **Oost NL / Twente Technology Fund** | Apeldoorn / Twente | Regional development capital; TTF covers ICT | Co-invests with private leads in Overijssel; Oost NL is also the VFF-regional counter |

**Angels:** BAN Nederland (the umbrella of Dutch angel networks) · Leapfunder (convertible notes, popular
for Dutch pre-seed) · **Golden Egg Check** (startup–investor matching, based in Enschede) · Arches Capital
(the syndicate behind CodeSandbox and PESCHECK). **Ex-founders of Twente/UT companies are the best
single-angel profile** — reachable via Novel-T and Golden Egg Check.

**Antler Amsterdam: probably skip** — the residency terms are expensive for a product this far along.

**Foreign funds for the real seed:** the investor logic in the category (GitButler↔a16z, Conductor↔Spark
/Matrix, Greptile↔Benchmark) implies the eventual competitive seed is led from London/Berlin/US. Dutch
funds are the warm, reachable start — and the intent-letter source for VFF.

**Running the raise:** build a ~30-name list (5 dream / 15 realistic / 10 backup) → warm paths first →
materials are the deck, a 90-second product video, and a data room (metrics dashboard, design-partner
references, FSL/security docs) → ~6 weeks of relationship-building (report-back emails with metric updates)
before the ask → then a 3–4-week sprint. **Anchor:** €750k–1.5M pre-seed (grants extend it ~40%) *or* skip
to a $2–4M seed on launch traction. **Decide after launch data, not before** — and saying so signals
discipline rather than indecision.

### 6.3 The advisor / stakeholder loop

**The pattern that builds an advocate: ask → act on the advice → report back what happened.** The
report-back is the step most founders skip, and the one that creates an emotional stake.

**Running a session.** Ask in advance for a dedicated hour — never pitch in a social setting. Don't open
with "will you be my advisor?"; ask for judgment on 2–3 *named* decisions (sequencing, first paying
customers, bootstrap vs raise) and let the role emerge. Structure it ~70/30, their input over your
context. Demo, then the questions. **Close with exactly one ask** — don't stack them.

**Rank the outcomes honestly.** (1) A working relationship with a reason to talk again in a month — the
win condition. (2) Their engineering team as a design partner: concrete, bounded, useful to *them*, and
it produces evidence instead of promises. (3) A formal advisor role, later. (4) **Money is explicitly not
a goal in a first conversation** — *"ask for money, get advice; ask for advice, get money twice."*

**Write down** their objections (they preview every investor objection you'll ever hear), names they drop
— never ask for intros in meeting one, let them offer — and their own workflow pain.

**Afterwards:** thank-you within 48h naming the single most useful thing they said → report back in 2–4
weeks ("you said X, I did X, here's what happened") plus one new question → repeat monthly.

**Formalizing, once the loop has run 2–3 times — FAST norms** (fi.co/fast; one page, no lawyers). At
pre-launch stage a standard advisor is **0.25%**, an expert advisor up to **1.0%**; post-launch, 0.20% /
0.80%. Terms are **2-year monthly vesting with a 3-month cliff** — if it fizzles in the first quarter, no
equity moved and nothing is awkward. That cliff is exactly why formalizing is *safer* for a
family-adjacent relationship, not riskier.

**Mistakes to avoid:** overselling product state (honesty about built-vs-planned *is* the credibility
play — the Git client being real and fast does the impressing); twenty minutes of architecture when they
care about customers, moat and you; asking for an NDA; skipping the follow-up.

---

## 7. Company setup (Netherlands)

*Detail: [Master Market Document](business/go-to-market/Mainguard_Master_Market_Document_2026-07.md) Parts IX, XI, XII, XVIII and XIX.*

### 7.1 Entity

- **Costs:** incorporating a single BV ≈ **€500–900 all-in** (notary €400–800 + KVK ~€75–82); online
  notaries are the cheap path. Minimum share capital €0.01.
- **Structure:** the standard Dutch startup setup is **Holding BV → Werk-BV** — the holding owns your
  shares, IP can sit at holding level, operating risk sits in the werk-BV, and dividends flow tax-free
  holding-ward under the participation exemption. Roughly 2× notary fees. **Do it at incorporation;
  restructuring later is expensive.**
- **DGA salary:** the *gebruikelijk loon* norm is **€58,000**, but startups and loss-making BVs may pay down
  to statutory minimum wage for up to three years with substantiation.
- **Why BV over eenmanszaak**, despite eenmanszaak being cheaper below ~€70–80k profit: investors and
  VFF/Innovatiekrediet effectively require it; limited liability matters for a tool that executes
  AI-written code on customer machines; the Innovatiebox only exists inside a BV; and employee options need
  shares. **Incorporate before launch**, put the IP in, and sign an IP assignment from yourself to the BV.

### 7.2 Selling software from NL: VAT and merchant of record

Selling digital services to EU consumers means charging **each buyer's local VAT**; B2B in-EU
reverse-charges. Self-managing this on Stripe (2.9% + $0.30 + Stripe Tax + OSS filings) is doable but costs
founder time; a **Merchant of Record** (Paddle or Lemon Squeezy, ~5% + $0.50) becomes the legal seller and
handles global VAT and disputes. **Recommendation:** launch Pro on a MoR — Paddle for billing-complexity
headroom (perpetual-fallback licenses, team seats), Lemon Squeezy if integration speed wins. Revisit
Stripe-direct above ~$30K MRR, when the MoR fee exceeds a part-time accountant. Enterprise deals invoice
directly from the BV regardless.

**GDPR:** local-first is a structural advantage — the product processes no customer code server-side. Ship a
privacy policy, the published telemetry schema, a DPA template for the later cloud tier, and EU data
residency for any hosted component. **Say all of it on the website**; it is a differentiator in EU
procurement.

### 7.3 Setup checklist, in order

1. Trademark screen on the name (Benelux + EU word mark, ~€250–1,000; Nice classes 9 + 42).
2. Online notary → Holding BV + Werk-BV; KVK + RSIN/VAT numbers arrive with registration.
3. Business bank account + accountant (fixed-fee startup packages).
4. IP assignment (you → werk-BV), DGA employment agreement, minimum-wage salary election.
5. eHerkenning eH3 → **WBSO application** (§6.1).
6. MoR account + EUR pricing page; Terms of Service and Privacy Policy **with entity and jurisdiction
   stated**.

### 7.4 Hiring & talent (Twente)

- **Salaries (gross/yr):** medior developer €45–65k; senior €70–95k. Twente runs **€5–10k below the
  Randstad** for the same seniority — senior ~€70–85k here vs €85–100k in Amsterdam/Utrecht. Budget ~+30%
  employer costs on top.
- **Interns and afstudeerders — the cheapest strong pipeline.** UT and Saxion place 5-month full-time
  internships (Sep–Jan / Feb–Jul) at ~€250–500/month. A native Avalonia Git client with agent sandboxes is
  a *magnet* graduation project. **Post two afstudeeropdrachten for the February 2027 block** — pitch to
  programme coordinators in October–November.
- **Stock options:** today's Dutch regime taxes options as wage income when shares become tradable —
  painful. The new startup stock-option law (targeted **1 Jan 2027**) taxes only 65% of the gain and defers
  taxation to the moment shares are sold, for young non-listed companies. **Promise ESOP percentages now
  (e.g. a 10% pool); issue under the new regime in 2027**, via a STAK or agreements drafted by a startup
  lawyer, not the notary's default.
- **First hires:** with WBSO's 50% starter rate, a €55k medior costs the BV net ≈ €40k-equivalent in year
  one — **the subsidy stack effectively funds hire #1.** International hires recruited from abroad can use
  the 30%-ruling.
- **Where to find them:** UT/Saxion career events, the dotNed meetup (offer to host one at Kennispark), and
  the build-in-public feed itself — devtool startups recruit their users.

### 7.5 The Enschede/Twente base

**Novel-T** is the Twente innovation agency; its startup programs are **free of charge**, and the **START
program** is a 6-week validation program run at **Incubase**, which also provides cheap co-working and
on-tap subsidy advice from partners. **Register with Novel-T** — it is the free front door to Twente's
grant advisors, angels and corporate network. **Kennispark Twente**'s 2026–2035 strategy targets growth to
~700 companies and 16,500 jobs, with a €100M national campus-innovation budget flowing largely into existing
science parks — a rising tide for anything based there. **Local track record:** CodeSandbox is
Enschede-origin, and Cottonwood runs its European HQ on the UT campus. Use that story with local press,
Novel-T and angels; it primes them to believe it can happen twice.

### 7.6 EU expansion & the sovereignty tailwind

**Digital sovereignty is a tailwind we're born into.** >80% of Europe's digital infrastructure is imported
and US providers hold ~85% of the EU cloud market; the **EuroStack** movement and "Buy European"
procurement proposals moved into mainstream policy in 2026, and well-governed auditable, self-hostable
tools are explicitly favoured. Mainguard is structurally the compliant answer: **European vendor,
local-first, source-available daemon, default-deny egress, BYO keys.** Actions: a "European digital
sovereignty" page on the website; listings in European-alternative directories; and use it as the wedge for
Dutch and German public-sector-adjacent ISVs that US cloud tools can't enter.

**Expansion sequencing:** NL (design partners, logos, subsidies) → **DACH** (the world's densest
Windows/.NET-enterprise + compliance-culture market; Germany is 20 minutes from Enschede) → Nordics/UK → US
enterprise with the seed round. **Don't localize the product; localize the sales conversations and case
studies.** First DACH motion: the same segment map for Germany once 2–3 Dutch logos exist.

### 7.7 Dutch playbooks worth pattern-matching

**Mendix** — €250k from henQ → Dutch enterprise logos → US capital for scale → $730M Siemens exit. The
Dutch enterprise-software playbook: local seed, Dutch logos first, US capital for scale. **Framer** — a
world-class Dutch product team mis-aimed the wedge for six years and still won by moving one step toward
where the money is; our equivalent move (orchestration → verification) is already made on paper — execute
it. **Weaviate** — open/auditable core + a global developer community + cloud monetization; validates the
FSL-daemon + free-GUI funnel + cloud sequence. **CodeSandbox** — Enschede can birth a global devtool.

**The common thread: none of them won in the Dutch market — they won *from* it.**

---

## 8. The calendar — gates before dates

*Detail: [GTM Execution](business/go-to-market/GTM_Execution.md) §4; [Master Market Document](business/go-to-market/Mainguard_Master_Market_Document_2026-07.md) Part XXI.*

| Gate | Fundraising | Marketing | Sales |
|---|---|---|---|
| **Now** (beta lane) | WBSO filing (before-the-month rule); BV incorporation; VFF plan aligned to the phases | Manifesto first; six-week LinkedIn-primary calendar; essays publish as ready; **Show HN held in reserve** | Target list worked at ten sends/week; ~25 discovery interviews; design partners recruited. **Nothing sold** |
| **Client GA-ready** → **act one** | Traction slide starts filling (no empty-slot presentations) | Show HN act one + first-hour FAQ; launch-week satellites; press kit variant 1 | Paid media phase 1 (≤ €1.5K); interviews continue |
| **Interlude** | Board-update cadence begins | One substantial piece per two weeks; release notes per the Voice Bible | 3–5 design-partner teams qualified (6-month, time-boxed) |
| **External-PR intake works** → **act two** | Seed conversations on act-two evidence | Manifesto re-run at L2 − 3–5 days; Show HN act two; wedge campaign; paid phase 2 (€5–9K) | Paid pilots open; compliance persona still nurture-only |
| **Governance tier ships; beta unit economics match the model** | MIT feasibility (April 2027 round); Innovatiebox after the first WBSO year | Scale phase (€8–15K/mo) if funnel metrics hold | Team-tier selling opens; NL → DACH |

**Fixed dates inside that sequence:** Techorama NL 26–28 Oct 2026 · Hacktoberfest at Arcady 8 + 15 Oct 2026
· Bitbash 22–23 Jan 2027 · MIT application ready February 2027 for the April window · new stock-option
regime targeted 1 Jan 2027.

**Tripwires that freeze a lane:** the paid-pilot pre-commit tripwire (§3 and
[`BUSINESS.md`](BUSINESS.md) §7.6), each paid-media phase's kill criteria (§4.3), and the erosion dashboard
([`BUSINESS.md`](BUSINESS.md) §8.4).

---

## 9. Working-list hygiene

- **Re-verify before every send.** Job ads expire, people move, companies merge — three did while the
  target research ran. Quotes in the asset list are dated 2026-08-20.
- **Never assert an `[inferred]` or ⚠️ item to the company.** Ask about it instead; it makes a better
  question than a claim.
- **Track outcomes in one place** — company, score, contact, date sent, reply, call held, what they said.
  **The interview notes are the real asset; the list is just the index.**
