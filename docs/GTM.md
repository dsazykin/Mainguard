# Mainguard — Go-To-Market

**How the business gets in front of people.** What stage we're in, how the launch is sequenced, how beta
users and design partners are recruited, which channels carry what, how the sales motion runs, when to
raise, and the calendar that governs all of it.

| | |
|---|---|
| **The strategy this executes** — positioning, market, pricing, moat | [`BUSINESS.md`](BUSINESS.md) |
| **The copy each step uses** | [`GTM_Assets.md`](GTM_Assets.md) |
| **Dutch lookups** — grant schemes, funds, incorporating, hiring, press | [`business/NL_Operations.md`](business/NL_Operations.md) |
| **The research behind all four** | [`business/`](business/) — each section below links into it |

**Two rules outrank everything else here.** **Gates, not dates** — an act slips before an asset lies. And
the honesty contract ([`creative/Narrative.md`](creative/Narrative.md) §0): shipped is shipped, anything
unbuilt is **[Horizon]**, and **no asset claims the agent platform is ready for someone else to rely on
until the end-to-end run is reproducible.** Most of the pipeline is implemented
([`STATUS.md`](STATUS.md)); that is not the same as it being ready, and the copy must not blur the two.

---

## 1. Where we are: the stage rule

*Detail: [GTM Execution](business/go-to-market/GTM_Execution.md) §3b.*

**We are not selling anything. We are recruiting people who will tell us the truth, and paying them in
early access and founding-user terms.** The Git client works and is worth installing today. The agent
control center is largely *implemented* but not yet *dependable* — the full leg is a manual runbook and
several surfaces are still shells — so it is not something to hand someone and walk away from. Every message says both of those things out loud, because
the people we're writing to can tell the difference in about ten seconds — and the ones who can are
exactly the ones we want.

Three rules govern this stage:

1. **The manifesto is the anchor, not the launch.** It is finished, it is pure argument, and it needs no
   demo — which makes it the only substantial thing we can publish while the pipeline is in integration.
   The calendar runs *toward* a launch rather than away from one.
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
  Dutch press wave ([`business/NL_Operations.md`](business/NL_Operations.md) §4) pitched to land the same week — so the story is "launch," not "plans."
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

### 4.4 The Dutch layer

Dutch press and the .NET event circuit are timed to the acts, never run standalone — LinkedIn is
disproportionately effective for B2B here, and sales conversations in Dutch close local companies faster
even though product copy stays English. **The outlet list, the per-outlet angle and the event calendar
are in [`business/NL_Operations.md`](business/NL_Operations.md) §4.**

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

*Scheme parameters, deadlines, and the Dutch fund and angel tables are in
[`business/NL_Operations.md`](business/NL_Operations.md) §§1–2 — this section is the sequence and the
triggers.*

### 6.1 Non-dilutive first — the sequence

**WBSO now → VFF once an investor intent letter exists → MIT in April 2027 → Innovatiebox at first
profit → Innovatiekrediet or EIC at scale-up.** This runs regardless of whether a round happens, and
between them the schemes return roughly a third to a half of early engineering wages before any dilution.

Two dependencies decide the timing. **WBSO for a company with payroll requires the BV**, so incorporation
gates the whole stack; and **VFF requires a letter of intent from a follow-on investor** for at least the
loan amount, which is what makes it pair naturally with the design-partner phase — an angel's intent
letter unlocks state money that roughly doubles the runway *before* a priced round.

**Treat the funding calendar like a release calendar.** The windows are rigid and missing one costs a
year: WBSO must be filed before the month the work starts, and MIT opens on a single day in April.

### 6.2 Equity — the trigger and the shape

**The trigger:** 3–5 active design-partner teams + a retention curve + verified-merges growth, **or** a
competitive forcing event (Cursor Origin shipping local execution + provenance).

**Where the money comes from:** the Dutch and Benelux funds are the warm, reachable start and the
intent-letter source for VFF. The eventual competitive seed is more likely led from London, Berlin or the
US — that is the pattern across this category.

**Running the raise:** build a ~30-name list (5 dream / 15 realistic / 10 backup) → warm paths first →
materials are the deck, a 90-second product video, and a data room (metrics dashboard, design-partner
references, FSL/security docs) → ~6 weeks of relationship-building (report-back emails with metric updates)
before the ask → then a 3–4-week sprint. **Anchor:** €750k–1.5M pre-seed (grants extend it ~40%) *or* skip
to a $2–4M seed on launch traction. **Decide after launch data, not before** — and saying so signals
discipline rather than indecision.

### 6.3 The advisor / stakeholder loop

**The loop: ask → act on the advice → report back what happened.** Ask for judgment on 2–3 *named*
decisions rather than for a relationship; close each session with exactly one ask; report back in 2–4
weeks with what you did and one new question. The report-back is the step that turns an advisor into an
advocate, and the one most founders skip.

**The best outcome is not money.** It is their engineering team as a design partner — bounded, useful to
*them*, and it produces evidence instead of promises.

**Advisor equity, when it is time (FAST norms, fi.co/fast — one page, no lawyers):** at pre-launch stage
a standard advisor is **0.25%**, an expert advisor up to **1.0%**; post-launch, 0.20% / 0.80%. Terms are
**2-year monthly vesting with a 3-month cliff**, which is what makes formalizing safe rather than
awkward: if it fizzles in the first quarter, no equity moved.

---

## 7. EU expansion & the sovereignty tailwind

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

The Dutch software companies that got large — Mendix, Framer, Weaviate — did not win *in* this market;
they won *from* it, using local seed money, Dutch enterprise logos and Dutch talent to fund a global,
developer-led motion. **That is the shape of this plan, and the reason NL traction is treated as evidence
rather than revenue.**

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
