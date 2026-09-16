# Mainguard — Business Plan & Financial-Model Narrative

**Date:** 2026-07-11 · **Status:** Lane-F stakeholder document · **Register:** brand (Voice Bible rules apply)
**What this is.** The business plan as a *story with receipts*: the strategy layer reads from `Mainguard_Master_Market_Document_2026-07.md` (the main GTM reference), and every number in the financial narrative reads from the cost & scaling model in `docs/phase-2/Mainguard_Cloud_Vibe_Companion.md` §5 (revision 2026-07-11.2) — this document adds **no new figures**; it explains why the existing ones are shaped the way they are. Where a claim is a placeholder, it says so; where a capability is unbuilt, it is marked **[Horizon]** (honesty contract, `docs/creative/Narrative.md` §0).
**Companions:** the pitch decks in `docs/business/go-to-market/decks/` · the defensibility memo (`Mainguard_Defensibility_Memo.md`) · objection handling (`Mainguard_Objection_Handling.md`) · the NL grant drafts (`Mainguard_Grant_Applications_NL.md`).

---

## 1. The business in one paragraph

Mainguard launches as a free, excellent, native, no-login Git GUI — the trust wedge and top-of-funnel — then monetizes the layer nobody has monetized: **making AI-agent output safe to merge**. Sandboxed local execution, deterministic test-verification before human review, a merge queue that re-verifies stale branches, risk-ranked review with per-hunk provenance, and an audit trail an enterprise can show its compliance team. Windows-first in a Mac-first category; agent-vendor-neutral in a locked-in category. Free users come for the Git client; teams pay for review throughput and governance; the cloud tier (2027) converts the honest local hardware ceiling into usage revenue. The 2025–26 failure ledger (Bloop, Terragon) proves orchestration alone monetizes at zero — so we never sell orchestration; we sell *trustworthy merges*. (Master Market Document Part I, verbatim in substance.)

**Where we stand (July 2026):** the Git client is shipped and working (T-01…T-33, 1,042 tests); the agent platform is specified (P2-01…P2-26) and in development on `phase2`; launch targeted ~October (free client) + Q4 (agent layer); team scaling 1→5–6; **pre-revenue by design** until design partners validate; founder based in Enschede, NL.

---

## 2. What we sell, and what we refuse to sell

The pricing structure is locked (Master Market Document §8.1); this section is the *reasoning*, because in this category the refusals are as load-bearing as the tiers.

| Tier | Price | What it buys | Why this number |
|---|---|---|---|
| **Free** | $0, no login, ever | The full Git client + one sandboxed agent **[Horizon]** | The funnel must be genuinely excellent free. GitKraken's free tier is account-walled and blocks private repos; ours has no wall to hit. Warp's multi-year trust tax is why "no login" is a feature, not an absence. |
| **Pro** | $20/mo or $199/yr with perpetual fallback | Unlimited local agents, verification pipeline, review cockpit, AI gateway, BYOK **[Horizon]** | $20 is the established individual AI-tool price (Cursor Pro, Claude Pro, Copilot Pro+ band) — no anchoring fight. BYOK means no inference-margin death. The JetBrains-style fallback is a loyalty signal to subscription-fatigued Windows developers, support-scoped with a separately versioned adapter channel. |
| **Team / Enterprise** | $50+/seat | Merge-queue + re-verification analytics, per-hunk provenance, audit/SIEM, RBAC/SSO/SCIM, budget caps **[Horizon]** | Sits credibly above CodeRabbit Pro ($24–48/dev/mo) and Graphite (~$40) because it bundles what they each sell a slice of: review + queue + governance. **Standing rule: not sold before the governance features exist.** |
| **Cloud worktrees** | usage-based, 2027 | Hosted execution sessions **[Horizon]** | The usage-revenue lever BYOK deliberately forfeits locally; solves the honest 4–6-agent local ceiling. §4 below is its full narrative. |

**The refusals (each one a priced lesson):**

- **We never charge for spawning agents.** The market priced orchestration at zero, twice, fatally — Bloop/Vibe Kanban died with 27k stars and thousands of DAU; Terragon's shutdown notice pointed users at the platform vendor (GTM Plan §5.4).
- **We never resell inference at a flat rate.** A "$25/month, everything included" tier dies when heavy users arrive: one heavy session per day is ~$117/month of model COGS against $25 of revenue (Cloud Companion §5.4). BYOK locally; metered pass-through in the cloud.
- **We never sell the Team tier before its features exist.** "Do not sell to Priya" (GTM Plan §3.2) — the buyer persona converts on provenance, audit export, and queue analytics; selling promises to a compliance buyer is the one unrecoverable trust failure.
- **We never meter the customer's own hardware.** MergeLoom charges £2–4 per opened PR; Mainguard BYOK local runs cost tokens only. *No per-PR meter on your own hardware* (Narrative §4.3).

