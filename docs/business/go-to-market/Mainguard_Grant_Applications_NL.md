# Mainguard — Grant & Subsidy Application Drafts (Netherlands / Twente stack)

**Date:** 2026-07-11 · **Status:** Lane-F drafts — ready for a subsidy advisor to hold the pen · **Register:** brand-formal (application prose), with working notes per scheme
**Reads from:** `Mainguard_Master_Market_Document_2026-07.md` Part X (the funding stack, walkthroughs, and sources), Part IX (Novel-T/ecosystem), Part XI (BV prerequisite), Part XII (hiring/WBSO interaction), and `docs/phase-2/` for the technical substance every application cites.
**Sequence (locked, Part X):** WBSO now → VFF once an investor intent letter exists → MIT April 2027 → Innovatiebox at first profit → Innovatiekrediet/EIC at scale-up.

**Honesty & accuracy rules for every draft below.**
1. Scheme parameters (percentages, ceilings, windows) are the **2026 figures from the Master Market Document, cited to RVO/provincial sources there** — re-verify against the current regeling before filing; grant windows are rigid and treated like a release calendar (Part XX §7).
2. Technical-novelty claims follow the WBSO standard — *technically new to the applicant, with named technical bottlenecks* — and every bottleneck below is a real, specified engineering problem with its task ID. Nothing is dressed up.
3. Product-state honesty is unchanged in grant prose: the Git client is shipped; the agent platform is in development. A subsidy application is precisely the place where "in development" is the *correct and advantageous* tense — WBSO funds R&D, not shipped software.
4. Final submissions to RVO/Overijssel may need to be in Dutch; these drafts are English masters for the advisor to translate. Free advisory access: Novel-T's partner subsidy advisors (Leap is an Incubase partner — Part IX §9.1).

---

## 0. Prerequisites checklist (before any filing)

- [ ] **Entity:** Holding BV → Werk-BV incorporated; IP assigned into the werk-BV (Part XI §11.1; investors and VFF effectively require the BV; Innovatiebox exists only inside one).
- [ ] **eHerkenning eH3** for the BV (or DigiD while still zzp — the self-employed WBSO variant applies until payroll exists: ≥500 R&D hours/year → €15,979 + €7,996 starter top-up, 2026 figures).
- [ ] **Hours ledger from day one** — per-project R&D hours administration (RVO audits; the mededeling of realized hours is due by 31 March of the following year).
- [ ] **Register with Novel-T** — the free front door to Twente's grant advisors, angels, and corporate network (Part IX).
- [ ] **Timing discipline:** WBSO for a BV must be filed **before the month in which the R&D starts**; MIT Overijssel opens on one day in April and is oversubscribed with a notarial lottery — the application must be ready-on-day-one (reminder: February 2027).

---

## 1. WBSO — R&D wage-cost credit (file first; the foundation)

**Scheme (2026):** payroll-tax reduction of **36% (starters: 50%) of the first €391,020 of R&D wages**, 16% above that; national budget €1.817B (RVO, via Part X §10.1). A granted WBSO declaration is also the **entry ticket to the Innovatiebox** (9% corporate tax on profits from the self-developed software) — structure the IP in the BV now even though it's irrelevant pre-profit.

**Fit:** Mainguard's daemon/sandbox/merge-queue development is textbook S&O — technically new software development with demonstrable technical bottlenecks (Part X's own examples are drawn from this project).

### 1.1 Draft project description — Project 1: "Deterministic multi-process Git coordination engine"

