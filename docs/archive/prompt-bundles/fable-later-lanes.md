# Fable Bundle — Part 2 (Lanes F–J) + Consolidation Plan  [SAVE FOR LATER]

Run these AFTER (or alongside) the A–E bundle in `fable-creative-bundle.md`. They reuse the SAME shared
Master Brief — prepend it (from `fable-creative-bundle.md`) to every lane. Same autonomous rules: one
subagent per part, a PHASE REPORT after each, halt only on a twice-failed self-gate or a contradiction of
DESIGN.md / PRODUCT.md / the Voice Bible. Same safeguard discipline: F/G/I/J are business/creative/product
(clean); H is NON-SECURITY engineering only (scope fence below) — keep all framing constructive.

Dependencies: F reads Lane D's cost model; G reads Lane C's voice/GTM — so run F and G after C/D produce
output (or point them at the in-progress files). H, I, J are independent → fire anytime, in parallel.
Output homes follow the AGENTS.md taxonomy: business/GTM → docs/business/go-to-market/ · product/UX → docs/design/ ·
engineering → code + ADRs. (Everything gets consolidated at the end — see the Consolidation Plan.)

═══════════════════════════════════════════════════════════════════════════════════════════
LANE F — Stakeholder & Fundraising Communication  (builds on Lane D's cost model)
Output: docs/business/go-to-market/decks/ + docs/business/go-to-market/Mainguard_Business_Plan.md
═══════════════════════════════════════════════════════════════════════════════════════════
Read docs/business/go-to-market/*, docs/phase-2/Mainguard_Cloud_Vibe_Companion.md (the cost model), docs/business/market-analysis/*.
Part 1 — The investor pitch deck (narrative arc: problem → why now → the wedge → the moat → traction →
market → business model → the ask), each slide with speaker notes. Plus three variants: a technical
deep-dive deck (the safe-to-merge guarantee AS the moat), an advisor/board-update deck, and a "swarm
vision" deck.
Part 2 — The business plan + the financial-model narrative (Lane D supplies the numbers; F tells the story
around them — unit economics, the BYOK-vs-cloud revenue logic, the path to a Team/Enterprise tier).
Part 3 — Grant / subsidy application drafts against the Twente/Netherlands subsidy stack already mapped in
the GTM docs.
Part 4 — A defensibility/moat memo + an objection-handling doc (why "just use GitHub's merge queue,"
"Conductor already exists," "agents will get good enough to not need this" each fail).

═══════════════════════════════════════════════════════════════════════════════════════════
LANE G — Marketing, Content & Launch (organic)  (builds on Lane C's voice/GTM)
Output: docs/business/go-to-market/marketing/ (campaign, calendar, drafted posts, social, PR, emails, scripts)
═══════════════════════════════════════════════════════════════════════════════════════════
Read the Voice & Delight Bible, docs/creative/LaunchReserve.md, docs/business/market-analysis/*, docs/business/go-to-market/*.
Part 1 — The launch campaign plan: channels, sequencing, and the wedge (P2-12 external-PR intake = the
day-one "useful without changing how you run agents" hook).
Part 2 — A content calendar + a backlog of DRAFTED posts: comparison pieces (vs GitKraken/Fork/Conductor)
and engineering blogs that double as marketing (the 60fps commit-graph router; how the governed merge
queue works).
Part 3 — The social launch reserve, fully written: a Show HN post WITH the FAQ pre-drafted for the comment
thread, X/Bluesky/LinkedIn thread series, the "why I built this" founder story.
Part 4 — A manifesto/thesis essay (the enemy framing as a sharp standalone piece: "ungoverned AI merges
are a time bomb").
Part 5 — Email sequences (waitlist nurture → launch → onboarding drip), a press/PR one-pager kit, and
video/demo scripts (explainer + founder story + the live-demo storyboard & narration that pairs with Lane
E's prototype).

═══════════════════════════════════════════════════════════════════════════════════════════
LANE H — Engineering Quality & Optimization  (NON-SECURITY — scope fence is mandatory)
Output: real code + ADRs; implementation notes destined for master-v2 (see Consolidation Plan)
═══════════════════════════════════════════════════════════════════════════════════════════
SCOPE FENCE (non-negotiable): work ONLY on the shipped single-user Git client and the NON-security phase-2
surfaces. Do NOT touch the daemon, sandbox, merge queue, orchestration, audit, or any merge-safety code —
that is Opus's, and its framing trips the safeguards anyway. If a task pulls you toward that code, STOP and
flag it.
Read AGENTS.md, the shipped Mainguard.Agents/Mainguard.App.Shell, docs/phase-2/Mainguard_Performance_Hotspot_Register.md.
Part 1 — Implement the Performance Hotspot Register wins that are in the client (RepositoryAnalyzer
per-HEAD cache, commit-graph virtualization tuning). Verify with dotnet build + a measured before/after.
Part 2 — The commit-graph layout algorithm: lane routing, edge bundling, crossing minimization, 60fps on
pathological DAGs.
Part 3 — Diff-engine quality: intra-line diff, syntax highlighting, image-diff polish.
Part 4 — A maintainability/readability + test-coverage audit of the client (dead code, complexity
hotspots, property tests for the pure engines: PatchParser, MergeChunker, ChangelogGenerator).
Part 5 — Reliability hardening of the shipped client — the "never lose work" promise, robust index.lock
handling, error/edge-case coverage (framed as reliability).
Part 6 — ADRs + a contributor guide + XML doc comments.
Every code change: dotnet build (+ dotnet test for Core), a proposed commit message, no commit/push/main.

═══════════════════════════════════════════════════════════════════════════════════════════
LANE I — Product & UX Depth  (what this actually is)
Output: docs/design/ProductAndUX.md (+ dataviz assets)
═══════════════════════════════════════════════════════════════════════════════════════════
WHAT LANE I IS (since it was unclear): this is the "is the product itself smart and delightful to USE"
lane — product management + UX research + interaction design. It's distinct from the *visual* design of
Lanes A/B (how it looks) and the *code quality* of Lane H (how it's built). Lane I asks: are we building
the right things, are the existing flows actually usable, and are they discoverable and learnable? Four
parts:
Part 1 — Feature ideation: a prioritized backlog of net-new features that would genuinely delight, BEYOND
the C1–C5 set — e.g. richer stash management, worktree UX, interactive-rebase enhancements, smarter
conflict resolution. Each with its job-to-be-done, a rough sketch, and a priority rationale.
Part 2 — A usability heuristic audit of the SHIPPED client: Nielsen heuristics + cognitive walkthroughs of
the core flows (clone → commit → push; conflict resolution; interactive rebase). Where does a real user
hesitate, get lost, or fear losing work? Concrete findings + fixes.
Part 3 — The command-palette + keyboard-shortcut system (extends T-18): a comprehensive, discoverable,
learnable command surface — every action reachable, named consistently, with progressive-disclosure hints.
Part 4 — Analytics dataviz: turn the T-22 punch-card / churn / contributor stats into genuinely insightful,
on-brand visuals (LOAD THE `dataviz` SKILL; conform to the 5-theme tokens).

═══════════════════════════════════════════════════════════════════════════════════════════
LANE J — Paid Acquisition & B2B Sales  (ads + how to find and pitch companies)
Output: docs/business/go-to-market/sales/ (paid-media plan, sourcing playbook, outreach + pitch kit)
═══════════════════════════════════════════════════════════════════════════════════════════
Read docs/business/go-to-market/* (the ICP + the Twente/Enschede target-company map), docs/business/market-analysis/*.
Part 1 — Paid advertising: WHERE to run ads for a developer / B2B-dev tool (not consumer channels) and HOW.
Cover the fitting channels with a per-channel plan (fit · targeting · budget share · drafted ad creative ·
measurement): Reddit (r/programming, r/devops, r/git), Hacker News sponsorships + a well-timed Show HN, dev
newsletters (TLDR, Console, Pointer, Bytes), X/Twitter promoted, LinkedIn (for the eng-manager / compliance
buyer), Google Search ads on competitor + intent keywords, YouTube dev-channel + dev-podcast sponsorships,
Dev.to / Stack Overflow, and OSS/conference sponsorship. Give a phased budget (pre-launch → launch → scale)
and the funnel metrics to watch (CAC, activation, attribution).
Part 2 — Finding target companies: sharpen the ICP (teams already running AI coding agents; Windows-first
dev shops — the underserved flank; regulated/compliance-driven orgs — the audit-trail angle), then the
SOURCING playbook: how to actually find them — the Twente/Enschede map, LinkedIn Sales Navigator filters,
GitHub org signals, job postings mentioning Copilot/agents/Windows, and the right communities.
Part 3 — Pitching: outreach sequences (cold email + LinkedIn DM templates, per persona), the tailored pitch
(eng manager vs security/compliance vs founder — different pain, different hook), the demo → pilot → close
flow, a POC/pilot structure, and pricing/negotiation talking points + objection handling.

═══════════════════════════════════════════════════════════════════════════════════════════
CONSOLIDATION PLAN — run ONCE, after all lanes (A–J) finish  (a dedicated merge pass, not mid-flight)
═══════════════════════════════════════════════════════════════════════════════════════════
Goal: collapse the many lane outputs into a small, navigable set of hubs so everything is easy to find and
manage. Merge related files; update the AGENTS.md taxonomy to point at the hubs. Mapping:

- IMPLEMENTATION → fold into docs/phase-2/Mainguard_Master_Implementation_Document_v2.md (the binding spec).
  Anything that tells an engineer WHAT or HOW to build goes here, into its relevant P2/P3 task section:
  Lane E's control-center/Vibe design + prototype decisions → the P2-13/P2-11/P2-10/P2-14/P3-03 tasks;
  Lane A/B design decisions that drive UI implementation → their tasks; Lane H's optimizations + ADRs →
  the relevant tasks (or a linked docs/phase-2/ADRs.md the master doc references). This is the user's rule:
  implementation-related content → master v2.
- DESIGN HUB (docs/design/): merge Lane A (design system) + B (surfaces/features) + E (control-center/Vibe
  *design*, not the code) + I (product/UX) into one navigable design reference.
- BRAND & VOICE HUB (docs/creative/): merge the Voice Bible + microcopy + empty states + motion + narrative.
- GTM HUB (docs/business/go-to-market/): keep Mainguard_Master_Market_Document as the strategy hub; fold Lane C
  (positioning/launch narrative) + F (business plan/decks index) + G (marketing/content) + J (ads/sales)
  into a "GTM Execution" companion doc + subfolders (decks/, marketing/, sales/). Decks, blog drafts, and
  the prototype code stay as their own files, indexed from the hub.
- Then update the AGENTS.md documentation-map table to name the consolidated hubs.

Sequencing note: this merge is an editing/reconciling job best done as its own pass once the lanes are done
— DO NOT run it while lanes are still writing. It can be Opus or Fable; it touches no security content.

═══════════════════════════════════════════════════════════════════════════════════════════
HOW TO LAUNCH — the one message to send (dependencies baked in)
═══════════════════════════════════════════════════════════════════════════════════════════
This file lives at fable-later-lanes.md (repo root); every subagent can read it and its lane blocks. Send
this single orchestration message to a lead Fable agent:

────────────────────────────────────────────────────────────────────────────
You are the orchestrator for the Part-2 lane bundle in fable-later-lanes.md (repo root). Read the whole
file first, and prepend the SHARED MASTER BRIEF from fable-creative-bundle.md to every lane. Dispatch the
lanes as SUBAGENTS with exactly these dependencies:
- NOW, in parallel (independent, different outputs): spawn Lane H, Lane I, and Lane J.
- Spawn Lane F once Lane D's deepened cost model exists (docs/phase-2/Mainguard_Cloud_Vibe_Companion.md
  updated). Spawn Lane G once Lane C's elevated Voice Bible / GTM output exists (docs/creative/ +
  docs/business/go-to-market/). If those upstream lanes (C, D) are already finished, spawn F and G now too.
Each subagent prepends the Master Brief, then executes ONLY its lane block, autonomously — one subagent per
part, a PHASE REPORT after each part (what changed · self-gate vs the goal · files/tokens/types named),
then continue. Halt a lane only if a part fails its self-gate twice or a choice contradicts DESIGN.md /
PRODUCT.md / the Voice Bible. SCOPE: F/G/I/J are business/creative/product and clean; Lane H is
NON-SECURITY engineering only — enforce its scope fence (shipped client + non-security phase-2 surfaces;
never the daemon / sandbox / merge-safety code). Lane H (and any code Lane I produces) verifies each
increment with dotnet build (+ dotnet test for Core) and hands back a proposed commit message; no lane
commits, pushes, or touches main. Relay each lane's phase reports to me as they land, and tell me
immediately if any lane halts. Do NOT run the Consolidation Plan — that is a separate pass to run only
after every lane (A–J) is finished.
────────────────────────────────────────────────────────────────────────────
