# Mainguard — Objection Handling (the live-fire companion to the Defensibility Memo)

**Date:** 2026-07-11 · **Status:** Lane-F stakeholder document · **Register:** brand (Voice Bible: concede what's true, state the fact, never bristle — the register locked by Narrative §5.4's comment kit)
**How to use.** Each objection is steel-manned first — the *strongest* form a smart skeptic would actually say — then answered in three moves: **concede** (what's true in it), **the fact** (the sourced counter), **the line** (the one sentence to leave in the room). Objections 1–3 are the load-bearing trio and get full treatment; the standing set follows in compressed form. HN-specific phrasings live in Narrative §5.4 and are not duplicated here; this doc is the investor/buyer register.
**Honesty contract (Narrative §0):** no answer below claims a shipped capability that isn't; [Horizon] items are argued as architecture and sequencing, never as present tense.

---

## Objection 1 — "Just use GitHub's merge queue."

**Steel-manned.** "GitHub already ships a merge queue: it batches PRs, re-runs CI against the merged result, and it's free with the platform every team already uses. Copilot even has Agent Merge now. Your queue is a feature GitHub gives away."

**Concede.** GitHub's merge queue is real, good at what it does, and the right tool for the problem it solves: keeping *CI* green on `main` under high PR volume. If a team's only failure mode is "CI broke because two PRs raced," GitHub's queue solves it and we'd say so.

**The fact — three structural gaps, each verified:**

1. **It re-runs CI, not verification.** Probe-verified across every queue on the market (GitHub, Mergify, Trunk): none re-runs *verification* — your test suite in the agent's sandbox — on the post-rebase state of agent branches (GTM Plan §5.2 #4). CI-green answers "does the pipeline pass on the batch"; it does not answer "is this agent branch, verified an hour ago against an older main, still trustworthy now" — the distinction our named failure mode carries: **validated-then-stale is unvalidated.**
2. **It's cloud- and platform-locked; the verdict is remote.** GitHub's queue governs what happens *after* push, on GitHub, with GitHub's runners. Mainguard's pipeline gates *before* anything reaches the remote — local execution, local test gates, on branches from any vendor's agents, including repos that aren't on GitHub at all. For the regulated/Windows-enterprise ICP (Thales-class, air-gap-friendly — Master Market Document Part IX), "the merge gate runs on our hardware" is the requirement, not a preference.
3. **It carries no agent semantics.** No provenance ("which agent, under which approved plan, wrote this hunk"), no risk-ranked review, no sandbox containment, no plan approval, no tamper-evident record of who approved what. The queue is one stage of a pipeline; GitHub sells the stage, we sell the pipeline — and the empty-squares analysis shows nobody combines the stages (GTM Plan §5.2).

**The line.** *GitHub's queue keeps CI green on the batch. Mainguard keeps the promise that nothing lands on main that wasn't verified — your tests, the agent's sandbox — against the main it lands on.*

---

## Objection 2 — "Conductor already exists."

**Steel-manned.** "Conductor raised a $22M Series A from Spark and Matrix, ships weekly, runs Claude Code, Codex, and Cursor agents in parallel worktrees with GitHub and Linear integration, has Vercel/Notion/Ramp logos — and it's free. You're describing a product that already won."

**Concede.** Conductor is the funded category leader, the team is small and fast (~6 people, weekly releases through v0.72.0), and on *orchestration* — spawning and watching parallel agents — they deserve the lead. We concede orchestration parity as strategy, not as courtesy: it's commoditized ground (GTM Plan §5.3).

**The fact — where they stop, verified against their own changelog and site (Competitor Research §3):**

1. **macOS-only.** No Windows or Linux signals anywhere in the changelog — while Windows is the largest developer OS (59.2% personal / ~48% professional, Stack Overflow via GTM Plan §4). The category leader structurally does not serve the largest flank.
2. **Worktree-only isolation; no verification layer.** Their "queue" is a task queue, not a verification queue; no sandbox, no test gates, no staleness model, no provenance, no audit (§3). Everything downstream of "the agent finished" — the part review-drowned teams actually pay for — is absent.
3. **Free means unmonetized.** They ride the user's own agent subscriptions with unshipped collaboration features as the future business — in a category where free orchestration has already killed two companies (Bloop, Terragon — GTM Plan §5.4). We are not competing with their price; we are selling what they don't sell.

**And the standing caution, stated before the investor states it:** $22M can fund a Windows port any quarter. The defense is sequencing — own Windows/WSL2 first and ship queue semantics they would have to re-architect to follow (Competitor Research §3) — plus the fact that following us downstream means building a Git client (Defensibility Memo, Layer 1).

**The line.** *"Conductor for Windows — with verification." The analogy flatters twice: the category leader is Mac-only, and it has no verification layer.* (The handed analogy — GTM Plan §2.4.)

---

## Objection 3 — "Agents will get good enough to not need this."

**Steel-manned.** "Model capability curves haven't bent yet. In two years agent output will be reliable enough that verification theater is friction nobody pays for — you're building a seatbelt for a car that's about to stop crashing. Composio AO already auto-merges when CI is green."

**Concede.** Agents will keep getting better — we're counting on it; our users run six of them, and the product is pro-agent by construction (the enemy is the blind merge, never AI — Master Market Document §4.3). And yes: some of today's babysitting UX will age out. Anything that is "a view over one vendor's agent" gets absorbed; we've written that down ourselves (Viability §2).

**The fact — four reasons the trust layer grows rather than shrinks as capability grows:**

1. **The evidence points the other way, so far.** As capability rose through 2025–26, trust *fell*: distrust of agent accuracy went from 31% to 46–87% depending on measure; AI adoption still correlates with *worse* delivery stability (DORA 2025); code churn rose +861% under high AI adoption (GTM Plan §4). Better generation has so far produced more volume to verify, not less need to.
2. **Verification demand scales with volume, not with error rate.** Even at very low per-change failure rates, ten branches an hour against a fixed human review ceiling (review time +91%) is a throughput problem that gets *worse* as agents get better — more output, same humans. The blind-merge gap "grows on its own with every agent seat sold, by anyone" (Master Market Document Part I).
3. **The most capable organizations verify the most.** Meta — arguably the world's densest concentration of both models and engineers — built RADAR internally to risk-review agent code and catches a 1/3 revert rate (GTM Plan §5.1). Aviation is the pattern: autopilots getting better produced *more* instrumentation and audit, not less, because delegation at scale is precisely what demands attribution and proof. And the fully-autonomous philosophy already has a shipping representative — Composio AO merges when CI is green; *we prove it's still green after everyone else merged* (Narrative §2.2).
4. **Half the product is not about model quality at all.** Attribution ("which agent, under whose approval, wrote this line"), audit evidence for procurement, budget governance, and merge coordination between *parallel* actors are organizational requirements that hold even for flawless agents — you still need to know who did what, prove it, and serialize the merges. Perfect drivers still need traffic law.

**The residual risk, owned honestly:** if agents become both near-perfect *and* organizations stop caring about attribution and process evidence, the governance layer shrinks to coordination plumbing. We consider the second condition the less likely one — regulation, procurement, and insurance are all moving toward *more* required evidence, not less (Part XVIII, stated with the Omnibus caveat).

**The line.** *Better agents produce more branches, not more trust. The gap between "merged" and "verified" grows with every agent seat sold — by anyone.*

---

## The standing set (compressed: concede → fact → line)

**4. "GitHub / Anthropic / Cursor will just ship this."**
Concede: they're shipping the generation side aggressively, and any single feature has a ~2-quarter window. Fact: each is single-vendor by incentive — none will make its GUI a better home for a rival's agents; each reviews with its own model, not deterministic local gates; none models cross-branch staleness (GTM Plan §5.3). The one announced exception is named as our tripwire in writing (Cursor Origin — cloud forge; if it ships local execution + provenance, we re-plan within a quarter). Line: *Vendor-neutral verification is structurally Switzerland's job — and we've published the tripwire for the day that changes.*

**5. "The Git-client market is commoditized — why build another one?"**
Concede: fair — it's mature, and Fork proves craft alone earns $59.99 once, not a company. Fact: the client is the wedge and the *prerequisite*, not the business — verification and merge governance are only buildable on a real Git engine, and the free tier exists because the funnel must be genuinely excellent free against an account-walled incumbent (Narrative §4.2; Viability §1.5). Line: *The client is the foundation, not the pitch — and it has to be excellent anyway, because you'll live in it.*

**6. "Orchestration tools all died. Why are you different?"**
Concede: they did — Bloop with 27k stars, Terragon pointing users to the platform vendor on the way out. Fact: they died selling orchestration at $0 to individuals; we never sell orchestration — the free tier is a Git client with independent daily-driver value, and the paid tiers price against what teams demonstrably pay for in adjacent markets (review $24–48, merge reliability $8+, governance premiums — GTM Plan §5.4, §8). Line: *The corpse pile is our moat map — every rule it taught is priced into the model.*

**7. "MergeLoom already sells governed AI delivery."**
Concede: they're live, billing, and 6–12 months ahead on the governance-pipeline *story* — the most instructive competitor we have. Fact: they're our structural opposite — no client, no review surface, no interactive steering, no merge coordination (validated-then-stale is intrinsic: they stop at "PR opened" and never re-verify), no sandbox/egress claims, audit without integrity, and a one-person company with an SEO wall stamped on a single day (Deep Dive §§5–6, all [V]). Line: *They stop at "PR opened." We govern the last mile — and a branch validated an hour ago, against an older main, is not validated.*

**8. "Individual developers don't pay. Your funnel is worthless."**
Concede: Kite's law holds; individuals are not the revenue plan. Fact: the funnel's outputs are distribution, the in-company champion, and the two investor-grade metrics download counts can't fake (weekly active repos; verified merges/week); revenue is teams at $50+/seat, benchmarked above CodeRabbit/Graphite because it bundles review + queue + governance (GTM Plan §8; Narrative §4.5). Line: *Individuals are the funnel; the business is the team tier — and we don't sell it before the governance features exist.*

**9. "The EU AI Act doesn't actually require any of this."**
Concede: correct — Article 12 mandates logging and traceability, not cryptography, and the Digital Omnibus moved high-risk obligations to Dec 2027. We say so unprompted; it's in our honesty contract (Narrative §0.2). Fact: the pitch is *audit-grade evidence and where procurement is heading* — auditors are already asking (Codacy, the AI-BOM movement), and converging frameworks (Art. 12, NIST AI RMF, OWASP LLM Top 10, ISO 42001) all point the same direction (Part XVIII). Line: *We sell what procurement is starting to ask for — not a deadline scare. If we're early, early is where trust features have to be.*

**10. "Your cloud revenue is mostly pass-through — the ARR is fake."**
Concede: at 10,000 active cloud users, ~$212K of ~$228K monthly revenue would be model spend passing through our invoice — and our own cost model says quoting it as ARR unflagged would flatter the business (Cloud Companion §5.6). Fact: the tier is run on gross-margin dollars ($3.11/user-month, illustrative), break-even ≈ 3,200 active users, and the GA gate is "beta unit economics match the model within tolerance" — the spreadsheet is the acceptance test (§5.5–5.8). Line: *We flagged that number ourselves before you found it — the tier is priced on margin dollars, not revenue optics.*

**11. "Windows-first is a niche bet."**
Concede: the loud devtool market is Mac-first, and every beautiful-tool precedent shipped there. Fact: Windows is the *largest* developer OS (59.2% personal / ~48% professional) and the entire polished wave skipped it — the "Dana" persona (locked-down Windows enterprise machine, no premium native client, agents arriving with no safe way to run them) is the default enterprise developer in our beachhead markets, and the WSL2 depth is the unglamorous work Mac-first teams fund last (GTM Plan §§3.2, 4; Viability D-4). Line: *Mac-first is where the demos are; Windows is where the developers are.*

**12. "4–6 agents is too small a swarm to matter."**
Concede: that's the honest local ceiling on 16 GB (WSL2 takes half the RAM by default; rate limits bind earlier), and we refuse to claim more — "50 agents" is the vocabulary of the dead companies (Narrative §0.3). Fact: 4–6 *governed* agents already breaks every workflow on the market (the duct-tape baseline is tmux + worktrees + a manual merge tax — Master Market Document §2.3), and scale beyond the laptop is the cloud tier's job on the same binary, with the unit economics modeled and gated (Cloud Companion §5). Line: *A few agents, perfectly managed, beats fifty that OOM your laptop — and when you need fifty, that's what the cloud tier is for.*

---

## Register rules (apply to every answer above)

1. **Concede first, always.** Every objection contains a true fact; agreeing with it buys the credibility the counter spends. Never open with "actually."
2. **Sourced facts, no adjectives.** The counter is a figure, a probe result, or a named mechanism — never "we believe" or "we're confident."
3. **Never a villain.** Competitors get respect-then-stop (Narrative §3.4); the enemy stays the blind merge.
4. **Tense discipline under pressure.** The moment an answer needs an unbuilt feature, it says "in development" or "[Horizon]" — especially when conceding would sting. The honesty *is* the differentiator in a category that died of over-claiming.
5. **Leave the line, then stop.** Each answer ends on its one sentence; piling on after a good answer reads as insecurity.

*Self-gate: all three named objections steel-manned at full strength and answered from sourced material only (GTM Plan §§4–5, Competitor Research §3, MergeLoom Deep Dive §§5–6, Cloud Companion §5, Master Market Document Parts I/IX/XVIII); every concession is genuine; every line survives the delete-a-word test; no exclamation marks; residual risks owned where they exist (objection 3).*