---

## 3. The revenue logic: BYOK vs cloud — one decision, told straight

The single most consequential financial decision in this plan is that **the desktop product earns nothing from usage — on purpose.**

**Locally, the user brings their own key (BYOK).** The model bill goes straight from the model vendor to the user; Mainguard never touches that money. What this buys us: no inference cost on our books, no exposure to model-price swings, no incentive to throttle the user's agents, and the trust posture the market has repeatedly rewarded (the Cursor/Windsurf "quota rage" backlash confirmed users punish opaque caps — Master Market Document §2.3). What it costs us: every dollar of usage economics, forfeited. The desktop revenue is therefore *pure software revenue* — Pro subscriptions and Team seats — clean, predictable, and capped by seat counts.

**In the cloud, we run the compute, so usage finally becomes revenue.** The strategic sentence from the market doc: *"be honest about scale locally; monetize scale in the cloud."* The cloud tier is not a pivot — it is the same daemon binary in a per-tenant pod (invariant `G-14`, no cloud-only fork), and it exists for two customers the desktop structurally cannot serve: the developer who wants more than 4–6 agents (the honest 16 GB laptop ceiling — Part XX §8), and the Vibe founder who has no terminal, no WSL2, and no API console, for whom cloud + managed-key isn't an upsell but *the only door into the product at all* (Cloud Companion §5.8).

**The two key modes in the cloud, because two audiences (§5.4):** *managed-key* (Mainguard's model account, +10% handling — the Vibe founder cannot BYOK) and *cloud-BYOK* (developers using cloud for scale; model spend drops out of our revenue and our risk, and we bill platform units only). Copilot's move to metered AI Credits (June 2026) trained the market to expect a meter; our differentiator is that the dashboard and the invoice are the same number from the same stream (ADR-4).

---

## 4. The financial-model narrative (Lane D's numbers, the story around them)

Everything in this section traces to the assumptions ledger in Cloud Companion §5.1 (A1–A9). **Every figure is an illustrative placeholder chosen to show the shape of the math — not a quote, not a committed price.** Real numbers come from Stage-4 beta telemetry, and the GA gate is literally "beta unit economics match the §5 model within tolerance." That framing is not a hedge; it is the model's strongest feature — *the spreadsheet is the acceptance test*.

### 4.1 What one unit costs, and the fact that shapes everything

A cloud session's cost has two big lines: platform (compute-seconds, storage, bandwidth) and model tokens. Worked through (§5.3), a typical 20-minute session costs **~$1.23 all-in — of which ~$1.20 is model spend and ~$0.03 is us.** Model spend is **87–95% of session cost; the platform's own cost is cents.**

That one ratio dictates the entire pricing architecture:

- **Model spend is pass-through, never product revenue.** It is re-billed at cost +10% handling — compensation for carrying the vendor's bill and its risk — because pretending the dominant cost line is *our* revenue would inflate the business (§1.6, §5.4).
- **Platform units are the product, marked up 3.5×** — because they're cents, the markup is invisible to the customer (~$0.11 on a $1.43 session) and entirely ours.
- **Resulting session price: ~$1.43 typical** — credibly under MergeLoom's £4/PR, and a full A6-profile month (~$22.85) lands inside the band users already pay for AI tooling (Pro $20, Copilot Pro+ $39). The price is credible before any sales motion (§5.4).

### 4.2 Where the margin actually lives

Price one active cloud user's month (A6 profile: 12 typical + 4 light + 1 heavy session, 2 GB stored — §5.5): revenue **$22.85**, COGS **$19.74**, gross margin **$3.11**. The decomposition is the teaching point — that $3.11 is exactly two levers and there is no third:

| Lever | Value | Character |
|---|---|---|
| Platform markup ($0.475 COGS × 2.5) | $1.19 | Small, because platform COGS is small — but priced by *us* |
| Model handling ($19.26 × 0.10) | $1.93 | Larger — but rides a price *we don't control* |

The margin's real protection is operational, and each protection is a design decision already made: nested container packing (ADR-1 — the topology decision *is* a pricing decision: going flat would nearly triple platform price for identical value, §5.7), aggressive idle reclamation (safe only because P3-06's eviction/reattach machinery exists — a user never pays for stillness, and neither do we), storage crypto-shred discipline, and one metered number (metering = billing source, so the dashboard and the invoice can never disagree, §5.5).

