# Mainguard — Investor Pitch Deck (Seed / Pre-Seed Master)

**Date:** 2026-07-11 · **Register:** brand (Voice Bible rules apply — no exclamation marks, no hype vocabulary, severity rides the facts)
**Status:** Deck script — slide copy + speaker notes, ready to be laid out. Use when the Part-X raise trigger fires (3–5 active Track B design-partner teams + retention curve + verified-merges growth, or a competitive forcing event — Master Market Document §10.2).
**Narrative arc (locked):** problem → why now → the wedge → the moat → traction → market → business model → the ask.
**Honesty contract (binding, from `docs/creative/Narrative.md` §0):** present-tense claims are on `main` (T-01…T-33, 1,042 tests); the agent control plane is **[Horizon]** — say "in development," never "works today." Capacity is an honest **4–6 agents on a 16 GB laptop**, never "swarms of 50." The audit story is "audit-grade, where procurement is heading," never "legally required." Cloud numbers are the §5 cost model's **illustrative placeholders**, flagged as such out loud.
**Variants:** technical deep-dive (`Mainguard_Deck_Technical_DeepDive.md`), advisor/board update (`Mainguard_Deck_Advisor_Board_Update.md`), swarm vision (`Mainguard_Deck_Swarm_Vision.md`).

---

## Slide 1 — Purpose

**On the slide:**

> **Mainguard makes AI-agent code safe to merge.**
>
> A native Git client, shipped. On top of it: the control plane where any agent's work becomes trustworthy commits on `main`.

**Speaker notes.**
One sentence, then stop. The subtitle carries the two-part structure the whole deck follows: something real underneath (the shipped client), something valuable on top (the verification layer, in development). If the listener remembers one thing, it is the first line — it is the Part-XV purpose sentence verbatim, and every later slide is an expansion of one of its words: *AI-agent* (why now), *safe* (the pipeline), *merge* (the moat — nobody else owns the merge step).

Do not open with the demo or the architecture. Open with the sentence, pause, advance.

---

## Slide 2 — Problem: the blind merge

**On the slide:**

> Agent CLIs made it trivial to produce ten branches an hour. Reviewing them didn't get faster.
>
> - AI-assisted teams merge **~2× more PRs**; review time is up **91%** — against a fixed human ceiling
> - **87%** of developers are concerned about agent accuracy; **81%** about security
> - Copilot's coding agent alone: **1M+ PRs in five months**; code churn **+861%** under high AI adoption
>
> **And the code gets merged anyway.** Hope is not a merge strategy.

**Speaker notes.**
The enemy is a *practice*, not a competitor and not "AI" (Master Market Document §4.3 — our users run six agents; we are pro-agent). Name it: **the blind merge** — code nobody fully read, verified against a `main` that has since moved, written by a process nobody can attribute, merged because the diff was too long and the queue was too deep. Every investor in the room has either committed one or funded a team that does it daily.

Sources, kept ready for diligence: review time +91% and 2× PR volume — Viability §1.3; 87%/81% distrust — Stack Overflow 2025 AI survey (via GTM Plan §4); 1M+ PRs / +861% churn — Octoverse 2025 / Faros AI (via GTM Plan §4). The gap between "merged" and "verified" grows on its own with every agent seat sold — by anyone. That is the shape of a good problem: it compounds without us spending a dollar.

---

## Slide 3 — Why now

**On the slide:**

> The bottleneck moved from writing code to trusting it.
>
> - Agent CLIs went mainstream in 24 months — yet only **31%** of developers run agents today. Early innings.
> - "Agents in worktrees + a GUI" is now **free table stakes** from every platform vendor
> - Every vendor sells generation. **Nobody sells trust** — the five capabilities that would create it are verified empty across the field
> - Meta built **RADAR** internally (catching a 1/3 revert rate) because the verification layer couldn't be bought