> **Objective.** Develop a coordination engine that allows multiple autonomous processes (interactive coding agents and a human's own tooling) to operate concurrently against a single Git repository without index corruption, lock leakage, or silent loss of work.
>
> **Technical bottlenecks (why this is technically new for us, and unresolved by available components):**
> 1. *Cross-process handle determinism.* Native libgit2 handles held by any crashing process leave `.git/index.lock` artifacts that block all other processes. No available library guarantees deterministic acquisition/release across N concurrent writers; we must design a single-owner handle discipline (our `ExecuteWithRepo` invariant) and extend it to a daemon serving multiple clients.
> 2. *Merge linearization under concurrency.* When several agent branches are verified in parallel, merges must serialize through one linearization point while verification results remain bound to the exact base commit (`main@<sha>`). Designing a lock+CAS scheme whose verification records stay consistent under rebase is an open engineering problem for us (task P2-10).
> 3. *Stale-verification invalidation.* After any merge, previously verified branches are verified against a base that no longer exists. Efficient detection and automatic re-queue (rebase → re-verify) without O(N²) re-verification storms requires new scheduling logic (P2-10; no available merge-queue product re-runs verification on post-rebase state).
>
> **Planned S&O work (period):** design and implementation of the daemon's repository-access layer, the verification-record store, the staleness detector, and the re-queue scheduler; test harnesses simulating concurrent-writer crash scenarios.

### 1.2 Draft project description — Project 2: "Hardened execution sandbox for autonomous coding agents on Windows/WSL2"

> **Objective.** Develop an execution environment in which untrusted, AI-generated code runs on a developer's Windows machine with (a) no network path except an allowlist, (b) no credential or route to the user's real Git remote, and (c) survivable failure semantics.
>
> **Technical bottlenecks:**
> 1. *Default-deny egress on WSL2.* Existing container tooling on Windows/WSL2 provides no off-the-shelf default-deny egress proxy with per-agent allowlists; we must build the proxy configuration layer and policy enforcement ourselves (P2-07, `EgressProxyConfigurator`/`IEgressPolicy`).
> 2. *Structural remote quarantine.* Preventing a prompt-injected agent from pushing to a real remote by *construction* (a worktree whose only configured `origin` is a daemon-owned bare mirror, with no real credential present in the sandbox) rather than by firewall rule — and keeping this invariant across provisioning, sync, and cleanup (ESC-I3, P2-06).
> 3. *Windows↔Linux boundary performance.* Avoiding 9P filesystem latency for repository I/O while the UI stays native Windows requires a client-daemon protocol whose every timeout is derived from a measured round-trip budget rather than hardcoded local assumptions (OPS §2.8; P2-25 WAN CI job).
>
> **Planned S&O work:** sandbox provisioning pipeline, egress proxy + policy engine, quarantine-remote provisioner, crash/reattach semantics, and the latency-budget instrumentation.

### 1.3 Draft project description — Project 3: "Tamper-evident attribution and audit chain for AI-generated code changes"

> **Objective.** Develop an append-only, hash-chained audit structure that records, per code change: the generating agent/model/session, the approving human identity, the verification verdict and its base commit, and the merge decision — verifiable offline by a third party.
>
> **Technical bottlenecks:**
> 1. *Per-hunk attribution.* Mapping agent output through rebases, squashes, and partial staging down to hunk granularity, and rendering it in blame/diff structures, has no existing implementation (the emerging Agent Trace standard specifies emission only; no consumer/renderer exists).
> 2. *Chain integrity under a local-first model.* Hash-chaining and externally anchoring an audit log that lives on the user's machine (not a trusted server), such that tampering is detectable without a cloud dependency (P2-15, `audit verify`).
> 3. *Identity binding without a server.* Binding OS-level user identity to approval events in a way that survives export to enterprise SIEM systems (P2-16, P2-23).
>
> **Planned S&O work:** the chain format and verifier, the provenance mapper across Git operations, blame/diff-gutter rendering, and SIEM export.

**Working notes (advisor):** file via RVO eLoket **before the development month**; keep the three projects separate (clean hour attribution); the hours ledger starts the same day the declaration period does. The project descriptions deliberately name bottlenecks that map to real specified tasks — if an RVO reviewer probes, the phase-2 specs are the evidence.

---

## 2. VFF — Vroegefasefinanciering (the €50–350k pre-seed loan)

**Scheme (2026):** government loan for validating an "early-phase plan"; €50k–€350k at 7.19%, up to 100% of plan costs; repayable; requires a **letter of intent from a follow-on investor** for at least the same amount; the trajectory must not have started before the application; completable within 2 years. **Overijssel quirk:** apply via **Oost NL** (the regional financier), not RVO directly. Window open all year (Part X §10.1).

**Trigger:** an angel/VC intent letter (henQ/NP-Hard/Cottonwood-class, or a committed angel via Leapfunder) — targeted November–December 2026 on launch data (Part XXI calendar).

### 2.1 Draft early-phase plan (the validation plan the loan funds)

> **Company.** Mainguard B.V. (Enschede) — a native Git client for Windows (shipped; 1,042 automated tests) evolving into a verification and merge-governance layer for AI-coding-agent output (in development).
>
> **The proposition to validate.** Development teams adopting AI coding agents face a verification bottleneck: AI-assisted teams merge ~2× more pull requests while review time has risen 91%, and 87% of developers report distrust of agent output (Stack Overflow 2025; sources in our market documentation). We build the layer that makes agent output safe to merge: sandboxed execution, deterministic test-verification before human review, a merge queue that re-verifies stale branches, per-line attribution, and an audit trail. No shipped product on the market combines these capabilities (verified capability-gap analysis, July 2026).
>
> **Early-phase milestones (= the plan the loan finances, ≤ 2 years):**
> 1. **M1 — Public launch of the free client** (validation: installation base, weekly active repositories, retention curve).
> 2. **M2 — Verification pipeline in design-partner hands:** 3–5 committed teams (target profile: one .NET enterprise shop, one AI-forward startup, one agency — candidate map: Topicus, Nedap, Sigmax, Betabit tier) using the merge queue on real repositories (validation: agent runs verified per week; % of merges against non-stale verification).
> 3. **M3 — Willingness-to-pay:** conversion of design partners to paid pilots at the Pro/Team price points ($20 / $50+ per seat) (validation: signed pilots; tripwire — if pilots don't pre-commit within two months of M2, packaging is revised before further enterprise build).
> 4. **M4 — Raise-readiness:** metrics pack for a priced round (the follow-on investor per the intent letter).
>
> **Use of funds:** engineering completion of the verification pipeline (the WBSO-declared projects above), design-partner program costs, launch execution. **Repayment source:** the follow-on round and/or revenue from M3 onward.

**Working notes:** the milestones are deliberately identical to the launch plan's Phase 0–2 (GTM Plan §7) so the company reports one set of numbers to everyone. The intent-letter ask pairs naturally with the Part-X fund list — a Dutch fund's intent letter effectively doubles itself in state money; say exactly that in the fund conversation.

---

## 3. MIT Haalbaarheidsproject — €20k feasibility grant (April 2027 round)

**Scheme:** 35% of feasibility-study costs, max €20,000; Overijssel's 2026 ceiling was €980,000, window opened 7 April 2026, first-come-first-served with day-one oversubscription and notarial lottery. **The 2026 round has passed — target April 2027 with a ready-on-day-one application; reminder set for February 2027** (Part X §10.1).

### 3.1 Draft feasibility-study proposal

> **Title.** Feasibility of cloud-hosted verification worktrees for EU enterprises.
>
> **Applicant.** Mainguard B.V., Enschede (mkb/SME).
>
> **Background.** Mainguard develops a desktop product in which AI coding agents run in isolated sandboxes on the developer's own machine and their output is test-verified before merge. The local architecture honestly supports 4–6 concurrent agents on standard 16 GB hardware. Enterprise demand for larger capacity, browser access, and centrally governed teams requires a cloud variant: per-tenant execution pods running the identical engine, with per-tenant encryption, usage metering, and EU data residency.
>
> **The feasibility questions (what the study answers before development investment):**
> 1. *Technical:* can the existing client-daemon protocol drive a cloud pod over WAN latency without modification (the transport-agnostic invariant), and which pod topology (nested session containers per tenant vs pod-per-agent) satisfies isolation and cost requirements? (Method: the specified 2-week topology spike + WAN test harness, extended into a documented architecture decision.)
> 2. *Economic:* do measured session costs confirm the modeled unit economics (illustrative model: platform cost in cents per session, model spend dominant and passed through, break-even at ~3,200 active users), and at which price points is the tier viable for EU mid-market teams? (Method: instrumented pilot sessions; sensitivity analysis per the published cost model.)
> 3. *Market/compliance:* what do EU/Dutch enterprise buyers (the applicant's Overijssel/East-NL design-partner network: Microsoft-stack product companies and agencies) require regarding data residency, per-tenant encryption, deletion guarantees (crypto-shred), and audit evidence — and does "EU-vendor, local-first, source-available" measurably shorten procurement? (Method: structured interviews with 10–15 named regional prospects; procurement-requirements matrix.)
>
> **Output.** A go/no-go investment decision with a written architecture ADR, a validated cost model, and a procurement-requirements report — gating a subsequent development project (candidate for Innovatiekrediet at that stage).
>
> **Why regional relevance.** The applicant is a Kennispark-adjacent Twente company; the study's interview base is the East-NL software industry; a positive outcome anchors cloud-infrastructure engineering employment in Enschede.

**Working notes:** the study design is lifted directly from the de-risking sequence the engineering docs already lock (Cloud Companion §3: Stage 1 spike → gates), so the grant funds work the roadmap requires anyway — the correct shape for a feasibility grant. A subsidy advisor should hold the pen on the final form and the cost breakdown; February 2027 is the drafting deadline, the window opens in April.

---

## 4. The rest of the stack (positioned, not yet drafted — with triggers)

| Scheme | What | Trigger to draft | Note |
|---|---|---|---|
| **Innovatiebox** | 9% CIT on profits from self-developed software (vs 19%/25.8%) | First profitable quarter | The granted WBSO declaration is the entry ticket; IP must sit in the BV — structured now (Part XI). Accountant-led, not application prose. |
| **Innovatiekrediet** | Development loan, up to 45% of project costs (small co.); requires 55% co-financing + investment declaration; 2026 budget €30M technical | The cloud-worktrees buildout decision (post-MIT feasibility, ~2027) | Do the mandatory RVO quick-scan first; the MIT study's ADR + cost validation is designed to be its technical annex. |
| **EIC Accelerator / Horizon Europe** | Grant ≤€2.5M + equity | Traction + a grant writer (2027+) | High effort; only with professional support; UT partnership route for Horizon calls. |
| **Provincial "regional importance" projects** | Up to €500k, pre-consultation required | Opportunistic — check SubsidieMatch (Overijssel, pre-seed) quarterly | Part X §10.1 item 6. |

**Ecosystem actions that cost nothing and precede all of the above:** Novel-T registration (free front door — subsidy advisors, angels, corporates); Incubase co-working + on-tap advice; the START validation program if a structured 6-week push is useful around launch (Part IX §9.1).

---

## 5. The one-page funding calendar (grants as a release calendar)

| When | Action | Depends on |
|---|---|---|
| **Now (July 2026)** | Novel-T registration; advisor intro; hours ledger starts | — |
| **Aug–Sep 2026** | BV incorporation + IP assignment; eHerkenning; **WBSO filed before the development month** (§1 drafts) | BV |
| **Nov–Dec 2026** | Investor intent letter (fund list, Part X §10.2) → **VFF application at Oost NL** (§2 draft) | Launch data; intent letter |
| **Feb 2027** | MIT application finalized with advisor (§3 draft) | — |
| **April 2027** | **MIT submitted on opening day** (lottery) | Feb draft |
| **First profitable quarter** | Innovatiebox groundwork with accountant | WBSO granted |
| **Cloud buildout decision** | Innovatiekrediet quick-scan | MIT study outcome |
| **Every 31 March** | WBSO mededeling (realized hours) filed | Hours ledger |

---

*Self-gate: every scheme parameter, percentage, window, and quirk above traces to Master Market Document Part X/XI/XII (which cites RVO, Overijssel, Belastingdienst, and advisor sources inline); every technical claim in the WBSO drafts names its task ID in the phase-2 corpus; product state is tensed honestly (client shipped / platform in development) — which for R&D subsidy purposes is also the strongest possible framing. Re-verify current-year parameters before each filing.*