### 4.3 Break-even and the honest scale table

Fixed platform cost at beta scale is assumed at $10K/month (A8), so the tier has a break-even: **$10,000 ÷ $3.11 ≈ ~3,200 active cloud users/month** before it contributes a dollar. Above it, contribution grows linearly: at 10,000 active users, ≈ +$21.1K/month (≈ $253K/yr); at 25,000, ≈ +$67.8K/month — directional only, since fixed costs step up with scale (§5.6).

**The caveat this plan will always carry into a pitch:** the revenue column is mostly pass-through model spend — at 10,000 users, $212K of the $228.5K monthly revenue is the model vendor's money passing through our invoice. **Quoting it as ARR without that caveat would flatter the business.** The number the tier runs on is gross-margin dollars, and every internal target and every Stage-4 gate is set against *that* column (§5.6). We volunteer this before any diligence finds it; in a category that died of over-claiming, the caveat is a moat.

### 4.4 Sensitivity — what breaks the model, and what we do about it

Named failure modes, priced (§5.7):

1. **Model prices fall 50%** (the biggest exposure): handling margin halves; per-user margin $3.11 → $2.16; break-even rises to ~4,600 users. Mitigations: usage historically expands as prices fall, and the platform markup — the lever we control — is untouched. The model is *volume-and-markup driven, not model-spend driven*, by design.
2. **Prompt caching lands** (upside): a typical session's model COGS could fall $1.20 → ~$0.71; in managed-key mode it widens handling margin or funds price cuts; in cloud-BYOK it is simply the user's savings. Either way it helps.
3. **Topology goes flat instead of nested:** platform COGS roughly triples. This is why ADR-1's default is nested and per-agent pods are a *paid isolation capability*, not the architecture.
4. **Idle reclamation fails:** compute COGS ×5 and users get billed for stillness — a margin leak *and* a trust breach. The reattach machinery is the guard.

### 4.5 Desktop funnel math, kept honest