**Speaker notes.**
"Why now" is our strongest card (Part XV §15.1). Three moves happened in sequence: generation got commoditized (Claude Code Desktop, Codex app, Cursor 3, Copilot app — all ship worktree agents free); trust collapsed (the slide-2 numbers); and the verification layer stayed empty — we checked, capability by capability, across the entire field (GTM Plan §5.2: no shipped product does local test-gated review, per-hunk provenance, risk-ranked review, a re-verifying merge queue, or cross-vendor Windows-native — Meta's RADAR exists only inside Meta).

The 31% figure is the timing argument: the agent wave is *early*, not late — the trust problem is about to get 3× bigger as the other 69% arrive. RADAR is the make-vs-buy proof the category is real: a company with infinite engineers built it rather than buying it, because there was nothing to buy.

If asked "why hasn't anyone built it": because it requires a real Git engine underneath — partial staging, three-way merge, rebase, an undo journal — and everyone else in the category is a wrapper. That's slide 6.

---

## Slide 4 — The wedge

**On the slide:**

> The free, excellent, native Git client Windows never got.
>
> - **Shipped and working today** — 60fps commit graph, line-level staging validated against `git apply`, 3-pane conflict resolver. 1,042 tests.
> - **No login, no private-repo wall, nothing leaves your machine** — the incumbent's free tier has both walls
> - **Windows-first in a Mac-first category** — Windows is the largest developer OS (59.2% personal / ~48% professional); the category leader is Mac-only
>
> Free users come for the Git client. Teams pay for trustworthy merges.

**Speaker notes.**
The wedge is deliberately *not* the AI story — it is a real, complete product whose value doesn't depend on believing a roadmap. That ordering is priced-in failure analysis: the 2025–26 corpse pile (Bloop/Vibe Kanban dead April 2026 with 27k stars, Terragon dead February 2026) proves free orchestration monetizes at zero (GTM Plan §5.4). We never sell orchestration; the free tier is a Git client with independent daily-driver value.

The asymmetries are checkable: GitKraken's free tier requires an account and blocks private repos (GTM Plan §5.3); ours has no wall. Conductor — $22M Series A, the funded category leader — is macOS-only with no Windows signals anywhere in its changelog (Competitor Research §3). Windows OS-share figures: Stack Overflow, via GTM Plan §4. And the launch plan spends the wedge in two acts (GTM Plan §7): act one earns trust with the client; act two, 4–8 weeks later, spends it on the verification thesis.

Honesty note, if probed on distribution: today the client is a build-from-source dev preview; packaged distribution ships with launch act one.

---

## Slide 5 — The product: the verification pipeline *(roadmap — in development)*

**On the slide:**

> From "an agent produced a branch" to "a verified commit on main":
>
> **plan approval → sandboxed execution → your tests pass in the agent's sandbox → risk-ranked, provenance-annotated review → a merge queue that re-verifies whatever goes stale → human-gated merge**
>
> *Validated-then-stale is unvalidated.* Every merge to main marks other "verified" branches stale — and re-verifies them before they can land.

**Speaker notes.**
Walk the pipeline left to right; it is one sentence long on purpose. Three stops deserve ten seconds each:

1. **Test gates, not opinions.** The verification verdict is your own test suite passing in the agent's sandbox — a fact, not an LLM comment. (The review-layer incumbents ship opinions: an audit found ~35% of CodeRabbit comments genuinely useful — GTM Plan §5.3.)
2. **The re-verifying queue** is the step no product on the market performs — every queue re-runs CI; none re-runs verification on the post-rebase state of agent branches (GTM Plan §5.2, probe-verified). This is the single hardest coordination problem of parallel agents, and it is pure Git mechanics — our home turf.
3. **Provenance per hunk** — "agent X, model Y, session Z, under approved plan P wrote these lines," in the diff gutter. The Agent Trace standard has emitters coming and no consumer/renderer exists; we intend to ship the first (Narrative §2.4).

Tense discipline (honesty contract): everything on this slide is **[Horizon]** — specified (P2-10, P2-11, P2-14, P2-15 in the master implementation doc), in development on `phase2`, not shipped. Say so unprompted. Under-claiming here *is* the credibility play; this audience has been vaporware-pitched all year.

---

## Slide 6 — The moat

**On the slide:**

> Any single feature has a ~2-quarter exclusivity window. The combination doesn't.
>
> 1. **A real Git engine** — the prerequisite every wrapper lacks; shipped, 1,042 tests
> 2. **The compound pipeline** — sandbox + test gates + re-verifying queue + provenance + audit; retrofit-hostile
> 3. **Vendor-neutral** — every first-party GUI manages only its own agents; none will make its GUI a better home for a rival's. Be Switzerland.
> 4. **Windows/WSL2-native** — the largest developer OS, structurally unserved by the Mac-first wave

**Speaker notes.**
The moat argument is architectural, not feature-list. Verification and merge governance are only buildable on a real Git client — stale re-verification means rebase machinery; provenance in review means a diff stack and blame; "undo what the agent did" means an operation journal. Conductor, Superset, and the orchestrator field would have to *build a Git client first* to follow (GTM Plan §5.3). GitKraken has a client but executes agents unsandboxed on the host with no verification or merge pipeline, and its queue semantics would be a re-architecture (Competitor Research §2).

The first-party vendors stop structurally, not accidentally: each is incentivized to lock in, each reviews with its own model rather than deterministic local gates, and none models cross-branch staleness (Narrative §2.2). Their agents are the PR firehose our vendor-neutral intake drinks from — Jules and Codex are supply, not competition.

Full defensibility argument, objection by objection, lives in `docs/business/go-to-market/Mainguard_Defensibility_Memo.md` — bring it to diligence.

---

## Slide 7 — Competition, on one slide

**On the slide:**

> | Class | Who | Where they stop |
> |---|---|---|
> | First-party vendors | Copilot app, Claude Code Desktop, Codex app, Cursor 3 | Single-vendor each; model self-review, not test gates; no cross-branch staleness |
> | Review layer | CodeRabbit ($550M val), Greptile, Graphite→Cursor | Cloud opinions on PRs; no local verification, no provenance, queues re-run CI only |
> | Orchestrator GUIs | Conductor ($22M, Mac-only), Superset, Sculptor | No verification, no merge queue, no audit; monetization unsolved at ~$0 |
> | Headless governance | MergeLoom (£2–4/PR) | Stops at "PR opened" — never re-verifies; no client, no sandbox claims |
>
> **We own the intersection: local verification × merge governance × vendor-neutral × Windows-native.**

**Speaker notes.**
Respect, then the stop — never sneer (Narrative §2 convention; the teardown sentences there are as sharp as public comparison gets). The two names investors will raise:

- **Conductor** — hand them the analogy ourselves: *"Conductor for Windows — with verification."* It flatters twice: the category leader is Mac-only and has no verification layer (GTM Plan §2.4). The standing caution is real — $22M can fund a Windows port any quarter — which is why we own Windows/WSL2 first and ship queue semantics they'd have to re-architect to follow.
- **MergeLoom** — the mirror image: governed-delivery *language*, live and billing, and structurally opposite — no client, no interactive steering, no merge coordination; a branch they validated an hour ago is stale the moment main moves and they never re-verify (MergeLoom Deep Dive §5.3). One LinkedIn employee selling governance [V].

The tripwire, stated proactively if asked what would worry us: **Cursor Origin** (announced, fall 2026) — the only announced product aiming at agent-scale review + merge queue. It's a cloud forge; our local-first counter-position is clean. If Origin ships local execution + provenance, we re-plan within a quarter — and we've said so in writing (GTM Plan §12.1). Naming your own tripwire is worth more than a competition slide.

---

## Slide 8 — Traction *(template — populate at raise time)*

**On the slide:**

> - **Shipped:** the full native Git client — T-01…T-33, 1,042 tests
> - **Weekly active repos:** `[n]` · retention curve `[chart]`
> - **Agent runs verified per week:** `[n]` · **% of merges against non-stale verification:** `[n]`
> - **Design partners:** `[3–5 named teams]` on the verification pipeline
> - **Free→Pro conversion:** `[n]%` · MRR `[$n]`

**Speaker notes.**
The metrics are chosen against the seed bar, not vanity: **weekly active repos** and **agent runs verified/merged per week** are the two numbers investors can't get from download counts (GTM Plan §8), instrumented from day one. The fundable story at the low end (Part X §10.2): 3–5 named design-partner teams actively using the merge queue + a strong retention curve on the free GUI + weekly-verified-merges growth — roughly $10–50K MRR with that shape is pitchable, positioned as **agent infrastructure**, never "Git client" (median seed valuations concentrate in AI-positioned companies). Precedents for credibility-weighted raises: GitButler raised on HN demand pre-revenue; Conductor's A rested on logos, not seats.

Until the slots fill, this slide is honest about what exists: a shipped client with 1,042 tests and a launch plan — "pre-revenue by design until design partners validate" (Master Market Document Part I). Do not present this deck externally with empty slots; the deck ships when the slots do.

---

## Slide 9 — Market

**On the slide:**

> - AI code tools: **~$7–9B today → $20–30B by 2030–31** (22–27% CAGR, consensus band)
> - **~47M developers** worldwide, 59.5M forecast by 2029
> - The wedge OS: Windows — **largest developer OS**, served Mac-first by the entire polished-devtool wave
> - Willingness-to-pay, already proven adjacent: review throughput (CodeRabbit $24–48/dev/mo), merge reliability (Mergify $8+), stacked review (Graphite ~$40) — governance premiums above all

**Speaker notes.**
The market slide argues *bands and benchmarks*, not a TAM fantasy. Sources: Grand View / Mordor / Research & Markets consensus for the $20–30B band; SlashData for 47M / IDC for 59.5M (GTM Plan §4). The adjacent-pricing row matters more than the CAGR: teams already pay $24–48/dev/month for AI review *comments*; we sell the layer with deterministic verdicts and the merge gate, priced above it (Part VIII).

For Dutch/EU investors, add the NL sub-slide (Master Market Document §2.5): 575k ICT workers, 66% AI adoption in 250+ employee firms, Europe's highest AI-talent density — with the honest caveat we keep in writing: **NL is a beachhead, not a market** — design partners, subsidies, talent, and first logos; the revenue plan stays global, HN-first.

---

## Slide 10 — Business model

**On the slide:**

> | Tier | Price | What it buys |
> |---|---|---|
> | Free | $0, no login | The full Git client + one sandboxed agent *(roadmap)* |
> | Pro | $20/mo | Unlimited local agents, verification pipeline, review cockpit, BYOK *(roadmap)* |
> | Team/Enterprise | $50+/seat | Merge-queue analytics, per-hunk provenance, audit/SIEM, RBAC/SSO *(roadmap)* |
> | Cloud worktrees | usage-based, 2027 | Hosted execution — the revenue BYOK deliberately forfeits locally |
>
> **BYOK = no inference-margin death. We charge for trust, not tokens.**

**Speaker notes.**
Three sentences of logic, then the anchors. First: the market has priced orchestration at zero, twice, fatally (Bloop, Terragon) — so nothing in this table charges for spawning agents; every paid line is the pipeline that makes agent output mergeable (Narrative §4.1). Second: BYOK means the model bill goes straight from the model vendor to the user — no reselling tokens at a loss when prices move; the flat "$25 all-you-can-prompt" tiers that died of heavy users are not in this table. Third: the cloud tier (2027) converts the honest local ceiling — **4–6 agents on a 16 GB laptop** — into usage revenue with two margin levers, platform markup and a model-handling fee.

Cloud unit economics exist at full arithmetic depth (`docs/phase-2/Mainguard_Cloud_Vibe_Companion.md` §5): illustrative placeholders show ~$1.43 per typical session, ~$3.11 gross margin per active user-month, break-even ~3,200 active cloud users. Two flags to raise *yourself* before a diligence associate does: the numbers are placeholders pending beta telemetry (the GA gate is literally "beta unit economics match the model within tolerance"), and the cloud revenue line is **mostly pass-through model spend — quote gross-margin dollars, never the revenue column as ARR** (§5.6). Volunteering that caveat is the trust posture in miniature.

Price anchors: $20 = the established individual AI-tool band (Cursor Pro, Claude Pro, Copilot Pro+); $50+ sits credibly above CodeRabbit Pro and Graphite because it bundles what they each sell a slice of. Standing rule: the Team tier is not sold before its governance features exist (GTM Plan §3.2).

---

## Slide 11 — Team & home base

**On the slide:**

> - Shipped a **real Git engine** — the hard prerequisite every wrapper in the category lacks
> - Windows/.NET depth in a Mac-first field; the product is itself a .NET flagship app
> - Based in Enschede (NL): UT/Saxion talent pipeline, salaries €5–10k below Randstad, and a non-dilutive subsidy stack (WBSO, VFF, MIT) that funds 30–50% of R&D wages before any dilution
> - Team scaling 1 → 5–6

**Speaker notes.**
"Why us" is the engine, not the résumé: everyone can promise a control plane; the way you earn the right to build one is to ship the instrument underneath it (founder story, Narrative §5.5). The client exists, is fast, and is exercised by 1,042 tests — the demo does the impressing.

The Enschede base is a capital-efficiency argument, not sentiment: WBSO returns 36–50% of R&D wages (Part X), Twente seniors cost €70–85k vs €85–100k in Amsterdam (Part XII), and CodeSandbox proves Enschede can birth a global devtool (Part IX). For non-EU investors, compress it to one line: "our burn multiple benefits from a Dutch subsidy stack and Twente engineering costs."

Execution risk, stated honestly if probed (Part XX §3): one founder plus a forming team against funded incumbents; the Git core that everything depends on is the part that's already done.

---

## Slide 12 — The ask

**On the slide:**

> **€750k–1.5M pre-seed** *(or, on launch traction: a $2–4M seed)*
>
> Buys, in order:
> 1. Launch act two — the verification pipeline in design partners' hands
> 2. First 2–3 engineering hires (WBSO-subsidized; grants extend the round ~40%)
> 3. The governance layer (audit/SIEM, provenance) that unlocks the $50+/seat tier
>
> Milestones: `[weekly verified merges target]` · `[n]` paying pilot teams · Team-tier readiness

**Speaker notes.**
The two-track anchor is deliberate (Part X walkthrough, step 6): raise €750k–1.5M pre-seed — which grants extend roughly 40% — *or* skip to a $2–4M seed on launch traction; the decision is made after launch data, not before, and saying so signals discipline rather than indecision. The use-of-funds ordering mirrors the moat: the money goes to the parts competitors can't copy with a checkbox (queue semantics, audit integrity), not to marketing spend.

Tailor the closing ask per audience (Part XV): for a fund, the round; for an angel, the intent letter that unlocks VFF (€50–350k of state matching via Oost NL); for an operator, a design-partner team. End on the purpose sentence from slide 1 and stop talking.

---

## Appendix slides (hold ready, don't present)

- **A. The failure ledger** — Bloop, Terragon, Kite, Warp's trust tax, Omnara's pivot; the rule each death bought (GTM Plan §5.4). Use when asked "why won't you die like the others."
- **B. Cloud cost model** — the §5.3/§5.5 tables from the Cloud Vibe Companion, with the "illustrative placeholder" flag printed on the slide itself and the pass-through-revenue caveat in the footer.
- **C. The five empty squares** — the field-wide capability gaps, probe-verified (GTM Plan §5.2). Use for "isn't this crowded."
- **D. EU/sovereignty angle** — audit-grade trail + EU AI Act Art. 12 framing ("where procurement is heading" — never a deadline scare; the Digital Omnibus moved high-risk obligations to Dec 2027), EuroStack tailwind (Part XVIII). Use with EU funds and enterprise-adjacent LPs.
- **E. Demo** — the 10-minute script (Part XV §15.2). The demo is the pitch; anything mocked is labeled "landing in vN" out loud.

---

## Self-gate (five-question gate, Voice Bible Appendix A, applied to this deck)

1. **Point at the object** — every figure names its source (Viability §1.3, SO 2025, Octoverse, GTM Plan §§4–5, Cloud Companion §5); every capability names its task ID or test count.
2. **Way back** — n/a for a deck; risk slides name their tripwires and mitigations instead.
3. **Audit-log test** — every claim is tensed shipped or [Horizon]; the cloud table carries its own "illustrative" flag; the ARR caveat is scripted into the notes.
4. **Delete a word** — no "blazing," "game-changing," "massive"; no exclamation marks anywhere.
5. **Severity rides the role** — competitors get respect-then-stop, never a villain slide; the enemy is the blind merge.
