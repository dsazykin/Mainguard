# Mainguard — Netherlands Operations

**The lookup material for running the company from Enschede.** Grant schemes and their deadlines, the
Dutch fund and angel landscape, incorporating and invoicing, hiring and salary bands, and the local press
and events map.

**This is reference, not reading.** Open it when you are filing a grant, incorporating, hiring, or
pitching a Dutch outlet. Nothing here is needed to understand what Mainguard is — that is
[`../BUSINESS.md`](../BUSINESS.md) — or how it goes to market, which is [`../GTM.md`](../GTM.md).

**Every figure is a 2026 parameter and every window is rigid.** Re-verify against the current *regeling*
before filing anything. Grant deadlines are treated like a release calendar: MIT opens on one day in
April, and WBSO must be filed *before* the month the work starts.

*Detail: [Grant Applications NL](go-to-market/Mainguard_Grant_Applications_NL.md) for the drafted
applications; [Master Market Document](go-to-market/Mainguard_Master_Market_Document_2026-07.md) Parts
IX–XII for the full research and its sources.*

---

## 1. Non-dilutive funding — the grant stack


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
so it's available later. *Draft project descriptions:* [`../GTM_Assets.md`](../GTM_Assets.md) §8.

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


---

## 2. The Dutch fund and angel landscape

These are the warm, reachable start and the intent-letter source for VFF. **The trigger for approaching
any of them, and the shape of the raise itself, is [`../GTM.md`](../GTM.md) §6.2** — this section is only
the map of who exists.


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

---

## 3. Company setup, hiring and the Twente base

### 3.1 Entity

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

### 3.2 Selling software from NL: VAT and merchant of record

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

### 3.3 Setup checklist, in order

1. Trademark screen on the name (Benelux + EU word mark, ~€250–1,000; Nice classes 9 + 42).
2. Online notary → Holding BV + Werk-BV; KVK + RSIN/VAT numbers arrive with registration.
3. Business bank account + accountant (fixed-fee startup packages).
4. IP assignment (you → werk-BV), DGA employment agreement, minimum-wage salary election.
5. eHerkenning eH3 → **WBSO application** (§6.1).
6. MoR account + EUR pricing page; Terms of Service and Privacy Policy **with entity and jurisdiction
   stated**.

### 3.4 Hiring & talent (Twente)

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

### 3.5 The Enschede/Twente base

**Novel-T** is the Twente innovation agency; its startup programs are **free of charge**, and the **START
program** is a 6-week validation program run at **Incubase**, which also provides cheap co-working and
on-tap subsidy advice from partners. **Register with Novel-T** — it is the free front door to Twente's
grant advisors, angels and corporate network. **Kennispark Twente**'s 2026–2035 strategy targets growth to
~700 companies and 16,500 jobs, with a €100M national campus-innovation budget flowing largely into existing
science parks — a rising tide for anything based there. **Local track record:** CodeSandbox is
Enschede-origin, and Cottonwood runs its European HQ on the UT campus. Use that story with local press,
Novel-T and angels; it primes them to believe it can happen twice.


---

## 4. Dutch press and events

Time local press to land the **same week as a launch**, so the story is "launch," not "plans." Per outlet:
find the journalist who wrote the nearest story, send a five-sentence personal pitch, embargo-free, with
screenshots and video attached, and offer a 15-minute call. **Dutch pitches to Dutch outlets.**

### 4.1 The media list, in pitch order

| Outlet | What it is | Angle |
|---|---|---|
| **Tweakers** | NL's biggest tech community; devs read it daily | Product + local-founder angle at act one |
| **AG Connect** | Dutch IT-professional trade press | The enterprise/governance story at act two |
| **Silicon Canals** | English-language European startup news | Funding / design-partner milestones |
| **Emerce** | Dutch digital business press | Business angle once first paying teams exist |
| **MT/Sprout** | Dutch startup/scale-up magazine | Founder story + subsidy-stack angle |
| **IO+ / Innovation Origins** | Covers East-NL innovation | "Twente builds a global devtool" — an easy yes |
| **Tubantia / 1Twente / U-Today** | Regional + campus media | Local-hero coverage; cheap credibility for outreach and hiring |

### 4.2 Community & events

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

**Dutch-market nuances:** LinkedIn is disproportionately effective for B2B in NL. Product copy stays
English; sales conversations in Dutch close Tier-1/2 companies faster. "Mainguard is proof you can build
world-class native UI in .NET" is itself a marketing asset in this community.

---