Devtool freemium converts at 1–3%. At 2% of 10,000 active free users × $20/mo ≈ **$50K ARR** — which is the argument, not the disappointment: **real money lives in team seats** (Warp's B2B2C pattern; GTM Plan §8). The desktop funnel's job is not Pro revenue; it is (a) distribution and trust for the act-two thesis, (b) the champion inside the company who becomes the Team-tier deal, and (c) the two metrics investors can't get from download counts — **weekly active repos** and **agent runs verified/merged per week** — instrumented before any launch.

The seed bar this plan is run against (Part X §10.2): conventional thresholds (~5,000+ MAU / 500 paying / $300–500K ARR) *or* the AI-infra reality — growth rate and logo quality. Our fundable story at the low end: 3–5 named design-partner teams actively using the merge queue + free-GUI retention + weekly-verified-merges growth, at roughly $10–50K MRR, positioned as agent infrastructure.

---

## 5. The path to Team/Enterprise — how $20 seats become $50+ seats

The Team tier is the business; everything before it is the approach path. The sequence is deliberate and gated:

**Step 1 — Land (act one + act two, Q4 2026).** Individuals adopt the free client (no login, no wall); power users convert to Pro at $20 for unlimited local agents + the verification pipeline. Kite's law is priced in: individuals are the funnel, not the revenue plan (GTM Plan §5.4).

**Step 2 — Prove willingness-to-pay before building the enterprise layer (Q4 2026–Q1 2027).** 3–5 Track B design-partner teams use the merge queue on real repos — early access + roadmap influence + free year, for real usage, bi-weekly feedback, and logo rights at GA; time-boxed to 6 months (Part XIII). **Tripwire:** if these teams won't pre-commit to paid pilots by two months post-act-two, packaging gets revisited before another euro of enterprise build (GTM Plan §12.2).

**Step 3 — Expand on governance (2027, post-PMF signal).** The expansion products are the audit dashboard and merge-queue analytics — sold to "Priya," the EM accountable for AI governance whose pain is audit questions she can't answer (GTM Plan §3.2). What makes $50+ credible against $24–48 review tools: the bundle (review + queue + governance) and the compliance features enterprises demonstrably pay premiums for. The EU angle is a tailwind stated honestly: audit-grade evidence and "where procurement is heading" (EU AI Act Art. 12, NIST AI RMF, converging auditor demand) — never a deadline scare, since the Digital Omnibus moved high-risk obligations to Dec 2027 (Part XVIII).

**Step 4 — The Team tier rides the cloud rails (2027+).** P3-10's org dashboard and shared queue run *server-side over the P3-06 tenant store* — the identity stack (P2-23 OIDC/RBAC/SCIM) and tenant infrastructure built for cloud sessions carry the Team tier's server features. Part of the cloud buildout's return therefore lands as seat revenue on top of session margin (Cloud Companion §5.8 #3) — one infrastructure investment, two revenue lines.

**Geographic sequencing (Part XVIII):** NL design partners and first logos (the Part-IX target map: Topicus, Thales Hengelo, Nedap, Sigmax, Betabit, Info Support tier) → DACH (the densest Windows/.NET + compliance-culture market, 20 minutes from Enschede) → Nordics/UK → US enterprise with the seed round. NL is a beachhead, not a market — evidence, not revenue.

---

## 6. Operating plan (compressed; the master calendar governs)

- **Launch:** two acts (GTM Plan §7) — free client ~October; verification story +4–8 weeks; three technical essays published pre-launch; trust assets (security architecture doc, telemetry policy, FSL statement) live before act one.
- **Team & cost base:** Enschede — Twente seniors €70–85k vs €85–100k Randstad; UT/Saxion internship pipeline (€250–500/mo norms); with WBSO's 50% starter rate a €55k medior costs the BV net ≈ €40k-equivalent in year one — the subsidy stack effectively funds hire #1 (Part XII). ESOP under the new Dutch option regime targeted 1 Jan 2027.
- **Legal:** Holding BV → Werk-BV before launch (Aug–Sep 2026); IP assigned into the BV; MoR (Paddle-class) for Pro billing; enterprise invoices direct; GDPR posture is structural (local-first — no customer code processed server-side).
- **Funding:** the non-dilutive sequence runs regardless of the raise — WBSO now → VFF (€50–350k, needs an investor intent letter, via Oost NL) → MIT April 2027 → Innovatiebox at first profit (Part X; full application drafts in `Mainguard_Grant_Applications_NL.md`). Equity anchor: €750k–1.5M pre-seed (grants extend it ~40%) *or* skip to a $2–4M seed on launch traction — decided after October data, not before.

---

## 7. Risks, stated honestly (the plan's own tripwires)

Carried from Part XX without dilution: **platform absorption** (highest — Cursor Origin is the named tripwire: local execution + provenance ships → re-plan within a quarter); **monetization ceiling** (the design-partner pre-commit tripwire above); **execution capacity** (one founder, forming team); **BYOK/ToS fragility** (API-key path primary; local-model pressure valve; vendor-neutral adapters); **naming** (the MergeLoom collision — decide before KVK registration); **NL-specifics** (rigid grant windows — the funding calendar is maintained like a release calendar); **hardware honesty** (4–6 agents local; the cloud is the scale answer; the "50 agents" claim stays dead).

---

## 8. Source ledger

- **Numbers:** `docs/phase-2/Mainguard_Cloud_Vibe_Companion.md` §5 (assumptions A1–A9; worked sessions §5.3; pricing §5.4; margin §5.5; break-even §5.6; sensitivity §5.7; business case §5.8). All flagged illustrative pending Stage-4 telemetry.
- **Strategy & pricing structure:** `Mainguard_Master_Market_Document_2026-07.md` Parts I, II, VIII, IX–XIII, XV, XVIII, XX–XXI; `Mainguard_GTM_Plan_2026-07.md` §§3, 5, 7–9, 12.
- **Positioning & honesty contract:** `docs/creative/Narrative.md` §§0, 3–4.
- **Competitor facts:** `docs/business/market-analysis/Mainguard_MergeLoom_Deep_Dive_2026-07-07.md` (pricing §2), `Mainguard_Competitor_Research_2026-07-07.md`, `Mainguard_Viability_And_Differentiation_2026-07.md` §1.3.

*Self-gate: no figure in this document originates here; every euro/dollar amount, percentage, and date traces to a named section above. Unbuilt capabilities are tensed [Horizon]; illustrative numbers are flagged at first use and again where they'd be most tempting to quote bare (§4.3).*
